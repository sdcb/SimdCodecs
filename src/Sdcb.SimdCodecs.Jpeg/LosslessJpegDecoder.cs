namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Lossless JPEG (ITU-T T.81 Annex H, SOF3) decoder. Hand-written, idiomatic
/// C# — does NOT reuse the baseline/progressive DCT decoder in
/// <see cref="JpegDecoder"/> because the lossless bitstream layout
/// is completely different (no DCT, no quantisation, no zig-zag scan;
/// Huffman-coded sample-difference predictors instead).
///
/// Supported markers: SOI, DHT, SOF3, DRI, SOS (+ entropy data + RSTn), EOI.
/// APPn/COM/DQT are skipped on the parse path; SOF0/1/2 → throws (use
/// <see cref="JpegDecoder.Decode"/> for baseline / progressive).
///
/// Precision: 2..16 bits (T.81 Annex H.1 allows up to 16). Components: any
/// number; interleaved and non-interleaved scans both supported. Restart
/// intervals via DRI / RSTn markers supported. Point transform Al applied
/// as a left-shift on the final sample value.
///
/// Predictors per H.1.2.1 / Table H.1, selected by Ss in the SOS:
/// 1=Ra, 2=Rb, 3=Rc, 4=Ra+Rb-Rc, 5=Ra+((Rb-Rc)>>1), 6=Rb+((Ra-Rc)>>1),
/// 7=(Ra+Rb)>>1. The special-case default predictor for the first sample
/// of the scan / first sample after a restart is <c>2^(P-Pt-1)</c>.
/// </summary>
internal static class LosslessJpegDecoder
{
    // Marker constants
    private const byte MarkerEscape = 0xFF;
    private const byte SOI  = 0xD8;
    private const byte EOI  = 0xD9;
    private const byte SOS  = 0xDA;
    private const byte DHT  = 0xC4;
    private const byte DRI  = 0xDD;
    private const byte DQT  = 0xDB;
    private const byte COM  = 0xFE;
    private const byte SOF0 = 0xC0;
    private const byte SOF1 = 0xC1;
    private const byte SOF2 = 0xC2;
    private const byte SOF3 = 0xC3;
    private const byte RST0 = 0xD0;
    private const byte RST7 = 0xD7;
    private const byte APP0 = 0xE0;
    private const byte APP15 = 0xEF;

    public static LosslessJpegResult Decode(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != MarkerEscape || jpeg[1] != SOI)
            throw new InvalidDataException("missing SOI marker");

        var pos = 2;
        FrameHeader? frame = null;
        int restartInterval = 0;
        var huffTables = new HuffmanTable?[4];
        ushort[]? output = null;

        while (pos < jpeg.Length)
        {
            if (jpeg[pos] != MarkerEscape)
                throw new InvalidDataException($"expected marker prefix 0xFF at offset {pos}, got 0x{jpeg[pos]:X2}");

            // Marker can be preceded by fill bytes (more 0xFF). Skip them.
            while (pos < jpeg.Length && jpeg[pos] == MarkerEscape) pos++;
            if (pos >= jpeg.Length) throw new InvalidDataException("truncated marker");
            var marker = jpeg[pos++];

            switch (marker)
            {
                case EOI:
                    return BuildResult(frame, output);

                case SOF0:
                case SOF1:
                case SOF2:
                    throw new InvalidDataException(
                        $"SOF{marker - SOF0} is baseline/progressive JPEG — use JpegDecoder.Decode instead of LosslessJpeg");

                case SOF3:
                    frame = ParseFrameHeader(jpeg, ref pos);
                    output = new ushort[frame.Value.Width * frame.Value.Height * frame.Value.Components.Length];
                    break;

                case DHT:
                    ParseHuffmanTables(jpeg, ref pos, huffTables);
                    break;

                case DRI:
                    restartInterval = ParseRestartInterval(jpeg, ref pos);
                    break;

                case SOS:
                    if (frame is null)
                        throw new InvalidDataException("SOS encountered before SOF");
                    if (output is null)
                        throw new InvalidDataException("output buffer not allocated");
                    var scan = ParseScanHeader(jpeg, ref pos, frame.Value);
                    DecodeScan(jpeg, ref pos, frame.Value, scan, huffTables, restartInterval, output);
                    break;

                case DQT:
                case COM:
                    SkipSegment(jpeg, ref pos);
                    break;

                default:
                    if (marker >= APP0 && marker <= APP15)
                    {
                        SkipSegment(jpeg, ref pos);
                        break;
                    }
                    throw new InvalidDataException($"unsupported marker 0xFF 0x{marker:X2}");
            }
        }

        throw new InvalidDataException("no EOI marker before end of input");
    }

    private static LosslessJpegResult BuildResult(FrameHeader? frame, ushort[]? output)
    {
        if (frame is null)
            throw new InvalidDataException("EOI without SOF3");
        if (output is null)
            throw new InvalidDataException("EOI without SOS");
        return new LosslessJpegResult(
            Width: frame.Value.Width,
            Height: frame.Value.Height,
            Precision: frame.Value.Precision,
            Components: frame.Value.Components.Length,
            Samples: output);
    }

    // -----------------------------------------------------------------
    // Marker parsers
    // -----------------------------------------------------------------

    private static FrameHeader ParseFrameHeader(ReadOnlySpan<byte> jpeg, ref int pos)
    {
        // SOF3: marker length (2) + P (1) + Y (2) + X (2) + Nf (1) + Nf*(Ci, HiVi, Tq)
        var length = ReadBigEndianUInt16(jpeg, ref pos);
        var precision = jpeg[pos++];
        var height = ReadBigEndianUInt16(jpeg, ref pos);
        var width = ReadBigEndianUInt16(jpeg, ref pos);
        var nf = jpeg[pos++];
        if (precision < 2 || precision > 16)
            throw new InvalidDataException($"unsupported precision {precision} (lossless JPEG: 2..16)");
        if (nf == 0)
            throw new InvalidDataException("Nf must be >= 1");
        if (length != 8 + 3 * nf)
            throw new InvalidDataException("malformed SOF3 segment length");

        var comps = new FrameComponent[nf];
        for (var i = 0; i < nf; i++)
        {
            var ci = jpeg[pos++];
            var hv = jpeg[pos++];
            _ = jpeg[pos++]; // Tq (quant table) — unused in lossless
            var h = (hv >> 4) & 0x0F;
            var v = hv & 0x0F;
            if (h is < 1 or > 4 || v is < 1 or > 4)
                throw new InvalidDataException($"sampling factor {h}x{v} out of range for component {ci}");
            comps[i] = new FrameComponent(ci, h, v);
        }
        return new FrameHeader(precision, height, width, comps);
    }

    private static void ParseHuffmanTables(ReadOnlySpan<byte> jpeg, ref int pos, HuffmanTable?[] tables)
    {
        // DHT: marker length (2) + repeated [TcTh (1) + BITS (16) + HUFFVAL (sum of BITS)]
        var length = ReadBigEndianUInt16(jpeg, ref pos);
        var end = pos + length - 2;

        while (pos < end)
        {
            var tcth = jpeg[pos++];
            var tc = (tcth >> 4) & 0x0F;
            var th = tcth & 0x0F;
            if (tc != 0)
                throw new InvalidDataException("lossless JPEG only uses DC Huffman tables (Tc must be 0)");
            if (th > 3)
                throw new InvalidDataException($"Th index {th} out of range (0..3)");

            var bits = new byte[16];
            for (var i = 0; i < 16; i++) bits[i] = jpeg[pos++];
            var totalCodes = 0;
            for (var i = 0; i < 16; i++) totalCodes += bits[i];
            if (totalCodes > 256)
                throw new InvalidDataException($"DHT total code count {totalCodes} > 256");
            var huffval = new byte[totalCodes];
            for (var i = 0; i < totalCodes; i++) huffval[i] = jpeg[pos++];

            tables[th] = HuffmanTable.Build(bits, huffval);
        }
    }

    private static int ParseRestartInterval(ReadOnlySpan<byte> jpeg, ref int pos)
    {
        var length = ReadBigEndianUInt16(jpeg, ref pos);
        if (length != 4)
            throw new InvalidDataException($"DRI segment must be length 4, got {length}");
        return ReadBigEndianUInt16(jpeg, ref pos);
    }

    private static ScanHeader ParseScanHeader(ReadOnlySpan<byte> jpeg, ref int pos, FrameHeader frame)
    {
        // SOS: length (2) + Ns (1) + Ns*(Cs, Td/Ta) + Ss (1) + Se (1) + Ah/Al (1)
        var length = ReadBigEndianUInt16(jpeg, ref pos);
        var ns = jpeg[pos++];
        if (ns == 0 || ns > frame.Components.Length)
            throw new InvalidDataException($"SOS Ns={ns} out of range");
        if (length != 6 + 2 * ns)
            throw new InvalidDataException("malformed SOS segment length");

        var comps = new ScanComponent[ns];
        for (var i = 0; i < ns; i++)
        {
            var cs = jpeg[pos++];
            var tdta = jpeg[pos++];
            var td = (tdta >> 4) & 0x0F;
            // Ta (AC table index) is part of the byte but irrelevant for lossless.
            var frameIdx = -1;
            for (var j = 0; j < frame.Components.Length; j++)
            {
                if (frame.Components[j].Id == cs) { frameIdx = j; break; }
            }
            if (frameIdx < 0)
                throw new InvalidDataException($"scan component id {cs} not present in frame");
            comps[i] = new ScanComponent(frameIdx, td);
        }

        var ss = jpeg[pos++];           // predictor selection (1..7 for lossless)
        var se = jpeg[pos++];           // unused — must be 0
        var ahal = jpeg[pos++];
        var pt = ahal & 0x0F;           // point transform (Al)
        if (ss < 1 || ss > 7)
            throw new InvalidDataException($"lossless SOS Ss must be 1..7, got {ss}");
        if (se != 0)
            throw new InvalidDataException($"lossless SOS Se must be 0, got {se}");
        return new ScanHeader(comps, ss, pt);
    }

    private static void SkipSegment(ReadOnlySpan<byte> jpeg, ref int pos)
    {
        var length = ReadBigEndianUInt16(jpeg, ref pos);
        pos += length - 2;
    }

    // -----------------------------------------------------------------
    // Scan body (entropy-coded segment)
    // -----------------------------------------------------------------

    private static void DecodeScan(
        ReadOnlySpan<byte> jpeg,
        ref int pos,
        FrameHeader frame,
        ScanHeader scan,
        HuffmanTable?[] huffTables,
        int restartInterval,
        ushort[] output)
    {
        // For each component in scan order, allocate a "previous-row" sample
        // buffer (Rb / Rc neighbours come from here).
        var ns = scan.Components.Length;
        var width = frame.Width;
        var height = frame.Height;
        var totalComponents = frame.Components.Length;
        var defaultPred = 1 << (frame.Precision - scan.PointTransform - 1);
        var sampleMask = (1 << frame.Precision) - 1;

        // Decoded sample storage layout (interleaved): output[(row*width + col)*totalComponents + ci].
        // Sample reconstruction uses neighbours within the same component.
        // prevRow[scanIdx][col] = sample directly above the current (row, col, component).
        var prevRow = new int[ns][];
        for (var i = 0; i < ns; i++) prevRow[i] = new int[width];

        var br = new BitReader(jpeg, pos);
        int mcusUntilRestart = restartInterval; // 0 means "no restart"
        // Per T.81 H.1.2.1: the first sample of EACH component in the scan
        // uses the default predictor (1 << (P - Pt - 1)) — not just the
        // first sample of the first component. Track per-component so all
        // chains start from defaultPred. Same semantics applies after every
        // restart marker (H.1.2.2). Treating this as a single global flag
        // (the prior implementation) only seeded comp 0's chain correctly;
        // comp 1+ would fall through to prevRow[i][0] = 0 on their first
        // sample, encoding all subsequent values offset by -defaultPred.
        Span<bool> firstSampleOfComponent = ns <= 16 ? stackalloc bool[ns] : new bool[ns];
        firstSampleOfComponent.Fill(true);

        // Pull per-scan Huffman table references once.
        var dcTables = new HuffmanTable[ns];
        for (var i = 0; i < ns; i++)
        {
            var t = huffTables[scan.Components[i].DcTableIndex];
            if (t is null)
                throw new InvalidDataException(
                    $"scan references undefined DC Huffman table {scan.Components[i].DcTableIndex}");
            dcTables[i] = t;
        }

        // Carry "left neighbour" per scan-component across each row (reset
        // implicitly at the start of every row since col=0 doesn't read from
        // leftNeighbour). Hoisted out of the loop to satisfy CA2014.
        Span<int> leftNeighbour = ns <= 16 ? stackalloc int[ns] : new int[ns];

        // The interleaved MCU loop. For non-interleaved (Ns=1) the MCU is one
        // sample wide; for interleaved (Ns=Nf) it's one sample per component.
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                for (var i = 0; i < ns; i++)
                {
                    int predictor;
                    if (firstSampleOfComponent[i])
                    {
                        // T.81 H.1.2.1: first sample of THIS component (scan start or
                        // after restart) uses the default predictor.
                        predictor = defaultPred;
                        firstSampleOfComponent[i] = false;
                    }
                    else if (col == 0)
                    {
                        // First sample of a new row: predictor = Rb (sample directly above).
                        predictor = prevRow[i][0];
                    }
                    else if (row == 0)
                    {
                        // First row, not first column: only Ra is valid → use predictor 1.
                        predictor = leftNeighbour[i];
                    }
                    else
                    {
                        var ra = leftNeighbour[i];
                        var rb = prevRow[i][col];
                        var rc = prevRow[i][col - 1];
                        predictor = scan.PredictorSelection switch
                        {
                            1 => ra,
                            2 => rb,
                            3 => rc,
                            4 => ra + rb - rc,
                            5 => ra + ((rb - rc) >> 1),
                            6 => rb + ((ra - rc) >> 1),
                            7 => (ra + rb) >> 1,
                            _ => throw new InvalidOperationException("invalid predictor"),
                        };
                    }

                    var diff = DecodeDifference(ref br, dcTables[i]);
                    var sample = (predictor + diff) & sampleMask;

                    // Store in output buffer (interleaved per frame's component order).
                    // ScanComponent.FrameIndex maps scan-position → frame-component position.
                    var frameIdx = scan.Components[i].FrameIndex;
                    output[(row * width + col) * totalComponents + frameIdx] = (ushort)(sample << scan.PointTransform);

                    // Update neighbour caches with the *unshifted* prediction-domain value
                    // so subsequent predictors see the original codomain (T.81 H.1).
                    leftNeighbour[i] = sample;
                    prevRow[i][col] = sample;
                }

                if (restartInterval > 0)
                {
                    mcusUntilRestart--;
                    if (mcusUntilRestart == 0)
                    {
                        // T.81 H.1.2.2: at a restart, only the predictor state
                        // resets to the default value. The previous-row sample
                        // buffer stays valid — restart markers don't invalidate
                        // already-decoded neighbours.
                        br.ExpectRestartMarker();
                        mcusUntilRestart = restartInterval;
                        firstSampleOfComponent.Fill(true);
                    }
                }
            }
        }

        pos = br.Position;
    }

    /// <summary>
    /// Decode one lossless-mode difference value: read a Huffman symbol Ssss
    /// (the magnitude category), then read Ssss raw bits and apply the
    /// signed-magnitude expansion per T.81 F.1.2.1.1 / H.1.2.2.
    /// </summary>
    private static int DecodeDifference(ref BitReader br, HuffmanTable table)
    {
        var ssss = table.DecodeSymbol(ref br);
        if (ssss == 0) return 0;
        if (ssss == 16)
        {
            // T.81 H.1.2.2 special case: Ssss=16 → diff = 32768.
            return 32768;
        }
        if (ssss > 16)
            throw new InvalidDataException($"Huffman-decoded magnitude category {ssss} > 16");

        var raw = br.ReadBits(ssss);
        // If high bit of raw is 0 the difference is negative: extend with the
        // (1 << ssss) - 1 mask in T.81 Figure F.12 (= -((1<<ssss)-1) + raw).
        if ((raw & (1 << (ssss - 1))) == 0)
            raw -= (1 << ssss) - 1;
        return raw;
    }

    private static int ReadBigEndianUInt16(ReadOnlySpan<byte> data, ref int pos)
    {
        var v = (data[pos] << 8) | data[pos + 1];
        pos += 2;
        return v;
    }

    // -----------------------------------------------------------------
    // Internal frame / scan / Huffman record types
    // -----------------------------------------------------------------

    private readonly record struct FrameComponent(int Id, int H, int V);

    private readonly record struct FrameHeader(int Precision, int Height, int Width, FrameComponent[] Components);

    private readonly record struct ScanComponent(int FrameIndex, int DcTableIndex);

    private readonly record struct ScanHeader(ScanComponent[] Components, int PredictorSelection, int PointTransform);

    /// <summary>
    /// Canonical JPEG Huffman table built from BITS (16 bytes — count of
    /// codes of each length) + HUFFVAL (the symbols in canonical order).
    ///
    /// <para>Decode is a two-tier dispatch:</para>
    /// <list type="bullet">
    /// <item><b>Fast path</b>: peek 8 bits, look up
    /// <see cref="_fastTable"/>. Each entry packs <c>(codeLength &lt;&lt; 8) |
    /// symbol</c> for codes of length 1..8; 0 means "code longer than 8
    /// bits, use the slow path". Every symbol with an &lt;=8-bit code
    /// resolves in one table read + one bit-buffer advance.</item>
    /// <item><b>Slow path</b>: the classic T.81 F.2.2.3 walk for codes
    /// longer than 8 bits. Reuses the existing
    /// <see cref="_mincode"/>/<see cref="_maxcode"/>/<see cref="_valPtr"/>
    /// arrays starting at length 9.</item>
    /// </list>
    ///
    /// <para>Canon CR2 raw IFD codes cluster heavily in 3-8 bits, so the
    /// fast path lands almost every symbol — a 2-3x speedup over the
    /// bit-at-a-time walk on the multi-megapixel raw decode.</para>
    /// </summary>
    private sealed class HuffmanTable
    {
        private readonly int[] _mincode = new int[17];
        private readonly int[] _maxcode = new int[17];
        private readonly int[] _valPtr  = new int[17];
        private readonly byte[] _huffval;
        // 256-entry lookup keyed by an 8-bit MSB-aligned window:
        //   entry == 0           -> miss, walk the slow path
        //   entry != 0           -> (entry >> 8) is codeLength in 1..8,
        //                           (entry & 0xFF) is the decoded symbol
        // Built once per table; queried per symbol.
        private readonly int[] _fastTable = new int[256];

        private HuffmanTable(byte[] huffval) => _huffval = huffval;

        public static HuffmanTable Build(byte[] bits, byte[] huffval)
        {
            var t = new HuffmanTable(huffval);

            // T.81 Annex C: generate canonical Huffman codes from BITS.
            Span<int> huffsize = stackalloc int[257];
            Span<int> huffcode = stackalloc int[257];
            var p = 0;
            for (var l = 1; l <= 16; l++)
            {
                for (var i = 1; i <= bits[l - 1]; i++)
                {
                    huffsize[p++] = l;
                }
            }
            huffsize[p] = 0;
            var lastp = p;

            var code = 0;
            var si = huffsize[0];
            p = 0;
            while (huffsize[p] != 0)
            {
                while (huffsize[p] == si)
                {
                    huffcode[p++] = code++;
                }
                if (huffsize[p] == 0) break;
                do
                {
                    code <<= 1;
                    si++;
                } while (huffsize[p] != si);
            }

            // Build mincode / maxcode / valPtr (F.2.2.3).
            p = 0;
            for (var l = 1; l <= 16; l++)
            {
                if (bits[l - 1] == 0)
                {
                    t._maxcode[l] = -1;
                }
                else
                {
                    t._valPtr[l] = p;
                    t._mincode[l] = huffcode[p];
                    p += bits[l - 1];
                    t._maxcode[l] = huffcode[p - 1];
                }
            }
            t._maxcode[0] = -1; // sentinel; unused

            // Populate the 8-bit fast-lookup table. For each canonical
            // code of length L<=8, fill all 2^(8-L) entries whose MSB
            // bits match the code — the trailing bits are "don't care"
            // and will be discarded when the caller consumes only L bits.
            p = 0;
            for (var l = 1; l <= 8; l++)
            {
                var entriesAtThisLength = bits[l - 1];
                var shift = 8 - l;
                var span = 1 << shift;
                for (var i = 0; i < entriesAtThisLength; i++, p++)
                {
                    var entry = (l << 8) | huffval[p];
                    var prefix = huffcode[p] << shift;
                    for (var s = 0; s < span; s++)
                    {
                        t._fastTable[prefix | s] = entry;
                    }
                }
            }
            _ = lastp;
            return t;
        }

        public int DecodeSymbol(ref BitReader br)
        {
            // Fast path: peek an 8-bit window, table lookup. Codes 1..8
            // bits long resolve here in one read + one advance. For the
            // canonical CR2 raw IFD Huffman tables this catches >99% of
            // symbols (the only longer codes are the rarest high-magnitude
            // SSSS categories), making this loop's body branch-free except
            // for the entry-zero miss test.
            var peek = (int)br.PeekBits(8);
            var entry = _fastTable[peek];
            if (entry != 0)
            {
                br.Consume(entry >> 8);
                return entry & 0xFF;
            }
            // Slow path: code is >8 bits. Consume the 8 peek bits we
            // already have, then continue the classic F.2.2.3 walk one
            // bit at a time. mincode/maxcode entries for l<=8 are
            // unused on this path; we start at l=9.
            br.Consume(8);
            var code = peek;
            var l = 9;
            code = (code << 1) | br.ReadBit();
            while (code > _maxcode[l])
            {
                code = (code << 1) | br.ReadBit();
                l++;
                if (l > 16)
                    throw new InvalidDataException("Huffman decode ran past 16-bit limit");
            }
            var j = _valPtr[l] + (code - _mincode[l]);
            return _huffval[j];
        }
    }

    /// <summary>
    /// MSB-first JPEG bit reader. Reads one byte at a time from the input
    /// span; on a <c>0xFF</c> byte the next is peeked: <c>0x00</c> is
    /// byte-stuffing (emit literal 0xFF), <c>0xD0..0xD7</c> is an in-band
    /// restart marker (consumed by <see cref="ExpectRestartMarker"/>), and
    /// any other follow-on byte ends the entropy segment.
    ///
    /// When a marker is encountered or input is exhausted, <see cref="FillBuffer"/>
    /// stuffs zero bits into the buffer rather than throwing — the Huffman
    /// decoder is allowed to *speculatively* over-read by up to 8 bits past
    /// the actual symbol boundary (the over-reads are discarded once the
    /// symbol length is known), and the natural end-of-scan ought not to be
    /// reported as an error. The outer scan loop knows when it's finished
    /// (it counted MCUs); the parser resumes from <see cref="Position"/>
    /// which rewinds to the start of the parked marker.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private uint _bitBuf;
        private int _bitCount;
        private bool _markerSeen;
        private byte _pendingMarker;

        public BitReader(ReadOnlySpan<byte> data, int startPos)
        {
            _data = data;
            _pos = startPos;
            _bitBuf = 0;
            _bitCount = 0;
            _markerSeen = false;
            _pendingMarker = 0;
        }

        /// <summary>
        /// Position the outer parser should resume from. If a marker was
        /// parked mid-scan, this rewinds to the start of that marker (the
        /// FF byte) so the outer marker dispatch sees it.
        /// </summary>
        public int Position => _markerSeen ? _pos - 2 : _pos;

        public int ReadBit()
        {
            if (_bitCount == 0) FillBuffer();
            _bitCount--;
            return (int)((_bitBuf >> _bitCount) & 1u);
        }

        public int ReadBits(int n)
        {
            while (_bitCount < n) FillBuffer();
            _bitCount -= n;
            return (int)((_bitBuf >> _bitCount) & ((1u << n) - 1u));
        }

        /// <summary>Read <paramref name="n"/> bits WITHOUT consuming them.
        /// Used by the Huffman fast-path: peek 8 bits, look up the
        /// (symbol, codeLength) entry, then <see cref="Consume"/> exactly
        /// codeLength bits — avoiding the per-bit advance cost of the
        /// canonical walk.</summary>
        public uint PeekBits(int n)
        {
            while (_bitCount < n) FillBuffer();
            return (uint)((_bitBuf >> (_bitCount - n)) & ((1u << n) - 1u));
        }

        /// <summary>Advance the bit buffer by <paramref name="n"/> bits
        /// without reading. Caller is responsible for having already
        /// peeked or otherwise ensured at least <paramref name="n"/>
        /// bits are buffered — pair with <see cref="PeekBits"/>.</summary>
        public void Consume(int n) => _bitCount -= n;

        /// <summary>
        /// Consume an in-band RSTn marker (0xFF 0xD0..0xD7) at the current
        /// position and reset the bit buffer. Any leftover partial-byte
        /// bits are discarded per T.81 F.1.4. The marker may already have
        /// been parked by speculative read-ahead in <see cref="FillBuffer"/>.
        /// </summary>
        public void ExpectRestartMarker()
        {
            _bitBuf = 0;
            _bitCount = 0;
            if (_markerSeen)
            {
                if (_pendingMarker >= LosslessJpegDecoder.RST0
                    && _pendingMarker <= LosslessJpegDecoder.RST7)
                {
                    _markerSeen = false;
                    _pendingMarker = 0;
                    return;
                }
                throw new InvalidDataException(
                    $"expected RSTn marker but saw 0xFF 0x{_pendingMarker:X2}");
            }

            // Marker not yet parked — read it directly. Skip any fill 0xFFs.
            while (_pos < _data.Length && _data[_pos] == 0xFF) _pos++;
            if (_pos >= _data.Length)
                throw new InvalidDataException("unexpected end of input while reading RSTn marker");
            // We've already advanced past the FF run; the previous byte was FF.
            // Now read the marker code byte.
            var code = _data[_pos++];
            if (code < LosslessJpegDecoder.RST0 || code > LosslessJpegDecoder.RST7)
                throw new InvalidDataException(
                    $"expected RSTn marker but saw 0xFF 0x{code:X2}");
        }

        private void FillBuffer()
        {
            if (_markerSeen || _pos >= _data.Length)
            {
                // Past end of entropy segment — pad with zero bits so the
                // Huffman decoder's speculative over-reads don't crash.
                // The caller's MCU counter is the authoritative end-of-scan
                // signal, not the byte-stream.
                _bitBuf <<= 8;
                _bitCount += 8;
                return;
            }

            var b = _data[_pos++];
            if (b == LosslessJpegDecoder.MarkerEscape)
            {
                if (_pos >= _data.Length)
                    throw new InvalidDataException("unexpected end after 0xFF in entropy data");
                var next = _data[_pos++];
                if (next == 0x00)
                {
                    // Byte stuffing — emit literal 0xFF.
                    b = 0xFF;
                }
                else
                {
                    // Real marker — park it, pad with zero bits, return.
                    _markerSeen = true;
                    _pendingMarker = next;
                    _bitBuf <<= 8;
                    _bitCount += 8;
                    return;
                }
            }
            _bitBuf = (_bitBuf << 8) | b;
            _bitCount += 8;
        }
    }
}
