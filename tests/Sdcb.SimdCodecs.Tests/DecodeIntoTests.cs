using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The <see cref="IImageDecoder.TryDecodeInto"/> contract, through the facade, for every codec: in every
/// <see cref="ImagePixelFormat"/>, at a padded stride and tightly packed, the pixels are
/// <see cref="IDecodedImage.ToRgba8"/> of <see cref="SimdCodecs.TryDecode"/>'s image converted by
/// <see cref="PixelConverterTests.Reference"/> (and, for integer images, the same as converting
/// <see cref="IDecodedImage.Pixels"/> directly), and the bytes between and after rows are untouched.
/// The native paths (JPEG writing BGR from its colour conversion, PNG converting a row at a time, WebP
/// swapping to BGRA in place) are what this pins against the generic definition. The exception the
/// contract makes: <see cref="ImagePixelFormat.Gray8"/> from a YCbCr JPEG is its Y plane, checked
/// against the plane the full colour decode reconstructs (<c>JpegDiagnostics.DecodePlanes</c>) and
/// held to within <see cref="MaxLumaDrift"/> of the RGB's luma where no channel clips.
/// </summary>
public sealed class DecodeIntoTests
{
    private const byte Sentinel = 0xA5;

    /// <summary>
    /// How far a YCbCr JPEG's Y may sit from the BT.601 luma of its decoded RGB where no channel clips:
    /// the two are the same quantity (JFIF's Y is that luma), apart from the colour conversion's rounding
    /// of each channel and the luma's own. A clipped channel discards part of the colour, so there the
    /// luma of the RGB has no bound relative to Y, and only the mean difference is checked.
    /// </summary>
    private const int MaxLumaDrift = 1;

    // A YCbCr JPEG's Y plane at full resolution, or null for every other input.
    private static byte[]? YPlane(string name, byte[] data, int width, int height)
    {
        if (!name.StartsWith("jpeg ", StringComparison.Ordinal))
            return null;
        var planes = Jpeg.JpegDiagnostics.DecodePlanes(data);
        if (planes.Length != 3 || name.Contains("rgb-coded", StringComparison.Ordinal))
            return null; // grey (its plane is its RGB), CMYK or RGB-coded (no luma plane)
        planes[0].Width.ShouldBe(width);
        planes[0].Height.ShouldBe(height);
        return planes[0].Samples;
    }

    public static TheoryData<string> Inputs() => [.. Corpus().Keys];

    private static readonly Lazy<Dictionary<string, byte[]>> CorpusCache = new(BuildCorpus);

    private static Dictionary<string, byte[]> Corpus() => CorpusCache.Value;

    private static Dictionary<string, byte[]> BuildCorpus()
    {
        var corpus = new Dictionary<string, byte[]>();

        // PNG: every colour type and depth, plain and Adam7, written by ImageMagick (libpng).
        (int ColorType, int Depth, bool PaletteAlpha)[] png =
        [
            (0, 1, false), (0, 4, false), (0, 8, false), (0, 16, false), (2, 8, false), (2, 16, false),
            (3, 2, false), (3, 8, false), (3, 8, true), (4, 8, false), (4, 16, false), (6, 8, false), (6, 16, false),
        ];
        foreach (var (colorType, depth, paletteAlpha) in png)
        foreach (var (width, height) in new[] { (1, 1), (37, 5), (70, 9) })
        foreach (var interlace in new[] { false, true })
        {
            corpus[$"png ct{colorType} d{depth}{(paletteAlpha ? " trns" : "")} {width}x{height}{(interlace ? " adam7" : "")}"] =
                PngRgba8ExpansionTests.Encode(colorType, depth, paletteAlpha, width, height, interlace);
        }

        // PNG with a tRNS transparent colour, which an 8-bit row cannot convert as it is: the key colour
        // gains alpha 0 in the RGBA expansion.
        var keyed = new byte[19 * 6 * 3];
        new Random(19).NextBytes(keyed);
        for (var i = 0; i < keyed.Length; i += 9)
            (keyed[i], keyed[i + 1], keyed[i + 2]) = (10, 20, 30);
        corpus["png rgb8 tRNS 19x6"] = Png.PngWriter.EncodeRgb8(keyed, 19, 6, new Png.PngWriteOptions { TransparentColor = [10, 20, 30] });
        var keyedGrey = keyed[..(19 * 6)];
        corpus["png grey8 tRNS 19x6"] = Png.PngWriter.EncodeGray8(keyedGrey, 19, 6, new Png.PngWriteOptions { TransparentColor = [keyedGrey[0]] });

        // JPEG: grey, colour at three samplings, progressive, CMYK, at sizes with partial MCUs.
        foreach (var (width, height) in new[] { (1, 1), (37, 19), (70, 33) })
        {
            var rgb = JpegDecoderOracleTests.MakeRgbPattern(width, height);
            corpus[$"jpeg grey {width}x{height}"] = JpegDecoderOracleTests.EncodeJpeg(rgb, width, height, gray: true);
            corpus[$"jpeg 4:2:0 {width}x{height}"] = JpegDecoderOracleTests.EncodeJpeg(rgb, width, height, sampling: "2x2");
            corpus[$"jpeg 4:2:2 {width}x{height}"] = JpegDecoderOracleTests.EncodeJpeg(rgb, width, height, sampling: "2x1");
            corpus[$"jpeg 4:4:4 progressive {width}x{height}"] = JpegDecoderOracleTests.EncodeJpeg(rgb, width, height, sampling: "1x1", progressive: true);
            corpus[$"jpeg cmyk {width}x{height}"] = JpegDecoderOracleTests.EncodeJpeg(rgb, width, height, cmyk: true);
        }
        corpus["jpeg gain map"] = GainMapFacadeTests.BuildUltraHdr(out _);
        // RGB-coded (component IDs 'R', 'G', 'B'): three components but no luma plane, so its grey is the
        // luma of its RGB.
        var pattern = JpegDecoderOracleTests.MakeRgbPattern(37, 19);
        corpus["jpeg rgb-coded 37x19"] = RelabelAsRgb(JpegDecoderOracleTests.EncodeJpeg(pattern, 37, 19, sampling: "1x1"));

        // TIFF: 16-bit grey and RGB, and 8-bit RGBA from ImageMagick.
        var random = new Random(5);
        int[] Samples(int n) => [.. Enumerable.Range(0, n).Select(_ => random.Next(65536))];
        corpus["tiff grey16 23x7"] = HdrTiff.Uint16Gray(23, 7, Samples(23 * 7));
        corpus["tiff rgb16 23x7"] = HdrTiff.Uint16Rgb(23, 7, Samples(23 * 7), Samples(23 * 7), Samples(23 * 7));
        corpus["tiff rgba8 31x6"] = MagickRgba(31, 6, MagickFormat.Tiff);

        corpus["jbig2"] = Jbig2FacadeTests.BuildFile();

        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        foreach (var file in Directory.GetFiles(Path.Combine(fixtures, "webp"), "*.webp").Order())
            corpus[$"webp {Path.GetFileName(file)}"] = File.ReadAllBytes(file);
        foreach (var file in Directory.GetFiles(Path.Combine(fixtures, "jpeg2000"), "*.j2k").Order().Take(6))
            corpus[$"j2k {Path.GetFileName(file)}"] = File.ReadAllBytes(file);

        return corpus;
    }

    /// <summary>
    /// Renames the three components of a baseline JPEG to 'R', 'G', 'B' in its SOF0 and SOS segments,
    /// which a decoder reads as "the samples are RGB" (no colour transform).
    /// </summary>
    private static byte[] RelabelAsRgb(byte[] jpeg)
    {
        var bytes = (byte[])jpeg.Clone();
        byte[] ids = [(byte)'R', (byte)'G', (byte)'B'];
        var pos = 2;
        while (pos + 4 <= bytes.Length && bytes[pos] == 0xFF)
        {
            int marker = bytes[pos + 1], length = (bytes[pos + 2] << 8) | bytes[pos + 3];
            if (marker == 0xC0)
            {
                bytes[pos + 9].ShouldBe((byte)3);
                for (var c = 0; c < 3; c++)
                    bytes[pos + 10 + 3 * c] = ids[c];
            }
            else if (marker == 0xDA)
            {
                bytes[pos + 4].ShouldBe((byte)3);
                for (var c = 0; c < 3; c++)
                    bytes[pos + 5 + 2 * c] = ids[c];
                return bytes;
            }
            pos += 2 + length;
        }
        throw new InvalidOperationException("no SOF0/SOS found");
    }

    private static byte[] MagickRgba(int width, int height, MagickFormat format)
    {
        var pixels = new byte[width * height * 4];
        new Random(width * height).NextBytes(pixels);
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGBA"));
        return image.ToByteArray(format);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void EveryFormatIsTheConvertedImage(string name)
    {
        var data = Corpus()[name];
        if (!SimdCodecs.TryDecode(data, out var image))
        {
            // Not every fixture is a still the facade presents (a broken or unsupported one): then the
            // display path refuses it too.
            foreach (var format in PixelConverterTests.Formats)
                SimdCodecs.TryDecodeInto(data, new byte[1 << 20], 0, format).ShouldBeFalse();
            return;
        }

        int width = image.Width, height = image.Height;
        var rgba = image.ToRgba8();
        var yPlane = YPlane(name, data, width, height);
        foreach (var format in PixelConverterTests.Formats)
        {
            var expected = PixelConverterTests.Reference(rgba, 4, SampleFormat.UInt8, format, width * height);
            if (image.SampleFormat != SampleFormat.Float32)
            {
                PixelConverterTests.Reference(image.Pixels, image.Channels, image.SampleFormat, format, width * height)
                    .ShouldBe(expected, $"{format}: converting Pixels differs from converting ToRgba8");
            }

            if (format == ImagePixelFormat.Gray8 && yPlane is not null)
            {
                long total = 0;
                for (var i = 0; i < expected.Length; i++)
                {
                    var drift = Math.Abs(yPlane[i] - expected[i]);
                    total += drift;
                    var clipped = false;
                    for (var c = 0; c < 3; c++)
                        clipped |= rgba[4 * i + c] is 0 or 255;
                    if (!clipped)
                        drift.ShouldBeLessThanOrEqualTo(MaxLumaDrift, $"pixel {i}: Y {yPlane[i]}, RGB luma {expected[i]}");
                }
                ((double)total / expected.Length).ShouldBeLessThan(0.5, "mean |Y - RGB luma|");
                expected = yPlane;
            }

            var rowBytes = width * PixelConverter.BytesPerPixel(format);
            foreach (var stride in new[] { 0, rowBytes + 7 })
            {
                var step = stride == 0 ? rowBytes : stride;
                var destination = new byte[step * (height - 1) + rowBytes + 13];
                Array.Fill(destination, Sentinel);

                SimdCodecs.TryDecodeInto(data, destination, stride, format).ShouldBeTrue($"{format} stride {stride}");

                for (var y = 0; y < height; y++)
                {
                    destination.AsSpan(y * step, rowBytes).ToArray()
                        .ShouldBe(expected.AsSpan(y * rowBytes, rowBytes).ToArray(), $"{format} stride {stride} row {y}");
                    var gap = y == height - 1 ? 13 : step - rowBytes;
                    destination.AsSpan(y * step + rowBytes, gap).ToArray()
                        .ShouldAllBe(v => v == Sentinel, $"{format} stride {stride}: wrote outside row {y}");
                }
            }

            // One byte short is refused, not written past.
            var tight = new byte[width * height * PixelConverter.BytesPerPixel(format) - 1];
            SimdCodecs.TryDecodeInto(data, tight, 0, format).ShouldBeFalse($"{format} short destination");
        }

        // The RGBA entry point is the Rgba32 layout.
        var direct = new byte[width * height * 4];
        SimdCodecs.TryDecodeIntoRgba8(data, direct).ShouldBeTrue();
        direct.ShouldBe(rgba);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void InfoDescribesTheDecodedImage(string name)
    {
        var data = Corpus()[name];
        if (!SimdCodecs.TryDecode(data, out var image))
            return;

        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        (info.Width, info.Height, info.Channels, info.SampleFormat)
            .ShouldBe((image.Width, image.Height, image.Channels, image.SampleFormat));
        info.BitDepth.ShouldBe(ExpectedBitDepth(name));
        info.Orientation.ShouldBe(ImageOrientation.Normal);
        // Animated WebP fixtures: the frames ImageMagick (libwebp's demuxer) reads; every other entry is a still.
        info.FrameCount.ShouldBe(name.StartsWith("webp anim", StringComparison.Ordinal)
            ? new MagickImageCollection(data).Count
            : 1);
    }

    // The bit depth each corpus entry was written with, from how it was made rather than from our reader.
    private static int ExpectedBitDepth(string name)
    {
        if (name.StartsWith("png ct3", StringComparison.Ordinal)) return 8; // palette entries are 8-bit
        if (name.StartsWith("png ct", StringComparison.Ordinal))
            return int.Parse(name.Split(' ')[2][1..], System.Globalization.CultureInfo.InvariantCulture);
        if (name == "jpeg gain map") return 32; // the reconstructed HDR is float
        if (name.StartsWith("j2k ", StringComparison.Ordinal))
        {
            // OpenJPEG's decode of the fixture sits beside it as a PGM / PPM whose maxval is 2^depth - 1.
            var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");
            var stem = Path.GetFileNameWithoutExtension(name[4..]);
            var reference = Directory.GetFiles(fixtures, stem + ".p?m").Single();
            var header = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(reference), 0, 32)
                .Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return 32 - System.Numerics.BitOperations.LeadingZeroCount(uint.Parse(header[3], System.Globalization.CultureInfo.InvariantCulture));
        }
        return name switch
        {
            "jbig2" => 1,
            "tiff grey16 23x7" or "tiff rgb16 23x7" => 16,
            _ => 8, // 8-bit PNG / JPEG / TIFF, WebP
        };
    }

    [Fact]
    public void TheCorpusDecodes()
    {
        // The theory above checks a refusal for an input that does not decode; this keeps it from
        // passing because everything was refused.
        var refused = Corpus().Where(c => !SimdCodecs.TryDecode(c.Value, out _)).Select(c => c.Key).ToArray();
        refused.Where(name => !name.StartsWith("webp ", StringComparison.Ordinal)).ShouldBeEmpty();
        refused.Length.ShouldBeLessThan(Corpus().Count(c => c.Key.StartsWith("webp ", StringComparison.Ordinal)) / 2);
    }

    [Fact]
    public void AStrideShorterThanARowIsRefused()
    {
        var data = Corpus()["jpeg 4:2:0 37x19"];
        SimdCodecs.TryDecodeInto(data, new byte[1 << 16], 37 * 3 - 1, ImagePixelFormat.Bgr24).ShouldBeFalse();
        SimdCodecs.TryDecodeInto(data, new byte[1 << 16], -1, ImagePixelFormat.Bgr24).ShouldBeFalse();
    }
}
