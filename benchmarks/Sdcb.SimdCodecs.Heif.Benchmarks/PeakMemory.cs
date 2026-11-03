using System.Diagnostics;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// Peak working set per decode, each measured in a fresh process so that native memory (libheif,
/// libde265, ImageMagick's Q16 cache) is counted — which <see cref="GC.GetTotalAllocatedBytes"/>
/// cannot see, and which is why a managed-allocation column is not a memory comparison.
/// <para>Protocol in the child: load the fixture, allocate and touch the destination, decode once
/// while a sampler thread polls the working set, report the peak and its rise over the baseline.</para>
/// </summary>
internal static class PeakMemory
{
    public sealed record Sample(string Fixture, string Contestant, long BaselineBytes, long PeakBytes, long OutputBytes, double Milliseconds)
    {
        public long RiseBytes => PeakBytes - BaselineBytes;
    }

    public static readonly string[] Contestants = ["ours", "libheif", "magick"];

    /// <summary>The parent: one fresh child process per (contestant, fixture), median of <paramref name="repeats"/>.</summary>
    public static string Run(string[] fixtures, int repeats)
    {
        var lines = new List<string>
        {
            $"{"fixture",-42} {"contestant",-10} {"ms",8} {"peak MB",9} {"rise MB",9} {"output MB",10}",
        };
        foreach (var fixture in fixtures)
        {
            foreach (var contestant in Contestants)
            {
                var samples = new List<Sample>();
                for (var r = 0; r < repeats; r++) samples.Add(SpawnChild(contestant, fixture));
                var median = samples.OrderBy(s => s.RiseBytes).ElementAt(samples.Count / 2);
                lines.Add($"{median.Fixture,-42} {median.Contestant,-10} {median.Milliseconds,8:F0} " +
                          $"{median.PeakBytes / 1048576.0,9:F0} {median.RiseBytes / 1048576.0,9:F0} {median.OutputBytes / 1048576.0,10:F0}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static Sample SpawnChild(string contestant, string fixture)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (!Path.GetFileName(Environment.ProcessPath!).StartsWith("Sdcb.", StringComparison.Ordinal))
        {
            psi.ArgumentList.Add(typeof(PeakMemory).Assembly.Location);
        }
        psi.ArgumentList.Add("peakmem-child");
        psi.ArgumentList.Add(contestant);
        psi.ArgumentList.Add(fixture);
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"peakmem child failed ({process.ExitCode}): {output}");
        var parts = output.Trim().Split('|');
        return new Sample(parts[0], parts[1], long.Parse(parts[2]), long.Parse(parts[3]), long.Parse(parts[4]), double.Parse(parts[5]));
    }

    /// <summary>The child: one decode, sampled.</summary>
    public static int Child(string contestant, string fixture)
    {
        var data = HeifInputs.Load(fixture);
        long outputBytes = contestant == "ours"
            ? HeifDecoder.ReadInfo(data).Width * (long)HeifDecoder.ReadInfo(data).Height * 4
            : long.Parse(fixture.Contains("8192x6144") ? "201326592" : fixture.Contains("3072x4096") ? "50331648" : "0");
        if (outputBytes == 0)
        {
            var info = HeifDecoder.ReadInfo(data);
            outputBytes = info.Width * (long)info.Height * 4;
        }

        var process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = process.WorkingSet64;

        long peak = baseline;
        var sampler = new Thread(() =>
        {
            while (true)
            {
                process.Refresh();
                long now = process.WorkingSet64;
                if (now > peak) peak = now;
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        sampler.Start();

        var stopwatch = Stopwatch.StartNew();
        switch (contestant)
        {
            case "ours":
            {
                using var image = HeifDecoder.Decode(data);
                Touch(image.Rgba);
                break;
            }
            case "libheif":
            {
                var rgba = NativeContestants.DecodeRgba(data, int.MaxValue);
                Touch(rgba);
                break;
            }
            case "magick":
            {
                using var image = new ImageMagick.MagickImage(data);
                using var pixels = image.GetPixels();
                Touch(pixels.ToByteArray(ImageMagick.PixelMapping.RGBA));
                break;
            }
            default:
                throw new ArgumentException($"Unknown contestant '{contestant}'.", nameof(contestant));
        }
        stopwatch.Stop();
        process.Refresh();
        long finalPeak = Math.Max(peak, process.WorkingSet64);
        Console.WriteLine($"{fixture}|{contestant}|{baseline}|{finalPeak}|{outputBytes}|{stopwatch.Elapsed.TotalMilliseconds:F1}");
        return 0;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Touch(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length > 0) _ = pixels[^1] + pixels[0];
    }
}
