using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>An <see cref="ArrayPool{T}"/> that counts what it hands out and takes back (and never reuses an array).</summary>
internal sealed class CountingArrayPool : ArrayPool<byte>
{
    public int Rents { get; private set; }

    public int Returns { get; private set; }

    public List<byte[]> Rented { get; } = [];

    public List<byte[]> Returned { get; } = [];

    public override byte[] Rent(int minimumLength)
    {
        Rents++;
        // Longer than asked, as a real pool is: the image must not read the slack as pixels.
        var array = new byte[minimumLength + 5];
        Rented.Add(array);
        return array;
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        Returns++;
        Returned.Add(array);
    }
}

/// <summary>
/// <see cref="DecodeOptions"/> through the facade: the JPEG scale denominators against <see cref="JpegDecoder"/>'s own scaled
/// decode, <see cref="SimdCodecs.TryGetClosestSize"/>, the codecs that do not scale, regions against a crop of the full
/// decode, and the pool.
/// </summary>
public sealed class DecodeOptionsTests
{
    private const int W = 67, H = 45;

    private static byte[] Rgb(int w, int h, int seed)
    {
        var pixels = new byte[w * h * 3];
        var random = new Random(seed);
        // Smooth-ish content so a JPEG is not pure noise, plus some noise.
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        for (var c = 0; c < 3; c++)
            pixels[(y * w + x) * 3 + c] = (byte)((x * (c + 2) * 3 + y * (4 - c) * 5 + random.Next(16)) & 0xFF);
        return pixels;
    }

    private static byte[] Rgba(int w, int h, int seed)
    {
        var pixels = new byte[w * h * 4];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    private static byte[] ColourJpeg() =>
        JpegEncoder.Encode(Rgb(W, H, 1), W, H, 3, new JpegEncodeOptions { Quality = 85, Subsampling = JpegSubsampling.Chroma420 });

    private static readonly Dictionary<string, byte[]> Stills = new()
    {
        ["png"] = Png.PngWriter.Encode(Rgba(W, H, 2), W, H),
        ["jpeg"] = ColourJpeg(),
        ["qoi"] = Qoi.QoiEncoder.Encode(Rgba(W, H, 3), W, H, PixelFormat.Rgba32),
    };

    public static TheoryData<string> StillNames() => [.. Stills.Keys];

    // ---- scale ---------------------------------------------------------------------------------------------------

    public static TheoryData<string, int> JpegScales()
    {
        var data = new TheoryData<string, int>();
        foreach (var kind in new[] { "colour", "grey" })
        foreach (var d in new[] { 2, 4, 8 })
            data.Add(kind, d);
        return data;
    }

    private static byte[] JpegOf(string kind) =>
        kind == "colour" ? ColourJpeg() : JpegEncoder.Encode(Rgb(W, H, 4).AsSpan(0, W * H).ToArray(), W, H, 1);

    [Theory]
    [MemberData(nameof(JpegScales))]
    public void AJpegScaleDenominatorIsTheDecodersScaledDecode(string kind, int denominator)
    {
        var data = JpegOf(kind);
        var expected = JpegDecoder.Decode(data, (JpegScale)denominator);
        expected.Width.ShouldBe((W + denominator - 1) / denominator);
        expected.Height.ShouldBe((H + denominator - 1) / denominator);

        var options = new DecodeOptions { ScaleDenominator = denominator };
        SimdCodecs.TryDecode(data, out var image, options, TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            (image.Width, image.Height).ShouldBe((expected.Width, expected.Height));
            image.ToRgba8().ShouldBe(expected.Pixels);
        }

        var into = new byte[expected.Width * expected.Height * 4];
        SimdCodecs.TryDecodeInto(data, into, 0, PixelFormat.Rgba32, options, TestContext.Current.CancellationToken).ShouldBeTrue();
        into.ShouldBe(expected.Pixels);
    }

    private static int ExpectedScale(int width, int height, int wantedWidth, int wantedHeight)
    {
        var best = 1;
        foreach (var d in new[] { 2, 4, 8 })
        {
            if ((width + d - 1) / d >= wantedWidth && (height + d - 1) / d >= wantedHeight)
                best = d;
        }
        return best;
    }

    [Fact]
    public void TryGetClosestSizePicksTheSmallestScaleThatStillCoversTheRequest()
    {
        var data = ColourJpeg();
        (int W, int H)[] requests =
        [
            (1, 1), (8, 6), (9, 6), (10, 6), (9, 7), (17, 12), (18, 12), (34, 23), (35, 23), (W, H), (W + 1, H), (W, H + 1), (1000, 1000), (W, 1), (1, H),
        ];
        foreach (var (wantedWidth, wantedHeight) in requests)
        {
            SimdCodecs.TryGetClosestSize(data, wantedWidth, wantedHeight, out var width, out var height, out var scale).ShouldBeTrue();
            var d = ExpectedScale(W, H, wantedWidth, wantedHeight);
            scale.ShouldBe(d, $"{wantedWidth}x{wantedHeight}");
            (width, height).ShouldBe(((W + d - 1) / d, (H + d - 1) / d), $"{wantedWidth}x{wantedHeight}");
            if (d == 1 && (wantedWidth > W || wantedHeight > H))
                (width, height).ShouldBe((W, H), "a request larger than the image is the full size");
            else if (d > 1)
            {
                // The next smaller output (a larger denominator, if there is one) would not cover the request.
                (width >= wantedWidth && height >= wantedHeight).ShouldBeTrue();
            }

            // And that is the size a decode at that denominator produces.
            SimdCodecs.TryDecode(data, out var image, new DecodeOptions { ScaleDenominator = scale }, TestContext.Current.CancellationToken).ShouldBeTrue();
            using (image)
                (image.Width, image.Height).ShouldBe((width, height));
        }
    }

    [Fact]
    public void TryGetClosestSizeRefusesWhatIsNotAnImage()
    {
        SimdCodecs.TryGetClosestSize(new byte[40], 8, 8, out _, out _, out var scale).ShouldBeFalse();
        scale.ShouldBe(1);
        SimdCodecs.TryGetClosestSize(ColourJpeg().AsSpan(0, 3), 8, 8, out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void AnUnscalingCodecRefusesAScaleAndReportsItsStoredSize()
    {
        var png = Stills["png"];
        foreach (var d in new[] { 2, 4, 8 })
        {
            var options = new DecodeOptions { ScaleDenominator = d };
            Should.Throw<NotSupportedException>(() => SimdCodecs.TryDecode(png, out _, options, TestContext.Current.CancellationToken));
            Should.Throw<NotSupportedException>(() =>
                SimdCodecs.TryDecodeInto(png, new byte[W * H * 4], 0, PixelFormat.Rgba32, options, TestContext.Current.CancellationToken));
        }

        foreach (var (wantedWidth, wantedHeight) in new[] { (1, 1), (W / 2, H / 2), (W, H), (W * 3, H * 3) })
        {
            SimdCodecs.TryGetClosestSize(png, wantedWidth, wantedHeight, out var width, out var height, out var scale).ShouldBeTrue();
            (width, height, scale).ShouldBe((W, H, 1));
        }
    }

    [Fact]
    public void ScaleOneIsTheFullImage()
    {
        var data = ColourJpeg();
        SimdCodecs.TryDecode(data, out var plain, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        SimdCodecs.TryDecode(data, out var scaled, new DecodeOptions { ScaleDenominator = 1 }, TestContext.Current.CancellationToken).ShouldBeTrue();
        using (plain)
        using (scaled)
            scaled.Pixels.ToArray().ShouldBe(plain.Pixels.ToArray());
    }

    // ---- region --------------------------------------------------------------------------------------------------

    private static byte[] Crop(ReadOnlySpan<byte> pixels, int width, int bytesPerPixel, PixelRect r)
    {
        var result = new byte[r.Width * r.Height * bytesPerPixel];
        for (var y = 0; y < r.Height; y++)
            pixels.Slice(((r.Y + y) * width + r.X) * bytesPerPixel, r.Width * bytesPerPixel).CopyTo(result.AsSpan(y * r.Width * bytesPerPixel));
        return result;
    }

    private static readonly PixelRect[] Regions =
    [
        new(3, 2, 10, 7), new(0, 0, W, H), new(0, 0, 1, 1), new(W - 1, H - 1, 1, 1), new(W - 9, H - 5, 9, 5), new(5, 0, 1, H), new(0, 7, W, 1),
    ];

    [Theory]
    [MemberData(nameof(StillNames))]
    public void ARegionIsACropOfTheFullDecode(string name)
    {
        var data = Stills[name];
        SimdCodecs.TryDecode(data, out var full, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (full)
        {
            var bpp = full.Channels * full.SampleFormat switch { SampleFormat.UInt16 => 2, _ => 1 };
            foreach (var region in Regions)
            {
                var expected = Crop(full.Pixels, full.Width, bpp, region);
                SimdCodecs.TryDecode(data, out var cropped, new DecodeOptions { Region = region }, TestContext.Current.CancellationToken)
                    .ShouldBeTrue($"{name} {region}");
                using (cropped)
                {
                    (cropped.Width, cropped.Height, cropped.Channels, cropped.SampleFormat)
                        .ShouldBe((region.Width, region.Height, full.Channels, full.SampleFormat), $"{name} {region}");
                    cropped.Pixels.ToArray().ShouldBe(expected, $"{name} {region}");
                }

                // The same region through TryDecodeInto, in a layout the codec has no native path for.
                var native = PixelFormat.ForChannels(full.Channels, full.SampleFormat);
                var format = PixelFormat.Bgra64Premultiplied;
                var wanted = PixelConverterTests.ReferenceConvert(expected, native, format, region.Width * region.Height);
                var into = new byte[wanted.Length];
                SimdCodecs.TryDecodeInto(data, into, 0, format, new DecodeOptions { Region = region }, TestContext.Current.CancellationToken)
                    .ShouldBeTrue($"{name} {region} into");
                into.ShouldBe(wanted, $"{name} {region} into");

                var rgba = new byte[region.Width * region.Height * 4];
                SimdCodecs.TryDecodeInto(data, rgba, 0, PixelFormat.Rgba32, new DecodeOptions { Region = region }, TestContext.Current.CancellationToken)
                    .ShouldBeTrue($"{name} {region} into rgba");
                rgba.ShouldBe(PixelConverterTests.ReferenceConvert(expected, native, PixelFormat.Rgba32, region.Width * region.Height), $"{name} {region} into rgba");
            }
        }
    }

    [Fact]
    public void ARegionOfAScaledJpegIsInScaledCoordinates()
    {
        var data = ColourJpeg();
        var scaled = JpegDecoder.Decode(data, JpegScale.Quarter); // 17 x 12
        var region = new PixelRect(4, 3, 9, 6);
        SimdCodecs.TryDecode(data, out var image, new DecodeOptions { ScaleDenominator = 4, Region = region }, TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            (image.Width, image.Height).ShouldBe((9, 6));
            image.ToRgba8().ShouldBe(Crop(scaled.Pixels, scaled.Width, 4, region));
        }

        // The full-resolution coordinates of the same rectangle do not fit the scaled image.
        Should.Throw<ArgumentException>(() => SimdCodecs.TryDecode(data, out _,
            new DecodeOptions { ScaleDenominator = 4, Region = new PixelRect(40, 30, 9, 6) }, TestContext.Current.CancellationToken));
    }

    public static TheoryData<string, int, int, int, int> BadRegions()
    {
        var data = new TheoryData<string, int, int, int, int>();
        foreach (var name in new[] { "png", "jpeg", "qoi" })
        {
            data.Add(name, W - 5, 0, 10, 5);   // past the right edge
            data.Add(name, 0, H - 1, 5, 2);    // past the bottom edge
            data.Add(name, -1, 0, 5, 5);       // negative origin
            data.Add(name, 0, -3, 5, 5);
            data.Add(name, 0, 0, 0, 5);        // empty
            data.Add(name, 0, 0, 5, 0);
            data.Add(name, W, 0, 1, 1);        // starts outside
            data.Add(name, 0, 0, W + 1, H);
            data.Add(name, int.MaxValue, 0, 2, 2); // would overflow an int sum
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BadRegions))]
    public void ARegionOutsideTheImageThrowsArgumentException(string name, int x, int y, int width, int height)
    {
        var data = Stills[name];
        var options = new DecodeOptions { Region = new PixelRect(x, y, width, height) };
        Should.Throw<ArgumentException>(() => SimdCodecs.TryDecode(data, out _, options, TestContext.Current.CancellationToken));
        Should.Throw<ArgumentException>(() =>
            SimdCodecs.TryDecodeInto(data, new byte[W * H * 4], 0, PixelFormat.Rgba32, options, TestContext.Current.CancellationToken));
    }

    // ---- pool ----------------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, byte[]> RentingCodecs = new()
    {
        ["jpeg"] = ColourJpeg(),
        ["qoi"] = Stills["qoi"],
        ["bmp"] = Bmp.BmpEncoder.Encode(Rgba(W, H, 5), W, H, PixelFormat.Rgba32),
        ["webp"] = Webp.WebpEncoder.EncodeLossless(Rgba(W, H, 6), W, H, PixelFormat.Rgba32),
        ["gif"] = Gif.GifEncoder.Encode(Rgba(W, H, 7), W, H, 4),
        ["pnm"] = Sdcb.SimdCodecs.Pnm.PnmEncoder.Encode(Rgb(W, H, 8), W, H, 3),
    };

    public static TheoryData<string> RentingNames() => [.. RentingCodecs.Keys];

    [Theory]
    [MemberData(nameof(RentingNames))]
    public void APoolMakesTheImageRentedAndDisposeReturnsIt(string name)
    {
        var pool = new CountingArrayPool();
        SimdCodecs.TryDecode(RentingCodecs[name], out var image, new DecodeOptions { Pool = pool }, TestContext.Current.CancellationToken).ShouldBeTrue();

        pool.Rents.ShouldBeGreaterThan(0, name);
        pool.Returns.ShouldBe(pool.Rents - 1, $"{name}: exactly the image's buffer is outstanding");
        image.Pixels.Length.ShouldBe(image.Width * image.Height * image.Channels, $"{name}: the pool's slack is not part of the pixels");

        // The pixels are the same as an unpooled decode's.
        SimdCodecs.TryDecode(RentingCodecs[name], out var plain, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (plain)
            image.Pixels.ToArray().ShouldBe(plain.Pixels.ToArray(), name);

        image.Dispose();
        pool.Returns.ShouldBe(pool.Rents, name);
        pool.Returned.ShouldBe(pool.Rented, ignoreOrder: true);
        Should.Throw<ObjectDisposedException>(() => _ = image.Pixels.Length);
        image.Dispose(); // twice is fine
        pool.Returns.ShouldBe(pool.Rents, $"{name}: a second Dispose returns nothing more");
    }

    [Fact]
    public void ARegionOfAnUnpooledCodecIsRentedFromThePool()
    {
        // PNG decodes into its own buffer; the crop is what the pool serves.
        var pool = new CountingArrayPool();
        SimdCodecs.TryDecode(Stills["png"], out var image, new DecodeOptions { Pool = pool, Region = new PixelRect(2, 3, 11, 5) },
            TestContext.Current.CancellationToken).ShouldBeTrue();
        pool.Rents.ShouldBe(1);
        pool.Returns.ShouldBe(0);
        (image.Width, image.Height).ShouldBe((11, 5));
        image.Dispose();
        pool.Returns.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => _ = image.Pixels.Length);
    }

    [Fact]
    public void ARegionOfAPooledCodecReturnsTheIntermediateAtOnce()
    {
        // QOI decodes the whole image, then crops it.
        var pool = new CountingArrayPool();
        SimdCodecs.TryDecode(RentingCodecs["qoi"], out var image, new DecodeOptions { Pool = pool, Region = new PixelRect(2, 3, 11, 5) },
            TestContext.Current.CancellationToken).ShouldBeTrue();
        pool.Rents.ShouldBe(2); // the full decode and the crop
        pool.Returns.ShouldBe(1); // the full decode, disposed once cropped
        image.Dispose();
        pool.Returns.ShouldBe(2);
    }

    [Fact]
    public void AJpegRegionRentsOnlyTheRegion()
    {
        // JPEG decodes a region natively: no whole-image intermediate.
        var pool = new CountingArrayPool();
        SimdCodecs.TryDecode(RentingCodecs["jpeg"], out var image, new DecodeOptions { Pool = pool, Region = new PixelRect(2, 3, 11, 5) },
            TestContext.Current.CancellationToken).ShouldBeTrue();
        pool.Rents.ShouldBe(1);
        pool.Returns.ShouldBe(0);
        (image.Width, image.Height).ShouldBe((11, 5));
        image.Dispose();
        pool.Returns.ShouldBe(1);
    }

    [Fact]
    public void TryDecodeIntoIgnoresThePool()
    {
        var pool = new CountingArrayPool();
        foreach (var (name, data) in RentingCodecs)
        {
            var destination = new byte[W * H * 4];
            SimdCodecs.TryDecodeInto(data, destination, 0, PixelFormat.Bgra64Premultiplied.To8Bit(), new DecodeOptions { Pool = pool },
                TestContext.Current.CancellationToken).ShouldBeTrue(name);
        }
        pool.Rents.ShouldBe(0);
    }

    // ---- the options type ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(16)]
    public void AScaleThatIsNotAPowerOfTwoUpToEightIsRefused(int denominator) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new DecodeOptions { ScaleDenominator = denominator });

    [Fact]
    public void TheDefaultsAreTheFullImage()
    {
        DecodeOptions.Default.ScaleDenominator.ShouldBe(1);
        DecodeOptions.Default.Region.ShouldBeNull();
        DecodeOptions.Default.Pool.ShouldBeNull();
        DecodeOptions.Default.IsFullImage.ShouldBeTrue();
        (new DecodeOptions { ScaleDenominator = 2 }).IsFullImage.ShouldBeFalse();
        (new DecodeOptions { Region = new PixelRect(0, 0, 1, 1) }).IsFullImage.ShouldBeFalse();
        (new DecodeOptions { Pool = ArrayPool<byte>.Shared }).IsFullImage.ShouldBeTrue();
        new DecodeOptions { ScaleDenominator = 8 }.GetScaledSize(67, 45).ShouldBe((9, 6));
        new DecodeOptions { ScaleDenominator = 4, Region = new PixelRect(1, 1, 3, 2) }.GetOutputSize(67, 45).ShouldBe((3, 2));
    }
}
