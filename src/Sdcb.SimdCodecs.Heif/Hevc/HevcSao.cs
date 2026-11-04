using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// Row kernels of sample adaptive offset (§8.7.3.2) for the samples whose neighbours the caller has proved
/// usable: one run of samples of one row, read from the deblocked picture and written to the output.
/// </summary>
/// <remarks>
/// Samples are at most 10 bits and |SaoOffsetVal| ≤ 31, so sample + offset fits a signed 16-bit lane. Each vector kernel has a scalar twin and all tiers write the same
/// samples; <c>HeifEquivalenceTests</c> checks them.
/// </remarks>
internal static class HevcSao
{
    /// <summary>
    /// Edge offset (8-411 .. 8-413): edgeIdx = 2 + Sign(v − a) + Sign(v − b), remapped so that a local minimum takes
    /// <paramref name="offsets"/>[0] (SaoOffsetVal[1]) through a local maximum taking [3]; a flat sample keeps
    /// its value. <paramref name="a"/> and <paramref name="b"/> are the two neighbours of each sample, already
    /// offset by (hPos, vPos).
    /// </summary>
    internal static void EdgeRow(ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> dst,
        int count, ReadOnlySpan<int> offsets, int max)
    {
        Check(cur, a, b, dst, count, offsets);
        var done = 0;
        if (Vector256.IsHardwareAccelerated && count >= 16) done = EdgeRowVector256(cur, a, b, dst, count, offsets, max);
        else if (Vector128.IsHardwareAccelerated && count >= 8) done = EdgeRowVector128(cur, a, b, dst, count, offsets, max);
        EdgeRowScalar(cur, a, b, dst, done, count, offsets, max);
    }

    /// <summary>Band offset (8.7.3.2 steps 3 .. 5): the four bands starting at <paramref name="bandPosition"/> take the four offsets.</summary>
    internal static void BandRow(ReadOnlySpan<ushort> cur, Span<ushort> dst, int count, int bandPosition, int bitDepth,
        ReadOnlySpan<int> offsets)
    {
        Check(cur, cur, cur, dst, count, offsets);
        var done = 0;
        if (Vector256.IsHardwareAccelerated && count >= 16) done = BandRowVector256(cur, dst, count, bandPosition, bitDepth, offsets);
        else if (Vector128.IsHardwareAccelerated && count >= 8) done = BandRowVector128(cur, dst, count, bandPosition, bitDepth, offsets);
        BandRowScalar(cur, dst, done, count, bandPosition, bitDepth, offsets);
    }

    /// <summary>The proof for the unchecked accesses of the vector tiers: every span holds <paramref name="count"/> samples.</summary>
    private static void Check(ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> dst, int count, ReadOnlySpan<int> offsets)
    {
        if (count < 0 || cur.Length < count || a.Length < count || b.Length < count || dst.Length < count || offsets.Length < 4)
        {
            throw new ArgumentException("The SAO row does not fit its buffers.");
        }
    }

    internal static void EdgeRowScalar(ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> dst,
        int start, int count, ReadOnlySpan<int> offsets, int max)
    {
        for (var i = start; i < count; i++)
        {
            int v = cur[i];
            var offset = (Math.Sign(v - a[i]) + Math.Sign(v - b[i])) switch
            {
                -2 => offsets[0],
                -1 => offsets[1],
                1 => offsets[2],
                2 => offsets[3],
                _ => 0,
            };
            dst[i] = (ushort)Math.Clamp(v + offset, 0, max);
        }
    }

    internal static void BandRowScalar(ReadOnlySpan<ushort> cur, Span<ushort> dst, int start, int count, int bandPosition, int bitDepth,
        ReadOnlySpan<int> offsets)
    {
        var shift = bitDepth - 5;
        var max = (1 << bitDepth) - 1;
        for (var i = start; i < count; i++)
        {
            int v = cur[i];
            var k = ((v >> shift) - bandPosition) & 31;
            if (k < 4) dst[i] = (ushort)Math.Clamp(v + offsets[k], 0, max);
            else dst[i] = (ushort)v;
        }
    }

    /// <summary>Vector128 tier of <see cref="EdgeRow"/>; returns the samples done (a multiple of 8).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int EdgeRowVector128(ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> dst,
        int count, ReadOnlySpan<int> offsets, int max)
    {
        Check(cur, a, b, dst, count, offsets);
        // i + 8 <= count <= every span's length (Check): the loads and stores below are in range.
        ref var pc = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(cur));
        ref var pa = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(a));
        ref var pb = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(b));
        ref var pd = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(dst));
        var o0 = Vector128.Create((short)offsets[0]);
        var o1 = Vector128.Create((short)offsets[1]);
        var o2 = Vector128.Create((short)offsets[2]);
        var o3 = Vector128.Create((short)offsets[3]);
        var vmax = Vector128.Create((short)max);
        var two = Vector128.Create((short)2);
        var one = Vector128<short>.One;
        var i = 0;
        for (; i <= count - 8; i += 8)
        {
            var v = Vector128.LoadUnsafe(ref pc, (nuint)i);
            var va = Vector128.LoadUnsafe(ref pa, (nuint)i);
            var vb = Vector128.LoadUnsafe(ref pb, (nuint)i);
            // Comparison masks are −1 where true: Sign(v − n) = lt − gt.
            var s = Vector128.LessThan(v, va) + Vector128.LessThan(v, vb) - Vector128.GreaterThan(v, va) - Vector128.GreaterThan(v, vb);
            var offset = (Vector128.Equals(s, -two) & o0) | (Vector128.Equals(s, -one) & o1) |
                         (Vector128.Equals(s, one) & o2) | (Vector128.Equals(s, two) & o3);
            Vector128.Min(Vector128.Max(v + offset, Vector128<short>.Zero), vmax).StoreUnsafe(ref pd, (nuint)i);
        }
        return i;
    }

    /// <summary>Vector256 tier of <see cref="EdgeRow"/>; returns the samples done (a multiple of 16).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int EdgeRowVector256(ReadOnlySpan<ushort> cur, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, Span<ushort> dst,
        int count, ReadOnlySpan<int> offsets, int max)
    {
        Check(cur, a, b, dst, count, offsets);
        // i + 16 <= count <= every span's length (Check): the loads and stores below are in range.
        ref var pc = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(cur));
        ref var pa = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(a));
        ref var pb = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(b));
        ref var pd = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(dst));
        var o0 = Vector256.Create((short)offsets[0]);
        var o1 = Vector256.Create((short)offsets[1]);
        var o2 = Vector256.Create((short)offsets[2]);
        var o3 = Vector256.Create((short)offsets[3]);
        var vmax = Vector256.Create((short)max);
        var two = Vector256.Create((short)2);
        var one = Vector256<short>.One;
        var i = 0;
        for (; i <= count - 16; i += 16)
        {
            var v = Vector256.LoadUnsafe(ref pc, (nuint)i);
            var va = Vector256.LoadUnsafe(ref pa, (nuint)i);
            var vb = Vector256.LoadUnsafe(ref pb, (nuint)i);
            var s = Vector256.LessThan(v, va) + Vector256.LessThan(v, vb) - Vector256.GreaterThan(v, va) - Vector256.GreaterThan(v, vb);
            var offset = (Vector256.Equals(s, -two) & o0) | (Vector256.Equals(s, -one) & o1) |
                         (Vector256.Equals(s, one) & o2) | (Vector256.Equals(s, two) & o3);
            Vector256.Min(Vector256.Max(v + offset, Vector256<short>.Zero), vmax).StoreUnsafe(ref pd, (nuint)i);
        }
        return i;
    }

    /// <summary>Vector128 tier of <see cref="BandRow"/>; returns the samples done (a multiple of 8).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int BandRowVector128(ReadOnlySpan<ushort> cur, Span<ushort> dst, int count, int bandPosition, int bitDepth,
        ReadOnlySpan<int> offsets)
    {
        Check(cur, cur, cur, dst, count, offsets);
        // i + 8 <= count <= both spans' lengths (Check): the loads and stores below are in range.
        ref var pc = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(cur));
        ref var pd = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(dst));
        var shift = bitDepth - 5;
        var o0 = Vector128.Create((short)offsets[0]);
        var o1 = Vector128.Create((short)offsets[1]);
        var o2 = Vector128.Create((short)offsets[2]);
        var o3 = Vector128.Create((short)offsets[3]);
        var vmax = Vector128.Create((short)((1 << bitDepth) - 1));
        var band = Vector128.Create((short)bandPosition);
        var mask = Vector128.Create((short)31);
        var i = 0;
        for (; i <= count - 8; i += 8)
        {
            var v = Vector128.LoadUnsafe(ref pc, (nuint)i);
            var k = ((v >> shift) - band) & mask;
            var offset = (Vector128.Equals(k, Vector128<short>.Zero) & o0) | (Vector128.Equals(k, Vector128<short>.One) & o1) |
                         (Vector128.Equals(k, Vector128.Create((short)2)) & o2) | (Vector128.Equals(k, Vector128.Create((short)3)) & o3);
            Vector128.Min(Vector128.Max(v + offset, Vector128<short>.Zero), vmax).StoreUnsafe(ref pd, (nuint)i);
        }
        return i;
    }

    /// <summary>Vector256 tier of <see cref="BandRow"/>; returns the samples done (a multiple of 16).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int BandRowVector256(ReadOnlySpan<ushort> cur, Span<ushort> dst, int count, int bandPosition, int bitDepth,
        ReadOnlySpan<int> offsets)
    {
        Check(cur, cur, cur, dst, count, offsets);
        // i + 16 <= count <= both spans' lengths (Check): the loads and stores below are in range.
        ref var pc = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(cur));
        ref var pd = ref Unsafe.As<ushort, short>(ref MemoryMarshal.GetReference(dst));
        var shift = bitDepth - 5;
        var o0 = Vector256.Create((short)offsets[0]);
        var o1 = Vector256.Create((short)offsets[1]);
        var o2 = Vector256.Create((short)offsets[2]);
        var o3 = Vector256.Create((short)offsets[3]);
        var vmax = Vector256.Create((short)((1 << bitDepth) - 1));
        var band = Vector256.Create((short)bandPosition);
        var mask = Vector256.Create((short)31);
        var i = 0;
        for (; i <= count - 16; i += 16)
        {
            var v = Vector256.LoadUnsafe(ref pc, (nuint)i);
            var k = ((v >> shift) - band) & mask;
            var offset = (Vector256.Equals(k, Vector256<short>.Zero) & o0) | (Vector256.Equals(k, Vector256<short>.One) & o1) |
                         (Vector256.Equals(k, Vector256.Create((short)2)) & o2) | (Vector256.Equals(k, Vector256.Create((short)3)) & o3);
            Vector256.Min(Vector256.Max(v + offset, Vector256<short>.Zero), vmax).StoreUnsafe(ref pd, (nuint)i);
        }
        return i;
    }
}
