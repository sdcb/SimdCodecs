using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Tests for JBIG2 segment header parsing (ITU-T T.88 §7.2) and the region
/// segment information field (§7.4.1).
/// <para>
/// The headers here are hand-assembled byte by byte rather than produced by
/// <see cref="Jbig2StreamBuilder"/>. Building them with the same size rules the
/// parser applies would make the test circular — the point is that a header
/// written to the spec's layout parses, not that the suite agrees with itself.
/// </para>
/// </summary>
public sealed class Jbig2SegmentTests
{
    [Fact]
    public void ReadHeader_ShortForm_ParsesNumberTypePageAndLength()
    {
        byte[] header =
        [
            0x00, 0x00, 0x00, 0x07,   // segment number 7
            0x26,                     // flags: type 38 (immediate generic region), 1-byte page association
            0x00,                     // referred-to: short form, count 0, no retain bits set
            0x01,                     // page association 1
            0x00, 0x00, 0x00, 0x05,   // data length 5
            1, 2, 3, 4, 5,            // the data part
        ];

        var position = 0;
        var segment = Jbig2Segment.ReadHeader(header, ref position);

        segment.Number.ShouldBe(7u);
        segment.Type.ShouldBe(SegmentType.ImmediateGenericRegion);
        segment.Page.ShouldBe(1u);
        segment.ReferredTo.ShouldBeEmpty();
        segment.DataLength.ShouldBe(5);
        segment.DataStart.ShouldBe(11);
        position.ShouldBe(11);
    }

    [Fact]
    public void ReadHeader_ShortForm_ParsesUpToFourReferredToSegments()
    {
        byte[] header =
        [
            0x00, 0x00, 0x00, 0x09,   // segment number 9
            0x06,                     // flags: type 6 (immediate text region), 1-byte page association
            0x60,                     // referred-to: count 3 in the top three bits, retain bits clear
            0x02, 0x05, 0x08,         // referred-to segment numbers (1 byte each: this segment is <= 256)
            0x01,                     // page association 1
            0x00, 0x00, 0x00, 0x00,   // data length 0
        ];

        var position = 0;
        var segment = Jbig2Segment.ReadHeader(header, ref position);

        segment.ReferredTo.ShouldBe([2u, 5u, 8u]);
        segment.Type.ShouldBe(SegmentType.ImmediateTextRegion);
        position.ShouldBe(header.Length);
    }

    [Fact]
    public void ReadHeader_LongForm_ParsesCountAndSkipsRetainFlags()
    {
        // §7.2.4: the escape value 7 in the top three bits means a four-byte
        // count follows, then ceil((count+1)/8) bytes of retain flags. With 5
        // referred-to segments that is one retain byte.
        byte[] header =
        [
            0x00, 0x00, 0x00, 0x0A,   // segment number 10
            0x26,                     // flags: type 38, 1-byte page association
            0xE0, 0x00, 0x00, 0x05,   // referred-to: long form (top three bits 111), count 5
            0x00,                     // retain flags: ceil(6/8) = 1 byte
            0x01, 0x02, 0x03, 0x04, 0x05,
            0x01,                     // page association 1
            0x00, 0x00, 0x00, 0x00,   // data length 0
        ];

        var position = 0;
        var segment = Jbig2Segment.ReadHeader(header, ref position);

        segment.ReferredTo.ShouldBe([1u, 2u, 3u, 4u, 5u]);
        position.ShouldBe(header.Length);
    }

    [Fact]
    public void ReadHeader_LongForm_SizesRetainFlagsByCount()
    {
        // 8 referred-to segments needs ceil(9/8) = 2 retain bytes, not 1. Getting
        // this wrong shifts everything after it by a byte.
        var header = new List<byte>
        {
            0x00, 0x00, 0x00, 0x0B,
            0x26,
            0xE0, 0x00, 0x00, 0x08,   // count 8
            0x00, 0x00,               // two retain bytes
        };
        header.AddRange([1, 2, 3, 4, 5, 6, 7, 8]);
        header.AddRange([0x01, 0x00, 0x00, 0x00, 0x00]);

        var position = 0;
        var segment = Jbig2Segment.ReadHeader(header.ToArray(), ref position);

        segment.ReferredTo.Length.ShouldBe(8);
        position.ShouldBe(header.Count);
    }

    [Fact]
    public void ReadHeader_SegmentNumberAbove256_UsesTwoByteReferredToNumbers()
    {
        // §7.2.5 sizes the referred-to numbers by this segment's own number,
        // since a segment can only refer to lower-numbered ones.
        byte[] header =
        [
            0x00, 0x00, 0x01, 0x2C,   // segment number 300 (> 256, so 2-byte references)
            0x26,
            0x40,                     // short form, count 2
            0x00, 0x2A, 0x01, 0x00,   // references 42 and 256, 2 bytes each
            0x01,
            0x00, 0x00, 0x00, 0x00,
        ];

        var position = 0;
        var segment = Jbig2Segment.ReadHeader(header, ref position);

        segment.Number.ShouldBe(300u);
        segment.ReferredTo.ShouldBe([42u, 256u]);
        position.ShouldBe(header.Length);
    }

    [Fact]
    public void ReadHeader_SegmentNumberAbove65536_UsesFourByteReferredToNumbers()
    {
        byte[] header =
        [
            0x00, 0x01, 0x11, 0x70,   // segment number 70000 (> 65536, so 4-byte references)
            0x26,
            0x20,                     // short form, count 1
            0x00, 0x00, 0x04, 0xD2,   // reference 1234
            0x01,
            0x00, 0x00, 0x00, 0x00,
        ];

        var position = 0;
        var segment = Jbig2Segment.ReadHeader(header, ref position);

        segment.Number.ShouldBe(70000u);
        segment.ReferredTo.ShouldBe([1234u]);
        position.ShouldBe(header.Length);
    }

    [Fact]
    public void ReadHeader_PageAssociationFlag_SelectsTheFourByteForm()
    {
        byte[] header =
        [
            0x00, 0x00, 0x00, 0x01,
            0x66,                     // flags: type 38, bit 6 set -> 4-byte page association
            0x00,
            0x00, 0x00, 0x04, 0x00,   // page 1024
            0x00, 0x00, 0x00, 0x00,
        ];

        var position = 0;
        var segment = Jbig2Segment.ReadHeader(header, ref position);

        segment.Page.ShouldBe(1024u);
        position.ShouldBe(header.Length);
    }

    [Fact]
    public void ReadHeader_UnknownDataLength_IsRejectedExplicitly()
    {
        // §7.2.7 allows 0xFFFFFFFF on an immediate generic region, whose data is
        // then terminated by a search sequence instead. Not implemented, and it
        // must say so rather than treat the value as a length.
        byte[] header =
        [
            0x00, 0x00, 0x00, 0x01,
            0x26,
            0x00,
            0x01,
            0xFF, 0xFF, 0xFF, 0xFF,
        ];

        var position = 0;
        Should.Throw<NotSupportedException>(() => Jbig2Segment.ReadHeader(header, ref position))
            .Message.ShouldContain("unknown data length");
    }

    [Fact]
    public void ReadHeader_DataLengthPastTheEnd_Throws()
    {
        byte[] header =
        [
            0x00, 0x00, 0x00, 0x01,
            0x26,
            0x00,
            0x01,
            0x00, 0x00, 0x10, 0x00,   // claims 4096 bytes of data that are not there
        ];

        var position = 0;
        Should.Throw<InvalidDataException>(() => Jbig2Segment.ReadHeader(header, ref position));
    }

    [Fact]
    public void ReadHeader_TruncatedHeader_Throws()
    {
        var position = 0;
        Should.Throw<InvalidDataException>(() =>
        {
            var p = position;
            Jbig2Segment.ReadHeader([0x00, 0x00, 0x00], ref p);
        });
    }

    [Fact]
    public void ReadRegionInfo_ParsesGeometryAndOperator()
    {
        byte[] info =
        [
            0x00, 0x00, 0x00, 0x40,   // width 64
            0x00, 0x00, 0x00, 0x20,   // height 32
            0x00, 0x00, 0x00, 0x0A,   // x 10
            0x00, 0x00, 0x00, 0x14,   // y 20
            0x02,                     // flags: external combination operator XOR
        ];

        var position = 0;
        var region = Jbig2Segment.ReadRegionInfo(info, ref position);

        region.Width.ShouldBe(64);
        region.Height.ShouldBe(32);
        region.X.ShouldBe(10);
        region.Y.ShouldBe(20);
        region.Operator.ShouldBe(CombinationOperator.Xor);
        position.ShouldBe(17);
    }

    [Fact]
    public void ReadRegionInfo_ReservedCombinationOperator_Throws()
    {
        byte[] info =
        [
            0x00, 0x00, 0x00, 0x08,
            0x00, 0x00, 0x00, 0x08,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x05,                     // 5..7 are reserved
        ];

        var position = 0;
        Should.Throw<InvalidDataException>(() => Jbig2Segment.ReadRegionInfo(info, ref position));
    }

    [Fact]
    public void ReadRegionInfo_ZeroSizedRegion_Throws()
    {
        byte[] info =
        [
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x08,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00,
        ];

        var position = 0;
        Should.Throw<InvalidDataException>(() => Jbig2Segment.ReadRegionInfo(info, ref position));
    }
}
