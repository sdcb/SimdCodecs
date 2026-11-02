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

    /// <summary>
    /// Inputs with alpha: per input and quality, the ALPH and VP8 chunk sizes of libwebp's file and of
    /// ours under each override set, and the visible PSNR-RGB, to tell the alpha plane's cost from the colour's.
    /// </summary>
    public static int RunAlpha(int maxDim, string? filter, IReadOnlyList<string> variants)
    {
        List<EncodeImage> images = EncodeCorpus.WithAlpha(EncodeCorpus.Load(maxDim, filter));
        Console.WriteLine("| input | q | encoder | ALPH | VP8 | visible PSNR-RGB |");
        Console.WriteLine("|---|---:|---|---:|---:|---:|");
        foreach (EncodeImage image in images)
        {
            foreach (int q in new[] { 50, 75, 90 })
            {
                LibWebp.EncodeRgba(image.Rgba, image.Width, image.Height, q); // warm
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                byte[] theirs = LibWebp.EncodeRgba(image.Rgba, image.Width, image.Height, q);
                double t = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                Row(image, q, string.Create(CultureInfo.InvariantCulture, $"libwebp ({t:F0} ms)"), theirs);
                foreach (string variant in variants)
                {
                    EncodeBenchmark.OursLossy(image, q, variant); // warm
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    byte[] webp = EncodeBenchmark.OursLossy(image, q, variant);
                    double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Row(image, q, string.Create(CultureInfo.InvariantCulture, $"{(variant.Length == 0 ? "ours" : variant)} ({ms:F0} ms)"), webp);
                }
            }
        }

        return 0;

        static void Row(EncodeImage image, int q, string name, byte[] webp)
        {
            (double psnr, bool exact) = EncodeRd.ScoreVisible(image, EncodeBenchmark.DecodeWithLibwebp(webp, image));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {image.Name} | {q} | {name} | {ChunkSize(webp, "ALPH")} | {ChunkSize(webp, "VP8 ")} | {psnr:F2}{(exact ? "" : " (alpha differs)")} |"));
        }
    }

    private static int ChunkSize(byte[] webp, string fourCc)
    {
        for (int at = 12; at + 8 <= webp.Length;)
        {
            int size = BitConverter.ToInt32(webp, at + 4);
            if (System.Text.Encoding.ASCII.GetString(webp, at, 4) == fourCc)
            {
                return size;
            }

            at += 8 + size + (size & 1);
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
