using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>
/// The two source images every case is derived from, as 16-bit RGBA so each bit depth can be cut
/// from one picture: <c>photo</c> (natural content: noise, texture, smooth gradients — PNG's worst
/// case) and <c>screen</c> (flat UI panels, text-like glyphs, a thumbnail — PNG's home ground).
/// Both are deterministic. The photo is a centre crop of a JPEG benchmark corpus master when that
/// cache exists (CC0 / Public Domain, see <c>Sdcb.SimdCodecs.Jpeg.Benchmarks/corpus/manifest.tsv</c>), else
/// a synthetic stand-in; the report states which.
/// </summary>
internal sealed class SourceImage
{
    public const string Photo = "photo";
    public const string Screen = "screen";
    public static readonly string[] Contents = [Photo, Screen];

    private static readonly Dictionary<(string, int, int), SourceImage> Cache = [];

    public required string Content { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Where the pixels came from (a corpus master's path, or "synthetic").</summary>
    public required string Origin { get; init; }

    /// <summary>RGBA, 16 bits per sample, alpha = 65535 everywhere.</summary>
    public required ushort[] Opaque { get; init; }

    /// <summary>The same RGB with a content-appropriate alpha channel.</summary>
    public required ushort[] Translucent { get; init; }

    public static SourceImage Get(string content, int width, int height)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((content, width, height), out SourceImage? image))
            {
                image = content switch
                {
                    Photo => CreatePhoto(width, height),
                    Screen => CreateScreen(width, height),
                    _ => throw new ArgumentOutOfRangeException(nameof(content), content, "photo or screen"),
                };
                Cache[(content, width, height)] = image;
            }

            return image;
        }
    }

    public ushort[] Rgba16(bool alpha) => alpha ? Translucent : Opaque;

    public byte[] Rgba8(bool alpha) => HighBytes(Rgba16(alpha), [0, 1, 2, 3]);

    public byte[] Rgb8() => HighBytes(Opaque, [0, 1, 2]);

    public ushort[] Rgb16() => Pick(Opaque, [0, 1, 2]);

    public byte[] Gray8()
    {
        byte[] rgb = Rgb8();
        var gray = new byte[Width * Height];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 3)
        {
            gray[i] = (byte)((77 * rgb[j] + 150 * rgb[j + 1] + 29 * rgb[j + 2] + 128) >> 8);
        }

        return gray;
    }

    public ushort[] Gray16()
    {
        var gray = new ushort[Width * Height];
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        {
            gray[i] = (ushort)((19595L * Opaque[j] + 38470L * Opaque[j + 1] + 7471L * Opaque[j + 2] + 32768) >> 16);
        }

        return gray;
    }

    /// <summary>Grey + alpha, 8 bits per sample.</summary>
    public byte[] GrayAlpha8()
    {
        byte[] gray = Gray8();
        var ga = new byte[gray.Length * 2];
        for (int i = 0; i < gray.Length; i++)
        {
            ga[2 * i] = gray[i];
            ga[2 * i + 1] = (byte)(Translucent[4 * i + 3] >> 8);
        }

        return ga;
    }

    /// <summary>Grey + alpha, 16 bits per sample.</summary>
    public ushort[] GrayAlpha16()
    {
        ushort[] gray = Gray16();
        var ga = new ushort[gray.Length * 2];
        for (int i = 0; i < gray.Length; i++)
        {
            ga[2 * i] = gray[i];
            ga[2 * i + 1] = Translucent[4 * i + 3];
        }

        return ga;
    }

    private byte[] HighBytes(ushort[] rgba, int[] channels)
    {
        var result = new byte[Width * Height * channels.Length];
        for (int p = 0, o = 0; p < rgba.Length; p += 4)
        {
            foreach (int c in channels)
            {
                result[o++] = (byte)(rgba[p + c] >> 8);
            }
        }

        return result;
    }

    private ushort[] Pick(ushort[] rgba, int[] channels)
    {
        var result = new ushort[Width * Height * channels.Length];
        for (int p = 0, o = 0; p < rgba.Length; p += 4)
        {
            foreach (int c in channels)
            {
                result[o++] = rgba[p + c];
            }
        }

        return result;
    }

    // A deterministic per-pixel hash: the noise must not depend on iteration order or Random's
    // implementation, so the same size always yields the same bytes.
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

    /// <summary>The JPEG benchmark's corpus masters (CC0 / PD photographs, losslessly downscaled).</summary>
    private static string? FindPhotoMaster()
    {
        if (Environment.GetEnvironmentVariable("PNG_BENCH_PHOTO") is { Length: > 0 } configured)
        {
            return configured;
        }

        string master = Path.Combine(BenchPaths.Root, "..", "Sdcb.SimdCodecs.Jpeg.Benchmarks", ".cache", "corpus", "masters",
            "landscape-almsee.png");
        return File.Exists(master) ? Path.GetFullPath(master) : null;
    }

    private static SourceImage CreatePhoto(int width, int height)
    {
        var opaque = new ushort[width * height * 4];
        string origin;
        if (FindPhotoMaster() is { } path)
        {
            using Image<Rgb24> master = Image.Load<Rgb24>(path);
            // Cover the target, then centre-crop: no resampling when the master is large enough.
            if (master.Width < width || master.Height < height)
            {
                double scale = Math.Max((double)width / master.Width, (double)height / master.Height);
                master.Mutate(c => c.Resize((int)Math.Ceiling(master.Width * scale), (int)Math.Ceiling(master.Height * scale)));
            }

            master.Mutate(c => c.Crop(new Rectangle((master.Width - width) / 2, (master.Height - height) / 2, width, height)));
            var rgb = new Rgb24[width * height];
            master.CopyPixelDataTo(rgb);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    uint noise = Hash(x, y, 1);
                    // 16-bit samples: the 8-bit value in the high byte, sensor-like noise in the low byte,
                    // so the 8-bit cuts are exactly the master's pixels.
                    opaque[4 * i] = (ushort)((uint)rgb[i].R << 8 | (noise & 0xFF));
                    opaque[4 * i + 1] = (ushort)((uint)rgb[i].G << 8 | (noise >> 8 & 0xFF));
                    opaque[4 * i + 2] = (ushort)((uint)rgb[i].B << 8 | (noise >> 16 & 0xFF));
                    opaque[4 * i + 3] = ushort.MaxValue;
                }
            }

            origin = $"centre crop of {Path.GetFileName(path)} (JPEG benchmark corpus master)";
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    double fx = (double)x / width, fy = (double)y / height;
                    // Smooth sky-to-ground gradient, low-frequency "terrain" and per-pixel noise.
                    double r = 0.35 + 0.25 * fy + 0.15 * Math.Sin(x * 0.011 + Math.Sin(y * 0.017) * 2);
                    double g = 0.45 + 0.20 * Math.Cos(fx * 5 + fy * 3) + 0.10 * Math.Sin(x * 0.043 * fy);
                    double b = 0.75 - 0.45 * fy + 0.08 * Math.Sin((x + y) * 0.021);
                    uint noise = Hash(x, y, 2);
                    double n = ((noise & 0xFFF) / 4095.0 - 0.5) * 0.06;
                    opaque[4 * i] = ToU16(r + n);
                    opaque[4 * i + 1] = ToU16(g + n * 0.8);
                    opaque[4 * i + 2] = ToU16(b - n * 0.6);
                    opaque[4 * i + 3] = ushort.MaxValue;
                }
            }

            origin = "synthetic (no JPEG corpus master found; run the JPEG benchmark's scripts/fetch-corpus.sh for a real photo)";
        }

        var translucent = (ushort[])opaque.Clone();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // A soft vignette into full transparency, plus a hard-edged transparent cut-out.
                double dx = (x - width / 2.0) / (width / 2.0), dy = (y - height / 2.0) / (height / 2.0);
                double a = Math.Clamp(1.35 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
                if (x > width * 0.70 && x < width * 0.85 && y > height * 0.15 && y < height * 0.35)
                {
                    a = 0;
                }

                translucent[4 * (y * width + x) + 3] = ToU16(a);
            }
        }

        return new SourceImage { Content = Photo, Width = width, Height = height, Origin = origin, Opaque = opaque, Translucent = translucent };
    }

    private static SourceImage CreateScreen(int width, int height)
    {
        // Paint in 8-bit RGBA, then widen: screen content has no information below 8 bits.
        var rgba = new byte[width * height * 4];
        var alpha = new byte[width * height];
        void Fill(int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a = 255)
        {
            x0 = Math.Clamp(x0, 0, width); x1 = Math.Clamp(x1, 0, width);
            y0 = Math.Clamp(y0, 0, height); y1 = Math.Clamp(y1, 0, height);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = y * width + x;
                    rgba[4 * i] = r; rgba[4 * i + 1] = g; rgba[4 * i + 2] = b;
                    alpha[i] = a;
                }
            }
        }

        // Desktop: a flat background, transparent in the alpha variant.
        Fill(0, 0, width, height, 0x2B, 0x4A, 0x6F, 0);
        var random = new Random(42);
        int windows = 5;
        for (int w = 0; w < windows; w++)
        {
            int ww = width * random.Next(30, 60) / 100, wh = height * random.Next(35, 70) / 100;
            int wx = random.Next(0, Math.Max(1, width - ww)), wy = random.Next(0, Math.Max(1, height - wh));

            // Drop shadow: a translucent band below and to the right.
            for (int s = 1; s <= 8; s++)
            {
                byte a = (byte)(110 - s * 12);
                Fill(wx + s, wy + wh + s - 1, wx + ww + s, wy + wh + s, 0, 0, 0, a);
                Fill(wx + ww + s - 1, wy + s, wx + ww + s, wy + wh + s, 0, 0, 0, a);
            }

            Fill(wx, wy, wx + ww, wy + wh, 0xF4, 0xF4, 0xF4);                            // client area
            byte tr = (byte)random.Next(40, 200), tg = (byte)random.Next(40, 200), tb = (byte)random.Next(40, 200);
            Fill(wx, wy, wx + ww, wy + 28, tr, tg, tb);                                    // title bar
            Fill(wx, wy + 28, wx + 180, wy + wh, 0xE6, 0xE8, 0xEB);                        // sidebar
            Fill(wx + 180, wy + 28, wx + 181, wy + wh, 0xC8, 0xC8, 0xC8);                  // divider

            // A thumbnail with a smooth gradient: the one non-flat region.
            int tx = wx + ww - 220, ty = wy + 44;
            for (int y = Math.Max(0, ty); y < Math.Min(height, ty + 150); y++)
            {
                for (int x = Math.Max(0, tx); x < Math.Min(width, tx + 200); x++)
                {
                    int i = y * width + x;
                    rgba[4 * i] = (byte)(x - tx + 30);
                    rgba[4 * i + 1] = (byte)(y - ty + 60);
                    rgba[4 * i + 2] = (byte)(200 - (x - tx) / 2);
                    alpha[i] = 255;
                }
            }

            // Lines of text: 6×9 glyphs from a fixed set of 64 bitmaps, anti-aliased edges in grey.
            for (int line = 0, ly = wy + 44; ly + 12 < wy + wh && line < 60; line++, ly += 18)
            {
                int lx = wx + 192, end = tx - 12 > lx ? tx - 12 : wx + ww - 12;
                if (line % 7 == 6)
                {
                    continue; // paragraph break
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
                                int bit = (int)(glyph >> ((gy * 6 + gx) % 31)) & 1;
                                if (bit == 0 || lx + gx >= width || ly + gy >= height)
                                {
                                    continue;
                                }

                                int i = (ly + gy) * width + lx + gx;
                                byte ink = (gx == 0 || gx == 5) ? (byte)0x80 : (byte)0x22;
                                rgba[4 * i] = ink; rgba[4 * i + 1] = ink; rgba[4 * i + 2] = ink;
                            }
                        }
                    }

                    lx += 7;
                }
            }
        }

        var opaque = new ushort[width * height * 4];
        var translucent = new ushort[opaque.Length];
        for (int i = 0; i < width * height; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                opaque[4 * i + c] = translucent[4 * i + c] = (ushort)(rgba[4 * i + c] * 257);
            }

            opaque[4 * i + 3] = ushort.MaxValue;
            translucent[4 * i + 3] = (ushort)(alpha[i] * 257);
        }

        return new SourceImage { Content = Screen, Width = width, Height = height, Origin = "synthetic", Opaque = opaque, Translucent = translucent };
    }

    private static ushort ToU16(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
}
