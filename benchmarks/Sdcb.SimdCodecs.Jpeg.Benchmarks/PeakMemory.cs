using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Jpeg;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// Peak working set per decode, each measured in a fresh process so native memory
/// (libjpeg-turbo) and ImageSharp's pooled unmanaged buffers are counted — which a
/// managed-allocation diagnoser cannot see.
/// <para>Protocol in the child: load the file, allocate <b>and touch</b> the RGBA
/// destination (ours / libjpeg-turbo; ImageSharp allocates its own), JIT/load the
/// decoder on a tiny image, collect, record the working set, then decode the file once
/// while a sampler thread polls the current working set; report the peak and its rise
/// over that baseline.</para>
/// </summary>
internal static class PeakMemory
{
    public static readonly string[] DefaultCases =
    [
        "420-q90-12mp/landscape-almsee",
        "444-q90-12mp/landscape-almsee",
        "420-q90-12mp/night-alexanderplatz",
        "444-q90-12mp/night-alexanderplatz",
        "420-q95-3mp-prog/landscape-almsee",
        "camera/cam-sony-a7r3",
    ];

    public sealed record Sample(string Case, Contestant Contestant, long BaselineBytes, long PeakBytes, long OutputBytes)
    {
        public long RiseBytes => PeakBytes - BaselineBytes;
    }

    public static string ResultFile => Path.Combine(BenchPaths.Work, "peakmem.tsv");

    public static void Run(IReadOnlyList<string> cases, int repeats = 3)
    {
        var self = typeof(PeakMemory).Assembly.Location;
        var samples = new List<Sample>();
        foreach (var id in cases)
        {
            foreach (var c in new[] { Contestant.LibjpegTurbo, Contestant.Ours, Contestant.ImageSharp })
            {
                var runs = new List<Sample>();
                for (var r = 0; r < repeats; r++)
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                    };
                    if (!Path.GetFileName(Environment.ProcessPath!).StartsWith("Sdcb.", StringComparison.Ordinal))
                        psi.ArgumentList.Add(self);
                    psi.ArgumentList.Add("peakmem-child");
                    psi.ArgumentList.Add(c.ToString());
                    psi.ArgumentList.Add(id);
                    psi.Environment[BenchPaths.RootVariable] = BenchPaths.Root;
                    using var p = Process.Start(psi)!;
                    var line = p.StandardOutput.ReadToEnd().Trim().Split('\n').Last();
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new InvalidOperationException($"peakmem child failed for {c} {id}: {line}");
                    var f = line.Split('\t');
                    runs.Add(new Sample(id, c, long.Parse(f[0], CultureInfo.InvariantCulture), long.Parse(f[1], CultureInfo.InvariantCulture),
                        long.Parse(f[2], CultureInfo.InvariantCulture)));
                }

                var median = runs.OrderBy(s => s.RiseBytes).ElementAt(runs.Count / 2);
                samples.Add(median);
                Console.WriteLine($"[peakmem] {id,-40} {Contestants.Label(c),-16} peak {median.PeakBytes / 1048576.0,8:F1} MiB  rise {median.RiseBytes / 1048576.0,8:F1} MiB");
            }
        }

        Directory.CreateDirectory(BenchPaths.Work);
        using var w = new StreamWriter(ResultFile);
        w.WriteLine("case\tcontestant\tbaseline_bytes\tpeak_bytes\trise_bytes\toutput_bytes");
        foreach (var s in samples)
            w.WriteLine(string.Join('\t', s.Case, s.Contestant, s.BaselineBytes, s.PeakBytes, s.RiseBytes, s.OutputBytes));
    }

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
            GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<ProcessMemoryCounters>());
            return (long)c.WorkingSetSize;
        }

        var fields = File.ReadAllText("/proc/self/statm").Split(' ');
        return long.Parse(fields[1], CultureInfo.InvariantCulture) * Environment.SystemPageSize;
    }

    /// <summary>Child-process body. Prints "baseline\tpeak\toutputBytes".</summary>
    public static int Child(string contestant, string id)
    {
        var c = Enum.Parse<Contestant>(contestant);
        var jpeg = File.ReadAllBytes(Path.Combine(BenchPaths.Matrix, id.Replace('/', Path.DirectorySeparatorChar) + ".jpg"));
        var info = JpegDecoder.ReadInfo(jpeg);
        var outputBytes = (long)info.Width * info.Height * 4;

        var tiny = JpegEncoder.Encode(new byte[16 * 16 * 3], 16, 16, 3);
        byte[]? rgba = null;
        TurboDecoder? turbo = null;
        switch (c)
        {
            case Contestant.Ours:
                rgba = new byte[outputBytes];
                JpegDecoder.DecodeTo(tiny, rgba);
                break;
            case Contestant.LibjpegTurbo:
                rgba = new byte[outputBytes];
                turbo = new TurboDecoder(fast: false);
                turbo.DecodeRgba(tiny, rgba);
                break;
            case Contestant.ImageSharp:
                using (Image.Load<Rgba32>(Contestants.ImageSharpOptions, tiny)) { }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contestant));
        }

        rgba?.AsSpan().Fill(0xFF); // commit the destination's pages before the baseline
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        var baseline = CurrentWorkingSet();

        // The OS peak counter covers the whole process lifetime, so a transient spike before the
        // baseline would hide a smaller decode-time rise. Poll the current working set instead.
        long peak = baseline;
        var done = 0;
        var sampler = new Thread(() =>
        {
            while (Volatile.Read(ref done) == 0)
                peak = Math.Max(peak, CurrentWorkingSet());
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        sampler.Start();

        switch (c)
        {
            case Contestant.Ours:
                JpegDecoder.DecodeTo(jpeg, rgba);
                break;
            case Contestant.LibjpegTurbo:
                turbo!.DecodeRgba(jpeg, rgba);
                break;
            case Contestant.ImageSharp:
                using (var image = Image.Load<Rgba32>(Contestants.ImageSharpOptions, jpeg))
                {
                    peak = Math.Max(peak, CurrentWorkingSet());
                    GC.KeepAlive(image);
                }

                break;
        }

        Volatile.Write(ref done, 1);
        sampler.Join();
        peak = Math.Max(peak, CurrentWorkingSet());
        Console.WriteLine($"{baseline}\t{peak}\t{outputBytes}");
        turbo?.Dispose();
        return 0;
    }
}
