using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>
/// The whole decode and encode matrix, timed in-process: per case every contestant is warmed (at
/// least 300 ms, so nothing is timed as tier-0 code) and then timed in interleaved rounds, so machine
/// drift lands on all of them alike; the median per contestant is reported. It answers in minutes,
/// not hours. For a number that goes into a commit, time the cases in question with <c>bdn</c>.
/// </summary>
internal static class Matrix
{
    /// <param name="contestants">What to time.</param>
    /// <param name="rounds">Interleaved rounds; the median of them is reported.</param>
    /// <param name="allocated">Receives the managed bytes one warm operation allocates, per contestant.</param>
    /// <param name="sampleMs">Minimum length of one timed sample.</param>
    public static Dictionary<string, double> Time(IReadOnlyList<Contestant> contestants, int rounds,
        Dictionary<string, long>? allocated = null, double sampleMs = 30)
    {
        foreach (Contestant c in contestants)
        {
            long warmStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < 3 || Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds < 300; i++)
            {
                c.Run();
            }

            // One warm operation on this thread: pooled buffers are already rented once, so this is
            // what a steady caller pays. Native and pooled-unmanaged memory (libpng, ImageSharp's
            // pixel buffers) is invisible here; `peakmem` measures the working set for that.
            if (allocated is not null)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                c.Run();
                allocated[c.Name] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        var samples = contestants.ToDictionary(c => c.Name, _ => new List<double>());
        for (int round = 0; round < rounds; round++)
        {
            foreach (Contestant c in contestants)
            {
                int ops = 0;
                long start = Stopwatch.GetTimestamp();
                do
                {
                    c.Run();
                    ops++;
                }
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < sampleMs);

                samples[c.Name].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / ops);
            }
        }

        return samples.ToDictionary(kv => kv.Key, kv => Median(kv.Value));
    }

    public static string RunDecode(int rounds)
    {
        var rows = new List<(DecodeSetup Setup, Dictionary<string, double> Ms)>();
        foreach (DecodeCase c in DecodeCase.Selected)
        {
            DecodeSetup setup = DecodeSetup.Create(c);
            Dictionary<string, double> ms = Time(setup.Contestants, rounds, setup.Allocated);
            rows.Add((setup, ms));
            Console.WriteLine($"[decode] {c.Name,-22} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {kv.Value:F3}")) +
                              (setup.Notes.Count > 0 ? "  (" + string.Join("; ", setup.Notes) + ")" : ""));
        }

        return RenderDecode(rows, $"in-process medians of {rounds} interleaved rounds");
    }

    public static string RunEncode(int rounds)
    {
        var rows = new List<(EncodeSetup Setup, Dictionary<string, double> Ms)>();
        foreach (EncodeCase c in EncodeCase.Selected)
        {
            EncodeSetup setup = EncodeSetup.Create(c);
            Dictionary<string, double> ms = Time(setup.Contestants, rounds, setup.Allocated);
            rows.Add((setup, ms));
            Console.WriteLine($"[encode] {c.Name,-26} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {kv.Value:F3}")) +
                              (setup.Notes.Count > 0 ? "  (" + string.Join("; ", setup.Notes) + ")" : ""));
        }

        return RenderEncode(rows, $"in-process medians of {rounds} interleaved rounds");
    }

    public static string RenderDecode(IReadOnlyList<(DecodeSetup Setup, Dictionary<string, double> Ms)> rows, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Decode to 8-bit RGBA ({source}, ms)");
        sb.AppendLine();
        sb.AppendLine("`current` = `PngImageDecoder.TryDecodeIntoRgba8` into a caller buffer; `native` = `PngReader.Decode` " +
                      "(samples as stored, no RGBA expansion); ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread; " +
                      "libpng = simplified API `png_image_finish_read` to RGBA. Ratios below 1 mean we are faster. " +
                      "Inputs are encoded by ImageSharp (adaptive filters, zlib level 6).");
        sb.AppendLine();
        sb.AppendLine("| Case | KB | current | native | baseline | ImageSharp | libpng | current MP/s | ÷ ImageSharp | ÷ libpng | ÷ baseline |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var notes = new List<string>();
        foreach ((DecodeSetup s, Dictionary<string, double> ms) in rows)
        {
            double? current = Get(ms, Names.Current);
            double mp = s.Width * (double)s.Height / 1e6;
            sb.AppendLine($"| `{s.Case.Name}` | {s.Png.Length / 1024.0:F0} | {Ms(ms, Names.Current)} | {Ms(ms, Names.CurrentNative)} | " +
                          $"{Ms(ms, Names.Baseline)} | {Ms(ms, Names.ImageSharp)} | {Ms(ms, Names.LibPng)} | " +
                          $"{(current is { } c ? (mp / (c / 1000)).ToString("F0", CultureInfo.InvariantCulture) : "—")} | " +
                          $"{Ratio(ms, Names.Current, Names.ImageSharp)} | {Ratio(ms, Names.Current, Names.LibPng)} | {Ratio(ms, Names.Current, Names.Baseline)} |");
            notes.AddRange(s.Notes.Select(n => $"`{s.Case.Name}`: {n}"));
        }

        sb.AppendLine();
        sb.AppendLine("Geometric means of current ÷ competitor over the cases both decode:");
        sb.AppendLine();
        foreach (string competitor in new[] { Names.ImageSharp, Names.LibPng, Names.Baseline })
        {
            foreach (string content in SourceImage.Contents)
            {
                var subset = rows.Where(r => r.Setup.Case.Content == content).Select(r => r.Ms).ToList();
                if (GeoMean(subset, Names.Current, competitor) is { } g)
                {
                    sb.AppendLine($"- vs {Label(competitor)}, {content}: {g.ToString("F2", CultureInfo.InvariantCulture)}× ({subset.Count(m => m.ContainsKey(Names.Current) && m.ContainsKey(competitor))} cases)");
                }
            }
        }

        if (rows.Any(r => r.Ms.ContainsKey(Names.InflateOnly)))
        {
            sb.AppendLine();
            sb.AppendLine("Stage cost = current − (current with that stage skipped), ms; \"inflate only\" is what is left with CRC, " +
                          "unfilter and expansion all skipped (chunk walk + zlib inflate). Attribution by ablation, not sampling.");
            sb.AppendLine();
            sb.AppendLine("| Case | current | CRC | unfilter | expand | inflate only | libpng |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            foreach ((DecodeSetup s, Dictionary<string, double> ms) in rows)
            {
                if (Get(ms, Names.Current) is not { } current || Get(ms, Names.InflateOnly) is not { } inflate)
                {
                    continue;
                }

                string Cost(string name) => Get(ms, name) is { } v ? F(current - v) : "—";
                sb.AppendLine($"| `{s.Case.Name}` | {F(current)} | {Cost(Names.NoCrc)} | {Cost(Names.NoUnfilter)} | {Cost(Names.NoExpand)} | " +
                              $"{F(inflate)} | {Ms(ms, Names.LibPng)} |");
            }
        }

        AppendAllocations(sb, rows.Select(r => (r.Setup.Case.Name, r.Setup.Allocated)).ToList(),
            [Names.Current, Names.CurrentNative, Names.Baseline, Names.ImageSharp, Names.LibPng]);
        AppendNotes(sb, notes);
        return sb.ToString();
    }

    public static string RenderEncode(IReadOnlyList<(EncodeSetup Setup, Dictionary<string, double> Ms)> rows, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Encode ({source}, ms; sizes in KB)");
        sb.AppendLine();
        sb.AppendLine("`current` = `PngWriter.Encode*` with `PngWriteOptions.CompressionLevel`, one thread; ImageSharp = `PngEncoder` " +
                      "at the same zlib level (store 0, fastest 1, optimal 6, smallest 9), adaptive filters, one thread; " +
                      "libpng = simplified API `png_image_write_to_memory`, which has no level setting, so it appears on the " +
                      "optimal rows (its default). Every output is decoded and must reproduce the samples exactly.");
        sb.AppendLine();
        sb.AppendLine("| Case | current | KB | baseline | ImageSharp | KB | libpng | KB | time ÷ ImageSharp | size ÷ ImageSharp | time ÷ libpng | size ÷ libpng | time ÷ baseline |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var notes = new List<string>();
        foreach ((EncodeSetup s, Dictionary<string, double> ms) in rows)
        {
            sb.AppendLine($"| `{s.Case.Name}` | {Ms(ms, Names.Current)} | {Kb(s, Names.Current)} | {Ms(ms, Names.Baseline)} | " +
                          $"{Ms(ms, Names.ImageSharp)} | {Kb(s, Names.ImageSharp)} | {Ms(ms, Names.LibPng)} | {Kb(s, Names.LibPng)} | " +
                          $"{Ratio(ms, Names.Current, Names.ImageSharp)} | {SizeRatio(s, Names.ImageSharp)} | " +
                          $"{Ratio(ms, Names.Current, Names.LibPng)} | {SizeRatio(s, Names.LibPng)} | {Ratio(ms, Names.Current, Names.Baseline)} |");
            notes.AddRange(s.Notes.Select(n => $"`{s.Case.Name}`: {n}"));
        }

        sb.AppendLine();
        sb.AppendLine("Geometric means of current ÷ ImageSharp per compression level (time / size):");
        sb.AppendLine();
        foreach (var level in EncodeCase.Levels)
        {
            var subset = rows.Where(r => r.Setup.Case.Level == level).ToList();
            if (GeoMean(subset.Select(r => r.Ms).ToList(), Names.Current, Names.ImageSharp) is { } t)
            {
                double size = Math.Exp(subset.Where(r => r.Setup.Sizes.ContainsKey(Names.ImageSharp))
                    .Average(r => Math.Log((double)r.Setup.Sizes[Names.Current] / r.Setup.Sizes[Names.ImageSharp])));
                sb.AppendLine($"- {EncodeCase.LevelName(level)}: time {t.ToString("F2", CultureInfo.InvariantCulture)}×, " +
                              $"size {size.ToString("F3", CultureInfo.InvariantCulture)}×");
            }
        }

        if (rows.Any(r => r.Ms.ContainsKey(Names.CurrentParallel)))
        {
            sb.AppendLine();
            sb.AppendLine($"Opt-in parallel deflate (`ParallelFragments = {EncodeSetup.ParallelFragments}`), not part of the " +
                          "single-thread comparison above:");
            sb.AppendLine();
            sb.AppendLine("| Case | current (1 thread) | parallel | speed-up | KB | size ÷ 1 thread |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach ((EncodeSetup s, Dictionary<string, double> ms) in rows)
            {
                if (Get(ms, Names.Current) is { } one && Get(ms, Names.CurrentParallel) is { } par)
                {
                    sb.AppendLine($"| `{s.Case.Name}` | {F(one)} | {F(par)} | {(one / par).ToString("F2", CultureInfo.InvariantCulture)}× | " +
                                  $"{Kb(s, Names.CurrentParallel)} | {((double)s.Sizes[Names.CurrentParallel] / s.Sizes[Names.Current]).ToString("F3", CultureInfo.InvariantCulture)} |");
                }
            }
        }

        AppendAllocations(sb, rows.Select(r => (r.Setup.Case.Name, r.Setup.Allocated)).ToList(),
            [Names.Current, Names.CurrentParallel, Names.Baseline, Names.ImageSharp, Names.LibPng]);
        AppendNotes(sb, notes);
        return sb.ToString();
    }

    private static void AppendAllocations(StringBuilder sb, List<(string Case, Dictionary<string, long> Bytes)> rows, string[] names)
    {
        if (!rows.Any(r => r.Bytes.Count > 0))
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps " +
                      "pixel buffers in its own unmanaged pool and libpng allocates natively, so neither shows here: compare " +
                      "working sets with `peakmem`.");
        sb.AppendLine();
        sb.AppendLine("| Case | " + string.Join(" | ", names.Select(Label)) + " |");
        sb.AppendLine("|---|" + string.Concat(names.Select(_ => "---:|")));
        foreach ((string name, Dictionary<string, long> bytes) in rows)
        {
            sb.AppendLine($"| `{name}` | " + string.Join(" | ", names.Select(n =>
                bytes.TryGetValue(n, out long b) ? (b / 1024.0).ToString("F1", CultureInfo.InvariantCulture) : "—")) + " |");
        }
    }

    private static void AppendNotes(StringBuilder sb, List<string> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Notes:");
        sb.AppendLine();
        foreach (string note in notes)
        {
            sb.AppendLine("- " + note);
        }
    }

    private static string Label(string name) => name switch
    {
        Names.LibPng => "libpng",
        Names.Baseline => "baseline",
        Names.Current => "current",
        Names.CurrentNative => "native",
        Names.CurrentParallel => "parallel",
        _ => name,
    };

    private static double? GeoMean(IReadOnlyList<Dictionary<string, double>> rows, string a, string b)
    {
        var logs = rows.Where(r => r.ContainsKey(a) && r.ContainsKey(b)).Select(r => Math.Log(r[a] / r[b])).ToList();
        return logs.Count == 0 ? null : Math.Exp(logs.Average());
    }

    private static double? Get(Dictionary<string, double> ms, string name) => ms.TryGetValue(name, out double v) ? v : null;

    private static string F(double v) => v.ToString(v < 10 ? "F3" : "F1", CultureInfo.InvariantCulture);

    private static string Ms(Dictionary<string, double> ms, string name) => Get(ms, name) is { } v ? F(v) : "—";

    private static string Ratio(Dictionary<string, double> ms, string a, string b) =>
        Get(ms, a) is { } x && Get(ms, b) is { } y ? (x / y).ToString("F2", CultureInfo.InvariantCulture) + "×" : "—";

    private static string Kb(EncodeSetup s, string name) =>
        s.Sizes.TryGetValue(name, out int bytes) ? (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) : "—";

    private static string SizeRatio(EncodeSetup s, string competitor) =>
        s.Sizes.TryGetValue(Names.Current, out int a) && s.Sizes.TryGetValue(competitor, out int b)
            ? ((double)a / b).ToString("F3", CultureInfo.InvariantCulture)
            : "—";

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}

/// <summary>Machine and contestant facts recorded alongside every result.</summary>
internal static class MachineInfo
{
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- CPU: {CpuName()}");
        sb.AppendLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier}), process {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"- Logical processors: {Environment.ProcessorCount}");
        sb.AppendLine($"- Vector128.IsHardwareAccelerated: {Vector128.IsHardwareAccelerated}, " +
                      $"Vector256.IsHardwareAccelerated: {Vector256.IsHardwareAccelerated}, " +
                      $"Vector512.IsHardwareAccelerated: {Vector512.IsHardwareAccelerated}");
        sb.AppendLine($"- AVX2: {Avx2.IsSupported}, AVX-512F: {Avx512F.IsSupported}");
        var size = Settings.Size;
        sb.AppendLine($"- Image size: {size.Width}×{size.Height}");
        foreach (string content in SourceImage.Contents)
        {
            sb.AppendLine($"- `{content}` source: {SourceImage.Get(content, size.Width, size.Height).Origin}");
        }

        sb.AppendLine($"- ImageSharp: {typeof(SixLabors.ImageSharp.Image).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]}");
        sb.AppendLine($"- libpng: {LibPng.Describe()}");
        sb.AppendLine($"- Baseline: {BaselinePng.Description ?? "(none built)"}");
        return sb.ToString();
    }

    private static string CpuName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown";
            }

            if (File.Exists("/proc/cpuinfo"))
            {
                string? line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                return line?.Split(':', 2)[1].Trim() ?? "unknown";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return "unknown";
    }
}
