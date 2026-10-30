using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

/// <summary>
/// Peak working set per operation, each measured in a fresh process, so native and pooled-unmanaged
/// memory (WIC, ImageSharp's pixel buffers) counts as well as managed allocations. (The protocol is the
/// PNG benchmark's.)
/// <para>Protocol in the child: run the contestant once on a 16×16 image of the same case (JIT, library
/// load and type initialisation excluded), set up the full-size case for that contestant alone (input,
/// destination or source buffers allocated <b>and touched</b>), collect, then run it once while a
/// sampler thread polls the working set: the rise is the operation's working memory ("first"). Then
/// collect and run it again: the rise with pooled buffers already resident ("steady").</para>
/// </summary>
internal static class PeakMemory
{
    public sealed record Sample(string Case, string Contestant, long FirstRise, long SteadyRise);

    public static string Run(bool decode, int repeats = 3)
    {
        string self = typeof(PeakMemory).Assembly.Location;
        var samples = new List<Sample>();
        IEnumerable<string> cases = decode ? DecodeCase.Selected.Select(c => c.Name) : EncodeCase.Selected.Select(c => c.Name);
        foreach (string name in cases)
        {
            // Which contestants this case has: from the driver's (verified) setup.
            List<string> contestants = decode
                ? DecodeSetup.Create(DecodeCase.Find(name)).Contestants.Select(c => c.Name).ToList()
                : EncodeSetup.Create(EncodeCase.Find(name)).Contestants.Select(c => c.Name).ToList();
            foreach (string contestant in contestants)
            {
                var runs = new List<Sample>();
                for (int r = 0; r < repeats; r++)
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
                    if (!Path.GetFileName(Environment.ProcessPath!).StartsWith("Sdcb.", StringComparison.Ordinal))
                    {
                        psi.ArgumentList.Add(self);
                    }

                    foreach (string arg in new[] { "peakmem-child", decode ? "decode" : "encode", name, contestant })
                    {
                        psi.ArgumentList.Add(arg);
                    }

                    psi.Environment[BenchPaths.RootVariable] = BenchPaths.Root;
                    using Process p = Process.Start(psi)!;
                    string line = p.StandardOutput.ReadToEnd().Trim().Split('\n').Last();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"peakmem child failed for {contestant} {name}: {line}");
                    }

                    string[] f = line.Split('\t');
                    runs.Add(new Sample(name, contestant, long.Parse(f[0], CultureInfo.InvariantCulture), long.Parse(f[1], CultureInfo.InvariantCulture)));
                }

                Sample median = runs.OrderBy(s => s.FirstRise).ElementAt(runs.Count / 2);
                samples.Add(median);
                Console.WriteLine($"[peakmem] {name,-26} {contestant,-16} first {Mib(median.FirstRise),8} MiB  steady {Mib(median.SteadyRise),8} MiB");
            }
        }

        var size = Settings.Size;
        var sb = new StringBuilder();
        sb.AppendLine($"### Peak working set per {(decode ? "decode" : "encode")}, MiB (one fresh process per run, median of {repeats})");
        sb.AppendLine();
        sb.AppendLine("\"first\" is the working-set rise during the first full-size operation, after a warm-up on a 16×16 image of " +
                      "the same case (JIT and library loading excluded) and with the operation's own inputs and outputs already " +
                      "allocated and touched — " + (decode
                          ? $"the {size.Width}×{size.Height} destination ({Mib((long)size.Width * size.Height * 4)} MiB) is not part of it"
                          : "the source samples (and ImageSharp's source image) are not part of it, the encoded output is") +
                      ". \"steady\" is the rise of a second operation, pooled buffers already resident. Native and unmanaged " +
                      "memory counts, so WIC and ImageSharp are measured on the same footing as the managed codecs.");
        sb.AppendLine();
        string[] order = [Names.Current, Names.CurrentStream, Names.CurrentBgra, Names.Baseline, Names.ImageSharp, Names.Wic];
        string[] present = order.Where(o => samples.Any(s => s.Contestant == o)).ToArray();
        sb.AppendLine("| Case | " + string.Join(" | ", present.Select(p => Names.Label(p) + " first / steady")) + " |");
        sb.AppendLine("|---|" + string.Concat(present.Select(_ => "---:|")));
        foreach (var group in samples.GroupBy(s => s.Case))
        {
            sb.AppendLine($"| `{group.Key}` | " + string.Join(" | ", present.Select(p =>
                group.FirstOrDefault(s => s.Contestant == p) is { } s ? $"{Mib(s.FirstRise)} / {Mib(s.SteadyRise)}" : "—")) + " |");
        }

        return sb.ToString();
    }

    /// <summary>Child-process body. Prints "firstRise\tsteadyRise" in bytes.</summary>
    public static int Child(bool decode, string caseName, string contestant)
    {
        // Warm-up on a 16×16 image of the same case: JIT, library load, static tables.
        string? size = Environment.GetEnvironmentVariable(Settings.SizeVariable);
        Environment.SetEnvironmentVariable(Settings.SizeVariable, "16x16");
        Single(decode, caseName, contestant).Run();
        Environment.SetEnvironmentVariable(Settings.SizeVariable, size);

        Contestant op = Single(decode, caseName, contestant);
        TouchManagedHeap();
        long first = Measure(op.Run);
        TouchManagedHeap();
        long steady = Measure(op.Run);
        Console.WriteLine($"{first}\t{steady}");
        return 0;
    }

    private static Contestant Single(bool decode, string caseName, string contestant)
    {
        IReadOnlyList<Contestant> list = decode
            ? DecodeSetup.Create(DecodeCase.Find(caseName), only: contestant).Contestants
            : EncodeSetup.Create(EncodeCase.Find(caseName), only: contestant).Contestants;
        return list.Count == 1 ? list[0] : throw new InvalidOperationException($"{contestant} cannot run {caseName}");
    }

    private static void TouchManagedHeap()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private static long Measure(Action operation)
    {
        long baseline = CurrentWorkingSet();
        // The OS peak counter covers the process lifetime, so a transient spike before the baseline
        // would hide a smaller rise during the operation. Poll the current working set instead.
        long peak = baseline;
        int done = 0;
        var sampler = new Thread(() =>
        {
            while (Volatile.Read(ref done) == 0)
            {
                peak = Math.Max(peak, CurrentWorkingSet());
            }
        })
        { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();
        operation();
        Volatile.Write(ref done, 1);
        sampler.Join();
        peak = Math.Max(peak, CurrentWorkingSet());
        return peak - baseline;
    }

    private static string Mib(long bytes) => (bytes / 1048576.0).ToString("F2", CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(nint process, out ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    /// <summary>Current resident set: GetProcessMemoryInfo on Windows, /proc/self/statm elsewhere.</summary>
    private static long CurrentWorkingSet()
    {
        if (OperatingSystem.IsWindows())
        {
            GetProcessMemoryInfo(GetCurrentProcess(), out ProcessMemoryCounters c, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
            return (long)c.WorkingSetSize;
        }

        string[] fields = File.ReadAllText("/proc/self/statm").Split(' ');
        return long.Parse(fields[1], CultureInfo.InvariantCulture) * Environment.SystemPageSize;
    }
}
