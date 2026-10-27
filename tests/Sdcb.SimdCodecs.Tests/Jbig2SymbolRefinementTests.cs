using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The two places refinement reaches inside the text path: a text region that
/// corrects a symbol for one placement only (SBREFINE, §6.4.11), and a symbol
/// dictionary that builds a glyph by refining an imported one (SDREFAGG,
/// §6.5.8.2.2).
/// <para>
/// Both are how a JBIG2 encoder gets from "close enough" to lossless. A symbol
/// matcher replaces near-identical glyphs with a shared bitmap, which is lossy;
/// refinement then codes the handful of pixels that differ, which is far cheaper
/// than coding the glyph again. jbig2enc has the machinery for neither — its
/// <c>-r</c> flag produces an empty file — so these streams are synthetic and
/// jbig2dec is the check.
/// </para>
/// </summary>
public sealed class Jbig2SymbolRefinementTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>
    /// One instance stamped as-is and another refined, from the same dictionary
    /// entry — so the test fails if refinement is skipped <em>or</em> applied to
    /// the wrong instance.
    /// </summary>
    [Fact]
    public void TextRegion_RefinesOneInstanceAndNotTheOther()
    {
        var (stream, expected) = BuildTextRefinement();

        Jbig2Decoder.Decode(stream, expected.Width, expected.Height).Bits.ToArray().ShouldBe(expected.Data);
    }

    [Fact]
    public void TextRegion_RefinedInstance_AgreesWithJbig2dec()
    {
        Jbig2Oracle.RequireOrSkip();

        var (segments, expected) = BuildTextRefinementSegments();
        var file = Jbig2StreamBuilder.SequentialFile([.. segments,
            Jbig2StreamBuilder.Segment(9, SegmentType.EndOfPage, 1, [])]);

        _out.WriteLine($"SBREFINE: {file.Length} bytes");
        Jbig2Oracle.Decode(file).Bits.ShouldBe(expected.Data);
    }

    [Fact]
    public void SymbolDictionary_BuildsSymbolsByRefiningItsImports()
    {
        var (stream, expected) = BuildDictionaryRefinement();

        Jbig2Decoder.Decode(stream, expected.Width, expected.Height).Bits.ToArray().ShouldBe(expected.Data);
    }

    [Fact]
    public void SymbolDictionary_RefinedSymbols_AgreeWithJbig2dec()
    {
        Jbig2Oracle.RequireOrSkip();

        var (segments, expected) = BuildDictionaryRefinementSegments();
        var file = Jbig2StreamBuilder.SequentialFile([.. segments,
            Jbig2StreamBuilder.Segment(9, SegmentType.EndOfPage, 1, [])]);

        _out.WriteLine($"SDREFAGG: {file.Length} bytes");
        Jbig2Oracle.Decode(file).Bits.ShouldBe(expected.Data);
    }

    // ---- stream construction -----------------------------------------------------

    private static (byte[] Stream, Jbig2Bitmap Expected) BuildTextRefinement()
    {
        var (segments, expected) = BuildTextRefinementSegments();
        return (Jbig2StreamBuilder.Stream(segments), expected);
    }

    private static (byte[][] Segments, Jbig2Bitmap Expected) BuildTextRefinementSegments()
    {
        const int w = 40, h = 16;
        var symbols = Jbig2TextRegionTests.Alphabet();

        // The correction: the same glyph with its interior filled in.
        var corrected = new Jbig2Bitmap(symbols[1].Width, symbols[1].Height);
        symbols[1].Data.CopyTo(corrected.Data, 0);
        corrected.Data[corrected.Width + 1] = 1;
        corrected.Data[corrected.Width + 2] = 1;

        Jbig2SymbolBuilder.Placement[] placements =
        [
            new(1, 4, 4),
            new(1, 16, 4, corrected),
        ];

        byte[][] segments =
        [
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(w, h)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1,
                Jbig2SymbolBuilder.SymbolDictionarySegment(symbols)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateTextRegion, 1,
                Jbig2SymbolBuilder.TextRegionSegment(w, h, symbols, placements), referredTo: [1]),
        ];

        var expected = new Jbig2Bitmap(w, h);
        expected.Combine(symbols[1], 4, 4, CombinationOperator.Or);
        expected.Combine(corrected, 16, 4, CombinationOperator.Or);

        return (segments, expected);
    }

    private static (byte[] Stream, Jbig2Bitmap Expected) BuildDictionaryRefinement()
    {
        var (segments, expected) = BuildDictionaryRefinementSegments();
        return (Jbig2StreamBuilder.Stream(segments), expected);
    }

    private static (byte[][] Segments, Jbig2Bitmap Expected) BuildDictionaryRefinementSegments()
    {
        const int w = 48, h = 16;
        var imported = Jbig2TextRegionTests.Alphabet();

        // Each refined glyph is its import with one pixel flipped — small enough
        // that refinement is the cheap way to say it, which is the point.
        var refined = new Jbig2Bitmap[imported.Length];
        for (var i = 0; i < imported.Length; i++)
        {
            var copy = new Jbig2Bitmap(imported[i].Width, imported[i].Height);
            imported[i].Data.CopyTo(copy.Data, 0);
            copy.Data[copy.Width + 1] ^= 1;
            refined[i] = copy;
        }

        Jbig2SymbolBuilder.Placement[] placements =
        [
            new(0, 3, 3),
            new(1, 12, 3),
            new(2, 24, 3),
        ];

        byte[][] segments =
        [
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(w, h)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1,
                Jbig2SymbolBuilder.SymbolDictionarySegment(imported)),
            Jbig2StreamBuilder.Segment(2, SegmentType.SymbolDictionary, 1,
                Jbig2SymbolBuilder.RefiningSymbolDictionarySegment(imported, refined), referredTo: [1]),
            Jbig2StreamBuilder.Segment(3, SegmentType.ImmediateTextRegion, 1,
                Jbig2SymbolBuilder.TextRegionSegment(w, h, refined, placements), referredTo: [2]),
        ];

        var expected = new Jbig2Bitmap(w, h);
        foreach (var placement in placements)
            expected.Combine(refined[placement.Id], placement.S, placement.T, CombinationOperator.Or);

        return (segments, expected);
    }
}
