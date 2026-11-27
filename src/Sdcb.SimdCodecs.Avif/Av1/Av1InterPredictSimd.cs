using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AVX2 (<see cref="Vector256{T}"/>, 8-wide) and <see cref="Vector128{T}"/> (4-wide) kernels for
/// AV1 Inter prediction (§7.11.3): unscaled copy, 1D horizontal/vertical, 2D separable 8-tap subpel
/// interpolation, and single/compound output blending. Bit-exact with scalar <see cref="Av1InterPredict"/>.
/// </summary>
/// <remarks>
/// Range proof for the pointer accesses (the caller, <c>Av1InterPredict</c>, establishes every premise):
/// <list type="bullet">
/// <item>Reference reads: rows are clamped to [0, lastY]; columns span [xInt - 3, xInt + w + 3] (8-tap horizontal,
/// each vector of 8 / 4 samples at srcRow + c + t with c + 7 &lt; w + 7) or [xInt, xInt + w - 1] (vertical-only), which
/// the caller's interior test keeps within [0, lastX]. <c>CheckReferenceBounds</c> proves lastX &lt; refStride and
/// (lastY + 1) * refStride &lt;= refBuf.Length, so every offset sy * refStride + sx is inside refBuf.</item>
/// <item>Intermediate / prediction buffers: w, h &lt;= 128, so rows * w &lt;= (128 + 7) * 128 fits <c>_interBuf</c>
/// (288 * 128) and w * h fits <c>_pred0</c> / <c>_pred1</c> (128 * 128).</item>
/// <item>Output writes (WriteSingle / BlendAvg / BlendDist): the caller checks the block's rectangle against
/// dstStride and dstPlane.Length before calling.</item>
/// </list>
/// </remarks>
internal static class Av1InterPredictSimd
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Store8U16FromI32V256(Vector256<int> sum, Vector256<int> vZero, Vector256<int> vMax, ushort* dst)
    {
        Vector256<int> clamped = Vector256.Min(vMax, Vector256.Max(vZero, sum));
        Vector256<ushort> packed = Avx2.PackUnsignedSaturate(clamped, clamped);
        Vector256<ulong> perm = Avx2.Permute4x64(packed.AsUInt64(), 0b11_01_10_00);
        Vector128.Store(perm.GetLower().AsUInt16(), dst);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Store4U16FromI32V128(Vector128<int> sum, Vector128<int> vZero, Vector128<int> vMax, ushort* dst)
    {
        Vector128<int> clamped = Vector128.Min(vMax, Vector128.Max(vZero, sum));
        Vector128<ushort> packed = Vector128.Narrow(clamped.AsUInt32(), clamped.AsUInt32());
        *(ulong*)dst = packed.AsUInt64().GetElement(0);
    }

    // ==================== Unscaled Horizontal Filter (refBuf[ushort] -> dst[int]) ====================

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ConvolveHorizInteriorV256(
        ushort[] refBuf, int refStride, int lastY, int xInt, int yStart, int rows, int w,
        short[] filters, int fOff, int round0, int postShift, int[] dst)
    {
        // In range by the class remarks: xInt - 3 >= 0, xInt + w + 3 <= lastX, rows * w <= dst.Length.
        int tStart = 0;
        while (tStart < 8 && filters[fOff + tStart] == 0) tStart++;
        int tEnd = 8;
        while (tEnd > tStart && filters[fOff + tEnd - 1] == 0) tEnd--;

        Vector256<int> vRnd0 = Vector256.Create(1 << (round0 - 1));
        Vector256<int> vPostRnd = postShift > 0 ? Vector256.Create(1 << (postShift - 1)) : Vector256<int>.Zero;
        int* taps = stackalloc int[8];
        for (int t = tStart; t < tEnd; t++)
            taps[t] = filters[fOff + t];

        fixed (ushort* refBase = refBuf)
        fixed (int* dstBase = dst)
        {
            for (int r = 0; r < rows; r++)
            {
                int sy = yStart + r;
                if (sy < 0) sy = 0;
                else if (sy > lastY) sy = lastY;
                ushort* srcRow = refBase + sy * refStride + (xInt - 3);
                int* dstRow = dstBase + r * w;

                int c = 0;
                for (; c + 8 <= w; c += 8)
                {
                    Vector256<int> acc = Avx2.ConvertToVector256Int32(srcRow + c + tStart) * Vector256.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Avx2.ConvertToVector256Int32(srcRow + c + t) * Vector256.Create(taps[t]);
                    Vector256<int> v = Vector256.ShiftRightArithmetic(acc + vRnd0, round0);
                    if (postShift > 0)
                        v = Vector256.ShiftRightArithmetic(v + vPostRnd, postShift);
                    Vector256.Store(v, dstRow + c);
                }
                if (c + 4 <= w)
                {
                    Vector128<int> vRnd0_128 = vRnd0.GetLower();
                    Vector128<int> acc = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(srcRow + c + tStart)).AsUInt16()).AsInt32() * Vector128.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(srcRow + c + t)).AsUInt16()).AsInt32() * Vector128.Create(taps[t]);
                    Vector128<int> v = Vector128.ShiftRightArithmetic(acc + vRnd0_128, round0);
                    if (postShift > 0)
                        v = Vector128.ShiftRightArithmetic(v + vPostRnd.GetLower(), postShift);
                    Vector128.Store(v, dstRow + c);
                    c += 4;
                }
                for (; c < w; c++)
                {
                    int acc = 0;
                    for (int t = tStart; t < tEnd; t++)
                        acc += taps[t] * srcRow[c + t];
                    int v = (acc + (1 << (round0 - 1))) >> round0;
                    if (postShift > 0)
                        v = (v + (1 << (postShift - 1))) >> postShift;
                    dstRow[c] = v;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ConvolveHorizInteriorV128(
        ushort[] refBuf, int refStride, int lastY, int xInt, int yStart, int rows, int w,
        short[] filters, int fOff, int round0, int postShift, int[] dst)
    {
        int tStart = 0;
        while (tStart < 8 && filters[fOff + tStart] == 0) tStart++;
        int tEnd = 8;
        while (tEnd > tStart && filters[fOff + tEnd - 1] == 0) tEnd--;

        Vector128<int> vRnd0 = Vector128.Create(1 << (round0 - 1));
        Vector128<int> vPostRnd = postShift > 0 ? Vector128.Create(1 << (postShift - 1)) : Vector128<int>.Zero;
        int* taps = stackalloc int[8];
        for (int t = tStart; t < tEnd; t++)
            taps[t] = filters[fOff + t];

        fixed (ushort* refBase = refBuf)
        fixed (int* dstBase = dst)
        {
            for (int r = 0; r < rows; r++)
            {
                int sy = yStart + r;
                if (sy < 0) sy = 0;
                else if (sy > lastY) sy = lastY;
                ushort* srcRow = refBase + sy * refStride + (xInt - 3);
                int* dstRow = dstBase + r * w;

                int c = 0;
                for (; c + 4 <= w; c += 4)
                {
                    Vector128<int> acc = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(srcRow + c + tStart)).AsUInt16()).AsInt32() * Vector128.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(srcRow + c + t)).AsUInt16()).AsInt32() * Vector128.Create(taps[t]);
                    Vector128<int> v = Vector128.ShiftRightArithmetic(acc + vRnd0, round0);
                    if (postShift > 0)
                        v = Vector128.ShiftRightArithmetic(v + vPostRnd, postShift);
                    Vector128.Store(v, dstRow + c);
                }
                for (; c < w; c++)
                {
                    int acc = 0;
                    for (int t = tStart; t < tEnd; t++)
                        acc += taps[t] * srcRow[c + t];
                    int v = (acc + (1 << (round0 - 1))) >> round0;
                    if (postShift > 0)
                        v = (v + (1 << (postShift - 1))) >> postShift;
                    dstRow[c] = v;
                }
            }
        }
    }

    // ==================== Unscaled Vertical Filter (interBuf[int] -> pred[int]) ====================

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ConvolveVertIntV256(
        int[] interBuf, int w, int h, short[] filters, int fOff, int round1, int[] pred)
    {
        int tStart = 0;
        while (tStart < 8 && filters[fOff + tStart] == 0) tStart++;
        int tEnd = 8;
        while (tEnd > tStart && filters[fOff + tEnd - 1] == 0) tEnd--;

        Vector256<int> vRnd1 = Vector256.Create(1 << (round1 - 1));
        int* taps = stackalloc int[8];
        for (int t = tStart; t < tEnd; t++)
            taps[t] = filters[fOff + t];

        fixed (int* srcBase = interBuf)
        fixed (int* dstBase = pred)
        {
            for (int r = 0; r < h; r++)
            {
                int* dstRow = dstBase + r * w;
                int c = 0;
                for (; c + 8 <= w; c += 8)
                {
                    Vector256<int> acc = Vector256.Load(srcBase + (r + tStart) * w + c) * Vector256.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Vector256.Load(srcBase + (r + t) * w + c) * Vector256.Create(taps[t]);
                    Vector256.Store(Vector256.ShiftRightArithmetic(acc + vRnd1, round1), dstRow + c);
                }
                if (c + 4 <= w)
                {
                    Vector128<int> acc = Vector128.Load(srcBase + (r + tStart) * w + c) * Vector128.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Vector128.Load(srcBase + (r + t) * w + c) * Vector128.Create(taps[t]);
                    Vector128.Store(Vector128.ShiftRightArithmetic(acc + vRnd1.GetLower(), round1), dstRow + c);
                    c += 4;
                }
                for (; c < w; c++)
                {
                    int acc = 0;
                    for (int t = tStart; t < tEnd; t++)
                        acc += taps[t] * srcBase[(r + t) * w + c];
                    dstRow[c] = (acc + (1 << (round1 - 1))) >> round1;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ConvolveVertIntV128(
        int[] interBuf, int w, int h, short[] filters, int fOff, int round1, int[] pred)
    {
        int tStart = 0;
        while (tStart < 8 && filters[fOff + tStart] == 0) tStart++;
        int tEnd = 8;
        while (tEnd > tStart && filters[fOff + tEnd - 1] == 0) tEnd--;

        Vector128<int> vRnd1 = Vector128.Create(1 << (round1 - 1));
        int* taps = stackalloc int[8];
        for (int t = tStart; t < tEnd; t++)
            taps[t] = filters[fOff + t];

        fixed (int* srcBase = interBuf)
        fixed (int* dstBase = pred)
        {
            for (int r = 0; r < h; r++)
            {
                int* dstRow = dstBase + r * w;
                int c = 0;
                for (; c + 4 <= w; c += 4)
                {
                    Vector128<int> acc = Vector128.Load(srcBase + (r + tStart) * w + c) * Vector128.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Vector128.Load(srcBase + (r + t) * w + c) * Vector128.Create(taps[t]);
                    Vector128.Store(Vector128.ShiftRightArithmetic(acc + vRnd1, round1), dstRow + c);
                }
                for (; c < w; c++)
                {
                    int acc = 0;
                    for (int t = tStart; t < tEnd; t++)
                        acc += taps[t] * srcBase[(r + t) * w + c];
                    dstRow[c] = (acc + (1 << (round1 - 1))) >> round1;
                }
            }
        }
    }

    // ==================== Unscaled Vertical-Only Filter (refBuf[ushort] -> pred[int]) ====================

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ConvolveVertOnlyInteriorV256(
        ushort[] refBuf, int refStride, int lastY, int xInt, int yInt, int w, int h,
        short[] filters, int fOff, int shift, int[] pred)
    {
        // In range by the class remarks: xInt >= 0, xInt + w - 1 <= lastX, rows clamped to [0, lastY].
        int tStart = 0;
        while (tStart < 8 && filters[fOff + tStart] == 0) tStart++;
        int tEnd = 8;
        while (tEnd > tStart && filters[fOff + tEnd - 1] == 0) tEnd--;

        Vector256<int> vRnd = Vector256.Create(1 << (shift - 1));
        int* taps = stackalloc int[8];
        int* rowOffs = stackalloc int[8];
        for (int t = tStart; t < tEnd; t++)
            taps[t] = filters[fOff + t];

        fixed (ushort* refBase = refBuf)
        fixed (int* dstBase = pred)
        {
            for (int r = 0; r < h; r++)
            {
                int* dstRow = dstBase + r * w;
                for (int t = tStart; t < tEnd; t++)
                {
                    int sy = yInt + r - 3 + t;
                    if (sy < 0) sy = 0;
                    else if (sy > lastY) sy = lastY;
                    rowOffs[t] = sy * refStride + xInt;
                }

                int c = 0;
                for (; c + 8 <= w; c += 8)
                {
                    Vector256<int> acc = Avx2.ConvertToVector256Int32(refBase + rowOffs[tStart] + c) * Vector256.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Avx2.ConvertToVector256Int32(refBase + rowOffs[t] + c) * Vector256.Create(taps[t]);
                    Vector256.Store(Vector256.ShiftRightArithmetic(acc + vRnd, shift), dstRow + c);
                }
                if (c + 4 <= w)
                {
                    Vector128<int> acc = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(refBase + rowOffs[tStart] + c)).AsUInt16()).AsInt32() * Vector128.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(refBase + rowOffs[t] + c)).AsUInt16()).AsInt32() * Vector128.Create(taps[t]);
                    Vector128.Store(Vector128.ShiftRightArithmetic(acc + vRnd.GetLower(), shift), dstRow + c);
                    c += 4;
                }
                for (; c < w; c++)
                {
                    int acc = 0;
                    for (int t = tStart; t < tEnd; t++)
                        acc += taps[t] * refBase[rowOffs[t] + c];
                    dstRow[c] = (acc + (1 << (shift - 1))) >> shift;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void ConvolveVertOnlyInteriorV128(
        ushort[] refBuf, int refStride, int lastY, int xInt, int yInt, int w, int h,
        short[] filters, int fOff, int shift, int[] pred)
    {
        int tStart = 0;
        while (tStart < 8 && filters[fOff + tStart] == 0) tStart++;
        int tEnd = 8;
        while (tEnd > tStart && filters[fOff + tEnd - 1] == 0) tEnd--;

        Vector128<int> vRnd = Vector128.Create(1 << (shift - 1));
        int* taps = stackalloc int[8];
        int* rowOffs = stackalloc int[8];
        for (int t = tStart; t < tEnd; t++)
            taps[t] = filters[fOff + t];

        fixed (ushort* refBase = refBuf)
        fixed (int* dstBase = pred)
        {
            for (int r = 0; r < h; r++)
            {
                int* dstRow = dstBase + r * w;
                for (int t = tStart; t < tEnd; t++)
                {
                    int sy = yInt + r - 3 + t;
                    if (sy < 0) sy = 0;
                    else if (sy > lastY) sy = lastY;
                    rowOffs[t] = sy * refStride + xInt;
                }

                int c = 0;
                for (; c + 4 <= w; c += 4)
                {
                    Vector128<int> acc = Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(refBase + rowOffs[tStart] + c)).AsUInt16()).AsInt32() * Vector128.Create(taps[tStart]);
                    for (int t = tStart + 1; t < tEnd; t++)
                        acc += Vector128.WidenLower(Vector128.CreateScalarUnsafe(*(ulong*)(refBase + rowOffs[t] + c)).AsUInt16()).AsInt32() * Vector128.Create(taps[t]);
                    Vector128.Store(Vector128.ShiftRightArithmetic(acc + vRnd, shift), dstRow + c);
                }
                for (; c < w; c++)
                {
                    int acc = 0;
                    for (int t = tStart; t < tEnd; t++)
                        acc += taps[t] * refBase[rowOffs[t] + c];
                    dstRow[c] = (acc + (1 << (shift - 1))) >> shift;
                }
            }
        }
    }

    // ==================== Output Single / Compound Blends ====================

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void WriteSingleV256(int[] pred0, ushort[] dstPlane, int dstStride, int x, int y, int w, int h, int maxSample)
    {
        Vector256<int> vZero = Vector256<int>.Zero;
        Vector256<int> vMax = Vector256.Create(maxSample);
        fixed (int* p0Base = pred0)
        fixed (ushort* dstBase = dstPlane)
        {
            for (int r = 0; r < h; r++)
            {
                int* p0 = p0Base + r * w;
                ushort* dst = dstBase + (y + r) * dstStride + x;
                int c = 0;
                for (; c + 8 <= w; c += 8)
                    Store8U16FromI32V256(Vector256.Load(p0 + c), vZero, vMax, dst + c);
                for (; c + 4 <= w; c += 4)
                    Store4U16FromI32V128(Vector128.Load(p0 + c), vZero.GetLower(), vMax.GetLower(), dst + c);
                for (; c < w; c++)
                {
                    int v = p0[c];
                    dst[c] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void BlendAvgV256(int[] pred0, int[] pred1, ushort[] dstPlane, int dstStride, int x, int y, int w, int h, int shift, int maxSample)
    {
        Vector256<int> vZero = Vector256<int>.Zero;
        Vector256<int> vMax = Vector256.Create(maxSample);
        Vector256<int> vRnd = Vector256.Create(1 << (shift - 1));
        fixed (int* p0Base = pred0)
        fixed (int* p1Base = pred1)
        fixed (ushort* dstBase = dstPlane)
        {
            for (int r = 0; r < h; r++)
            {
                int* p0 = p0Base + r * w;
                int* p1 = p1Base + r * w;
                ushort* dst = dstBase + (y + r) * dstStride + x;
                int c = 0;
                for (; c + 8 <= w; c += 8)
                {
                    Vector256<int> v = Vector256.ShiftRightArithmetic(Vector256.Load(p0 + c) + Vector256.Load(p1 + c) + vRnd, shift);
                    Store8U16FromI32V256(v, vZero, vMax, dst + c);
                }
                for (; c + 4 <= w; c += 4)
                {
                    Vector128<int> v = Vector128.ShiftRightArithmetic(Vector128.Load(p0 + c) + Vector128.Load(p1 + c) + vRnd.GetLower(), shift);
                    Store4U16FromI32V128(v, vZero.GetLower(), vMax.GetLower(), dst + c);
                }
                for (; c < w; c++)
                {
                    int v = (p0[c] + p1[c] + (1 << (shift - 1))) >> shift;
                    dst[c] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void BlendDistV256(int[] pred0, int[] pred1, ushort[] dstPlane, int dstStride, int x, int y, int w, int h, int fwdW, int bckW, int shift, int maxSample)
    {
        Vector256<int> vZero = Vector256<int>.Zero;
        Vector256<int> vMax = Vector256.Create(maxSample);
        Vector256<int> vFwd = Vector256.Create(fwdW);
        Vector256<int> vBck = Vector256.Create(bckW);
        Vector256<int> vRnd = Vector256.Create(1 << (shift - 1));
        fixed (int* p0Base = pred0)
        fixed (int* p1Base = pred1)
        fixed (ushort* dstBase = dstPlane)
        {
            for (int r = 0; r < h; r++)
            {
                int* p0 = p0Base + r * w;
                int* p1 = p1Base + r * w;
                ushort* dst = dstBase + (y + r) * dstStride + x;
                int c = 0;
                for (; c + 8 <= w; c += 8)
                {
                    Vector256<int> v = Vector256.ShiftRightArithmetic(vFwd * Vector256.Load(p0 + c) + vBck * Vector256.Load(p1 + c) + vRnd, shift);
                    Store8U16FromI32V256(v, vZero, vMax, dst + c);
                }
                for (; c + 4 <= w; c += 4)
                {
                    Vector128<int> v = Vector128.ShiftRightArithmetic(vFwd.GetLower() * Vector128.Load(p0 + c) + vBck.GetLower() * Vector128.Load(p1 + c) + vRnd.GetLower(), shift);
                    Store4U16FromI32V128(v, vZero.GetLower(), vMax.GetLower(), dst + c);
                }
                for (; c < w; c++)
                {
                    int v = (fwdW * p0[c] + bckW * p1[c] + (1 << (shift - 1))) >> shift;
                    dst[c] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void WriteSingleV128(int[] pred0, ushort[] dstPlane, int dstStride, int x, int y, int w, int h, int maxSample)
    {
        Vector128<int> vZero = Vector128<int>.Zero;
        Vector128<int> vMax = Vector128.Create(maxSample);
        fixed (int* p0Base = pred0)
        fixed (ushort* dstBase = dstPlane)
        {
            for (int r = 0; r < h; r++)
            {
                int* p0 = p0Base + r * w;
                ushort* dst = dstBase + (y + r) * dstStride + x;
                int c = 0;
                for (; c + 4 <= w; c += 4)
                    Store4U16FromI32V128(Vector128.Load(p0 + c), vZero, vMax, dst + c);
                for (; c < w; c++)
                {
                    int v = p0[c];
                    dst[c] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void BlendAvgV128(int[] pred0, int[] pred1, ushort[] dstPlane, int dstStride, int x, int y, int w, int h, int shift, int maxSample)
    {
        Vector128<int> vZero = Vector128<int>.Zero;
        Vector128<int> vMax = Vector128.Create(maxSample);
        Vector128<int> vRnd = Vector128.Create(1 << (shift - 1));
        fixed (int* p0Base = pred0)
        fixed (int* p1Base = pred1)
        fixed (ushort* dstBase = dstPlane)
        {
            for (int r = 0; r < h; r++)
            {
                int* p0 = p0Base + r * w;
                int* p1 = p1Base + r * w;
                ushort* dst = dstBase + (y + r) * dstStride + x;
                int c = 0;
                for (; c + 4 <= w; c += 4)
                {
                    Vector128<int> v = Vector128.ShiftRightArithmetic(Vector128.Load(p0 + c) + Vector128.Load(p1 + c) + vRnd, shift);
                    Store4U16FromI32V128(v, vZero, vMax, dst + c);
                }
                for (; c < w; c++)
                {
                    int v = (p0[c] + p1[c] + (1 << (shift - 1))) >> shift;
                    dst[c] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static unsafe void BlendDistV128(int[] pred0, int[] pred1, ushort[] dstPlane, int dstStride, int x, int y, int w, int h, int fwdW, int bckW, int shift, int maxSample)
    {
        Vector128<int> vZero = Vector128<int>.Zero;
        Vector128<int> vMax = Vector128.Create(maxSample);
        Vector128<int> vFwd = Vector128.Create(fwdW);
        Vector128<int> vBck = Vector128.Create(bckW);
        Vector128<int> vRnd = Vector128.Create(1 << (shift - 1));
        fixed (int* p0Base = pred0)
        fixed (int* p1Base = pred1)
        fixed (ushort* dstBase = dstPlane)
        {
            for (int r = 0; r < h; r++)
            {
                int* p0 = p0Base + r * w;
                int* p1 = p1Base + r * w;
                ushort* dst = dstBase + (y + r) * dstStride + x;
                int c = 0;
                for (; c + 4 <= w; c += 4)
                {
                    Vector128<int> v = Vector128.ShiftRightArithmetic(vFwd * Vector128.Load(p0 + c) + vBck * Vector128.Load(p1 + c) + vRnd, shift);
                    Store4U16FromI32V128(v, vZero, vMax, dst + c);
                }
                for (; c < w; c++)
                {
                    int v = (fwdW * p0[c] + bckW * p1[c] + (1 << (shift - 1))) >> shift;
                    dst[c] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
        }
    }
}
