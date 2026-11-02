using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// Encode A/B: current build vs the cached baseline vs libwebp, all in the same process and
/// interleaved image by image. Reports size ratio and speed ratio to both baselines.
/// </summary>
internal static class EncodeAb
{
    public static int Run(string mode, int reps, int maxDim, string? filter, string? cwebpPath, bool noCwebp)
    {
        var sb = new StringBuilder();
        void Line(string s = "") { Console.WriteLine(s); sb.AppendLine(s); }

        Line(MachineInfo.Describe().TrimEnd());
        string? cwebp = noCwebp ? null : Cwebp.Find(cwebpPath);
        Line($"- libwebp encoder version: {LibWebp.EncoderVersion()}; cwebp: {(cwebp is null ? "not found" : $"{Cwebp.Version(cwebp)} ({cwebp})")}");
        string baselineLabel = BaselineDecoder.Description is null ? "no baseline" : "baseline " + BaselineDecoder.Description;
        Line($"- baseline: {baselineLabel}");
        List<EncodeImage> images = EncodeCorpus.Load(maxDim, filter);
        Line($"- inputs: {images.Count} images, {images.Sum(i => i.Pixels) / 1e6:F2} MP, max-dim {maxDim}; median of {reps}");
        Line();

        bool ok = true;
        foreach (string m in mode == "all" ? EncodeBenchmark.Modes : [mode])
        {
            ok &= RunMode(m, images, reps, cwebp, Line);
        }

        Directory.CreateDirectory(BenchPaths.Work);
        string file = Path.Combine(BenchPaths.Work, "encode-ab.md");
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine($"[encode-ab] {file}");
        return ok ? 0 : 1;
    }

    private static bool RunMode(string mode, List<EncodeImage> images, int reps, string? cwebp, Action<string> line)
    {
        bool lossless = mode == "lossless";

        // Build contestants: libwebp, optional baseline, ours (current).
        var contestants = new List<EncodeBenchmark.Contestant>();

        static (byte[], double) Time(Func<byte[]> f)
        {
            long t = Stopwatch.GetTimestamp();
            byte[] b = f();
            return (b, Stopwatch.GetElapsedTime(t).TotalSeconds);
        }

        contestants.Add(new EncodeBenchmark.Contestant("libwebp",
            img => Time(() => lossless
                ? LibWebp.EncodeLosslessRgba(img.Rgba, img.Width, img.Height)
                : LibWebp.EncodeRgba(img.Rgba, img.Width, img.Height, 75)),
            InProcess: true));

        // Baseline encoder: locate via reflection so a baseline without that method is graceful.
        Func<EncodeImage, byte[]>? baselineEncode = lossless
            ? BaselineEncoder.Find("EncodeLossless", "WebpLosslessEncodeOptions", ("Effort", 6))
            : BaselineEncoder.Find("EncodeLossy", "WebpLossyEncodeOptions", ("Quality", 75));
        if (baselineEncode is not null)
        {
            contestants.Add(new EncodeBenchmark.Contestant("baseline",
                img => Time(() => baselineEncode(img)), InProcess: true));
        }

        if (lossless)
        {
            foreach (int effort in EncodeBenchmark.OurEfforts)
            {
                int e = effort;
                contestants.Add(new EncodeBenchmark.Contestant(EncodeBenchmark.OursName(e),
                    img => Time(() => EncodeBenchmark.OursLossless(img, e)), InProcess: true));
            }
        }
        else
        {
            // The lossy encoder lands in a later commit; until then only libwebp and the baseline run.
            Console.WriteLine("[encode-ab] lossy: no lossy encoder in this build yet");
        }

        // Warm up in-process contestants.
        var warm = Stopwatch.StartNew();
        do
        {
            foreach (var (_, encode, inProcess) in contestants.Where(c => c.InProcess))
            {
                encode(images[^1]);
            }
        }
        while (warm.Elapsed.TotalSeconds < 1.5);

        line($"### {mode} (A/B)");
        line("");

        var tallies = contestants.ToDictionary(c => c.Name, _ => new AbTally());
        var perImage = new Dictionary<(string, string), (long Bytes, double Seconds)>();
        foreach (EncodeImage image in images)
        {
            foreach (EncodeBenchmark.Contestant c in contestants)
            {
                var (name, encode, _) = (c.Name, c.Encode, c.InProcess);
                var t = tallies[name];
                var times = new double[Math.Max(1, reps)];
                (byte[] webp, times[0]) = encode(image);
                for (int r = 1; r < times.Length; r++) times[r] = encode(image).Item2;
                Array.Sort(times);
                perImage[(image.Name, c.Name)] = (webp.Length, times[times.Length / 2]);
                t.Bytes += webp.Length;
                t.Seconds += times[times.Length / 2];
                t.Pixels += image.Pixels;
            }

            Console.WriteLine($"[encode-ab] {mode}: {image.Name} done");
        }

        AbTally libwebpTally = tallies["libwebp"];
        AbTally? baselineTally = tallies.TryGetValue("baseline", out AbTally? bt) ? bt : null;

        line("| encoder | MB | bpp | vs libwebp size | vs libwebp speed | vs baseline size | vs baseline speed |");
        line("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var (name, _, _) in contestants)
        {
            var t = tallies[name];
            string vLibSize = string.Create(CultureInfo.InvariantCulture, $"{(double)t.Bytes / libwebpTally.Bytes:F4}x");
            string vLibSpd = string.Create(CultureInfo.InvariantCulture, $"{libwebpTally.Seconds / t.Seconds:F3}x");
            string vBaseSize = baselineTally is null ? "—" : string.Create(CultureInfo.InvariantCulture, $"{(double)t.Bytes / baselineTally.Bytes:F4}x");
            string vBaseSpd = baselineTally is null ? "—" : string.Create(CultureInfo.InvariantCulture, $"{baselineTally.Seconds / t.Seconds:F3}x");
            string mbps = string.Create(CultureInfo.InvariantCulture,
                $"{t.Bytes / 1e6:F3} | {t.Bytes * 8.0 / t.Pixels:F3}");
            line($"| {name} | {mbps} | {vLibSize} | {vLibSpd} | {vBaseSize} | {vBaseSpd} |");
        }

        line("");
        line("Bytes (and ms) per input:");
        line("");
        line("| input | " + string.Join(" | ", contestants.Select(c => c.Name)) + " |");
        line("|---|" + string.Concat(contestants.Select(_ => "---:|")));
        foreach (EncodeImage image in images)
        {
            line($"| {image.Name} | " + string.Join(" | ", contestants.Select(c =>
                string.Create(CultureInfo.InvariantCulture, $"{perImage[(image.Name, c.Name)].Bytes} ({perImage[(image.Name, c.Name)].Seconds * 1000:F0})"))) + " |");
        }

        line("");
        return true;
    }

    private sealed class AbTally
    {
        public long Bytes;
        public double Seconds;
        public long Pixels;
    }
}
