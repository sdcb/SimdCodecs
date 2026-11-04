using System.Diagnostics;
using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// Attributes single-core decode time to stages by switching one stage off at a time
/// (<see cref="HeifAblation"/>) and timing the full decode to RGBA: the time that disappears is the stage's
/// share. Pinned to CPU 0 with tile parallelism off, like <see cref="SingleCore"/>. What no switch removes —
/// slice parsing, CABAC and residual parsing, side information — is the remainder.
/// </summary>
internal static class Ablation
{
    private static readonly (string Name, HeifAblation Flags)[] Cases =
    [
        ("full", HeifAblation.None),
        ("-residual", HeifAblation.SkipResidual),
        ("-intra", HeifAblation.SkipIntraPred),
        ("-deblock", HeifAblation.SkipDeblock),
        ("-sao", HeifAblation.SkipSao),
        ("-color", HeifAblation.SkipColor),
        ("parse only", HeifAblation.SkipResidual | HeifAblation.SkipIntraPred | HeifAblation.SkipDeblock | HeifAblation.SkipSao | HeifAblation.SkipColor),
    ];

    public static string Run(string[] fixtures, int reps)
    {
        var process = Process.GetCurrentProcess();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) process.ProcessorAffinity = 1;
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        ThreadPool.SetMinThreads(1, minIo);
        ThreadPool.SetMaxThreads(1, maxIo);

        var header = $"{"fixture",-42}" + string.Concat(Cases.Select(c => $" {c.Name,11}"));
        var lines = new List<string> { "pinned to CPU 0; ms, median; the delta column is full minus the case", header };
        try
        {
            foreach (var fixture in fixtures)
            {
                var data = HeifInputs.Load(fixture);
                var times = new double[Cases.Length];
                for (var i = 0; i < Cases.Length; i++)
                {
                    HeifDiagnostics.Ablation = Cases[i].Flags;
                    times[i] = Median(reps, () => { using var image = HeifDecoder.Decode(data); return image.Rgba[0]; });
                }
                HeifDiagnostics.Ablation = HeifAblation.None;
                lines.Add($"{fixture,-42}" + string.Concat(times.Select(t => $" {t,11:F1}")));
                lines.Add($"{"  share (full - case)",-42}" + string.Concat(times.Select(t => $" {times[0] - t,11:F1}")));
            }
        }
        finally
        {
            HeifDiagnostics.Ablation = HeifAblation.None;
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
