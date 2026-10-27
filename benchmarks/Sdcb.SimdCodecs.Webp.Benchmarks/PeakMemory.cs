using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// Peak working set per decode, each measured in a fresh process so native memory (libwebp's
/// own allocations) is counted — which a managed-allocation diagnoser cannot see.
/// <para>Protocol in the child: JIT / load the decoder on a tiny file of the same kind, allocate
/// <b>and touch</b> the RGBA destination, collect, record the working set, then decode the file
/// while a sampler thread polls the current working set ("first decode": the working memory the
/// decode needs). Then collect and decode it again ("steady": pooled buffers already resident,
/// as in a long-running process).</para>
/// </summary>
internal static class PeakMemory
{
    public static readonly string[] Contestants = ["LibWebp", "Baseline", "Current"];

    public sealed record Sample(string Input, string Contestant, long FirstRiseBytes, long BaselineBytes, long PeakBytes)
    {
        public long RiseBytes => PeakBytes - BaselineBytes;
    }

    public static string Run(IReadOnlyList<string> inputs, int repeats = 3)
    {
        string self = typeof(PeakMemory).Assembly.Location;
        var samples = new List<Sample>();
        bool hasBaseline = BaselineDecoder.Description is not null;
        foreach (string input in inputs)
        {
            foreach (string contestant in Contestants)
            {
                if (contestant == "Baseline" && !hasBaseline)
                {
                    continue;
                }

                var runs = new List<Sample>();
                for (int r = 0; r < repeats; r++)
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                    };
                    if (!Path.GetFileName(Environment.ProcessPath!).StartsWith("Sdcb.", StringComparison.Ordinal))
                    {
                        psi.ArgumentList.Add(self);
                    }

                    psi.ArgumentList.Add("peakmem-child");
                    psi.ArgumentList.Add(contestant);
                    psi.ArgumentList.Add(input);
                    psi.Environment[BenchPaths.RootVariable] = BenchPaths.Root;
                    using Process p = Process.Start(psi)!;
                    string line = p.StandardOutput.ReadToEnd().Trim().Split('\n').Last();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"peakmem child failed for {contestant} {input}: {line}");
                    }

                    string[] f = line.Split('\t');
                    runs.Add(new Sample(input, contestant,
                        long.Parse(f[0], CultureInfo.InvariantCulture),
                        long.Parse(f[1], CultureInfo.InvariantCulture),
                        long.Parse(f[2], CultureInfo.InvariantCulture)));
                }

                Sample median = runs.OrderBy(s => s.RiseBytes).ElementAt(runs.Count / 2);
                samples.Add(median);
                Console.WriteLine($"[peakmem] {input,-26} {contestant,-9} first-decode rise {Mib(median.FirstRiseBytes),7} MiB, " +
                                  $"steady peak {Mib(median.PeakBytes),7} MiB, rise {Mib(median.RiseBytes),7} MiB");
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("Peak working set, one fresh process per decode, median of 3 (MiB). \"first decode\" is the rise of the " +
                              "first full-size decode after a warm-up on a tiny file of the same kind (JIT excluded): the decode's " +
                              "working memory;  \"steady\" is a second decode of the same " +
                      "file with pooled buffers already resident. The 1920×1080 RGBA destination (7.9 MiB) is allocated and " +
                      "touched before either baseline, so it is not part of any rise.");
        sb.AppendLine();
        sb.AppendLine("| Input | contestant | first-decode rise | steady peak | steady rise |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        foreach (Sample s in samples)
        {
            sb.AppendLine($"| `{s.Input}` | {s.Contestant} | {Mib(s.FirstRiseBytes)} | {Mib(s.PeakBytes)} | {Mib(s.RiseBytes)} |");
        }

        Directory.CreateDirectory(BenchPaths.Work);
        string file = Path.Combine(BenchPaths.Work, "peakmem.md");
        File.WriteAllText(file, sb.ToString());
        return sb.ToString();
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

    /// <summary>Child-process body. Prints "firstRise\tbaseline\tpeak".</summary>
    public static int Child(string contestant, string input)
    {
        byte[] source = WebpBenchmarkInputs.Read(input);
        WebpInfo info = WebpDecoder.ReadInfo(source);
        byte[] rgba = new byte[info.Width * info.Height * 4];
        Action decode = contestant switch
        {
            "Current" => () => WebpDecoder.DecodeTo(source, rgba),
            "Baseline" => () => BaselineDecoder.DecodeTo(source, rgba),
            "LibWebp" => () => LibWebp.DecodeRgbaInto(source, rgba, info.Width, info.Height),
            _ => throw new ArgumentOutOfRangeException(nameof(contestant)),
        };

        // JIT / library load on a tiny file of the same kind, so the measured rise is the decode's
        // working memory, not code.
        string tinyName = input switch
        {
            WebpBenchmarkInputs.Lossless => "lossless-3x7.webp",
            WebpBenchmarkInputs.LossyAlpha => "lossy-alpha-q50.webp",
            _ => "gradient-16x16.webp",
        };
        byte[] tiny = File.ReadAllBytes(Path.Combine(BenchPaths.Root, "..", "..", "tests", "Sdcb.SimdCodecs.Tests",
            "Fixtures", "webp", tinyName));
        WebpInfo tinyInfo = WebpDecoder.ReadInfo(tiny);
        byte[] tinyRgba = new byte[tinyInfo.Width * tinyInfo.Height * 4];
        switch (contestant)
        {
            case "Current": WebpDecoder.DecodeTo(tiny, tinyRgba); break;
            case "Baseline": BaselineDecoder.DecodeTo(tiny, tinyRgba); break;
            default: LibWebp.DecodeRgbaInto(tiny, tinyRgba, tinyInfo.Width, tinyInfo.Height); break;
        }

        rgba.AsSpan().Fill(0xFF); // commit the destination's pages before any baseline
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        long firstRise = Measure(decode, out _);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Measure(decode, out (long Baseline, long Peak) steady);
        Console.WriteLine($"{firstRise}\t{steady.Baseline}\t{steady.Peak}");
        return 0;
    }

    private static long Measure(Action decode, out (long Baseline, long Peak) result)
    {
        long baseline = CurrentWorkingSet();

        // The OS peak counter covers the whole process lifetime, so a transient spike before the
        // baseline would hide a smaller decode-time rise. Poll the current working set instead.
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
        decode();
        Volatile.Write(ref done, 1);
        sampler.Join();
        peak = Math.Max(peak, CurrentWorkingSet());
        result = (baseline, peak);
        return peak - baseline;
    }
}
