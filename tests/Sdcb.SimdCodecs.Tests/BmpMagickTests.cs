using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Foreign bytes: bitmaps ImageMagick writes (core, 40-byte and V4/V5 headers; 1-, 4- and 8-bit palettes, RLE8,
/// 16-bit subtypes, 24- and 32-bit) decode to exactly the pixels ImageMagick reads back from the same bytes, and
/// every encoder layout reads back in ImageMagick as the pixels we meant.
/// <para>
/// Two places where ImageMagick 14.13 is not the reference. Its RLE8 reader misreads its own RLE8 output (rows
/// after a run that pads a row past its edge), so that variant is held against the source pixels, losslessly
/// written. And it widens 16-bit masks with alpha (ARGB1555, ARGB4444) by shifting, where it widens 5-6-5 and
/// WIC widens all of them by repeating bits; those layouts are pinned by <see cref="BmpDecoderTests"/> and the
/// WIC oracle instead.
/// </para>
/// </summary>
public sealed class BmpMagickTests
{
    internal static MagickImage Source(int width, int height, int colors, bool alpha, int seed)
    {
        var random = new Random(seed);
        var palette = new byte[colors * 4];
        random.NextBytes(palette);
        for (var c = 0; c < colors; c++) palette[c * 4 + 3] = alpha ? (byte)(c * 37) : (byte)255;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++) palette.AsSpan(random.Next(colors) * 4, 4).CopyTo(pixels.AsSpan(i * 4));
        var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGBA"));
        return image;
    }

    /// <summary>ImageMagick's reading of <paramref name="bytes"/>, as 8-bit RGBA.</summary>
    internal static byte[] MagickRgba(byte[] bytes, MagickFormat format = MagickFormat.Bmp)
    {
        using var image = new MagickImage(bytes, format);
        using var pixels = image.GetPixels();
        return [.. pixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8))];
    }

    public static TheoryData<string> Variants() =>
    [
        "bmp3 truecolor", "bmp3 truecoloralpha", "bmp truecolor", "bmp truecoloralpha", "bmp2 truecolor",
        "bmp3 palette", "bmp3 palette rle", "bmp2 palette", "bmp3 bilevel", "bmp3 grayscale", "bmp3 palette16",
        "bmp rgb565", "bmp rgb555",
    ];

    [Theory]
    [MemberData(nameof(Variants))]
    public void Bitmaps_imagemagick_writes_decode_to_its_own_reading(string variant)
    {
        var parts = variant.Split(' ');
        var format = parts[0] switch { "bmp2" => MagickFormat.Bmp2, "bmp3" => MagickFormat.Bmp3, _ => MagickFormat.Bmp };
        foreach (var width in BmpDecoderTests.Widths)
        {
            var colors = parts[1] switch { "bilevel" => 2, "palette16" => 16, "palette" or "grayscale" => 200, _ => 1000 };
            var alpha = parts[1] is "truecoloralpha" or "argb1555" or "argb4444";
            using var image = Source(width, 7, colors, alpha, width * 31 + variant.Length);
            switch (parts[1])
            {
                case "palette" or "palette16": image.ColorType = ColorType.Palette; break;
                case "bilevel": image.ColorType = ColorType.Bilevel; break;
                case "grayscale": image.ColorType = ColorType.Grayscale; break;
                case "truecoloralpha": image.ColorType = ColorType.TrueColorAlpha; break;
                case "truecolor": image.ColorType = ColorType.TrueColor; break;
                default: image.Settings.SetDefine(MagickFormat.Bmp, "subtype", parts[1].ToUpperInvariant()); break;
            }
            if (parts.Length > 2) image.Settings.Compression = CompressionMethod.RLE;
            var bmp = image.ToByteArray(format);
            var info = BmpDecoder.ReadInfo(bmp);
            var because = $"{variant}, width {width}: {info}";
            if (parts.Length > 2) info.Compression.ShouldBe(BmpCompression.Rle8, because);

            var rgba = new byte[width * 7 * 4];
            BmpDecoder.DecodeTo(bmp, rgba);
            BmpDecoderTests.ShouldMatch(rgba, parts.Length > 2 ? image.ToByteArray(MagickFormat.Rgba) : MagickRgba(bmp), because);
            BmpDecoderTests.CheckEveryFormat(bmp, because);
        }
    }

    public static TheoryData<BmpEncodeFormat, BmpHeaderVersion, bool, bool> Layouts()
    {
        var data = new TheoryData<BmpEncodeFormat, BmpHeaderVersion, bool, bool>();
        foreach (var header in new[] { BmpHeaderVersion.Core, BmpHeaderVersion.Info, BmpHeaderVersion.V4, BmpHeaderVersion.V5 })
        foreach (var format in Enum.GetValues<BmpEncodeFormat>().Where(f => f != BmpEncodeFormat.Auto))
        foreach (var rle in new[] { false, true })
        foreach (var topDown in new[] { false, true })
        {
            var core = header == BmpHeaderVersion.Core;
            var palette = format is BmpEncodeFormat.Indexed1 or BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8;
            if (core && (topDown || rle || !(palette || format == BmpEncodeFormat.Rgb24))) continue;
            if (rle && (topDown || format is not (BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8))) continue;
            data.Add(format, header, rle, topDown);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Every_encoder_layout_reads_back_in_imagemagick(BmpEncodeFormat format, BmpHeaderVersion header, bool rle, bool topDown)
    {
        foreach (var width in BmpDecoderTests.Widths)
        {
            const int height = 6;
            var colors = format switch { BmpEncodeFormat.Indexed1 => 2, BmpEncodeFormat.Indexed4 => 16, _ => 256 };
            using var image = Source(width, height, colors, format == BmpEncodeFormat.Argb32, width + (int)format * 100);
            var rgba = image.ToByteArray(MagickFormat.Rgba);
            if (format is BmpEncodeFormat.Rgb555 or BmpEncodeFormat.Rgb565)
            {
                // Values the 5- and 6-bit channels hold exactly, so the read-back is exact too.
                for (var i = 0; i < rgba.Length; i++)
                {
                    var bits = format == BmpEncodeFormat.Rgb565 && i % 4 == 1 ? 6 : 5;
                    rgba[i] = i % 4 == 3 ? (byte)255 : BmpDecoderTests.Widen((uint)(rgba[i] >> (8 - bits)), bits);
                }
            }
            if (format != BmpEncodeFormat.Argb32)
            {
                for (var i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            }
            var options = new BmpEncodeOptions { Format = format, Header = header, Rle = rle, TopDown = topDown };
            var bmp = BmpEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32, options);
            var because = $"{format}, {header}, RLE {rle}, top-down {topDown}, width {width}";
            var info = BmpDecoder.ReadInfo(bmp);
            info.HeaderKind.ShouldBe(header switch
            {
                BmpHeaderVersion.Core => BmpHeaderKind.Core,
                BmpHeaderVersion.Info => BmpHeaderKind.Info,
                BmpHeaderVersion.V4 => BmpHeaderKind.V4,
                _ => BmpHeaderKind.V5,
            }, because);
            info.TopDown.ShouldBe(topDown, because);

            BmpDecoderTests.ShouldMatch(MagickRgba(bmp), rgba, because);
            var ours = new byte[rgba.Length];
            BmpDecoder.DecodeTo(bmp, ours);
            BmpDecoderTests.ShouldMatch(ours, rgba, because);
        }
    }
}
