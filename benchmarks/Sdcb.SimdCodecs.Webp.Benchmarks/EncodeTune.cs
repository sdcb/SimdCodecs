using System.Collections.Concurrent;
using System.Globalization;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// Tuning aid for the lossy encoder: BD-rate against libwebp's default of our encoder under each of
/// several configuration overrides (<c>Name=value,…</c>, as <c>VP8CFG</c>), in one run, with libwebp's
/// curves computed once. Besides the whole sweep (what the gate scores) it reports the low band
/// (q ≤ 60) and the high band (q ≥ 70) separately, and at q90 the byte ratio and PSNR-Y difference.
/// Images are encoded in parallel; the numbers are sizes and qualities only, so that is safe. Not a gate.
/// </summary>
internal static class EncodeTune
{
    private sealed record Point(int Q, long Bytes, double PsnrY, double SsimDb);

    public static int Run(int maxDim, string? filter, IReadOnlyList<string> variants)
    {
        List<EncodeImage> images = EncodeCorpus.Load(maxDim, filter).Where(i => !i.HasAlpha).ToList();
        int[] qs = EncodeRd.DefaultQualities;
        Dictionary<string, List<Point>> reference = Sweep(images, (img, q) => LibWebp.EncodeRgba(img.Rgba, img.Width, img.Height, q), qs);
        Console.WriteLine($"{images.Count} opaque inputs; BD-rate vs libwebp {LibWebp.EncoderVersion()} (method 4); mean over inputs");
        Console.WriteLine();
        Console.WriteLine("| overrides | PSNR-Y all | SSIM-Y all | PSNR-Y q≤60 | SSIM-Y q≤60 | PSNR-Y q≥70 | SSIM-Y q≥70 | q90 bytes | q90 ΔPSNR-Y | s |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (string variant in variants)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            Dictionary<string, List<Point>> ours = Sweep(images, (img, q) => EncodeBenchmark.OursLossy(img, q, variant), qs);
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
            double[] sums = new double[6];
            long refBytes90 = 0, ourBytes90 = 0;
            double dPsnr90 = 0;
            foreach (EncodeImage image in images)
            {
                List<Point> a = reference[image.Name], b = ours[image.Name];
                sums[0] += Bd(a, b, _ => true, p => p.PsnrY);
                sums[1] += Bd(a, b, _ => true, p => p.SsimDb);
                sums[2] += Bd(a, b, q => q <= 60, p => p.PsnrY);
                sums[3] += Bd(a, b, q => q <= 60, p => p.SsimDb);
                sums[4] += Bd(a, b, q => q >= 70, p => p.PsnrY);
                sums[5] += Bd(a, b, q => q >= 70, p => p.SsimDb);
                Point ra = a.First(p => p.Q == 90), rb = b.First(p => p.Q == 90);
                refBytes90 += ra.Bytes;
                ourBytes90 += rb.Bytes;
                dPsnr90 += rb.PsnrY - ra.PsnrY;
            }

            string cells = string.Join(" | ", sums.Select(v => Pct(v / images.Count)));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {(variant.Length == 0 ? "(default)" : variant)} | {cells} | {(double)ourBytes90 / refBytes90:F3}x | {dPsnr90 / images.Count:+0.00;-0.00} dB | {seconds:F0} |"));
        }

        return 0;
    }

    private static double Bd(List<Point> a, List<Point> b, Func<int, bool> band, Func<Point, double> metric) =>
        EncodeRd.BdRate(a.Where(p => band(p.Q)).Select(p => (p.Bytes, metric(p))), b.Where(p => band(p.Q)).Select(p => (p.Bytes, metric(p))));

    private static Dictionary<string, List<Point>> Sweep(List<EncodeImage> images, Func<EncodeImage, int, byte[]> encode, int[] qs)
    {
        var result = new ConcurrentDictionary<string, List<Point>>();
        Parallel.ForEach(images, image =>
        {
            var points = new List<Point>();
            foreach (int q in qs)
            {
                byte[] webp = encode(image, q);
                (double psnrY, _, double ssimY) = Quality.Score(image, EncodeBenchmark.DecodeWithLibwebp(webp, image));
                points.Add(new Point(q, webp.Length, psnrY, -10 * Math.Log10(Math.Max(1 - ssimY, 1e-10))));
            }

            result[image.Name] = points;
        });
        return new Dictionary<string, List<Point>>(result);
    }

    private static string Pct(double v) => double.IsNaN(v)
        ? "n/a"
        : string.Create(CultureInfo.InvariantCulture, $"{(v >= 0 ? "+" : "")}{v * 100:F2}%");
}
