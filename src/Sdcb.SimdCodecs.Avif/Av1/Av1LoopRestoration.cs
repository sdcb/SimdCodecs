namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 Loop Restoration (§7.17): Wiener 7-tap separable filter and Self-Guided Restoration (SGR)
/// operating on 64-luma-row stripes offset by -8 rows.
/// </summary>
internal static class Av1LoopRestoration
{
    private const int SgrprojRstBits = 4;
    private const int SgrprojPrjBits = 7;
    private const int SgrprojMtableBits = 20;
    private const int SgrprojSgrBits = 8;
    private const int SgrprojRecipBits = 12;
    private const int FilterBits = 7;

    internal static void Apply(
        Av1SequenceHeader seq,
        Av1FrameHeader fh,
        Av1Picture pic,
        Av1FrameState st,
        ushort[][] deblockPlanes,
        ushort[][] cdefPlanes,
        ushort[][] dstPlanes)
    {
        int numPlanes = seq.NumPlanes;
        for (int p = 0; p < numPlanes; p++)
            cdefPlanes[p].AsSpan().CopyTo(dstPlanes[p]);

        if (!fh.UsesLr)
            return;

        int bitDepth = seq.BitDepth;
        int interRound0 = bitDepth == 12 ? 5 : 3;
        int interRound1 = bitDepth == 12 ? 9 : 11;

        for (int plane = 0; plane < numPlanes; plane++)
        {
            if (fh.FrameRestorationType[plane] == Av1Constants.RestoreNone)
                continue;

            int subX = plane == 0 ? 0 : seq.SubsamplingX;
            int subY = plane == 0 ? 0 : seq.SubsamplingY;
            int unitSize = fh.LoopRestorationSize[plane];
            int unitRows = st.LrUnitRows[plane];
            int unitCols = st.LrUnitCols[plane];
            int planeW = Av1Constants.Round2(fh.UpscaledWidth, subX);
            int planeH = Av1Constants.Round2(fh.FrameHeight, subY);
            int planeEndX = planeW - 1;
            int planeEndY = planeH - 1;

            int stride = pic.PlaneStrides[plane];
            ushort[] deblock = deblockPlanes[plane];
            ushort[] cdef = cdefPlanes[plane];
            ushort[] dst = dstPlanes[plane];

            int stripeH = 64 >> subY;
            int stripeOffset = 8 >> subY;

            // Process by (stripe, unitCol) sub-blocks — each sub-block belongs to a single stripe and a single restoration unit,
            // and its start y is always even (matching §7.17.3 box filter pass=0 odd-row parity).
            int maxStripe = (planeH + stripeOffset + stripeH - 1) / stripeH;
            int maxBlockW = Math.Min(unitSize * 2, planeW);
            var wienerInter = new int[(stripeH + 6) * maxBlockW];
            var sgrA = new int[(stripeH + 2) * (maxBlockW + 2)];
            var sgrB = new int[(stripeH + 2) * (maxBlockW + 2)];
            var sgrF0 = new int[stripeH * maxBlockW];
            var sgrF1 = new int[stripeH * maxBlockW];

            for (int stripeNum = 0; stripeNum < maxStripe; stripeNum++)
            {
                int stripeStartY = stripeNum * stripeH - stripeOffset;
                int stripeEndY = stripeStartY + stripeH - 1;
                int yStart = Math.Max(0, stripeStartY);
                int yEnd = Math.Min(planeH, stripeEndY + 1);
                if (yStart >= yEnd) continue;

                // Restoration unit row is constant across a stripe when unitSize >= stripeH;
                // when unitSize < stripeH (32x32 chroma), split vertically at unitSize boundaries.
                for (int y0 = yStart; y0 < yEnd;)
                {
                    int unitRow = Math.Min(unitRows - 1, (y0 + stripeOffset) / unitSize);
                    int nextUnitY = unitRow == unitRows - 1 ? planeH : (unitRow + 1) * unitSize - stripeOffset;
                    int y1 = Math.Min(yEnd, nextUnitY);
                    int h = y1 - y0;

                    for (int uc = 0; uc < unitCols; uc++)
                    {
                        int x0 = uc * unitSize;
                        int x1 = uc == unitCols - 1 ? planeW : Math.Min(planeW, (uc + 1) * unitSize);
                        int w = x1 - x0;
                        if (w <= 0) continue;

                        int uIdx = unitRow * unitCols + uc;
                        int rType = st.LrType[plane][uIdx];
                        if (rType == Av1Constants.RestoreNone) continue;

                        if (rType == Av1Constants.RestoreWiener)
                        {
                            ApplyWiener(
                                deblock, cdef, dst, stride, bitDepth,
                                interRound0, interRound1,
                                st.LrWiener[plane], uIdx * 6,
                                x0, y0, w, h,
                                stripeStartY, stripeEndY, planeEndX, planeEndY,
                                wienerInter);
                        }
                        else if (rType == Av1Constants.RestoreSgrproj)
                        {
                            ApplySgr(
                                deblock, cdef, dst, stride, bitDepth,
                                st.LrSgrSet[plane][uIdx],
                                st.LrSgrXqd[plane][uIdx * 2],
                                st.LrSgrXqd[plane][uIdx * 2 + 1],
                                x0, y0, w, h,
                                stripeStartY, stripeEndY, planeEndX, planeEndY,
                                sgrA, sgrB, sgrF0, sgrF1);
                        }
                    }

                    y0 = y1;
                }
            }
        }
    }

    private static int GetSourceSample(
        ushort[] deblock, ushort[] cdef, int stride,
        int x, int y, int stripeStartY, int stripeEndY, int planeEndX, int planeEndY)
    {
        if (x < 0) x = 0;
        else if (x > planeEndX) x = planeEndX;
        if (y < 0) y = 0;
        else if (y > planeEndY) y = planeEndY;

        if (y < stripeStartY)
        {
            if (y < stripeStartY - 2) y = stripeStartY - 2;
            return deblock[y * stride + x];
        }
        if (y > stripeEndY)
        {
            if (y > stripeEndY + 2) y = stripeEndY + 2;
            return deblock[y * stride + x];
        }
        return cdef[y * stride + x];
    }

    private static void ApplyWiener(
        ushort[] deblock, ushort[] cdef, ushort[] dst, int stride, int bitDepth,
        int interRound0, int interRound1,
        sbyte[] wienerCoeffs, int coeffOff,
        int x, int y, int w, int h,
        int stripeStartY, int stripeEndY, int planeEndX, int planeEndY,
        int[] intermediate)
    {
        Span<int> vFilter = stackalloc int[7];
        Span<int> hFilter = stackalloc int[7];
        BuildWienerTaps(wienerCoeffs, coeffOff, vFilter);
        BuildWienerTaps(wienerCoeffs, coeffOff + 3, hFilter);

        int offset = 1 << (bitDepth + FilterBits - interRound0 - 1);
        int limit = (1 << (bitDepth + 1 + FilterBits - interRound0)) - 1;
        int minInter = -offset;
        int maxInter = limit - offset;

        for (int r = 0; r < h + 6; r++)
        {
            int sy = y + r - 3;
            int rowOff = r * w;
            for (int c = 0; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 7; t++)
                    s += hFilter[t] * GetSourceSample(deblock, cdef, stride, x + c + t - 3, sy, stripeStartY, stripeEndY, planeEndX, planeEndY);
                intermediate[rowOff + c] = Av1Constants.Clip3(minInter, maxInter, Av1Constants.Round2(s, interRound0));
            }
        }

        for (int r = 0; r < h; r++)
        {
            int dstRow = (y + r) * stride + x;
            for (int c = 0; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 7; t++)
                    s += vFilter[t] * intermediate[(r + t) * w + c];
                dst[dstRow + c] = (ushort)Av1Constants.Clip1(Av1Constants.Round2(s, interRound1), bitDepth);
            }
        }
    }

    private static void BuildWienerTaps(sbyte[] coeffs, int off, Span<int> filter)
    {
        int c0 = coeffs[off];
        int c1 = coeffs[off + 1];
        int c2 = coeffs[off + 2];
        filter[0] = filter[6] = c0;
        filter[1] = filter[5] = c1;
        filter[2] = filter[4] = c2;
        filter[3] = 128 - 2 * (c0 + c1 + c2);
    }

    private static void ApplySgr(
        ushort[] deblock, ushort[] cdef, ushort[] dst, int stride, int bitDepth,
        int set, int w0, int w1,
        int x, int y, int w, int h,
        int stripeStartY, int stripeEndY, int planeEndX, int planeEndY,
        int[] bufA, int[] bufB, int[] flt0, int[] flt1)
    {
        int r0 = Av1Tables.SgrParams[set * 4];
        int eps0 = Av1Tables.SgrParams[set * 4 + 1];
        int r1 = Av1Tables.SgrParams[set * 4 + 2];
        int eps1 = Av1Tables.SgrParams[set * 4 + 3];

        if (r0 != 0)
            BoxFilter(deblock, cdef, stride, bitDepth, x, y, w, h, r0, eps0, pass: 0, stripeStartY, stripeEndY, planeEndX, planeEndY, bufA, bufB, flt0);
        if (r1 != 0)
            BoxFilter(deblock, cdef, stride, bitDepth, x, y, w, h, r1, eps1, pass: 1, stripeStartY, stripeEndY, planeEndX, planeEndY, bufA, bufB, flt1);

        int w2 = (1 << SgrprojPrjBits) - w0 - w1;
        for (int i = 0; i < h; i++)
        {
            int cdefRow = (y + i) * stride + x;
            int fltRow = i * w;
            for (int j = 0; j < w; j++)
            {
                int u = cdef[cdefRow + j] << SgrprojRstBits;
                int v = w1 * u;
                v += w0 * (r0 != 0 ? flt0[fltRow + j] : u);
                v += w2 * (r1 != 0 ? flt1[fltRow + j] : u);
                dst[cdefRow + j] = (ushort)Av1Constants.Clip1(Av1Constants.Round2(v, SgrprojRstBits + SgrprojPrjBits), bitDepth);
            }
        }
    }

    private static void BoxFilter(
        ushort[] deblock, ushort[] cdef, int stride, int bitDepth,
        int x, int y, int w, int h, int r, int eps, int pass,
        int stripeStartY, int stripeEndY, int planeEndX, int planeEndY,
        int[] bufA, int[] bufB, int[] outF)
    {
        int n = (2 * r + 1) * (2 * r + 1);
        int n2e = n * n * eps;
        int s = ((1 << SgrprojMtableBits) + (n2e / 2)) / n2e;
        int oneOverN = ((1 << SgrprojRecipBits) + (n / 2)) / n;
        int bdShift = bitDepth - 8;
        int abStride = w + 2;

        for (int i = -1; i < h + 1; i++)
        {
            // When pass == 0, only odd rows of A and B are ever read (§7.17.3 Note).
            if (pass == 0 && ((i & 1) == 0)) continue;

            int abRow = (i + 1) * abStride;
            for (int j = -1; j < w + 1; j++)
            {
                long a = 0;
                int b = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    int sy = y + i + dy;
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int c = GetSourceSample(deblock, cdef, stride, x + j + dx, sy, stripeStartY, stripeEndY, planeEndX, planeEndY);
                        a += (long)c * c;
                        b += c;
                    }
                }
                int aScaled = (int)Av1Constants.Round2(a, 2 * bdShift);
                int d = Av1Constants.Round2(b, bdShift);
                int p = Math.Max(0, aScaled * n - d * d);
                int z = (int)Av1Constants.Round2((long)p * s, SgrprojMtableBits);
                int a2 = z >= 255 ? 256
                       : z == 0 ? 1
                       : ((z << SgrprojSgrBits) + (z / 2)) / (z + 1);
                int b2 = ((1 << SgrprojSgrBits) - a2) * b * oneOverN;
                bufA[abRow + (j + 1)] = a2;
                bufB[abRow + (j + 1)] = Av1Constants.Round2(b2, SgrprojRecipBits);
            }
        }

        for (int i = 0; i < h; i++)
        {
            int shift = (pass == 0 && (i & 1) != 0) ? 4 : 5;
            int totalShift = SgrprojSgrBits + shift - SgrprojRstBits;
            int cdefRow = (y + i) * stride + x;
            int outRow = i * w;

            for (int j = 0; j < w; j++)
            {
                int a = 0;
                int b = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (pass == 0 && (((i + dy) & 1) == 0)) continue;
                    int abRow = (i + dy + 1) * abStride;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int weight = pass == 0
                            ? (dx == 0 ? 6 : 5)
                            : (dx == 0 || dy == 0 ? 4 : 3);
                        int abIdx = abRow + (j + dx + 1);
                        a += weight * bufA[abIdx];
                        b += weight * bufB[abIdx];
                    }
                }
                int v = a * cdef[cdefRow + j] + b;
                outF[outRow + j] = Av1Constants.Round2(v, totalShift);
            }
        }
    }
}
