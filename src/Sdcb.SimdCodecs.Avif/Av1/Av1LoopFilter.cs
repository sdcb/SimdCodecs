using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 deblocking loop filter (§7.14).
/// Vertical edges first, then horizontal edges, across all planes.
/// </summary>
internal static partial class Av1LoopFilter
{
    internal static void Apply(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState st)
    {
        if (fh.CodedLossless || fh.AllowIntrabc)
            return;
        if (fh.LoopFilterLevel[0] == 0 && fh.LoopFilterLevel[1] == 0)
            return;

        int miRows = fh.MiRows;
        int miCols = fh.MiCols;
        int bitDepth = seq.BitDepth;

        for (int plane = 0; plane < seq.NumPlanes; plane++)
        {
            if (plane > 0 && fh.LoopFilterLevel[1 + plane] == 0)
                continue;

            int subX = plane == 0 ? 0 : seq.SubsamplingX;
            int subY = plane == 0 ? 0 : seq.SubsamplingY;
            int rowStep = 1 << subY;
            int colStep = 1 << subX;
            ushort[] planeBuf = pic.Planes[plane];
            int stride = pic.PlaneStrides[plane];
            byte[] lfTx = st.LoopfilterTxSizes[plane];
            int lfCols = st.LfTxCols[plane];

            // Pass 0: vertical edges (rows are independent — each row band writes only horizontal samples on its rows).
            int rowCount = (miRows + rowStep - 1) / rowStep;
            if (rowCount >= 32)
            {
                Parallel.For(0, rowCount, AvifDiagnostics.ParallelOptions, rowIdx =>
                {
                    int row = rowIdx * rowStep;
                    FilterVerticalRow(fh, st, planeBuf, stride, lfTx, lfCols, bitDepth, plane, subX, subY, colStep, miCols, row);
                });
            }
            else
            {
                for (int row = 0; row < miRows; row += rowStep)
                    FilterVerticalRow(fh, st, planeBuf, stride, lfTx, lfCols, bitDepth, plane, subX, subY, colStep, miCols, row);
            }

            // Pass 1: horizontal edges (columns are independent — each column writes only vertical samples on its columns).
            int colCount = (miCols + colStep - 1) / colStep;
            if (colCount >= 32)
            {
                Parallel.For(0, colCount, AvifDiagnostics.ParallelOptions, colIdx =>
                {
                    int col = colIdx * colStep;
                    FilterHorizontalCol(fh, st, planeBuf, stride, lfTx, lfCols, bitDepth, plane, subX, subY, rowStep, miRows, miCols, col);
                });
            }
            else
            {
                for (int col = 0; col < miCols; col += colStep)
                    FilterHorizontalCol(fh, st, planeBuf, stride, lfTx, lfCols, bitDepth, plane, subX, subY, rowStep, miRows, miCols, col);
            }
        }
    }

    private static void FilterVerticalRow(
        Av1FrameHeader fh, Av1FrameState st, ushort[] planeBuf, int stride, byte[] lfTx, int lfCols,
        int bitDepth, int plane, int subX, int subY, int colStep, int miCols, int row)
    {
        int y = row * 4;
        if (y >= fh.FrameHeight) return;

        int adjRow = row | subY;
        int yP = y >> subY;
        int txRow = adjRow >> subY;

        for (int col = 0; col < miCols; col += colStep)
        {
            int x = col * 4;
            if (x == 0 || x >= fh.FrameWidth) continue;

            int adjCol = col | subX;
            int prevCol = adjCol - (1 << subX);
            int xP = x >> subX;

            int txSz = lfTx[txRow * lfCols + (adjCol >> subX)];
            if ((xP & (Av1Constants.TxWidth[txSz] - 1)) != 0) continue;

            int adjIdx = adjRow * miCols + adjCol;
            int planeSize = Av1Constants.SubsampledSize(st.MiSizes[adjIdx], subX, subY);
            bool isBlockEdge = (xP & (Av1Constants.BlockWidth[planeSize] - 1)) == 0;
            bool skip = st.Skips[adjIdx] != 0;
            bool isIntra = st.RefFrames[adjIdx * 2] <= Av1Constants.IntraFrame;
            if (!isBlockEdge && skip && !isIntra)
                continue;

            int prevTxSz = lfTx[txRow * lfCols + (prevCol >> subX)];
            int baseSize = Math.Min(Av1Constants.TxWidth[prevTxSz], Av1Constants.TxWidth[txSz]);
            int filterSize = plane == 0 ? Math.Min(16, baseSize) : Math.Min(8, baseSize);

            int lvl = GetFilterLevel(fh, st, adjRow, adjCol, plane, 0);
            if (lvl == 0)
                lvl = GetFilterLevel(fh, st, adjRow, prevCol, plane, 0);
            if (lvl == 0)
                continue;

            int shift = fh.LoopFilterSharpness > 4 ? 2 : fh.LoopFilterSharpness > 0 ? 1 : 0;
            int limit = fh.LoopFilterSharpness > 0
                ? Av1Constants.Clip3(1, 9 - fh.LoopFilterSharpness, lvl >> shift)
                : Math.Max(1, lvl >> shift);
            int blimit = 2 * (lvl + 2) + limit;
            int thresh = lvl >> 4;

            FilterEdge(planeBuf, stride, bitDepth, plane, xP, yP, limit, blimit, thresh, 1, 0, filterSize);
        }
    }

    private static void FilterHorizontalCol(
        Av1FrameHeader fh, Av1FrameState st, ushort[] planeBuf, int stride, byte[] lfTx, int lfCols,
        int bitDepth, int plane, int subX, int subY, int rowStep, int miRows, int miCols, int col)
    {
        int x = col * 4;
        if (x >= fh.FrameWidth) return;

        int adjCol = col | subX;
        int xP = x >> subX;
        int txCol = adjCol >> subX;

        for (int row = 0; row < miRows; row += rowStep)
        {
            int y = row * 4;
            if (y == 0 || y >= fh.FrameHeight) continue;

            int adjRow = row | subY;
            int prevRow = adjRow - (1 << subY);
            int yP = y >> subY;
            int txRow = adjRow >> subY;
            int prevTxRow = prevRow >> subY;

            int txSz = lfTx[txRow * lfCols + txCol];
            if ((yP & (Av1Constants.TxHeight[txSz] - 1)) != 0) continue;

            int adjIdx = adjRow * miCols + adjCol;
            int planeSize = Av1Constants.SubsampledSize(st.MiSizes[adjIdx], subX, subY);
            bool isBlockEdge = (yP & (Av1Constants.BlockHeight[planeSize] - 1)) == 0;
            bool skip = st.Skips[adjIdx] != 0;
            bool isIntra = st.RefFrames[adjIdx * 2] <= Av1Constants.IntraFrame;
            if (!isBlockEdge && skip && !isIntra)
                continue;

            int prevTxSz = lfTx[prevTxRow * lfCols + txCol];
            int baseSize = Math.Min(Av1Constants.TxHeight[prevTxSz], Av1Constants.TxHeight[txSz]);
            int filterSize = plane == 0 ? Math.Min(16, baseSize) : Math.Min(8, baseSize);

            int lvl = GetFilterLevel(fh, st, adjRow, adjCol, plane, 1);
            if (lvl == 0)
                lvl = GetFilterLevel(fh, st, prevRow, adjCol, plane, 1);
            if (lvl == 0)
                continue;

            int shift = fh.LoopFilterSharpness > 4 ? 2 : fh.LoopFilterSharpness > 0 ? 1 : 0;
            int limit = fh.LoopFilterSharpness > 0
                ? Av1Constants.Clip3(1, 9 - fh.LoopFilterSharpness, lvl >> shift)
                : Math.Max(1, lvl >> shift);
            int blimit = 2 * (lvl + 2) + limit;
            int thresh = lvl >> 4;

            FilterEdge(planeBuf, stride, bitDepth, plane, xP, yP, limit, blimit, thresh, 0, 1, filterSize);
        }
    }

    /// <summary>
    /// Filters the four samples of one edge segment: for a vertical edge (dx = 1) rows y .. y + 3 at column x, for a
    /// horizontal edge (dy = 1) columns x .. x + 3 at row y.
    /// </summary>
    internal static void FilterEdge(
        ushort[] buf, int stride, int bitDepth, int plane,
        int x, int y, int limit, int blimit, int thresh, int dx, int dy, int filterSize)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            FilterEdgeVector(buf, stride, bitDepth, plane, x, y, limit, blimit, thresh, dx, dy, filterSize);
            return;
        }
        FilterEdgeScalar(buf, stride, bitDepth, plane, x, y, limit, blimit, thresh, dx, dy, filterSize);
    }

    internal static void FilterEdgeScalar(
        ushort[] buf, int stride, int bitDepth, int plane,
        int x, int y, int limit, int blimit, int thresh, int dx, int dy, int filterSize)
    {
        for (int i = 0; i < 4; i++)
            FilterSample(buf, stride, bitDepth, plane, x + i * dy, y + i * dx, limit, blimit, thresh, dx, dy, filterSize);
    }

    private static int GetFilterLevel(Av1FrameHeader fh, Av1FrameState st, int row, int col, int plane, int pass)
    {
        int idx = row * st.MiCols + col;
        int segment = st.SegmentIds[idx];
        int lfIdx = plane == 0 ? pass : plane + 1;
        int deltaLf = fh.DeltaLfPresent
            ? st.DeltaLfs[idx * 4 + (fh.DeltaLfMulti ? lfIdx : 0)]
            : 0;

        int lvl = Av1Constants.Clip3(0, 63, deltaLf + fh.LoopFilterLevel[lfIdx]);
        int segFeature = Av1Constants.SegLvlAltLfYV + lfIdx;
        if (fh.SegmentationEnabled && fh.FeatureEnabled[segment, segFeature])
            lvl = Av1Constants.Clip3(0, 63, lvl + fh.FeatureData[segment, segFeature]);

        if (fh.LoopFilterDeltaEnabled)
        {
            int nShift = lvl >> 5;
            int refFrame = st.RefFrames[idx * 2];
            if (refFrame <= Av1Constants.IntraFrame)
            {
                lvl = Av1Constants.Clip3(0, 63, lvl + (fh.LoopFilterRefDeltas[Av1Constants.IntraFrame] << nShift));
            }
            else
            {
                int mode = st.YModes[idx];
                int modeType = (mode >= Av1Constants.NearestMv && mode != Av1Constants.GlobalMv && mode != Av1Constants.GlobalGlobalMv) ? 1 : 0;
                lvl = Av1Constants.Clip3(0, 63, lvl + (fh.LoopFilterRefDeltas[refFrame] << nShift) + (fh.LoopFilterModeDeltas[modeType] << nShift));
            }
        }
        return lvl;
    }

    private static void FilterSample(
        ushort[] buf, int stride, int bitDepth, int plane,
        int x, int y, int limit, int blimit, int thresh, int dx, int dy, int filterSize)
    {
        int step = dy * stride + dx;
        int pos = y * stride + x;

        int q0 = buf[pos];
        int q1 = buf[pos + step];
        int p0 = buf[pos - step];
        int p1 = buf[pos - step * 2];

        int bdShift = bitDepth - 8;
        int limitBd = limit << bdShift;
        int blimitBd = blimit << bdShift;

        if (Math.Abs(p1 - p0) > limitBd ||
            Math.Abs(q1 - q0) > limitBd ||
            Math.Abs(p0 - q0) * 2 + (Math.Abs(p1 - q1) >> 1) > blimitBd)
        {
            return;
        }

        int filterLen = filterSize == 4 ? 4
            : plane != 0 ? 6
            : filterSize == 8 ? 8
            : 16;

        int p2 = 0, q2 = 0, p3 = 0, q3 = 0;
        if (filterLen >= 6)
        {
            p2 = buf[pos - step * 3];
            q2 = buf[pos + step * 2];
            if (Math.Abs(p2 - p1) > limitBd || Math.Abs(q2 - q1) > limitBd)
                return;
        }
        if (filterLen >= 8)
        {
            p3 = buf[pos - step * 4];
            q3 = buf[pos + step * 3];
            if (Math.Abs(p3 - p2) > limitBd || Math.Abs(q3 - q2) > limitBd)
                return;
        }

        int threshBd = thresh << bdShift;
        bool hev = Math.Abs(p1 - p0) > threshBd || Math.Abs(q1 - q0) > threshBd;

        bool flat = false;
        int flatThresh = 1 << bdShift;
        if (filterSize >= 8)
        {
            flat = Math.Abs(p1 - p0) <= flatThresh &&
                   Math.Abs(q1 - q0) <= flatThresh &&
                   Math.Abs(p2 - p0) <= flatThresh &&
                   Math.Abs(q2 - q0) <= flatThresh;
            if (flat && filterLen >= 8)
            {
                flat = Math.Abs(p3 - p0) <= flatThresh &&
                       Math.Abs(q3 - q0) <= flatThresh;
            }
        }

        if (filterSize == 4 || !flat)
        {
            NarrowFilter(buf, pos, step, bitDepth, hev, p1, p0, q0, q1);
            return;
        }

        bool flat2 = false;
        if (filterSize >= 16)
        {
            int p4 = buf[pos - step * 5];
            int p5 = buf[pos - step * 6];
            int p6 = buf[pos - step * 7];
            int q4 = buf[pos + step * 4];
            int q5 = buf[pos + step * 5];
            int q6 = buf[pos + step * 6];
            flat2 = Math.Abs(p4 - p0) <= flatThresh &&
                    Math.Abs(q4 - q0) <= flatThresh &&
                    Math.Abs(p5 - p0) <= flatThresh &&
                    Math.Abs(q5 - q0) <= flatThresh &&
                    Math.Abs(p6 - p0) <= flatThresh &&
                    Math.Abs(q6 - q0) <= flatThresh;
        }

        int log2Size = (filterSize == 8 || !flat2) ? 3 : 4;
        WideFilter(buf, pos, step, plane, log2Size);
    }

    private static void NarrowFilter(ushort[] buf, int pos, int step, int bitDepth, bool hev, int p1, int p0, int q0, int q1)
    {
        int half = 1 << (bitDepth - 1);
        int min = -half;
        int max = half - 1;

        int ps1 = p1 - half;
        int ps0 = p0 - half;
        int qs0 = q0 - half;
        int qs1 = q1 - half;

        int filter = hev ? Av1Constants.Clip3(min, max, ps1 - qs1) : 0;
        filter = Av1Constants.Clip3(min, max, filter + 3 * (qs0 - ps0));
        int filter1 = Av1Constants.Clip3(min, max, filter + 4) >> 3;
        int filter2 = Av1Constants.Clip3(min, max, filter + 3) >> 3;

        buf[pos] = (ushort)(Av1Constants.Clip3(min, max, qs0 - filter1) + half);
        buf[pos - step] = (ushort)(Av1Constants.Clip3(min, max, ps0 + filter2) + half);

        if (!hev)
        {
            int f = Av1Constants.Round2(filter1, 1);
            buf[pos + step] = (ushort)(Av1Constants.Clip3(min, max, qs1 - f) + half);
            buf[pos - step * 2] = (ushort)(Av1Constants.Clip3(min, max, ps1 + f) + half);
        }
    }

    private static void WideFilter(ushort[] buf, int pos, int step, int plane, int log2Size)
    {
        int n = log2Size == 4 ? 6 : plane == 0 ? 3 : 2;
        int n2 = (log2Size == 3 && plane == 0) ? 0 : 1;

        // Output i is Round2(sum over j in [-n, n] of tap(j) * s[Clip3(-(n + 1), n, i + j)], log2Size), with tap 2 for
        // |j| <= n2 and 1 otherwise. With e[m] = s[Clip3(-(n + 1), n, m)] for m in [-2n, 2n), that sum is the window
        // e[i - n .. i + n] plus e[i - n2 .. i + n2] once more; the window slides by one sample per output. The same
        // integers are added, so the outputs are exactly the per-tap sums. e[k] is held at index k + 2n.
        int o = 2 * n;
        Span<int> e = stackalloc int[24];
        for (int m = -o; m < o; m++)
            e[m + o] = buf[pos + Av1Constants.Clip3(-(n + 1), n, m) * step];
        int window = 0;
        for (int m = -o; m <= 0; m++)
            window += e[m + o];
        Span<ushort> f = stackalloc ushort[12];
        for (int i = -n; i < n; i++)
        {
            int t = window + e[i + o] + (n2 == 1 ? e[i - 1 + o] + e[i + 1 + o] : 0);
            f[i + n] = (ushort)Av1Constants.Round2(t, log2Size);
            if (i + 1 < n)
                window += e[i + n + 1 + o] - e[i - n + o];
        }
        for (int i = -n; i < n; i++)
            buf[pos + i * step] = f[i + n];
    }
}
