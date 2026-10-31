using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>One encoder input: tightly packed RGBA, 8 bits per sample.</summary>
internal sealed record EncodeImage(string Name, byte[] Rgba, int Width, int Height, bool HasAlpha, string Kind)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// The encode benchmark's inputs. Photographs are the JPEG benchmark's corpus masters (CC0 / Public
/// Domain, see <c>Sdcb.SimdCodecs.Jpeg.Benchmarks/corpus/manifest.tsv</c>), box-downscaled to
/// <c>maxDim</c> on the long side; the rest is synthetic and deterministic, so nothing third-party is
/// committed. Content is chosen for what a lossless encoder does with it:
/// <list type="bullet">
/// <item><c>photo-*</c>: natural content, where prediction + entropy coding decide.</item>
/// <item><c>photo-alpha</c>: a photo with a soft vignette into full transparency and a hard cut-out.</item>
/// <item><c>screen</c> / <c>screen-alpha</c>: flat UI panels, glyph-like text, one gradient thumbnail;
/// backward references and the colour cache decide.</item>
/// <item><c>palette</c>: a 12-colour chart, where colour indexing (pixel bundling) decides.</item>
/// </list>
/// </summary>
internal static class EncodeCorpus
{
    public static string Masters => Path.Combine(BenchPaths.Root, "..", "Sdcb.SimdCodecs.Jpeg.Benchmarks", ".cache", "corpus", "masters");

    public static List<EncodeImage> Load(int maxDim, string? filter)
    {
        var list = new List<EncodeImage>();
        string[] masters = Directory.Exists(Masters) ? Directory.GetFiles(Masters, "*.png").Order(StringComparer.Ordinal).ToArray() : [];
        if (masters.Length == 0)
        {
            Console.WriteLine($"[encode] no photo masters in {Path.GetFullPath(Masters)}: photos are left out. Run the JPEG benchmark's " +
                              "scripts/fetch-corpus.sh and its 'prepare' command to add them.");
        }

        foreach (string file in masters)
        {
            string name = "photo-" + Path.GetFileNameWithoutExtension(file);
            if (filter is null || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(LoadMaster(file, name, maxDim));
            }
        }

        int w = Math.Min(maxDim, 1280), h = w * 5 / 8;
        var synthetic = new List<Func<EncodeImage>>
        {
            () => Screen(w, h, alpha: false),
            () => Screen(w, h, alpha: true),
            () => Palette(w, h),
        };
        if (masters.Length > 0)
        {
            synthetic.Insert(0, () => PhotoAlpha(LoadMaster(masters[0], "photo-alpha", maxDim)));
        }

        foreach (Func<EncodeImage> make in synthetic)
        {
            EncodeImage image = make();
            if (filter is null || image.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(image);
            }
        }

        if (list.Count == 0)
        {
            throw new InvalidOperationException($"No encode input matches '{filter}'.");
        }

        return list;
    }

    private static EncodeImage LoadMaster(string file, string name, int maxDim)
    {
        using Image<Rgba32> image = Image.Load<Rgba32>(file);
        if (maxDim > 0 && Math.Max(image.Width, image.Height) > maxDim)
        {
            double f = (double)maxDim / Math.Max(image.Width, image.Height);
            image.Mutate(x => x.Resize((int)Math.Round(image.Width * f), (int)Math.Round(image.Height * f), KnownResamplers.Box));
        }

        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        return new EncodeImage(name, rgba, image.Width, image.Height, HasAlpha: false, "photo");
    }

    private static EncodeImage PhotoAlpha(EncodeImage photo)
    {
        byte[] rgba = (byte[])photo.Rgba.Clone();
        int width = photo.Width, height = photo.Height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = (x - width / 2.0) / (width / 2.0), dy = (y - height / 2.0) / (height / 2.0);
                double a = Math.Clamp(1.35 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                if (x > width * 0.70 && x < width * 0.85 && y > height * 0.15 && y < height * 0.35)
                {
                    a = 0;
                }

                rgba[4 * (y * width + x) + 3] = (byte)Math.Round(a * 255);
            }
        }

        return photo with { Rgba = rgba, HasAlpha = true, Kind = "photo+alpha" };
    }

    // A deterministic per-pixel hash, so the same size always yields the same bytes.
    private static uint Hash(int x, int y, uint seed)
    {
        uint h = (uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ seed * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    private static EncodeImage Screen(int width, int height, bool alpha)
    {
        var rgba = new byte[width * height * 4];
        void Fill(int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a = 255)
        {
            x0 = Math.Clamp(x0, 0, width); x1 = Math.Clamp(x1, 0, width);
            y0 = Math.Clamp(y0, 0, height); y1 = Math.Clamp(y1, 0, height);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = 4 * (y * width + x);
                    rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = alpha ? a : (byte)255;
                }
            }
        }

        // Desktop: a flat background, fully transparent in the alpha variant.
        Fill(0, 0, width, height, 0x2B, 0x4A, 0x6F, 0);
        var random = new Random(42);
        for (int w = 0; w < 5; w++)
        {
            int ww = width * random.Next(30, 60) / 100, wh = height * random.Next(35, 70) / 100;
            int wx = random.Next(0, Math.Max(1, width - ww)), wy = random.Next(0, Math.Max(1, height - wh));
            for (int s = 1; s <= 8; s++)
            {
                byte a = (byte)(110 - s * 12);
                byte shade = alpha ? (byte)0 : (byte)(0x2B - s * 2);
                Fill(wx + s, wy + wh + s - 1, wx + ww + s, wy + wh + s, shade, shade, shade, a);
                Fill(wx + ww + s - 1, wy + s, wx + ww + s, wy + wh + s, shade, shade, shade, a);
            }

            Fill(wx, wy, wx + ww, wy + wh, 0xF4, 0xF4, 0xF4);
            byte tr = (byte)random.Next(40, 200), tg = (byte)random.Next(40, 200), tb = (byte)random.Next(40, 200);
            Fill(wx, wy, wx + ww, wy + 28, tr, tg, tb);
            Fill(wx, wy + 28, wx + 180, wy + wh, 0xE6, 0xE8, 0xEB);
            Fill(wx + 180, wy + 28, wx + 181, wy + wh, 0xC8, 0xC8, 0xC8);

            int tx = wx + ww - 220, ty = wy + 44;
            for (int y = Math.Max(0, ty); y < Math.Min(height, ty + 150); y++)
            {
                for (int x = Math.Max(0, tx); x < Math.Min(width, tx + 200); x++)
                {
                    int i = 4 * (y * width + x);
                    rgba[i] = (byte)(x - tx + 30);
                    rgba[i + 1] = (byte)(y - ty + 60);
                    rgba[i + 2] = (byte)(200 - (x - tx) / 2);
                    rgba[i + 3] = 255;
                }
            }

            for (int line = 0, ly = wy + 44; ly + 12 < wy + wh && line < 60; line++, ly += 18)
            {
                int lx = wx + 192, end = tx - 12 > lx ? tx - 12 : wx + ww - 12;
                if (line % 7 == 6)
                {
                    continue;
                }

                while (lx + 6 < end)
                {
                    int word = random.Next(2, 10);
                    for (int c = 0; c < word && lx + 6 < end; c++, lx += 7)
                    {
                        uint glyph = Hash(random.Next(64), 0, 3);
                        for (int gy = 0; gy < 9; gy++)
                        {
                            for (int gx = 0; gx < 6; gx++)
                            {
                                if (((glyph >> ((gy * 6 + gx) % 31)) & 1) == 0 || lx + gx >= width || ly + gy >= height)
                                {
                                    continue;
                                }

                                int i = 4 * ((ly + gy) * width + lx + gx);
                                byte ink = (gx == 0 || gx == 5) ? (byte)0x80 : (byte)0x22;
                                rgba[i] = ink; rgba[i + 1] = ink; rgba[i + 2] = ink; rgba[i + 3] = 255;
                            }
                        }
                    }

                    lx += 7;
                }
            }
        }

        return new EncodeImage(alpha ? "screen-alpha" : "screen", rgba, width, height, alpha, alpha ? "screen+alpha" : "screen");
    }

    private static EncodeImage Palette(int width, int height)
    {
        // A bar chart in 12 flat colours: background, grid, axes, eight series, labels.
        ReadOnlySpan<uint> colours =
        [
            0xFFFFFF, 0xE0E0E0, 0x404040, 0x1F77B4, 0xFF7F0E, 0x2CA02C, 0xD62728,
            0x9467BD, 0x8C564B, 0xE377C2, 0x7F7F7F, 0x17BECF,
        ];
        var rgba = new byte[width * height * 4];
        int bars = 24, left = width / 12, bottom = height * 9 / 10, barWidth = (width - 2 * left) / bars;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int c = 0;
                if (y % 40 == 0)
                {
                    c = 1;
                }

                if (x == left || y == bottom)
                {
                    c = 2;
                }
                else if (x > left && y < bottom)
                {
                    int bar = (x - left) / barWidth;
                    int top = bottom - (int)(Hash(bar, 0, 9) % (uint)(bottom - height / 10));
                    if (bar < bars && y >= top && (x - left) % barWidth >= 3)
                    {
                        c = 3 + bar % 8;
                    }
                }
                else if (y > bottom + 8 && y < bottom + 17 && x > left && ((Hash(x / 6, y, 5) & 3) == 0))
                {
                    c = 2;
                }

                int i = 4 * (y * width + x);
                rgba[i] = (byte)(colours[c] >> 16);
                rgba[i + 1] = (byte)(colours[c] >> 8);
                rgba[i + 2] = (byte)colours[c];
                rgba[i + 3] = 255;
            }
        }

        return new EncodeImage("palette", rgba, width, height, HasAlpha: false, "palette");
    }
}
