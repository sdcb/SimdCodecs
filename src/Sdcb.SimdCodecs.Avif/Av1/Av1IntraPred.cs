namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 intra prediction (§7.11.2), palette prediction (§7.11.4), and chroma-from-luma (§7.11.5).
/// </summary>
internal static partial class Av1IntraPred
{
    private static readonly int[,] IntraEdgeKernel =
    {
        { 0, 4, 8, 4, 0 },
        { 0, 5, 6, 5, 0 },
        { 2, 4, 4, 4, 2 },
    };

    /// <summary>
    /// Intra prediction into <paramref name="planeBuf"/> at (<paramref name="x"/>, <paramref name="y"/>) (§7.11.2).
    /// </summary>
    internal static void PredictIntra(
        ushort[] planeBuf,
        int stride,
        int bitDepth,
        bool enableIntraEdgeFilter,
        int plane,
        int x,
        int y,
        bool haveLeft,
        bool haveAbove,
        bool haveAboveRight,
        bool haveBelowLeft,
        int mode,
        int angleDelta,
        bool useFilterIntra,
        int filterIntraMode,
        bool isSmoothFilterType,
        int log2W,
        int log2H,
        int maxX,
        int maxY)
    {
        int w = 1 << log2W;
        int h = 1 << log2H;

        // Index +2 maps logical [-2 .. 2*(w+h)-2] into [0 .. 2*(w+h)]. Max w+h = 128 => max len = 258.
        Span<int> aboveBuf = stackalloc int[260];
        Span<int> leftBuf = stackalloc int[260];
        const int Off = 2;

        int wh = w + h;
        if (!haveAbove && haveLeft)
        {
            int fill = planeBuf[y * stride + (x - 1)];
            aboveBuf.Slice(Off, wh).Fill(fill);
        }
        else if (!haveAbove && !haveLeft)
        {
            int fill = (1 << (bitDepth - 1)) - 1;
            aboveBuf.Slice(Off, wh).Fill(fill);
        }
        else
        {
            // Samples x .. aboveLimit of the row above, then the last one repeated. Plain loops: most edges are short.
            int aboveLimit = Math.Min(maxX, x + (haveAboveRight ? 2 * w : w) - 1);
            int rowOff = (y - 1) * stride;
            int direct = Math.Min(aboveLimit - x + 1, wh);
            int i = 0;
            if (direct >= 16)
            {
                WidenEdge(planeBuf.AsSpan(rowOff + x, direct), aboveBuf.Slice(Off, direct));
                i = direct;
            }
            for (; i < direct; i++)
                aboveBuf[Off + i] = planeBuf[rowOff + x + i];
            int last = planeBuf[rowOff + aboveLimit];
            for (; i < wh; i++)
                aboveBuf[Off + i] = last;
        }

        if (!haveLeft && haveAbove)
        {
            int fill = planeBuf[(y - 1) * stride + x];
            leftBuf.Slice(Off, wh).Fill(fill);
        }
        else if (!haveLeft && !haveAbove)
        {
            int fill = (1 << (bitDepth - 1)) + 1;
            leftBuf.Slice(Off, wh).Fill(fill);
        }
        else
        {
            int leftLimit = Math.Min(maxY, y + (haveBelowLeft ? 2 * h : h) - 1);
            int direct = Math.Min(leftLimit - y + 1, wh);
            int col = y * stride + (x - 1);
            int i = 0;
            for (; i < direct; i++)
                leftBuf[Off + i] = planeBuf[col + i * stride];
            int last = planeBuf[leftLimit * stride + (x - 1)];
            for (; i < wh; i++)
                leftBuf[Off + i] = last;
        }

        int corner;
        if (haveAbove && haveLeft)
            corner = planeBuf[(y - 1) * stride + (x - 1)];
        else if (haveAbove)
            corner = planeBuf[(y - 1) * stride + x];
        else if (haveLeft)
            corner = planeBuf[y * stride + (x - 1)];
        else
            corner = 1 << (bitDepth - 1);

        aboveBuf[Off - 1] = corner;
        leftBuf[Off - 1] = corner;

        if (plane == 0 && useFilterIntra)
        {
            PredictFilterIntra(planeBuf, stride, bitDepth, x, y, w, h, filterIntraMode, aboveBuf, leftBuf, Off);
            return;
        }

        if (mode is >= Av1Constants.VPred and <= Av1Constants.D67Pred)
        {
            PredictDirectional(
                planeBuf, stride, bitDepth, enableIntraEdgeFilter,
                x, y, haveLeft, haveAbove, mode, angleDelta, isSmoothFilterType,
                w, h, maxX, maxY, aboveBuf, leftBuf, Off);
            return;
        }

        if (mode is Av1Constants.SmoothPred or Av1Constants.SmoothVPred or Av1Constants.SmoothHPred)
        {
            PredictSmooth(planeBuf, stride, x, y, mode, log2W, log2H, w, h, aboveBuf, leftBuf, Off);
            return;
        }

        if (mode == Av1Constants.DcPred)
        {
            PredictDc(planeBuf, stride, bitDepth, x, y, haveLeft, haveAbove, log2W, log2H, w, h, aboveBuf, leftBuf, Off);
            return;
        }

        // PAETH_PRED (§7.11.2.2)
        PredictPaeth(planeBuf, stride, x, y, w, h, aboveBuf, leftBuf, Off);
    }

    private static void PredictPaeth(ushort[] dst, int stride, int x, int y, int w, int h, Span<int> above, Span<int> left, int off)
    {
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated && w >= 4)
        {
            PredictPaethVector(dst, stride, x, y, w, h, above, left, off);
            return;
        }
        PredictPaethScalar(dst, stride, x, y, w, h, above, left, off);
    }

    internal static void PredictPaethScalar(ushort[] dst, int stride, int x, int y, int w, int h, Span<int> above, Span<int> left, int off)
    {
        int tl = above[off - 1];
        for (int i = 0; i < h; i++)
        {
            int l = left[off + i];
            int rowOff = (y + i) * stride + x;
            for (int j = 0; j < w; j++)
            {
                int a = above[off + j];
                int baseVal = a + l - tl;
                int pLeft = Math.Abs(baseVal - l);
                int pTop = Math.Abs(baseVal - a);
                int pTopLeft = Math.Abs(baseVal - tl);
                dst[rowOff + j] = (ushort)(pLeft <= pTop && pLeft <= pTopLeft ? l : pTop <= pTopLeft ? a : tl);
            }
        }
    }

    private static void PredictDc(
        ushort[] dst, int stride, int bitDepth, int x, int y,
        bool haveLeft, bool haveAbove, int log2W, int log2H, int w, int h,
        Span<int> above, Span<int> left, int off)
    {
        int avg;
        if (haveLeft && haveAbove)
        {
            int sum = 0;
            for (int k = 0; k < h; k++) sum += left[off + k];
            for (int k = 0; k < w; k++) sum += above[off + k];
            sum += (w + h) >> 1;
            avg = sum / (w + h);
        }
        else if (haveLeft)
        {
            int sum = 0;
            for (int k = 0; k < h; k++) sum += left[off + k];
            avg = Av1Constants.Clip1((sum + (h >> 1)) >> log2H, bitDepth);
        }
        else if (haveAbove)
        {
            int sum = 0;
            for (int k = 0; k < w; k++) sum += above[off + k];
            avg = Av1Constants.Clip1((sum + (w >> 1)) >> log2W, bitDepth);
        }
        else
        {
            avg = 1 << (bitDepth - 1);
        }

        ushort val = (ushort)avg;
        for (int i = 0; i < h; i++)
            dst.AsSpan((y + i) * stride + x, w).Fill(val);
    }

    private static byte[] GetSmWeights(int log2) => log2 switch
    {
        2 => Av1Tables.SmWeights4,
        3 => Av1Tables.SmWeights8,
        4 => Av1Tables.SmWeights16,
        5 => Av1Tables.SmWeights32,
        _ => Av1Tables.SmWeights64,
    };

    private static void PredictSmooth(
        ushort[] dst, int stride, int x, int y, int mode, int log2W, int log2H, int w, int h,
        Span<int> above, Span<int> left, int off)
    {
        if (mode == Av1Constants.SmoothPred)
        {
            byte[] wx = GetSmWeights(log2W);
            byte[] wy = GetSmWeights(log2H);
            int leftBottom = left[off + h - 1];
            int aboveRight = above[off + w - 1];
            for (int i = 0; i < h; i++)
            {
                int rowOff = (y + i) * stride + x;
                int wyi = wy[i];
                int wyiInv = 256 - wyi;
                int li = left[off + i];
                for (int j = 0; j < w; j++)
                {
                    int wxj = wx[j];
                    int s = wyi * above[off + j] + wyiInv * leftBottom + wxj * li + (256 - wxj) * aboveRight;
                    dst[rowOff + j] = (ushort)Av1Constants.Round2(s, 9);
                }
            }
        }
        else if (mode == Av1Constants.SmoothVPred)
        {
            byte[] wy = GetSmWeights(log2H);
            int leftBottom = left[off + h - 1];
            for (int i = 0; i < h; i++)
            {
                int rowOff = (y + i) * stride + x;
                int wyi = wy[i];
                int wyiInv = 256 - wyi;
                for (int j = 0; j < w; j++)
                    dst[rowOff + j] = (ushort)Av1Constants.Round2(wyi * above[off + j] + wyiInv * leftBottom, 8);
            }
        }
        else // SMOOTH_H_PRED
        {
            byte[] wx = GetSmWeights(log2W);
            int aboveRight = above[off + w - 1];
            for (int i = 0; i < h; i++)
            {
                int rowOff = (y + i) * stride + x;
                int li = left[off + i];
                for (int j = 0; j < w; j++)
                {
                    int wxj = wx[j];
                    dst[rowOff + j] = (ushort)Av1Constants.Round2(wxj * li + (256 - wxj) * aboveRight, 8);
                }
            }
        }
    }

    private static void PredictFilterIntra(
        ushort[] dst, int stride, int bitDepth, int x, int y, int w, int h,
        int filterIntraMode, Span<int> above, Span<int> left, int off)
    {
        Span<int> p = stackalloc int[7];
        int modeBase = filterIntraMode * 56; // 8 * 7
        int w4 = w >> 2;
        int h2 = h >> 1;

        for (int i2 = 0; i2 < h2; i2++)
        {
            int py = i2 << 1;
            for (int j4 = 0; j4 < w4; j4++)
            {
                int px = j4 << 2;
                for (int i = 0; i < 5; i++)
                {
                    if (i2 == 0)
                        p[i] = above[off + px + i - 1];
                    else if (j4 == 0 && i == 0)
                        p[i] = left[off + py - 1];
                    else
                        p[i] = dst[(y + py - 1) * stride + (x + px + i - 1)];
                }
                for (int i = 5; i < 7; i++)
                {
                    if (j4 == 0)
                        p[i] = left[off + py + i - 5];
                    else
                        p[i] = dst[(y + py + i - 5) * stride + (x + px - 1)];
                }

                for (int i1 = 0; i1 < 2; i1++)
                {
                    int rowOff = (y + py + i1) * stride + (x + px);
                    for (int j1 = 0; j1 < 4; j1++)
                    {
                        int tapOff = modeBase + ((i1 << 2) + j1) * 7;
                        int pr = 0;
                        for (int k = 0; k < 7; k++)
                            pr += Av1Tables.IntraFilterTaps[tapOff + k] * p[k];
                        dst[rowOff + j1] = (ushort)Av1Constants.Clip1(Av1Constants.Round2Signed(pr, 4), bitDepth);
                    }
                }
            }
        }
    }

    private static void PredictDirectional(
        ushort[] dst, int stride, int bitDepth, bool enableIntraEdgeFilter,
        int x, int y, bool haveLeft, bool haveAbove, int mode, int angleDelta, bool isSmoothFilterType,
        int w, int h, int maxX, int maxY, Span<int> above, Span<int> left, int off)
    {
        int pAngle = Av1Constants.ModeToAngle[mode] + angleDelta * 3;
        int upsampleAbove = 0;
        int upsampleLeft = 0;

        if (enableIntraEdgeFilter && pAngle != 90 && pAngle != 180)
        {
            if (pAngle > 90 && pAngle < 180 && (w + h) >= 24)
            {
                int corner = Av1Constants.Round2(left[off] * 5 + above[off - 1] * 6 + above[off] * 5, 4);
                left[off - 1] = corner;
                above[off - 1] = corner;
            }

            int filterType = isSmoothFilterType ? 1 : 0;
            if (haveAbove)
            {
                int strength = SelectEdgeFilterStrength(w, h, filterType, pAngle - 90);
                int numPx = Math.Min(w, maxX - x + 1) + (pAngle < 90 ? h : 0) + 1;
                if (strength > 0)
                    FilterIntraEdge(above, off, numPx, strength);
            }
            if (haveLeft)
            {
                int strength = SelectEdgeFilterStrength(w, h, filterType, pAngle - 180);
                int numPx = Math.Min(h, maxY - y + 1) + (pAngle > 180 ? w : 0) + 1;
                if (strength > 0)
                    FilterIntraEdge(left, off, numPx, strength);
            }

            upsampleAbove = SelectEdgeUpsample(w, h, filterType, pAngle - 90) ? 1 : 0;
            if (upsampleAbove != 0)
            {
                int numPx = w + (pAngle < 90 ? h : 0);
                UpsampleIntraEdge(above, off, numPx, bitDepth);
            }

            upsampleLeft = SelectEdgeUpsample(w, h, filterType, pAngle - 180) ? 1 : 0;
            if (upsampleLeft != 0)
            {
                int numPx = h + (pAngle > 180 ? w : 0);
                UpsampleIntraEdge(left, off, numPx, bitDepth);
            }
        }

        if (pAngle == 90)
        {
            for (int i = 0; i < h; i++)
            {
                int rowOff = (y + i) * stride + x;
                for (int j = 0; j < w; j++)
                    dst[rowOff + j] = (ushort)above[off + j];
            }
            return;
        }

        if (pAngle == 180)
        {
            for (int i = 0; i < h; i++)
                dst.AsSpan((y + i) * stride + x, w).Fill((ushort)left[off + i]);
            return;
        }

        int dx = pAngle < 90 ? Av1Tables.DrIntraDerivative[pAngle]
            : pAngle < 180 ? Av1Tables.DrIntraDerivative[180 - pAngle]
            : 0;
        int dy = pAngle > 90 && pAngle < 180 ? Av1Tables.DrIntraDerivative[pAngle - 90]
            : pAngle > 180 ? Av1Tables.DrIntraDerivative[270 - pAngle]
            : 0;

        if (pAngle < 90)
        {
            int maxBaseX = (w + h - 1) << upsampleAbove;
            for (int i = 0; i < h; i++)
            {
                int rowOff = (y + i) * stride + x;
                int idx = (i + 1) * dx;
                int base0 = idx >> (6 - upsampleAbove);
                int shift = ((idx << upsampleAbove) >> 1) & 0x1F;
                for (int j = 0; j < w; j++)
                {
                    int b = base0 + (j << upsampleAbove);
                    dst[rowOff + j] = (ushort)(b < maxBaseX
                        ? Av1Constants.Round2(above[off + b] * (32 - shift) + above[off + b + 1] * shift, 5)
                        : above[off + maxBaseX]);
                }
            }
        }
        else if (pAngle < 180)
        {
            int minBase = -(1 << upsampleAbove);
            for (int i = 0; i < h; i++)
            {
                int rowOff = (y + i) * stride + x;
                for (int j = 0; j < w; j++)
                {
                    int idx = (j << 6) - (i + 1) * dx;
                    int b = idx >> (6 - upsampleAbove);
                    if (b >= minBase)
                    {
                        int shift = ((idx << upsampleAbove) >> 1) & 0x1F;
                        dst[rowOff + j] = (ushort)Av1Constants.Round2(above[off + b] * (32 - shift) + above[off + b + 1] * shift, 5);
                    }
                    else
                    {
                        int idxY = (i << 6) - (j + 1) * dy;
                        int bY = idxY >> (6 - upsampleLeft);
                        int shiftY = ((idxY << upsampleLeft) >> 1) & 0x1F;
                        dst[rowOff + j] = (ushort)Av1Constants.Round2(left[off + bY] * (32 - shiftY) + left[off + bY + 1] * shiftY, 5);
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < h; i++)
            {
                int rowOff = (y + i) * stride + x;
                for (int j = 0; j < w; j++)
                {
                    int idx = (j + 1) * dy;
                    int b = (idx >> (6 - upsampleLeft)) + (i << upsampleLeft);
                    int shift = ((idx << upsampleLeft) >> 1) & 0x1F;
                    dst[rowOff + j] = (ushort)Av1Constants.Round2(left[off + b] * (32 - shift) + left[off + b + 1] * shift, 5);
                }
            }
        }
    }

    private static int SelectEdgeFilterStrength(int w, int h, int filterType, int delta)
    {
        int d = Math.Abs(delta);
        int blkWh = w + h;
        int strength = 0;
        if (filterType == 0)
        {
            if (blkWh <= 8)
            {
                if (d >= 56) strength = 1;
            }
            else if (blkWh <= 16)
            {
                if (d >= 40) strength = 1;
            }
            else if (blkWh <= 24)
            {
                if (d >= 8) strength = 1;
                if (d >= 16) strength = 2;
                if (d >= 32) strength = 3;
            }
            else if (blkWh <= 32)
            {
                strength = 1;
                if (d >= 4) strength = 2;
                if (d >= 32) strength = 3;
            }
            else
            {
                strength = 3;
            }
        }
        else
        {
            if (blkWh <= 8)
            {
                if (d >= 40) strength = 1;
                if (d >= 64) strength = 2;
            }
            else if (blkWh <= 16)
            {
                if (d >= 20) strength = 1;
                if (d >= 48) strength = 2;
            }
            else if (blkWh <= 24)
            {
                if (d >= 4) strength = 3;
            }
            else
            {
                strength = 3;
            }
        }
        return strength;
    }

    private static bool SelectEdgeUpsample(int w, int h, int filterType, int delta)
    {
        int d = Math.Abs(delta);
        if (d <= 0 || d >= 40) return false;
        int blkWh = w + h;
        return filterType == 0 ? blkWh <= 16 : blkWh <= 8;
    }

    private static void FilterIntraEdge(Span<int> buf, int off, int sz, int strength)
    {
        Span<int> edge = stackalloc int[sz];
        for (int i = 0; i < sz; i++)
            edge[i] = buf[off + i - 1];

        int kIdx = strength - 1;
        for (int i = 1; i < sz; i++)
        {
            int s = 0;
            for (int j = 0; j < 5; j++)
            {
                int k = Av1Constants.Clip3(0, sz - 1, i - 2 + j);
                s += IntraEdgeKernel[kIdx, j] * edge[k];
            }
            buf[off + i - 1] = (s + 8) >> 4;
        }
    }

    private static void UpsampleIntraEdge(Span<int> buf, int off, int numPx, int bitDepth)
    {
        Span<int> dup = stackalloc int[numPx + 3];
        dup[0] = buf[off - 1];
        for (int i = -1; i < numPx; i++)
            dup[i + 2] = buf[off + i];
        dup[numPx + 2] = buf[off + numPx - 1];

        buf[off - 2] = dup[0];
        for (int i = 0; i < numPx; i++)
        {
            int s = -dup[i] + 9 * dup[i + 1] + 9 * dup[i + 2] - dup[i + 3];
            buf[off + 2 * i - 1] = Av1Constants.Clip1(Av1Constants.Round2(s, 4), bitDepth);
            buf[off + 2 * i] = dup[i + 2];
        }
    }

    /// <summary>Palette prediction (§7.11.4).</summary>
    internal static void PredictPalette(
        ushort[] dst, int stride, int startX, int startY, int txRelX4, int txRelY4,
        int txSz, ReadOnlySpan<int> palette, byte[] colorMap, int mapStride)
    {
        int w = Av1Constants.TxWidth[txSz];
        int h = Av1Constants.TxHeight[txSz];
        int baseX = txRelX4 * 4;
        int baseY = txRelY4 * 4;
        for (int i = 0; i < h; i++)
        {
            int dstRow = (startY + i) * stride + startX;
            int mapRow = (baseY + i) * mapStride + baseX;
            for (int j = 0; j < w; j++)
                dst[dstRow + j] = (ushort)palette[colorMap[mapRow + j]];
        }
    }

    /// <summary>Chroma from Luma prediction (§7.11.5).</summary>
    internal static void PredictChromaFromLuma(
        ushort[] yBuf, int yStride,
        ushort[] cBuf, int cStride,
        int bitDepth, int subX, int subY,
        int startX, int startY, int txSz, int alpha,
        int maxLumaW, int maxLumaH)
    {
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            PredictChromaFromLumaVector(yBuf, yStride, cBuf, cStride, bitDepth, subX, subY, startX, startY, txSz, alpha, maxLumaW, maxLumaH);
            return;
        }
        PredictChromaFromLumaScalar(yBuf, yStride, cBuf, cStride, bitDepth, subX, subY, startX, startY, txSz, alpha, maxLumaW, maxLumaH);
    }

    internal static void PredictChromaFromLumaScalar(
        ushort[] yBuf, int yStride,
        ushort[] cBuf, int cStride,
        int bitDepth, int subX, int subY,
        int startX, int startY, int txSz, int alpha,
        int maxLumaW, int maxLumaH)
    {
        int w = Av1Constants.TxWidth[txSz];
        int h = Av1Constants.TxHeight[txSz];
        Span<int> l = stackalloc int[w * h];

        int lumaAvg = 0;
        int shift = 3 - subX - subY;
        int maxLY = maxLumaH - (1 << subY);
        int maxLX = maxLumaW - (1 << subX);

        for (int i = 0; i < h; i++)
        {
            int lumaY = Math.Min((startY + i) << subY, maxLY);
            for (int j = 0; j < w; j++)
            {
                int lumaX = Math.Min((startX + j) << subX, maxLX);
                int t = 0;
                for (int dy = 0; dy <= subY; dy++)
                    for (int dx = 0; dx <= subX; dx++)
                        t += yBuf[(lumaY + dy) * yStride + (lumaX + dx)];
                int v = t << shift;
                l[i * w + j] = v;
                lumaAvg += v;
            }
        }

        lumaAvg = Av1Constants.Round2(lumaAvg, Av1Constants.TxWidthLog2[txSz] + Av1Constants.TxHeightLog2[txSz]);

        for (int i = 0; i < h; i++)
        {
            int cRow = (startY + i) * cStride + startX;
            int lRow = i * w;
            for (int j = 0; j < w; j++)
            {
                int dc = cBuf[cRow + j];
                int scaledLuma = Av1Constants.Round2Signed(alpha * (l[lRow + j] - lumaAvg), 6);
                cBuf[cRow + j] = (ushort)Av1Constants.Clip1(dc + scaledLuma, bitDepth);
            }
        }
    }
}
