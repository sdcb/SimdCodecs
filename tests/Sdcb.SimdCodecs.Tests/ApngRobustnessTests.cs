using System.Buffers.Binary;
using System.Diagnostics;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Untrusted animated PNGs: every truncation of a seed corpus (our encoder's output, hand-built streams with every feature of the
/// container, and the committed ffmpeg-made fixtures) and seeded mutations of it — random bytes, bytes with the CRCs put right again
/// so the damage reaches the animation chunks, whole fields of the header and the control chunks set to the values that break
/// arithmetic, chunks deleted, repeated and swapped — either decode or fail with <see cref="InvalidDataException"/> (false from the
/// <c>Try</c> methods), never another exception, a hang or an unbounded allocation. The mutation count scales with
/// <c>APNG_FUZZ_ITERATIONS</c> (default 4000) and its seed with <c>APNG_FUZZ_SEED</c>.
/// </summary>
public sealed class ApngRobustnessTests
{
    private const long MaxPixels = 1L << 22;

    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("APNG_FUZZ_ITERATIONS"), out var n) ? n : 4000;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("APNG_FUZZ_SEED"), out var n) ? n : 20261105;

    internal static List<(string Name, byte[] Data)> Seeds()
    {
        var seeds = new List<(string, byte[])>();
        var frames = PngApngOracleTests.Sequence(30, 20, 6, opaqueBackground: false);
        foreach (var reduce in new[] { false, true })
        {
            var encoder = new PngAnimationEncoder(30, 20, new PngWriteOptions { LosslessReduction = reduce, LoopCount = 2 });
            foreach (var f in frames)
                encoder.AddFrame(f, PixelFormat.Rgba32, TimeSpan.FromMilliseconds(40));
            seeds.Add(($"encoder 8-bit reduce {reduce}", encoder.Encode()));
        }
        var wide = new PngAnimationEncoder(30, 20, null, sixteenBit: true);
        foreach (var f in frames.Take(4))
            wide.AddFrame(f, PixelFormat.Rgba32, TimeSpan.FromMilliseconds(40));
        seeds.Add(("encoder 16-bit", wide.Encode()));

        // Hand-built: the default image outside the animation, Adam7 frames, every dispose and blend operation, a palette.
        byte[] Px(int x, int y) => [(byte)(x * 40), (byte)(y * 50), (byte)(x * y), (byte)(x % 3 == 0 ? 0 : 255)];
        var interlaced = new ApngStream(7, 6, 8, 6, interlaced: true).Actl(3, 0)
            .Idat(ApngStream.Rows(7, 6, 4, Px, interlaced: true))
            .Fctl(0, 7, 6, 0, 0, 1, 10, 1, 0).Fdat(1, ApngStream.Rows(7, 6, 4, Px, interlaced: true))
            .Fctl(2, 5, 4, 1, 1, 2, 0, 2, 1).Fdat(3, ApngStream.Rows(5, 4, 4, Px, interlaced: true))
            .Fctl(4, 3, 3, 4, 3, 3, 100, 0, 1).Fdat(5, ApngStream.Rows(3, 3, 4, Px, interlaced: true))
            .End();
        seeds.Add(("interlaced default outside", interlaced));
        var palette = new byte[12];
        for (var i = 0; i < 12; i++)
            palette[i] = (byte)(i * 20);
        var indexed = new ApngStream(6, 5, 8, 3).Plte(palette).Trns([0, 255, 128]).Actl(2, 1)
            .Fctl(0, 6, 5, 0, 0, 1, 10, 2, 1).Idat(ApngStream.Rows(6, 5, 1, (x, y) => [(byte)((x + y) % 4)]))
            .Fctl(1, 3, 2, 2, 1, 1, 10, 0, 0).Fdat(2, ApngStream.Rows(3, 2, 1, (x, y) => [(byte)((x * y) % 4)]))
            .End();
        seeds.Add(("indexed", indexed));
        var grey16 = new ApngStream(5, 4, 16, 4).Trns([]).Actl(2, 0)
            .Fctl(0, 5, 4, 0, 0, 1, 10, 0, 0).Idat(ApngStream.Rows(5, 4, 4, (x, y) => [(byte)x, (byte)y, 0xFF, (byte)(x * 50)]))
            .Fctl(1, 2, 2, 1, 1, 1, 10, 1, 1).Fdat(2, ApngStream.Rows(2, 2, 4, (x, y) => [1, 2, 3, 4]))
            .End();
        seeds.Add(("grey+alpha 16", grey16));

        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "png", "apng"), "*.apng").OrderBy(f => f))
            seeds.Add((Path.GetFileName(file), File.ReadAllBytes(file)));
        return seeds;
    }

    [Fact]
    public void Every_truncation_is_refused_or_decoded()
    {
        foreach (var (name, data) in Seeds())
        {
            // Past the signature and IHDR the cut can land anywhere; every length is tried for the small seeds, every 7th for the fixtures.
            var step = data.Length > 4000 ? 7 : 1;
            for (var length = 0; length < data.Length; length += step)
                DecodeOne(data[..length], $"{name} cut to {length}");
        }
    }

    [Fact]
    public void Seeds_decode_and_survive_mutation()
    {
        var seeds = Seeds();
        foreach (var (name, data) in seeds)
            DecodeOne(data, name, mustDecode: true);

        var rng = new Random(Seed);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++)
        {
            var (name, seed) = seeds[rng.Next(seeds.Count)];
            var mutated = Mutate(seed, rng, out var how);
            DecodeOne(mutated, $"{name}, {how}, iteration {i}, seed {Seed}");
        }
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(30));
    }

    // ---------------------------------------------------------------- mutations

    private static readonly uint[] Interesting = [0, 1, 2, 3, 7, 8, 255, 256, 65535, 65536, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF];

    private static byte[] Mutate(byte[] seed, Random rng, out string how)
    {
        var data = (byte[])seed.Clone();
        var chunks = ChunkOffsets(data);
        switch (rng.Next(7))
        {
            case 0:
                how = "random bytes";
                for (var k = rng.Next(1, 5); k > 0; k--)
                    data[rng.Next(data.Length)] = (byte)rng.Next(256);
                return data;
            case 1:
                how = "random bytes, CRCs fixed";
                for (var k = rng.Next(1, 5); k > 0; k--)
                    data[rng.Next(8, data.Length)] = (byte)rng.Next(256);
                FixCrcs(data);
                return data;
            case 2 or 3:
            {
                // A 32-bit field of an animation or header chunk set to a value that breaks arithmetic.
                var interesting = chunks.Where(c => c.Type is "IHDR" or "acTL" or "fcTL" or "fdAT").ToList();
                if (interesting.Count == 0)
                    goto case 0;
                var c = interesting[rng.Next(interesting.Count)];
                how = $"{c.Type} field";
                var fields = Math.Max(1, c.Length / 4);
                var at = c.PayloadOffset + 4 * rng.Next(fields);
                if (at + 4 > c.PayloadOffset + c.Length)
                    at = c.PayloadOffset;
                if (c.Length >= 4)
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), rng.Next(3) == 0 ? (uint)rng.Next(70) : Interesting[rng.Next(Interesting.Length)]);
                FixCrcs(data);
                return data;
            }
            case 4:
            {
                how = "chunk deleted";
                var c = chunks[rng.Next(chunks.Count)];
                return [.. data[..c.Offset], .. data[(c.Offset + 12 + c.Length)..]];
            }
            case 5:
            {
                how = "chunk repeated";
                var c = chunks[rng.Next(chunks.Count)];
                var end = c.Offset + 12 + c.Length;
                return [.. data[..end], .. data[c.Offset..end], .. data[end..]];
            }
            default:
            {
                how = "chunks swapped";
                if (chunks.Count < 3)
                    goto case 0;
                var i = rng.Next(chunks.Count - 1);
                var (a, b) = (chunks[i], chunks[i + 1]);
                var bEnd = b.Offset + 12 + b.Length;
                return [.. data[..a.Offset], .. data[b.Offset..bEnd], .. data[a.Offset..b.Offset], .. data[bEnd..]];
            }
        }
    }

    private readonly record struct ChunkRef(int Offset, string Type, int Length)
    {
        public int PayloadOffset => Offset + 8;
    }

    private static List<ChunkRef> ChunkOffsets(byte[] data)
    {
        var list = new List<ChunkRef>();
        for (var pos = 8; pos + 12 <= data.Length;)
        {
            var length = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (pos + 12 + length > data.Length)
                break;
            list.Add(new ChunkRef(pos, System.Text.Encoding.Latin1.GetString(data, pos + 4, 4), (int)length));
            pos += 12 + (int)length;
        }
        return list;
    }

    private static void FixCrcs(byte[] data)
    {
        foreach (var c in ChunkOffsets(data))
        {
            var crc = PngCrc32.Compute(data.AsSpan(c.Offset + 4, 4), data.AsSpan(c.PayloadOffset, c.Length));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(c.PayloadOffset + c.Length), crc);
        }
    }

    // ---------------------------------------------------------------- one input, through every API

    private static void DecodeOne(byte[] data, string what, bool mustDecode = false)
    {
        // The canvas of a mutated header can claim more than a test should allocate: those are the decoder's to refuse by data
        // size (1032:1), which the still decoder shares; here they are skipped like the other codecs' fuzzers skip them.
        if (data.Length >= 24 && !mustDecode)
        {
            var w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
            var h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
            if ((long)w * h > MaxPixels)
                return;
        }

        var clock = Stopwatch.StartNew();
        var decoded = 0;
        try
        {
            var decoder = new PngAnimationDecoder(data);
            var size = checked(decoder.CanvasWidth * decoder.CanvasHeight * 4);
            var canvas = new byte[size];
            var order = new List<int>();
            for (var i = 0; i < Math.Min(decoder.FrameCount, 12); i++)
                order.Add(i);
            var rng = new Random(data.Length);
            for (var i = 0; i < 6; i++)
                order.Add(rng.Next(decoder.FrameCount));
            foreach (var index in order)
            {
                try
                {
                    decoder.Seek(index);
                    if (decoder.TryDecodeNextFrame(canvas, out _))
                        decoded++;
                }
                catch (InvalidDataException)
                {
                }
            }
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e)
        {
            Assert.Fail($"{what}: PngAnimationDecoder threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }

        try
        {
            if (ImageCodecs.TryOpenFrames(data, out var reader))
            {
                using (reader)
                {
                    var last = Math.Min(reader.FrameCount, 8);
                    for (var i = 0; i < last; i++)
                    {
                        if (reader.TryGetFrameInfo(i, out var info))
                        {
                            var pixels = new byte[(long)info.Info.Width * info.Info.Height * 4];
                            reader.TryDecodeFrameInto(i, pixels, 0, PixelFormat.Rgba32);
                            reader.TryDecodeFrame(i, out var image);
                            image?.Dispose();
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Assert.Fail($"{what}: the frame reader threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), $"{what}: took long");
        if (mustDecode)
            decoded.ShouldBeGreaterThan(0, what);
    }
}
