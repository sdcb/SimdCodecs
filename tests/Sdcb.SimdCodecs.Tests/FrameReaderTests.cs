using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="SimdCodecs.TryOpenFrames"/> and the <see cref="IImageFrameReader"/> it returns. Animated
/// WebP frames are checked against anim_dump 1.6.0's renderings (the golden manifest of
/// <see cref="WebpAnimationOracleTests"/>), read in reverse so every frame but the last is a seek;
/// multi-page TIFF pages against ImageMagick's own pixels. Every frame keeps the still-image contract:
/// its info describes the image it decodes to, and <c>TryDecodeFrameInto</c> in every layout is that
/// image's <see cref="IDecodedImage.ToRgba8"/> converted.
/// </summary>
public sealed class FrameReaderTests
{
    private const byte Sentinel = 0x5A;

    private static string WebpFixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", name);

    public static TheoryData<string> GoldenAnimations() => [.. WebpAnimationOracleTests.GoldenFixtures()];

    [Theory]
    [MemberData(nameof(GoldenAnimations))]
    public void AnimatedWebpFramesAreLibwebpsInAnyOrder(string fixture)
    {
        var data = File.ReadAllBytes(WebpFixture(fixture));
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
            reader.FrameCount.ShouldBe(info.FrameCount);
            reader.FrameCount.ShouldBeGreaterThan(1);
            reader.LoopCount.ShouldBe(Webp.WebpDecoder.DecodeAnimation(data).LoopCount);

            var rgba = new byte[info.Width * info.Height * 4];
            for (var index = reader.FrameCount - 1; index >= 0; index--)
            {
                var (durationMs, sha256) = WebpAnimationOracleTests.GoldenFrame(fixture, index);
                reader.TryGetFrameInfo(index, out var frame).ShouldBeTrue();
                frame.Duration.ShouldBe(TimeSpan.FromMilliseconds(durationMs));
                frame.Info.ShouldBe(info with { FrameCount = 1 });

                reader.TryDecodeFrameInto(index, rgba, 0, ImagePixelFormat.Rgba32).ShouldBeTrue();
                WebpAnimationOracleTests.Sha256Hex(rgba).ShouldBe(sha256, $"frame {index}");
            }
        }
    }

    [Fact]
    public void AnimatedWebpFrameZeroIsTheStillImage()
    {
        foreach (var fixture in Directory.GetFiles(Path.GetDirectoryName(WebpFixture("x"))!, "anim-*.webp"))
        {
            var data = File.ReadAllBytes(fixture);
            SimdCodecs.TryDecode(data, out var still).ShouldBeTrue();
            SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
            using (reader)
            {
                reader.TryDecodeFrame(0, out var first).ShouldBeTrue();
                (first.Width, first.Height, first.Channels, first.SampleFormat)
                    .ShouldBe((still.Width, still.Height, still.Channels, still.SampleFormat));
                first.Pixels.ToArray().ShouldBe(still.Pixels.ToArray(), Path.GetFileName(fixture));
                first.IccProfile.ToArray().ShouldBe(still.IccProfile.ToArray());
            }
        }
    }

    public static TheoryData<string> AllAnimations() =>
        [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp"), "anim-*.webp").Select(static path => Path.GetFileName(path)!).Order()];

    [Theory]
    [MemberData(nameof(AllAnimations))]
    public void AnimatedWebpFramesKeepTheStillContract(string fixture)
    {
        var data = File.ReadAllBytes(WebpFixture(fixture));
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            // Every other frame, so the reader is asked to skip ahead as well as step.
            for (var index = 0; index < reader.FrameCount; index += 2)
                CheckFrame(reader, index);
        }
    }

    // One frame against the still-image contract, in every layout, tightly packed and at a padded stride.
    // storedLuma: a YCbCr JPEG, whose Gray8 is its Y plane by contract (checked in DecodeIntoTests), not
    // the luma of its RGB.
    private static void CheckFrame(IImageFrameReader reader, int index, bool storedLuma = false)
    {
        reader.TryGetFrameInfo(index, out var frame).ShouldBeTrue();
        reader.TryDecodeFrame(index, out var image).ShouldBeTrue();
        (frame.Info.Width, frame.Info.Height, frame.Info.Channels, frame.Info.SampleFormat)
            .ShouldBe((image.Width, image.Height, image.Channels, image.SampleFormat), $"frame {index}");
        frame.Info.FrameCount.ShouldBe(1);
        if (image.SampleFormat == SampleFormat.Float32) return;

        int width = image.Width, height = image.Height;
        var rgba = image.ToRgba8();
        foreach (var format in PixelConverterTests.Formats)
        {
            if (storedLuma && format == ImagePixelFormat.Gray8) continue;
            var expected = PixelConverterTests.Reference(rgba, 4, SampleFormat.UInt8, format, width * height);
            var rowBytes = width * PixelConverter.BytesPerPixel(format);
            foreach (var stride in new[] { 0, rowBytes + 5 })
            {
                var step = stride == 0 ? rowBytes : stride;
                var destination = new byte[step * (height - 1) + rowBytes];
                Array.Fill(destination, Sentinel);
                reader.TryDecodeFrameInto(index, destination, stride, format).ShouldBeTrue($"frame {index} {format}");
                for (var y = 0; y < height; y++)
                {
                    destination.AsSpan(y * step, rowBytes).ToArray()
                        .ShouldBe(expected.AsSpan(y * rowBytes, rowBytes).ToArray(), $"frame {index} {format} stride {stride} row {y}");
                    if (y < height - 1)
                        destination.AsSpan(y * step + rowBytes, step - rowBytes).ToArray().ShouldAllBe(v => v == Sentinel);
                }
            }
            reader.TryDecodeFrameInto(index, new byte[width * height * PixelConverter.BytesPerPixel(format) - 1], 0, format)
                .ShouldBeFalse("a destination one byte short");
        }
    }

    // Three pages of different sizes and layouts, and ImageMagick's pixels for each (as stored, top-down).
    private static byte[] ThreePageTiff(out (int Width, int Height, int Channels, byte[] Pixels)[] pages)
    {
        var random = new Random(3);
        (int W, int H, string Map)[] shapes = [(9, 4, "R"), (5, 7, "RGB"), (11, 6, "RGBA")]; // the last is the largest
        using var collection = new MagickImageCollection();
        pages = new (int, int, int, byte[])[shapes.Length];
        for (var i = 0; i < shapes.Length; i++)
        {
            var (w, h, map) = shapes[i];
            var pixels = new byte[w * h * map.Length];
            random.NextBytes(pixels);
            var image = new MagickImage();
            image.ReadPixels(pixels, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, map == "R" ? "I" : map));
            if (map == "R") image.ColorType = ColorType.Grayscale;
            image.Depth = 8;
            collection.Add(image);
            pages[i] = (w, h, map.Length, pixels);
        }
        return collection.ToByteArray(MagickFormat.Tiff);
    }

    [Fact]
    public void TiffPagesAreImageMagicksPixelsInAnyOrder()
    {
        var tiff = ThreePageTiff(out var pages);
        SimdCodecs.TryOpenFrames(tiff, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(3);
            reader.LoopCount.ShouldBeNull();
            foreach (var index in new[] { 2, 0, 1, 1, 2 })
            {
                var (w, h, channels, pixels) = pages[index];
                reader.TryGetFrameInfo(index, out var frame).ShouldBeTrue();
                frame.Duration.ShouldBeNull();
                (frame.Info.Width, frame.Info.Height, frame.Info.Channels).ShouldBe((w, h, channels));
                reader.TryDecodeFrame(index, out var image).ShouldBeTrue();
                image.Pixels.ToArray().ShouldBe(pixels, $"page {index}");
            }
            for (var index = 0; index < 3; index++)
                CheckFrame(reader, index);
        }
    }

    [Fact]
    public void ARefusedTiffPageDoesNotStopTheOthers()
    {
        using var collection = new MagickImageCollection();
        collection.Add(new MagickImage(MagickColors.Red, 4, 2));
        var palette = new MagickImage(MagickColors.Blue, 3, 3);
        palette.ColorType = ColorType.Palette; // a photometric the adapter refuses
        collection.Add(palette);
        collection.Add(new MagickImage(MagickColors.Green, 2, 5));
        var tiff = collection.ToByteArray(MagickFormat.Tiff);

        SimdCodecs.TryOpenFrames(tiff, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(3);
            reader.TryGetFrameInfo(1, out _).ShouldBeFalse();
            reader.TryDecodeFrame(1, out _).ShouldBeFalse();
            reader.TryDecodeFrameInto(1, new byte[1 << 10], 0, ImagePixelFormat.Rgba32).ShouldBeFalse();
            reader.TryDecodeFrame(2, out var last).ShouldBeTrue();
            (last.Width, last.Height).ShouldBe((2, 5));
        }
    }

    public static TheoryData<string> Stills() => ["png", "jpeg", "webp", "jbig2", "j2k", "tiff"];

    [Theory]
    [MemberData(nameof(Stills))]
    public void AStillIsOneFrame(string kind)
    {
        var data = kind switch
        {
            "png" => Png.PngWriter.EncodeRgb8(new byte[7 * 3 * 3], 7, 3),
            "jpeg" => JpegDecoderOracleTests.EncodeJpeg(JpegDecoderOracleTests.MakeRgbPattern(13, 6), 13, 6),
            "webp" => File.ReadAllBytes(WebpFixture("lossless-3x7.webp")),
            "jbig2" => Jbig2FacadeTests.BuildFile(),
            "j2k" => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000", "flat64.j2k")),
            _ => new MagickImage(MagickColors.Teal, 6, 2).ToByteArray(MagickFormat.Tiff),
        };
        SimdCodecs.TryDecode(data, out var still).ShouldBeTrue();
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(1);
            reader.LoopCount.ShouldBeNull();
            reader.TryGetFrameInfo(0, out var frame).ShouldBeTrue();
            frame.Duration.ShouldBeNull();
            reader.TryDecodeFrame(0, out var image).ShouldBeTrue();
            image.Pixels.ToArray().ShouldBe(still.Pixels.ToArray());
            CheckFrame(reader, 0, storedLuma: kind == "jpeg");
        }
    }

    [Fact]
    public void AnApngIsItsStillImageUntilApngDecodes()
    {
        // The file says 3 frames (ImageInfo.FrameCount); the reader decodes the one PNG defines as the
        // fallback.
        var png = Png.PngWriter.EncodeGray8(new byte[4 * 3], 4, 3);
        var acTL = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(acTL, 3);
        var apng = new MemoryStream();
        apng.Write(png, 0, 33);
        PngRgba8ExpansionTests.Chunk(apng, "acTL"u8, acTL);
        apng.Write(png, 33, png.Length - 33);

        SimdCodecs.TryReadInfo(apng.ToArray(), out var info).ShouldBeTrue();
        info.FrameCount.ShouldBe(3);
        SimdCodecs.TryOpenFrames(apng.ToArray(), out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(1);
            reader.TryGetFrameInfo(0, out var frame).ShouldBeTrue();
            frame.Info.FrameCount.ShouldBe(1);
        }
    }

    [Theory]
    [MemberData(nameof(GoldenAnimations))]
    public void EnumeratingPlaysLibwebpsFramesThroughOneBuffer(string fixture)
    {
        var data = File.ReadAllBytes(WebpFixture(fixture));
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.TryGetFrameInfo(0, out var first).ShouldBeTrue();
            var buffer = new byte[first.Info.Width * first.Info.Height * 4];
            // Twice through: a second loop starts again at frame 0.
            for (var pass = 0; pass < 2; pass++)
            {
                var expected = 0;
                foreach (var step in reader.EnumerateFramesInto(buffer, 0, ImagePixelFormat.Rgba32))
                {
                    step.Index.ShouldBe(expected);
                    var (durationMs, sha256) = WebpAnimationOracleTests.GoldenFrame(fixture, step.Index);
                    step.Duration.ShouldBe(TimeSpan.FromMilliseconds(durationMs));
                    WebpAnimationOracleTests.Sha256Hex(buffer).ShouldBe(sha256, $"pass {pass} frame {step.Index}");
                    expected++;
                }
                expected.ShouldBe(reader.FrameCount);
            }
        }
    }

    [Fact]
    public void EnumeratingPagesWritesEachPageAtItsOwnWidth()
    {
        var tiff = ThreePageTiff(out var pages);
        SimdCodecs.TryOpenFrames(tiff, out var reader).ShouldBeTrue();
        using (reader)
        {
            var largest = pages.Max(p => p.Width * p.Height * 3);
            var buffer = new byte[largest];
            var seen = 0;
            foreach (var step in reader.EnumerateFramesInto(buffer, 0, ImagePixelFormat.Bgr24))
            {
                step.Duration.ShouldBeNull();
                var (w, h, _, _) = pages[step.Index];
                var alone = new byte[w * h * 3];
                reader.TryDecodeFrameInto(step.Index, alone, 0, ImagePixelFormat.Bgr24).ShouldBeTrue();
                buffer[..alone.Length].ShouldBe(alone, $"page {step.Index}");
                seen++;
            }
            seen.ShouldBe(3);

            // A buffer that fits the first two pages but not the third: two frames, then the misfit.
            var small = new byte[pages.Take(2).Max(p => p.Width * p.Height * 3)];
            small.Length.ShouldBeLessThan(pages[2].Width * pages[2].Height * 3);
            var played = new List<int>();
            Should.Throw<ArgumentException>(() =>
            {
                foreach (var step in reader.EnumerateFramesInto(small, 0, ImagePixelFormat.Bgr24))
                    played.Add(step.Index);
            });
            played.ShouldBe([0, 1]);
        }
    }

    [Fact]
    public void EnumeratingStopsAtAnUndecodableFrame()
    {
        using var collection = new MagickImageCollection();
        collection.Add(new MagickImage(MagickColors.Red, 4, 2));
        var palette = new MagickImage(MagickColors.Blue, 3, 3);
        palette.ColorType = ColorType.Palette;
        collection.Add(palette);
        SimdCodecs.TryOpenFrames(collection.ToByteArray(MagickFormat.Tiff), out var reader).ShouldBeTrue();
        using (reader)
        {
            var played = new List<int>();
            Should.Throw<InvalidDataException>(() =>
            {
                foreach (var step in reader.EnumerateFramesInto(new byte[1 << 10], 0, ImagePixelFormat.Rgba32))
                    played.Add(step.Index);
            }).Message.ShouldContain("Frame 1");
            played.ShouldBe([0]);
        }

        // Float pages have no 8-bit projection.
        SimdCodecs.TryOpenFrames(HdrTiff.Float32Gray(3, 2, new float[6]), out var floats).ShouldBeTrue();
        using (floats)
            Should.Throw<NotSupportedException>(() => floats.EnumerateFramesInto(new byte[64], 0, ImagePixelFormat.Gray8).ToList());
    }

    [Fact]
    public void EnumerationArgumentsAreCheckedOnTheCall()
    {
        SimdCodecs.TryOpenFrames(ThreePageTiff(out _), out var reader).ShouldBeTrue();
        using (reader)
        {
            // Thrown by the call itself, before anything is enumerated.
            Should.Throw<ArgumentOutOfRangeException>(() => reader.EnumerateFramesInto(new byte[64], -1, ImagePixelFormat.Rgba32));
            Should.Throw<ArgumentOutOfRangeException>(() => reader.EnumerateFramesInto(new byte[64], 0, (ImagePixelFormat)99));
            Should.Throw<ArgumentNullException>(() => ((IImageFrameReader)null!).EnumerateFramesInto(new byte[64], 0, ImagePixelFormat.Rgba32));
        }
    }

    [Fact]
    public void MisuseThrowsAndGarbageIsRefused()
    {
        foreach (var data in new[] { ThreePageTiff(out _), File.ReadAllBytes(WebpFixture("anim-img2webp-lossy.webp")), Png.PngWriter.EncodeGray8(new byte[4], 2, 2) })
        {
            SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
            Should.Throw<ArgumentOutOfRangeException>(() => reader.TryDecodeFrame(-1, out _));
            Should.Throw<ArgumentOutOfRangeException>(() => reader.TryGetFrameInfo(reader.FrameCount, out _));
            reader.Dispose();
            Should.Throw<ObjectDisposedException>(() => reader.TryDecodeFrame(0, out _));
            Should.Throw<ObjectDisposedException>(() => reader.TryDecodeFrameInto(0, new byte[64], 0, ImagePixelFormat.Gray8));
        }

        SimdCodecs.TryOpenFrames(new byte[64], out _).ShouldBeFalse();
        var webp = File.ReadAllBytes(WebpFixture("anim-img2webp-lossy.webp"));
        SimdCodecs.TryOpenFrames(webp.AsMemory(0, 40), out _).ShouldBeFalse();
        SimdCodecs.TryOpenFrames("II*\0\0\0\0\0"u8.ToArray(), out _).ShouldBeFalse(); // a TIFF with no pages
    }
}
