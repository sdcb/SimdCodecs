using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The encoder's sample arithmetic: RGB → YCbCr (T.871), 2×2 chroma averaging, and the
/// forward DCT + quantisation (AAN factorisation, Arai–Agui–Nakajima 1988, with the scale
/// factors folded into the quantiser). Every kernel has a Vector256, a Vector128 and a scalar
/// form that perform the same float operations in the same order — no FMA, no reassociation —
/// so the output does not depend on the instruction set (<c>JpegEncoderEquivalenceTests</c>).
/// </summary>
internal static class JpegEncoderKernels
{
    internal static bool UseV256 => Vector256.IsHardwareAccelerated && Avx2.IsSupported;
    internal static bool UseV128 => Vector128.IsHardwareAccelerated;

    // T.871 RGB → YCbCr. Y carries the -128 level shift of T.81 A.3.1; Cb / Cr are centred on 0.
    private const float YR = 0.299f, YG = 0.587f, YB = 0.114f;
    private const float CbR = -0.168736f, CbG = -0.331264f, CbB = 0.5f;
    private const float CrR = 0.5f, CrG = -0.418688f, CrB = -0.081312f;

    // ------------------------------------------------------------------ colour conversion

    /// <summary>
    /// Converts <paramref name="width"/> pixels of one interleaved row to level-shifted Y and
    /// centred Cb / Cr floats. Channels 1 and 2 (grey, grey + alpha) write only <paramref name="y"/>,
    /// as the exact sample − 128; channel 4's alpha is ignored.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void ConvertRow(ReadOnlySpan<byte> src, int channels, int width, Span<float> y, Span<float> cb, Span<float> cr)
    {
        var x = 0;
        if (UseV256) x = ConvertRowV256(src, channels, width, y, cb, cr);
        else if (UseV128) x = ConvertRowV128(src, channels, width, y, cb, cr);
        ConvertRowScalar(src, channels, x, width, y, cb, cr);
    }

    internal static void ConvertRowScalar(ReadOnlySpan<byte> src, int channels, int from, int width, Span<float> y, Span<float> cb, Span<float> cr)
    {
        if (channels <= 2)
        {
            for (var x = from; x < width; x++) y[x] = src[x * channels] - 128f;
            return;
        }

        for (var x = from; x < width; x++)
        {
            var p = x * channels;
            float r = src[p], g = src[p + 1], b = src[p + 2];
            y[x] = YR * r + YG * g + YB * b - 128f;
            cb[x] = CbR * r + CbG * g + CbB * b;
            cr[x] = CrR * r + CrG * g + CrB * b;
        }
    }

    /// <summary>Gathers 8 pixels' R, G, B (or grey) bytes into the low 8 lanes of three vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Gather8(ref byte p, int channels, out Vector128<byte> r, out Vector128<byte> g, out Vector128<byte> b)
    {
        // Every load stays inside the 8 pixels' own bytes: 24 for RGB (two 16-byte loads at 0 and 8),
        // 32 for RGBA, 16 for grey+alpha and 8 for grey.
        switch (channels)
        {
            case 3:
            {
                var v0 = Vector128.LoadUnsafe(ref p);
                var v1 = Vector128.LoadUnsafe(ref p, 8);
                r = Vector128.Shuffle(v0, Vector128.Create((byte)0, 3, 6, 9, 12, 15, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255))
                    | Vector128.Shuffle(v1, Vector128.Create((byte)255, 255, 255, 255, 255, 255, 10, 13, 255, 255, 255, 255, 255, 255, 255, 255));
                g = Vector128.Shuffle(v0, Vector128.Create((byte)1, 4, 7, 10, 13, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255))
                    | Vector128.Shuffle(v1, Vector128.Create((byte)255, 255, 255, 255, 255, 8, 11, 14, 255, 255, 255, 255, 255, 255, 255, 255));
                b = Vector128.Shuffle(v0, Vector128.Create((byte)2, 5, 8, 11, 14, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255))
                    | Vector128.Shuffle(v1, Vector128.Create((byte)255, 255, 255, 255, 255, 9, 12, 15, 255, 255, 255, 255, 255, 255, 255, 255));
                return;
            }
            case 4:
            {
                var v0 = Vector128.LoadUnsafe(ref p);
                var v1 = Vector128.LoadUnsafe(ref p, 16);
                r = Vector128.Shuffle(v0, Vector128.Create((byte)0, 4, 8, 12, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255))
                    | Vector128.Shuffle(v1, Vector128.Create((byte)255, 255, 255, 255, 0, 4, 8, 12, 255, 255, 255, 255, 255, 255, 255, 255));
                g = Vector128.Shuffle(v0, Vector128.Create((byte)1, 5, 9, 13, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255))
                    | Vector128.Shuffle(v1, Vector128.Create((byte)255, 255, 255, 255, 1, 5, 9, 13, 255, 255, 255, 255, 255, 255, 255, 255));
                b = Vector128.Shuffle(v0, Vector128.Create((byte)2, 6, 10, 14, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255))
                    | Vector128.Shuffle(v1, Vector128.Create((byte)255, 255, 255, 255, 2, 6, 10, 14, 255, 255, 255, 255, 255, 255, 255, 255));
                return;
            }
            case 2:
                r = Vector128.Shuffle(Vector128.LoadUnsafe(ref p), Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 255, 255, 255, 255, 255, 255, 255, 255));
                g = b = r;
                return;
            default:
                r = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref p)).AsByte();
                g = b = r;
                return;
        }
    }

    internal static int ConvertRowV256(ReadOnlySpan<byte> src, int channels, int width, Span<float> y, Span<float> cb, Span<float> cr)
    {
        ref var s = ref MemoryMarshal.GetReference(src);
        ref var yo = ref MemoryMarshal.GetReference(y);
        ref var cbo = ref MemoryMarshal.GetReference(cb);
        ref var cro = ref MemoryMarshal.GetReference(cr);
        var shift = Vector256.Create(128f);
        var x = 0;
        // src holds width * channels bytes and y/cb/cr at least width floats; x + 8 <= width keeps
        // every load and store in range.
        for (; x + 8 <= width; x += 8)
        {
            Gather8(ref Unsafe.Add(ref s, x * channels), channels, out var rb, out var gb, out var bb);
            var r = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(rb));
            if (channels <= 2)
            {
                (r - shift).StoreUnsafe(ref yo, (nuint)x);
                continue;
            }

            var g = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(gb));
            var b = Avx.ConvertToVector256Single(Avx2.ConvertToVector256Int32(bb));
            (Vector256.Create(YR) * r + Vector256.Create(YG) * g + Vector256.Create(YB) * b - shift).StoreUnsafe(ref yo, (nuint)x);
            (Vector256.Create(CbR) * r + Vector256.Create(CbG) * g + Vector256.Create(CbB) * b).StoreUnsafe(ref cbo, (nuint)x);
            (Vector256.Create(CrR) * r + Vector256.Create(CrG) * g + Vector256.Create(CrB) * b).StoreUnsafe(ref cro, (nuint)x);
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Widen8(Vector128<byte> v, out Vector128<float> lo, out Vector128<float> hi)
    {
        var w = Vector128.WidenLower(v);
        lo = Vector128.ConvertToSingle(Vector128.WidenLower(w).AsInt32());
        hi = Vector128.ConvertToSingle(Vector128.WidenUpper(w).AsInt32());
    }

    internal static int ConvertRowV128(ReadOnlySpan<byte> src, int channels, int width, Span<float> y, Span<float> cb, Span<float> cr)
    {
        ref var s = ref MemoryMarshal.GetReference(src);
        ref var yo = ref MemoryMarshal.GetReference(y);
        ref var cbo = ref MemoryMarshal.GetReference(cb);
        ref var cro = ref MemoryMarshal.GetReference(cr);
        var shift = Vector128.Create(128f);
        var x = 0;
        // Same bounds as the Vector256 form.
        for (; x + 8 <= width; x += 8)
        {
            Gather8(ref Unsafe.Add(ref s, x * channels), channels, out var rb, out var gb, out var bb);
            Widen8(rb, out var r0, out var r1);
            if (channels <= 2)
            {
                (r0 - shift).StoreUnsafe(ref yo, (nuint)x);
                (r1 - shift).StoreUnsafe(ref yo, (nuint)x + 4);
                continue;
            }

            Widen8(gb, out var g0, out var g1);
            Widen8(bb, out var b0, out var b1);
            (Vector128.Create(YR) * r0 + Vector128.Create(YG) * g0 + Vector128.Create(YB) * b0 - shift).StoreUnsafe(ref yo, (nuint)x);
            (Vector128.Create(YR) * r1 + Vector128.Create(YG) * g1 + Vector128.Create(YB) * b1 - shift).StoreUnsafe(ref yo, (nuint)x + 4);
            (Vector128.Create(CbR) * r0 + Vector128.Create(CbG) * g0 + Vector128.Create(CbB) * b0).StoreUnsafe(ref cbo, (nuint)x);
            (Vector128.Create(CbR) * r1 + Vector128.Create(CbG) * g1 + Vector128.Create(CbB) * b1).StoreUnsafe(ref cbo, (nuint)x + 4);
            (Vector128.Create(CrR) * r0 + Vector128.Create(CrG) * g0 + Vector128.Create(CrB) * b0).StoreUnsafe(ref cro, (nuint)x);
            (Vector128.Create(CrR) * r1 + Vector128.Create(CrG) * g1 + Vector128.Create(CrB) * b1).StoreUnsafe(ref cro, (nuint)x + 4);
        }

        return x;
    }

    // ------------------------------------------------------------------ 2×2 chroma averaging

    /// <summary><c>dst[x] = ((r0[2x] + r1[2x]) + (r0[2x+1] + r1[2x+1])) * 0.25</c> for x &lt; <paramref name="count"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void Downsample2x2(ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, Span<float> dst, int count)
    {
        var x = 0;
        if (UseV256) x = Downsample2x2V256(r0, r1, dst, count);
        else if (Sse3.IsSupported || AdvSimd.Arm64.IsSupported) x = Downsample2x2V128(r0, r1, dst, count);
        Downsample2x2Scalar(r0, r1, dst, x, count);
    }

    internal static void Downsample2x2Scalar(ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, Span<float> dst, int from, int count)
    {
        for (var x = from; x < count; x++)
            dst[x] = ((r0[2 * x] + r1[2 * x]) + (r0[2 * x + 1] + r1[2 * x + 1])) * 0.25f;
    }

    internal static int Downsample2x2V256(ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, Span<float> dst, int count)
    {
        // Bounds: r0 / r1 hold 2 * count floats and dst count; x + 8 <= count keeps every access in range.
        ref var a = ref MemoryMarshal.GetReference(r0);
        ref var b = ref MemoryMarshal.GetReference(r1);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var quarter = Vector256.Create(0.25f);
        var x = 0;
        for (; x + 8 <= count; x += 8)
        {
            var s0 = Vector256.LoadUnsafe(ref a, (nuint)(2 * x)) + Vector256.LoadUnsafe(ref b, (nuint)(2 * x));
            var s1 = Vector256.LoadUnsafe(ref a, (nuint)(2 * x + 8)) + Vector256.LoadUnsafe(ref b, (nuint)(2 * x + 8));
            var h = Avx2.Permute4x64(Avx.HorizontalAdd(s0, s1).AsDouble(), 0b11_01_10_00).AsSingle();
            (h * quarter).StoreUnsafe(ref d, (nuint)x);
        }

        return x;
    }

    internal static int Downsample2x2V128(ReadOnlySpan<float> r0, ReadOnlySpan<float> r1, Span<float> dst, int count)
    {
        ref var a = ref MemoryMarshal.GetReference(r0);
        ref var b = ref MemoryMarshal.GetReference(r1);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var quarter = Vector128.Create(0.25f);
        var x = 0;
        for (; x + 4 <= count; x += 4)
        {
            var s0 = Vector128.LoadUnsafe(ref a, (nuint)(2 * x)) + Vector128.LoadUnsafe(ref b, (nuint)(2 * x));
            var s1 = Vector128.LoadUnsafe(ref a, (nuint)(2 * x + 4)) + Vector128.LoadUnsafe(ref b, (nuint)(2 * x + 4));
            var h = Sse3.IsSupported ? Sse3.HorizontalAdd(s0, s1) : AdvSimd.Arm64.AddPairwise(s0, s1);
            (h * quarter).StoreUnsafe(ref d, (nuint)x);
        }

        return x;
    }

    // ------------------------------------------------------------------ forward DCT + quantisation

    /// <summary>
    /// Position in the kernel's coefficient layout (<c>t[u * 8 + v]</c>, u horizontal and v
    /// vertical frequency) of the i-th zig-zag coefficient.
    /// </summary>
    private static ReadOnlySpan<byte> ZigZagToKernel =>
    [
        0, 8, 1, 2, 9, 16, 24, 17, 10, 3, 4, 11, 18, 25, 32, 40, 33, 26, 19, 12, 5, 6, 13, 20, 27, 34, 41, 48, 56, 49, 42, 35,
        28, 21, 14, 7, 15, 22, 29, 36, 43, 50, 57, 58, 51, 44, 37, 30, 23, 31, 38, 45, 52, 59, 60, 53, 46, 39, 47, 54, 61, 62, 55, 63,
    ];

    /// <summary>
    /// Forward DCT of the 8×8 block at <paramref name="src"/> (row stride <paramref name="stride"/>
    /// floats), multiplied by <paramref name="recip"/> (reciprocal quantiser × AAN scale, in the
    /// kernel layout <c>[u * 8 + v]</c>), rounded half to even, written to <paramref name="zz"/>
    /// in zig-zag order.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void ForwardDct(ReadOnlySpan<float> src, int stride, ReadOnlySpan<float> recip, Span<short> zz)
    {
        if (UseV256) ForwardDctV256(src, stride, recip, zz);
        else if (UseV128) ForwardDctV128(src, stride, recip, zz);
        else ForwardDctScalar(src, stride, recip, zz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Gather(ReadOnlySpan<int> t, Span<short> zz)
    {
        ref var tz = ref MemoryMarshal.GetReference(ZigZagToKernel);
        ref var tr = ref MemoryMarshal.GetReference(t);
        ref var zr = ref MemoryMarshal.GetReference(zz);
        // t has 64 entries, zz at least 64 (checked by the callers' slices), and ZigZagToKernel is a permutation of 0..63.
        _ = zz[63];
        for (var i = 0; i < 64; i++)
            Unsafe.Add(ref zr, i) = (short)Unsafe.Add(ref tr, Unsafe.Add(ref tz, i));
    }

    internal static void ForwardDctScalar(ReadOnlySpan<float> src, int stride, ReadOnlySpan<float> recip, Span<short> zz)
    {
        Span<float> c = stackalloc float[64];
        // Column pass: c[v * 8 + x].
        for (var x = 0; x < 8; x++)
        {
            Aan(src[x], src[stride + x], src[2 * stride + x], src[3 * stride + x],
                src[4 * stride + x], src[5 * stride + x], src[6 * stride + x], src[7 * stride + x],
                out c[x], out c[8 + x], out c[16 + x], out c[24 + x], out c[32 + x], out c[40 + x], out c[48 + x], out c[56 + x]);
        }

        // Row pass into the kernel layout t[u * 8 + v].
        Span<float> f = stackalloc float[64];
        for (var v = 0; v < 8; v++)
        {
            var o = v * 8;
            Aan(c[o], c[o + 1], c[o + 2], c[o + 3], c[o + 4], c[o + 5], c[o + 6], c[o + 7],
                out f[v], out f[8 + v], out f[16 + v], out f[24 + v], out f[32 + v], out f[40 + v], out f[48 + v], out f[56 + v]);
        }

        Span<int> t = stackalloc int[64];
        for (var i = 0; i < 64; i++) t[i] = (int)MathF.Round(f[i] * recip[i]);
        Gather(t, zz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Aan(float d0, float d1, float d2, float d3, float d4, float d5, float d6, float d7,
        out float o0, out float o1, out float o2, out float o3, out float o4, out float o5, out float o6, out float o7)
    {
        var tmp0 = d0 + d7;
        var tmp7 = d0 - d7;
        var tmp1 = d1 + d6;
        var tmp6 = d1 - d6;
        var tmp2 = d2 + d5;
        var tmp5 = d2 - d5;
        var tmp3 = d3 + d4;
        var tmp4 = d3 - d4;

        var tmp10 = tmp0 + tmp3;
        var tmp13 = tmp0 - tmp3;
        var tmp11 = tmp1 + tmp2;
        var tmp12 = tmp1 - tmp2;
        o0 = tmp10 + tmp11;
        o4 = tmp10 - tmp11;
        var z1 = (tmp12 + tmp13) * 0.707106781f;
        o2 = tmp13 + z1;
        o6 = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;
        var z5 = (tmp10 - tmp12) * 0.382683433f;
        var z2 = tmp10 * 0.541196100f + z5;
        var z4 = tmp12 * 1.306562965f + z5;
        var z3 = tmp11 * 0.707106781f;
        var z11 = tmp7 + z3;
        var z13 = tmp7 - z3;
        o5 = z13 + z2;
        o3 = z13 - z2;
        o1 = z11 + z4;
        o7 = z11 - z4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Aan(ref Vector256<float> d0, ref Vector256<float> d1, ref Vector256<float> d2, ref Vector256<float> d3,
        ref Vector256<float> d4, ref Vector256<float> d5, ref Vector256<float> d6, ref Vector256<float> d7)
    {
        var tmp0 = d0 + d7;
        var tmp7 = d0 - d7;
        var tmp1 = d1 + d6;
        var tmp6 = d1 - d6;
        var tmp2 = d2 + d5;
        var tmp5 = d2 - d5;
        var tmp3 = d3 + d4;
        var tmp4 = d3 - d4;

        var tmp10 = tmp0 + tmp3;
        var tmp13 = tmp0 - tmp3;
        var tmp11 = tmp1 + tmp2;
        var tmp12 = tmp1 - tmp2;
        d0 = tmp10 + tmp11;
        d4 = tmp10 - tmp11;
        var z1 = (tmp12 + tmp13) * Vector256.Create(0.707106781f);
        d2 = tmp13 + z1;
        d6 = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;
        var z5 = (tmp10 - tmp12) * Vector256.Create(0.382683433f);
        var z2 = tmp10 * Vector256.Create(0.541196100f) + z5;
        var z4 = tmp12 * Vector256.Create(1.306562965f) + z5;
        var z3 = tmp11 * Vector256.Create(0.707106781f);
        var z11 = tmp7 + z3;
        var z13 = tmp7 - z3;
        d5 = z13 + z2;
        d3 = z13 - z2;
        d1 = z11 + z4;
        d7 = z11 - z4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Aan(ref Vector128<float> d0, ref Vector128<float> d1, ref Vector128<float> d2, ref Vector128<float> d3,
        ref Vector128<float> d4, ref Vector128<float> d5, ref Vector128<float> d6, ref Vector128<float> d7)
    {
        var tmp0 = d0 + d7;
        var tmp7 = d0 - d7;
        var tmp1 = d1 + d6;
        var tmp6 = d1 - d6;
        var tmp2 = d2 + d5;
        var tmp5 = d2 - d5;
        var tmp3 = d3 + d4;
        var tmp4 = d3 - d4;

        var tmp10 = tmp0 + tmp3;
        var tmp13 = tmp0 - tmp3;
        var tmp11 = tmp1 + tmp2;
        var tmp12 = tmp1 - tmp2;
        d0 = tmp10 + tmp11;
        d4 = tmp10 - tmp11;
        var z1 = (tmp12 + tmp13) * Vector128.Create(0.707106781f);
        d2 = tmp13 + z1;
        d6 = tmp13 - z1;

        tmp10 = tmp4 + tmp5;
        tmp11 = tmp5 + tmp6;
        tmp12 = tmp6 + tmp7;
        var z5 = (tmp10 - tmp12) * Vector128.Create(0.382683433f);
        var z2 = tmp10 * Vector128.Create(0.541196100f) + z5;
        var z4 = tmp12 * Vector128.Create(1.306562965f) + z5;
        var z3 = tmp11 * Vector128.Create(0.707106781f);
        var z11 = tmp7 + z3;
        var z13 = tmp7 - z3;
        d5 = z13 + z2;
        d3 = z13 - z2;
        d1 = z11 + z4;
        d7 = z11 - z4;
    }

    internal static void ForwardDctV256(ReadOnlySpan<float> src, int stride, ReadOnlySpan<float> recip, Span<short> zz)
    {
        // src must hold 7 * stride + 8 floats and recip 64: the slices below are bounds-checked.
        var r0 = Vector256.Create(src.Slice(0, 8));
        var r1 = Vector256.Create(src.Slice(stride, 8));
        var r2 = Vector256.Create(src.Slice(2 * stride, 8));
        var r3 = Vector256.Create(src.Slice(3 * stride, 8));
        var r4 = Vector256.Create(src.Slice(4 * stride, 8));
        var r5 = Vector256.Create(src.Slice(5 * stride, 8));
        var r6 = Vector256.Create(src.Slice(6 * stride, 8));
        var r7 = Vector256.Create(src.Slice(7 * stride, 8));

        Aan(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Aan(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        // Quantise, pack to int16 rows (packs interleaves 128-bit lanes; vpermq restores row order),
        // then permute into zig-zag order with byte shuffles.
        var p01 = Pack(Quantize(r0, recip, 0), Quantize(r1, recip, 8)).AsByte();
        var p23 = Pack(Quantize(r2, recip, 16), Quantize(r3, recip, 24)).AsByte();
        var p45 = Pack(Quantize(r4, recip, 32), Quantize(r5, recip, 40)).AsByte();
        var p67 = Pack(Quantize(r6, recip, 48), Quantize(r7, recip, 56)).AsByte();
        ZigZagShuffle(p01.GetLower(), p01.GetUpper(), p23.GetLower(), p23.GetUpper(),
            p45.GetLower(), p45.GetUpper(), p67.GetLower(), p67.GetUpper(), zz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Quantize(Vector256<float> v, ReadOnlySpan<float> recip, int o) =>
        Vector256.ConvertToInt32Native(Vector256.Round(v * Vector256.Create(recip.Slice(o, 8))));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Pack(Vector256<int> a, Vector256<int> b) =>
        Avx2.Permute4x64(Avx2.PackSignedSaturate(a, b).AsInt64(), 0b11_01_10_00).AsInt16();

    // Byte-shuffle masks for the zig-zag permutation of a kernel-layout int16 block: output group j
    // (zig-zag 8j..8j+7) is the OR over source rows r of shuffle(row r, mask[j * 8 + r]). A row
    // that feeds no lane of a group has an all-0x80 (zeroing) mask and is left out of ZigZagShuffle.
    private static readonly byte[] ZigZagMasks = BuildZigZagMasks();

    private static byte[] BuildZigZagMasks()
    {
        var masks = new byte[8 * 8 * 16];
        masks.AsSpan().Fill(0x80);
        for (var j = 0; j < 8; j++)
        {
            for (var l = 0; l < 8; l++)
            {
                var k = ZigZagToKernel[j * 8 + l];
                int row = k >> 3, col = k & 7;
                masks[(j * 8 + row) * 16 + 2 * l] = (byte)(2 * col);
                masks[(j * 8 + row) * 16 + 2 * l + 1] = (byte)(2 * col + 1);
            }
        }

        return masks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> S(Vector128<byte> row, ref byte masks, int j, int r) =>
        Ssse3.Shuffle(row, Vector128.LoadUnsafe(ref masks, (nuint)((j * 8 + r) * 16)));

    /// <summary>Zig-zag permutation of eight kernel-layout int16 rows (each group ORs exactly the rows its lanes come from).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ZigZagShuffle(Vector128<byte> a0, Vector128<byte> a1, Vector128<byte> a2, Vector128<byte> a3,
        Vector128<byte> a4, Vector128<byte> a5, Vector128<byte> a6, Vector128<byte> a7, Span<short> zz)
    {
        // zz holds 64 int16 (checked); mask offsets stay inside the 1024-byte table.
        _ = zz[63];
        ref var d = ref Unsafe.As<short, byte>(ref MemoryMarshal.GetReference(zz));
        ref var m = ref MemoryMarshal.GetArrayDataReference(ZigZagMasks);
        (S(a0, ref m, 0, 0) | S(a1, ref m, 0, 1) | S(a2, ref m, 0, 2) | S(a3, ref m, 0, 3)).StoreUnsafe(ref d, 0);
        (S(a0, ref m, 1, 0) | S(a1, ref m, 1, 1) | S(a2, ref m, 1, 2) | S(a3, ref m, 1, 3) | S(a4, ref m, 1, 4) | S(a5, ref m, 1, 5)).StoreUnsafe(ref d, 16);
        (S(a0, ref m, 2, 0) | S(a1, ref m, 2, 1) | S(a2, ref m, 2, 2) | S(a3, ref m, 2, 3) | S(a4, ref m, 2, 4)).StoreUnsafe(ref d, 32);
        (S(a3, ref m, 3, 3) | S(a4, ref m, 3, 4) | S(a5, ref m, 3, 5) | S(a6, ref m, 3, 6) | S(a7, ref m, 3, 7)).StoreUnsafe(ref d, 48);
        (S(a0, ref m, 4, 0) | S(a1, ref m, 4, 1) | S(a2, ref m, 4, 2) | S(a3, ref m, 4, 3) | S(a4, ref m, 4, 4)).StoreUnsafe(ref d, 64);
        (S(a3, ref m, 5, 3) | S(a4, ref m, 5, 4) | S(a5, ref m, 5, 5) | S(a6, ref m, 5, 6) | S(a7, ref m, 5, 7)).StoreUnsafe(ref d, 80);
        (S(a2, ref m, 6, 2) | S(a3, ref m, 6, 3) | S(a4, ref m, 6, 4) | S(a5, ref m, 6, 5) | S(a6, ref m, 6, 6) | S(a7, ref m, 6, 7)).StoreUnsafe(ref d, 96);
        (S(a4, ref m, 7, 4) | S(a5, ref m, 7, 5) | S(a6, ref m, 7, 6) | S(a7, ref m, 7, 7)).StoreUnsafe(ref d, 112);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        var t0 = Avx.UnpackLow(r0, r1);
        var t1 = Avx.UnpackHigh(r0, r1);
        var t2 = Avx.UnpackLow(r2, r3);
        var t3 = Avx.UnpackHigh(r2, r3);
        var t4 = Avx.UnpackLow(r4, r5);
        var t5 = Avx.UnpackHigh(r4, r5);
        var t6 = Avx.UnpackLow(r6, r7);
        var t7 = Avx.UnpackHigh(r6, r7);
        var u0 = Avx.Shuffle(t0, t2, 0x44);
        var u1 = Avx.Shuffle(t0, t2, 0xEE);
        var u2 = Avx.Shuffle(t1, t3, 0x44);
        var u3 = Avx.Shuffle(t1, t3, 0xEE);
        var u4 = Avx.Shuffle(t4, t6, 0x44);
        var u5 = Avx.Shuffle(t4, t6, 0xEE);
        var u6 = Avx.Shuffle(t5, t7, 0x44);
        var u7 = Avx.Shuffle(t5, t7, 0xEE);
        r0 = Avx.Permute2x128(u0, u4, 0x20);
        r1 = Avx.Permute2x128(u1, u5, 0x20);
        r2 = Avx.Permute2x128(u2, u6, 0x20);
        r3 = Avx.Permute2x128(u3, u7, 0x20);
        r4 = Avx.Permute2x128(u0, u4, 0x31);
        r5 = Avx.Permute2x128(u1, u5, 0x31);
        r6 = Avx.Permute2x128(u2, u6, 0x31);
        r7 = Avx.Permute2x128(u3, u7, 0x31);
    }

    internal static void ForwardDctV128(ReadOnlySpan<float> src, int stride, ReadOnlySpan<float> recip, Span<short> zz)
    {
        // Each pass runs on the left and right four lanes; the transpose goes through scratch memory.
        Span<float> c = stackalloc float[64];
        for (var half = 0; half < 8; half += 4)
        {
            var r0 = Vector128.Create(src.Slice(half, 4));
            var r1 = Vector128.Create(src.Slice(stride + half, 4));
            var r2 = Vector128.Create(src.Slice(2 * stride + half, 4));
            var r3 = Vector128.Create(src.Slice(3 * stride + half, 4));
            var r4 = Vector128.Create(src.Slice(4 * stride + half, 4));
            var r5 = Vector128.Create(src.Slice(5 * stride + half, 4));
            var r6 = Vector128.Create(src.Slice(6 * stride + half, 4));
            var r7 = Vector128.Create(src.Slice(7 * stride + half, 4));
            Aan(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            // c[x * 8 + v]: transposed on the way out.
            StoreColumn(r0, c, half, 0);
            StoreColumn(r1, c, half, 1);
            StoreColumn(r2, c, half, 2);
            StoreColumn(r3, c, half, 3);
            StoreColumn(r4, c, half, 4);
            StoreColumn(r5, c, half, 5);
            StoreColumn(r6, c, half, 6);
            StoreColumn(r7, c, half, 7);
        }

        Span<int> t = stackalloc int[64];
        for (var half = 0; half < 8; half += 4)
        {
            var r0 = Vector128.Create(c.Slice(half, 4));
            var r1 = Vector128.Create(c.Slice(8 + half, 4));
            var r2 = Vector128.Create(c.Slice(16 + half, 4));
            var r3 = Vector128.Create(c.Slice(24 + half, 4));
            var r4 = Vector128.Create(c.Slice(32 + half, 4));
            var r5 = Vector128.Create(c.Slice(40 + half, 4));
            var r6 = Vector128.Create(c.Slice(48 + half, 4));
            var r7 = Vector128.Create(c.Slice(56 + half, 4));
            Aan(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Quantize(r0, recip, t, half);
            Quantize(r1, recip, t, 8 + half);
            Quantize(r2, recip, t, 16 + half);
            Quantize(r3, recip, t, 24 + half);
            Quantize(r4, recip, t, 32 + half);
            Quantize(r5, recip, t, 40 + half);
            Quantize(r6, recip, t, 48 + half);
            Quantize(r7, recip, t, 56 + half);
        }

        Gather(t, zz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreColumn(Vector128<float> v, Span<float> c, int x0, int row)
    {
        c[x0 * 8 + row] = v.GetElement(0);
        c[(x0 + 1) * 8 + row] = v.GetElement(1);
        c[(x0 + 2) * 8 + row] = v.GetElement(2);
        c[(x0 + 3) * 8 + row] = v.GetElement(3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Quantize(Vector128<float> v, ReadOnlySpan<float> recip, Span<int> t, int o) =>
        Vector128.ConvertToInt32Native(Vector128.Round(v * Vector128.Create(recip.Slice(o, 4)))).CopyTo(t.Slice(o, 4));

    // ------------------------------------------------------------------ non-zero mask

    /// <summary>Bit i set when <c>|zz[i]| &gt;&gt; shift</c> is non-zero (the progressive point transform of T.81 G.1.2).</summary>
    internal static ulong SignificantMask(ReadOnlySpan<short> zz, int shift)
    {
        // zz holds at least 64 coefficients (the callers slice whole blocks).
        _ = zz[63];
        ref var z = ref MemoryMarshal.GetReference(zz);
        if (UseV256)
        {
            var e0 = Vector256.Equals(Vector256.ShiftRightLogical(Vector256.Abs(Vector256.LoadUnsafe(ref z)).AsUInt16(), shift), Vector256<ushort>.Zero).AsInt16();
            var e1 = Vector256.Equals(Vector256.ShiftRightLogical(Vector256.Abs(Vector256.LoadUnsafe(ref z, 16)).AsUInt16(), shift), Vector256<ushort>.Zero).AsInt16();
            var e2 = Vector256.Equals(Vector256.ShiftRightLogical(Vector256.Abs(Vector256.LoadUnsafe(ref z, 32)).AsUInt16(), shift), Vector256<ushort>.Zero).AsInt16();
            var e3 = Vector256.Equals(Vector256.ShiftRightLogical(Vector256.Abs(Vector256.LoadUnsafe(ref z, 48)).AsUInt16(), shift), Vector256<ushort>.Zero).AsInt16();
            var lo = Avx2.Permute4x64(Avx2.PackSignedSaturate(e0, e1).AsInt64(), 0b11_01_10_00).AsByte();
            var hi = Avx2.Permute4x64(Avx2.PackSignedSaturate(e2, e3).AsInt64(), 0b11_01_10_00).AsByte();
            return ~((ulong)(uint)Avx2.MoveMask(lo) | ((ulong)(uint)Avx2.MoveMask(hi) << 32));
        }

        ulong mask = 0;
        for (var i = 0; i < 64; i++)
            mask |= (ulong)((Math.Abs((int)zz[i]) >> shift) != 0 ? 1 : 0) << i;
        return mask;
    }

    /// <summary>Bit i set when zig-zag coefficient i is non-zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong NonZeroMask(ReadOnlySpan<short> zz)
    {
        // zz holds at least 64 coefficients (the callers slice whole blocks).
        _ = zz[63];
        ref var z = ref MemoryMarshal.GetReference(zz);
        if (UseV256)
        {
            var e0 = Vector256.Equals(Vector256.LoadUnsafe(ref z), Vector256<short>.Zero);
            var e1 = Vector256.Equals(Vector256.LoadUnsafe(ref z, 16), Vector256<short>.Zero);
            var e2 = Vector256.Equals(Vector256.LoadUnsafe(ref z, 32), Vector256<short>.Zero);
            var e3 = Vector256.Equals(Vector256.LoadUnsafe(ref z, 48), Vector256<short>.Zero);
            var lo = Avx2.Permute4x64(Avx2.PackSignedSaturate(e0, e1).AsInt64(), 0b11_01_10_00).AsByte();
            var hi = Avx2.Permute4x64(Avx2.PackSignedSaturate(e2, e3).AsInt64(), 0b11_01_10_00).AsByte();
            return ~((ulong)(uint)Avx2.MoveMask(lo) | ((ulong)(uint)Avx2.MoveMask(hi) << 32));
        }

        if (UseV128)
        {
            ulong zero = 0;
            for (var i = 0; i < 4; i++)
            {
                var a = Vector128.Equals(Vector128.LoadUnsafe(ref z, (nuint)(i * 16)), Vector128<short>.Zero);
                var b = Vector128.Equals(Vector128.LoadUnsafe(ref z, (nuint)(i * 16 + 8)), Vector128<short>.Zero);
                zero |= (ulong)Vector128.ExtractMostSignificantBits(Vector128.Narrow(a, b)) << (i * 16);
            }

            return ~zero;
        }

        ulong mask = 0;
        for (var i = 0; i < 64; i++)
            mask |= (ulong)(zz[i] != 0 ? 1 : 0) << i;
        return mask;
    }
}
