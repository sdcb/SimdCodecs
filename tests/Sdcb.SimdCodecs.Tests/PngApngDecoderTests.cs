using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// APNG decoding from hand-assembled streams: every dispose and blend combination against a reference compositor, the cases
/// the specification singles out (Previous on the first frame, a default image outside the animation), every colour type
/// the frames can use, Adam7 frames, sequence and geometry violations, and seeking.
/// </summary>
public sealed class PngApngDecoderTests
{
    private const int W = 6, H = 5;

    // ---------------------------------------------------------------- sources: a colour type and depth, and the pixels that go in

    /// <summary>A sample layout: the bytes of a pixel (<c>Raw</c>) and the RGBA it decodes to (<c>Rgba</c>), for a frame seed.</summary>
    private sealed record Source(string Name, byte ColorType, byte Depth, Func<int, int, int, byte[]> Raw, Func<int, int, int, int[]> Rgba,
        byte[]? Plte = null, byte[]? Trns = null)
    {
        public int Max => Depth == 16 ? 65535 : 255;
        public int BytesPerPixel => (ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 }) * (Depth / 8);
        public override string ToString() => Name;
    }

    // Alphas that reach every branch of OVER: 0, max, a third, two thirds, a half, 1 and max − 1.
    private static int Alpha(int max, int n) => (n % 7) switch { 0 => 0, 1 => max, 2 => max / 3, 3 => 2 * max / 3 + 1, 4 => max / 2, 5 => 1, _ => max - 1 };

    private static int Sample(int max, int seed, int x, int y, int c) => (x * 37 + y * 61 + seed * 113 + c * 29 + (max > 255 ? (x + y + seed) * 9973 : 0)) % (max + 1);

    private static byte[] Be(int value, int depth) => depth == 16 ? [(byte)(value >> 8), (byte)value] : [(byte)value];

    private static Source Rgba(int depth) => new($"rgba{depth}", 6, (byte)depth,
        (seed, x, y) => [.. Enumerable.Range(0, 4).SelectMany(c => Be(c == 3 ? Alpha(Max(depth), x + 2 * y + seed) : Sample(Max(depth), seed, x, y, c), depth))],
        (seed, x, y) => [.. Enumerable.Range(0, 4).Select(c => c == 3 ? Alpha(Max(depth), x + 2 * y + seed) : Sample(Max(depth), seed, x, y, c))]);

    private static int Max(int depth) => depth == 16 ? 65535 : 255;

    private static Source GreyAlpha(int depth) => new($"greyalpha{depth}", 4, (byte)depth,
        (seed, x, y) => [.. Be(Sample(Max(depth), seed, x, y, 0), depth), .. Be(Alpha(Max(depth), x + y + seed), depth)],
        (seed, x, y) => { var v = Sample(Max(depth), seed, x, y, 0); return [v, v, v, Alpha(Max(depth), x + y + seed)]; });

    private static Source Grey(int depth) => new($"grey{depth}", 0, (byte)depth,
        (seed, x, y) => Be(Sample(Max(depth), seed, x, y, 0), depth),
        (seed, x, y) => { var v = Sample(Max(depth), seed, x, y, 0); return [v, v, v, Max(depth)]; });

    private static Source Rgb(int depth) => new($"rgb{depth}", 2, (byte)depth,
        (seed, x, y) => [.. Enumerable.Range(0, 3).SelectMany(c => Be(Sample(Max(depth), seed, x, y, c), depth))],
        (seed, x, y) => [.. Enumerable.Range(0, 3).Select(c => Sample(Max(depth), seed, x, y, c)), Max(depth)]);

    // A colour key: pixels equal to the sample at (1, 1) of seed 2 are transparent (their colour is kept).
    private static Source GreyKey(int depth)
    {
        var key = Sample(Max(depth), 2, 1, 1, 0);
        var plain = Grey(depth);
        return plain with
        {
            Name = $"grey{depth}-key",
            Trns = Be(key, 16),
            Rgba = (seed, x, y) => { var p = plain.Rgba(seed, x, y); if (p[0] == key) p[3] = 0; return p; },
        };
    }

    private static Source RgbKey(int depth)
    {
        var key = Enumerable.Range(0, 3).Select(c => Sample(Max(depth), 2, 1, 1, c)).ToArray();
        var plain = Rgb(depth);
        return plain with
        {
            Name = $"rgb{depth}-key",
            Trns = [.. key.SelectMany(v => Be(v, 16))],
            Rgba = (seed, x, y) => { var p = plain.Rgba(seed, x, y); if (p[0] == key[0] && p[1] == key[1] && p[2] == key[2]) p[3] = 0; return p; },
        };
    }

    private static Source Palette()
    {
        var plte = new byte[16 * 3];
        for (var i = 0; i < 16; i++)
            (plte[3 * i], plte[3 * i + 1], plte[3 * i + 2]) = ((byte)(i * 16 + 3), (byte)(255 - i * 9), (byte)(i * i));
        var trns = new byte[] { 0, 255, 128, 77, 0, 10 }; // the first six entries; the rest are opaque
        return new Source("palette8-trns", 3, 8,
            (seed, x, y) => [(byte)((x + 3 * y + seed) % 16)],
            (seed, x, y) => { var i = (x + 3 * y + seed) % 16; return [plte[3 * i], plte[3 * i + 1], plte[3 * i + 2], i < trns.Length ? trns[i] : 255]; },
            plte, trns);
    }

    private static readonly Source[] Sources =
    [
        Rgba(8), Rgba(16), GreyAlpha(8), GreyAlpha(16), Grey(8), Grey(16), Rgb(8), Rgb(16), GreyKey(8), GreyKey(16), RgbKey(8), RgbKey(16), Palette(),
    ];

    public static TheoryData<string> SourceNames() => [.. Sources.Select(s => s.Name)];

    private static Source SourceNamed(string name) => Sources.Single(s => s.Name == name);

    // ---------------------------------------------------------------- building streams and reading frames back

    private sealed record Spec(int X, int Y, int Width, int Height, byte Dispose, byte Blend, int Seed, ushort DelayNum = 1, ushort DelayDen = 10);

    private static ApngStream Header(Source s, bool interlaced, uint frames, uint plays)
    {
        var stream = new ApngStream(W, H, s.Depth, s.ColorType, interlaced);
        if (s.Plte is not null) stream.Plte(s.Plte);
        if (s.Trns is not null) stream.Trns(s.Trns);
        return stream.Actl(frames, plays);
    }

    private static byte[] Data(Source s, Spec f, bool interlaced) =>
        ApngStream.Rows(f.Width, f.Height, s.BytesPerPixel, (x, y) => s.Raw(f.Seed, x, y), interlaced);

    /// <summary>An APNG of the given frames; when <paramref name="defaultIsFrame"/> is false the default image is a separate, whole-canvas picture (seed 99).</summary>
    private static byte[] Build(Source s, bool defaultIsFrame, IReadOnlyList<Spec> frames, bool interlaced = false, uint plays = 0)
    {
        var stream = Header(s, interlaced, (uint)frames.Count, plays);
        uint seq = 0;
        var first = 0;
        if (defaultIsFrame)
        {
            var f = frames[0];
            f.Width.ShouldBe(W);
            stream.Fctl(seq++, (uint)f.Width, (uint)f.Height, 0, 0, f.DelayNum, f.DelayDen, f.Dispose, f.Blend);
            stream.Idat(Data(s, f, interlaced));
            first = 1;
        }
        else
        {
            stream.Idat(Data(s, new Spec(0, 0, W, H, 0, 0, 99), interlaced));
        }
        for (var i = first; i < frames.Count; i++)
        {
            var f = frames[i];
            stream.Fctl(seq++, (uint)f.Width, (uint)f.Height, (uint)f.X, (uint)f.Y, f.DelayNum, f.DelayDen, f.Dispose, f.Blend);
            stream.Fdat(seq++, Data(s, f, interlaced));
        }
        return stream.End();
    }

    private static List<int[]> Model(Source s, IReadOnlyList<Spec> frames) =>
        ApngStream.Composite(W, H, s.Max, [.. frames.Select(f => new ApngStream.Frame(f.X, f.Y, f.Width, f.Height, f.Dispose, f.Blend,
            (x, y) => s.Rgba(f.Seed, x, y)))]);

    private static int[] Next(PngAnimationDecoder decoder, bool wide)
    {
        if (wide)
        {
            var samples = new ushort[W * H * 4];
            decoder.TryDecodeNextFrame(samples, out _).ShouldBeTrue();
            return [.. samples.Select(v => (int)v)];
        }
        var bytes = new byte[W * H * 4];
        decoder.TryDecodeNextFrame(bytes, out _).ShouldBeTrue();
        return [.. bytes.Select(v => (int)v)];
    }

    private static List<int[]> DecodeAll(byte[] file, Source s)
    {
        var decoder = new PngAnimationDecoder(file);
        var frames = new List<int[]>();
        for (var i = 0; i < decoder.FrameCount; i++)
            frames.Add(Next(decoder, s.Depth == 16));
        return frames;
    }

    private static void ShouldEqualModel(List<int[]> actual, List<int[]> expected, string what)
    {
        actual.Count.ShouldBe(expected.Count, what);
        for (var i = 0; i < expected.Count; i++)
            actual[i].ShouldBe(expected[i], $"{what}: frame {i}");
    }

    // ---------------------------------------------------------------- dispose × blend

    public static TheoryData<byte, byte, byte, byte, byte, int, bool> Combinations()
    {
        var data = new TheoryData<byte, byte, byte, byte, byte, int, bool>();
        foreach (byte d0 in new byte[] { 0, 1, 2 })
        foreach (byte d1 in new byte[] { 0, 1, 2 })
        foreach (byte d2 in new byte[] { 0, 1, 2 })
        foreach (byte b1 in new byte[] { 0, 1 })
        foreach (byte b2 in new byte[] { 0, 1 })
        foreach (var depth in new[] { 8, 16 })
        foreach (var defaultIsFrame in new[] { true, false })
            data.Add(d0, d1, d2, b1, b2, depth, defaultIsFrame);
        return data;
    }

    private static Spec[] FourFrames(byte d0, byte d1, byte d2, byte b1, byte b2) =>
    [
        new(0, 0, W, H, d0, 0, 1),
        new(1, 1, 4, 3, d1, b1, 2),
        new(0, 2, 3, 3, d2, b2, 3),
        new(2, 0, 4, 4, 0, 1, 4),
    ];

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryDisposeAndBlendCombinationMatchesTheSpecification(byte d0, byte d1, byte d2, byte b1, byte b2, int depth, bool defaultIsFrame)
    {
        var s = Rgba(depth);
        var frames = FourFrames(d0, d1, d2, b1, b2);
        var file = Build(s, defaultIsFrame, frames);
        ShouldEqualModel(DecodeAll(file, s), Model(s, frames), $"dispose {d0}{d1}{d2} blend {b1}{b2} depth {depth} default {defaultIsFrame}");
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void SeekingInAnyOrderGivesTheSameFrames(byte d0, byte d1, byte d2, byte b1, byte b2, int depth, bool defaultIsFrame)
    {
        var s = Rgba(depth);
        var frames = FourFrames(d0, d1, d2, b1, b2);
        var expected = Model(s, frames);
        var decoder = new PngAnimationDecoder(Build(s, defaultIsFrame, frames));
        foreach (var index in new[] { 3, 1, 2, 0, 3, 3, 0, 2, 1, 1, 2, 3, 0 })
        {
            decoder.Seek(index, TestContext.Current.CancellationToken);
            decoder.NextFrameIndex.ShouldBe(index);
            Next(decoder, depth == 16).ShouldBe(expected[index], $"seek to frame {index}");
            decoder.NextFrameIndex.ShouldBe(index + 1);
        }
    }

    [Fact]
    public void BlendOverOfAnOpaquePixelReplacesAndOfATransparentOneKeepsTheCanvas()
    {
        // Explicit values, not the model: frame 0 is a mid grey of alpha 200; frame 1 puts alpha 0, alpha 255 and alpha 100 over it.
        var s = Rgba(8);
        var stream = Header(s, false, 2, 0)
            .Fctl(0, W, H, 0, 0, 1, 1, 0, 0)
            .Idat(ApngStream.Rows(W, H, 4, (_, _) => [100, 150, 200, 200]))
            .Fctl(1, 3, 1, 0, 0, 1, 1, 0, 1)
            .Fdat(2, ApngStream.Rows(3, 1, 4, (x, _) => x switch { 0 => [9, 9, 9, 0], 1 => [9, 8, 7, 255], _ => [250, 0, 0, 100] }));
        var decoder = new PngAnimationDecoder(stream.End());
        Next(decoder, false);
        var second = Next(decoder, false);
        second[..4].ShouldBe([100, 150, 200, 200]);   // alpha 0 source: canvas kept
        second[4..8].ShouldBe([9, 8, 7, 255]);        // alpha 255 source: replaced
        // alpha 100 over alpha 200: X = 100·255 + 200·155 = 56500, alpha = round(56500 / 255) = 222;
        // red = round((250·100·255 + 100·200·155) / 56500) = round(167.7) = 168, green = round(82.3) = 82, blue = round(109.7) = 110.
        second[8..12].ShouldBe([168, 82, 110, 222]);
    }

    [Fact]
    public void OverAnEmptyCanvasGivesTheSourcePixelBackExactly()
    {
        var s = Rgba(8);
        var stream = Header(s, false, 1, 0)
            .Fctl(0, W, H, 0, 0, 1, 1, 0, 1)
            .Idat(Data(s, new Spec(0, 0, W, H, 0, 1, 5), false));
        var decoder = new PngAnimationDecoder(stream.End());
        Next(decoder, false).ShouldBe(Model(s, [new(0, 0, W, H, 0, 1, 5)])[0]);
    }

    [Fact]
    public void PreviousOnTheFirstFrameIsBackground()
    {
        // The default image is separate and the first frame a 2x2 rectangle with dispose Previous (an APNG whose default image is not
        // a frame): nothing precedes it, so it is cleared, and frame 1 shows the canvas without it.
        var s = Rgba(8);
        var frames = new Spec[] { new(1, 1, 2, 2, 2, 0, 7), new(4, 3, 2, 2, 0, 0, 8) };
        var file = Build(s, false, frames);
        var actual = DecodeAll(file, s);
        ShouldEqualModel(actual, Model(s, frames), "previous on frame 0");
        // Explicitly: frame 1 holds no trace of frame 0's rectangle.
        for (var y = 1; y < 3; y++)
        for (var x = 1; x < 3; x++)
            actual[1].Skip((y * W + x) * 4).Take(4).ShouldBe([0, 0, 0, 0]);
    }

    [Fact]
    public void WhenTheDefaultImageIsNotAFrameTheFramesAreTheFdatOnes()
    {
        var s = Rgba(8);
        var frames = new Spec[] { new(0, 0, 3, 2, 0, 0, 1, 5, 100), new(2, 2, 4, 3, 1, 0, 2, 7, 100) };
        var file = Build(s, false, frames);
        var decoder = new PngAnimationDecoder(file);
        decoder.DefaultImageIsFrame.ShouldBeFalse();
        decoder.FrameCount.ShouldBe(2);
        decoder.IsAnimated.ShouldBeTrue();
        // The default image, as every PNG reader sees it, is the IDAT picture.
        PngReader.Decode(file, TestContext.Current.CancellationToken).ToRgba8().Select(b => (int)b).ToArray().ShouldBe(Enumerable.Range(0, W * H).SelectMany(i => s.Rgba(99, i % W, i / W)).ToArray());
        SimdCodecs.TryDecode(file, out var still, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        still.Width.ShouldBe(W);
        ShouldEqualModel(DecodeAll(file, s), Model(s, frames), "fdAT frames");

        SimdCodecs.TryOpenFrames(file, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(2);
            reader.LoopCount.ShouldBe(0);
            SimdCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
            info.FrameCount.ShouldBe(2);
        }
    }

    [Fact]
    public void DelaysAndLoopCountComeFromTheFrameControlChunks()
    {
        var s = Rgba(8);
        Spec[] frames = [new(0, 0, W, H, 0, 0, 1, 3, 10), new(0, 0, 2, 2, 0, 0, 2, 5, 0), new(0, 0, 2, 2, 0, 0, 3, 0, 7), new(0, 0, 2, 2, 0, 0, 3, 65535, 1)];
        var decoder = new PngAnimationDecoder(Build(s, true, frames, plays: 3));
        decoder.LoopCount.ShouldBe(3);
        decoder.Frames[0].Delay.ShouldBe(TimeSpan.FromMilliseconds(300));
        decoder.Frames[1].Delay.ShouldBe(TimeSpan.FromMilliseconds(50)); // a denominator of 0 is 100
        decoder.Frames[1].DelayDenominator.ShouldBe(0);
        decoder.Frames[2].Delay.ShouldBe(TimeSpan.Zero);
        decoder.Frames[3].Delay.ShouldBe(TimeSpan.FromSeconds(65535));
    }

    [Fact]
    public void IndependentFramesAreTheOnesThatDoNotNeedTheFramesBefore()
    {
        var s = Rgba(8);
        Spec[] frames =
        [
            new(0, 0, W, H, 1, 0, 1),   // 0: the first; clears the whole canvas afterwards
            new(1, 1, 2, 2, 0, 0, 2),   // 1: follows a full-canvas Background: independent
            new(0, 0, W, H, 0, 1, 3),   // 2: a whole canvas but OVER with transparent pixels: not independent
            new(0, 0, W, H, 0, 0, 4),   // 3: a whole canvas replacing everything: independent
            new(1, 1, 2, 2, 2, 0, 5),   // 4: a rectangle
            new(0, 0, W, H, 2, 0, 6),   // 5: whole, SOURCE, but restored by Previous afterwards: not a start
            new(3, 3, 2, 2, 0, 0, 7),   // 6
        ];
        var decoder = new PngAnimationDecoder(Build(s, true, frames));
        decoder.Frames.Select(f => f.IsIndependent).ShouldBe([true, true, false, true, false, false, false]);
    }

    // ---------------------------------------------------------------- colour types

    [Theory]
    [MemberData(nameof(SourceNames))]
    public void EveryColourTypeAndDepthDecodesToItsRgba(string name)
    {
        var s = SourceNamed(name);
        Spec[] frames = [new(0, 0, W, H, 0, 0, 1), new(1, 1, 4, 3, 0, 1, 2), new(2, 2, 3, 2, 1, 0, 3), new(0, 3, 5, 2, 2, 1, 4)];
        var file = Build(s, true, frames);
        ShouldEqualModel(DecodeAll(file, s), Model(s, frames), name);

        // Through the facade reader, at the file's depth: 8-bit RGBA bytes, or 16-bit RGBA samples.
        SimdCodecs.TryOpenFrames(file, out var reader).ShouldBeTrue();
        using (reader)
        {
            var expected = Model(s, frames);
            for (var i = 0; i < frames.Length; i++)
            {
                reader.TryDecodeFrame(i, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
                image.SampleFormat.ShouldBe(s.Depth == 16 ? SampleFormat.UInt16 : SampleFormat.UInt8);
                var pixels = s.Depth == 16
                    ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.Pixels).ToArray().Select(v => (int)v).ToArray()
                    : image.Pixels.ToArray().Select(v => (int)v).ToArray();
                // The reader hands out RGB when nothing is transparent; compare the colour channels then.
                if (image.Channels == 3)
                    pixels = [.. pixels.Chunk(3).SelectMany(p => new[] { p[0], p[1], p[2], s.Max })];
                pixels.ShouldBe(expected[i], $"{name} frame {i} through the facade");
            }
        }
    }

    [Theory]
    [InlineData("rgba8")]
    [InlineData("rgba16")]
    [InlineData("rgb8")]
    [InlineData("grey16-key")]
    [InlineData("palette8-trns")]
    public void AdamSevenFramesDecodeAsTheNonInterlacedOnes(string name)
    {
        var s = SourceNamed(name);
        // Rectangles whose sizes make the passes uneven, including ones small enough to miss whole passes.
        Spec[] frames = [new(0, 0, W, H, 0, 0, 1), new(1, 1, 5, 4, 0, 1, 2), new(3, 0, 1, 5, 1, 0, 3), new(0, 4, 6, 1, 0, 1, 4), new(2, 2, 2, 2, 0, 0, 5)];
        var interlaced = Build(s, true, frames, interlaced: true);
        var plain = Build(s, true, frames);
        var expected = Model(s, frames);
        ShouldEqualModel(DecodeAll(interlaced, s), expected, "interlaced");
        ShouldEqualModel(DecodeAll(plain, s), expected, "plain");
    }

    // ---------------------------------------------------------------- malformed

    private static ApngStream Basic(Source s, uint frames) => Header(s, false, frames, 0);

    private static byte[] Good(Source s, int index) => Data(s, new Spec(0, 0, 2, 2, 0, 0, index), false);

    private static byte[] Whole(Source s) => Data(s, new Spec(0, 0, W, H, 0, 0, 1), false);

    public static TheoryData<string> Malformed() =>
    [
        "sequence skips", "sequence repeats", "first sequence not zero", "fdat sequence wrong", "rect past right edge", "rect past bottom edge",
        "zero width", "zero height", "huge offset", "bad dispose", "bad blend", "frame count too high", "frame count too low", "zero frame count",
        "missing fdat", "fdat before idat", "fdat without fctl", "fdat shorter than sequence", "default fctl not whole canvas", "default fctl offset",
        "two fctl before idat", "double actl", "short actl", "short fctl", "fctl count above acTL",
    ];

    private static byte[] Build(string kind)
    {
        var s = Rgba(8);
        switch (kind)
        {
            case "sequence skips":
                return Basic(s, 2).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).Fctl(3, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(4, Good(s, 2)).End();
            case "sequence repeats":
                return Basic(s, 2).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).Fctl(1, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(2, Good(s, 2)).End();
            case "first sequence not zero":
                return Basic(s, 1).Idat(Whole(s)).Fctl(1, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(2, Good(s, 1)).End();
            case "fdat sequence wrong":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(5, Good(s, 1)).End();
            case "rect past right edge":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, W - 1, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
            case "rect past bottom edge":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0, H - 1, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
            case "zero width":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 0, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
            case "zero height":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 0, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
            case "huge offset":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0xFFFFFFFF, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
            case "bad dispose":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 3, 0).Fdat(1, Good(s, 1)).End();
            case "bad blend":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 2).Fdat(1, Good(s, 1)).End();
            case "frame count too high":
                return Basic(s, 3).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
            case "frame count too low":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).Fctl(2, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(3, Good(s, 2)).End();
            case "zero frame count":
                return Basic(s, 0).Idat(Whole(s)).End();
            case "missing fdat":
                return Basic(s, 2).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).Fctl(2, 2, 2, 0, 0, 1, 1, 0, 0).End();
            case "fdat before idat":
                return Basic(s, 1).Fdat(0, Good(s, 1)).Idat(Whole(s)).End();
            case "fdat without fctl":
                return Basic(s, 1).Idat(Whole(s)).Fdat(1, Good(s, 1)).End();
            case "fdat shorter than sequence":
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Raw("fdAT"u8, [0, 0]).End();
            case "default fctl not whole canvas":
                return Basic(s, 1).Fctl(0, W - 1, H, 0, 0, 1, 1, 0, 0).Idat(Whole(s)).End();
            case "default fctl offset":
                return Basic(s, 1).Fctl(0, W, H, 1, 0, 1, 1, 0, 0).Idat(Whole(s)).End();
            case "two fctl before idat":
                return Basic(s, 2).Fctl(0, W, H, 0, 0, 1, 1, 0, 0).Fctl(1, W, H, 0, 0, 1, 1, 0, 0).Idat(Whole(s)).End();
            case "double actl":
                return Basic(s, 1).Actl(1, 0).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
            case "short actl":
                return new ApngStream(W, H, 8, 6).Raw("acTL"u8, [0, 0, 0, 1]).Idat(Whole(s)).End();
            case "short fctl":
                return Basic(s, 1).Idat(Whole(s)).Raw("fcTL"u8, new byte[25]).Fdat(1, Good(s, 1)).End();
            default: // fctl count above acTL
                return Basic(s, 1).Idat(Whole(s)).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).Fctl(2, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(3, Good(s, 2)).End();
        }
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedAnimationChunksAreInvalidData(string kind)
    {
        var file = Build(kind);
        Should.Throw<InvalidDataException>(() => new PngAnimationDecoder(file), kind);
        SimdCodecs.TryOpenFrames(file, out _).ShouldBeFalse(kind);
    }

    [Fact]
    public void ATruncatedFdatIsAFailedFrameNotAHang()
    {
        var s = Rgba(8);
        var whole = Data(s, new Spec(0, 0, 3, 3, 0, 0, 1), false);
        var file = Basic(s, 2).Fctl(0, W, H, 0, 0, 1, 1, 0, 0).Idat(Whole(s)).Fctl(1, 3, 3, 0, 0, 1, 1, 0, 0).Fdat(2, whole[..(whole.Length / 2)]).End();
        var decoder = new PngAnimationDecoder(file);
        Next(decoder, false);
        Should.Throw<InvalidDataException>(() => decoder.TryDecodeNextFrame(new byte[W * H * 4], out _));

        SimdCodecs.TryOpenFrames(file, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.TryDecodeFrame(0, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            reader.TryDecodeFrame(1, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
            // The reader is still usable.
            reader.TryDecodeFrame(0, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        }
    }

    [Fact]
    public void AFrameThatCouldNotInflateToItsSizeIsRefusedBeforeAnythingIsAllocated()
    {
        // A 2000x2000 grey canvas: the default image is a genuine all-zero picture (a few kilobytes of deflate), and the second frame
        // claims the whole canvas again with 40 bytes of data, far under the 1032:1 ratio deflate can reach.
        var s = Grey(8);
        var stream = new ApngStream(2000, 2000, 8, 0).Actl(2, 0)
            .Fctl(0, 2000, 2000, 0, 0, 1, 1, 0, 0)
            .Idat(ApngStream.Zlib(new byte[2000 * 2001], System.IO.Compression.CompressionLevel.SmallestSize))
            .Fctl(1, 2000, 2000, 0, 0, 1, 1, 0, 0)
            .Fdat(2, new byte[40]);
        var ex = Should.Throw<InvalidDataException>(() => new PngAnimationDecoder(stream.End()));
        ex.Message.ShouldContain("cannot inflate");
        _ = s;
    }

    [Fact]
    public void APlainPngIsAOneFrameAnimation()
    {
        // A PNG without an acTL is a one-frame animation of its still image.
        var png = Png.PngWriter.EncodeRgb8(new byte[W * H * 3], W, H);
        var decoder = new PngAnimationDecoder(png);
        decoder.IsAnimated.ShouldBeFalse();
        decoder.FrameCount.ShouldBe(1);
        decoder.DefaultImageIsFrame.ShouldBeTrue();
        decoder.HasAlpha.ShouldBeFalse();
    }

    [Fact]
    public void AnAcTLAfterTheImageDataIsNotAnAnimation()
    {
        var s = Rgba(8);
        var file = new ApngStream(W, H, 8, 6).Idat(Whole(s)).Actl(1, 0).Fctl(0, 2, 2, 0, 0, 1, 1, 0, 0).Fdat(1, Good(s, 1)).End();
        var decoder = new PngAnimationDecoder(file);
        decoder.IsAnimated.ShouldBeFalse();
        decoder.FrameCount.ShouldBe(1);
    }

    [Fact]
    public void CancellingStopsTheDecode()
    {
        var s = Rgba(8);
        var file = Build(s, true, FourFrames(0, 0, 0, 0, 0));
        var decoder = new PngAnimationDecoder(file);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Should.Throw<OperationCanceledException>(() => decoder.TryDecodeNextFrame(new byte[W * H * 4], out _, cts.Token));
        decoder.NextFrameIndex.ShouldBe(0);
    }

    [Fact]
    public void AnOpaqueAnimationHasNoAlphaUntilARegionIsCleared()
    {
        var rgb = Rgb(8);
        Spec[] opaque = [new(0, 0, W, H, 0, 0, 1), new(1, 1, 2, 2, 2, 0, 2), new(0, 0, 3, 3, 0, 0, 3)];
        new PngAnimationDecoder(Build(rgb, true, opaque)).HasAlpha.ShouldBeFalse();
        Spec[] cleared = [new(0, 0, W, H, 1, 0, 1), new(1, 1, 2, 2, 0, 0, 2)];
        new PngAnimationDecoder(Build(rgb, true, cleared)).HasAlpha.ShouldBeTrue();
        Spec[] partial = [new(0, 0, W, H, 0, 0, 1)];
        new PngAnimationDecoder(Build(Rgba(8), true, partial)).HasAlpha.ShouldBeTrue();
    }
}
