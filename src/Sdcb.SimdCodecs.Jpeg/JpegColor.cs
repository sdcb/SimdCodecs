using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Colour-conversion row kernels: JFIF (ITU-T T.871) YCbCr → RGB, the Blinn 8×8
/// byte multiply used for Adobe CMYK / YCCK, and the grayscale / RGB packers. Every
/// kernel writes packed 32-bit pixels, RGBA or (with <c>bgr</c>) BGRA, alpha 255.
///
/// <para>
/// The YCbCr arithmetic is defined here, clean-room, from T.871 §7 and nothing else:
/// <code>
/// R = Y + 1.402·(Cr − 128)
/// G = Y − (0.114·1.772/0.587)·(Cb − 128) − (0.299·1.402/0.587)·(Cr − 128)
/// B = Y + 1.772·(Cb − 128)
/// </code>
/// Chroma arrives at 16× scale (<c>c16</c>, an exact integer from
/// <see cref="JpegResample.Row16"/>, or a full-resolution sample × 16), so upsampled
/// chroma is never rounded to 8 bits on the way. With <c>cb = c16b − 2048</c> and
/// <c>cr = c16r − 2048</c> as exact floats and the coefficients divided by 16 (each
/// rounded once from double to float at compile time), the IEEE single-precision
/// sequence is
/// <code>
/// r = Y + cr·KR
/// g = (Y + cb·KGb) + cr·KGr
/// b = Y + cb·KB
/// out = trunc(min(max(v + 0.5, 0), 255))
/// </code>
/// with no fused multiply-add. That sequence is the contract every implementation
/// must reproduce bit for bit: the scalar, <see cref="Vector128"/> and
/// <see cref="Vector256"/> kernels all evaluate it through the textually identical
/// <c>Ycc</c> cores (<c>JpegColorEquivalenceTests</c> compares their float bits).
/// </para>
/// </summary>
internal static class JpegColor
{
    private const float KR = (float)(1.402 / 16);
    private const float KGb = (float)(-(0.114 * 1.772 / 0.587) / 16);
    private const float KGr = (float)(-(0.299 * 1.402 / 0.587) / 16);
    private const float KB = (float)(1.772 / 16);
    private const int Alpha = unchecked((int)0xFF000000);

    private static bool UseV256 => Vector256.IsHardwareAccelerated && Avx2.IsSupported;
    private static bool UseV128 => Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    // ================================================================ dispatch

    /// <summary>YCbCr → packed pixels; chroma at 16× scale (subsampled, from <see cref="JpegResample.Row16"/>).</summary>
    public static void YccRow(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, int n, bool bgr = false)
    {
        Check(dst, n, y.Length, cb16.Length, cr16.Length);
        var i = UseV256 ? YccRow16V256(dst, y, cb16, cr16, n, bgr) : UseV128 ? YccRow16V128(dst, y, cb16, cr16, n, bgr) : 0;
        YccRow16Scalar(dst, y, cb16, cr16, i, n, bgr);
    }

    /// <summary>YCbCr → packed pixels; full-resolution 8-bit chroma.</summary>
    public static void YccRow(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int n, bool bgr = false)
    {
        Check(dst, n, y.Length, cb.Length, cr.Length);
        var i = UseV256 ? YccRow8V256(dst, y, cb, cr, n, bgr) : UseV128 ? YccRow8V128(dst, y, cb, cr, n, bgr) : 0;
        YccRow8Scalar(dst, y, cb, cr, i, n, bgr);
    }

    /// <summary>YCCK → CMY (inverted YCbCr → RGB) with K folded in by <see cref="Blinn8x8"/>; chroma at 16× scale.</summary>
    public static void YcckRow(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, ReadOnlySpan<byte> k, int n, bool bgr = false)
    {
        Check(dst, n, y.Length, cb16.Length, cr16.Length);
        Check(dst, n, k.Length, k.Length, k.Length);
        var i = UseV256 ? YcckRow16V256(dst, y, cb16, cr16, k, n, bgr) : UseV128 ? YcckRow16V128(dst, y, cb16, cr16, k, n, bgr) : 0;
        YcckRow16Scalar(dst, y, cb16, cr16, k, i, n, bgr);
    }

    /// <summary>YCCK with full-resolution 8-bit chroma.</summary>
    public static void YcckRow(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, int n, bool bgr = false)
    {
        Check(dst, n, y.Length, cb.Length, cr.Length);
        Check(dst, n, k.Length, k.Length, k.Length);
        var i = UseV256 ? YcckRow8V256(dst, y, cb, cr, k, n, bgr) : UseV128 ? YcckRow8V128(dst, y, cb, cr, k, n, bgr) : 0;
        YcckRow8Scalar(dst, y, cb, cr, k, i, n, bgr);
    }

    /// <summary>Plain Adobe CMYK: each ink channel times K by <see cref="Blinn8x8"/>.</summary>
    public static void CmykRow(Span<byte> dst, ReadOnlySpan<byte> c, ReadOnlySpan<byte> m, ReadOnlySpan<byte> y, ReadOnlySpan<byte> k, int n, bool bgr = false)
    {
        Check(dst, n, c.Length, m.Length, y.Length);
        Check(dst, n, k.Length, k.Length, k.Length);
        var i = UseV256 ? CmykRowV256(dst, c, m, y, k, n, bgr) : UseV128 ? CmykRowV128(dst, c, m, y, k, n, bgr) : 0;
        CmykRowScalar(dst, c, m, y, k, i, n, bgr);
    }

    /// <summary>Three full-resolution planes straight into packed pixels.</summary>
    public static void RgbRow(Span<byte> dst, ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, int n, bool bgr = false)
    {
        Check(dst, n, r.Length, g.Length, b.Length);
        var i = UseV256 ? RgbRowV256(dst, r, g, b, n, bgr) : UseV128 ? RgbRowV128(dst, r, g, b, n, bgr) : 0;
        RgbRowScalar(dst, r, g, b, i, n, bgr);
    }

    /// <summary>Grayscale replicated into R, G and B.</summary>
    public static void GrayRow(Span<byte> dst, ReadOnlySpan<byte> y, int n)
    {
        Check(dst, n, y.Length, y.Length, y.Length);
        var i = UseV256 ? GrayRowV256(dst, y, n) : UseV128 ? GrayRowV128(dst, y, n) : 0;
        GrayRowScalar(dst, y, i, n);
    }

    /// <summary>
    /// Compacts <paramref name="n"/> packed 4-byte pixels to 3-byte RGB (dropping
    /// the fourth byte). <paramref name="dst"/> must not overlap <paramref name="src"/>.
    /// </summary>
    public static void PackRgb24(Span<byte> dst, ReadOnlySpan<byte> src, int n)
    {
        if ((uint)n > (uint)(src.Length / 4) || (uint)n > (uint)(dst.Length / 3))
            ThrowRange();
        var i = 0;
        ref var s = ref MemoryMarshal.GetReference(src);
        ref var d = ref MemoryMarshal.GetReference(dst);
        if (Vector128.IsHardwareAccelerated)
        {
            // 4 pixels → 12 bytes per step; the 16-byte store overruns by 4 bytes, so
            // stop while at least two more pixels' (6 bytes ≥ 4) worth of room follows.
            var shuffle = Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 15, 15, 15, 15);
            for (; i + 6 <= n; i += 4)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)(i * 4));
                Vector128.Shuffle(v, shuffle).StoreUnsafe(ref d, (nuint)(i * 3));
            }
        }

        for (; i < n; ++i)
        {
            Unsafe.Add(ref d, i * 3) = Unsafe.Add(ref s, i * 4);
            Unsafe.Add(ref d, i * 3 + 1) = Unsafe.Add(ref s, i * 4 + 1);
            Unsafe.Add(ref d, i * 3 + 2) = Unsafe.Add(ref s, i * 4 + 2);
        }
    }

    /// <summary>
    /// Blinn's accurate byte multiply: round(x * y / 255) without a divide.
    /// Used to fold the K plane into CMY (and the alpha-style inversion for YCCK).
    /// </summary>
    public static byte Blinn8x8(byte x, byte y)
    {
        var t = (uint)(x * y + 128);
        return (byte)((t + (t >> 8)) >> 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Check(Span<byte> dst, int n, int a, int b, int c)
    {
        if ((uint)n > (uint)a || (uint)n > (uint)b || (uint)n > (uint)c || (uint)n > (uint)(dst.Length / 4))
            ThrowRange();
    }

    private static void ThrowRange() => throw new ArgumentOutOfRangeException("n", "row kernel: count exceeds a buffer");

    // ================================================================ cores
    // The three Ycc overloads are textually identical apart from the operand type.
    // Keep them that way: identical text is what makes identical rounding.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ycc(float yf, float cb, float cr, out float r, out float g, out float b)
    {
        r = yf + cr * KR;
        g = yf + cb * KGb + cr * KGr;
        b = yf + cb * KB;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ycc(Vector128<float> yf, Vector128<float> cb, Vector128<float> cr,
        out Vector128<float> r, out Vector128<float> g, out Vector128<float> b)
    {
        r = yf + cr * KR;
        g = yf + cb * KGb + cr * KGr;
        b = yf + cb * KB;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ycc(Vector256<float> yf, Vector256<float> cb, Vector256<float> cr,
        out Vector256<float> r, out Vector256<float> g, out Vector256<float> b)
    {
        r = yf + cr * KR;
        g = yf + cb * KGb + cr * KGr;
        b = yf + cb * KB;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToByte(float v) => (int)MathF.Min(MathF.Max(v + 0.5f, 0f), 255f);

    // The Native min/max/convert are the single hardware instructions; the portable ones
    // add NaN/±0/saturation fix-ups (≈10 extra instructions per vector). Inputs are never
    // NaN and the clamp keeps them in [0, 255], so the results are identical.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ToByte(Vector128<float> v) =>
        Vector128.ConvertToInt32Native(Vector128.MinNative(Vector128.MaxNative(v + Vector128.Create(0.5f), Vector128<float>.Zero), Vector128.Create(255f)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ToByte(Vector256<float> v) =>
        Vector256.ConvertToInt32Native(Vector256.MinNative(Vector256.MaxNative(v + Vector256.Create(0.5f), Vector256<float>.Zero), Vector256.Create(255f)));

    /// <summary>
    /// Eight pixels' pre-rounding r, g, b → 32 packed bytes, AVX2. trunc(v + 0.5) first and
    /// the clamp to [0, 255] by the saturating packs afterwards — the same bytes as
    /// <see cref="ToByte(Vector256{float})"/> (truncation toward zero sends (−1, 0) to 0
    /// either way, and anything ≥ 255 saturates), in four instructions instead of the
    /// min/max/shift/or sequence. packssdw keeps lane order per 128-bit half, packuswb
    /// leaves R0–3 G0–3 B0–3 A0–3 in each half, and one in-lane byte shuffle interleaves.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> PackSaturate(Vector256<float> r, Vector256<float> g, Vector256<float> b, bool bgr)
    {
        var half = Vector256.Create(0.5f);
        var ri = Vector256.ConvertToInt32Native(r + half);
        var gi = Vector256.ConvertToInt32Native(g + half);
        var bi = Vector256.ConvertToInt32Native(b + half);
        var lo = Avx2.PackSignedSaturate(bgr ? bi : ri, gi);
        var hi = Avx2.PackSignedSaturate(bgr ? ri : bi, Vector256.Create(255));
        return Avx2.Shuffle(Avx2.PackUnsignedSaturate(lo, hi),
            Vector256.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15, 0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Blinn(int x, int k)
    {
        var t = x * k + 128;
        return (t + (t >> 8)) >> 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Blinn(Vector128<int> x, Vector128<int> k)
    {
        var t = x * k + Vector128.Create(128);
        return Vector128.ShiftRightLogical(t + Vector128.ShiftRightLogical(t, 8), 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Blinn(Vector256<int> x, Vector256<int> k)
    {
        var t = x * k + Vector256.Create(128);
        return Vector256.ShiftRightLogical(t + Vector256.ShiftRightLogical(t, 8), 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pack(int r, int g, int b, bool bgr) =>
        bgr ? b | (g << 8) | (r << 16) | Alpha : r | (g << 8) | (b << 16) | Alpha;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Pack(Vector128<int> r, Vector128<int> g, Vector128<int> b, bool bgr) =>
        bgr
            ? b | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(r, 16) | Vector128.Create(Alpha)
            : r | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(b, 16) | Vector128.Create(Alpha);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Pack(Vector256<int> r, Vector256<int> g, Vector256<int> b, bool bgr) =>
        bgr
            ? b | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(r, 16) | Vector256.Create(Alpha)
            : r | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(b, 16) | Vector256.Create(Alpha);

    // ================================================================ loads
    // Vector128: 8 elements → two int vectors. Vector256 (AVX2): 8 elements → one.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load8(ref byte p, int i, out Vector128<int> lo, out Vector128<int> hi)
    {
        var w = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i))).AsByte());
        lo = Vector128.WidenLower(w).AsInt32();
        hi = Vector128.WidenUpper(w).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load8(ref ushort p, int i, out Vector128<int> lo, out Vector128<int> hi)
    {
        var w = Vector128.LoadUnsafe(ref p, (nuint)i);
        lo = Vector128.WidenLower(w).AsInt32();
        hi = Vector128.WidenUpper(w).AsInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Load8(ref byte p, int i) =>
        Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i))).AsByte());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Load8(ref ushort p, int i) =>
        Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref p, (nuint)i));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref byte d, int i, Vector128<int> px) => px.AsByte().StoreUnsafe(ref d, (nuint)(i * 4));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref byte d, int i, Vector256<int> px) => px.AsByte().StoreUnsafe(ref d, (nuint)(i * 4));

    // ================================================================ YCbCr, 16× chroma

    internal static void YccRow16Scalar(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, int from, int n, bool bgr)
    {
        for (var i = from; i < n; ++i)
        {
            Ycc(y[i], cb16[i] - 2048, cr16[i] - 2048, out var r, out var g, out var b);
            MemoryMarshal.Write(dst.Slice(i * 4), Pack(ToByte(r), ToByte(g), ToByte(b), bgr));
        }
    }

    /// <returns>Pixels done (a multiple of 8); the caller finishes with the scalar kernel.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YccRow16V128(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb16);
        ref var crp = ref MemoryMarshal.GetReference(cr16);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector128.Create(2048);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Load8(ref yp, i, out var y0, out var y1);
            Load8(ref cbp, i, out var cb0, out var cb1);
            Load8(ref crp, i, out var cr0, out var cr1);
            Ycc(Vector128.ConvertToSingle(y0), Vector128.ConvertToSingle(cb0 - bias), Vector128.ConvertToSingle(cr0 - bias), out var r0, out var g0, out var b0);
            Ycc(Vector128.ConvertToSingle(y1), Vector128.ConvertToSingle(cb1 - bias), Vector128.ConvertToSingle(cr1 - bias), out var r1, out var g1, out var b1);
            Store(ref d, i, Pack(ToByte(r0), ToByte(g0), ToByte(b0), bgr));
            Store(ref d, i + 4, Pack(ToByte(r1), ToByte(g1), ToByte(b1), bgr));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YccRow16V256(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb16);
        ref var crp = ref MemoryMarshal.GetReference(cr16);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector256.Create(2048);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Ycc(Vector256.ConvertToSingle(Load8(ref yp, i)), Vector256.ConvertToSingle(Load8(ref cbp, i) - bias),
                Vector256.ConvertToSingle(Load8(ref crp, i) - bias), out var r, out var g, out var b);
            PackSaturate(r, g, b, bgr).StoreUnsafe(ref d, (nuint)(i * 4));
        }

        return i;
    }

    // ================================================================ YCbCr, 8-bit chroma

    internal static void YccRow8Scalar(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int from, int n, bool bgr)
    {
        for (var i = from; i < n; ++i)
        {
            Ycc(y[i], (cb[i] << 4) - 2048, (cr[i] << 4) - 2048, out var r, out var g, out var b);
            MemoryMarshal.Write(dst.Slice(i * 4), Pack(ToByte(r), ToByte(g), ToByte(b), bgr));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YccRow8V128(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb);
        ref var crp = ref MemoryMarshal.GetReference(cr);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector128.Create(2048);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Load8(ref yp, i, out var y0, out var y1);
            Load8(ref cbp, i, out var cb0, out var cb1);
            Load8(ref crp, i, out var cr0, out var cr1);
            Ycc(Vector128.ConvertToSingle(y0), Vector128.ConvertToSingle(Vector128.ShiftLeft(cb0, 4) - bias),
                Vector128.ConvertToSingle(Vector128.ShiftLeft(cr0, 4) - bias), out var r0, out var g0, out var b0);
            Ycc(Vector128.ConvertToSingle(y1), Vector128.ConvertToSingle(Vector128.ShiftLeft(cb1, 4) - bias),
                Vector128.ConvertToSingle(Vector128.ShiftLeft(cr1, 4) - bias), out var r1, out var g1, out var b1);
            Store(ref d, i, Pack(ToByte(r0), ToByte(g0), ToByte(b0), bgr));
            Store(ref d, i + 4, Pack(ToByte(r1), ToByte(g1), ToByte(b1), bgr));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YccRow8V256(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb);
        ref var crp = ref MemoryMarshal.GetReference(cr);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector256.Create(2048);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Ycc(Vector256.ConvertToSingle(Load8(ref yp, i)), Vector256.ConvertToSingle(Vector256.ShiftLeft(Load8(ref cbp, i), 4) - bias),
                Vector256.ConvertToSingle(Vector256.ShiftLeft(Load8(ref crp, i), 4) - bias), out var r, out var g, out var b);
            PackSaturate(r, g, b, bgr).StoreUnsafe(ref d, (nuint)(i * 4));
        }

        return i;
    }

    // ================================================================ YCCK

    internal static void YcckRow16Scalar(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, ReadOnlySpan<byte> k, int from, int n, bool bgr)
    {
        for (var i = from; i < n; ++i)
        {
            Ycc(y[i], cb16[i] - 2048, cr16[i] - 2048, out var r, out var g, out var b);
            int kk = k[i];
            MemoryMarshal.Write(dst.Slice(i * 4), Pack(Blinn(255 - ToByte(r), kk), Blinn(255 - ToByte(g), kk), Blinn(255 - ToByte(b), kk), bgr));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YcckRow16V128(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, ReadOnlySpan<byte> k, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb16);
        ref var crp = ref MemoryMarshal.GetReference(cr16);
        ref var kp = ref MemoryMarshal.GetReference(k);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector128.Create(2048);
        var full = Vector128.Create(255);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Load8(ref yp, i, out var y0, out var y1);
            Load8(ref cbp, i, out var cb0, out var cb1);
            Load8(ref crp, i, out var cr0, out var cr1);
            Load8(ref kp, i, out var k0, out var k1);
            Ycc(Vector128.ConvertToSingle(y0), Vector128.ConvertToSingle(cb0 - bias), Vector128.ConvertToSingle(cr0 - bias), out var r0, out var g0, out var b0);
            Ycc(Vector128.ConvertToSingle(y1), Vector128.ConvertToSingle(cb1 - bias), Vector128.ConvertToSingle(cr1 - bias), out var r1, out var g1, out var b1);
            Store(ref d, i, Pack(Blinn(full - ToByte(r0), k0), Blinn(full - ToByte(g0), k0), Blinn(full - ToByte(b0), k0), bgr));
            Store(ref d, i + 4, Pack(Blinn(full - ToByte(r1), k1), Blinn(full - ToByte(g1), k1), Blinn(full - ToByte(b1), k1), bgr));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YcckRow16V256(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, ReadOnlySpan<byte> k, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb16);
        ref var crp = ref MemoryMarshal.GetReference(cr16);
        ref var kp = ref MemoryMarshal.GetReference(k);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector256.Create(2048);
        var full = Vector256.Create(255);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Ycc(Vector256.ConvertToSingle(Load8(ref yp, i)), Vector256.ConvertToSingle(Load8(ref cbp, i) - bias),
                Vector256.ConvertToSingle(Load8(ref crp, i) - bias), out var r, out var g, out var b);
            var kk = Load8(ref kp, i);
            Store(ref d, i, Pack(Blinn(full - ToByte(r), kk), Blinn(full - ToByte(g), kk), Blinn(full - ToByte(b), kk), bgr));
        }

        return i;
    }

    internal static void YcckRow8Scalar(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, int from, int n, bool bgr)
    {
        for (var i = from; i < n; ++i)
        {
            Ycc(y[i], (cb[i] << 4) - 2048, (cr[i] << 4) - 2048, out var r, out var g, out var b);
            int kk = k[i];
            MemoryMarshal.Write(dst.Slice(i * 4), Pack(Blinn(255 - ToByte(r), kk), Blinn(255 - ToByte(g), kk), Blinn(255 - ToByte(b), kk), bgr));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YcckRow8V128(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb);
        ref var crp = ref MemoryMarshal.GetReference(cr);
        ref var kp = ref MemoryMarshal.GetReference(k);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector128.Create(2048);
        var full = Vector128.Create(255);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Load8(ref yp, i, out var y0, out var y1);
            Load8(ref cbp, i, out var cb0, out var cb1);
            Load8(ref crp, i, out var cr0, out var cr1);
            Load8(ref kp, i, out var k0, out var k1);
            Ycc(Vector128.ConvertToSingle(y0), Vector128.ConvertToSingle(Vector128.ShiftLeft(cb0, 4) - bias),
                Vector128.ConvertToSingle(Vector128.ShiftLeft(cr0, 4) - bias), out var r0, out var g0, out var b0);
            Ycc(Vector128.ConvertToSingle(y1), Vector128.ConvertToSingle(Vector128.ShiftLeft(cb1, 4) - bias),
                Vector128.ConvertToSingle(Vector128.ShiftLeft(cr1, 4) - bias), out var r1, out var g1, out var b1);
            Store(ref d, i, Pack(Blinn(full - ToByte(r0), k0), Blinn(full - ToByte(g0), k0), Blinn(full - ToByte(b0), k0), bgr));
            Store(ref d, i + 4, Pack(Blinn(full - ToByte(r1), k1), Blinn(full - ToByte(g1), k1), Blinn(full - ToByte(b1), k1), bgr));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int YcckRow8V256(Span<byte> dst, ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, ReadOnlySpan<byte> k, int n, bool bgr)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var cbp = ref MemoryMarshal.GetReference(cb);
        ref var crp = ref MemoryMarshal.GetReference(cr);
        ref var kp = ref MemoryMarshal.GetReference(k);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var bias = Vector256.Create(2048);
        var full = Vector256.Create(255);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Ycc(Vector256.ConvertToSingle(Load8(ref yp, i)), Vector256.ConvertToSingle(Vector256.ShiftLeft(Load8(ref cbp, i), 4) - bias),
                Vector256.ConvertToSingle(Vector256.ShiftLeft(Load8(ref crp, i), 4) - bias), out var r, out var g, out var b);
            var kk = Load8(ref kp, i);
            Store(ref d, i, Pack(Blinn(full - ToByte(r), kk), Blinn(full - ToByte(g), kk), Blinn(full - ToByte(b), kk), bgr));
        }

        return i;
    }

    // ================================================================ CMYK / RGB / gray

    internal static void CmykRowScalar(Span<byte> dst, ReadOnlySpan<byte> c, ReadOnlySpan<byte> m, ReadOnlySpan<byte> y, ReadOnlySpan<byte> k, int from, int n, bool bgr)
    {
        for (var i = from; i < n; ++i)
        {
            int kk = k[i];
            MemoryMarshal.Write(dst.Slice(i * 4), Pack(Blinn(c[i], kk), Blinn(m[i], kk), Blinn(y[i], kk), bgr));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int CmykRowV128(Span<byte> dst, ReadOnlySpan<byte> c, ReadOnlySpan<byte> m, ReadOnlySpan<byte> y, ReadOnlySpan<byte> k, int n, bool bgr)
    {
        ref var cp = ref MemoryMarshal.GetReference(c);
        ref var mp = ref MemoryMarshal.GetReference(m);
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var kp = ref MemoryMarshal.GetReference(k);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Load8(ref cp, i, out var c0, out var c1);
            Load8(ref mp, i, out var m0, out var m1);
            Load8(ref yp, i, out var y0, out var y1);
            Load8(ref kp, i, out var k0, out var k1);
            Store(ref d, i, Pack(Blinn(c0, k0), Blinn(m0, k0), Blinn(y0, k0), bgr));
            Store(ref d, i + 4, Pack(Blinn(c1, k1), Blinn(m1, k1), Blinn(y1, k1), bgr));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int CmykRowV256(Span<byte> dst, ReadOnlySpan<byte> c, ReadOnlySpan<byte> m, ReadOnlySpan<byte> y, ReadOnlySpan<byte> k, int n, bool bgr)
    {
        ref var cp = ref MemoryMarshal.GetReference(c);
        ref var mp = ref MemoryMarshal.GetReference(m);
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var kp = ref MemoryMarshal.GetReference(k);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            var kk = Load8(ref kp, i);
            Store(ref d, i, Pack(Blinn(Load8(ref cp, i), kk), Blinn(Load8(ref mp, i), kk), Blinn(Load8(ref yp, i), kk), bgr));
        }

        return i;
    }

    internal static void RgbRowScalar(Span<byte> dst, ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, int from, int n, bool bgr)
    {
        for (var i = from; i < n; ++i)
            MemoryMarshal.Write(dst.Slice(i * 4), Pack(r[i], g[i], b[i], bgr));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int RgbRowV128(Span<byte> dst, ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, int n, bool bgr)
    {
        ref var rp = ref MemoryMarshal.GetReference(r);
        ref var gp = ref MemoryMarshal.GetReference(g);
        ref var bp = ref MemoryMarshal.GetReference(b);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Load8(ref rp, i, out var r0, out var r1);
            Load8(ref gp, i, out var g0, out var g1);
            Load8(ref bp, i, out var b0, out var b1);
            Store(ref d, i, Pack(r0, g0, b0, bgr));
            Store(ref d, i + 4, Pack(r1, g1, b1, bgr));
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int RgbRowV256(Span<byte> dst, ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, int n, bool bgr)
    {
        ref var rp = ref MemoryMarshal.GetReference(r);
        ref var gp = ref MemoryMarshal.GetReference(g);
        ref var bp = ref MemoryMarshal.GetReference(b);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var i = 0;
        for (; i + 8 <= n; i += 8)
            Store(ref d, i, Pack(Load8(ref rp, i), Load8(ref gp, i), Load8(ref bp, i), bgr));
        return i;
    }

    internal static void GrayRowScalar(Span<byte> dst, ReadOnlySpan<byte> y, int from, int n)
    {
        for (var i = from; i < n; ++i)
            MemoryMarshal.Write(dst.Slice(i * 4), y[i] * 0x010101 | Alpha);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int GrayRowV128(Span<byte> dst, ReadOnlySpan<byte> y, int n)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var mul = Vector128.Create(0x010101);
        var alpha = Vector128.Create(Alpha);
        var i = 0;
        for (; i + 8 <= n; i += 8)
        {
            Load8(ref yp, i, out var y0, out var y1);
            Store(ref d, i, y0 * mul | alpha);
            Store(ref d, i + 4, y1 * mul | alpha);
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int GrayRowV256(Span<byte> dst, ReadOnlySpan<byte> y, int n)
    {
        ref var yp = ref MemoryMarshal.GetReference(y);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var mul = Vector256.Create(0x010101);
        var alpha = Vector256.Create(Alpha);
        var i = 0;
        for (; i + 8 <= n; i += 8)
            Store(ref d, i, Load8(ref yp, i) * mul | alpha);
        return i;
    }

    // ================================================================ test hooks

    /// <summary>Pre-rounding r, g, b floats (interleaved, 3 per pixel) from each tier's core.</summary>
    internal static void YccFloats(int tier, ReadOnlySpan<byte> y, ReadOnlySpan<ushort> cb16, ReadOnlySpan<ushort> cr16, Span<float> rgb)
    {
        var n = y.Length;
        var i = 0;
        if (tier == 256)
        {
            Span<float> r = stackalloc float[8], g = stackalloc float[8], b = stackalloc float[8];
            for (; i + 8 <= n; i += 8)
            {
                var yv = Vector256.Create(Widen(y.Slice(i, 8)));
                var cbv = Vector256.Create(Widen(cb16.Slice(i, 8))) - Vector256.Create(2048);
                var crv = Vector256.Create(Widen(cr16.Slice(i, 8))) - Vector256.Create(2048);
                Ycc(Vector256.ConvertToSingle(yv), Vector256.ConvertToSingle(cbv), Vector256.ConvertToSingle(crv), out var rv, out var gv, out var bv);
                rv.CopyTo(r);
                gv.CopyTo(g);
                bv.CopyTo(b);
                for (var j = 0; j < 8; ++j)
                    (rgb[(i + j) * 3], rgb[(i + j) * 3 + 1], rgb[(i + j) * 3 + 2]) = (r[j], g[j], b[j]);
            }
        }
        else if (tier == 128)
        {
            Span<float> r = stackalloc float[4], g = stackalloc float[4], b = stackalloc float[4];
            for (; i + 4 <= n; i += 4)
            {
                var yv = Vector128.Create(Widen(y.Slice(i, 4)));
                var cbv = Vector128.Create(Widen(cb16.Slice(i, 4))) - Vector128.Create(2048);
                var crv = Vector128.Create(Widen(cr16.Slice(i, 4))) - Vector128.Create(2048);
                Ycc(Vector128.ConvertToSingle(yv), Vector128.ConvertToSingle(cbv), Vector128.ConvertToSingle(crv), out var rv, out var gv, out var bv);
                rv.CopyTo(r);
                gv.CopyTo(g);
                bv.CopyTo(b);
                for (var j = 0; j < 4; ++j)
                    (rgb[(i + j) * 3], rgb[(i + j) * 3 + 1], rgb[(i + j) * 3 + 2]) = (r[j], g[j], b[j]);
            }
        }

        for (; i < n; ++i)
        {
            Ycc(y[i], cb16[i] - 2048, cr16[i] - 2048, out var r, out var g, out var b);
            (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (r, g, b);
        }
    }

    private static int[] Widen(ReadOnlySpan<byte> s)
    {
        var a = new int[s.Length];
        for (var i = 0; i < s.Length; ++i)
            a[i] = s[i];
        return a;
    }

    private static int[] Widen(ReadOnlySpan<ushort> s)
    {
        var a = new int[s.Length];
        for (var i = 0; i < s.Length; ++i)
            a[i] = s[i];
        return a;
    }
}
