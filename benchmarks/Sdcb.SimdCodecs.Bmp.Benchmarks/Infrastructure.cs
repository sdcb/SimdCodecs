using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using Sdcb.SimdCodecs.Png.Benchmarks;

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

/// <summary>Locates the benchmark project directory and its git-ignored cache.</summary>
internal static class BenchPaths
{
    public const string RootVariable = "SIMDCODECS_BENCH_ROOT";

    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    /// <summary><c>benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks</c>.</summary>
    public static string Root => LazyRoot.Value;

    public static string Cache => Path.Combine(Root, ".cache");
    public static string Work => Path.Combine(Cache, "work");

    private static string FindRoot()
    {
        // BenchmarkDotNet children run from a generated directory; the driver passes the root down.
        if (Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } configured) return configured;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sdcb.SimdCodecs.Bmp.Benchmarks.csproj"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cannot find benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks; set " + RootVariable);
    }
}

/// <summary>Benchmark-wide settings, passed to BenchmarkDotNet's child processes through the environment.</summary>
internal static class Settings
{
    public const string SizeVariable = "BMP_BENCH_SIZE";
    public const string CasesVariable = "BMP_BENCH_CASES";

    /// <summary>Size of every bitmap case, <c>WIDTHxHEIGHT</c> (default 1920x1080). Icons have their own sizes.</summary>
    public static (int Width, int Height) Size
    {
        get
        {
            var text = Environment.GetEnvironmentVariable(SizeVariable) is { Length: > 0 } s ? s : "1920x1080";
            var m = Regex.Match(text, @"^(\d+)x(\d+)$");
            return m.Success
                ? (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                : throw new ArgumentException($"{SizeVariable} must look like 1920x1080, got '{text}'");
        }
    }

    public static bool Selected(string caseName) =>
        Environment.GetEnvironmentVariable(CasesVariable) is not { Length: > 0 } filter || Regex.IsMatch(caseName, filter);
}

/// <summary>Contestant names; each doubles as a BenchmarkDotNet method name.</summary>
internal static class Names
{
    public const string Current = "Current";
    public const string CurrentBgra = "CurrentBgra";
    public const string Baseline = "Baseline";
    public const string ImageSharp = "ImageSharp";
    public const string Wic = "Wic";

    public static string Label(string name) => name switch
    {
        Current => "current",
        CurrentBgra => "current BGR(A)",
        Baseline => "baseline",
        Wic => "WIC",
        _ => name,
    };
}

/// <summary>One timed operation.</summary>
internal sealed record Contestant(string Name, Action Run);

/// <summary>
/// In-process timing: every contestant is warmed (at least 300 ms, so nothing is timed as tier-0 code), then
/// timed in interleaved rounds so machine drift lands on all of them alike; the median per contestant is
/// reported. For a number that goes into a commit, time the cases in question with <c>bdn</c> as well.
/// </summary>
internal static class Timing
{
    public static Dictionary<string, double> Time(IReadOnlyList<Contestant> contestants, int rounds,
        Dictionary<string, long>? allocated = null, double sampleMs = 30)
    {
        foreach (var c in contestants)
        {
            var warmStart = Stopwatch.GetTimestamp();
            for (var i = 0; i < 3 || Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds < 300; i++) c.Run();
            // One warm operation on this thread: what a steady caller pays in managed allocations. Native and
            // unmanaged memory (WIC, ImageSharp's pixel buffers) is invisible here; `peakmem` measures it.
            if (allocated is not null)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                c.Run();
                allocated[c.Name] = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        var samples = contestants.ToDictionary(c => c.Name, _ => new List<double>());
        for (var round = 0; round < rounds; round++)
        {
            foreach (var c in contestants)
            {
                var ops = 0;
                var start = Stopwatch.GetTimestamp();
                do
                {
                    c.Run();
                    ops++;
                }
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < sampleMs);
                samples[c.Name].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / ops);
            }
        }
        return samples.ToDictionary(kv => kv.Key, kv =>
        {
            kv.Value.Sort();
            return kv.Value[kv.Value.Count / 2];
        });
    }

    public static double? Get(Dictionary<string, double> ms, string name) => ms.TryGetValue(name, out var v) ? v : null;

    public static string F(double v) => v.ToString(v < 1 ? "F4" : v < 10 ? "F3" : "F1", CultureInfo.InvariantCulture);

    public static string Ms(Dictionary<string, double> ms, string name) => Get(ms, name) is { } v ? F(v) : "—";

    public static string Ratio(Dictionary<string, double> ms, string a, string b) =>
        Get(ms, a) is { } x && Get(ms, b) is { } y ? (x / y).ToString("F2", CultureInfo.InvariantCulture) + "×" : "—";

    public static double? GeoMean(IEnumerable<Dictionary<string, double>> rows, string a, string b)
    {
        var logs = rows.Where(r => r.ContainsKey(a) && r.ContainsKey(b)).Select(r => Math.Log(r[a] / r[b])).ToList();
        return logs.Count == 0 ? null : Math.Exp(logs.Average());
    }

    public static void AppendAllocations(StringBuilder sb, List<(string Case, Dictionary<string, long> Bytes)> rows, string[] names)
    {
        if (!rows.Any(r => r.Bytes.Count > 0)) return;
        sb.AppendLine();
        sb.AppendLine("Managed bytes allocated per operation, KB (one warm operation on the timing thread). ImageSharp keeps pixel " +
                      "buffers in its own unmanaged pool and WIC allocates natively, so neither shows here: compare working sets " +
                      "with `peakmem`.");
        sb.AppendLine();
        sb.AppendLine("| Case | " + string.Join(" | ", names.Select(Names.Label)) + " |");
        sb.AppendLine("|---|" + string.Concat(names.Select(_ => "---:|")));
        foreach (var (name, bytes) in rows)
        {
            sb.AppendLine($"| `{name}` | " + string.Join(" | ", names.Select(n =>
                bytes.TryGetValue(n, out var b) ? (b / 1024.0).ToString("F1", CultureInfo.InvariantCulture) : "—")) + " |");
        }
    }

    public static void AppendNotes(StringBuilder sb, List<string> notes)
    {
        if (notes.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("Notes:");
        sb.AppendLine();
        foreach (var note in notes) sb.AppendLine("- " + note);
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
        sb.AppendLine($"- Bitmap size: {size.Width}×{size.Height}; icons 256×256 and 48×48");
        foreach (var content in SourceImage.Contents)
            sb.AppendLine($"- `{content}` source: {SourceImage.Get(content, size.Width, size.Height).Origin}");
        sb.AppendLine($"- ImageSharp: {typeof(SixLabors.ImageSharp.Image).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]}");
        sb.AppendLine($"- WIC: {Wic.Describe()}");
        sb.AppendLine($"- Baseline: {Baseline.Description ?? "(none built)"}");
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
                var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
                return line?.Split(':', 2)[1].Trim() ?? "unknown";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return "unknown";
    }
}
