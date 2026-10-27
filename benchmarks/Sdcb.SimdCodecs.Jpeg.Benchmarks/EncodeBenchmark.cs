using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using IsJpegEncoder = SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder;
using IsJpegColor = SixLabors.ImageSharp.Formats.Jpeg.JpegEncodingColor;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// JPEG <b>encode</b> comparison on the corpus masters: our encoder (and optionally the
/// <c>baseline</c> build of an earlier commit) against libjpeg-turbo (<c>tj3Compress8</c>) and
/// ImageSharp, all black boxes but ours. Every output is decoded by the same decoder
/// (libjpeg-turbo, accurate mode) and scored against the source: size, PSNR (luma and RGB) and
/// SSIM on luma. Timings are the median of <c>--reps</c> single-threaded encodes, the
/// contestants interleaved image by image so machine drift hits them alike.
/// <para>
/// Modes: <c>baseline</c> (Annex K tables; libjpeg-turbo default), <c>optimize</c> (optimised
/// Huffman tables on both sides) and <c>progressive</c> (both sides progressive).
/// <c>--gate</c> fails (exit code 1) unless, for every configuration, our mean PSNR-Y is at least
/// libjpeg-turbo's and our total size at most 1% larger.
/// </para>
/// </summary>
internal static class EncodeBenchmark
{
    private sealed record Config(int Quality, bool Subsample420)
    {
        public override string ToString() => $"q{Quality} {(Subsample420 ? "4:2:0" : "4:4:4")}";
    }

    private static readonly Config[] Configs =
    [
        new(50, true), new(75, true), new(85, true), new(90, true), new(95, true), new(90, false), new(95, false),
    ];

    private sealed record Image(string Name, byte[] Rgb, int Width, int Height);

    private sealed record Contestant(string Name, Func<Image, Config, byte[]> Encode);

    private sealed class Tally
    {
        public long Bytes, Pixels;
        public double Seconds, PsnrY, PsnrRgb, SsimY;
        public int Images;
    }

    public static int Run(string mode, int reps, int maxDim, bool gate, bool withBaseline, string? only)
    {
        Console.Write(MachineInfo.Describe());
        TurboJpeg.SelfCheck();
        var images = LoadMasters(maxDim);
        Console.WriteLine($"[encode] mode {mode}, {images.Count} images, {images.Sum(i => (long)i.Width * i.Height) / 1e6:F1} MP total, " +
                          $"max dimension {maxDim}, median of {reps}");

        var modes = mode == "all" ? new[] { "baseline", "optimize", "progressive" } : [mode];
        var failed = false;
        foreach (var m in modes)
            failed |= !RunMode(m, images, reps, gate, withBaseline, only);

        if (gate) Console.WriteLine(failed ? "[gate] FAILED" : "[gate] passed");
        return gate && failed ? 1 : 0;
    }

    private static bool RunMode(string mode, List<Image> images, int reps, bool gate, bool withBaseline, string? only)
    {
        var optimize = mode == "optimize";
        var progressive = mode == "progressive";
        if (!optimize && !progressive && mode != "baseline") throw new ArgumentException($"unknown mode '{mode}'");

        var contestants = new List<Contestant>
        {
            new("ours", (img, c) => JpegEncoder.Encode(img.Rgb, img.Width, img.Height, 3, new JpegEncodeOptions
            {
                Quality = c.Quality,
                Subsampling = c.Subsample420 ? JpegSubsampling.Chroma420 : JpegSubsampling.Chroma444,
                OptimizeHuffman = optimize,
                Progressive = progressive,
            })),
            new("libjpeg-turbo", (img, c) => TurboEncoder.Encode(img.Rgb, img.Width, img.Height,
                new EncodeSettings(c.Subsample420 ? TurboJpeg.Samp420 : TurboJpeg.Samp444, c.Quality, Optimize: optimize, Progressive: progressive))),
        };
        if (withBaseline && BaselineEncoder.TryCreate(optimize, progressive) is { } baseline)
            contestants.Insert(1, new("baseline", baseline));
        if (mode == "baseline")
        {
            contestants.Add(new("ImageSharp", (img, c) =>
            {
                using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgb24>(img.Rgb, img.Width, img.Height);
                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms, new IsJpegEncoder { Quality = c.Quality, ColorType = c.Subsample420 ? IsJpegColor.YCbCrRatio420 : IsJpegColor.YCbCrRatio444 });
                return ms.ToArray();
            }));
        }

        if (only is not null) contestants.RemoveAll(c => c.Name != "ours" && c.Name != "libjpeg-turbo" && !c.Name.Contains(only, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine();
        Console.WriteLine($"### {mode}");
        Console.WriteLine();
        Console.WriteLine("| config | encoder | MB | bpp | PSNR-Y | PSNR-RGB | SSIM-Y | MP/s | time vs turbo |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");

        // Warm up every contestant on every image until the JIT has tiered up, so the first
        // configuration is not timed against tier-0 code.
        var warm = Stopwatch.StartNew();
        do
        {
            foreach (var image in images)
                foreach (var c in contestants)
                    c.Encode(image, Configs[0]);
        }
        while (warm.Elapsed.TotalSeconds < 2);

        using var decoder = new TurboDecoder(fast: false);
        var ok = true;
        foreach (var config in Configs)
        {
            var tallies = contestants.ToDictionary(c => c.Name, _ => new Tally());
            foreach (var image in images)
            {
                foreach (var c in contestants)
                {
                    var t = tallies[c.Name];
                    var jpeg = c.Encode(image, config); // also the warm-up
                    var times = new double[reps];
                    for (var r = 0; r < reps; r++)
                    {
                        var sw = Stopwatch.StartNew();
                        c.Encode(image, config);
                        times[r] = sw.Elapsed.TotalSeconds;
                    }

                    Array.Sort(times);
                    t.Seconds += times[reps / 2];
                    t.Bytes += jpeg.Length;
                    t.Pixels += (long)image.Width * image.Height;
                    var (psnrY, psnrRgb, ssimY) = Score(image, decoder, jpeg);
                    t.PsnrY += psnrY;
                    t.PsnrRgb += psnrRgb;
                    t.SsimY += ssimY;
                    t.Images++;
                }
            }

            var turbo = tallies["libjpeg-turbo"];
            foreach (var c in contestants)
            {
                var t = tallies[c.Name];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {config} | {c.Name} | {t.Bytes / 1e6:F3} | {t.Bytes * 8.0 / t.Pixels:F3} | {t.PsnrY / t.Images:F3} | {t.PsnrRgb / t.Images:F3} | " +
                    $"{t.SsimY / t.Images:F4} | {t.Pixels / 1e6 / t.Seconds:F0} | {t.Seconds / turbo.Seconds:F2}x |"));
            }

            var ours = tallies["ours"];
            var psnrOk = ours.PsnrY / ours.Images >= turbo.PsnrY / turbo.Images;
            var sizeOk = ours.Bytes <= turbo.Bytes * 1.01;
            if (!psnrOk || !sizeOk)
            {
                ok = false;
                if (gate)
                    Console.WriteLine($"[gate] {mode} {config}: " + (psnrOk ? "" : "PSNR-Y below libjpeg-turbo ") + (sizeOk ? "" : "size more than 1% over libjpeg-turbo"));
            }
        }

        return ok;
    }

    // ---------------------------------------------------------------- inputs

    private static List<Image> LoadMasters(int maxDim)
    {
        var files = Directory.Exists(BenchPaths.Masters) ? Directory.GetFiles(BenchPaths.Masters, "*.png").Order().ToArray() : [];
        if (files.Length == 0)
            throw new InvalidOperationException($"No masters in {BenchPaths.Masters}; run scripts/fetch-corpus.sh and the 'prepare' command first.");

        var list = new List<Image>();
        foreach (var file in files)
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgb24>(file);
            if (maxDim > 0 && Math.Max(image.Width, image.Height) > maxDim)
            {
                var f = (double)maxDim / Math.Max(image.Width, image.Height);
                image.Mutate(x => x.Resize((int)Math.Round(image.Width * f), (int)Math.Round(image.Height * f), KnownResamplers.Box));
            }

            var rgb = new byte[image.Width * image.Height * 3];
            image.CopyPixelDataTo(rgb);
            list.Add(new Image(Path.GetFileNameWithoutExtension(file), rgb, image.Width, image.Height));
        }

        return list;
    }

    // ---------------------------------------------------------------- scoring

    private static (double PsnrY, double PsnrRgb, double SsimY) Score(Image src, TurboDecoder decoder, byte[] jpeg)
    {
        int w = src.Width, h = src.Height;
        var rgba = new byte[w * h * 4];
        var (dw, dh) = decoder.DecodeRgba(jpeg, rgba);
        if (dw != w || dh != h) throw new InvalidOperationException($"decoded {dw}x{dh}, expected {w}x{h}");

        var ya = new float[w * h];
        var yb = new float[w * h];
        double se = 0, seY = 0;
        for (var i = 0; i < w * h; i++)
        {
            int r0 = src.Rgb[i * 3], g0 = src.Rgb[i * 3 + 1], b0 = src.Rgb[i * 3 + 2];
            int r1 = rgba[i * 4], g1 = rgba[i * 4 + 1], b1 = rgba[i * 4 + 2];
            se += (r0 - r1) * (r0 - r1) + (g0 - g1) * (g0 - g1) + (b0 - b1) * (b0 - b1);
            ya[i] = 0.299f * r0 + 0.587f * g0 + 0.114f * b0;
            yb[i] = 0.299f * r1 + 0.587f * g1 + 0.114f * b1;
            double d = ya[i] - yb[i];
            seY += d * d;
        }

        var psnrRgb = 10 * Math.Log10(255.0 * 255 / Math.Max(se / (3.0 * w * h), 1e-12));
        var psnrY = 10 * Math.Log10(255.0 * 255 / Math.Max(seY / ((double)w * h), 1e-12));
        return (psnrY, psnrRgb, Ssim(ya, yb, w, h));
    }

    /// <summary>Mean SSIM (Wang et al. 2004 constants) over 8×8 luma windows at a stride of 4.</summary>
    private static double Ssim(float[] a, float[] b, int w, int h)
    {
        const double c1 = 6.5025, c2 = 58.5225;
        double sum = 0;
        var count = 0;
        for (var y = 0; y + 8 <= h; y += 4)
        {
            for (var x = 0; x + 8 <= w; x += 4)
            {
                double ma = 0, mb = 0;
                for (var j = 0; j < 8; j++)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var k = (y + j) * w + x + i;
                        ma += a[k];
                        mb += b[k];
                    }
                }

                ma /= 64;
                mb /= 64;
                double va = 0, vb = 0, cov = 0;
                for (var j = 0; j < 8; j++)
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var k = (y + j) * w + x + i;
                        double da = a[k] - ma, db = b[k] - mb;
                        va += da * da;
                        vb += db * db;
                        cov += da * db;
                    }
                }

                va /= 63;
                vb /= 63;
                cov /= 63;
                sum += (2 * ma * mb + c1) * (2 * cov + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                count++;
            }
        }

        return count == 0 ? 1 : sum / count;
    }

    // ---------------------------------------------------------------- baseline build

    /// <summary>The <c>baseline</c> build's <c>JpegEncoder.Encode</c>, called through a function pointer.</summary>
    private static unsafe class BaselineEncoder
    {
        public static Func<Image, Config, byte[]>? TryCreate(bool optimize, bool progressive)
        {
            if (BaselineDecoder.Description is null)
            {
                Console.WriteLine("[encode] no baseline built; run 'baseline [git-ref]' to add one");
                return null;
            }

            var asm = BaselineDecoder.Assembly;
            var optionsType = asm.GetType("Sdcb.SimdCodecs.Jpeg.JpegEncodeOptions", throwOnError: true)!;
            var encoder = asm.GetType("Sdcb.SimdCodecs.Jpeg.JpegEncoder", throwOnError: true)!;
            var method = encoder.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "Encode" && m.GetParameters().Length == 5);
            var fn = method.MethodHandle.GetFunctionPointer();

            var optProp = optionsType.GetProperty("OptimizeHuffman");
            var progProp = optionsType.GetProperty("Progressive");
            if ((optimize && optProp is null) || (progressive && progProp is null))
            {
                Console.WriteLine("[encode] the baseline build has no such mode; leaving it out");
                return null;
            }

            var subsampling = optionsType.GetProperty("Subsampling")!;
            return (img, c) =>
            {
                var options = Activator.CreateInstance(optionsType)!;
                optionsType.GetProperty("Quality")!.SetValue(options, c.Quality);
                subsampling.SetValue(options, Enum.ToObject(subsampling.PropertyType, c.Subsample420 ? 2 : 1));
                if (optimize) optProp!.SetValue(options, true);
                if (progressive) progProp!.SetValue(options, true);
                var call = (delegate*<ReadOnlySpan<byte>, int, int, int, object, byte[]>)fn;
                return call(img.Rgb, img.Width, img.Height, 3, options);
            };
        }
    }
}
