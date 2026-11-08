using System.Buffers.Binary;
using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The JBIG2 features added for issue #1, each in a hand-assembled stream checked
/// against jbig2dec: HENABLESKIP, GRAYMAX above 255, an MMR pattern dictionary,
/// generic regions of unknown data length (§7.2.7), Huffman-coded symbol
/// dictionaries and text regions with standard and custom tables, and multi-page
/// files. The issue's own files are not in the repository (their licence is
/// unknown); <c>Issue1Jbig2CorpusTests</c> runs them when they are on disk.
/// <para>
/// The streams' coded bytes come from encoders written here, so a round trip
/// alone would only prove that encoder and decoder share a reading. jbig2dec is
/// what makes them checks: every test decodes the same bytes with it too.
/// </para>
/// </summary>
public sealed class Jbig2ExtendedOracleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- halftone ------------------------------------------------------------------

    /// <summary>
    /// HENABLESKIP with a grid that hangs off the region on each side, so cells are
    /// skipped on the left, top, right and bottom, plus a sheared lattice.
    /// </summary>
    [Theory]
    [InlineData(-6 * 256, -5 * 256, 1024, 0)]
    [InlineData(-2 * 256, 0, 1024, 0)]
    [InlineData(0, -9 * 256, 1024, 0)]
    [InlineData(-3 * 256, -3 * 256, 1024, 256)]
    [InlineData(4 * 256, 2 * 256, 1280, 512)]
    public void HalftoneWithSkip_AgreesWithJbig2dec(int gridX, int gridY, int vectorX, int vectorY)
    {
        Jbig2Oracle.RequireOrSkip();

        const int levels = 4, gridWidth = 9, gridHeight = 7, width = 22, height = 15;
        var patterns = Jbig2HalftoneTests.Patterns(levels);
        var grey = Ramp(gridWidth * gridHeight, levels);

        var file = HalftoneFile(width, height, patterns, Jbig2HalftoneTests.PatternDictionarySegment(patterns),
            Jbig2HalftoneTests.HalftoneRegionSegment(width, height, grey, gridWidth, gridHeight, levels, patterns,
                gridX, gridY, vectorX, vectorY, enableSkip: true));

        var expected = Stamp(width, height, patterns, grey, gridWidth, gridHeight, gridX, gridY, vectorX, vectorY);
        ShouldAgree(file, expected);
    }

    /// <summary>GRAYMAX 299: more patterns than a byte can count, nine bitplanes.</summary>
    [Fact]
    public void GrayMaxAbove255_AgreesWithJbig2dec()
    {
        Jbig2Oracle.RequireOrSkip();

        const int levels = 300, gridWidth = 6, gridHeight = 5;
        var patterns = Jbig2HalftoneTests.Patterns(levels, 18, 17);
        var grey = new int[gridWidth * gridHeight];
        for (var i = 0; i < grey.Length; i++) grey[i] = i * 37 % levels;

        var width = gridWidth * 18;
        var height = gridHeight * 17;
        var file = HalftoneFile(width, height, patterns, Jbig2HalftoneTests.PatternDictionarySegment(patterns),
            Jbig2HalftoneTests.HalftoneRegionSegment(width, height, grey, gridWidth, gridHeight, levels, patterns));

        ShouldAgree(file, Stamp(width, height, patterns, grey, gridWidth, gridHeight, 0, 0, 18 * 256, 0));
    }

    /// <summary>
    /// HDMMR = 1: the collective bitmap is Group 4, and the bytes come from libtiff,
    /// not from anything in this repository.
    /// </summary>
    [Fact]
    public void MmrPatternDictionary_AgreesWithJbig2dec()
    {
        Jbig2Oracle.RequireOrSkip();

        const int levels = 8, gridWidth = 6, gridHeight = 5;
        var patterns = Jbig2HalftoneTests.Patterns(levels);
        var collective = new Jbig2Bitmap(levels * 4, 4);
        for (var i = 0; i < levels; i++) collective.Combine(patterns[i], i * 4, 0, CombinationOperator.Replace);

        var dictionary = new List<byte> { 0x01, 4, 4 };   // HDMMR = 1, HDPW, HDPH
        WriteUInt32(dictionary, levels - 1);
        dictionary.AddRange(Group4Tiff.Encode(collective.Data, collective.Width, collective.Height).Coded);

        var grey = Ramp(gridWidth * gridHeight, levels);
        var file = HalftoneFile(gridWidth * 4, gridHeight * 4, patterns, [.. dictionary],
            Jbig2HalftoneTests.HalftoneRegionSegment(gridWidth * 4, gridHeight * 4, grey, gridWidth, gridHeight, levels, patterns));

        ShouldAgree(file, Stamp(gridWidth * 4, gridHeight * 4, patterns, grey, gridWidth, gridHeight, 0, 0, 1024, 0));
    }

    // ---- unknown data length -------------------------------------------------------

    /// <summary>
    /// An arithmetic generic region with data length 0xFFFFFFFF, ended by FF AC and a
    /// row count smaller than the declared height (§7.4.6.4), in an embedded stream
    /// as PDF carries them.
    /// </summary>
    [Fact]
    public void UnknownLengthArithmeticRegion_AgreesWithJbig2dec()
    {
        Jbig2Oracle.RequireOrSkip();

        const int width = 37, height = 29, rows = 21;
        var page = Pattern(width, rows);
        var at = GenericRegionDecoder.NominalAt(0).ToArray();
        var data = new List<byte>(RegionInfo(width, height));
        data.Add(0x00);   // MMR = 0, GBTEMPLATE 0
        data.AddRange(at.Select(a => (byte)a));
        // The MQ flush (T.88 E.3.2) already ends on the FF AC marker, which is
        // exactly the end sequence §7.2.7 asks for; the row count follows it.
        var coded = Jbig2StreamBuilder.EncodeGenericRegion(page, 0, false, at);
        coded[^2..].ShouldBe(new byte[] { 0xFF, 0xAC });
        data.AddRange(coded);
        WriteUInt32(data, rows);

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(width, height)),
            UnknownLength(Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1, [.. data])));

        var expected = new Jbig2Bitmap(width, height);
        expected.Combine(page, 0, 0, CombinationOperator.Or);

        Jbig2Oracle.Decode(stream, embedded: true).Bits.ShouldBe(expected.Data);
        Jbig2Decoder.Decode(stream, width, height, Ct).Bits.ToArray().ShouldBe(expected.Data);
    }

    /// <summary>The same with MMR (libtiff's bytes) and the 00 00 end sequence.</summary>
    [Fact]
    public void UnknownLengthMmrRegion_AgreesWithJbig2dec()
    {
        Jbig2Oracle.RequireOrSkip();

        const int width = 64, height = 40;
        var page = Pattern(width, height);
        var coded = Group4Tiff.Encode(page.Data, width, height).Coded;

        // 00 00 cannot occur inside T.6 codes, but a trailing zero pad byte would run
        // into the end sequence and make it ambiguous; libtiff ends on EOFB, so check.
        coded[^1].ShouldNotBe((byte)0);

        var data = new List<byte>(RegionInfo(width, height));
        data.Add(0x01);   // MMR = 1
        data.AddRange(coded);
        data.AddRange([0x00, 0x00]);
        WriteUInt32(data, height);

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(width, height)),
            UnknownLength(Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1, [.. data])));

        Jbig2Oracle.Decode(stream, embedded: true).Bits.ShouldBe(page.Data);
        Jbig2Decoder.Decode(stream, width, height, Ct).Bits.ToArray().ShouldBe(page.Data);
    }

    [Fact]
    public void UnknownLength_WithoutEndSequence_IsInvalidData()
    {
        var data = new List<byte>(RegionInfo(16, 16)) { 0x00, 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE, 0x12, 0x34 };
        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(16, 16)),
            UnknownLength(Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1, [.. data])));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.Decode(stream, 16, 16, Ct))
            .Message.ShouldContain("end sequence");
    }

    [Fact]
    public void UnknownLength_InARandomAccessFile_IsInvalidData()
    {
        var region = Jbig2StreamBuilder.Split(1, SegmentType.ImmediateGenericRegion, 1, [.. RegionInfo(8, 8), 0x01, 0, 0, 0, 0, 0, 8]);
        var header = region.Header.ToArray();
        header.AsSpan(header.Length - 4).Fill(0xFF);
        var file = Jbig2StreamBuilder.RandomAccessFile(
            Jbig2StreamBuilder.Split(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(8, 8)),
            (header, region.Data));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file, Ct));
    }

    // ---- Huffman -------------------------------------------------------------------

    public static TheoryData<string> HuffmanVariants => new() { "standard", "custom-dh", "mmr-collective" };

    /// <summary>
    /// A Huffman-coded symbol dictionary (SDHUFF = 1) and a Huffman-coded text region
    /// (SBHUFF = 1) that stamps its symbols, against jbig2dec: standard tables
    /// throughout, a custom table segment for SDHUFFDH, and MMR collective bitmaps
    /// (libtiff's bytes) in place of uncompressed ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(HuffmanVariants))]
    public void HuffmanDictionaryAndTextRegion_AgreeWithJbig2dec(string variant)
    {
        Jbig2Oracle.RequireOrSkip();

        var (file, expected) = HuffmanFile(variant);
        ShouldAgree(file, expected);
    }

    // ---- pages ---------------------------------------------------------------------

    /// <summary>
    /// A two-page file whose pages differ in size, with a page-0 dictionary-free
    /// generic region on each: every page decodes to its own raster, and page 1 is
    /// still what jbig2dec writes first.
    /// </summary>
    [Fact]
    public void MultiPageFile_DecodesEveryPage()
    {
        var first = Pattern(30, 20);
        var second = Pattern(41, 13, seed: 5);

        var file = MultiPageFile(first, second);

        Jbig2Decoder.TryReadFileInfo(file, out var w, out var h, out var pages).ShouldBeTrue();
        (w, h, pages).ShouldBe((30, 20, 2));

        Jbig2Decoder.DecodeFile(file, 1, Ct).Bits.ToArray().ShouldBe(first.Data);
        var page2 = Jbig2Decoder.DecodeFile(file, 2, Ct);
        (page2.Width, page2.Height).ShouldBe((41, 13));
        page2.Bits.ToArray().ShouldBe(second.Data);

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.DecodeFile(file, 3, Ct));
        Should.Throw<ArgumentOutOfRangeException>(() => Jbig2Decoder.DecodeFile(file, 0, Ct));

        if (Jbig2Oracle.IsAvailable) Jbig2Oracle.Decode(file).Bits.ShouldBe(first.Data);
    }

    // ---- builders ------------------------------------------------------------------

    private static void ShouldAgree(byte[] file, Jbig2Bitmap expected)
    {
        var oracle = Jbig2Oracle.Decode(file);
        (oracle.Width, oracle.Height).ShouldBe((expected.Width, expected.Height));
        oracle.Bits.ShouldBe(expected.Data, "jbig2dec");
        Jbig2Decoder.DecodeFile(file, Ct).Bits.ToArray().ShouldBe(expected.Data, "Sdcb.SimdCodecs.Jbig2");
    }

    private static int[] Ramp(int count, int levels)
    {
        var grey = new int[count];
        for (var i = 0; i < count; i++) grey[i] = (i * 7 + i / 5) % levels;
        return grey;
    }

    private static Jbig2Bitmap Stamp(
        int width, int height, Jbig2Bitmap[] patterns, int[] grey, int gridWidth, int gridHeight,
        int gridX, int gridY, int vectorX, int vectorY)
    {
        var expected = new Jbig2Bitmap(width, height);
        for (var m = 0; m < gridHeight; m++)
            for (var n = 0; n < gridWidth; n++)
                expected.Combine(patterns[grey[m * gridWidth + n]],
                    (gridX + m * vectorY + n * vectorX) >> 8, (gridY + m * vectorX - n * vectorY) >> 8,
                    CombinationOperator.Or);
        return expected;
    }

    private static byte[] HalftoneFile(int width, int height, Jbig2Bitmap[] patterns, byte[] dictionary, byte[] region) =>
        Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(width, height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.PatternDictionary, 1, dictionary),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateHalftoneRegion, 1, region, referredTo: [1]),
            Jbig2StreamBuilder.Segment(3, SegmentType.EndOfPage, 1, []));

    /// <summary>A deterministic bitmap with runs, edges and isolated pixels.</summary>
    internal static Jbig2Bitmap Pattern(int width, int height, int seed = 1)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.Data[y * width + x] = (byte)((((x + seed) / 3 + y / 2) % 4 == 0 || (x * 7 + y * 13 + seed) % 23 == 0) ? 1 : 0);
        return bitmap;
    }

    private static List<byte> RegionInfo(int width, int height)
    {
        var body = new List<byte>();
        WriteUInt32(body, (uint)width);
        WriteUInt32(body, (uint)height);
        WriteUInt32(body, 0);
        WriteUInt32(body, 0);
        body.Add(0);   // OR
        return body;
    }

    /// <summary>Rewrites a built segment's data length field to 0xFFFFFFFF.</summary>
    private static byte[] UnknownLength(byte[] segment)
    {
        // Short-form header with no referred-to segments and a one-byte page:
        // number(4) flags(1) count(1) page(1) length(4).
        segment.AsSpan(7, 4).Fill(0xFF);
        return segment;
    }

    /// <summary>A sequential file with one page per bitmap, page <c>n</c> holding an immediate generic region of
    /// bitmap <c>n - 1</c> and its own end-of-page segment.</summary>
    internal static byte[] MultiPageFile(params Jbig2Bitmap[] pages)
    {
        var file = new List<byte>(Jbig2Decoder.FileSignature.ToArray()) { 0x01 };
        WriteUInt32(file, (uint)pages.Length);
        uint number = 0;
        for (var i = 0; i < pages.Length; i++)
        {
            var page = (uint)(i + 1);
            file.AddRange(Jbig2StreamBuilder.Segment(number++, SegmentType.PageInformation, page,
                Jbig2StreamBuilder.PageInformation(pages[i].Width, pages[i].Height)));
            file.AddRange(Jbig2StreamBuilder.Segment(number++, SegmentType.ImmediateGenericRegion, page,
                Jbig2StreamBuilder.GenericRegionSegment(pages[i])));
            file.AddRange(Jbig2StreamBuilder.Segment(number++, SegmentType.EndOfPage, page, []));
        }
        file.AddRange(Jbig2StreamBuilder.Segment(number, SegmentType.EndOfFile, 0, []));
        return [.. file];
    }

    /// <summary>
    /// Builds the Huffman test file: a page, optionally a custom table segment, a
    /// Huffman symbol dictionary of glyphs in three height classes, and a Huffman text
    /// region placing them on two strips.
    /// </summary>
    private static (byte[] File, Jbig2Bitmap Expected) HuffmanFile(string variant)
    {
        // Glyphs: heights 5, 7, 7, 9, 9, 9 with varied widths, each a distinct shape.
        (int W, int H)[] sizes = [(4, 5), (6, 5), (3, 7), (8, 7), (5, 9), (7, 9), (11, 9)];
        var glyphs = new Jbig2Bitmap[sizes.Length];
        for (var i = 0; i < sizes.Length; i++)
        {
            var (w, h) = sizes[i];
            glyphs[i] = new Jbig2Bitmap(w, h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    glyphs[i].Data[y * w + x] = (byte)((x == 0 || y == h - 1 || (x + y + i) % 3 == 0) ? 1 : 0);
        }

        var customDh = variant == "custom-dh";
        var mmr = variant == "mmr-collective";

        // Custom table for SDHUFFDH: HTPS = 2, HTRS = 3, HTLOW = 1, HTHIGH = 17. Lines:
        // 1..8 (PREFLEN 1, RANGELEN 3), 9..16 (2, 3), lower (3), upper (3).
        HuffmanTable.Line[] customLines =
        [
            new(1, 3, 1), new(2, 3, 9),
            new(3, 32, 0, HuffmanTable.LineKind.Lower), new(3, 32, 17, HuffmanTable.LineKind.Upper),
        ];
        var table = new BitWriter();
        table.Write(1, 2); table.Write(3, 3);
        table.Write(2, 2); table.Write(3, 3);
        table.Write(3, 2); table.Write(3, 2);
        var tableSegment = new List<byte> { (byte)((1 << 1) | (2 << 4)) };   // HTOOB = 0, HTPS - 1 = 1, HTRS - 1 = 2
        WriteUInt32(tableSegment, 1);
        WriteUInt32(tableSegment, 17);
        tableSegment.AddRange(table.ToArray());

        // ---- symbol dictionary (§7.4.2), SDHUFF = 1, SDREFAGG = 0.
        var dh = customDh ? customLines : HuffmanTable.StandardLines(4);
        var dw = HuffmanTable.StandardLines(2);
        var b1 = HuffmanTable.StandardLines(1);

        var sd = new BitWriter();
        var height = 0;
        foreach (var group in glyphs.Select((g, i) => (g, i)).GroupBy(t => t.g.Height))
        {
            HuffmanEncode(sd, dh, group.Key - height);
            height = group.Key;

            var width = 0;
            var members = group.Select(t => t.g).ToArray();
            foreach (var glyph in members)
            {
                HuffmanEncode(sd, dw, glyph.Width - width);
                width = glyph.Width;
            }
            HuffmanEncodeOob(sd, dw);

            var total = members.Sum(g => g.Width);
            var collective = new Jbig2Bitmap(total, height);
            var x0 = 0;
            foreach (var glyph in members) { collective.Combine(glyph, x0, 0, CombinationOperator.Replace); x0 += glyph.Width; }

            if (mmr)
            {
                var coded = Group4Tiff.Encode(collective.Data, total, height).Coded;
                HuffmanEncode(sd, b1, coded.Length);
                sd.Align();
                sd.WriteBytes(coded);
            }
            else
            {
                HuffmanEncode(sd, b1, 0);
                sd.Align();
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < total; x++) sd.Write(collective.Data[y * total + x], 1);
                    sd.Align();
                }
            }
        }

        // Export everything: a zero-length skip run, then all of them.
        HuffmanEncode(sd, b1, 0);
        HuffmanEncode(sd, b1, glyphs.Length);

        var dictionary = new List<byte>();
        WriteUInt16(dictionary, (ushort)(0x0001 | (customDh ? 0x000C : 0)));   // SDHUFF, SDHUFFDH
        WriteUInt32(dictionary, (uint)glyphs.Length);
        WriteUInt32(dictionary, (uint)glyphs.Length);
        dictionary.AddRange(sd.ToArray());

        // ---- text region (§7.4.3), SBHUFF = 1, REFCORNER = TOPLEFT, one strip line each.
        const int pageWidth = 70, pageHeight = 40;
        (int Id, int S, int T)[][] strips =
        [
            [(0, 2, 3), (3, 8, 3), (1, 17, 3), (6, 24, 3), (2, 36, 3)],
            [(5, 5, 18), (4, 13, 18), (0, 20, 18), (6, 26, 18), (3, 40, 18), (1, 49, 18)],
        ];
        var instances = strips.Sum(s => s.Length);

        var codeLength = 3;   // seven symbols: every code length 3, so symbol i is code i
        var tr = new BitWriter();
        for (var r = 0; r < 35; r++) tr.Write(r == codeLength ? 1u : 0u, 4);   // run code lengths
        for (var i = 0; i < glyphs.Length; i++) tr.Write(0, 1);               // RUNCODE3 = "0", once per symbol
        tr.Align();

        var fs = HuffmanTable.StandardLines(6);
        var ds = HuffmanTable.StandardLines(8);
        var dt = HuffmanTable.StandardLines(11);

        HuffmanEncode(tr, dt, 1);   // initial STRIPT = -1
        var stript = -1;
        var firsts = 0;
        foreach (var strip in strips)
        {
            HuffmanEncode(tr, dt, strip[0].T - stript);
            stript = strip[0].T;
            HuffmanEncode(tr, fs, strip[0].S - firsts);
            firsts = strip[0].S;

            var curs = strip[0].S;
            for (var k = 0; k < strip.Length; k++)
            {
                tr.Write((uint)strip[k].Id, codeLength);
                curs += glyphs[strip[k].Id].Width - 1;
                if (k + 1 < strip.Length)
                {
                    HuffmanEncode(tr, ds, strip[k + 1].S - curs);
                    curs = strip[k + 1].S;
                }
                else
                {
                    HuffmanEncodeOob(tr, ds);
                }
            }
        }

        var text = new List<byte>(RegionInfo(pageWidth, pageHeight));
        WriteUInt16(text, 0x0001 | (1 << 4));   // SBHUFF, REFCORNER = TOPLEFT
        WriteUInt16(text, 0x0000);              // FS B.6, DS B.8, DT B.11
        WriteUInt32(text, (uint)instances);
        text.AddRange(tr.ToArray());

        var segments = new List<byte[]>
        {
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(pageWidth, pageHeight)),
        };
        if (customDh) segments.Add(Jbig2StreamBuilder.Segment(1, SegmentType.Tables, 1, [.. tableSegment]));
        segments.Add(Jbig2StreamBuilder.Segment(2, SegmentType.SymbolDictionary, 1, [.. dictionary],
            referredTo: customDh ? [1] : null));
        segments.Add(Jbig2StreamBuilder.Segment(3, SegmentType.ImmediateLosslessTextRegion, 1, [.. text], referredTo: [2]));
        segments.Add(Jbig2StreamBuilder.Segment(4, SegmentType.EndOfPage, 1, []));

        var expected = new Jbig2Bitmap(pageWidth, pageHeight);
        foreach (var strip in strips)
            foreach (var (id, s, t) in strip)
                expected.Combine(glyphs[id], s, t, CombinationOperator.Or);

        return (Jbig2StreamBuilder.SequentialFile([.. segments]), expected);
    }

    /// <summary>
    /// Writes <paramref name="value"/> with a table given as lines, assigning the
    /// prefix codes by T.88 B.3 here rather than through the decoder's table.
    /// </summary>
    private static void HuffmanEncode(BitWriter writer, HuffmanTable.Line[] lines, int value)
    {
        var codes = Codes(lines);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.PrefixLength == 0) continue;
            switch (line.Kind)
            {
                case HuffmanTable.LineKind.Normal when value >= line.RangeLow && value < line.RangeLow + (1L << line.RangeLength):
                    writer.Write(codes[i], line.PrefixLength);
                    writer.Write((uint)(value - line.RangeLow), line.RangeLength);
                    return;
                case HuffmanTable.LineKind.Lower when value <= line.RangeLow:
                    writer.Write(codes[i], line.PrefixLength);
                    writer.Write((uint)(line.RangeLow - value), 32);
                    return;
                case HuffmanTable.LineKind.Upper when value >= line.RangeLow:
                    writer.Write(codes[i], line.PrefixLength);
                    writer.Write((uint)(value - line.RangeLow), 32);
                    return;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "Not representable in this table.");
    }

    private static void HuffmanEncodeOob(BitWriter writer, HuffmanTable.Line[] lines)
    {
        var codes = Codes(lines);
        var i = Array.FindIndex(lines, l => l.Kind == HuffmanTable.LineKind.OutOfBand);
        writer.Write(codes[i], lines[i].PrefixLength);
    }

    private static uint[] Codes(HuffmanTable.Line[] lines)
    {
        var max = lines.Max(l => l.PrefixLength);
        var codes = new uint[lines.Length];
        uint first = 0;
        var previous = 0;
        for (var length = 1; length <= max; length++)
        {
            first = (uint)((first + previous) << 1);
            var current = first;
            previous = 0;
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].PrefixLength == length) { codes[i] = current++; previous++; }
        }
        return codes;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;

        public void Write(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (_bits % 8 == 0) _bytes.Add(0);
                if (((value >> i) & 1) != 0) _bytes[^1] |= (byte)(0x80 >> (_bits % 8));
                _bits++;
            }
        }

        public void Align() => _bits = (_bits + 7) & ~7;

        public void WriteBytes(byte[] data)
        {
            Align();
            _bytes.AddRange(data);
            _bits += data.Length * 8;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private static void WriteUInt16(List<byte> target, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        target.AddRange(buffer);
    }

    private static void WriteUInt32(List<byte> target, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        target.AddRange(buffer);
    }
}
