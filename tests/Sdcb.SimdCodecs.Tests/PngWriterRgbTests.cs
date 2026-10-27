using System.IO.Compression;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The writer's colour-type-2 (RGB, no alpha) paths, and the two knobs that reach them:
/// <see cref="PngWriter.EncodeRgb8"/> / <see cref="PngWriter.EncodeRgb16"/> for a caller holding
/// packed RGB, and <see cref="PngWriteOptions.DiscardAlpha"/> for one holding RGBA it knows is
/// opaque.
/// </summary>
/// <remarks>
/// <para>The reader has accepted colour type 2 since it was written; the writer could only emit
/// 4 and 6, so an opaque render paid for an alpha plane that was a constant 0xFF(FF) all the way
/// through filtering, scoring, deflate and the file. On a 31.3 MP 16-bit frame that plane was a
/// quarter of the encode.</para>
/// <para><b>The load-bearing assertion is that the two routes agree byte for byte.</b> They are
/// deliberately different code paths into the same file -- one copies a row, the other gathers
/// three channels out of four while it swaps endianness -- and a gather that dropped the wrong
/// channel would still produce a perfectly valid PNG of subtly wrong colours, which no
/// size or round-trip check would catch.</para>
/// </remarks>
public sealed class PngWriterRgbTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(17, 5)]
    [InlineData(64, 64)]
    public void DiscardingAlphaWritesExactlyTheFileThePackedRgbEntryPointWrites_8Bit(int width, int height)
    {
        var (rgba, rgb) = Pixels8(width, height);

        var viaOption = PngWriter.Encode(rgba, width, height, new PngWriteOptions { DiscardAlpha = true });
        var viaPacked = PngWriter.EncodeRgb8(rgb, width, height, new PngWriteOptions());

        viaOption.ShouldBe(viaPacked);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(17, 5)]
    [InlineData(64, 64)]
    public void DiscardingAlphaWritesExactlyTheFileThePackedRgbEntryPointWrites_16Bit(int width, int height)
    {
        var (rgba, rgb) = Pixels16(width, height);

        var viaOption = PngWriter.EncodeRgba16(rgba, width, height, new PngWriteOptions { DiscardAlpha = true });
        var viaPacked = PngWriter.EncodeRgb16(rgb, width, height, new PngWriteOptions());

        viaOption.ShouldBe(viaPacked);
    }

    [Fact]
    public void ARgb8FileIsColourType2AndReadsBackTheSourcePixels()
    {
        const int width = 23;
        const int height = 9;
        var (_, rgb) = Pixels8(width, height);

        var decoded = PngReader.Decode(PngWriter.EncodeRgb8(rgb, width, height));

        decoded.ColorType.ShouldBe(2);
        decoded.BitDepth.ShouldBe(8);
        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.Pixels.ShouldBe(rgb);
    }

    [Fact]
    public void ARgb16FileIsColourType2AndReadsBackTheSourceSamples()
    {
        const int width = 23;
        const int height = 9;
        var (_, rgb) = Pixels16(width, height);

        var decoded = PngReader.Decode(PngWriter.EncodeRgb16(rgb, width, height));

        decoded.ColorType.ShouldBe(2);
        decoded.BitDepth.ShouldBe(16);

        // PNG stores 16-bit samples big-endian; the reader hands them back as raw bytes.
        var expected = new byte[rgb.Length * 2];
        for (var i = 0; i < rgb.Length; i++)
        {
            expected[i * 2] = (byte)(rgb[i] >> 8);
            expected[(i * 2) + 1] = (byte)rgb[i];
        }

        decoded.Pixels.ShouldBe(expected);
    }

    [Fact]
    public void DroppingAlphaKeepsTheColourChannelsInOrder()
    {
        // Three pixels whose channels are all distinguishable, so a gather that took the wrong
        // three of the four still shows up. Pure red / green / blue would not: swapping R and B
        // on a red pixel gives a blue one, but swapping R and A on it gives a red one back.
        ReadOnlySpan<byte> rgba = [10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255];

        var decoded = PngReader.Decode(PngWriter.Encode(rgba, 3, 1, new PngWriteOptions { DiscardAlpha = true }));

        decoded.Pixels.ShouldBe(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90 });
    }

    [Fact]
    public void TheCompressionLevelReachesDeflateWithoutChangingThePixels()
    {
        const int width = 96;
        const int height = 64;
        var (_, rgb) = Pixels8(width, height);

        var fastest = PngWriter.EncodeRgb8(rgb, width, height,
            new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest });
        var smallest = PngWriter.EncodeRgb8(rgb, width, height,
            new PngWriteOptions { CompressionLevel = CompressionLevel.SmallestSize });

        // The level must actually be plumbed through: identical lengths would mean it was ignored.
        smallest.Length.ShouldBeLessThan(fastest.Length);

        // ...and it must be a pure encoding choice, invisible on the way back out.
        PngReader.Decode(fastest).Pixels.ShouldBe(rgb);
        PngReader.Decode(smallest).Pixels.ShouldBe(rgb);
    }

    [Fact]
    public void ADefaultOptionsRgbaEncodeStillCarriesItsAlpha()
    {
        // The negative half of DiscardAlpha: the default must not have quietly become RGB.
        ReadOnlySpan<byte> rgba = [10, 20, 30, 44, 40, 50, 60, 88];

        var decoded = PngReader.Decode(PngWriter.Encode(rgba, 2, 1, new PngWriteOptions()));

        decoded.ColorType.ShouldBe(6);
        decoded.Pixels.ShouldBe(new byte[] { 10, 20, 30, 44, 40, 50, 60, 88 });
    }

    /// <summary>
    /// Deterministic pixels with enough structure that several row filters get chosen across the
    /// image, and enough noise that none of them is trivially perfect.
    /// </summary>
    private static (byte[] Rgba, byte[] Rgb) Pixels8(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        var rgb = new byte[width * height * 3];
        for (int p = 0, s4 = 0, s3 = 0; p < width * height; p++, s4 += 4, s3 += 3)
        {
            var x = p % width;
            var y = p / width;
            rgba[s4] = rgb[s3] = (byte)((x * 7) + (y * 3));
            rgba[s4 + 1] = rgb[s3 + 1] = (byte)((x * 3) ^ (y * 11));
            rgba[s4 + 2] = rgb[s3 + 2] = (byte)(200 - x + (y * 5));
            rgba[s4 + 3] = 255;
        }

        return (rgba, rgb);
    }

    private static (ushort[] Rgba, ushort[] Rgb) Pixels16(int width, int height)
    {
        var rgba = new ushort[width * height * 4];
        var rgb = new ushort[width * height * 3];
        for (int p = 0, s4 = 0, s3 = 0; p < width * height; p++, s4 += 4, s3 += 3)
        {
            var x = p % width;
            var y = p / width;
            rgba[s4] = rgb[s3] = (ushort)((x * 613) + (y * 271));
            rgba[s4 + 1] = rgb[s3 + 1] = (ushort)((x * 1031) ^ (y * 4099));
            rgba[s4 + 2] = rgb[s3 + 2] = (ushort)(50021 - (x * 13) + (y * 97));
            rgba[s4 + 3] = ushort.MaxValue;
        }

        return (rgba, rgb);
    }
}
