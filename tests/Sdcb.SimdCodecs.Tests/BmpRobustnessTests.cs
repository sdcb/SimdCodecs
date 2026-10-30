using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Shouldly;
using W = Sdcb.SimdCodecs.Tests.TestBmpWriter;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Untrusted bitmaps: every truncation of a seed corpus and seeded mutations of it (header fields, masks,
/// palettes, RLE and Huffman streams, icon images) either decode or fail with <see cref="InvalidDataException"/>
/// (false from the <c>Try</c> methods) — never another exception, a hang, or an unbounded allocation. The
/// mutation count scales with <c>BMP_FUZZ_ITERATIONS</c> (default 4000) and its seed with <c>BMP_FUZZ_SEED</c>.
/// </summary>
public sealed class BmpRobustnessTests
{
    private const long MaxPixels = 1L << 22;

    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("BMP_FUZZ_ITERATIONS"), out var n) ? n : 4000;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("BMP_FUZZ_SEED"), out var n) ? n : 20261030;

    internal static List<(string Name, byte[] Data, bool IconImage)> Seeds()
    {
        var seeds = new List<(string, byte[], bool)>();
        var random = new Random(1);
        var rgba = new byte[23 * 9 * 4];
        random.NextBytes(rgba);
        foreach (var format in Enum.GetValues<BmpEncodeFormat>())
        foreach (var rle in new[] { false, true })
        {
            if (rle && format is not (BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8)) continue;
            var options = new BmpEncodeOptions { Format = format, Rle = rle, Header = format == BmpEncodeFormat.Rgb565 ? BmpHeaderVersion.V5 : BmpHeaderVersion.Auto };
            seeds.Add(($"{format} RLE {rle}", BmpEncoder.Encode(rgba, 23, 9, ImagePixelFormat.Rgba32, options), false));
        }
        seeds.Add(("core 4-bit", BmpEncoder.Encode(rgba, 23, 9, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed4, Header = BmpHeaderVersion.Core }), false));
        seeds.Add(("V5 ICC", BmpEncoder.Encode(rgba, 23, 9, ImagePixelFormat.Rgba32, new BmpEncodeOptions { IccProfile = new byte[64] }), false));
        seeds.Add(("4444 alphabitfields", W.File(W.InfoHeader(40, 7, 3, 16, 6), W.Masks(0x0F00, 0x00F0, 0x000F, 0xF000),
            W.Rows(7, 3, 16, (x, y) => (uint)(x * 977 + y * 31)), null), false));
        seeds.Add(("RLE24", W.File(W.InfoHeader(64, 7, 3, 24, 4), [],
            new W.Rle(7, 3, 24, []).Run(4, 0x123456).Literal(0xFF0000, 0x00FF00, 0x0000FF).EndOfLine().Delta(1, 1).Run(2, 9).EndOfBitmap().Stream), false));
        seeds.Add(("RLE8 delta", W.File(W.InfoHeader(40, 9, 6, 8, 1, colorsUsed: 7), W.Palette(new int[7]),
            new W.Rle(9, 6, 8, new int[7]).Run(3, 1).Literal(2, 3, 4).Delta(2, 1).Run(2, 6).EndOfLine().Run(200, 2).EndOfBitmap().Stream), false));
        var bits = Enumerable.Range(0, 70 * 3).Select(i => (i / 7 + i / 70) & 1).ToArray();
        seeds.Add(("Huffman", W.File(W.InfoHeader(64, 70, 3, 1, 3), W.Palette([0xFFFFFF, 0]),
            BmpDecoderTests.HuffmanStream(bits, 70, 3, eols: true)), false));
        seeds.Add(("BI_PNG", W.File(W.InfoHeader(40, 5, 4, 0, 5), [], Png.PngWriter.Encode(new byte[5 * 4 * 4], 5, 4)), false));
        seeds.Add(("icon 32", BmpEncoder.EncodeDib(rgba, 23, 9, ImagePixelFormat.Rgba32, iconImage: true), true));
        seeds.Add(("icon 4", BmpEncoder.EncodeDib(rgba, 23, 9, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed4 }, iconImage: true), true));
        return seeds;
    }

    [Fact]
    public void Seeds_decode_cleanly()
    {
        foreach (var (name, data, icon) in Seeds())
        {
            if (icon) BmpDecoder.DecodeDib(data, iconImage: true).Width.ShouldBe(23, name);
            else BmpImageDecoder.TryDecode(data, out _).ShouldBeTrue(name);
        }
    }

    [Fact]
    public void Every_truncation_is_refused_or_decoded()
    {
        foreach (var (name, data, icon) in Seeds())
        {
            for (var length = 0; length < data.Length; length++)
                DecodeOne(data[..length], icon, $"{name} cut to {length}");
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
                var (name, seed, icon) = seeds[random.Next(seeds.Count)];
                var data = Mutate(seed, random, out var description);
                current = $"#{i} {name}: {description}";
                DecodeOne(data, icon, current);
            }
        }, TestContext.Current.CancellationToken);
        if (await Task.WhenAny(run, Task.Delay(budget, TestContext.Current.CancellationToken)) != run)
            throw new ShouldAssertException($"The fuzz loop is stuck at {current}");
        await run;
    }

    // Headers and palettes hold most of the meaning, so half the mutations land in the first 160 bytes.
    private static byte[] Mutate(byte[] seed, Random random, out string description)
    {
        var data = seed.ToArray();
        var span = random.Next(2) == 0 ? Math.Min(160, data.Length) : data.Length;
        var at = random.Next(Math.Max(1, span));
        switch (random.Next(8))
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
                // A 32-bit field set to an extreme: sizes, offsets, masks, counts.
                if (at + 4 <= data.Length)
                {
                    var value = random.Next(4) switch { 0 => 0u, 1 => uint.MaxValue, 2 => 0x80000000u, _ => (uint)random.Next() };
                    BitConverter.GetBytes(value).CopyTo(data, at & ~3);
                }
                description = $"word at {at & ~3}";
                break;
            case 5:
                description = $"cut to {at}";
                return data[..at];
            case 6:
                description = $"{at} bytes inserted";
                return [.. data[..at], .. new byte[random.Next(1, 64)], .. data[at..]];
            default:
                var length = Math.Min(random.Next(1, 32), data.Length - at);
                description = $"{length} bytes deleted at {at}";
                return [.. data[..at], .. data[(at + length)..]];
        }
        return data;
    }

    internal static void DecodeOne(byte[] data, bool iconImage, string context)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var info = iconImage ? BmpDecoder.ReadDibInfo(data, true) : BmpDecoder.ReadInfo(data);
            if ((long)info.Width * info.Height > MaxPixels) return;
            if (iconImage)
            {
                BmpDecoder.DecodeDib(data, true);
            }
            else
            {
                BmpImageDecoder.TryDecode(data, out _);
                var rgba = new byte[info.Width * info.Height * 4];
                BmpImageDecoder.TryDecodeInto(data, rgba, 0, ImagePixelFormat.Rgba32);
                var gray = new byte[info.Width * info.Height];
                BmpImageDecoder.TryDecodeInto(data, gray, 0, ImagePixelFormat.Gray8);
            }
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{context}: {e.GetType().Name}: {e.Message}", e);
        }
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), context);
    }

    [Fact]
    public void DecodeInto_allocates_nothing_for_the_common_layouts()
    {
        var random = new Random(2);
        var rgba = new byte[640 * 48 * 4];
        random.NextBytes(rgba);
        foreach (var format in new[] { BmpEncodeFormat.Indexed1, BmpEncodeFormat.Indexed4, BmpEncodeFormat.Indexed8, BmpEncodeFormat.Rgb565,
                     BmpEncodeFormat.Rgb555, BmpEncodeFormat.Rgb24, BmpEncodeFormat.Rgb32, BmpEncodeFormat.Argb32 })
        foreach (var rle in new[] { false, true })
        {
            if (rle && format is not (BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8)) continue;
            var bmp = BmpEncoder.Encode(rgba, 640, 48, ImagePixelFormat.Rgba32, new BmpEncodeOptions { Format = format, Rle = rle });
            foreach (var target in Enum.GetValues<ImagePixelFormat>())
            {
                var destination = new byte[640 * 48 * 4];
                for (var i = 0; i < 3; i++) BmpImageDecoder.TryDecodeInto(bmp, destination, 0, target).ShouldBeTrue();
                var best = long.MaxValue;
                // Best of several: a pooled row can miss the pool under parallel test load.
                for (var attempt = 0; attempt < 8 && best > 0; attempt++)
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    BmpImageDecoder.TryDecodeInto(bmp, destination, 0, target);
                    best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
                }
                best.ShouldBe(0, $"{format}, RLE {rle} into {target}");
            }
        }
    }
}
