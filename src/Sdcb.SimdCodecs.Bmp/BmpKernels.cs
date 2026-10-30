using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// The row kernels behind BMP decode and encode: byte-order changes between the stored layouts (B, G, R
/// triples; 32-bit words; 16-bit 5-5-5 / 5-6-5) and the caller's. Each kernel runs a 256-bit loop (AVX2),
/// then a 128-bit one (SSSE3 / AdvSimd), then a scalar tail, and produces the same bytes on every tier: the
/// <c>tier</c> parameter caps it, so the tests can hold each tier against the scalar one.
/// <para>
/// Bounds: every kernel takes spans and checks up front that they hold <c>width</c> pixels; a vector step
/// runs only while its whole load and store lie inside the spans (each loop condition says which), so the
/// unchecked loads and stores never leave them.
/// </para>
/// </summary>
internal static class BmpKernels
{
    public const int Scalar = 0;
    public const int Vector128Tier = 1;
    public const int Vector256Tier = 2;

    private const byte Z = 0x80; // a shuffle index that yields 0, for both pshufb and TBL

    /// <summary>Byte shuffles need a table lookup with zeroing: SSSE3 pshufb or arm64 TBL.</summary>
    private static bool CanShuffle128 => BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

    /// <summary>The fastest tier for the byte-shuffle kernels on this machine.</summary>
    public static int BestTier => Avx2.IsSupported ? Vector256Tier : CanShuffle128 ? Vector128Tier : Scalar;

    /// <summary>The fastest tier for the arithmetic kernels (<see cref="Expand16"/>), which need no shuffle.</summary>
    public static int BestArithmeticTier =>
        !BitConverter.IsLittleEndian ? Scalar
        : Vector256.IsHardwareAccelerated ? Vector256Tier
        : Vector128.IsHardwareAccelerated ? Vector128Tier
        : Scalar;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> mask) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(value, mask) : AdvSimd.Arm64.VectorTableLookup(value, mask);

    /// <summary>
    /// Three bytes per pixel to four, alpha 255: <c>B, G, R → R, G, B, 255</c> when <paramref name="swap"/>,
    /// else <c>B, G, R → B, G, R, 255</c>. (Symmetrically, R, G, B in gives B, G, R, 255 with <paramref name="swap"/>.)
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Expand24(ReadOnlySpan<byte> source, Span<byte> destination, int width, bool swap, int tier)
    {
        if (source.Length < 3L * width || destination.Length < 4L * width)
            throw new ArgumentException("The spans are shorter than the row.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        int x = 0, outer = swap ? 2 : 0;

        if (tier >= Vector256Tier && Avx2.IsSupported)
        {
            // Lane 0 takes pixels 0-3 (bytes 0-11), lane 1 pixels 4-7 (bytes 12-23): a dword permute moves
            // bytes 12-27 into lane 1, then one in-lane shuffle expands both.
            var lanes = Vector256.Create(0, 1, 2, 3, 3, 4, 5, 6);
            var mask = Vector256.Create(Mask24To32(outer));
            var alpha = Vector256.Create(0xFF000000u).AsByte();
            // Loads 32 bytes at 3x (needs 3x + 32 <= source.Length); stores 32 at 4x (x + 8 <= width).
            for (; x + 8 <= width && 3L * x + 32 <= source.Length; x += 8)
            {
                var v = Vector256.LoadUnsafe(ref s, (nuint)(3 * x));
                v = Avx2.PermuteVar8x32(v.AsInt32(), lanes).AsByte();
                (Avx2.Shuffle(v, mask) | alpha).StoreUnsafe(ref d, (nuint)(4 * x));
            }
        }
        if (tier >= Vector128Tier && CanShuffle128)
        {
            var mask = Mask24To32(outer);
            var alpha = Vector128.Create(0xFF000000u).AsByte();
            // Loads 16 bytes at 3x (3x + 16 <= source.Length); stores 16 at 4x (x + 4 <= width).
            for (; x + 4 <= width && 3L * x + 16 <= source.Length; x += 4)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)(3 * x));
                (Shuffle(v, mask) | alpha).StoreUnsafe(ref d, (nuint)(4 * x));
            }
        }
        for (; x < width; x++)
        {
            destination[4 * x] = source[3 * x + outer];
            destination[4 * x + 1] = source[3 * x + 1];
            destination[4 * x + 2] = source[3 * x + 2 - outer];
            destination[4 * x + 3] = 255;
        }
    }

    // Pixel i's bytes 3i..3i+2 to 4i..4i+2, the first from 3i + outer and the third from 3i + 2 - outer.
    private static Vector128<byte> Mask24To32(int outer)
    {
        Span<byte> m = stackalloc byte[16];
        for (var i = 0; i < 4; i++)
        {
            m[4 * i] = (byte)(3 * i + outer);
            m[4 * i + 1] = (byte)(3 * i + 1);
            m[4 * i + 2] = (byte)(3 * i + 2 - outer);
            m[4 * i + 3] = Z;
        }
        return Vector128.Create((ReadOnlySpan<byte>)m);
    }

    /// <summary>
    /// Four bytes per pixel to four, rearranged: output byte <c>k</c> of each pixel is source byte
    /// <c>control[k]</c> (0..3), or 0 for <see cref="Z"/>; then <paramref name="set"/> is ORed into each pixel
    /// (little-endian, so 0xFF000000 sets the fourth byte).
    /// </summary>
    /// <returns>Every output pixel ORed together, so a caller can tell whether a byte was ever non-zero.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static uint Shuffle32(ReadOnlySpan<byte> source, Span<byte> destination, int width, ReadOnlySpan<byte> control,
        uint set, int tier)
    {
        if (source.Length < 4L * width || destination.Length < 4L * width || control.Length < 4)
            throw new ArgumentException("The spans are shorter than the row.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var x = 0;
        uint seen = 0;

        if (tier >= Vector256Tier && Avx2.IsSupported)
        {
            var mask = Vector256.Create(Mask32(control));
            var or = Vector256.Create(set).AsByte();
            var acc = Vector256<byte>.Zero;
            // Loads and stores 32 bytes at 4x: x + 8 <= width.
            for (; x + 8 <= width; x += 8)
            {
                var v = Avx2.Shuffle(Vector256.LoadUnsafe(ref s, (nuint)(4 * x)), mask) | or;
                acc |= v;
                v.StoreUnsafe(ref d, (nuint)(4 * x));
            }
            var a = acc.AsUInt32();
            for (var i = 0; i < Vector256<uint>.Count; i++) seen |= a.GetElement(i);
        }
        if (tier >= Vector128Tier && CanShuffle128)
        {
            var mask = Mask32(control);
            var or = Vector128.Create(set).AsByte();
            var acc = Vector128<byte>.Zero;
            // Loads and stores 16 bytes at 4x: x + 4 <= width.
            for (; x + 4 <= width; x += 4)
            {
                var v = Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(4 * x)), mask) | or;
                acc |= v;
                v.StoreUnsafe(ref d, (nuint)(4 * x));
            }
            var a = acc.AsUInt32();
            seen |= a.GetElement(0) | a.GetElement(1) | a.GetElement(2) | a.GetElement(3);
        }
        for (; x < width; x++)
        {
            uint pixel = 0;
            for (var k = 0; k < 4; k++)
            {
                if (control[k] < 4) pixel |= (uint)source[4 * x + control[k]] << (8 * k);
            }
            pixel |= set;
            seen |= pixel;
            destination[4 * x] = (byte)pixel;
            destination[4 * x + 1] = (byte)(pixel >> 8);
            destination[4 * x + 2] = (byte)(pixel >> 16);
            destination[4 * x + 3] = (byte)(pixel >> 24);
        }
        return seen;
    }

    private static Vector128<byte> Mask32(ReadOnlySpan<byte> control)
    {
        Span<byte> m = stackalloc byte[16];
        for (var i = 0; i < 16; i++)
        {
            var c = control[i & 3];
            m[i] = c < 4 ? (byte)((i & ~3) + c) : Z;
        }
        return Vector128.Create((ReadOnlySpan<byte>)m);
    }

    /// <summary>Four bytes per pixel to three: output byte <c>k</c> of each pixel is source byte <c>control[k]</c> (0..3).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Pack32To24(ReadOnlySpan<byte> source, Span<byte> destination, int width, ReadOnlySpan<byte> control, int tier)
    {
        if (source.Length < 4L * width || destination.Length < 3L * width || control.Length < 3)
            throw new ArgumentException("The spans are shorter than the row.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var x = 0;
        int c0 = control[0], c1 = control[1], c2 = control[2];

        if (tier >= Vector256Tier && Avx2.IsSupported)
        {
            // Each lane packs its four pixels into its low 12 bytes; a dword permute closes the gap.
            var mask = Vector256.Create(Mask32To24(c0, c1, c2));
            var lanes = Vector256.Create(0, 1, 2, 4, 5, 6, 7, 7);
            // Loads 32 bytes at 4x (x + 8 <= width); stores 32 at 3x (3x + 32 <= destination.Length): the last
            // 8 bytes of the store are overwritten by the next step or the scalar tail.
            for (; x + 8 <= width && 3L * x + 32 <= destination.Length; x += 8)
            {
                var v = Avx2.Shuffle(Vector256.LoadUnsafe(ref s, (nuint)(4 * x)), mask);
                Avx2.PermuteVar8x32(v.AsInt32(), lanes).AsByte().StoreUnsafe(ref d, (nuint)(3 * x));
            }
        }
        if (tier >= Vector128Tier && CanShuffle128)
        {
            var mask = Mask32To24(c0, c1, c2);
            // Loads 16 bytes at 4x (x + 4 <= width); stores 16 at 3x (3x + 16 <= destination.Length), the last
            // 4 rewritten by the next step or the scalar tail.
            for (; x + 4 <= width && 3L * x + 16 <= destination.Length; x += 4)
                Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(4 * x)), mask).StoreUnsafe(ref d, (nuint)(3 * x));
        }
        for (; x < width; x++)
        {
            destination[3 * x] = source[4 * x + c0];
            destination[3 * x + 1] = source[4 * x + c1];
            destination[3 * x + 2] = source[4 * x + c2];
        }
    }

    private static Vector128<byte> Mask32To24(int c0, int c1, int c2)
    {
        Span<byte> m = stackalloc byte[16];
        m.Fill(Z);
        for (var i = 0; i < 4; i++)
        {
            m[3 * i] = (byte)(4 * i + c0);
            m[3 * i + 1] = (byte)(4 * i + c1);
            m[3 * i + 2] = (byte)(4 * i + c2);
        }
        return Vector128.Create((ReadOnlySpan<byte>)m);
    }

    /// <summary>Three bytes per pixel, the first and third swapped (B, G, R ↔ R, G, B).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Swap24(ReadOnlySpan<byte> source, Span<byte> destination, int width, int tier)
    {
        if (source.Length < 3L * width || destination.Length < 3L * width)
            throw new ArgumentException("The spans are shorter than the row.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var x = 0;

        if (tier >= Vector256Tier && Avx2.IsSupported)
        {
            var spread = Vector256.Create(0, 1, 2, 3, 3, 4, 5, 6);
            var close = Vector256.Create(0, 1, 2, 4, 5, 6, 7, 7);
            var mask = Vector256.Create(MaskSwap24());
            // Loads 32 bytes at 3x (3x + 32 <= source.Length); stores 32 at 3x (3x + 32 <= destination.Length),
            // the last 8 rewritten by the next step or the scalar tail.
            for (; x + 8 <= width && 3L * x + 32 <= source.Length && 3L * x + 32 <= destination.Length; x += 8)
            {
                var v = Avx2.PermuteVar8x32(Vector256.LoadUnsafe(ref s, (nuint)(3 * x)).AsInt32(), spread).AsByte();
                Avx2.PermuteVar8x32(Avx2.Shuffle(v, mask).AsInt32(), close).AsByte().StoreUnsafe(ref d, (nuint)(3 * x));
            }
        }
        if (tier >= Vector128Tier && CanShuffle128)
        {
            var mask = MaskSwap24();
            // Loads and stores 16 bytes at 3x (3x + 16 within both spans), the last 4 rewritten later.
            for (; x + 4 <= width && 3L * x + 16 <= source.Length && 3L * x + 16 <= destination.Length; x += 4)
                Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(3 * x)), mask).StoreUnsafe(ref d, (nuint)(3 * x));
        }
        for (; x < width; x++)
        {
            var b = source[3 * x];
            destination[3 * x + 1] = source[3 * x + 1];
            destination[3 * x] = source[3 * x + 2];
            destination[3 * x + 2] = b;
        }
    }

    private static Vector128<byte> MaskSwap24()
    {
        Span<byte> m = stackalloc byte[16];
        for (var i = 0; i < 16; i++) m[i] = Z;
        for (var i = 0; i < 4; i++)
        {
            m[3 * i] = (byte)(3 * i + 2);
            m[3 * i + 1] = (byte)(3 * i + 1);
            m[3 * i + 2] = (byte)(3 * i);
        }
        return Vector128.Create((ReadOnlySpan<byte>)m);
    }

    /// <summary>
    /// 16-bit 5-6-5 (<paramref name="green6"/>) or x-5-5-5 pixels to four bytes, alpha 255, each channel widened
    /// by repeating its bits (<c>v &lt;&lt; 3 | v &gt;&gt; 2</c> for 5 bits): R, G, B, 255 with <paramref name="rgba"/>,
    /// else B, G, R, 255.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void Expand16(ReadOnlySpan<byte> source, Span<byte> destination, int width, bool green6, bool rgba, int tier)
    {
        if (source.Length < 2L * width || destination.Length < 4L * width)
            throw new ArgumentException("The spans are shorter than the row.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var x = 0;
        int redShift = green6 ? 11 : 10, greenBits = green6 ? 6 : 5;

        if (tier >= Vector256Tier && Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            // Loads 32 bytes at 2x and stores 64 at 4x: x + 16 <= width.
            for (; x + 16 <= width; x += 16)
            {
                var p = Vector256.LoadUnsafe(ref s, (nuint)(2 * x)).AsUInt16();
                var (lo, hi) = Spread565(p, redShift, greenBits, rgba);
                (Vector256.WidenLower(lo) | (Vector256.WidenLower(hi) << 16)).AsByte().StoreUnsafe(ref d, (nuint)(4 * x));
                (Vector256.WidenUpper(lo) | (Vector256.WidenUpper(hi) << 16)).AsByte().StoreUnsafe(ref d, (nuint)(4 * x + 32));
            }
        }
        if (tier >= Vector128Tier && Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            // Loads 16 bytes at 2x and stores 32 at 4x: x + 8 <= width.
            for (; x + 8 <= width; x += 8)
            {
                var p = Vector128.LoadUnsafe(ref s, (nuint)(2 * x)).AsUInt16();
                var (lo, hi) = Spread565(p, redShift, greenBits, rgba);
                (Vector128.WidenLower(lo) | (Vector128.WidenLower(hi) << 16)).AsByte().StoreUnsafe(ref d, (nuint)(4 * x));
                (Vector128.WidenUpper(lo) | (Vector128.WidenUpper(hi) << 16)).AsByte().StoreUnsafe(ref d, (nuint)(4 * x + 16));
            }
        }
        for (; x < width; x++)
        {
            var p = source[2 * x] | source[2 * x + 1] << 8;
            int r = (p >> redShift) & 31, g = (p >> 5) & ((1 << greenBits) - 1), b = p & 31;
            r = (r << 3) | (r >> 2);
            g = green6 ? (g << 2) | (g >> 4) : (g << 3) | (g >> 2);
            b = (b << 3) | (b >> 2);
            destination[4 * x] = (byte)(rgba ? r : b);
            destination[4 * x + 1] = (byte)g;
            destination[4 * x + 2] = (byte)(rgba ? b : r);
            destination[4 * x + 3] = 255;
        }
    }

    // The widened channels as two 16-bit planes: first | second << 8, and third | 0xFF00.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector128<ushort> Low, Vector128<ushort> High) Spread565(Vector128<ushort> p, int redShift, int greenBits, bool rgba)
    {
        var five = Vector128.Create((ushort)31);
        var r = (p >> redShift) & five;
        var g = (p >> 5) & Vector128.Create((ushort)((1 << greenBits) - 1));
        var b = p & five;
        r = (r << 3) | (r >> 2);
        g = greenBits == 6 ? (g << 2) | (g >> 4) : (g << 3) | (g >> 2);
        b = (b << 3) | (b >> 2);
        var (first, third) = rgba ? (r, b) : (b, r);
        return (first | (g << 8), third | Vector128.Create((ushort)0xFF00));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector256<ushort> Low, Vector256<ushort> High) Spread565(Vector256<ushort> p, int redShift, int greenBits, bool rgba)
    {
        var five = Vector256.Create((ushort)31);
        var r = (p >> redShift) & five;
        var g = (p >> 5) & Vector256.Create((ushort)((1 << greenBits) - 1));
        var b = p & five;
        r = (r << 3) | (r >> 2);
        g = greenBits == 6 ? (g << 2) | (g >> 4) : (g << 3) | (g >> 2);
        b = (b << 3) | (b >> 2);
        var (first, third) = rgba ? (r, b) : (b, r);
        return (first | (g << 8), third | Vector256.Create((ushort)0xFF00));
    }
}
