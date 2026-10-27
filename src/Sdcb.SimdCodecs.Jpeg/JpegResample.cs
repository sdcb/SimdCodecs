using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Row upsamplers for subsampled components. For 2× factors this is linear
/// interpolation at the true sample positions of JFIF's centred chroma siting
/// (T.871): an output sample sits ¼ of a source sample away from its nearest
/// source sample, so it is ¾·near + ¼·far, with the edge sample replicated.
/// Other factors fall back to nearest neighbour.
///
/// <para><see cref="Row16"/> is the exact result at 16× scale (integers, no
/// rounding at all) and is what feeds YCbCr → RGB conversion, so chroma is never
/// rounded to 8 bits between upsampling and colour conversion. <see cref="Row"/>
/// is the same value rounded half-up to 8 bits, <c>(Row16 + 8) &gt;&gt; 4</c>, for
/// components that are not colour-converted (RGB, CMYK, the K of YCCK).</para>
///
/// <para>Horizontal 2× (<see cref="H2"/>) is <see cref="Hv2"/> with the far row equal
/// to the near one: with <c>t = 3·near + far = 4·in</c>, <c>3·t0 + t1 = (3·in0 + in1)·4</c>.
/// All arithmetic is exact integer arithmetic, so the vector paths agree with the
/// scalar loops trivially; <c>JpegResampleEquivalenceTests</c> checks it anyway.</para>
/// </summary>
internal static class JpegResample
{
    public const int OneToOne = 0;
    public const int V2 = 1;
    public const int H2 = 2;
    public const int Hv2 = 3;
    public const int Generic = 4;

    /// <summary>Forces a tier in tests: 0 = auto, 1 = scalar, 128, 256.</summary>
    internal static int ForceTier;

    private static bool UseV256 => (ForceTier == 0 || ForceTier == 256) && Vector256.IsHardwareAccelerated && Avx2.IsSupported;
    private static bool UseV128 => ForceTier != 1 && Vector128.IsHardwareAccelerated;

    /// <summary>Output samples a kernel writes for <paramref name="w"/> low-resolution samples.</summary>
    public static int OutputCount(int kind, int w, int hs) => kind switch
    {
        OneToOne or V2 => w,
        H2 or Hv2 => 2 * w,
        _ => w * hs,
    };

    /// <summary>
    /// Runs the selected kernel to 8 bits: <see cref="Row16"/> into
    /// <paramref name="scratch"/>, then <c>(v + 8) &gt;&gt; 4</c>.
    /// <paramref name="inNear"/>/<paramref name="inFar"/> are row offsets into
    /// <paramref name="plane"/>; <paramref name="w"/> is the low-resolution sample
    /// count; <paramref name="hs"/> the horizontal expansion.
    /// </summary>
    public static void Row(int kind, Span<byte> dst, Span<ushort> scratch, ReadOnlySpan<byte> plane, int inNear, int inFar, int w, int hs)
    {
        Row16(kind, scratch, plane, inNear, inFar, w, hs);
        var n = OutputCount(kind, w, hs);
        if ((uint)n > (uint)dst.Length || (uint)n > (uint)scratch.Length)
            ThrowRange();
        ref var s = ref MemoryMarshal.GetReference(scratch);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var i = 0;
        if (UseV128)
        {
            var eight = Vector128.Create((ushort)8);
            for (; i + 16 <= n; i += 16)
            {
                var lo = Vector128.ShiftRightLogical(Vector128.LoadUnsafe(ref s, (nuint)i) + eight, 4);
                var hi = Vector128.ShiftRightLogical(Vector128.LoadUnsafe(ref s, (nuint)(i + 8)) + eight, 4);
                Vector128.Narrow(lo, hi).StoreUnsafe(ref d, (nuint)i);
            }
        }

        for (; i < n; ++i)
            Unsafe.Add(ref d, i) = (byte)((Unsafe.Add(ref s, i) + 8) >> 4);
    }

    /// <summary>
    /// Same interpolation as <see cref="Row"/>, unrounded, at 16× scale
    /// (0 … 4080). Writes <c>w × hs</c> samples (2w for the 2× kernels).
    /// </summary>
    public static void Row16(int kind, Span<ushort> dst, ReadOnlySpan<byte> plane, int inNear, int inFar, int w, int hs)
    {
        if (w <= 0 || (uint)inNear > (uint)plane.Length - (uint)w || (uint)inFar > (uint)plane.Length - (uint)w ||
            (uint)OutputCount(kind, w, hs) > (uint)dst.Length)
            ThrowRange();

        ref var near = ref Unsafe.Add(ref MemoryMarshal.GetReference(plane), inNear);
        ref var far = ref Unsafe.Add(ref MemoryMarshal.GetReference(plane), inFar);
        ref var d = ref MemoryMarshal.GetReference(dst);

        switch (kind)
        {
            case OneToOne:
            {
                var i = UseV256 ? OneToOneV256(ref d, ref near, w) : UseV128 ? OneToOneV128(ref d, ref near, w) : 0;
                for (; i < w; ++i)
                    Unsafe.Add(ref d, i) = (ushort)(Unsafe.Add(ref near, i) << 4);
                break;
            }

            case V2:
            {
                var i = UseV256 ? V2V256(ref d, ref near, ref far, w) : UseV128 ? V2V128(ref d, ref near, ref far, w) : 0;
                for (; i < w; ++i)
                    Unsafe.Add(ref d, i) = (ushort)((3 * Unsafe.Add(ref near, i) + Unsafe.Add(ref far, i)) << 2);
                break;
            }

            case H2:
                Hv2Row(ref d, ref near, ref near, w);
                break;

            case Hv2:
                Hv2Row(ref d, ref near, ref far, w);
                break;

            default:
                for (var i = 0; i < w; ++i)
                    for (var j = 0; j < hs; ++j)
                        Unsafe.Add(ref d, i * hs + j) = (ushort)(Unsafe.Add(ref near, i) << 4);
                break;
        }
    }

    private static void ThrowRange() => throw new ArgumentOutOfRangeException("w", "resample: row exceeds a buffer");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int T(ref byte near, ref byte far, int i) => 3 * Unsafe.Add(ref near, i) + Unsafe.Add(ref far, i);

    private static void Hv2Row(ref ushort d, ref byte near, ref byte far, int w)
    {
        var t1 = T(ref near, ref far, 0);
        if (w == 1)
        {
            d = Unsafe.Add(ref d, 1) = (ushort)(t1 << 2);
            return;
        }

        d = (ushort)(t1 << 2);
        var i = UseV256 ? Hv2V256(ref d, ref near, ref far, w) : UseV128 ? Hv2V128(ref d, ref near, ref far, w) : 1;
        for (; i < w; ++i)
        {
            var t0 = T(ref near, ref far, i - 1);
            t1 = T(ref near, ref far, i);
            Unsafe.Add(ref d, i * 2 - 1) = (ushort)(3 * t0 + t1);
            Unsafe.Add(ref d, i * 2) = (ushort)(3 * t1 + t0);
        }

        Unsafe.Add(ref d, w * 2 - 1) = (ushort)(T(ref near, ref far, w - 1) << 2);
    }

    // ------------------------------------------------------------------ Vector128

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Load8(ref byte p, int i) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i))).AsByte());

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int OneToOneV128(ref ushort d, ref byte near, int w)
    {
        var i = 0;
        for (; i + 8 <= w; i += 8)
            Vector128.ShiftLeft(Load8(ref near, i), 4).StoreUnsafe(ref d, (nuint)i);
        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int V2V128(ref ushort d, ref byte near, ref byte far, int w)
    {
        var three = Vector128.Create((ushort)3);
        var i = 0;
        for (; i + 8 <= w; i += 8)
            Vector128.ShiftLeft(Load8(ref near, i) * three + Load8(ref far, i), 2).StoreUnsafe(ref d, (nuint)i);
        return i;
    }

    /// <summary>Output pairs (2i−1, 2i) for i = 1 … ; returns the first i not done.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int Hv2V128(ref ushort d, ref byte near, ref byte far, int w)
    {
        var three = Vector128.Create((ushort)3);
        var i = 1;
        for (; i + 8 <= w; i += 8)
        {
            var tp = Load8(ref near, i - 1) * three + Load8(ref far, i - 1);
            var tc = Load8(ref near, i) * three + Load8(ref far, i);
            var a = tp * three + tc; // output 2i−1
            var b = tc * three + tp; // output 2i
            var lo = Vector128.WidenLower(a) | Vector128.ShiftLeft(Vector128.WidenLower(b), 16);
            var hi = Vector128.WidenUpper(a) | Vector128.ShiftLeft(Vector128.WidenUpper(b), 16);
            lo.AsUInt16().StoreUnsafe(ref d, (nuint)(2 * i - 1));
            hi.AsUInt16().StoreUnsafe(ref d, (nuint)(2 * i + 7));
        }

        return i;
    }

    // ------------------------------------------------------------------ Vector256 (AVX2)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> Load16(ref byte p, int i) =>
        Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref p, (nuint)i)).AsUInt16();

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int OneToOneV256(ref ushort d, ref byte near, int w)
    {
        var i = 0;
        for (; i + 16 <= w; i += 16)
            Vector256.ShiftLeft(Load16(ref near, i), 4).StoreUnsafe(ref d, (nuint)i);
        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int V2V256(ref ushort d, ref byte near, ref byte far, int w)
    {
        var three = Vector256.Create((ushort)3);
        var i = 0;
        for (; i + 16 <= w; i += 16)
            Vector256.ShiftLeft(Load16(ref near, i) * three + Load16(ref far, i), 2).StoreUnsafe(ref d, (nuint)i);
        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int Hv2V256(ref ushort d, ref byte near, ref byte far, int w)
    {
        var three = Vector256.Create((ushort)3);
        var i = 1;
        for (; i + 16 <= w; i += 16)
        {
            var tp = Load16(ref near, i - 1) * three + Load16(ref far, i - 1);
            var tc = Load16(ref near, i) * three + Load16(ref far, i);
            var a = tp * three + tc;
            var b = tc * three + tp;
            var lo = Avx2.UnpackLow(a, b);  // pairs 0-3 | 8-11
            var hi = Avx2.UnpackHigh(a, b); // pairs 4-7 | 12-15
            Avx2.Permute2x128(lo, hi, 0x20).StoreUnsafe(ref d, (nuint)(2 * i - 1));
            Avx2.Permute2x128(lo, hi, 0x31).StoreUnsafe(ref d, (nuint)(2 * i + 15));
        }

        return i;
    }
}
