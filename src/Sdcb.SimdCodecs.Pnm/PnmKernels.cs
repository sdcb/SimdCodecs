using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// The row kernels behind Netpbm decode and encode. Each runs a 256-bit loop, then a 128-bit one, then a scalar
/// tail, and produces the same bytes on every tier: the <c>tier</c> parameter caps it, so the tests can hold
/// each tier against the scalar one.
/// <para>
/// Bounds: every kernel checks up front that its spans hold <c>count</c> elements; a vector step runs only
/// while its whole load and store lie inside them (each loop condition says which).
/// </para>
/// </summary>
internal static class PnmKernels
{
    public const int Scalar = 0;
    public const int Vector128Tier = 1;
    public const int Vector256Tier = 2;

    /// <summary>The fastest tier on this machine.</summary>
    public static int BestTier =>
        !BitConverter.IsLittleEndian ? Scalar
        : Vector256.IsHardwareAccelerated ? Vector256Tier
        : Vector128.IsHardwareAccelerated ? Vector128Tier
        : Scalar;

    /// <summary>
    /// The high byte of each big-endian 16-bit sample — the first of its two — as an 8-bit sample:
    /// <paramref name="count"/> samples from <c>2 × count</c> bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void HighBytes(ReadOnlySpan<byte> source, Span<byte> destination, int count, int tier)
    {
        if (source.Length < 2L * count || destination.Length < count)
            throw new ArgumentException("The spans are shorter than the samples.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var i = 0;
        // Narrowing truncates each 16-bit lane to its low byte, which on a little-endian load is the first byte.
        if (tier >= Vector256Tier && Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            // Loads 64 bytes at 2i, stores 32 at i: i + 32 <= count.
            for (; i + 32 <= count; i += 32)
            {
                var a = Vector256.LoadUnsafe(ref s, (nuint)(2 * i)).AsUInt16();
                var b = Vector256.LoadUnsafe(ref s, (nuint)(2 * i + 32)).AsUInt16();
                Vector256.Narrow(a, b).StoreUnsafe(ref d, (nuint)i);
            }
        }
        if (tier >= Vector128Tier && Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            // Loads 32 bytes at 2i, stores 16 at i: i + 16 <= count.
            for (; i + 16 <= count; i += 16)
            {
                var a = Vector128.LoadUnsafe(ref s, (nuint)(2 * i)).AsUInt16();
                var b = Vector128.LoadUnsafe(ref s, (nuint)(2 * i + 16)).AsUInt16();
                Vector128.Narrow(a, b).StoreUnsafe(ref d, (nuint)i);
            }
        }
        for (; i < count; i++)
            Unsafe.Add(ref d, i) = Unsafe.Add(ref s, 2 * i);
    }

    /// <summary>
    /// PBM bits to 8-bit grey, most significant bit first, a set bit (black) to 0 and a clear one to 255:
    /// <paramref name="count"/> pixels from <c>ceil(count / 8)</c> bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void ExpandBits(ReadOnlySpan<byte> source, Span<byte> destination, int count, int tier)
    {
        if (source.Length < (count + 7L) / 8 || destination.Length < count)
            throw new ArgumentException("The spans are shorter than the pixels.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var i = 0;
        if (tier >= Vector128Tier && Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            // Two source bytes become 16 pixels: each byte is broadcast across 8 lanes, and lane k tests bit 7 − k.
            var spread = Vector128.Create((byte)0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1);
            var bits = Vector128.Create((byte)0x80, 0x40, 0x20, 0x10, 8, 4, 2, 1, 0x80, 0x40, 0x20, 0x10, 8, 4, 2, 1);
            // Reads source bytes i/8 and i/8 + 1, stores 16 at i: i + 16 <= count.
            for (; i + 16 <= count; i += 16)
            {
                var pair = Vector128.Create(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref s, i >> 3))).AsByte();
                var v = Vector128.Shuffle(pair, spread);
                Vector128.Equals(v & bits, Vector128<byte>.Zero).StoreUnsafe(ref d, (nuint)i);
            }
        }
        for (; i < count; i++)
            Unsafe.Add(ref d, i) = (Unsafe.Add(ref s, i >> 3) & (0x80 >> (i & 7))) != 0 ? (byte)0 : (byte)255;
    }

    /// <summary>
    /// 8-bit grey to PBM bits, most significant bit first: a sample below 128 is black (a set bit). The last
    /// byte's unused bits are 0. <paramref name="count"/> pixels into <c>ceil(count / 8)</c> bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void PackBits(ReadOnlySpan<byte> source, Span<byte> destination, int count, int tier)
    {
        if (source.Length < count || destination.Length < (count + 7L) / 8)
            throw new ArgumentException("The spans are shorter than the pixels.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var i = 0;
        if (tier >= Vector128Tier && Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            // An inverted sample's top bit is set for 0..127. Reversing each group of 8 lanes puts pixel 0 at lane
            // 7, so ExtractMostSignificantBits yields each byte most significant bit first.
            // Loads 16 bytes at i (i + 16 <= count), stores 2 at i / 8.
            var reverse = Vector128.Create((byte)7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8);
            for (; i + 16 <= count; i += 16)
            {
                var mask = Vector128.Shuffle(~Vector128.LoadUnsafe(ref s, (nuint)i), reverse).ExtractMostSignificantBits();
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i >> 3), (ushort)mask);
            }
        }
        for (; i < count; i += 8)
        {
            var n = Math.Min(8, count - i);
            var b = 0;
            for (var k = 0; k < n; k++)
                if (Unsafe.Add(ref s, i + k) < 128) b |= 0x80 >> k;
            Unsafe.Add(ref d, i >> 3) = (byte)b;
        }
    }
}
