using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AVX2 (<see cref="Vector256{T}"/>, 8-wide) and <see cref="Vector128{T}"/> (4-wide) 2D inverse transform
/// kernels for AV1 (§7.13). Bit-exact with <see cref="Av1Transform"/> scalar butterflies for all clamp
/// ranges <c>r &lt;= 18</c> (all 8-bit and 10-bit row/column passes, and all 12-bit column passes).
/// </summary>
internal static class Av1TransformSimd
{
    private const int KindDct = 0;
    private const int KindAdst = 1;
    private const int KindIdtx = 2;
    private const int KindWht = 3;

    private const int Sinpi1_9 = 1321;
    private const int Sinpi2_9 = 2482;
    private const int Sinpi3_9 = 3344;
    private const int Sinpi4_9 = 3803;

    internal static readonly int[] Cos128Table = BuildCos128Table();
    internal static readonly byte[][] BrevTables = BuildBrevTables();
    private static readonly byte[] AdstIn3 = BuildAdstIn(3);
    private static readonly byte[] AdstIn4 = BuildAdstIn(4);
    private static readonly byte[] AdstOut3 = BuildAdstOut(3);
    private static readonly byte[] AdstOut4 = BuildAdstOut(4);

    private static int[] BuildCos128Table()
    {
        int[] lookup =
        [
            4096, 4095, 4091, 4085, 4076, 4065, 4052, 4036,
            4017, 3996, 3973, 3948, 3920, 3889, 3857, 3822,
            3784, 3745, 3703, 3659, 3612, 3564, 3513, 3461,
            3406, 3349, 3290, 3229, 3166, 3102, 3035, 2967,
            2896, 2824, 2751, 2675, 2598, 2520, 2440, 2359,
            2276, 2191, 2106, 2019, 1931, 1842, 1751, 1660,
            1567, 1474, 1380, 1285, 1189, 1092, 995, 897,
            799, 700, 601, 501, 401, 301, 201, 101, 0,
        ];
        var table = new int[256];
        for (int a = 0; a < 256; a++)
        {
            table[a] = a <= 64 ? lookup[a]
                : a <= 128 ? -lookup[128 - a]
                : a <= 192 ? -lookup[a - 128]
                : lookup[256 - a];
        }
        return table;
    }

    private static byte[][] BuildBrevTables()
    {
        var tables = new byte[7][];
        for (int n = 1; n <= 6; n++)
        {
            int len = 1 << n;
            var tbl = new byte[len];
            for (int x = 0; x < len; x++)
            {
                int t = 0;
                for (int i = 0; i < n; i++)
                    t |= ((x >> i) & 1) << (n - 1 - i);
                tbl[x] = (byte)t;
            }
            tables[n] = tbl;
        }
        return tables;
    }

    private static byte[] BuildAdstIn(int n)
    {
        int n0 = 1 << n;
        var tbl = new byte[n0];
        for (int i = 0; i < n0; i++)
            tbl[i] = (byte)((i & 1) != 0 ? (i - 1) : (n0 - i - 1));
        return tbl;
    }

    private static byte[] BuildAdstOut(int n)
    {
        int n0 = 1 << n;
        var tbl = new byte[n0];
        for (int i = 0; i < n0; i++)
        {
            int a = (i >> 3) & 1;
            int b = ((i >> 2) & 1) ^ ((i >> 3) & 1);
            int c = ((i >> 1) & 1) ^ ((i >> 2) & 1);
            int d = (i & 1) ^ ((i >> 1) & 1);
            tbl[i] = (byte)(((d << 3) | (c << 2) | (b << 1) | a) >> (4 - n));
        }
        return tbl;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void AddConstantResidualV256(ushort[] planeBuf, int stride, int x, int y, int w, int h, int res, int maxSample)
    {
        // Writes land at (y + r) * stride + x + c with r < h, c < w: this check keeps them inside planeBuf.
        if (x < 0 || y < 0 || (long)(y + h - 1) * stride + x + w > planeBuf.Length)
            throw new InvalidDataException("AV1 transform block lies outside its buffers.");

        Vector256<short> vRes = Vector256.Create((short)res);
        Vector256<short> vZero = Vector256<short>.Zero;
        Vector256<short> vMax = Vector256.Create((short)maxSample);

        fixed (ushort* pBase = planeBuf)
        {
            short* dst = (short*)pBase + y * stride + x;
            if (w >= 16)
            {
                for (int i = 0; i < h; i++)
                {
                    short* row = dst + i * stride;
                    for (int j = 0; j < w; j += 16)
                    {
                        Vector256<short> v = Vector256.Load(row + j) + vRes;
                        Vector256.Store(Vector256.Min(vMax, Vector256.Max(vZero, v)), row + j);
                    }
                }
            }
            else if (w == 8)
            {
                for (int i = 0; i < h; i += 2)
                {
                    short* r0 = dst + i * stride;
                    short* r1 = r0 + stride;
                    Vector256<short> v = Vector256.Create(Vector128.Load(r0), Vector128.Load(r1)) + vRes;
                    v = Vector256.Min(vMax, Vector256.Max(vZero, v));
                    Vector128.Store(v.GetLower(), r0);
                    Vector128.Store(v.GetUpper(), r1);
                }
            }
            else // w == 4
            {
                Vector128<short> vRes128 = vRes.GetLower();
                Vector128<short> vZero128 = Vector128<short>.Zero;
                Vector128<short> vMax128 = vMax.GetLower();
                for (int i = 0; i < h; i += 2)
                {
                    short* r0 = dst + i * stride;
                    short* r1 = r0 + stride;
                    Vector128<short> v = Vector128.Create(*(long*)r0, *(long*)r1).AsInt16() + vRes128;
                    v = Vector128.Min(vMax128, Vector128.Max(vZero128, v));
                    *(long*)r0 = v.AsInt64().GetElement(0);
                    *(long*)r1 = v.AsInt64().GetElement(1);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void AddConstantResidualV128(ushort[] planeBuf, int stride, int x, int y, int w, int h, int res, int maxSample)
    {
        // Writes land at (y + r) * stride + x + c with r < h, c < w: this check keeps them inside planeBuf.
        if (x < 0 || y < 0 || (long)(y + h - 1) * stride + x + w > planeBuf.Length)
            throw new InvalidDataException("AV1 transform block lies outside its buffers.");

        Vector128<short> vRes = Vector128.Create((short)res);
        Vector128<short> vZero = Vector128<short>.Zero;
        Vector128<short> vMax = Vector128.Create((short)maxSample);

        fixed (ushort* pBase = planeBuf)
        {
            short* dst = (short*)pBase + y * stride + x;
            if (w >= 8)
            {
                for (int i = 0; i < h; i++)
                {
                    short* row = dst + i * stride;
                    for (int j = 0; j < w; j += 8)
                    {
                        Vector128<short> v = Vector128.Load(row + j) + vRes;
                        Vector128.Store(Vector128.Min(vMax, Vector128.Max(vZero, v)), row + j);
                    }
                }
            }
            else // w == 4
            {
                for (int i = 0; i < h; i += 2)
                {
                    short* r0 = dst + i * stride;
                    short* r1 = r0 + stride;
                    Vector128<short> v = Vector128.Create(*(long*)r0, *(long*)r1).AsInt16() + vRes;
                    v = Vector128.Min(vMax, Vector128.Max(vZero, v));
                    *(long*)r0 = v.AsInt64().GetElement(0);
                    *(long*)r1 = v.AsInt64().GetElement(1);
                }
            }
        }
    }

    // ==================== Row Transform Passes (V256 / V128) ====================

    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void RowPassV256(
        int[] dequantBuf, int[] residualBuf,
        int tw, int w, int activeRows, int h,
        int rowKind, int log2W, bool rectScale,
        int rowShift, int rowClampRange, int colMin, int colMax)
    {
        // v / tmp hold 64 vectors: w <= 64 bounds v[j] for j < w, tw <= w the dequant columns (tw is 4 or a multiple of
        // 8, AV1 transform sizes). Rows read: i .. i + 7 < activeRows, so dequantBuf reads stay below activeRows * tw.
        if (activeRows * tw > dequantBuf.Length || h * w > residualBuf.Length || w > 64 || tw > w)
            throw new InvalidDataException("AV1 transform block lies outside its buffers.");

        Vector256<int>* v = stackalloc Vector256<int>[64];
        Vector256<int>* tmp = stackalloc Vector256<int>[64];
        Vector256<int> vRowMin = Vector256.Create(-(1 << (rowClampRange - 1)));
        Vector256<int> vRowMax = Vector256.Create((1 << (rowClampRange - 1)) - 1);
        Vector256<int> vColMin = Vector256.Create(colMin);
        Vector256<int> vColMax = Vector256.Create(colMax);
        Vector256<int> vRect = Vector256.Create(2896);
        Vector256<int> v2048 = Vector256.Create(2048);
        Vector256<int> vRowRnd = rowShift > 0 ? Vector256.Create(1 << (rowShift - 1)) : Vector256<int>.Zero;

        fixed (int* dqBase = dequantBuf)
        fixed (int* resBase = residualBuf)
        {
            int i = 0;
            for (; i + 8 <= activeRows; i += 8)
            {
                // Load 8 rows x tw cols and transpose into v[0..tw-1] (each Vector256 holds 8 rows at column j).
                if (tw >= 8)
                {
                    for (int blk = 0; blk < tw; blk += 8)
                    {
                        int* p = dqBase + i * tw + blk;
                        Transpose8x8V256(
                            Vector256.Load(p + 0 * tw), Vector256.Load(p + 1 * tw),
                            Vector256.Load(p + 2 * tw), Vector256.Load(p + 3 * tw),
                            Vector256.Load(p + 4 * tw), Vector256.Load(p + 5 * tw),
                            Vector256.Load(p + 6 * tw), Vector256.Load(p + 7 * tw),
                            out v[blk + 0], out v[blk + 1], out v[blk + 2], out v[blk + 3],
                            out v[blk + 4], out v[blk + 5], out v[blk + 6], out v[blk + 7]);
                    }
                }
                else // tw == 4
                {
                    int* p = dqBase + i * 4;
                    Transpose4x4V128(
                        Vector128.Load(p + 0), Vector128.Load(p + 4), Vector128.Load(p + 8), Vector128.Load(p + 12),
                        out var a0, out var a1, out var a2, out var a3);
                    Transpose4x4V128(
                        Vector128.Load(p + 16), Vector128.Load(p + 20), Vector128.Load(p + 24), Vector128.Load(p + 28),
                        out var b0, out var b1, out var b2, out var b3);
                    v[0] = Vector256.Create(a0, b0);
                    v[1] = Vector256.Create(a1, b1);
                    v[2] = Vector256.Create(a2, b2);
                    v[3] = Vector256.Create(a3, b3);
                }

                for (int j = tw; j < w; j++)
                    v[j] = Vector256<int>.Zero;

                if (rectScale)
                {
                    for (int j = 0; j < tw; j++)
                        v[j] = Vector256.ShiftRightArithmetic(v[j] * vRect + v2048, 12);
                }

                if (rowClampRange > 18)
                    Run1DV256<TxWide>(v, tmp, rowKind, log2W, vRowMin, vRowMax, whtShift: 2);
                else
                    Run1DV256<TxNarrow>(v, tmp, rowKind, log2W, vRowMin, vRowMax, whtShift: 2);

                if (rowShift > 0)
                {
                    for (int j = 0; j < w; j++)
                        v[j] = Vector256.Min(vColMax, Vector256.Max(vColMin, Vector256.ShiftRightArithmetic(v[j] + vRowRnd, rowShift)));
                }
                else
                {
                    for (int j = 0; j < w; j++)
                        v[j] = Vector256.Min(vColMax, Vector256.Max(vColMin, v[j]));
                }

                // Transpose back into 8 rows of residualBuf
                if (w >= 8)
                {
                    for (int blk = 0; blk < w; blk += 8)
                    {
                        Transpose8x8V256(
                            v[blk + 0], v[blk + 1], v[blk + 2], v[blk + 3],
                            v[blk + 4], v[blk + 5], v[blk + 6], v[blk + 7],
                            out var r0, out var r1, out var r2, out var r3,
                            out var r4, out var r5, out var r6, out var r7);
                        int* dst = resBase + i * w + blk;
                        Vector256.Store(r0, dst + 0 * w);
                        Vector256.Store(r1, dst + 1 * w);
                        Vector256.Store(r2, dst + 2 * w);
                        Vector256.Store(r3, dst + 3 * w);
                        Vector256.Store(r4, dst + 4 * w);
                        Vector256.Store(r5, dst + 5 * w);
                        Vector256.Store(r6, dst + 6 * w);
                        Vector256.Store(r7, dst + 7 * w);
                    }
                }
                else // w == 4
                {
                    Transpose4x4V128(v[0].GetLower(), v[1].GetLower(), v[2].GetLower(), v[3].GetLower(),
                        out var a0, out var a1, out var a2, out var a3);
                    Transpose4x4V128(v[0].GetUpper(), v[1].GetUpper(), v[2].GetUpper(), v[3].GetUpper(),
                        out var b0, out var b1, out var b2, out var b3);
                    int* dst = resBase + i * 4;
                    Vector128.Store(a0, dst + 0);
                    Vector128.Store(a1, dst + 4);
                    Vector128.Store(a2, dst + 8);
                    Vector128.Store(a3, dst + 12);
                    Vector128.Store(b0, dst + 16);
                    Vector128.Store(b1, dst + 20);
                    Vector128.Store(b2, dst + 24);
                    Vector128.Store(b3, dst + 28);
                }
            }

            if (i < activeRows)
            {
                RowPassV128Range(dqBase, resBase, i, activeRows, tw, w, rowKind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
            }
        }

        if (activeRows < h)
            residualBuf.AsSpan(activeRows * w, (h - activeRows) * w).Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void RowPassV128(
        int[] dequantBuf, int[] residualBuf,
        int tw, int w, int activeRows, int h,
        int rowKind, int log2W, bool rectScale,
        int rowShift, int rowClampRange, int colMin, int colMax)
    {
        // v / tmp hold 64 vectors: w <= 64 bounds v[j] for j < w, tw <= w the dequant columns (tw is 4 or a multiple of
        // 8, AV1 transform sizes). Rows read: i .. i + 7 < activeRows, so dequantBuf reads stay below activeRows * tw.
        if (activeRows * tw > dequantBuf.Length || h * w > residualBuf.Length || w > 64 || tw > w)
            throw new InvalidDataException("AV1 transform block lies outside its buffers.");

        fixed (int* dqBase = dequantBuf)
        fixed (int* resBase = residualBuf)
        {
            RowPassV128Range(dqBase, resBase, 0, activeRows, tw, w, rowKind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
        }

        if (activeRows < h)
            residualBuf.AsSpan(activeRows * w, (h - activeRows) * w).Clear();
    }

    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    private static unsafe void RowPassV128Range(
        int* dqBase, int* resBase, int startRow, int activeRows,
        int tw, int w, int rowKind, int log2W, bool rectScale,
        int rowShift, int rowClampRange, int colMin, int colMax)
    {
        Vector128<int>* v = stackalloc Vector128<int>[64];
        Vector128<int>* tmp = stackalloc Vector128<int>[64];
        Vector128<int> vRowMin = Vector128.Create(-(1 << (rowClampRange - 1)));
        Vector128<int> vRowMax = Vector128.Create((1 << (rowClampRange - 1)) - 1);
        Vector128<int> vColMin = Vector128.Create(colMin);
        Vector128<int> vColMax = Vector128.Create(colMax);
        Vector128<int> vRect = Vector128.Create(2896);
        Vector128<int> v2048 = Vector128.Create(2048);
        Vector128<int> vRowRnd = rowShift > 0 ? Vector128.Create(1 << (rowShift - 1)) : Vector128<int>.Zero;

        for (int i = startRow; i < activeRows; i += 4)
        {
            int rem = activeRows - i;
            if (rem >= 4)
            {
                for (int blk = 0; blk < tw; blk += 4)
                {
                    int* p = dqBase + i * tw + blk;
                    Transpose4x4V128(
                        Vector128.Load(p + 0 * tw), Vector128.Load(p + 1 * tw),
                        Vector128.Load(p + 2 * tw), Vector128.Load(p + 3 * tw),
                        out v[blk + 0], out v[blk + 1], out v[blk + 2], out v[blk + 3]);
                }
            }
            else
            {
                for (int j = 0; j < tw; j++)
                {
                    int a = dqBase[i * tw + j];
                    int b = rem > 1 ? dqBase[(i + 1) * tw + j] : 0;
                    int c = rem > 2 ? dqBase[(i + 2) * tw + j] : 0;
                    v[j] = Vector128.Create(a, b, c, 0);
                }
            }

            for (int j = tw; j < w; j++)
                v[j] = Vector128<int>.Zero;

            if (rectScale)
            {
                for (int j = 0; j < tw; j++)
                    v[j] = Vector128.ShiftRightArithmetic(v[j] * vRect + v2048, 12);
            }

            if (rowClampRange > 18)
                Run1DV128<TxWide>(v, tmp, rowKind, log2W, vRowMin, vRowMax, whtShift: 2);
            else
                Run1DV128<TxNarrow>(v, tmp, rowKind, log2W, vRowMin, vRowMax, whtShift: 2);

            if (rowShift > 0)
            {
                for (int j = 0; j < w; j++)
                    v[j] = Vector128.Min(vColMax, Vector128.Max(vColMin, Vector128.ShiftRightArithmetic(v[j] + vRowRnd, rowShift)));
            }
            else
            {
                for (int j = 0; j < w; j++)
                    v[j] = Vector128.Min(vColMax, Vector128.Max(vColMin, v[j]));
            }

            if (rem >= 4)
            {
                for (int blk = 0; blk < w; blk += 4)
                {
                    Transpose4x4V128(
                        v[blk + 0], v[blk + 1], v[blk + 2], v[blk + 3],
                        out var r0, out var r1, out var r2, out var r3);
                    int* dst = resBase + i * w + blk;
                    Vector128.Store(r0, dst + 0 * w);
                    Vector128.Store(r1, dst + 1 * w);
                    Vector128.Store(r2, dst + 2 * w);
                    Vector128.Store(r3, dst + 3 * w);
                }
            }
            else
            {
                for (int k = 0; k < rem; k++)
                {
                    int* dst = resBase + (i + k) * w;
                    for (int j = 0; j < w; j++)
                        dst[j] = v[j].GetElement(k);
                }
            }
        }
    }

    // ==================== Column Transform + Direct Plane Add Passes (V256 / V128) ====================

    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ColPassAndAddV256(
        int[] residualBuf, ushort[] planeBuf, int stride,
        int x, int y, int w, int h,
        int colKind, int log2H, int colShift, int colClampRange,
        bool flipUd, bool flipLr, int maxSample)
    {
        // v / tmp hold 64 vectors, one per row: h <= 64. residualBuf reads stay below h * w; plane writes land at
        // (y + r) * stride + x + c with r < h, c < w, which the last clause keeps inside planeBuf.
        if (h * w > residualBuf.Length || h > 64 || x < 0 || y < 0 || (long)(y + h - 1) * stride + x + w > planeBuf.Length)
            throw new InvalidDataException("AV1 transform block lies outside its buffers.");

        Vector256<int>* v = stackalloc Vector256<int>[64];
        Vector256<int>* tmp = stackalloc Vector256<int>[64];
        Vector256<int> vColMin = Vector256.Create(-(1 << (colClampRange - 1)));
        Vector256<int> vColMax = Vector256.Create((1 << (colClampRange - 1)) - 1);
        Vector256<int> vColRnd = colShift > 0 ? Vector256.Create(1 << (colShift - 1)) : Vector256<int>.Zero;
        Vector256<int> vZero = Vector256<int>.Zero;
        Vector256<int> vMaxS = Vector256.Create(maxSample);

        fixed (int* resBase = residualBuf)
        fixed (ushort* planeBase = planeBuf)
        {
            for (int j = 0; j < w; j += 8)
            {
                for (int i = 0; i < h; i++)
                    v[i] = Vector256.Load(resBase + i * w + j);

                Run1DV256<TxNarrow>(v, tmp, colKind, log2H, vColMin, vColMax, whtShift: 0);

                if (!flipLr)
                {
                    for (int i = 0; i < h; i++)
                    {
                        Vector256<int> r = colShift > 0 ? Vector256.ShiftRightArithmetic(v[i] + vColRnd, colShift) : v[i];
                        int yy = flipUd ? (h - i - 1) : i;
                        ushort* dst = planeBase + (y + yy) * stride + x + j;
                        Vector256<int> pred = Avx2.ConvertToVector256Int32(dst);
                        Vector256<int> sum = Vector256.Min(vMaxS, Vector256.Max(vZero, pred + r));
                        // Pack 8 int32 (0..4095) into 8 ushort via PackUnsignedSaturate + Permute4x64
                        Vector256<ushort> packed = Avx2.PackUnsignedSaturate(sum, sum);
                        Vector256<ulong> perm = Avx2.Permute4x64(packed.AsUInt64(), 0b11_01_10_00);
                        Vector128.Store(perm.GetLower().AsUInt16(), dst);
                    }
                }
                else
                {
                    int dstCol = x + w - j - 8;
                    Vector256<int> revIdx = Vector256.Create(7, 6, 5, 4, 3, 2, 1, 0);
                    for (int i = 0; i < h; i++)
                    {
                        Vector256<int> r = colShift > 0 ? Vector256.ShiftRightArithmetic(v[i] + vColRnd, colShift) : v[i];
                        r = Avx2.PermuteVar8x32(r, revIdx);
                        int yy = flipUd ? (h - i - 1) : i;
                        ushort* dst = planeBase + (y + yy) * stride + dstCol;
                        Vector256<int> pred = Avx2.ConvertToVector256Int32(dst);
                        Vector256<int> sum = Vector256.Min(vMaxS, Vector256.Max(vZero, pred + r));
                        Vector256<ushort> packed = Avx2.PackUnsignedSaturate(sum, sum);
                        Vector256<ulong> perm = Avx2.Permute4x64(packed.AsUInt64(), 0b11_01_10_00);
                        Vector128.Store(perm.GetLower().AsUInt16(), dst);
                    }
                }
            }
        }
    }

    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ColPassAndAddV128(
        int[] residualBuf, ushort[] planeBuf, int stride,
        int x, int y, int w, int h,
        int colKind, int log2H, int colShift, int colClampRange,
        bool flipUd, bool flipLr, int maxSample)
    {
        // v / tmp hold 64 vectors, one per row: h <= 64. residualBuf reads stay below h * w; plane writes land at
        // (y + r) * stride + x + c with r < h, c < w, which the last clause keeps inside planeBuf.
        if (h * w > residualBuf.Length || h > 64 || x < 0 || y < 0 || (long)(y + h - 1) * stride + x + w > planeBuf.Length)
            throw new InvalidDataException("AV1 transform block lies outside its buffers.");

        Vector128<int>* v = stackalloc Vector128<int>[64];
        Vector128<int>* tmp = stackalloc Vector128<int>[64];
        Vector128<int> vColMin = Vector128.Create(-(1 << (colClampRange - 1)));
        Vector128<int> vColMax = Vector128.Create((1 << (colClampRange - 1)) - 1);
        Vector128<int> vColRnd = colShift > 0 ? Vector128.Create(1 << (colShift - 1)) : Vector128<int>.Zero;
        Vector128<int> vZero = Vector128<int>.Zero;
        Vector128<int> vMaxS = Vector128.Create(maxSample);

        fixed (int* resBase = residualBuf)
        fixed (ushort* planeBase = planeBuf)
        {
            for (int j = 0; j < w; j += 4)
            {
                for (int i = 0; i < h; i++)
                    v[i] = Vector128.Load(resBase + i * w + j);

                Run1DV128<TxNarrow>(v, tmp, colKind, log2H, vColMin, vColMax, whtShift: 0);

                if (!flipLr)
                {
                    for (int i = 0; i < h; i++)
                    {
                        Vector128<int> r = colShift > 0 ? Vector128.ShiftRightArithmetic(v[i] + vColRnd, colShift) : v[i];
                        int yy = flipUd ? (h - i - 1) : i;
                        ushort* dst = planeBase + (y + yy) * stride + x + j;
                        Vector128<int> pred = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)dst).AsUInt16()).AsInt32();
                        Vector128<int> sum = Vector128.Min(vMaxS, Vector128.Max(vZero, pred + r));
                        Vector128<ushort> packed = Vector128.Narrow(sum.AsUInt32(), sum.AsUInt32());
                        *(ulong*)dst = packed.AsUInt64().GetElement(0);
                    }
                }
                else
                {
                    int dstCol = x + w - j - 4;
                    for (int i = 0; i < h; i++)
                    {
                        Vector128<int> r = colShift > 0 ? Vector128.ShiftRightArithmetic(v[i] + vColRnd, colShift) : v[i];
                        r = Vector128.Shuffle(r, Vector128.Create(3, 2, 1, 0));
                        int yy = flipUd ? (h - i - 1) : i;
                        ushort* dst = planeBase + (y + yy) * stride + dstCol;
                        Vector128<int> pred = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)dst).AsUInt16()).AsInt32();
                        Vector128<int> sum = Vector128.Min(vMaxS, Vector128.Max(vZero, pred + r));
                        Vector128<ushort> packed = Vector128.Narrow(sum.AsUInt32(), sum.AsUInt32());
                        *(ulong*)dst = packed.AsUInt64().GetElement(0);
                    }
                }
            }
        }
    }

    // ==================== 1D Vectorized Butterflies (V256) ====================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BV256<TM>(ref Vector256<int> a, ref Vector256<int> b, int angle, bool flip, Vector256<int> vMin, Vector256<int> vMax) where TM : struct, ITxRange
    {
        Vector256<int> ta = a;
        Vector256<int> tb = b;
        Vector256<int> vc = Vector256.Create(Cos128Table[angle & 255]);
        Vector256<int> vs = Vector256.Create(Cos128Table[(angle - 64) & 255]);
        Vector256<int> v2048 = Vector256.Create(2048);
        Vector256<int> x, y;
        if (TM.Wide)
        {
            // a = ah * 4096 + al with 0 <= al < 4096: Round2(a * c - b * s, 12) = ah * c - bh * s + Round2(al * c - bl * s, 12)
            // exactly, and every term fits int32 for 20-bit inputs (ITxRange).
            var mask = Vector256.Create(4095);
            var ah = Vector256.ShiftRightArithmetic(ta, 12);
            var bh = Vector256.ShiftRightArithmetic(tb, 12);
            var al = ta & mask;
            var bl = tb & mask;
            x = ah * vc - bh * vs + Vector256.ShiftRightArithmetic(al * vc - bl * vs + v2048, 12);
            y = ah * vs + bh * vc + Vector256.ShiftRightArithmetic(al * vs + bl * vc + v2048, 12);
        }
        else
        {
            x = Vector256.ShiftRightArithmetic(ta * vc - tb * vs + v2048, 12);
            y = Vector256.ShiftRightArithmetic(ta * vs + tb * vc + v2048, 12);
        }
        x = Vector256.Min(vMax, Vector256.Max(vMin, x));
        y = Vector256.Min(vMax, Vector256.Max(vMin, y));
        if (flip) { a = y; b = x; }
        else { a = x; b = y; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HV256(ref Vector256<int> a, ref Vector256<int> b, bool flip, Vector256<int> vMin, Vector256<int> vMax)
    {
        Vector256<int> x = flip ? b : a;
        Vector256<int> y = flip ? a : b;
        Vector256<int> add = Vector256.Min(vMax, Vector256.Max(vMin, x + y));
        Vector256<int> sub = Vector256.Min(vMax, Vector256.Max(vMin, x - y));
        if (flip) { b = add; a = sub; }
        else { a = add; b = sub; }
    }

    private static unsafe void Run1DV256<TM>(Vector256<int>* t, Vector256<int>* copyT, int kind, int n, Vector256<int> vMin, Vector256<int> vMax, int whtShift) where TM : struct, ITxRange
    {
        switch (kind)
        {
            case KindDct:
                InverseDctV256<TM>(t, copyT, n, vMin, vMax);
                break;
            case KindAdst:
                InverseAdstV256<TM>(t, copyT, n, vMin, vMax);
                break;
            case KindWht:
                InverseWht4V256(t, whtShift);
                break;
            default:
                InverseIdtxV256<TM>(t, n);
                break;
        }
    }

    private static unsafe void InverseDctV256<TM>(Vector256<int>* t, Vector256<int>* copyT, int n, Vector256<int> vMin, Vector256<int> vMax) where TM : struct, ITxRange
    {
        int len = 1 << n;
        byte[] brev = BrevTables[n];
        for (int i = 0; i < len; i++) copyT[i] = t[i];
        for (int i = 0; i < len; i++) t[i] = copyT[brev[i]];

        byte[] brev4 = BrevTables[4];
        byte[] brev3 = BrevTables[3];
        byte[] brev2 = BrevTables[2];

        if (n == 6)
            for (int i = 0; i < 16; i++) BV256<TM>(ref t[32 + i], ref t[63 - i], 63 - 4 * brev4[i], false, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 8; i++) BV256<TM>(ref t[16 + i], ref t[31 - i], 6 + (brev3[7 - i] << 3), false, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 16; i++) HV256(ref t[32 + i * 2], ref t[33 + i * 2], (i & 1) != 0, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 4; i++) BV256<TM>(ref t[8 + i], ref t[15 - i], 12 + (brev2[3 - i] << 4), false, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 8; i++) HV256(ref t[16 + 2 * i], ref t[17 + 2 * i], (i & 1) != 0, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    BV256<TM>(ref t[62 - i * 4 - j], ref t[33 + i * 4 + j], 60 - 16 * brev2[i] + 64 * j, true, vMin, vMax);
        if (n >= 3)
            for (int i = 0; i < 2; i++) BV256<TM>(ref t[4 + i], ref t[7 - i], 56 - 32 * i, false, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 4; i++) HV256(ref t[8 + 2 * i], ref t[9 + 2 * i], (i & 1) != 0, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    BV256<TM>(ref t[30 - 4 * i - j], ref t[17 + 4 * i + j], 24 + (j << 6) + ((1 - i) << 5), true, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 2; j++)
                    HV256(ref t[32 + i * 4 + j], ref t[35 + i * 4 - j], (i & 1) != 0, vMin, vMax);

        BV256<TM>(ref t[0], ref t[1], 32, true, vMin, vMax);
        BV256<TM>(ref t[2], ref t[3], 48, false, vMin, vMax);

        if (n >= 3)
        {
            HV256(ref t[4], ref t[5], false, vMin, vMax);
            HV256(ref t[6], ref t[7], true, vMin, vMax);
        }
        if (n >= 4)
            for (int i = 0; i < 2; i++) BV256<TM>(ref t[14 - i], ref t[9 + i], 48 + 64 * i, true, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    HV256(ref t[16 + 4 * i + j], ref t[19 + 4 * i - j], (i & 1) != 0, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                    BV256<TM>(ref t[61 - i * 8 - j], ref t[34 + i * 8 + j], 56 - i * 32 + (j >> 1) * 64, true, vMin, vMax);

        HV256(ref t[0], ref t[3], false, vMin, vMax);
        HV256(ref t[1], ref t[2], false, vMin, vMax);

        if (n >= 3)
            BV256<TM>(ref t[6], ref t[5], 32, true, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    HV256(ref t[8 + 4 * i + j], ref t[11 + 4 * i - j], i != 0, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 4; i++) BV256<TM>(ref t[29 - i], ref t[18 + i], 48 + (i >> 1) * 64, true, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    HV256(ref t[32 + 8 * i + j], ref t[39 + 8 * i - j], (i & 1) != 0, vMin, vMax);

        if (n >= 3)
            for (int i = 0; i < 4; i++) HV256(ref t[i], ref t[7 - i], false, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 2; i++) BV256<TM>(ref t[13 - i], ref t[10 + i], 32, true, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                    HV256(ref t[16 + i * 8 + j], ref t[23 + i * 8 - j], i != 0, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 8; i++) BV256<TM>(ref t[59 - i], ref t[36 + i], i < 4 ? 48 : 112, true, vMin, vMax);

        if (n >= 4)
            for (int i = 0; i < 8; i++) HV256(ref t[i], ref t[15 - i], false, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 4; i++) BV256<TM>(ref t[27 - i], ref t[20 + i], 32, true, vMin, vMax);
        if (n == 6)
        {
            for (int i = 0; i < 8; i++)
            {
                HV256(ref t[32 + i], ref t[47 - i], false, vMin, vMax);
                HV256(ref t[48 + i], ref t[63 - i], true, vMin, vMax);
            }
        }

        if (n >= 5)
            for (int i = 0; i < 16; i++) HV256(ref t[i], ref t[31 - i], false, vMin, vMax);
        if (n == 6)
        {
            for (int i = 0; i < 8; i++) BV256<TM>(ref t[55 - i], ref t[40 + i], 32, true, vMin, vMax);
            for (int i = 0; i < 32; i++) HV256(ref t[i], ref t[63 - i], false, vMin, vMax);
        }
    }

    private static unsafe void InverseAdstV256<TM>(Vector256<int>* t, Vector256<int>* copyT, int n, Vector256<int> vMin, Vector256<int> vMax) where TM : struct, ITxRange
    {
        if (n == 2 && TM.Wide)
        {
            // The products need 33 bits at 20-bit inputs: each input split as hi * 4096 + lo, the sums of hi
            // products are exact multiples of 4096 and only the lo sums are rounded (see BV256).
            var mask = Vector256.Create(4095);
            Vector256<int> h0 = Vector256.ShiftRightArithmetic(t[0], 12), h1 = Vector256.ShiftRightArithmetic(t[1], 12);
            Vector256<int> h2 = Vector256.ShiftRightArithmetic(t[2], 12), h3 = Vector256.ShiftRightArithmetic(t[3], 12);
            Vector256<int> l0 = t[0] & mask, l1 = t[1] & mask, l2 = t[2] & mask, l3 = t[3] & mask;
            var k1 = Vector256.Create(Sinpi1_9);
            var k2 = Vector256.Create(Sinpi2_9);
            var k3 = Vector256.Create(Sinpi3_9);
            var k4 = Vector256.Create(Sinpi4_9);
            var hs0 = k1 * h0 + k4 * h2 + k2 * h3;
            var hs1 = k2 * h0 - k1 * h2 - k4 * h3;
            var hs3 = k3 * h1;
            var hs2 = k3 * (h0 - h2 + h3);
            var ls0 = k1 * l0 + k4 * l2 + k2 * l3;
            var ls1 = k2 * l0 - k1 * l2 - k4 * l3;
            var ls3 = k3 * l1;
            var ls2 = k3 * (l0 - l2 + l3);
            var r = Vector256.Create(2048);
            t[0] = hs0 + hs3 + Vector256.ShiftRightArithmetic(ls0 + ls3 + r, 12);
            t[1] = hs1 + hs3 + Vector256.ShiftRightArithmetic(ls1 + ls3 + r, 12);
            t[2] = hs2 + Vector256.ShiftRightArithmetic(ls2 + r, 12);
            t[3] = hs0 + hs1 - hs3 + Vector256.ShiftRightArithmetic(ls0 + ls1 - ls3 + r, 12);
            return;
        }

        if (n == 2)
        {
            Vector256<int> t0 = t[0], t1 = t[1], t2 = t[2], t3 = t[3];
            Vector256<int> s0 = Vector256.Create(Sinpi1_9) * t0 + Vector256.Create(Sinpi4_9) * t2 + Vector256.Create(Sinpi2_9) * t3;
            Vector256<int> s1 = Vector256.Create(Sinpi2_9) * t0 - Vector256.Create(Sinpi1_9) * t2 - Vector256.Create(Sinpi4_9) * t3;
            Vector256<int> s3 = Vector256.Create(Sinpi3_9) * t1;
            Vector256<int> s2 = Vector256.Create(Sinpi3_9) * (t0 - t2 + t3);
            Vector256<int> v2048 = Vector256.Create(2048);
            t[0] = Vector256.ShiftRightArithmetic(s0 + s3 + v2048, 12);
            t[1] = Vector256.ShiftRightArithmetic(s1 + s3 + v2048, 12);
            t[2] = Vector256.ShiftRightArithmetic(s2 + v2048, 12);
            t[3] = Vector256.ShiftRightArithmetic(s0 + s1 - s3 + v2048, 12);
            return;
        }

        if (n == 3)
        {
            for (int i = 0; i < 8; i++) copyT[i] = t[i];
            for (int i = 0; i < 8; i++) t[i] = copyT[AdstIn3[i]];
            for (int i = 0; i < 4; i++) BV256<TM>(ref t[2 * i], ref t[2 * i + 1], 60 - 16 * i, true, vMin, vMax);
            for (int i = 0; i < 4; i++) HV256(ref t[i], ref t[4 + i], false, vMin, vMax);
            for (int i = 0; i < 2; i++) BV256<TM>(ref t[4 + 3 * i], ref t[5 + i], 48 - 32 * i, true, vMin, vMax);
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                    HV256(ref t[4 * j + i], ref t[2 + 4 * j + i], false, vMin, vMax);
            for (int i = 0; i < 2; i++) BV256<TM>(ref t[2 + 4 * i], ref t[3 + 4 * i], 32, true, vMin, vMax);
            for (int i = 0; i < 8; i++) copyT[i] = t[i];
            for (int i = 0; i < 8; i++) t[i] = (i & 1) != 0 ? -copyT[AdstOut3[i]] : copyT[AdstOut3[i]];
            return;
        }

        // n == 4
        for (int i = 0; i < 16; i++) copyT[i] = t[i];
        for (int i = 0; i < 16; i++) t[i] = copyT[AdstIn4[i]];
        for (int i = 0; i < 8; i++) BV256<TM>(ref t[2 * i], ref t[2 * i + 1], 62 - 8 * i, true, vMin, vMax);
        for (int i = 0; i < 8; i++) HV256(ref t[i], ref t[8 + i], false, vMin, vMax);
        for (int i = 0; i < 2; i++)
        {
            BV256<TM>(ref t[8 + 2 * i], ref t[9 + 2 * i], 56 - 32 * i, true, vMin, vMax);
            BV256<TM>(ref t[13 + 2 * i], ref t[12 + 2 * i], 8 + 32 * i, true, vMin, vMax);
        }
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 4; i++)
                HV256(ref t[8 * j + i], ref t[4 + 8 * j + i], false, vMin, vMax);
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
                BV256<TM>(ref t[4 + 8 * j + 3 * i], ref t[5 + 8 * j + i], 48 - 32 * i, true, vMin, vMax);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 2; i++)
                HV256(ref t[4 * j + i], ref t[2 + 4 * j + i], false, vMin, vMax);
        for (int i = 0; i < 4; i++) BV256<TM>(ref t[2 + 4 * i], ref t[3 + 4 * i], 32, true, vMin, vMax);
        for (int i = 0; i < 16; i++) copyT[i] = t[i];
        for (int i = 0; i < 16; i++) t[i] = (i & 1) != 0 ? -copyT[AdstOut4[i]] : copyT[AdstOut4[i]];
    }

    private static unsafe void InverseWht4V256(Vector256<int>* t, int shift)
    {
        Vector256<int> a = shift > 0 ? Vector256.ShiftRightArithmetic(t[0], shift) : t[0];
        Vector256<int> c = shift > 0 ? Vector256.ShiftRightArithmetic(t[1], shift) : t[1];
        Vector256<int> d = shift > 0 ? Vector256.ShiftRightArithmetic(t[2], shift) : t[2];
        Vector256<int> b = shift > 0 ? Vector256.ShiftRightArithmetic(t[3], shift) : t[3];
        a += c;
        d -= b;
        Vector256<int> e = Vector256.ShiftRightArithmetic(a - d, 1);
        b = e - b;
        c = e - c;
        a -= b;
        d += c;
        t[0] = a; t[1] = b; t[2] = c; t[3] = d;
    }

    private static unsafe void InverseIdtxV256<TM>(Vector256<int>* t, int n) where TM : struct, ITxRange
    {
        if (n == 2)
        {
            Vector256<int> k = Vector256.Create(5793);
            Vector256<int> r = Vector256.Create(2048);
            if (TM.Wide)
            {
                var mask = Vector256.Create(4095);
                for (int i = 0; i < 4; i++)
                    t[i] = Vector256.ShiftRightArithmetic(t[i], 12) * k + Vector256.ShiftRightArithmetic((t[i] & mask) * k + r, 12);
            }
            else
            {
                for (int i = 0; i < 4; i++) t[i] = Vector256.ShiftRightArithmetic(t[i] * k + r, 12);
            }
        }
        else if (n == 3)
        {
            for (int i = 0; i < 8; i++) t[i] += t[i];
        }
        else if (n == 4)
        {
            Vector256<int> k = Vector256.Create(11586);
            Vector256<int> r = Vector256.Create(2048);
            if (TM.Wide)
            {
                var mask = Vector256.Create(4095);
                for (int i = 0; i < 16; i++)
                    t[i] = Vector256.ShiftRightArithmetic(t[i], 12) * k + Vector256.ShiftRightArithmetic((t[i] & mask) * k + r, 12);
            }
            else
            {
                for (int i = 0; i < 16; i++) t[i] = Vector256.ShiftRightArithmetic(t[i] * k + r, 12);
            }
        }
        else
        {
            for (int i = 0; i < 32; i++) t[i] = Vector256.ShiftLeft(t[i], 2);
        }
    }

    // ==================== 1D Vectorized Butterflies (V128) ====================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BV128<TM>(ref Vector128<int> a, ref Vector128<int> b, int angle, bool flip, Vector128<int> vMin, Vector128<int> vMax) where TM : struct, ITxRange
    {
        Vector128<int> ta = a;
        Vector128<int> tb = b;
        Vector128<int> vc = Vector128.Create(Cos128Table[angle & 255]);
        Vector128<int> vs = Vector128.Create(Cos128Table[(angle - 64) & 255]);
        Vector128<int> v2048 = Vector128.Create(2048);
        Vector128<int> x, y;
        if (TM.Wide)
        {
            // a = ah * 4096 + al with 0 <= al < 4096: Round2(a * c - b * s, 12) = ah * c - bh * s + Round2(al * c - bl * s, 12)
            // exactly, and every term fits int32 for 20-bit inputs (ITxRange).
            var mask = Vector128.Create(4095);
            var ah = Vector128.ShiftRightArithmetic(ta, 12);
            var bh = Vector128.ShiftRightArithmetic(tb, 12);
            var al = ta & mask;
            var bl = tb & mask;
            x = ah * vc - bh * vs + Vector128.ShiftRightArithmetic(al * vc - bl * vs + v2048, 12);
            y = ah * vs + bh * vc + Vector128.ShiftRightArithmetic(al * vs + bl * vc + v2048, 12);
        }
        else
        {
            x = Vector128.ShiftRightArithmetic(ta * vc - tb * vs + v2048, 12);
            y = Vector128.ShiftRightArithmetic(ta * vs + tb * vc + v2048, 12);
        }
        x = Vector128.Min(vMax, Vector128.Max(vMin, x));
        y = Vector128.Min(vMax, Vector128.Max(vMin, y));
        if (flip) { a = y; b = x; }
        else { a = x; b = y; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HV128(ref Vector128<int> a, ref Vector128<int> b, bool flip, Vector128<int> vMin, Vector128<int> vMax)
    {
        Vector128<int> x = flip ? b : a;
        Vector128<int> y = flip ? a : b;
        Vector128<int> add = Vector128.Min(vMax, Vector128.Max(vMin, x + y));
        Vector128<int> sub = Vector128.Min(vMax, Vector128.Max(vMin, x - y));
        if (flip) { b = add; a = sub; }
        else { a = add; b = sub; }
    }

    private static unsafe void Run1DV128<TM>(Vector128<int>* t, Vector128<int>* copyT, int kind, int n, Vector128<int> vMin, Vector128<int> vMax, int whtShift) where TM : struct, ITxRange
    {
        switch (kind)
        {
            case KindDct:
                InverseDctV128<TM>(t, copyT, n, vMin, vMax);
                break;
            case KindAdst:
                InverseAdstV128<TM>(t, copyT, n, vMin, vMax);
                break;
            case KindWht:
                InverseWht4V128(t, whtShift);
                break;
            default:
                InverseIdtxV128<TM>(t, n);
                break;
        }
    }

    private static unsafe void InverseDctV128<TM>(Vector128<int>* t, Vector128<int>* copyT, int n, Vector128<int> vMin, Vector128<int> vMax) where TM : struct, ITxRange
    {
        int len = 1 << n;
        byte[] brev = BrevTables[n];
        for (int i = 0; i < len; i++) copyT[i] = t[i];
        for (int i = 0; i < len; i++) t[i] = copyT[brev[i]];

        byte[] brev4 = BrevTables[4];
        byte[] brev3 = BrevTables[3];
        byte[] brev2 = BrevTables[2];

        if (n == 6)
            for (int i = 0; i < 16; i++) BV128<TM>(ref t[32 + i], ref t[63 - i], 63 - 4 * brev4[i], false, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 8; i++) BV128<TM>(ref t[16 + i], ref t[31 - i], 6 + (brev3[7 - i] << 3), false, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 16; i++) HV128(ref t[32 + i * 2], ref t[33 + i * 2], (i & 1) != 0, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 4; i++) BV128<TM>(ref t[8 + i], ref t[15 - i], 12 + (brev2[3 - i] << 4), false, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 8; i++) HV128(ref t[16 + 2 * i], ref t[17 + 2 * i], (i & 1) != 0, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    BV128<TM>(ref t[62 - i * 4 - j], ref t[33 + i * 4 + j], 60 - 16 * brev2[i] + 64 * j, true, vMin, vMax);
        if (n >= 3)
            for (int i = 0; i < 2; i++) BV128<TM>(ref t[4 + i], ref t[7 - i], 56 - 32 * i, false, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 4; i++) HV128(ref t[8 + 2 * i], ref t[9 + 2 * i], (i & 1) != 0, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    BV128<TM>(ref t[30 - 4 * i - j], ref t[17 + 4 * i + j], 24 + (j << 6) + ((1 - i) << 5), true, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 2; j++)
                    HV128(ref t[32 + i * 4 + j], ref t[35 + i * 4 - j], (i & 1) != 0, vMin, vMax);

        BV128<TM>(ref t[0], ref t[1], 32, true, vMin, vMax);
        BV128<TM>(ref t[2], ref t[3], 48, false, vMin, vMax);

        if (n >= 3)
        {
            HV128(ref t[4], ref t[5], false, vMin, vMax);
            HV128(ref t[6], ref t[7], true, vMin, vMax);
        }
        if (n >= 4)
            for (int i = 0; i < 2; i++) BV128<TM>(ref t[14 - i], ref t[9 + i], 48 + 64 * i, true, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    HV128(ref t[16 + 4 * i + j], ref t[19 + 4 * i - j], (i & 1) != 0, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                    BV128<TM>(ref t[61 - i * 8 - j], ref t[34 + i * 8 + j], 56 - i * 32 + (j >> 1) * 64, true, vMin, vMax);

        HV128(ref t[0], ref t[3], false, vMin, vMax);
        HV128(ref t[1], ref t[2], false, vMin, vMax);

        if (n >= 3)
            BV128<TM>(ref t[6], ref t[5], 32, true, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    HV128(ref t[8 + 4 * i + j], ref t[11 + 4 * i - j], i != 0, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 4; i++) BV128<TM>(ref t[29 - i], ref t[18 + i], 48 + (i >> 1) * 64, true, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    HV128(ref t[32 + 8 * i + j], ref t[39 + 8 * i - j], (i & 1) != 0, vMin, vMax);

        if (n >= 3)
            for (int i = 0; i < 4; i++) HV128(ref t[i], ref t[7 - i], false, vMin, vMax);
        if (n >= 4)
            for (int i = 0; i < 2; i++) BV128<TM>(ref t[13 - i], ref t[10 + i], 32, true, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                    HV128(ref t[16 + i * 8 + j], ref t[23 + i * 8 - j], i != 0, vMin, vMax);
        if (n == 6)
            for (int i = 0; i < 8; i++) BV128<TM>(ref t[59 - i], ref t[36 + i], i < 4 ? 48 : 112, true, vMin, vMax);

        if (n >= 4)
            for (int i = 0; i < 8; i++) HV128(ref t[i], ref t[15 - i], false, vMin, vMax);
        if (n >= 5)
            for (int i = 0; i < 4; i++) BV128<TM>(ref t[27 - i], ref t[20 + i], 32, true, vMin, vMax);
        if (n == 6)
        {
            for (int i = 0; i < 8; i++)
            {
                HV128(ref t[32 + i], ref t[47 - i], false, vMin, vMax);
                HV128(ref t[48 + i], ref t[63 - i], true, vMin, vMax);
            }
        }

        if (n >= 5)
            for (int i = 0; i < 16; i++) HV128(ref t[i], ref t[31 - i], false, vMin, vMax);
        if (n == 6)
        {
            for (int i = 0; i < 8; i++) BV128<TM>(ref t[55 - i], ref t[40 + i], 32, true, vMin, vMax);
            for (int i = 0; i < 32; i++) HV128(ref t[i], ref t[63 - i], false, vMin, vMax);
        }
    }

    private static unsafe void InverseAdstV128<TM>(Vector128<int>* t, Vector128<int>* copyT, int n, Vector128<int> vMin, Vector128<int> vMax) where TM : struct, ITxRange
    {
        if (n == 2 && TM.Wide)
        {
            // The products need 33 bits at 20-bit inputs: each input split as hi * 4096 + lo, the sums of hi
            // products are exact multiples of 4096 and only the lo sums are rounded (see BV128).
            var mask = Vector128.Create(4095);
            Vector128<int> h0 = Vector128.ShiftRightArithmetic(t[0], 12), h1 = Vector128.ShiftRightArithmetic(t[1], 12);
            Vector128<int> h2 = Vector128.ShiftRightArithmetic(t[2], 12), h3 = Vector128.ShiftRightArithmetic(t[3], 12);
            Vector128<int> l0 = t[0] & mask, l1 = t[1] & mask, l2 = t[2] & mask, l3 = t[3] & mask;
            var k1 = Vector128.Create(Sinpi1_9);
            var k2 = Vector128.Create(Sinpi2_9);
            var k3 = Vector128.Create(Sinpi3_9);
            var k4 = Vector128.Create(Sinpi4_9);
            var hs0 = k1 * h0 + k4 * h2 + k2 * h3;
            var hs1 = k2 * h0 - k1 * h2 - k4 * h3;
            var hs3 = k3 * h1;
            var hs2 = k3 * (h0 - h2 + h3);
            var ls0 = k1 * l0 + k4 * l2 + k2 * l3;
            var ls1 = k2 * l0 - k1 * l2 - k4 * l3;
            var ls3 = k3 * l1;
            var ls2 = k3 * (l0 - l2 + l3);
            var r = Vector128.Create(2048);
            t[0] = hs0 + hs3 + Vector128.ShiftRightArithmetic(ls0 + ls3 + r, 12);
            t[1] = hs1 + hs3 + Vector128.ShiftRightArithmetic(ls1 + ls3 + r, 12);
            t[2] = hs2 + Vector128.ShiftRightArithmetic(ls2 + r, 12);
            t[3] = hs0 + hs1 - hs3 + Vector128.ShiftRightArithmetic(ls0 + ls1 - ls3 + r, 12);
            return;
        }

        if (n == 2)
        {
            Vector128<int> t0 = t[0], t1 = t[1], t2 = t[2], t3 = t[3];
            Vector128<int> s0 = Vector128.Create(Sinpi1_9) * t0 + Vector128.Create(Sinpi4_9) * t2 + Vector128.Create(Sinpi2_9) * t3;
            Vector128<int> s1 = Vector128.Create(Sinpi2_9) * t0 - Vector128.Create(Sinpi1_9) * t2 - Vector128.Create(Sinpi4_9) * t3;
            Vector128<int> s3 = Vector128.Create(Sinpi3_9) * t1;
            Vector128<int> s2 = Vector128.Create(Sinpi3_9) * (t0 - t2 + t3);
            Vector128<int> v2048 = Vector128.Create(2048);
            t[0] = Vector128.ShiftRightArithmetic(s0 + s3 + v2048, 12);
            t[1] = Vector128.ShiftRightArithmetic(s1 + s3 + v2048, 12);
            t[2] = Vector128.ShiftRightArithmetic(s2 + v2048, 12);
            t[3] = Vector128.ShiftRightArithmetic(s0 + s1 - s3 + v2048, 12);
            return;
        }

        if (n == 3)
        {
            for (int i = 0; i < 8; i++) copyT[i] = t[i];
            for (int i = 0; i < 8; i++) t[i] = copyT[AdstIn3[i]];
            for (int i = 0; i < 4; i++) BV128<TM>(ref t[2 * i], ref t[2 * i + 1], 60 - 16 * i, true, vMin, vMax);
            for (int i = 0; i < 4; i++) HV128(ref t[i], ref t[4 + i], false, vMin, vMax);
            for (int i = 0; i < 2; i++) BV128<TM>(ref t[4 + 3 * i], ref t[5 + i], 48 - 32 * i, true, vMin, vMax);
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                    HV128(ref t[4 * j + i], ref t[2 + 4 * j + i], false, vMin, vMax);
            for (int i = 0; i < 2; i++) BV128<TM>(ref t[2 + 4 * i], ref t[3 + 4 * i], 32, true, vMin, vMax);
            for (int i = 0; i < 8; i++) copyT[i] = t[i];
            for (int i = 0; i < 8; i++) t[i] = (i & 1) != 0 ? -copyT[AdstOut3[i]] : copyT[AdstOut3[i]];
            return;
        }

        for (int i = 0; i < 16; i++) copyT[i] = t[i];
        for (int i = 0; i < 16; i++) t[i] = copyT[AdstIn4[i]];
        for (int i = 0; i < 8; i++) BV128<TM>(ref t[2 * i], ref t[2 * i + 1], 62 - 8 * i, true, vMin, vMax);
        for (int i = 0; i < 8; i++) HV128(ref t[i], ref t[8 + i], false, vMin, vMax);
        for (int i = 0; i < 2; i++)
        {
            BV128<TM>(ref t[8 + 2 * i], ref t[9 + 2 * i], 56 - 32 * i, true, vMin, vMax);
            BV128<TM>(ref t[13 + 2 * i], ref t[12 + 2 * i], 8 + 32 * i, true, vMin, vMax);
        }
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 4; i++)
                HV128(ref t[8 * j + i], ref t[4 + 8 * j + i], false, vMin, vMax);
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
                BV128<TM>(ref t[4 + 8 * j + 3 * i], ref t[5 + 8 * j + i], 48 - 32 * i, true, vMin, vMax);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 2; i++)
                HV128(ref t[4 * j + i], ref t[2 + 4 * j + i], false, vMin, vMax);
        for (int i = 0; i < 4; i++) BV128<TM>(ref t[2 + 4 * i], ref t[3 + 4 * i], 32, true, vMin, vMax);
        for (int i = 0; i < 16; i++) copyT[i] = t[i];
        for (int i = 0; i < 16; i++) t[i] = (i & 1) != 0 ? -copyT[AdstOut4[i]] : copyT[AdstOut4[i]];
    }

    private static unsafe void InverseWht4V128(Vector128<int>* t, int shift)
    {
        Vector128<int> a = shift > 0 ? Vector128.ShiftRightArithmetic(t[0], shift) : t[0];
        Vector128<int> c = shift > 0 ? Vector128.ShiftRightArithmetic(t[1], shift) : t[1];
        Vector128<int> d = shift > 0 ? Vector128.ShiftRightArithmetic(t[2], shift) : t[2];
        Vector128<int> b = shift > 0 ? Vector128.ShiftRightArithmetic(t[3], shift) : t[3];
        a += c;
        d -= b;
        Vector128<int> e = Vector128.ShiftRightArithmetic(a - d, 1);
        b = e - b;
        c = e - c;
        a -= b;
        d += c;
        t[0] = a; t[1] = b; t[2] = c; t[3] = d;
    }

    private static unsafe void InverseIdtxV128<TM>(Vector128<int>* t, int n) where TM : struct, ITxRange
    {
        if (n == 2)
        {
            Vector128<int> k = Vector128.Create(5793);
            Vector128<int> r = Vector128.Create(2048);
            if (TM.Wide)
            {
                var mask = Vector128.Create(4095);
                for (int i = 0; i < 4; i++)
                    t[i] = Vector128.ShiftRightArithmetic(t[i], 12) * k + Vector128.ShiftRightArithmetic((t[i] & mask) * k + r, 12);
            }
            else
            {
                for (int i = 0; i < 4; i++) t[i] = Vector128.ShiftRightArithmetic(t[i] * k + r, 12);
            }
        }
        else if (n == 3)
        {
            for (int i = 0; i < 8; i++) t[i] += t[i];
        }
        else if (n == 4)
        {
            Vector128<int> k = Vector128.Create(11586);
            Vector128<int> r = Vector128.Create(2048);
            if (TM.Wide)
            {
                var mask = Vector128.Create(4095);
                for (int i = 0; i < 16; i++)
                    t[i] = Vector128.ShiftRightArithmetic(t[i], 12) * k + Vector128.ShiftRightArithmetic((t[i] & mask) * k + r, 12);
            }
            else
            {
                for (int i = 0; i < 16; i++) t[i] = Vector128.ShiftRightArithmetic(t[i] * k + r, 12);
            }
        }
        else
        {
            for (int i = 0; i < 32; i++) t[i] = Vector128.ShiftLeft(t[i], 2);
        }
    }

    // ==================== In-Register SIMD Transposes ====================

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose4x4V128(
        Vector128<int> r0, Vector128<int> r1, Vector128<int> r2, Vector128<int> r3,
        out Vector128<int> c0, out Vector128<int> c1, out Vector128<int> c2, out Vector128<int> c3)
    {
        if (Sse2.IsSupported)
        {
            Vector128<int> t0 = Sse2.UnpackLow(r0, r1);
            Vector128<int> t1 = Sse2.UnpackHigh(r0, r1);
            Vector128<int> t2 = Sse2.UnpackLow(r2, r3);
            Vector128<int> t3 = Sse2.UnpackHigh(r2, r3);
            c0 = Sse2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32();
            c1 = Sse2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
            c2 = Sse2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32();
            c3 = Sse2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        }
        else
        {
            c0 = Vector128.Create(r0.GetElement(0), r1.GetElement(0), r2.GetElement(0), r3.GetElement(0));
            c1 = Vector128.Create(r0.GetElement(1), r1.GetElement(1), r2.GetElement(1), r3.GetElement(1));
            c2 = Vector128.Create(r0.GetElement(2), r1.GetElement(2), r2.GetElement(2), r3.GetElement(2));
            c3 = Vector128.Create(r0.GetElement(3), r1.GetElement(3), r2.GetElement(3), r3.GetElement(3));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8V256(
        Vector256<int> r0, Vector256<int> r1, Vector256<int> r2, Vector256<int> r3,
        Vector256<int> r4, Vector256<int> r5, Vector256<int> r6, Vector256<int> r7,
        out Vector256<int> c0, out Vector256<int> c1, out Vector256<int> c2, out Vector256<int> c3,
        out Vector256<int> c4, out Vector256<int> c5, out Vector256<int> c6, out Vector256<int> c7)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1);
        Vector256<int> t1 = Avx2.UnpackHigh(r0, r1);
        Vector256<int> t2 = Avx2.UnpackLow(r2, r3);
        Vector256<int> t3 = Avx2.UnpackHigh(r2, r3);
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5);
        Vector256<int> t5 = Avx2.UnpackHigh(r4, r5);
        Vector256<int> t6 = Avx2.UnpackLow(r6, r7);
        Vector256<int> t7 = Avx2.UnpackHigh(r6, r7);

        Vector256<int> u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32();
        Vector256<int> u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
        Vector256<int> u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32();
        Vector256<int> u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        Vector256<int> u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()).AsInt32();
        Vector256<int> u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64()).AsInt32();
        Vector256<int> u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()).AsInt32();
        Vector256<int> u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64()).AsInt32();

        c0 = Avx2.Permute2x128(u0, u4, 0x20);
        c1 = Avx2.Permute2x128(u1, u5, 0x20);
        c2 = Avx2.Permute2x128(u2, u6, 0x20);
        c3 = Avx2.Permute2x128(u3, u7, 0x20);
        c4 = Avx2.Permute2x128(u0, u4, 0x31);
        c5 = Avx2.Permute2x128(u1, u5, 0x31);
        c6 = Avx2.Permute2x128(u2, u6, 0x31);
        c7 = Avx2.Permute2x128(u3, u7, 0x31);
    }
}

/// <summary>
/// Selects the multiply of the vector butterflies. <see cref="TxNarrow"/>: products in int32, exact while inputs stay within
/// 18 bits (the column pass at every depth, the row pass at 8 and 10 bits). <see cref="TxWide"/>: each input split as
/// hi * 4096 + lo so that 20-bit inputs (the 12-bit row pass) stay exact in int32. A struct type argument, so the JIT
/// compiles each variant separately and the test folds away.
/// </summary>
internal interface ITxRange
{
    static abstract bool Wide { get; }
}

internal readonly struct TxNarrow : ITxRange
{
    public static bool Wide => false;
}

internal readonly struct TxWide : ITxRange
{
    public static bool Wide => true;
}
