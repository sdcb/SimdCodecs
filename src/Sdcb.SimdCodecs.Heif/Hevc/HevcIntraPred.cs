using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// Intra sample prediction of H.265 §8.4.4.2.
/// </summary>
/// <remarks>
/// The 4N + 1 reference samples p[x][y] are held in one array <c>refs</c>, ordered the way the substitution
/// process of §8.4.4.2.2 scans them: index 0 is p[-1][2N-1] (bottom of the left column), index 2N - 1 - y is
/// p[-1][y], index 2N is the corner p[-1][-1], and index 2N + 1 + x is p[x][-1]. In that order both the
/// substitution and the [1 2 1] smoothing filter of §8.4.4.2.3 become one pass over consecutive entries.
/// </remarks>
internal static class HevcIntraPred
{
    private static readonly sbyte[] IntraPredAngle =
    [
        0, 0, 32, 26, 21, 17, 13, 9, 5, 2, 0, -2, -5, -9, -13, -17, -21, -26,
        -32, -26, -21, -17, -13, -9, -5, -2, 0, 2, 5, 9, 13, 17, 21, 26, 32,
    ];

    private static readonly short[] InvAngle =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        -4096, -1638, -910, -630, -482, -390, -315, -256, -315, -390, -482, -630, -910, -1638, -4096,
    ];

    /// <summary>
    /// Substitutes unavailable reference samples (§8.4.4.2.2). <paramref name="available"/> marks, per entry of
    /// <paramref name="refs"/>, whether the sample was available; <paramref name="anyAvailable"/> is false when none was.
    /// </summary>
    internal static void Substitute(Span<int> refs, ReadOnlySpan<bool> available, bool anyAvailable, int bitDepth)
    {
        if (!anyAvailable)
        {
            refs.Fill(1 << (bitDepth - 1));
            return;
        }
        if (!available[0])
        {
            var k = 1;
            while (!available[k]) k++;
            refs[0] = refs[k];
        }
        for (var i = 1; i < refs.Length; i++)
        {
            if (!available[i]) refs[i] = refs[i - 1];
        }
    }

    /// <summary>
    /// Predicts one N×N block into <paramref name="dst"/> (row stride <paramref name="stride"/>), applying the
    /// neighbouring-sample filter of §8.4.4.2.3 when <paramref name="filterNeighbours"/> is set (luma for 4:2:0).
    /// <paramref name="refs"/> is modified in place by the filter.
    /// </summary>
    internal static void Predict(Span<int> refs, int log2Size, int mode, bool isLuma, bool filterNeighbours,
        bool strongSmoothing, int bitDepth, Span<ushort> dst, int stride) =>
        Predict(refs, log2Size, mode, isLuma, filterNeighbours, strongSmoothing, bitDepth, dst, stride, AutoTier);

    /// <summary>Tier selectors for the sample loops of <see cref="Predict(Span{int}, int, int, bool, bool, bool, int, Span{ushort}, int, int)"/>; tests force each one.</summary>
    internal const int AutoTier = 0, ScalarTier = 1, Vector128Tier = 128, Vector256Tier = 256;

    /// <summary>
    /// <see cref="Predict(Span{int}, int, int, bool, bool, bool, int, Span{ushort}, int)"/> with the sample loops on
    /// <paramref name="tier"/>; every tier writes the same samples.
    /// </summary>
    internal static void Predict(Span<int> refs, int log2Size, int mode, bool isLuma, bool filterNeighbours,
        bool strongSmoothing, int bitDepth, Span<ushort> dst, int stride, int tier)
    {
        var size = 1 << log2Size;
        // The proof for the unchecked accesses of the vector tiers below.
        if (refs.Length < 4 * size + 1 || stride < size || dst.Length < (size - 1) * stride + size)
        {
            throw new ArgumentException("The intra block does not fit its buffers.");
        }
        if (tier == AutoTier)
        {
            tier = Vector256.IsHardwareAccelerated && size >= 8 ? Vector256Tier
                : Vector128.IsHardwareAccelerated ? Vector128Tier : ScalarTier;
        }
        if (tier == Vector256Tier && size < 8) tier = Vector128Tier;

        var n = 1 << log2Size;
        var corner = 2 * n;

        if (filterNeighbours && mode != 1 && n != 4)
        {
            var minDistVerHor = Math.Min(Math.Abs(mode - 26), Math.Abs(mode - 10));
            var threshold = n switch { 8 => 7, 16 => 1, _ => 0 };
            if (minDistVerHor > threshold) Filter(refs, n, strongSmoothing, bitDepth);
        }

        var max = (1 << bitDepth) - 1;
        if (mode == 0)
        {
            // INTRA_PLANAR (8-46).
            if (tier == ScalarTier) PlanarScalar(refs, log2Size, dst, stride);
            else if (tier == Vector128Tier) PlanarVector128(refs, log2Size, dst, stride);
            else PlanarVector256(refs, log2Size, dst, stride);
            return;
        }

        if (mode == 1)
        {
            // INTRA_DC (8-47 .. 8-52).
            var sum = n;
            for (var i = 0; i < n; i++) sum += refs[corner + 1 + i] + refs[corner - 1 - i];
            var dcVal = sum >> (log2Size + 1);
            for (var y = 0; y < n; y++) dst.Slice(y * stride, n).Fill((ushort)dcVal);
            if (isLuma && n < 32)
            {
                dst[0] = (ushort)((refs[corner - 1] + 2 * dcVal + refs[corner + 1] + 2) >> 2);
                for (var x = 1; x < n; x++) dst[x] = (ushort)((refs[corner + 1 + x] + 3 * dcVal + 2) >> 2);
                for (var y = 1; y < n; y++) dst[y * stride] = (ushort)((refs[corner - 1 - y] + 3 * dcVal + 2) >> 2);
            }
            return;
        }

        // INTRA_ANGULAR2..34 (8-53 .. 8-68). ref[] is indexed from -n; refBuf[i + n] holds ref[i].
        var angle = (int)IntraPredAngle[mode];
        Span<int> refBuf = stackalloc int[3 * n + 1];
        var vertical = mode >= 18;

        // main(i) = p[-1 + i][-1] (vertical) or p[-1][-1 + i] (horizontal); side(i) is the other edge.
        for (var x = 0; x <= n; x++) refBuf[n + x] = vertical ? refs[corner + x] : refs[corner - x];
        if (angle < 0)
        {
            var last = (n * angle) >> 5;
            if (last < -1)
            {
                var inv = (int)InvAngle[mode];
                for (var x = last; x <= -1; x++)
                {
                    var s = -1 + ((x * inv + 128) >> 8);
                    // side(s): vertical → p[-1][s] (left column), horizontal → p[s][-1] (top row).
                    refBuf[n + x] = vertical ? refs[corner - 1 - s] : refs[corner + 1 + s];
                }
            }
        }
        else
        {
            for (var x = n + 1; x <= 2 * n; x++) refBuf[n + x] = vertical ? refs[corner + x] : refs[corner - x];
        }

        if (tier == ScalarTier) AngularScalar(refBuf, n, angle, vertical, dst, stride);
        else if (tier == Vector128Tier) AngularVector128(refBuf, n, angle, vertical, dst, stride);
        else AngularVector256(refBuf, n, angle, vertical, dst, stride);

        if (isLuma && n < 32)
        {
            if (mode == 26)
            {
                for (var y = 0; y < n; y++)
                {
                    dst[y * stride] = (ushort)Math.Clamp(refs[corner + 1] + ((refs[corner - 1 - y] - refs[corner]) >> 1), 0, max);
                }
            }
            else if (mode == 10)
            {
                for (var x = 0; x < n; x++)
                {
                    dst[x] = (ushort)Math.Clamp(refs[corner - 1] + ((refs[corner + 1 + x] - refs[corner]) >> 1), 0, max);
                }
            }
        }
    }

    private static void PlanarScalar(ReadOnlySpan<int> refs, int log2Size, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        var corner = 2 * n;
        var topRight = refs[corner + 1 + n];
        var bottomLeft = refs[corner - 1 - n];
        for (var y = 0; y < n; y++)
        {
            var left = refs[corner - 1 - y];
            var row = dst.Slice(y * stride, n);
            for (var x = 0; x < n; x++)
            {
                row[x] = (ushort)(((n - 1 - x) * left + (x + 1) * topRight +
                                   (n - 1 - y) * refs[corner + 1 + x] + (y + 1) * bottomLeft + n) >> (log2Size + 1));
            }
        }
    }

    // The planar vector tiers compute the same integer sums (exact in int32, so the order of the terms is free)
    // four or eight columns at a time. Every load is refs[corner + 1 + c .. + lanes] with c + lanes <= n, every
    // store dst[y * stride + c .. + lanes] with y < n: in range by the check at the top of Predict.

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void PlanarVector128(ReadOnlySpan<int> refs, int log2Size, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        var corner = 2 * n;
        ref var r = ref MemoryMarshal.GetReference(refs);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var topRight = Unsafe.Add(ref r, corner + 1 + n);
        var bottomLeft = Unsafe.Add(ref r, corner - 1 - n);
        var lane = Vector128.Create(0, 1, 2, 3);
        for (var c = 0; c < n; c += 4)
        {
            var x = lane + Vector128.Create(c);
            var top = Vector128.LoadUnsafe(ref r, (nuint)(corner + 1 + c));
            // Row-invariant part (x + 1)·topRight + n; each row adds (n − 1 − x)·left + (n − 1 − y)·top + (y + 1)·bottomLeft.
            var fixedPart = (x + Vector128<int>.One) * topRight + Vector128.Create(n);
            var leftWeight = Vector128.Create(n - 1) - x;
            for (var y = 0; y < n; y++)
            {
                var left = Unsafe.Add(ref r, corner - 1 - y);
                var v = (leftWeight * left + fixedPart + top * (n - 1 - y) + Vector128.Create((y + 1) * bottomLeft)) >> (log2Size + 1);
                var packed = Vector128.Narrow(v.AsUInt32(), v.AsUInt32()).AsUInt64().ToScalar();
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref d, y * stride + c)), packed);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void PlanarVector256(ReadOnlySpan<int> refs, int log2Size, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        var corner = 2 * n;
        ref var r = ref MemoryMarshal.GetReference(refs);
        ref var d = ref MemoryMarshal.GetReference(dst);
        var topRight = Unsafe.Add(ref r, corner + 1 + n);
        var bottomLeft = Unsafe.Add(ref r, corner - 1 - n);
        var lane = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        for (var c = 0; c < n; c += 8)
        {
            var x = lane + Vector256.Create(c);
            var top = Vector256.LoadUnsafe(ref r, (nuint)(corner + 1 + c));
            var fixedPart = (x + Vector256<int>.One) * topRight + Vector256.Create(n);
            var leftWeight = Vector256.Create(n - 1) - x;
            for (var y = 0; y < n; y++)
            {
                var left = Unsafe.Add(ref r, corner - 1 - y);
                var v = (leftWeight * left + fixedPart + top * (n - 1 - y) + Vector256.Create((y + 1) * bottomLeft)) >> (log2Size + 1);
                Vector128.Narrow(v.GetLower().AsUInt32(), v.GetUpper().AsUInt32()).StoreUnsafe(ref d, (nuint)(y * stride + c));
            }
        }
    }

    /// <summary>
    /// The angular sample loop of (8-59 .. 8-68) over refBuf (refBuf[n + i] = ref[i]): row j of the predicted
    /// block (column j for a horizontal mode) interpolates ref at i + iIdx + 1 and i + iIdx + 2.
    /// </summary>
    private static void AngularScalar(ReadOnlySpan<int> refBuf, int n, int angle, bool vertical, Span<ushort> dst, int stride)
    {
        for (var j = 0; j < n; j++)
        {
            var iIdx = ((j + 1) * angle) >> 5;
            var iFact = ((j + 1) * angle) & 31;
            for (var i = 0; i < n; i++)
            {
                var a = refBuf[n + i + iIdx + 1];
                var value = iFact != 0 ? ((32 - iFact) * a + iFact * refBuf[n + i + iIdx + 2] + 16) >> 5 : a;
                // Vertical: j is the row y and i the column x; horizontal: the reverse.
                if (vertical) dst[j * stride + i] = (ushort)value;
                else dst[i * stride + j] = (ushort)value;
            }
        }
    }

    // Angular vector tiers. The first tap reads refBuf[n + iIdx + 1 + i] and, when iFact != 0, the second
    // refBuf[n + iIdx + 2 + i], for i < n. With |angle| <= 32, -n <= iIdx <= n - 1 when iFact != 0 and iIdx <= n
    // when iFact == 0, so every index lies in 1 .. 3n of the 3n + 1 entries (checked length). A horizontal mode is
    // computed into a transposed scratch block, then written column-wise.

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void AngularVector128(ReadOnlySpan<int> refBuf, int n, int angle, bool vertical, Span<ushort> dst, int stride)
    {
        if (refBuf.Length < 3 * n + 1 || (uint)(angle + 32) > 64) throw new ArgumentException("The angular reference does not fit.");
        ref var r = ref MemoryMarshal.GetReference(refBuf);
        Span<ushort> scratch = stackalloc ushort[vertical ? 0 : n * n];
        ref var d = ref vertical ? ref MemoryMarshal.GetReference(dst) : ref MemoryMarshal.GetReference(scratch);
        var rowStride = vertical ? stride : n;
        var r16 = Vector128.Create(16);
        for (var j = 0; j < n; j++)
        {
            var iIdx = ((j + 1) * angle) >> 5;
            var iFact = ((j + 1) * angle) & 31;
            var at = n + iIdx + 1;
            for (var c = 0; c < n; c += 4)
            {
                var a = Vector128.LoadUnsafe(ref r, (nuint)(at + c));
                var v = iFact == 0 ? a : (a * (32 - iFact) + Vector128.LoadUnsafe(ref r, (nuint)(at + c + 1)) * iFact + r16) >> 5;
                var packed = Vector128.Narrow(v.AsUInt32(), v.AsUInt32()).AsUInt64().ToScalar();
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref d, j * rowStride + c)), packed);
            }
        }
        if (!vertical) Transpose(scratch, n, dst, stride);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void AngularVector256(ReadOnlySpan<int> refBuf, int n, int angle, bool vertical, Span<ushort> dst, int stride)
    {
        if (refBuf.Length < 3 * n + 1 || n < 8 || (uint)(angle + 32) > 64) throw new ArgumentException("The angular reference does not fit.");
        ref var r = ref MemoryMarshal.GetReference(refBuf);
        Span<ushort> scratch = stackalloc ushort[vertical ? 0 : n * n];
        ref var d = ref vertical ? ref MemoryMarshal.GetReference(dst) : ref MemoryMarshal.GetReference(scratch);
        var rowStride = vertical ? stride : n;
        var r16 = Vector256.Create(16);
        for (var j = 0; j < n; j++)
        {
            var iIdx = ((j + 1) * angle) >> 5;
            var iFact = ((j + 1) * angle) & 31;
            var at = n + iIdx + 1;
            for (var c = 0; c < n; c += 8)
            {
                var a = Vector256.LoadUnsafe(ref r, (nuint)(at + c));
                var v = iFact == 0 ? a : (a * (32 - iFact) + Vector256.LoadUnsafe(ref r, (nuint)(at + c + 1)) * iFact + r16) >> 5;
                Vector128.Narrow(v.GetLower().AsUInt32(), v.GetUpper().AsUInt32()).StoreUnsafe(ref d, (nuint)(j * rowStride + c));
            }
        }
        if (!vertical) Transpose(scratch, n, dst, stride);
    }

    /// <summary>dst[i * stride + j] = scratch[j * n + i].</summary>
    private static void Transpose(ReadOnlySpan<ushort> scratch, int n, Span<ushort> dst, int stride)
    {
        for (var i = 0; i < n; i++)
        {
            var row = dst.Slice(i * stride, n);
            for (var j = 0; j < n; j++) row[j] = scratch[j * n + i];
        }
    }

    private static void Filter(Span<int> refs, int n, bool strongSmoothing, int bitDepth)
    {
        var corner = 2 * n;
        var last = 4 * n;
        if (strongSmoothing && n == 32)
        {
            var threshold = 1 << (bitDepth - 5);
            var c = refs[corner];
            var topEnd = refs[last];   // p[63][-1]
            var leftEnd = refs[0];     // p[-1][63]
            if (Math.Abs(c + topEnd - 2 * refs[corner + n]) < threshold &&
                Math.Abs(c + leftEnd - 2 * refs[corner - n]) < threshold)
            {
                // (8-36 .. 8-40): bilinear between the corner and the far ends.
                for (var i = 0; i < 63; i++)
                {
                    refs[corner + 1 + i] = ((63 - i) * c + (i + 1) * topEnd + 32) >> 6;
                    refs[corner - 1 - i] = ((63 - i) * c + (i + 1) * leftEnd + 32) >> 6;
                }
                return;
            }
        }

        // (8-41 .. 8-45): [1 2 1] over consecutive entries, the two ends unchanged.
        var previous = refs[0];
        for (var i = 1; i < last; i++)
        {
            var current = refs[i];
            refs[i] = (previous + 2 * current + refs[i + 1] + 2) >> 2;
            previous = current;
        }
    }
}
