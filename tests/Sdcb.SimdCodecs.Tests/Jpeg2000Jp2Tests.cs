using System.Buffers.Binary;
using System.Text;
using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The JP2 file format (T.800 Annex I): enough of it to find the codestream and report the colour
/// specification, and to refuse the boxes that would change what the components mean.
/// <para>
/// Two fixtures come from opj_compress and decode exactly, like every lossless fixture. Everything
/// opj_compress does not write — an ICC profile, a palette, a channel definition, a box with a
/// 64-bit or run-to-the-end length, a malformed length — is built here around a committed
/// codestream, which is safe for this layer in a way it would not be for the codestream itself:
/// the boxes are a container with no coding in them, so a builder cannot agree with the reader by
/// sharing its mistakes about the coded data.
/// </para>
/// </summary>
public class Jpeg2000Jp2Tests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    private static byte[] Codestream(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k"));

    /// <summary>OpenJPEG's own JP2 files decode exactly and say what colour they are.</summary>
    [Theory]
    [InlineData("jp2-rgb-struct64", Jp2EnumeratedColourSpace.Srgb)]
    [InlineData("jp2-gray-struct64", Jp2EnumeratedColourSpace.Greyscale)]
    public void OpenJpegJp2Files_DecodeExactlyAndReportTheirColour(string name, Jp2EnumeratedColourSpace colour)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".jp2"));
        var expected = Pnm.Read(Jpeg2000FixtureTests.SourceRaster(name));

        Jpeg2000Decoder.IsJp2(bytes).ShouldBeTrue();
        Jpeg2000Decoder.IsCodestream(bytes).ShouldBeFalse();

        var decoded = Jpeg2000Decoder.Decode(bytes);
        decoded.Components.ShouldBe(expected.Components);
        decoded.Samples.ShouldBe(expected.Samples);
        decoded.Colour.ShouldNotBeNull();
        decoded.Colour.Method.ShouldBe(1);
        decoded.Colour.EnumeratedColourSpace.ShouldBe(colour);
    }

    /// <summary>A raw codestream carries no colour specification, and reports none.</summary>
    [Fact]
    public void ARawCodestream_ReportsNoColour()
    {
        Jpeg2000Decoder.Decode(Codestream("rgb-struct64")).Colour.ShouldBeNull();
    }

    /// <summary>
    /// The wrapper built here decodes as the codestream inside it, so the tests below that vary the
    /// boxes test the boxes and nothing else.
    /// </summary>
    [Fact]
    public void ABuiltWrapper_DecodesAsItsCodestream()
    {
        var codestream = Codestream("rgb-struct64");
        var wrapped = Jp2(Header(ImageHeader(), EnumeratedColour(16)), Box("jp2c", codestream));

        Jpeg2000Decoder.Decode(wrapped).Samples.ShouldBe(Jpeg2000Decoder.Decode(codestream).Samples);
    }

    /// <summary>A restricted ICC profile (method 2) comes back as its bytes, unread.</summary>
    [Fact]
    public void AnIccColourSpecification_IsReportedAsItsProfile()
    {
        byte[] profile = [1, 2, 3, 4, 5, 6, 7, 8];
        var wrapped = Jp2(
            Header(ImageHeader(), Box("colr", [2, 0, 0, .. profile])),
            Box("jp2c", Codestream("rgb-struct64")));

        var colour = Jpeg2000Decoder.Decode(wrapped).Colour;

        colour.ShouldNotBeNull();
        colour.Method.ShouldBe(2);
        colour.EnumeratedColourSpace.ShouldBeNull();
        colour.IccProfile.ShouldBe(profile);
    }

    /// <summary>Only the first colour specification counts (I.5.3.3); a reader ignores the rest.</summary>
    [Fact]
    public void OnlyTheFirstColourSpecification_Counts()
    {
        var wrapped = Jp2(
            Header(ImageHeader(), EnumeratedColour(18), EnumeratedColour(16)),
            Box("jp2c", Codestream("rgb-struct64")));

        Jpeg2000Decoder.Decode(wrapped).Colour!.EnumeratedColourSpace.ShouldBe(Jp2EnumeratedColourSpace.Sycc);
    }

    /// <summary>
    /// The three boxes that change what the components mean are refused by name. Skipped, each
    /// would hand back samples whose meaning had silently changed: palette indices as if they were
    /// colour, channels out of order, an alpha channel drawn as colour.
    /// </summary>
    [Theory]
    [InlineData("pclr", "palette")]
    [InlineData("cmap", "component mapping")]
    [InlineData("cdef", "channel definition")]
    public void BoxesThatChangeTheMeaningOfComponents_AreRefused(string type, string named)
    {
        var wrapped = Jp2(
            Header(ImageHeader(), EnumeratedColour(16), Box(type, [0, 0, 0, 0])),
            Box("jp2c", Codestream("rgb-struct64")));

        Should.Throw<NotSupportedException>(() => Jpeg2000Decoder.Decode(wrapped)).Message.ShouldContain(named);
    }

    /// <summary>
    /// A box may give its length in 64 bits (LBox = 1, XLBox after the type), or say 0 to run to
    /// the end of the file; both are ordinary for a codestream box, which is often last.
    /// </summary>
    [Fact]
    public void ACodestreamBox_MayUseALongOrAnOpenLength()
    {
        var codestream = Codestream("rgb-struct64");
        var expected = Jpeg2000Decoder.Decode(codestream).Samples;

        byte[] extended = [0, 0, 0, 1, .. "jp2c"u8, .. BigEndian64(16 + codestream.Length), .. codestream];
        Jpeg2000Decoder.Decode(Jp2(Header(ImageHeader(), EnumeratedColour(16)), extended))
            .Samples.ShouldBe(expected);

        byte[] open = [0, 0, 0, 0, .. "jp2c"u8, .. codestream];
        Jpeg2000Decoder.Decode(Jp2(Header(ImageHeader(), EnumeratedColour(16)), open))
            .Samples.ShouldBe(expected);
    }

    /// <summary>
    /// Malformed containers are malformed data: a box longer than the file, a box too short for its
    /// own header, a file with no codestream, a header box that does not start with an image
    /// header. Each must be <see cref="InvalidDataException"/>, never an index out of range.
    /// </summary>
    [Fact]
    public void MalformedContainers_AreInvalidData()
    {
        var codestream = Codestream("rgb-struct64");
        var header = Header(ImageHeader(), EnumeratedColour(16));

        byte[] tooLong = [.. BigEndian32(8 + codestream.Length + 1), .. "jp2c"u8, .. codestream];
        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(Jp2(header, tooLong)));

        byte[] tooShort = [0, 0, 0, 4, .. "jp2c"u8, .. codestream];
        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(Jp2(header, tooShort)));

        Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(Jp2(header)));

        Should.Throw<InvalidDataException>(() =>
            Jpeg2000Decoder.Decode(Jp2(Header(EnumeratedColour(16), ImageHeader()), Box("jp2c", codestream))));

        var whole = Jp2(header, Box("jp2c", codestream));
        for (var keep = 12; keep < 80; keep += 7)
        {
            var truncated = whole[..keep];
            Should.Throw<InvalidDataException>(() => Jpeg2000Decoder.Decode(truncated));
        }
    }

    /// <summary>
    /// A file type box that does not list the <c>jp2 </c> brand is not a Part 1 file, and this
    /// decoder does not pretend to read Part 2.
    /// </summary>
    [Fact]
    public void AFileWithoutTheJp2Brand_IsRefused()
    {
        var bytes = Jp2(Header(ImageHeader(), EnumeratedColour(16)), Box("jp2c", Codestream("rgb-struct64")));

        // The file type box follows the 12-byte signature: LBox, TBox, BR, MinV, then CL — the
        // compatibility list, whose one entry here is the brand. Rename it.
        "jpx "u8.CopyTo(bytes.AsSpan(12 + 16));

        Should.Throw<NotSupportedException>(() => Jpeg2000Decoder.Decode(bytes)).Message.ShouldContain("jp2 ");
    }

    private static byte[] Jp2(params byte[][] boxes)
    {
        byte[] signature = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
        var fileType = Box("ftyp", [.. "jp2 "u8, 0, 0, 0, 0, .. "jp2 "u8]);
        return [.. signature, .. fileType, .. boxes.SelectMany(b => b)];
    }

    private static byte[] Header(params byte[][] boxes) => Box("jp2h", [.. boxes.SelectMany(b => b)]);

    /// <summary>ihdr for the 64x64, three-component, 8-bit fixtures, compression type 7.</summary>
    private static byte[] ImageHeader() =>
        Box("ihdr", [.. BigEndian32(64), .. BigEndian32(64), 0, 3, 7, 7, 0, 0]);

    private static byte[] EnumeratedColour(int colourSpace) =>
        Box("colr", [1, 0, 0, .. BigEndian32(colourSpace)]);

    private static byte[] Box(string type, byte[] body) =>
        [.. BigEndian32(8 + body.Length), .. Encoding.ASCII.GetBytes(type), .. body];

    private static byte[] BigEndian32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] BigEndian64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }
}
