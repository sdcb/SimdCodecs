using Sdcb.SimdCodecs.Png;

namespace Sdcb.SimdCodecs.Quantize.Benchmarks;

/// <summary>
/// How close a quantized image is to its source, and what it costs as a PNG. SSIM is not what any of the quantizers
/// minimises, so a quantizer cannot score well on it merely by agreeing with itself.
/// </summary>
internal static class Metrics
{
    /// <summary>Distinct RGBA values (all fully transparent pixels count as one): must not exceed what was asked for.</summary>
    public static int DistinctColors(byte[] rgba)
    {
        var set = new HashSet<uint>();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            set.Add(rgba[i + 3] == 0 ? 0 : BitConverter.ToUInt32(rgba, i));
        }

        return set.Count;
    }

    /// <summary>
    /// The quantized image written by one common PNG writer (ours: lossless reduction to a palette, default level), so
    /// sizes compare the palettes and index maps rather than the competitors' deflate. Fails unless the result is indexed (or 8-bit grey).
    /// </summary>
    public static long CommonBytes(byte[] rgba, int width, int height)
    {
        byte[] png = PngWriter.Encode(Canonical(rgba), width, height, new PngWriteOptions { LosslessReduction = true });
        // A grey image may reduce to grey (type 0 / 4) instead of a palette: also lossless, and no larger.
        if (png[25] is not (0 or 3 or 4) || png[24] > 8) throw new InvalidOperationException($"common writer produced colour type {png[25]} at {png[24]} bits");
        return png.Length;
    }

    // Fully transparent pixels to one value, so colours hidden under alpha 0 do not cost a palette entry.
    private static byte[] Canonical(byte[] rgba)
    {
        byte[] copy = (byte[])rgba.Clone();
        for (int i = 0; i < copy.Length; i += 4)
        {
            if (copy[i + 3] == 0) copy[i] = copy[i + 1] = copy[i + 2] = 0;
        }

        return copy;
    }

    /// <summary>
    /// Mean luma SSIM (Wang et al. 2004, 8×8 windows at a stride of 4) of the image composited over black and over
    /// white, averaged; for an opaque image both are the plain SSIM. Reported as dB: −10·log10(1 − SSIM).
    /// </summary>
    public static double SsimDb(byte[] source, byte[] test, int width, int height, bool hasAlpha)
    {
        double s = hasAlpha
            ? (Ssim(Luma(source, 0), Luma(test, 0), width, height) + Ssim(Luma(source, 255), Luma(test, 255), width, height)) / 2
            : Ssim(Luma(source, 0), Luma(test, 0), width, height);
        return -10 * Math.Log10(Math.Max(1 - s, 1e-10));
    }

    /// <summary>
    /// RMS distance in OKLab (B. Ottosson, "A perceptual color space for image processing", 2020) of premultiplied
    /// colour plus alpha, (L·α, a·α, b·α, α), ×100. A transparent pixel's colour does not count.
    /// </summary>
    public static double OklabRms(byte[] source, byte[] test)
    {
        double sum = 0;
        int n = source.Length / 4;
        for (int i = 0; i < n; i++)
        {
            (double l0, double a0, double b0) = Oklab(source, 4 * i);
            (double l1, double a1, double b1) = Oklab(test, 4 * i);
            double alpha0 = source[4 * i + 3] / 255.0, alpha1 = test[4 * i + 3] / 255.0;
            double dl = l0 * alpha0 - l1 * alpha1, da = a0 * alpha0 - a1 * alpha1, db = b0 * alpha0 - b1 * alpha1, dA = alpha0 - alpha1;
            sum += dl * dl + da * da + db * db + dA * dA;
        }

        return 100 * Math.Sqrt(sum / n);
    }

    /// <summary>
    /// <see cref="OklabRms"/> after blurring both images (a 3×3 box twice, about a Gaussian of σ 1.15 px, on
    /// premultiplied colour): what the eye sees from a normal distance, where dithering's noise averages out and
    /// banding does not. The measure of what a dither is for; SSIM and plain OKLab error penalise the noise instead.
    /// </summary>
    public static double BlurredOklabRms(byte[] source, byte[] test, int width, int height) =>
        OklabRms(Blur(source, width, height), Blur(test, width, height));

    private static byte[] Blur(byte[] rgba, int width, int height)
    {
        var premultiplied = new float[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            float a = rgba[i + 3] / 255f;
            premultiplied[i] = rgba[i] * a;
            premultiplied[i + 1] = rgba[i + 1] * a;
            premultiplied[i + 2] = rgba[i + 2] * a;
            premultiplied[i + 3] = rgba[i + 3];
        }

        float[] once = Box(premultiplied, width, height), twice = Box(once, width, height);
        var result = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            float a = twice[i + 3];
            result[i + 3] = (byte)Math.Clamp(MathF.Round(a), 0, 255);
            float k = a > 0 ? 255 / a : 0;
            for (int c = 0; c < 3; c++) result[i + c] = (byte)Math.Clamp(MathF.Round(twice[i + c] * k), 0, 255);
        }

        return result;
    }

    // 3×3 mean, edges clamped.
    private static float[] Box(float[] v, int width, int height)
    {
        var o = new float[v.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, height - 1);
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, width - 1);
                            sum += v[(yy * width + xx) * 4 + c];
                        }
                    }

                    o[(y * width + x) * 4 + c] = sum / 9;
                }
            }
        }

        return o;
    }

    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(v =>
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }).ToArray();

    // sRGB (IEC 61966-2-1) → linear → OKLab, with the matrices published in Ottosson's post.
    private static (double L, double A, double B) Oklab(byte[] rgba, int o)
    {
        double r = Linear[rgba[o]], g = Linear[rgba[o + 1]], b = Linear[rgba[o + 2]];
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    private static float[] Luma(byte[] rgba, int background)
    {
        var y = new float[rgba.Length / 4];
        for (int i = 0; i < y.Length; i++)
        {
            float a = rgba[4 * i + 3] / 255f;
            float r = rgba[4 * i] * a + background * (1 - a), g = rgba[4 * i + 1] * a + background * (1 - a), b = rgba[4 * i + 2] * a + background * (1 - a);
            y[i] = 0.299f * r + 0.587f * g + 0.114f * b;
        }

        return y;
    }

    private static double Ssim(float[] a, float[] b, int w, int h)
    {
        const double c1 = 6.5025, c2 = 58.5225;
        double sum = 0;
        int count = 0;
        for (int y = 0; y + 8 <= h; y += 4)
        {
            for (int x = 0; x + 8 <= w; x += 4)
            {
                double ma = 0, mb = 0;
                for (int j = 0; j < 8; j++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int k = (y + j) * w + x + i;
                        ma += a[k];
                        mb += b[k];
                    }
                }

                ma /= 64;
                mb /= 64;
                double va = 0, vb = 0, cov = 0;
                for (int j = 0; j < 8; j++)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int k = (y + j) * w + x + i;
                        double da = a[k] - ma, db = b[k] - mb;
                        va += da * da;
                        vb += db * db;
                        cov += da * db;
                    }
                }

                va /= 63;
                vb /= 63;
                cov /= 63;
                sum += (2 * ma * mb + c1) * (2 * cov + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                count++;
            }
        }

        return count == 0 ? 1 : sum / count;
    }

    /// <summary>
    /// Bjøntegaard delta rate of <paramref name="test"/> against <paramref name="reference"/>: the mean change in bytes
    /// at equal quality over the qualities both curves cover (−0.05 = 5% smaller). Piecewise-linear in
    /// (quality, ln bytes); NaN when the curves do not overlap.
    /// </summary>
    public static double BdRate(IEnumerable<(long Bytes, double Quality)> reference, IEnumerable<(long Bytes, double Quality)> test)
    {
        (double[] qa, double[] ra) = Curve(reference);
        (double[] qb, double[] rb) = Curve(test);
        if (qa.Length < 2 || qb.Length < 2) return double.NaN;
        double lo = Math.Max(qa[0], qb[0]), hi = Math.Min(qa[^1], qb[^1]);
        if (!(hi > lo)) return double.NaN;
        const int Steps = 400;
        double sum = 0;
        for (int k = 0; k <= Steps; k++)
        {
            double q = lo + (hi - lo) * k / Steps;
            double w = k == 0 || k == Steps ? 0.5 : 1;
            sum += w * (Eval(qb, rb, q) - Eval(qa, ra, q));
        }

        return Math.Exp(sum / Steps) - 1;
    }

    // Quality → ln(bytes), sorted by size, keeping only points that raise quality.
    private static (double[] Q, double[] R) Curve(IEnumerable<(long Bytes, double Quality)> points)
    {
        var q = new List<double>();
        var r = new List<double>();
        foreach ((long bytes, double quality) in points.OrderBy(p => p.Bytes))
        {
            if (q.Count == 0 || quality > q[^1] + 1e-9)
            {
                q.Add(quality);
                r.Add(Math.Log(bytes));
            }
        }

        return ([.. q], [.. r]);
    }

    private static double Eval(double[] q, double[] r, double x)
    {
        int i = 1;
        while (i < q.Length - 1 && q[i] < x) i++;
        double t = (x - q[i - 1]) / (q[i] - q[i - 1]);
        return r[i - 1] + t * (r[i] - r[i - 1]);
    }
}
