using System.Diagnostics;
using System.Runtime.Intrinsics;
using ImageMagick;
using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// One logical CPU for the whole process, the way the m0 record measured "single core" — with the
/// difference that makes it honest: our decoder is run with its tile parallelism <b>off</b>, libheif
/// with <c>heif_context_set_max_decoding_threads(_, 0)</c>, libde265 with no worker threads, and
/// Magick with <c>ResourceLimits.Thread = 1</c>. Pinning a process that still spawns 16 workers only
/// measures contention.
/// </summary>
internal static class SingleCore
{
    public static string Run(string[] fixtures, int reps, bool verify)
    {
        var process = Process.GetCurrentProcess();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) process.ProcessorAffinity = 1;

        var lines = new List<string>
        {
            $"pinned to CPU 0 | Vector128 {Vector128.IsHardwareAccelerated}, Vector256 {Vector256.IsHardwareAccelerated}, Vector512 {Vector512.IsHardwareAccelerated}",
            $"{"fixture",-42} {"stage ours",10} {"stage libde265",14} {"ours yuv",9} {"ours RGBA",10} {"libheif yuv",12} {"libheif RGBA",13} {"magick",8}",
        };

        ResourceLimits.Thread = 1;
        // Serial execution without touching the package: with one worker thread, Parallel.For runs the
        // tile loop inline on the calling thread, which is what "one core, no parallelism" means.
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        ThreadPool.SetMinThreads(1, minIo);
        ThreadPool.SetMaxThreads(1, maxIo);
        try
        {
            foreach (var fixture in fixtures)
            {
                var data = HeifInputs.Load(fixture);
                var tiles = Tiles.Extract(data);

                var (stageOurs, _) = Median(reps, () =>
                {
                    var sum = 0;
                    foreach (var tile in tiles) sum += Tiles.Decode(tile).Planes[0][0];
                    return sum;
                });
                var (stageLib, _) = Median(reps, () =>
                {
                    var sum = 0;
                    foreach (var tile in tiles) sum += NativeContestants.DecodeTile(tile).Y[0];
                    return sum;
                });
                var (oursYuv, yuvSample) = Median(reps, () => { var (y, _, _, _, _, _) = HeifDecoder.DecodeYuv(data); return y[0]; });
                var (oursRgba, rgbaSample) = Median(reps, () => { using var i = HeifDecoder.Decode(data); return i.Rgba[0]; });
                var (libYuv, _) = Median(reps, () => NativeContestants.DecodeYuv(data, 0).Y[0]);
                var (libRgba, _) = Median(reps, () => NativeContestants.DecodeRgba(data, 0)[0]);
                var (magick, _) = Median(reps, () =>
                {
                    using var m = new MagickImage(data);
                    using var px = m.GetPixels();
                    return px.ToByteArray(PixelMapping.RGBA)![0];
                });
                lines.Add($"{fixture,-42} {stageOurs,10:F0} {stageLib,14:F0} {oursYuv,9:F0} {oursRgba,10:F0} {libYuv,12:F0} {libRgba,13:F0} {magick,8:F0}");

                if (verify)
                {
                    // Serial and parallel decode must produce the identical pixels; the first sample is
                    // the cheap probe, DetCheck compares whole buffers when that needs proving.
                    ThreadPool.SetMinThreads(minWorker, minIo);
                    ThreadPool.SetMaxThreads(maxWorker, maxIo);
                    int parallelSample = HeifDecoder.Decode(data).Rgba[0];
                    ThreadPool.SetMinThreads(1, minIo);
                    ThreadPool.SetMaxThreads(1, maxIo);
                    if (parallelSample != rgbaSample)
                    {
                        throw new InvalidOperationException(
                            $"{fixture}: serial decode gives RGBA sample {rgbaSample}, parallel decode {parallelSample}.");
                    }
                    _ = yuvSample;
                }
            }
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorker, minIo);
            ThreadPool.SetMaxThreads(maxWorker, maxIo);
            ResourceLimits.Thread = 0;
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Median wall time over <paramref name="reps"/> runs after one warm-up, with the value the last run returned.</summary>
    private static (double Milliseconds, int Sample) Median(int reps, Func<int> action)
    {
        int sample = action();
        var times = new List<double>(reps);
        for (var i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            sample = action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return (times[times.Count / 2], sample);
    }
}
