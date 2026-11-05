using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// JPEGs whose whole raster does not fit one .NET array (26493 × 37200 RGB is 2.96 GB): a full-size decode is refused
/// with guidance, never an <see cref="OverflowException"/>; scaled and region decodes allocate only their output. The
/// giant streams are hand-assembled headers with no scan (SOI, DQT, SOF0, EOI), which the decoder reads as a frame of
/// zero samples, so no test decodes more than a few MCU rows.
/// <para>
/// The region path writes only a window of the output rows and columns; <see cref="ARegionIsExactlyThosePixelsOfTheWholeDecode"/>
/// pins it to the whole decode on small streams of every frame shape.
/// </para>
/// </summary>
public sealed class JpegGigapixelTests
{
    private const int GiantW = 26493, GiantH = 37200;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>SOI, one DQT, a baseline SOF of <paramref name="width"/> × <paramref name="height"/>, EOI.</summary>
    private static byte[] HeaderOnly(int width, int height, int components = 3, bool progressive = false)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00 };
        bytes.AddRange(Enumerable.Repeat((byte)1, 64));
        var sofLength = 8 + 3 * components;
        bytes.AddRange([0xFF, progressive ? (byte)0xC2 : (byte)0xC0, (byte)(sofLength >> 8), (byte)sofLength, 8,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components]);
        for (var c = 0; c < components; c++)
            bytes.AddRange([(byte)(c + 1), (byte)(c == 0 && components == 3 ? 0x22 : 0x11), 0]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    [Fact]
    public void AFullSizeGigapixelDecodeIsRefusedWithoutAllocating()
    {
        var jpeg = HeaderOnly(GiantW, GiantH);
        var before = GC.GetAllocatedBytesForCurrentThread();
        JpegImageDecoder.TryDecode(jpeg, out var image, cancellationToken: Ct).ShouldBeFalse();
        image.ShouldBeNull();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(1 << 20);

        // The throwing APIs say why, and what to do instead.
        var direct = Should.Throw<InvalidDataException>(() => JpegDecoder.Decode(jpeg));
        direct.Message.ShouldContain("ScaleDenominator");
        direct.Message.ShouldContain(GiantW + "x" + GiantH);

        using var session = ImageDecoder.Open((ReadOnlyMemory<byte>)jpeg);
        var facade = Should.Throw<InvalidDataException>(() => session.GetFrame(0).Decode(cancellationToken: Ct));
        facade.Message.ShouldContain("2 GB");
        facade.Message.ShouldContain("ScaleDenominator");
    }

    [Fact]
    public void AProgressiveGigapixelFrameIsRefusedAtAnyScale()
    {
        // A progressive frame holds every coefficient (2 bytes per sample) whatever the output scale; past int.MaxValue
        // samples in all it is refused before anything is allocated: a short stream cannot ask for gigabytes.
        var jpeg = HeaderOnly(GiantW, GiantH, progressive: true);
        JpegImageDecoder.TryDecode(jpeg, out _, new DecodeOptions { ScaleDenominator = 8 }, Ct).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => JpegDecoder.Decode(jpeg, JpegScale.Eighth)).Message.ShouldContain("progressive");
    }

    [Fact]
    public void ARasterTooLargeForOneArrayIsRefusedWithGuidance()
    {
        var e = Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Allocate(GiantW, GiantH, 3, SampleFormat.UInt8));
        e.Message.ShouldContain("ScaleDenominator");
        e.Message.ShouldContain("2956618800");
        Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Rent(GiantW, GiantH, 3, SampleFormat.UInt8));
        Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Wrap(65535, 65535, 4, SampleFormat.UInt16, new byte[1]));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            PixelConverter.ToPacked(default, 0, PixelFormat.Rgb24, PixelFormat.Rgba32, GiantW, GiantH));
    }

    [Theory]
    [InlineData(2, 13247, 18600)]
    [InlineData(4, 6624, 9300)]
    [InlineData(8, 3312, 4650)]
    public void ScaledGigapixelSizesFitOneBuffer(int denominator, int width, int height)
    {
        // What TryDecode sizes its raster from: the DCT-scaled size, never the stored one.
        var jpeg = HeaderOnly(GiantW, GiantH);
        JpegDecoder.ReadInfo(jpeg).ScaledSize((JpegScale)denominator).ShouldBe((width, height));
        JpegImageDecoder.TryGetClosestSize(jpeg, width, height, out var w, out var h, out var d).ShouldBeTrue();
        (w, h, d).ShouldBe((width, height, denominator));
        ((long)width * height * 3).ShouldBeLessThan(Array.MaxLength);
    }

    [Fact]
    public void AnEighthScaleGigapixelDecodeAllocatesOnlyTheScaledRaster()
    {
        // 3312 × 4650 grey (15 MB): the whole decode, every MCU row of a stream that is all padding.
        var jpeg = HeaderOnly(GiantW, GiantH, components: 1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        JpegImageDecoder.TryDecode(jpeg, out var image, new DecodeOptions { ScaleDenominator = 8 }, Ct).ShouldBeTrue();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        using (image)
        {
            (image.Width, image.Height, image.Channels).ShouldBe((3312, 4650, 1));
            allocated.ShouldBeLessThan(3312L * 4650 + (4 << 20));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ARegionOfAGigapixelJpegAllocatesOnlyTheRegion(int denominator)
    {
        var jpeg = HeaderOnly(GiantW, GiantH);
        var region = new PixelRect(1000, 40, 300, 50);
        var options = new DecodeOptions { ScaleDenominator = denominator, Region = region };

        var before = GC.GetAllocatedBytesForCurrentThread();
        JpegImageDecoder.TryDecode(jpeg, out var image, options, Ct).ShouldBeTrue();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(4 << 20);
        using (image)
        {
            (image.Width, image.Height, image.Channels).ShouldBe((300, 50, 3));
            // A frame of zero samples is one colour, the same everywhere.
            var pixels = image.Pixels;
            for (var i = 3; i < pixels.Length; i++)
                pixels[i].ShouldBe(pixels[i % 3]);
        }

        var into = new byte[300 * 50 * 4];
        JpegImageDecoder.TryDecodeInto(jpeg, into, 0, PixelFormat.Bgra32, options, Ct).ShouldBeTrue();
        into[3].ShouldBe((byte)255);
    }

    public static TheoryData<string> Shapes() =>
        new() { "4:2:0", "4:2:0 progressive", "4:2:2", "4:4:4", "gray", "cmyk", "restart non-interleaved" };

    private static byte[] Build(string shape, int w, int h)
    {
        byte Sample(int k, int x, int y) => (byte)((x * 7 + y * 13 + k * 61 + x * y / 5) % 251);
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        for (var k = 0; k < 3; k++)
            rgb[(y * w + x) * 3 + k] = Sample(k, x, y);
        TestJpegWriter.Component[] ycc420 = [new(1, 2, 2), new(2, 1, 1), new(3, 1, 1)];
        return shape switch
        {
            "4:2:0" => TestJpegWriter.Encode(w, h, ycc420, Sample),
            "4:2:0 progressive" => JpegEncoder.Encode(rgb, w, h, 3,
                new JpegEncodeOptions { Quality = 80, Subsampling = JpegSubsampling.Chroma420, Progressive = true }),
            "4:2:2" => TestJpegWriter.Encode(w, h, [new(1, 2, 1), new(2, 1, 1), new(3, 1, 1)], Sample),
            "4:4:4" => TestJpegWriter.Encode(w, h, [new(1, 1, 1), new(2, 1, 1), new(3, 1, 1)], Sample),
            "gray" => TestJpegWriter.Encode(w, h, [new(1, 1, 1)], Sample),
            "cmyk" => TestJpegWriter.Encode(w, h, [new(1, 1, 1), new(2, 1, 1), new(3, 1, 1), new(4, 1, 1)], Sample,
                new() { AdobeTransform = 0, Jfif = false }),
            _ => TestJpegWriter.Encode(w, h, ycc420, Sample, new() { RestartInterval = 2, Interleaved = false }),
        };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ARegionIsExactlyThosePixelsOfTheWholeDecode(string shape)
    {
        const int w = 83, h = 61;
        var jpeg = Build(shape, w, h);
        foreach (var scale in (ReadOnlySpan<JpegScale>)[JpegScale.Full, JpegScale.Half, JpegScale.Eighth])
        {
            var (sw, sh) = JpegDecoder.ReadInfo(jpeg).ScaledSize(scale);
            foreach (var format in (ReadOnlySpan<JpegPixelFormat>)[JpegPixelFormat.Rgba32, JpegPixelFormat.Bgr24, JpegPixelFormat.Gray8])
            {
                var bpp = format switch { JpegPixelFormat.Rgba32 => 4, JpegPixelFormat.Bgr24 => 3, _ => 1 };
                var whole = new byte[sw * sh * bpp];
                JpegDecoder.DecodeTo(jpeg, whole, sw * bpp, format, scale, Ct);
                PixelRect[] regions =
                [
                    new(0, 0, sw, sh), new(0, 0, 1, 1), new(sw - 1, sh - 1, 1, 1), new(0, sh / 3, sw, sh / 3 + 1),
                    new(sw / 4, sh / 5, sw / 2 + 1, sh / 2 + 1), new(sw / 3, 0, sw - sw / 3, sh),
                ];
                foreach (var r in regions)
                {
                    // A padded stride, its padding left untouched.
                    var stride = r.Width * bpp + 5;
                    var region = new byte[stride * r.Height];
                    region.AsSpan().Fill(0xA5);
                    JpegDecoder.DecodeRegionTo(jpeg, region, stride, format, scale, r.X, r.Y, r.Width, r.Height, Ct);
                    for (var y = 0; y < r.Height; y++)
                    {
                        var expected = whole.AsSpan(((r.Y + y) * sw + r.X) * bpp, r.Width * bpp);
                        if (!region.AsSpan(y * stride, r.Width * bpp).SequenceEqual(expected))
                            Assert.Fail($"{shape} {scale} {format} region {r}: row {y} differs from the whole decode.");
                        if (region.AsSpan(y * stride + r.Width * bpp, 5).ContainsAnyExcept((byte)0xA5))
                            Assert.Fail($"{shape} {scale} {format} region {r}: row {y} wrote into the stride padding.");
                    }
                }
            }
        }
    }

    [Fact]
    public void ARegionOutsideTheImageIsRefused()
    {
        var jpeg = Build("4:2:0", 40, 30);
        var buffer = new byte[40 * 30 * 4];
        Should.Throw<ArgumentOutOfRangeException>(() =>
            JpegDecoder.DecodeRegionTo(jpeg, buffer, 40 * 4, JpegPixelFormat.Rgba32, JpegScale.Full, 1, 0, 40, 30, Ct));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            JpegDecoder.DecodeRegionTo(jpeg, buffer, 40 * 4, JpegPixelFormat.Rgba32, JpegScale.Half, 0, 0, 20, 16, Ct));
        Should.Throw<ArgumentException>(() =>
            JpegDecoder.DecodeRegionTo(jpeg, buffer.AsSpan(0, 100), 40 * 4, JpegPixelFormat.Rgba32, JpegScale.Full, 0, 0, 40, 30, Ct));
    }
}
