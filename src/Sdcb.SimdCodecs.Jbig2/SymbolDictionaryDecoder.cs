using System;
using System.Collections.Generic;
using System.IO;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>Everything §6.5 needs beyond the coded data and the input symbols.</summary>
/// <param name="NewSymbols">SDNUMNEWSYMS.</param>
/// <param name="ExportedSymbols">SDNUMEXSYMS.</param>
/// <param name="Template">SDTEMPLATE, the generic template new symbols are coded with.</param>
/// <param name="RefinementAggregate">SDREFAGG — symbols may be built by refining or aggregating existing ones.</param>
/// <param name="RefinementTemplate">SDRTEMPLATE.</param>
internal readonly record struct SymbolDictionaryParameters(
    int NewSymbols,
    int ExportedSymbols,
    int Template,
    bool RefinementAggregate,
    int RefinementTemplate);

/// <summary>
/// Symbol dictionary decoding — ITU-T T.88 §6.5. Produces the glyph bitmaps a
/// text region stamps onto the page.
/// <para>
/// Symbols are grouped into <em>height classes</em>: the decoder walks up through
/// heights, and within each height reads a run of symbols whose widths are coded
/// as deltas. Grouping by height is what makes the width deltas small, and an
/// out-of-band width is how a height class says it is finished.
/// </para>
/// <para>
/// A dictionary can also import symbols from the dictionaries its segment refers
/// to, and re-export any mixture of imported and new ones. That is the mechanism
/// behind PDF's <c>/JBIG2Globals</c>: one dictionary of glyphs shared across
/// every page of a scanned document.
/// </para>
/// <para>
/// Huffman-coded dictionaries (SDHUFF = 1) go through <see cref="DecodeHuffman"/>:
/// the same height classes, but the numbers come from Huffman tables, and unless
/// SDREFAGG is set each height class's bitmaps arrive together as one collective
/// bitmap, uncompressed or MMR-coded (§6.5.9).
/// </para>
/// </summary>
internal static class SymbolDictionaryDecoder
{
    /// <summary>
    /// Decodes a dictionary and returns just the exported symbols — the imported
    /// ones a caller passed in are re-exported only if the stream says so.
    /// </summary>
    /// <param name="mq">The arithmetic decoder, positioned at the dictionary's coded data.</param>
    /// <param name="p">Parameters from the segment header.</param>
    /// <param name="inputSymbols">SDINSYMS, gathered from the dictionaries this segment refers to.</param>
    /// <param name="at">SDAT, the generic template's AT pixels.</param>
    /// <param name="refinementAt">SDRAT, used only when SDREFAGG is set.</param>
    /// <param name="budget">
    /// The decode's remaining pixel allowance. A dictionary is metered like any
    /// other producer of pixels: symbol widths and heights accumulate from coded
    /// deltas and are bounded only by "&gt; 0", so the declared symbol count times
    /// the per-bitmap ceiling is not a useful bound on its own.
    /// </param>
    public static Jbig2Bitmap[] Decode(
        ref MqDecoder mq,
        SymbolDictionaryParameters p,
        Jbig2Bitmap[] inputSymbols,
        scoped ReadOnlySpan<sbyte> at,
        scoped ReadOnlySpan<sbyte> refinementAt,
        Jbig2PixelBudget budget)
    {
        var dh = new ArithIntDecoder();
        var dw = new ArithIntDecoder();
        var ex = new ArithIntDecoder();
        var ai = new ArithIntDecoder();
        var fields = new TextRegionDecoder.Fields();

        var genericContexts = new byte[1 << GenericRegionDecoder.ContextBits(p.Template)];
        var refinementContexts = p.RefinementAggregate
            ? new byte[1 << RefinementRegionDecoder.ContextBits(p.RefinementTemplate)]
            : [];

        // §6.5.8.2.3: the symbol ID alphabet spans the imported symbols plus every
        // new one, including those not yet decoded — an aggregate symbol may refer
        // to an earlier sibling.
        var total = inputSymbols.Length + p.NewSymbols;
        var codeLength = TextRegionDecoder.SymbolCodeLength(total);
        var idContexts = p.RefinementAggregate
            ? new byte[ArithIntDecoder.IdContextSize(codeLength)]
            : [];

        var newSymbols = new List<Jbig2Bitmap>(p.NewSymbols);
        var height = 0;

        while (newSymbols.Count < p.NewSymbols)
        {
            var deltaHeight = dh.Decode(ref mq);
            if (deltaHeight == ArithIntDecoder.OutOfBand)
                throw new InvalidDataException("JBIG2: unexpected OOB decoding a symbol dictionary height class.");

            height += deltaHeight;
            if (height <= 0)
                throw new InvalidDataException($"JBIG2: symbol dictionary height class has non-positive height {height}.");

            var countBeforeClass = newSymbols.Count;
            var width = 0;
            while (true)
            {
                // OOB on IADW ends the height class — the one place in the format
                // where "no more" is cheaper than a count.
                var deltaWidth = dw.Decode(ref mq);
                if (deltaWidth == ArithIntDecoder.OutOfBand) break;

                width += deltaWidth;
                if (width <= 0)
                    throw new InvalidDataException($"JBIG2: symbol dictionary symbol has non-positive width {width}.");
                if (newSymbols.Count >= p.NewSymbols)
                    throw new InvalidDataException("JBIG2: symbol dictionary codes more symbols than it declared.");

                newSymbols.Add(p.RefinementAggregate
                    ? DecodeAggregate(
                        ref mq, p, inputSymbols, newSymbols, width, height, codeLength,
                        ai, fields, idContexts, refinementContexts, refinementAt, budget)
                    : GenericRegionDecoder.Decode(
                        ref mq, genericContexts, width, height, p.Template,
                        typicalPrediction: false, at, budget));
            }

            // Progress guard — the same discipline MmrDecoder applies to its own
            // row loop, and for the same reason. A height class that closes on the
            // first IADW without coding a symbol advances nothing, and the MQ
            // decoder never runs dry: T.88 E.3.4 has it read every byte past the
            // end of its data as 0xFF, so a stream that keeps yielding "empty
            // class" spins here for ever. Nothing is allocated on that path, so
            // there is no memory growth for the pixel budget to catch — found by
            // fuzzing, not by reading. With this, each pass adds at least one
            // symbol, so the loop is bounded by SDNUMNEWSYMS.
            if (newSymbols.Count == countBeforeClass)
                throw new InvalidDataException(
                    $"JBIG2: symbol dictionary height class at height {height} codes no symbols.");
        }

        var selector = new ExportSelector(inputSymbols, newSymbols, p.ExportedSymbols);
        while (!selector.Done) selector.Take(ex.Decode(ref mq));
        return selector.Result();
    }

    /// <summary>The Huffman tables a Huffman-coded dictionary reads its numbers with (§7.4.2.1.1).</summary>
    internal sealed record HuffmanTables(
        HuffmanTable DeltaHeight, HuffmanTable DeltaWidth, HuffmanTable BitmapSize, HuffmanTable AggregateInstances);

    /// <summary>
    /// Decodes a Huffman-coded dictionary (SDHUFF = 1, T.88 §6.5.5 with the Huffman
    /// branches of §6.5.6 to §6.5.10).
    /// </summary>
    /// <param name="data">The coded data, after the segment's fixed fields.</param>
    /// <param name="p">Parameters from the segment header.</param>
    /// <param name="inputSymbols">SDINSYMS.</param>
    /// <param name="tables">SDHUFFDH, SDHUFFDW, SDHUFFBMSIZE and SDHUFFAGGINST.</param>
    /// <param name="refinementAt">SDRAT, used only when SDREFAGG is set.</param>
    /// <param name="budget">The decode's remaining pixel allowance.</param>
    public static Jbig2Bitmap[] DecodeHuffman(
        ReadOnlySpan<byte> data,
        SymbolDictionaryParameters p,
        Jbig2Bitmap[] inputSymbols,
        HuffmanTables tables,
        scoped ReadOnlySpan<sbyte> refinementAt,
        Jbig2PixelBudget budget)
    {
        var reader = new Jbig2BitReader(data);
        var refinementContexts = p.RefinementAggregate
            ? new byte[1 << RefinementRegionDecoder.ContextBits(p.RefinementTemplate)]
            : [];

        // §6.5.8.2.3, Huffman case: the symbol IDs of a refinement or aggregate are
        // plain binary numbers SBSYMCODELEN = ceil(log2(SDNUMINSYMS + SDNUMNEWSYMS))
        // bits wide.
        var total = inputSymbols.Length + p.NewSymbols;
        var codeLength = 0;
        while (1L << codeLength < total) codeLength++;

        var newSymbols = new List<Jbig2Bitmap>(Math.Min(p.NewSymbols, 1 << 16));
        var widths = new List<int>();
        var height = 0;

        while (newSymbols.Count < p.NewSymbols)
        {
            var deltaHeight = tables.DeltaHeight.Decode(ref reader);
            if (deltaHeight == HuffmanTable.OutOfBand)
                throw new InvalidDataException("JBIG2: unexpected OOB decoding a symbol dictionary height class.");

            height = (int)Math.Clamp((long)height + deltaHeight, int.MinValue, int.MaxValue);
            if (height <= 0)
                throw new InvalidDataException($"JBIG2: symbol dictionary height class has non-positive height {height}.");

            var countBeforeClass = newSymbols.Count;
            var width = 0;
            long totalWidth = 0;
            widths.Clear();

            while (true)
            {
                var deltaWidth = tables.DeltaWidth.Decode(ref reader);
                if (deltaWidth == HuffmanTable.OutOfBand) break;

                width = (int)Math.Clamp((long)width + deltaWidth, int.MinValue, int.MaxValue);
                if (width <= 0)
                    throw new InvalidDataException($"JBIG2: symbol dictionary symbol has non-positive width {width}.");
                if (newSymbols.Count + widths.Count >= p.NewSymbols)
                    throw new InvalidDataException("JBIG2: symbol dictionary codes more symbols than it declared.");

                if (p.RefinementAggregate)
                {
                    newSymbols.Add(DecodeAggregateHuffman(
                        ref reader, p, inputSymbols, newSymbols, width, height, codeLength,
                        tables, refinementContexts, refinementAt, budget));
                }
                else
                {
                    widths.Add(width);
                    totalWidth += width;
                }
            }

            if (!p.RefinementAggregate && widths.Count > 0)
                DecodeCollective(ref reader, tables, widths, totalWidth, height, newSymbols, budget);

            // The same progress guard as the arithmetic path.
            if (newSymbols.Count == countBeforeClass)
                throw new InvalidDataException(
                    $"JBIG2: symbol dictionary height class at height {height} codes no symbols.");
        }

        // §6.5.10: with SDHUFF = 1 the export run lengths use table B.1.
        var runs = HuffmanTable.Get(1);
        var selector = new ExportSelector(inputSymbols, newSymbols, p.ExportedSymbols);
        while (!selector.Done) selector.Take(runs.Decode(ref reader));
        return selector.Result();
    }

    /// <summary>
    /// §6.5.9: a height class's bitmaps, side by side in one collective bitmap
    /// TOTWIDTH wide, byte-aligned, BMSIZE bytes long. BMSIZE 0 means stored
    /// uncompressed, row by row, each row padded to a byte; anything else is that
    /// many bytes of MMR.
    /// </summary>
    private static void DecodeCollective(
        ref Jbig2BitReader reader, HuffmanTables tables, List<int> widths, long totalWidth, int height,
        List<Jbig2Bitmap> newSymbols, Jbig2PixelBudget budget)
    {
        var size = tables.BitmapSize.Decode(ref reader);
        if (size == HuffmanTable.OutOfBand || size < 0)
            throw new InvalidDataException("JBIG2: symbol dictionary collective bitmap size is not a non-negative integer.");

        if (totalWidth > int.MaxValue || totalWidth * height > Jbig2Limits.MaxBitmapPixels)
            throw new InvalidDataException(
                $"JBIG2: symbol dictionary height class is {totalWidth}x{height}, past the " +
                $"{Jbig2Limits.MaxBitmapPixels:N0} pixel ceiling for one bitmap.");

        reader.AlignToByte();
        var start = reader.BytePosition;
        var data = reader.Data;
        var w = (int)totalWidth;
        Jbig2Bitmap collective;
        long length;

        if (size == 0)
        {
            var stride = (w + 7) / 8;
            length = (long)stride * height;
            if (start + length > data.Length)
                throw new InvalidDataException("JBIG2: uncompressed symbol dictionary collective bitmap ends early.");

            budget.Charge(w, height);
            collective = new Jbig2Bitmap(w, height);
            for (var y = 0; y < height; y++)
            {
                var row = data.Slice(start + y * stride, stride);
                for (var x = 0; x < w; x++)
                    collective.Data[y * w + x] = (byte)((row[x >> 3] >> (7 - (x & 7))) & 1);
            }
        }
        else
        {
            length = size;
            if (start + length > data.Length)
                throw new InvalidDataException("JBIG2: MMR symbol dictionary collective bitmap ends early.");
            collective = MmrDecoder.Decode(data.Slice(start, size), w, height, budget);
        }

        reader.SeekToByte(start + length);

        var x0 = 0;
        foreach (var width in widths)
        {
            newSymbols.Add(collective.Crop(x0, 0, width, height));
            x0 += width;
        }
    }

    /// <summary>§6.5.8.2 with SDHUFF = 1: a refined or aggregated symbol.</summary>
    private static Jbig2Bitmap DecodeAggregateHuffman(
        ref Jbig2BitReader reader,
        SymbolDictionaryParameters p,
        Jbig2Bitmap[] inputSymbols,
        List<Jbig2Bitmap> newSymbols,
        int width,
        int height,
        int codeLength,
        HuffmanTables tables,
        Span<byte> refinementContexts,
        scoped ReadOnlySpan<sbyte> refinementAt,
        Jbig2PixelBudget budget)
    {
        var instances = tables.AggregateInstances.Decode(ref reader);
        if (instances == HuffmanTable.OutOfBand || instances < 1)
            throw new InvalidDataException("JBIG2: symbol dictionary aggregate instance count is not a positive integer.");

        var alphabet = new Jbig2Bitmap[inputSymbols.Length + p.NewSymbols];
        inputSymbols.CopyTo(alphabet, 0);
        newSymbols.CopyTo(alphabet, inputSymbols.Length);
        for (var i = inputSymbols.Length + newSymbols.Count; i < alphabet.Length; i++)
            alphabet[i] = Jbig2Bitmap.Empty;

        if (instances == 1)
        {
            // §6.5.8.2.2: ID as SBSYMCODELEN raw bits, RDX and RDY with B.15, the
            // refinement's byte count with B.1, then the refinement itself,
            // arithmetically coded, starting on the next byte.
            var id = (int)reader.ReadBits(codeLength);
            if ((uint)id >= (uint)alphabet.Length)
                throw new InvalidDataException($"JBIG2: aggregate symbol names symbol {id} of {alphabet.Length}.");

            var rdx = Require(HuffmanTable.Get(15).Decode(ref reader), "RDX");
            var rdy = Require(HuffmanTable.Get(15).Decode(ref reader), "RDY");
            var size = Require(HuffmanTable.Get(1).Decode(ref reader), "BMSIZE");

            return TextRegionDecoder.DecodeHuffmanRefinement(
                ref reader, size, refinementContexts, width, height, p.RefinementTemplate,
                alphabet[id], rdx, rdy, refinementAt, budget);
        }

        // §6.5.8.2.1: a text region inside the dictionary, with the tables Table 17
        // fixes and uniform-length symbol IDs.
        var parameters = new TextRegionParameters(
            Width: width,
            Height: height,
            Instances: instances,
            Strips: 1,
            DefaultPixel: 0,
            Combination: CombinationOperator.Or,
            Corner: ReferenceCorner.TopLeft,
            Transposed: false,
            DsOffset: 0,
            Refine: true,
            RefinementTemplate: p.RefinementTemplate);

        return TextRegionDecoder.DecodeHuffman(
            ref reader, parameters, alphabet, TextRegionDecoder.HuffmanTables.SymbolDictionaryAggregate,
            symbolCodes: null, codeLength, refinementContexts, refinementAt, budget);
    }

    /// <summary>
    /// §6.5.8.2: a symbol built by refining another, or by aggregating several.
    /// The single-instance case is by far the common one — it is how an encoder
    /// says "this glyph is that glyph with a few pixels different" — and the
    /// multi-instance case falls back on a whole text region.
    /// </summary>
    private static Jbig2Bitmap DecodeAggregate(
        ref MqDecoder mq,
        SymbolDictionaryParameters p,
        Jbig2Bitmap[] inputSymbols,
        List<Jbig2Bitmap> newSymbols,
        int width,
        int height,
        int codeLength,
        ArithIntDecoder ai,
        TextRegionDecoder.Fields fields,
        scoped Span<byte> idContexts,
        scoped Span<byte> refinementContexts,
        scoped ReadOnlySpan<sbyte> refinementAt,
        Jbig2PixelBudget budget)
    {
        var instances = ai.Decode(ref mq);
        if (instances == ArithIntDecoder.OutOfBand || instances < 1)
            throw new InvalidDataException("JBIG2: symbol dictionary aggregate instance count is not a positive integer.");

        // The alphabet visible to this symbol: imports, then the siblings decoded
        // so far. T.88 sizes it by the *declared* total, so the tail is blank and
        // naming it is a malformed stream rather than a crash.
        var alphabet = new Jbig2Bitmap[inputSymbols.Length + p.NewSymbols];
        inputSymbols.CopyTo(alphabet, 0);
        newSymbols.CopyTo(alphabet, inputSymbols.Length);
        for (var i = inputSymbols.Length + newSymbols.Count; i < alphabet.Length; i++)
            alphabet[i] = Jbig2Bitmap.Empty;

        if (instances == 1)
        {
            var id = ArithIntDecoder.DecodeId(ref mq, idContexts, codeLength);
            if ((uint)id >= (uint)alphabet.Length)
                throw new InvalidDataException($"JBIG2: aggregate symbol names symbol {id} of {alphabet.Length}.");

            var rdx = Require(fields.Rdx.Decode(ref mq), "IARDX");
            var rdy = Require(fields.Rdy.Decode(ref mq), "IARDY");

            return RefinementRegionDecoder.Decode(
                ref mq, refinementContexts, width, height, p.RefinementTemplate,
                alphabet[id], rdx, rdy, typicalPrediction: false, refinementAt, budget);
        }

        // §6.5.8.2.1: more than one instance means the symbol is literally a small
        // text region composed from the alphabet.
        var parameters = new TextRegionParameters(
            Width: width,
            Height: height,
            Instances: instances,
            Strips: 1,
            DefaultPixel: 0,
            Combination: CombinationOperator.Or,
            Corner: ReferenceCorner.TopLeft,
            Transposed: false,
            DsOffset: 0,
            Refine: true,
            RefinementTemplate: p.RefinementTemplate);

        return TextRegionDecoder.Decode(
            ref mq, parameters, alphabet, fields, idContexts, refinementContexts, refinementAt, budget);
    }

    /// <summary>
    /// §6.5.10: which symbols leave the dictionary, as alternating runs of
    /// "skip this many" and "export this many" over the imports followed by the
    /// new symbols. Fed one run at a time, so the arithmetic and Huffman paths can
    /// share it whatever decodes their runs.
    /// </summary>
    private sealed class ExportSelector(Jbig2Bitmap[] inputSymbols, List<Jbig2Bitmap> newSymbols, int expected)
    {
        private readonly int _total = inputSymbols.Length + newSymbols.Count;
        private readonly List<Jbig2Bitmap> _exported = new(Math.Min(expected, 1 << 16));
        private int _index;
        private bool _exporting;

        // A zero-length run advances nothing, and a never-dry MQ decoder can yield
        // zero for ever. A *leading* zero run is completely normal (a dictionary
        // that exports from index 0 codes one), so this is a ceiling on the number
        // of runs rather than a ban on empty ones.
        private int _runs;

        public bool Done => _index >= _total;

        public void Take(int run)
        {
            if (++_runs > 2 * _total + 8)
                throw new InvalidDataException(
                    $"JBIG2: symbol dictionary export runs do not terminate — more than {2 * _total + 8} runs " +
                    $"over {_total} symbols.");
            if (run == ArithIntDecoder.OutOfBand || run < 0)
                throw new InvalidDataException("JBIG2: symbol dictionary export run length is not a non-negative integer.");
            if (_index + run > _total)
                throw new InvalidDataException("JBIG2: symbol dictionary export runs overrun the symbol list.");

            if (_exporting)
                for (var i = 0; i < run; i++)
                    _exported.Add(_index + i < inputSymbols.Length
                        ? inputSymbols[_index + i]
                        : newSymbols[_index + i - inputSymbols.Length]);

            _index += run;
            _exporting = !_exporting;
        }

        public Jbig2Bitmap[] Result() => _exported.Count == expected
            ? [.. _exported]
            : throw new InvalidDataException(
                $"JBIG2: symbol dictionary exported {_exported.Count} symbols but declared {expected}.");
    }

    private static int Require(int value, string field) => value != ArithIntDecoder.OutOfBand
        ? value
        : throw new InvalidDataException($"JBIG2: unexpected OOB decoding {field} in a symbol dictionary.");
}
