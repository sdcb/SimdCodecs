using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using IsWebpEncoder = SixLabors.ImageSharp.Formats.Webp.WebpEncoder;
using IsWebpFileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// WebP <b>encode</b> comparison: speed, encoded size and accuracy, all contestants single-threaded
/// and interleaved image by image so machine drift hits them alike.
/// <list type="bullet">
/// <item><c>libwebp</c>: the SIMD build's simple API (<c>WebPEncodeLosslessRGBA</c> /
/// <c>WebPEncodeRGBA</c>), in process; the reference every ratio is taken against.</item>
/// <item><c>cwebp -z N</c> / <c>cwebp -q Q -m M</c>: the official 1.6.0 release binary, for the effort
/// levels the simple API cannot select. Its time is the one it reports itself (<c>-v</c>, "Time to
/// encode picture"), so process start-up and reading the input are not counted.</item>
/// <item><c>ImageSharp</c>: the managed competitor.</item>
/// </list>
/// Every output is decoded by libwebp (a black box, not our decoder) before anything is timed:
/// lossless output must reproduce every visible pixel and every alpha value (libwebp's default
/// rewrites the RGB of fully transparent pixels, so "exact" and "visible" are told apart), and a
/// lossless contestant that fails is reported and never timed. Lossy output is scored against the
/// source by PSNR (luma and RGB) and SSIM on luma.
/// </summary>
internal static class EncodeBenchmark
{
    public static readonly string[] Modes = ["lossless", "lossy"];
    private static readonly int[] LossyQualities = [50, 75, 90];
    private static readonly int[] LosslessLevels = [0, 3, 6, 9];

    /// <summary>One encoder at one setting. <c>Encode</c> returns the file and the seconds it took.</summary>
    internal sealed record Contestant(string Name, Func<EncodeImage, (byte[] Webp, double Seconds)> Encode, bool InProcess);

    private sealed class Tally
    {
        public long Bytes, Pixels;
        public double Seconds, PsnrY, PsnrRgb, SsimY;
        public int Images, Scored;
        public string Accuracy = "exact";
        public bool Failed;
    }

    public static int Run(string mode, int reps, int maxDim, string? filter, string? cwebpPath, bool gate)
    {
        var sb = new StringBuilder();
        void Line(string s = "")
        {
            Console.WriteLine(s);
            sb.AppendLine(s);
        }

        Line(MachineInfo.Describe().TrimEnd());
        string? cwebp = Cwebp.Find(cwebpPath);
        Line($"- libwebp encoder version: {LibWebp.EncoderVersion()}; cwebp: {(cwebp is null ? "not found (set CWEBP or --cwebp)" : $"{Cwebp.Version(cwebp)} ({cwebp})")}");
        List<EncodeImage> images = EncodeCorpus.Load(maxDim, filter);
        Line($"- inputs: {images.Count} images, {images.Sum(i => i.Pixels) / 1e6:F2} MP in all, max dimension {maxDim}; median of {reps} encodes");
        Line();
        Line("| input | kind | size | MP |");
        Line("|---|---|---|---:|");
        foreach (EncodeImage image in images)
        {
            Line($"| {image.Name} | {image.Kind} | {image.Width}×{image.Height} | {image.Pixels / 1e6:F2} |");
        }

        if (cwebp is not null)
        {
            Line();
            Line(SimdCheck(images[0], cwebp, reps));
        }

        bool ok = true;
        foreach (string m in mode == "all" ? Modes : [mode])
        {
            ok &= m switch
            {
                "lossless" => RunMode("lossless", images, LosslessContestants(cwebp), reps, Line),
                "lossy" => RunLossy(images, cwebp, reps, Line),
                _ => throw new ArgumentException($"unknown mode '{m}' (lossless, lossy or all)"),
            };
        }

        Directory.CreateDirectory(BenchPaths.Work);
        string file = Path.Combine(BenchPaths.Work, $"encode-{mode}.md");
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine($"[encode] {file}");
        if (gate)
        {
            Console.WriteLine(ok ? "[gate] passed" : "[gate] FAILED");
        }

        return gate && !ok ? 1 : 0;
    }

    // ---------------------------------------------------------------- contestants

    internal static List<Contestant> LosslessContestants(string? cwebp)
    {
        var list = new List<Contestant>
        {
            new("libwebp", img => Time(() => LibWebp.EncodeLosslessRgba(img.Rgba, img.Width, img.Height)), InProcess: true),
        };
        if (cwebp is not null)
        {
            foreach (int level in LosslessLevels)
            {
                list.Add(new($"cwebp -z {level}", img => Cwebp.Encode(cwebp, img, "-z", level.ToString(CultureInfo.InvariantCulture)), InProcess: false));
            }
        }

        list.Add(new("ImageSharp", img => Time(() => ImageSharpEncode(img, new IsWebpEncoder { FileFormat = IsWebpFileFormat.Lossless })), InProcess: true));
        return list;
    }

    internal static List<Contestant> LossyContestants(string? cwebp, int quality)
    {
        string q = quality.ToString(CultureInfo.InvariantCulture);
        var list = new List<Contestant>
        {
            new($"libwebp q{q}", img => Time(() => LibWebp.EncodeRgba(img.Rgba, img.Width, img.Height, quality)), InProcess: true),
        };
        if (cwebp is not null)
        {
            foreach (int method in new[] { 0, 4, 6 })
            {
                list.Add(new($"cwebp -q {q} -m {method}", img => Cwebp.Encode(cwebp, img, "-q", q, "-m", method.ToString(CultureInfo.InvariantCulture)), InProcess: false));
            }
        }

        list.Add(new($"ImageSharp q{q}", img => Time(() => ImageSharpEncode(img, new IsWebpEncoder { FileFormat = IsWebpFileFormat.Lossy, Quality = quality })), InProcess: true));
        return list;
    }

    private static (byte[], double) Time(Func<byte[]> encode)
    {
        long start = Stopwatch.GetTimestamp();
        byte[] webp = encode();
        return (webp, Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    internal static byte[] ImageSharpEncode(EncodeImage img, IsWebpEncoder encoder)
    {
        using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(img.Rgba, img.Width, img.Height);
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- runs

    private static bool RunLossy(List<EncodeImage> images, string? cwebp, int reps, Action<string> line)
    {
        // Lossy scoring is on opaque inputs: with alpha, a lossy encoder may rewrite the colour of
        // invisible pixels, which a whole-image PSNR would count as error.
        List<EncodeImage> opaque = images.Where(i => !i.HasAlpha).ToList();
        bool ok = true;
        foreach (int quality in LossyQualities)
        {
            ok &= RunMode($"lossy q{quality}", opaque, LossyContestants(cwebp, quality), reps, line);
        }

        return ok;
    }

    private static bool RunMode(string title, List<EncodeImage> images, List<Contestant> contestants, int reps, Action<string> line)
    {
        bool lossless = title == "lossless";
        line("");
        line($"### {title}");
        line("");

        // Warm the in-process contestants until the JIT has tiered up, so the first image is not
        // timed against tier-0 code (cwebp runs as a fresh process each time and needs none).
        var warm = Stopwatch.StartNew();
        do
        {
            foreach (Contestant c in contestants.Where(c => c.InProcess))
            {
                c.Encode(images[^1]);
            }
        }
        while (warm.Elapsed.TotalSeconds < 1.5);

        var tallies = contestants.ToDictionary(c => c.Name, _ => new Tally());
        var perImage = new Dictionary<(string Image, string Contestant), long>();
        foreach (EncodeImage image in images)
        {
            foreach (Contestant c in contestants)
            {
                Tally t = tallies[c.Name];
                (byte[] webp, double first) = c.Encode(image);
                perImage[(image.Name, c.Name)] = webp.Length;

                // Correctness before timing.
                byte[] decoded = DecodeWithLibwebp(webp, image);
                if (lossless)
                {
                    string accuracy = CompareLossless(image, decoded);
                    if (accuracy == "WRONG")
                    {
                        Console.WriteLine($"[encode] {c.Name} on {image.Name}: decoded pixels differ from the source; not timed");
                        t.Failed = true;
                        t.Accuracy = "WRONG";
                        continue;
                    }

                    if (accuracy == "visible" && t.Accuracy == "exact")
                    {
                        t.Accuracy = "visible";
                    }
                }
                else
                {
                    (double psnrY, double psnrRgb, double ssimY) = Quality.Score(image, decoded);
                    t.PsnrY += psnrY;
                    t.PsnrRgb += psnrRgb;
                    t.SsimY += ssimY;
                    t.Scored++;
                }

                var times = new double[Math.Max(1, reps)];
                times[0] = first;
                for (int r = 1; r < times.Length; r++)
                {
                    times[r] = c.Encode(image).Seconds;
                }

                Array.Sort(times);
                t.Seconds += times[times.Length / 2];
                t.Bytes += webp.Length;
                t.Pixels += image.Pixels;
                t.Images++;
            }

            Console.WriteLine($"[encode] {title}: {image.Name} done");
        }

        Tally reference = tallies[contestants[0].Name];
        line(lossless
            ? "| encoder | MB | bpp | size vs libwebp | MP/s | time vs libwebp | lossless |"
            : "| encoder | MB | bpp | size vs libwebp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs libwebp |");
        line(lossless ? "|---|---:|---:|---:|---:|---:|---|" : "|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (Contestant c in contestants)
        {
            Tally t = tallies[c.Name];
            if (t.Images == 0)
            {
                line($"| {c.Name} | — | — | — | — | — | {t.Accuracy} |");
                continue;
            }

            string common = string.Create(CultureInfo.InvariantCulture,
                $"| {c.Name} | {t.Bytes / 1e6:F3} | {t.Bytes * 8.0 / t.Pixels:F3} | {(double)t.Bytes / reference.Bytes:F3}x");
            string speed = string.Create(CultureInfo.InvariantCulture,
                $"{t.Pixels / 1e6 / t.Seconds:F2} | {t.Seconds / reference.Seconds:F2}x");
            line(lossless
                ? $"{common} | {speed} | {(t.Failed ? "WRONG on some" : t.Accuracy)} |"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{common} | {t.PsnrY / t.Scored:F3} | {t.PsnrRgb / t.Scored:F3} | {t.SsimY / t.Scored:F4} | {speed} |"));
        }

        line("");
        line("Bytes per input:");
        line("");
        line("| input | " + string.Join(" | ", contestants.Select(c => c.Name)) + " |");
        line("|---|" + string.Concat(contestants.Select(_ => "---:|")));
        foreach (EncodeImage image in images)
        {
            line($"| {image.Name} | " + string.Join(" | ", contestants.Select(c =>
                perImage.TryGetValue((image.Name, c.Name), out long b) ? b.ToString(CultureInfo.InvariantCulture) : "—")) + " |");
        }

        return tallies.Values.All(t => !t.Failed);
    }

    private static byte[] DecodeWithLibwebp(byte[] webp, EncodeImage image)
    {
        (int w, int h) = LibWebp.GetInfo(webp);
        if (w != image.Width || h != image.Height)
        {
            throw new InvalidOperationException($"{image.Name}: decoded {w}×{h}, expected {image.Width}×{image.Height}");
        }

        var rgba = new byte[image.Rgba.Length];
        LibWebp.DecodeRgbaInto(webp, rgba, w, h);
        return rgba;
    }

    /// <summary>"exact", "visible" (only the RGB of alpha = 0 pixels differs) or "WRONG".</summary>
    internal static string CompareLossless(EncodeImage image, byte[] decoded)
    {
        if (image.Rgba.AsSpan().SequenceEqual(decoded))
        {
            return "exact";
        }

        byte[] src = image.Rgba;
        for (int i = 0; i < src.Length; i += 4)
        {
            if (src[i + 3] != decoded[i + 3] ||
                (src[i + 3] != 0 && (src[i] != decoded[i] || src[i + 1] != decoded[i + 1] || src[i + 2] != decoded[i + 2])))
            {
                return "WRONG";
            }
        }

        return "visible";
    }

    // ---------------------------------------------------------------- SIMD check

    /// <summary>
    /// Shows that the in-process libwebp is a SIMD build: it must produce the same bytes as the
    /// official cwebp at the same settings, and take about as long, not as long as <c>cwebp -noasm</c>.
    /// </summary>
    private static string SimdCheck(EncodeImage image, string cwebp, int reps)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SIMD check on {image.Name} (median of {reps}; in-process libwebp should match cwebp, not cwebp -noasm):");
        sb.AppendLine();
        sb.AppendLine("| setting | libwebp (in process) | cwebp | cwebp -noasm | same bytes as cwebp |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach ((string label, Func<byte[]> dll, string[] cli) in new (string, Func<byte[]>, string[])[]
                 {
                     ("lossless", () => LibWebp.EncodeLosslessRgba(image.Rgba, image.Width, image.Height), ["-lossless"]),
                     ("lossy q75", () => LibWebp.EncodeRgba(image.Rgba, image.Width, image.Height, 75), ["-q", "75"]),
                 })
        {
            byte[] mine = dll();
            double dllTime = Median(reps, () => Time(dll).Item2);
            byte[] official = Cwebp.Encode(cwebp, image, cli).Webp;
            double cliTime = Median(reps, () => Cwebp.Encode(cwebp, image, cli).Seconds);
            double noAsm = Median(reps, () => Cwebp.Encode(cwebp, image, [.. cli, "-noasm"]).Seconds);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {label} | {dllTime * 1000:F1} ms | {cliTime * 1000:F1} ms | {noAsm * 1000:F1} ms | {(mine.AsSpan().SequenceEqual(official) ? "yes" : $"no ({mine.Length} vs {official.Length})")} |"));
        }

        return sb.ToString().TrimEnd();
    }

    private static double Median(int reps, Func<double> run)
    {
        var t = new double[Math.Max(1, reps)];
        for (int i = 0; i < t.Length; i++)
        {
            t[i] = run();
        }

        Array.Sort(t);
        return t[t.Length / 2];
    }
}

/// <summary>The official <c>cwebp</c> release binary, run as a black box.</summary>
internal static partial class Cwebp
{
    public static string? Find(string? configured)
    {
        foreach (string? candidate in new[] { configured, Environment.GetEnvironmentVariable("CWEBP") })
        {
            if (!string.IsNullOrEmpty(candidate))
            {
                return File.Exists(candidate) ? candidate : throw new FileNotFoundException("cwebp not found", candidate);
            }
        }

        // The official release archive unpacked into the cache (see README).
        string release = Path.Combine(BenchPaths.Cache, "release");
        if (Directory.Exists(release))
        {
            string? found = Directory.GetFiles(release, OperatingSystem.IsWindows() ? "cwebp.exe" : "cwebp", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        string exe = OperatingSystem.IsWindows() ? "cwebp.exe" : "cwebp";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    public static string Version(string cwebp) => Run(cwebp, ["-version"]).StdOut.Split('\n')[0].Trim();

    private static readonly Dictionary<string, string> InputFiles = [];

    /// <summary>Encodes <paramref name="image"/> (written once as an RGBA PNG) and returns the file and cwebp's own encode time.</summary>
    public static (byte[] Webp, double Seconds) Encode(string cwebp, EncodeImage image, params string[] options)
    {
        string input = InputFor(image);
        string output = Path.Combine(BenchPaths.Work, "cwebp-out.webp");
        File.Delete(output);
        (string stdout, string stderr) = Run(cwebp, [.. options, "-v", input, "-o", output]);
        Match m = EncodeTime().Match(stderr + stdout);
        if (!m.Success || !File.Exists(output))
        {
            throw new InvalidOperationException($"cwebp {string.Join(' ', options)} failed on {image.Name}: {stderr}");
        }

        return (File.ReadAllBytes(output), double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    // PNG, because the Windows release reads everything but WebP through WIC (it rejects PNM there).
    // Reading the input is outside cwebp's reported encode time, and the lossless check proves the
    // round trip through the file is exact.
    private static string InputFor(EncodeImage image)
    {
        lock (InputFiles)
        {
            if (!InputFiles.TryGetValue(image.Name, out string? path))
            {
                Directory.CreateDirectory(BenchPaths.Work);
                path = Path.Combine(BenchPaths.Work, $"encode-{image.Name}.png");
                using var img = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(image.Rgba, image.Width, image.Height);
                img.SaveAsPng(path, new SixLabors.ImageSharp.Formats.Png.PngEncoder
                {
                    ColorType = SixLabors.ImageSharp.Formats.Png.PngColorType.RgbWithAlpha,
                    BitDepth = SixLabors.ImageSharp.Formats.Png.PngBitDepth.Bit8,
                    CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed,
                });
                InputFiles[image.Name] = path;
            }

            return path;
        }
    }

    private static (string StdOut, string StdErr) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (stdout, stderr.Result);
    }

    [GeneratedRegex(@"Time to encode picture:\s*([0-9.]+)\s*s")]
    private static partial Regex EncodeTime();
}

/// <summary>PSNR and SSIM of a decoded image against its source (opaque inputs).</summary>
internal static class Quality
{
    public static (double PsnrY, double PsnrRgb, double SsimY) Score(EncodeImage src, byte[] rgba)
    {
        int w = src.Width, h = src.Height;
        var ya = new float[w * h];
        var yb = new float[w * h];
        double se = 0, seY = 0;
        byte[] s = src.Rgba;
        for (int i = 0; i < w * h; i++)
        {
            int r0 = s[i * 4], g0 = s[i * 4 + 1], b0 = s[i * 4 + 2];
            int r1 = rgba[i * 4], g1 = rgba[i * 4 + 1], b1 = rgba[i * 4 + 2];
            se += (r0 - r1) * (r0 - r1) + (g0 - g1) * (g0 - g1) + (b0 - b1) * (b0 - b1);
            ya[i] = 0.299f * r0 + 0.587f * g0 + 0.114f * b0;
            yb[i] = 0.299f * r1 + 0.587f * g1 + 0.114f * b1;
            double d = ya[i] - yb[i];
            seY += d * d;
        }

        double psnrRgb = 10 * Math.Log10(255.0 * 255 / Math.Max(se / (3.0 * w * h), 1e-12));
        double psnrY = 10 * Math.Log10(255.0 * 255 / Math.Max(seY / ((double)w * h), 1e-12));
        return (psnrY, psnrRgb, Ssim(ya, yb, w, h));
    }

    /// <summary>Mean SSIM (Wang et al. 2004 constants) over 8×8 luma windows at a stride of 4.</summary>
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
}

/// <summary>
/// Peak working set of one encode, in a fresh process, so libwebp's native allocations count too.
/// The child loads the input, warms the encoder on a 64×64 crop of it (JIT and library load
/// excluded), collects, and samples the working set during one full encode; then repeats the encode
/// for the steady state. Managed allocations of that encode are reported alongside.
/// </summary>
internal static class EncodePeakMemory
{
    public static int Run(string mode, int maxDim, string? filter)
    {
        string self = typeof(EncodePeakMemory).Assembly.Location;
        List<EncodeImage> images = EncodeCorpus.Load(maxDim, filter);
        var sb = new StringBuilder();
        sb.AppendLine(MachineInfo.Describe().TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"Peak working set rise of one {mode} encode, fresh process each, median of 3 (MiB); the input " +
                      "(width × height × 4 bytes) is resident before the baseline and not part of the rise.");
        sb.AppendLine();
        sb.AppendLine("| input | encoder | first-encode rise | steady rise | managed alloc |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        foreach (EncodeImage image in images.Where(i => mode == "lossless" || !i.HasAlpha))
        {
            foreach (string contestant in Contestants(mode))
            {
                var runs = new List<long[]>();
                for (int r = 0; r < 3; r++)
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
                    if (!Path.GetFileName(Environment.ProcessPath!).StartsWith("Sdcb.", StringComparison.Ordinal))
                    {
                        psi.ArgumentList.Add(self);
                    }

                    foreach (string a in new[] { "encode-peakmem-child", mode, contestant, image.Name, maxDim.ToString(CultureInfo.InvariantCulture) })
                    {
                        psi.ArgumentList.Add(a);
                    }

                    psi.Environment[BenchPaths.RootVariable] = BenchPaths.Root;
                    using Process p = Process.Start(psi)!;
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"encode-peakmem child failed for {contestant} {image.Name}: {output}");
                    }

                    runs.Add(output.Split('\n').Last().Split('\t').Select(f => long.Parse(f, CultureInfo.InvariantCulture)).ToArray());
                }

                long[] median = runs.OrderBy(v => v[0]).ElementAt(1);
                string row = string.Create(CultureInfo.InvariantCulture,
                    $"| {image.Name} | {contestant} | {median[0] / 1048576.0:F2} | {median[1] / 1048576.0:F2} | {median[2] / 1048576.0:F2} |");
                Console.WriteLine(row);
                sb.AppendLine(row);
            }
        }

        Directory.CreateDirectory(BenchPaths.Work);
        string file = Path.Combine(BenchPaths.Work, $"encode-peakmem-{mode}.md");
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"[encode-peakmem] {file}");
        return 0;
    }

    private static string[] Contestants(string mode) => ["libwebp", "ImageSharp"];

    private static Func<EncodeImage, byte[]> Encoder(string mode, string contestant) => (mode, contestant) switch
    {
        ("lossless", "libwebp") => img => LibWebp.EncodeLosslessRgba(img.Rgba, img.Width, img.Height),
        ("lossy", "libwebp") => img => LibWebp.EncodeRgba(img.Rgba, img.Width, img.Height, 75),
        ("lossless", "ImageSharp") => img => EncodeBenchmark.ImageSharpEncode(img, new IsWebpEncoder { FileFormat = IsWebpFileFormat.Lossless }),
        ("lossy", "ImageSharp") => img => EncodeBenchmark.ImageSharpEncode(img, new IsWebpEncoder { FileFormat = IsWebpFileFormat.Lossy, Quality = 75 }),
        _ => throw new ArgumentOutOfRangeException(nameof(contestant), $"{mode} {contestant}"),
    };

    /// <summary>Child-process body. Prints "firstRise\tsteadyRise\tmanagedBytes".</summary>
    public static int Child(string mode, string contestant, string imageName, int maxDim)
    {
        EncodeImage image = EncodeCorpus.Load(maxDim, imageName).First(i => i.Name == imageName);
        Func<EncodeImage, byte[]> encode = Encoder(mode, contestant);

        var crop = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        {
            image.Rgba.AsSpan(y * image.Width * 4, 64 * 4).CopyTo(crop.AsSpan(y * 64 * 4));
        }

        encode(image with { Rgba = crop, Width = 64, Height = 64 });
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long first = PeakMemory.Measure(() => encode(image), out _);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long steady = PeakMemory.Measure(() => encode(image), out _);
        Console.WriteLine($"{first}\t{steady}\t{allocated}");
        return 0;
    }
}
