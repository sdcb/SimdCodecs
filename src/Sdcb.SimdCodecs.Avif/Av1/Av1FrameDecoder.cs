namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Decodes AV1 still-image (including scalable layered items) and multi-frame bitstreams into <see cref="Av1Picture"/>.
/// </summary>
internal static class Av1FrameDecoder
{
    /// <summary>
    /// Decodes a single-item AV1 bitstream (from an AVIF <c>av01</c> item payload, with optional <c>av1C</c> configOBUs).
    /// Supports both single-frame intra stills and multi-layer scalable items (<c>a1lx</c>/<c>a1op</c>).
    /// </summary>
    internal static Av1Picture DecodeStill(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> configObus = default)
    {
        var session = new Av1DecoderSession();
        session.ParseConfigObus(configObus);
        return session.DecodeTemporalUnit(payload);
    }

    internal static int DecodeTileGroup(
        ReadOnlySpan<byte> data,
        Av1FrameHeader fh,
        Av1TileDecoder decoder,
        ref Av1CdfState? savedUpdateCdf)
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

            bool saveCdf = !fh.DisableFrameEndUpdateCdf && tileNum == fh.ContextUpdateTileId;
            decoder.DecodeTile(tileRow, tileCol, data.Slice(offset, tileSize), saveCdf, ref savedUpdateCdf);
            offset += tileSize;
        }

        return tgEnd - tgStart + 1;
    }

    internal static void RunPostFiltersBeforeGrain(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState state)
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
