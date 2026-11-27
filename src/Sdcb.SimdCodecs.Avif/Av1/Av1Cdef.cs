using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 Constrained Directional Enhancement Filter (CDEF) (§7.15).
/// Vectorized with AVX2 (<see cref="Vector256{T}"/>), <see cref="Vector128{T}"/>, and scalar twin kernels.
/// </summary>
internal static class Av1Cdef
{
    private static readonly int[] DivTable = [0, 840, 420, 280, 210, 168, 140, 120, 105];

    private static readonly int[,] PriTaps = { { 4, 2 }, { 3, 3 } };
    private static readonly int[,] SecTaps = { { 2, 1 }, { 2, 1 } };

    private static readonly int[,,] CdefUvDir =
    {
        { { 0, 1, 2, 3, 4, 5, 6, 7 }, { 1, 2, 2, 2, 3, 4, 6, 0 } },
        { { 7, 0, 2, 4, 5, 6, 6, 6 }, { 0, 1, 2, 3, 4, 5, 6, 7 } },
    };

    /// <summary>
    /// Runs CDEF reading from <paramref name="srcPlanes"/> and writing into <paramref name="dstPlanes"/>.
    /// </summary>
    internal static void Apply(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState st, ushort[][] srcPlanes, ushort[][] dstPlanes)
    {
        int numPlanes = seq.NumPlanes;
        int bitDepth = seq.BitDepth;
        int coeffShift = bitDepth - 8;
        int miRows = fh.MiRows;
        int miCols = fh.MiCols;

        // First copy entire source planes to destination so unfiltered / skipped / padded pixels match.
        for (int p = 0; p < numPlanes; p++)
            srcPlanes[p].AsSpan(0, pic.PlaneLengths[p]).CopyTo(dstPlanes[p]);

        if (fh.CodedLossless || fh.AllowIntrabc || !seq.EnableCdef)
            return;

        int rowBlocks = (miRows + 1) >> 1;
        if (rowBlocks >= 16)
        {
            Parallel.For(0, rowBlocks, AvifDiagnostics.ParallelOptions, rb =>
                ApplyRow(seq, fh, pic, st, srcPlanes, dstPlanes, numPlanes, bitDepth, coeffShift, miRows, miCols, rb << 1));
        }
        else
        {
            for (int r = 0; r < miRows; r += 2)
                ApplyRow(seq, fh, pic, st, srcPlanes, dstPlanes, numPlanes, bitDepth, coeffShift, miRows, miCols, r);
        }
    }

    private static void ApplyRow(
        Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState st,
        ushort[][] srcPlanes, ushort[][] dstPlanes,
        int numPlanes, int bitDepth, int coeffShift, int miRows, int miCols, int r)
    {
        int cr = r >> 4;
        for (int c = 0; c < miCols; c += 2)
        {
            int cc = c >> 4;
            int idx = st.CdefIdx[cr * st.CdefCols + cc];
            if (idx < 0) continue;

            bool skip = st.Skips[r * miCols + c] != 0
                     && st.Skips[(r + 1) * miCols + c] != 0
                     && st.Skips[r * miCols + (c + 1)] != 0
                     && st.Skips[(r + 1) * miCols + (c + 1)] != 0;
            if (skip) continue;

            FindDirection(srcPlanes[0], pic.PlaneStrides[0], bitDepth, r, c, out int yDir, out int variance);

            // Luma
            int priStr = fh.CdefYPriStrength[idx] << coeffShift;
            int secStr = fh.CdefYSecStrength[idx] << coeffShift;
            int dir = priStr == 0 ? 0 : yDir;
            int varStr = (variance >> 6) != 0 ? Math.Min(Av1Constants.FloorLog2(variance >> 6), 12) : 0;
            priStr = variance != 0 ? ((priStr * (4 + varStr) + 8) >> 4) : 0;
            int damping = fh.CdefDamping + coeffShift;

            if (priStr != 0 || secStr != 0)
            {
                FilterBlock(
                    srcPlanes[0], dstPlanes[0], pic.PlaneStrides[0],
                    miRows, miCols, 0, 0, coeffShift,
                    r, c, priStr, secStr, damping, dir);
            }

            if (numPlanes == 1) continue;

            // Chroma U & V
            int uvPri = fh.CdefUvPriStrength[idx] << coeffShift;
            int uvSec = fh.CdefUvSecStrength[idx] << coeffShift;
            if (uvPri != 0 || uvSec != 0)
            {
                int uvDir = uvPri == 0 ? 0 : CdefUvDir[seq.SubsamplingX, seq.SubsamplingY, yDir];
                int uvDamping = fh.CdefDamping + coeffShift - 1;
                FilterBlock(
                    srcPlanes[1], dstPlanes[1], pic.PlaneStrides[1],
                    miRows, miCols, seq.SubsamplingX, seq.SubsamplingY, coeffShift,
                    r, c, uvPri, uvSec, uvDamping, uvDir);
                FilterBlock(
                    srcPlanes[2], dstPlanes[2], pic.PlaneStrides[2],
                    miRows, miCols, seq.SubsamplingX, seq.SubsamplingY, coeffShift,
                    r, c, uvPri, uvSec, uvDamping, uvDir);
            }
        }
    }

    private static void FindDirection(ushort[] yBuf, int stride, int bitDepth, int r, int c, out int yDir, out int variance)
    {
        int x0 = c << 2;
        int y0 = r << 2;
        int shift = bitDepth - 8;
        if (Vector128.IsHardwareAccelerated)
            FindDirectionV128(yBuf, stride, x0, y0, shift, out yDir, out variance);
        else
            FindDirectionScalar(yBuf, stride, x0, y0, shift, out yDir, out variance);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void FindDirectionV128(ushort[] yBuf, int stride, int x0, int y0, int shift, out int yDir, out int variance)
    {
        // One up-front bounds check proves all 8 rows x 8 cols in yBuf are in-bounds.
        int lastIdx = (y0 + 7) * stride + x0 + 7;
        if ((uint)lastIdx >= (uint)yBuf.Length || x0 < 0 || y0 < 0)
        {
            FindDirectionScalar(yBuf, stride, x0, y0, shift, out yDir, out variance);
            return;
        }

        // Accumulate directional partial sums in 16-bit signed shorts (max |partial| = 8 * 128 = 1024).
        // Each buffer holds 16 shorts; the highest index touched is 14 (p0 / p4: i + j, 7 + i - j and the 8-wide
        // stores at i <= 7 end at 14; p1 / p3 / p5 / p7 reach i + 3 <= 10), so all stay in range.
        short* p0 = stackalloc short[16];
        short* p1 = stackalloc short[16];
        short* p3 = stackalloc short[16];
        short* p4 = stackalloc short[16];
        short* p5 = stackalloc short[16];
        short* p7 = stackalloc short[16];
        Unsafe.InitBlockUnaligned(p0, 0, 32);
        Unsafe.InitBlockUnaligned(p1, 0, 32);
        Unsafe.InitBlockUnaligned(p3, 0, 32);
        Unsafe.InitBlockUnaligned(p4, 0, 32);
        Unsafe.InitBlockUnaligned(p5, 0, 32);
        Unsafe.InitBlockUnaligned(p7, 0, 32);

        Vector128<short> v128 = Vector128.Create((short)128);
        Vector128<short> p6 = Vector128<short>.Zero;
        Vector128<byte> revMask = Vector128.Create((byte)14, 15, 12, 13, 10, 11, 8, 9, 6, 7, 4, 5, 2, 3, 0, 1);
        int cost2 = 0;

        fixed (ushort* basePtr = yBuf)
        {
            ushort* rowPtr = basePtr + y0 * stride + x0;
            for (int i = 0; i < 8; i++)
            {
                Vector128<short> row = Vector128.ShiftRightLogical(Vector128.Load((short*)rowPtr), shift) - v128;
                rowPtr += stride;

                // Dir 6 (vertical): sum columns
                p6 += row;

                // Dir 0 (45 deg): p0[i + j] += row[j]
                Vector128.Store(Vector128.Load(p0 + i) + row, p0 + i);

                // Dir 4 (135 deg): p4[7 + i - j] += row[j] => store reversed row at p4 + i
                Vector128<short> rev = Vector128.Shuffle(row.AsByte(), revMask).AsInt16();
                Vector128.Store(Vector128.Load(p4 + i) + rev, p4 + i);

                // Dir 5: p5[3 - (i>>1) + j] += row[j]
                int off5 = 3 - (i >> 1);
                Vector128.Store(Vector128.Load(p5 + off5) + row, p5 + off5);

                // Dir 7: p7[(i>>1) + j] += row[j]
                int off7 = i >> 1;
                Vector128.Store(Vector128.Load(p7 + off7) + row, p7 + off7);

                // Pairwise horizontal sums for Dir 1, Dir 3, and Dir 2:
                // pair = [r0+r1, r2+r3, r4+r5, r6+r7]
                int s01 = row.GetElement(0) + row.GetElement(1);
                int s23 = row.GetElement(2) + row.GetElement(3);
                int s45 = row.GetElement(4) + row.GetElement(5);
                int s67 = row.GetElement(6) + row.GetElement(7);

                int rowSum = s01 + s23 + s45 + s67;
                cost2 += rowSum * rowSum;

                p1[i] = (short)(p1[i] + s01);
                p1[i + 1] = (short)(p1[i + 1] + s23);
                p1[i + 2] = (short)(p1[i + 2] + s45);
                p1[i + 3] = (short)(p1[i + 3] + s67);

                p3[3 + i] = (short)(p3[3 + i] + s01);
                p3[2 + i] = (short)(p3[2 + i] + s23);
                p3[1 + i] = (short)(p3[1 + i] + s45);
                p3[i] = (short)(p3[i] + s67);
            }
        }

        Span<int> cost = stackalloc int[8];
        cost[2] = cost2 * DivTable[8];

        int cost6 = 0;
        for (int i = 0; i < 8; i++)
        {
            int v = p6.GetElement(i);
            cost6 += v * v;
        }
        cost[6] = cost6 * DivTable[8];

        int c0 = p0[7] * p0[7] * DivTable[8];
        int c4 = p4[7] * p4[7] * DivTable[8];
        for (int i = 0; i < 7; i++)
        {
            int a0 = p0[i], b0 = p0[14 - i];
            c0 += (a0 * a0 + b0 * b0) * DivTable[i + 1];
            int a4 = p4[i], b4 = p4[14 - i];
            c4 += (a4 * a4 + b4 * b4) * DivTable[i + 1];
        }
        cost[0] = c0;
        cost[4] = c4;

        cost[1] = ComputeOddDirCost(p1);
        cost[3] = ComputeOddDirCost(p3);
        cost[5] = ComputeOddDirCost(p5);
        cost[7] = ComputeOddDirCost(p7);

        int bestCost = 0;
        yDir = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cost[i] > bestCost)
            {
                bestCost = cost[i];
                yDir = i;
            }
        }
        variance = (bestCost - cost[(yDir + 4) & 7]) >> 10;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int ComputeOddDirCost(short* p)
    {
        int mid = p[3] * p[3] + p[4] * p[4] + p[5] * p[5] + p[6] * p[6] + p[7] * p[7];
        return mid * DivTable[8]
            + (p[0] * p[0] + p[10] * p[10]) * DivTable[2]
            + (p[1] * p[1] + p[9] * p[9]) * DivTable[4]
            + (p[2] * p[2] + p[8] * p[8]) * DivTable[6];
    }

    private static void FindDirectionScalar(ushort[] yBuf, int stride, int x0, int y0, int shift, out int yDir, out int variance)
    {
        Span<int> partial = stackalloc int[8 * 15];
        partial.Clear();
        Span<int> cost = stackalloc int[8];
        cost.Clear();

        for (int i = 0; i < 8; i++)
        {
            int rowOff = (y0 + i) * stride + x0;
            for (int j = 0; j < 8; j++)
            {
                int x = (yBuf[rowOff + j] >> shift) - 128;
                partial[0 * 15 + (i + j)] += x;
                partial[1 * 15 + (i + (j >> 1))] += x;
                partial[2 * 15 + i] += x;
                partial[3 * 15 + (3 + i - (j >> 1))] += x;
                partial[4 * 15 + (7 + i - j)] += x;
                partial[5 * 15 + (3 - (i >> 1) + j)] += x;
                partial[6 * 15 + j] += x;
                partial[7 * 15 + ((i >> 1) + j)] += x;
            }
        }

        for (int i = 0; i < 8; i++)
        {
            int p2 = partial[2 * 15 + i];
            int p6 = partial[6 * 15 + i];
            cost[2] += p2 * p2;
            cost[6] += p6 * p6;
        }
        cost[2] *= DivTable[8];
        cost[6] *= DivTable[8];

        for (int i = 0; i < 7; i++)
        {
            int a0 = partial[0 * 15 + i], b0 = partial[0 * 15 + (14 - i)];
            cost[0] += (a0 * a0 + b0 * b0) * DivTable[i + 1];
            int a4 = partial[4 * 15 + i], b4 = partial[4 * 15 + (14 - i)];
            cost[4] += (a4 * a4 + b4 * b4) * DivTable[i + 1];
        }
        cost[0] += partial[0 * 15 + 7] * partial[0 * 15 + 7] * DivTable[8];
        cost[4] += partial[4 * 15 + 7] * partial[4 * 15 + 7] * DivTable[8];

        for (int i = 1; i < 8; i += 2)
        {
            int baseOff = i * 15;
            for (int j = 0; j < 5; j++)
            {
                int v = partial[baseOff + 3 + j];
                cost[i] += v * v;
            }
            cost[i] *= DivTable[8];
            for (int j = 0; j < 3; j++)
            {
                int a = partial[baseOff + j];
                int b = partial[baseOff + 10 - j];
                cost[i] += (a * a + b * b) * DivTable[2 * j + 2];
            }
        }

        int bestCost = 0;
        yDir = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cost[i] > bestCost)
            {
                bestCost = cost[i];
                yDir = i;
            }
        }
        variance = (bestCost - cost[(yDir + 4) & 7]) >> 10;
    }

    private static void FilterBlock(
        ushort[] src, ushort[] dst, int stride,
        int miRows, int miCols, int subX, int subY, int coeffShift,
        int r, int c, int priStr, int secStr, int damping, int dir)
    {
        int x0 = (c * 4) >> subX;
        int y0 = (r * 4) >> subY;
        int w = 8 >> subX;
        int h = 8 >> subY;
        int maxX = (miCols * 4) >> subX;
        int maxY = (miRows * 4) >> subY;

        int tapSet = (priStr >> coeffShift) & 1;
        int priDampAdj = priStr != 0 ? Math.Max(0, damping - Av1Constants.FloorLog2(priStr)) : 0;
        int secDampAdj = secStr != 0 ? Math.Max(0, damping - Av1Constants.FloorLog2(secStr)) : 0;

        int dirSec1 = (dir + 6) & 7;
        int dirSec2 = (dir + 2) & 7;

        // Fast SIMD paths for interior blocks (all +/-2 neighbors guaranteed in-bounds).
        if (x0 >= 2 && y0 >= 2 && x0 + w + 2 <= maxX && y0 + h + 2 <= maxY)
        {
            if (w == 8 && (h & 1) == 0 && Vector256.IsHardwareAccelerated)
            {
                FilterInterior8xH_V256(src, dst, stride, x0, y0, h, tapSet, priStr, priDampAdj, secStr, secDampAdj, dir, dirSec1, dirSec2);
                return;
            }
            if (w == 8 && Vector128.IsHardwareAccelerated)
            {
                FilterInterior8xH_V128(src, dst, stride, x0, y0, h, tapSet, priStr, priDampAdj, secStr, secDampAdj, dir, dirSec1, dirSec2);
                return;
            }
            if (w == 4 && h == 4 && Vector128.IsHardwareAccelerated)
            {
                FilterInterior4x4_V128(src, dst, stride, x0, y0, tapSet, priStr, priDampAdj, secStr, secDampAdj, dir, dirSec1, dirSec2);
                return;
            }
        }

        FilterBlockScalar(src, dst, stride, x0, y0, w, h, maxX, maxY, tapSet, priStr, priDampAdj, secStr, secDampAdj, dir, dirSec1, dirSec2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> ConstrainV256(Vector256<short> diff, Vector256<short> threshold, int dampAdj)
    {
        Vector256<short> absDiff = Vector256.Abs(diff);
        Vector256<short> mag = Vector256.Max(
            Vector256<short>.Zero,
            Vector256.Min(absDiff, threshold - Vector256.ShiftRightArithmetic(absDiff, dampAdj)));
        Vector256<short> sign = Vector256.ShiftRightArithmetic(diff, 15);
        return (mag ^ sign) - sign;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ConstrainV128(Vector128<short> diff, Vector128<short> threshold, int dampAdj)
    {
        Vector128<short> absDiff = Vector128.Abs(diff);
        Vector128<short> mag = Vector128.Max(
            Vector128<short>.Zero,
            Vector128.Min(absDiff, threshold - Vector128.ShiftRightArithmetic(absDiff, dampAdj)));
        Vector128<short> sign = Vector128.ShiftRightArithmetic(diff, 15);
        return (mag ^ sign) - sign;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void FilterInterior8xH_V256(
        ushort[] src, ushort[] dst, int stride,
        int x0, int y0, int h,
        int tapSet, int priStr, int priDampAdj, int secStr, int secDampAdj,
        int dir, int dirSec1, int dirSec2)
    {
        // Range proof: every tap reads src at (y0 + r + dy) * stride + x0 + c + dx with r < h, c < 8 and (dy, dx) from
        // CdefDirections, whose offsets lie in [-2, 2]; so all reads fall in [minOff, maxOff] (a vector of 8 at column
        // x0 + c + dx ends at x0 + 9). Writes fall at (y0 + r) * stride + x0 + c. Out of range: the scalar path.
        int minOff = (y0 - 2) * stride + (x0 - 2);
        int maxOff = (y0 + h + 1) * stride + (x0 + 9);
        if (minOff < 0 || maxOff >= src.Length || (y0 + h - 1) * stride + x0 + 7 >= dst.Length)
        {
            FilterBlockScalar(src, dst, stride, x0, y0, 8, h, int.MaxValue, int.MaxValue, tapSet, priStr, priDampAdj, secStr, secDampAdj, dir, dirSec1, dirSec2);
            return;
        }

        int pTap0 = PriTaps[tapSet, 0], pTap1 = PriTaps[tapSet, 1];
        int sTap0 = SecTaps[tapSet, 0], sTap1 = SecTaps[tapSet, 1];

        int pOff0 = Av1Tables.CdefDirections[(dir * 2) * 2] * stride + Av1Tables.CdefDirections[(dir * 2) * 2 + 1];
        int pOff1 = Av1Tables.CdefDirections[(dir * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dir * 2 + 1) * 2 + 1];
        int s1Off0 = Av1Tables.CdefDirections[(dirSec1 * 2) * 2] * stride + Av1Tables.CdefDirections[(dirSec1 * 2) * 2 + 1];
        int s1Off1 = Av1Tables.CdefDirections[(dirSec1 * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dirSec1 * 2 + 1) * 2 + 1];
        int s2Off0 = Av1Tables.CdefDirections[(dirSec2 * 2) * 2] * stride + Av1Tables.CdefDirections[(dirSec2 * 2) * 2 + 1];
        int s2Off1 = Av1Tables.CdefDirections[(dirSec2 * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dirSec2 * 2 + 1) * 2 + 1];

        Vector256<short> vPriStr = Vector256.Create((short)priStr);
        Vector256<short> vSecStr = Vector256.Create((short)secStr);
        Vector256<short> vPTap0 = Vector256.Create((short)pTap0);
        Vector256<short> vPTap1 = Vector256.Create((short)pTap1);
        Vector256<short> vSTap0 = Vector256.Create((short)sTap0);
        Vector256<short> vSTap1 = Vector256.Create((short)sTap1);
        Vector256<short> vEight = Vector256.Create((short)8);

        fixed (ushort* srcBase = src)
        fixed (ushort* dstBase = dst)
        {
            short* sBase = (short*)srcBase;
            short* dBase = (short*)dstBase;

            // Process 2 rows (16 pixels) per Vector256 iteration.
            for (int i = 0; i < h; i += 2)
            {
                short* r0 = sBase + (y0 + i) * stride + x0;
                short* r1 = r0 + stride;
                Vector256<short> x = Vector256.Create(Vector128.Load(r0), Vector128.Load(r1));
                Vector256<short> sum = Vector256<short>.Zero;
                Vector256<short> max = x;
                Vector256<short> min = x;

                if (priStr != 0)
                {
                    Vector256<short> p0m = Vector256.Create(Vector128.Load(r0 - pOff0), Vector128.Load(r1 - pOff0));
                    Vector256<short> p0p = Vector256.Create(Vector128.Load(r0 + pOff0), Vector128.Load(r1 + pOff0));
                    max = Vector256.Max(max, Vector256.Max(p0m, p0p));
                    min = Vector256.Min(min, Vector256.Min(p0m, p0p));
                    sum += vPTap0 * (ConstrainV256(p0m - x, vPriStr, priDampAdj) + ConstrainV256(p0p - x, vPriStr, priDampAdj));

                    Vector256<short> p1m = Vector256.Create(Vector128.Load(r0 - pOff1), Vector128.Load(r1 - pOff1));
                    Vector256<short> p1p = Vector256.Create(Vector128.Load(r0 + pOff1), Vector128.Load(r1 + pOff1));
                    max = Vector256.Max(max, Vector256.Max(p1m, p1p));
                    min = Vector256.Min(min, Vector256.Min(p1m, p1p));
                    sum += vPTap1 * (ConstrainV256(p1m - x, vPriStr, priDampAdj) + ConstrainV256(p1p - x, vPriStr, priDampAdj));
                }
                else
                {
                    // Even when priStr == 0, primary neighbors still participate in min/max clamping per §7.15.3!
                    Vector256<short> p0m = Vector256.Create(Vector128.Load(r0 - pOff0), Vector128.Load(r1 - pOff0));
                    Vector256<short> p0p = Vector256.Create(Vector128.Load(r0 + pOff0), Vector128.Load(r1 + pOff0));
                    Vector256<short> p1m = Vector256.Create(Vector128.Load(r0 - pOff1), Vector128.Load(r1 - pOff1));
                    Vector256<short> p1p = Vector256.Create(Vector128.Load(r0 + pOff1), Vector128.Load(r1 + pOff1));
                    max = Vector256.Max(max, Vector256.Max(Vector256.Max(p0m, p0p), Vector256.Max(p1m, p1p)));
                    min = Vector256.Min(min, Vector256.Min(Vector256.Min(p0m, p0p), Vector256.Min(p1m, p1p)));
                }

                if (secStr != 0)
                {
                    Vector256<short> a0m = Vector256.Create(Vector128.Load(r0 - s1Off0), Vector128.Load(r1 - s1Off0));
                    Vector256<short> a0p = Vector256.Create(Vector128.Load(r0 + s1Off0), Vector128.Load(r1 + s1Off0));
                    Vector256<short> b0m = Vector256.Create(Vector128.Load(r0 - s2Off0), Vector128.Load(r1 - s2Off0));
                    Vector256<short> b0p = Vector256.Create(Vector128.Load(r0 + s2Off0), Vector128.Load(r1 + s2Off0));
                    max = Vector256.Max(max, Vector256.Max(Vector256.Max(a0m, a0p), Vector256.Max(b0m, b0p)));
                    min = Vector256.Min(min, Vector256.Min(Vector256.Min(a0m, a0p), Vector256.Min(b0m, b0p)));
                    sum += vSTap0 * (
                        ConstrainV256(a0m - x, vSecStr, secDampAdj) + ConstrainV256(a0p - x, vSecStr, secDampAdj) +
                        ConstrainV256(b0m - x, vSecStr, secDampAdj) + ConstrainV256(b0p - x, vSecStr, secDampAdj));

                    Vector256<short> a1m = Vector256.Create(Vector128.Load(r0 - s1Off1), Vector128.Load(r1 - s1Off1));
                    Vector256<short> a1p = Vector256.Create(Vector128.Load(r0 + s1Off1), Vector128.Load(r1 + s1Off1));
                    Vector256<short> b1m = Vector256.Create(Vector128.Load(r0 - s2Off1), Vector128.Load(r1 - s2Off1));
                    Vector256<short> b1p = Vector256.Create(Vector128.Load(r0 + s2Off1), Vector128.Load(r1 + s2Off1));
                    max = Vector256.Max(max, Vector256.Max(Vector256.Max(a1m, a1p), Vector256.Max(b1m, b1p)));
                    min = Vector256.Min(min, Vector256.Min(Vector256.Min(a1m, a1p), Vector256.Min(b1m, b1p)));
                    sum += vSTap1 * (
                        ConstrainV256(a1m - x, vSecStr, secDampAdj) + ConstrainV256(a1p - x, vSecStr, secDampAdj) +
                        ConstrainV256(b1m - x, vSecStr, secDampAdj) + ConstrainV256(b1p - x, vSecStr, secDampAdj));
                }
                else
                {
                    Vector256<short> a0m = Vector256.Create(Vector128.Load(r0 - s1Off0), Vector128.Load(r1 - s1Off0));
                    Vector256<short> a0p = Vector256.Create(Vector128.Load(r0 + s1Off0), Vector128.Load(r1 + s1Off0));
                    Vector256<short> b0m = Vector256.Create(Vector128.Load(r0 - s2Off0), Vector128.Load(r1 - s2Off0));
                    Vector256<short> b0p = Vector256.Create(Vector128.Load(r0 + s2Off0), Vector128.Load(r1 + s2Off0));
                    Vector256<short> a1m = Vector256.Create(Vector128.Load(r0 - s1Off1), Vector128.Load(r1 - s1Off1));
                    Vector256<short> a1p = Vector256.Create(Vector128.Load(r0 + s1Off1), Vector128.Load(r1 + s1Off1));
                    Vector256<short> b1m = Vector256.Create(Vector128.Load(r0 - s2Off1), Vector128.Load(r1 - s2Off1));
                    Vector256<short> b1p = Vector256.Create(Vector128.Load(r0 + s2Off1), Vector128.Load(r1 + s2Off1));
                    max = Vector256.Max(max, Vector256.Max(Vector256.Max(Vector256.Max(a0m, a0p), Vector256.Max(b0m, b0p)),
                                                           Vector256.Max(Vector256.Max(a1m, a1p), Vector256.Max(b1m, b1p))));
                    min = Vector256.Min(min, Vector256.Min(Vector256.Min(Vector256.Min(a0m, a0p), Vector256.Min(b0m, b0p)),
                                                           Vector256.Min(Vector256.Min(a1m, a1p), Vector256.Min(b1m, b1p))));
                }

                Vector256<short> delta = Vector256.ShiftRightArithmetic(sum + Vector256.ShiftRightArithmetic(sum, 15) + vEight, 4);
                Vector256<short> res = Vector256.Min(max, Vector256.Max(min, x + delta));

                short* d0 = dBase + (y0 + i) * stride + x0;
                Vector128.Store(res.GetLower(), d0);
                Vector128.Store(res.GetUpper(), d0 + stride);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void FilterInterior8xH_V128(
        ushort[] src, ushort[] dst, int stride,
        int x0, int y0, int h,
        int tapSet, int priStr, int priDampAdj, int secStr, int secDampAdj,
        int dir, int dirSec1, int dirSec2)
    {
        // Range proof: every tap reads src at (y0 + r + dy) * stride + x0 + c + dx with r < h, c < 8 and (dy, dx) from
        // CdefDirections, whose offsets lie in [-2, 2]; so all reads fall in [minOff, maxOff] (a vector of 8 at column
        // x0 + c + dx ends at x0 + 9). Writes fall at (y0 + r) * stride + x0 + c. Out of range: the scalar path.
        int minOff = (y0 - 2) * stride + (x0 - 2);
        int maxOff = (y0 + h + 1) * stride + (x0 + 9);
        if (minOff < 0 || maxOff >= src.Length || (y0 + h - 1) * stride + x0 + 7 >= dst.Length)
        {
            FilterBlockScalar(src, dst, stride, x0, y0, 8, h, int.MaxValue, int.MaxValue, tapSet, priStr, priDampAdj, secStr, secDampAdj, dir, dirSec1, dirSec2);
            return;
        }

        int pOff0 = Av1Tables.CdefDirections[(dir * 2) * 2] * stride + Av1Tables.CdefDirections[(dir * 2) * 2 + 1];
        int pOff1 = Av1Tables.CdefDirections[(dir * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dir * 2 + 1) * 2 + 1];
        int s1Off0 = Av1Tables.CdefDirections[(dirSec1 * 2) * 2] * stride + Av1Tables.CdefDirections[(dirSec1 * 2) * 2 + 1];
        int s1Off1 = Av1Tables.CdefDirections[(dirSec1 * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dirSec1 * 2 + 1) * 2 + 1];
        int s2Off0 = Av1Tables.CdefDirections[(dirSec2 * 2) * 2] * stride + Av1Tables.CdefDirections[(dirSec2 * 2) * 2 + 1];
        int s2Off1 = Av1Tables.CdefDirections[(dirSec2 * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dirSec2 * 2 + 1) * 2 + 1];

        Vector128<short> vPriStr = Vector128.Create((short)priStr);
        Vector128<short> vSecStr = Vector128.Create((short)secStr);
        Vector128<short> vPTap0 = Vector128.Create((short)PriTaps[tapSet, 0]);
        Vector128<short> vPTap1 = Vector128.Create((short)PriTaps[tapSet, 1]);
        Vector128<short> vSTap0 = Vector128.Create((short)SecTaps[tapSet, 0]);
        Vector128<short> vSTap1 = Vector128.Create((short)SecTaps[tapSet, 1]);
        Vector128<short> vEight = Vector128.Create((short)8);

        fixed (ushort* srcBase = src)
        fixed (ushort* dstBase = dst)
        {
            short* sBase = (short*)srcBase;
            short* dBase = (short*)dstBase;

            for (int i = 0; i < h; i++)
            {
                short* r0 = sBase + (y0 + i) * stride + x0;
                Vector128<short> x = Vector128.Load(r0);
                Vector128<short> sum = Vector128<short>.Zero;
                Vector128<short> max = x;
                Vector128<short> min = x;

                Vector128<short> p0m = Vector128.Load(r0 - pOff0);
                Vector128<short> p0p = Vector128.Load(r0 + pOff0);
                Vector128<short> p1m = Vector128.Load(r0 - pOff1);
                Vector128<short> p1p = Vector128.Load(r0 + pOff1);
                max = Vector128.Max(max, Vector128.Max(Vector128.Max(p0m, p0p), Vector128.Max(p1m, p1p)));
                min = Vector128.Min(min, Vector128.Min(Vector128.Min(p0m, p0p), Vector128.Min(p1m, p1p)));
                if (priStr != 0)
                {
                    sum += vPTap0 * (ConstrainV128(p0m - x, vPriStr, priDampAdj) + ConstrainV128(p0p - x, vPriStr, priDampAdj));
                    sum += vPTap1 * (ConstrainV128(p1m - x, vPriStr, priDampAdj) + ConstrainV128(p1p - x, vPriStr, priDampAdj));
                }

                Vector128<short> a0m = Vector128.Load(r0 - s1Off0);
                Vector128<short> a0p = Vector128.Load(r0 + s1Off0);
                Vector128<short> b0m = Vector128.Load(r0 - s2Off0);
                Vector128<short> b0p = Vector128.Load(r0 + s2Off0);
                Vector128<short> a1m = Vector128.Load(r0 - s1Off1);
                Vector128<short> a1p = Vector128.Load(r0 + s1Off1);
                Vector128<short> b1m = Vector128.Load(r0 - s2Off1);
                Vector128<short> b1p = Vector128.Load(r0 + s2Off1);
                max = Vector128.Max(max, Vector128.Max(Vector128.Max(Vector128.Max(a0m, a0p), Vector128.Max(b0m, b0p)),
                                                       Vector128.Max(Vector128.Max(a1m, a1p), Vector128.Max(b1m, b1p))));
                min = Vector128.Min(min, Vector128.Min(Vector128.Min(Vector128.Min(a0m, a0p), Vector128.Min(b0m, b0p)),
                                                       Vector128.Min(Vector128.Min(a1m, a1p), Vector128.Min(b1m, b1p))));
                if (secStr != 0)
                {
                    sum += vSTap0 * (
                        ConstrainV128(a0m - x, vSecStr, secDampAdj) + ConstrainV128(a0p - x, vSecStr, secDampAdj) +
                        ConstrainV128(b0m - x, vSecStr, secDampAdj) + ConstrainV128(b0p - x, vSecStr, secDampAdj));
                    sum += vSTap1 * (
                        ConstrainV128(a1m - x, vSecStr, secDampAdj) + ConstrainV128(a1p - x, vSecStr, secDampAdj) +
                        ConstrainV128(b1m - x, vSecStr, secDampAdj) + ConstrainV128(b1p - x, vSecStr, secDampAdj));
                }

                Vector128<short> delta = Vector128.ShiftRightArithmetic(sum + Vector128.ShiftRightArithmetic(sum, 15) + vEight, 4);
                Vector128.Store(Vector128.Min(max, Vector128.Max(min, x + delta)), dBase + (y0 + i) * stride + x0);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static unsafe void FilterInterior4x4_V128(
        ushort[] src, ushort[] dst, int stride,
        int x0, int y0,
        int tapSet, int priStr, int priDampAdj, int secStr, int secDampAdj,
        int dir, int dirSec1, int dirSec2)
    {
        // Range proof: as for the 8xH kernels with a 4x4 block — reads in [minOff, maxOff] (CdefDirections offsets lie in
        // [-2, 2]), writes at (y0 + r) * stride + x0 + c, r, c < 4. Out of range: the scalar path.
        int minOff = (y0 - 2) * stride + (x0 - 2);
        int maxOff = (y0 + 5) * stride + (x0 + 5);
        if (minOff < 0 || maxOff >= src.Length || (y0 + 3) * stride + x0 + 3 >= dst.Length)
        {
            FilterBlockScalar(src, dst, stride, x0, y0, 4, 4, int.MaxValue, int.MaxValue, tapSet, priStr, priDampAdj, secStr, secDampAdj, dir, dirSec1, dirSec2);
            return;
        }

        int pOff0 = Av1Tables.CdefDirections[(dir * 2) * 2] * stride + Av1Tables.CdefDirections[(dir * 2) * 2 + 1];
        int pOff1 = Av1Tables.CdefDirections[(dir * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dir * 2 + 1) * 2 + 1];
        int s1Off0 = Av1Tables.CdefDirections[(dirSec1 * 2) * 2] * stride + Av1Tables.CdefDirections[(dirSec1 * 2) * 2 + 1];
        int s1Off1 = Av1Tables.CdefDirections[(dirSec1 * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dirSec1 * 2 + 1) * 2 + 1];
        int s2Off0 = Av1Tables.CdefDirections[(dirSec2 * 2) * 2] * stride + Av1Tables.CdefDirections[(dirSec2 * 2) * 2 + 1];
        int s2Off1 = Av1Tables.CdefDirections[(dirSec2 * 2 + 1) * 2] * stride + Av1Tables.CdefDirections[(dirSec2 * 2 + 1) * 2 + 1];

        Vector128<short> vPriStr = Vector128.Create((short)priStr);
        Vector128<short> vSecStr = Vector128.Create((short)secStr);
        Vector128<short> vPTap0 = Vector128.Create((short)PriTaps[tapSet, 0]);
        Vector128<short> vPTap1 = Vector128.Create((short)PriTaps[tapSet, 1]);
        Vector128<short> vSTap0 = Vector128.Create((short)SecTaps[tapSet, 0]);
        Vector128<short> vSTap1 = Vector128.Create((short)SecTaps[tapSet, 1]);
        Vector128<short> vEight = Vector128.Create((short)8);

        fixed (ushort* srcBase = src)
        fixed (ushort* dstBase = dst)
        {
            short* sBase = (short*)srcBase;
            short* dBase = (short*)dstBase;

            // Pack 2 rows of 4 pixels into one Vector128<short> (2 iterations for 4x4 chroma).
            for (int i = 0; i < 4; i += 2)
            {
                short* r0 = sBase + (y0 + i) * stride + x0;
                short* r1 = r0 + stride;
                Vector128<short> x = Load2x4(r0, r1);
                Vector128<short> sum = Vector128<short>.Zero;
                Vector128<short> max = x;
                Vector128<short> min = x;

                Vector128<short> p0m = Load2x4(r0 - pOff0, r1 - pOff0);
                Vector128<short> p0p = Load2x4(r0 + pOff0, r1 + pOff0);
                Vector128<short> p1m = Load2x4(r0 - pOff1, r1 - pOff1);
                Vector128<short> p1p = Load2x4(r0 + pOff1, r1 + pOff1);
                max = Vector128.Max(max, Vector128.Max(Vector128.Max(p0m, p0p), Vector128.Max(p1m, p1p)));
                min = Vector128.Min(min, Vector128.Min(Vector128.Min(p0m, p0p), Vector128.Min(p1m, p1p)));
                if (priStr != 0)
                {
                    sum += vPTap0 * (ConstrainV128(p0m - x, vPriStr, priDampAdj) + ConstrainV128(p0p - x, vPriStr, priDampAdj));
                    sum += vPTap1 * (ConstrainV128(p1m - x, vPriStr, priDampAdj) + ConstrainV128(p1p - x, vPriStr, priDampAdj));
                }

                Vector128<short> a0m = Load2x4(r0 - s1Off0, r1 - s1Off0);
                Vector128<short> a0p = Load2x4(r0 + s1Off0, r1 + s1Off0);
                Vector128<short> b0m = Load2x4(r0 - s2Off0, r1 - s2Off0);
                Vector128<short> b0p = Load2x4(r0 + s2Off0, r1 + s2Off0);
                Vector128<short> a1m = Load2x4(r0 - s1Off1, r1 - s1Off1);
                Vector128<short> a1p = Load2x4(r0 + s1Off1, r1 + s1Off1);
                Vector128<short> b1m = Load2x4(r0 - s2Off1, r1 - s2Off1);
                Vector128<short> b1p = Load2x4(r0 + s2Off1, r1 + s2Off1);
                max = Vector128.Max(max, Vector128.Max(Vector128.Max(Vector128.Max(a0m, a0p), Vector128.Max(b0m, b0p)),
                                                       Vector128.Max(Vector128.Max(a1m, a1p), Vector128.Max(b1m, b1p))));
                min = Vector128.Min(min, Vector128.Min(Vector128.Min(Vector128.Min(a0m, a0p), Vector128.Min(b0m, b0p)),
                                                       Vector128.Min(Vector128.Min(a1m, a1p), Vector128.Min(b1m, b1p))));
                if (secStr != 0)
                {
                    sum += vSTap0 * (
                        ConstrainV128(a0m - x, vSecStr, secDampAdj) + ConstrainV128(a0p - x, vSecStr, secDampAdj) +
                        ConstrainV128(b0m - x, vSecStr, secDampAdj) + ConstrainV128(b0p - x, vSecStr, secDampAdj));
                    sum += vSTap1 * (
                        ConstrainV128(a1m - x, vSecStr, secDampAdj) + ConstrainV128(a1p - x, vSecStr, secDampAdj) +
                        ConstrainV128(b1m - x, vSecStr, secDampAdj) + ConstrainV128(b1p - x, vSecStr, secDampAdj));
                }

                Vector128<short> delta = Vector128.ShiftRightArithmetic(sum + Vector128.ShiftRightArithmetic(sum, 15) + vEight, 4);
                Vector128<short> res = Vector128.Min(max, Vector128.Max(min, x + delta));

                short* d0 = dBase + (y0 + i) * stride + x0;
                *(long*)d0 = res.AsInt64().GetElement(0);
                *(long*)(d0 + stride) = res.AsInt64().GetElement(1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector128<short> Load2x4(short* a, short* b)
        => Vector128.Create(*(long*)a, *(long*)b).AsInt16();

    private static void FilterBlockScalar(
        ushort[] src, ushort[] dst, int stride,
        int x0, int y0, int w, int h, int maxX, int maxY,
        int tapSet, int priStr, int priDampAdj, int secStr, int secDampAdj,
        int dir, int dirSec1, int dirSec2)
    {
        for (int i = 0; i < h; i++)
        {
            int py = y0 + i;
            for (int j = 0; j < w; j++)
            {
                int px = x0 + j;
                int x = src[py * stride + px];
                int sum = 0;
                int max = x;
                int min = x;

                for (int k = 0; k < 2; k++)
                {
                    int pTap = PriTaps[tapSet, k];
                    int sTap = SecTaps[tapSet, k];
                    int pOff = (dir * 2 + k) * 2;
                    int s1Off = (dirSec1 * 2 + k) * 2;
                    int s2Off = (dirSec2 * 2 + k) * 2;

                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        int ny = py + sign * Av1Tables.CdefDirections[pOff];
                        int nx = px + sign * Av1Tables.CdefDirections[pOff + 1];
                        if ((uint)ny < (uint)maxY && (uint)nx < (uint)maxX)
                        {
                            int p = src[ny * stride + nx];
                            sum += pTap * Constrain(p - x, priStr, priDampAdj);
                            if (p > max) max = p;
                            if (p < min) min = p;
                        }

                        ny = py + sign * Av1Tables.CdefDirections[s1Off];
                        nx = px + sign * Av1Tables.CdefDirections[s1Off + 1];
                        if ((uint)ny < (uint)maxY && (uint)nx < (uint)maxX)
                        {
                            int s = src[ny * stride + nx];
                            sum += sTap * Constrain(s - x, secStr, secDampAdj);
                            if (s > max) max = s;
                            if (s < min) min = s;
                        }

                        ny = py + sign * Av1Tables.CdefDirections[s2Off];
                        nx = px + sign * Av1Tables.CdefDirections[s2Off + 1];
                        if ((uint)ny < (uint)maxY && (uint)nx < (uint)maxX)
                        {
                            int s = src[ny * stride + nx];
                            sum += sTap * Constrain(s - x, secStr, secDampAdj);
                            if (s > max) max = s;
                            if (s < min) min = s;
                        }
                    }
                }

                int delta = (8 + sum - (sum < 0 ? 1 : 0)) >> 4;
                dst[py * stride + px] = (ushort)Av1Constants.Clip3(min, max, x + delta);
            }
        }
    }

    private static int Constrain(int diff, int threshold, int dampingAdj)
    {
        if (threshold == 0) return 0;
        int absDiff = Math.Abs(diff);
        int mag = Av1Constants.Clip3(0, absDiff, threshold - (absDiff >> dampingAdj));
        return diff < 0 ? -mag : mag;
    }
}
