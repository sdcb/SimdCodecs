using System.Globalization;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>A lossy contestant swept over a quality scale of its own.</summary>
internal sealed record RdContestant(string Name, Func<EncodeImage, int, byte[]> Encode, int[] Qualities);

/// <summary>
/// Rate-distortion comparison of lossy encoders. Each contestant is swept over its quality scale;
/// every output is decoded by libwebp and scored against the source. Contestants are compared by
/// the Bjøntegaard delta rate (VCEG-M33, 2001): the mean bitrate difference at equal quality over
/// the quality range both curves cover, here with a shape-preserving piecewise-cubic interpolation
/// (Fritsch and Carlson, 1980) of log rate as a function of quality. A BD-rate of −3% means 3%
/// fewer bytes than the reference for the same quality. Because the comparison is at equal quality,
/// the contestants' quality scales need not agree.
/// <para>Opaque inputs are scored by PSNR-Y and by SSIM-Y in decibels (−10·log10(1 − SSIM)); inputs
/// with alpha by the PSNR of the RGB of visible (alpha &gt; 0) pixels, and the alpha must come back
/// exactly (lossless alpha) for the contestant to count as exact.</para>
/// </summary>
internal static class EncodeRd
{
    public static readonly int[] DefaultQualities = [20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95];

    private sealed class Point
    {
        public long Bytes;
        public double PsnrY, SsimDb, PsnrVisible;
        public bool AlphaExact = true;
    }

    /// <summary>Runs the sweep; the first contestant is the reference. Returns the per-contestant mean BD-rates.</summary>
    public static Dictionary<string, (double PsnrY, double SsimY, double Visible, bool AlphaExact)> Run(
        string title, List<EncodeImage> images, List<RdContestant> contestants, bool alpha, Action<string> line)
    {
        line("");
        line($"### {title}: rate-distortion (BD-rate vs {contestants[0].Name})");
        line("");
        var curves = new Dictionary<(string Image, string Contestant), List<Point>>();
        foreach (EncodeImage image in images)
        {
            foreach (RdContestant c in contestants)
            {
                var points = new List<Point>();
                foreach (int q in c.Qualities)
                {
                    byte[] webp = c.Encode(image, q);
                    byte[] decoded = EncodeBenchmark.DecodeWithLibwebp(webp, image);
                    var p = new Point { Bytes = webp.Length };
                    if (alpha)
                    {
                        (p.PsnrVisible, p.AlphaExact) = ScoreVisible(image, decoded);
                    }
                    else
                    {
                        (double psnrY, _, double ssimY) = Quality.Score(image, decoded);
                        p.PsnrY = psnrY;
                        p.SsimDb = -10 * Math.Log10(Math.Max(1 - ssimY, 1e-10));
                    }

                    points.Add(p);
                }

                curves[(image.Name, c.Name)] = points;
            }

            Console.WriteLine($"[encode] {title} RD: {image.Name} done");
        }

        string reference = contestants[0].Name;
        var result = new Dictionary<string, (double, double, double, bool)>();
        line(alpha
            ? "| encoder | BD-rate (visible PSNR-RGB) | alpha exact | bpp range |"
            : "| encoder | BD-rate (PSNR-Y) | BD-rate (SSIM-Y) | bpp range |");
        line(alpha ? "|---|---:|---|---|" : "|---|---:|---:|---|");
        var perImage = new List<string>();
        foreach (RdContestant c in contestants)
        {
            double sumP = 0, sumS = 0, sumV = 0;
            int count = 0;
            bool exact = true;
            double minBpp = double.MaxValue, maxBpp = 0;
            var cells = new List<string>();
            foreach (EncodeImage image in images)
            {
                List<Point> mine = curves[(image.Name, c.Name)], refs = curves[(image.Name, reference)];
                foreach (Point p in mine)
                {
                    double bpp = p.Bytes * 8.0 / image.Pixels;
                    minBpp = Math.Min(minBpp, bpp);
                    maxBpp = Math.Max(maxBpp, bpp);
                    exact &= p.AlphaExact;
                }

                if (alpha)
                {
                    double v = BdRate(refs.Select(p => (p.Bytes, p.PsnrVisible)), mine.Select(p => (p.Bytes, p.PsnrVisible)));
                    sumV += v;
                    cells.Add(Pct(v));
                }
                else
                {
                    double bp = BdRate(refs.Select(p => (p.Bytes, p.PsnrY)), mine.Select(p => (p.Bytes, p.PsnrY)));
                    double bs = BdRate(refs.Select(p => (p.Bytes, p.SsimDb)), mine.Select(p => (p.Bytes, p.SsimDb)));
                    sumP += bp;
                    sumS += bs;
                    cells.Add($"{Pct(bp)} / {Pct(bs)}");
                }

                count++;
            }

            var mean = (sumP / count, sumS / count, sumV / count, exact);
            result[c.Name] = mean;
            string range = string.Create(CultureInfo.InvariantCulture, $"{minBpp:F2}–{maxBpp:F2}");
            line(alpha
                ? $"| {c.Name} | {Pct(mean.Item3)} | {(exact ? "yes" : "no")} | {range} |"
                : $"| {c.Name} | {Pct(mean.Item1)} | {Pct(mean.Item2)} | {range} |");
            perImage.Add($"| {c.Name} | {string.Join(" | ", cells)} |");
        }

        line("");
        line(alpha ? "BD-rate per input (visible PSNR-RGB):" : "BD-rate per input (PSNR-Y / SSIM-Y):");
        line("");
        line("| encoder | " + string.Join(" | ", images.Select(i => i.Name)) + " |");
        line("|---|" + string.Concat(images.Select(_ => "---:|")));
        foreach (string row in perImage)
        {
            line(row);
        }

        return result;
    }

    private static string Pct(double v) => double.IsNaN(v)
        ? "n/a"
        : string.Create(CultureInfo.InvariantCulture, $"{(v >= 0 ? "+" : "")}{v * 100:F2}%");

    /// <summary>PSNR of the RGB of visible pixels, and whether every alpha value came back.</summary>
    internal static (double Psnr, bool AlphaExact) ScoreVisible(EncodeImage src, byte[] decoded)
    {
        byte[] s = src.Rgba;
        double se = 0;
        long n = 0;
        bool exact = true;
        for (int i = 0; i < s.Length; i += 4)
        {
            exact &= s[i + 3] == decoded[i + 3];
            if (s[i + 3] == 0)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                int d = s[i + c] - decoded[i + c];
                se += d * d;
            }

            n += 3;
        }

        return (n == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / Math.Max(se / n, 1e-12)), exact);
    }

    /// <summary>
    /// Bjøntegaard delta rate of <paramref name="test"/> against <paramref name="reference"/>: the mean
    /// of exp(log rate_test − log rate_ref) − 1 over the shared quality interval. NaN when the curves
    /// do not overlap.
    /// </summary>
    internal static double BdRate(IEnumerable<(long Bytes, double Quality)> reference, IEnumerable<(long Bytes, double Quality)> test)
    {
        (double[] qa, double[] ra) = Curve(reference);
        (double[] qb, double[] rb) = Curve(test);
        if (qa.Length < 2 || qb.Length < 2)
        {
            return double.NaN;
        }

        double lo = Math.Max(qa[0], qb[0]), hi = Math.Min(qa[^1], qb[^1]);
        if (!(hi > lo))
        {
            return double.NaN;
        }

        double[] da = Slopes(qa, ra), db = Slopes(qb, rb);
        const int Steps = 400;
        double sum = 0;
        for (int k = 0; k <= Steps; k++)
        {
            double q = lo + (hi - lo) * k / Steps;
            double w = k == 0 || k == Steps ? 0.5 : 1;
            sum += w * (Eval(qb, rb, db, q) - Eval(qa, ra, da, q));
        }

        return Math.Exp(sum / Steps) - 1;
    }

    /// <summary>The curve as quality → ln(rate), sorted, keeping only points that raise quality.</summary>
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

        return (q.ToArray(), r.ToArray());
    }

    /// <summary>Fritsch–Carlson monotone slopes at the knots.</summary>
    private static double[] Slopes(double[] x, double[] y)
    {
        int n = x.Length;
        var delta = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            delta[k] = (y[k + 1] - y[k]) / (x[k + 1] - x[k]);
        }

        var d = new double[n];
        d[0] = delta[0];
        d[n - 1] = delta[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            if (delta[k - 1] * delta[k] <= 0)
            {
                d[k] = 0;
            }
            else
            {
                double h0 = x[k] - x[k - 1], h1 = x[k + 1] - x[k];
                double w1 = 2 * h1 + h0, w2 = h1 + 2 * h0;
                d[k] = (w1 + w2) / (w1 / delta[k - 1] + w2 / delta[k]);
            }
        }

        return d;
    }

    /// <summary>The cubic Hermite interpolant at <paramref name="t"/> (inside the knot range).</summary>
    private static double Eval(double[] x, double[] y, double[] d, double t)
    {
        int k = 0;
        while (k < x.Length - 2 && t > x[k + 1])
        {
            k++;
        }

        double h = x[k + 1] - x[k], s = (t - x[k]) / h;
        double h00 = (1 + 2 * s) * (1 - s) * (1 - s), h10 = s * (1 - s) * (1 - s);
        double h01 = s * s * (3 - 2 * s), h11 = s * s * (s - 1);
        return h00 * y[k] + h10 * h * d[k] + h01 * y[k + 1] + h11 * h * d[k + 1];
    }
}
