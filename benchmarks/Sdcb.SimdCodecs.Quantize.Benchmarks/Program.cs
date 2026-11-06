using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Sdcb.SimdCodecs.Quantize.Benchmarks;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

const string Usage = """
    Palette quantizer benchmark: quality, size and speed of ours against pngquant (black-box CLI) and ImageSharp.
    Run from the repository root with
    dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks -- rd [options]:

      --max-dim N        long side of the photo inputs (default 1024)
      --images TEXT      only inputs whose name contains TEXT
      --colors LIST      palette sizes to sweep (default 256,128,64,32,16)
      --dither MODE      off, on or both (default both)
      --reps N           timing repetitions, best kept (default 3; pngquant runs once per point after a warm-up)
      --pngquant PATH    the pngquant executable (default: pngquant on PATH)
      --speeds LIST      pngquant --speed values (default 4)
      --contestants LIST only these (pngquant always runs: it is the reference)
      --ablate-dither    add ours with dithering variants: ours-raster, ours-full-fs (plain Floyd–Steinberg), ours-no-edge, ours-diff16, ours-edge16/64
      --ablate           add ours with stages switched off or resized: ours-no-kmeans, ours-rgb-space, ours-kmeans4/20, ours-hist64k/16k

    Photos are the JPEG benchmark's masters (run its scripts/fetch-corpus.sh and 'prepare' first); the rest is synthetic.
    """;

if (args.Length > 0 && args[0] == "stages")
{
    // Time per stage of ours: histogram, palette (split + k-means), mapping; best of 5.
    string? stageImages = args.SkipWhile(a => a != "--images").Skip(1).FirstOrDefault();
    foreach (EncodeImage image in Corpus.Load(1024, stageImages))
    {
        foreach (bool dither in new[] { false, true })
        {
            double h = double.MaxValue, b = double.MaxValue, m = double.MaxValue;
            for (int r = 0; r < 5; r++)
            {
                (double h1, double b1, double m1) = OursQuantizer.Stages(image, 256, dither);
                (h, b, m) = (Math.Min(h, h1), Math.Min(b, b1), Math.Min(m, m1));
            }

            Console.WriteLine($"{image.Name,-28} {(dither ? "fs" : "-"),3}  histogram {h,7:F1} ms  palette {b,7:F1} ms  map {m,7:F1} ms");
        }
    }

    return 0;
}

if (args.Length == 0 || args[0] != "rd")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 0 : 1;
}

string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
int maxDim = int.Parse(Opt("--max-dim") ?? "1024", CultureInfo.InvariantCulture);
int[] colorCounts = (Opt("--colors") ?? "256,128,64,32,16").Split(',').Select(int.Parse).ToArray();
bool[] dithers = (Opt("--dither") ?? "both") switch { "off" => [false], "on" => [true], _ => [false, true] };
int reps = int.Parse(Opt("--reps") ?? "3", CultureInfo.InvariantCulture);
string pngquant = Opt("--pngquant") ?? "pngquant";
int[] speeds = (Opt("--speeds") ?? "4").Split(',').Select(int.Parse).ToArray();

Console.WriteLine($"Machine: {RuntimeInformation.OSArchitecture}, {RuntimeInformation.OSDescription}, .NET {Environment.Version}, " +
                  $"{Environment.ProcessorCount} logical CPUs, Vector256 {Vector256.IsHardwareAccelerated}, Vector512 {Vector512.IsHardwareAccelerated}");

var reference = new List<PngquantCli>();
foreach (int s in speeds) reference.Add(new PngquantCli(pngquant, s));
Console.WriteLine($"pngquant: {reference[0].Version}");

var contestants = new List<IContestant>(reference)
{
#if HAS_BASELINE
    new BaselineWu(),
#endif
    new OursQuantizer("ours"),
    new ImageSharpQuantizer("imagesharp-wu", o => new WuQuantizer(o)),
    new ImageSharpQuantizer("imagesharp-octree", o => new OctreeQuantizer(o)),
};

if (args.Contains("--ablate"))
{
    contestants.Add(new OursQuantizer("ours-no-kmeans", iterations: 0));
    contestants.Add(new OursQuantizer("ours-rgb-space", perceptual: false));
    contestants.Add(new OursQuantizer("ours-kmeans4", iterations: 4));
    contestants.Add(new OursQuantizer("ours-kmeans20", iterations: 20));
    contestants.Add(new OursQuantizer("ours-hist64k", maxDistinct: 1 << 16));
    contestants.Add(new OursQuantizer("ours-hist16k", maxDistinct: 1 << 14));
}

if (args.Contains("--ablate-dither"))
{
    contestants.Add(new OursQuantizer("ours-raster", serpentine: false));
    contestants.Add(new OursQuantizer("ours-full-fs", diffusion: 16, edgeLimit: 0));
    contestants.Add(new OursQuantizer("ours-no-edge", edgeLimit: 0));
    contestants.Add(new OursQuantizer("ours-diff16", diffusion: 16));
    contestants.Add(new OursQuantizer("ours-edge16", edgeLimit: 16));
    contestants.Add(new OursQuantizer("ours-edge64", edgeLimit: 64));
}

if (Opt("--contestants") is { } keep)
{
    string[] names = keep.Split(',');
    contestants.RemoveAll(c => c is not PngquantCli && !names.Contains(c.Name));
}

List<EncodeImage> images = Corpus.Load(maxDim, null);
images.Add(Corpus.WithAlpha(images).Single(i => i.Name == "sticker"));
if (Opt("--images") is { } only) images = images.Where(i => i.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

// (contestant, image, dither) → points; one line per point.
var points = new Dictionary<(string C, string I, bool D), List<(long Bytes, double Ssim, double Ok, double Blur, double Ms)>>();
Console.WriteLine();
Console.WriteLine("image                  px     contestant          dith colors  used  common-B  native-B  SSIM-dB  OKLab-RMS  blurred      ms");
foreach (EncodeImage image in images)
{
    foreach (bool dither in dithers)
    {
        foreach (IContestant c in contestants)
        {
            c.Run(image, colorCounts[0], dither); // warm-up: JIT, caches, the pngquant input file
            foreach (int colors in colorCounts)
            {
                Quantized best = c.Run(image, colors, dither);
                int runs = c is PngquantCli ? 1 : reps;
                for (int r = 1; r < runs; r++)
                {
                    Quantized q = c.Run(image, colors, dither);
                    if (q.Milliseconds < best.Milliseconds) best = q;
                }

                int used = Metrics.DistinctColors(best.Rgba);
                if (used > colors) throw new InvalidOperationException($"{c.Name} used {used} colours on {image.Name}, asked for {colors}");
                long bytes = Metrics.CommonBytes(best.Rgba, image.Width, image.Height);
                double ssim = Metrics.SsimDb(image.Rgba, best.Rgba, image.Width, image.Height, image.HasAlpha);
                double ok = Metrics.OklabRms(image.Rgba, best.Rgba);
                double blur = Metrics.BlurredOklabRms(image.Rgba, best.Rgba, image.Width, image.Height);
                Console.WriteLine($"{image.Name,-20} {image.Pixels / 1e6,5:F2}M  {c.Name,-19} {(dither ? "fs" : "-"),4} {colors,6} {used,5} {bytes,9} " +
                                  $"{(best.NativeBytes is { } nb ? nb.ToString() : ""),9} {ssim,8:F2} {ok,10:F3} {blur,8:F3} {best.Milliseconds,7:F1}");
                if (!points.TryGetValue((c.Name, image.Name, dither), out var list)) points[(c.Name, image.Name, dither)] = list = [];
                list.Add((bytes, ssim, ok, blur, best.Milliseconds));
            }
        }
    }
}

// Summary: BD-rate against the first pngquant speed (size at equal SSIM, and at equal OKLab error), and time ratio.
string refName = reference[0].Name;
Console.WriteLine();
Console.WriteLine($"Summary against {refName}: BD-rate (bytes at equal quality; negative = smaller), mean over images; time = geometric mean ratio.");
Console.WriteLine("contestant          dith  BD(SSIM)  BD(OKLab)  BD(blur)   time×   worst-image BD(SSIM)");
foreach (bool dither in dithers)
{
    foreach (IContestant c in contestants.Where(c => c.Name != refName))
    {
        var bdSsim = new List<double>();
        var bdOk = new List<double>();
        var bdBlur = new List<double>();
        var logTime = new List<double>();
        (string Name, double Bd) worst = ("", double.NegativeInfinity);
        foreach (EncodeImage image in images)
        {
            var a = points[(refName, image.Name, dither)];
            var b = points[(c.Name, image.Name, dither)];
            double s = Metrics.BdRate(a.Select(p => (p.Bytes, p.Ssim)), b.Select(p => (p.Bytes, p.Ssim)));
            // Lower OKLab error is better: negate it so the curve rises with quality.
            double o = Metrics.BdRate(a.Select(p => (p.Bytes, -p.Ok)), b.Select(p => (p.Bytes, -p.Ok)));
            if (!double.IsNaN(s)) bdSsim.Add(s);
            if (!double.IsNaN(o)) bdOk.Add(o);
            double bl = Metrics.BdRate(a.Select(p => (p.Bytes, -p.Blur)), b.Select(p => (p.Bytes, -p.Blur)));
            if (!double.IsNaN(bl)) bdBlur.Add(bl);
            if (!double.IsNaN(s) && s > worst.Bd) worst = (image.Name, s);
            for (int k = 0; k < a.Count; k++) logTime.Add(Math.Log(b[k].Ms / a[k].Ms));
        }

        static string Pct(List<double> v) => v.Count == 0 ? "n/a" : $"{v.Average() * 100:+0.0;-0.0}%";
        Console.WriteLine($"{c.Name,-19} {(dither ? "fs" : "-"),4} {Pct(bdSsim),9} {Pct(bdOk),10} {Pct(bdBlur),9} {Math.Exp(logTime.Average()),7:F2}   " +
                          (worst.Name.Length > 0 ? $"{worst.Name} {worst.Bd * 100:+0.0;-0.0}%" : ""));
    }
}

return 0;

namespace Sdcb.SimdCodecs.Quantize.Benchmarks
{
    /// <summary>Locates the benchmark project directory and its git-ignored cache.</summary>
    internal static class BenchPaths
    {
        public const string RootVariable = "SIMDCODECS_BENCH_ROOT";

        private static readonly Lazy<string> LazyRoot = new(FindRoot);

        /// <summary><c>benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks</c>.</summary>
        public static string Root => LazyRoot.Value;

        public static string Cache => Path.Combine(Root, ".cache");
        public static string Work => Path.Combine(Cache, "work");

        private static string FindRoot()
        {
            string? configured = Environment.GetEnvironmentVariable(RootVariable);
            if (!string.IsNullOrEmpty(configured)) return configured;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Sdcb.SimdCodecs.Quantize.Benchmarks.csproj"))) return dir.FullName;
            }

            throw new DirectoryNotFoundException("Cannot find benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks; set " + RootVariable);
        }
    }
}
