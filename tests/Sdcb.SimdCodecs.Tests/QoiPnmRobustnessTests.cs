using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Hostile input for QOI and Netpbm: every truncation of a set of seed files, and random mutations of them,
/// either decode or fail with <see cref="InvalidDataException"/> (false from the <c>Try</c> methods), within
/// seconds. <c>QOI_FUZZ_ITERATIONS</c> / <c>PNM_FUZZ_ITERATIONS</c> (default 4000) and <c>…_FUZZ_SEED</c> set the
/// soak. Also: decoding into a caller's buffer allocates nothing.
/// </summary>
public sealed class QoiPnmRobustnessTests
{
    private const long MaxPixels = 1L << 22;

    private static int Iterations(string codec) =>
        int.TryParse(Environment.GetEnvironmentVariable($"{codec}_FUZZ_ITERATIONS"), out var n) ? n : 4000;

    private static int Seed(string codec) =>
        int.TryParse(Environment.GetEnvironmentVariable($"{codec}_FUZZ_SEED"), out var n) ? n : 20261030;

    private static List<(string Name, byte[] Data)> Seeds(string codec)
    {
        var rgba = QoiTests.Pattern(23, 9, alpha: true, seed: 4);
        if (codec == "QOI")
        {
            return
            [
                ("rgba", QoiEncoder.Encode(rgba, 23, 9, ImagePixelFormat.Rgba32)),
                ("rgb", QoiEncoder.Encode([.. rgba.Where((_, i) => i % 4 != 3)], 23, 9, ImagePixelFormat.Rgb24)),
                ("hand", QoiTests.HandStream().Stream),
            ];
        }

        var seeds = new List<(string, byte[])>();
        foreach (var format in new[] { PnmFormat.PlainBitmap, PnmFormat.PlainGraymap, PnmFormat.PlainPixmap, PnmFormat.Bitmap, PnmFormat.Graymap, PnmFormat.Pixmap, PnmFormat.Arbitrary })
        foreach (var max in new[] { 0, 7, 1000 })
            seeds.Add(($"{format} max {max}", PnmEncoder.Encode(rgba, 23, 9, ImagePixelFormat.Rgba32, new PnmEncodeOptions { Format = format, MaxValue = max })));
        seeds.Add(("pfm", PnmEncoder.EncodePfm([.. rgba.Take(23 * 9 * 3).Select(v => v / 7f)], 23, 9, 3)));
        seeds.Add(("pam comments", "P7\n# c\nWIDTH 2\nHEIGHT 1\nDEPTH 2\nMAXVAL 255\nTUPLTYPE GRAYSCALE_ALPHA\nENDHDR\n\u0001\u0002\u0003\u0004"u8.ToArray()));
        return seeds;
    }

    [Theory]
    [InlineData("QOI")]
    [InlineData("PNM")]
    public void Every_truncation_is_refused_or_decoded(string codec)
    {
        foreach (var (name, data) in Seeds(codec))
        {
            for (var length = 0; length < data.Length; length++)
                DecodeOne(codec, data[..length], $"{name} cut to {length}");
        }
    }

    [Theory]
    [InlineData("QOI")]
    [InlineData("PNM")]
    public async Task Mutations_decode_or_throw_invalid_data(string codec)
    {
        var seeds = Seeds(codec);
        var iterations = Iterations(codec);
        var budget = TimeSpan.FromSeconds(Math.Max(60, iterations / 20));
        var current = "";
        var run = Task.Run(() =>
        {
            var random = new Random(Seed(codec));
            for (var i = 0; i < iterations; i++)
            {
                var (name, seed) = seeds[random.Next(seeds.Count)];
                var data = Mutate(seed, random, out var description);
                current = $"#{i} {name}: {description}";
                DecodeOne(codec, data, current);
            }
        }, TestContext.Current.CancellationToken);
        if (await Task.WhenAny(run, Task.Delay(budget, TestContext.Current.CancellationToken)) != run)
            throw new ShouldAssertException($"The fuzz loop is stuck at {current}");
        await run;
    }

    // The header holds most of the meaning, so half the mutations land in the first 64 bytes; for Netpbm a
    // mutation also swaps in header characters (digits, whitespace, '#').
    private static byte[] Mutate(byte[] seed, Random random, out string description)
    {
        var data = seed.ToArray();
        var span = random.Next(2) == 0 ? Math.Min(64, data.Length) : data.Length;
        var at = random.Next(Math.Max(1, span));
        if (data.Length == 0)
        {
            description = "empty";
            return data;
        }
        switch (random.Next(7))
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
                data[at] = "0123456789 \n#9"u8[random.Next(14)];
                description = $"header character at {at}";
                break;
            case 3:
            {
                var n = random.Next(2, 9);
                for (var k = 0; k < n; k++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                description = $"{n} flips";
                break;
            }
            case 4:
                description = $"cut to {at}";
                return data[..at];
            case 5:
                description = $"bytes inserted at {at}";
                return [.. data[..at], .. "99999 "u8, .. data[at..]];
            default:
                var length = Math.Min(random.Next(1, 16), data.Length - at);
                description = $"{length} bytes deleted at {at}";
                return [.. data[..at], .. data[(at + length)..]];
        }
        return data;
    }

    private static void DecodeOne(string codec, byte[] data, string context)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            if (codec == "QOI")
            {
                var info = QoiDecoder.ReadInfo(data);
                if ((long)info.Width * info.Height > MaxPixels) return;
                QoiImageDecoder.TryDecode(data, out _);
                var rgba = new byte[info.Width * info.Height * 4];
                QoiImageDecoder.TryDecodeInto(data, rgba, 0, ImagePixelFormat.Rgba32);
                var gray = new byte[info.Width * info.Height];
                QoiImageDecoder.TryDecodeInto(data, gray, 0, ImagePixelFormat.Gray8);
            }
            else
            {
                var info = PnmDecoder.ReadInfo(data);
                if ((long)info.Width * info.Height > MaxPixels) return;
                PnmImageDecoder.TryDecode(data, out _);
                PfmImageDecoder.TryDecode(data, out _);
                var rgba = new byte[info.Width * info.Height * 4];
                PnmImageDecoder.TryDecodeInto(data, rgba, 0, ImagePixelFormat.Rgba32);
                var gray = new byte[info.Width * info.Height];
                PnmImageDecoder.TryDecodeInto(data, gray, 0, ImagePixelFormat.Gray8);
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
    public void DecodeInto_allocates_nothing()
    {
        var rgba = QoiTests.Pattern(640, 48, alpha: true, seed: 9);
        var files = new List<(string, byte[], Func<byte[], byte[], ImagePixelFormat, bool>)>
        {
            ("qoi rgba", QoiEncoder.Encode(rgba, 640, 48, ImagePixelFormat.Rgba32), (d, o, f) => QoiImageDecoder.TryDecodeInto(d, o, 0, f)),
            ("qoi rgb", QoiEncoder.Encode([.. rgba.Where((_, i) => i % 4 != 3)], 640, 48, ImagePixelFormat.Rgb24), (d, o, f) => QoiImageDecoder.TryDecodeInto(d, o, 0, f)),
        };
        foreach (var format in new[] { PnmFormat.PlainBitmap, PnmFormat.PlainPixmap, PnmFormat.Bitmap, PnmFormat.Graymap, PnmFormat.Pixmap, PnmFormat.Arbitrary })
        foreach (var max in new[] { 0, 7, 1000, 65535 })
        {
            if (max != 0 && format is PnmFormat.Bitmap or PnmFormat.PlainBitmap) continue;
            files.Add(($"{format} max {max}", PnmEncoder.Encode(rgba, 640, 48, ImagePixelFormat.Rgba32, new PnmEncodeOptions { Format = format, MaxValue = max }),
                (d, o, f) => PnmImageDecoder.TryDecodeInto(d, o, 0, f)));
        }

        foreach (var (name, data, decode) in files)
        foreach (var target in Enum.GetValues<ImagePixelFormat>())
        {
            var destination = new byte[640 * 48 * 4];
            for (var i = 0; i < 3; i++) decode(data, destination, target).ShouldBeTrue(name);
            var best = long.MaxValue;
            for (var attempt = 0; attempt < 8 && best > 0; attempt++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                decode(data, destination, target);
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            best.ShouldBe(0, $"{name} into {target}");
        }
    }
}

/// <summary>
/// Every Netpbm row kernel gives the same bytes on every tier as the test's own per-sample model, at counts that
/// end inside, on and past each vector step, into spans exactly as long as the output with a sentinel after it.
/// </summary>
public sealed class PnmKernelTests
{
    private static readonly int[] Counts = [1, 2, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 200];
    private static readonly int[] Tiers = [PnmKernels.Scalar, PnmKernels.Vector128Tier, PnmKernels.Vector256Tier];

    private static void Run(int length, Action<Span<byte>> kernel, byte[] expected, string because)
    {
        var buffer = Enumerable.Repeat((byte)0xA5, length + 64).ToArray();
        kernel(buffer.AsSpan(0, length));
        buffer.AsSpan(0, length).ToArray().ShouldBe(expected, because);
        buffer.AsSpan(length).ToArray().ShouldAllBe(b => b == 0xA5, because);
    }

    [Fact]
    public void HighBytes_takes_the_first_byte_of_each_pair()
    {
        foreach (var count in Counts)
        foreach (var tier in Tiers)
        {
            var source = new byte[2 * count];
            new Random(count).NextBytes(source);
            Run(count, d => PnmKernels.HighBytes(source, d, count, tier), [.. Enumerable.Range(0, count).Select(i => source[2 * i])], $"count {count} tier {tier}");
        }
    }

    [Fact]
    public void ExpandBits_turns_set_bits_black_most_significant_first()
    {
        foreach (var count in Counts)
        foreach (var tier in Tiers)
        {
            var source = new byte[(count + 7) / 8];
            new Random(count).NextBytes(source);
            Run(count, d => PnmKernels.ExpandBits(source, d, count, tier),
                [.. Enumerable.Range(0, count).Select(i => (source[i / 8] >> (7 - i % 8) & 1) == 1 ? (byte)0 : (byte)255)], $"count {count} tier {tier}");
        }
    }

    [Fact]
    public void PackBits_sets_a_bit_below_128_most_significant_first()
    {
        foreach (var count in Counts)
        foreach (var tier in Tiers)
        {
            var source = new byte[count];
            new Random(count).NextBytes(source);
            var expected = new byte[(count + 7) / 8];
            for (var i = 0; i < count; i++)
                if (source[i] < 128) expected[i / 8] |= (byte)(0x80 >> (i % 8));
            Run(expected.Length, d => PnmKernels.PackBits(source, d, count, tier), expected, $"count {count} tier {tier}");
        }
    }
}
