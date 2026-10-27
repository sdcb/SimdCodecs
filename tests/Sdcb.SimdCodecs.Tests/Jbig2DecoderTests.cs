using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// End-to-end tests for <see cref="Jbig2Decoder"/>: segment streams in, pages
/// out. Covers the PDF-shaped entry point (embedded stream + globals + explicit
/// dimensions), standalone <c>.jb2</c> files in both organizations, page
/// composition, and the refusals — a stream needing a feature this decoder does
/// not implement has to say so rather than return a plausible-looking blank
/// page.
/// </summary>
public sealed class Jbig2DecoderTests
{
    private static readonly string[] Glyph =
    [
        "................",
        "..####....####..",
        "..#..#....#..#..",
        "..#..#....#..#..",
        "..####....####..",
        "..#..........#..",
        "..#..........#..",
        "..############..",
        "................",
    ];

    [Fact]
    public void Decode_EmbeddedStream_ReconstructsThePage()
    {
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)));

        var page = Jbig2Decoder.Decode(stream, source.Width, source.Height);

        page.Width.ShouldBe(source.Width);
        page.Height.ShouldBe(source.Height);
        Jbig2StreamBuilder.ToRows(page.Width, page.Height, page.Bits).ShouldBe(Glyph);
    }

    [Fact]
    public void Decode_WithoutAPageInformationSegment_StillDecodes()
    {
        // The dimensions come from the PDF image dictionary, so a stream that
        // omits page information is perfectly decodable.
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));

        var page = Jbig2Decoder.Decode(stream, source.Width, source.Height);

        Jbig2StreamBuilder.ToRows(page.Width, page.Height, page.Bits).ShouldBe(Glyph);
    }

    [Fact]
    public void Decode_Globals_AreProcessedAheadOfTheEmbeddedStream()
    {
        // A /JBIG2Globals stream is a prefix of the image's own segment stream.
        // Nothing in this rung's feature set actually needs shared state, so the
        // check is that its segments are seen at all, and seen first.
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var globals = Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
            Jbig2StreamBuilder.PageInformation(source.Width, source.Height, defaultPixelBlack: true));
        var embedded = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));

        var page = Jbig2Decoder.Decode(embedded, globals, source.Width, source.Height);

        // Page default black, then the glyph OR-ed on: everything stays black.
        page.Bits.ToArray().ShouldAllBe(b => b == 1);
    }

    [Fact]
    public void Decode_PageDefaultPixel_PaintsTheBackgroundBeforeAnyRegion()
    {
        var source = Jbig2StreamBuilder.FromRows("....", "....");
        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(4, 2, defaultPixelBlack: true)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source, op: CombinationOperator.Or)));

        var page = Jbig2Decoder.Decode(stream, 4, 2);

        Jbig2StreamBuilder.ToRows(4, 2, page.Bits).ShouldBe(["####", "####"]);
    }

    [Fact]
    public void Decode_RegionLocation_CompositesAtItsXAndY()
    {
        var block = Jbig2StreamBuilder.FromRows("##", "##");
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(block, x: 3, y: 1));

        var page = Jbig2Decoder.Decode(stream, 6, 4);

        Jbig2StreamBuilder.ToRows(6, 4, page.Bits).ShouldBe(
        [
            "......",
            "...##.",
            "...##.",
            "......",
        ]);
    }

    [Fact]
    public void Decode_RegionRunningPastThePageEdge_IsClippedNotRejected()
    {
        // A PDF image dictionary can declare a page smaller than the region the
        // encoder emitted; clipping is the useful behaviour, not an exception.
        var block = Jbig2StreamBuilder.FromRows("####", "####", "####");
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(block, x: 2, y: 1));

        var page = Jbig2Decoder.Decode(stream, 4, 2);

        Jbig2StreamBuilder.ToRows(4, 2, page.Bits).ShouldBe(["....", "..##"]);
    }

    // The operator codes of T.88 §7.4.1.5. Spelled as ints because the enum is
    // internal to the codec and an InlineData argument has to be as public as
    // the test method.
    [Theory]
    [InlineData(0, "#####.")]   // OR
    [InlineData(2, "##.##.")]   // XOR
    [InlineData(1, "###...")]   // AND
    [InlineData(3, "###..#")]   // XNOR
    [InlineData(4, "###.#.")]   // REPLACE
    public void Decode_ExternalCombinationOperator_MergesAsSpecified(int operatorCode, string expected)
    {
        var op = (CombinationOperator)operatorCode;

        // First region lays down "####.." across a white page; the second is
        // "#.#." placed at x=2 and merged with the operator under test.
        var first = Jbig2StreamBuilder.FromRows("####", "####");
        var second = Jbig2StreamBuilder.FromRows("#.#.", "#.#.");

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(first)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(second, x: 2, op: op)));

        var page = Jbig2Decoder.Decode(stream, 6, 2);

        Jbig2StreamBuilder.ToRows(6, 2, page.Bits).ShouldBe([expected, expected]);
    }

    [Fact]
    public void Decode_IntermediateGenericRegion_IsNotCompositedOntoThePage()
    {
        // Intermediate regions go to an auxiliary buffer for a later refinement
        // region to consume, not onto the page.
        var source = Jbig2StreamBuilder.FromRows("####", "####");
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.IntermediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));

        var page = Jbig2Decoder.Decode(stream, 4, 2);

        Jbig2StreamBuilder.ToRows(4, 2, page.Bits).ShouldBe(["....", "...."]);
    }

    [Fact]
    public void Decode_UnknownSegmentType_IsSkippedByItsDataLength()
    {
        // §7.2.3: skip what you do not recognise. The data length in the header
        // is what makes that safe, and the region after it must still land.
        var source = Jbig2StreamBuilder.FromRows("####", "####");
        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(1, (SegmentType)60, 1, [0xDE, 0xAD, 0xBE, 0xEF]),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)));

        var page = Jbig2Decoder.Decode(stream, 4, 2);

        Jbig2StreamBuilder.ToRows(4, 2, page.Bits).ShouldBe(["####", "####"]);
    }

    /// <summary>
    /// What is left unimplemented, and it is now one thing: custom Huffman table
    /// segments (§7.4.13). Every region and dictionary type this list used to
    /// name — symbol dictionary, text, refinement, pattern, halftone — decodes,
    /// so a malformed one of those fails as corrupt data instead, which their own
    /// test files cover.
    /// </summary>
    [Fact]
    public void Decode_CustomHuffmanTableSegment_SaysWhatIsMissing()
    {
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.Tables, 1, [0x00, 0x00, 0x00, 0x00]);

        Should.Throw<NotSupportedException>(() => Jbig2Decoder.Decode(stream, 8, 8))
            .Message.ShouldContain("Huffman table");
    }

    /// <summary>
    /// MMR regions are decoded now, so garbage in one has to fail as
    /// <em>malformed data</em> rather than as a missing feature. The distinction
    /// is worth a test: a <see cref="NotSupportedException"/> here would send a
    /// caller off looking for a decoder that already exists.
    /// </summary>
    [Fact]
    public void Decode_MmrCodedGenericRegion_FailsAsCorruptRatherThanUnsupported()
    {
        byte[] regionData =
        [
            0x00, 0x00, 0x00, 0x08,   // width 8
            0x00, 0x00, 0x00, 0x02,   // height 2
            0x00, 0x00, 0x00, 0x00,   // x
            0x00, 0x00, 0x00, 0x00,   // y
            0x00,                     // combination operator OR
            0x01,                     // generic region flags: MMR = 1
            0x00, 0x00,               // not a T.6 codestream: all-zero is the EOL prefix
        ];

        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1, regionData);

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.Decode(stream, 8, 2))
            .Message.ShouldContain("MMR");
    }

    [Fact]
    public void Decode_ExtTemplateGenericRegion_SaysWhatIsMissing()
    {
        byte[] regionData =
        [
            0x00, 0x00, 0x00, 0x08,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00,
            0x10,                     // generic region flags: EXTTEMPLATE = 1
            0x00, 0x00,
        ];

        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1, regionData);

        Should.Throw<NotSupportedException>(() => Jbig2Decoder.Decode(stream, 8, 2))
            .Message.ShouldContain("EXTTEMPLATE");
    }

    [Fact]
    public void Decode_NonPositiveDimensions_Throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Jbig2Decoder.Decode([], 0, 8));
        Should.Throw<ArgumentOutOfRangeException>(() => Jbig2Decoder.Decode([], 8, -1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Decode_EveryTemplateThroughTheSegmentLayer_RoundTrips(int template)
    {
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source, template: template, typicalPrediction: true));

        var page = Jbig2Decoder.Decode(stream, source.Width, source.Height);

        Jbig2StreamBuilder.ToRows(page.Width, page.Height, page.Bits).ShouldBe(Glyph);
    }

    // ---- polarity and projections ------------------------------------------------

    [Fact]
    public void Bits_UseT88Polarity_AndGrayInvertsIt()
    {
        var source = Jbig2StreamBuilder.FromRows("#.");
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));

        var page = Jbig2Decoder.Decode(stream, 2, 1);

        // T.88: 1 is black. The grey projection is the conventional visual
        // reading, black 0 / white 255.
        page.Bits[0].ShouldBe((byte)1);
        page.Bits[1].ShouldBe((byte)0);
        page.ToGray8().ShouldBe(new byte[] { 0, 255 });
    }

    [Fact]
    public void ToRaster_IsSingleChannelEightBitGray()
    {
        var source = Jbig2StreamBuilder.FromRows("#.", ".#");
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));

        var raster = Jbig2Decoder.Decode(stream, 2, 2).ToRaster();

        raster.Channels.ShouldBe(1);
        raster.SampleFormat.ShouldBe(SampleFormat.UInt8);
        raster.Pixels.ToArray().ShouldBe(new byte[] { 0, 255, 255, 0 });
    }

    [Fact]
    public void ExpandToGray8_RejectsAnUndersizedDestination()
    {
        var source = Jbig2StreamBuilder.FromRows("##", "##");
        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
            Jbig2StreamBuilder.GenericRegionSegment(source));
        var page = Jbig2Decoder.Decode(stream, 2, 2);

        Should.Throw<ArgumentException>(() => page.ExpandToGray8(new byte[3]));
    }

    // ---- standalone .jb2 files ---------------------------------------------------

    [Fact]
    public void DecodeFile_SequentialOrganization_ReconstructsThePage()
    {
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)),
            Jbig2StreamBuilder.Segment(2, SegmentType.EndOfPage, 1, []),
            Jbig2StreamBuilder.Segment(3, SegmentType.EndOfFile, 0, []));

        var page = Jbig2Decoder.DecodeFile(file);

        Jbig2StreamBuilder.ToRows(page.Width, page.Height, page.Bits).ShouldBe(Glyph);
    }

    [Fact]
    public void DecodeFile_RandomAccessOrganization_ReconstructsThePage()
    {
        // §D.2: every header first, then every data part in the same order. The
        // header block ends where the bytes left over equal the data lengths
        // declared so far.
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var file = Jbig2StreamBuilder.RandomAccessFile(
            Jbig2StreamBuilder.Split(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Split(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)),
            Jbig2StreamBuilder.Split(2, SegmentType.EndOfFile, 0, []));

        var page = Jbig2Decoder.DecodeFile(file);

        Jbig2StreamBuilder.ToRows(page.Width, page.Height, page.Bits).ShouldBe(Glyph);
    }

    [Fact]
    public void DecodeFile_StripedPage_TakesItsHeightFromTheEndOfStripeSegment()
    {
        // §7.4.8.2: an unknown page height means the extent is only settled by
        // the end-of-stripe segments that follow.
        var source = Jbig2StreamBuilder.FromRows("####", "#..#", "####");
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(4, 0, stripedUnknownHeight: true)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)),
            Jbig2StreamBuilder.Segment(2, SegmentType.EndOfStripe, 1, Jbig2StreamBuilder.EndOfStripe(2)));

        var page = Jbig2Decoder.DecodeFile(file);

        page.Height.ShouldBe(3);
        Jbig2StreamBuilder.ToRows(4, 3, page.Bits).ShouldBe(["####", "#..#", "####"]);
    }

    [Fact]
    public void DecodeFile_OtherPagesSegments_AreIgnored()
    {
        var source = Jbig2StreamBuilder.FromRows("####", "####");
        var other = Jbig2StreamBuilder.FromRows("....", "....");
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(4, 2)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)),
            // Page 2's region would blank the raster if the page filter were not applied.
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateGenericRegion, 2,
                Jbig2StreamBuilder.GenericRegionSegment(other, op: CombinationOperator.Replace)));

        var page = Jbig2Decoder.DecodeFile(file);

        Jbig2StreamBuilder.ToRows(4, 2, page.Bits).ShouldBe(["####", "####"]);
    }

    [Fact]
    public void DecodeFile_WithoutTheSignature_Throws()
    {
        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile([0x00, 0x01, 0x02, 0x03]));
    }

    [Fact]
    public void DecodeFile_WithoutPageInformation_Throws()
    {
        var source = Jbig2StreamBuilder.FromRows("##", "##");
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file));
    }

    [Fact]
    public void TryReadFileInfo_ReadsDimensionsWithoutDecoding()
    {
        var source = Jbig2StreamBuilder.FromRows(Glyph);
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)));

        Jbig2Decoder.TryReadFileInfo(file, out var width, out var height).ShouldBeTrue();
        width.ShouldBe(source.Width);
        height.ShouldBe(source.Height);
    }

    [Fact]
    public void TryReadFileInfo_OnGarbage_ReturnsFalse()
    {
        Jbig2Decoder.TryReadFileInfo([1, 2, 3], out _, out _).ShouldBeFalse();
    }
}
