namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// Double-precision reference inverse DCT, evaluated <b>directly</b> from ITU-T T.81
/// A.3.3 — no separable pass, no fast factorisation:
/// <code>
/// s(y,x) = 1/4 · Σu Σv C(u) C(v) S(v,u) cos((2x+1)uπ/16) cos((2y+1)vπ/16)
/// C(0) = 1/√2, C(k) = 1 otherwise
/// </code>
/// 4096 multiply-adds per block. Measurement only; never shipped.
/// </summary>
internal static class ReferenceIdct
{
    // Basis[x * 8 + u] = C(u) · cos((2x+1)uπ/16)
    private static readonly double[] Basis = BuildBasis();

    private static double[] BuildBasis()
    {
        var b = new double[64];
        for (var x = 0; x < 8; x++)
            for (var u = 0; u < 8; u++)
                b[x * 8 + u] = (u == 0 ? 1 / Math.Sqrt(2) : 1.0) * Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return b;
    }

    /// <summary>Inverse DCT of one natural-order block of <b>dequantized</b> coefficients (no level shift).</summary>
    public static void Inverse(ReadOnlySpan<short> coeffs, Span<double> samples)
    {
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var sum = 0.0;
                for (var v = 0; v < 8; v++)
                {
                    var cy = Basis[y * 8 + v];
                    for (var u = 0; u < 8; u++)
                        sum += Basis[x * 8 + u] * cy * coeffs[v * 8 + u];
                }

                samples[y * 8 + x] = sum / 4;
            }
        }
    }

    /// <summary>Forward DCT (T.81 A.3.3), direct form — used only to build 1180-style test blocks.</summary>
    public static void Forward(ReadOnlySpan<double> samples, Span<double> coeffs)
    {
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0.0;
                for (var y = 0; y < 8; y++)
                {
                    var cy = Basis[y * 8 + v];
                    for (var x = 0; x < 8; x++)
                        sum += Basis[x * 8 + u] * cy * samples[y * 8 + x];
                }

                coeffs[v * 8 + u] = sum / 4;
            }
        }
    }
}
