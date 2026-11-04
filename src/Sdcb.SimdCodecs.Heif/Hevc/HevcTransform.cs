using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// Scaling (§8.6.3) and inverse transformation (§8.6.2, §8.6.4) of one transform block, and the addition of the
/// residual to the prediction (§8.6.7).
/// </summary>
/// <remarks>
/// <para>
/// The inverse transform is the two matrix products of (8-314) and (8-316): a vertical pass
/// e[x][y] = Σj transMatrix[j][y] · d[x][j], clipped to 16 bits after (e + 64) &gt;&gt; 7, then a horizontal
/// pass with the final (r + 2^(bdShift − 1)) &gt;&gt; bdShift of (8-299). Every sum is exact in 32-bit integers
/// (|d| ≤ 2^15, |transMatrix| ≤ 90, at most 32 terms), so the order of the additions cannot change a result
/// and every tier computes the same residuals.
/// </para>
/// <para>
/// Two exact shortcuts keep the work proportional to the coded coefficients: the products only run over the
/// rows and columns up to the last non-zero coefficient of the block (<c>maxX</c>, <c>maxY</c>; the rest of
/// each sum is zero), and the vertical pass uses the symmetry of the DCT basis,
/// transMatrix[j][N − 1 − y] = (−1)^j · transMatrix[j][y]: the even-j and odd-j partial sums E and O give
/// output y as E + O and output N − 1 − y as E − O.
/// </para>
/// </remarks>
internal static class HevcTransform
{
    private static readonly int[] LevelScale = [40, 45, 51, 57, 64, 72];

    /// <summary>
    /// transMatrix of (8-319 .. 8-321) as <c>Dct[k * 32 + n]</c>: basis function k (frequency) at sample n.
    /// Generated from the 33 distinct magnitudes of the matrix, which equal the integer approximations of
    /// 64·√2·cos(mπ/64); a unit test checks every entry against the printed table.
    /// </summary>
    internal static readonly sbyte[] Dct = BuildDct();

    /// <summary>The 4×4 DST of (8-317): <c>Dst[k * 4 + n]</c>.</summary>
    private static readonly sbyte[] Dst =
    [
        29, 55, 74, 84,
        74, 74, 0, -74,
        84, -29, -74, 55,
        55, -84, 74, -29,
    ];

    /// <summary>The N-point DCT matrices for N = 4, 8, 16, 32 as <c>int</c>: <c>[log2N - 2][k * N + n]</c>.</summary>
    private static readonly int[][] DctN = [BuildDctN(2), BuildDctN(3), BuildDctN(4), BuildDctN(5)];

    private static readonly int[] DstN = Array.ConvertAll(Dst, v => (int)v);

    private static sbyte[] BuildDct()
    {
        ReadOnlySpan<byte> magnitude =
        [
            64, 90, 90, 90, 89, 88, 87, 85, 83, 82, 80, 78, 75, 73, 70, 67,
            64, 61, 57, 54, 50, 46, 43, 38, 36, 31, 25, 22, 18, 13, 9, 4, 0,
        ];
        var result = new sbyte[32 * 32];
        for (var k = 0; k < 32; k++)
        {
            for (var n = 0; n < 32; n++)
            {
                // cos((2n + 1) k π / 64): reduce the angle (2n + 1) k to [0, 64] by symmetry, then to [0, 32] with a sign.
                var a = (2 * n + 1) * k % 128;
                if (a > 64) a = 128 - a;
                result[k * 32 + n] = (sbyte)(a > 32 ? -magnitude[64 - a] : magnitude[a]);
            }
        }
        return result;
    }

    /// <summary>transMatrix for an N-point transform: rows 0, 32/N, 2·32/N, … of the 32-point one (8-319 .. 8-321).</summary>
    private static int[] BuildDctN(int log2Size)
    {
        var n = 1 << log2Size;
        var step = 32 >> log2Size;
        var result = new int[n * n];
        for (var k = 0; k < n; k++)
        {
            for (var x = 0; x < n; x++) result[k * n + x] = Dct[k * step * 32 + x];
        }
        return result;
    }

    /// <summary>The scaling of (8-309) for one block: TransCoeffLevel → d.</summary>
    internal readonly struct Scaler
    {
        private readonly long _scale;
        private readonly long _round;
        private readonly int _bdShift;

        internal Scaler(int log2Size, int qp, int bitDepth)
        {
            _bdShift = bitDepth + log2Size - 5;
            _scale = (long)LevelScale[qp % 6] << (qp / 6);
            _round = 1L << (_bdShift - 1);
        }

        /// <summary>d = Clip3(−32768, 32767, (level · m · levelScale[qP % 6] &lt;&lt; (qP / 6) + round) &gt;&gt; bdShift).</summary>
        internal int Scale(int level, int m) =>
            (int)Math.Clamp((level * m * _scale + _round) >> _bdShift, short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Inverse-transforms the scaled coefficients <paramref name="coeffs"/> (row-major N×N, zero beyond column
    /// <paramref name="maxX"/> and row <paramref name="maxY"/>) and adds the residual to the prediction in
    /// <paramref name="dst"/> (row stride <paramref name="stride"/>), clipping to the sample range.
    /// <paramref name="scratch"/> holds the intermediate N×N block.
    /// </summary>
    internal static void InverseAdd(ReadOnlySpan<int> coeffs, Span<int> scratch, int log2Size, bool dst4, int maxX, int maxY,
        int bitDepth, Span<ushort> dst, int stride)
    {
        if (Vector256.IsHardwareAccelerated && log2Size >= 3)
            InverseAddVector256(coeffs, scratch, log2Size, maxX, maxY, bitDepth, dst, stride);
        else if (Vector128.IsHardwareAccelerated)
            InverseAddVector128(coeffs, scratch, log2Size, dst4, maxX, maxY, bitDepth, dst, stride);
        else
            InverseAddScalar(coeffs, scratch, log2Size, dst4, maxX, maxY, bitDepth, dst, stride);
    }

    /// <summary>Checks every index the kernels touch: the proof for their unchecked accesses.</summary>
    private static void CheckBlock(ReadOnlySpan<int> coeffs, Span<int> scratch, int n, int maxX, int maxY, Span<ushort> dst, int stride)
    {
        if (coeffs.Length < n * n || scratch.Length < n * n || (uint)maxX >= (uint)n || (uint)maxY >= (uint)n ||
            stride < n || dst.Length < (n - 1) * stride + n)
        {
            throw new ArgumentException("The transform block does not fit its buffers.");
        }
    }

    /// <summary>The scalar definition: the same passes and shortcuts as the vector tiers, one sample at a time.</summary>
    internal static void InverseAddScalar(ReadOnlySpan<int> coeffs, Span<int> scratch, int log2Size, bool dst4, int maxX, int maxY,
        int bitDepth, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        CheckBlock(coeffs, scratch, n, maxX, maxY, dst, stride);
        ReadOnlySpan<int> m = dst4 ? DstN : DctN[log2Size - 2];
        var g = scratch;

        // Vertical pass over the columns 0..maxX.
        if (dst4)
        {
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x <= maxX; x++)
                {
                    var sum = 0;
                    for (var j = 0; j <= maxY; j++) sum += m[j * n + y] * coeffs[j * n + x];
                    g[y * n + x] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
                }
            }
        }
        else
        {
            var lastRow = maxY | 1; // the row after an even maxY is zero, so the odd sums may include it
            for (var y = 0; y < n / 2; y++)
            {
                for (var x = 0; x <= maxX; x++)
                {
                    int even = 0, odd = 0;
                    for (var j = 0; j <= lastRow; j += 2)
                    {
                        even += m[j * n + y] * coeffs[j * n + x];
                        odd += m[(j + 1) * n + y] * coeffs[(j + 1) * n + x];
                    }
                    g[y * n + x] = Math.Clamp((even + odd + 64) >> 7, short.MinValue, short.MaxValue);
                    g[(n - 1 - y) * n + x] = Math.Clamp((even - odd + 64) >> 7, short.MinValue, short.MaxValue);
                }
            }
        }

        // Horizontal pass over the terms 0..maxX, the final shift (8-299) and the reconstruction (8-325).
        var bdShift = 20 - bitDepth;
        var round = 1 << (bdShift - 1);
        var max = (1 << bitDepth) - 1;
        for (var y = 0; y < n; y++)
        {
            var row = dst.Slice(y * stride, n);
            for (var x = 0; x < n; x++)
            {
                var sum = 0;
                for (var j = 0; j <= maxX; j++) sum += g[y * n + j] * m[j * n + x];
                row[x] = (ushort)Math.Clamp(row[x] + ((sum + round) >> bdShift), 0, max);
            }
        }
    }

    /// <summary>The Vector128 tier: four columns per vector.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void InverseAddVector128(ReadOnlySpan<int> coeffs, Span<int> scratch, int log2Size, bool dst4, int maxX, int maxY,
        int bitDepth, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        CheckBlock(coeffs, scratch, n, maxX, maxY, dst, stride);
        // Every access below is at j * n + c * 4 + (0..3) with j < n and c * 4 + 4 <= n (n is a multiple of 4), or at
        // y * stride + c * 4 + (0..3) with y < n: in range by CheckBlock and the n × n matrices.
        ref var d = ref MemoryMarshal.GetReference(coeffs);
        ref var g = ref MemoryMarshal.GetReference(scratch);
        ref var m = ref MemoryMarshal.GetArrayDataReference(dst4 ? DstN : DctN[log2Size - 2]);
        var chunks = (maxX >> 2) + 1;
        var lo16 = Vector128.Create((int)short.MinValue);
        var hi16 = Vector128.Create((int)short.MaxValue);
        var r64 = Vector128.Create(64);

        if (dst4)
        {
            for (var y = 0; y < 4; y++)
            {
                var sum = Vector128<int>.Zero;
                for (var j = 0; j <= maxY; j++) sum += Vector128.Create(Unsafe.Add(ref m, j * 4 + y)) * Vector128.LoadUnsafe(ref d, (nuint)(j * 4));
                Vector128.Min(Vector128.Max((sum + r64) >> 7, lo16), hi16).StoreUnsafe(ref g, (nuint)(y * 4));
            }
        }
        else
        {
            var lastRow = maxY | 1;
            for (var y = 0; y < n / 2; y++)
            {
                for (var c = 0; c < chunks; c++)
                {
                    var even = Vector128<int>.Zero;
                    var odd = Vector128<int>.Zero;
                    for (var j = 0; j <= lastRow; j += 2)
                    {
                        even += Vector128.Create(Unsafe.Add(ref m, j * n + y)) * Vector128.LoadUnsafe(ref d, (nuint)(j * n + c * 4));
                        odd += Vector128.Create(Unsafe.Add(ref m, (j + 1) * n + y)) * Vector128.LoadUnsafe(ref d, (nuint)((j + 1) * n + c * 4));
                    }
                    Vector128.Min(Vector128.Max((even + odd + r64) >> 7, lo16), hi16).StoreUnsafe(ref g, (nuint)(y * n + c * 4));
                    Vector128.Min(Vector128.Max((even - odd + r64) >> 7, lo16), hi16).StoreUnsafe(ref g, (nuint)((n - 1 - y) * n + c * 4));
                }
            }
        }

        var bdShift = 20 - bitDepth;
        var round = Vector128.Create(1 << (bdShift - 1));
        var max = Vector128.Create((1 << bitDepth) - 1);
        ref var p = ref MemoryMarshal.GetReference(dst);
        for (var y = 0; y < n; y++)
        {
            for (var c = 0; c < n >> 2; c++)
            {
                var sum = Vector128<int>.Zero;
                for (var j = 0; j <= maxX; j++) sum += Vector128.Create(Unsafe.Add(ref g, y * n + j)) * Vector128.LoadUnsafe(ref m, (nuint)(j * n + c * 4));
                ref var target = ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref p, y * stride + c * 4));
                var pred = Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref target)).AsUInt16()).AsInt32();
                var value = Vector128.Min(Vector128.Max(pred + ((sum + round) >> bdShift), Vector128<int>.Zero), max).AsUInt32();
                Unsafe.WriteUnaligned(ref target, Vector128.Narrow(value, value).AsUInt64().ToScalar());
            }
        }
    }

    /// <summary>The Vector256 tier (N ≥ 8): eight columns per vector.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void InverseAddVector256(ReadOnlySpan<int> coeffs, Span<int> scratch, int log2Size, int maxX, int maxY,
        int bitDepth, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        if (n < 8) throw new ArgumentOutOfRangeException(nameof(log2Size));
        CheckBlock(coeffs, scratch, n, maxX, maxY, dst, stride);
        // Every access below is at j * n + c * 8 + (0..7) with j < n and c * 8 + 8 <= n (n is a multiple of 8), or at
        // y * stride + c * 8 + (0..7) with y < n: in range by CheckBlock and the n × n matrices.
        ref var d = ref MemoryMarshal.GetReference(coeffs);
        ref var g = ref MemoryMarshal.GetReference(scratch);
        ref var m = ref MemoryMarshal.GetArrayDataReference(DctN[log2Size - 2]);
        var chunks = (maxX >> 3) + 1;
        var lo16 = Vector256.Create((int)short.MinValue);
        var hi16 = Vector256.Create((int)short.MaxValue);
        var r64 = Vector256.Create(64);

        var lastRow = maxY | 1;
        for (var y = 0; y < n / 2; y++)
        {
            for (var c = 0; c < chunks; c++)
            {
                var even = Vector256<int>.Zero;
                var odd = Vector256<int>.Zero;
                for (var j = 0; j <= lastRow; j += 2)
                {
                    even += Vector256.Create(Unsafe.Add(ref m, j * n + y)) * Vector256.LoadUnsafe(ref d, (nuint)(j * n + c * 8));
                    odd += Vector256.Create(Unsafe.Add(ref m, (j + 1) * n + y)) * Vector256.LoadUnsafe(ref d, (nuint)((j + 1) * n + c * 8));
                }
                Vector256.Min(Vector256.Max((even + odd + r64) >> 7, lo16), hi16).StoreUnsafe(ref g, (nuint)(y * n + c * 8));
                Vector256.Min(Vector256.Max((even - odd + r64) >> 7, lo16), hi16).StoreUnsafe(ref g, (nuint)((n - 1 - y) * n + c * 8));
            }
        }

        var bdShift = 20 - bitDepth;
        var round = Vector256.Create(1 << (bdShift - 1));
        var max = Vector256.Create((1 << bitDepth) - 1);
        ref var p = ref MemoryMarshal.GetReference(dst);
        for (var y = 0; y < n; y++)
        {
            for (var c = 0; c < n >> 3; c++)
            {
                var sum = Vector256<int>.Zero;
                for (var j = 0; j <= maxX; j++) sum += Vector256.Create(Unsafe.Add(ref g, y * n + j)) * Vector256.LoadUnsafe(ref m, (nuint)(j * n + c * 8));
                ref var target = ref Unsafe.Add(ref p, y * stride + c * 8);
                var (predLo, predHi) = Vector128.Widen(Vector128.LoadUnsafe(ref target));
                var pred = Vector256.Create(predLo, predHi).AsInt32();
                var value = Vector256.Min(Vector256.Max(pred + ((sum + round) >> bdShift), Vector256<int>.Zero), max).AsUInt32();
                Vector128.Narrow(value.GetLower(), value.GetUpper()).StoreUnsafe(ref target);
            }
        }
    }

    /// <summary>Transform skip (8-298, 8-299): r = (d &lt;&lt; tsShift + round) &gt;&gt; bdShift, in place.</summary>
    internal static void TransformSkip(Span<int> d, int log2Size, int bitDepth)
    {
        var tsShift = 5 + log2Size;
        var bdShift = 20 - bitDepth;
        var round = 1 << (bdShift - 1);
        var count = 1 << (2 * log2Size);
        for (var i = 0; i < count; i++) d[i] = ((d[i] << tsShift) + round) >> bdShift;
    }

    /// <summary>The reconstruction of (8-325) for a residual that is already in sample units (transform skip, bypass).</summary>
    internal static void AddResidual(ReadOnlySpan<int> residual, int log2Size, int bitDepth, Span<ushort> dst, int stride)
    {
        var n = 1 << log2Size;
        var max = (1 << bitDepth) - 1;
        for (var y = 0; y < n; y++)
        {
            var row = dst.Slice(y * stride, n);
            var res = residual.Slice(y * n, n);
            for (var x = 0; x < n; x++) row[x] = (ushort)Math.Clamp(row[x] + res[x], 0, max);
        }
    }
}
