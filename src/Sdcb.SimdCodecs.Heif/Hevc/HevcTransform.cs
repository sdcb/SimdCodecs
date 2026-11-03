namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// Scaling (§8.6.3) and inverse transformation (§8.6.2, §8.6.4) of one transform block.
/// </summary>
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

    /// <summary>
    /// Scales the quantized levels in <paramref name="coeffs"/> (row-major N×N, index y * N + x) in place, per (8-309).
    /// <paramref name="scalingFactors"/> is the m[x][y] array of the block (row-major) or empty for the flat m = 16.
    /// </summary>
    internal static void Dequantize(Span<int> coeffs, int log2Size, int qp, int bitDepth, ReadOnlySpan<byte> scalingFactors)
    {
        var bdShift = bitDepth + log2Size - 5;
        var scale = (long)LevelScale[qp % 6] << (qp / 6);
        var round = 1L << (bdShift - 1);
        var count = 1 << (2 * log2Size);
        for (var i = 0; i < count; i++)
        {
            var level = coeffs[i];
            if (level == 0) continue;
            var m = scalingFactors.IsEmpty ? 16 : scalingFactors[i];
            var value = (level * m * scale + round) >> bdShift;
            coeffs[i] = (int)Math.Clamp(value, short.MinValue, short.MaxValue);
        }
    }

    /// <summary>
    /// Inverse transform of the scaled coefficients <paramref name="d"/> into residuals (8-299, 8-314):
    /// columns first, a clip to 16 bits after the first stage, then rows, then the final bdShift rounding.
    /// <paramref name="residual"/> must not overlap <paramref name="d"/>.
    /// </summary>
    internal static void Inverse(ReadOnlySpan<int> d, int log2Size, bool dst4, int bitDepth, Span<int> residual)
    {
        var n = 1 << log2Size;
        Span<int> g = stackalloc int[n * n];
        ReadOnlySpan<sbyte> matrix = dst4 ? Dst : Dct;
        var matrixStride = dst4 ? 4 : 32;
        var step = dst4 ? 1 : 32 >> log2Size;

        // Stage 1 (vertical): e[x][y] = Σj M[j][y] · d[x][j]; g = Clip3(-32768, 32767, (e + 64) >> 7).
        for (var x = 0; x < n; x++)
        {
            // Highest non-zero row in this column, so zero tails cost nothing.
            var last = n - 1;
            while (last >= 0 && d[last * n + x] == 0) last--;
            for (var y = 0; y < n; y++)
            {
                var sum = 0;
                for (var j = 0; j <= last; j++) sum += matrix[j * step * matrixStride + y] * d[j * n + x];
                g[y * n + x] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }

        // Stage 2 (horizontal) and the final shift (8-299) with bdShift = 20 - bitDepth.
        var bdShift = 20 - bitDepth;
        var round = 1 << (bdShift - 1);
        for (var y = 0; y < n; y++)
        {
            var row = g.Slice(y * n, n);
            var last = n - 1;
            while (last >= 0 && row[last] == 0) last--;
            for (var x = 0; x < n; x++)
            {
                var sum = 0;
                for (var j = 0; j <= last; j++) sum += matrix[j * step * matrixStride + x] * row[j];
                residual[y * n + x] = (sum + round) >> bdShift;
            }
        }
    }

    /// <summary>Transform skip (8-298, 8-299): r = (d &lt;&lt; tsShift + round) &gt;&gt; bdShift.</summary>
    internal static void TransformSkip(ReadOnlySpan<int> d, int log2Size, int bitDepth, Span<int> residual)
    {
        var tsShift = 5 + log2Size;
        var bdShift = 20 - bitDepth;
        var round = 1 << (bdShift - 1);
        var count = 1 << (2 * log2Size);
        for (var i = 0; i < count; i++) residual[i] = ((d[i] << tsShift) + round) >> bdShift;
    }
}
