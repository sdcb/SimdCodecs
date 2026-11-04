using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// Black-box calibration of the lossy quality scale: for each libwebp quality (simple API, default
/// method), the base quantizer index at which our encoder writes the same total bytes on the opaque
/// photos, by bisection. Only output sizes are observed. The table it prints is what
/// <c>Vp8EncoderConfig.For</c> maps quality through, so a quality gives about libwebp's file size.
/// </summary>
internal static class EncodeCalibrate
{
    public static int Run(int maxDim, string? filter)
    {
        List<EncodeImage> images = EncodeCorpus.Load(maxDim, filter).Where(i => i.Kind == "photo").ToList();
        var sb = new StringBuilder();
        sb.AppendLine(MachineInfo.Describe().TrimEnd());
        sb.AppendLine($"- {images.Count} photos; libwebp {LibWebp.EncoderVersion()}");
        sb.AppendLine();
        sb.AppendLine("| quality | libwebp bytes | index | our bytes |");
        sb.AppendLine("|---:|---:|---:|---:|");
        var table = new int[101];
        for (int quality = 0; quality <= 100; quality += 5)
        {
            long target = images.Sum(i => (long)LibWebp.EncodeRgba(i.Rgba, i.Width, i.Height, quality).Length);
            int lo = 0, hi = 127;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Bytes(images, mid) > target)
                {
                    lo = mid + 1; // too big: coarser
                }
                else
                {
                    hi = mid;
                }
            }

            // lo is the finest index not above the target; take the nearer of it and the next finer one.
            int index = lo;
            if (lo > 0 && Math.Abs(Bytes(images, lo - 1) - target) < Math.Abs(Bytes(images, lo) - target))
            {
                index = lo - 1;
            }

            table[quality] = index;
            string row = string.Create(CultureInfo.InvariantCulture, $"| {quality} | {target} | {index} | {Bytes(images, index)} |");
            Console.WriteLine(row);
            sb.AppendLine(row);
        }

        // Linear in between the measured points.
        for (int quality = 0; quality < 100; quality++)
        {
            int a = quality / 5 * 5, b = Math.Min(100, a + 5);
            table[quality] = (int)Math.Round(table[a] + (table[b] - table[a]) * (quality - a) / 5.0);
        }

        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine("private static ReadOnlySpan<byte> QualityToIndex =>");
        sb.AppendLine("[");
        for (int i = 0; i <= 100; i += 10)
        {
            sb.AppendLine("    " + string.Join(", ", table.Skip(i).Take(Math.Min(10, 101 - i))) + ",");
        }

        sb.AppendLine("];");
        sb.AppendLine("```");
        Directory.CreateDirectory(BenchPaths.Work);
        string file = Path.Combine(BenchPaths.Work, "encode-calibrate.md");
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine(sb.ToString());
        Console.WriteLine($"[calibrate] {file}");
        return 0;
    }

    private static readonly Dictionary<int, long> Cache = [];

    private static long Bytes(List<EncodeImage> images, int index)
    {
        if (!Cache.TryGetValue(index, out long bytes))
        {
            var config = Vp8EncoderConfig.For(75, 4) with { QuantIndex = index };
            bytes = images.Sum(i => (long)WebpEncoder.EncodeLossy(i.Rgba, i.Width, i.Height, PixelFormat.Rgba32, config).Length);
            Cache[index] = bytes;
        }

        return bytes;
    }
}
