using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// The display layouts of <see cref="JpegImageDecoder.TryDecodeInto"/>: for each case and each
/// <see cref="ImagePixelFormat"/> other than RGBA, the native path (the colour conversion writes the
/// layout; grey frames copy their samples) against the two-step path a caller of an RGBA-only API takes
/// (decode to RGBA, then <see cref="PixelConverter"/> over the frame), with RGBA itself and
/// libjpeg-turbo's <c>tj3Decompress8</c> in the same layout for scale. The two of ours must produce
/// identical bytes, checked before timing. Every contestant is warmed for 300 ms, then timed in
/// interleaved rounds; the median per contestant is reported.
/// </summary>
internal static class Formats
{
    private static readonly (ImagePixelFormat Format, int Turbo)[] Targets =
    [
        (ImagePixelFormat.Bgra32, TurboJpeg.PfBgra), (ImagePixelFormat.Rgb24, TurboJpeg.PfRgb),
        (ImagePixelFormat.Bgr24, TurboJpeg.PfBgr), (ImagePixelFormat.Gray8, TurboJpeg.PfGray),
    ];

    public static int Run(IReadOnlyList<string> cases, int rounds)
    {
        Console.Write(MachineInfo.Describe());
        TurboJpeg.SelfCheck();
        using var turbo = new TurboDecoder(fast: false);
        var sb = new StringBuilder();
        sb.Append(MachineInfo.Describe()).AppendLine();
        sb.AppendLine($"### Decode into each layout (in-process medians of {rounds} interleaved rounds, ms)");
        sb.AppendLine();
        sb.AppendLine("`native` = `JpegImageDecoder.TryDecodeInto(format)`; `two-step` = decode to RGBA + `PixelConverter.ConvertRow` over " +
                      "the frame (what an RGBA-only API leaves a caller to do); `rgba` = decode to RGBA alone; turbo = libjpeg-turbo " +
                      "`tj3Decompress8` in the same layout (accurate IDCT, fancy upsampling). Grey from a YCbCr JPEG is its Y plane for " +
                      "both (chroma is never inverse-transformed, upsampled or converted), and `two-step` is then the luma of the RGB, a " +
                      "different (slower) result shown for scale; `max ∆ turbo` is the largest difference from turbo's grey. Ratios below 1 " +
                      "mean native is faster. `÷ baseline`: native against the last commit's decoder in the same layout (built with " +
                      "`baseline`; it had RGBA, BGRA and RGB only), with RGBA's own ratio in brackets.");
        sb.AppendLine();
        sb.AppendLine("| Case | MP | Layout | rgba | native | two-step | turbo | native ÷ two-step | native ÷ rgba | native ÷ turbo | ÷ baseline | max ∆ turbo |");
        sb.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (string id in cases)
        {
            byte[] jpeg = File.ReadAllBytes(Path.Combine(BenchPaths.Matrix, id.Replace('/', Path.DirectorySeparatorChar) + ".jpg"));
            JpegInfo info = JpegDecoder.ReadInfo(jpeg);
            int pixels = info.Width * info.Height;
            var rgba = new byte[pixels * 4];
            foreach ((ImagePixelFormat format, int turboFormat) in Targets)
            {
                int bpp = PixelConverter.BytesPerPixel(format);
                var native = new byte[pixels * bpp];
                var twoStep = new byte[pixels * bpp];
                var turboOut = new byte[pixels * bpp];
                var baselineOut = new byte[pixels * bpp];
                var contestants = new List<(string Name, Action Run)>
                {
                    ("rgba", () => JpegDecoder.DecodeTo(jpeg, rgba)),
                    ("native", () => Check(JpegImageDecoder.TryDecodeInto(jpeg, native, 0, format))),
                    ("two-step", () =>
                    {
                        JpegDecoder.DecodeTo(jpeg, rgba);
                        PixelConverter.ConvertRow(rgba, 4, SampleFormat.UInt8, twoStep, format, pixels);
                    }),
                    ("turbo", () => turbo.Decode(jpeg, turboOut, turboFormat, bpp)),
                };
                // The last commit's decoder, for the layouts it already had (same process, interleaved).
                int baselineFormat = format switch { ImagePixelFormat.Bgra32 => 1, ImagePixelFormat.Rgb24 => 2, _ => -1 };
                if (BaselineDecoder.Description is not null && baselineFormat >= 0)
                {
                    contestants.Add(("baseline", () => BaselineDecoder.DecodeTo(jpeg, baselineOut, info.Width * bpp, baselineFormat)));
                    contestants.Add(("baseline-rgba", () => BaselineDecoder.DecodeTo(jpeg, rgba)));
                }

                // Correctness before timing.
                foreach (var c in contestants)
                    c.Run();
                // The contract: the converted RGBA, except grey from a YCbCr frame, which is its Y plane.
                var planes = format == ImagePixelFormat.Gray8 ? JpegDiagnostics.DecodePlanes(jpeg) : null;
                bool yPlane = planes is { Length: 3 } && planes[0].Width == info.Width && planes[0].Height == info.Height;
                if (!native.AsSpan().SequenceEqual(yPlane ? planes![0].Samples : twoStep))
                    throw new InvalidOperationException($"{id} {format}: the native path differs from {(yPlane ? "the Y plane" : "decode + convert")}");
                int maxDelta = 0;
                for (int i = 0; i < native.Length; i++)
                    maxDelta = Math.Max(maxDelta, Math.Abs(native[i] - turboOut[i]));
                if (contestants.Any(c => c.Name == "baseline") && !native.AsSpan().SequenceEqual(baselineOut))
                    throw new InvalidOperationException($"{id} {format}: the output differs from the baseline's");

                Dictionary<string, double> ms = Time(contestants, rounds);
                Console.WriteLine($"[formats] {id,-36} {format,-6} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {kv.Value:F3}")));
                string vsBaseline = ms.TryGetValue("baseline", out double b)
                    ? $"{ms["native"] / b:F2} (rgba {ms["rgba"] / ms["baseline-rgba"]:F2})"
                    : "—";
                sb.AppendLine($"| `{id}` | {pixels / 1e6:F1} | {format} | {F(ms["rgba"])} | {F(ms["native"])} | {F(ms["two-step"])} | " +
                              $"{F(ms["turbo"])} | {ms["native"] / ms["two-step"]:F2} | {ms["native"] / ms["rgba"]:F2} | {ms["native"] / ms["turbo"]:F2} | {vsBaseline} | {maxDelta} |");
            }
        }

        Directory.CreateDirectory(BenchPaths.Work);
        string file = Path.Combine(BenchPaths.Work, "formats.md");
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine();
        Console.WriteLine(sb.ToString());
        Console.WriteLine("[report] " + file);
        return 0;
    }

    private static Dictionary<string, double> Time(List<(string Name, Action Run)> contestants, int rounds)
    {
        foreach (var c in contestants)
        {
            long warm = Stopwatch.GetTimestamp();
            for (int i = 0; i < 3 || Stopwatch.GetElapsedTime(warm).TotalMilliseconds < 300; i++)
                c.Run();
        }

        var samples = contestants.ToDictionary(c => c.Name, _ => new List<double>());
        for (int round = 0; round < rounds; round++)
        {
            foreach (var c in contestants)
            {
                int ops = 0;
                long start = Stopwatch.GetTimestamp();
                do
                {
                    c.Run();
                    ops++;
                }
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 50);
                samples[c.Name].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / ops);
            }
        }

        return samples.ToDictionary(kv => kv.Key, kv =>
        {
            var sorted = kv.Value.Order().ToList();
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        });
    }

    private static void Check(bool ok)
    {
        if (!ok)
            throw new InvalidOperationException("decode failed");
    }

    private static string F(double ms) => ms.ToString(ms < 10 ? "F3" : "F2", CultureInfo.InvariantCulture);
}
