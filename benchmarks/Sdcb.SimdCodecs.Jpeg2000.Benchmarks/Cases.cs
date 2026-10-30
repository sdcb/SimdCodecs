using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal sealed record BenchCase(
    string Name,
    string Description,
    int Width,
    int Height,
    int Components,
    bool Lossless,
    Func<byte[]> LoadData);

internal static class Cases
{
    public static readonly BenchCase[] All =
    [
        // Small Fixtures
        new("fixture-rgb64-lossless", "64x64 RGB 5/3 Lossless", 64, 64, 3, true,
            () => File.ReadAllBytes(Path.Combine(BenchPaths.FixturesLossless, "rgb-struct64.j2k"))),
        new("fixture-noise64-lossless", "64x64 Gray 5/3 Lossless", 64, 64, 1, true,
            () => File.ReadAllBytes(Path.Combine(BenchPaths.FixturesLossless, "noise64.j2k"))),
        new("fixture-dwt5-lossless", "64x64 Gray 5/3 5-level DWT", 64, 64, 1, true,
            () => File.ReadAllBytes(Path.Combine(BenchPaths.FixturesLossless, "dwt5-struct64.j2k"))),
        new("fixture-rgb64-lossy", "64x64 RGB 9/7 Lossy", 64, 64, 3, false,
            () => File.ReadAllBytes(Path.Combine(BenchPaths.FixturesLossy, "lossy97-rgb-struct64.j2k"))),
        new("fixture-gray64-lossy", "64x64 Gray 9/7 Lossy", 64, 64, 1, false,
            () => File.ReadAllBytes(Path.Combine(BenchPaths.FixturesLossy, "lossy97-struct64.j2k"))),

        // 512x512
        new("fuji-512-lossless", "512x512 RGB 5/3 Lossless", 512, 512, 3, true,
            () => EnsureGenerated(512, 512, true)),
        new("fuji-512-lossy", "512x512 RGB 9/7 Lossy (q=40)", 512, 512, 3, false,
            () => EnsureGenerated(512, 512, false, 40)),

        // 1024x1024
        new("fuji-1024-lossless", "1024x1024 RGB 5/3 Lossless", 1024, 1024, 3, true,
            () => EnsureGenerated(1024, 1024, true)),
        new("fuji-1024-lossy", "1024x1024 RGB 9/7 Lossy (q=40)", 1024, 1024, 3, false,
            () => EnsureGenerated(1024, 1024, false, 40)),

        // 1600x1200 (~2 MP)
        new("fuji-1600-lossless", "1600x1200 RGB 5/3 Lossless", 1600, 1200, 3, true,
            () => EnsureGenerated(1600, 1200, true)),
        new("fuji-1600-lossy", "1600x1200 RGB 9/7 Lossy (q=40)", 1600, 1200, 3, false,
            () => EnsureGenerated(1600, 1200, false, 40)),
    ];

    private static byte[] EnsureGenerated(int width, int height, bool lossless, int quality = 40)
    {
        Directory.CreateDirectory(BenchPaths.Inputs);
        var tag = lossless ? "lossless" : $"lossy-q{quality}";
        var j2kPath = Path.Combine(BenchPaths.Inputs, $"gen-{width}x{height}-{tag}.j2k");

        if (File.Exists(j2kPath))
        {
            return File.ReadAllBytes(j2kPath);
        }

        // Generate source PNG
        var pngPath = Path.Combine(BenchPaths.Inputs, $"src-{width}x{height}.png");
        if (!File.Exists(pngPath))
        {
            CreateSourcePng(pngPath, width, height);
        }

        // Compress with opj_compress
        string[] args = lossless
            ? []
            : ["-I", "-q", quality.ToString()];

        var j2kBytes = OpenJpeg.Compress(pngPath, args);
        File.WriteAllBytes(j2kPath, j2kBytes);
        return j2kBytes;
    }

    private static void CreateSourcePng(string destPath, int width, int height)
    {
        // Try master image first
        var masterFuji = Path.Combine(BenchPaths.RepoRoot, "benchmarks", "Sdcb.SimdCodecs.Jpeg.Benchmarks",
            ".cache", "corpus", "masters", "sunrise-fuji.png");

        if (File.Exists(masterFuji))
        {
            using var img = Image.Load<Rgba32>(masterFuji);
            img.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Crop
            }));
            img.SaveAsPng(destPath);
            return;
        }

        // Fallback: procedural pattern (gradient + circles + noise)
        using var synthetic = new Image<Rgb24>(width, height);
        synthetic.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                {
                    var r = (byte)((x * 255) / width);
                    var g = (byte)((y * 255) / height);
                    var b = (byte)(((x + y) * 128) / (width + height));
                    row[x] = new Rgb24(r, g, b);
                }
            }
        });
        synthetic.SaveAsPng(destPath);
    }
}
