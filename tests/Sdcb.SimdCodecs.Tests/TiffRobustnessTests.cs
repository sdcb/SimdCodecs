using System.Diagnostics;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using B = Sdcb.SimdCodecs.Tests.TestTiffBuilder;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Untrusted TIFFs: every truncation of a seed corpus and seeded mutations of it (header, directory entries, tag
/// arrays, coded segments) either decode or fail with <see cref="InvalidDataException"/> or
/// <see cref="NotSupportedException"/> from <see cref="TiffReader"/>, and with false from the facade adapter — never
/// another exception (<see cref="OverflowException"/>, <see cref="IndexOutOfRangeException"/>,
/// <see cref="ArgumentOutOfRangeException"/>, ...), a hang, or an unbounded allocation. The seeds are libtiff-written
/// (Magick.NET) and hand-assembled files of every compression and layout the reader handles. The mutation count
/// scales with <c>TIFF_FUZZ_ITERATIONS</c> (default 4000) and its seed with <c>TIFF_FUZZ_SEED</c>.
/// </summary>
public sealed class TiffRobustnessTests
{
    /// <summary>Pages larger than this are not decoded by the harness: the bound is the test's memory, not the reader's.</summary>
    private const long MaxRasterBytes = 1L << 24;

    private const int W = 37;
    private const int H = 23;

    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("TIFF_FUZZ_ITERATIONS"), out var n) ? n : 4000;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("TIFF_FUZZ_SEED"), out var n) ? n : 20261105;

    private static readonly Lazy<List<(string Name, byte[] Data)>> SeedCache = new(BuildSeeds);

    internal static List<(string Name, byte[] Data)> Seeds() => SeedCache.Value;

    private static List<(string Name, byte[] Data)> BuildSeeds()
    {
        var seeds = new List<(string, byte[])>();
        var random = new Random(3);
        var rgb = new byte[W * H * 3];
        random.NextBytes(rgb);
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)((rgb[i] & 0x0F) + (i / 9 % 200)); // compressible, not flat

        foreach (var compression in new[] { CompressionMethod.NoCompression, CompressionMethod.LZW, CompressionMethod.Zip, CompressionMethod.RLE })
        foreach (var tiled in new[] { false, true })
        foreach (var planar in new[] { false, true })
            seeds.Add(($"{compression} tiled {tiled} planar {planar}", Magick(rgb, "RGB", 8, compression, tiled, planar, predictor: compression is CompressionMethod.LZW ? 2 : 1)));

        seeds.Add(("16-bit zip predictor 2 MM", Magick(rgb, "RGB", 16, CompressionMethod.Zip, false, false, predictor: 2, endian: "msb")));
        seeds.Add(("float zip predictor 3 tiled", Magick(rgb, "RGB", 32, CompressionMethod.Zip, true, false, predictor: 3, floating: true)));
        seeds.Add(("gray lzw", Magick(rgb[..(W * H)], "I", 8, CompressionMethod.LZW, false, false, predictor: 2)));
        foreach (var fax in new[] { CompressionMethod.Group4, CompressionMethod.Fax })
            seeds.Add(($"bilevel {fax}", Bilevel(rgb, fax)));
        seeds.Add(("multi-page", MultiPage(rgb)));
        // Photometrics the facade converts: palette (libtiff), CMYK (libtiff), subsampled YCbCr (hand-built units).
        using (var palette = new MagickImage())
        {
            palette.ReadPixels(rgb, new PixelReadSettings(W, H, StorageType.Char, "RGB"));
            palette.ColorType = ColorType.Palette;
            palette.Settings.Compression = CompressionMethod.LZW;
            seeds.Add(("palette lzw", palette.ToByteArray(MagickFormat.Tiff)));
        }
        using (var cmyk = new MagickImage())
        {
            cmyk.ReadPixels(Noise4(random), new PixelReadSettings(W, H, StorageType.Char, "CMYK"));
            cmyk.Settings.Compression = CompressionMethod.Zip;
            seeds.Add(("cmyk zip", cmyk.ToByteArray(MagickFormat.Tiff)));
        }
        var ycc = B.Basic(W, H, 3, 8, TiffCompression.Uncompressed, TiffPhotometric.YCbCr, rowsPerStrip: 6);
        var unitStrips = new List<byte[]>();
        for (var y = 0; y < H; y += 6)
            unitStrips.Add(rgb.AsSpan(0, ((W + 1) / 2) * ((Math.Min(6, H - y) + 1) / 2) * 6).ToArray());
        seeds.Add(("ycbcr 2x2", new B().AddPage(ycc, [.. unitStrips]).Build()));
        seeds.Add(("BigTIFF lzw tiled", Magick(rgb, "RGB", 8, CompressionMethod.LZW, true, false, predictor: 2, bigTiff: true)));
        seeds.Add(("BigTIFF zip planar MM", Magick(rgb, "RGB", 8, CompressionMethod.Zip, false, true, predictor: 2, endian: "msb", bigTiff: true)));

        // Hand-assembled: an uncompressed RGBA page with unassociated alpha, and a big-endian 16-bit grey one.
        var rgba = new byte[W * H * 4];
        random.NextBytes(rgba);
        var tags = B.Basic(W, H, 4, 8, TiffCompression.Uncompressed, TiffPhotometric.Rgb, rowsPerStrip: 5);
        tags.Add(B.Short(TiffTag.ExtraSamples, 2));
        seeds.Add(("hand RGBA", new B().AddPage(tags, Strips(rgba, W * 4, H, 5)).Build()));
        var gray16 = new byte[W * H * 2];
        random.NextBytes(gray16);
        seeds.Add(("hand MM gray16", new B(littleEndian: false).AddPage(
            B.Basic(W, H, 1, 16, TiffCompression.Uncompressed, TiffPhotometric.MinIsBlack, rowsPerStrip: 4),
            Strips(gray16, W * 2, H, 4)).Build()));
        return seeds;
    }

    private static byte[] Noise4(Random random)
    {
        var bytes = new byte[W * H * 4];
        random.NextBytes(bytes);
        return bytes;
    }

    internal static byte[][] Strips(byte[] raster, int rowBytes, int height, int rowsPerStrip)
    {
        var strips = new List<byte[]>();
        for (var y = 0; y < height; y += rowsPerStrip)
            strips.Add(raster.AsSpan(y * rowBytes, Math.Min(rowsPerStrip, height - y) * rowBytes).ToArray());
        return [.. strips];
    }

    private static byte[] Magick(byte[] pixels, string mapping, int depth, CompressionMethod compression, bool tiled,
        bool planar, int predictor, string? endian = null, bool floating = false, bool bigTiff = false)
    {
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings(W, H, StorageType.Char, mapping));
        if (mapping == "I") image.ColorSpace = ColorSpace.Gray;
        image.Depth = (uint)depth;
        image.Settings.Interlace = planar ? Interlace.Plane : Interlace.NoInterlace;
        image.Settings.Compression = compression;
        image.Settings.SetDefine(MagickFormat.Tiff, "predictor", predictor.ToString());
        if (tiled) image.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", "16x16");
        else image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "5");
        if (endian is not null) image.Settings.SetDefine(MagickFormat.Tiff, "endian", endian);
        if (floating) image.Settings.SetDefine("quantum:format", "floating-point");
        return image.ToByteArray(bigTiff ? MagickFormat.Tiff64 : MagickFormat.Tiff);
    }

    private static byte[] Bilevel(byte[] noise, CompressionMethod method)
    {
        var grey = new byte[W * H];
        for (var i = 0; i < grey.Length; i++) grey[i] = noise[i] < 100 ? (byte)0 : (byte)255;
        using var image = new MagickImage(grey, new MagickReadSettings { Format = MagickFormat.Gray, Width = W, Height = H, Depth = 8 });
        image.ColorType = ColorType.Bilevel;
        image.Depth = 1;
        image.Settings.Compression = method;
        image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "8");
        return image.ToByteArray(MagickFormat.Tiff);
    }

    private static byte[] MultiPage(byte[] rgb)
    {
        using var collection = new MagickImageCollection();
        for (var p = 0; p < 3; p++)
        {
            var image = new MagickImage();
            image.ReadPixels(rgb, new PixelReadSettings(W, H, StorageType.Char, "RGB"));
            image.Settings.Compression = p == 1 ? CompressionMethod.Zip : CompressionMethod.LZW;
            collection.Add(image);
        }
        using var ms = new MemoryStream();
        collection.Write(ms, MagickFormat.Tiff);
        return ms.ToArray();
    }

    [Fact]
    public void Seeds_decode_cleanly()
    {
        foreach (var (name, data) in Seeds())
        {
            TiffReader.Read(data).Pages.Count.ShouldBeGreaterThan(0, name);
            TiffImageDecoder.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue(name);
            image!.Width.ShouldBe(W, name);
        }
    }

    [Fact]
    public void Every_truncation_is_refused_or_decoded()
    {
        foreach (var (name, data) in Seeds())
        {
            // Every cut through the header and the directories, a sparser sweep through the segments.
            for (var length = 0; length < data.Length; length += length < 64 || length > data.Length - 1024 ? 1 : 7)
                DecodeOne(data[..length], $"{name} cut to {length}");
        }
    }

    [Fact]
    public async Task Mutations_decode_or_throw_invalid_data()
    {
        var seeds = Seeds();
        var iterations = Iterations;
        var budget = TimeSpan.FromSeconds(Math.Max(60, iterations / 20));
        var current = "";
        var run = Task.Run(() =>
        {
            var random = new Random(Seed);
            for (var i = 0; i < iterations; i++)
            {
                var (name, seed) = seeds[random.Next(seeds.Count)];
                var data = Mutate(seed, random, out var description);
                current = $"#{i} {name}: {description}";
                DecodeOne(data, current);
            }
        }, TestContext.Current.CancellationToken);
        if (await Task.WhenAny(run, Task.Delay(budget, TestContext.Current.CancellationToken)) != run)
            throw new ShouldAssertException($"The fuzz loop is stuck at {current}");
        await run;
    }

    /// <summary>
    /// The directories hold the meaning, and libtiff writes them after the image data, so a third of the mutations
    /// land in the header, a third in the last 512 bytes (directories and tag arrays) and a third anywhere.
    /// </summary>
    private static byte[] Mutate(byte[] seed, Random random, out string description)
    {
        var data = seed.ToArray();
        var at = random.Next(3) switch
        {
            0 => random.Next(Math.Min(16, data.Length)),
            1 => Math.Max(0, data.Length - 1 - random.Next(Math.Min(512, data.Length))),
            _ => random.Next(data.Length),
        };
        switch (random.Next(9))
        {
            case 0:
                data[at] ^= (byte)(1 << random.Next(8));
                description = $"bit flip at {at}";
                break;
            case 1:
                data[at] = (byte)random.Next(256);
                description = $"byte {at} = {data[at]}";
                break;
            case 2:
                data[at] = random.Next(2) == 0 ? (byte)0 : (byte)0xFF;
                description = $"byte {at} saturated";
                break;
            case 3:
            {
                var n = random.Next(2, 9);
                for (var k = 0; k < n; k++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                description = $"{n} flips";
                break;
            }
            case 4:
            case 5:
                // A 16- or 32-bit field set to an extreme: counts, offsets, sizes, types.
                if (at + 4 <= data.Length)
                {
                    var value = random.Next(6) switch
                    {
                        0 => 0u, 1 => uint.MaxValue, 2 => 0x80000000u, 3 => 0x7FFFFFFFu, 4 => (uint)random.Next(1, 20),
                        _ => (uint)random.Next(),
                    };
                    var bytes = BitConverter.GetBytes(value);
                    var start = at & ~1;
                    var width = random.Next(2) == 0 ? 2 : 4;
                    if (start + width <= data.Length) bytes.AsSpan(0, width).CopyTo(data.AsSpan(start));
                }
                description = $"field at {at & ~1}";
                break;
            case 6:
                description = $"cut to {at}";
                return data[..at];
            case 7:
                description = $"{at} bytes inserted";
                return [.. data[..at], .. new byte[random.Next(1, 64)], .. data[at..]];
            default:
                var length = Math.Min(random.Next(1, 32), data.Length - at);
                description = $"{length} bytes deleted at {at}";
                return [.. data[..at], .. data[(at + length)..]];
        }
        return data;
    }

    internal static void DecodeOne(byte[] data, string context)
    {
        var timer = Stopwatch.StartNew();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            // The harness's memory bound: every page is sized from its directory before anything is decoded.
            int pages;
            try { pages = TiffReader.CountPages(data); }
            catch (InvalidDataException) { pages = 0; }
            var tooLarge = false;
            for (var p = 0; p < pages; p++)
            {
                try
                {
                    var layout = TiffReader.ReadLayout(data, p);
                    if ((long)layout.Width * layout.Height * layout.BytesPerPixel > MaxRasterBytes) tooLarge = true;
                    if (p == 0 && !tooLarge)
                    {
                        var buffer = new byte[Math.Max(1, layout.SegmentBytes(0))];
                        for (var s = 0; s < layout.SegmentCount; s++)
                        {
                            var need = layout.SegmentBytes(s);
                            if (need > buffer.Length) buffer = new byte[need];
                            Expect(() => TiffReader.ReadSegment(data, layout, s, buffer));
                        }
                    }
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                {
                }
            }
            if (tooLarge) return;

            Expect(() => TiffReader.Read(data));
            TiffImageDecoder.TryReadInfo(data, out var info);
            TiffImageDecoder.TryReadMetadata(data, out _);
            if (TiffImageDecoder.TryDecode(data, out var image, cancellationToken: ct))
            {
                image.Dispose();
                var rgba = new byte[info.Width * info.Height * 4];
                TiffImageDecoder.TryDecodeInto(data, rgba, 0, PixelFormat.Rgba32, cancellationToken: ct);
                if (info.Width > 2 && info.Height > 2)
                {
                    var region = new DecodeOptions { Region = new PixelRect(1, 1, info.Width - 2, info.Height - 2) };
                    TiffImageDecoder.TryDecode(data, out var cropped, region, ct);
                    cropped?.Dispose();
                }
            }
            if (TiffImageDecoder.TryOpenFrames(data, out var frames))
            {
                using (frames)
                {
                    for (var f = 0; f < frames.FrameCount; f++)
                    {
                        frames.TryDecodeFrame(f, out var frame, cancellationToken: ct);
                        frame?.Dispose();
                    }
                }
            }
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{context}: {e.GetType().Name}: {e.Message}", e);
        }
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), context);
    }

    private static void Expect(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
        }
    }
}
