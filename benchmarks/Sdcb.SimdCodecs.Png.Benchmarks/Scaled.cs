using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>
/// <see cref="DecodeOptions.Region"/> and <see cref="DecodeOptions.ScaleDenominator"/>: for each case and each mode, the
/// current <see cref="PngImageDecoder.TryDecode"/> with the options (native: only the window's rows, box-filtered as they
/// are decoded, stopping after the last row needed) against the baseline's (<c>baseline [git-ref]</c>; on
/// <c>scaled-decode</c> that is the generic path: decode the whole image, then box-filter and crop), with the full decode
/// for scale. The two must give identical pixels, checked before timing. <c>scaled-peakmem</c> measures the working set
/// of each, one fresh process per run.
/// </summary>
internal static class Scaled
{
    /// <summary>Without <c>--cases</c>: the common colour types, non-interlaced (Adam7 keeps the generic path).</summary>
    public const string DefaultCases = "^(photo|screen)-(g8|rgb8|p8|rgba8|rgb16)$";

    public const string Full = "full decode";
    public const string Native = "native";
    public const string Fallback = "baseline";

    /// <summary>A scale and a region of the scaled image, the region clipped to fit a small image (the warm-up's).</summary>
    public sealed record Mode(string Name, int Scale, Func<int, int, PixelRect?> Region);

    public static readonly Mode[] Modes =
    [
        new("region 512² top-left", 1, (w, h) => Clip(0, 0, 512, 512, w, h)),
        new("region 512² centre", 1, (w, h) => Clip(w / 2 - 256, h / 2 - 256, 512, 512, w, h)),
        new("region 512² bottom-right", 1, (w, h) => Clip(w - 512, h - 512, 512, 512, w, h)),
        new("scale 1/2", 2, (_, _) => null),
        new("scale 1/4", 4, (_, _) => null),
        new("scale 1/8", 8, (_, _) => null),
        new("1/4 + region 128² centre", 4, (w, h) => Clip(w / 2 - 64, h / 2 - 64, 128, 128, w, h)),
    ];

    private static PixelRect Clip(int x, int y, int width, int height, int w, int h)
    {
        width = Math.Min(width, w);
        height = Math.Min(height, h);
        return new PixelRect(Math.Clamp(x, 0, w - width), Math.Clamp(y, 0, h - height), width, height);
    }

    private static (int Width, int Height) SizeOf(byte[] png) =>
        (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

    private static DecodeOptions OptionsFor(Mode mode, byte[] png, out PixelRect? region)
    {
        var (w, h) = SizeOf(png);
        region = mode.Region((w + mode.Scale - 1) / mode.Scale, (h + mode.Scale - 1) / mode.Scale);
        return new DecodeOptions { ScaleDenominator = mode.Scale, Region = region };
    }

    /// <summary>The contestants of one case and mode, each checked against the full decode's box + crop first.</summary>
    public static List<Contestant> Contestants(byte[] png, Mode mode, string? only = null)
    {
        DecodeOptions options = OptionsFor(mode, png, out PixelRect? region);
        var list = new List<Contestant>();
        bool Want(string name) => only is null || only == name;

        if (Want(Full))
            list.Add(new(Full, () => { Check(PngImageDecoder.TryDecode(png, out var image)); image!.Dispose(); }));

        byte[]? reference = null;
        if (Want(Native))
        {
            Check(PngImageDecoder.TryDecode(png, out var image, options));
            reference = image!.Pixels.ToArray();
            image.Dispose();
            list.Add(new(Native, () => { Check(PngImageDecoder.TryDecode(png, out var i, options)); i!.Dispose(); }));
        }

        if (Want(Fallback) && BaselinePng.IsBuilt && BaselinePng.CanDecodeWithOptions)
        {
            object baselineOptions = BaselinePng.CreateDecodeOptions(mode.Scale,
                region is { } r ? (r.X, r.Y, r.Width, r.Height) : null)!;
            Check(BaselinePng.TryDecode(png, out object? image, baselineOptions));
            byte[] pixels = BaselinePng.PixelsOf(image!);
            ((IDisposable)image!).Dispose();
            if (reference is not null && !pixels.AsSpan().SequenceEqual(reference))
                throw new InvalidOperationException($"{mode.Name}: native and baseline pixels differ");
            list.Add(new(Fallback, () =>
            {
                Check(BaselinePng.TryDecode(png, out object? i, baselineOptions));
                ((IDisposable)i!).Dispose();
            }));
        }

        return list;
    }

    public static string Run(int rounds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Region and scale decode (`TryDecode` with `DecodeOptions`; in-process medians of {rounds} interleaved rounds, ms)");
        sb.AppendLine();
        sb.AppendLine($"`{Native}` = the current `TryDecode(options)`; `{Fallback}` = the baseline's (`{BaselinePng.Description ?? "none built"}`), " +
                      $"which decodes the whole image, then box-filters and crops; `{Full}` = `TryDecode` without options, for scale. " +
                      "Both give identical pixels (checked first). Allocated = managed bytes of one warm operation.");
        sb.AppendLine();
        sb.AppendLine($"| Case | Mode | {Full} | {Fallback} | {Native} | {Native} ÷ {Fallback} | {Native} ÷ {Full} | alloc {Fallback} | alloc {Native} |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (DecodeCase c in DecodeCase.Selected)
        {
            byte[] png = c.Read();
            foreach (Mode mode in Modes)
            {
                List<Contestant> contestants = Contestants(png, mode);
                var allocated = new Dictionary<string, long>();
                Dictionary<string, double> ms = Matrix.Time(contestants, rounds, allocated);
                Console.WriteLine($"[scaled] {c.Name,-14} {mode.Name,-26} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {kv.Value:F3}")));
                string Ms(string n) => ms.TryGetValue(n, out double v) ? F(v) : "—";
                string Ratio(string a, string b) => ms.TryGetValue(a, out double x) && ms.TryGetValue(b, out double y)
                    ? (x / y).ToString("F2", CultureInfo.InvariantCulture) : "—";
                string Kb(string n) => allocated.TryGetValue(n, out long v) ? $"{v / 1024.0:F0} KB" : "—";
                sb.AppendLine($"| `{c.Name}` | {mode.Name} | {Ms(Full)} | {Ms(Fallback)} | {Ms(Native)} | {Ratio(Native, Fallback)} | " +
                              $"{Ratio(Native, Full)} | {Kb(Fallback)} | {Kb(Native)} |");
            }
        }

        return sb.ToString();
    }

    // ---- Peak working set ------------------------------------------------------------------------------------------

    public static string RunPeakMemory(int repeats = 3)
    {
        string self = typeof(Scaled).Assembly.Location;
        var size = Settings.Size;
        var sb = new StringBuilder();
        sb.AppendLine($"### Peak working set per region / scale decode, MiB (one fresh process per run, median of {repeats})");
        sb.AppendLine();
        sb.AppendLine("\"first\" is the working-set rise during the first full-size operation, after a warm-up on a 64×64 image of " +
                      "the same case and mode (JIT excluded). It includes the decoded output, which for the region modes is " +
                      $"{512 * 512 * 4 / 1048576.0:F2} MiB at most (512² RGBA); the {size.Width}×{size.Height} full decode's samples are " +
                      $"{(long)size.Width * size.Height * 4 / 1048576.0:F2} MiB for RGBA8.");
        sb.AppendLine();
        string[] names = [Full, Fallback, Native];
        sb.AppendLine("| Case | Mode | " + string.Join(" | ", names.Select(n => n + " first")) + " |");
        sb.AppendLine("|---|---|" + string.Concat(names.Select(_ => "---:|")));
        foreach (DecodeCase c in DecodeCase.Selected)
        {
            for (int m = 0; m < Modes.Length; m++)
            {
                var cells = new List<string>();
                foreach (string name in names)
                {
                    if (name == Full && m > 0)
                    {
                        cells.Add("″");
                        continue;
                    }

                    var runs = new List<long>();
                    for (int r = 0; r < repeats; r++)
                    {
                        var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
                        if (!Path.GetFileName(Environment.ProcessPath!).StartsWith("Sdcb.", StringComparison.Ordinal))
                            psi.ArgumentList.Add(self);
                        foreach (string arg in new[] { "scaled-child", c.Name, m.ToString(CultureInfo.InvariantCulture), name })
                            psi.ArgumentList.Add(arg);
                        psi.Environment[BenchPaths.RootVariable] = BenchPaths.Root;
                        using Process p = Process.Start(psi)!;
                        string line = p.StandardOutput.ReadToEnd().Trim().Split('\n').Last();
                        p.WaitForExit();
                        if (p.ExitCode != 0)
                            throw new InvalidOperationException($"scaled child failed for {name} {c.Name} {Modes[m].Name}: {line}");
                        runs.Add(long.Parse(line, CultureInfo.InvariantCulture));
                    }

                    long median = runs.Order().ElementAt(runs.Count / 2);
                    cells.Add(Mib(median));
                    Console.WriteLine($"[scaled-peakmem] {c.Name,-14} {Modes[m].Name,-26} {name,-12} {Mib(median),8} MiB");
                }

                sb.AppendLine($"| `{c.Name}` | {Modes[m].Name} | " + string.Join(" | ", cells) + " |");
            }
        }

        return sb.ToString();
    }

    /// <summary>Child-process body: prints the working-set rise of one operation in bytes.</summary>
    public static int Child(string caseName, int mode, string contestant)
    {
        DecodeCase c = DecodeCase.Find(caseName);
        string? size = Environment.GetEnvironmentVariable(Settings.SizeVariable);
        Environment.SetEnvironmentVariable(Settings.SizeVariable, "64x64");
        Single(c.Read(), Modes[mode], contestant).Run();
        Environment.SetEnvironmentVariable(Settings.SizeVariable, size);

        Contestant op = Single(c.Read(), Modes[mode], contestant);
        PeakMemory.TouchManagedHeap();
        Console.WriteLine(PeakMemory.Measure(op.Run).ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    private static Contestant Single(byte[] png, Mode mode, string contestant) =>
        Contestants(png, mode, contestant) is [var only] ? only : throw new InvalidOperationException($"{contestant} cannot run {mode.Name}");

    private static void Check(bool ok)
    {
        if (!ok)
            throw new InvalidOperationException("decode failed");
    }

    private static string F(double ms) => ms.ToString(ms < 10 ? "F3" : "F2", CultureInfo.InvariantCulture);

    private static string Mib(long bytes) => (bytes / 1048576.0).ToString("F2", CultureInfo.InvariantCulture);
}
