using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Generic refinement regions — T.88 §6.3 and §7.4.7.
/// <para>
/// Refinement is the one rung with no third-party encoder available: jbig2enc
/// advertises <c>-r</c> but emits an empty file, and no other tool this repo can
/// legally use produces refinement segments. So the streams here are synthesised
/// by <c>Jbig2StreamBuilder</c> and the conformance check is
/// <c>Jbig2RefinementOracleTests</c> pushing them through jbig2dec — the same
/// split rung 1 uses, and for the same reason: a round-trip through our own
/// encoder shares the decoder's templates and so cannot validate them.
/// </para>
/// </summary>
public sealed class Jbig2RefinementTests
{
    /// <summary>
    /// Reference/target pairs chosen for what they make the coder do. A refined
    /// bitmap identical to its reference is the cheap case TPGRON exists for;
    /// edits at edges are where the reference neighbourhood stops being uniform
    /// and real decisions get coded.
    /// </summary>
    public static TheoryData<string, int, bool> Cases()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (var edit in (string[])["identical", "speckle", "grow", "shrink", "shift", "inverted"])
            foreach (var template in (int[])[0, 1])
                // TPGRON is refused rather than decoded — see
                // RefinementRegionDecoder's remarks and the refusal test below.
                data.Add(edit, template, false);

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTrip_ReproducesTheRefinedBitmap(string edit, int template, bool tpgron)
    {
        var (reference, target) = Build(edit, 40, 24);

        var coded = Jbig2StreamBuilder.EncodeRefinementRegion(
            target, reference, template, tpgron, [.. RefinementRegionDecoder.NominalAt]);

        var contexts = new byte[1 << RefinementRegionDecoder.ContextBits(template)];
        var mq = new MqDecoder(coded);
        var decoded = RefinementRegionDecoder.Decode(
            ref mq, contexts, target.Width, target.Height, template,
            reference, 0, 0, tpgron, RefinementRegionDecoder.NominalAt, Jbig2PixelBudget.Unmetered());

        decoded.Data.ShouldBe(target.Data);
    }

    /// <summary>
    /// A refinement region that names an intermediate generic region takes that
    /// region's buffer as its reference (§7.4.7.2) — and the intermediate region
    /// itself must never reach the page.
    /// </summary>
    [Fact]
    public void RefinementRegion_RefinesTheIntermediateRegionItNames()
    {
        var (reference, target) = Build("grow", 40, 24);

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(40, 24)),
            Jbig2StreamBuilder.Segment(1, SegmentType.IntermediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(reference)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateRefinementRegion, 1,
                Jbig2StreamBuilder.RefinementRegionSegment(target, reference), referredTo: [1]));

        Jbig2Decoder.Decode(stream, 40, 24).Bits.ToArray().ShouldBe(target.Data);
    }

    /// <summary>
    /// With no intermediate region to name, a refinement region corrects the page
    /// itself — and <em>replaces</em> the rectangle rather than OR-ing into it.
    /// The distinction is visible precisely when the refinement clears pixels:
    /// under OR the "shrink" case would keep every pixel the reference had.
    /// </summary>
    [Fact]
    public void RefinementRegion_WithNoReference_ReplacesThePageRectangle()
    {
        var (reference, target) = Build("shrink", 40, 24);
        target.Data.ShouldNotBe(reference.Data, "the shrink case must actually clear pixels to be a test");

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(40, 24, allowOperatorOverride: true)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(reference)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateRefinementRegion, 1,
                Jbig2StreamBuilder.RefinementRegionSegment(
                    target, reference, op: CombinationOperator.Replace)));

        Jbig2Decoder.Decode(stream, 40, 24).Bits.ToArray().ShouldBe(target.Data);
    }

    /// <summary>
    /// A refinement placed away from the origin refines the page under its own
    /// rectangle, not under (0,0).
    /// </summary>
    [Fact]
    public void RefinementRegion_RefinesThePageUnderItsOwnRectangle()
    {
        var page = Jbig2StreamBuilder.FromRows(
            "................",
            "..####....####..",
            "..####....####..",
            "................");

        // Blank out the right-hand block by refining just that rectangle.
        var reference = Jbig2StreamBuilder.FromRows("####", "####");
        var target = Jbig2StreamBuilder.FromRows("....", "....");

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(16, 4, allowOperatorOverride: true)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(page)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateRefinementRegion, 1,
                Jbig2StreamBuilder.RefinementRegionSegment(
                    target, reference, x: 10, y: 1, op: CombinationOperator.Replace)));

        Jbig2StreamBuilder.ToRows(16, 4, Jbig2Decoder.Decode(stream, 16, 4).Bits).ShouldBe(
        [
            "................",
            "..####..........",
            "..####..........",
            "................",
        ]);
    }

    /// <summary>
    /// TPGRON is refused. §6.3 is otherwise confirmed against jbig2dec, but with
    /// this flag the two decoders disagree in ways that measurement narrowed and
    /// did not resolve — see RefinementRegionDecoder's remarks. "Mostly agrees"
    /// is not a decoder a caller can use, so it throws.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Tpgron_IsRefusedByName(int template)
    {
        var (reference, target) = Build("identical", 16, 8);
        var contexts = new byte[1 << RefinementRegionDecoder.ContextBits(template)];

        var exception = Should.Throw<NotSupportedException>(() =>
        {
            var mq = new MqDecoder(new byte[16]);
            RefinementRegionDecoder.Decode(
                ref mq, contexts, target.Width, target.Height, template,
                reference, 0, 0, typicalPrediction: true, RefinementRegionDecoder.NominalAt,
                Jbig2PixelBudget.Unmetered());
        });

        exception.Message.ShouldContain("TPGRON");
    }

    /// <summary>And the refusal has to survive the segment layer, not just the inner call.</summary>
    [Fact]
    public void Tpgron_InARefinementSegment_IsRefused()
    {
        var (reference, target) = Build("identical", 16, 8);

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(16, 8)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.GenericRegionSegment(reference)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateRefinementRegion, 1,
                Jbig2StreamBuilder.RefinementRegionSegment(target, reference, typicalPrediction: true)));

        Should.Throw<NotSupportedException>(() => Jbig2Decoder.Decode(stream, 16, 8))
            .Message.ShouldContain("TPGRON");
    }

    [Fact]
    public void RefinementRegion_TruncatedFlags_Fails()
    {
        // A valid 8x8 region info and then nothing — the flags byte the segment
        // promises is missing.
        byte[] data =
        [
            0x00, 0x00, 0x00, 0x08,   // width 8
            0x00, 0x00, 0x00, 0x08,   // height 8
            0x00, 0x00, 0x00, 0x00,   // x
            0x00, 0x00, 0x00, 0x00,   // y
            0x00,                     // combination operator OR
        ];

        var stream = Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateRefinementRegion, 1, data);

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.Decode(stream, 8, 8))
            .Message.ShouldContain("refinement");
    }

    /// <summary>
    /// Each refinement template cell, set alone, must contribute exactly its own
    /// power of two — the same pinning the generic templates get, and the layer
    /// that catches a bit landing in the wrong place. The pixel <em>set</em> is
    /// what jbig2dec checks; this checks the numbering the SLTP constants depend
    /// on.
    /// </summary>
    [Theory]
    [InlineData(0, 13)]
    [InlineData(1, 10)]
    public void EveryTemplateCell_ContributesItsOwnBit(int template, int bits)
    {
        var at = (sbyte[])[.. RefinementRegionDecoder.NominalAt];

        // A cell may live in either bitmap, so both are swept. The probe point is
        // kept away from the edges so no template cell falls outside.
        const int w = 9, h = 9, px = 4, py = 4;
        var seen = new HashSet<int>();

        for (var which = 0; which < 2; which++)
        {
            for (var dy = -3; dy <= 3; dy++)
            {
                for (var dx = -3; dx <= 3; dx++)
                {
                    var b = new Jbig2Bitmap(w, h);
                    var r = new Jbig2Bitmap(w, h);
                    (which == 0 ? b : r).Data[(py + dy) * w + px + dx] = 1;

                    var context = RefinementRegionDecoder.Context(b, px, py, r, px, py, template, at);
                    if (context == 0) continue;

                    context.ShouldBeOneOf([.. Enumerable.Range(0, bits).Select(i => 1 << i)]);
                    seen.Add(context).ShouldBeTrue(
                        $"{(which == 0 ? "current" : "reference")} ({dx},{dy}) duplicates bit {context:x}");
                }
            }
        }

        seen.Count.ShouldBe(bits, $"GRTEMPLATE {template} should read exactly {bits} cells");
    }

    /// <summary>
    /// Deterministic reference/target pairs. The target is always a small edit of
    /// the reference, which is what refinement is for.
    /// </summary>
    internal static (Jbig2Bitmap Reference, Jbig2Bitmap Target) Build(string edit, int width, int height)
    {
        var reference = new Jbig2Bitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                reference.Data[y * width + x] =
                    (x / 5 + y / 4) % 2 == 0 && x > 2 && x < width - 3 && y > 1 && y < height - 2 ? (byte)1 : (byte)0;

        var target = new Jbig2Bitmap(width, height);
        reference.Data.CopyTo(target.Data, 0);

        switch (edit)
        {
            case "identical":
                break;

            case "speckle":
                for (var i = 0; i < target.Data.Length; i += 37) target.Data[i] ^= 1;
                break;

            case "grow":
                // Thicken every black run by one pixel to the right.
                for (var y = 0; y < height; y++)
                    for (var x = width - 1; x > 0; x--)
                        if (reference.Data[y * width + x - 1] != 0) target.Data[y * width + x] = 1;
                break;

            case "shrink":
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width - 1; x++)
                        if (reference.Data[y * width + x + 1] == 0) target.Data[y * width + x] = 0;
                break;

            case "shift":
                Array.Clear(target.Data);
                for (var y = 0; y < height - 1; y++)
                    reference.Data.AsSpan(y * width, width).CopyTo(target.Data.AsSpan((y + 1) * width));
                break;

            case "inverted":
                for (var i = 0; i < target.Data.Length; i++) target.Data[i] ^= 1;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(edit), edit, "Unknown refinement edit.");
        }

        return (reference, target);
    }
}
