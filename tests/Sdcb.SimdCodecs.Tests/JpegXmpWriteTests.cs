using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using ImageMagick;
using Sdcb.SimdCodecs.Exif;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegXmp"/> and <see cref="JpegEncodeOptions.Xmp"/>: every property round-trips through
/// <see cref="JpegSegmentScanner"/> + <see cref="XDocument.Parse(string)"/>, XML-special and non-ASCII text is
/// escaped and restored, the APP1 size limit is exact and checked before any output, keys must be NCNames, EXIF and
/// XMP sit together in the right order, a null option changes no byte, and Magick.NET reads the profile back.
/// </summary>
public sealed class JpegXmpWriteTests
{
    private const int W = 16, H = 16;
    private const string CustomNs = "https://example.org/ns/test/1.0/";

    private static readonly XNamespace Xmp = JpegXmp.XmpNamespaceUri;
    private static readonly XNamespace Dc = JpegXmp.DublinCoreNamespaceUri;
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Custom = CustomNs;

    private static byte[] Pixels()
    {
        var p = new byte[W * H * 3];
        for (var i = 0; i < p.Length; i++) p[i] = (byte)(i * 7);
        return p;
    }

    private static byte[] Encode(JpegXmp? xmp, ReadOnlyMemory<byte>? exif = null) =>
        JpegEncoder.Encode(Pixels(), W, H, 3, new JpegEncodeOptions { Xmp = xmp, Exif = exif });

    private static JpegXmp AllFields() => new()
    {
        CreatorTool = "Sdcb.SimdCodecs.Jpeg test",
        Description = "A short description",
        CustomPrefix = "sa",
        CustomNamespaceUri = CustomNs,
        CustomProperties = [new("Target", "M31"), new("Exposure", "120.5")],
    };

    private static bool IsXmp(ReadOnlySpan<byte> payload) =>
        payload.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8);

    /// <summary>The XMP packet in a JPEG, found the way a consumer would: scan the APP1 segments.</summary>
    private static string ExtractPacket(byte[] jpeg)
    {
        foreach (var seg in JpegSegmentScanner.Scan(jpeg))
        {
            if (seg.Marker != 0xE1) continue;
            var payload = seg.Payload(jpeg);
            if (IsXmp(payload)) return Encoding.UTF8.GetString(payload[29..]);
        }
        throw new InvalidOperationException("No XMP APP1 segment.");
    }

    private static XElement Description(byte[] jpeg) =>
        XDocument.Parse(ExtractPacket(jpeg)).Descendants(Rdf + "Description").Single();

    [Fact]
    public void EveryProperty_RoundTripsThroughTheScannerAndXDocument()
    {
        var d = Description(Encode(AllFields()));

        d.Attribute(Xmp + "CreatorTool")!.Value.ShouldBe("Sdcb.SimdCodecs.Jpeg test");
        d.Attribute(Dc + "description")!.Value.ShouldBe("A short description");
        d.Attribute(Custom + "Target")!.Value.ShouldBe("M31");
        d.Attribute(Custom + "Exposure")!.Value.ShouldBe("120.5");
    }

    [Fact]
    public void XmlSpecialAndNonAsciiText_IsEscapedAndRestored()
    {
        const string text = "a < b > c & \"d\" 'e' &amp; </x:xmpmeta> Zażółć gęślą 星雲 🌌 é";
        var xmp = AllFields() with
        {
            CreatorTool = text,
            Description = text,
            CustomProperties = [new("Note", text)],
        };
        var jpeg = Encode(xmp);

        var d = Description(jpeg);
        d.Attribute(Xmp + "CreatorTool")!.Value.ShouldBe(text);
        d.Attribute(Dc + "description")!.Value.ShouldBe(text);
        d.Attribute(Custom + "Note")!.Value.ShouldBe(text);

        // On the wire the specials are references, never the raw characters inside an attribute value.
        var packet = ExtractPacket(jpeg);
        packet.ShouldContain("a &lt; b &gt; c &amp; &quot;d&quot; &apos;e&apos; &amp;amp; &lt;/x:xmpmeta&gt;");
    }

    [Fact]
    public void TabNewlineAndCarriageReturn_SurviveAttributeNormalisation()
    {
        const string text = "line one\nline two\r\n\tindented";
        var d = Description(Encode(new JpegXmp { Description = text }));
        d.Attribute(Dc + "description")!.Value.ShouldBe(text);
    }

    [Fact]
    public void NullPropertiesAreOmitted_EmptyOnesAreWritten()
    {
        var onlyTool = Description(Encode(new JpegXmp { CreatorTool = "t" }));
        onlyTool.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != Rdf + "about")
            .Select(a => a.Name).ShouldBe([Xmp + "CreatorTool"]);

        var empty = Description(Encode(new JpegXmp { Description = "", CreatorTool = "" }));
        empty.Attribute(Dc + "description")!.Value.ShouldBe("");
        empty.Attribute(Xmp + "CreatorTool")!.Value.ShouldBe("");

        // Nothing set is still a well-formed packet, with an empty Description.
        Description(Encode(new JpegXmp())).Attributes().Count(a => !a.IsNamespaceDeclaration).ShouldBe(1);
    }

    [Fact]
    public void Packet_IsWellFormedWithNoDtdOrProcessingInstructions()
    {
        var doc = XDocument.Parse(ExtractPacket(Encode(AllFields())));
        doc.DocumentType.ShouldBeNull();
        doc.Root!.Name.ShouldBe((XNamespace)"adobe:ns:meta/" + "xmpmeta");
        doc.Root.Element(Rdf + "RDF").ShouldNotBeNull();
    }

    // ------------------------------------------------------------------ placement

    [Fact]
    public void Xmp_FollowsApp0_AndPrecedesDqt()
    {
        var jpeg = Encode(AllFields());
        var markers = JpegSegmentScanner.Scan(jpeg).Select(s => s.Marker).ToArray();
        markers.Take(4).ShouldBe([(byte)0xD8, (byte)0xE0, (byte)0xE1, (byte)0xDB]);
    }

    [Fact]
    public void ExifAndXmpTogether_ExifFirst_BothReadable()
    {
        var exifBlob = ExifWriter.Build(new ExifWriteData(Software: "exif software", Artist: "exif artist"));
        var jpeg = Encode(AllFields(), exifBlob);

        var segs = JpegSegmentScanner.Scan(jpeg);
        segs.Select(s => s.Marker).Take(5).ShouldBe([(byte)0xD8, (byte)0xE0, (byte)0xE1, (byte)0xE1, (byte)0xDB]);
        segs[2].Payload(jpeg).StartsWith("Exif\0\0"u8).ShouldBeTrue();
        IsXmp(segs[3].Payload(jpeg)).ShouldBeTrue();

        var exif = ExifReader.FromJpeg(jpeg);
        exif.ShouldNotBeNull();
        exif.Software.ShouldBe("exif software");
        exif.Artist.ShouldBe("exif artist");
        Description(jpeg).Attribute(Xmp + "CreatorTool")!.Value.ShouldBe("Sdcb.SimdCodecs.Jpeg test");
    }

    [Fact]
    public void ImageStillDecodes_WithTheSegmentPresent()
    {
        var decoded = JpegDecoder.Decode(Encode(AllFields()));
        decoded.Width.ShouldBe(W);
        decoded.Height.ShouldBe(H);
    }

    // ------------------------------------------------------------------ null changes nothing

    [Fact]
    public void NullXmp_LeavesTheBytesIdentical()
    {
        var plain = JpegEncoder.Encode(Pixels(), W, H, 3);
        Encode(null).ShouldBe(plain);
        JpegEncoder.Encode(Pixels(), W, H, 3, new JpegEncodeOptions { Xmp = null }).ShouldBe(plain);

        // With EXIF only, setting Xmp = null changes nothing either.
        var blob = ExifWriter.Build(new ExifWriteData(Artist: "a"));
        Encode(null, blob).ShouldBe(JpegEncoder.Encode(Pixels(), W, H, 3, new JpegEncodeOptions { Exif = blob }));
    }

    [Fact]
    public void WithXmp_OnlyTheApp1SegmentIsAdded()
    {
        var plain = JpegEncoder.Encode(Pixels(), W, H, 3);
        var packet = AllFields().BuildPacket();
        var withXmp = Encode(AllFields());

        var inserted = 2 + 2 + 29 + packet.Length;
        withXmp.Length.ShouldBe(plain.Length + inserted);
        var at = 20; // SOI (2) + APP0 (18)
        withXmp.AsSpan(0, at).SequenceEqual(plain.AsSpan(0, at)).ShouldBeTrue();
        withXmp.AsSpan(at + inserted).SequenceEqual(plain.AsSpan(at)).ShouldBeTrue();
    }

    // ------------------------------------------------------------------ the APP1 size limit

    private static JpegXmp WithDescriptionOfPacketLength(int packetBytes)
    {
        var baseLength = new JpegXmp { Description = "" }.BuildPacket().Length;
        return new JpegXmp { Description = new string('a', packetBytes - baseLength) };
    }

    [Fact]
    public void PacketExactlyAtTheLimit_IsAcceptedAndFillsTheSegment()
    {
        JpegEncodeOptions.MaxXmpPacketBytes.ShouldBe(65504);
        var xmp = WithDescriptionOfPacketLength(JpegEncodeOptions.MaxXmpPacketBytes);
        xmp.BuildPacket().Length.ShouldBe(65504);

        var jpeg = Encode(xmp);
        var seg = JpegSegmentScanner.Scan(jpeg)[2];
        seg.Marker.ShouldBe((byte)0xE1);
        seg.TotalLength.ShouldBe(2 + 65535);
        (seg.PayloadLength + 2).ShouldBe(65535);
        // The next marker is the DQT, so nothing overran.
        jpeg[seg.SegmentOffset + seg.TotalLength].ShouldBe((byte)0xFF);
        jpeg[seg.SegmentOffset + seg.TotalLength + 1].ShouldBe((byte)0xDB);
        Description(jpeg).Attribute(Dc + "description")!.Value.Length.ShouldBeGreaterThan(65000);
    }

    [Fact]
    public void PacketOneOverTheLimit_IsRefusedBeforeAnyOutput()
    {
        var xmp = WithDescriptionOfPacketLength(JpegEncodeOptions.MaxXmpPacketBytes + 1);
        var options = new JpegEncodeOptions { Xmp = xmp };
        Should.Throw<ArgumentException>(() => JpegEncoder.Encode(Pixels(), W, H, 3, options))
            .ParamName.ShouldBe("options");
        Should.Throw<ArgumentException>(() => xmp.BuildPacket());
    }

    [Fact]
    public void LimitIsCountedInUtf8BytesNotCharacters()
    {
        // 3 bytes each in UTF-8: fewer than the limit in characters, over it in bytes.
        var xmp = new JpegXmp { Description = new string('星', 30000) };
        Should.Throw<ArgumentException>(() => Encode(xmp));
    }

    [Fact]
    public void LimitIsCheckedBeforeThePixelBufferIsLookedAt()
    {
        var options = new JpegEncodeOptions { Xmp = WithDescriptionOfPacketLength(JpegEncodeOptions.MaxXmpPacketBytes + 1) };
        Should.Throw<ArgumentException>(() => JpegEncoder.Encode(new byte[1], W, H, 3, options))
            .ParamName.ShouldBe("options");
    }

    // ------------------------------------------------------------------ refusals

    [Theory]
    [InlineData("1starts-with-digit")]
    [InlineData("has space")]
    [InlineData("has:colon")]
    [InlineData("has\"quote")]
    [InlineData("a<b")]
    [InlineData("")]
    public void CustomKeyThatIsNotAnNcName_IsRefused(string key)
    {
        var xmp = AllFields() with { CustomProperties = [new(key, "v")] };
        Should.Throw<ArgumentException>(() => Encode(xmp)).ParamName.ShouldBe("options");
    }

    [Theory]
    [InlineData("Target")]
    [InlineData("_under.score-and-dash9")]
    [InlineData("Zażółć")]
    public void CustomKeyThatIsAnNcName_IsAccepted(string key)
    {
        var d = Description(Encode(AllFields() with { CustomProperties = [new(key, "v")] }));
        d.Attribute(Custom + key)!.Value.ShouldBe("v");
    }

    [Fact]
    public void DuplicateKeys_AreRefused() =>
        Should.Throw<ArgumentException>(() => Encode(AllFields() with { CustomProperties = [new("k", "1"), new("k", "2")] }));

    [Fact]
    public void CustomProperties_NeedAPrefixAndANamespace()
    {
        var props = new KeyValuePair<string, string>[] { new("k", "v") };
        Should.Throw<ArgumentException>(() => Encode(new JpegXmp { CustomProperties = props }));
        Should.Throw<ArgumentException>(() => Encode(new JpegXmp { CustomProperties = props, CustomPrefix = "p" }));
        Should.Throw<ArgumentException>(() => Encode(new JpegXmp { CustomProperties = props, CustomNamespaceUri = CustomNs }));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("rdf")]
    [InlineData("xmp")]
    [InlineData("dc")]
    [InlineData("xml")]
    [InlineData("XMLfoo")]
    [InlineData("1p")]
    [InlineData("a:b")]
    public void BadOrReservedPrefix_IsRefused(string prefix) =>
        Should.Throw<ArgumentException>(() => Encode(AllFields() with { CustomPrefix = prefix }));

    [Theory]
    [InlineData(JpegXmp.XmpNamespaceUri)]
    [InlineData(JpegXmp.DublinCoreNamespaceUri)]
    [InlineData("http://www.w3.org/1999/02/22-rdf-syntax-ns#")]
    [InlineData("adobe:ns:meta/")]
    public void ReservedNamespaceUri_IsRefused(string uri) =>
        Should.Throw<ArgumentException>(() => Encode(AllFields() with { CustomNamespaceUri = uri }));

    [Theory]
    [InlineData("bell \a char")]
    [InlineData("nul \0 char")]
    [InlineData("nonchar ￿")]
    public void TextXmlCannotCarry_IsRefused(string text) => AssertRefusedEverywhere(text);

    // xunit cannot carry a lone surrogate through InlineData (it round-trips them to U+FFFD), so these are built here.
    [Fact]
    public void UnpairedSurrogates_AreRefused()
    {
        AssertRefusedEverywhere("unpaired " + '\ud800' + " high");
        AssertRefusedEverywhere("lone " + '\udc00' + " low");
        AssertRefusedEverywhere("reversed " + '\udc00' + '\ud800' + " pair");
        // A proper pair is fine.
        Description(Encode(new JpegXmp { Description = "ok 🌌" })).Attribute(Dc + "description")!.Value.ShouldBe("ok 🌌");
    }

    private static void AssertRefusedEverywhere(string text)
    {
        Should.Throw<ArgumentException>(() => Encode(new JpegXmp { Description = text }));
        Should.Throw<ArgumentException>(() => Encode(new JpegXmp { CreatorTool = text }));
        Should.Throw<ArgumentException>(() => Encode(AllFields() with { CustomProperties = [new("k", text)] }));
    }

    [Fact]
    public void NamespaceUriWithSpecials_IsEscapedAndRestored()
    {
        const string uri = "https://example.org/a?x=1&y=\"2\"<3>";
        var d = Description(Encode(AllFields() with { CustomNamespaceUri = uri }));
        d.Attribute((XNamespace)uri + "Target")!.Value.ShouldBe("M31");
    }

    // ------------------------------------------------------------------ an external reader

    /// <summary>
    /// Magick.NET (libjpeg for the container, its own XMP profile handling) run against the packet above: the APP1
    /// segment arrives as the XMP profile, the raw bytes are the packet written, and the attribute values read back
    /// through its XDocument view.
    /// </summary>
    [Fact]
    public void MagickXmpProfile_ReadsTheWrittenPacket()
    {
        const string text = "a < b & \"c\" Zażółć 星雲";
        var xmp = AllFields() with { Description = text };
        using var image = new MagickImage(Encode(xmp));

        var profile = image.GetXmpProfile();
        profile.ShouldNotBeNull();
        var raw = profile.ToByteArray();
        Encoding.UTF8.GetString(raw).ShouldBe(Encoding.UTF8.GetString(xmp.BuildPacket()));

        var d = XDocument.Load(new System.IO.MemoryStream(raw)).Descendants(Rdf + "Description").Single();
        d.Attribute(Xmp + "CreatorTool")!.Value.ShouldBe("Sdcb.SimdCodecs.Jpeg test");
        d.Attribute(Dc + "description")!.Value.ShouldBe(text);
        d.Attribute(Custom + "Target")!.Value.ShouldBe("M31");
    }
}
