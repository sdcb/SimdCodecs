using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// Encodes the corpus with each VP8L stage disabled in turn, comparing size and speed against the
/// full encoder to attribute where time and bits go. Useful before optimising to know which stage
/// is the bottleneck and which has the most size headroom.
/// </summary>
internal static class EncodeAblate
{
    private static readonly (string Label, Vp8LAblation Flag)[] Stages =
    [
        ("no-palette",        Vp8LAblation.NoPalette),
        ("no-subgreen",       Vp8LAblation.NoSubtractGreen),
        ("no-predictor",      Vp8LAblation.NoPredictor),
        ("no-cross-color",    Vp8LAblation.NoCrossColor),
        ("no-cache",          Vp8LAblation.NoCache),
        ("no-backref",        Vp8LAblation.NoBackwardRefs),
        ("no-clustering",     Vp8LAblation.NoClustering),
        ("no-optimal-parse",  Vp8LAblation.NoOptimalParse),
    ];

    public static int Run(string mode, int reps, int maxDim, string? filter)
    {
        if (mode != "lossless")
        {
            Console.Error.WriteLine("ablate currently only supports --mode lossless");
            return 1;
        }

        var sb = new StringBuilder();
        void Line(string s = "") { Console.WriteLine(s); sb.AppendLine(s); }

        Line(MachineInfo.Describe().TrimEnd());
        List<EncodeImage> images = EncodeCorpus.Load(maxDim, filter);
        Line($"- inputs: {images.Count} images, {images.Sum(i => i.Pixels) / 1e6:F2} MP, max-dim {maxDim}; median of {reps}");
        Line($"- effort: 6 (default); each row disables one stage and keeps everything else on");
        Line();

        // Full encoder is the reference.
        var (fullBytes, fullSec) = Measure(images, reps, Vp8LAblation.None);

        // Headers.
        Line("| stage disabled | MB | bpp | size vs full | MP/s | time vs full | size change |");
        Line("|---|---:|---:|---:|---:|---:|---|");

        // Full row.
        double totalPixels = images.Sum(i => (double)i.Pixels);
        string fullRow = FormatRow("(full)", fullBytes, fullSec, fullBytes, fullSec, totalPixels);
        Line(fullRow);

        foreach ((string label, Vp8LAblation flag) in Stages)
        {
            var (bytes, sec) = Measure(images, reps, flag);
            Line(FormatRow(label, bytes, sec, fullBytes, fullSec, totalPixels));
        }

        Line();
        // Per-image breakdown for the most impactful stage.
        Line("#### Per-image bytes: full vs each stage disabled");
        Line();
        var header = "| input | full | " + string.Join(" | ", Stages.Select(s => s.Label)) + " |";
        Line(header);
        Line("|---|---:|" + string.Concat(Stages.Select(_ => "---:|")));
        foreach (EncodeImage image in images)
        {
            long fullB = MeasureOne(image, Vp8LAblation.None);
            var cols = Stages.Select(s => MeasureOne(image, s.Flag).ToString(CultureInfo.InvariantCulture));
            Line($"| {image.Name} | {fullB} | {string.Join(" | ", cols)} |");
        }

        Directory.CreateDirectory(BenchPaths.Work);
        string file = Path.Combine(BenchPaths.Work, "encode-ablate.md");
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine($"[ablate] {file}");
        return 0;
    }

    private static string FormatRow(string label, long bytes, double sec, long refBytes, double refSec, double totalPixels)
    {
        double mpps = totalPixels / 1e6 / sec;
        double sizeRatio = (double)bytes / refBytes;
        double speedRatio = refSec / sec;
        long delta = bytes - refBytes;
        string change = delta == 0 ? "—"
            : string.Create(CultureInfo.InvariantCulture,
                $"{(delta > 0 ? "+" : "")}{delta / 1e3:F1} KB ({(delta > 0 ? "+" : "")}{(sizeRatio - 1) * 100:F2}%)");
        return string.Create(CultureInfo.InvariantCulture,
            $"| {label} | {bytes / 1e6:F3} | {bytes * 8.0 / totalPixels:F3} | {sizeRatio:F4}x | {mpps:F2} | {speedRatio:F3}x | {change} |");
    }

    private static (long Bytes, double Seconds) Measure(List<EncodeImage> images, int reps, Vp8LAblation ablation)
    {
        // Warm up.
        for (int i = 0; i < 3; i++)
        {
            WebpEncoder.EncodeLossless(images[^1].Rgba, images[^1].Width, images[^1].Height,
                PixelFormat.Rgba32, null, 0, ablation);
        }

        long totalBytes = 0;
        double totalSec = 0;
        foreach (EncodeImage image in images)
        {
            var times = new double[Math.Max(1, reps)];
            for (int r = 0; r < times.Length; r++)
            {
                long t = Stopwatch.GetTimestamp();
                WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height,
                    PixelFormat.Rgba32, null, 0, ablation);
                times[r] = Stopwatch.GetElapsedTime(t).TotalSeconds;
            }

            Array.Sort(times);
            totalBytes += MeasureOne(image, ablation);
            totalSec += times[times.Length / 2];
            Console.WriteLine($"[ablate] {ablation}: {image.Name} done");
        }

        return (totalBytes, totalSec);
    }

    private static long MeasureOne(EncodeImage image, Vp8LAblation ablation) =>
        WebpEncoder.EncodeLossless(image.Rgba, image.Width, image.Height,
            PixelFormat.Rgba32, null, 0, ablation).Length;
}
