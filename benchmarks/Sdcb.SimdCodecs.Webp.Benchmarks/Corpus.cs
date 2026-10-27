using System.Diagnostics;
using System.Text;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// A synthetic verification corpus: deterministic images (gradients, sinusoids, noise, hard edges,
/// varying alpha) encoded by the official <c>cwebp</c> with a spread of options, then decoded by
/// both this decoder and libwebp and compared byte for byte. Nothing third-party; lives in the
/// git-ignored cache. It complements the small committed fixtures with large frames that exercise
/// every macroblock path, partition count, filter type and alpha filter.
/// </summary>
internal static class Corpus
{
    public static string Dir => Path.Combine(BenchPaths.Cache, "corpus");

    private static readonly (string Name, int Width, int Height, int Seed, bool Alpha)[] Images =
    [
        ("mixed-1920x1080", 1920, 1080, 1, false),
        ("alpha-1920x1080", 1920, 1080, 5, true),
        ("mixed-1001x777", 1001, 777, 2, true),
        ("noisy-640x480", 640, 480, 3, false),
        ("small-33x17", 33, 17, 4, true),
    ];

    private static readonly (string Suffix, string Options)[] Encodings =
    [
        ("q10", "-q 10"),
        ("q50", "-q 50"),
        ("q75-m6", "-q 75 -m 6"),
        ("q90", "-q 90"),
        ("q100", "-q 100 -m 4"),
        ("q75-simple", "-q 75 -nostrong"),
        ("q60-sharp7", "-q 60 -sharpness 7 -f 80"),
        ("q75-f0", "-q 75 -f 0"),
        ("q70-seg2-sharpyuv", "-q 70 -segments 2 -sharp_yuv"),
        ("q70-seg1", "-q 70 -segments 1"),
        ("q80-sns100", "-q 80 -sns 100 -f 100"),
        ("alpha-q75", "-q 75 -alpha_q 100"),
        ("alpha-lossy-fast", "-q 75 -alpha_q 50 -alpha_filter fast"),
        ("alpha-best", "-q 75 -alpha_filter best -alpha_method 1"),
        ("alpha-raw", "-q 75 -alpha_method 0"),
        ("lossless-z0", "-lossless -z 0"),
        ("lossless-z6", "-lossless -z 6"),
        ("lossless-z9", "-lossless -z 9"),
        ("lossless-near60", "-near_lossless 60"),
        ("lossless-exact", "-lossless -exact -z 3"),
    ];

    public static void Build(string cwebp)
    {
        Directory.CreateDirectory(Dir);
        foreach ((string name, int width, int height, int seed, bool alpha) in Images)
        {
            // cwebp on Windows reads through WIC, which has no PAM codec, so the input is a PNG.
            string pam = Path.Combine(Dir, name + ".png");
            byte[] rgba = Synthesize(width, height, seed, alpha);
            if (alpha)
            {
                File.WriteAllBytes(pam, Png.PngWriter.Encode(rgba, width, height));
            }
            else
            {
                byte[] rgb = new byte[width * height * 3];
                for (int i = 0, j = 0; i < rgba.Length; i += 4, j += 3)
                {
                    rgb[j] = rgba[i];
                    rgb[j + 1] = rgba[i + 1];
                    rgb[j + 2] = rgba[i + 2];
                }

                File.WriteAllBytes(pam, Png.PngWriter.EncodeRgb8(rgb, width, height));
            }
            foreach ((string suffix, string options) in Encodings)
            {
                string output = Path.Combine(Dir, $"{name}-{suffix}.webp");
                var psi = new ProcessStartInfo(cwebp, $"-quiet {options} \"{pam}\" -o \"{output}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                using Process p = Process.Start(psi)!;
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    throw new InvalidOperationException($"cwebp {options} failed: {err}");
                }
            }

            File.Delete(pam);
        }

        Console.WriteLine($"[corpus] {Directory.GetFiles(Dir, "*.webp").Length} files in {Dir}");
    }

    /// <summary>Decodes every corpus file (plus the benchmark inputs) with both decoders and compares.</summary>
    public static int Verify()
    {
        var files = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "*.webp").ToList() : [];
        files.AddRange(AbBenchmarks.AllInputs.Select(WebpBenchmarkInputs.PathOf));
        int failures = 0;
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            byte[] source = File.ReadAllBytes(file);
            WebpInfo info = WebpDecoder.ReadInfo(source);
            byte[] ours = new byte[info.Width * info.Height * 4];
            byte[] reference = new byte[ours.Length];
            WebpDecoder.DecodeTo(source, ours);
            LibWebp.DecodeRgbaInto(source, reference, info.Width, info.Height);
            int mismatch = FirstDifference(ours, reference);
            if (mismatch >= 0)
            {
                failures++;
                int pixel = mismatch / 4;
                Console.WriteLine($"[verify] FAIL {Path.GetFileName(file)}: first difference at pixel ({pixel % info.Width}, {pixel / info.Width}) " +
                                  $"channel {mismatch % 4}: ours {ours[mismatch]}, libwebp {reference[mismatch]}");
            }
            else
            {
                Console.WriteLine($"[verify] ok   {Path.GetFileName(file)} ({info.Width}x{info.Height}, {info.Format})");
            }
        }

        Console.WriteLine(failures == 0 ? $"[verify] all {files.Count} identical" : $"[verify] {failures} of {files.Count} differ");
        return failures == 0 ? 0 : 1;
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] Synthesize(int width, int height, int seed, bool hasAlpha)
    {
        var random = new Random(seed);
        byte[] result = new byte[width * height * 4];
        Span<byte> pixels = result;
        int rectangles = 12;
        var rects = new (int X0, int Y0, int X1, int Y1, byte R, byte G, byte B)[rectangles];
        for (int i = 0; i < rectangles; i++)
        {
            int x0 = random.Next(width), y0 = random.Next(height);
            rects[i] = (x0, y0, x0 + random.Next(1, width / 3 + 2), y0 + random.Next(1, height / 3 + 2),
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double fx = (double)x / width, fy = (double)y / height;
                int r = (int)(255 * fx), g = (int)(255 * fy), b = (int)(128 + 100 * Math.Sin(x * 0.05 + y * 0.03));
                // Bands of texture, noise and flat colour so every token / mode path is exercised.
                int band = (x / 97 + y / 61) % 4;
                if (band == 1)
                {
                    int n = random.Next(-60, 61);
                    r += n; g += n / 2; b -= n;
                }
                else if (band == 2)
                {
                    int checker = ((x >> 2) ^ (y >> 2)) & 1;
                    r = checker * 255; g = 255 - r;
                }

                foreach (var rect in rects)
                {
                    if (x >= rect.X0 && x < rect.X1 && y >= rect.Y0 && y < rect.Y1)
                    {
                        r = rect.R; g = rect.G; b = rect.B;
                    }
                }

                int alpha = !hasAlpha ? 255 : (x / 64 + y / 48) % 5 == 0 ? 0 : (int)(255 * (0.5 + 0.5 * Math.Cos(fx * 7 + fy * 3)));
                int offset = (y * width + x) * 4;
                pixels[offset] = (byte)Math.Clamp(r, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(g, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(b, 0, 255);
                pixels[offset + 3] = (byte)Math.Clamp(alpha, 0, 255);
            }
        }

        return result;
    }
}
