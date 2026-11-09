using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// JPEG 2000 through the <c>Sdcb.SimdCodecs</c> facade: what it presents (grey and RGB, scaled
/// into the facade's sample formats, an ICC profile passed through) and what it refuses rather than
/// present in the wrong colours (four components, sYCC). The decoder itself is tested elsewhere;
/// this is the mapping onto the facade's contract.
/// </summary>
public class Jpeg2000FacadeTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    internal static byte[] File(string name) =>
        System.IO.File.ReadAllBytes(Path.Combine(FixtureDirectory, name));

    /// <summary>Both signatures are sniffed: the raw codestream's SOC+SIZ and the JP2 signature box.</summary>
    [Theory]
    [InlineData("rgb-struct64.j2k")]
    [InlineData("jp2-rgb-struct64.jp2")]
    public void TheFacade_RecognisesBothForms(string file)
    {
        ImageCodecs.CanDecode(File(file)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("rgb-struct64.j2k", 3, SampleFormat.UInt8)]
    [InlineData("jp2-gray-struct64.jp2", 1, SampleFormat.UInt8)]
    [InlineData("deep12-ramp64.j2k", 1, SampleFormat.UInt16)]
    public void TryReadInfo_ReportsWhatTheDecodeWillHold(string file, int channels, SampleFormat format)
    {
        ImageCodecs.TryReadInfo(File(file), out var info).ShouldBeTrue();

        info.Width.ShouldBe(64);
        info.Height.ShouldBe(64);
        info.Channels.ShouldBe(channels);
        info.SampleFormat.ShouldBe(format);
    }

    /// <summary>Eight-bit grey and RGB come through sample for sample, from either form.</summary>
    [Theory]
    [InlineData("rgb-struct64.j2k", "rgb-struct64.ppm")]
    [InlineData("jp2-rgb-struct64.jp2", "jp2-rgb-struct64.ppm")]
    [InlineData("dwt5-struct64.j2k", "dwt5-struct64.pgm")]
    public void TryDecode_GivesTheSourceSamples(string file, string source)
    {
        var expected = Pnm.Read(Path.Combine(FixtureDirectory, source));

        ImageCodecs.TryDecode(File(file), out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();

        image.Channels.ShouldBe(expected.Components);
        image.SampleFormat.ShouldBe(SampleFormat.UInt8);
        image.Pixels.ToArray().ShouldBe(expected.Samples.Select(s => (byte)s).ToArray());
        image.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
    }

    /// <summary>
    /// A 12-bit component is scaled to fill <see cref="SampleFormat.UInt16"/>, because the facade
    /// divides by the format's maximum: 4095 must read as white, not as a sixteenth of it.
    /// </summary>
    [Fact]
    public void TwelveBitSamples_FillTheSixteenBitRange()
    {
        var expected = Pnm.Read(Path.Combine(FixtureDirectory, "deep12-ramp64.pgm"));
        expected.MaxValue.ShouldBe(4095);

        ImageCodecs.TryDecode(File("deep12-ramp64.j2k"), out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();

        var samples = MemoryMarshal.Cast<byte, ushort>(image.Pixels).ToArray();
        samples.Length.ShouldBe(expected.Samples.Length);
        for (var i = 0; i < samples.Length; i++)
            samples[i].ShouldBe((ushort)((expected.Samples[i] * 65535L + 2047) / 4095));

        samples.ShouldContain((ushort)65535);
    }

    /// <summary>The 8-bit display path gives opaque RGBA of the same samples.</summary>
    [Fact]
    public void TryDecodeIntoRgba8_WritesOpaqueRgba()
    {
        var expected = Pnm.Read(Path.Combine(FixtureDirectory, "rgb-struct64.ppm"));
        var rgba = new byte[64 * 64 * 4];

        ImageCodecs.TryDecodeInto(File("rgb-struct64.j2k"), rgba, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();

        for (var i = 0; i < 64 * 64; i++)
        {
            rgba[i * 4].ShouldBe((byte)expected.Samples[i * 3]);
            rgba[i * 4 + 1].ShouldBe((byte)expected.Samples[i * 3 + 1]);
            rgba[i * 4 + 2].ShouldBe((byte)expected.Samples[i * 3 + 2]);
            rgba[i * 4 + 3].ShouldBe((byte)255);
        }
    }

    /// <summary>A JP2 file's restricted ICC profile becomes the image's profile.</summary>
    [Fact]
    public void AnIccProfile_IsPassedThrough()
    {
        byte[] profile = [0x10, 0x20, 0x30, 0x40];
        var jp2 = WithColour(File("jp2-rgb-struct64.jp2"), [2, 0, 0, .. profile]);

        ImageCodecs.TryDecode(jp2, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();

        image.Metadata.Icc.Span.ToArray().ShouldBe(profile);
    }

    /// <summary>
    /// sYCC samples are YCbCr, and the facade promises RGB or grey, so it refuses rather than hand
    /// them over as RGB. <see cref="Jpeg2000Decoder"/> still decodes them, reporting the colour.
    /// </summary>
    [Fact]
    public void SyccIsRefused()
    {
        var sycc = WithColour(File("jp2-rgb-struct64.jp2"), [1, 0, 0, 0, 0, 0, 18]);

        ImageCodecs.TryDecode(sycc, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        ImageCodecs.TryReadInfo(sycc, out _).ShouldBeFalse();
        Jpeg2000Decoder.Decode(sycc, cancellationToken: TestContext.Current.CancellationToken).Colour!.EnumeratedColourSpace.ShouldBe(Jp2EnumeratedColourSpace.Sycc);
    }

    /// <summary>
    /// Four components could be CMYK or RGB with alpha, and without a channel-definition box
    /// nothing says which, so the facade refuses them. The decoder reads them exactly, which the
    /// raw-sourced fixture shows: its planar source is the expected output.
    /// </summary>
    [Fact]
    public void FourComponents_AreRefusedByTheFacadeAndDecodedExactlyBehindIt()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-raw");
        var codestream = System.IO.File.ReadAllBytes(Path.Combine(directory, "cmyk16.j2k"));
        var planar = System.IO.File.ReadAllBytes(Path.Combine(directory, "cmyk16.raw"));

        ImageCodecs.CanDecode(codestream).ShouldBeTrue();
        ImageCodecs.TryDecode(codestream, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();

        var decoded = Jpeg2000Decoder.Decode(codestream, cancellationToken: TestContext.Current.CancellationToken);
        decoded.Components.ShouldBe(4);
        const int pixels = 16 * 16;
        for (var i = 0; i < pixels; i++)
            for (var c = 0; c < 4; c++)
                decoded.Samples[i * 4 + c].ShouldBe(planar[c * pixels + i]);
    }

    /// <summary>
    /// A scale denominator is a reduction: the facade decodes at 1/d by leaving out log2(d) levels, which is
    /// <c>opj_decompress -r</c>, exactly on the reversible path.
    /// </summary>
    [Theory]
    [InlineData("dwt5-struct64.j2k", ".pgm", 2)]
    [InlineData("dwt5-struct64.j2k", ".pgm", 8)]
    [InlineData("rgb-struct64.j2k", ".ppm", 4)]
    [InlineData("rgb-layers2-struct64.j2k", ".ppm", 8)]
    [InlineData("odd37x23.j2k", ".pgm", 2)]
    [InlineData("odd37x23.j2k", ".pgm", 4)]
    public void AScaledDecode_IsExactlyOpenJpegsReduction(string file, string extension, int denominator)
    {
        OpenJpegOracle.RequireOrSkip();
        var bytes = File(file);
        var expected = OpenJpegOracle.Decode(bytes, extension, "-r", int.Log2(denominator).ToString());

        ImageCodecs.TryDecode(bytes, out var image, new DecodeOptions { ScaleDenominator = denominator },
            TestContext.Current.CancellationToken).ShouldBeTrue();

        image.Width.ShouldBe(expected.Width);
        image.Height.ShouldBe(expected.Height);
        image.Pixels.ToArray().ShouldBe(expected.Samples.Select(s => (byte)s).ToArray());
        ImageCodecs.TryReadInfo(bytes, out var info).ShouldBeTrue();
        new DecodeOptions { ScaleDenominator = denominator }.GetScaledSize(info.Width, info.Height)
            .ShouldBe((image.Width, image.Height));
    }

    /// <summary>The 9/7 path under the tolerance policy of the reduced-resolution tests: within 1, at most 1% differing.</summary>
    [Fact]
    public void AScaledLossyDecode_IsWithinToleranceOfOpenJpegs()
    {
        OpenJpegOracle.RequireOrSkip();
        var bytes = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-lossy", "lossy97-rgb-struct64.j2k"));
        var expected = OpenJpegOracle.Decode(bytes, ".ppm", "-r", "2");

        ImageCodecs.TryDecode(bytes, out var image, new DecodeOptions { ScaleDenominator = 4 },
            TestContext.Current.CancellationToken).ShouldBeTrue();

        image.Width.ShouldBe(expected.Width);
        var pixels = image.Pixels.ToArray();
        var differing = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var difference = Math.Abs(pixels[i] - expected.Samples[i]);
            difference.ShouldBeLessThanOrEqualTo(1);
            if (difference != 0) differing++;
        }

        ((double)differing / pixels.Length).ShouldBeLessThanOrEqualTo(0.01);
    }

    /// <summary>TryGetClosestSize picks the largest reduction that still covers the request, and TryDecode honours it.</summary>
    [Theory]
    [InlineData(64, 64, 64, 1)]
    [InlineData(33, 33, 64, 1)]
    [InlineData(32, 32, 32, 2)]
    [InlineData(9, 9, 16, 4)]
    [InlineData(1, 1, 2, 32)]
    [InlineData(0, 0, 2, 32)]
    [InlineData(1000, 1000, 64, 1)]
    [InlineData(32, 33, 64, 1)]
    public void TryGetClosestSize_PicksTheLargestReductionThatCoversTheRequest(
        int requestedWidth, int requestedHeight, int expectedSize, int expectedDenominator)
    {
        var bytes = File("dwt5-struct64.j2k");

        ImageCodecs.TryGetClosestSize(bytes, requestedWidth, requestedHeight, out var width, out var height, out var denominator)
            .ShouldBeTrue();

        (width, height, denominator).ShouldBe((expectedSize, expectedSize, expectedDenominator));
        ImageCodecs.TryDecode(bytes, out var image, new DecodeOptions { ScaleDenominator = denominator },
            TestContext.Current.CancellationToken).ShouldBeTrue();
        (image.Width, image.Height).ShouldBe((width, height));
    }

    /// <summary>Odd sizes round up (⌈w / d⌉), and a codestream is never scaled natively past its levels: beyond them the
    /// decode at the largest reduction is box-filtered by the rest.</summary>
    [Fact]
    public void ClosestSize_RoundsUpAndNeverExceedsTheLevels()
    {
        ImageCodecs.TryGetClosestSize(File("odd37x23.j2k"), 1, 1, out var w, out var h, out var d).ShouldBeTrue();
        (w, h, d).ShouldBe((10, 6, 4)); // two levels: 37x23 -> 19x12 -> 10x6

        ImageCodecs.TryGetClosestSize(File("nodwt-struct32.j2k"), 1, 1, out w, out h, out d).ShouldBeTrue();
        (w, h, d).ShouldBe((32, 32, 1));

        foreach (var (file, k, native) in new[] { ("odd37x23.j2k", 8, 4), ("odd37x23.j2k", 32, 4), ("nodwt-struct32.j2k", 2, 1) })
        {
            var ct = TestContext.Current.CancellationToken;
            ImageCodecs.TryDecode(File(file), out var reduced, new DecodeOptions { ScaleDenominator = native }, ct).ShouldBeTrue();
            using (reduced)
            {
                var expected = BoxDownscaleTests.Reference(reduced.Pixels, reduced.Width, reduced.Height, reduced.Channels,
                    reduced.SampleFormat, k / native);
                ImageCodecs.TryDecode(File(file), out var scaled, new DecodeOptions { ScaleDenominator = k }, ct).ShouldBeTrue();
                using (scaled)
                {
                    ImageCodecs.TryReadInfo(File(file), out var info).ShouldBeTrue();
                    (scaled.Width, scaled.Height).ShouldBe(new DecodeOptions { ScaleDenominator = k }.GetScaledSize(info.Width, info.Height));
                    scaled.Pixels.ToArray().ShouldBe(expected, $"{file} d{k}");
                }
            }
        }
    }

    /// <summary>A region is in the scaled image's coordinates and is exactly that crop of the scaled decode.</summary>
    [Fact]
    public void ARegionOfAScaledDecode_IsACropOfTheScaledImage()
    {
        var bytes = File("rgb-layers2-struct64.j2k");
        var scaled = new DecodeOptions { ScaleDenominator = 4 };
        ImageCodecs.TryDecode(bytes, out var whole, scaled, TestContext.Current.CancellationToken).ShouldBeTrue();
        whole.Width.ShouldBe(16);

        var region = new PixelRect(3, 2, 9, 5);
        ImageCodecs.TryDecode(bytes, out var part, scaled with { Region = region }, TestContext.Current.CancellationToken).ShouldBeTrue();

        (part.Width, part.Height).ShouldBe((9, 5));
        var expected = new List<byte>();
        for (var y = region.Y; y < region.Y + region.Height; y++)
            expected.AddRange(whole.Pixels.Slice((y * 16 + region.X) * 3, region.Width * 3).ToArray());
        part.Pixels.ToArray().ShouldBe(expected.ToArray());

        // Outside the scaled image (but inside the stored one): refused.
        Should.Throw<ArgumentException>(() => ImageCodecs.TryDecode(bytes, out _,
            scaled with { Region = new PixelRect(10, 10, 20, 20) }, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The JP2 file with its colour specification box's body replaced. The fixture's header box
    /// holds the image header then the colour box, so both box lengths change with the body.
    /// </summary>
    internal static byte[] WithColour(byte[] jp2, byte[] colourBody)
    {
        var colr = jp2.AsSpan().IndexOf("colr"u8) - 4;
        var oldLength = BinaryPrimitives.ReadInt32BigEndian(jp2.AsSpan(colr));
        var header = jp2.AsSpan().IndexOf("jp2h"u8) - 4;
        var headerLength = BinaryPrimitives.ReadInt32BigEndian(jp2.AsSpan(header));

        byte[] box = [.. new byte[4], .. Encoding.ASCII.GetBytes("colr"), .. colourBody];
        BinaryPrimitives.WriteInt32BigEndian(box, box.Length);

        byte[] result = [.. jp2[..colr], .. box, .. jp2[(colr + oldLength)..]];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(header), headerLength - oldLength + box.Length);
        return result;
    }
}
