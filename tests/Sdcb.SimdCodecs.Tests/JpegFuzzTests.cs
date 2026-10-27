using System.Diagnostics;
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
        int.TryParse(Environment.GetEnvironmentVariable("JPEG_FUZZ_ITERATIONS"), out var n) && n > 0 ? n : 4000;

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
