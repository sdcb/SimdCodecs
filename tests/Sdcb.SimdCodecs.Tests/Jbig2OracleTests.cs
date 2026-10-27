using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The external-oracle layer for JBIG2: push streams through <b>jbig2dec</b>, the
/// reference decoder, and require it to agree with us.
/// <para>
/// This is the direction <see cref="Jbig2EncoderFixtureTests"/> cannot cover.
/// jbig2enc only ever emits GBTEMPLATE 0 with nominal AT pixels, so those
/// fixtures say nothing about templates 1-3 or a moved AT pixel. Here we drive
/// the stream, so every template, every AT placement, and TPGDON on and off all
/// get checked against an implementation that shares no code with ours.
/// </para>
/// <para>
/// <b>Why this catches template bugs that a round-trip cannot.</b> Our test
/// encoder forms contexts with the shipped decoder's own
/// <c>GenericRegionDecoder.Context</c>, so a template reading the wrong pixel
/// leaves the two agreeing with each other perfectly — the round-trip tests pass
/// on a stream no conforming decoder could read. jbig2dec's templates are its
/// own. If ours read a different pixel set, its raster comes back garbage.
/// </para>
/// <para>
/// Confirmed by deliberately breaking the decoder: changing one template pixel's
/// coordinate fails five tests here while every round-trip test still passes.
/// Note the converse, which is the more surprising half — merely <em>swapping
/// two template bits</em> does not fail anything, because a context value is just
/// an index into adaptive-state slots and a permutation preserves which pixels
/// share one. The pixel set is what conformance rests on; the bit numbering is
/// pinned separately, by the one-hot tests, and matters mainly because TPGDON's
/// SLTP contexts are hard-coded constants in the spec's numbering.
/// </para>
/// <para>
/// Skips (visibly, via <c>Assert.SkipUnless</c> — not a silent pass) when
/// jbig2dec is absent. Install: <c>apt-get install jbig2dec</c>, or on Windows
/// <c>wsl -- sudo apt-get install -y jbig2dec</c>.
/// </para>
/// </summary>
public sealed class Jbig2OracleTests
{
    private static readonly string[] Pattern =
    [
        "................................",
        "..####...####...#....#..######..",
        "..#..........#..#....#..#.......",
        "..#..........#..#....#..#.......",
        "..####...####...######..####....",
        "..#..........#.......#..#.......",
        "..#..........#.......#..#.......",
        "..####...####........#..######..",
        "................................",
        ".#.#.#.#.#.#.#.#.#.#.#.#.#.#.#.#",
        "################################",
        "#..............................#",
        "#..##########################..#",
        "#..............................#",
        "################################",
        "................................",
    ];

    /// <summary>Wraps generic-region coded data in a standalone .jb2 file jbig2dec can open.</summary>
    private static byte[] BuildFile(Jbig2Bitmap source, int template, bool typicalPrediction, sbyte[]? at = null) =>
        Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(
                    source, template: template, typicalPrediction: typicalPrediction, at: at)),
            Jbig2StreamBuilder.Segment(2, SegmentType.EndOfPage, 1, []),
            Jbig2StreamBuilder.Segment(3, SegmentType.EndOfFile, 0, []));

    private static void ShouldMatch(Jbig2Oracle.Raster actual, Jbig2Bitmap expected)
    {
        actual.Width.ShouldBe(expected.Width);
        actual.Height.ShouldBe(expected.Height);

        // Compare as pictures so a failure shows the divergence rather than 512
        // unlabelled bytes.
        Jbig2StreamBuilder.ToRows(actual.Width, actual.Height, actual.Bits)
            .ShouldBe(Jbig2StreamBuilder.ToRows(expected.Width, expected.Height, expected.Data));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void OurEncode_DecodedByJbig2dec_MatchesTheSource(int template, bool typicalPrediction)
    {
        Jbig2Oracle.RequireOrSkip();

        var source = Jbig2StreamBuilder.FromRows(Pattern);
        var decoded = Jbig2Oracle.Decode(BuildFile(source, template, typicalPrediction));

        ShouldMatch(decoded, source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OurEncode_WithMovedAtPixels_DecodedByJbig2dec_MatchesTheSource(int template)
    {
        // The case no jbig2enc fixture can reach: it only ever emits nominal AT
        // pixels, so nothing else here checks that a moved AT offset is applied
        // as T.88 specifies. Get the offset arithmetic wrong and the template
        // reads a different pixel — which is precisely the class of error
        // jbig2dec detects.
        Jbig2Oracle.RequireOrSkip();

        sbyte[] at = template == 0 ? [-4, -1, 2, -3, -2, -3, 1, -3] : [-4, -1];

        var source = Jbig2StreamBuilder.FromRows(Pattern);
        var decoded = Jbig2Oracle.Decode(BuildFile(source, template, false, at));

        ShouldMatch(decoded, source);
    }

    [Fact]
    public void OurEmbeddedStream_DecodedByJbig2dec_MatchesTheSource()
    {
        // The PDF-shaped form: no file header, jbig2dec invoked with -e. Same
        // bytes a /JBIG2Decode stream would carry.
        Jbig2Oracle.RequireOrSkip();

        var source = Jbig2StreamBuilder.FromRows(Pattern);
        var embedded = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(source.Width, source.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(source)));

        ShouldMatch(Jbig2Oracle.Decode(embedded, embedded: true), source);
    }

    [Fact]
    public void OurEncode_LargerNoisyBitmap_DecodedByJbig2dec_MatchesTheSource()
    {
        // Bigger than one MQ byte-stuffing cycle, and noisy enough to drive the
        // probability estimator across a wide span of the Qe table — including
        // the 0xFF stuffing path that a small tidy pattern never reaches.
        Jbig2Oracle.RequireOrSkip();

        var random = new Random(20260728);
        var source = new Jbig2Bitmap(101, 73);
        for (var y = 0; y < source.Height; y++)
            for (var x = 0; x < source.Width; x++)
                source.Data[y * source.Width + x] =
                    (byte)(random.Next(4) == 0 || (x / 7 + y / 5) % 3 == 0 ? 1 : 0);

        ShouldMatch(Jbig2Oracle.Decode(BuildFile(source, 0, false)), source);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OurEncode_DegenerateBitmaps_DecodedByJbig2dec_MatchTheSource(int template)
    {
        // All-white, all-black, and a one-pixel-tall strip: the edges where the
        // off-bitmap "reads as white" rule does most of the work, checked against
        // an implementation that applies that rule independently.
        Jbig2Oracle.RequireOrSkip();

        var white = new Jbig2Bitmap(24, 9);
        ShouldMatch(Jbig2Oracle.Decode(BuildFile(white, template, false)), white);

        var black = new Jbig2Bitmap(24, 9);
        black.Data.AsSpan().Fill(1);
        ShouldMatch(Jbig2Oracle.Decode(BuildFile(black, template, true)), black);

        var strip = Jbig2StreamBuilder.FromRows("#.##..###...####");
        ShouldMatch(Jbig2Oracle.Decode(BuildFile(strip, template, false)), strip);
    }

    [Fact]
    public void Oracle_IsActuallyRunning()
    {
        // Guards the guard. Every other test here skips when jbig2dec is absent,
        // so without this a broken resolver would look like a clean run. This one
        // skips too, but it fails loudly if the oracle reports available and then
        // cannot decode the simplest possible stream.
        Jbig2Oracle.RequireOrSkip();

        var source = Jbig2StreamBuilder.FromRows("##", "##");
        var decoded = Jbig2Oracle.Decode(BuildFile(source, 0, false));

        decoded.Bits.ShouldAllBe(b => b == 1);
    }
}
