using System.Runtime.InteropServices;
using ImageMagick;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The floating-point predictor (Predictor 3, Adobe TIFF Technical Note 3) on the read path, with
/// libtiff (through Magick.NET) as the writer.
/// </summary>
/// <remarks>
/// <para>ImageMagick turns Predictor 3 on by itself for Deflate-compressed float output, so it is
/// what float TIFFs from ImageMagick, GDAL and other libtiff-based tools carry in practice.</para>
/// <para><b>The comparison is exact and shares nothing with the code under test:</b> the same image
/// is written twice by libtiff, once with Predictor 3 and once with no predictor, and the two must
/// decode to identical float bits. The no-predictor read goes through none of the predictor code,
/// and an inverted predictor that is off by a byte plane or a pixel cannot produce the same bits.</para>
/// </remarks>
public sealed class TiffFloatPredictorOracleTests
{
    private const int Width = 37;   // odd, so no row is a whole number of anything
    private const int Height = 23;

    [Theory]
    [InlineData(32, 1, "lsb", null)]
    [InlineData(32, 1, "msb", null)]
    [InlineData(32, 3, "lsb", null)]
    [InlineData(32, 3, "msb", null)]
    [InlineData(16, 1, "lsb", null)]
    [InlineData(16, 3, "msb", null)]
    [InlineData(32, 3, "lsb", "16x16")]
    [InlineData(32, 1, "msb", "16x16")]
    public void Predictor3_DecodesToTheSameFloatsAsTheUnpredictedFile(int bits, int channels, string endian, string? tiles)
    {
        var predicted = Write(bits, channels, endian, tiles, predictor: 3);
        var plain = Write(bits, channels, endian, tiles, predictor: 1);

        // The fixture's own premise first: libtiff really wrote the predictor we asked for.
        var layout = TiffReader.ReadLayout(predicted);
        layout.Predictor.ShouldBe(TiffPredictor.FloatingPoint);
        layout.SampleFormat.ShouldBe(TiffSampleFormat.IeeeFloat);
        layout.BitsPerSample.ShouldBe(bits);
        layout.IsTiled.ShouldBe(tiles is not null);
        layout.FileIsLittleEndian.ShouldBe(endian == "lsb");
        TiffReader.ReadLayout(plain).Predictor.ShouldBe(TiffPredictor.None);

        var expected = TiffReader.ReadPage(plain).Pixels;
        var actual = TiffReader.ReadPage(predicted).Pixels;
        actual.Length.ShouldBe(Width * Height * channels * (bits / 8));
        actual.ShouldBe(expected);

        // And the values are the image, not something that merely matches itself: a ramp in (0, 1].
        if (bits == 32)
        {
            var floats = MemoryMarshal.Cast<byte, float>(actual);
            floats[0].ShouldBe(Value(0, 0, 0), 1e-4f);
            floats[^1].ShouldBe(Value(Width - 1, Height - 1, channels - 1), 1e-4f);
        }
    }

    [Fact]
    public void Predictor3_SegmentReadEqualsTheWholePage()
    {
        var predicted = Write(32, 3, "msb", "16x16", predictor: 3);
        var page = TiffReader.ReadPage(predicted).Pixels;
        var layout = TiffReader.ReadLayout(predicted);

        var bytesPerPixel = layout.BytesPerPixel;
        for (var segment = 0; segment < layout.SegmentCount; segment++)
        {
            var buffer = new byte[layout.SegmentBytes(segment)];
            var rect = TiffReader.ReadSegment(predicted, layout, segment, buffer);
            for (var y = 0; y < rect.Height; y++)
            {
                var row = buffer.AsSpan(y * rect.Width * bytesPerPixel, rect.Width * bytesPerPixel);
                var whole = page.AsSpan((((rect.Y + y) * Width) + rect.X) * bytesPerPixel, rect.Width * bytesPerPixel);
                row.SequenceEqual(whole).ShouldBeTrue($"segment {segment} row {y}");
            }
        }
    }

    private static float Value(int x, int y, int c) => (1 + x + (y * Width) + (c * 977)) / 4096f;

    private static byte[] Write(int bits, int channels, string endian, string? tiles, int predictor)
    {
        // Handed over as HDRI quanta (0..Quantum.Max), always RGB; a grey image repeats its one value and is then marked grey.
        var data = new float[Width * Height * 3];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                for (var c = 0; c < 3; c++)
                    data[(((y * Width) + x) * 3) + c] = Value(x, y, channels == 1 ? 0 : c) * Quantum.Max;

        using var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings((uint)Width, (uint)Height, StorageType.Quantum, PixelMapping.RGB));
        if (channels == 1)
        {
            image.ColorType = ColorType.Grayscale;
            image.ColorSpace = ColorSpace.Gray;
        }

        image.Format = MagickFormat.Tiff;
        image.Depth = (uint)bits;
        image.Settings.Compression = CompressionMethod.Zip;
        image.Settings.SetDefine("quantum:format", "floating-point");
        image.Settings.SetDefine(MagickFormat.Tiff, "endian", endian);
        image.Settings.SetDefine(MagickFormat.Tiff, "predictor", predictor.ToString());
        if (tiles is not null) image.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", tiles);
        return image.ToByteArray();
    }
}
