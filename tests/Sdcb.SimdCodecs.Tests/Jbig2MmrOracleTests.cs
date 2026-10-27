using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The MMR decoder checked against third-party T.6 bytes: ImageMagick/libtiff
/// encodes a raster as CCITT Group 4, and <c>Sdcb.SimdCodecs.Jbig2</c> has to get the
/// same pixels back.
/// <para>
/// This is the layer the synthetic tests cannot reach. There is no MMR encoder
/// in this repo — encoding is a stated non-goal — so unlike the arithmetic
/// generic-region path there is no round-trip available at all, and the code
/// tables have no self-consistency check to hide behind. A transcription error
/// in T.4 Table 2 or 3 produces a wrong run length, and nothing but a foreign
/// encoder will say so.
/// </para>
/// <para>
/// The last case runs the same bytes on through the segment layer and past
/// jbig2dec, which closes the loop: libtiff wrote them, we decode them, and the
/// reference JBIG2 decoder agrees on the result.
/// </para>
/// </summary>
public sealed class Jbig2MmrOracleTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>
    /// Patterns chosen for what they force the coder to do, not for looking like
    /// anything: solid fields keep it in vertical mode, fine detail forces
    /// horizontal mode and short run codes, and the wide cases push run lengths
    /// past 64 (makeup codes) and past 2560 (the shared extended makeup codes,
    /// chained).
    /// </summary>
    public static TheoryData<string, int, int> Patterns => new()
    {
        { "white",      64,  16 },
        { "black",      64,  16 },
        { "checker1",   64,  16 },   // every pixel a changing element
        { "checker8",   64,  32 },
        { "vstripes",   61,  17 },   // non-byte-aligned width
        { "hstripes",   64,  16 },   // identical rows: pure vertical mode
        { "diagonal",   96,  48 },   // one changing element sliding one pixel a row
        // Edges moving by exactly 2 and 3 pixels a row, forwards and back: the
        // only way to reach VR2/VR3/VL2/VL3, which every picture-shaped pattern
        // above leaves almost untouched.
        { "slope2",    120,  40 },
        { "slope3",    140,  40 },
        { "backslope3", 140, 40 },
        { "dots",       57,  23 },
        { "noise",      80,  40 },   // horizontal mode, constantly
        { "text",      200,  60 },
        { "wide",     3000,   6 },   // runs > 2560: extended makeup, chained
        { "edges",      33,   9 },   // black touching both margins
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void Group4Bytes_FromLibTiff_DecodeToTheSamePixels(string pattern, int width, int height)
    {
        var expected = Render(pattern, width, height);
        var encoded = Group4Tiff.Encode(expected, width, height);

        _out.WriteLine($"{pattern} {width}x{height}: {encoded.Coded.Length} coded bytes, " +
                       $"photometric {(encoded.MinIsWhite ? "MinIsWhite" : "MinIsBlack")}");

        var bitmap = MmrDecoder.Decode(encoded.Coded, width, height, Jbig2PixelBudget.Unmetered());
        ShouldMatch(bitmap.Data, expected, width, height);
    }

    /// <summary>
    /// The same libtiff bytes wrapped in a T.88 generic region segment with
    /// <c>MMR = 1</c>, decoded through the public API — so this covers the flags
    /// parsing and the "no AT pixel list when MMR is 1" rule as well as the
    /// coding.
    /// </summary>
    [Theory]
    [MemberData(nameof(Patterns))]
    public void MmrGenericRegion_ThroughTheEmbeddedApi_DecodesToTheSamePixels(string pattern, int width, int height)
    {
        var expected = Render(pattern, width, height);
        var encoded = Group4Tiff.Encode(expected, width, height);

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(width, height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.MmrGenericRegionSegment(width, height, encoded.Coded)));

        var image = Jbig2Decoder.Decode(stream, width, height);
        ShouldMatch(image.Bits.ToArray(), expected, width, height);
    }

    /// <summary>
    /// Closes the loop: libtiff encoded the region, we decoded it, and jbig2dec —
    /// which shares no code with either — has to produce the same page.
    /// </summary>
    [Theory]
    [MemberData(nameof(Patterns))]
    public void MmrGenericRegion_AgreesWithJbig2dec(string pattern, int width, int height)
    {
        Jbig2Oracle.RequireOrSkip();

        var expected = Render(pattern, width, height);
        var encoded = Group4Tiff.Encode(expected, width, height);

        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(width, height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                Jbig2StreamBuilder.MmrGenericRegionSegment(width, height, encoded.Coded)),
            Jbig2StreamBuilder.Segment(2, SegmentType.EndOfPage, 1, []));

        var reference = Jbig2Oracle.Decode(file);
        reference.Width.ShouldBe(width);
        reference.Height.ShouldBe(height);

        // Both halves matter and they fail differently. jbig2dec against the
        // original catches a malformed segment envelope — our MMR = 1 region
        // header, which no other test can check, since the coded bytes came from
        // libtiff and would decode fine out of a broken wrapper. Us against
        // jbig2dec is what makes this an oracle at all.
        ShouldMatch(reference.Bits, expected, width, height);
        ShouldMatch(Jbig2Decoder.DecodeFile(file).Bits, reference.Bits, width, height);
    }

    /// <summary>
    /// Every run length in T.4 Tables 2 and 3, one at a time, against libtiff.
    /// <para>
    /// This exists because the picture-shaped cases above are <b>not</b> enough,
    /// and that was established rather than assumed: mislabelling white run 24 as
    /// 25 left every one of them passing, because none happens to contain a white
    /// run of exactly 24. Correctness here is per table entry, so the coverage has
    /// to be per table entry too.
    /// </para>
    /// <para>
    /// Each length gets a row shaped so the encoder has no choice but to code it
    /// literally: the run sits well past vertical mode's ±3 reach, and its
    /// reference line is all white, so pass mode would swallow the whole row.
    /// That leaves horizontal mode and the exact run asked for.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EveryRunLength_DecodesToItself(int colour)
    {
        // Terminating codes, then every makeup code, then lengths past 2560 that
        // can only be a *chain* of makeup codes — the one rule in T.4 §4.1.2 that
        // no single table entry expresses. Black starts at 1: a zero-length black
        // run needs two changing elements at the same position, so no encoder
        // emits one, and asserting on it would only ever be a fake pass.
        List<int> runs = [.. Enumerable.Range(colour == 0 ? 0 : 1, colour == 0 ? 64 : 63)];
        for (var run = 64; run <= 2560; run += 64) runs.Add(run);
        runs.AddRange([2561, 2624, 2700, 3000, 4000, 5120, 5121]);

        // One test row per length, each preceded by a blank row so its reference
        // line is all white — the same position row 0 is in, and the reason the
        // encoder has no shortcut available. The rows share one image because
        // ImageMagick's per-call overhead, not the pixel count, is what costs.
        var width = runs.Max() + 10;
        var height = runs.Count * 2;
        var expected = new byte[width * height];

        for (var i = 0; i < runs.Count; i++)
        {
            // A white run of N is coded as "white N, black 1"; a black run of N as
            // "white 5, black N". Either way the varying part is the run under
            // test, and its neighbour is a short run covered elsewhere.
            var lead = colour == 0 ? runs[i] : 5;
            var body = colour == 0 ? 1 : runs[i];
            expected.AsSpan((i * 2 + 1) * width + lead, body).Fill(1);
        }

        var encoded = Group4Tiff.Encode(expected, width, height);
        _out.WriteLine($"{(colour == 0 ? "white" : "black")} runs 0-{runs.Max()}: " +
                       $"{runs.Count} lengths, {width}x{height}, {encoded.Coded.Length} coded bytes");

        var actual = MmrDecoder.Decode(encoded.Coded, width, height, Jbig2PixelBudget.Unmetered()).Data;

        for (var i = 0; i < runs.Count; i++)
        {
            var row = i * 2 + 1;
            var got = actual.AsSpan(row * width, width);
            if (got.SequenceEqual(expected.AsSpan(row * width, width))) continue;

            var lead = colour == 0 ? runs[i] : 5;
            var body = colour == 0 ? 1 : runs[i];
            var first = got.IndexOf((byte)1);
            var last = got.LastIndexOf((byte)1);
            throw new Shouldly.ShouldAssertException(
                $"{(colour == 0 ? "white" : "black")} run {runs[i]} (row {row}): " +
                $"expected black at [{lead},{lead + body}), " +
                $"got {(first < 0 ? "none" : $"[{first},{last + 1})")}");
        }
    }

    // ---- helpers -----------------------------------------------------------------

    private static void ShouldMatch(ReadOnlySpan<byte> actual, byte[] expected, int width, int height)
    {
        if (actual.SequenceEqual(expected)) return;

        actual.Length.ShouldBe(expected.Length);

        // Locate the first disagreement before dumping anything: a 3000-pixel
        // wide picture is unreadable, and the coordinate is the useful part.
        for (var i = 0; i < expected.Length; i++)
        {
            if (actual[i] == expected[i]) continue;

            var message = $"first difference at ({i % width},{i / width}): expected {expected[i]}, got {actual[i]}";
            if (width <= 120 && height <= 80)
            {
                message += "\nexpected:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(width, height, expected)) +
                           "\nactual:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(width, height, actual));
            }

            throw new Shouldly.ShouldAssertException(message);
        }
    }

    /// <summary>Deterministic bilevel test patterns, one byte per pixel, 1 = black.</summary>
    private static byte[] Render(string pattern, int width, int height)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var black = pattern switch
                {
                    "white" => false,
                    "black" => true,
                    "checker1" => ((x + y) & 1) == 0,
                    "checker8" => (((x / 8) + (y / 8)) & 1) == 0,
                    "vstripes" => (x / 3) % 2 == 0,
                    "hstripes" => (y / 4) % 2 == 0,
                    "diagonal" => x == y || x == y + 1,
                    "slope2" => x >= 2 * y && x < 2 * y + 20,
                    "slope3" => x >= 3 * y && x < 3 * y + 20,
                    "backslope3" => x >= 130 - 3 * y && x < 150 - 3 * y,
                    "dots" => x % 7 == 3 && y % 5 == 2,
                    "noise" => ((x * 37 + y * 71) ^ (x * y)) % 5 < 2,
                    "text" => IsTextLike(x, y),
                    "wide" => x < 2700 || (x % 3) == 0,
                    "edges" => x == 0 || x == width - 1 || y == 0 || y == height - 1,
                    _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "Unknown test pattern."),
                };

                pixels[y * width + x] = black ? (byte)1 : (byte)0;
            }
        }

        return pixels;
    }

    /// <summary>Blocky glyph-ish shapes — short black runs on mostly white rows, the way scanned text codes.</summary>
    private static bool IsTextLike(int x, int y)
    {
        var line = y % 20;
        if (line is < 4 or > 14) return false;

        var glyph = x / 12;
        var inGlyph = x % 12;
        if (inGlyph > 8) return false;

        var shape = (glyph * 2654435761u) >> 24;
        return ((shape >> (line % 8)) & 1) != 0 || inGlyph == 0;
    }
}
