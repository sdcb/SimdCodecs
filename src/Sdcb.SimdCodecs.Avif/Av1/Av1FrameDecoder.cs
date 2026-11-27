using System.Buffers;
using System.Runtime.ExceptionServices;

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
        var session = new Av1DecoderSession { OneShot = true };
        session.ParseConfigObus(configObus);
        return session.DecodeTemporalUnit(payload);
    }

    internal static unsafe int DecodeTileGroup(
        ReadOnlySpan<byte> data,
        Av1SequenceHeader seq,
        Av1FrameHeader fh,
        Av1Picture pic,
        Av1FrameState state,
        Av1DecoderSession session,
        ref Av1TileDecoder? singleTileDecoder,
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
        int tileCount = tgEnd - tgStart + 1;

        if (tileCount == 1)
        {
            int tileRow = tgStart / fh.TileCols;
            int tileCol = tgStart % fh.TileCols;
            bool saveCdf = !fh.DisableFrameEndUpdateCdf && tgStart == fh.ContextUpdateTileId;
            singleTileDecoder ??= new Av1TileDecoder(seq, fh, pic, state, session);
            singleTileDecoder.DecodeTile(tileRow, tileCol, data.Slice(offset, remaining), saveCdf, ref savedUpdateCdf);
            return 1;
        }

        // Parse tile byte ranges up front so all tiles in the group can be decoded independently in parallel.
        var tileOffsets = new int[tileCount];
        var tileSizes = new int[tileCount];
        for (int idx = 0; idx < tileCount; idx++)
        {
            bool lastTile = idx == tileCount - 1;
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
            tileOffsets[idx] = offset;
            tileSizes[idx] = tileSize;
            offset += tileSize;
        }

        Av1CdfState? capturedCdf = savedUpdateCdf;
        // Tiles decode in parallel even under IntraBC, which copies from the frame being decoded: a conforming vector
        // stays inside its own tile. A corrupt one may read another tile's samples while that tile is being written;
        // the read stays inside the plane (CheckReferenceBounds), so the only effect is unspecified output for a
        // non-conforming stream. Serialising IntraBC frames instead cost screen content ~40% on 16 threads.
        // The tile spans rebuilt from ptr are in range: the loop above keeps every tileOffsets[idx] + tileSizes[idx]
        // within data (each size is checked against what remains), and data stays pinned for the whole Parallel.For.
        fixed (byte* basePtr = data)
        {
            nint ptr = (nint)basePtr;
            try
            {
                Parallel.For<Av1TileDecoder>(
                    0,
                    tileCount,
                    () => new Av1TileDecoder(seq, fh, pic, state, session),
                    (idx, _, worker) =>
                    {
                        int tileNum = tgStart + idx;
                        int tileRow = tileNum / fh.TileCols;
                        int tileCol = tileNum % fh.TileCols;
                        bool saveCdf = !fh.DisableFrameEndUpdateCdf && tileNum == fh.ContextUpdateTileId;
                        Av1CdfState? localCdf = null;
                        var tileSpan = new ReadOnlySpan<byte>((byte*)ptr + tileOffsets[idx], tileSizes[idx]);
                        worker.DecodeTile(tileRow, tileCol, tileSpan, saveCdf, ref localCdf);
                        if (saveCdf && localCdf is not null)
                            Volatile.Write(ref capturedCdf, localCdf);
                        return worker;
                    },
                    _ => { });
            }
            catch (AggregateException ex)
            {
                var inner = ex.Flatten().InnerExceptions;
                foreach (var e in inner)
                {
                    if (e is InvalidDataException ide)
                        throw new InvalidDataException(ide.Message, ide);
                }
                ExceptionDispatchInfo.Capture(inner[0]).Throw();
                throw;
            }
        }
        savedUpdateCdf = capturedCdf;
        return tileCount;
    }

    internal static void RunPostFiltersBeforeGrain(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState state, bool pooledOutput)
    {
        AvifAblation ablation = AvifDiagnostics.Ablation;

        // 1. Deblocking loop filter (in-place on pic.Planes at coded FrameWidth)
        if ((ablation & AvifAblation.SkipLoopFilter) == 0)
            Av1LoopFilter.Apply(seq, fh, pic, state);

        // 2. CDEF (at coded FrameWidth)
        bool runCdef = !fh.CodedLossless && !fh.AllowIntrabc && seq.EnableCdef && (ablation & AvifAblation.SkipCdef) == 0;
        bool runSuperres = fh.UseSuperres && fh.UpscaledWidth > fh.FrameWidth && (ablation & AvifAblation.SkipSuperres) == 0;
        bool runLr = fh.UsesLr && (ablation & AvifAblation.SkipLoopRestoration) == 0;

        ushort[][] deblockPlanes = pic.Planes;
        ushort[][] cdefPlanes = deblockPlanes;
        ushort[][]? rentedCdef = null;
        ushort[][]? rentedUpCdef = null;
        ushort[][]? rentedUpDeblock = null;
        ushort[][]? rentedFinal = null;

        try
        {
            if (runCdef)
            {
                rentedCdef = RentLike(pic);
                cdefPlanes = rentedCdef;
                Av1Cdef.Apply(seq, fh, pic, state, deblockPlanes, cdefPlanes);
            }

            // 3. Superres horizontal upscaling (§7.16): upscales CDEF output (and deblock output if LR needs it)
            ushort[][] upDeblock = deblockPlanes;
            ushort[][] upCdef = cdefPlanes;
            if (runSuperres)
            {
                rentedUpCdef = RentLike(pic);
                upCdef = rentedUpCdef;
                Av1Superres.Apply(seq, fh, pic, cdefPlanes, upCdef);
                if (runLr && !ReferenceEquals(deblockPlanes, cdefPlanes))
                {
                    rentedUpDeblock = RentLike(pic);
                    upDeblock = rentedUpDeblock;
                    Av1Superres.Apply(seq, fh, pic, deblockPlanes, upDeblock);
                }
                else
                {
                    upDeblock = upCdef;
                }
            }

            // 4. Loop Restoration (§7.17) at UpscaledWidth
            ushort[][] finalPlanes = upCdef;
            if (runLr)
            {
                rentedFinal = RentLike(pic);
                finalPlanes = rentedFinal;
                Av1LoopRestoration.Apply(seq, fh, pic, state, upDeblock, upCdef, finalPlanes);
            }

            if (!ReferenceEquals(finalPlanes, pic.Planes))
            {
                // Swap the rented finalPlanes into pic.Planes and return the previous planes if pooled. A picture a
                // reference slot keeps must not hand its planes back to the pool on Dispose (pooledOutput false):
                // they are then just left to the GC.
                ushort[][] oldPlanes = pic.Planes;
                bool oldPooled = pic.PlanesPooled;
                pic.TakePlanes(finalPlanes, pooled: pooledOutput);

                if (ReferenceEquals(finalPlanes, rentedFinal)) rentedFinal = null;
                else if (ReferenceEquals(finalPlanes, rentedUpCdef)) rentedUpCdef = null;
                else if (ReferenceEquals(finalPlanes, rentedCdef)) rentedCdef = null;

                if (oldPooled)
                    ReturnPlanes(oldPlanes);
            }
        }
        finally
        {
            if (rentedFinal is not null) ReturnPlanes(rentedFinal);
            if (rentedUpDeblock is not null) ReturnPlanes(rentedUpDeblock);
            if (rentedUpCdef is not null) ReturnPlanes(rentedUpCdef);
            if (rentedCdef is not null) ReturnPlanes(rentedCdef);
        }
    }

    private static ushort[][] RentLike(Av1Picture pic)
    {
        var arr = new ushort[pic.Planes.Length][];
        for (int p = 0; p < arr.Length; p++)
            arr[p] = ArrayPool<ushort>.Shared.Rent(pic.PlaneLengths[p]);
        return arr;
    }

    internal static void ReturnPlanes(ushort[][] planes)
    {
        for (int p = 0; p < planes.Length; p++)
        {
            if (planes[p] is { Length: > 0 } buf)
            {
                ArrayPool<ushort>.Shared.Return(buf);
                planes[p] = [];
            }
        }
    }
}
