using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Tests for the generic region decoding procedure of ITU-T T.88 §6.2.
/// <para>
/// The context-template tests set exactly one pixel in the neighbourhood and
/// require the CONTEXT value to be exactly the corresponding power of two,
/// template cell by template cell — a complete one-hot enumeration of T.88
/// Figures 4-7. A round-trip through <see cref="Jbig2StreamBuilder"/> cannot
/// check any of it, because its encoder forms contexts with the same code the
/// decoder does and would share the mistake.
/// </para>
/// <para>
/// Be precise about what these buy, though. They pin two things, and the two are
/// not equally load-bearing:
/// </para>
/// <list type="number">
/// <item><b>Which pixels each template reads</b> — the set, AT offsets included.
/// This is what conformance turns on: read the wrong pixel and every conforming
/// decoder disagrees. Also covered, harder, by <see cref="Jbig2OracleTests"/>
/// against jbig2dec.</item>
/// <item><b>Which bit each cell occupies</b> — mostly free. A context value is
/// only an index into adaptive-state slots that all start identical, so permuting
/// the bit positions preserves which pixels share a slot and leaves the coded
/// bytes unchanged. It bites in exactly one place: TPGDON's SLTP decision uses
/// hard-coded context constants (0x9B25 and friends), which name a specific
/// neighbourhood in the spec's numbering, so a permutation changes which
/// neighbourhood collides with that slot.</item>
/// </list>
/// <para>
/// Both established by deliberately breaking the decoder rather than by
/// reasoning alone: swapping two template bits left jbig2dec still agreeing with
/// us, while changing one template pixel's coordinate made it disagree at once.
/// </para>
/// </summary>
public sealed class Jbig2GenericRegionTests
{
    // T.88 Figures 4-7, written out as (dx, dy, bit) relative to the pixel being
    // decoded, with each template's AT pixels at their nominal offsets.
    private static readonly (int Dx, int Dy, int Bit)[] Template0 =
    [
        (-2, -2, 15),  // A4
        (-1, -2, 14), (0, -2, 13), (1, -2, 12),
        (2, -2, 11),   // A3
        (-3, -1, 10),  // A2
        (-2, -1, 9), (-1, -1, 8), (0, -1, 7), (1, -1, 6), (2, -1, 5),
        (3, -1, 4),    // A1
        (-4, 0, 3), (-3, 0, 2), (-2, 0, 1), (-1, 0, 0),
    ];

    private static readonly (int Dx, int Dy, int Bit)[] Template1 =
    [
        (-1, -2, 12), (0, -2, 11), (1, -2, 10), (2, -2, 9),
        (-2, -1, 8), (-1, -1, 7), (0, -1, 6), (1, -1, 5), (2, -1, 4),
        (3, -1, 3),    // A1
        (-3, 0, 2), (-2, 0, 1), (-1, 0, 0),
    ];

    private static readonly (int Dx, int Dy, int Bit)[] Template2 =
    [
        (-1, -2, 9), (0, -2, 8), (1, -2, 7),
        (-2, -1, 6), (-1, -1, 5), (0, -1, 4), (1, -1, 3),
        (2, -1, 2),    // A1
        (-2, 0, 1), (-1, 0, 0),
    ];

    private static readonly (int Dx, int Dy, int Bit)[] Template3 =
    [
        (-3, -1, 9), (-2, -1, 8), (-1, -1, 7), (0, -1, 6), (1, -1, 5),
        (2, -1, 4),    // A1
        (-4, 0, 3), (-3, 0, 2), (-2, 0, 1), (-1, 0, 0),
    ];

    private static (int Dx, int Dy, int Bit)[] Cells(int template) => template switch
    {
        0 => Template0,
        1 => Template1,
        2 => Template2,
        _ => Template3,
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Context_EachTemplateCell_SetsExactlyItsOwnBit(int template)
    {
        var at = GenericRegionDecoder.NominalAt(template).ToArray();
        const int cx = 6, cy = 4;

        foreach (var (dx, dy, bit) in Cells(template))
        {
            var bitmap = new Jbig2Bitmap(12, 8);
            bitmap.Data[(cy + dy) * bitmap.Width + cx + dx] = 1;

            GenericRegionDecoder.Context(bitmap, cx, cy, template, at)
                .ShouldBe(1 << bit, $"GBTEMPLATE {template} cell ({dx},{dy})");
        }
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 13)]
    [InlineData(2, 10)]
    [InlineData(3, 10)]
    public void Context_AllTemplateCells_FillsEveryContextBit(int template, int contextBits)
    {
        // Every cell distinct, and together they cover the context word exactly:
        // no gaps (a cell mapped to the wrong bit) and no overflow.
        GenericRegionDecoder.ContextBits(template).ShouldBe(contextBits);
        Cells(template).Length.ShouldBe(contextBits);

        var at = GenericRegionDecoder.NominalAt(template).ToArray();
        var bitmap = new Jbig2Bitmap(12, 8);
        const int cx = 6, cy = 4;

        foreach (var (dx, dy, _) in Cells(template))
            bitmap.Data[(cy + dy) * bitmap.Width + cx + dx] = 1;

        GenericRegionDecoder.Context(bitmap, cx, cy, template, at).ShouldBe((1 << contextBits) - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Context_PixelsOutsideTheTemplate_ContributeNothing(int template)
    {
        var at = GenericRegionDecoder.NominalAt(template).ToArray();
        const int cx = 6, cy = 4;
        var cells = Cells(template).Select(c => (c.Dx, c.Dy)).ToHashSet();

        // Sweep the whole causal neighbourhood; anything the template does not
        // name must leave the context at zero.
        for (var dy = -3; dy <= 0; dy++)
        {
            for (var dx = -5; dx <= 4; dx++)
            {
                if (cells.Contains((dx, dy)) || (dx == 0 && dy == 0)) continue;

                var bitmap = new Jbig2Bitmap(14, 8);
                bitmap.Data[(cy + dy) * bitmap.Width + cx + dx] = 1;

                GenericRegionDecoder.Context(bitmap, cx, cy, template, at)
                    .ShouldBe(0, $"GBTEMPLATE {template} should ignore ({dx},{dy})");
            }
        }
    }

    [Fact]
    public void Context_MovedAtPixel_KeepsItsTemplateCellsBit()
    {
        // A1 moved from its nominal (+3,-1) to (-5,-3). The pixel it now points
        // at must light bit 4 — the cell A1 occupies in Figure 4 — and the
        // nominal position must go quiet.
        sbyte[] at = [-5, -3, -3, -1, 2, -2, -2, -2];
        const int cx = 6, cy = 4;

        var moved = new Jbig2Bitmap(12, 8);
        moved.Data[(cy - 3) * moved.Width + cx - 5] = 1;
        GenericRegionDecoder.Context(moved, cx, cy, 0, at).ShouldBe(1 << 4);

        var nominal = new Jbig2Bitmap(12, 8);
        nominal.Data[(cy - 1) * nominal.Width + cx + 3] = 1;
        GenericRegionDecoder.Context(nominal, cx, cy, 0, at).ShouldBe(0);
    }

    [Fact]
    public void Context_OffBitmapNeighbours_ReadAsWhite()
    {
        // T.88 §6.2.5.2: template pixels above row 0 or beyond the sides are 0.
        // Without that the first two rows and the left edge could not decode.
        var bitmap = new Jbig2Bitmap(4, 4);
        bitmap.Data.AsSpan().Fill(1);

        GenericRegionDecoder.Context(bitmap, 0, 0, 0, GenericRegionDecoder.NominalAt(0).ToArray()).ShouldBe(0);
    }

    [Fact]
    public void TypicalPredictionContexts_MatchT88Section6257()
    {
        GenericRegionDecoder.TypicalPredictionContext(0).ShouldBe(0x9B25);
        GenericRegionDecoder.TypicalPredictionContext(1).ShouldBe(0x0795);
        GenericRegionDecoder.TypicalPredictionContext(2).ShouldBe(0x00E5);
        GenericRegionDecoder.TypicalPredictionContext(3).ShouldBe(0x0195);
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
    public void RoundTrip_TextLikePattern_IsLossless(int template, bool typicalPrediction)
    {
        var source = Jbig2StreamBuilder.FromRows(
            "........................",
            "..####...####...#....#..",
            "..#..........#..#....#..",
            "..#..........#..#....#..",
            "..####...####...######..",
            "..#..........#.......#..",
            "..#..........#.......#..",
            "..####...####........#..",
            "........................",
            "..######################",
            "........................",
            "..#.#.#.#.#.#.#.#.#.#.#.");

        RoundTrip(source, template, typicalPrediction, [.. GenericRegionDecoder.NominalAt(template)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RoundTrip_MovedAtPixels_IsLossless(int template)
    {
        // Non-nominal but still causal AT offsets. This is the case the one-hot
        // tests protect and the nominal round-trips cannot reach.
        sbyte[] at = template == 0 ? [-4, -1, 2, -3, -2, -3, 1, -3] : [-4, -1];

        var source = Jbig2StreamBuilder.FromRows(
            "#....#....#....#",
            ".#..#..#..#..#..",
            "..##....##....##",
            "................",
            "################",
            "#..............#",
            "#..####..####..#",
            "#..#..#..#..#..#",
            "#..####..####..#",
            "#..............#",
            "################");

        RoundTrip(source, template, typicalPrediction: false, at);
        RoundTrip(source, template, typicalPrediction: true, at);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RoundTrip_DegenerateBitmaps_AreLossless(int template)
    {
        var at = GenericRegionDecoder.NominalAt(template).ToArray();

        // All white, all black, a single pixel, and one-pixel-wide/tall regions —
        // the edges where the off-bitmap rule does most of the work.
        RoundTrip(new Jbig2Bitmap(9, 7), template, false, at);

        var black = new Jbig2Bitmap(9, 7);
        black.Data.AsSpan().Fill(1);
        RoundTrip(black, template, false, at);

        var single = new Jbig2Bitmap(9, 7);
        single.Data[3 * 9 + 4] = 1;
        RoundTrip(single, template, false, at);

        RoundTrip(Jbig2StreamBuilder.FromRows("#"), template, false, at);
        RoundTrip(Jbig2StreamBuilder.FromRows("#.#.#.#.##"), template, false, at);
        RoundTrip(Jbig2StreamBuilder.FromRows("#", ".", "#", "#", "."), template, false, at);
    }

    [Fact]
    public void TypicalPrediction_OnRepeatedRows_CostsAlmostNothing()
    {
        // The point of TPGDON: a run of identical rows should code as a couple of
        // SLTP bits rather than a row of pixel decisions each. A stripe pattern
        // with long vertical runs is the case it was designed for.
        var rows = new string[64];
        for (var y = 0; y < rows.Length; y++)
            rows[y] = y % 16 < 8 ? new string('#', 64) : new string('.', 64);

        var source = Jbig2StreamBuilder.FromRows(rows);
        var at = GenericRegionDecoder.NominalAt(0).ToArray();

        var withPrediction = Jbig2StreamBuilder.EncodeGenericRegion(source, 0, true, at);
        var without = Jbig2StreamBuilder.EncodeGenericRegion(source, 0, false, at);

        withPrediction.Length.ShouldBeLessThan(without.Length);
        RoundTrip(source, 0, true, at);
    }

    [Fact]
    public void TypicalPrediction_LeadingBlankRows_StayBlank()
    {
        // Row 0 has nothing above it to copy, so LTP=1 there means "white".
        // Encoder and decoder have to agree about that or everything after it
        // shifts.
        var source = Jbig2StreamBuilder.FromRows(
            "........",
            "........",
            "........",
            "..####..",
            "..####..",
            "........");

        RoundTrip(source, 0, typicalPrediction: true, [.. GenericRegionDecoder.NominalAt(0)]);
    }

    [Fact]
    public void Decode_TruncatedCodedData_ProducesAPageInsteadOfThrowing()
    {
        // T.88's end-of-data convention feeds 0xFF forever, so a clipped region
        // degrades into wrong pixels rather than an exception. Callers get a
        // raster; they do not get a crash.
        var source = Jbig2StreamBuilder.FromRows(
            "..####..",
            "..#..#..",
            "..####..",
            "........");

        var at = GenericRegionDecoder.NominalAt(0).ToArray();
        var coded = Jbig2StreamBuilder.EncodeGenericRegion(source, 0, false, at);

        var contexts = new byte[1 << GenericRegionDecoder.ContextBits(0)];
        var mq = new MqDecoder(coded.AsSpan(0, 1));
        var decoded = GenericRegionDecoder.Decode(
            ref mq, contexts, 8, 4, 0, false, at, Jbig2PixelBudget.Unmetered());

        decoded.Width.ShouldBe(8);
        decoded.Height.ShouldBe(4);
    }

    private static void RoundTrip(Jbig2Bitmap source, int template, bool typicalPrediction, sbyte[] at)
    {
        var coded = Jbig2StreamBuilder.EncodeGenericRegion(source, template, typicalPrediction, at);

        var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];
        var mq = new MqDecoder(coded);
        var decoded = GenericRegionDecoder.Decode(
            ref mq, contexts, source.Width, source.Height, template, typicalPrediction, at,
            Jbig2PixelBudget.Unmetered());

        Jbig2StreamBuilder.ToRows(decoded.Width, decoded.Height, decoded.Data)
            .ShouldBe(Jbig2StreamBuilder.ToRows(source.Width, source.Height, source.Data));
    }
}
