using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Text region placement across the options jbig2enc never emits — every
/// REFCORNER, transposed strips, multi-row strips, DSOFFSET, and the
/// combination operators.
/// <para>
/// The committed fixtures in <c>Jbig2SymbolFixtureTests</c> cover one corner of
/// §6.4 (arithmetic, bottom-left, one strip, no refinement) with genuine
/// third-party bytes. Everything else in the section is reachable only through
/// synthetic streams, and the check that matters for those is
/// <c>Jbig2TextRegionOracleTests</c> putting the same bytes past jbig2dec — a
/// round-trip through our own encoder would share whatever the placement rules
/// get wrong.
/// </para>
/// </summary>
public sealed class Jbig2TextRegionTests
{
    /// <summary>Three easily-told-apart glyphs, ordered by height as §6.5 requires.</summary>
    internal static Jbig2Bitmap[] Alphabet() =>
    [
        Jbig2StreamBuilder.FromRows(
            "##.",
            ".#.",
            ".##"),
        Jbig2StreamBuilder.FromRows(
            "####",
            "#..#",
            "#..#",
            "####"),
        Jbig2StreamBuilder.FromRows(
            ".###.",
            "#...#",
            "#####",
            "#...#",
            "#...#"),
    ];

    /// <summary>
    /// The corner is passed as its T.88 code rather than the enum: the enum is
    /// internal, and an xunit theory parameter has to be as public as the method.
    /// </summary>
    public static TheoryData<int, bool> Corners()
    {
        var data = new TheoryData<int, bool>();
        for (var corner = 0; corner < 4; corner++)
            foreach (var transposed in (bool[])[false, true])
                data.Add(corner, transposed);

        return data;
    }

    [Theory]
    [MemberData(nameof(Corners))]
    public void EveryReferenceCorner_PlacesSymbolsWhereItSays(int corner, bool transposed)
    {
        var (stream, expected) = Build((ReferenceCorner)corner, transposed);

        var image = Jbig2Decoder.Decode(stream, expected.Width, expected.Height);
        Jbig2StreamBuilder.ToRows(expected.Width, expected.Height, image.Bits)
            .ShouldBe(Jbig2StreamBuilder.ToRows(expected.Width, expected.Height, expected.Data));
    }

    /// <summary>
    /// SBSTRIPS &gt; 1 changes the coding shape: the strip's T is quantised, and
    /// each instance carries the remainder through IAIT — a field that is not
    /// coded at all when there is one strip.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MultiRowStrips_PlaceSymbolsAtTheirOwnRows(int logStrips)
    {
        var (segments, expected) = BuildSegments(ReferenceCorner.TopLeft, false, logStrips);

        var image = Jbig2Decoder.Decode(Jbig2StreamBuilder.Stream(segments), expected.Width, expected.Height);
        image.Bits.ToArray().ShouldBe(expected.Data);
    }

    /// <summary>
    /// SBDSOFFSET is added to every inter-symbol gap, so the same coded deltas
    /// produce different spacing. Getting its sign backwards would still decode,
    /// just wrongly — hence a test that pins the direction.
    /// </summary>
    [Theory]
    [InlineData(-3)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void DsOffset_ShiftsEveryGapButTheFirst(int dsOffset)
    {
        var (segments, expected) = BuildSegments(ReferenceCorner.TopLeft, false, dsOffset: dsOffset);

        var image = Jbig2Decoder.Decode(Jbig2StreamBuilder.Stream(segments), expected.Width, expected.Height);
        image.Bits.ToArray().ShouldBe(expected.Data);
    }

    [Fact]
    public void TextRegion_WithNoSymbolDictionary_Fails()
    {
        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(16, 16)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateTextRegion, 1,
                Jbig2SymbolBuilder.TextRegionSegment(16, 16, Alphabet(), [new(0, 0, 0)])));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.Decode(stream, 16, 16))
            .Message.ShouldContain("symbol dictionary");
    }

    [Fact]
    public void HuffmanCodedTextRegion_IsRefusedByName()
    {
        var segment = Jbig2SymbolBuilder.TextRegionSegment(16, 16, Alphabet(), [new(0, 0, 0)]);
        segment[18] |= 0x01;   // SBHUFF, low byte of the flags at offset 17..18

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(16, 16)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1,
                Jbig2SymbolBuilder.SymbolDictionarySegment(Alphabet())),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateTextRegion, 1, segment, referredTo: [1]));

        Should.Throw<NotSupportedException>(() => Jbig2Decoder.Decode(stream, 16, 16))
            .Message.ShouldContain("SBHUFF");
    }

    [Fact]
    public void HuffmanCodedSymbolDictionary_IsRefusedByName()
    {
        var segment = Jbig2SymbolBuilder.SymbolDictionarySegment(Alphabet());
        segment[1] |= 0x01;   // SDHUFF, low byte of the 16-bit flags

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(16, 16)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1, segment));

        Should.Throw<NotSupportedException>(() => Jbig2Decoder.Decode(stream, 16, 16))
            .Message.ShouldContain("SDHUFF");
    }

    /// <summary>
    /// A dictionary that imports from another — the mechanism PDF's
    /// <c>/JBIG2Globals</c> is built on, and the one place symbol numbering spans
    /// two segments. Both export choices matter: dropping the imports and
    /// re-exporting them change what the downstream text region can name, and
    /// they change it by shifting every symbol ID.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymbolDictionary_ImportsFromTheDictionaryItRefers(bool reExportImports)
    {
        var first = Alphabet()[..2];
        var second = Alphabet()[2..];
        var visible = reExportImports ? (Jbig2Bitmap[])[.. first, .. second] : second;

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(40, 12)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1,
                Jbig2SymbolBuilder.SymbolDictionarySegment(first)),
            Jbig2StreamBuilder.Segment(2, SegmentType.SymbolDictionary, 1,
                Jbig2SymbolBuilder.SymbolDictionarySegment(
                    second, importedCount: first.Length, reExportImports: reExportImports),
                referredTo: [1]),
            Jbig2StreamBuilder.Segment(3, SegmentType.ImmediateTextRegion, 1,
                Jbig2SymbolBuilder.TextRegionSegment(
                    40, 12, visible, [new(visible.Length - 1, 2, 2)]),
                referredTo: [2]));

        // Whichever way the exports go, the last visible symbol is the new one.
        var expected = new Jbig2Bitmap(40, 12);
        expected.Combine(second[0], 2, 2, CombinationOperator.Or);

        Jbig2Decoder.Decode(stream, 40, 12).Bits.ToArray().ShouldBe(expected.Data);
    }

    // ---- helpers -----------------------------------------------------------------

    /// <summary>
    /// Builds a stream and the raster it should produce, placing one of each
    /// symbol so the corners are told apart by where the glyphs land.
    /// </summary>
    internal static (byte[] Stream, Jbig2Bitmap Expected) Build(ReferenceCorner corner, bool transposed)
    {
        var (segments, expected) = BuildSegments(corner, transposed);
        return (Jbig2StreamBuilder.Stream(segments), expected);
    }

    /// <summary>
    /// The segments and the raster they should produce. Returned unassembled so
    /// the oracle tests can wrap them in a <c>.jb2</c> file while the tests here
    /// use a bare embedded stream.
    /// </summary>
    internal static (byte[][] Segments, Jbig2Bitmap Expected) BuildSegments(
        ReferenceCorner corner,
        bool transposed,
        int logStrips = 0,
        int dsOffset = 0)
    {
        const int w = 40, h = 24;
        var symbols = Alphabet();

        // Placements are chosen so each corner puts the glyphs somewhere visibly
        // different, and so nothing lands off the region under any of them.
        Jbig2SymbolBuilder.Placement[] placements = transposed
            ? [new(0, 6, 6), new(1, 14, 6), new(2, 20, 18)]
            : [new(0, 6, 6), new(1, 14, 6), new(2, 24, 15)];

        byte[][] segments =
        [
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(w, h)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1,
                Jbig2SymbolBuilder.SymbolDictionarySegment(symbols)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateTextRegion, 1,
                Jbig2SymbolBuilder.TextRegionSegment(
                    w, h, symbols, placements, corner, transposed, logStrips, dsOffset),
                referredTo: [1]),
        ];

        // The expected raster comes from the placement rule directly rather than
        // from the decoder, so the two agree only if both are right.
        var expected = new Jbig2Bitmap(w, h);
        foreach (var placement in placements)
        {
            var symbol = symbols[placement.Id];
            var (s, t) = (placement.S, placement.T);
            var (x, y) = transposed
                ? (corner is ReferenceCorner.TopRight or ReferenceCorner.BottomRight ? t - symbol.Width + 1 : t,
                   corner is ReferenceCorner.BottomLeft or ReferenceCorner.BottomRight ? s - symbol.Height + 1 : s)
                : (corner is ReferenceCorner.TopRight or ReferenceCorner.BottomRight ? s - symbol.Width + 1 : s,
                   corner is ReferenceCorner.BottomLeft or ReferenceCorner.BottomRight ? t - symbol.Height + 1 : t);

            expected.Combine(symbol, x, y, CombinationOperator.Or);
        }

        return (segments, expected);
    }
}
