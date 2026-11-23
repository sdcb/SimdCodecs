using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Deterministic mutation fuzzing of <see cref="JpegDecoder"/>: every damaged stream
/// must either decode or throw <see cref="InvalidDataException"/> — never crash,
/// hang, read or write out of bounds, or throw anything else. This is the safety
/// net for the unchecked (bounds-check-free) hot loops: a decoder bug that walks
/// off a buffer surfaces here as an <see cref="IndexOutOfRangeException"/> (safe
/// code) or an access violation (unsafe code).
///
/// <para>The seed corpus covers baseline / progressive, every common sampling,
/// grayscale, CMYK / YCCK, restart intervals and non-interleaved scans. Mutations:
/// bit flips, byte overwrites (including 0x00 / 0xFF), truncation, insertion and
/// deletion of byte runs, duplicated runs, and injected markers. Each mutant is
/// decoded at a random scale. Iterations default to a quick CI-sized run; set
/// <c>JPEG_FUZZ_ITERATIONS</c> for a long soak (and <c>JPEG_FUZZ_SEED</c> to explore
/// another sequence).</para>
/// </summary>
public sealed class JpegFuzzTests
{
    private const long MaxPixels = 1 << 22;

    private static readonly Lazy<(string Name, byte[] Jpeg)[]> Seeds = new(BuildSeeds);

    private static (string, byte[])[] BuildSeeds()
    {
        const int w = 37, h = 29;
        var rgb = new byte[w * h * 3];
        var s = 0x9E3779B9u;
        for (var i = 0; i < rgb.Length; ++i)
        {
            s = s * 1664525u + 1013904223u;
            rgb[i] = (byte)((i * 7 % 251) ^ (s >> 27));
        }

        byte Sample(int k, int x, int y) => k < 3 ? rgb[(y * w + x) * 3 + k] : (byte)(x * 5 + y * 3);

        TestJpegWriter.Component[] Ycc(int yh, int yv) => [new(1, yh, yv), new(2, 1, 1), new(3, 1, 1)];

        return
        [
            ("4:2:0", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, "4:2:0")),
            ("4:2:2", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 60, "4:2:2")),
            ("4:4:4", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 95, "4:4:4")),
            ("4:2:0 progressive", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, "4:2:0", progressive: true)),
            ("4:4:4 progressive", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 50, "4:4:4", progressive: true)),
            ("gray", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, gray: true)),
            ("gray progressive", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, gray: true, progressive: true)),
            ("cmyk", JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, cmyk: true)),
            ("writer 4:2:0 restart 3", TestJpegWriter.Encode(w, h, Ycc(2, 2), Sample, new() { RestartInterval = 3 })),
            ("writer 4:1:1 restart 1", TestJpegWriter.Encode(w, h, Ycc(4, 1), Sample, new() { RestartInterval = 1 })),
            ("writer non-interleaved restart 2", TestJpegWriter.Encode(w, h, Ycc(2, 2), Sample, new() { Interleaved = false, RestartInterval = 2 })),
            ("writer YCCK", TestJpegWriter.Encode(w, h, [new(1, 2, 2), new(2, 1, 1), new(3, 1, 1), new(4, 2, 2)], Sample, new() { AdobeTransform = 2, Jfif = false })),
        ];
    }

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("JPEG_FUZZ_ITERATIONS"), out var n) && n > 0 ? n : 500;

    private static int Seed =>
        int.TryParse(Environment.GetEnvironmentVariable("JPEG_FUZZ_SEED"), out var n) ? n : 20261024;

    [Fact]
    public void Seeds_DecodeCleanly()
    {
        foreach (var (name, jpeg) in Seeds.Value)
            Should.NotThrow(() => JpegDecoder.Decode(jpeg), name);
    }

    [Fact]
    public async Task MutatedStreams_DecodeOrThrowInvalidData()
    {
        var seeds = Seeds.Value;
        var iterations = Iterations;
        var seed = Seed;
        string? current = null;

        // A hang must fail the test rather than stall the run: the loop runs on a
        // worker and every mutant gets a generous wall-clock budget.
        var progress = 0;
        var work = Task.Run(() =>
        {
            var rng = new Random(seed);
            var scales = new[] { JpegScale.Full, JpegScale.Full, JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth };
            for (var it = 0; it < iterations; ++it)
            {
                var (name, original) = seeds[rng.Next(seeds.Length)];
                var mutant = Mutate(original, rng, out var how);
                var scale = scales[rng.Next(scales.Length)];
                current = $"iteration {it} (seed {seed}): {name}, {how}, {scale}";
                DecodeOne(mutant, scale, current);
                Volatile.Write(ref progress, it + 1);
            }
        }, TestContext.Current.CancellationToken);

        var budget = TimeSpan.FromSeconds(Math.Max(60, iterations / 20));
        var finished = await Task.WhenAny(work, Task.Delay(budget, TestContext.Current.CancellationToken));
        (finished == work).ShouldBeTrue($"fuzz run did not finish in {budget} (stuck at {current}, {progress} done)");
        await work;
    }

    private static int StreamIterations =>
        int.TryParse(Environment.GetEnvironmentVariable("JPEG_STREAM_FUZZ_ITERATIONS"), out var n) && n > 0 ? n
        : int.TryParse(Environment.GetEnvironmentVariable("JPEG_FUZZ_ITERATIONS"), out n) && n > 0 ? n : 300;

    /// <summary>
    /// The streaming reader (<see cref="JpegImageDecoder.CreateRowReader"/>) against the whole decode on the same damaged
    /// streams: where <see cref="JpegImageDecoder.TryDecodeInto"/> succeeds the reader gives exactly its rows, fed in
    /// random chunks (one byte at a time now and then) or async-only; where it fails the reader throws
    /// <see cref="InvalidDataException"/> — and nothing else, ever. Set <c>JPEG_STREAM_FUZZ_ITERATIONS</c> (or <c>JPEG_FUZZ_ITERATIONS</c>, which it follows when unset) for a soak.
    /// </summary>
    [Fact]
    public async Task MutatedStreams_StreamingMatchesDecodeInto()
    {
        var seeds = Seeds.Value;
        var iterations = StreamIterations;
        var seed = Seed;
        string? current = null;
        var progress = 0;
        var work = Task.Run(() =>
        {
            var rng = new Random(seed ^ 0x5EED);
            var scales = new[] { 1, 1, 1, 2, 4, 8 };
            var formats = new[] { PixelFormat.Rgba32, PixelFormat.Rgb24, PixelFormat.Gray8, PixelFormat.Bgra32, PixelFormat.Rgba64 };
            var feeds = new[] { Streaming.StreamingAssert.Feed.RandomChunks, Streaming.StreamingAssert.Feed.RandomChunks, Streaming.StreamingAssert.Feed.AsyncOnly, Streaming.StreamingAssert.Feed.OneByte };
            for (var it = 0; it < iterations; ++it)
            {
                var (name, original) = seeds[rng.Next(seeds.Length)];
                var how = "intact";
                var mutant = rng.Next(16) == 0 ? original : Mutate(original, rng, out how);
                var scale = scales[rng.Next(scales.Length)];
                var format = formats[rng.Next(formats.Length)];
                var feed = feeds[rng.Next(feeds.Length)];
                current = $"iteration {it} (seed {seed}): {name}, {how}, 1/{scale}, {format}, {feed}";
                StreamOne(mutant, scale, format, feed, it, current);
                Volatile.Write(ref progress, it + 1);
            }
        }, TestContext.Current.CancellationToken);

        var budget = TimeSpan.FromSeconds(Math.Max(60, iterations / 20));
        var finished = await Task.WhenAny(work, Task.Delay(budget, TestContext.Current.CancellationToken));
        (finished == work).ShouldBeTrue($"fuzz run did not finish in {budget} (stuck at {current}, {progress} done)");
        await work;
    }

    private static void StreamOne(byte[] mutant, int scale, PixelFormat format, Streaming.StreamingAssert.Feed feed, int it, string context)
    {
        var options = scale == 1 ? null : new DecodeOptions { ScaleDenominator = scale };
        byte[]? expected = null;
        if (JpegImageDecoder.TryReadInfo(mutant, out var info))
        {
            var (w, h) = (options ?? DecodeOptions.Default).GetOutputSize(info);
            if ((long)info.Width * info.Height > MaxPixels)
                return;
            var buffer = new byte[w * h * format.BytesPerPixel];
            if (JpegImageDecoder.TryDecodeInto(mutant, buffer, w * format.BytesPerPixel, format, options, CancellationToken.None))
                expected = buffer;
        }

        var sw = Stopwatch.StartNew();
        byte[]? actual = null;
        try
        {
            actual = Streaming.StreamingAssert.ReadAll(mutant, feed, input => JpegImageDecoder.CreateRowReader(input, format, options),
                out _, out _, out _, seed: it + 1);
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{context}: {e.GetType().Name}: {e.Message}", e);
        }

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), context);
        if (expected is null)
        {
            actual.ShouldBeNull($"{context}: the whole decode rejects the stream, the reader accepted it");
            return;
        }

        actual.ShouldNotBeNull($"{context}: the whole decode accepts the stream, the reader rejected it");
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new ShouldAssertException($"{context}: rows differ from TryDecodeInto at byte {actual.AsSpan().CommonPrefixLength(expected)}");
    }

    /// <summary>
    /// The Extended XMP joiner (<c>JpegExtendedXmp</c>) on damaged XML: the standard and extended packets are mutated
    /// as text (markup characters, truncation, deletion, duplication) and the GUID recomputed from the damaged bytes,
    /// so the XML readers run rather than stopping at the MD5 check; the chunks are cut at random places and shuffled,
    /// and now and then a segment header is damaged too. Reading must never throw, and must finish in time.
    /// </summary>
    [Fact]
    public async Task MutatedExtendedXmp_ReadsWithoutThrowing()
    {
        var iterations = Iterations;
        var seed = Seed;
        string? current = null;
        var progress = 0;
        var joins = 0;
        var text = JpegExtendedXmpTests.LongText(3_000);
        var work = Task.Run(() =>
        {
            var rng = new Random(seed);
            for (var it = 0; it < iterations; ++it)
            {
                var extended = MutateXml(System.Text.Encoding.UTF8.GetBytes(JpegExtendedXmpTests.ExtendedPacket(text)), rng, rng.Next(3) == 0 ? 0 : 1 + rng.Next(3));
                var guid = JpegExtendedXmpTests.Md5Hex(extended);
                var standard = MutateXml(System.Text.Encoding.UTF8.GetBytes(JpegExtendedXmpTests.StandardPacket(guid, rng.Next(2) == 0 ? "xmpNote" : "n")), rng, rng.Next(3) == 0 ? 1 + rng.Next(3) : 0);
                var cuts = Enumerable.Range(0, rng.Next(4)).Select(_ => rng.Next(1, Math.Max(2, extended.Length))).Distinct().Order().Where(c => c < extended.Length).ToArray();
                var segments = JpegExtendedXmpTests.Chunks(guid, extended, cuts).OrderBy(_ => rng.Next()).ToList();
                if (rng.Next(8) == 0 && segments.Count > 0)
                {
                    var victim = segments[rng.Next(segments.Count)];
                    victim[4 + 35 + 32 + rng.Next(8)] ^= (byte)(1 << rng.Next(8)); // full length or offset
                }

                segments.Insert(rng.Next(segments.Count + 1), JpegExtendedXmpTests.StandardSegment(System.Text.Encoding.UTF8.GetString(standard)));
                var jpeg = JpegExtendedXmpTests.Jpeg(segments);
                current = $"iteration {it} (seed {seed})";
                var sw = Stopwatch.StartNew();
                try
                {
                    if (JpegMetadata.ReadXmp(jpeg).Length > standard.Length)
                        Interlocked.Increment(ref joins);
                }
                catch (Exception e)
                {
                    throw new ShouldAssertException($"{current}: {e.GetType().Name}: {e.Message}", e);
                }

                sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), current);
                Volatile.Write(ref progress, it + 1);
            }
        }, TestContext.Current.CancellationToken);

        var budget = TimeSpan.FromSeconds(Math.Max(60, iterations / 20));
        var finished = await Task.WhenAny(work, Task.Delay(budget, TestContext.Current.CancellationToken));
        (finished == work).ShouldBeTrue($"fuzz run did not finish in {budget} (stuck at {current}, {progress} done)");
        await work;

        // About one mutant in six is left intact; if none of them joins, the run tested only the early exits.
        joins.ShouldBeGreaterThan(iterations / 20, "mutants that joined");
    }

    private static byte[] MutateXml(byte[] src, Random rng, int ops)
    {
        ReadOnlySpan<byte> markup = "<>/=\"':&;#!?[]- x\n"u8;
        var data = new List<byte>(src);
        for (var op = 0; op < ops && data.Count > 2; ++op)
        {
            var pos = rng.Next(data.Count);
            switch (rng.Next(5))
            {
                case 0:
                case 1:
                    data[pos] = markup[rng.Next(markup.Length)];
                    break;
                case 2:
                    data.RemoveRange(pos, Math.Min(1 + rng.Next(40), data.Count - pos));
                    break;
                case 3:
                {
                    var n = Math.Min(1 + rng.Next(80), data.Count - pos);
                    data.InsertRange(rng.Next(data.Count), data.GetRange(pos, n));
                    break;
                }
                default:
                    data.RemoveRange(pos, data.Count - pos);
                    break;
            }
        }

        return [.. data];
    }

    private static void DecodeOne(byte[] mutant, JpegScale scale, string context)
    {
        JpegInfo info;
        try
        {
            info = JpegDecoder.ReadInfo(mutant);
        }
        catch (InvalidDataException)
        {
            return;
        }

        // Header-driven allocation is bounded by the frame size the header claims;
        // keep the fuzzer away from (legitimately) huge frames.
        var (w, h) = info.ScaledSize(scale);
        if ((long)info.Width * info.Height > MaxPixels || (long)w * h > MaxPixels)
            return;

        var sw = Stopwatch.StartNew();
        try
        {
            var image = JpegDecoder.Decode(mutant, scale);
            image.Pixels.Length.ShouldBe(image.Width * image.Height * 4, context);
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{context}: {e.GetType().Name}: {e.Message}", e);
        }

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), context);
    }

    private static byte[] Mutate(byte[] src, Random rng, out string how)
    {
        var data = new List<byte>(src);
        var ops = 1 + rng.Next(3);
        var parts = new List<string>();
        for (var op = 0; op < ops && data.Count > 4; ++op)
        {
            // Bias towards the entropy-coded tail half the time: header damage is
            // mostly rejected early, scan damage exercises the hot loops.
            var pos = rng.Next(2) == 0 ? rng.Next(data.Count) : data.Count / 3 + rng.Next(data.Count - data.Count / 3);
            switch (rng.Next(9))
            {
                case 0:
                case 1:
                    data[pos] ^= (byte)(1 << rng.Next(8));
                    parts.Add($"flip@{pos}");
                    break;
                case 2:
                    data[pos] = (byte)(rng.Next(3) switch { 0 => 0x00, 1 => 0xFF, _ => rng.Next(256) });
                    parts.Add($"set@{pos}={data[pos]:X2}");
                    break;
                case 3:
                    data.RemoveRange(pos, data.Count - pos);
                    parts.Add($"truncate@{pos}");
                    break;
                case 4:
                {
                    var n = 1 + rng.Next(16);
                    var bytes = new byte[n];
                    rng.NextBytes(bytes);
                    data.InsertRange(pos, bytes);
                    parts.Add($"insert@{pos}x{n}");
                    break;
                }
                case 5:
                {
                    var n = Math.Min(1 + rng.Next(32), data.Count - pos);
                    data.RemoveRange(pos, n);
                    parts.Add($"delete@{pos}x{n}");
                    break;
                }
                case 6:
                {
                    var n = Math.Min(1 + rng.Next(64), data.Count - pos);
                    data.InsertRange(pos, data.GetRange(pos, n));
                    parts.Add($"dup@{pos}x{n}");
                    break;
                }
                case 7:
                {
                    var marker = (byte)(rng.Next(4) switch { 0 => 0xD0 + rng.Next(8), 1 => 0xD9, 2 => 0xC4, _ => rng.Next(256) });
                    data.InsertRange(pos, [0xFF, marker]);
                    parts.Add($"marker@{pos}={marker:X2}");
                    break;
                }
                default:
                {
                    // Overwrite a 16-bit value (lengths, dimensions, table counts).
                    if (pos + 1 < data.Count)
                    {
                        var v = rng.Next(3) switch { 0 => 0, 1 => 0xFFFF, _ => rng.Next(65536) };
                        data[pos] = (byte)(v >> 8);
                        data[pos + 1] = (byte)v;
                        parts.Add($"word@{pos}={v:X4}");
                    }

                    break;
                }
            }
        }

        how = string.Join(",", parts);
        return data.ToArray();
    }
}
