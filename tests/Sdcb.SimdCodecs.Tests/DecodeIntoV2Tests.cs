using System.Runtime.InteropServices;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="ImageCodecs.TryDecodeInto"/> in every one of the sixteen <see cref="PixelFormat"/>s, for a small corpus
/// over the codecs: the pixels are the codec's own decoded samples converted by
/// <see cref="PixelConverterTests.ReferenceConvert"/> (the documented mapping, per pixel), tightly packed and at a padded
/// stride with the padding untouched. One exception the contract makes: <see cref="PixelFormat.Gray8"/> from a YCbCr
/// JPEG is its stored luma plane, compared to <c>JpegDiagnostics.DecodePlanes</c>.
/// </summary>
public sealed class DecodeIntoV2Tests
{
    private const byte Sentinel = 0xA5;
    private const int W = 29, H = 11;

    private static readonly Lazy<Dictionary<string, byte[]>> CorpusCache = new(BuildCorpus);

    public static TheoryData<string> Names() => [.. CorpusCache.Value.Keys];

    /// <summary>Deterministic RGBA with every alpha value (including 0 and 255) in play.</summary>
    private static byte[] Rgba(int w, int h, int seed)
    {
        var pixels = new byte[w * h * 4];
        new Random(seed).NextBytes(pixels);
        for (var p = 0; p < w * h; p++)
            pixels[p * 4 + 3] = (byte)(p % 7 == 0 ? 0 : p % 11 == 0 ? 255 : pixels[p * 4 + 3]);
        return pixels;
    }

    private static ushort[] Rgba16(int w, int h, int seed)
    {
        var samples = new ushort[w * h * 4];
        var random = new Random(seed);
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (ushort)random.Next(65536);
        for (var p = 0; p < w * h; p++)
            samples[p * 4 + 3] = (ushort)(p % 7 == 0 ? 0 : p % 11 == 0 ? 65535 : samples[p * 4 + 3]);
        return samples;
    }

    private static byte[] Rgb(int w, int h, int seed)
    {
        var rgba = Rgba(w, h, seed);
        var rgb = new byte[w * h * 3];
        for (var p = 0; p < w * h; p++)
            rgba.AsSpan(p * 4, 3).CopyTo(rgb.AsSpan(p * 3, 3));
        return rgb;
    }

    private static Dictionary<string, byte[]> BuildCorpus()
    {
        var corpus = new Dictionary<string, byte[]>
        {
            ["png rgba8"] = Png.PngWriter.Encode(Rgba(W, H, 1), W, H),
            ["png rgba16"] = Png.PngWriter.EncodeRgba16(Rgba16(W, H, 2), W, H),
            ["png rgb8"] = Png.PngWriter.EncodeRgb8(Rgb(W, H, 3), W, H),
            ["png gray16"] = Png.PngWriter.EncodeGray16(Rgba16(W, H, 4).AsSpan(0, W * H).ToArray(), W, H),
            ["jpeg 4:4:4 q95"] = Jpeg.JpegEncoder.Encode(Rgb(W, H, 5), W, H, 3, new Jpeg.JpegEncodeOptions { Quality = 95, Subsampling = Jpeg.JpegSubsampling.Chroma444 }),
            ["jpeg 4:2:0 q80"] = Jpeg.JpegEncoder.Encode(Rgb(W, H, 6), W, H, 3, new Jpeg.JpegEncodeOptions { Quality = 80, Subsampling = Jpeg.JpegSubsampling.Chroma420 }),
            ["jpeg grey"] = Jpeg.JpegEncoder.Encode(Rgb(W, H, 7).AsSpan(0, W * H).ToArray(), W, H, 1),
            ["qoi rgba"] = Qoi.QoiEncoder.Encode(Rgba(W, H, 8), W, H, PixelFormat.Rgba32),
            ["qoi rgb"] = Qoi.QoiEncoder.Encode(Rgb(W, H, 9), W, H, PixelFormat.Rgb24),
            ["bmp bgra32"] = Bmp.BmpEncoder.Encode(Rgba(W, H, 10), W, H, PixelFormat.Rgba32),
            ["bmp rgb24"] = Bmp.BmpEncoder.Encode(Rgb(W, H, 11), W, H, PixelFormat.Rgb24),
            ["webp lossless rgba"] = Webp.WebpEncoder.EncodeLossless(Rgba(W, H, 12), W, H, PixelFormat.Rgba32, new Webp.WebpLosslessEncodeOptions { Exact = true }),
            ["webp lossless rgb"] = Webp.WebpEncoder.EncodeLossless(Rgb(W, H, 13), W, H, PixelFormat.Rgb24),
            ["tiff rgb16"] = HdrTiff.Uint16Rgb(W, H,
                [.. Enumerable.Range(0, W * H).Select(i => (i * 977) & 0xFFFF)],
                [.. Enumerable.Range(0, W * H).Select(i => (i * 4099 + 5) & 0xFFFF)],
                [.. Enumerable.Range(0, W * H).Select(i => (i * 31013 + 77) & 0xFFFF)]),
            ["tiff gray16"] = HdrTiff.Uint16Gray(W, H, [.. Enumerable.Range(0, W * H).Select(i => (i * 2111 + 3) & 0xFFFF)]),
            ["pnm ppm 16-bit"] = Sdcb.SimdCodecs.Pnm.PnmEncoder.Encode(Rgba16(W, H, 14).AsSpan(0, W * H * 3).ToArray(), W, H, 3),
            ["pnm pgm 16-bit"] = Sdcb.SimdCodecs.Pnm.PnmEncoder.Encode(Rgba16(W, H, 15).AsSpan(0, W * H).ToArray(), W, H, 1),
        };

        using (var image = GifDecoderTests.PaletteImage(W, H, 40, seed: 16))
            corpus["gif opaque"] = image.ToByteArray(MagickFormat.Gif);
        using (var image = GifDecoderTests.PaletteImage(W, H, 9, seed: 17, transparent: true))
            corpus["gif transparent"] = image.ToByteArray(MagickFormat.Gif);
        corpus["gif ours rgba"] = Gif.GifEncoder.Encode(Rgba(W, H, 18), W, H, 4);
        return corpus;
    }

    // The stored-luma exception: a YCbCr JPEG's Gray8 is its Y plane.
    private static byte[]? YPlane(string name, byte[] data)
    {
        if (!name.StartsWith("jpeg ", StringComparison.Ordinal))
            return null;
        var planes = Jpeg.JpegDiagnostics.DecodePlanes(data);
        return planes.Length == 3 ? planes[0].Samples : null;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryFormatIsTheConvertedImage(string name)
    {
        var data = CorpusCache.Value[name];
        ImageCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue(name);
        using (image)
        {
            int width = image.Width, height = image.Height;
            var native = PixelFormat.ForChannels(image.Channels, image.SampleFormat);
            var yPlane = YPlane(name, data);

            foreach (var format in PixelFormat.All)
            {
                var expected = PixelConverterTests.ReferenceConvert(image.Pixels, native, format, width * height);
                if (format == PixelFormat.Gray8 && yPlane is not null)
                    expected = yPlane;

                var rowBytes = width * format.BytesPerPixel;
                foreach (var stride in new[] { 0, rowBytes + 7 })
                {
                    var step = stride == 0 ? rowBytes : stride;
                    var destination = new byte[step * (height - 1) + rowBytes + 13];
                    Array.Fill(destination, Sentinel);

                    ImageCodecs.TryDecodeInto(data, destination, stride, format, cancellationToken: TestContext.Current.CancellationToken)
                        .ShouldBeTrue($"{name} -> {format} stride {stride}");

                    for (var y = 0; y < height; y++)
                    {
                        destination.AsSpan(y * step, rowBytes).ToArray()
                            .ShouldBe(expected.AsSpan(y * rowBytes, rowBytes).ToArray(), $"{name} -> {format} stride {stride} row {y}");
                        var gap = y == height - 1 ? 13 : step - rowBytes;
                        destination.AsSpan(y * step + rowBytes, gap).ToArray()
                            .ShouldAllBe(v => v == Sentinel, $"{name} -> {format} stride {stride}: wrote outside row {y}");
                    }
                }

                // One byte short of a tight image is refused, not written past.
                ImageCodecs.TryDecodeInto(data, new byte[width * height * format.BytesPerPixel - 1], 0, format,
                    cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse($"{name} -> {format} short destination");
            }
        }
    }

    [Fact]
    public void ThePngRgba16IsDecodedToRgba64WithTheExactSamples()
    {
        var samples = Rgba16(W, H, 2);
        var data = CorpusCache.Value["png rgba16"];

        var destination = new ushort[W * H * 4];
        ImageCodecs.TryDecodeInto(data, MemoryMarshal.AsBytes(destination.AsSpan()), 0, PixelFormat.Rgba64,
            cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        destination.ShouldBe(samples);

        // Bgra64 is the same samples with red and blue swapped.
        var bgra = new ushort[W * H * 4];
        ImageCodecs.TryDecodeInto(data, MemoryMarshal.AsBytes(bgra.AsSpan()), 0, PixelFormat.Bgra64,
            cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        for (var p = 0; p < W * H; p++)
        {
            bgra[p * 4].ShouldBe(samples[p * 4 + 2]);
            bgra[p * 4 + 1].ShouldBe(samples[p * 4 + 1]);
            bgra[p * 4 + 2].ShouldBe(samples[p * 4]);
            bgra[p * 4 + 3].ShouldBe(samples[p * 4 + 3]);
        }

        // And 8 bits keeps the high byte.
        var eight = new byte[W * H * 4];
        ImageCodecs.TryDecodeInto(data, eight, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        eight.ShouldBe(samples.Select(s => (byte)(s >> 8)).ToArray());
    }

    [Fact]
    public void ThePnm16BitRoundTripsThroughRgb48()
    {
        var samples = Rgba16(W, H, 14).AsSpan(0, W * H * 3).ToArray();
        var destination = new ushort[W * H * 3];
        ImageCodecs.TryDecodeInto(CorpusCache.Value["pnm ppm 16-bit"], MemoryMarshal.AsBytes(destination.AsSpan()), 0, PixelFormat.Rgb48,
            cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        destination.ShouldBe(samples);
    }

    [Fact]
    public void TheTiff16BitGoesToRgb48WithTheExactSamples()
    {
        var data = CorpusCache.Value["tiff rgb16"];
        var destination = new ushort[W * H * 3];
        ImageCodecs.TryDecodeInto(data, MemoryMarshal.AsBytes(destination.AsSpan()), 0, PixelFormat.Rgb48,
            cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        for (var i = 0; i < W * H; i++)
        {
            destination[i * 3].ShouldBe((ushort)((i * 977) & 0xFFFF));
            destination[i * 3 + 1].ShouldBe((ushort)((i * 4099 + 5) & 0xFFFF));
            destination[i * 3 + 2].ShouldBe((ushort)((i * 31013 + 77) & 0xFFFF));
        }
    }

    [Fact]
    public void AStrideShorterThanARowIsRefused()
    {
        var data = CorpusCache.Value["png rgba8"];
        ImageCodecs.TryDecodeInto(data, new byte[1 << 16], W * 4 - 1, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        ImageCodecs.TryDecodeInto(data, new byte[1 << 16], -1, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    [Fact]
    public void UnknownDataIsRefused()
    {
        ImageCodecs.TryDecodeInto(new byte[64], new byte[1 << 12], 0, PixelFormat.Rgba64Premultiplied,
            cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
    }
}
