using System.Diagnostics;
using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// Our decoder alone, single core: the HEVC stage (per tile, no container, no colour) and the full decode to
/// RGBA, median of <c>reps</c>. Meant for A/B rounds between two builds run back to back (see the M3 record):
/// no competitors, so a round takes seconds. The CPU is <c>HEIF_BENCH_CPU</c> (default 2; CPU 0 takes most of
/// the system's interrupts on Windows).
/// </summary>
internal static class Quick
{
    public static string Run(string[] fixtures, int reps)
    {
        var cpu = int.TryParse(Environment.GetEnvironmentVariable("HEIF_BENCH_CPU"), out var c) ? c : 2;
        var process = Process.GetCurrentProcess();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) process.ProcessorAffinity = (nint)(1L << cpu);
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        ThreadPool.SetMinThreads(1, minIo);
        ThreadPool.SetMaxThreads(1, maxIo);
        var lines = new List<string> { $"{"fixture",-42} {"stage ms",9} {"RGBA ms",9}   (CPU {cpu})" };
        try
        {
            foreach (var fixture in fixtures)
            {
                var data = HeifInputs.Load(fixture);
                var tiles = Tiles.Extract(data);
                var stage = Median(reps, () =>
                {
                    var sum = 0;
                    foreach (var tile in tiles) sum += Tiles.Decode(tile).Planes[0][0];
                    return sum;
                });
                var rgba = Median(reps, () => { using var image = HeifDecoder.Decode(data); return image.Rgba[0]; });
                lines.Add($"{fixture,-42} {stage,9:F1} {rgba,9:F1}");
            }
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorker, minIo);
            ThreadPool.SetMaxThreads(maxWorker, maxIo);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static double Median(int reps, Func<int> action)
    {
        action();
        var times = new List<double>(reps);
        for (var i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }
}
