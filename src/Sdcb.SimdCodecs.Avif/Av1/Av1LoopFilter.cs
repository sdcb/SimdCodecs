namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 deblocking loop filter (§7.14).
/// Vertical edges first, then horizontal edges, across all planes.
/// </summary>
internal static class Av1LoopFilter
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

            for (int pass = 0; pass < 2; pass++)
            {
                int dx = pass == 0 ? 1 : 0;
                int dy = pass == 0 ? 0 : 1;

                for (int row = 0; row < miRows; row += rowStep)
                {
                    int y = row * 4;
                    if (y >= fh.FrameHeight) continue;
                    if (pass == 1 && y == 0) continue;

                    int adjRow = row | subY;
                    int prevRow = adjRow - (dy << subY);
                    int yP = y >> subY;
                    int txRow = adjRow >> subY;
                    int prevTxRow = prevRow >> subY;

                    for (int col = 0; col < miCols; col += colStep)
                    {
                        int x = col * 4;
                        if (x >= fh.FrameWidth) continue;
                        if (pass == 0 && x == 0) continue;

                        int adjCol = col | subX;
                        int prevCol = adjCol - (dx << subX);
                        int xP = x >> subX;

                        int txSz = lfTx[txRow * lfCols + (adjCol >> subX)];
                        bool isTxEdge = pass == 0
                            ? (xP % Av1Constants.TxWidth[txSz]) == 0
                            : (yP % Av1Constants.TxHeight[txSz]) == 0;
                        if (!isTxEdge) continue;

                        int adjIdx = adjRow * miCols + adjCol;
                        int planeSize = Av1Constants.SubsampledSize(st.MiSizes[adjIdx], subX, subY);
                        bool isBlockEdge = pass == 0
                            ? (xP % Av1Constants.BlockWidth[planeSize]) == 0
                            : (yP % Av1Constants.BlockHeight[planeSize]) == 0;
                        bool skip = st.Skips[adjIdx] != 0;
                        bool isIntra = st.RefFrames[adjIdx * 2] <= Av1Constants.IntraFrame;
                        if (!isBlockEdge && skip && !isIntra)
                            continue;

                        int prevTxSz = lfTx[prevTxRow * lfCols + (prevCol >> subX)];
                        int baseSize = pass == 0
                            ? Math.Min(Av1Constants.TxWidth[prevTxSz], Av1Constants.TxWidth[txSz])
                            : Math.Min(Av1Constants.TxHeight[prevTxSz], Av1Constants.TxHeight[txSz]);
                        int filterSize = plane == 0 ? Math.Min(16, baseSize) : Math.Min(8, baseSize);

                        int lvl = GetFilterLevel(fh, st, adjRow, adjCol, plane, pass);
                        if (lvl == 0)
                            lvl = GetFilterLevel(fh, st, prevRow, prevCol, plane, pass);
                        if (lvl == 0)
                            continue;

                        int shift = fh.LoopFilterSharpness > 4 ? 2 : fh.LoopFilterSharpness > 0 ? 1 : 0;
                        int limit = fh.LoopFilterSharpness > 0
                            ? Av1Constants.Clip3(1, 9 - fh.LoopFilterSharpness, lvl >> shift)
                            : Math.Max(1, lvl >> shift);
                        int blimit = 2 * (lvl + 2) + limit;
                        int thresh = lvl >> 4;

                        for (int i = 0; i < 4; i++)
                        {
                            FilterSample(
                                planeBuf, stride, bitDepth, plane,
                                xP + dy * i, yP + dx * i,
                                limit, blimit, thresh, dx, dy, filterSize);
                        }
                    }
                }
            }
        }
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

        Span<ushort> f = stackalloc ushort[12];
        for (int i = -n; i < n; i++)
        {
            int t = 0;
            for (int j = -n; j <= n; j++)
            {
                int p = Av1Constants.Clip3(-(n + 1), n, i + j);
                int tap = Math.Abs(j) <= n2 ? 2 : 1;
                t += buf[pos + p * step] * tap;
            }
            f[i + n] = (ushort)Av1Constants.Round2(t, log2Size);
        }
        for (int i = -n; i < n; i++)
            buf[pos + i * step] = f[i + n];
    }
}
