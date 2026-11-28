using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
            cdefPlanes[p].AsSpan(0, pic.PlaneLengths[p]).CopyTo(dstPlanes[p]);

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
            int wienerLen = (stripeH + 6) * maxBlockW;
            int sgrAbLen = (stripeH + 2) * (maxBlockW + 2);
            int sgrFLen = stripeH * maxBlockW;
            int srcLen = (stripeH + 6) * (maxBlockW + 8);
            int capturedPlane = plane;

            if (maxStripe >= 4)
            {
                Parallel.For(
                    0,
                    maxStripe,
                    AvifDiagnostics.ParallelOptions,
                    () => (
                        wienerInter: ArrayPool<int>.Shared.Rent(wienerLen),
                        sgrA: ArrayPool<int>.Shared.Rent(sgrAbLen),
                        sgrB: ArrayPool<int>.Shared.Rent(sgrAbLen),
                        sgrF0: ArrayPool<int>.Shared.Rent(sgrFLen),
                        sgrF1: ArrayPool<int>.Shared.Rent(sgrFLen),
                        src: ArrayPool<ushort>.Shared.Rent(srcLen)),
                    (stripeNum, _, scratch) =>
                    {
                        ProcessStripe(
                            st, capturedPlane, bitDepth, interRound0, interRound1,
                            unitSize, unitRows, unitCols, planeW, planeH, planeEndX, planeEndY,
                            stride, deblock, cdef, dst, stripeH, stripeOffset, stripeNum,
                            scratch.wienerInter, scratch.sgrA, scratch.sgrB, scratch.sgrF0, scratch.sgrF1, scratch.src);
                        return scratch;
                    },
                    scratch =>
                    {
                        ArrayPool<int>.Shared.Return(scratch.wienerInter);
                        ArrayPool<int>.Shared.Return(scratch.sgrA);
                        ArrayPool<int>.Shared.Return(scratch.sgrB);
                        ArrayPool<int>.Shared.Return(scratch.sgrF0);
                        ArrayPool<int>.Shared.Return(scratch.sgrF1);
                        ArrayPool<ushort>.Shared.Return(scratch.src);
                    });
            }
            else
            {
                var wienerInter = ArrayPool<int>.Shared.Rent(wienerLen);
                var sgrA = ArrayPool<int>.Shared.Rent(sgrAbLen);
                var sgrB = ArrayPool<int>.Shared.Rent(sgrAbLen);
                var sgrF0 = ArrayPool<int>.Shared.Rent(sgrFLen);
                var sgrF1 = ArrayPool<int>.Shared.Rent(sgrFLen);
                var src = ArrayPool<ushort>.Shared.Rent(srcLen);
                try
                {
                    for (int stripeNum = 0; stripeNum < maxStripe; stripeNum++)
                    {
                        ProcessStripe(
                            st, capturedPlane, bitDepth, interRound0, interRound1,
                            unitSize, unitRows, unitCols, planeW, planeH, planeEndX, planeEndY,
                            stride, deblock, cdef, dst, stripeH, stripeOffset, stripeNum,
                            wienerInter, sgrA, sgrB, sgrF0, sgrF1, src);
                    }
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(wienerInter);
                    ArrayPool<int>.Shared.Return(sgrA);
                    ArrayPool<int>.Shared.Return(sgrB);
                    ArrayPool<int>.Shared.Return(sgrF0);
                    ArrayPool<int>.Shared.Return(sgrF1);
                    ArrayPool<ushort>.Shared.Return(src);
                }
            }
        }
    }

    private static void ProcessStripe(
        Av1FrameState st, int plane, int bitDepth, int interRound0, int interRound1,
        int unitSize, int unitRows, int unitCols, int planeW, int planeH, int planeEndX, int planeEndY,
        int stride, ushort[] deblock, ushort[] cdef, ushort[] dst, int stripeH, int stripeOffset, int stripeNum,
        int[] wienerInter, int[] sgrA, int[] sgrB, int[] sgrF0, int[] sgrF1, ushort[] src)
    {
        int stripeStartY = stripeNum * stripeH - stripeOffset;
        int stripeEndY = stripeStartY + stripeH - 1;
        int yStart = Math.Max(0, stripeStartY);
        int yEnd = Math.Min(planeH, stripeEndY + 1);
        if (yStart >= yEnd) return;

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

                if (rType != Av1Constants.RestoreWiener && rType != Av1Constants.RestoreSgrproj)
                    continue;
                BuildSource(deblock, cdef, stride, x0, y0, w, h, stripeStartY, stripeEndY, planeEndX, planeEndY, src);
                if (rType == Av1Constants.RestoreWiener)
                {
                    ApplyWiener(
                        src, dst, stride, bitDepth,
                        interRound0, interRound1,
                        st.LrWiener[plane], uIdx * 6,
                        x0, y0, w, h,
                        wienerInter);
                }
                else
                {
                    ApplySgr(
                        src, cdef, dst, stride, bitDepth,
                        st.LrSgrSet[plane][uIdx],
                        st.LrSgrXqd[plane][uIdx * 2],
                        st.LrSgrXqd[plane][uIdx * 2 + 1],
                        x0, y0, w, h,
                        sgrA, sgrB, sgrF0, sgrF1);
                }
            }

            y0 = y1;
        }
    }

    /// <summary>
    /// Fills <paramref name="src"/> (rows y - 3 .. y + h + 2, columns x - 4 .. x + w + 3, row stride w + 8) with the
    /// samples §7.17.6's get_source_sample returns: the column clamped to the plane, the row clamped to the plane and
    /// then to two rows beyond the stripe, rows outside the stripe from the deblocked frame, rows inside from CDEF's
    /// output. The source plane and row depend on the row alone, so they are chosen once per row.
    /// </summary>
    private static void BuildSource(
        ushort[] deblock, ushort[] cdef, int stride, int x, int y, int w, int h,
        int stripeStartY, int stripeEndY, int planeEndX, int planeEndY, ushort[] src)
    {
        int srcStride = w + 8;
        int cLo = Math.Clamp(4 - x, 0, srcStride);                  // first column inside the plane
        int cHi = Math.Clamp(planeEndX - x + 5, cLo, srcStride);    // first column past its right edge
        for (int r = 0; r < h + 6; r++)
        {
            int sy = Math.Clamp(y + r - 3, 0, planeEndY);
            ushort[] buf;
            if (sy < stripeStartY)
            {
                sy = Math.Max(sy, stripeStartY - 2);
                buf = deblock;
            }
            else if (sy > stripeEndY)
            {
                sy = Math.Min(sy, stripeEndY + 2);
                buf = deblock;
            }
            else
            {
                buf = cdef;
            }
            int rowOff = sy * stride;
            Span<ushort> dst = src.AsSpan(r * srcStride, srcStride);
            dst.Slice(0, cLo).Fill(buf[rowOff]);
            buf.AsSpan(rowOff + x - 4 + cLo, cHi - cLo).CopyTo(dst.Slice(cLo));
            dst.Slice(cHi).Fill(buf[rowOff + planeEndX]);
        }
    }

    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    private static void ApplyWiener(
        ushort[] src, ushort[] dst, int stride, int bitDepth,
        int interRound0, int interRound1,
        sbyte[] wienerCoeffs, int coeffOff,
        int x, int y, int w, int h,
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
        int maxSample = (1 << bitDepth) - 1;
        int srcStride = w + 8;

        // Horizontal: output column c of source row r reads source columns c - 3 .. c + 3, at src[r][c + 1 .. c + 7].
        for (int r = 0; r < h + 6; r++)
        {
            ReadOnlySpan<ushort> row = src.AsSpan(r * srcStride + 1, w + 6);
            Span<int> outRow = intermediate.AsSpan(r * w, w);
            int done = Vector128.IsHardwareAccelerated ? WienerRowHVector(row, outRow, hFilter, interRound0, minInter, maxInter) : 0;
            WienerRowHScalar(row, outRow, hFilter, interRound0, minInter, maxInter, done);
        }

        // Vertical: output row r reads intermediate rows r .. r + 6.
        for (int r = 0; r < h; r++)
        {
            Span<ushort> outRow = dst.AsSpan((y + r) * stride + x, w);
            int done = Vector128.IsHardwareAccelerated ? WienerRowVVector(intermediate, r, w, vFilter, interRound1, maxSample, outRow) : 0;
            WienerRowVScalar(intermediate, r, w, vFilter, interRound1, maxSample, outRow, done);
        }
    }

    internal static void WienerRowHScalar(ReadOnlySpan<ushort> row, Span<int> outRow, ReadOnlySpan<int> f, int round0, int minInter, int maxInter, int start)
    {
        for (int c = start; c < outRow.Length; c++)
        {
            int s = 0;
            for (int t = 0; t < 7; t++)
                s += f[t] * row[c + t];
            outRow[c] = Av1Constants.Clip3(minInter, maxInter, Av1Constants.Round2(s, round0));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int WienerRowHVector(ReadOnlySpan<ushort> row, Span<int> outRow, ReadOnlySpan<int> f, int round0, int minInter, int maxInter)
    {
        var f0 = Vector128.Create(f[0]); var f1 = Vector128.Create(f[1]); var f2 = Vector128.Create(f[2]); var f3 = Vector128.Create(f[3]);
        var f4 = Vector128.Create(f[4]); var f5 = Vector128.Create(f[5]); var f6 = Vector128.Create(f[6]);
        var rnd = Vector128.Create(1 << (round0 - 1));
        var lo = Vector128.Create(minInter);
        var hi = Vector128.Create(maxInter);
        int c = 0;
        for (; c + 4 <= outRow.Length; c += 4)
        {
            var s = f0 * Load4(row.Slice(c, 4)) + f1 * Load4(row.Slice(c + 1, 4)) + f2 * Load4(row.Slice(c + 2, 4))
                  + f3 * Load4(row.Slice(c + 3, 4)) + f4 * Load4(row.Slice(c + 4, 4)) + f5 * Load4(row.Slice(c + 5, 4))
                  + f6 * Load4(row.Slice(c + 6, 4));
            Vector128.Clamp(Vector128.ShiftRightArithmetic(s + rnd, round0), lo, hi).CopyTo(outRow.Slice(c, 4));
        }
        return c;
    }

    internal static void WienerRowVScalar(int[] inter, int r, int w, ReadOnlySpan<int> f, int round1, int maxSample, Span<ushort> outRow, int start)
    {
        for (int c = start; c < w; c++)
        {
            int s = 0;
            for (int t = 0; t < 7; t++)
                s += f[t] * inter[(r + t) * w + c];
            outRow[c] = (ushort)Math.Clamp(Av1Constants.Round2(s, round1), 0, maxSample);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int WienerRowVVector(int[] inter, int r, int w, ReadOnlySpan<int> f, int round1, int maxSample, Span<ushort> outRow)
    {
        var rnd = Vector128.Create(1 << (round1 - 1));
        var max = Vector128.Create(maxSample);
        ReadOnlySpan<int> i0 = inter.AsSpan(r * w, w), i1 = inter.AsSpan((r + 1) * w, w), i2 = inter.AsSpan((r + 2) * w, w);
        ReadOnlySpan<int> i3 = inter.AsSpan((r + 3) * w, w), i4 = inter.AsSpan((r + 4) * w, w), i5 = inter.AsSpan((r + 5) * w, w);
        ReadOnlySpan<int> i6 = inter.AsSpan((r + 6) * w, w);
        int c = 0;
        for (; c + 4 <= w; c += 4)
        {
            var s = f[0] * Vector128.Create(i0.Slice(c, 4)) + f[1] * Vector128.Create(i1.Slice(c, 4)) + f[2] * Vector128.Create(i2.Slice(c, 4))
                  + f[3] * Vector128.Create(i3.Slice(c, 4)) + f[4] * Vector128.Create(i4.Slice(c, 4)) + f[5] * Vector128.Create(i5.Slice(c, 4))
                  + f[6] * Vector128.Create(i6.Slice(c, 4));
            Store4(Vector128.Clamp(Vector128.ShiftRightArithmetic(s + rnd, round1), Vector128<int>.Zero, max), outRow.Slice(c, 4));
        }
        return c;
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
        ushort[] src, ushort[] cdef, ushort[] dst, int stride, int bitDepth,
        int set, int w0, int w1,
        int x, int y, int w, int h,
        int[] bufA, int[] bufB, int[] flt0, int[] flt1)
    {
        int r0 = Av1Tables.SgrParams[set * 4];
        int eps0 = Av1Tables.SgrParams[set * 4 + 1];
        int r1 = Av1Tables.SgrParams[set * 4 + 2];
        int eps1 = Av1Tables.SgrParams[set * 4 + 3];

        if (r0 != 0)
            BoxFilter(src, cdef, stride, bitDepth, x, y, w, h, r0, eps0, pass: 0, bufA, bufB, flt0);
        if (r1 != 0)
            BoxFilter(src, cdef, stride, bitDepth, x, y, w, h, r1, eps1, pass: 1, bufA, bufB, flt1);

        int w2 = (1 << SgrprojPrjBits) - w0 - w1;
        int maxSample = (1 << bitDepth) - 1;
        for (int i = 0; i < h; i++)
        {
            int cdefRow = (y + i) * stride + x;
            ReadOnlySpan<ushort> u = cdef.AsSpan(cdefRow, w);
            ReadOnlySpan<int> f0 = r0 != 0 ? flt0.AsSpan(i * w, w) : default;
            ReadOnlySpan<int> f1 = r1 != 0 ? flt1.AsSpan(i * w, w) : default;
            Span<ushort> outRow = dst.AsSpan(cdefRow, w);
            int done = Vector128.IsHardwareAccelerated ? SgrCombineVector(u, f0, f1, w0, w1, w2, maxSample, outRow) : 0;
            SgrCombineScalar(u, f0, f1, w0, w1, w2, maxSample, outRow, done);
        }
    }

    /// <summary>Projection of §7.17.4: an absent filter (radius 0) contributes the source sample itself.</summary>
    internal static void SgrCombineScalar(ReadOnlySpan<ushort> cdefRow, ReadOnlySpan<int> f0, ReadOnlySpan<int> f1,
        int w0, int w1, int w2, int maxSample, Span<ushort> outRow, int start)
    {
        for (int j = start; j < outRow.Length; j++)
        {
            int u = cdefRow[j] << SgrprojRstBits;
            int v = w1 * u;
            v += w0 * (f0.IsEmpty ? u : f0[j]);
            v += w2 * (f1.IsEmpty ? u : f1[j]);
            outRow[j] = (ushort)Math.Clamp(Av1Constants.Round2(v, SgrprojRstBits + SgrprojPrjBits), 0, maxSample);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int SgrCombineVector(ReadOnlySpan<ushort> cdefRow, ReadOnlySpan<int> f0, ReadOnlySpan<int> f1,
        int w0, int w1, int w2, int maxSample, Span<ushort> outRow)
    {
        const int shift = SgrprojRstBits + SgrprojPrjBits;
        var vW0 = Vector128.Create(w0);
        var vW1 = Vector128.Create(w1);
        var vW2 = Vector128.Create(w2);
        var rnd = Vector128.Create(1 << (shift - 1));
        var max = Vector128.Create(maxSample);
        int j = 0;
        for (; j + 4 <= outRow.Length; j += 4)
        {
            var u = Vector128.ShiftLeft(Load4(cdefRow.Slice(j, 4)), SgrprojRstBits);
            var v = vW1 * u
                  + vW0 * (f0.IsEmpty ? u : Vector128.Create(f0.Slice(j, 4)))
                  + vW2 * (f1.IsEmpty ? u : Vector128.Create(f1.Slice(j, 4)));
            Store4(Vector128.Clamp(Vector128.ShiftRightArithmetic(v + rnd, shift), Vector128<int>.Zero, max), outRow.Slice(j, 4));
        }
        return j;
    }

    /// <summary>a2 of §7.17.3 for z in 0 .. 255 (z of 255 or more gives 256).</summary>
    private static readonly int[] SgrA2 = BuildSgrA2();

    private static int[] BuildSgrA2()
    {
        var t = new int[256];
        t[0] = 1;
        for (int z = 1; z < 255; z++)
            t[z] = ((z << SgrprojSgrBits) + (z / 2)) / (z + 1);
        t[255] = 256;
        return t;
    }

    /// <summary>
    /// Box filter process (§7.17.3). The (2r + 1)² sums of each A / B position are built from column sums over the
    /// window's rows, slid along the row; the integers are those of the per-position sums.
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    private static void BoxFilter(
        ushort[] src, ushort[] cdef, int stride, int bitDepth,
        int x, int y, int w, int h, int r, int eps, int pass,
        int[] bufA, int[] bufB, int[] outF)
    {
        int n = (2 * r + 1) * (2 * r + 1);
        int n2e = n * n * eps;
        int s = ((1 << SgrprojMtableBits) + (n2e / 2)) / n2e;
        int oneOverN = ((1 << SgrprojRecipBits) + (n / 2)) / n;
        int bdShift = bitDepth - 8;
        int abStride = w + 2;
        int srcStride = w + 8;
        Span<int> colB = stackalloc int[srcStride];
        Span<int> colA = stackalloc int[srcStride];

        for (int i = -1; i < h + 1; i++)
        {
            // When pass == 0, only odd rows of A and B are ever read (§7.17.3 Note).
            if (pass == 0 && ((i & 1) == 0)) continue;

            // Column sums over source rows i - r .. i + r (src row = frame row - y + 3).
            colB.Clear();
            colA.Clear();
            for (int dy = -r; dy <= r; dy++)
                AccumulateColumns(src.AsSpan((i + dy + 3) * srcStride, srcStride), colB, colA);

            // Position j (-1 .. w) covers source columns j - r .. j + r, at src columns j + 4 - r .. j + 4 + r.
            int b = 0, a = 0;
            for (int k = 3 - r; k < 3 + r; k++)
            {
                b += colB[k];
                a += colA[k];
            }
            int abRow = (i + 1) * abStride;
            for (int j = -1; j < w + 1; j++)
            {
                int add = j + 4 + r, sub = j + 4 - r;
                b += colB[add];
                a += colA[add];
                int aScaled = Av1Constants.Round2(a, 2 * bdShift);
                int d = Av1Constants.Round2(b, bdShift);
                int p = Math.Max(0, aScaled * n - d * d);
                int z = (int)Av1Constants.Round2((long)p * s, SgrprojMtableBits);
                int a2 = SgrA2[Math.Min(z, 255)];
                long b2 = (long)((1 << SgrprojSgrBits) - a2) * b * oneOverN; // 33 bits at 12-bit depth
                bufA[abRow + (j + 1)] = a2;
                bufB[abRow + (j + 1)] = (int)Av1Constants.Round2(b2, SgrprojRecipBits);
                b -= colB[sub];
                a -= colA[sub];
            }
        }

        for (int i = 0; i < h; i++)
        {
            int shift = (pass == 0 && (i & 1) != 0) ? 4 : 5;
            int totalShift = SgrprojSgrBits + shift - SgrprojRstBits;
            // 0: 3x3 with weights 4 / 3; 1: the row itself, 6 / 5; 2: the rows above and below, 6 / 5.
            int mode = pass != 0 ? 0 : (i & 1) != 0 ? 1 : 2;
            ReadOnlySpan<int> aUp = bufA.AsSpan(i * abStride, abStride), aMid = bufA.AsSpan((i + 1) * abStride, abStride), aDown = bufA.AsSpan((i + 2) * abStride, abStride);
            ReadOnlySpan<int> bUp = bufB.AsSpan(i * abStride, abStride), bMid = bufB.AsSpan((i + 1) * abStride, abStride), bDown = bufB.AsSpan((i + 2) * abStride, abStride);
            ReadOnlySpan<ushort> cdefRow = cdef.AsSpan((y + i) * stride + x, w);
            Span<int> outRow = outF.AsSpan(i * w, w);
            int done = Vector128.IsHardwareAccelerated ? SgrFilterRowVector(aUp, aMid, aDown, bUp, bMid, bDown, cdefRow, outRow, mode, totalShift) : 0;
            SgrFilterRowScalar(aUp, aMid, aDown, bUp, bMid, bDown, cdefRow, outRow, mode, totalShift, done);
        }
    }

    private static void AccumulateColumns(ReadOnlySpan<ushort> row, Span<int> colB, Span<int> colA)
    {
        int c = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; c + 4 <= row.Length; c += 4)
            {
                var v = Load4(row.Slice(c, 4));
                (Vector128.Create(colB.Slice(c, 4)) + v).CopyTo(colB.Slice(c, 4));
                (Vector128.Create(colA.Slice(c, 4)) + v * v).CopyTo(colA.Slice(c, 4));
            }
        }
        for (; c < row.Length; c++)
        {
            int v = row[c];
            colB[c] += v;
            colA[c] += v * v;
        }
    }

    /// <summary>
    /// One output row of the box filter: the weighted 3x3 (or 1x3 / 2x3) sums of A and B around each position,
    /// then <c>Round2(a · source + b, shift)</c>. Row spans start at position -1.
    /// </summary>
    internal static void SgrFilterRowScalar(ReadOnlySpan<int> aUp, ReadOnlySpan<int> aMid, ReadOnlySpan<int> aDown,
        ReadOnlySpan<int> bUp, ReadOnlySpan<int> bMid, ReadOnlySpan<int> bDown,
        ReadOnlySpan<ushort> cdefRow, Span<int> outRow, int mode, int totalShift, int start)
    {
        for (int j = start; j < outRow.Length; j++)
        {
            int a, b;
            if (mode == 0)
            {
                a = 4 * (aUp[j + 1] + aMid[j] + aMid[j + 1] + aMid[j + 2] + aDown[j + 1]) + 3 * (aUp[j] + aUp[j + 2] + aDown[j] + aDown[j + 2]);
                b = 4 * (bUp[j + 1] + bMid[j] + bMid[j + 1] + bMid[j + 2] + bDown[j + 1]) + 3 * (bUp[j] + bUp[j + 2] + bDown[j] + bDown[j + 2]);
            }
            else if (mode == 1)
            {
                a = 6 * aMid[j + 1] + 5 * (aMid[j] + aMid[j + 2]);
                b = 6 * bMid[j + 1] + 5 * (bMid[j] + bMid[j + 2]);
            }
            else
            {
                a = 6 * (aUp[j + 1] + aDown[j + 1]) + 5 * (aUp[j] + aUp[j + 2] + aDown[j] + aDown[j + 2]);
                b = 6 * (bUp[j + 1] + bDown[j + 1]) + 5 * (bUp[j] + bUp[j + 2] + bDown[j] + bDown[j + 2]);
            }
            outRow[j] = Av1Constants.Round2(a * cdefRow[j] + b, totalShift);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int SgrFilterRowVector(ReadOnlySpan<int> aUp, ReadOnlySpan<int> aMid, ReadOnlySpan<int> aDown,
        ReadOnlySpan<int> bUp, ReadOnlySpan<int> bMid, ReadOnlySpan<int> bDown,
        ReadOnlySpan<ushort> cdefRow, Span<int> outRow, int mode, int totalShift)
    {
        var rnd = Vector128.Create(1 << (totalShift - 1));
        int j = 0;
        for (; j + 4 <= outRow.Length; j += 4)
        {
            Vector128<int> a, b;
            if (mode == 0)
            {
                a = Weighted3x3(aUp, aMid, aDown, j);
                b = Weighted3x3(bUp, bMid, bDown, j);
            }
            else if (mode == 1)
            {
                a = Vector128.Create(aMid.Slice(j + 1, 4)) * 6 + (Vector128.Create(aMid.Slice(j, 4)) + Vector128.Create(aMid.Slice(j + 2, 4))) * 5;
                b = Vector128.Create(bMid.Slice(j + 1, 4)) * 6 + (Vector128.Create(bMid.Slice(j, 4)) + Vector128.Create(bMid.Slice(j + 2, 4))) * 5;
            }
            else
            {
                a = (Vector128.Create(aUp.Slice(j + 1, 4)) + Vector128.Create(aDown.Slice(j + 1, 4))) * 6
                  + (Vector128.Create(aUp.Slice(j, 4)) + Vector128.Create(aUp.Slice(j + 2, 4)) + Vector128.Create(aDown.Slice(j, 4)) + Vector128.Create(aDown.Slice(j + 2, 4))) * 5;
                b = (Vector128.Create(bUp.Slice(j + 1, 4)) + Vector128.Create(bDown.Slice(j + 1, 4))) * 6
                  + (Vector128.Create(bUp.Slice(j, 4)) + Vector128.Create(bUp.Slice(j + 2, 4)) + Vector128.Create(bDown.Slice(j, 4)) + Vector128.Create(bDown.Slice(j + 2, 4))) * 5;
            }
            var v = a * Load4(cdefRow.Slice(j, 4)) + b;
            Vector128.ShiftRightArithmetic(v + rnd, totalShift).CopyTo(outRow.Slice(j, 4));
        }
        return j;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Weighted3x3(ReadOnlySpan<int> up, ReadOnlySpan<int> mid, ReadOnlySpan<int> down, int j) =>
        (Vector128.Create(up.Slice(j + 1, 4)) + Vector128.Create(mid.Slice(j, 4)) + Vector128.Create(mid.Slice(j + 1, 4))
            + Vector128.Create(mid.Slice(j + 2, 4)) + Vector128.Create(down.Slice(j + 1, 4))) * 4
        + (Vector128.Create(up.Slice(j, 4)) + Vector128.Create(up.Slice(j + 2, 4)) + Vector128.Create(down.Slice(j, 4))
            + Vector128.Create(down.Slice(j + 2, 4))) * 3;

    /// <summary>Four samples widened to int lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load4(ReadOnlySpan<ushort> src4) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(MemoryMarshal.Read<ulong>(MemoryMarshal.AsBytes(src4.Slice(0, 4)))).AsUInt16()).AsInt32();

    /// <summary>Stores the four low 16-bit lanes of <paramref name="v"/> (values below 2^16).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(Vector128<int> v, Span<ushort> dst4) =>
        MemoryMarshal.Write(MemoryMarshal.AsBytes(dst4.Slice(0, 4)), Vector128.Narrow(v.AsUInt32(), v.AsUInt32()).AsUInt64().ToScalar());
}
