using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// APNG decoding against ffmpeg, a black box that is not a source of ours (see <see cref="ApngOracle"/>): the committed files in
/// <c>Fixtures/png/apng</c> — written by ffmpeg's APNG encoder, so with the dispose and blend operations and sub-rectangles it picks —
/// decode to the frames whose MD5s ffmpeg's own decoder produced (<c>apng-golden.tsv</c>, which CI compares), and live, where ffmpeg is
/// installed, the same files, freshly encoded sequences in every pixel format its encoder takes, and hand-built streams decode to
/// exactly the frames ffmpeg decodes them to.
/// </summary>
public sealed class PngApngOracleTests
{
    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "png", "apng");

    public static TheoryData<string> Fixtures() => [.. Directory.GetFiles(FixtureDirectory, "*.apng").Select(f => Path.GetFileName(f)!).OrderBy(n => n)];

    /// <summary>Our frames of <paramref name="file"/>: the canvas RGBA after each, 8-bit bytes or 16-bit little-endian samples.</summary>
    internal static List<byte[]> OurFrames(byte[] file, out bool wide, out int width, out int height)
    {
        var decoder = new PngAnimationDecoder(file);
        wide = decoder.BitDepth == 16;
        width = decoder.CanvasWidth;
        height = decoder.CanvasHeight;
        var frames = new List<byte[]>();
        for (var i = 0; i < decoder.FrameCount; i++)
        {
            if (wide)
            {
                var samples = new ushort[width * height * 4];
                decoder.TryDecodeNextFrame(samples, out _, TestContext.Current.CancellationToken).ShouldBeTrue();
                frames.Add(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
            }
            else
            {
                var bytes = new byte[width * height * 4];
                decoder.TryDecodeNextFrame(bytes, out _, TestContext.Current.CancellationToken).ShouldBeTrue();
                frames.Add(bytes);
            }
        }
        return frames;
    }

    private static string Md5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

    private static void ShouldMatchFfmpeg(byte[] file, string ffmpeg, string what)
    {
        var ours = OurFrames(file, out var wide, out var width, out var height);
        var theirs = ApngOracle.DecodeFrames(ffmpeg, file, wide);
        var frameBytes = width * height * (wide ? 8 : 4);
        (theirs.Length / frameBytes).ShouldBe(ours.Count, $"{what}: frame count");
        for (var i = 0; i < ours.Count; i++)
            ours[i].ShouldBe(theirs.AsSpan(i * frameBytes, frameBytes).ToArray(), $"{what}: frame {i}");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixturesDecodeToTheFramesFfmpegDecoded(string fixture)
    {
        // apng-golden.tsv: fixture, frame, MD5 of that frame's RGBA (rgba64le for 16-bit files) from ffmpeg's decoder.
        var golden = File.ReadAllLines(Path.Combine(FixtureDirectory, "apng-golden.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).Where(p => p[0] == fixture)
            .ToDictionary(p => int.Parse(p[1]), p => p[2]);
        golden.ShouldNotBeEmpty($"no golden digests for {fixture}");
        var ours = OurFrames(File.ReadAllBytes(Path.Combine(FixtureDirectory, fixture)), out _, out _, out _);
        ours.Count.ShouldBe(golden.Count, fixture);
        for (var i = 0; i < ours.Count; i++)
            Md5(ours[i]).ShouldBe(golden[i], $"{fixture} frame {i}");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void LiveFfmpegDecodesTheFixturesLikeUs(string fixture)
    {
        var ffmpeg = ApngOracle.Require();
        ShouldMatchFfmpeg(File.ReadAllBytes(Path.Combine(FixtureDirectory, fixture)), ffmpeg, fixture);
    }

    // ---------------------------------------------------------------- a sequence of our own, encoded by ffmpeg

    /// <summary>A moving, partly transparent sprite over a transparent canvas, a static opaque stripe, and a pixel that blinks: 8-bit RGBA frames.</summary>
    internal static List<byte[]> Sequence(int width, int height, int frames, bool opaqueBackground)
    {
        var list = new List<byte[]>();
        for (var f = 0; f < frames; f++)
        {
            var px = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                if (opaqueBackground)
                    (px[o], px[o + 1], px[o + 2], px[o + 3]) = ((byte)(x * 255 / width), (byte)(y * 255 / height), 90, 255);
                if (y == height - 2)
                    (px[o], px[o + 1], px[o + 2], px[o + 3]) = (200, 30, 30, 255);
                var sx = x - (2 + 3 * f);
                var sy = y - (3 + f % 3);
                if (sx is >= 0 and < 7 && sy is >= 0 and < 6)
                    (px[o], px[o + 1], px[o + 2], px[o + 3]) = ((byte)(40 + sx * 30), (byte)(255 - sy * 40), (byte)(f * 20), sx == 0 ? (byte)255 : (byte)(sy < 3 ? 255 : 140));
            }
            list.Add(px);
        }
        return list;
    }

    private static byte[] ToWide(byte[] rgba8)
    {
        var wide = new ushort[rgba8.Length];
        for (var i = 0; i < rgba8.Length; i++)
            wide[i] = (ushort)(rgba8[i] * 257 + (i % 5 == 0 ? 3 : 0));
        return MemoryMarshal.AsBytes(wide.AsSpan()).ToArray();
    }

    public static TheoryData<string, bool, int> EncodedFormats() => new()
    {
        { "rgba", true, 0 }, { "rgba", false, 3 }, { "rgb24", true, 0 }, { "pal8", false, 0 }, { "pal8", true, 2 }, { "gray", true, 0 }, { "ya8", false, 0 },
        { "rgba64be", false, 0 }, { "rgba64be", true, 1 }, { "rgb48be", true, 0 }, { "gray16be", true, 0 }, { "ya16be", false, 0 },
    };

    [Theory]
    [MemberData(nameof(EncodedFormats))]
    public void FfmpegEncodedSequencesDecodeToWhatFfmpegDecodes(string pixelFormat, bool opaqueBackground, int plays)
    {
        var ffmpeg = ApngOracle.Require();
        const int w = 40, h = 24;
        var wideInput = pixelFormat.Contains("64") || pixelFormat.Contains("48") || pixelFormat.Contains("16");
        var frames = Sequence(w, h, 7, opaqueBackground);
        var raw = wideInput ? frames.SelectMany(ToWide).ToArray() : frames.SelectMany(f => f).ToArray();
        var file = ApngOracle.Encode(ffmpeg, raw, w, h, 10, plays, wideInput ? "rgba64le" : "rgba", pixelFormat);

        var decoder = new PngAnimationDecoder(file);
        decoder.LoopCount.ShouldBe(plays);
        decoder.FrameCount.ShouldBe(7);
        decoder.BitDepth.ShouldBe(wideInput ? 16 : 8);
        ShouldMatchFfmpeg(file, ffmpeg, $"{pixelFormat} plays {plays}");
    }

    // ---------------------------------------------------------------- hand-built streams

    public static TheoryData<byte, byte, byte, byte, byte, bool> HandBuilt()
    {
        var data = new TheoryData<byte, byte, byte, byte, byte, bool>();
        foreach (byte d0 in new byte[] { 0, 1, 2 })
        foreach (byte d1 in new byte[] { 0, 1, 2 })
        foreach (byte d2 in new byte[] { 0, 1, 2 })
        foreach (byte b1 in new byte[] { 0, 1 })
        foreach (byte b2 in new byte[] { 0, 1 })
        foreach (var defaultIsFrame in new[] { true, false })
            data.Add(d0, d1, d2, b1, b2, defaultIsFrame);
        return data;
    }

    [Theory]
    [MemberData(nameof(HandBuilt))]
    public void FfmpegCompositesTheHandBuiltStreamsAsWeDo(byte d0, byte d1, byte d2, byte b1, byte b2, bool defaultIsFrame)
    {
        var ffmpeg = ApngOracle.Require();
        // Alpha is 0 or 255 only here: how a decoder rounds a partly transparent OVER is its own (see the next test).
        const int w = 6, h = 5;
        (int X, int Y, int W, int H, byte Dispose, byte Blend, int Seed)[] frames =
        [
            (0, 0, w, h, d0, 0, 1), (1, 1, 4, 3, d1, b1, 2), (0, 2, 3, 3, d2, b2, 3), (2, 0, 4, 4, 0, 1, 4),
        ];
        byte[] Pixel(int seed, int x, int y)
        {
            var on = (x + 2 * y + seed) % 3 != 0;
            return [(byte)(seed * 50 + x * 7), (byte)(y * 40 + seed), (byte)(x * y * 9), on ? (byte)255 : (byte)0];
        }
        var stream = new ApngStream(w, h, 8, 6).Actl(4, 0);
        uint seq = 0;
        for (var i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            var data = ApngStream.Rows(f.W, f.H, 4, (x, y) => Pixel(f.Seed, x, y));
            if (i == 0 && defaultIsFrame)
            {
                stream.Fctl(seq++, (uint)f.W, (uint)f.H, 0, 0, 1, 10, f.Dispose, f.Blend).Idat(data);
                continue;
            }
            if (i == 0)
                stream.Idat(ApngStream.Rows(w, h, 4, (x, y) => Pixel(99, x, y)));
            stream.Fctl(seq++, (uint)f.W, (uint)f.H, (uint)f.X, (uint)f.Y, 1, 10, f.Dispose, f.Blend).Fdat(seq++, data);
        }
        ShouldMatchFfmpeg(stream.End(), ffmpeg, $"dispose {d0}{d1}{d2} blend {b1}{b2} default-is-frame {defaultIsFrame}");
    }

    [Fact]
    public void FfmpegAndWeAgreeOnAPartlyTransparentOverUpToItsRounding()
    {
        var ffmpeg = ApngOracle.Require();
        // Every source alpha over every canvas alpha, for one colour pair: a canvas of the backdrop (alpha by row), then a canvas of the
        // source (alpha by column) drawn with OVER (a 16 x 16 grid of alpha pairs, 8-bit).
        const int w = 16, h = 16;
        var file = new ApngStream(w, h, 8, 6).Actl(2, 0)
            .Fctl(0, w, h, 0, 0, 1, 10, 0, 0)
            .Idat(ApngStream.Rows(w, h, 4, (x, y) => [200, 60, 20, (byte)(y * 17)]))
            .Fctl(1, w, h, 0, 0, 1, 10, 0, 1)
            .Fdat(2, ApngStream.Rows(w, h, 4, (x, y) => [30, 220, 90, (byte)(x * 17)]))
            .End();
        var ours = OurFrames(file, out _, out _, out _);
        var theirs = ApngOracle.DecodeFrames(ffmpeg, file, wide: false);
        ours[0].ShouldBe(theirs.AsSpan(0, w * h * 4).ToArray());

        // The specification gives OVER in real numbers and leaves the rounding of the integer samples to the decoder. ffmpeg 8.1.2
        // computes the colour in its own integer approximation; we round to nearest (see
        // PngAnimationDecoder). The alpha of the result is exactly the same, so are all pixels whose source is transparent or opaque or whose
        // backdrop is transparent, and no colour sample is more than 2 apart.
        var second = theirs.AsSpan(w * h * 4, w * h * 4);
        var different = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 4;
            ours[1][o + 3].ShouldBe(second[o + 3], $"alpha at {x},{y}");
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(ours[1][o + c] - second[o + c]);
                d.ShouldBeLessThanOrEqualTo(2, $"colour {c} at {x},{y}");
                if (d != 0)
                {
                    different++;
                    (x is > 0 and < w - 1 && y > 0).ShouldBeTrue($"{x},{y} should be exact: alpha 0 or max source, or an empty backdrop");
                }
            }
        }
        different.ShouldBeGreaterThan(0); // the two do round differently: this test notes it, and fails if ffmpeg ever agrees to the bit
    }
}