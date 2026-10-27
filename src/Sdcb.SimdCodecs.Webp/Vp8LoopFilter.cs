using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// RFC 6386 §15 loop filter on 16 byte lanes. Every edge is presented as eight vectors p3..q3 of
/// 16 pixels: a horizontal luma edge directly, a vertical luma edge through a 16×8 transpose, and
/// the U and V edges of one macroblock side by side (8 + 8 lanes). The arithmetic works on the
/// signed bytes x ^ 0x80 with saturating adds, which is RFC 6386's c() clamp: for |x| ≤ 127,
/// c(x + 3d) equals three saturating additions of the saturated d (once a partial sum saturates in
/// d's direction the exact sum is past the same bound). Masks are computed on the unsigned bytes.
/// </summary>
internal static class Vp8LoopFilter
{
    public enum Kind
    {
        Simple,
        Inner,
        Macroblock,
    }

    /// <summary>The 16×8 transposes need two-input byte interleaves.</summary>
    public static bool IsSupported => Vector128.IsHardwareAccelerated && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void LumaHorizontal(ref byte q0Row, int stride, Kind kind, int edge, int interior, int hev)
    {
        ref byte p0Row = ref Unsafe.Subtract(ref q0Row, stride);
        if (kind == Kind.Simple)
        {
            Vector128<byte> sp1 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref p0Row, stride));
            Vector128<byte> sp0 = Vector128.LoadUnsafe(ref p0Row);
            Vector128<byte> sq0 = Vector128.LoadUnsafe(ref q0Row);
            Vector128<byte> sq1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref q0Row, stride));
            FilterSimple(sp1, ref sp0, ref sq0, sq1, edge);
            sp0.StoreUnsafe(ref p0Row);
            sq0.StoreUnsafe(ref q0Row);
            return;
        }

        Vector128<byte> p3 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref q0Row, 4 * stride));
        Vector128<byte> p2 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref q0Row, 3 * stride));
        Vector128<byte> p1 = Vector128.LoadUnsafe(ref Unsafe.Subtract(ref q0Row, 2 * stride));
        Vector128<byte> p0 = Vector128.LoadUnsafe(ref p0Row);
        Vector128<byte> q0 = Vector128.LoadUnsafe(ref q0Row);
        Vector128<byte> q1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref q0Row, stride));
        Vector128<byte> q2 = Vector128.LoadUnsafe(ref Unsafe.Add(ref q0Row, 2 * stride));
        Vector128<byte> q3 = Vector128.LoadUnsafe(ref Unsafe.Add(ref q0Row, 3 * stride));
        FilterNormal(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, kind == Kind.Macroblock, edge, interior, hev);
        if (kind == Kind.Macroblock)
        {
            p2.StoreUnsafe(ref Unsafe.Subtract(ref q0Row, 3 * stride));
            q2.StoreUnsafe(ref Unsafe.Add(ref q0Row, 2 * stride));
        }

        p1.StoreUnsafe(ref Unsafe.Subtract(ref q0Row, 2 * stride));
        p0.StoreUnsafe(ref p0Row);
        q0.StoreUnsafe(ref q0Row);
        q1.StoreUnsafe(ref Unsafe.Add(ref q0Row, stride));
    }

    /// <summary>The vertical edge left of <paramref name="q0"/>, 16 rows down.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void LumaVertical(ref byte q0, int stride, Kind kind, int edge, int interior, int hev)
    {
        ref byte left = ref Unsafe.Subtract(ref q0, 4);
        Load16x8(ref left, stride, ref Unsafe.Add(ref left, 8 * stride), stride,
            out var p3, out var p2, out var p1, out var p0, out var c0, out var c1, out var c2, out var c3);
        Filter(kind, p3, ref p2, ref p1, ref p0, ref c0, ref c1, ref c2, c3, edge, interior, hev);
        Store16x8(ref left, stride, ref Unsafe.Add(ref left, 8 * stride), stride, p3, p2, p1, p0, c0, c1, c2, c3);
    }

    /// <summary>The horizontal edges above <paramref name="uQ0Row"/> and <paramref name="vQ0Row"/> (8 pixels each).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void ChromaHorizontal(ref byte uQ0Row, ref byte vQ0Row, int stride, Kind kind, int edge, int interior, int hev)
    {
        Vector128<byte> p3 = LoadPair(ref uQ0Row, ref vQ0Row, -4 * stride);
        Vector128<byte> p2 = LoadPair(ref uQ0Row, ref vQ0Row, -3 * stride);
        Vector128<byte> p1 = LoadPair(ref uQ0Row, ref vQ0Row, -2 * stride);
        Vector128<byte> p0 = LoadPair(ref uQ0Row, ref vQ0Row, -stride);
        Vector128<byte> q0 = LoadPair(ref uQ0Row, ref vQ0Row, 0);
        Vector128<byte> q1 = LoadPair(ref uQ0Row, ref vQ0Row, stride);
        Vector128<byte> q2 = LoadPair(ref uQ0Row, ref vQ0Row, 2 * stride);
        Vector128<byte> q3 = LoadPair(ref uQ0Row, ref vQ0Row, 3 * stride);
        Filter(kind, p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, edge, interior, hev);
        if (kind == Kind.Macroblock)
        {
            StorePair(ref uQ0Row, ref vQ0Row, -3 * stride, p2);
            StorePair(ref uQ0Row, ref vQ0Row, 2 * stride, q2);
        }

        StorePair(ref uQ0Row, ref vQ0Row, -2 * stride, p1);
        StorePair(ref uQ0Row, ref vQ0Row, -stride, p0);
        StorePair(ref uQ0Row, ref vQ0Row, 0, q0);
        StorePair(ref uQ0Row, ref vQ0Row, stride, q1);
    }

    /// <summary>The vertical edges left of <paramref name="uQ0"/> and <paramref name="vQ0"/> (8 rows each).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static void ChromaVertical(ref byte uQ0, ref byte vQ0, int stride, Kind kind, int edge, int interior, int hev)
    {
        ref byte uLeft = ref Unsafe.Subtract(ref uQ0, 4);
        ref byte vLeft = ref Unsafe.Subtract(ref vQ0, 4);
        Load16x8(ref uLeft, stride, ref vLeft, stride,
            out var p3, out var p2, out var p1, out var p0, out var q0, out var q1, out var q2, out var q3);
        Filter(kind, p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, edge, interior, hev);
        Store16x8(ref uLeft, stride, ref vLeft, stride, p3, p2, p1, p0, q0, q1, q2, q3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> LoadPair(ref byte u, ref byte v, int offset) =>
        Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref u, offset)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref v, offset))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StorePair(ref byte u, ref byte v, int offset, Vector128<byte> value)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref u, offset), value.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref v, offset), value.AsUInt64().GetElement(1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Filter(Kind kind, Vector128<byte> p3, ref Vector128<byte> p2, ref Vector128<byte> p1,
        ref Vector128<byte> p0, ref Vector128<byte> q0, ref Vector128<byte> q1, ref Vector128<byte> q2, Vector128<byte> q3,
        int edge, int interior, int hev)
    {
        if (kind == Kind.Simple)
        {
            FilterSimple(p1, ref p0, ref q0, q1, edge);
        }
        else
        {
            FilterNormal(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, kind == Kind.Macroblock, edge, interior, hev);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AbsDifference(Vector128<byte> a, Vector128<byte> b) =>
        Vector128.SubtractSaturate(a, b) | Vector128.SubtractSaturate(b, a);

    // 2|p0 - q0| + |p1 - q1| / 2 <= edge. The saturated sum is exact up to 255, above any limit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> EdgeMask(Vector128<byte> p1, Vector128<byte> p0, Vector128<byte> q0, Vector128<byte> q1, int edge)
    {
        Vector128<byte> d0 = AbsDifference(p0, q0);
        Vector128<byte> d1 = Vector128.ShiftRightLogical(AbsDifference(p1, q1), 1);
        Vector128<byte> sum = Vector128.AddSaturate(Vector128.AddSaturate(d0, d0), d1);
        return Vector128.Equals(Vector128.SubtractSaturate(sum, Vector128.Create((byte)edge)), Vector128<byte>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> ToSigned(Vector128<byte> v) => (v ^ Vector128.Create((byte)0x80)).AsSByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ToUnsigned(Vector128<sbyte> v) => v.AsByte() ^ Vector128.Create((byte)0x80);

    // Arithmetic v >> 3 on signed bytes: (u >> 3) - 16 with u = v + 128, a logical byte shift.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> ShiftRight3(Vector128<sbyte> v) =>
        (Vector128.ShiftRightLogical(v.AsByte() ^ Vector128.Create((byte)0x80), 3) - Vector128.Create((byte)16)).AsSByte();

    /// <summary>RFC 6386 common_adjust: a = c(outer + 3 (q0 - p0)); q0 -= c(a + 4) >> 3, p0 += c(a + 3) >> 3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<sbyte> Adjust(Vector128<sbyte> outer, Vector128<sbyte> delta, Vector128<byte> mask,
        ref Vector128<sbyte> p0, ref Vector128<sbyte> q0)
    {
        Vector128<sbyte> a = Vector128.AddSaturate(Vector128.AddSaturate(Vector128.AddSaturate(outer, delta), delta), delta);
        a &= mask.AsSByte();
        Vector128<sbyte> qAdjust = ShiftRight3(Vector128.AddSaturate(a, Vector128.Create((sbyte)4)));
        Vector128<sbyte> pAdjust = ShiftRight3(Vector128.AddSaturate(a, Vector128.Create((sbyte)3)));
        q0 = Vector128.SubtractSaturate(q0, qAdjust);
        p0 = Vector128.AddSaturate(p0, pAdjust);
        return qAdjust;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterSimple(Vector128<byte> p1, ref Vector128<byte> p0, ref Vector128<byte> q0, Vector128<byte> q1, int edge)
    {
        Vector128<byte> mask = EdgeMask(p1, p0, q0, q1, edge);
        Vector128<sbyte> sp1 = ToSigned(p1), sp0 = ToSigned(p0), sq0 = ToSigned(q0), sq1 = ToSigned(q1);
        Adjust(Vector128.SubtractSaturate(sp1, sq1), Vector128.SubtractSaturate(sq0, sp0), mask, ref sp0, ref sq0);
        p0 = ToUnsigned(sp0);
        q0 = ToUnsigned(sq0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterNormal(Vector128<byte> p3, ref Vector128<byte> p2, ref Vector128<byte> p1, ref Vector128<byte> p0,
        ref Vector128<byte> q0, ref Vector128<byte> q1, ref Vector128<byte> q2, Vector128<byte> q3,
        bool macroblockEdge, int edge, int interior, int hev)
    {
        Vector128<byte> dp1p0 = AbsDifference(p1, p0);
        Vector128<byte> dq1q0 = AbsDifference(q1, q0);
        Vector128<byte> interiorMax = Vector128.Max(
            Vector128.Max(Vector128.Max(AbsDifference(p3, p2), AbsDifference(p2, p1)), dp1p0),
            Vector128.Max(Vector128.Max(AbsDifference(q3, q2), AbsDifference(q2, q1)), dq1q0));
        Vector128<byte> mask = EdgeMask(p1, p0, q0, q1, edge) &
            Vector128.Equals(Vector128.SubtractSaturate(interiorMax, Vector128.Create((byte)interior)), Vector128<byte>.Zero);
        Vector128<byte> highVariance = ~Vector128.Equals(
            Vector128.SubtractSaturate(Vector128.Max(dp1p0, dq1q0), Vector128.Create((byte)hev)), Vector128<byte>.Zero);

        Vector128<sbyte> sp2 = ToSigned(p2), sp1 = ToSigned(p1), sp0 = ToSigned(p0);
        Vector128<sbyte> sq0 = ToSigned(q0), sq1 = ToSigned(q1), sq2 = ToSigned(q2);
        Vector128<sbyte> outer = Vector128.SubtractSaturate(sp1, sq1);
        Vector128<sbyte> delta = Vector128.SubtractSaturate(sq0, sp0);
        if (macroblockEdge)
        {
            // High-variance lanes: common_adjust with the outer taps on p0 / q0 only.
            Adjust(outer, delta, mask & highVariance, ref sp0, ref sq0);

            // The others: w = c(c(p1 - q1) + 3 (q0 - p0)) spread over three pixels each side with
            // weights 27, 18, 9 (/128, rounded); |27 w + 63| >> 7 <= 27 fits a byte unclamped.
            Vector128<sbyte> w = Vector128.AddSaturate(Vector128.AddSaturate(Vector128.AddSaturate(outer, delta), delta), delta);
            w &= Vector128.AndNot(mask, highVariance).AsSByte();
            Vector128<short> wLow = Vector128.WidenLower(w);
            Vector128<short> wHigh = Vector128.WidenUpper(w);
            Vector128<short> round = Vector128.Create((short)63);
            Vector128<sbyte> a27 = Vector128.Narrow(
                Vector128.ShiftRightArithmetic(wLow * 27 + round, 7), Vector128.ShiftRightArithmetic(wHigh * 27 + round, 7));
            Vector128<sbyte> a18 = Vector128.Narrow(
                Vector128.ShiftRightArithmetic(wLow * 18 + round, 7), Vector128.ShiftRightArithmetic(wHigh * 18 + round, 7));
            Vector128<sbyte> a9 = Vector128.Narrow(
                Vector128.ShiftRightArithmetic(wLow * 9 + round, 7), Vector128.ShiftRightArithmetic(wHigh * 9 + round, 7));
            sq0 = Vector128.SubtractSaturate(sq0, a27);
            sp0 = Vector128.AddSaturate(sp0, a27);
            sq1 = Vector128.SubtractSaturate(sq1, a18);
            sp1 = Vector128.AddSaturate(sp1, a18);
            sq2 = Vector128.SubtractSaturate(sq2, a9);
            sp2 = Vector128.AddSaturate(sp2, a9);
            p2 = ToUnsigned(sp2);
            q2 = ToUnsigned(sq2);
        }
        else
        {
            // Inner edge: the outer taps only where the variance is high; elsewhere p1 / q1 also
            // move by (qAdjust + 1) >> 1.
            Vector128<sbyte> qAdjust = Adjust(outer & highVariance.AsSByte(), delta, mask, ref sp0, ref sq0);
            Vector128<sbyte> plusOne = qAdjust + Vector128.Create((sbyte)1);
            Vector128<sbyte> half = (Vector128.ShiftRightLogical(plusOne.AsByte() ^ Vector128.Create((byte)0x80), 1)
                - Vector128.Create((byte)64)).AsSByte();
            half &= Vector128.AndNot(mask, highVariance).AsSByte();
            sq1 = Vector128.SubtractSaturate(sq1, half);
            sp1 = Vector128.AddSaturate(sp1, half);
        }

        p1 = ToUnsigned(sp1);
        p0 = ToUnsigned(sp0);
        q0 = ToUnsigned(sq0);
        q1 = ToUnsigned(sq1);
    }

    /// <summary>
    /// Loads 8 rows of 8 bytes from each of two places and transposes them: output k holds column
    /// k of the 16 rows (rows of <paramref name="a"/> in lanes 0-7, of <paramref name="b"/> in 8-15).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Load16x8(ref byte a, int aStride, ref byte b, int bStride,
        out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3,
        out Vector128<byte> c4, out Vector128<byte> c5, out Vector128<byte> c6, out Vector128<byte> c7)
    {
        // Rows r and r + 8 of the 16 share a vector (low / high half).
        Vector128<byte> r0 = Row2(ref a, ref b, 0, aStride, bStride);
        Vector128<byte> r1 = Row2(ref a, ref b, 1, aStride, bStride);
        Vector128<byte> r2 = Row2(ref a, ref b, 2, aStride, bStride);
        Vector128<byte> r3 = Row2(ref a, ref b, 3, aStride, bStride);
        Vector128<byte> r4 = Row2(ref a, ref b, 4, aStride, bStride);
        Vector128<byte> r5 = Row2(ref a, ref b, 5, aStride, bStride);
        Vector128<byte> r6 = Row2(ref a, ref b, 6, aStride, bStride);
        Vector128<byte> r7 = Row2(ref a, ref b, 7, aStride, bStride);

        // Interleave bytes of rows (0,1), (2,3), ...: [r0c0 r1c0 r0c1 r1c1 ...] for rows 0..7 in the
        // low halves and 8..15 in the high halves.
        Vector128<byte> t0 = Low8(r0, r1), t1 = High8(r0, r1);
        Vector128<byte> t2 = Low8(r2, r3), t3 = High8(r2, r3);
        Vector128<byte> t4 = Low8(r4, r5), t5 = High8(r4, r5);
        Vector128<byte> t6 = Low8(r6, r7), t7 = High8(r6, r7);

        // 16-bit pairs → 4 rows per column group.
        Vector128<byte> u0 = Low16(t0, t2), u1 = High16(t0, t2);
        Vector128<byte> u2 = Low16(t4, t6), u3 = High16(t4, t6);
        Vector128<byte> u4 = Low16(t1, t3), u5 = High16(t1, t3);
        Vector128<byte> u6 = Low16(t5, t7), u7 = High16(t5, t7);

        // 32-bit → 8 rows (0..7) per column; columns pair up in 64-bit halves.
        Vector128<byte> v0 = Low32(u0, u2), v1 = High32(u0, u2);
        Vector128<byte> v2 = Low32(u1, u3), v3 = High32(u1, u3);
        Vector128<byte> v4 = Low32(u4, u6), v5 = High32(u4, u6);
        Vector128<byte> v6 = Low32(u5, u7), v7 = High32(u5, u7);

        // v0 = [c0 rows 0-7 | c1 rows 0-7], v4 = [c0 rows 8-15 | c1 rows 8-15], ...
        c0 = Low64(v0, v4);
        c1 = High64(v0, v4);
        c2 = Low64(v1, v5);
        c3 = High64(v1, v5);
        c4 = Low64(v2, v6);
        c5 = High64(v2, v6);
        c6 = Low64(v3, v7);
        c7 = High64(v3, v7);
    }

    /// <summary>The inverse of <see cref="Load16x8"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store16x8(ref byte a, int aStride, ref byte b, int bStride,
        Vector128<byte> c0, Vector128<byte> c1, Vector128<byte> c2, Vector128<byte> c3,
        Vector128<byte> c4, Vector128<byte> c5, Vector128<byte> c6, Vector128<byte> c7)
    {
        // Columns (0,1), (2,3), ... interleaved: [c0r0 c1r0 c0r1 c1r1 ...].
        Vector128<byte> t0 = Low8(c0, c1), t1 = High8(c0, c1);
        Vector128<byte> t2 = Low8(c2, c3), t3 = High8(c2, c3);
        Vector128<byte> t4 = Low8(c4, c5), t5 = High8(c4, c5);
        Vector128<byte> t6 = Low8(c6, c7), t7 = High8(c6, c7);

        // [c0 c1 c2 c3] for rows 0-3 / 4-7 / 8-11 / 12-15.
        Vector128<byte> u0 = Low16(t0, t2), u1 = High16(t0, t2);
        Vector128<byte> u2 = Low16(t4, t6), u3 = High16(t4, t6);
        Vector128<byte> u4 = Low16(t1, t3), u5 = High16(t1, t3);
        Vector128<byte> u6 = Low16(t5, t7), u7 = High16(t5, t7);

        // 8 bytes per row: rows (0,1), (2,3), ... in 64-bit halves.
        Vector128<byte> rows01 = Low32(u0, u2), rows23 = High32(u0, u2);
        Vector128<byte> rows45 = Low32(u1, u3), rows67 = High32(u1, u3);
        Vector128<byte> rows89 = Low32(u4, u6), rows1011 = High32(u4, u6);
        Vector128<byte> rows1213 = Low32(u5, u7), rows1415 = High32(u5, u7);

        StoreRows(ref a, aStride, 0, rows01);
        StoreRows(ref a, aStride, 2, rows23);
        StoreRows(ref a, aStride, 4, rows45);
        StoreRows(ref a, aStride, 6, rows67);
        StoreRows(ref b, bStride, 0, rows89);
        StoreRows(ref b, bStride, 2, rows1011);
        StoreRows(ref b, bStride, 4, rows1213);
        StoreRows(ref b, bStride, 6, rows1415);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Row2(ref byte a, ref byte b, int row, int aStride, int bStride) =>
        Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a, row * aStride)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, row * bStride))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRows(ref byte destination, int stride, int row, Vector128<byte> rows)
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, row * stride), rows.AsUInt64().GetElement(0));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (row + 1) * stride), rows.AsUInt64().GetElement(1));
    }

    // Interleave the low / high halves of two vectors element by element (x86 punpckl/h, Arm zip1/2).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Low8(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> High8(Vector128<byte> a, Vector128<byte> b) =>
        Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Low16(Vector128<byte> a, Vector128<byte> b) =>
        (Sse2.IsSupported ? Sse2.UnpackLow(a.AsUInt16(), b.AsUInt16()) : AdvSimd.Arm64.ZipLow(a.AsUInt16(), b.AsUInt16())).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> High16(Vector128<byte> a, Vector128<byte> b) =>
        (Sse2.IsSupported ? Sse2.UnpackHigh(a.AsUInt16(), b.AsUInt16()) : AdvSimd.Arm64.ZipHigh(a.AsUInt16(), b.AsUInt16())).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Low32(Vector128<byte> a, Vector128<byte> b) =>
        (Sse2.IsSupported ? Sse2.UnpackLow(a.AsUInt32(), b.AsUInt32()) : AdvSimd.Arm64.ZipLow(a.AsUInt32(), b.AsUInt32())).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> High32(Vector128<byte> a, Vector128<byte> b) =>
        (Sse2.IsSupported ? Sse2.UnpackHigh(a.AsUInt32(), b.AsUInt32()) : AdvSimd.Arm64.ZipHigh(a.AsUInt32(), b.AsUInt32())).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Low64(Vector128<byte> a, Vector128<byte> b) =>
        (Sse2.IsSupported ? Sse2.UnpackLow(a.AsUInt64(), b.AsUInt64()) : AdvSimd.Arm64.ZipLow(a.AsUInt64(), b.AsUInt64())).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> High64(Vector128<byte> a, Vector128<byte> b) =>
        (Sse2.IsSupported ? Sse2.UnpackHigh(a.AsUInt64(), b.AsUInt64()) : AdvSimd.Arm64.ZipHigh(a.AsUInt64(), b.AsUInt64())).AsByte();
}
