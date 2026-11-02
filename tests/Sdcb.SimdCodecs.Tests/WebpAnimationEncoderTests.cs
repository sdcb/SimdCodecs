using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The animation encoder against libwebp's <c>anim_dump</c> (a black box) and our animation decoder:
/// lossless output must reproduce every visible pixel and alpha value of every source frame at its time,
/// lossy output must decode alike in both decoders, and frames given as ANMF rectangles must composite as
/// the format describes (checked against an independent compositor written here).
/// </summary>
public sealed class WebpAnimationEncoderTests
{
    private const int Duration = 40;

    // ---------------------------------------------------------------- inputs

    /// <summary>Whole-canvas RGBA frames of one kind of motion.</summary>
    internal static List<byte[]> Sequence(string kind, int w, int h, int frames)
    {
        var list = new List<byte[]>();
        byte[] background = WebpLossyEncoderTests.Content("pattern", w, h, 11);
        for (int f = 0; f < frames; f++)
        {
            var rgba = new byte[w * h * 4];
            int cx = (int)(w * (0.2 + 0.6 * f / Math.Max(1, frames - 1))), cy = h / 2;
            int r = Math.Max(1, Math.Min(w, h) / 4);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = 4 * (y * w + x);
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    switch (kind)
                    {
                        case "sprite": // an opaque disc moving over a still pattern
                            if (d < r)
                            {
                                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(200 + f), 40, (byte)(x * 3), 255);
                            }
                            else
                            {
                                background.AsSpan(i, 4).CopyTo(rgba.AsSpan(i));
                            }

                            break;
                        case "alpha-sprite": // a soft-edged disc on transparency
                            double a = Math.Clamp((r - d) / 3 + 0.5, 0, 1);
                            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)(30 + x), (byte)(90 + f * 5), 160, (byte)Math.Round(a * 255));
                            break;
                        case "fade": // every pixel changes a little
                            for (int c = 0; c < 3; c++)
                            {
                                rgba[i + c] = (byte)(background[i + c] * (frames - f) / frames + (255 - background[i + c]) * f / frames);
                            }

                            rgba[i + 3] = 255;
                            break;
                        case "held": // frames 1-2 and 4-5 repeat the one before
                            int key = f is 1 or 2 ? 0 : f is 4 or 5 ? 3 : f;
                            background.AsSpan(i, 4).CopyTo(rgba.AsSpan(i));
                            rgba[i] = (byte)(rgba[i] + key * 37);
                            break;
                    }
                }
            }

            list.Add(rgba);
        }

        return list;
    }

    private static byte[] EncodeIncrementally(List<byte[]> frames, int w, int h, WebpAnimationEncodeOptions options)
    {
        var encoder = new WebpAnimationEncoder(w, h, options);
        foreach (byte[] frame in frames)
        {
            encoder.AddFrame(frame, ImagePixelFormat.Rgba32, TimeSpan.FromMilliseconds(Duration));
        }

        return encoder.Encode();
    }

    // ---------------------------------------------------------------- decoding

    /// <summary>Our decoder's canvases, one per encoded frame, with each frame's start time.</summary>
    private static List<(int Timestamp, byte[] Canvas)> DecodeOurs(byte[] webp)
    {
        var decoder = new WebpAnimationDecoder(webp);
        var list = new List<(int, byte[])>();
        var canvas = new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4];
        while (decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo info))
        {
            list.Add((info.TimestampMs, (byte[])canvas.Clone()));
        }

        return list;
    }

    private static byte[][] DecodeAnimDump(byte[] webp, int w, int h)
    {
        string animDump = WebpOracle.RequireAnimDump();
        string file = Path.Combine(Path.GetTempPath(), $"simdcodecs-anim-enc-{Guid.NewGuid():N}.webp");
        File.WriteAllBytes(file, webp);
        try
        {
            return WebpAnimationOracleTests.RunAnimDump(animDump, file, w, h);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Every alpha value equal, and the colour of every pixel visible in <paramref name="expected"/>.</summary>
    private static void ShouldBeVisiblyEqual(byte[] actual, byte[] expected, string because)
    {
        actual.Length.ShouldBe(expected.Length, because);
        for (int i = 0; i < expected.Length; i += 4)
        {
            if (actual[i + 3] != expected[i + 3] || (expected[i + 3] != 0 && !actual.AsSpan(i, 3).SequenceEqual(expected.AsSpan(i, 3))))
            {
                throw new ShouldAssertException($"{because}: pixel {i / 4} is {Convert.ToHexString(actual, i, 4)}, expected {Convert.ToHexString(expected, i, 4)}");
            }
        }
    }

    /// <summary>
    /// Decodes with both decoders, checks they agree frame by frame and that the timeline is the source's;
    /// returns the canvas shown at each source frame's time.
    /// </summary>
    private static List<byte[]> CheckDecoders(byte[] webp, int w, int h, int sourceFrames, string because)
    {
        List<(int Timestamp, byte[] Canvas)> ours = DecodeOurs(webp);
        byte[][] dumped = DecodeAnimDump(webp, w, h);
        dumped.Length.ShouldBe(ours.Count, because);
        for (int k = 0; k < ours.Count; k++)
        {
            ShouldBeVisiblyEqual(dumped[k], ours[k].Canvas, $"{because}: anim_dump frame {k}");
            (ours[k].Timestamp % Duration).ShouldBe(0, because);
        }

        WebpAnimation animation = WebpDecoder.DecodeAnimation(webp);
        animation.Frames.Sum(f => f.DurationMs).ShouldBe(sourceFrames * Duration, $"{because}: total duration");
        var shown = new List<byte[]>();
        for (int f = 0; f < sourceFrames; f++)
        {
            int at = ours.FindLastIndex(o => o.Timestamp <= f * Duration);
            shown.Add(ours[at].Canvas);
        }

        return shown;
    }

    // ---------------------------------------------------------------- whole-canvas frames

    public static TheoryData<string, WebpAnimationMode> Cases()
    {
        var data = new TheoryData<string, WebpAnimationMode>();
        foreach (string kind in new[] { "sprite", "alpha-sprite", "fade", "held" })
        {
            foreach (WebpAnimationMode mode in Enum.GetValues<WebpAnimationMode>())
            {
                data.Add(kind, mode);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Whole_canvas_frames_decode_alike_in_anim_dump_and_ours(string kind, WebpAnimationMode mode)
    {
        foreach ((int w, int h, int effortIndex) in new[] { (64, 40, 0), (33, 19, 1), (64, 40, 2), (1, 1, 1) })
        {
            int lossyEffort = new[] { 0, 4, 6 }[effortIndex], losslessEffort = new[] { 0, 6, 9 }[effortIndex];
            List<byte[]> frames = Sequence(kind, w, h, 7);
            var options = new WebpAnimationEncodeOptions { Mode = mode, LossyEffort = lossyEffort, LosslessEffort = losslessEffort, Quality = 80 };
            string because = $"{kind} {mode} {w}x{h} efforts {lossyEffort}/{losslessEffort}";
            byte[] webp = EncodeIncrementally(frames, w, h, options);
            List<byte[]> shown = CheckDecoders(webp, w, h, frames.Count, because);
            for (int f = 0; f < frames.Count; f++)
            {
                if (mode == WebpAnimationMode.Lossless)
                {
                    ShouldBeVisiblyEqual(shown[f], frames[f], $"{because}: frame {f}");
                }
                else
                {
                    // Lossy colour, exact alpha (AlphaQuality 100).
                    for (int i = 3; i < frames[f].Length; i += 4)
                    {
                        shown[f][i].ShouldBe(frames[f][i], $"{because}: frame {f} alpha {i / 4}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Unchanged_frames_lengthen_the_previous_one()
    {
        List<byte[]> frames = Sequence("held", 48, 32, 7);
        byte[] webp = EncodeIncrementally(frames, 48, 32, new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless });
        var decoder = new WebpAnimationDecoder(webp);
        decoder.FrameCount.ShouldBe(3);
        decoder.Frames.Select(f => f.DurationMs).ShouldBe([3 * Duration, 3 * Duration, Duration]);
    }

    [Fact]
    public void A_small_change_writes_a_small_rectangle()
    {
        List<byte[]> frames = Sequence("sprite", 200, 120, 4);
        byte[] webp = EncodeIncrementally(frames, 200, 120, new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless });
        var decoder = new WebpAnimationDecoder(webp);
        foreach (WebpFrameInfo f in decoder.Frames.Skip(1))
        {
            ((long)f.Width * f.Height).ShouldBeLessThan(200L * 120 / 2, $"frame {f.Index}");
            (f.X % 2).ShouldBe(0);
            (f.Y % 2).ShouldBe(0);
        }
    }

    [Fact]
    public void Max_keyframe_distance_bounds_the_frames_a_seek_decodes()
    {
        List<byte[]> frames = Sequence("sprite", 96, 64, 10);
        foreach (int distance in new[] { 1, 3 })
        {
            byte[] webp = EncodeIncrementally(frames, 96, 64,
                new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless, MaxKeyframeDistance = distance });
            var decoder = new WebpAnimationDecoder(webp);
            int since = 0;
            foreach (WebpFrameInfo f in decoder.Frames)
            {
                since = f.IsIndependent ? 0 : since + 1;
                since.ShouldBeLessThan(distance, $"distance {distance} frame {f.Index}");
            }

            // Seeking to any frame gives what decoding from the start gives.
            List<(int Timestamp, byte[] Canvas)> sequential = DecodeOurs(webp);
            var canvas = new byte[96 * 64 * 4];
            for (int k = decoder.FrameCount - 1; k >= 0; k--)
            {
                decoder.Seek(k);
                decoder.TryDecodeNextFrame(canvas, out _).ShouldBeTrue();
                canvas.ShouldBe(sequential[k].Canvas, $"distance {distance} seek {k}");
            }
        }

        var unbounded = new WebpAnimationDecoder(EncodeIncrementally(frames, 96, 64, new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless }));
        unbounded.Frames.Count(f => f.IsIndependent).ShouldBeLessThan(unbounded.FrameCount, "without a bound, sub-rectangles are used");
    }

    // ---------------------------------------------------------------- ANMF frames as given

    /// <summary>
    /// The four combinations of blending and disposal, from opaque and fully transparent pixels (the cases
    /// every decoder composites without rounding), against a compositor written from RFC 9649.
    /// </summary>
    [Fact]
    public void Frames_as_given_composite_as_the_format_describes()
    {
        const int w = 40, h = 30;
        foreach (bool lossless in new[] { true, false })
        {
            foreach (bool blend in new[] { false, true })
            {
                foreach (bool dispose in new[] { false, true })
                {
                    string because = $"lossless {lossless} blend {blend} dispose {dispose}";
                    var background = new byte[w * h * 4];
                    for (int i = 0; i < background.Length; i += 4)
                    {
                        (background[i], background[i + 1], background[i + 2], background[i + 3]) = (20, 120, 220, 255);
                    }

                    // A 12×10 patch: an opaque block and a transparent hole.
                    const int pw = 12, ph = 10, px = 6, py = 4;
                    var patch = new byte[pw * ph * 4];
                    for (int y = 0; y < ph; y++)
                    {
                        for (int x = 0; x < pw; x++)
                        {
                            int i = 4 * (y * pw + x);
                            bool hole = x >= 3 && x < 7 && y >= 2 && y < 6;
                            (patch[i], patch[i + 1], patch[i + 2], patch[i + 3]) = hole ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)240, (byte)30, (byte)(x * 20), (byte)255);
                        }
                    }

                    var dot = new byte[2 * 2 * 4];
                    for (int i = 0; i < dot.Length; i += 4)
                    {
                        (dot[i], dot[i + 1], dot[i + 2], dot[i + 3]) = (255, 255, 0, 255);
                    }

                    var encoder = new WebpAnimationEncoder(w, h, new WebpAnimationEncodeOptions { Mode = lossless ? WebpAnimationMode.Lossless : WebpAnimationMode.Lossy, Quality = 100 });
                    encoder.AddFrame(new WebpAnimationFrame { Pixels = background, Width = w, Height = h, Duration = TimeSpan.FromMilliseconds(Duration) });
                    encoder.AddFrame(new WebpAnimationFrame
                    {
                        Pixels = patch, X = px, Y = py, Width = pw, Height = ph, Duration = TimeSpan.FromMilliseconds(Duration),
                        Blend = blend, DisposeToBackground = dispose,
                    });
                    encoder.AddFrame(new WebpAnimationFrame { Pixels = dot, X = 30, Y = 20, Width = 2, Height = 2, Duration = TimeSpan.FromMilliseconds(Duration), Blend = true });
                    byte[] webp = encoder.Encode();

                    // Expected canvases, composited here.
                    byte[] c0 = (byte[])background.Clone();
                    byte[] c1 = (byte[])c0.Clone();
                    Composite(c1, w, patch, px, py, pw, ph, blend);
                    byte[] c2 = (byte[])c1.Clone();
                    if (dispose)
                    {
                        for (int y = py; y < py + ph; y++)
                        {
                            c2.AsSpan((y * w + px) * 4, pw * 4).Clear();
                        }
                    }

                    Composite(c2, w, dot, 30, 20, 2, 2, blend: true);
                    List<(int Timestamp, byte[] Canvas)> ours = DecodeOurs(webp);
                    byte[][] dumped = DecodeAnimDump(webp, w, h);
                    byte[][] expected = [c0, c1, c2];
                    for (int k = 0; k < 3; k++)
                    {
                        if (lossless)
                        {
                            ShouldBeVisiblyEqual(ours[k].Canvas, expected[k], $"{because}: ours frame {k}");
                        }
                        else
                        {
                            for (int i = 3; i < expected[k].Length; i += 4)
                            {
                                ours[k].Canvas[i].ShouldBe(expected[k][i], $"{because}: ours frame {k} alpha {i / 4}");
                            }
                        }

                        ShouldBeVisiblyEqual(dumped[k], ours[k].Canvas, $"{because}: anim_dump frame {k}");
                    }

                    new WebpAnimationDecoder(webp).Frames.Select(f => (f.Blend, f.DisposeToBackground))
                        .ShouldBe([(false, false), (blend, dispose), (true, false)], because);
                }
            }
        }

        static void Composite(byte[] canvas, int w, byte[] rect, int x0, int y0, int rw, int rh, bool blend)
        {
            for (int y = 0; y < rh; y++)
            {
                for (int x = 0; x < rw; x++)
                {
                    int s = 4 * (y * rw + x), d = 4 * ((y0 + y) * w + x0 + x);
                    if (!blend || rect[s + 3] == 255)
                    {
                        rect.AsSpan(s, 4).CopyTo(canvas.AsSpan(d));
                    }
                    else if (rect[s + 3] != 0)
                    {
                        throw new InvalidOperationException("this compositor covers opaque and transparent sources only");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Blending partly transparent pixels: every source alpha against 64 canvas alphas, hashed colours.
    /// Our decoder's canvas must be anim_dump's byte for byte (it once rounded differently, off by one).
    /// </summary>
    [Fact]
    public void Blending_partly_transparent_pixels_decodes_like_anim_dump()
    {
        const int w = 256, h = 64;
        var canvas = new byte[w * h * 4];
        var source = new byte[w * h * 4];
        var random = new Random(5);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = 4 * (y * w + x);
                int canvasAlpha = y == h - 1 ? 255 : y * 4 + (x & 3);
                for (int c = 0; c < 3; c++)
                {
                    canvas[i + c] = canvasAlpha == 0 ? (byte)0 : (byte)random.Next(256);
                    source[i + c] = x == 0 ? (byte)0 : (byte)random.Next(256);
                }

                canvas[i + 3] = (byte)canvasAlpha;
                source[i + 3] = (byte)x;
            }
        }

        var encoder = new WebpAnimationEncoder(w, h, new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless, LosslessEffort = 1 });
        encoder.AddFrame(new WebpAnimationFrame { Pixels = canvas, Width = w, Height = h, Duration = TimeSpan.FromMilliseconds(Duration) });
        encoder.AddFrame(new WebpAnimationFrame { Pixels = source, Width = w, Height = h, Duration = TimeSpan.FromMilliseconds(Duration), Blend = true });
        byte[] webp = encoder.Encode();
        byte[][] dumped = DecodeAnimDump(webp, w, h);
        List<(int Timestamp, byte[] Canvas)> ours = DecodeOurs(webp);
        for (int k = 0; k < 2; k++)
        {
            for (int i = 0; i < dumped[k].Length; i++)
            {
                if (dumped[k][i] != ours[k].Canvas[i])
                {
                    int p = i / 4;
                    throw new ShouldAssertException($"frame {k} pixel {p % w},{p / w} byte {i & 3}: anim_dump {dumped[k][i]}, ours {ours[k].Canvas[i]} " +
                        $"(source {Convert.ToHexString(source, 4 * p, 4)}, canvas {Convert.ToHexString(canvas, 4 * p, 4)})");
                }
            }
        }
    }

    [Fact]
    public void Whole_frames_after_frames_as_given_still_diff_correctly()
    {
        const int w = 32, h = 24;
        List<byte[]> frames = Sequence("sprite", w, h, 4);
        var encoder = new WebpAnimationEncoder(w, h, new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless });
        encoder.AddFrame(new WebpAnimationFrame { Pixels = frames[0], Width = w, Height = h, Duration = TimeSpan.FromMilliseconds(Duration), DisposeToBackground = true });
        for (int f = 1; f < frames.Count; f++)
        {
            encoder.AddFrame(frames[f], ImagePixelFormat.Rgba32, TimeSpan.FromMilliseconds(Duration));
        }

        List<byte[]> shown = CheckDecoders(encoder.Encode(), w, h, frames.Count, "mixed APIs");
        for (int f = 0; f < frames.Count; f++)
        {
            ShouldBeVisiblyEqual(shown[f], frames[f], $"frame {f}");
        }
    }

    // ---------------------------------------------------------------- container, API

    [Fact]
    public void Container_carries_loop_count_background_and_metadata_and_webpinfo_accepts_it()
    {
        string webpinfo = WebpOracle.RequireWebpinfo();
        List<byte[]> frames = Sequence("alpha-sprite", 40, 30, 3);
        byte[] icc = IccProfiles.SRgbV4.ToArray(), exif = [0x4D, 0x4D, 0, 42, 0, 0, 0, 8, 0, 0], xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        foreach (WebpAnimationMode mode in Enum.GetValues<WebpAnimationMode>())
        {
            var options = new WebpAnimationEncodeOptions { Mode = mode, LoopCount = 3, BackgroundColor = 0x80102030u, IccProfile = icc, Exif = exif, Xmp = xmp };
            byte[] webp = EncodeIncrementally(frames, 40, 30, options);
            WebpAnimation animation = WebpDecoder.DecodeAnimation(webp);
            animation.LoopCount.ShouldBe(3);
            animation.BackgroundColor.ShouldBe(0x80102030u);
            animation.IccProfile.ShouldBe(icc);
            animation.Exif.ShouldBe(exif);
            animation.Xmp.ShouldBe(xmp);
            WebpDecoder.ReadInfo(webp).HasAlpha.ShouldBeTrue();

            string file = Path.Combine(Path.GetTempPath(), $"simdcodecs-anim-info-{Guid.NewGuid():N}.webp");
            File.WriteAllBytes(file, webp);
            try
            {
                (int exit, string output) = WebpEncoderContainerTests.Run(webpinfo, "-diag", file);
                exit.ShouldBe(0, $"{mode}: {output}");
                output.ShouldContain("No error detected", customMessage: $"{mode}: {output}");
            }
            finally
            {
                File.Delete(file);
            }
        }

        new WebpAnimationDecoder(EncodeIncrementally(frames, 40, 30, new())).BackgroundColor.ShouldBe(0xFFFFFFFFu, "default as img2webp writes");
    }

    [Fact]
    public void The_static_method_writes_the_encoders_file_and_every_layout_gives_the_same_file()
    {
        const int w = 21, h = 13;
        List<byte[]> frames = Sequence("sprite", w, h, 3);
        var options = new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Mixed };
        byte[] expected = EncodeIncrementally(frames, w, h, options);
        WebpEncoder.EncodeAnimation([.. frames.Select(f => new WebpAnimationCanvasFrame(f, TimeSpan.FromMilliseconds(Duration)))], w, h,
            ImagePixelFormat.Rgba32, options).ShouldBe(expected);
        using var stream = new MemoryStream();
        WebpEncoder.EncodeAnimation([.. frames.Select(f => new WebpAnimationCanvasFrame(f, TimeSpan.FromMilliseconds(Duration)))], w, h,
            ImagePixelFormat.Rgba32, stream, options);
        stream.ToArray().ShouldBe(expected);

        // BGRA with a padded stride.
        const int stride = w * 4 + 6;
        var encoder = new WebpAnimationEncoder(w, h, options);
        foreach (byte[] f in frames)
        {
            var bgra = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int s = 4 * (y * w + x), d = y * stride + 4 * x;
                    (bgra[d], bgra[d + 1], bgra[d + 2], bgra[d + 3]) = (f[s + 2], f[s + 1], f[s], f[s + 3]);
                }
            }

            encoder.AddFrame(bgra, ImagePixelFormat.Bgra32, TimeSpan.FromMilliseconds(Duration), stride);
        }

        encoder.Encode().ShouldBe(expected);
    }

    [Fact]
    public void Bad_arguments_are_rejected()
    {
        var pixels = new byte[8 * 8 * 4];
        TimeSpan d = TimeSpan.FromMilliseconds(10);
        Should.Throw<ArgumentOutOfRangeException>(() => new WebpAnimationEncoder(0, 8));
        Should.Throw<ArgumentOutOfRangeException>(() => new WebpAnimationEncoder(16385, 8));
        Should.Throw<ArgumentOutOfRangeException>(() => new WebpAnimationEncoder(8, 8, new() { LoopCount = 65536 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new WebpAnimationEncoder(8, 8, new() { Quality = 101 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new WebpAnimationEncoder(8, 8, new() { LosslessEffort = 10 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new WebpAnimationEncoder(8, 8, new() { MaxKeyframeDistance = -1 }));
        Should.Throw<ArgumentOutOfRangeException>(() => new WebpAnimationEncoder(8, 8, new() { Mode = (WebpAnimationMode)7 }));

        var encoder = new WebpAnimationEncoder(8, 8);
        Should.Throw<InvalidOperationException>(() => encoder.Encode()); // no frames
        Should.Throw<ArgumentException>(() => encoder.AddFrame(pixels.AsSpan(0, 10), ImagePixelFormat.Rgba32, d));
        Should.Throw<ArgumentOutOfRangeException>(() => encoder.AddFrame(pixels, ImagePixelFormat.Rgba32, TimeSpan.FromMilliseconds(-1)));
        Should.Throw<ArgumentOutOfRangeException>(() => encoder.AddFrame(pixels, ImagePixelFormat.Rgba32, TimeSpan.FromMilliseconds(16777216)));
        Should.Throw<ArgumentException>(() => encoder.AddFrame(new WebpAnimationFrame { Pixels = pixels, X = 1, Width = 2, Height = 2, Duration = d }));
        Should.Throw<ArgumentException>(() => encoder.AddFrame(new WebpAnimationFrame { Pixels = pixels, X = 6, Width = 4, Height = 2, Duration = d }));
        Should.Throw<ArgumentException>(() => encoder.AddFrame(new WebpAnimationFrame { Pixels = pixels, Width = 0, Height = 2, Duration = d }));
        Should.Throw<ArgumentException>(() => encoder.AddFrame(new WebpAnimationFrame { Pixels = pixels.AsSpan(0, 3).ToArray(), Width = 2, Height = 2, Duration = d }));
        Should.Throw<ArgumentOutOfRangeException>(() => encoder.AddFrame(new WebpAnimationFrame { Pixels = pixels, Width = 2, Height = 2, Duration = d, Quality = -1 }));
        encoder.FrameCount.ShouldBe(0);

        encoder.AddFrame(pixels, ImagePixelFormat.Rgba32, d);
        encoder.Encode();
        Should.Throw<InvalidOperationException>(() => encoder.AddFrame(pixels, ImagePixelFormat.Rgba32, d));
        Should.Throw<ArgumentException>(() => WebpEncoder.EncodeAnimation([], 8, 8, ImagePixelFormat.Rgba32));
    }
}
