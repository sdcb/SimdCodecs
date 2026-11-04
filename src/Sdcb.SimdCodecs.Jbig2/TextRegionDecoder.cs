using System;
using System.IO;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>Where a symbol's placement coordinate sits on the symbol (T.88 §6.4.5, REFCORNER).</summary>
internal enum ReferenceCorner
{
    BottomLeft = 0,
    TopLeft = 1,
    BottomRight = 2,
    TopRight = 3,
}

/// <summary>Everything §6.4 needs that is not the coded data itself.</summary>
/// <param name="Width">SBW, the region width.</param>
/// <param name="Height">SBH, the region height.</param>
/// <param name="Instances">SBNUMINSTANCES — how many symbol placements the region holds.</param>
/// <param name="Strips">SBSTRIPS, the vertical quantisation of the T coordinate.</param>
/// <param name="DefaultPixel">SBDEFPIXEL, the colour the region starts as.</param>
/// <param name="Combination">SBCOMBOP, how each symbol merges into the region.</param>
/// <param name="Corner">SBREFCORNER.</param>
/// <param name="Transposed">SBTRANSPOSED — strips run down the page rather than across.</param>
/// <param name="DsOffset">SBDSOFFSET, added to every inter-symbol gap.</param>
/// <param name="Refine">SBREFINE — instances may carry a per-placement refinement.</param>
/// <param name="RefinementTemplate">SBRTEMPLATE.</param>
internal readonly record struct TextRegionParameters(
    int Width,
    int Height,
    int Instances,
    int Strips,
    byte DefaultPixel,
    CombinationOperator Combination,
    ReferenceCorner Corner,
    bool Transposed,
    int DsOffset,
    bool Refine,
    int RefinementTemplate);

/// <summary>
/// Text region decoding — ITU-T T.88 §6.4. A text region is a <em>layout</em>:
/// it carries no pixels of its own, only instructions for stamping symbols from
/// a dictionary onto a blank region.
/// <para>
/// The layout is coded as horizontal strips. Each strip has a T coordinate
/// (vertical, quantised by SBSTRIPS) and then a run of symbols along S
/// (horizontal), each given as a delta from the last. That is the whole trick
/// behind JBIG2's compression of scanned text: a page of prose becomes a
/// dictionary of a few hundred glyph bitmaps plus a few thousand small integers.
/// </para>
/// <para>
/// Every number here comes from an <see cref="ArithIntDecoder"/> — one per field,
/// as T.88 names them (IADT, IAFS, IADS, IAIT, IARI, IARDW…), because each
/// field's values have their own distribution and sharing contexts between them
/// would cost compression. A Huffman-coded region (SBHUFF = 1) reads the same
/// fields from Huffman tables instead, in <see cref="DecodeHuffman"/>.
/// </para>
/// </summary>
internal static class TextRegionDecoder
{
    /// <summary>The per-field integer decoders a text region needs, kept together so a symbol dictionary can share them.</summary>
    internal sealed class Fields
    {
        public ArithIntDecoder Dt { get; } = new();
        public ArithIntDecoder Fs { get; } = new();
        public ArithIntDecoder Ds { get; } = new();
        public ArithIntDecoder It { get; } = new();
        public ArithIntDecoder Ri { get; } = new();
        public ArithIntDecoder Rdw { get; } = new();
        public ArithIntDecoder Rdh { get; } = new();
        public ArithIntDecoder Rdx { get; } = new();
        public ArithIntDecoder Rdy { get; } = new();
    }

    /// <summary>
    /// The number of bits a symbol ID occupies (T.88 §6.4.5): just enough to
    /// count the dictionary, and never zero — a one-symbol dictionary still codes
    /// one bit per instance.
    /// </summary>
    public static int SymbolCodeLength(int symbolCount)
    {
        var bits = 0;
        while (1 << bits < symbolCount) bits++;
        return Math.Max(bits, 1);
    }

    /// <summary>
    /// Decodes a text region into a fresh bitmap (T.88 §6.4.5).
    /// </summary>
    /// <param name="mq">The arithmetic decoder, positioned at the region's coded data.</param>
    /// <param name="p">The region parameters, read from the segment header.</param>
    /// <param name="symbols">SBSYMS, the dictionary this region draws from.</param>
    /// <param name="fields">Per-field integer decoders; a caller may reuse them across calls that share state.</param>
    /// <param name="idContexts">Adaptive contexts for the symbol ID tree (A.3).</param>
    /// <param name="refinementContexts">Adaptive contexts for per-instance refinement, when SBREFINE is set.</param>
    /// <param name="refinementAt">SBRAT, the refinement AT pixels.</param>
    /// <param name="budget">
    /// The decode's remaining pixel allowance, charged for the region canvas and
    /// again by each per-instance refinement.
    /// </param>
    public static Jbig2Bitmap Decode(
        ref MqDecoder mq,
        TextRegionParameters p,
        Jbig2Bitmap[] symbols,
        Fields fields,
        scoped Span<byte> idContexts,
        scoped Span<byte> refinementContexts,
        scoped ReadOnlySpan<sbyte> refinementAt,
        Jbig2PixelBudget budget)
    {
        budget.Charge(p.Width, p.Height);
        var region = new Jbig2Bitmap(p.Width, p.Height, p.DefaultPixel);
        var codeLength = SymbolCodeLength(symbols.Length);

        // §6.4.5 step 1: the first strip's T is coded as a negative offset, so
        // that a region whose content starts at the top costs nothing to say.
        var stript = -Require(fields.Dt.Decode(ref mq), "IADT") * p.Strips;
        var firsts = 0;
        var instances = 0;

        while (instances < p.Instances)
        {
            stript += Require(fields.Dt.Decode(ref mq), "IADT") * p.Strips;

            // The first symbol of a strip is placed relative to the previous
            // strip's first symbol, the rest relative to their left neighbour.
            firsts += Require(fields.Fs.Decode(ref mq), "IAFS");
            var curs = firsts;

            while (true)
            {
                if (instances >= p.Instances)
                    throw new InvalidDataException("JBIG2: text region codes more symbol instances than it declared.");

                // With one strip there is no room for a within-strip offset, so
                // T.88 skips coding it entirely rather than coding a zero.
                var curt = p.Strips == 1 ? 0 : Require(fields.It.Decode(ref mq), "IAIT");
                var t = stript + curt;

                var id = ArithIntDecoder.DecodeId(ref mq, idContexts, codeLength);
                if ((uint)id >= (uint)symbols.Length)
                    throw new InvalidDataException($"JBIG2: text region names symbol {id} of {symbols.Length}.");

                var symbol = symbols[id];
                if (p.Refine && fields.Ri.Decode(ref mq) is var ri and not 0)
                {
                    if (ri == ArithIntDecoder.OutOfBand)
                        throw new InvalidDataException("JBIG2: unexpected OOB in a text region refinement flag.");

                    symbol = RefineInstance(ref mq, p, fields, symbol, refinementContexts, refinementAt, budget);
                }

                curs = PlaceSymbol(region, symbol, p, curs, t);
                instances++;

                // OOB on IADS is how a strip says it has no more symbols.
                var ids = fields.Ds.Decode(ref mq);
                if (ids == ArithIntDecoder.OutOfBand) break;

                curs += ids + p.DsOffset;
            }
        }

        return region;
    }

    /// <summary>
    /// §6.4.11: one instance may carry its own refinement, correcting the
    /// dictionary symbol for this placement only. The size delta is split across
    /// both edges, which is why the reference offset picks up half of it.
    /// </summary>
    private static Jbig2Bitmap RefineInstance(
        ref MqDecoder mq,
        TextRegionParameters p,
        Fields fields,
        Jbig2Bitmap symbol,
        scoped Span<byte> refinementContexts,
        scoped ReadOnlySpan<sbyte> refinementAt,
        Jbig2PixelBudget budget)
    {
        var rdw = Require(fields.Rdw.Decode(ref mq), "IARDW");
        var rdh = Require(fields.Rdh.Decode(ref mq), "IARDH");
        var rdx = Require(fields.Rdx.Decode(ref mq), "IARDX");
        var rdy = Require(fields.Rdy.Decode(ref mq), "IARDY");

        var width = symbol.Width + rdw;
        var height = symbol.Height + rdh;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"JBIG2: refined text instance has non-positive size {width}x{height}.");

        return RefinementRegionDecoder.Decode(
            ref mq, refinementContexts, width, height, p.RefinementTemplate, symbol,
            FloorHalf(rdw) + rdx, FloorHalf(rdh) + rdy,
            typicalPrediction: false, refinementAt, budget);
    }

    /// <summary>
    /// Stamps one symbol and returns the advanced S coordinate (§6.4.5 step 3c).
    /// <para>
    /// The order matters and is easy to get subtly wrong: for a right-hand
    /// reference corner S advances <em>before</em> the symbol is placed, so that S
    /// names the symbol's right edge; for a left-hand corner it advances after.
    /// Either way the next symbol's gap is measured from the edge just passed.
    /// </para>
    /// </summary>
    private static int PlaceSymbol(Jbig2Bitmap region, Jbig2Bitmap symbol, TextRegionParameters p, int curs, int t)
    {
        var w = symbol.Width;
        var h = symbol.Height;

        // Transposed strips run down the page, so S measures vertically and the
        // symbol's height is what advances it.
        var extent = p.Transposed ? h : w;
        var advanceFirst = p.Transposed
            ? p.Corner is ReferenceCorner.BottomLeft or ReferenceCorner.BottomRight
            : p.Corner is ReferenceCorner.TopRight or ReferenceCorner.BottomRight;

        if (advanceFirst) curs += extent - 1;

        var s = curs;
        var (x, y) = p.Transposed
            ? (p.Corner is ReferenceCorner.TopRight or ReferenceCorner.BottomRight ? t - w + 1 : t,
               p.Corner is ReferenceCorner.BottomLeft or ReferenceCorner.BottomRight ? s - h + 1 : s)
            : (p.Corner is ReferenceCorner.TopRight or ReferenceCorner.BottomRight ? s - w + 1 : s,
               p.Corner is ReferenceCorner.BottomLeft or ReferenceCorner.BottomRight ? t - h + 1 : t);

        region.Combine(symbol, x, y, p.Combination);

        return advanceFirst ? curs : curs + extent - 1;
    }

    /// <summary>The Huffman tables a Huffman-coded text region reads its fields with (§7.4.3.1.2).</summary>
    internal sealed record HuffmanTables(
        HuffmanTable Fs, HuffmanTable Ds, HuffmanTable Dt,
        HuffmanTable Rdw, HuffmanTable Rdh, HuffmanTable Rdx, HuffmanTable Rdy, HuffmanTable RefinementSize)
    {
        /// <summary>
        /// The tables T.88 Table 17 fixes for a text region decoded inside a
        /// Huffman-coded symbol dictionary's aggregate symbol (§6.5.8.2.1).
        /// </summary>
        public static HuffmanTables SymbolDictionaryAggregate => new(
            HuffmanTable.Get(6), HuffmanTable.Get(8), HuffmanTable.Get(11),
            HuffmanTable.Get(15), HuffmanTable.Get(15), HuffmanTable.Get(15), HuffmanTable.Get(15),
            HuffmanTable.Get(1));
    }

    /// <summary>
    /// Reads the symbol ID Huffman table that opens a Huffman-coded text region's data
    /// (§7.4.3.1.7): 35 four-bit lengths for the run codes, then one code length per
    /// symbol coded with those run codes, then padding to the next byte.
    /// </summary>
    public static HuffmanTable ReadSymbolCodeTable(ref Jbig2BitReader reader, int symbolCount)
    {
        Span<int> runLengths = stackalloc int[35];
        for (var i = 0; i < runLengths.Length; i++) runLengths[i] = (int)reader.ReadBits(4);
        var runCodes = HuffmanTable.FromCodeLengths(runLengths);

        var lengths = new int[symbolCount];
        var count = 0;
        while (count < symbolCount)
        {
            var code = runCodes.Decode(ref reader);
            int repeat, value;
            switch (code)
            {
                case >= 0 and < 32:
                    lengths[count++] = code;
                    continue;
                case 32:
                    // RUNCODE32: the previous length again, 3 to 6 times.
                    if (count == 0)
                        throw new InvalidDataException("JBIG2: symbol ID table repeats a length before any is given.");
                    value = lengths[count - 1];
                    repeat = 3 + (int)reader.ReadBits(2);
                    break;
                case 33:
                    value = 0;
                    repeat = 3 + (int)reader.ReadBits(3);
                    break;
                case 34:
                    value = 0;
                    repeat = 11 + (int)reader.ReadBits(7);
                    break;
                default:
                    throw new InvalidDataException($"JBIG2: symbol ID table has run code {code}.");
            }

            if (count + repeat > symbolCount)
                throw new InvalidDataException("JBIG2: symbol ID table runs past the symbol count.");
            lengths.AsSpan(count, repeat).Fill(value);
            count += repeat;
        }

        reader.AlignToByte();
        return HuffmanTable.FromCodeLengths(lengths);
    }

    /// <summary>
    /// Decodes a Huffman-coded text region (SBHUFF = 1, §6.4.5 with the Huffman
    /// branches of §6.4.6 to §6.4.11).
    /// </summary>
    /// <param name="reader">Positioned at the first field after the symbol ID table.</param>
    /// <param name="p">The region parameters.</param>
    /// <param name="symbols">SBSYMS.</param>
    /// <param name="tables">The field tables.</param>
    /// <param name="symbolCodes">
    /// SBSYMCODES, or null for the uniform <paramref name="codeLength"/>-bit IDs a symbol
    /// dictionary's aggregate region uses.
    /// </param>
    /// <param name="codeLength">The width of a uniform ID; unused when <paramref name="symbolCodes"/> is given.</param>
    /// <param name="refinementContexts">Contexts for per-instance refinement.</param>
    /// <param name="refinementAt">SBRAT.</param>
    /// <param name="budget">The decode's remaining pixel allowance.</param>
    public static Jbig2Bitmap DecodeHuffman(
        ref Jbig2BitReader reader,
        TextRegionParameters p,
        Jbig2Bitmap[] symbols,
        HuffmanTables tables,
        HuffmanTable? symbolCodes,
        int codeLength,
        Span<byte> refinementContexts,
        scoped ReadOnlySpan<sbyte> refinementAt,
        Jbig2PixelBudget budget)
    {
        budget.Charge(p.Width, p.Height);
        var region = new Jbig2Bitmap(p.Width, p.Height, p.DefaultPixel);
        var logStrips = System.Numerics.BitOperations.Log2((uint)p.Strips);

        var stript = -(long)RequireHuffman(tables.Dt.Decode(ref reader), "DT") * p.Strips;
        long firsts = 0;
        var instances = 0;

        while (instances < p.Instances)
        {
            stript += (long)RequireHuffman(tables.Dt.Decode(ref reader), "DT") * p.Strips;
            firsts += RequireHuffman(tables.Fs.Decode(ref reader), "FS");
            var curs = firsts;

            while (true)
            {
                if (instances >= p.Instances)
                    throw new InvalidDataException("JBIG2: text region codes more symbol instances than it declared.");

                // §6.4.9: with SBHUFF the within-strip T offset is LOG2SBSTRIPS raw bits.
                var curt = (long)reader.ReadBits(logStrips);
                var t = ClampCoordinate(stript + curt);

                var id = symbolCodes is null ? (int)reader.ReadBits(codeLength) : symbolCodes.Decode(ref reader);
                if ((uint)id >= (uint)symbols.Length)
                    throw new InvalidDataException($"JBIG2: text region names symbol {id} of {symbols.Length}.");

                var symbol = symbols[id];
                if (p.Refine && reader.ReadBit() != 0)
                {
                    var rdw = RequireHuffman(tables.Rdw.Decode(ref reader), "RDW");
                    var rdh = RequireHuffman(tables.Rdh.Decode(ref reader), "RDH");
                    var rdx = RequireHuffman(tables.Rdx.Decode(ref reader), "RDX");
                    var rdy = RequireHuffman(tables.Rdy.Decode(ref reader), "RDY");
                    var size = RequireHuffman(tables.RefinementSize.Decode(ref reader), "RSIZE");

                    var width = (long)symbol.Width + rdw;
                    var height = (long)symbol.Height + rdh;
                    if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue)
                        throw new InvalidDataException($"JBIG2: refined text instance has size {width}x{height}.");

                    symbol = DecodeHuffmanRefinement(
                        ref reader, size, refinementContexts, (int)width, (int)height, p.RefinementTemplate,
                        symbol, FloorHalf(rdw) + rdx, FloorHalf(rdh) + rdy, refinementAt, budget);
                }

                curs = PlaceSymbol(region, symbol, p, ClampCoordinate(curs), t);
                instances++;

                var ds = tables.Ds.Decode(ref reader);
                if (ds == HuffmanTable.OutOfBand) break;

                curs += (long)ds + p.DsOffset;
            }
        }

        return region;
    }

    /// <summary>
    /// A refinement inside Huffman-coded data (§6.4.11.1 and §6.5.8.2.2): it starts
    /// on the next byte boundary, is arithmetically coded, and occupies exactly
    /// <paramref name="size"/> bytes, after which the Huffman fields resume.
    /// </summary>
    public static Jbig2Bitmap DecodeHuffmanRefinement(
        ref Jbig2BitReader reader,
        int size,
        Span<byte> contexts,
        int width,
        int height,
        int template,
        Jbig2Bitmap reference,
        int dx,
        int dy,
        scoped ReadOnlySpan<sbyte> at,
        Jbig2PixelBudget budget)
    {
        if (size < 0) throw new InvalidDataException($"JBIG2: refinement data size {size} is negative.");

        reader.AlignToByte();
        var start = reader.BytePosition;
        if ((long)start + size > reader.Data.Length)
            throw new InvalidDataException("JBIG2: Huffman-coded refinement data runs past the segment.");

        // Each refinement is its own arithmetically coded unit, so it starts from
        // fresh adaptive state.
        contexts.Clear();
        var mq = new MqDecoder(reader.Data.Slice(start, size));
        var bitmap = RefinementRegionDecoder.Decode(
            ref mq, contexts, width, height, template, reference, dx, dy,
            typicalPrediction: false, at, budget);

        reader.SeekToByte((long)start + size);
        return bitmap;
    }

    // Coordinates are accumulated in long from coded deltas; anything outside int
    // can only place a symbol off the region, so pin it somewhere that still is.
    private static int ClampCoordinate(long value) => (int)Math.Clamp(value, int.MinValue / 2, int.MaxValue / 2);

    private static int RequireHuffman(int value, string field) => value != HuffmanTable.OutOfBand
        ? value
        : throw new InvalidDataException($"JBIG2: unexpected OOB decoding {field} in a text region.");

    /// <summary>Floor division by two that keeps working for negative deltas, which <c>/ 2</c> does not.</summary>
    private static int FloorHalf(int value) => value >> 1;

    private static int Require(int value, string field) => value != ArithIntDecoder.OutOfBand
        ? value
        : throw new InvalidDataException($"JBIG2: unexpected OOB decoding {field} in a text region.");
}
