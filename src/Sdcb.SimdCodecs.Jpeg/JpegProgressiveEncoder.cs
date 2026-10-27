using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Progressive DCT entropy coding (ITU-T T.81 Annex G) over the stored quantised
/// coefficients: DC scans (first and refinement), AC spectral-selection first scans with
/// end-of-band runs, and AC successive-approximation refinement scans. Every scan that needs
/// Huffman tables gets its own optimal ones (Annex K.2), counted in a dry run of the same coder.
/// </summary>
internal static class JpegProgressiveEncoder
{
    /// <summary>One scan: component indices (into the frame), spectral band Ss..Se, approximation Ah / Al.</summary>
    internal readonly record struct Scan(int[] Components, int Ss, int Se, int Ah, int Al);

    // Scan scripts chosen by measuring the total encoded size of the benchmark corpus (10 photos at
    // 2048 px; q75 / q90 4:2:0 and q95 4:4:4) over a set of candidate scripts: DC at full precision
    // in one interleaved scan, the lowest luma frequencies first in two short bands, chroma at one
    // bit less precision, the remaining luma band, then one refinement pass per approximation bit.
    // It beat the next candidates by 0.1-0.5% and a spectral-selection-only script by 1-4%.
    internal static readonly Scan[] ColorScript =
    [
        new([0, 1, 2], 0, 0, 0, 0),
        new([0], 1, 2, 0, 2),
        new([0], 3, 9, 0, 2),
        new([2], 1, 63, 0, 1),
        new([1], 1, 63, 0, 1),
        new([0], 10, 63, 0, 2),
        new([0], 1, 63, 2, 1),
        new([2], 1, 63, 1, 0),
        new([1], 1, 63, 1, 0),
        new([0], 1, 63, 1, 0),
    ];

    internal static readonly Scan[] GrayScript =
    [
        new([0], 0, 0, 0, 0),
        new([0], 1, 2, 0, 2),
        new([0], 3, 9, 0, 2),
        new([0], 10, 63, 0, 2),
        new([0], 1, 63, 2, 1),
        new([0], 1, 63, 1, 0),
    ];
    public static void Encode(ref JpegBitWriter w, JpegEncoder.Frame f, ReadOnlySpan<Scan> script)
    {
        foreach (var scan in script)
        {
            var comps = scan.Components.Select(i => f.Components[i]).ToArray();
            var tables = new (int Dc, int Ac)[comps.Length];

            if (scan.Ss == 0 && scan.Ah > 0)
            {
                // DC refinement: raw bits, no Huffman tables.
                JpegEncoder.WriteScanHeader(ref w, comps, tables, 0, 0, scan.Ah, scan.Al);
                var coder = new Coder(ref w, null);
                RunScan(ref coder, f, comps, scan);
            }
            else
            {
                var freq = new[] { new long[256], new long[256] };
                var counter = new Coder(ref w, freq);
                RunScan(ref counter, f, comps, scan);

                var huff = new JpegHuffmanEncodeTable?[2];
                var dht = new List<(int, int, JpegHuffmanEncodeTable)>();
                var cls = scan.Ss == 0 ? 0 : 1;
                for (var t = 0; t < 2; t++)
                {
                    if (!freq[t].AsSpan().ContainsAnyExcept(0)) continue;
                    huff[t] = JpegHuffmanEncodeTable.FromFrequencies(freq[t]);
                    dht.Add((cls, t, huff[t]!));
                }

                for (var i = 0; i < comps.Length; i++)
                    tables[i] = scan.Ss == 0 ? (comps[i].TableId, 0) : (0, 0);

                JpegEncoder.WriteHuffmanTables(ref w, [.. dht]);
                JpegEncoder.WriteScanHeader(ref w, comps, tables, scan.Ss, scan.Se, scan.Ah, scan.Al);
                var coder = new Coder(ref w, null) { Tables = huff };
                RunScan(ref coder, f, comps, scan);
            }

            w.FlushBits();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RunScan(ref Coder coder, JpegEncoder.Frame f, JpegEncoder.Component[] comps, Scan scan)
    {
        if (scan.Ss == 0)
        {
            Span<int> pred = stackalloc int[comps.Length];
            pred.Clear();
            if (comps.Length == 1)
            {
                var comp = comps[0];
                for (var row = 0; row < comp.ScanBlocksH; row++)
                {
                    for (var col = 0; col < comp.ScanBlocksW; col++)
                        coder.Dc(comp.Block(row, col)[0], ref pred[0], comp.TableId, scan.Ah, scan.Al);
                }
            }
            else
            {
                for (var my = 0; my < f.McusY; my++)
                {
                    for (var mx = 0; mx < f.McusX; mx++)
                    {
                        for (var c = 0; c < comps.Length; c++)
                        {
                            var comp = comps[c];
                            for (var v = 0; v < comp.V; v++)
                            {
                                for (var h = 0; h < comp.H; h++)
                                    coder.Dc(comp.Block(my * comp.V + v, mx * comp.H + h)[0], ref pred[c], comp.TableId, scan.Ah, scan.Al);
                            }
                        }
                    }
                }
            }

            return;
        }

        var single = comps[0];
        for (var row = 0; row < single.ScanBlocksH; row++)
        {
            for (var col = 0; col < single.ScanBlocksW; col++)
            {
                var block = single.Block(row, col);
                if (scan.Ah == 0) coder.AcFirst(block, scan.Ss, scan.Se, scan.Al);
                else coder.AcRefine(block, scan.Ss, scan.Se, scan.Al);
            }
        }

        coder.Finish();
    }

    /// <summary>
    /// Symbol and bit emitter for one scan. With <c>freq</c> set it only counts symbols (the
    /// dry run that sizes the tables); otherwise it writes, with tables built from that count.
    /// Both runs take identical decisions, so the counted symbols are exactly the written ones.
    /// </summary>
    private ref struct Coder
    {
        private const int MaxEobRun = 0x7FFF;
        private const int MaxBufferedBits = 1000;

        private ref JpegBitWriter _w;
        private readonly long[][]? _freq;
        public JpegHuffmanEncodeTable?[] Tables;

        private int _eobRun;
        private readonly byte[] _eobBits;
        private int _eobBitCount;

        public Coder(ref JpegBitWriter w, long[][]? freq)
        {
            _w = ref w;
            _freq = freq;
            Tables = [];
            _eobBits = new byte[MaxBufferedBits + 64];
        }

        private void Symbol(int table, int symbol)
        {
            if (_freq is not null)
            {
                _freq[table][symbol]++;
                return;
            }

            var code = Tables[table]!.Codes[symbol];
            _w.Put(code >> 8, (int)(code & 0xFF));
        }

        private void Bits(uint value, int n)
        {
            if (_freq is null && n > 0) _w.Put(value, n);
        }

        public void Dc(int coefficient, ref int pred, int table, int ah, int al)
        {
            BeginBlock();
            DcCore(coefficient, ref pred, table, ah, al);
            EndBlock();
        }

        private void DcCore(int coefficient, ref int pred, int table, int ah, int al)
        {
            if (ah > 0)
            {
                Bits((uint)(coefficient >> al) & 1, 1);
                return;
            }

            var v = coefficient >> al;
            var diff = v - pred;
            pred = v;
            var nb = JpegBitWriter.Category((uint)Math.Abs(diff));
            Symbol(table, nb);
            Bits((uint)(diff < 0 ? diff - 1 : diff) & ((1u << nb) - 1), nb);
        }

        // Unchecked Puts: each block (and the final end-of-band flush) first reserves room for all it
        // can write — at most 63 symbols with their bits plus a buffered end-of-band run, well under
        // 1 KiB, doubled for stuffing — and stuffs its bytes once at the end.
        private void BeginBlock()
        {
            if (_freq is null) _w.Ensure(4096);
        }

        private void EndBlock()
        {
            if (_freq is null) _w.Stuff();
        }

        public void Finish()
        {
            BeginBlock();
            FlushEobRun();
            EndBlock();
        }

        private void FlushEobRun()
        {
            if (_eobRun > 0)
            {
                var nb = JpegBitWriter.Category((uint)_eobRun) - 1;
                Symbol(0, nb << 4);
                Bits((uint)_eobRun & ((1u << nb) - 1), nb);
                _eobRun = 0;
            }

            for (var i = 0; i < _eobBitCount; i++) Bits(_eobBits[i], 1);
            _eobBitCount = 0;
        }

        /// <summary>AC first scan (G.1.2.2): band Ss..Se of the point-transformed coefficients.</summary>
        public void AcFirst(ReadOnlySpan<short> zz, int ss, int se, int al)
        {
            BeginBlock();
            AcFirstCore(zz, ss, se, al);
            EndBlock();
        }

        private void AcFirstCore(ReadOnlySpan<short> zz, int ss, int se, int al)
        {
            // Walk the coefficients still significant after the point transform; every other one in
            // the band is a zero of the current run.
            var m = JpegEncoderKernels.SignificantMask(zz, al) & Band(ss, se);
            var prev = ss - 1;
            while (m != 0)
            {
                var k = BitOperations.TrailingZeroCount(m);
                var run = k - prev - 1;
                FlushEobRun();
                for (; run > 15; run -= 16) Symbol(0, 0xF0);
                int v = zz[k];
                var a = Math.Abs(v) >> al;
                var nb = JpegBitWriter.Category((uint)a);
                Symbol(0, (run << 4) | nb);
                Bits((uint)(v < 0 ? ~a : a) & ((1u << nb) - 1), nb);
                prev = k;
                m &= m - 1;
            }

            if (prev < se && ++_eobRun == MaxEobRun) FlushEobRun();
        }

        /// <summary>Bits ss..se.</summary>
        private static ulong Band(int ss, int se) => (ulong.MaxValue >> (63 - se)) & (ulong.MaxValue << ss);
        /// <summary>
        /// AC refinement scan (G.1.2.3): newly significant coefficients are coded as run/size-1
        /// symbols with a sign bit; already significant ones contribute one correction bit each,
        /// sent after the next symbol (or with the end-of-band run) in the order the decoder
        /// walks them.
        /// </summary>
        public void AcRefine(ReadOnlySpan<short> zz, int ss, int se, int al)
        {
            BeginBlock();
            AcRefineCore(zz, ss, se, al);
            EndBlock();
        }

        private void AcRefineCore(ReadOnlySpan<short> zz, int ss, int se, int al)
        {
            // Significant at this bit plane (nz), already significant before it (old), newly
            // significant (nz & ~old). Only coefficients that are still zero count in a run.
            var band = Band(ss, se);
            var nz = JpegEncoderKernels.SignificantMask(zz, al) & band;
            var old = JpegEncoderKernels.SignificantMask(zz, al + 1) & band;
            var fresh = nz & ~old;
            var eob = fresh == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(fresh);

            Span<byte> pending = stackalloc byte[64];
            var pendingCount = 0;
            var run = 0;
            var prev = ss - 1;
            var m = nz;
            while (m != 0)
            {
                var k = BitOperations.TrailingZeroCount(m);
                run += k - prev - 1;
                prev = k;
                m &= m - 1;

                while (run > 15 && k <= eob)
                {
                    FlushEobRun();
                    Symbol(0, 0xF0);
                    run -= 16;
                    for (var i = 0; i < pendingCount; i++) Bits(pending[i], 1);
                    pendingCount = 0;
                }

                if (((old >> k) & 1) != 0)
                {
                    pending[pendingCount++] = (byte)((Math.Abs((int)zz[k]) >> al) & 1);
                    continue;
                }

                FlushEobRun();
                Symbol(0, (run << 4) | 1);
                Bits(zz[k] < 0 ? 0u : 1u, 1);
                for (var i = 0; i < pendingCount; i++) Bits(pending[i], 1);
                pendingCount = 0;
                run = 0;
            }

            run += se - prev;
            if (run > 0 || pendingCount > 0)
            {
                _eobRun++;
                pending[..pendingCount].CopyTo(_eobBits.AsSpan(_eobBitCount));
                _eobBitCount += pendingCount;
                if (_eobRun == MaxEobRun || _eobBitCount > MaxBufferedBits) FlushEobRun();
            }
        }    }
}
