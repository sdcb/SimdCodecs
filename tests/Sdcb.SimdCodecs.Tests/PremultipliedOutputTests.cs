using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Premultiplied alpha at the facade: decoding to <c>Bgra32Premultiplied</c> / <c>Rgba64Premultiplied</c> (and the other two) is the documented
/// conversion of the file's straight pixels, a few pixels are checked against numbers worked out by hand, and an encoder
/// given premultiplied pixels writes exactly the file it would write for the straight pixels
/// <see cref="PixelConverter"/> makes from them.
/// </summary>
public sealed class PremultipliedOutputTests
{
    private const int W = 23, H = 9;

    private static readonly PixelFormat[] Premultiplied =
        [PixelFormat.Rgba32Premultiplied, PixelFormat.Bgra32Premultiplied, PixelFormat.Rgba64Premultiplied, PixelFormat.Bgra64Premultiplied];

    /// <summary>Straight RGBA with every kind of alpha: 0, 255, 1 and arbitrary.</summary>
    private static byte[] Rgba8(int seed)
    {
        var pixels = new byte[W * H * 4];
        new Random(seed).NextBytes(pixels);
        for (var p = 0; p < W * H; p++)
        {
            pixels[p * 4 + 3] = (p % 5) switch { 0 => 0, 1 => 255, 2 => 1, _ => pixels[p * 4 + 3] };
        }
        return pixels;
    }

    private static ushort[] Rgba16(int seed)
    {
        var samples = new ushort[W * H * 4];
        var random = new Random(seed);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (ushort)random.Next(65536);
        for (var p = 0; p < W * H; p++)
            samples[p * 4 + 3] = (p % 5) switch { 0 => (ushort)0, 1 => (ushort)65535, 2 => (ushort)1, _ => samples[p * 4 + 3] };
        return samples;
    }

    // ---- decoding ----------------------------------------------------------------------------------------------

    [Fact]
    public void APngWithPartialAlphaDecodesToBgra32PremultipliedAsTheReferenceConverts()
    {
        var rgba = Rgba8(1);
        var png = Png.PngWriter.Encode(rgba, W, H);

        foreach (var stride in new[] { 0, W * 4 + 9 })
        {
            var step = stride == 0 ? W * 4 : stride;
            var destination = new byte[step * (H - 1) + W * 4];
            SimdCodecs.TryDecodeInto(png, destination, stride, PixelFormat.Bgra32Premultiplied, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            var expected = PixelConverterTests.ReferenceConvert(rgba, PixelFormat.Rgba32, PixelFormat.Bgra32Premultiplied, W * H);
            for (var y = 0; y < H; y++)
                destination.AsSpan(y * step, W * 4).ToArray().ShouldBe(expected.AsSpan(y * W * 4, W * 4).ToArray(), $"stride {stride} row {y}");
        }

        // The decoded image's own TryCopyTo is the same thing.
        SimdCodecs.TryDecode(png, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            var copy = new byte[W * H * 4];
            image.TryCopyTo(copy, 0, PixelFormat.Bgra32Premultiplied).ShouldBeTrue();
            copy.ShouldBe(PixelConverterTests.ReferenceConvert(rgba, PixelFormat.Rgba32, PixelFormat.Bgra32Premultiplied, W * H));
        }
    }

    [Fact]
    public void A16BitPngDecodesToRgba64PremultipliedAsTheReferenceConverts()
    {
        var samples = Rgba16(2);
        var png = Png.PngWriter.EncodeRgba16(samples, W, H);
        var destination = new byte[W * H * 8];
        SimdCodecs.TryDecodeInto(png, destination, 0, PixelFormat.Rgba64Premultiplied, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        destination.ShouldBe(PixelConverterTests.ReferenceConvert(MemoryMarshal.AsBytes(samples.AsSpan()), PixelFormat.Rgba64, PixelFormat.Rgba64Premultiplied, W * H));

        // The file's samples survive a trip to straight 16-bit untouched.
        var straight = new ushort[W * H * 4];
        SimdCodecs.TryDecodeInto(png, MemoryMarshal.AsBytes(straight.AsSpan()), 0, PixelFormat.Rgba64, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        straight.ShouldBe(samples);
    }

    [Fact]
    public void EveryPremultipliedFormatIsTheReferenceForEightAndSixteenBitSources()
    {
        var rgba = Rgba8(3);
        var samples = Rgba16(4);
        var sources = new (string Name, byte[] Data, byte[] Pixels, PixelFormat Native)[]
        {
            ("png8", Png.PngWriter.Encode(rgba, W, H), rgba, PixelFormat.Rgba32),
            ("png16", Png.PngWriter.EncodeRgba16(samples, W, H), MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), PixelFormat.Rgba64),
            ("qoi", Qoi.QoiEncoder.Encode(rgba, W, H, PixelFormat.Rgba32), rgba, PixelFormat.Rgba32),
            ("webp", Webp.WebpEncoder.EncodeLossless(rgba, W, H, PixelFormat.Rgba32, new Webp.WebpLosslessEncodeOptions { Exact = true }), rgba, PixelFormat.Rgba32),
            ("bmp", Bmp.BmpEncoder.Encode(rgba, W, H, PixelFormat.Rgba32), rgba, PixelFormat.Rgba32),
        };
        foreach (var (name, data, pixels, native) in sources)
        foreach (var format in Premultiplied)
        {
            var destination = new byte[W * H * format.BytesPerPixel];
            SimdCodecs.TryDecodeInto(data, destination, 0, format, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue($"{name} {format}");
            destination.ShouldBe(PixelConverterTests.ReferenceConvert(pixels, native, format, W * H), $"{name} {format}");
        }
    }

    [Fact]
    public void FewPixelsWorkedOutByHand()
    {
        // 8-bit: round(c * a / 255). (200, 100, 50) at 128 -> (100, 50, 25); alpha 0 -> black; alpha 255 -> unchanged;
        // 255 at alpha 1 -> 1.
        byte[] rgba = [200, 100, 50, 128, 17, 34, 51, 0, 1, 2, 3, 255, 255, 255, 255, 1];
        var png = Png.PngWriter.Encode(rgba, 4, 1);
        var bgra = new byte[16];
        SimdCodecs.TryDecodeInto(png, bgra, 0, PixelFormat.Bgra32Premultiplied, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        bgra.ShouldBe([25, 50, 100, 128, 0, 0, 0, 0, 3, 2, 1, 255, 1, 1, 1, 1]);
        var rgbaPremultiplied = new byte[16];
        SimdCodecs.TryDecodeInto(png, rgbaPremultiplied, 0, PixelFormat.Rgba32Premultiplied, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        rgbaPremultiplied.ShouldBe([100, 50, 25, 128, 0, 0, 0, 0, 1, 2, 3, 255, 1, 1, 1, 1]);

        // 16-bit: (c * a + 32767) / 65535 (the denominator is odd, so there are no halves).
        ushort[] samples = [60000, 30000, 10000, 32768, 65535, 65535, 65535, 0, 1000, 2000, 3000, 65535, 65535, 40000, 1, 2];
        var png16 = Png.PngWriter.EncodeRgba16(samples, 4, 1);
        var premultiplied = new ushort[16];
        SimdCodecs.TryDecodeInto(png16, MemoryMarshal.AsBytes(premultiplied.AsSpan()), 0, PixelFormat.Rgba64Premultiplied, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        for (var p = 0; p < 4; p++)
        {
            var a = samples[p * 4 + 3];
            for (var c = 0; c < 3; c++)
                premultiplied[p * 4 + c].ShouldBe((ushort)(((long)samples[p * 4 + c] * a + 32767) / 65535), $"pixel {p} channel {c}");
            premultiplied[p * 4 + 3].ShouldBe(a);
        }
        premultiplied[4].ShouldBe((ushort)0); // alpha 0
        premultiplied[8].ShouldBe((ushort)1000); // alpha 65535
    }

    [Fact]
    public void AnOpaqueImageIsTheSamePremultipliedOrNot()
    {
        var rgb = new byte[W * H * 3];
        new Random(7).NextBytes(rgb);
        var png = Png.PngWriter.EncodeRgb8(rgb, W, H);
        var straight = new byte[W * H * 4];
        var premultiplied = new byte[W * H * 4];
        SimdCodecs.TryDecodeInto(png, straight, 0, PixelFormat.Bgra32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        SimdCodecs.TryDecodeInto(png, premultiplied, 0, PixelFormat.Bgra32Premultiplied, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        premultiplied.ShouldBe(straight);
    }

    // ---- encoding ----------------------------------------------------------------------------------------------

    private static (byte[] Premultiplied, byte[] Straight) Source(PixelFormat format, int seed)
    {
        var native = format.Sample == SampleFormat.UInt16 ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        var original = format.Sample == SampleFormat.UInt16 ? MemoryMarshal.AsBytes(Rgba16(seed).AsSpan()).ToArray() : Rgba8(seed);
        var premultiplied = PixelConverterTests.ReferenceConvert(original, native, format, W * H);
        // What PixelConverter itself makes of the premultiplied pixels: straight 8-bit RGBA.
        var straight = new byte[W * H * 4];
        PixelConverter.ConvertRow(premultiplied, format, straight, PixelFormat.Rgba32, W * H);
        return (premultiplied, straight);
    }

    public static TheoryData<int> PremultipliedIndices() => [0, 1, 2, 3];

    [Theory]
    [MemberData(nameof(PremultipliedIndices))]
    public void QoiEncodesPremultipliedPixelsAsTheirStraightRgba(int index)
    {
        var format = Premultiplied[index];
        var (premultiplied, straight) = Source(format, 10 + index);
        Qoi.QoiEncoder.Encode(premultiplied, W, H, format).ShouldBe(Qoi.QoiEncoder.Encode(straight, W, H, PixelFormat.Rgba32), format.ToString());
    }

    [Theory]
    [MemberData(nameof(PremultipliedIndices))]
    public void WebpLosslessEncodesPremultipliedPixelsAsTheirStraightRgba(int index)
    {
        var format = Premultiplied[index];
        var (premultiplied, straight) = Source(format, 20 + index);
        foreach (var options in new Webp.WebpLosslessEncodeOptions?[] { null, new() { Exact = true }, new() { Effort = 2 } })
        {
            Webp.WebpEncoder.EncodeLossless(premultiplied, W, H, format, options)
                .ShouldBe(Webp.WebpEncoder.EncodeLossless(straight, W, H, PixelFormat.Rgba32, options), $"{format} {options}");
        }
    }

    [Theory]
    [MemberData(nameof(PremultipliedIndices))]
    public void BmpEncodesPremultipliedPixelsAsTheirStraightRgba(int index)
    {
        var format = Premultiplied[index];
        var (premultiplied, straight) = Source(format, 30 + index);
        Bmp.BmpEncoder.Encode(premultiplied, W, H, format).ShouldBe(Bmp.BmpEncoder.Encode(straight, W, H, PixelFormat.Rgba32), format.ToString());
        Bmp.BmpEncoder.Encode(premultiplied, W, H, format, new Bmp.BmpEncodeOptions { Format = Bmp.BmpEncodeFormat.Rgb24 })
            .ShouldBe(Bmp.BmpEncoder.Encode(straight, W, H, PixelFormat.Rgba32, new Bmp.BmpEncodeOptions { Format = Bmp.BmpEncodeFormat.Rgb24 }), $"{format} 24-bit");
    }

    [Theory]
    [MemberData(nameof(PremultipliedIndices))]
    public void ASteppedPremultipliedInputIsTheSameFile(int index)
    {
        var format = Premultiplied[index];
        var (premultiplied, straight) = Source(format, 40 + index);
        var rowBytes = W * format.BytesPerPixel;
        var stride = rowBytes + 11;
        var padded = new byte[stride * H];
        Array.Fill(padded, (byte)0xEE);
        for (var y = 0; y < H; y++)
            premultiplied.AsSpan(y * rowBytes, rowBytes).CopyTo(padded.AsSpan(y * stride));

        Qoi.QoiEncoder.Encode(padded, W, H, format, stride: stride).ShouldBe(Qoi.QoiEncoder.Encode(straight, W, H, PixelFormat.Rgba32));
        Webp.WebpEncoder.EncodeLossless(padded, W, H, format, null, stride).ShouldBe(Webp.WebpEncoder.EncodeLossless(straight, W, H, PixelFormat.Rgba32));
        Bmp.BmpEncoder.Encode(padded, W, H, format, null, stride).ShouldBe(Bmp.BmpEncoder.Encode(straight, W, H, PixelFormat.Rgba32));
    }

    [Fact]
    public void ThePremultipliedFilesDecodeToTheStraightPixels()
    {
        // The encoders' output is also what a decoder reads back (QOI and lossless WebP are lossless): the straight
        // RGBA the converter made, with the colour under alpha 0 gone.
        var (premultiplied, straight) = Source(PixelFormat.Bgra32Premultiplied, 50);
        foreach (var data in new[]
        {
            Qoi.QoiEncoder.Encode(premultiplied, W, H, PixelFormat.Bgra32Premultiplied),
            Webp.WebpEncoder.EncodeLossless(premultiplied, W, H, PixelFormat.Bgra32Premultiplied, new Webp.WebpLosslessEncodeOptions { Exact = true }),
            Bmp.BmpEncoder.Encode(premultiplied, W, H, PixelFormat.Bgra32Premultiplied),
        })
        {
            var decoded = new byte[W * H * 4];
            SimdCodecs.TryDecodeInto(data, decoded, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            decoded.ShouldBe(straight);
        }
    }
}
