using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Avif.Benchmarks;

/// <summary>
/// Peak memory of one decode, ours vs libavif[dav1d]. "alloc MB" elsewhere counts managed allocations, which a native
/// decoder does not make, so the comparable number is the process's own: each (fixture, decoder, threads) runs in a
/// fresh child process that loads both libraries, warms up on a tiny animation (JIT, thread pool, DLLs), collects,
/// records its private commit, decodes once to RGBA, and reports the OS-tracked peak above that baseline. The output
/// buffer is allocated after the baseline for both decoders, so both counts include it.
/// </summary>
internal static partial class MemoryRunner
{
    private const string WarmUpFixture = "anim-alltools-320x240-8f.avif";

    internal static string Run(string[] stills, string[] anims)
    {
        int cores = Environment.ProcessorCount;
        var lines = new List<string>
        {
            BenchRunner.MachineHeader(),
            $"Peak memory above the warmed-up process baseline, one decode to RGBA, fresh process per cell ({Metric}; MB):",
            $"{"fixture",-44} {"RGBA MB",8} {"ours 1T",8} {"dav1d 1T",9} {"ours " + cores + "T",8} {"dav1d " + cores + "T",9}",
        };
        foreach ((string fixture, bool anim) in stills.Select(f => (f, false)).Concat(anims.Select(f => (f, true))))
        {
            byte[] data = AvifInputs.Load(fixture);
            if (!AvifImageDecoder.TryReadInfo(data, out var info))
                throw new InvalidDataException($"{fixture}: TryReadInfo failed.");
            double rgbaMb = info.Width * 4.0 * info.Height / 1048576.0;
            string Cell(string decoder, int threads) => Child(fixture, decoder, threads, anim);
            lines.Add($"{fixture,-44} {rgbaMb,8:F1} {Cell("ours", 1),8} {Cell("dav1d", 1),9} {Cell("ours", cores),8} {Cell("dav1d", cores),9}");
        }
        lines.Add("A value marked '<' did not raise the process peak above its warm-up peak: the decode's peak is at most that.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Metric => OperatingSystem.IsWindows() ? "private commit" : "resident set";

    private static string Child(string fixture, string decoder, int threads, bool anim)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string a in new[] { "memchild", fixture, decoder, threads.ToString(CultureInfo.InvariantCulture), anim ? "anim" : "still" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"memchild {fixture} {decoder} {threads} failed ({p.ExitCode}): {stderr}");
        return stdout.Trim();
    }

    /// <summary>The child: prints the peak in MB, prefixed with '&lt;' when the decode stayed under the warm-up peak.</summary>
    internal static int ChildMain(string fixture, string decoder, int threads, bool anim)
    {
        NativeAvif.SelfCheck();
        byte[] data = AvifInputs.Load(fixture);
        byte[] warm = AvifInputs.Load(WarmUpFixture);
        if (threads == 1)
            AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism = 1;

        // Warm both decoders up on a tiny animation, still and every frame, so JIT and thread start-up are not counted.
        using (var img = AvifDecoder.Decode(warm)) { _ = img.Rgba[0]; }
        _ = BenchRunner.DecodeAllOurs(warm);
        _ = NativeAvif.DecodeRgba(warm, threads);
        _ = NativeAvif.DecodeAllFramesRgba(warm, threads);

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        ResetPeak();
        (long baseline, long peakBefore) = Read();
        int sink = (decoder, anim) switch
        {
            ("ours", false) => DecodeStill(data),
            ("ours", true) => BenchRunner.DecodeAllOurs(data),
            ("dav1d", false) => NativeAvif.DecodeRgba(data, threads)[0],
            ("dav1d", true) => NativeAvif.DecodeAllFramesRgba(data, threads),
            _ => throw new ArgumentException($"Unknown decoder '{decoder}'."),
        };
        (_, long peakAfter) = Read();
        GC.KeepAlive(sink);

        double mb = (peakAfter - baseline) / 1048576.0;
        Console.WriteLine(peakAfter > peakBefore
            ? mb.ToString("F1", CultureInfo.InvariantCulture)
            : "<" + mb.ToString("F1", CultureInfo.InvariantCulture));
        return 0;
    }

    private static int DecodeStill(byte[] data)
    {
        using var img = AvifDecoder.Decode(data);
        return img.Rgba[0];
    }

    /// <summary>Linux can reset the peak resident set; Windows cannot reset the peak commit (hence the '&lt;' check).</summary>
    private static void ResetPeak()
    {
        if (OperatingSystem.IsLinux())
            File.WriteAllText("/proc/self/clear_refs", "5");
    }

    /// <summary>(current, peak) bytes: private commit on Windows, resident set on Linux.</summary>
    private static (long Current, long Peak) Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var c = new ProcessMemoryCounters { Cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            if (!GetProcessMemoryInfo(GetCurrentProcess(), ref c, c.Cb))
                throw new InvalidOperationException("GetProcessMemoryInfo failed.");
            return ((long)c.PagefileUsage, (long)c.PeakPagefileUsage);
        }
        if (OperatingSystem.IsLinux())
        {
            long Field(string name) => long.Parse(File.ReadLines("/proc/self/status").First(l => l.StartsWith(name, StringComparison.Ordinal))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
            return (Field("VmRSS:"), Field("VmHWM:"));
        }
        throw new PlatformNotSupportedException("The memory benchmark needs Windows or Linux.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint cb);
}
