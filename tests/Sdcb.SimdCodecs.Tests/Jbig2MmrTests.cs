using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// MMR decoding read straight off ITU-T T.4 and T.6: hand-assembled codestreams,
/// the code tables' structure, and the refusals.
/// <para>
/// The empirical check on this decoder is <c>Jbig2MmrOracleTests</c>, which runs
/// real libtiff-produced Group 4 bytes through it. What these tests add is
/// <em>legibility</em> — each vector below is a list of named codes copied from
/// the printed tables, so a reader can confirm the decoder against the spec
/// without owning ImageMagick or trusting it. They also pin the modes the
/// picture-shaped oracle patterns reach only incidentally, and the error paths it
/// never reaches at all.
/// </para>
/// </summary>
public sealed class Jbig2MmrTests
{
    // T.4 Table 4 mode codes, named so the vectors below read as spec rather than
    // as bits. Vertical mode's suffix is the offset from b1.
    private const string V0 = "1";
    private const string Vr1 = "011";
    private const string Vr2 = "000011";
    private const string Vr3 = "0000011";
    private const string Vl1 = "010";
    private const string Vl2 = "000010";
    private const string Vl3 = "0000010";
    private const string Horizontal = "001";
    private const string Pass = "0001";
    private const string Extension = "0000001";

    // T.4 Table 2 terminating codes, only the handful the vectors use.
    private const string White0 = "00110101";
    private const string White2 = "0111";
    private const string White3 = "1000";
    private const string Black1 = "010";
    private const string Black3 = "10";
    private const string Black4 = "011";
    private const string Black5 = "0011";
    private const string Black8 = "000101";

    [Fact]
    public void AllWhite_IsOneVerticalCodePerRow()
    {
        // Nothing on the reference line means b1 sits at the right margin, so V0
        // places this row's only changing element there too: an empty row.
        Decode(8, 3, V0, V0, V0).ShouldBe(
        [
            "........",
            "........",
            "........",
        ]);
    }

    [Fact]
    public void HorizontalMode_CodesTwoExplicitRuns()
    {
        // Row 0 has no usable reference, so the run has to be spelled out: no
        // white, four black, then V0 to close the row at the margin.
        Decode(8, 1, Horizontal, White0, Black4, V0).ShouldBe(["####...."]);
    }

    [Fact]
    public void HorizontalMode_LeadingWhiteRun()
    {
        Decode(8, 1, Horizontal, White2, Black3, V0).ShouldBe(["..###..."]);
    }

    [Fact]
    public void VerticalModes_TrackTheRowAbove()
    {
        // Row 0 spells out "..###..."; row 1 moves the left edge one pixel left
        // (VL1 against b1 = 2) and leaves the right edge where it is (V0 against
        // b1 = 5), then closes at the margin.
        Decode(8, 2,
            Horizontal, White2, Black3, V0,
            Vl1, V0, V0).ShouldBe(
        [
            "..###...",
            ".####...",
        ]);
    }

    [Fact]
    public void VerticalMode_ReachesThreePixelsEitherWay()
    {
        // Row 1 extends the black run from column 5 to the margin — b1 = 5, so
        // +3. Row 2 pulls it back to 5 again, which from b1 = 8 is -3.
        Decode(8, 3,
            Horizontal, White0, Black5, V0,
            V0, Vr3,
            V0, Vl3, V0).ShouldBe(
        [
            "#####...",
            "########",
            "#####...",
        ]);
    }

    [Fact]
    public void VerticalMode_ReachesTwoPixelsEitherWay()
    {
        // Row 1 widens the run by two pixels at each end; row 2 narrows it back.
        Decode(8, 3,
            Horizontal, White3, Black3, V0,
            Vl2, Vr2,
            Vr2, Vl2, V0).ShouldBe(
        [
            "...###..",
            ".#######",
            "...###..",
        ]);
    }

    [Fact]
    public void PassMode_SkipsAReferenceRunEntirely()
    {
        // Row 1 is blank while row 0 has a black run from 2 to 6. Pass says "the
        // white I am in continues past b2", moving a0 to 6 without producing a
        // changing element; V0 then closes the row.
        Decode(8, 2,
            Horizontal, White2, Black4, V0,
            Pass, V0).ShouldBe(
        [
            "..####..",
            "........",
        ]);
    }

    [Fact]
    public void PassMode_LeavesTheColourUnchanged()
    {
        // The same skip, but starting inside a black run: row 1 is solid black,
        // so after V0 puts a0 at 0 the pass carries the *black* past row 0's
        // second changing element rather than ending it.
        Decode(8, 2,
            Horizontal, White2, Black4, V0,
            Vl2, Pass).ShouldBe(
        [
            "..####..",
            "########",
        ]);
    }

    [Fact]
    public void FirstRunOfALine_MayBeZeroLength()
    {
        // a0 starts at -1 rather than 0 precisely so a line can begin black: the
        // white run is coded as 0 and the first black pixel is column 0.
        Decode(4, 1, Horizontal, White0, Black1, V0).ShouldBe(["#..."]);
    }

    // ---- refusals ----------------------------------------------------------------

    [Fact]
    public void TruncatedData_Fails()
    {
        // Past the end every bit reads as 0, which is the EOL prefix and not a
        // mode — so a short stream fails instead of inventing white rows.
        Should.Throw<InvalidDataException>(() => Decode(8, 4, Horizontal, White0, Black4, V0))
            .Message.ShouldContain("MMR");
    }

    [Fact]
    public void EmptyData_Fails() =>
        Should.Throw<InvalidDataException>(() => MmrDecoder.Decode([], 8, 2, Jbig2PixelBudget.Unmetered()));

    [Fact]
    public void ExtensionCode_IsRefusedByName()
    {
        // T.6's uncompressed mode arrives through this code. Refusing it loudly
        // is the point: the alternative is a page that decodes to the wrong thing.
        Should.Throw<NotSupportedException>(() => Decode(8, 1, Extension))
            .Message.ShouldContain("uncompressed");
    }

    [Fact]
    public void UnknownRunCode_Fails()
    {
        // Horizontal mode, then twelve zero bits, which no white code starts with.
        Should.Throw<InvalidDataException>(() => Decode(8, 1, Horizontal, "000000000000"))
            .Message.ShouldContain("white");
    }

    [Fact]
    public void VerticalModePastTheMargin_Fails()
    {
        // b1 is the right margin on an empty reference line, so VR1 would place a
        // changing element outside the row.
        Should.Throw<InvalidDataException>(() => Decode(8, 1, Vr1))
            .Message.ShouldContain("outside");
    }

    // ---- the code tables themselves ----------------------------------------------

    /// <summary>
    /// Both colours must carry every terminating run 0-63 and every makeup run
    /// 64-1728, and the extended makeup codes must cover 1792-2560. A missing
    /// entry would show up in the oracle only for images that happen to contain
    /// that exact run length — which is how a mislabelled entry survived the
    /// first version of this suite.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RunTables_CoverEveryLengthTheSpecDefines(int colour)
    {
        var runs = MmrCodes.TableFor(colour).Select(e => e.Run).ToHashSet();

        for (var run = 0; run <= 63; run++)
            runs.ShouldContain(run, $"terminating run {run} is missing");

        for (var run = 64; run <= 1728; run += 64)
            runs.ShouldContain(run, $"makeup run {run} is missing");

        runs.Count.ShouldBe(64 + 27, "the table should hold exactly the runs T.4 defines, with no duplicates");
    }

    [Fact]
    public void ExtendedMakeupTable_Covers1792To2560()
    {
        var runs = MmrCodes.ExtendedMakeupTable.Select(e => e.Run).ToHashSet();

        for (var run = 1792; run <= MmrCodes.MaxRun; run += 64)
            runs.ShouldContain(run, $"extended makeup run {run} is missing");

        runs.Count.ShouldBe(13);
    }

    /// <summary>
    /// Every code must decode back to its own run when it is the next thing in
    /// the stream. This is self-referential by construction — it cannot tell a
    /// mislabelled entry from a correct one — but it does prove the flat lookup
    /// tables were expanded without collisions or off-by-one shifts, which the
    /// oracle would report only as a mystifying pixel difference.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EveryCode_ResolvesToItsOwnRunAndLength(int colour)
    {
        var bits = colour == 0 ? MmrCodes.WhiteBits : MmrCodes.BlackBits;

        foreach (var (pattern, run) in MmrCodes.TableFor(colour).Concat(MmrCodes.ExtendedMakeupTable))
        {
            // Left-align the code and fill the rest with alternating bits, so a
            // lookup that accidentally depended on the trailing bits would move.
            var padded = pattern.PadRight(bits, '0');
            var alternating = pattern + string.Concat(Enumerable.Repeat("10", bits))[..(bits - pattern.Length)];

            foreach (var probe in (string[])[padded, alternating])
            {
                var decoded = MmrCodes.LookupRun(colour, Convert.ToInt32(probe, 2));
                decoded.Length.ShouldBe(pattern.Length, $"'{pattern}' (run {run}) resolved to the wrong length");
                decoded.Value.ShouldBe(run, $"'{pattern}' resolved to the wrong run");
            }
        }
    }

    // ---- the segment layer -------------------------------------------------------

    /// <summary>
    /// T.88 §7.4.6.2 requires TPGDON to be 0 when MMR is 1, but the bit means
    /// nothing to T.6 coding, so a stream that sets it anyway still decodes to
    /// the right pixels. Ignoring it beats rejecting a page over a flag that
    /// changes no bytes — the loud-refusal rule is for streams this decoder would
    /// otherwise get <em>wrong</em>.
    /// </summary>
    [Fact]
    public void MmrRegion_WithTpgdonSetAnyway_StillDecodes()
    {
        const int width = 32, height = 12;
        var expected = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                expected[y * width + x] = (x / 4 + y / 3) % 2 == 0 ? (byte)1 : (byte)0;

        var coded = Group4Tiff.Encode(expected, width, height).Coded;
        var segment = Jbig2StreamBuilder.MmrGenericRegionSegment(width, height, coded);

        // Flags byte sits right after the 17-byte region info field.
        segment[17].ShouldBe((byte)0x01);
        segment[17] |= 0x08;

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(width, height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1, segment));

        Jbig2Decoder.Decode(stream, width, height).Bits.ToArray().ShouldBe(expected);
    }

    // ---- helpers -----------------------------------------------------------------

    /// <summary>
    /// Packs a sequence of code bit-strings MSB-first and decodes the result.
    /// Returns the picture as ASCII rows so a failure reads as a picture.
    /// </summary>
    private static string[] Decode(int width, int height, params string[] codes)
    {
        var bits = string.Concat(codes);
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i] == '1')
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));

        var bitmap = MmrDecoder.Decode(bytes, width, height, Jbig2PixelBudget.Unmetered());
        return Jbig2StreamBuilder.ToRows(width, height, bitmap.Data);
    }
}
