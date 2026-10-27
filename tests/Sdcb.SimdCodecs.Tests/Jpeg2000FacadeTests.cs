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

    private static byte[] File(string name) =>
        System.IO.File.ReadAllBytes(Path.Combine(FixtureDirectory, name));

    /// <summary>Both signatures are sniffed: the raw codestream's SOC+SIZ and the JP2 signature box.</summary>
    [Theory]
    [InlineData("rgb-struct64.j2k")]
    [InlineData("jp2-rgb-struct64.jp2")]
    public void TheFacade_RecognisesBothForms(string file)
    {
        SimdCodecs.CanDecode(File(file)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("rgb-struct64.j2k", 3, SampleFormat.UInt8)]
    [InlineData("jp2-gray-struct64.jp2", 1, SampleFormat.UInt8)]
    [InlineData("deep12-ramp64.j2k", 1, SampleFormat.UInt16)]
    public void TryReadInfo_ReportsWhatTheDecodeWillHold(string file, int channels, SampleFormat format)
    {
        SimdCodecs.TryReadInfo(File(file), out var info).ShouldBeTrue();

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

        SimdCodecs.TryDecode(File(file), out var image).ShouldBeTrue();

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

        SimdCodecs.TryDecode(File("deep12-ramp64.j2k"), out var image).ShouldBeTrue();

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

        SimdCodecs.TryDecodeIntoRgba8(File("rgb-struct64.j2k"), rgba).ShouldBeTrue();

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

        SimdCodecs.TryDecode(jp2, out var image).ShouldBeTrue();

        image.IccProfile.ToArray().ShouldBe(profile);
    }

    /// <summary>
    /// sYCC samples are YCbCr, and the facade promises RGB or grey, so it refuses rather than hand
    /// them over as RGB. <see cref="Jpeg2000Decoder"/> still decodes them, reporting the colour.
    /// </summary>
    [Fact]
    public void SyccIsRefused()
    {
        var sycc = WithColour(File("jp2-rgb-struct64.jp2"), [1, 0, 0, 0, 0, 0, 18]);

        SimdCodecs.TryDecode(sycc, out _).ShouldBeFalse();
        SimdCodecs.TryReadInfo(sycc, out _).ShouldBeFalse();
        Jpeg2000Decoder.Decode(sycc).Colour!.EnumeratedColourSpace.ShouldBe(Jp2EnumeratedColourSpace.Sycc);
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

        SimdCodecs.CanDecode(codestream).ShouldBeTrue();
        SimdCodecs.TryDecode(codestream, out _).ShouldBeFalse();

        var decoded = Jpeg2000Decoder.Decode(codestream);
        decoded.Components.ShouldBe(4);
        const int pixels = 16 * 16;
        for (var i = 0; i < pixels; i++)
            for (var c = 0; c < 4; c++)
                decoded.Samples[i * 4 + c].ShouldBe(planar[c * pixels + i]);
    }

    /// <summary>
    /// The JP2 file with its colour specification box's body replaced. The fixture's header box
    /// holds the image header then the colour box, so both box lengths change with the body.
    /// </summary>
    private static byte[] WithColour(byte[] jp2, byte[] colourBody)
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
