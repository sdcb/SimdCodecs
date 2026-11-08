using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// Our decoder alone, single core: the HEVC stage (per tile, no container, no colour) and the full decode to
/// RGBA, median of <c>reps</c>. Meant for A/B rounds between two builds run back to back (see the M3 record):
/// no competitors, so a round takes seconds. The CPU is <c>HEIF_BENCH_CPU</c> (default 2; CPU 0 takes most of
/// the system's interrupts on Windows); <c>HEIF_BENCH_CPU=all</c> runs on every core with the default thread pool.
/// <para>
/// <c>--scaled</c> adds the adapter's scaled decode (<c>ScaleDenominator</c> 2, 4, 8, to Rgb24 / Rgb48): native (each
/// tile boxed as it is written) next to the path it replaces in the same run (the full <c>TryDecode</c>, then
/// <see cref="BoxDownscaler.Downscale"/>), after checking the two give the same bytes.
/// </para>
/// </summary>
internal static class Quick
{
    private static readonly int[] Factors = [2, 4, 8];

    public static string Run(string[] fixtures, int reps, bool scaled = false)
    {
        var setting = Environment.GetEnvironmentVariable("HEIF_BENCH_CPU");
        var all = setting == "all";
        var cpu = int.TryParse(setting, out var c) ? c : 2;
        var process = Process.GetCurrentProcess();
        if (!all && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())) process.ProcessorAffinity = (nint)(1L << cpu);
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        if (!all)
        {
            ThreadPool.SetMinThreads(1, minIo);
            ThreadPool.SetMaxThreads(1, maxIo);
        }
        var where = all ? $"{Environment.ProcessorCount} logical CPUs" : $"CPU {cpu}";
        var lines = new List<string> { $"{"fixture",-42} {"stage ms",9} {"RGBA ms",9}   ({where})" };
        var scaledLines = new List<string> { "", $"{"fixture",-42} {"d",3} {"native ms",10} {"boxed ms",9} {"native/boxed",13}   ({where})" };
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
                if (!scaled) continue;

                foreach (var d in Factors)
                {
                    var options = new DecodeOptions { ScaleDenominator = d };
                    // Correctness before timing: the native scaled image is the box filter of the full decode.
                    using (var native = ScaledNative(data, options))
                    using (var boxed = ScaledBoxed(data, d))
                    {
                        if (!native.Pixels.SequenceEqual(boxed.Pixels))
                            throw new InvalidOperationException($"{fixture} d{d}: the native scaled decode differs from the box filter.");
                    }
                    var a = Median(reps, () => { using var image = ScaledNative(data, options); return image.Pixels[0]; });
                    var b = Median(reps, () => { using var image = ScaledBoxed(data, d); return image.Pixels[0]; });
                    scaledLines.Add($"{fixture,-42} {d,3} {a,10:F1} {b,9:F1} {a / b,13:F3}");
                }
            }
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorker, minIo);
            ThreadPool.SetMaxThreads(maxWorker, maxIo);
        }
        if (scaled) lines.AddRange(scaledLines);
        return string.Join(Environment.NewLine, lines);
    }

    private static IDecodedImage ScaledNative(byte[] data, DecodeOptions options) =>
        HeifImageDecoder.TryDecode(data, out var image, options) ? image : throw new InvalidOperationException("The scaled decode failed.");

    private static RasterImage ScaledBoxed(byte[] data, int factor)
    {
        if (!HeifImageDecoder.TryDecode(data, out var full)) throw new InvalidOperationException("The decode failed.");
        using (full)
            return BoxDownscaler.Downscale(full, factor);
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
