using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Qoi;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Hostile input for the streaming QOI reader: random mutations of a set of seed files, fed in random pieces (sync and
/// async-only), either give exactly <see cref="QoiImageDecoder.TryDecodeInto"/>'s rows or fail with
/// <see cref="InvalidDataException"/> exactly when it returns false, within seconds. <c>QOI_STREAM_FUZZ_ITERATIONS</c>
/// (default 500) and <c>QOI_STREAM_FUZZ_SEED</c> set the soak.
/// </summary>
public sealed class QoiStreamingRobustnessTests
{
    // Images up to this many pixels are decoded whole for the comparison; bigger headers are only read row by row.
    private const long MaxComparedPixels = 1L << 20;

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("QOI_STREAM_FUZZ_ITERATIONS"), out var n) ? n : 500;

    private static int Seed =>
        int.TryParse(Environment.GetEnvironmentVariable("QOI_STREAM_FUZZ_SEED"), out var n) ? n : 20261123;

    private static List<(string Name, byte[] Data)> Seeds()
    {
        var rgba = QoiTests.Pattern(23, 9, alpha: true, seed: 4);
        var flat = new byte[70 * 3 * 4];
        return
        [
            ("rgba", QoiEncoder.Encode(rgba, 23, 9, PixelFormat.Rgba32)),
            ("rgb", QoiEncoder.Encode([.. rgba.Where((_, i) => i % 4 != 3)], 23, 9, PixelFormat.Rgb24)),
            ("flat", QoiEncoder.Encode(flat, 70, 3, PixelFormat.Rgba32)),
            ("hand", QoiTests.HandStream().Stream),
        ];
    }

    [Fact]
    public async Task Mutations_stream_like_the_span_decoder_or_throw_invalid_data()
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
                var format = QoiStreamingTests.Formats[random.Next(QoiStreamingTests.Formats.Length)];
                var feed = random.Next(3);
                current = $"#{i} {name}: {description}, {format}, feed {feed}";
                StreamOne(data, format, feed, random.Next(), current);
            }
        }, TestContext.Current.CancellationToken);
        if (await Task.WhenAny(run, Task.Delay(budget, TestContext.Current.CancellationToken)) != run)
            throw new ShouldAssertException($"The streaming fuzz loop is stuck at {current}");
        await run;
    }

    private static void StreamOne(byte[] data, PixelFormat format, int feed, int seed, string what)
    {
        byte[]? expected = null;
        var spanOk = false;
        var compared = QoiImageDecoder.TryReadInfo(data, out var info) && (long)info.Width * info.Height <= MaxComparedPixels;
        if (compared)
        {
            expected = new byte[info.Width * info.Height * format.BytesPerPixel];
            spanOk = QoiImageDecoder.TryDecodeInto(data, expected, 0, format, null, TestContext.Current.CancellationToken);
        }

        Stream stream = feed switch
        {
            0 => new ChunkedStream(data, maxChunk: 1, seed),
            1 => new ChunkedStream(data, maxChunk: 64, seed),
            _ => new ChunkedStream(data, maxChunk: 37, seed, asyncOnly: true),
        };
        var async = feed == 2;
        var random = new Random(seed);
        byte[]? actual = null;
        try
        {
            using var reader = QoiImageDecoder.CreateRowReader(new StreamInput(stream, initialCapacity: 16), format, null);
            if (async) reader.InitializeAsync(TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
            else reader.Initialize(TestContext.Current.CancellationToken);
            var rowBytes = (long)reader.Width * format.BytesPerPixel;
            if (!compared)
            {
                // A header too big to compare: read one row where a row fits a test buffer; it must fail, the data is short.
                if (rowBytes <= 1 << 24)
                {
                    var row = new byte[rowBytes];
                    while (reader.RowsRead < reader.Height)
                        _ = async ? reader.ReadRowsAsync(row, 1, 0, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult()
                                  : reader.ReadRows(row, 1, 0, TestContext.Current.CancellationToken);
                }
                return;
            }
            actual = new byte[rowBytes * reader.Height];
            while (reader.RowsRead < reader.Height)
            {
                var at = (int)(reader.RowsRead * rowBytes);
                var band = random.Next(1, 6);
                var n = async ? reader.ReadRowsAsync(actual.AsMemory(at), band, 0, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult()
                              : reader.ReadRows(actual.AsSpan(at), band, 0, TestContext.Current.CancellationToken);
                n.ShouldBeGreaterThan(0, what);
            }
        }
        catch (InvalidDataException)
        {
            spanOk.ShouldBeFalse($"{what}: the stream threw where the span decoder decoded");
            return;
        }
        if (compared)
        {
            spanOk.ShouldBeTrue($"{what}: the stream decoded where the span decoder failed");
            actual.ShouldBe(expected, what);
        }
    }

    // Half the mutations land in the header and first chunks.
    private static byte[] Mutate(byte[] seed, Random random, out string description)
    {
        var data = seed.ToArray();
        var span = random.Next(2) == 0 ? Math.Min(24, data.Length) : data.Length;
        var at = random.Next(Math.Max(1, span));
        switch (random.Next(6))
        {
            case 0:
                data[at] ^= (byte)(1 << random.Next(8));
                description = $"bit flip at {at}";
                return data;
            case 1:
                data[at] = (byte)random.Next(256);
                description = $"byte {at} = {data[at]}";
                return data;
            case 2:
            {
                var n = random.Next(2, 9);
                for (var k = 0; k < n; k++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                description = $"{n} flips";
                return data;
            }
            case 3:
                description = $"cut to {at}";
                return data[..at];
            case 4:
            {
                // A size byte: width or height.
                var i = 4 + random.Next(8);
                data[i] = (byte)random.Next(256);
                description = $"size byte {i} = {data[i]}";
                return data;
            }
            default:
            {
                var n = random.Next(1, 16);
                description = $"{n} random bytes inserted at {at}";
                var extra = new byte[n];
                random.NextBytes(extra);
                return [.. data[..at], .. extra, .. data[at..]];
            }
        }
    }
}
