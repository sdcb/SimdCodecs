using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PngWriteOptions.Quantize"/>: the lossy palette mode. ImageMagick (libpng) reads every file back and must
/// see exactly the pixels our reader sees; the palette must fit its limit, keep translucency, follow the quality
/// target, and fall back to lossless output below the minimum quality.
/// </summary>
public sealed class PngQuantizeTests
{
    /// <summary>A soft gradient disc with a translucent edge and a drop shadow on transparency: thousands of RGBA colours.</summary>
    private static byte[] Sticker(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        double cx = width / 2.0, cy = height / 2.0, r = Math.Min(width, height) * 0.4;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var o = 4 * (y * width + x);
            var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            var disc = Math.Clamp((r - d) / 3 + 0.5, 0, 1);
            var shadow = 0.4 * Math.Clamp((r * 1.1 - d) / (r * 0.2), 0, 1);
            var a = disc + shadow * (1 - disc);
            if (a <= 0) continue;
            var k = disc / a;
            rgba[o] = (byte)Math.Round(x * 255.0 / width * k);
            rgba[o + 1] = (byte)Math.Round(y * 255.0 / height * k);
            rgba[o + 2] = (byte)Math.Round((x + y) * 127.0 / (width + height) * k + 60 * k);
            rgba[o + 3] = (byte)Math.Round(a * 255);
        }
        return rgba;
    }

    private static byte[] Decode(byte[] png, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        PngReader.Decode(png).ExpandToRgba8(rgba);
        return rgba;
    }

    private static void MagickSeesTheSame(byte[] png, byte[] ours)
    {
        using var image = new MagickImage(png);
        using var pixels = image.GetPixels();
        pixels.ToByteArray(PixelMapping.RGBA).ShouldBe(ours);
    }

    private static int Colours(byte[] rgba) =>
        Enumerable.Range(0, rgba.Length / 4).Select(i => BitConverter.ToUInt32(rgba, 4 * i)).Distinct().Count();

    [Theory]
    [InlineData(256, false)]
    [InlineData(256, true)]
    [InlineData(64, true)]
    [InlineData(16, false)]
    [InlineData(2, false)]
    public void Writes_an_indexed_file_within_the_limit_that_libpng_reads_the_same(int maxColors, bool dither)
    {
        const int width = 160, height = 120;
        var source = Sticker(width, height);
        var png = PngWriter.Encode(source, width, height,
            new PngWriteOptions { Quantize = new PngQuantizeOptions { MaxColors = maxColors, Dither = dither } });

        png[25].ShouldBe((byte)3, "indexed colour");
        var ours = Decode(png, width, height);
        MagickSeesTheSame(png, ours);
        Colours(ours).ShouldBeLessThanOrEqualTo(maxColors);
        // Translucency survives: partly transparent pixels stay partly transparent.
        if (maxColors >= 16) Enumerable.Range(0, width * height).Count(i => ours[4 * i + 3] is > 0 and < 255).ShouldBeGreaterThan(100);
        PngWriter.Encode(source, width, height,
                new PngWriteOptions { Quantize = new PngQuantizeOptions { MaxColors = maxColors, Dither = dither } })
            .ShouldBe(png, "deterministic");
    }

    [Fact]
    public void Lower_quality_means_fewer_colours_and_a_smaller_file()
    {
        const int width = 160, height = 120;
        var source = Sticker(width, height);
        int previousColours = int.MaxValue;
        long previousBytes = long.MaxValue;
        foreach (var quality in new[] { 100, 80, 50, 20, 5 })
        {
            var png = PngWriter.Encode(source, width, height,
                new PngWriteOptions { Quantize = new PngQuantizeOptions { MaxQuality = quality, Dither = false } });
            var colours = Colours(Decode(png, width, height));
            colours.ShouldBeLessThanOrEqualTo(previousColours, $"quality {quality}");
            ((long)png.Length).ShouldBeLessThanOrEqualTo(previousBytes, $"quality {quality}");
            (previousColours, previousBytes) = (colours, png.Length);
        }
        previousColours.ShouldBeLessThan(64);
    }

    [Fact]
    public void Below_the_minimum_quality_the_image_is_written_losslessly()
    {
        const int width = 160, height = 120;
        var source = Sticker(width, height);
        var png = PngWriter.Encode(source, width, height,
            new PngWriteOptions { Quantize = new PngQuantizeOptions { MaxColors = 4, MinQuality = 90 } });
        png[25].ShouldBe((byte)6, "truecolour with alpha");
        Decode(png, width, height).ShouldBe(source);
    }

    [Fact]
    public void An_image_that_fits_is_written_exactly_at_full_quality()
    {
        const int width = 40, height = 30;
        var random = new Random(3);
        uint[] colours = [.. Enumerable.Range(0, 200).Select(_ => (uint)random.Next() | 0x01000000u)]; // no alpha 0: those all become one colour
        var source = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++) BitConverter.TryWriteBytes(source.AsSpan(4 * i), colours[random.Next(colours.Length)]);
        var png = PngWriter.Encode(source, width, height, new PngWriteOptions { Quantize = new PngQuantizeOptions() });
        Decode(png, width, height).ShouldBe(source);
    }

    [Fact]
    public void Sixteen_bit_and_opaque_inputs_quantize()
    {
        const int width = 64, height = 48;
        var source = Sticker(width, height);
        var wide = source.Select(v => (ushort)(v * 257)).ToArray();
        var options = new PngWriteOptions { Quantize = new PngQuantizeOptions { MaxColors = 32 } };
        var fromWide = PngWriter.EncodeRgba16(wide, width, height, options);
        Decode(fromWide, width, height).ShouldBe(Decode(PngWriter.Encode(source, width, height, options), width, height),
            "16-bit samples v·257 quantize as the 8-bit v");

        var opaque = PngWriter.Encode(source, width, height, options with { DiscardAlpha = true });
        var pixels = Decode(opaque, width, height);
        Enumerable.Range(0, width * height).All(i => pixels[4 * i + 3] == 255).ShouldBeTrue();
        Colours(pixels).ShouldBeLessThanOrEqualTo(32);
    }

    [Fact]
    public void The_facade_passes_the_option_through()
    {
        const int width = 80, height = 60;
        var source = Sticker(width, height);
        var png = ImageCodecs.Encode(source, width, height, PixelFormat.Rgba32, ImageFormat.Png,
            options: new PngWriteOptions { Quantize = new PngQuantizeOptions { MaxColors = 16 } },
            cancellationToken: TestContext.Current.CancellationToken);
        png[25].ShouldBe((byte)3);
        Colours(Decode(png, width, height)).ShouldBeLessThanOrEqualTo(16);
    }

    [Theory]
    [InlineData(1, 0, 100)]
    [InlineData(257, 0, 100)]
    [InlineData(256, -1, 100)]
    [InlineData(256, 0, 101)]
    [InlineData(256, 60, 50)]
    public void Out_of_range_settings_are_refused(int maxColors, int min, int max)
    {
        var rgba = new byte[16];
        Should.Throw<ArgumentOutOfRangeException>(() => PngWriter.Encode(rgba, 2, 2, new PngWriteOptions
        {
            Quantize = new PngQuantizeOptions { MaxColors = maxColors, MinQuality = min, MaxQuality = max },
        }));
    }

    [Fact]
    public void Combinations_that_cannot_hold_are_refused()
    {
        var quantize = new PngQuantizeOptions();
        Should.Throw<ArgumentException>(() => PngWriter.EncodeRgb8(new byte[12], 2, 2,
            new PngWriteOptions { Quantize = quantize, TransparentColor = [0, 0, 0] }));
        Should.Throw<ArgumentException>(() => new PngAnimationEncoder(2, 2, new PngWriteOptions { Quantize = quantize }));
    }
}
