using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// The VP8 encoder's per-block arithmetic: residual + forward DCT, quantisation, squared error and
/// SATD. Only the encoder's choices depend on these, never the bitstream's meaning, so they need not
/// match any other implementation; the vector paths still give the scalar ones' exact results, so
/// every tier writes the same file.
/// </summary>
internal static class Vp8EncoderKernels
{
    /// <summary>
    /// Forward DCT (see <see cref="Vp8Fdct"/>) of <paramref name="src"/> − <paramref name="pred"/>,
    /// 4×4, into raster-order <paramref name="c"/>.
    /// </summary>
    public static void ResidualDct(ref byte src, int srcStride, ref byte pred, int predStride, Span<int> c)
    {
        if (VectorPath)
        {
            ResidualDctVector(ref src, srcStride, ref pred, predStride, c);
        }
        else
        {
            ResidualDctScalar(ref src, srcStride, ref pred, predStride, c);
        }
    }

    /// <summary>Whether the Vector128 kernels run (they need a 4×4 integer transpose).</summary>
    internal static bool VectorPath => Vector128.IsHardwareAccelerated && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported);

    internal static void ResidualDctScalar(ref byte src, int srcStride, ref byte pred, int predStride, Span<int> c)
    {
        ref int o = ref MemoryMarshal.GetReference(c);
        int t0, t1, t2, t3, t4, t5, t6, t7, t8, t9, t10, t11, t12, t13, t14, t15;
        Row(ref src, ref pred, out t0, out t1, out t2, out t3);
        Row(ref Unsafe.Add(ref src, srcStride), ref Unsafe.Add(ref pred, predStride), out t4, out t5, out t6, out t7);
        Row(ref Unsafe.Add(ref src, 2 * srcStride), ref Unsafe.Add(ref pred, 2 * predStride), out t8, out t9, out t10, out t11);
        Row(ref Unsafe.Add(ref src, 3 * srcStride), ref Unsafe.Add(ref pred, 3 * predStride), out t12, out t13, out t14, out t15);
        Column(t0, t4, t8, t12, ref o, 0);
        Column(t1, t5, t9, t13, ref o, 1);
        Column(t2, t6, t10, t14, ref o, 2);
        Column(t3, t7, t11, t15, ref o, 3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Row(ref byte s, ref byte p, out int r0, out int r1, out int r2, out int r3)
    {
        int x0 = s - p, x1 = Unsafe.Add(ref s, 1) - Unsafe.Add(ref p, 1);
        int x2 = Unsafe.Add(ref s, 2) - Unsafe.Add(ref p, 2), x3 = Unsafe.Add(ref s, 3) - Unsafe.Add(ref p, 3);
        int a = x0 + x3, b = x1 + x2, c = x1 - x2, d = x0 - x3;
        r0 = (a + b) * 8;
        r2 = (a - b) * 8;
        r1 = (d * 5352 + c * 2217 + 256) >> 9;
        r3 = (d * 2217 - c * 5352 + 256) >> 9;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Column(int y0, int y1, int y2, int y3, ref int o, int k)
    {
        int a = y0 + y3, b = y1 + y2, c = y1 - y2, d = y0 - y3;
        Unsafe.Add(ref o, k) = (a + b + 8) >> 4;
        Unsafe.Add(ref o, 8 + k) = (a - b + 8) >> 4;
        Unsafe.Add(ref o, 4 + k) = (d * 5352 + c * 2217 + 32768) >> 16;
        Unsafe.Add(ref o, 12 + k) = (d * 2217 - c * 5352 + 32768) >> 16;
    }

    /// <summary>
    /// Quantises raster coefficients from zigzag index <paramref name="start"/> into zigzag
    /// <paramref name="levels"/> and their raster dequantised values (positions before
    /// <paramref name="start"/> are left alone). Level = (|c| · reciprocal + rounding · 256) >> 16, at
    /// most 2048. Returns the zigzag index of the last non-zero level, or −1.
    /// </summary>
    public static int Quantize(ReadOnlySpan<int> coefficients, Span<short> levels, Span<short> dequant, int start,
        int dcStep, int dcReciprocal, int dcRounding, int acStep, int acReciprocal, int acRounding) =>
        VectorPath
            ? QuantizeVector(coefficients, levels, dequant, start, dcStep, dcReciprocal, dcRounding, acStep, acReciprocal, acRounding)
            : QuantizeScalar(coefficients, levels, dequant, start, dcStep, dcReciprocal, dcRounding, acStep, acReciprocal, acRounding);

    internal static int QuantizeScalar(ReadOnlySpan<int> coefficients, Span<short> levels, Span<short> dequant, int start,
        int dcStep, int dcReciprocal, int dcRounding, int acStep, int acReciprocal, int acRounding)
    {
        ref byte zigzag = ref MemoryMarshal.GetArrayDataReference(Vp8Tables.Zigzag);
        ref int c = ref MemoryMarshal.GetReference(coefficients);
        ref short l = ref MemoryMarshal.GetReference(levels);
        ref short dq = ref MemoryMarshal.GetReference(dequant);
        int last = -1;
        for (int i = start; i < 16; i++)
        {
            int pos = Unsafe.Add(ref zigzag, i);
            int v = Unsafe.Add(ref c, pos);
            int magnitude = Math.Abs(v);
            int level;
            int step;
            if (i == 0)
            {
                level = (magnitude * dcReciprocal + (dcRounding << 8)) >> 16;
                step = dcStep;
            }
            else
            {
                level = (magnitude * acReciprocal + (acRounding << 8)) >> 16;
                step = acStep;
            }

            level = Math.Min(level, Vp8EncoderCosts.MaxLevel);
            if (level != 0)
            {
                last = i;
                level = v < 0 ? -level : level;
            }

            Unsafe.Add(ref l, i) = (short)level;
            Unsafe.Add(ref dq, pos) = (short)(level * step);
        }

        return last;
    }

    /// <summary>Squared error of a <paramref name="width"/> × <paramref name="height"/> block (width 4, 8 or 16).</summary>
    public static int Sse(ref byte a, int aStride, ref byte b, int bStride, int width, int height)
    {
        if (Vector128.IsHardwareAccelerated && width >= 8)
        {
            Vector128<int> sum = Vector128<int>.Zero;
            for (int y = 0; y < height; y++)
            {
                ref byte ra = ref Unsafe.Add(ref a, y * aStride);
                ref byte rb = ref Unsafe.Add(ref b, y * bStride);
                for (int x = 0; x < width; x += 8)
                {
                    // Eight bytes each: the rows are at least x + 8 long.
                    Vector128<short> va = Vector128.WidenLower(
                        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ra, x))).AsByte()).AsInt16();
                    Vector128<short> vb = Vector128.WidenLower(
                        Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rb, x))).AsByte()).AsInt16();
                    Vector128<short> d = va - vb;
                    (Vector128<int> lo, Vector128<int> hi) = Vector128.Widen(d);
                    sum += lo * lo + hi * hi;
                }
            }

            return Vector128.Sum(sum);
        }

        int total = 0;
        for (int y = 0; y < height; y++)
        {
            ref byte ra = ref Unsafe.Add(ref a, y * aStride);
            ref byte rb = ref Unsafe.Add(ref b, y * bStride);
            for (int x = 0; x < width; x++)
            {
                int d = Unsafe.Add(ref ra, x) - Unsafe.Add(ref rb, x);
                total += d * d;
            }
        }

        return total;
    }

    /// <summary>Sum of absolute values of the 4×4 Hadamard transform of a − b, halved.</summary>
    public static int Satd4(ref byte a, int aStride, ref byte b, int bStride) =>
        VectorPath ? Satd4Vector(ref a, aStride, ref b, bStride) : Satd4Scalar(ref a, aStride, ref b, bStride);

    internal static int Satd4Scalar(ref byte a, int aStride, ref byte b, int bStride)
    {
        int d0, d1, d2, d3, d4, d5, d6, d7, d8, d9, d10, d11, d12, d13, d14, d15;
        HRow(ref a, ref b, out d0, out d1, out d2, out d3);
        HRow(ref Unsafe.Add(ref a, aStride), ref Unsafe.Add(ref b, bStride), out d4, out d5, out d6, out d7);
        HRow(ref Unsafe.Add(ref a, 2 * aStride), ref Unsafe.Add(ref b, 2 * bStride), out d8, out d9, out d10, out d11);
        HRow(ref Unsafe.Add(ref a, 3 * aStride), ref Unsafe.Add(ref b, 3 * bStride), out d12, out d13, out d14, out d15);
        return (HColumn(d0, d4, d8, d12) + HColumn(d1, d5, d9, d13) + HColumn(d2, d6, d10, d14) + HColumn(d3, d7, d11, d15)) >> 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void HRow(ref byte a, ref byte b, out int r0, out int r1, out int r2, out int r3)
    {
        int x0 = a - b, x1 = Unsafe.Add(ref a, 1) - Unsafe.Add(ref b, 1);
        int x2 = Unsafe.Add(ref a, 2) - Unsafe.Add(ref b, 2), x3 = Unsafe.Add(ref a, 3) - Unsafe.Add(ref b, 3);
        int e0 = x0 + x3, e1 = x1 + x2, e2 = x1 - x2, e3 = x0 - x3;
        r0 = e0 + e1;
        r1 = e3 + e2;
        r2 = e0 - e1;
        r3 = e3 - e2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HColumn(int y0, int y1, int y2, int y3)
    {
        int e0 = y0 + y3, e1 = y1 + y2, e2 = y1 - y2, e3 = y0 - y3;
        return Math.Abs(e0 + e1) + Math.Abs(e3 + e2) + Math.Abs(e0 - e1) + Math.Abs(e3 - e2);
    }

    /// <summary>SATD over a block of 4×4 tiles.</summary>
    public static int Satd(ref byte a, int aStride, ref byte b, int bStride, int width, int height)
    {
        int sum = 0;
        for (int y = 0; y < height; y += 4)
        {
            for (int x = 0; x < width; x += 4)
            {
                sum += Satd4(ref Unsafe.Add(ref a, y * aStride + x), aStride, ref Unsafe.Add(ref b, y * bStride + x), bStride);
            }
        }

        return sum;
    }

    // ---------------------------------------------------------------- Vector128 twins

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Widen4(ref byte p) =>
        Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref p)).AsByte())).AsInt32();

    /// <summary><see cref="ResidualDctScalar"/> with the rows in lanes: transpose, row pass, transpose, column pass.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void ResidualDctVector(ref byte src, int srcStride, ref byte pred, int predStride, Span<int> c)
    {
        Vector128<int> r0 = Widen4(ref src) - Widen4(ref pred);
        Vector128<int> r1 = Widen4(ref Unsafe.Add(ref src, srcStride)) - Widen4(ref Unsafe.Add(ref pred, predStride));
        Vector128<int> r2 = Widen4(ref Unsafe.Add(ref src, 2 * srcStride)) - Widen4(ref Unsafe.Add(ref pred, 2 * predStride));
        Vector128<int> r3 = Widen4(ref Unsafe.Add(ref src, 3 * srcStride)) - Widen4(ref Unsafe.Add(ref pred, 3 * predStride));
        Vp8Decoder.Transpose4x4(r0, r1, r2, r3, out Vector128<int> x0, out Vector128<int> x1, out Vector128<int> x2, out Vector128<int> x3);
        Vector128<int> a = x0 + x3, b = x1 + x2, cc = x1 - x2, d = x0 - x3;
        Vector128<int> k5352 = Vector128.Create(5352), k2217 = Vector128.Create(2217);
        Vector128<int> t0 = (a + b) << 3;
        Vector128<int> t2 = (a - b) << 3;
        Vector128<int> t1 = (d * k5352 + cc * k2217 + Vector128.Create(256)) >> 9;
        Vector128<int> t3 = (d * k2217 - cc * k5352 + Vector128.Create(256)) >> 9;
        Vp8Decoder.Transpose4x4(t0, t1, t2, t3, out Vector128<int> u0, out Vector128<int> u1, out Vector128<int> u2, out Vector128<int> u3);
        a = u0 + u3;
        b = u1 + u2;
        cc = u1 - u2;
        d = u0 - u3;
        ref int o = ref MemoryMarshal.GetReference(c);
        ((a + b + Vector128.Create(8)) >> 4).StoreUnsafe(ref o);
        ((d * k5352 + cc * k2217 + Vector128.Create(32768)) >> 16).StoreUnsafe(ref o, 4);
        ((a - b + Vector128.Create(8)) >> 4).StoreUnsafe(ref o, 8);
        ((d * k2217 - cc * k5352 + Vector128.Create(32768)) >> 16).StoreUnsafe(ref o, 12);
    }

    /// <summary><see cref="QuantizeScalar"/>: quantised in raster order four at a time, then gathered to zigzag.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int QuantizeVector(ReadOnlySpan<int> coefficients, Span<short> levels, Span<short> dequant, int start,
        int dcStep, int dcReciprocal, int dcRounding, int acStep, int acReciprocal, int acRounding)
    {
        ref int c = ref MemoryMarshal.GetReference(coefficients);
        ref short dq = ref MemoryMarshal.GetReference(dequant);
        Span<short> raster = stackalloc short[16];
        ref short rl = ref MemoryMarshal.GetReference(raster);
        Vector128<int> acR = Vector128.Create(acReciprocal), acB = Vector128.Create(acRounding << 8), acS = Vector128.Create(acStep);
        Vector128<int> max = Vector128.Create(Vp8EncoderCosts.MaxLevel);
        short keepDc = dq;
        for (int k = 0; k < 4; k++)
        {
            Vector128<int> v = Vector128.LoadUnsafe(ref c, (nuint)(k * 4));
            Vector128<int> recip = acR, bias = acB, step = acS;
            if (k == 0)
            {
                recip = recip.WithElement(0, dcReciprocal);
                bias = bias.WithElement(0, dcRounding << 8);
                step = step.WithElement(0, dcStep);
            }

            Vector128<int> level = Vector128.Min((Vector128.Abs(v) * recip + bias) >> 16, max);
            Vector128<int> negative = Vector128.LessThan(v, Vector128<int>.Zero);
            level = (level ^ negative) - negative;
            Vector128<int> scaled = level * step;
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref rl, k * 4)),
                Vector128.Narrow(level, level).AsUInt64().ToScalar());
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref Unsafe.Add(ref dq, k * 4)),
                Vector128.Narrow(scaled, scaled).AsUInt64().ToScalar());
        }

        if (start > 0)
        {
            // The DC position (raster 0) is the caller's, and its level is not part of this block.
            dq = keepDc;
            rl = 0;
        }

        ref byte zigzag = ref MemoryMarshal.GetArrayDataReference(Vp8Tables.Zigzag);
        ref short l = ref MemoryMarshal.GetReference(levels);
        for (int i = start; i < 16; i++)
        {
            Unsafe.Add(ref l, i) = Unsafe.Add(ref rl, Unsafe.Add(ref zigzag, i));
        }

        // Zigzag index 0 is raster 0, so the levels' non-zero mask can be taken from the zigzag copy.
        uint mask = Vector128.ExtractMostSignificantBits(~Vector128.Equals(Vector128.LoadUnsafe(ref l), Vector128<short>.Zero)) |
                    Vector128.ExtractMostSignificantBits(~Vector128.Equals(Vector128.LoadUnsafe(ref l, 8), Vector128<short>.Zero)) << 8;
        mask &= ~0u << start;
        return mask == 0 ? -1 : 31 - System.Numerics.BitOperations.LeadingZeroCount(mask);
    }

    /// <summary><see cref="Satd4Scalar"/> with the rows in lanes.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int Satd4Vector(ref byte a, int aStride, ref byte b, int bStride)
    {
        Vector128<int> r0 = Widen4(ref a) - Widen4(ref b);
        Vector128<int> r1 = Widen4(ref Unsafe.Add(ref a, aStride)) - Widen4(ref Unsafe.Add(ref b, bStride));
        Vector128<int> r2 = Widen4(ref Unsafe.Add(ref a, 2 * aStride)) - Widen4(ref Unsafe.Add(ref b, 2 * bStride));
        Vector128<int> r3 = Widen4(ref Unsafe.Add(ref a, 3 * aStride)) - Widen4(ref Unsafe.Add(ref b, 3 * bStride));
        Vp8Decoder.Transpose4x4(r0, r1, r2, r3, out Vector128<int> x0, out Vector128<int> x1, out Vector128<int> x2, out Vector128<int> x3);
        Vector128<int> e0 = x0 + x3, e1 = x1 + x2, e2 = x1 - x2, e3 = x0 - x3;
        Vector128<int> d0 = e0 + e1, d1 = e3 + e2, d2 = e0 - e1, d3 = e3 - e2;
        Vp8Decoder.Transpose4x4(d0, d1, d2, d3, out Vector128<int> y0, out Vector128<int> y1, out Vector128<int> y2, out Vector128<int> y3);
        e0 = y0 + y3;
        e1 = y1 + y2;
        e2 = y1 - y2;
        e3 = y0 - y3;
        Vector128<int> sum = Vector128.Abs(e0 + e1) + Vector128.Abs(e3 + e2) + Vector128.Abs(e0 - e1) + Vector128.Abs(e3 - e2);
        return Vector128.Sum(sum) >> 1;
    }
}
