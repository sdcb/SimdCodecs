using System.Buffers.Binary;
using Sdcb.SimdCodecs.Jbig2;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A test-only encoder for symbol dictionaries and text regions (T.88 §6.4/§6.5,
/// arithmetic variant).
/// <para>
/// It exists for one reason: jbig2enc emits exactly one shape — arithmetic
/// coding, no refinement, bottom-left reference corner, one strip — so the
/// committed fixtures cannot reach the rest of §6.4. These streams can, and
/// <c>Jbig2TextRegionOracleTests</c> pushes them through jbig2dec, which has its
/// own idea of what every REFCORNER and TRANSPOSED combination means.
/// </para>
/// <para>
/// Encoding JBIG2 is a shipped non-goal, and nothing here is or will be part of
/// the package. Note also what a round-trip through this proves on its own:
/// nothing about the placement rules, since both sides would share any mistake.
/// jbig2dec is the check that matters.
/// </para>
/// </summary>
internal static class Jbig2SymbolBuilder
{
    /// <summary>
    /// The write side of T.88 Annex A.2, mirroring <c>ArithIntDecoder</c>: sign,
    /// then a prefix naming one of six magnitude ranges, then the raw bits.
    /// </summary>
    internal sealed class IntEncoder
    {
        private readonly byte[] _contexts = new byte[512];

        /// <summary>Encodes the out-of-band marker — sign set, magnitude zero.</summary>
        public void EncodeOob(Jbig2MqEncoder mq) => Write(mq, negative: true, magnitude: 0);

        public void Encode(Jbig2MqEncoder mq, int value) =>
            Write(mq, value < 0, value < 0 ? -(long)value : value);

        private void Write(Jbig2MqEncoder mq, bool negative, long magnitude)
        {
            var prev = 1;
            Bit(mq, ref prev, negative ? 1 : 0);

            // The ranges are cumulative, so the first one that fits is the one
            // the decoder will read back.
            (long Offset, int Bits)[] ranges =
                [(0, 2), (4, 4), (20, 6), (84, 8), (340, 12), (4436, 32)];

            for (var i = 0; i < ranges.Length; i++)
            {
                var (offset, bits) = ranges[i];
                var span = bits == 32 ? long.MaxValue : 1L << bits;
                if (i < ranges.Length - 1 && magnitude >= offset + span) { Bit(mq, ref prev, 1); continue; }

                if (i < ranges.Length - 1) Bit(mq, ref prev, 0);

                var raw = magnitude - offset;
                for (var b = bits - 1; b >= 0; b--) Bit(mq, ref prev, (int)((raw >> b) & 1));
                return;
            }
        }

        private void Bit(Jbig2MqEncoder mq, ref int prev, int bit)
        {
            mq.Encode(_contexts, prev, bit);
            prev = prev < 256 ? (prev << 1) | bit : ((((prev << 1) | bit) & 511) | 256);
        }
    }

    /// <summary>The write side of A.3 — a fixed-width walk down the symbol ID context tree.</summary>
    private static void EncodeId(Jbig2MqEncoder mq, byte[] contexts, int codeLength, int id)
    {
        var prev = 1;
        for (var i = codeLength - 1; i >= 0; i--)
        {
            var bit = (id >> i) & 1;
            mq.Encode(contexts, prev, bit);
            prev = (prev << 1) | bit;
        }
    }

    /// <summary>
    /// A symbol dictionary segment holding <paramref name="symbols"/>, all
    /// exported (T.88 §7.4.3).
    /// </summary>
    /// <remarks>
    /// The symbols must be supplied already sorted by height — §6.5 codes them in
    /// height classes with non-decreasing height, and a decoder that meets a
    /// negative height delta is entitled to reject the stream.
    /// </remarks>
    /// <param name="symbols">The new symbols this dictionary codes, ordered by non-decreasing height.</param>
    /// <param name="template">SDTEMPLATE.</param>
    /// <param name="importedCount">
    /// How many symbols the segment inherits from the dictionaries it refers to.
    /// The export runs of §6.5.10 index imports first and new symbols after, so a
    /// dictionary that imports has to skip past them to export its own.
    /// </param>
    /// <param name="reExportImports">Export the inherited symbols too, ahead of the new ones.</param>
    public static byte[] SymbolDictionarySegment(
        Jbig2Bitmap[] symbols, int template = 0, int importedCount = 0, bool reExportImports = false)
    {
        for (var i = 1; i < symbols.Length; i++)
            if (symbols[i].Height < symbols[i - 1].Height)
                throw new ArgumentException("Symbols must be ordered by non-decreasing height.", nameof(symbols));

        var mq = new Jbig2MqEncoder();
        var dh = new IntEncoder();
        var dw = new IntEncoder();
        var ex = new IntEncoder();
        var at = (sbyte[])[.. GenericRegionDecoder.NominalAt(template)];

        // One context array shared by every symbol in the dictionary, exactly as
        // the decoder does — the whole point of a dictionary is that later glyphs
        // benefit from the statistics of earlier ones.
        var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];

        var height = 0;
        var index = 0;
        while (index < symbols.Length)
        {
            var classHeight = symbols[index].Height;
            dh.Encode(mq, classHeight - height);
            height = classHeight;

            var width = 0;
            while (index < symbols.Length && symbols[index].Height == classHeight)
            {
                var symbol = symbols[index++];
                dw.Encode(mq, symbol.Width - width);
                width = symbol.Width;
                EncodeGeneric(mq, contexts, symbol, template, at);
            }

            dw.EncodeOob(mq);
        }

        // §6.5.10 export flags: alternating skip/export runs over the imported
        // symbols followed by the new ones.
        var exported = symbols.Length + (reExportImports ? importedCount : 0);
        ex.Encode(mq, reExportImports ? 0 : importedCount);
        ex.Encode(mq, exported);

        var body = new List<byte>();
        WriteUInt16(body, (ushort)(template << 10));     // SDHUFF=0, SDREFAGG=0
        for (var i = 0; i < (template == 0 ? 8 : 2); i++) body.Add((byte)at[i]);
        WriteUInt32(body, (uint)exported);
        WriteUInt32(body, (uint)symbols.Length);         // new
        body.AddRange(mq.Flush());
        return [.. body];
    }

    /// <summary>
    /// A symbol dictionary whose new symbols are each built by <em>refining</em>
    /// an imported one (T.88 §6.5.8.2.2, SDREFAGG with one instance) rather than
    /// coded from scratch.
    /// </summary>
    /// <param name="imported">The symbols this dictionary inherits, and refines.</param>
    /// <param name="refined">The results, one per import and the same size as it.</param>
    public static byte[] RefiningSymbolDictionarySegment(Jbig2Bitmap[] imported, Jbig2Bitmap[] refined)
    {
        if (imported.Length != refined.Length)
            throw new ArgumentException("One refined symbol per import.", nameof(refined));

        var mq = new Jbig2MqEncoder();
        var dh = new IntEncoder();
        var dw = new IntEncoder();
        var ex = new IntEncoder();
        var ai = new IntEncoder();
        var rdx = new IntEncoder();
        var rdy = new IntEncoder();

        var total = imported.Length + refined.Length;
        var codeLength = TextRegionDecoder.SymbolCodeLength(total);
        var idContexts = new byte[ArithIntDecoder.IdContextSize(codeLength)];
        var refinementContexts = new byte[1 << RefinementRegionDecoder.ContextBits(0)];
        var at = (sbyte[])[.. RefinementRegionDecoder.NominalAt];

        var height = 0;
        var index = 0;
        while (index < refined.Length)
        {
            var classHeight = refined[index].Height;
            dh.Encode(mq, classHeight - height);
            height = classHeight;

            var width = 0;
            while (index < refined.Length && refined[index].Height == classHeight)
            {
                var symbol = refined[index];
                dw.Encode(mq, symbol.Width - width);
                width = symbol.Width;

                ai.Encode(mq, 1);                              // REFAGGNINST
                EncodeId(mq, idContexts, codeLength, index);   // refine the matching import
                rdx.Encode(mq, 0);
                rdy.Encode(mq, 0);
                EncodeRefinement(mq, refinementContexts, symbol, imported[index], at);
                index++;
            }

            dw.EncodeOob(mq);
        }

        // Export only the new symbols, skipping past the imports.
        ex.Encode(mq, imported.Length);
        ex.Encode(mq, refined.Length);

        var body = new List<byte>();
        WriteUInt16(body, 0x0002);                       // SDHUFF=0, SDREFAGG=1, SDRTEMPLATE=0
        for (var i = 0; i < 8; i++) body.Add((byte)GenericRegionDecoder.NominalAt(0)[i]);
        for (var i = 0; i < 4; i++) body.Add((byte)at[i]);
        WriteUInt32(body, (uint)refined.Length);         // exported
        WriteUInt32(body, (uint)refined.Length);         // new
        body.AddRange(mq.Flush());
        return [.. body];
    }

    /// <summary>One placement in a text region: which symbol, and where.</summary>
    /// <param name="Id">Index into the dictionary.</param>
    /// <param name="S">Coordinate along the strip.</param>
    /// <param name="T">Coordinate across it.</param>
    /// <param name="Refined">
    /// A per-instance correction of the dictionary symbol (SBREFINE), or null to
    /// stamp the symbol as-is. Must be the same size as the symbol: this builder
    /// codes a zero RDW/RDH, so only the pixels differ.
    /// </param>
    internal readonly record struct Placement(int Id, int S, int T, Jbig2Bitmap? Refined = null);

    /// <summary>
    /// A text region segment stamping <paramref name="placements"/> (T.88 §7.4.4).
    /// </summary>
    /// <remarks>
    /// Placements must be given in coding order: strips in increasing T, and
    /// within a strip increasing S. The encoder does not sort them, because a
    /// test that wants to see what an out-of-order stream does should be able to
    /// build one.
    /// </remarks>
    public static byte[] TextRegionSegment(
        int width,
        int height,
        Jbig2Bitmap[] symbols,
        Placement[] placements,
        ReferenceCorner corner = ReferenceCorner.TopLeft,
        bool transposed = false,
        int logStrips = 0,
        int dsOffset = 0,
        CombinationOperator combination = CombinationOperator.Or,
        byte defaultPixel = 0,
        int x = 0,
        int y = 0,
        CombinationOperator external = CombinationOperator.Or)
    {
        var strips = 1 << logStrips;
        var refine = placements.Any(p => p.Refined is not null);
        var mq = new Jbig2MqEncoder();
        var dt = new IntEncoder();
        var fs = new IntEncoder();
        var ds = new IntEncoder();
        var it = new IntEncoder();
        var ri = new IntEncoder();
        var rdw = new IntEncoder();
        var rdh = new IntEncoder();
        var rdx = new IntEncoder();
        var rdy = new IntEncoder();
        var refinementContexts = new byte[1 << RefinementRegionDecoder.ContextBits(0)];
        var refinementAt = (sbyte[])[.. RefinementRegionDecoder.NominalAt];

        var codeLength = TextRegionDecoder.SymbolCodeLength(symbols.Length);
        var idContexts = new byte[ArithIntDecoder.IdContextSize(codeLength)];

        // Group into strips the way §6.4.5 reads them back: T quantised by
        // SBSTRIPS, with the remainder coded per instance.
        var groups = placements
            .GroupBy(p => Math.DivRem(p.T, strips, out _) * strips)
            .OrderBy(g => g.Key)
            .ToArray();

        dt.Encode(mq, 0);   // STRIPT starts at 0, coded as a negated delta

        var stript = 0;
        var firsts = 0;
        foreach (var group in groups)
        {
            dt.Encode(mq, (group.Key - stript) / strips);
            stript = group.Key;

            var first = true;
            var curs = 0;
            foreach (var placement in group)
            {
                var extent = transposed ? symbols[placement.Id].Height : symbols[placement.Id].Width;
                var advanceFirst = transposed
                    ? corner is ReferenceCorner.BottomLeft or ReferenceCorner.BottomRight
                    : corner is ReferenceCorner.TopRight or ReferenceCorner.BottomRight;

                // The decoder's CURS lands on the far edge for a trailing-edge
                // corner, so the value coded here is the near edge either way.
                var codedS = advanceFirst ? placement.S - extent + 1 : placement.S;

                if (first)
                {
                    fs.Encode(mq, codedS - firsts);
                    firsts = codedS;
                    first = false;
                }
                else
                {
                    ds.Encode(mq, codedS - curs - dsOffset);
                }

                if (strips > 1) it.Encode(mq, placement.T - stript);
                EncodeId(mq, idContexts, codeLength, placement.Id);

                if (refine)
                {
                    ri.Encode(mq, placement.Refined is null ? 0 : 1);
                    if (placement.Refined is { } corrected)
                    {
                        // Same size, so both deltas are zero and the reference
                        // offset reduces to the RDX/RDY the decoder reads back.
                        rdw.Encode(mq, 0);
                        rdh.Encode(mq, 0);
                        rdx.Encode(mq, 0);
                        rdy.Encode(mq, 0);
                        EncodeRefinement(mq, refinementContexts, corrected, symbols[placement.Id], refinementAt);
                    }
                }

                curs = codedS + extent - 1;
            }

            ds.EncodeOob(mq);
        }

        var flags = (ushort)(
            (refine ? 0x0002 : 0) |
            (logStrips << 2) |
            ((int)corner << 4) |
            (transposed ? 0x0040 : 0) |
            ((int)combination << 7) |
            (defaultPixel << 9) |
            ((dsOffset & 0x1F) << 10));

        var body = new List<byte>();
        WriteUInt32(body, (uint)width);
        WriteUInt32(body, (uint)height);
        WriteUInt32(body, (uint)x);
        WriteUInt32(body, (uint)y);
        body.Add((byte)external);
        WriteUInt16(body, flags);

        // §7.4.4.1.2: SBRAT is present only when SBREFINE is set and SBRTEMPLATE is 0.
        if (refine) foreach (var v in refinementAt) body.Add((byte)v);

        WriteUInt32(body, (uint)placements.Length);
        body.AddRange(mq.Flush());
        return [.. body];
    }

    /// <summary>Refinement encode appended to an existing stream, sharing its context array.</summary>
    private static void EncodeRefinement(
        Jbig2MqEncoder mq, byte[] contexts, Jbig2Bitmap bitmap, Jbig2Bitmap reference, sbyte[] at)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                mq.Encode(contexts,
                    RefinementRegionDecoder.Context(bitmap, x, y, reference, x, y, 0, at),
                    bitmap.Data[y * bitmap.Width + x]);
    }

    /// <summary>Generic-region encode appended to an existing stream, sharing its context array.</summary>
    private static void EncodeGeneric(
        Jbig2MqEncoder mq, byte[] contexts, Jbig2Bitmap bitmap, int template, sbyte[] at)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                mq.Encode(contexts,
                    GenericRegionDecoder.Context(bitmap, x, y, template, at),
                    bitmap.Data[y * bitmap.Width + x]);
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
