using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Inverse DCT kernels.
///
/// <para>
/// <see cref="Idct8x8(byte[], int, int, ReadOnlySpan{short})"/> is the full-scale transform. Its arithmetic is defined here,
/// clean-room, from the T.81 A.3.3 definition and nothing else:
/// <code>
/// s(x) = ½ · Σu C(u) · S(u) · cos((2x+1)uπ/16),   C(0) = 1/√2, C(u) = 1 otherwise
/// </code>
/// applied separably — first down every column, then along every row — in IEEE single
/// precision. Each 1-D pass is the even/odd split that follows from the cosine
/// symmetries cos((2(7−x)+1)uπ/16) = (−1)^u · cos((2x+1)uπ/16) and the same identity
/// one level down for the even half:
/// <code>
/// a0 = (S0 + S4)·K4          a1 = (S0 − S4)·K4
/// b0 = S2·C2 + S6·C6         b1 = S2·C6 − S6·C2
/// e0 = a0 + b0   e1 = a1 + b1   e2 = a1 − b1   e3 = a0 − b0
/// o0 = S1·C1 + S3·C3 + S5·C5 + S7·C7
/// o1 = S1·C3 − S3·C7 − S5·C1 − S7·C5
/// o2 = S1·C5 − S3·C1 + S5·C7 + S7·C3
/// o3 = S1·C7 − S3·C5 + S5·C3 − S7·C1
/// s(x) = e(x) + o(x),  s(7−x) = e(x) − o(x)      (x = 0..3)
/// </code>
/// with Ck = cos(kπ/16)/2, K4 = 1/(2√2). Each sum is evaluated left to right, products
/// and sums are rounded separately (no fused multiply-add), and the output sample is
/// <c>trunc(min(max(s + 128.5, 0), 255))</c> — round-half-up of the level-shifted value,
/// clamped. That operation sequence is the contract: the scalar, <see cref="Vector128"/>
/// and <see cref="Vector256"/> kernels all execute exactly it, so they agree bit for bit
/// (<c>JpegIdctEquivalenceTests</c>), and any faster kernel must too. On real images and
/// on the IEEE 1180 random-block test the result is within rounding noise of a
/// double-precision IDCT.
/// </para>
///
/// <para>
/// <see cref="IdctReduced"/> is the scaled-decode kernel: a clean-room
/// DCT-domain decimation (NOT ported from libjpeg's jidctred.c, which carries
/// the IJG license). Taking the top-left B×B coefficients of an 8×8 block and
/// applying a B-point inverse transform with the same c_u/2 normalisation
/// yields a faithful B×B downsample of the block — for B=1 this degenerates to
/// the well-known "DC/8" property. High-frequency coefficients above B are
/// discarded, which acts as the anti-alias prefilter.
/// </para>
/// </summary>
internal static class JpegIdct
{
    // Ck = cos(kπ/16) / 2 and K4 = 1/(2√2), rounded once from double to float at compile time.
    private const float K4 = (float)0.35355339059327373;
    private const float C1 = (float)0.4903926402016152;
    private const float C2 = (float)0.46193976625564337;
    private const float C3 = (float)0.4157348061512726;
    private const float C5 = (float)0.27778511650980114;
    private const float C6 = (float)0.19134171618254492;
    private const float C7 = (float)0.09754516100806417;

    private const float Bias = 128.5f;

    /// <summary>The Vector256 kernels use AVX2 packs and conversions directly.</summary>
    private static bool UseV256 => Vector256.IsHardwareAccelerated && Avx2.IsSupported;
    private const float Max = 255f;

    private static byte Clamp(int x)
    {
        if ((uint)x > 255)
        {
            if (x < 0)
                return 0;
            return 255;
        }

        return (byte)x;
    }

    // ------------------------------------------------------------------ sparse fast paths
    //
    // The decoder knows the zig-zag index of the last coefficient it wrote. Every
    // coefficient after it is zero, and a zero input contributes exact +0 terms to
    // the defined sequence (x + 0 = x, x − 0 = x, 0·C = +0; integer inputs never
    // produce −0), so dropping those terms is bit-exact:
    //  * last = 0 (DC only): every sample is (DC·K4)·K4 — both passes collapse.
    //  * rows 4–7 all zero: the column pass runs the f0…f3 half of the 1-D transform.
    //  * columns 4–7 all zero: so does the row pass (columns 4–7 of the first
    //    pass's output are exactly +0).

    /// <summary>Highest coefficient row / column among zig-zag positions 0…k (k may overshoot to 79).</summary>
    private static readonly byte[] MaxRow = new byte[80], MaxCol = new byte[80];

    static JpegIdct()
    {
        int maxRow = 0, maxCol = 0, k = 0;
        for (var s = 0; s < 15; s++)
        {
            for (var t = 0; t <= s; t++)
            {
                var row = s % 2 == 0 ? s - t : t;
                var col = s - row;
                if (row > 7 || col > 7)
                    continue;
                maxRow = Math.Max(maxRow, row);
                maxCol = Math.Max(maxCol, col);
                MaxRow[k] = (byte)maxRow;
                MaxCol[k] = (byte)maxCol;
                k++;
            }
        }

        for (; k < 80; k++)
        {
            MaxRow[k] = 7;
            MaxCol[k] = 7;
        }
    }

    /// <summary>
    /// 8×8 inverse DCT of a block whose coefficients after zig-zag index
    /// <paramref name="last"/> are all zero; bit-identical to <see cref="Idct8x8(byte[], int, int, ReadOnlySpan{short})"/>.
    /// </summary>
    public static void Idct8x8(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data, int last)
    {
        if ((uint)last >= (uint)MaxRow.Length)
            last = MaxRow.Length - 1;
        if (last == 0)
            DcOnly(outPlane, outOffset, outStride, data[0]);
        else if (UseV256)
            Idct8x8V256(outPlane, outOffset, outStride, data, MaxRow[last] < 4, MaxCol[last] < 4);
        else
            Idct8x8(outPlane, outOffset, outStride, data);
    }

    /// <summary>Every pre-rounding sample of a DC-only block: the column pass gives DC·K4, the row pass ·K4 again.</summary>
    internal static float DcValue(short dc) => (float)dc * K4 * K4;

    /// <summary>Test hook: which halves <see cref="Idct8x8(byte[], int, int, ReadOnlySpan{short}, int)"/> drops for <paramref name="last"/>.</summary>
    internal static (bool HalfRows, bool HalfCols) SparseFlags(int last) => (MaxRow[last] < 4, MaxCol[last] < 4);

    /// <summary>A DC-only block: one sample value, (DC·K4)·K4 rounded like any other.</summary>
    internal static void DcOnly(byte[] outPlane, int outOffset, int outStride, short dc)
    {
        _ = outPlane[outOffset + 7 * outStride + 7];
        _ = outPlane[outOffset];
        var row = 0x0101010101010101UL * ToSample(DcValue(dc));
        ref var dst = ref MemoryMarshal.GetArrayDataReference(outPlane);
        for (var y = 0; y < 8; y++)
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + y * outStride), row);
    }

    /// <summary>
    /// Full 8×8 inverse DCT of one dequantized coefficient block (natural order) into an
    /// 8-bit sample plane (adds the +128 level shift, rounds, clamps to 0..255).
    /// </summary>
    public static void Idct8x8(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data)
    {
        if (UseV256)
            Idct8x8V256(outPlane, outOffset, outStride, data);
        else if (Vector128.IsHardwareAccelerated)
            Idct8x8V128(outPlane, outOffset, outStride, data);
        else
            Idct8x8Scalar(outPlane, outOffset, outStride, data);
    }

    // ------------------------------------------------------------------ scalar

    /// <summary>The reference kernel; the vector kernels must reproduce it bit for bit.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void Idct8x8Scalar(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data)
    {
        Span<float> s = stackalloc float[64];
        TransformScalar(data, s);
        for (var y = 0; y < 8; y++)
        {
            var o = outOffset + y * outStride;
            for (var x = 0; x < 8; x++)
                outPlane[o + x] = ToSample(s[y * 8 + x]);
        }
    }

    /// <summary>Both 1-D passes, scalar; <paramref name="result"/> is row-major, before level shift and rounding.</summary>
    internal static void TransformScalar(ReadOnlySpan<short> data, Span<float> result)
    {
        Span<float> t = stackalloc float[64];

        for (var x = 0; x < 8; x++)
        {
            Idct1D(data[x], data[8 + x], data[16 + x], data[24 + x], data[32 + x], data[40 + x], data[48 + x], data[56 + x],
                out var s0, out var s1, out var s2, out var s3, out var s4, out var s5, out var s6, out var s7);
            t[x] = s0;
            t[8 + x] = s1;
            t[16 + x] = s2;
            t[24 + x] = s3;
            t[32 + x] = s4;
            t[40 + x] = s5;
            t[48 + x] = s6;
            t[56 + x] = s7;
        }

        for (var y = 0; y < 8; y++)
        {
            var r = t.Slice(y * 8, 8);
            Idct1D(r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7],
                out result[y * 8], out result[y * 8 + 1], out result[y * 8 + 2], out result[y * 8 + 3],
                out result[y * 8 + 4], out result[y * 8 + 5], out result[y * 8 + 6], out result[y * 8 + 7]);
        }
    }

    // trunc then clamp: the same byte as trunc(min(max(s + 128.5, 0), 255)) for every finite s
    // in int range (|s| ≤ 2^15 here), without the IEEE NaN/±0 handling of MathF.Min/Max.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ToSample(float s) => (byte)Math.Clamp((int)(s + Bias), 0, 255);

    // The three Idct1D overloads are textually identical apart from the operand type.
    // Keep them that way: identical text is what makes identical rounding.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct1D(float f0, float f1, float f2, float f3, float f4, float f5, float f6, float f7,
        out float s0, out float s1, out float s2, out float s3, out float s4, out float s5, out float s6, out float s7)
    {
        var a0 = (f0 + f4) * K4;
        var a1 = (f0 - f4) * K4;
        var b0 = f2 * C2 + f6 * C6;
        var b1 = f2 * C6 - f6 * C2;
        var e0 = a0 + b0;
        var e1 = a1 + b1;
        var e2 = a1 - b1;
        var e3 = a0 - b0;
        var o0 = f1 * C1 + f3 * C3 + f5 * C5 + f7 * C7;
        var o1 = f1 * C3 - f3 * C7 - f5 * C1 - f7 * C5;
        var o2 = f1 * C5 - f3 * C1 + f5 * C7 + f7 * C3;
        var o3 = f1 * C7 - f3 * C5 + f5 * C3 - f7 * C1;
        s0 = e0 + o0;
        s1 = e1 + o1;
        s2 = e2 + o2;
        s3 = e3 + o3;
        s4 = e3 - o3;
        s5 = e2 - o2;
        s6 = e1 - o1;
        s7 = e0 - o0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct1D(Vector128<float> f0, Vector128<float> f1, Vector128<float> f2, Vector128<float> f3,
        Vector128<float> f4, Vector128<float> f5, Vector128<float> f6, Vector128<float> f7,
        out Vector128<float> s0, out Vector128<float> s1, out Vector128<float> s2, out Vector128<float> s3,
        out Vector128<float> s4, out Vector128<float> s5, out Vector128<float> s6, out Vector128<float> s7)
    {
        var a0 = (f0 + f4) * K4;
        var a1 = (f0 - f4) * K4;
        var b0 = f2 * C2 + f6 * C6;
        var b1 = f2 * C6 - f6 * C2;
        var e0 = a0 + b0;
        var e1 = a1 + b1;
        var e2 = a1 - b1;
        var e3 = a0 - b0;
        var o0 = f1 * C1 + f3 * C3 + f5 * C5 + f7 * C7;
        var o1 = f1 * C3 - f3 * C7 - f5 * C1 - f7 * C5;
        var o2 = f1 * C5 - f3 * C1 + f5 * C7 + f7 * C3;
        var o3 = f1 * C7 - f3 * C5 + f5 * C3 - f7 * C1;
        s0 = e0 + o0;
        s1 = e1 + o1;
        s2 = e2 + o2;
        s3 = e3 + o3;
        s4 = e3 - o3;
        s5 = e2 - o2;
        s6 = e1 - o1;
        s7 = e0 - o0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct1D(Vector256<float> f0, Vector256<float> f1, Vector256<float> f2, Vector256<float> f3,
        Vector256<float> f4, Vector256<float> f5, Vector256<float> f6, Vector256<float> f7,
        out Vector256<float> s0, out Vector256<float> s1, out Vector256<float> s2, out Vector256<float> s3,
        out Vector256<float> s4, out Vector256<float> s5, out Vector256<float> s6, out Vector256<float> s7)
    {
        var a0 = (f0 + f4) * K4;
        var a1 = (f0 - f4) * K4;
        var b0 = f2 * C2 + f6 * C6;
        var b1 = f2 * C6 - f6 * C2;
        var e0 = a0 + b0;
        var e1 = a1 + b1;
        var e2 = a1 - b1;
        var e3 = a0 - b0;
        var o0 = f1 * C1 + f3 * C3 + f5 * C5 + f7 * C7;
        var o1 = f1 * C3 - f3 * C7 - f5 * C1 - f7 * C5;
        var o2 = f1 * C5 - f3 * C1 + f5 * C7 + f7 * C3;
        var o3 = f1 * C7 - f3 * C5 + f5 * C3 - f7 * C1;
        s0 = e0 + o0;
        s1 = e1 + o1;
        s2 = e2 + o2;
        s3 = e3 + o3;
        s4 = e3 - o3;
        s5 = e2 - o2;
        s6 = e1 - o1;
        s7 = e0 - o0;
    }

    // ------------------------------------------------------------------ Vector256

    /// <summary>
    /// One row per <see cref="Vector256{T}"/>: the column pass is lane-parallel as loaded;
    /// a transpose makes the row pass lane-parallel too, and a second one restores rows.
    /// </summary>
    internal static void Idct8x8V256(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data) =>
        Idct8x8V256(outPlane, outOffset, outStride, data, false, false);

    /// <summary>
    /// <see cref="Idct8x8V256(byte[], int, int, ReadOnlySpan{short})"/>, dropping the
    /// zero half of the column pass (<paramref name="halfRows"/>: coefficient rows 4–7
    /// are zero) and/or of the row pass (<paramref name="halfCols"/>: columns 4–7 are zero).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void Idct8x8V256(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data, bool halfRows, bool halfCols)
    {
        TransformV256Rows(data, halfRows, halfCols, out var c0, out var c1, out var c2, out var c3, out var c4, out var c5, out var c6, out var c7);

        // trunc(s + 128.5) first, then the saturating packs clamp to [0, 255]: the same bytes
        // as trunc(min(max(s + 128.5, 0), 255)) — truncation sends (−1, 0) to 0 either way
        // and everything ≥ 255 saturates. packssdw/packuswb keep each 128-bit half's order,
        // leaving row r's two 4-byte halves in dword r of the low and high lane; one vpermd
        // per four rows puts each row's 8 bytes together.
        // (Without AVX2 — only the tests call this kernel then — the portable clamp + narrow.)
        var bias = Vector256.Create(Bias);
        Vector256<ulong> b0123, b4567;
        if (Avx2.IsSupported)
        {
            var p01 = Avx2.PackSignedSaturate(Vector256.ConvertToInt32Native(c0 + bias), Vector256.ConvertToInt32Native(c1 + bias));
            var p23 = Avx2.PackSignedSaturate(Vector256.ConvertToInt32Native(c2 + bias), Vector256.ConvertToInt32Native(c3 + bias));
            var p45 = Avx2.PackSignedSaturate(Vector256.ConvertToInt32Native(c4 + bias), Vector256.ConvertToInt32Native(c5 + bias));
            var p67 = Avx2.PackSignedSaturate(Vector256.ConvertToInt32Native(c6 + bias), Vector256.ConvertToInt32Native(c7 + bias));
            var rowOrder = Vector256.Create(0, 4, 1, 5, 2, 6, 3, 7);
            b0123 = Avx2.PermuteVar8x32(Avx2.PackUnsignedSaturate(p01, p23).AsInt32(), rowOrder).AsUInt64();
            b4567 = Avx2.PermuteVar8x32(Avx2.PackUnsignedSaturate(p45, p67).AsInt32(), rowOrder).AsUInt64();
        }
        else
        {
            var lo = Vector256<float>.Zero;
            var hi = Vector256.Create(Max);
            Vector256<int> Clamp(Vector256<float> v) => Vector256.ConvertToInt32(Vector256.Min(Vector256.Max(v + bias, lo), hi));
            var q01 = Vector256.Narrow(Clamp(c0), Clamp(c1)).AsUInt16();
            var q23 = Vector256.Narrow(Clamp(c2), Clamp(c3)).AsUInt16();
            var q45 = Vector256.Narrow(Clamp(c4), Clamp(c5)).AsUInt16();
            var q67 = Vector256.Narrow(Clamp(c6), Clamp(c7)).AsUInt16();
            b0123 = Vector256.Narrow(q01, q23).AsUInt64();
            b4567 = Vector256.Narrow(q45, q67).AsUInt64();
        }

        ref var dst = ref MemoryMarshal.GetArrayDataReference(outPlane);
        _ = outPlane[outOffset + 7 * outStride + 7];
        _ = outPlane[outOffset];
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset), b0123.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + outStride), b0123.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + 2 * outStride), b0123.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + 3 * outStride), b0123.GetElement(3));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + 4 * outStride), b4567.GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + 5 * outStride), b4567.GetElement(1));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + 6 * outStride), b4567.GetElement(2));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, outOffset + 7 * outStride), b4567.GetElement(3));
    }

    /// <summary>Both 1-D passes; outputs are rows, before level shift and rounding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransformV256Rows(ReadOnlySpan<short> data, bool halfRows, bool halfCols,
        out Vector256<float> c0, out Vector256<float> c1, out Vector256<float> c2, out Vector256<float> c3,
        out Vector256<float> c4, out Vector256<float> c5, out Vector256<float> c6, out Vector256<float> c7)
    {
        if (data.Length < 64)
            throw new ArgumentException("IDCT block needs 64 coefficients.", nameof(data));
        ref var d = ref MemoryMarshal.GetReference(data);
        Vector256<float> r0, r1, r2, r3, r4, r5, r6, r7;
        if (halfRows)
            Idct1DHalf(LoadRow256(ref d, 0), LoadRow256(ref d, 1), LoadRow256(ref d, 2), LoadRow256(ref d, 3),
                out r0, out r1, out r2, out r3, out r4, out r5, out r6, out r7);
        else
            Idct1D(LoadRow256(ref d, 0), LoadRow256(ref d, 1), LoadRow256(ref d, 2), LoadRow256(ref d, 3),
                LoadRow256(ref d, 4), LoadRow256(ref d, 5), LoadRow256(ref d, 6), LoadRow256(ref d, 7),
                out r0, out r1, out r2, out r3, out r4, out r5, out r6, out r7);

        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        if (halfCols)
            Idct1DHalf(r0, r1, r2, r3, out c0, out c1, out c2, out c3, out c4, out c5, out c6, out c7);
        else
            Idct1D(r0, r1, r2, r3, r4, r5, r6, r7,
                out c0, out c1, out c2, out c3, out c4, out c5, out c6, out c7);

        Transpose(ref c0, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, ref c7);
    }

    /// <summary>Test hook: <see cref="TransformV256Rows"/> stored row-major.</summary>
    internal static void TransformV256(ReadOnlySpan<short> data, Span<float> result) => TransformV256(data, result, false, false);

    /// <summary>Test hook: the sparse variants of <see cref="TransformV256Rows"/>, stored row-major.</summary>
    internal static void TransformV256(ReadOnlySpan<short> data, Span<float> result, bool halfRows, bool halfCols)
    {
        TransformV256Rows(data, halfRows, halfCols, out var c0, out var c1, out var c2, out var c3, out var c4, out var c5, out var c6, out var c7);
        c0.CopyTo(result);
        c1.CopyTo(result.Slice(8));
        c2.CopyTo(result.Slice(16));
        c3.CopyTo(result.Slice(24));
        c4.CopyTo(result.Slice(32));
        c5.CopyTo(result.Slice(40));
        c6.CopyTo(result.Slice(48));
        c7.CopyTo(result.Slice(56));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LoadRow256(ref short data, int row)
    {
        var s = Vector128.LoadUnsafe(ref data, (nuint)(row * 8));
        return Vector256.ConvertToSingle(Avx2.IsSupported
            ? Avx2.ConvertToVector256Int32(s)
            : Vector256.Create(Vector128.WidenLower(s), Vector128.WidenUpper(s)));
    }

    /// <summary>
    /// <see cref="Idct1D(Vector256{float}, Vector256{float}, Vector256{float}, Vector256{float}, Vector256{float}, Vector256{float}, Vector256{float}, Vector256{float}, out Vector256{float}, out Vector256{float}, out Vector256{float}, out Vector256{float}, out Vector256{float}, out Vector256{float}, out Vector256{float}, out Vector256{float})"/>
    /// with f4 = f5 = f6 = f7 = +0 and every resulting exact zero term deleted:
    /// (f0 ± 0)·K4 = f0·K4, f2·C2 + 0 = f2·C2, and sums of odd terms lose their last two
    /// addends. Same values bit for bit.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct1DHalf(Vector256<float> f0, Vector256<float> f1, Vector256<float> f2, Vector256<float> f3,
        out Vector256<float> s0, out Vector256<float> s1, out Vector256<float> s2, out Vector256<float> s3,
        out Vector256<float> s4, out Vector256<float> s5, out Vector256<float> s6, out Vector256<float> s7)
    {
        var a0 = f0 * K4;
        var b0 = f2 * C2;
        var b1 = f2 * C6;
        var e0 = a0 + b0;
        var e1 = a0 + b1;
        var e2 = a0 - b1;
        var e3 = a0 - b0;
        var o0 = f1 * C1 + f3 * C3;
        var o1 = f1 * C3 - f3 * C7;
        var o2 = f1 * C5 - f3 * C1;
        var o3 = f1 * C7 - f3 * C5;
        s0 = e0 + o0;
        s1 = e1 + o1;
        s2 = e2 + o2;
        s3 = e3 + o3;
        s4 = e3 - o3;
        s5 = e2 - o2;
        s6 = e1 - o1;
        s7 = e0 - o0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(
        ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        if (Avx.IsSupported)
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
            return;
        }

        Span<float> t = stackalloc float[64];
        r0.CopyTo(t);
        r1.CopyTo(t.Slice(8));
        r2.CopyTo(t.Slice(16));
        r3.CopyTo(t.Slice(24));
        r4.CopyTo(t.Slice(32));
        r5.CopyTo(t.Slice(40));
        r6.CopyTo(t.Slice(48));
        r7.CopyTo(t.Slice(56));
        TransposeInPlace(t);
        r0 = Vector256.Create<float>(t);
        r1 = Vector256.Create<float>(t.Slice(8));
        r2 = Vector256.Create<float>(t.Slice(16));
        r3 = Vector256.Create<float>(t.Slice(24));
        r4 = Vector256.Create<float>(t.Slice(32));
        r5 = Vector256.Create<float>(t.Slice(40));
        r6 = Vector256.Create<float>(t.Slice(48));
        r7 = Vector256.Create<float>(t.Slice(56));
    }

    private static void TransposeInPlace(Span<float> t)
    {
        for (var i = 0; i < 8; i++)
        {
            for (var j = i + 1; j < 8; j++)
                (t[i * 8 + j], t[j * 8 + i]) = (t[j * 8 + i], t[i * 8 + j]);
        }
    }

    // ------------------------------------------------------------------ Vector128

    /// <summary>
    /// Each row is a (left, right) pair of <see cref="Vector128{T}"/> halves; both 1-D
    /// passes run on four lanes at a time. The transposes go through a scratch block.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void Idct8x8V128(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data)
    {
        Span<float> o = stackalloc float[64];
        TransformV128(data, o);

        var lo = Vector128<float>.Zero;
        var hi = Vector128.Create(Max);
        var bias = Vector128.Create(Bias);
        for (var y = 0; y < 8; y++)
        {
            var a = Vector128.ConvertToInt32Native(Vector128.MinNative(Vector128.MaxNative(Vector128.Create<float>(o.Slice(y * 8)) + bias, lo), hi));
            var b = Vector128.ConvertToInt32Native(Vector128.MinNative(Vector128.MaxNative(Vector128.Create<float>(o.Slice(y * 8 + 4)) + bias, lo), hi));
            var w = Vector128.Narrow(a, b).AsUInt16();
            var bytes = Vector128.Narrow(w, w).AsUInt64();
            MemoryMarshal.Write(outPlane.AsSpan(outOffset + y * outStride, 8), bytes.GetElement(0));
        }
    }

    /// <summary>Both 1-D passes, four lanes at a time; <paramref name="o"/> is row-major, before level shift and rounding.</summary>
    internal static void TransformV128(ReadOnlySpan<short> data, Span<float> o)
    {
        Span<float> t = stackalloc float[64];

        for (var half = 0; half < 2; half++)
        {
            Idct1D(LoadHalf128(data, 0, half), LoadHalf128(data, 1, half), LoadHalf128(data, 2, half), LoadHalf128(data, 3, half),
                LoadHalf128(data, 4, half), LoadHalf128(data, 5, half), LoadHalf128(data, 6, half), LoadHalf128(data, 7, half),
                out var s0, out var s1, out var s2, out var s3, out var s4, out var s5, out var s6, out var s7);
            var c = half * 4;
            s0.CopyTo(t.Slice(c));
            s1.CopyTo(t.Slice(8 + c));
            s2.CopyTo(t.Slice(16 + c));
            s3.CopyTo(t.Slice(24 + c));
            s4.CopyTo(t.Slice(32 + c));
            s5.CopyTo(t.Slice(40 + c));
            s6.CopyTo(t.Slice(48 + c));
            s7.CopyTo(t.Slice(56 + c));
        }

        // t is rows × columns after the column pass; transposed, row u of t holds
        // frequency u of every output row, so the row pass is lane-parallel again.
        TransposeInPlace(t);

        for (var half = 0; half < 2; half++)
        {
            var c = half * 4;
            Idct1D(Vector128.Create<float>(t.Slice(c)), Vector128.Create<float>(t.Slice(8 + c)),
                Vector128.Create<float>(t.Slice(16 + c)), Vector128.Create<float>(t.Slice(24 + c)),
                Vector128.Create<float>(t.Slice(32 + c)), Vector128.Create<float>(t.Slice(40 + c)),
                Vector128.Create<float>(t.Slice(48 + c)), Vector128.Create<float>(t.Slice(56 + c)),
                out var s0, out var s1, out var s2, out var s3, out var s4, out var s5, out var s6, out var s7);
            s0.CopyTo(o.Slice(c));
            s1.CopyTo(o.Slice(8 + c));
            s2.CopyTo(o.Slice(16 + c));
            s3.CopyTo(o.Slice(24 + c));
            s4.CopyTo(o.Slice(32 + c));
            s5.CopyTo(o.Slice(40 + c));
            s6.CopyTo(o.Slice(48 + c));
            s7.CopyTo(o.Slice(56 + c));
        }

        // o[x * 8 + y] is output sample (y, x).
        TransposeInPlace(o);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> LoadHalf128(ReadOnlySpan<short> data, int row, int half)
    {
        var s = Vector128.Create(data.Slice(row * 8, 8));
        return Vector128.ConvertToSingle(half == 0 ? Vector128.WidenLower(s) : Vector128.WidenUpper(s));
    }

    // Reduced-IDCT basis tables, T[u * B + p] = (c_u / 2) * cos((2p+1) u π / (2B)).
    // With this normalisation a DC-only block yields X[0,0]/8 in 2D — identical
    // level handling to the full 8×8 transform — and B=8 would reproduce the
    // standard IDCT basis exactly.
    private static readonly float[] Table4 = BuildTable(4);
    private static readonly float[] Table2 = BuildTable(2);
    private static readonly float[] Table1 = BuildTable(1);

    private static float[] BuildTable(int b)
    {
        var t = new float[b * b];
        for (var u = 0; u < b; u++)
        {
            var cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
            for (var p = 0; p < b; p++)
                t[u * b + p] = (float)(cu / 2.0 * Math.Cos((2 * p + 1) * u * Math.PI / (2 * b)));
        }

        return t;
    }

    /// <summary>
    /// Reduced inverse DCT: transforms the top-left <paramref name="b"/>×<paramref name="b"/>
    /// coefficients of an 8×8 block into a b×b downsampled sample tile
    /// (b ∈ {4, 2, 1}). Adds the +128 level shift and clamps.
    /// </summary>
    public static void IdctReduced(byte[] outPlane, int outOffset, int outStride, ReadOnlySpan<short> data, int b)
    {
        if (b == 1)
        {
            // 2D DC-only: mean of the block = X[0,0] / 8.
            var dc = Clamp((int)MathF.Floor(data[0] * 0.125f + 128.5f));
            outPlane[outOffset] = dc;
            return;
        }

        var table = b == 4 ? Table4 : Table2;

        // Separable: rows of `tmp` are partially-transformed along the horizontal
        // frequency axis; the second pass finishes the vertical axis.
        Span<float> tmp = stackalloc float[16]; // b*b, max 4*4

        for (var u = 0; u < b; u++)
        {
            for (var q = 0; q < b; q++)
            {
                var acc = 0f;
                for (var v = 0; v < b; v++)
                    acc += table[v * b + q] * data[u * 8 + v];
                tmp[u * b + q] = acc;
            }
        }

        for (var p = 0; p < b; p++)
        {
            var o = outOffset + p * outStride;
            for (var q = 0; q < b; q++)
            {
                var acc = 0f;
                for (var u = 0; u < b; u++)
                    acc += table[u * b + p] * tmp[u * b + q];
                outPlane[o + q] = Clamp((int)MathF.Floor(acc + 128.5f));
            }
        }
    }
}
