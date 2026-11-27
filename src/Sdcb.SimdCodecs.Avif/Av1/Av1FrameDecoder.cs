using System.Buffers;
using System.Diagnostics.CodeAnalysis;
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

        // Tile byte ranges up front, so the tiles of the group can be decoded independently in parallel.
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

        bool split = UseTileSplit(seq, fh);
        bool wholeFrame = tgStart == 0 && tgEnd == numTiles - 1 && state.DeferredTiles is null;

        Av1CdfState? capturedCdf = savedUpdateCdf;
        // Tiles decode in parallel even under IntraBC, which copies from the frame being decoded: a conforming vector
        // stays inside its own tile. A corrupt one may read another tile's samples while that tile is being written;
        // the read stays inside the plane (CheckReferenceBounds), so the only effect is unspecified output for a
        // non-conforming stream. Serialising IntraBC frames instead cost screen content ~40% on 16 threads.
        // The tile spans rebuilt from ptr are in range: the loop above keeps every tileOffsets[idx] + tileSizes[idx]
        // within data (each size is checked against what remains), and data stays pinned while any worker runs.
        fixed (byte* basePtr = data)
        {
            nint ptr = (nint)basePtr;
            if (split && wholeFrame)
            {
                // The usual case: one tile group holds the whole frame. Parse and reconstruction overlap.
                var records = new Av1TileRecords[tileCount];
                for (int idx = 0; idx < tileCount; idx++)
                    records[idx] = NewRecords(seq, fh, idx / fh.TileCols, idx % fh.TileCols);
                try
                {
                    RunSplit(seq, fh, pic, state, session, records, ptr, tileOffsets, tileSizes, ref capturedCdf);
                }
                finally
                {
                    foreach (var r in records)
                        r.Dispose();
                }
                savedUpdateCdf = capturedCdf;
                return tileCount;
            }

            if (split)
                state.DeferredTiles ??= new Av1TileRecords?[numTiles];

            if (tileCount == 1)
            {
                int tileRow = tgStart / fh.TileCols;
                int tileCol = tgStart % fh.TileCols;
                bool saveCdf = !fh.DisableFrameEndUpdateCdf && tgStart == fh.ContextUpdateTileId;
                singleTileDecoder ??= new Av1TileDecoder(seq, fh, pic, state, session);
                var tileSpan = data.Slice(tileOffsets[0], tileSizes[0]);
                if (split)
                {
                    var records = NewRecords(seq, fh, tileRow, tileCol);
                    state.DeferredTiles![tgStart] = records;
                    singleTileDecoder.ParseTile(tileRow, tileCol, tileSpan, saveCdf, ref savedUpdateCdf, records);
                }
                else
                {
                    singleTileDecoder.DecodeTile(tileRow, tileCol, tileSpan, saveCdf, ref savedUpdateCdf);
                }
                return 1;
            }

            try
            {
                Parallel.For<Av1TileDecoder>(
                    0,
                    tileCount,
                    AvifDiagnostics.ParallelOptions,
                    () => new Av1TileDecoder(seq, fh, pic, state, session),
                    (idx, _, worker) =>
                    {
                        int tileNum = tgStart + idx;
                        int tileRow = tileNum / fh.TileCols;
                        int tileCol = tileNum % fh.TileCols;
                        bool saveCdf = !fh.DisableFrameEndUpdateCdf && tileNum == fh.ContextUpdateTileId;
                        Av1CdfState? localCdf = null;
                        var tileSpan = new ReadOnlySpan<byte>((byte*)ptr + tileOffsets[idx], tileSizes[idx]);
                        if (split)
                        {
                            var records = NewRecords(seq, fh, tileRow, tileCol);
                            state.DeferredTiles![tileNum] = records; // one slot per tile: no two workers share one
                            worker.ParseTile(tileRow, tileCol, tileSpan, saveCdf, ref localCdf, records);
                        }
                        else
                        {
                            worker.DecodeTile(tileRow, tileCol, tileSpan, saveCdf, ref localCdf);
                        }
                        if (saveCdf && localCdf is not null)
                            Volatile.Write(ref capturedCdf, localCdf);
                        return worker;
                    },
                    _ => { });
            }
            catch (AggregateException ex)
            {
                Rethrow(ex);
            }
        }
        savedUpdateCdf = capturedCdf;
        return tileCount;
    }

    /// <summary>
    /// Whether to parse the frame's tiles first and reconstruct them in a superblock wavefront, overlapping the two.
    /// Worth it when tile-level parallelism leaves threads idle (fewer tiles than threads) on a frame big enough to
    /// amortise the records (about a megapixel). IntraBC frames copy from the frame being reconstructed; they keep the
    /// block-by-block order.
    /// </summary>
    internal static bool UseTileSplit(Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        if (fh.AllowIntrabc)
            return false;
        if (AvifDiagnostics.TileSplit != 0)
            return AvifDiagnostics.TileSplit > 0;
        int threads = AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism is > 0 and var d ? d : Environment.ProcessorCount;
        return fh.TileCols * fh.TileRows < threads && (long)fh.MiRows * fh.MiCols >= 1 << 16;
    }

    private static Av1TileRecords NewRecords(Av1SequenceHeader seq, Av1FrameHeader fh, int tileRow, int tileCol) =>
        new(fh.MiRowStarts[tileRow], fh.MiRowStarts[tileRow + 1], fh.MiColStarts[tileCol], fh.MiColStarts[tileCol + 1],
            seq.Use128x128Superblock ? 32 : 16);

    /// <summary>
    /// Reconstructs tiles parsed by earlier tile groups (<see cref="Av1FrameState.DeferredTiles"/>), before the
    /// post-filters. Only a frame split over several tile groups gets here; one tile group overlaps parse and
    /// reconstruction in <see cref="DecodeTileGroup"/>.
    /// </summary>
    internal static void ReconstructDeferred(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState state, Av1DecoderSession session)
    {
        var tiles = state.DeferredTiles!;
        state.DeferredTiles = null;
        var parsed = tiles.Where(t => t is not null).Select(t => t!).ToArray();
        try
        {
            Av1CdfState? unused = null;
            RunSplit(seq, fh, pic, state, session, parsed, 0, null, null, ref unused);
        }
        finally
        {
            foreach (var tile in parsed)
                tile.Dispose();
        }
    }

    /// <summary>
    /// Runs the parse / reconstruction split of a frame. The jobs are, in order: parsing each tile (when
    /// <paramref name="tileSizes"/> is given; otherwise the records are complete already), then reconstructing each
    /// superblock row of each tile, row by row across the tiles. Workers claim jobs from one counter in that order.
    /// A reconstruction job waits, superblock by superblock, for that superblock to be parsed and for the row above
    /// to have finished the superblock above-right (intra edges reach that far; tiles do not depend on each other).
    /// Everything a job waits for is claimed earlier, so it is held by a running worker and the wait ends, whatever
    /// the number of workers; with one, all parsing precedes all reconstruction. A failure anywhere stops every waiter.
    /// </summary>
    private static unsafe void RunSplit(
        Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState state, Av1DecoderSession session,
        Av1TileRecords[] records, nint dataPtr, int[]? tileOffsets, int[]? tileSizes, ref Av1CdfState? capturedCdf)
    {
        int parseJobs = tileSizes is null ? 0 : records.Length;
        var rowDone = new Av1Progress[records.Length][];
        int maxRows = 0;
        for (int t = 0; t < records.Length; t++)
        {
            rowDone[t] = new Av1Progress[records[t].SbRows];
            for (int row = 0; row < rowDone[t].Length; row++)
                rowDone[t][row] = new Av1Progress();
            maxRows = Math.Max(maxRows, records[t].SbRows);
        }
        var reconJobs = new List<(int Tile, int Row)>();
        for (int row = 0; row < maxRows; row++)
        {
            for (int t = 0; t < records.Length; t++)
            {
                if (row < records[t].SbRows)
                    reconJobs.Add((t, row));
            }
        }

        int total = parseJobs + reconJobs.Count;
        int threads = AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism is > 0 and var d ? d : Environment.ProcessorCount;
        int workers = Math.Min(threads, total);
        int next = 0;
        int failed = 0;
        Av1CdfState? cdf = capturedCdf;

        void Abort()
        {
            Volatile.Write(ref failed, 1);
            foreach (var r in records)
                r.Parsed.Abort();
            foreach (var rows in rowDone)
            {
                foreach (var p in rows)
                    p.Abort();
            }
        }

        try
        {
            Parallel.For(0, workers, AvifDiagnostics.ParallelOptions, _ =>
            {
                var decoder = new Av1TileDecoder(seq, fh, pic, state, session);
                try
                {
                    for (int j = Interlocked.Increment(ref next) - 1; j < total; j = Interlocked.Increment(ref next) - 1)
                    {
                        if (j < parseJobs)
                        {
                            var rec = records[j];
                            int tileNum = j; // parse jobs exist only for a whole-frame tile group
                            int tileRow = tileNum / fh.TileCols;
                            int tileCol = tileNum % fh.TileCols;
                            bool saveCdf = !fh.DisableFrameEndUpdateCdf && tileNum == fh.ContextUpdateTileId;
                            Av1CdfState? localCdf = null;
                            var tileSpan = new ReadOnlySpan<byte>((byte*)dataPtr + tileOffsets![j], tileSizes![j]);
                            decoder.ParseTile(tileRow, tileCol, tileSpan, saveCdf, ref localCdf, rec);
                            if (saveCdf && localCdf is not null)
                                Volatile.Write(ref cdf, localCdf);
                            continue;
                        }

                        var (t, row) = reconJobs[j - parseJobs];
                        var tile = records[t];
                        var above = row > 0 ? rowDone[t][row - 1] : null;
                        var mine = rowDone[t][row];
                        for (int col = 0; col < tile.SbCols; col++)
                        {
                            if (!tile.Parsed.WaitFor(row * tile.SbCols + col + 1, ref failed))
                                return;
                            if (above is not null && !above.WaitFor(Math.Min(col + 2, tile.SbCols), ref failed))
                                return;
                            decoder.ReconstructSuperblock(tile, row, col);
                            mine.Publish(col + 1);
                        }
                    }
                }
                catch
                {
                    Abort();
                    throw;
                }
            });
        }
        catch (AggregateException ex)
        {
            Rethrow(ex);
        }
        capturedCdf = cdf;
    }

    [DoesNotReturn]
    private static void Rethrow(AggregateException ex)
    {
        var inner = ex.Flatten().InnerExceptions;
        foreach (var e in inner)
        {
            if (e is InvalidDataException ide)
                throw new InvalidDataException(ide.Message, ide);
        }
        ExceptionDispatchInfo.Capture(inner[0]).Throw();
        throw ex;
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
