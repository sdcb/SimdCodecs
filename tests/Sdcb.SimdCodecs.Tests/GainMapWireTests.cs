using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Wire-format tests for the two Ultra HDR metadata carriers: the hdrgm /
/// GContainer XMP packets and the MPF APP2 index. The byte-level asserts mirror
/// what the Chromium/Skia parser and libultrahdr actually require — offsets
/// relative to the MP endian field, log2 encoding of boost/capacity ratios,
/// required-field behaviour — because those are the details that silently
/// downgrade a file to SDR when wrong.
/// </summary>
public sealed class GainMapWireTests
{
    private static readonly GainMapMetadata Sample = new()
    {
        GainMapMin = 1.0,
        GainMapMax = 5.0,
        Gamma = 1.0,
        OffsetSdr = 1.0 / 64,
        OffsetHdr = 1.0 / 64,
        HdrCapacityMin = 1.0,
        HdrCapacityMax = 5.0,
    };

    /// <summary>Strips the APP1 framing (FF E1 + length + XMP URI) off a segment.</summary>
    private static byte[] PacketOf(byte[] app1Segment) =>
        app1Segment[(4 + GainMapXmp.App1Identifier.Length)..];

    // ------------------------------------------------------------------- XMP

    [Fact]
    public void GainMapXmp_RoundTrips_Metadata()
    {
        var packet = PacketOf(GainMapXmp.WriteGainMapApp1(Sample));

        GainMapXmp.TryParseGainMapMetadata(packet, out var parsed).ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed.GainMapMin.ShouldBe(Sample.GainMapMin, tolerance: 1e-9);
        parsed.GainMapMax.ShouldBe(Sample.GainMapMax, tolerance: 1e-9);
        parsed.Gamma.ShouldBe(Sample.Gamma, tolerance: 1e-12);
        parsed.OffsetSdr.ShouldBe(Sample.OffsetSdr, tolerance: 1e-12);
        parsed.OffsetHdr.ShouldBe(Sample.OffsetHdr, tolerance: 1e-12);
        parsed.HdrCapacityMin.ShouldBe(Sample.HdrCapacityMin, tolerance: 1e-9);
        parsed.HdrCapacityMax.ShouldBe(Sample.HdrCapacityMax, tolerance: 1e-9);
        parsed.BaseRenditionIsHdr.ShouldBeFalse();
    }

    [Fact]
    public void GainMapXmp_PowerOfTwo_Ratios_RoundTrip_Exactly()
    {
        // log2/exp2 of powers of two are exact in IEEE double — the wire
        // conversion must not perturb them at all.
        var meta = Sample with { GainMapMax = 4.0, HdrCapacityMax = 8.0 };
        var packet = PacketOf(GainMapXmp.WriteGainMapApp1(meta));

        GainMapXmp.TryParseGainMapMetadata(packet, out var parsed).ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed.GainMapMin.ShouldBe(1.0);
        parsed.GainMapMax.ShouldBe(4.0);
        parsed.HdrCapacityMax.ShouldBe(8.0);
    }

    [Fact]
    public void GainMapXmp_Wire_Is_WellFormed_Xml_With_Log2_Values()
    {
        var segment = GainMapXmp.WriteGainMapApp1(Sample);

        // APP1 framing: marker, big-endian length (covers everything after the
        // marker bytes), the XMP namespace URI + NUL.
        segment[0].ShouldBe((byte)0xFF);
        segment[1].ShouldBe((byte)0xE1);
        BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(2)).ShouldBe((ushort)(segment.Length - 2));
        segment.AsSpan(4, GainMapXmp.App1Identifier.Length).ToArray().ShouldBe(GainMapXmp.App1Identifier.ToArray());

        // The packet parses as XML and carries hdrgm values in log2.
        var doc = XDocument.Parse(Encoding.UTF8.GetString(PacketOf(segment)));
        XNamespace hdrgm = "http://ns.adobe.com/hdr-gain-map/1.0/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var description = doc.Descendants(rdf + "Description").Single();
        description.Attribute(hdrgm + "Version")!.Value.ShouldBe("1.0");
        double.Parse(description.Attribute(hdrgm + "GainMapMax")!.Value, CultureInfo.InvariantCulture).ShouldBe(Math.Log2(5.0), tolerance: 1e-12);
        double.Parse(description.Attribute(hdrgm + "GainMapMin")!.Value, CultureInfo.InvariantCulture).ShouldBe(0.0);
        description.Attribute(hdrgm + "BaseRenditionIsHDR")!.Value.ShouldBe("False");
    }

    [Fact]
    public void GainMapXmp_Parser_Applies_Spec_Defaults()
    {
        // Only the three required fields (libultrahdr's parse contract) — the
        // rest must come back as the hdrgm defaults.
        var packet = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/"
                    hdrgm:Version="1.0" hdrgm:GainMapMax="2" hdrgm:HDRCapacityMax="2"/>
              </rdf:RDF>
            </x:xmpmeta>
            """u8;

        GainMapXmp.TryParseGainMapMetadata(packet, out var parsed).ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed.GainMapMax.ShouldBe(4.0);        // 2 stops
        parsed.HdrCapacityMax.ShouldBe(4.0);
        parsed.GainMapMin.ShouldBe(1.0);        // defaults
        parsed.Gamma.ShouldBe(1.0);
        parsed.OffsetSdr.ShouldBe(1.0 / 64);
        parsed.OffsetHdr.ShouldBe(1.0 / 64);
        parsed.HdrCapacityMin.ShouldBe(1.0);
    }

    [Fact]
    public void GainMapXmp_Parser_Rejects_Missing_Required_Fields()
    {
        var noMax = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/"
                    hdrgm:Version="1.0" hdrgm:HDRCapacityMax="2"/>
              </rdf:RDF>
            </x:xmpmeta>
            """u8;

        GainMapXmp.TryParseGainMapMetadata(noMax, out _).ShouldBeFalse();
        GainMapXmp.TryParseGainMapMetadata("not xml at all"u8, out _).ShouldBeFalse();
    }

    [Fact]
    public void GainMapXmp_Parser_Accepts_Element_Form()
    {
        // ACR/libultrahdr write attributes, but XMP equally allows properties as
        // child elements — including the per-channel rdf:Seq form (first channel wins).
        var packet = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description rdf:about="" xmlns:hdrgm="http://ns.adobe.com/hdr-gain-map/1.0/">
                  <hdrgm:Version>1.0</hdrgm:Version>
                  <hdrgm:GainMapMax><rdf:Seq><rdf:li>3</rdf:li><rdf:li>3</rdf:li><rdf:li>3</rdf:li></rdf:Seq></hdrgm:GainMapMax>
                  <hdrgm:HDRCapacityMax>3</hdrgm:HDRCapacityMax>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """u8;

        GainMapXmp.TryParseGainMapMetadata(packet, out var parsed).ShouldBeTrue();
        parsed.ShouldNotBeNull();
        parsed.GainMapMax.ShouldBe(8.0);        // 3 stops
        parsed.HdrCapacityMax.ShouldBe(8.0);
    }

    [Fact]
    public void ContainerXmp_RoundTrips_GainMap_Length()
    {
        var packet = PacketOf(GainMapXmp.WritePrimaryApp1(48213));

        GainMapXmp.TryParseContainerGainMapLength(packet, out var length).ShouldBeTrue();
        length.ShouldBe(48213);

        // And the directory shape Chromium walks: Primary first, GainMap second.
        var doc = XDocument.Parse(Encoding.UTF8.GetString(packet));
        XNamespace container = "http://ns.google.com/photos/1.0/container/";
        XNamespace item = "http://ns.google.com/photos/1.0/container/item/";
        var items = doc.Descendants(container + "Item").ToList();
        items.Count.ShouldBe(2);
        items[0].Attribute(item + "Semantic")!.Value.ShouldBe("Primary");
        items[0].Attribute(item + "Mime")!.Value.ShouldBe("image/jpeg");
        items[1].Attribute(item + "Semantic")!.Value.ShouldBe("GainMap");
        items[1].Attribute(item + "Length")!.Value.ShouldBe("48213");
    }

    // ------------------------------------------------------------------- MPF

    [Fact]
    public void Mpf_Write_Emits_The_Skia_Compatible_Layout()
    {
        const int segmentOffset = 20;      // where the APP2 sits in the file
        const int primaryLength = 10_000;  // gain map starts here
        const int gainMapLength = 3_000;

        var segment = MpfSegment.Write(segmentOffset, primaryLength, gainMapLength);

        segment.Length.ShouldBe(MpfSegment.TotalLength);
        segment[0].ShouldBe((byte)0xFF);
        segment[1].ShouldBe((byte)0xE2);
        BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(2)).ShouldBe((ushort)88);
        segment.AsSpan(4, 4).ToArray().ShouldBe("MPF\0"u8.ToArray());
        // Big-endian MP header (libultrahdr's default), IFD right after it.
        segment.AsSpan(8, 4).ToArray().ShouldBe(new byte[] { 0x4D, 0x4D, 0x00, 0x2A });
        BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan(12)).ShouldBe(8u);
        BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(16)).ShouldBe((ushort)3);
        // MPFVersion "0100" inline.
        BinaryPrimitives.ReadUInt16BigEndian(segment.AsSpan(18)).ShouldBe((ushort)0xB000);
        segment.AsSpan(26, 4).ToArray().ShouldBe("0100"u8.ToArray());
        // Gain map wire offset is measured from the endian field (segment + 8) —
        // THE detail Skia's GetAbsoluteOffset assumes and sloppy writers get wrong.
        BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan(82))
            .ShouldBe((uint)(primaryLength - segmentOffset - 8));
    }

    [Fact]
    public void Mpf_Write_TryParse_RoundTrips_To_Absolute_Offsets()
    {
        const int segmentOffset = 20;
        const int primaryLength = 10_000;
        const int gainMapLength = 3_000;

        var segment = MpfSegment.Write(segmentOffset, primaryLength, gainMapLength);
        // The parser receives the payload after "MPF\0" and its absolute position.
        var payload = segment[8..];
        MpfSegment.TryParse(payload, segmentOffset + 8, out var entries).ShouldBeTrue();

        entries.Length.ShouldBe(2);
        entries[0].Attribute.ShouldBe(0x00030000u); // Baseline MP Primary, JPEG
        entries[0].ImageLength.ShouldBe((uint)primaryLength);
        entries[0].ImageOffset.ShouldBe(0);         // primary offset is 0 by spec
        entries[1].Attribute.ShouldBe(0u);          // undefined type, JPEG
        entries[1].ImageLength.ShouldBe((uint)gainMapLength);
        entries[1].ImageOffset.ShouldBe(primaryLength); // back to absolute
    }

    [Fact]
    public void Mpf_TryParse_Accepts_Little_Endian()
    {
        // Same IFD hand-built little-endian: some cameras write II-order MPF.
        var payload = new byte[82];
        payload[0] = 0x49; payload[1] = 0x49; payload[2] = 0x2A; payload[3] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 2); // just B001 + B002
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), 0xB001);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(18), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 0xB002);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(24), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(26), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30), 42); // entries at rel 42
        // next-IFD pointer (rel 34..38) stays 0; entries:
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(42), 0x00030000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(46), 5000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(50), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(58), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(62), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(66), 4992);

        MpfSegment.TryParse(payload, 8, out var entries).ShouldBeTrue();
        entries.Length.ShouldBe(2);
        entries[1].ImageOffset.ShouldBe(5000); // 8 + 4992
    }

    [Fact]
    public void Mpf_TryParse_Rejects_Garbage()
    {
        MpfSegment.TryParse([1, 2, 3], 0, out _).ShouldBeFalse();
        MpfSegment.TryParse(new byte[40], 0, out _).ShouldBeFalse("no endian marker");
        var truncated = MpfSegment.Write(0, 100, 50)[8..40];
        MpfSegment.TryParse(truncated, 8, out _).ShouldBeFalse("entry table past EOF");
    }
}
