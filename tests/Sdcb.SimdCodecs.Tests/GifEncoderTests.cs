using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Gif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The GIF encoder, read back by ImageMagick (an independent LZW decoder and compositor) and by our own
/// decoder: an image whose colours fit a palette comes back exactly, in every channel count and through
/// every code width and table clear; a quantised one keeps its structure; animations come back frame for
/// frame, with their timing and loop count. Quality targets for the quantiser are a later, separate gate.
/// </summary>
public sealed class GifEncoderTests
{
    // RGBA pixels drawn from `colours` random colours, some transparent when asked.
    private static byte[] PaletteRgba(int width, int height, int colours, int seed, bool transparent = false)
    {
        var random = new Random(seed);
        var palette = new byte[colours * 4];
        random.NextBytes(palette);
        for (var c = 0; c < colours; c++) palette[c * 4 + 3] = (byte)(transparent && c % 4 == 0 ? random.Next(128) : 128 + random.Next(128));
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++) palette.AsSpan(random.Next(colours) * 4, 4).CopyTo(rgba.AsSpan(i * 4));
        return rgba;
    }

    // What the encoder promises for RGBA input: alpha below 128 is transparent (alpha 0, colour immaterial),
    // the rest opaque with its colour.
    private static byte[] AsGifSees(byte[] rgba)
    {
        var expected = (byte[])rgba.Clone();
        for (var i = 0; i < expected.Length; i += 4)
        {
            if (expected[i + 3] < 128) expected.AsSpan(i, 4).Clear();
            else expected[i + 3] = 255;
        }
        return expected;
    }

    private static void BothDecodersShouldSee(byte[] gif, IReadOnlyList<byte[]> expected, string because)
    {
        var magick = GifDecoderTests.MagickFrames(gif);
        var ours = GifDecoderTests.OurFrames(gif);
        magick.Count.ShouldBe(expected.Count, because);
        ours.Count.ShouldBe(expected.Count, because);
        for (var i = 0; i < expected.Count; i++)
        {
            GifDecoderTests.ShouldMatch(magick[i], expected[i], $"{because}: ImageMagick, frame {i}");
            GifDecoderTests.ShouldMatch(ours[i], expected[i], $"{because}: ours, frame {i}");
        }
    }

    public static TheoryData<int, int, int, bool> LosslessStills() => new()
    {
        { 1, 1, 1, false },
        { 1, 1, 1, true },
        { 5, 3, 2, false },     // a 2-entry table, minimum code size 2
        { 37, 19, 4, false },
        { 37, 19, 17, true },
        { 64, 64, 255, true },  // 255 colours beside the transparent index: still exact
        { 64, 64, 256, false }, // all 256 entries
        { 512, 384, 256, false }, // noise: every code width, many table clears
        { 333, 251, 129, true },
    };

    [Theory]
    [MemberData(nameof(LosslessStills))]
    public void StillsWhoseColoursFitAreLossless(int width, int height, int colours, bool transparent)
    {
        var rgba = PaletteRgba(width, height, colours, seed: width + height + colours, transparent);
        var gif = GifEncoder.Encode(rgba, width, height, 4);
        BothDecodersShouldSee(gif, [AsGifSees(rgba)], $"{width}x{height}, {colours} colours");
        SimdCodecs.TryReadInfo(gif, out var info).ShouldBeTrue();
        info.Channels.ShouldBe(AsGifSees(rgba).Where((_, i) => i % 4 == 3).Any(a => a == 0) ? 4 : 3);
    }

    [Fact]
    public void AFullPaletteBesideTransparencyIsQuantisedToMakeRoom()
    {
        // 256 opaque colours and transparent pixels: 257 entries, one too many. The colours are quantised to
        // at most 255 (fewer here: Wu's 32-level histogram puts some of them in one bin) so the transparent
        // index still has its own entry, and transparency survives exactly.
        var random = new Random(256);
        var rgba = new byte[32 * 16 * 4];
        for (var i = 0; i < 32 * 16; i++)
        {
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2]) = ((byte)i, (byte)(i * 3), (byte)(i * 7)); // 256 distinct (i < 256)
            rgba[4 * i + 3] = i >= 256 && random.Next(3) == 0 ? (byte)0 : (byte)255;
        }
        var gif = GifEncoder.Encode(rgba, 32, 16, 4);
        var frame = GifContainer.Read(gif).Frames.Single();
        frame.TransparentIndex.ShouldBeInRange(1, 255);
        var colours = GifQuantizerColours(gif);
        colours.ShouldBeLessThanOrEqualTo(255);
        frame.TransparentIndex.ShouldBe(colours, "the entry after the colours");

        var expected = AsGifSees(rgba);
        foreach (var decoded in new[] { GifDecoderTests.MagickFrames(gif).Single(), GifDecoderTests.OurFrames(gif).Single() })
        {
            for (var i = 3; i < decoded.Length; i += 4)
                decoded[i].ShouldBe(expected[i], $"alpha of pixel {i / 4}");
        }
    }

    // The colours an encoded still uses: the distinct opaque colours both decoders see.
    private static int GifQuantizerColours(byte[] gif)
    {
        var frame = GifDecoderTests.OurFrames(gif).Single();
        var set = new HashSet<int>();
        for (var i = 0; i < frame.Length; i += 4)
            if (frame[i + 3] == 255) set.Add(frame[i] << 16 | frame[i + 1] << 8 | frame[i + 2]);
        return set.Count;
    }

    /// <summary>
    /// Reads an encoded image's code stream strictly, at exactly the widths a decoder uses: it must start with
    /// a clear code, end with the end-of-information code, and leave nothing after it but the padding of its
    /// last byte. (Lenient decoders, ours and ImageMagick's, stop once the pixels are full and never read the
    /// end code, so they do not check this.) Returns the codes read.
    /// </summary>
    internal static List<int> StrictCodes(byte[] gif, int frame = 0)
    {
        var f = GifContainer.Read(gif).Frames[frame];
        var data = GifLzw.GatherSubBlocks(gif, f.DataOffset, out var length);
        int clear = 1 << f.MinCodeSize, end = clear + 1, size = f.MinCodeSize + 1, next = clear + 2;
        var first = true;
        var codes = new List<int>();
        long bit = 0;
        while (true)
        {
            (bit + size).ShouldBeLessThanOrEqualTo(length * 8L, "the code stream ran out before its end code");
            var code = 0;
            for (var k = 0; k < size; k++, bit++)
                code |= ((data[(int)(bit >> 3)] >> (int)(bit & 7)) & 1) << k;
            codes.Add(code);
            if (code == end) break;
            if (code == clear)
            {
                (size, next, first) = (f.MinCodeSize + 1, clear + 2, true);
                continue;
            }
            code.ShouldBeLessThanOrEqualTo(next, "a code past the table");
            if (first)
            {
                first = false;
                continue;
            }
            if (next < 4096)
            {
                next++;
                if (next == 1 << size && size < 12) size++;
            }
        }
        codes[0].ShouldBe(clear, "the stream starts with a clear code");
        (length * 8L - bit).ShouldBeLessThan(8, "bytes left after the end code");
        System.Buffers.ArrayPool<byte>.Shared.Return(data);
        return codes;
    }

    [Theory]
    [MemberData(nameof(LosslessStills))]
    public void TheCodeStreamEndsExactlyWithItsEndCode(int width, int height, int colours, bool transparent)
    {
        var rgba = PaletteRgba(width, height, colours, seed: width + height + colours, transparent);
        var codes = StrictCodes(GifEncoder.Encode(rgba, width, height, 4));
        if (width * height > 100_000)
            codes.Count(c => c == codes[0]).ShouldBeGreaterThan(5, "a large noise image clears its table repeatedly");
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2 })]
    [InlineData(new byte[] { 0, 1, 2, 3, 0, 2, 1, 3, 1, 0, 3 })]
    public void AnEndCodeOnAWidthBoundaryIsWidened(byte[] pixels)
    {
        // Pixels whose adjacent pairs are all distinct, at minimum code size 2: one literal code each. After
        // the 3rd code the decoder's next free code is 8 = 2^3, after the 11th 16 = 2^4, so the end code that
        // follows is one bit wider than the codes before it. With 11 codes the stream is 3 + 3*3 + 8*4 = 44
        // bits before the end code: a 4-bit end code would fill the 6th byte exactly and leave the decoder's
        // 5th bit missing, so only a 5-bit one reads back.
        var colours = new byte[] { 10, 60, 120, 200 };
        var rgb = pixels.SelectMany(p => new[] { colours[p], (byte)0, (byte)0 }).ToArray();
        var gif = GifEncoder.Encode(rgb, pixels.Length, 1, 3);
        GifContainer.Read(gif).Frames.Single().MinCodeSize.ShouldBe(2);
        var codes = StrictCodes(gif);
        codes.Count.ShouldBe(pixels.Length + 2, "clear, one literal per pixel, end");
        codes[^1].ShouldBe(5);
        GifDecoderTests.OurFrames(gif).Single().Where((_, i) => i % 4 == 0).ShouldBe(pixels.Select(p => colours[p]));
    }

    [Fact]
    public void GreyAndRgbInputAreOpaque()
    {
        var random = new Random(7);
        var grey = new byte[23 * 9];
        random.NextBytes(grey);
        var fromGrey = GifEncoder.Encode(grey, 23, 9, 1);
        BothDecodersShouldSee(fromGrey, [[.. grey.SelectMany(v => new[] { v, v, v, (byte)255 })]], "grey");

        var rgb = PaletteRgba(23, 9, 30, seed: 8).Where((_, i) => i % 4 != 3).ToArray();
        var fromRgb = GifEncoder.Encode(rgb, 23, 9, 3);
        BothDecodersShouldSee(fromRgb, [[.. rgb.Chunk(3).SelectMany(p => new[] { p[0], p[1], p[2], (byte)255 })]], "rgb");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyColoursAreQuantisedToOnePalette(bool dither)
    {
        // A smooth two-axis gradient: 64k colours, far more than a palette holds.
        const int width = 256, height = 160;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)x, (byte)(y * 255 / (height - 1)), (byte)((x + y) / 2), 255);
        }

        var gif = GifEncoder.Encode(rgba, width, height, 4, new GifEncodeOptions { Dither = dither });
        var container = GifContainer.Read(gif);
        container.Frames.Single().PaletteCount.ShouldBe(256);

        // ImageMagick reads what we read.
        var ours = GifDecoderTests.OurFrames(gif).Single();
        GifDecoderTests.ShouldMatch(GifDecoderTests.MagickFrames(gif).Single(), ours, "ImageMagick");
        // Close to the source: a loose bound, not the quality gate (that comes with the benchmark work).
        var psnr = Psnr(rgba, ours);
        psnr.ShouldBeGreaterThan(dither ? 30 : 33, $"PSNR {psnr:F1} dB");
        GifEncoder.Encode(rgba, width, height, 4, new GifEncodeOptions { Dither = dither }).ShouldBe(gif, "deterministic");
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        long n = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (i % 4 == 3) continue;
            double d = a[i] - b[i];
            sum += d * d;
            n++;
        }
        return 10 * Math.Log10(255.0 * 255 / Math.Max(sum / n, 1e-9));
    }

    // Opaque frames that change in small areas: a moving square over a fixed background.
    private static List<byte[]> MovingSquare(int width, int height, int frames, bool transparentBackground = false)
    {
        var background = PaletteRgba(width, height, 12, seed: 3);
        if (transparentBackground)
            for (var i = 0; i < background.Length; i += 4) background[i + 3] = (byte)(i % 12 == 0 ? 0 : 255);
        var result = new List<byte[]>();
        for (var f = 0; f < frames; f++)
        {
            var frame = (byte[])background.Clone();
            for (var y = 4; y < 12; y++)
            for (var x = 3 * f; x < 3 * f + 8 && x < width; x++)
                (frame[(y * width + x) * 4], frame[(y * width + x) * 4 + 1], frame[(y * width + x) * 4 + 2], frame[(y * width + x) * 4 + 3]) = (250, 10, 10, 255);
            result.Add(frame);
        }
        return result;
    }

    [Fact]
    public void AnOpaqueAnimationWritesOnlyWhatChanged()
    {
        var frames = MovingSquare(40, 20, 5);
        frames.Add(frames[^1]); // an unchanged frame
        var gif = GifEncoder.EncodeAnimation([.. frames.Select(f => new GifAnimationFrame(f, TimeSpan.FromMilliseconds(70)))], 40, 20, 4);

        BothDecodersShouldSee(gif, [.. frames.Select(AsGifSees)], "moving square");
        var stored = GifContainer.Read(gif).Frames;
        stored[0].Width.ShouldBe(40);
        stored.Skip(1).Take(4).ShouldAllBe(f => f.Width < 40 && f.Height <= 8 && f.TransparentIndex >= 0);
        (stored[^1].Width, stored[^1].Height).ShouldBe((1, 1));
        stored.ShouldAllBe(f => f.Disposal == GifDisposal.Keep);
    }

    [Fact]
    public void AnAnimationWithTransparencyClearsEachFrame()
    {
        var frames = MovingSquare(30, 16, 4, transparentBackground: true);
        // A pixel opaque in one frame and transparent in the next must become transparent again.
        frames[1][3] = 255;
        frames[2][3] = 0;
        var gif = GifEncoder.EncodeAnimation([.. frames.Select(f => new GifAnimationFrame(f, TimeSpan.FromMilliseconds(40)))], 30, 16, 4);

        BothDecodersShouldSee(gif, [.. frames.Select(AsGifSees)], "transparent animation");
        GifContainer.Read(gif).Frames.ShouldAllBe(f => f.Disposal == GifDisposal.Background && f.Width == 30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void TimingAndLoopCountRoundTrip(int plays)
    {
        var frames = MovingSquare(24, 16, 3);
        int[] milliseconds = [0, 120, 1000];
        var gif = GifEncoder.EncodeAnimation(
            [.. frames.Select((f, i) => new GifAnimationFrame(f, TimeSpan.FromMilliseconds(milliseconds[i])))], 24, 16, 4,
            new GifEncodeOptions { LoopCount = plays });

        SimdCodecs.TryOpenFrames(gif, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.LoopCount.ShouldBe(plays);
            for (var i = 0; i < 3; i++)
            {
                reader.TryGetFrameInfo(i, out var frame).ShouldBeTrue();
                frame.Duration.ShouldBe(TimeSpan.FromMilliseconds(milliseconds[i]));
            }
        }
        using var magick = new MagickImageCollection(gif);
        magick.Select(f => (int)f.AnimationDelay).ShouldBe([0, 12, 100]);
        ((int)magick[0].AnimationIterations).ShouldBe(plays); // ImageMagick reads the count as plays too
    }

    [Fact]
    public void AFrameReaderRoundTripIsLossless()
    {
        // Decode an ImageMagick animation with our reader, encode the frames, read them back: the same.
        var source = GifDecoderTests.Animation("mixed");
        var frames = GifDecoderTests.OurFrames(source);
        SimdCodecs.TryOpenFrames(source, out var reader).ShouldBeTrue();
        var durations = new List<TimeSpan>();
        using (reader)
        {
            for (var i = 0; i < reader.FrameCount; i++)
            {
                reader.TryGetFrameInfo(i, out var info).ShouldBeTrue();
                durations.Add(info.Duration!.Value);
            }
        }
        var gif = GifEncoder.EncodeAnimation([.. frames.Select((f, i) => new GifAnimationFrame(f, durations[i]))], 40, 30, 4);
        BothDecodersShouldSee(gif, frames, "re-encoded");
    }

    [Fact]
    public void BadArgumentsAreRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => GifEncoder.Encode(new byte[4], 0, 1, 4));
        Should.Throw<ArgumentOutOfRangeException>(() => GifEncoder.Encode(new byte[4], 1, 1, 2));
        Should.Throw<ArgumentException>(() => GifEncoder.Encode(new byte[11], 2, 2, 3));
        Should.Throw<ArgumentException>(() => GifEncoder.EncodeAnimation([], 2, 2, 4));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            GifEncoder.EncodeAnimation([new GifAnimationFrame(new byte[16], TimeSpan.FromMilliseconds(-1))], 2, 2, 4));
    }
}
