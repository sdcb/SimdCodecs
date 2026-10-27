using ImageMagick;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// 1-, 2- and 4-bit greyscale and indexed PNGs decode, matching ImageMagick pixel for pixel. The reader refused them,
/// and an emoji font's 4-bit indexed CBDT bitmap (Noto Color Emoji's U+1F6D1) then fell through to an outline the font
/// does not have (pilot's rail, 2026-10-25).
/// </summary>
public sealed class PngSubByteDepthTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(4, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(4, 3)]
    public void ASubByteImageDecodesAsImageMagickReadsIt(int depth, int colorType)
    {
        // An odd width, so each row ends part-way through a byte and the padding bits are skipped.
        const int w = 13, h = 9;
        var levels = 1 << depth;
        using var image = new MagickImage(MagickColors.Black, w, h);
        using (var pixels = image.GetPixels())
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    // An indexed image keeps a palette entry spare, as ImageMagick writes one more than it is given colours.
                    var k = (x * 7 + y * 3) % (colorType == 3 ? Math.Max(1, levels - 1) : levels);
                    // Greyscale: the depth's own levels. Indexed: as many distinct colours as the depth holds.
                    byte[] rgb = colorType == 0
                        ? [(byte)(k * 255 / (levels - 1)), (byte)(k * 255 / (levels - 1)), (byte)(k * 255 / (levels - 1))]
                        : [(byte)(k * 37 % 256), (byte)(255 - k * 53 % 256), (byte)(k * 91 % 256)];
                    pixels.SetPixel(x, y, [.. rgb.Select(c => (float)(c * Quantum.Max / 255.0))]);
                }
            }
        }
        if (colorType == 3)
        {
            image.ColorType = ColorType.Palette;
        }
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        image.Settings.SetDefine(MagickFormat.Png, "color-type", colorType.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var png = image.ToByteArray(MagickFormat.Png);
        // IHDR's depth and colour type, so the test is about what it says it is.
        (png[24], png[25]).ShouldBe(((byte)depth, (byte)colorType));

        PngImageDecoder.TryDecodeRgba8(png, out var rgba, out var width, out var height).ShouldBeTrue();

        (width, height).ShouldBe((w, h));
        using var expected = new MagickImage(png);
        using var expectedPixels = expected.GetPixels();
        rgba.ShouldBe(expectedPixels.ToByteArray(PixelMapping.RGBA));
        PngReader.Decode(png).SourceBitDepth.ShouldBe(depth);
    }
}
