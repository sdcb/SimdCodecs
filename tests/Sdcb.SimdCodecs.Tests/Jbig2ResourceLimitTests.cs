using System.Buffers.Binary;
using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Resource limits for hostile streams — the decompression-bomb axis, which no
/// other test in this family covers because every other builder here encodes a
/// bitmap it actually holds in memory, and so can only ever declare dimensions it
/// told the truth about.
/// <para>
/// The shape these guard against was found by mutation-fuzzing the committed
/// fixtures: flip four bytes of a region's height field and a 127-byte file
/// declares half a billion pixels. Nothing downstream objected, because T.88's MQ
/// decoder reads every byte past the end of its data as <c>0xFF</c> (E.3.4) and so
/// keeps producing decisions forever — running out of input is not a backstop. A
/// hand-built worst case reached 2 GiB of allocation and 20+ seconds of CPU from
/// <b>82 input bytes</b>, an amplification of 26 million to one.
/// </para>
/// <para>
/// Compression ratio cannot be the bound: a blank 1200 dpi page with TPGDON codes
/// about one decision per row, so "pixels must be proportional to coded bytes"
/// would reject ordinary faxes. The ceilings are therefore absolute per bitmap
/// (<see cref="Jbig2Limits.MaxBitmapPixels"/>) plus a running total tied to the
/// page the caller asked for — see <see cref="Jbig2Limits.BudgetFor"/>.
/// </para>
/// </summary>
public sealed class Jbig2ResourceLimitTests
{
    // ---- the measured bomb ---------------------------------------------------

    [Fact]
    public void RegionFarLargerThanItsPage_IsRejectedRatherThanDecoded()
    {
        // 46340^2 = 2,147,395,600 pixels: the largest area that slipped under the
        // old "> 1L << 31" ceiling, and the exact case measured at 2 GiB.
        var file = FileWithRegion(pageWidth: 32, pageHeight: 16, regionWidth: 46340, regionHeight: 46340);

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file))
              .Message.ShouldContain("pixel");
    }

    [Fact]
    public void RegionInsideTheBitmapCeilingButOverTheBudget_IsRejected()
    {
        // 16384^2 is exactly MaxBitmapPixels, so the per-bitmap ceiling admits it
        // and only the running budget can refuse it. That makes this the test that
        // proves the budget is wired in and load-bearing, not merely present.
        ((long)16384 * 16384).ShouldBe(Jbig2Limits.MaxBitmapPixels);

        var file = FileWithRegion(pageWidth: 32, pageHeight: 16, regionWidth: 16384, regionHeight: 16384);

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file))
              .Message.ShouldContain("budget");
    }

    // ---- non-termination ----------------------------------------------------

    /// <summary>
    /// The trap the pixel budget cannot see: a height class that closes on its
    /// first IADW codes no symbol, so the outer loop of §6.5 makes no progress —
    /// and since the MQ decoder never runs dry (E.3.4: every byte past the end of
    /// the data reads as <c>0xFF</c>), a stream that keeps saying "empty class"
    /// spins for ever. Nothing is allocated on that path, so memory stays flat and
    /// no allocation ceiling fires. Deliberate here; found by fuzzing.
    /// </summary>
    [Fact]
    public void SymbolDictionaryWithAnEmptyHeightClass_IsRejected()
    {
        var mq = new Jbig2MqEncoder();
        var dh = new Jbig2SymbolBuilder.IntEncoder();
        var dw = new Jbig2SymbolBuilder.IntEncoder();

        dh.Encode(mq, 5);       // open a height class at height 5
        dw.EncodeOob(mq);       // and close it at once, coding no symbol at all

        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(32, 16)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1,
                SymbolDictionarySegment(mq, exported: 1, created: 1)));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file))
              .Message.ShouldContain("codes no symbols");
    }

    /// <summary>
    /// The fuzzer's actual witness — a 277-byte mutation of <c>sym.jb2</c> that
    /// span for ever with flat memory before the progress guard existed. Bounded by
    /// a timeout so that a regression fails this test instead of hanging the run.
    /// </summary>
    [Fact]
    public async Task NonTerminatingSymbolDictionaryWitness_Terminates()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "jbig2", "nonterminating-symbol-dict.jb2"),
            cancellation);

        // The exception is captured rather than left to fault the task, because
        // "did it finish at all" is the whole assertion here — a faulted task has
        // still terminated, which is the property under test.
        Exception? caught = null;
        var decode = Task.Run(
            () =>
            {
                try { Jbig2Decoder.DecodeFile(bytes); }
                catch (Exception e) { caught = e; }
            },
            cancellation);

        // WhenAny rather than a blocking Wait: the decode cannot be cancelled from
        // outside (it is a tight synchronous loop by nature), so the timeout has to
        // be a racing task rather than a token the decoder would have to observe.
        var first = await Task.WhenAny(decode, Task.Delay(TimeSpan.FromSeconds(30), cancellation));

        first.ShouldBe(decode, "decoding must terminate rather than spin on malformed input");
        caught.ShouldBeOfType<InvalidDataException>();
    }

    [Fact]
    public void SymbolDictionary_CannotDeclareAGiantGlyph()
    {
        // Symbol dictionaries never touch region info: §6.5 accumulates a symbol's
        // width and height from coded deltas with nothing but "> 0" on them, so
        // this path bypasses every region-level guard. One glyph, 40000 x 60000.
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(32, 16)),
            Jbig2StreamBuilder.Segment(1, SegmentType.SymbolDictionary, 1,
                GiantSymbolDictionarySegment(width: 40000, height: 60000)));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file));
    }

    /// <summary>
    /// The guarantee the real consumer rests on. In the PDF path the page comes from
    /// the image dictionary, i.e. from the caller, so the budget is scaled to what
    /// the <em>PDF</em> declares and no codestream can talk its way past it. That
    /// matters because the standalone-file path has no such anchor — there the page
    /// size is itself read from the stream's page-information segment, so a hostile
    /// <c>.jb2</c> can legitimately ask for a large page and get a large budget.
    /// </summary>
    [Fact]
    public void EmbeddedPath_BudgetsAgainstTheCallersPageNotTheCodestreams()
    {
        // Region info declaring 14418162 x 16 — 230 million pixels, and inside the
        // per-bitmap ceiling, so only a budget can refuse it.
        var segment = Jbig2StreamBuilder.Segment(
            1, SegmentType.ImmediateGenericRegion, 1, LyingRegionSegment(14418162, 16));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.Decode(segment, default, 32, 16))
              .Message.ShouldContain("budget");
    }

    // ---- the off-by-one at the old ceiling ----------------------------------

    /// <summary>
    /// The old guard was <c>(long)w * h &gt; 1L &lt;&lt; 31</c>, so an area of
    /// exactly 2^31 passed it and then overflowed the <c>checked</c> multiply that
    /// sizes the bitmap; an area just under 2^31 but over the CLR's maximum
    /// <c>byte[]</c> length (2,147,483,591) reached the allocation and threw
    /// <see cref="OutOfMemoryException"/>. Both are malformed input and must read
    /// as malformed input.
    /// </summary>
    [Theory]
    [InlineData(65536, 32768)]      // exactly 2^31 pixels
    [InlineData(8, 268435455)]      // 2,147,483,640 — under 2^31, over max byte[]
    [InlineData(46341, 46341)]      // just over the old ceiling; was always rejected
    public void PageAreaAtOrPastTheCeiling_ReadsAsMalformedData(int width, int height)
    {
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(width, height)));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file));
        Jbig2Decoder.TryReadFileInfo(file, out _, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(65536, 32768)]
    [InlineData(8, 268435455)]
    public void CallerSuppliedPageAtOrPastTheCeiling_IsAnArgumentError(int width, int height)
    {
        // The embedded (PDF) entry point takes its dimensions from the image
        // dictionary, which is no more trustworthy than the codestream — but it is
        // an argument, so it reports as one rather than as stream corruption.
        Should.Throw<ArgumentOutOfRangeException>(
            () => Jbig2Decoder.Decode(new byte[] { 0, 0, 0, 0 }, default, width, height));
    }

    // ---- the bool contract --------------------------------------------------

    /// <summary>
    /// A <c>bool</c>-returning <c>Try</c> method must answer <c>false</c> on
    /// hostile input, never throw: <c>SimdCodecs.TryDecode</c> delegates to these
    /// without a catch of its own, so anything escaping here escapes the facade and
    /// contradicts its documented "returns false when the payload is undecodable".
    /// </summary>
    [Theory]
    [InlineData(65536, 32768)]
    [InlineData(8, 268435455)]
    [InlineData(46341, 46341)]
    public void TryDecode_AnswersFalseInsteadOfThrowing(int width, int height)
    {
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(width, height)));

        Jbig2ImageDecoder.TryDecode(file, out var image).ShouldBeFalse();
        image.ShouldBeNull();
        Jbig2ImageDecoder.TryReadInfo(file, out _).ShouldBeFalse();
        Jbig2ImageDecoder.TryDecodeIntoRgba8(file, new byte[64]).ShouldBeFalse();

        // And through the facade, which is where a consumer actually meets it.
        SimdCodecs.TryDecode(file, out var viaFacade).ShouldBeFalse();
        viaFacade.ShouldBeNull();
    }

    [Fact]
    public void TryDecode_AnswersFalseForABombRatherThanThrowing()
    {
        var file = FileWithRegion(pageWidth: 32, pageHeight: 16, regionWidth: 46340, regionHeight: 46340);

        Jbig2ImageDecoder.TryDecode(file, out _).ShouldBeFalse();
        SimdCodecs.TryDecode(file, out _).ShouldBeFalse();
    }

    // ---- the budget's own arithmetic ----------------------------------------

    [Fact]
    public void Budget_AccumulatesAcrossChargesAndRefusesTheOneThatOverruns()
    {
        // Charged directly rather than through a decode: proving "many separately
        // plausible regions cannot outrun the total" end-to-end would mean actually
        // decoding tens of millions of pixels, which is the cost this guards.
        var budget = new Jbig2PixelBudget(1000);

        budget.Charge(20, 20);      // 400 spent
        budget.Charge(20, 20);      // 800 spent
        Should.Throw<InvalidDataException>(() => budget.Charge(20, 20));

        // The refused charge is not deducted, so a smaller one still fits.
        budget.Charge(10, 20);      // 1000 spent exactly
        Should.Throw<InvalidDataException>(() => budget.Charge(1, 1));
    }

    [Fact]
    public void Budget_ChargeCannotOverflowOnHostileDimensions() =>
        // (long) arithmetic inside Charge, not int: two values whose int product
        // wraps positive-small must still be refused.
        Should.Throw<InvalidDataException>(
            () => new Jbig2PixelBudget(1000).Charge(65536, 65536));

    // ---- the limits stay wide enough for real documents ---------------------

    /// <summary>
    /// The counterweight to everything above: a ceiling that rejects real scans is
    /// a bug too. These are the documents this codec exists to decode.
    /// </summary>
    [Theory]
    [InlineData(2480, 3508, "A4 at 300 dpi")]
    [InlineData(4960, 7016, "A4 at 600 dpi")]
    [InlineData(9921, 14031, "A4 at 1200 dpi")]
    public void RealisticScannedPages_AreWellInsideTheLimits(int width, int height, string what)
    {
        var pixels = (long)width * height;
        pixels.ShouldBeLessThan(Jbig2Limits.MaxBitmapPixels, $"{what} must fit one bitmap");

        // Room for the page itself plus a refinement pass over it, at minimum.
        Jbig2Limits.BudgetFor(width, height).ShouldBeGreaterThanOrEqualTo(pixels * 2, what);
    }

    [Fact]
    public void SmallPages_StillGetTheFloorBudget() =>
        // A 10x10 page must not be handed a 400-pixel allowance: a symbol
        // dictionary and its text region legitimately decode more than the page.
        Jbig2Limits.BudgetFor(10, 10).ShouldBe(Jbig2Limits.MinPixelBudget);

    /// <summary>
    /// Real jbig2enc output, as the belt-and-braces that none of the ceilings above
    /// have narrowed the codec's actual job. Standalone files only — <c>e*.jb2</c>
    /// are PDF-embedded streams with no file header.
    /// </summary>
    [Theory]
    [InlineData("s.jb2")]
    [InlineData("s_tpgd.jb2")]
    [InlineData("sym.jb2")]
    [InlineData("sym_tpgd.jb2")]
    public void CommittedEncoderFixtures_StillDecode(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jbig2", name));
        var image = Jbig2Decoder.DecodeFile(bytes);

        image.Width.ShouldBeGreaterThan(0);
        image.Height.ShouldBeGreaterThan(0);
    }

    // ---- builders that lie about their size ---------------------------------

    /// <summary>
    /// A file whose generic region segment <em>declares</em>
    /// <paramref name="regionWidth"/> x <paramref name="regionHeight"/> while
    /// carrying two bytes of coded data. This is the whole trick: no honest
    /// encoder can express it, so no other builder in this project can either.
    /// </summary>
    private static byte[] FileWithRegion(int pageWidth, int pageHeight, uint regionWidth, uint regionHeight) =>
        Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(pageWidth, pageHeight)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1,
                LyingRegionSegment(regionWidth, regionHeight)));

    /// <summary>
    /// The generic-region segment data part behind <see cref="FileWithRegion"/>:
    /// region info declaring whatever it likes, then two bytes of coded data.
    /// </summary>
    private static byte[] LyingRegionSegment(uint regionWidth, uint regionHeight)
    {
        var body = new List<byte>();
        WriteUInt32(body, regionWidth);
        WriteUInt32(body, regionHeight);
        WriteUInt32(body, 0);                       // X
        WriteUInt32(body, 0);                       // Y
        body.Add((byte)CombinationOperator.Or);
        body.Add(0);                                // flags: arithmetic, GBTEMPLATE 0, no TPGDON
        foreach (var v in GenericRegionDecoder.NominalAt(0)) body.Add((byte)v);
        body.AddRange([0x00, 0x00]);                // the MQ decoder invents 0xFF from here on
        return [.. body];
    }

    /// <summary>
    /// A symbol dictionary declaring one symbol of the given size — the height and
    /// width deltas are coded for real (via <see cref="Jbig2SymbolBuilder.IntEncoder"/>,
    /// the write side of Annex A.2) and the symbol's own bitmap data is simply
    /// absent, since a decoder must size the glyph before it can decode into it.
    /// </summary>
    private static byte[] GiantSymbolDictionarySegment(int width, int height)
    {
        var mq = new Jbig2MqEncoder();
        var dh = new Jbig2SymbolBuilder.IntEncoder();
        var dw = new Jbig2SymbolBuilder.IntEncoder();

        dh.Encode(mq, height);      // first height class, DH from a running height of 0
        dw.Encode(mq, width);       // first symbol in it, DW from a running width of 0

        return SymbolDictionarySegment(mq, exported: 1, created: 1);
    }

    /// <summary>
    /// Wraps already-arithmetic-coded bytes in a symbol dictionary segment data
    /// part (§7.4.3): arithmetic, no refinement, SDTEMPLATE 0, nominal AT pixels.
    /// </summary>
    private static byte[] SymbolDictionarySegment(Jbig2MqEncoder mq, uint exported, uint created)
    {
        var body = new List<byte>();
        body.AddRange([0x00, 0x00]);                // SDHUFF=0, SDREFAGG=0, SDTEMPLATE=0
        foreach (var v in GenericRegionDecoder.NominalAt(0)) body.Add((byte)v);
        WriteUInt32(body, exported);                // SDNUMEXSYMS
        WriteUInt32(body, created);                 // SDNUMNEWSYMS
        body.AddRange(mq.Flush());
        return [.. body];
    }

    [Fact]
    public void ReferredToCountPastTheEndOfTheStream_IsRejectedBeforeAllocating()
    {
        // Found by mutation fuzzing: the escape form of the referred-to count
        // (T.88 §7.2.4) declares 0x1FFFFFFF references in a 22-byte file, and the
        // header reader allocated a 2 GiB array for them before reading any.
        byte[] file =
        [
            0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A, // file signature
            0x01, 0x00, 0x00, 0x00, 0x01,                   // sequential, 1 page
            0x00, 0x00, 0x00, 0x00,                         // segment number 0
            0x30,                                           // page information
            0xFF, 0xFF, 0xFF, 0xFF,                         // long-form count 0x1FFFFFFF
        ];

        var before = GC.GetAllocatedBytesForCurrentThread();
        Jbig2Decoder.TryReadFileInfo(file, out _, out _).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file));
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(1 << 20);
    }

    private static void WriteUInt32(List<byte> target, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        target.AddRange(buffer);
    }
}
