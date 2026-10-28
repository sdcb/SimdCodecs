using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Gif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// GIF decoding against ImageMagick: files ImageMagick writes (stills of every size class, interlaced,
/// transparent, and animations with offsets, every disposal method and delays) decode to exactly the frames
/// ImageMagick's own coalesce composites from the same bytes. Where a pixel is transparent only alpha is
/// compared: ImageMagick keeps the transparent palette entry's colour under alpha 0, the decoder the canvas.
/// The decisions the GIF89a specification leaves open are pinned by <see cref="GifDecisionTests"/>.
/// </summary>
public sealed class GifDecoderTests
{
    // An image of `colors` distinct colours (at most 256, so ImageMagick writes them exactly), some of them
    // transparent when `transparent` is set.
    internal static MagickImage PaletteImage(int width, int height, int colors, int seed, bool transparent = false)
    {
        var random = new Random(seed);
        var palette = new byte[colors * 4];
        random.NextBytes(palette);
        for (var c = 0; c < colors; c++)
            palette[c * 4 + 3] = (byte)(transparent && c % 5 == 0 ? 0 : 255);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
            palette.AsSpan(random.Next(colors) * 4, 4).CopyTo(pixels.AsSpan(i * 4));
        // A transparent pixel's colour is immaterial; make them all the same so they share one index.
        for (var i = 0; i < width * height; i++)
            if (pixels[i * 4 + 3] == 0) pixels.AsSpan(i * 4, 4).Clear();
        var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGBA"));
        return image;
    }

    // ImageMagick's composited frames of a GIF, as RGBA.
    internal static List<byte[]> MagickFrames(byte[] gif)
    {
        using var frames = new MagickImageCollection(gif);
        frames.Coalesce();
        return [.. frames.Select(f =>
        {
            f.Depth = 8;
            return f.ToByteArray(MagickFormat.Rgba);
        })];
    }

    internal static void ShouldMatch(byte[] actual, byte[] expected, string because)
    {
        actual.Length.ShouldBe(expected.Length, because);
        for (var i = 0; i < actual.Length; i += 4)
        {
            if (expected[i + 3] == 0 && actual[i + 3] == 0) continue;
            if (!actual.AsSpan(i, 4).SequenceEqual(expected.AsSpan(i, 4)))
                throw new ShouldAssertException($"{because}: pixel {i / 4} is {Hex(actual, i)}, ImageMagick has {Hex(expected, i)}");
        }

        static string Hex(byte[] p, int i) => Convert.ToHexString(p, i, 4);
    }

    // Every frame through the facade's reader, in order, as RGBA.
    internal static List<byte[]> OurFrames(byte[] gif)
    {
        SimdCodecs.TryOpenFrames(gif, out var reader).ShouldBeTrue();
        using (reader)
        {
            SimdCodecs.TryReadInfo(gif, out var info).ShouldBeTrue();
            var frames = new List<byte[]>();
            for (var i = 0; i < reader.FrameCount; i++)
            {
                var rgba = new byte[info.Width * info.Height * 4];
                reader.TryDecodeFrameInto(i, rgba, 0, ImagePixelFormat.Rgba32).ShouldBeTrue($"frame {i}");
                frames.Add(rgba);
            }
            return frames;
        }
    }

    public static TheoryData<int, int, int, bool, bool> Stills() => new()
    {
        { 1, 1, 2, false, false },
        { 7, 3, 4, false, false },
        { 37, 19, 17, false, false },
        { 37, 19, 17, true, false },
        { 64, 64, 256, false, false },
        { 301, 203, 256, false, false },  // enough noise to fill the code table and force clears
        { 301, 203, 256, true, false },
        { 37, 19, 17, false, true },      // interlaced
        { 9, 13, 3, true, true },
        { 120, 97, 256, false, true },
    };

    [Theory]
    [MemberData(nameof(Stills))]
    public void StillsAreImageMagicks(int width, int height, int colors, bool transparent, bool interlaced)
    {
        using var image = PaletteImage(width, height, colors, seed: width * 31 + height, transparent);
        if (interlaced) image.Settings.Interlace = Interlace.Gif;
        var gif = image.ToByteArray(MagickFormat.Gif);
        var container = GifContainer.Read(gif);
        container.Frames.Single().Interlaced.ShouldBe(interlaced, "fixture: ImageMagick wrote the interlace flag");

        var expected = MagickFrames(gif).Single();
        var ours = OurFrames(gif).Single();
        ShouldMatch(ours, expected, "still");

        SimdCodecs.TryReadInfo(gif, out var info).ShouldBeTrue();
        (info.Width, info.Height, info.FrameCount, info.BitDepth).ShouldBe((width, height, 1, 8));
        info.Channels.ShouldBe(container.Frames[0].TransparentIndex >= 0 ? 4 : 3);
        GifDecoder.Decode(gif).Rgba.ShouldBe(ours);
    }

    internal static readonly string[] AnimationKinds = ["offsets", "background", "previous", "mixed", "transparent-over", "full-frames"];

    public static TheoryData<string> Animations() => [.. AnimationKinds];

    // Animations ImageMagick writes as given: each frame a page of its own size at an offset, with its own
    // disposal method and delay.
    internal static byte[] Animation(string kind)
    {
        using var frames = new MagickImageCollection();
        var random = new Random(kind.Length);
        (int X, int Y, int W, int H, GifDisposeMethod Dispose, bool Transparent)[] plan = kind switch
        {
            "offsets" => [(0, 0, 40, 30, GifDisposeMethod.None, false), (5, 7, 10, 9, GifDisposeMethod.None, false), (31, 22, 9, 8, GifDisposeMethod.None, true)],
            "background" => [(0, 0, 40, 30, GifDisposeMethod.None, true), (3, 4, 20, 10, GifDisposeMethod.Background, false), (10, 10, 12, 12, GifDisposeMethod.Background, true), (0, 0, 8, 8, GifDisposeMethod.None, false)],
            "previous" => [(0, 0, 40, 30, GifDisposeMethod.None, false), (2, 2, 20, 20, GifDisposeMethod.Previous, false), (15, 5, 20, 20, GifDisposeMethod.Previous, true), (6, 6, 6, 6, GifDisposeMethod.None, false)],
            "mixed" => [(0, 0, 40, 30, GifDisposeMethod.Background, true), (1, 1, 30, 20, GifDisposeMethod.Previous, true), (9, 3, 25, 25, GifDisposeMethod.None, true), (0, 0, 40, 30, GifDisposeMethod.Previous, true), (20, 15, 20, 15, GifDisposeMethod.Background, false)],
            "transparent-over" => [(0, 0, 40, 30, GifDisposeMethod.None, false), (0, 0, 40, 30, GifDisposeMethod.None, true), (0, 0, 40, 30, GifDisposeMethod.None, true)],
            _ => [(0, 0, 40, 30, GifDisposeMethod.None, false), (0, 0, 40, 30, GifDisposeMethod.Background, false), (0, 0, 40, 30, GifDisposeMethod.None, false)],
        };
        foreach (var (x, y, w, h, dispose, transparent) in plan)
        {
            var frame = PaletteImage(w, h, 1 + random.Next(60), random.Next(), transparent);
            frame.Page = new MagickGeometry(x, y, (uint)w, (uint)h);
            frame.GifDisposeMethod = dispose;
            frame.AnimationDelay = (uint)random.Next(1, 30);
            frames.Add(frame);
        }
        frames[0].Page = new MagickGeometry(0, 0, 40, 30);
        frames[0].AnimationIterations = 3;
        return frames.ToByteArray(MagickFormat.Gif);
    }

    [Fact]
    public void TheAnimationFixturesCarryWhatTheyAreFor()
    {
        // ImageMagick writes the frames as given, not re-optimised: otherwise matching its coalesce would
        // not exercise disposal or offsets at all.
        GifDisposal[] Disposals(string kind) => [.. GifContainer.Read(Animation(kind)).Frames.Select(f => f.Disposal)];
        Disposals("background").ShouldBe([GifDisposal.Keep, GifDisposal.Background, GifDisposal.Background, GifDisposal.Keep]);
        Disposals("previous").ShouldBe([GifDisposal.Keep, GifDisposal.Previous, GifDisposal.Previous, GifDisposal.Keep]);
        Disposals("mixed").ShouldBe([GifDisposal.Background, GifDisposal.Previous, GifDisposal.Keep, GifDisposal.Previous, GifDisposal.Background]);

        var offsets = GifContainer.Read(Animation("offsets")).Frames;
        offsets.Select(f => (f.X, f.Y, f.Width, f.Height)).ShouldBe([(0, 0, 40, 30), (5, 7, 10, 9), (31, 22, 9, 8)]);
        offsets[2].TransparentIndex.ShouldBeGreaterThanOrEqualTo(0);
        GifContainer.Read(Animation("transparent-over")).Frames.Skip(1).ShouldAllBe(f => f.TransparentIndex >= 0);
    }

    [Theory]
    [MemberData(nameof(Animations))]
    public void AnimationsAreImageMagicksCoalescedFrames(string kind)
    {
        var gif = Animation(kind);
        var expected = MagickFrames(gif);
        var ours = OurFrames(gif);
        ours.Count.ShouldBe(expected.Count);
        for (var i = 0; i < ours.Count; i++)
            ShouldMatch(ours[i], expected[i], $"{kind} frame {i}");
    }

    [Theory]
    [MemberData(nameof(Animations))]
    public void FramesReadInAnyOrderAreTheSame(string kind)
    {
        var gif = Animation(kind);
        var inOrder = OurFrames(gif);
        SimdCodecs.TryOpenFrames(gif, out var reader).ShouldBeTrue();
        using (reader)
        {
            foreach (var i in Enumerable.Range(0, reader.FrameCount).Reverse().Concat([1, 1, 0, reader.FrameCount - 1]))
            {
                var rgba = new byte[inOrder[0].Length];
                reader.TryDecodeFrameInto(i, rgba, 0, ImagePixelFormat.Rgba32).ShouldBeTrue();
                rgba.ShouldBe(inOrder[i], $"frame {i}");
            }
        }
    }

    [Fact]
    public void TimingAndLoopCountAreAsStored()
    {
        var gif = Animation("mixed");
        using var magick = new MagickImageCollection(gif);
        SimdCodecs.TryOpenFrames(gif, out var reader).ShouldBeTrue();
        using (reader)
        {
            // Written as AnimationIterations 3: stored as 2 repetitions, which is 3 plays.
            reader.LoopCount.ShouldBe((int)magick[0].AnimationIterations);
            reader.LoopCount.ShouldBe(3);
            for (var i = 0; i < reader.FrameCount; i++)
            {
                reader.TryGetFrameInfo(i, out var frame).ShouldBeTrue();
                frame.Duration.ShouldBe(TimeSpan.FromMilliseconds(magick[i].AnimationDelay * 10));
            }
        }
        GifDecoder.ReadInfo(gif).ShouldBe(new GifInfo(40, 30, 5, true, 2));
    }
}
