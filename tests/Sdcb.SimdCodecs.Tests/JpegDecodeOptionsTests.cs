using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegDecodeOptions"/>: separately stored tables (T.81 B.5 abbreviated format) and an explicit colour
/// interpretation. A stream split into tables and an abbreviated image decodes exactly as the whole stream; the
/// override selects which frozen conversion runs and nothing else.
/// </summary>
public sealed class JpegDecodeOptionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Photo(int channels, bool progressive = false)
    {
        var px = new byte[61 * 37 * channels];
        for (var i = 0; i < px.Length; i++) px[i] = (byte)(128 + (90 * Math.Sin(i * 0.013)) + (i % 7));
        return JpegEncoder.Encode(px, 61, 37, channels, new JpegEncodeOptions { Quality = 85, Progressive = progressive });
    }

    /// <summary>Moves every DQT and DHT before the first SOS out of <paramref name="jpeg"/> into a tables stream.</summary>
    private static (byte[] Tables, byte[] Image) Split(byte[] jpeg)
    {
        var tables = new List<byte> { 0xFF, 0xD8 };
        var image = new List<byte> { 0xFF, 0xD8 };
        var at = 2;
        while (jpeg[at + 1] != 0xDA)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
            (jpeg[at + 1] is 0xDB or 0xC4 ? tables : image).AddRange(jpeg.AsSpan(at, length + 2).ToArray());
            at += length + 2;
        }
        image.AddRange(jpeg.AsSpan(at).ToArray());
        tables.AddRange([0xFF, 0xD9]);
        return ([.. tables], [.. image]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void AbbreviatedImagePlusTables_DecodesAsTheWholeStream(int channels, bool progressive)
    {
        var jpeg = Photo(channels, progressive);
        var (tables, image) = Split(jpeg);
        Should.Throw<InvalidDataException>(() => JpegDecoder.Decode(image), "premise: the image alone lacks its tables");

        var options = new JpegDecodeOptions { Tables = tables };
        JpegDecoder.Decode(image, options, JpegScale.Full).Pixels.ShouldBe(JpegDecoder.Decode(jpeg).Pixels);
        JpegDecoder.ReadInfo(image, options).Width.ShouldBe(61);
        var half = new byte[31 * 19 * 4];
        JpegDecoder.DecodeTo(image, half, 31 * 4, JpegPixelFormat.Rgba32, options, JpegScale.Half, Ct);
        half.ShouldBe(JpegDecoder.Decode(jpeg, JpegScale.Half).Pixels);
    }

    [Fact]
    public void TablesWithoutEoi_AreAccepted_ButAnyOtherMarkerIsRefused()
    {
        var (tables, image) = Split(Photo(3));
        var noEoi = tables[..^2];
        JpegDecoder.Decode(image, new JpegDecodeOptions { Tables = noEoi }, JpegScale.Full).Width.ShouldBe(61);

        byte[] withSof = [.. tables[..^2], 0xFF, 0xC0, 0x00, 0x0B, 8, 0, 1, 0, 1, 1, 1, 0x11, 0, 0xFF, 0xD9];
        Should.Throw<InvalidDataException>(() => JpegDecoder.Decode(image, new JpegDecodeOptions { Tables = withSof }, JpegScale.Full));
        Should.Throw<InvalidDataException>(() => JpegDecoder.Decode(image, new JpegDecodeOptions { Tables = new byte[] { 1, 2, 3 } }, JpegScale.Full));
    }

    [Fact]
    public void ColorTransform_SelectsTheConversion()
    {
        var jpeg = Photo(3); // YCbCr, JFIF
        var auto = JpegDecoder.Decode(jpeg).Pixels;
        JpegDecoder.Decode(jpeg, new JpegDecodeOptions { ColorTransform = JpegColorTransform.YCbCr }, JpegScale.Full).Pixels.ShouldBe(auto);
        var raw = JpegDecoder.Decode(jpeg, new JpegDecodeOptions { ColorTransform = JpegColorTransform.None }, JpegScale.Full).Pixels;
        raw.ShouldNotBe(auto);
        // None writes the component samples: its first channel is the Y the Gray8 path writes.
        var luma = new byte[61 * 37];
        JpegDecoder.DecodeTo(jpeg, luma, 61, JpegPixelFormat.Gray8, JpegScale.Full, Ct);
        for (var i = 0; i < luma.Length; i++) raw[i * 4].ShouldBe(luma[i]);
    }

    [Fact]
    public void ColorTransformNone_OnAdobeCmyk_GivesTheInks()
    {
        var cmyk = new byte[24 * 16 * 4];
        new Random(5).NextBytes(cmyk);
        using var image = new MagickImage();
        image.ReadPixels(cmyk, new PixelReadSettings(24, 16, StorageType.Char, "CMYK"));
        image.Quality = 95;
        var jpeg = image.ToByteArray(MagickFormat.Jpeg);
        JpegDecoder.ReadInfo(jpeg).Components.ShouldBe(4, "premise: a four-component JPEG");
        var adobe = jpeg.AsSpan().IndexOf("Adobe"u8);
        adobe.ShouldBeGreaterThan(0, "premise: an Adobe APP14 marker");
        var transform = jpeg[adobe + 11];

        var raw = JpegDecoder.Decode(jpeg, new JpegDecodeOptions { ColorTransform = JpegColorTransform.None }, JpegScale.Full).Pixels;
        var converted = JpegDecoder.Decode(jpeg).Pixels;
        raw.ShouldNotBe(converted);
        // None skips exactly the conversion the default runs: the frozen CmykRow (transform 0) or YcckRow (transform 2)
        // over the raw components gives it back, as the default path computes it without chroma subsampling.
        var n = 24 * 16;
        byte[] c = new byte[n], m = new byte[n], y = new byte[n], k = new byte[n];
        for (var i = 0; i < n; i++) (c[i], m[i], y[i], k[i]) = (raw[i * 4], raw[(i * 4) + 1], raw[(i * 4) + 2], raw[(i * 4) + 3]);
        var expected = new byte[n * 4];
        if (transform == 0) JpegColor.CmykRow(expected, c, m, y, k, n);
        else JpegColor.YcckRow(expected, c, m, y, k, n);
        converted.ShouldBe(expected);
    }

    [Fact]
    public void AnUnknownColorTransform_IsAnArgumentError()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            JpegDecoder.Decode(Photo(1), new JpegDecodeOptions { ColorTransform = (JpegColorTransform)9 }, JpegScale.Full));
    }
}
