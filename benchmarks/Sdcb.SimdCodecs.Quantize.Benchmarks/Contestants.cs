using System.Diagnostics;
using Sdcb.SimdCodecs.Png;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Dithering;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using GifQuantizer = Sdcb.SimdCodecs.Gif.PaletteQuantizer;

namespace Sdcb.SimdCodecs.Quantize.Benchmarks;

/// <summary>One quantization: the reduced image as RGBA, how long it took, and the competitor's own file when it writes one.</summary>
internal sealed record Quantized(byte[] Rgba, double Milliseconds, long? NativeBytes);

/// <summary>A palette quantizer under test: reduce an image to at most <c>colors</c> RGBA colours.</summary>
internal interface IContestant
{
    string Name { get; }
    Quantized Run(EncodeImage image, int colors, bool dither);
}

#if HAS_BASELINE
/// <summary>
/// The quantizer as it was at the commit <c>.cache/baseline/PaletteQuantizer.cs</c> came from (see README): Wu's box
/// split in RGB, alpha below 128 transparent and every other pixel opaque. One entry goes to the transparent colour
/// when there is one.
/// </summary>
internal sealed class BaselineWu : IContestant
{
    public string Name => "baseline-wu";

    public Quantized Run(EncodeImage image, int colors, bool dither)
    {
        var sw = Stopwatch.StartNew();
        int n = image.Width * image.Height;
        int[] argb = Argb(image);
        bool anyTransparent = argb.Any(Baseline.PaletteQuantizer.IsTransparent);
        var quantizer = new Baseline.PaletteQuantizer();
        foreach (int p in argb)
        {
            if (!Baseline.PaletteQuantizer.IsTransparent(p)) quantizer.Add(p & 0xFFFFFF);
        }

        int[] palette = quantizer.BuildPalette(anyTransparent ? colors - 1 : colors);
        var indices = new byte[n];
        int transparentIndex = palette.Length;
        new Baseline.PaletteQuantizer.Mapper(palette).Map(argb, image.Width, image.Height, indices, transparentIndex, dither);
        return new Quantized(OursQuantizer.Expand(indices, palette, transparentIndex, opaque: true), sw.Elapsed.TotalMilliseconds, null);
    }

    internal static int[] Argb(EncodeImage image) => OursQuantizer.Argb(image);
}
#endif

/// <summary>
/// The current shared quantizer (<c>src/Shared/PaletteQuantizer.cs</c>, as GIF compiles it) in its RGBA mode, the
/// way the PNG encoder uses it; <paramref name="iterations"/> and <paramref name="perceptual"/> are for ablation.
/// </summary>
internal sealed class OursQuantizer(string name, int iterations = 16, bool perceptual = true, int maxDistinct = GifQuantizer.MaxDistinct,
    bool? serpentine = null, int? diffusion = null, int? edgeLimit = null) : IContestant
{
    public string Name => name;

    public Quantized Run(EncodeImage image, int colors, bool dither)
    {
        var sw = Stopwatch.StartNew();
        int[] argb = Argb(image);
        var quantizer = new GifQuantizer(alpha: true, maxDistinct) { Iterations = iterations, Perceptual = perceptual };
        foreach (int p in argb) quantizer.Add(p);
        int[] palette = quantizer.BuildPalette(colors);
        var indices = new byte[argb.Length];
        var defaults = new GifQuantizer.Mapper([0], alpha: true);
        var mapper = new GifQuantizer.Mapper(palette, alpha: true, perceptual)
        {
            Serpentine = serpentine ?? defaults.Serpentine,
            Diffusion = diffusion ?? defaults.Diffusion,
            EdgeLimit = edgeLimit ?? defaults.EdgeLimit,
        };
        mapper.Map(argb, image.Width, image.Height, indices, -1, dither);
        return new Quantized(Expand(indices, palette, -1, opaque: false), sw.Elapsed.TotalMilliseconds, null);
    }

    /// <summary>The three stages of <see cref="Run"/> timed apart (ms): histogram, palette, mapping.</summary>
    internal static (double Histogram, double Palette, double Map) Stages(EncodeImage image, int colors, bool dither)
    {
        int[] argb = Argb(image);
        var sw = Stopwatch.StartNew();
        var quantizer = new GifQuantizer(alpha: true);
        foreach (int p in argb) quantizer.Add(p);
        double h = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        int[] palette = quantizer.BuildPalette(colors);
        double b = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var indices = new byte[argb.Length];
        new GifQuantizer.Mapper(palette, alpha: true).Map(argb, image.Width, image.Height, indices, -1, dither);
        return (h, b, sw.Elapsed.TotalMilliseconds);
    }

    internal static int[] Argb(EncodeImage image)
    {
        int n = image.Width * image.Height;
        var argb = new int[n];
        byte[] s = image.Rgba;
        for (int i = 0; i < n; i++) argb[i] = (s[4 * i + 3] << 24) | (s[4 * i] << 16) | (s[4 * i + 1] << 8) | s[4 * i + 2];
        return argb;
    }

    internal static byte[] Expand(byte[] indices, int[] palette, int transparentIndex, bool opaque)
    {
        var rgba = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] == transparentIndex) continue;
            int p = palette[indices[i]];
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = ((byte)(p >> 16), (byte)(p >> 8), (byte)p, opaque ? (byte)255 : (byte)(p >> 24));
        }

        return rgba;
    }
}

/// <summary>
/// The PNG encoder's lossy mode end to end: <c>PngWriter.Encode</c> with <c>PngWriteOptions.Quantize</c>, decoded back
/// with our reader. Time includes the PNG encode (deflate), like pngquant's.
/// </summary>
internal sealed class OursPng(System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Optimal) : IContestant
{
    public string Name => "ours-png";

    public Quantized Run(EncodeImage image, int colors, bool dither) =>
        Run(image, new PngQuantizeOptions { MaxColors = colors, Dither = dither });

    public Quantized Run(EncodeImage image, PngQuantizeOptions quantize)
    {
        var sw = Stopwatch.StartNew();
        byte[] file = PngWriter.Encode(image.Rgba, image.Width, image.Height, new PngWriteOptions { Quantize = quantize, CompressionLevel = level });
        double ms = sw.Elapsed.TotalMilliseconds;
        PngImage png = PngReader.Decode(file);
        var rgba = new byte[image.Rgba.Length];
        png.ExpandToRgba8(rgba);
        return new Quantized(rgba, ms, file.Length);
    }
}

/// <summary>ImageSharp's quantizers through the documented <c>Quantize</c> processor (black box).</summary>
internal sealed class ImageSharpQuantizer(string name, Func<QuantizerOptions, IQuantizer> make) : IContestant
{
    public string Name => name;

    public Quantized Run(EncodeImage image, int colors, bool dither)
    {
        using Image<Rgba32> img = Image.LoadPixelData<Rgba32>(image.Rgba, image.Width, image.Height);
        var options = new QuantizerOptions { MaxColors = colors, Dither = dither ? KnownDitherings.FloydSteinberg : null };
        var sw = Stopwatch.StartNew();
        img.Mutate(x => x.Quantize(make(options)));
        double ms = sw.Elapsed.TotalMilliseconds;
        var rgba = new byte[image.Rgba.Length];
        img.CopyPixelDataTo(rgba);
        return new Quantized(rgba, ms, null);
    }
}

/// <summary>
/// The pngquant command-line tool (GPL; run only as a black-box binary, never read). Time is the whole process: PNG
/// decode, quantize, PNG encode and start-up, so it overstates the quantizer alone.
/// </summary>
internal sealed class PngquantCli(string exe, int speed) : IContestant
{
    public string Name => $"pngquant-s{speed}";

    public string Version => Exec(exe, ["--version"]).Trim();

    public Quantized Run(EncodeImage image, int colors, bool dither)
    {
        string input = Input(image);
        string output = Path.Combine(BenchPaths.Work, $"pq-{image.Name}-{colors}-{(dither ? "fs" : "nofs")}-s{speed}.png");
        var args = new List<string> { "--force", "--strip", "--speed", speed.ToString(), "--output", output };
        if (!dither) args.Add("--nofs");
        args.Add(colors.ToString());
        args.Add(input);
        var sw = Stopwatch.StartNew();
        Exec(exe, args);
        double ms = sw.Elapsed.TotalMilliseconds;
        byte[] file = File.ReadAllBytes(output);
        PngImage png = PngReader.Decode(file);
        var rgba = new byte[image.Rgba.Length];
        png.ExpandToRgba8(rgba);
        return new Quantized(rgba, ms, file.Length);
    }

    /// <summary>pngquant's own quality search: <c>--quality=min-max</c>; null when it refuses (exit 99, quality below min).</summary>
    public Quantized? RunQuality(EncodeImage image, int min, int max, bool dither)
    {
        string input = Input(image);
        string output = Path.Combine(BenchPaths.Work, $"pq-{image.Name}-q{min}-{max}-{(dither ? "fs" : "nofs")}-s{speed}.png");
        var args = new List<string> { "--force", "--strip", "--speed", speed.ToString(), "--quality", $"{min}-{max}", "--output", output };
        if (!dither) args.Add("--nofs");
        args.Add(input);
        var sw = Stopwatch.StartNew();
        try
        {
            Exec(exe, args);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("exited 99"))
        {
            return null;
        }

        double ms = sw.Elapsed.TotalMilliseconds;
        byte[] file = File.ReadAllBytes(output);
        PngImage png = PngReader.Decode(file);
        var rgba = new byte[image.Rgba.Length];
        png.ExpandToRgba8(rgba);
        return new Quantized(rgba, ms, file.Length);
    }

    private static readonly Dictionary<string, string> Inputs = [];

    private static string Input(EncodeImage image)
    {
        lock (Inputs)
        {
            if (!Inputs.TryGetValue(image.Name, out string? path))
            {
                Directory.CreateDirectory(BenchPaths.Work);
                path = Path.Combine(BenchPaths.Work, $"in-{image.Name}.png");
                File.WriteAllBytes(path, PngWriter.Encode(image.Rgba, image.Width, image.Height,
                    new PngWriteOptions { CompressionLevel = System.IO.Compression.CompressionLevel.Fastest }));
                Inputs[image.Name] = path;
            }

            return path;
        }
    }

    private static string Exec(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} exited {p.ExitCode}: {stderr.Result}");
        return stdout + stderr.Result;
    }
}
