using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Vector128 twin of <see cref="Av1LoopFilter.FilterEdgeScalar"/>: the four samples of an edge segment are the four
/// int32 lanes. Every decision of §7.14.6 (filter mask, hev, flat, flat2) becomes a lane mask, each candidate filter
/// is computed for all lanes with the scalar code's integer operations, and the masks pick per lane what the scalar
/// code would have written. Lanes the mask rejects keep their samples.
/// </summary>
internal static partial class Av1LoopFilter
{
    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void FilterEdgeVector(
        ushort[] buf, int stride, int bitDepth, int plane,
        int x, int y, int limit, int blimit, int thresh, int dx, int dy, int filterSize)
    {
        // Taps k in [-reach, reach) across the edge (p0 is k = -1, q0 is k = 0), as many as the scalar code may read.
        int reach = filterSize == 4 ? 2 : plane != 0 ? 3 : filterSize == 8 ? 4 : 7;
        int step = dy * stride + dx;          // across the edge
        int lane = dx * stride + dy;          // along the edge
        int pos = y * stride + x;
        // One check for every index read and written below: the extremes are the first lane's tap -reach and the last
        // lane's tap reach - 1 (step and lane are positive). Out of range only for malformed geometry: take the
        // bounds-checked scalar path then.
        long lo = pos - (long)reach * step;
        long hi = pos + 3L * lane + (long)(reach - 1) * step;
        if (lo < 0 || hi >= buf.Length)
        {
            FilterEdgeScalar(buf, stride, bitDepth, plane, x, y, limit, blimit, thresh, dx, dy, filterSize);
            return;
        }

        ref ushort b = ref MemoryMarshal.GetArrayDataReference(buf);
        Span<Vector128<int>> s = stackalloc Vector128<int>[14]; // tap k at s[k + 7]
        for (int k = -reach; k < reach; k++)
            s[k + 7] = Load(ref b, pos + k * step, lane, dy);

        int bdShift = bitDepth - 8;
        var limitBd = Vector128.Create(limit << bdShift);
        var blimitBd = Vector128.Create(blimit << bdShift);
        var threshBd = Vector128.Create(thresh << bdShift);
        var flatThresh = Vector128.Create(1 << bdShift);

        Vector128<int> p0 = s[6], q0 = s[7], p1 = s[5], q1 = s[8];
        var ap1p0 = Vector128.Abs(p1 - p0);
        var aq1q0 = Vector128.Abs(q1 - q0);
        var mask = Vector128.LessThanOrEqual(ap1p0, limitBd)
                 & Vector128.LessThanOrEqual(aq1q0, limitBd)
                 & Vector128.LessThanOrEqual(Vector128.Abs(p0 - q0) * 2 + Vector128.ShiftRightArithmetic(Vector128.Abs(p1 - q1), 1), blimitBd);

        int filterLen = filterSize == 4 ? 4 : plane != 0 ? 6 : filterSize == 8 ? 8 : 16;
        Vector128<int> p2 = default, q2 = default, p3 = default, q3 = default;
        if (filterLen >= 6)
        {
            p2 = s[4];
            q2 = s[9];
            mask &= Vector128.LessThanOrEqual(Vector128.Abs(p2 - p1), limitBd) & Vector128.LessThanOrEqual(Vector128.Abs(q2 - q1), limitBd);
        }
        if (filterLen >= 8)
        {
            p3 = s[3];
            q3 = s[10];
            mask &= Vector128.LessThanOrEqual(Vector128.Abs(p3 - p2), limitBd) & Vector128.LessThanOrEqual(Vector128.Abs(q3 - q2), limitBd);
        }
        if (mask == Vector128<int>.Zero)
            return;

        var hev = Vector128.GreaterThan(ap1p0, threshBd) | Vector128.GreaterThan(aq1q0, threshBd);

        Vector128<int> flat = Vector128<int>.Zero;
        if (filterSize >= 8)
        {
            flat = Vector128.LessThanOrEqual(ap1p0, flatThresh)
                 & Vector128.LessThanOrEqual(aq1q0, flatThresh)
                 & Vector128.LessThanOrEqual(Vector128.Abs(p2 - p0), flatThresh)
                 & Vector128.LessThanOrEqual(Vector128.Abs(q2 - q0), flatThresh);
            if (filterLen >= 8)
                flat &= Vector128.LessThanOrEqual(Vector128.Abs(p3 - p0), flatThresh) & Vector128.LessThanOrEqual(Vector128.Abs(q3 - q0), flatThresh);
            flat &= mask;
        }
        var narrowMask = mask & ~flat;

        // Outputs start as the inputs; each filter overwrites the taps it changes in its own lanes.
        Span<Vector128<int>> o = stackalloc Vector128<int>[14];
        s.CopyTo(o);

        if (narrowMask != Vector128<int>.Zero)
            Narrow(o, narrowMask, hev, bitDepth, p1, p0, q0, q1);

        if (flat != Vector128<int>.Zero)
        {
            Vector128<int> flat2 = Vector128<int>.Zero;
            if (filterSize >= 16)
            {
                flat2 = flat
                      & Vector128.LessThanOrEqual(Vector128.Abs(s[2] - p0), flatThresh)
                      & Vector128.LessThanOrEqual(Vector128.Abs(s[11] - q0), flatThresh)
                      & Vector128.LessThanOrEqual(Vector128.Abs(s[1] - p0), flatThresh)
                      & Vector128.LessThanOrEqual(Vector128.Abs(s[12] - q0), flatThresh)
                      & Vector128.LessThanOrEqual(Vector128.Abs(s[0] - p0), flatThresh)
                      & Vector128.LessThanOrEqual(Vector128.Abs(s[13] - q0), flatThresh);
            }
            var wide8 = flat & ~flat2;
            if (wide8 != Vector128<int>.Zero)
                Wide(s, o, wide8, plane, 3);
            if (flat2 != Vector128<int>.Zero)
                Wide(s, o, flat2, plane, 4);
        }

        // Narrow changes p1..q1, the wide filters at most p5..q5 (taps -6 .. 5).
        int written = filterSize >= 16 ? 6 : filterLen == 8 ? 3 : 2;
        for (int k = -written; k < written; k++)
            Store(ref b, pos + k * step, lane, dy, o[k + 7]);
    }

    private static void Narrow(Span<Vector128<int>> o, Vector128<int> m, Vector128<int> hev, int bitDepth,
        Vector128<int> p1, Vector128<int> p0, Vector128<int> q0, Vector128<int> q1)
    {
        int halfS = 1 << (bitDepth - 1);
        var half = Vector128.Create(halfS);
        var min = Vector128.Create(-halfS);
        var max = Vector128.Create(halfS - 1);

        var ps1 = p1 - half;
        var ps0 = p0 - half;
        var qs0 = q0 - half;
        var qs1 = q1 - half;

        var filter = hev & Vector128.Clamp(ps1 - qs1, min, max);
        filter = Vector128.Clamp(filter + (qs0 - ps0) * 3, min, max);
        var filter1 = Vector128.ShiftRightArithmetic(Vector128.Clamp(filter + Vector128.Create(4), min, max), 3);
        var filter2 = Vector128.ShiftRightArithmetic(Vector128.Clamp(filter + Vector128.Create(3), min, max), 3);

        o[7] = Vector128.ConditionalSelect(m, Vector128.Clamp(qs0 - filter1, min, max) + half, o[7]);
        o[6] = Vector128.ConditionalSelect(m, Vector128.Clamp(ps0 + filter2, min, max) + half, o[6]);

        var m2 = m & ~hev;
        var f = Vector128.ShiftRightArithmetic(filter1 + Vector128<int>.One, 1);
        o[8] = Vector128.ConditionalSelect(m2, Vector128.Clamp(qs1 - f, min, max) + half, o[8]);
        o[5] = Vector128.ConditionalSelect(m2, Vector128.Clamp(ps1 + f, min, max) + half, o[5]);
    }

    /// <summary>The scalar <c>WideFilter</c>'s sliding window, on lanes; writes outputs -n .. n - 1 where <paramref name="m"/> is set.</summary>
    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    private static void Wide(ReadOnlySpan<Vector128<int>> s, Span<Vector128<int>> o, Vector128<int> m, int plane, int log2Size)
    {
        int n = log2Size == 4 ? 6 : plane == 0 ? 3 : 2;
        bool n2 = !(log2Size == 3 && plane == 0);
        int ofs = 2 * n;
        Span<Vector128<int>> e = stackalloc Vector128<int>[24];
        for (int k = -ofs; k < ofs; k++)
            e[k + ofs] = s[Av1Constants.Clip3(-(n + 1), n, k) + 7];
        var window = Vector128<int>.Zero;
        for (int k = -ofs; k <= 0; k++)
            window += e[k + ofs];
        var round = Vector128.Create(1 << (log2Size - 1));
        for (int i = -n; i < n; i++)
        {
            var t = window + e[i + ofs];
            if (n2)
                t += e[i - 1 + ofs] + e[i + 1 + ofs];
            o[i + 7] = Vector128.ConditionalSelect(m, Vector128.ShiftRightArithmetic(t + round, log2Size), o[i + 7]);
            if (i + 1 < n)
                window += e[i + n + 1 + ofs] - e[i - n + ofs];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load(ref ushort b, int index, int lane, int contiguous)
    {
        if (contiguous != 0)
        {
            // Four adjacent samples: one 8-byte read, widened.
            ulong raw = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref b, (nint)index)));
            return Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw).AsUInt16()).AsInt32();
        }
        return Vector128.Create(
            Unsafe.Add(ref b, (nint)index),
            Unsafe.Add(ref b, (nint)(index + lane)),
            Unsafe.Add(ref b, (nint)(index + 2 * lane)),
            (int)Unsafe.Add(ref b, (nint)(index + 3 * lane)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref ushort b, int index, int lane, int contiguous, Vector128<int> v)
    {
        if (contiguous != 0)
        {
            var packed = Vector128.Narrow(v.AsUInt32(), v.AsUInt32());
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref b, (nint)index)), packed.AsUInt64().ToScalar());
            return;
        }
        Unsafe.Add(ref b, (nint)index) = (ushort)v.GetElement(0);
        Unsafe.Add(ref b, (nint)(index + lane)) = (ushort)v.GetElement(1);
        Unsafe.Add(ref b, (nint)(index + 2 * lane)) = (ushort)v.GetElement(2);
        Unsafe.Add(ref b, (nint)(index + 3 * lane)) = (ushort)v.GetElement(3);
    }
}
