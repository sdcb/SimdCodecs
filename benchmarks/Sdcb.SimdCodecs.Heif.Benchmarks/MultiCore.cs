using System.Diagnostics;
using ImageMagick;
using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// Multi-core, all contestants in this process on the same bytes, interleaved per fixture so a
/// drifting machine affects everyone alike. Times are the median of <c>reps</c> runs after one
/// warm-up; our managed allocation is recorded beside the time.
/// </summary>
internal static class MultiCore
{
    public static string Run(string[] fixtures, int reps)
    {
        ResourceLimits.Thread = 0; // ImageMagick's default (all cores)
        var lines = new List<string>
        {
            $"{"fixture",-42} {"ours ms",8} {"libheif ms",11} {"magick ms",10} {"ours alloc MB",14} {"ours MP/s",10} {"libheif MP/s",13} {"magick MP/s",12}",
        };
        foreach (var fixture in fixtures)
        {
            var data = HeifInputs.Load(fixture);
            var info = HeifDecoder.ReadInfo(data);
            double mp = info.Width * (double)info.Height / 1e6;

            var (ours, _) = Median(reps, () =>
            {
                using var image = HeifDecoder.Decode(data);
                return image.Rgba[0];
            });
            var (libheif, _) = Median(reps, () => NativeContestants.DecodeRgba(data, int.MaxValue)[0]);
            var (magick, _) = Median(reps, () =>
            {
                using var m = new MagickImage(data);
                using var px = m.GetPixels();
                return px.ToByteArray(PixelMapping.RGBA)![0];
            });

            _ = Allocated(() => { using var image = HeifDecoder.Decode(data); });
            double alloc = Allocated(() => { using var image = HeifDecoder.Decode(data); });

            lines.Add($"{fixture,-42} {ours,8:F0} {libheif,11:F0} {magick,10:F0} {alloc,14:F0} {mp / ours * 1000,10:F1} {mp / libheif * 1000,13:F1} {mp / magick * 1000,12:F1}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static double Allocated(Action action)
    {
        long before = GC.GetTotalAllocatedBytes(true);
        action();
        return (GC.GetTotalAllocatedBytes(true) - before) / 1048576.0;
    }

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
