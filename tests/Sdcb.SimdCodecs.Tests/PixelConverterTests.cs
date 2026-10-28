using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PixelConverter"/> against <see cref="Reference"/>, a per-pixel transcription of the
/// mapping its documentation states, for every source layout (grey, grey+alpha, RGB, RGBA at 8 and 16
/// bits) and every <see cref="ImagePixelFormat"/>. Widths run past every vector step and tail, and past
/// the 256-pixel chunk of the 16-bit path; spans longer than the row check that nothing outside it is
/// read into the output or written. CI runs this on every tier (AVX2, Vector128, scalar, arm64).
/// </summary>
public sealed class PixelConverterTests
{
    public static readonly ImagePixelFormat[] Formats =
        [ImagePixelFormat.Rgba32, ImagePixelFormat.Bgra32, ImagePixelFormat.Rgb24, ImagePixelFormat.Bgr24, ImagePixelFormat.Gray8];

    public static TheoryData<int, SampleFormat, ImagePixelFormat> Layouts()
    {
        var data = new TheoryData<int, SampleFormat, ImagePixelFormat>();
        foreach (var channels in new[] { 1, 2, 3, 4 })
        foreach (var sample in new[] { SampleFormat.UInt8, SampleFormat.UInt16 })
        foreach (var format in Formats)
            data.Add(channels, sample, format);
        return data;
    }

    /// <summary>
    /// The mapping, one pixel at a time: 16-bit samples keep their high byte, grey broadcasts, a missing
    /// alpha is 255, alpha is dropped without compositing, colour goes to grey as
    /// (77 R + 150 G + 29 B + 128) >> 8.
    /// </summary>
    internal static byte[] Reference(ReadOnlySpan<byte> samples, int channels, SampleFormat sampleFormat, ImagePixelFormat format, int pixels)
    {
        var bpp = format switch { ImagePixelFormat.Gray8 => 1, ImagePixelFormat.Rgb24 or ImagePixelFormat.Bgr24 => 3, _ => 4 };
        var result = new byte[pixels * bpp];
        var source = samples.ToArray();
        for (var p = 0; p < pixels; p++)
        {
            byte S(int c) => sampleFormat == SampleFormat.UInt16
                ? (byte)(BitConverter.ToUInt16(source, (p * channels + c) * 2) >> 8)
                : source[p * channels + c];
            int r, g, b, a = 255;
            if (channels <= 2)
            {
                r = g = b = S(0);
                if (channels == 2) a = S(1);
            }
            else
            {
                r = S(0); g = S(1); b = S(2);
                if (channels == 4) a = S(3);
            }

            var o = p * bpp;
            switch (format)
            {
                case ImagePixelFormat.Rgba32: result[o] = (byte)r; result[o + 1] = (byte)g; result[o + 2] = (byte)b; result[o + 3] = (byte)a; break;
                case ImagePixelFormat.Bgra32: result[o] = (byte)b; result[o + 1] = (byte)g; result[o + 2] = (byte)r; result[o + 3] = (byte)a; break;
                case ImagePixelFormat.Rgb24: result[o] = (byte)r; result[o + 1] = (byte)g; result[o + 2] = (byte)b; break;
                case ImagePixelFormat.Bgr24: result[o] = (byte)b; result[o + 1] = (byte)g; result[o + 2] = (byte)r; break;
                default: result[o] = channels <= 2 ? (byte)r : (byte)((77 * r + 150 * g + 29 * b + 128) >> 8); break;
            }
        }
        return result;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ConvertRowMatchesTheReference(int channels, SampleFormat sampleFormat, ImagePixelFormat format)
    {
        var sampleBytes = sampleFormat == SampleFormat.UInt16 ? 2 : 1;
        var bpp = PixelConverter.BytesPerPixel(format);
        var random = new Random(channels * 100 + (int)sampleFormat * 10 + (int)format);
        int[] widths = [.. Enumerable.Range(0, 70), 100, 255, 256, 257, 300, 513, 1000];
        foreach (var width in widths)
        {
            // Trailing source bytes the row must not read into its output; trailing destination bytes it
            // must not write.
            var source = new byte[width * channels * sampleBytes + 37];
            random.NextBytes(source);
            var destination = new byte[width * bpp + 41];
            Array.Fill(destination, (byte)0xA5);

            PixelConverter.ConvertRow(source, channels, sampleFormat, destination, format, width);

            var expected = Reference(source, channels, sampleFormat, format, width);
            destination.AsSpan(0, width * bpp).ToArray().ShouldBe(expected, $"width {width}");
            destination.AsSpan(width * bpp).ToArray().ShouldAllBe(v => v == 0xA5, $"width {width}: wrote past the row");
        }
    }

    [Fact]
    public void GreyPixelsKeepTheirValueInGray8()
    {
        // The luma weights sum to 256, so R = G = B = v comes back as v for every v.
        var rgb = new byte[256 * 3];
        for (var v = 0; v < 256; v++)
            rgb[3 * v] = rgb[3 * v + 1] = rgb[3 * v + 2] = (byte)v;
        var grey = new byte[256];
        PixelConverter.ConvertRow(rgb, 3, SampleFormat.UInt8, grey, ImagePixelFormat.Gray8, 256);
        grey.ShouldBe(Enumerable.Range(0, 256).Select(v => (byte)v).ToArray());
    }

    [Fact]
    public void RgbaToBgraWorksInPlace()
    {
        var random = new Random(7);
        foreach (var width in new[] { 1, 3, 4, 5, 17, 64, 99 })
        {
            var row = new byte[width * 4];
            random.NextBytes(row);
            var expected = Reference(row, 4, SampleFormat.UInt8, ImagePixelFormat.Bgra32, width);
            PixelConverter.ConvertRow(row, 4, SampleFormat.UInt8, row, ImagePixelFormat.Bgra32, width);
            row.ShouldBe(expected);
        }
    }

    [Theory]
    [InlineData(10, 3, ImagePixelFormat.Rgba32, 0, 120, true, 40)]
    [InlineData(10, 3, ImagePixelFormat.Rgba32, 0, 119, false, 0)]
    [InlineData(10, 3, ImagePixelFormat.Bgr24, 32, 32 * 2 + 30, true, 32)]
    [InlineData(10, 3, ImagePixelFormat.Bgr24, 32, 32 * 2 + 29, false, 0)]
    [InlineData(10, 3, ImagePixelFormat.Gray8, 9, 100, false, 0)]     // stride shorter than a row
    [InlineData(10, 3, ImagePixelFormat.Gray8, -1, 100, false, 0)]
    [InlineData(10, 1, ImagePixelFormat.Gray8, 1000, 10, true, 1000)] // one row: the stride never applies
    [InlineData(0, 3, ImagePixelFormat.Gray8, 0, 100, false, 0)]
    [InlineData(10, 3, (ImagePixelFormat)5, 0, 1000, false, 0)]
    public void TryGetLayoutStatesTheRequirement(int width, int height, ImagePixelFormat format, int stride, int length, bool fits, int rowStride)
    {
        PixelConverter.TryGetLayout(width, height, format, stride, length, out var actual).ShouldBe(fits);
        actual.ShouldBe(rowStride);
    }

    [Fact]
    public void TryConvertLeavesTheRowPaddingAlone()
    {
        const int width = 23, height = 5, channels = 3;
        var pixels = new byte[width * height * channels];
        new Random(3).NextBytes(pixels);
        var image = new RasterImage(width, height, channels, SampleFormat.UInt8, pixels);
        const int stride = width * 4 + 9;
        var destination = new byte[stride * height];
        Array.Fill(destination, (byte)0x5A);

        PixelConverter.TryConvert(image, destination, stride, ImagePixelFormat.Bgra32).ShouldBeTrue();

        var expected = Reference(pixels, channels, SampleFormat.UInt8, ImagePixelFormat.Bgra32, width * height);
        for (var y = 0; y < height; y++)
        {
            destination.AsSpan(y * stride, width * 4).ToArray().ShouldBe(expected.AsSpan(y * width * 4, width * 4).ToArray());
            destination.AsSpan(y * stride + width * 4, 9).ToArray().ShouldAllBe(v => v == 0x5A);
        }
    }

    [Fact]
    public void FloatSamplesHaveNoEightBitProjection()
    {
        var image = new RasterImage(2, 2, 1, SampleFormat.Float32, new byte[16]);
        PixelConverter.TryConvert(image, new byte[16], 0, ImagePixelFormat.Gray8).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() =>
            PixelConverter.ConvertRow(new byte[16], 1, SampleFormat.Float32, new byte[4], ImagePixelFormat.Gray8, 4));
    }

    [Fact]
    public void ShortSpansAreRefused()
    {
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[11], 3, SampleFormat.UInt8, new byte[16], ImagePixelFormat.Rgba32, 4));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[12], 3, SampleFormat.UInt8, new byte[15], ImagePixelFormat.Rgba32, 4));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[23], 3, SampleFormat.UInt16, new byte[16], ImagePixelFormat.Rgba32, 4));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[16], 5, SampleFormat.UInt8, new byte[16], ImagePixelFormat.Rgba32, 1));
    }

    [Fact]
    public void ExpandToRgba8IsTheRgbaConversion()
    {
        var random = new Random(11);
        foreach (var channels in new[] { 1, 2, 3, 4 })
        foreach (var sample in new[] { SampleFormat.UInt8, SampleFormat.UInt16 })
        {
            const int width = 37, height = 11;
            var pixels = new byte[width * height * channels * (sample == SampleFormat.UInt16 ? 2 : 1)];
            random.NextBytes(pixels);
            var image = new RasterImage(width, height, channels, sample, pixels);
            image.ToRgba8().ShouldBe(Reference(pixels, channels, sample, ImagePixelFormat.Rgba32, width * height));
        }
    }
}
