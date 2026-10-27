using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Refinement regions pushed through jbig2dec — the layer that establishes the
/// §6.3 templates read the right pixels.
/// <para>
/// Nothing else can. The round-trip in <c>Jbig2RefinementTests</c> encodes with
/// the decoder's own <c>Context</c>, so both sides share any mistake made there;
/// the one-hot tests pin the bit <em>numbering</em> but say nothing about which
/// coordinates are read. Only a decoder with its own templates can tell us the
/// pixel set is right, and jbig2dec is the only one available — jbig2enc's
/// <c>-r</c> flag produces an empty file, so there is no fixture route either.
/// </para>
/// <para>
/// <b>Why these streams refine the page rather than an intermediate region.</b>
/// The intermediate shape is the canonical one in §7.4.7.2 and the one a real
/// encoder emits, but jbig2dec rejects it outright — <c>unhandled segment type
/// 'intermediate generic region' (NYI)</c> — so it cannot be the oracle path.
/// Refining the page in place exercises the same §6.3 decoding procedure with
/// the same templates; the intermediate route is covered by
/// <c>Jbig2RefinementTests</c> against our own decoder, which is all that is
/// available for it.
/// </para>
/// </summary>
public sealed class Jbig2RefinementOracleTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Theory]
    [MemberData(nameof(Jbig2RefinementTests.Cases), MemberType = typeof(Jbig2RefinementTests))]
    public void RefinedRegion_AgreesWithJbig2dec(string edit, int template, bool tpgron)
    {
        Jbig2Oracle.RequireOrSkip();

        const int w = 40, h = 24;
        var (reference, target) = Jbig2RefinementTests.Build(edit, w, h);

        var file = PageRefinementFile(reference, target, template, tpgron);

        var raster = Jbig2Oracle.Decode(file);
        _out.WriteLine($"{edit} GRTEMPLATE {template} TPGRON {tpgron}: {file.Length} bytes");

        raster.Width.ShouldBe(w);
        raster.Height.ShouldBe(h);
        ShouldMatch(raster.Bits, target.Data, w, h, $"{edit}/{template}/{tpgron}");

        // And our own decode of the same file, so the segment layer is covered too.
        Jbig2Decoder.DecodeFile(file).Bits.ToArray().ShouldBe(target.Data);
    }

    /// <summary>
    /// Moved AT pixels, which only GRTEMPLATE 0 has. Nominal offsets are the case
    /// every encoder emits, so a template whose adaptive cells were wired to the
    /// wrong place would go unnoticed without this.
    /// </summary>
    [Theory]
    [InlineData(-1, -1, -1, -1)]   // nominal
    [InlineData(-2, -1, -1, -1)]
    [InlineData(2, -1, -1, -1)]
    [InlineData(-1, -1, 1, 1)]
    [InlineData(-3, -2, 2, -2)]
    public void MovedAtPixels_AgreeWithJbig2dec(int a1x, int a1y, int a2x, int a2y)
    {
        Jbig2Oracle.RequireOrSkip();

        var (reference, target) = Jbig2RefinementTests.Build("speckle", 40, 24);
        sbyte[] at = [(sbyte)a1x, (sbyte)a1y, (sbyte)a2x, (sbyte)a2y];

        var file = PageRefinementFile(reference, target, template: 0, tpgron: false, at);

        Jbig2Oracle.Decode(file).Bits.ShouldBe(target.Data, $"AT ({a1x},{a1y}) ({a2x},{a2y})");
        Jbig2Decoder.DecodeFile(file).Bits.ToArray().ShouldBe(target.Data);
    }

    /// <summary>
    /// Compares rasters, and on a mismatch says <em>where</em> — a refinement
    /// that desynchronises mid-stream looks like noise from some row onward, and
    /// the row it starts on is the whole diagnosis.
    /// </summary>
    private static void ShouldMatch(ReadOnlySpan<byte> actual, byte[] expected, int w, int h, string what)
    {
        if (actual.SequenceEqual(expected)) return;

        var first = -1;
        for (var i = 0; i < expected.Length && first < 0; i++)
            if (actual[i] != expected[i])
                first = i;

        throw new Shouldly.ShouldAssertException(
            $"jbig2dec disagrees for {what}, first at ({first % w},{first / w}) of {w}x{h}\n" +
            "expected:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(w, h, expected)) +
            "\njbig2dec:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(w, h, actual)));
    }

    /// <summary>
    /// A generic region painting <paramref name="reference"/> onto the page, then
    /// a refinement region correcting that same rectangle in place.
    /// </summary>
    /// <remarks>
    /// The refinement composites with REPLACE, and the page information says
    /// operators may be overridden (§7.4.8.5). Under the page default of OR a
    /// refinement could only ever add black, so every case here that clears a
    /// pixel would be untestable — which is exactly how the operator question got
    /// settled in the first place.
    /// </remarks>
    private static byte[] PageRefinementFile(
        Jbig2Bitmap reference, Jbig2Bitmap target, int template, bool tpgron, sbyte[]? at = null) =>
        Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(
                    reference.Width, reference.Height, allowOperatorOverride: true)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(reference)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateRefinementRegion, 1,
                Jbig2StreamBuilder.RefinementRegionSegment(
                    target, reference, op: CombinationOperator.Replace,
                    template: template, typicalPrediction: tpgron, at: at)),
            Jbig2StreamBuilder.Segment(3, SegmentType.EndOfPage, 1, []));
}
