namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Decodes an AV1 still-image bitstream (OBU stream) into an <see cref="Av1Picture"/>.
/// </summary>
internal static class Av1FrameDecoder
{
    /// <summary>
    /// Decodes a single intra AV1 bitstream (from an AVIF <c>av01</c> item payload, with optional <c>av1C</c> configOBUs).
    /// </summary>
    internal static Av1Picture DecodeStill(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> configObus = default)
    {
        Av1SequenceHeader? seq = null;
        if (!configObus.IsEmpty)
        {
            foreach (var obu in Av1ObuParser.ParseAll(configObus))
            {
                if (obu.Type == Av1Constants.ObuSequenceHeader)
                {
                    seq = Av1SequenceHeader.Parse(configObus.Slice(obu.PayloadOffset, obu.PayloadLength));
                    break;
                }
            }
        }

        var obus = Av1ObuParser.ParseAll(payload);

        // If the still-image payload carries multiple frames (e.g. scalable layered AVIF with a1lx/a1op),
        // verify whether enhancement layers require INTER_FRAME decoding (which lands in M3).
        int frameObuCount = 0;
        foreach (var obu in obus)
        {
            if (obu.Type is Av1Constants.ObuFrame or Av1Constants.ObuFrameHeader)
            {
                frameObuCount++;
                if (frameObuCount > 1)
                    throw new InvalidDataException("Layered scalable AVIF with multiple AV1 frames ('a1lx'/'a1op') is not supported yet (lands in M3).");
            }
        }

        Av1FrameHeader? fh = null;
        Av1Picture? pic = null;
        Av1FrameState? state = null;
        Av1TileDecoder? tileDecoder = null;
        int decodedTiles = 0;

        foreach (var obu in obus)
        {
            var obuPayload = payload.Slice(obu.PayloadOffset, obu.PayloadLength);
            switch (obu.Type)
            {
                case Av1Constants.ObuSequenceHeader:
                    seq = Av1SequenceHeader.Parse(obuPayload);
                    break;

                case Av1Constants.ObuFrame:
                    if (seq is null)
                        throw new InvalidDataException("AV1 frame OBU appeared before any sequence header OBU.");
                    DecodeFrameObu(obuPayload, obu.TemporalId, obu.SpatialId, seq, out fh, out pic, out state);
                    return pic;

                case Av1Constants.ObuFrameHeader:
                    if (seq is null)
                        throw new InvalidDataException("AV1 frame header OBU appeared before any sequence header OBU.");
                    var br = new Av1BitReader(obuPayload);
                    fh = Av1FrameHeader.Parse(ref br, seq, obu.TemporalId, obu.SpatialId);
                    pic = new Av1Picture(seq, fh);
                    state = new Av1FrameState(seq, fh);
                    tileDecoder = new Av1TileDecoder(seq, fh, pic, state);
                    decodedTiles = 0;
                    break;

                case Av1Constants.ObuTileGroup:
                    if (seq is null || fh is null || pic is null || state is null || tileDecoder is null)
                        throw new InvalidDataException("AV1 tile group OBU appeared without an active frame header.");
                    decodedTiles += DecodeTileGroup(obuPayload, fh, tileDecoder);
                    if (decodedTiles >= fh.TileCols * fh.TileRows)
                    {
                        RunPostFilters(seq, fh, pic, state);
                        return pic;
                    }
                    break;

                case Av1Constants.ObuTemporalDelimiter:
                case Av1Constants.ObuMetadata:
                case Av1Constants.ObuPadding:
                case Av1Constants.ObuRedundantFrameHeader:
                    break;
            }
        }

        if (pic is not null && seq is not null && fh is not null && state is not null && decodedTiles > 0)
        {
            RunPostFilters(seq, fh, pic, state);
            return pic;
        }

        throw new InvalidDataException("No decodable AV1 frame found in bitstream.");
    }

    private static void DecodeFrameObu(
        ReadOnlySpan<byte> obuPayload,
        int temporalId,
        int spatialId,
        Av1SequenceHeader seq,
        out Av1FrameHeader fh,
        out Av1Picture pic,
        out Av1FrameState state)
    {
        var br = new Av1BitReader(obuPayload);
        fh = Av1FrameHeader.Parse(ref br, seq, temporalId, spatialId);
        br.ByteAlign();
        int headerBytes = br.BytePosition;

        pic = new Av1Picture(seq, fh);
        state = new Av1FrameState(seq, fh);
        var tileDecoder = new Av1TileDecoder(seq, fh, pic, state);

        var tileGroupBytes = obuPayload[headerBytes..];
        DecodeTileGroup(tileGroupBytes, fh, tileDecoder);
        RunPostFilters(seq, fh, pic, state);
    }

    private static int DecodeTileGroup(ReadOnlySpan<byte> data, Av1FrameHeader fh, Av1TileDecoder decoder)
    {
        int numTiles = fh.TileCols * fh.TileRows;
        var br = new Av1BitReader(data);
        bool startAndEndPresent = numTiles > 1 && br.ReadBool();
        int tgStart, tgEnd;
        if (numTiles == 1 || !startAndEndPresent)
        {
            tgStart = 0;
            tgEnd = numTiles - 1;
        }
        else
        {
            int tileBits = fh.TileColsLog2 + fh.TileRowsLog2;
            tgStart = (int)br.ReadBits(tileBits);
            tgEnd = (int)br.ReadBits(tileBits);
            if (tgStart > tgEnd || tgEnd >= numTiles)
                throw new InvalidDataException("Invalid AV1 tile group range.");
        }
        br.ByteAlign();

        int offset = br.BytePosition;
        int remaining = data.Length - offset;

        for (int tileNum = tgStart; tileNum <= tgEnd; tileNum++)
        {
            int tileRow = tileNum / fh.TileCols;
            int tileCol = tileNum % fh.TileCols;
            bool lastTile = tileNum == tgEnd;
            int tileSize;
            if (lastTile)
            {
                tileSize = remaining;
            }
            else
            {
                if (remaining < fh.TileSizeBytes)
                    throw new InvalidDataException("Truncated AV1 tile size field.");
                uint raw = 0;
                for (int b = 0; b < fh.TileSizeBytes; b++)
                    raw |= (uint)data[offset + b] << (b * 8);
                offset += fh.TileSizeBytes;
                remaining -= fh.TileSizeBytes;
                if (raw >= (uint)remaining)
                    throw new InvalidDataException("AV1 tile extends past end of tile group OBU.");
                tileSize = (int)raw + 1;
                remaining -= tileSize;
            }

            decoder.DecodeTile(tileRow, tileCol, data.Slice(offset, tileSize));
            offset += tileSize;
        }

        return tgEnd - tgStart + 1;
    }

    private static void RunPostFilters(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState state)
    {
        // 1. Deblocking loop filter (in-place on pic.Planes at coded FrameWidth)
        Av1LoopFilter.Apply(seq, fh, pic, state);

        // 2. CDEF (at coded FrameWidth)
        bool runCdef = !fh.CodedLossless && !fh.AllowIntrabc && seq.EnableCdef;
        bool runSuperres = fh.UseSuperres && fh.UpscaledWidth > fh.FrameWidth;
        bool runLr = fh.UsesLr;

        ushort[][] deblockPlanes = pic.Planes;
        ushort[][] cdefPlanes;
        if (runCdef)
        {
            cdefPlanes = AllocateLike(pic);
            Av1Cdef.Apply(seq, fh, pic, state, deblockPlanes, cdefPlanes);
        }
        else
        {
            cdefPlanes = deblockPlanes;
        }

        // 3. Superres horizontal upscaling (§7.16): upscales CDEF output (and deblock output if LR needs it)
        ushort[][] upDeblock = deblockPlanes;
        ushort[][] upCdef = cdefPlanes;
        if (runSuperres)
        {
            upCdef = AllocateLike(pic);
            Av1Superres.Apply(seq, fh, pic, cdefPlanes, upCdef);
            if (runLr && !ReferenceEquals(deblockPlanes, cdefPlanes))
            {
                upDeblock = AllocateLike(pic);
                Av1Superres.Apply(seq, fh, pic, deblockPlanes, upDeblock);
            }
            else
            {
                upDeblock = upCdef;
            }
        }

        // 4. Loop Restoration (§7.17) at UpscaledWidth
        ushort[][] finalPlanes;
        if (runLr)
        {
            finalPlanes = AllocateLike(pic);
            Av1LoopRestoration.Apply(seq, fh, pic, state, upDeblock, upCdef, finalPlanes);
        }
        else
        {
            finalPlanes = upCdef;
        }

        if (!ReferenceEquals(finalPlanes, pic.Planes))
            CopyBack(finalPlanes, pic.Planes);

        // 5. Film Grain synthesis (§7.18.3)
        if (fh.ApplyGrain && fh.FilmGrain is not null)
            Av1FilmGrain.Apply(seq, fh.FilmGrain, pic);
    }

    private static ushort[][] AllocateLike(Av1Picture pic)
    {
        var arr = new ushort[pic.Planes.Length][];
        for (int p = 0; p < arr.Length; p++)
            arr[p] = new ushort[pic.Planes[p].Length];
        return arr;
    }

    private static void CopyBack(ushort[][] src, ushort[][] dst)
    {
        for (int p = 0; p < dst.Length; p++)
            dst[p] = src[p];
    }
}
