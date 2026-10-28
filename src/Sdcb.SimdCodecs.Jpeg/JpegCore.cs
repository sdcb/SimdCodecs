using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The decoder engine: marker parsing, Huffman/entropy decode (baseline +
/// progressive), restart intervals, and an MCU-row pipeline into packed RGBA.
/// Baseline frames whose first scan covers every component stream: each MCU row
/// is entropy-decoded and inverse-transformed into a two-MCU-row strip per
/// component, then every output row it completes is upsampled and colour-converted
/// immediately, so no full-resolution sample plane ever exists. Progressive frames
/// and baseline frames split over several scans keep their coefficients and run the
/// same row pipeline once the scans are in. Marker parsing and entropy decoding
/// started as a port of stb_image's (public-domain) JPEG decoder and are checked
/// against libjpeg-turbo coefficient for coefficient by the benchmark's accuracy
/// tooling. The sample arithmetic — IDCT (<see cref="JpegIdct"/>), upsampling
/// (<see cref="JpegResample"/>) and colour conversion (<see cref="JpegColor"/>) —
/// is this library's own, defined from T.81 / T.871 and pinned by the committed
/// golden digests (<c>Fixtures/jpeg-oracle-golden.tsv</c>, driven by
/// <c>JpegDecoderOracleTests</c>). Notes on the stb lineage:
///
/// <list type="bullet">
/// <item>Strips, coefficient buffers, and line buffers are rented from
/// <see cref="ArrayPool{T}"/> and zeroed, so padding no block covers is
/// deterministic. Data that never arrives (a scan cut short, a component no scan
/// delivered, a progressive frame abandoned before EOI) decodes as zero samples,
/// exactly as stb's zeroed planes would.</item>
/// <item>A scale denominator of 2/4/8 swaps the 8×8 IDCT for the reduced kernel,
/// shrinking strips and output by the square of the factor (the entropy layout —
/// MCU geometry, block counts, coefficient buffers — stays full-resolution, as
/// the bitstream dictates).</item>
/// <item>APP0 JFIF detection follows upstream stb C (<c>"JFIF\0"</c>); the
/// machine-transpiled port reuses the Adobe tag there by mistake, which only
/// diverges for files carrying both JFIF and an Adobe transform-0 marker.</item>
/// <item>Errors throw <see cref="InvalidDataException"/> instead of returning
/// null — except unknown/corrupt markers after the first scan, where stb (and we)
/// salvage what has been decoded. A streamed (single-scan) baseline frame that
/// illegally sends a component again in a later scan keeps its first scan's
/// samples; the later scan is entropy-decoded and dropped.</item>
/// </list>
/// </summary>
internal ref struct JpegCore
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    // --- entropy decoding: marker hit inside entropy data (see the bit reader) ---
    private byte _marker = 0xff;
    private bool _nomore;

    // --- frame state ---
    internal int ImgX;
    internal int ImgY;
    internal int ImgN;
    private int _imgHMax, _imgVMax;
    private int _imgMcuX, _imgMcuY, _imgMcuW, _imgMcuH;
    private bool _progressive;
    private bool _jfif;
    private int _app14ColorTransform = -1;
    private ImageOrientation _orientation = ImageOrientation.Normal; // from the first APP1 Exif segment
    private bool _exifSeen;
    private int _rgbCount;
    private int _restartInterval;
    private int _todo;
    private int _eobRun;

    // --- current scan ---
    private int _scanN;
    private readonly int[] _order;
    private int _specStart, _specEnd, _succHigh, _succLow;

    // --- tables (from a per-thread cached DecoderState, reset to "fresh" per decode) ---
    private readonly DecoderState _state;
    private readonly JpegHuffman[] _huffDc;
    private readonly JpegHuffman[] _huffAc;
    private readonly ushort[][] _dequant;
    private readonly Component[] _comp;

    // --- scaled decode ---
    private readonly int _scale;     // 1, 2, 4, 8
    private readonly int _blockSize; // 8 / _scale — IDCT output tile edge

    internal int OutX { get; private set; }
    internal int OutY { get; private set; }

    /// <summary>
    /// Measurement-only tap (see <see cref="JpegDiagnostics"/>): when set, sees every
    /// block's dequantized coefficients in natural order just before its IDCT.
    /// </summary>
    internal JpegBlockHook? BlockHook;

    /// <summary>Measurement-only stage switches (see <see cref="JpegAblation"/>).</summary>
    internal JpegAblation Ablation;

    public JpegCore(ReadOnlySpan<byte> data, int scale)
    {
        _data = data;
        _scale = scale;
        _blockSize = 8 / scale;
        _state = DecoderState.Rent();
        _order = _state.Order;
        _huffDc = _state.HuffDc;
        _huffAc = _state.HuffAc;
        _dequant = _state.Dequant;
        _comp = _state.Comp;
    }

    /// <summary>
    /// The decoder's heap-allocated working set (Huffman and quantization tables,
    /// component records), cached per thread so a decode allocates nothing on the
    /// managed heap. Every rent resets it to exactly the state of freshly constructed
    /// objects, so nothing — a table, a predictor, a pooled-buffer reference — can leak
    /// from one image into the next.
    /// </summary>
    private sealed class DecoderState
    {
        public readonly int[] Order = new int[4];
        public readonly JpegHuffman[] HuffDc = { new(), new(), new(), new() };
        public readonly JpegHuffman[] HuffAc = { new(), new(), new(), new() };
        public readonly ushort[][] Dequant = { new ushort[64], new ushort[64], new ushort[64], new ushort[64] };
        public readonly Component[] Comp = { new(), new(), new(), new() };

        [ThreadStatic]
        private static DecoderState? t_cached;

        public static DecoderState Rent()
        {
            var s = t_cached;
            if (s == null)
                return new DecoderState();
            t_cached = null; // a nested decode on this thread gets its own
            Array.Clear(s.Order);
            for (var i = 0; i < 4; ++i)
            {
                s.HuffDc[i].Reset();
                s.HuffAc[i].Reset();
                Array.Clear(s.Dequant[i]);
                s.Comp[i].Reset();
            }

            return s;
        }

        public static void Return(DecoderState s) => t_cached = s;
    }

    private sealed class Component
    {
        public int Id, H, V, Tq, Hd, Ha;
        public JpegHuffman? DcTable, AcTable; // the current scan's tables
        public ushort[]? Dequant;
        /// <summary>Per zig-zag index k (padded to 79 like <see cref="Dezigzag"/>): Dezigzag[k] &lt;&lt; 16 | Dequant[Dezigzag[k]].</summary>
        public readonly int[] ZigQ = new int[64 + 15];
        public int DcPred;
        public int X, Y;             // full-resolution sample dimensions
        public int W2, H2;           // scaled plane stride / full plane height
        public byte[]? Ring;         // pooled sample strip: two MCU rows (RingRows × W2)
        public int RowsPerMcu;       // sample rows one MCU row contributes (V × block size)
        public int RingRows;         // 2 × RowsPerMcu
        public int RowsAvail;        // plane rows decoded so far (rows below are final)
        public byte[]? Data;         // full plane (W2 × H2), diagnostics capture only
        public short[]? Coeff;       // pooled coefficient store (coefficient mode only)
        public byte[]? Written;      // per block: decoded at least once (baseline coefficient mode)
        public int CoeffW, CoeffH;   // in 8×8 blocks (full resolution)
        public byte[]? LineBuf;      // pooled upsample row buffer
        public ushort[]? LineBuf16;  // pooled 16×-scale upsample row buffer

        // per-decode resample state (mirrors stb's stbi__resample)
        public int Hs, Vs, YStep, YPos, WLores, YsScaled;
        public int Line0, Line1;     // plane row indices (Line1 = Line0 or Line0 + 1)
        public int ResampleKind;
        public byte[]? RowArr;       // current output-resolution row (array, offset)
        public int RowOff;

        /// <summary>Back to the state of a new instance (JpegDecoderOracleTests checks every field).</summary>
        public void Reset()
        {
            Id = H = V = Tq = Hd = Ha = 0;
            DcTable = AcTable = null;
            Dequant = null;
            Array.Clear(ZigQ);
            DcPred = 0;
            X = Y = W2 = H2 = 0;
            Ring = null;
            RowsPerMcu = RingRows = RowsAvail = 0;
            Data = null;
            Coeff = null;
            Written = null;
            CoeffW = CoeffH = 0;
            LineBuf = null;
            LineBuf16 = null;
            Hs = Vs = YStep = YPos = WLores = YsScaled = 0;
            Line0 = Line1 = 0;
            ResampleKind = 0;
            RowArr = null;
            RowOff = 0;
        }
    }

    private enum ScanMode
    {
        Load,
        Header,
    }

    /// <summary>
    /// How samples reach the output. <see cref="Streaming"/>: the first scan is
    /// interleaved over every component (every baseline file in practice), so each
    /// MCU row goes entropy → IDCT → strip → RGBA rows before the next is decoded.
    /// <see cref="Coefficients"/>: progressive frames, and baseline frames split
    /// over several scans, keep every block's coefficients and run IDCT + output
    /// MCU row by MCU row once the scans are in.
    /// </summary>
    private enum DecodeMode
    {
        Undecided,
        Streaming,
        Coefficients,
    }

    private DecodeMode _mode;
    private bool _streamDone;
    private Span<byte> _dest;
    private int _stride;
    private JpegPixelFormat _format;
    private byte[]? _packScratch;     // one RGBA row, for the 3- and 1-byte formats
    private bool _lumaOnly;           // Gray8 from a YCbCr frame: Y is the output, chroma is skipped
    private int _outRow;
    private bool _isRgb;
    private bool _chroma16;

    /// <summary>Diagnostics: also keep full component planes for <see cref="ExportPlanes"/>.</summary>
    internal bool CapturePlanes;

    // ============================== public surface ==============================

    /// <summary>Parses SOI → SOF only; allocates nothing, decodes no entropy data.</summary>
    public JpegInfo ParseInfo()
    {
        DecodeHeader(ScanMode.Header);

        // Header-scan mode returns before the frame header computes the sampling
        // maxima, so derive subsampling directly from the component factors here.
        var hMax = 1;
        var vMax = 1;
        for (var i = 0; i < ImgN; ++i)
        {
            hMax = Math.Max(hMax, _comp[i].H);
            vMax = Math.Max(vMax, _comp[i].V);
        }

        var subsampled = false;
        for (var i = 0; i < ImgN; ++i)
        {
            if (_comp[i].H != hMax || _comp[i].V != vMax)
                subsampled = true;
        }

        return new JpegInfo(ImgX, ImgY, ImgN, _progressive) { ChromaSubsampled = subsampled, Orientation = _orientation };
    }

    /// <summary>
    /// Parses SOI → SOF, sizes the output and rents the per-component strips. No
    /// entropy data is decoded until <see cref="DecodeInto"/>.
    /// </summary>
    public void Begin()
    {
        _restartInterval = 0;
        DecodeHeader(ScanMode.Load);
        OutX = (ImgX + _scale - 1) / _scale;
        OutY = (ImgY + _scale - 1) / _scale;
        if ((long)OutX * OutY * 4 > int.MaxValue)
            throw Err("too large");
    }

    /// <summary>
    /// After <see cref="Begin"/>: decodes every scan, writing rows of
    /// <see cref="OutX"/> pixels in <paramref name="format"/>, <paramref name="stride"/>
    /// bytes apart, into <paramref name="dest"/> as soon as the samples they need are final.
    /// </summary>
    public void DecodeInto(Span<byte> dest, int stride, JpegPixelFormat format = JpegPixelFormat.Rgba32)
    {
        _dest = dest;
        _stride = stride;
        _format = format;
        _outRow = 0;
        _mode = _progressive ? DecodeMode.Coefficients : DecodeMode.Undecided;
        _streamDone = false;
        PrepareOutput();
        // Grey from a YCbCr frame is its Y plane (see JpegPixelFormat.Gray8): the chroma blocks are still
        // entropy-decoded, as the scans interleave them, but never inverse-transformed, upsampled or
        // colour-converted. RGB-coded, CMYK and YCCK frames have no luma plane; their grey is the luma
        // of the RGB row.
        _lumaOnly = format == JpegPixelFormat.Gray8 && ImgN == 3 && !_isRgb;
        if (format is JpegPixelFormat.Rgb24 or JpegPixelFormat.Bgr24 || (format == JpegPixelFormat.Gray8 && ImgN != 1 && !_lumaOnly))
            _packScratch = ArrayPool<byte>.Shared.Rent(OutX * 4);

        var reachedEoi = DecodeScans();

        if (_mode == DecodeMode.Coefficients && (reachedEoi || !_progressive))
        {
            // Progressive data is only reconstructed once EOI is reached (a frame
            // abandoned mid-way stays zero samples, as stb does); split baseline
            // frames emit whatever blocks their scans delivered.
            EmitCoefficients();
        }
        else if (_mode != DecodeMode.Streaming || !_streamDone)
        {
            ZeroFillFrom(0);
        }
    }

    /// <summary>
    /// After <see cref="DecodeInto"/> with <see cref="CapturePlanes"/> set: copies
    /// each component's decoded sample plane (post-IDCT, pre-upsampling, at the
    /// component's own resolution) plus the quantization table it was dequantized with.
    /// </summary>
    internal JpegComponentPlane[] ExportPlanes()
    {
        var planes = new JpegComponentPlane[ImgN];
        for (var i = 0; i < ImgN; ++i)
        {
            var c = _comp[i];
            var w = (c.X + _scale - 1) / _scale;
            var h = (c.Y + _scale - 1) / _scale;
            var samples = new byte[w * h];
            for (var y = 0; y < h; ++y)
                c.Data.AsSpan(y * c.W2, w).CopyTo(samples.AsSpan(y * w, w));
            planes[i] = new JpegComponentPlane(c.Id, c.H, c.V, w, h, (ushort[])_dequant[c.Tq].Clone(), samples);
        }

        return planes;
    }

    // ============================== MCU-row output ==============================

    /// <summary>Sets up upsampling state and line buffers before the first scan.</summary>
    private void PrepareOutput()
    {
        _isRgb = ImgN == 3 && (_rgbCount == 3 || (_app14ColorTransform == 0 && !_jfif));

        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            c.LineBuf = ArrayPool<byte>.Shared.Rent(OutX + 3);
            c.LineBuf16 = ArrayPool<ushort>.Shared.Rent(OutX + 3);
            c.Hs = _imgHMax / c.H;
            c.Vs = _imgVMax / c.V;
            c.YStep = c.Vs >> 1;
            c.WLores = (OutX + c.Hs - 1) / c.Hs;
            c.YPos = 0;
            c.Line0 = c.Line1 = 0;
            c.RowsAvail = 0;
            c.YsScaled = (c.Y + _scale - 1) / _scale;
            c.ResampleKind =
                c.Hs == 1 && c.Vs == 1 ? JpegResample.OneToOne :
                c.Hs == 1 && c.Vs == 2 ? JpegResample.V2 :
                c.Hs == 2 && c.Vs == 1 ? JpegResample.H2 :
                c.Hs == 2 && c.Vs == 2 ? JpegResample.Hv2 :
                JpegResample.Generic;
        }

        // Components 1 and 2 are YCbCr chroma for 3-component YCbCr and for every
        // 4-component stream except plain Adobe CMYK. When either is subsampled, both
        // are upsampled at 16× scale and fed to the converter unrounded.
        var ycc = (ImgN == 3 && !_isRgb) || (ImgN == 4 && _app14ColorTransform != 0);
        _chroma16 = ycc &&
                    (_comp[1].ResampleKind != JpegResample.OneToOne || _comp[2].ResampleKind != JpegResample.OneToOne);
    }

    /// <summary>Strip offset of plane row <paramref name="row"/> of component <paramref name="c"/>.</summary>
    private static int RingOffset(Component c, int row) => row % c.RingRows * c.W2;

    /// <summary>
    /// MCU row <paramref name="m"/> is final in every component's strip: capture it
    /// (diagnostics) and emit every output row whose source rows are now available.
    /// </summary>
    private void CommitMcuRow(int m)
    {
        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            c.RowsAvail = (m + 1) * c.RowsPerMcu;
            if (c.Data != null)
            {
                var off = RingOffset(c, m * c.RowsPerMcu);
                var len = c.RowsPerMcu * c.W2;
                c.Ring.AsSpan(off, len).CopyTo(c.Data.AsSpan(m * len, len));
            }
        }

        if ((Ablation & JpegAblation.SkipBlockSink) == 0)
            EmitRows();
    }

    /// <summary>Emits zero samples for MCU rows <paramref name="m"/>… (data that never arrived).</summary>
    private void ZeroFillFrom(int m)
    {
        for (; m < _imgMcuY; ++m)
        {
            for (var k = 0; k < ImgN; ++k)
            {
                var c = _comp[k];
                c.Ring.AsSpan(RingOffset(c, m * c.RowsPerMcu), c.RowsPerMcu * c.W2).Clear();
            }

            CommitMcuRow(m);
        }
    }

    /// <summary>Zeroes block <paramref name="bx"/>, <paramref name="by"/> of a component's strip.</summary>
    private void ZeroBlock(Component c, int bx, int by)
    {
        var b = _blockSize;
        var off = RingOffset(c, by * b) + bx * b;
        for (var r = 0; r < b; ++r)
            c.Ring.AsSpan(off + r * c.W2, b).Clear();
    }

    private void EmitRows()
    {
        while (_outRow < OutY)
        {
            for (var k = 0; k < ImgN; ++k)
            {
                if (_comp[k].Line1 >= _comp[k].RowsAvail)
                    return;
            }

            EmitRow(_outRow++);
        }
    }

    /// <summary>Upsamples + colour-converts output row <paramref name="j"/> into RGBA.</summary>
    private void EmitRow(int j)
    {
        var skipUpsample = (Ablation & JpegAblation.SkipUpsample) != 0;
        var skipColor = (Ablation & JpegAblation.SkipColor) != 0;
        var chroma16 = _chroma16;

        for (var k = 0; k < ImgN; ++k)
        {
            var c = _comp[k];
            if (_lumaOnly && k > 0)
            {
                AdvanceLine(c);
                continue;
            }

            var yBot = c.YStep >= (c.Vs >> 1);
            var inNear = RingOffset(c, yBot ? c.Line1 : c.Line0);
            var inFar = RingOffset(c, yBot ? c.Line0 : c.Line1);
            if (chroma16 && k is 1 or 2)
            {
                if (!skipUpsample)
                    JpegResample.Row16(c.ResampleKind, c.LineBuf16, c.Ring, inNear, inFar, c.WLores, c.Hs);
            }
            else if (c.ResampleKind == JpegResample.OneToOne)
            {
                c.RowArr = c.Ring;
                c.RowOff = inNear;
            }
            else
            {
                if (!skipUpsample)
                    JpegResample.Row(c.ResampleKind, c.LineBuf, c.LineBuf16, c.Ring, inNear, inFar, c.WLores, c.Hs);
                c.RowArr = c.LineBuf;
                c.RowOff = 0;
            }

            AdvanceLine(c);
        }

        if (skipColor)
            return;

        var y = Row(0);
        if (_format == JpegPixelFormat.Gray8 && (ImgN == 1 || _lumaOnly))
        {
            // A greyscale frame's samples, or a YCbCr frame's Y: the grey output as it is.
            y[..OutX].CopyTo(_dest.Slice(j * _stride, OutX));
            return;
        }

        var bgr = _format is JpegPixelFormat.Bgra32 or JpegPixelFormat.Bgr24;
        var outRow = _packScratch != null ? _packScratch.AsSpan(0, OutX * 4) : _dest.Slice(j * _stride, OutX * 4);

        if (ImgN == 3)
        {
            if (_isRgb)
                JpegColor.RgbRow(outRow, y, Row(1), Row(2), OutX, bgr);
            else
                YccRow(outRow, y, bgr);
        }
        else if (ImgN == 4)
        {
            if (_app14ColorTransform == 0)
            {
                // Plain Adobe CMYK: fold K into each ink channel.
                JpegColor.CmykRow(outRow, y, Row(1), Row(2), Row(3), OutX, bgr);
            }
            else if (_app14ColorTransform == 2)
            {
                // YCCK: YCbCr → RGB, invert to CMY, then fold K.
                if (_chroma16)
                    JpegColor.YcckRow(outRow, y, _comp[1].LineBuf16, _comp[2].LineBuf16, Row(3), OutX, bgr);
                else
                    JpegColor.YcckRow(outRow, y, Row(1), Row(2), Row(3), OutX, bgr);
            }
            else
            {
                // No (or unrecognised) transform tag — treated as YCbCr, K ignored.
                YccRow(outRow, y, bgr);
            }
        }
        else
        {
            JpegColor.GrayRow(outRow, y, OutX);
        }

        if (_packScratch != null)
            PackRow(j, outRow);
    }

    /// <summary>Writes the RGBA (or BGRA) row <paramref name="rgba"/> as the 3- or 1-byte output format.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void PackRow(int j, ReadOnlySpan<byte> rgba)
    {
        if (_format == JpegPixelFormat.Gray8)
            PixelConverter.ConvertRow(rgba, 4, SampleFormat.UInt8, _dest.Slice(j * _stride, OutX), ImagePixelFormat.Gray8, OutX);
        else
            JpegColor.PackRgb24(_dest.Slice(j * _stride, OutX * 3), rgba, OutX);
    }

    private readonly ReadOnlySpan<byte> Row(int k) => _comp[k].RowArr.AsSpan(_comp[k].RowOff);

    /// <summary>Steps component <paramref name="c"/>'s vertical upsampling position past one output row.</summary>
    private static void AdvanceLine(Component c)
    {
        if (++c.YStep >= c.Vs)
        {
            c.YStep = 0;
            c.Line0 = c.Line1;
            if (++c.YPos < c.YsScaled)
                ++c.Line1;
        }
    }

    private readonly void YccRow(Span<byte> outRow, ReadOnlySpan<byte> y, bool bgr)
    {
        if (_chroma16)
            JpegColor.YccRow(outRow, y, _comp[1].LineBuf16, _comp[2].LineBuf16, OutX, bgr);
        else
            JpegColor.YccRow(outRow, y, Row(1), Row(2), OutX, bgr);
    }
    /// <summary>Returns all pooled buffers and the cached decoder state. Call exactly once.</summary>
    public void ReturnBuffers()
    {
        ReturnPools();
        DecoderState.Return(_state);
    }

    private void ReturnPools()
    {
        if (_packScratch != null)
        {
            ArrayPool<byte>.Shared.Return(_packScratch);
            _packScratch = null;
        }

        for (var i = 0; i < 4; ++i)
        {
            var c = _comp[i];
            if (c.Data != null)
            {
                ArrayPool<byte>.Shared.Return(c.Data);
                c.Data = null;
            }

            if (c.Ring != null)
            {
                ArrayPool<byte>.Shared.Return(c.Ring);
                c.Ring = null;
            }

            if (c.Written != null)
            {
                ArrayPool<byte>.Shared.Return(c.Written);
                c.Written = null;
            }

            if (c.Coeff != null)
            {
                ArrayPool<short>.Shared.Return(c.Coeff);
                c.Coeff = null;
            }

            if (c.LineBuf != null)
            {
                ArrayPool<byte>.Shared.Return(c.LineBuf);
                c.LineBuf = null;
            }

            if (c.LineBuf16 != null)
            {
                ArrayPool<ushort>.Shared.Return(c.LineBuf16);
                c.LineBuf16 = null;
            }
        }
    }

    // ============================== byte reader ==============================

    private bool AtEof => _pos >= _data.Length;

    private byte Get8() => _pos < _data.Length ? _data[_pos++] : (byte)0;

    private int Get16()
    {
        int hi = Get8();
        return (hi << 8) | Get8();
    }

    private void Skip(int n) => _pos = Math.Min(_data.Length, _pos + n);

    // ============================== bit reader ==============================
    //
    // A 64-bit, MSB-first bit buffer (T.81 F.2.2.5 / F.2.4): _bitBuf holds _bitCnt
    // valid bits at the top. Refill takes 8 bytes at once when none of them is 0xFF
    // (so no stuffing and no marker can be inside), ORing them in below the valid
    // bits and advancing over the whole bytes that fit; the partial byte left in the
    // low bits is re-ORed, identically, by the next refill. Anything else goes byte
    // by byte: 0xFF00 is a literal 0xFF, 0xFF + anything else is a marker, after which
    // the scan reads zero bits forever (stb's "nomore"), as it does past end of data.

    private const ulong Ones = 0x0101010101010101UL;
    private const ulong Highs = 0x8080808080808080UL;

    /// <summary>Upper bound on blocks in one interleaved MCU: ≤ 4 scan components × H·V ≤ 16 (H, V ≤ 4 are validated).</summary>
    private const int MaxBlocksPerMcu = 4 * 16;

    private ulong _bitBuf;

    /// <summary>The AC table of the block <see cref="DecodeBlock"/> is decoding, for <see cref="AcSlow"/>.</summary>
    private JpegHuffman? _acSlowTable;
    private int _bitCnt;

    private static ReadOnlySpan<byte> Dezigzag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7,
        14, 21, 28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
        // padding so a run can overshoot to k=78 (63 + a 15-zero run) without bounds failure, as in stb
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    ];

    /// <summary>Refills to ≥ 57 valid bits (64 after a marker or past end of data). Locals version for hot loops.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill(ref ulong buf, ref int cnt, ref int pos)
    {
        if (!_nomore && pos <= _data.Length - 8)
        {
            var x = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos)));
            if (((~x - Ones) & x & Highs) == 0) // no 0xFF byte in x
            {
                buf |= x >> cnt;
                pos += (63 - cnt) >> 3;
                cnt |= 56;
                return;
            }
        }

        _bitBuf = buf;
        _bitCnt = cnt;
        _pos = pos;
        RefillSlow();
        buf = _bitBuf;
        cnt = _bitCnt;
        pos = _pos;
    }

    private void Refill()
    {
        var buf = _bitBuf;
        var cnt = _bitCnt;
        var pos = _pos;
        Refill(ref buf, ref cnt, ref pos);
        _bitBuf = buf;
        _bitCnt = cnt;
        _pos = pos;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefillSlow()
    {
        // Drop the partial byte a fast refill may have left below the valid bits.
        // Defensive only: that byte is the (non-0xFF) one at _pos, which the loop
        // below ORs in again at the same position, so clearing it changes nothing.
        if (_bitCnt < 64)
            _bitBuf &= ~(ulong.MaxValue >> _bitCnt);
        if (_nomore)
        {
            _bitCnt = 64;
            return;
        }

        while (_bitCnt <= 56)
        {
            ulong b = Get8();
            if (b == 0xff)
            {
                int c = Get8();
                while (c == 0xff)
                    c = Get8(); // fill bytes (0 at end of data ends the loop)

                if (c != 0)
                {
                    // A real marker terminates entropy data: remember it, read zeros from here on.
                    _marker = (byte)c;
                    _nomore = true;
                    _bitCnt = 64;
                    return;
                }
                // 0xFF00 is a stuffed literal 0xFF byte.
            }

            _bitBuf |= b << (56 - _bitCnt);
            _bitCnt += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Extend(int v, int n) => v + (((v >> (n - 1)) - 1) & ((-1 << n) + 1));

    /// <summary>
    /// Canonical decode for codes the lookup table does not hold (longer than
    /// <see cref="JpegHuffman.LutBits"/>). Returns code length &lt;&lt; 8 | symbol, or −1 for
    /// no code — packed rather than an out parameter, which would pin the caller's
    /// length local to memory on its hot path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int SlowSymbol(JpegHuffman h, ulong buf)
    {
        var temp = (uint)(buf >> 48);
        int k;
        for (k = JpegHuffman.LutBits + 1; k < 17; ++k)
        {
            if (temp < h.MaxCode[k])
                break;
        }

        if (k == 17)
            return -1;
        var c = (int)((buf >> (64 - k)) & ((1UL << k) - 1)) + h.Delta[k];
        if ((uint)c >= 256)
            return -1;
        return k << 8 | h.Values[c];
    }

    /// <summary>Decodes one Huffman symbol from the field state; −1 for no code.</summary>
    private int HuffDecode(JpegHuffman h)
    {
        if (_bitCnt < 16)
            Refill();
        var e = h.Lut[(int)(_bitBuf >> (64 - JpegHuffman.LutBits))];
        int len, sym;
        if (e != 0)
        {
            len = (e >> JpegHuffman.CodeLenShift) & 15;
            sym = (e >> 11) & 255;
        }
        else
        {
            var packed = SlowSymbol(h, _bitBuf);
            if (packed < 0)
                return -1;
            len = packed >> 8;
            sym = packed & 255;
        }

        _bitBuf <<= len;
        _bitCnt -= len;
        return sym;
    }

    /// <summary>Receives n (1…15) magnitude bits and sign-extends per the JPEG EXTEND procedure.</summary>
    private int ExtendReceive(int n) => Extend(GetBits(n), n);

    /// <summary>Reads n (1…16) raw bits.</summary>
    private int GetBits(int n)
    {
        if (_bitCnt < n)
            Refill();
        var v = (int)(_bitBuf >> (64 - n));
        _bitBuf <<= n;
        _bitCnt -= n;
        return v;
    }

    private bool GetBit()
    {
        if (_bitCnt < 1)
            Refill();
        var bit = (long)_bitBuf < 0;
        _bitBuf <<= 1;
        --_bitCnt;
        return bit;
    }

    // ============================== block decode ==============================

    [DoesNotReturn]
    private static void ThrowBadCode() => throw Err("bad huffman code");

    /// <summary>
    /// Baseline block: DC difference + AC run/values, dequantized into natural order.
    /// The bit state lives in locals for the whole block; codes the lookup table cannot
    /// resolve together with their magnitude bits go through <see cref="DcSlow"/> /
    /// <see cref="AcSlow"/>, which work on the field state, so no local is ever
    /// address-exposed. Returns an upper bound on the zig-zag index of the last
    /// coefficient written: exact (0 when only DC is present) unless zero-run (ZRL)
    /// symbols follow the last coefficient, which only makes the IDCT take a fuller —
    /// equally exact — path.
    /// </summary>
    private int DecodeBlock(scoped Span<short> data, Component c)
    {
        if (data.Length < 64)
            ThrowBadCode();
        var buf = _bitBuf;
        var cnt = _bitCnt;
        var pos = _pos;
        var last = DecodeBlockCore(ref MemoryMarshal.GetReference(data), c, ref buf, ref cnt, ref pos);
        _bitBuf = buf;
        _bitCnt = cnt;
        _pos = pos;
        return last;
    }

    /// <summary>
    /// One MCU row of an interleaved streaming scan: each block is entropy-decoded (the
    /// inlined <see cref="DecodeBlockCore"/>, bit state in registers across the row) and
    /// transformed straight away, so the out-of-order core overlaps one block's
    /// latency-bound Huffman chain with the previous block's IDCT. Block b of an MCU
    /// belongs to component <paramref name="blockComp"/>[b] at offset
    /// (<paramref name="blockDx"/>[b], <paramref name="blockDy"/>[b]) in its H×V grid.
    /// Returns −1, or the MCU after which the scan ended early.
    /// </summary>
    private int DecodeInterleavedRow(int m, scoped Span<short> data, scoped ReadOnlySpan<int> blockComp,
        scoped ReadOnlySpan<int> blockDx, scoped ReadOnlySpan<int> blockDy)
    {
        var nb = blockComp.Length;
        if (data.Length < 64 || blockDx.Length < nb || blockDy.Length < nb)
            ThrowBadCode();
        var comps = _comp;
        ref var d0 = ref MemoryMarshal.GetReference(data);
        var buf = _bitBuf;
        var cnt = _bitCnt;
        var pos = _pos;
        for (var i = 0; i < _imgMcuX; ++i)
        {
            for (var b = 0; b < nb; b++)
            {
                var n = blockComp[b];
                var c = comps[n];
                var last = DecodeBlockCore(ref d0, c, ref buf, ref cnt, ref pos);
                IdctToRing(n, i * c.H + blockDx[b], m * c.V + blockDy[b], data, last, m);
            }

            if (--_todo > 0)
                continue;
            _bitBuf = buf;
            _bitCnt = cnt;
            _pos = pos;
            if (!RestartBoundary())
                return i;
            buf = _bitBuf;
            cnt = _bitCnt;
            pos = _pos;
        }

        _bitBuf = buf;
        _bitCnt = cnt;
        _pos = pos;
        return -1;
    }

    /// <summary>
    /// <see cref="DecodeInterleavedRow"/> for a single-component scan, whose MCU is one
    /// block: block rows <paramref name="by0"/>…<paramref name="by1"/> − 1 of component
    /// <paramref name="n"/>, <paramref name="w"/> blocks each. Returns −1, or by·w + bx
    /// of the block after which the scan ended early.
    /// </summary>
    private int DecodeSingleRows(int m, int n, int by0, int by1, int w, scoped Span<short> data)
    {
        if (data.Length < 64)
            ThrowBadCode();
        var c = _comp[n];
        ref var d0 = ref MemoryMarshal.GetReference(data);
        var buf = _bitBuf;
        var cnt = _bitCnt;
        var pos = _pos;
        for (var by = by0; by < by1; ++by)
        {
            for (var bx = 0; bx < w; ++bx)
            {
                var last = DecodeBlockCore(ref d0, c, ref buf, ref cnt, ref pos);
                IdctToRing(n, bx, by, data, last, m);
                if (--_todo > 0)
                    continue;
                _bitBuf = buf;
                _bitCnt = cnt;
                _pos = pos;
                if (!RestartBoundary())
                    return by * w + bx;
                buf = _bitBuf;
                cnt = _bitCnt;
                pos = _pos;
            }
        }

        _bitBuf = buf;
        _bitCnt = cnt;
        _pos = pos;
        return -1;
    }

    /// <summary>
    /// <see cref="DecodeBlock"/>'s body on caller-held bit state (inlined, so the state
    /// stays in registers); <paramref name="d0"/> must start 64 writable coefficients.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeBlockCore(ref short d0, Component c, ref ulong buf, ref int cnt, ref int pos)
    {
        var zq = c.ZigQ;
        if (zq.Length < 64 + 15)
            ThrowBadCode();

        // Unchecked accesses below: the caller guarantees 64 coefficients at d0 and
        // zq.Length ≥ 79 was checked above, every zig-zag position in zq is ≤ 63, k ≤ 78
        // because k is < 64 at the top of each iteration and grows by at most a 15-zero
        // run, and lookup indices are buf >> (64 − LutBits) < Lut.Length.
        ref var zq0 = ref MemoryMarshal.GetArrayDataReference(zq);
        Unsafe.InitBlockUnaligned(ref Unsafe.As<short, byte>(ref d0), 0, 64 * sizeof(short));

        // --- DC ---
        if (cnt < 32)
            Refill(ref buf, ref cnt, ref pos);
        var dcH = c.DcTable!;
        var e = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(dcH.Lut), (nint)(buf >> (64 - JpegHuffman.LutBits)));
        int diff;
        if ((e & JpegHuffman.LutFull) != 0 && (uint)((e >> 11) & 255) <= 15)
        {
            buf <<= e; // count mod 64 = the entry's bits to consume (bit 5 is 0)
            cnt -= e & 31;
            diff = e >> JpegHuffman.ValueShift;
        }
        else if (e != 0 && (uint)((e >> 11) & 255) <= 15)
        {
            // Code in the table, magnitude bits past its window: both are already in buf
            // (≥ 32 valid bits, code ≤ LutBits, magnitude ≤ 15). t ≥ 1: t = 0 entries are full.
            var len = (e >> JpegHuffman.CodeLenShift) & 15;
            buf <<= len;
            var t = (e >> 11) & 15;
            diff = Extend((int)(buf >> (64 - t)), t);
            buf <<= t;
            cnt -= len + t;
        }
        else
        {
            _bitBuf = buf;
            _bitCnt = cnt;
            _pos = pos;
            diff = DcSlow(dcH);
            buf = _bitBuf;
            cnt = _bitCnt;
            pos = _pos;
        }

        var dcLong = (long)c.DcPred + diff;
        if (dcLong != (int)dcLong)
            ThrowBadDelta();
        var dc = (int)dcLong;
        c.DcPred = dc;
        var dcProduct = (long)dc * (zq0 & 0xffff);
        if (dcProduct != (short)dcProduct)
            ThrowCannotMerge();
        d0 = (short)dcProduct;

        // --- AC ---
        var acH = c.AcTable!;
        _acSlowTable = acH;
        ref var lut0 = ref MemoryMarshal.GetArrayDataReference(acH.Lut);
        var k = 1;
        do
        {
            if (cnt < 32)
                Refill(ref buf, ref cnt, ref pos);
            e = Unsafe.Add(ref lut0, (nint)(buf >> (64 - JpegHuffman.LutBits)));
            int rs, v;
            if ((e & JpegHuffman.LutFull) != 0)
            {
                buf <<= e; // count mod 64 = the entry's bits to consume (bit 5 is 0)
                cnt -= e & 31;
                rs = (e >> 11) & 255;
                v = e >> JpegHuffman.ValueShift;
            }
            else if (e != 0)
            {
                // Code in the table, magnitude bits past its window (see the DC case).
                var len = (e >> JpegHuffman.CodeLenShift) & 15;
                buf <<= len;
                rs = (e >> 11) & 255;
                var t = rs & 15;
                v = Extend((int)(buf >> (64 - t)), t);
                buf <<= t;
                cnt -= len + t;
            }
            else
            {
                _bitBuf = buf;
                _bitCnt = cnt;
                _pos = pos;
                var packed = AcSlow();
                buf = _bitBuf;
                cnt = _bitCnt;
                pos = _pos;
                rs = packed & 255;
                v = packed >> 8;
            }

            if ((rs & 15) != 0)
            {
                k += rs >> 4;
                var zigQ = Unsafe.Add(ref zq0, k++);
                Unsafe.Add(ref d0, zigQ >> 16) = (short)(v * (zigQ & 0xffff));
                continue;
            }

            if (rs != 0xf0)
                break; // EOB
            k += 16;   // ZRL
        } while (k < 64);

        return k - 1;
    }

    /// <summary>DC difference for a code the lookup table cannot resolve with its magnitude bits (field state).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int DcSlow(JpegHuffman h)
    {
        var t = HuffDecode(h);
        if ((uint)t > 15)
            ThrowBadCode();
        return t != 0 ? ExtendReceive(t) : 0;
    }

    /// <summary>
    /// AC run/size symbol (table <see cref="_acSlowTable"/>) plus its sign-extended value,
    /// packed as rs | value &lt;&lt; 8 (field state).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int AcSlow()
    {
        var rs = HuffDecode(_acSlowTable!);
        if (rs < 0)
            ThrowBadCode();
        var s = rs & 15;
        return rs | ((s != 0 ? ExtendReceive(s) : 0) << 8);
    }

    [DoesNotReturn]
    private static void ThrowBadDelta() => throw Err("bad delta");

    [DoesNotReturn]
    private static void ThrowCannotMerge() => throw Err("can't merge dc and ac");

    private void DecodeBlockProgDc(scoped Span<short> data, JpegHuffman hdc, int b)
    {
        if (_specEnd != 0)
            throw Err("can't merge dc and ac");

        if (_succHigh == 0)
        {
            // First DC scan: full magnitude at succ_low precision.
            data.Clear();
            var t = HuffDecode(hdc);
            if (t < 0 || t > 15)
                throw Err("can't merge dc and ac");
            var diff = t != 0 ? ExtendReceive(t) : 0;
            if (!AddIntsValid(_comp[b].DcPred, diff))
                throw Err("bad delta");
            var dc = _comp[b].DcPred + diff;
            _comp[b].DcPred = dc;
            if (!Mul2ShortsValid(dc, 1 << _succLow))
                throw Err("can't merge dc and ac");
            data[0] = (short)(dc * (1 << _succLow));
        }
        else
        {
            // DC refinement: one bit per block.
            if (GetBit())
                data[0] = (short)(data[0] + (1 << _succLow));
        }
    }

    /// <summary>Bit i set where z[i] ≠ 0 (64 coefficients).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NonZeroMask(ref short z)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            ulong m = 0;
            for (var i = 0; i < 4; ++i)
                m |= (ulong)Vector256.Equals(Vector256.LoadUnsafe(ref z, (nuint)(i * 16)), Vector256<short>.Zero).ExtractMostSignificantBits() << (16 * i);
            return ~m;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            ulong m = 0;
            for (var i = 0; i < 8; ++i)
                m |= (ulong)Vector128.Equals(Vector128.LoadUnsafe(ref z, (nuint)(i * 8)), Vector128<short>.Zero).ExtractMostSignificantBits() << (8 * i);
            return ~m;
        }

        ulong s = 0;
        for (var i = 0; i < 64; ++i)
        {
            if (Unsafe.Add(ref z, i) != 0)
                s |= 1UL << i;
        }

        return s;
    }

    /// <summary>
    /// Progressive AC scan for one block. Progressive coefficients are stored in
    /// zig-zag order, so a spectral band is contiguous: refinement walks the band
    /// through a non-zero bitmask instead of position by position. Bit reads happen in
    /// exactly the order T.81 G.1.2.3 prescribes (sign of a new coefficient first, then
    /// one correction bit per non-zero coefficient passed, in band order).
    /// </summary>
    private void DecodeBlockProgAc(scoped Span<short> z, JpegHuffman hac)
    {
        if (_specStart == 0)
            throw Err("can't merge dc and ac");
        if (z.Length < 64)
            ThrowBadCode();

        // Unchecked z accesses: z.Length ≥ 64 (checked above) and every index is
        // either a mask bit (< 64), ≤ _specEnd ≤ 63, or clamped to 63.
        ref var z0 = ref MemoryMarshal.GetReference(z);
        ref var lut0 = ref MemoryMarshal.GetArrayDataReference(hac.Lut);
        var buf = _bitBuf;
        var cnt = _bitCnt;
        var pos = _pos;

        if (_succHigh == 0)
        {
            // First AC scan for this band.
            if (_eobRun != 0)
            {
                --_eobRun;
                return;
            }

            var shift = _succLow;
            var k = _specStart;
            do
            {
                if (cnt < 32)
                    Refill(ref buf, ref cnt, ref pos);
                var e = Unsafe.Add(ref lut0, (nint)(buf >> (64 - JpegHuffman.LutBits)));
                int rs, len;
                if ((e & JpegHuffman.LutFull) != 0 && (e & (15 << 11)) != 0)
                {
                    buf <<= e; // count mod 64 = the entry's bits to consume (bit 5 is 0)
                    cnt -= e & 31;
                    k += (e >> 15) & 15;
                    Unsafe.Add(ref z0, Math.Min(k, 63)) = (short)((e >> JpegHuffman.ValueShift) * (1 << shift));
                    ++k;
                    continue;
                }

                if (e != 0)
                {
                    len = (e >> JpegHuffman.CodeLenShift) & 15;
                    rs = (e >> 11) & 255;
                }
                else
                {
                    var packed = SlowSymbol(hac, buf);
                    if (packed < 0)
                        ThrowBadCode();
                    len = packed >> 8;
                    rs = packed & 255;
                }

                buf <<= len;
                cnt -= len;
                var s = rs & 15;
                var r = rs >> 4;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        var run = 1 << r;
                        if (r != 0)
                        {
                            run += (int)(buf >> (64 - r));
                            buf <<= r;
                            cnt -= r;
                        }

                        _eobRun = run - 1;
                        break;
                    }

                    k += 16;
                }
                else
                {
                    k += r;
                    var v = Extend((int)(buf >> (64 - s)), s);
                    buf <<= s;
                    cnt -= s;
                    Unsafe.Add(ref z0, Math.Min(k, 63)) = (short)(v * (1 << shift));
                    ++k;
                }
            } while (k <= _specEnd);
        }
        else
        {
            // AC refinement.
            var bit = (short)(1 << _succLow);
            var band = (ulong.MaxValue >> (63 - _specEnd)) & (ulong.MaxValue << _specStart);
            var nz = NonZeroMask(ref z0) & band;

            if (_eobRun != 0)
            {
                --_eobRun;
                Correct(ref z0, nz, bit, ref buf, ref cnt, ref pos);
            }
            else
            {
                var k = _specStart;
                do
                {
                    if (cnt < 32)
                        Refill(ref buf, ref cnt, ref pos);
                    var e = Unsafe.Add(ref lut0, (nint)(buf >> (64 - JpegHuffman.LutBits)));
                    int rs, len;
                    if (e != 0)
                    {
                        len = (e >> JpegHuffman.CodeLenShift) & 15;
                        rs = (e >> 11) & 255;
                    }
                    else
                    {
                        var packed = SlowSymbol(hac, buf);
                        if (packed < 0)
                            ThrowBadCode();
                        len = packed >> 8;
                        rs = packed & 255;
                    }

                    buf <<= len;
                    cnt -= len;
                    var s = rs & 15;
                    var r = rs >> 4;
                    var newCoeff = 0;
                    if (s == 0)
                    {
                        if (r < 15)
                        {
                            var run = (1 << r) - 1;
                            if (r != 0)
                            {
                                run += (int)(buf >> (64 - r));
                                buf <<= r;
                                cnt -= r;
                            }

                            _eobRun = run;
                            r = 64; // no new coefficient: correct the rest of the band
                        }
                    }
                    else
                    {
                        if (s != 1)
                            ThrowBadCode();
                        newCoeff = (long)buf < 0 ? bit : -bit;
                        buf <<= 1;
                        --cnt;
                    }

                    // The new coefficient goes to the (r+1)-th still-zero position at or
                    // after k; every non-zero coefficient passed on the way gets a
                    // correction bit. No such position: correct to the end of the band.
                    var rest = band & (ulong.MaxValue << k);
                    var target = 64;
                    if (r < 64)
                    {
                        var nth = NthSetBit(rest & ~nz, r);
                        if (nth != 0)
                            target = BitOperations.TrailingZeroCount(nth);
                    }

                    var passed = target == 64 ? rest : rest & ((1UL << target) - 1);
                    Correct(ref z0, nz & passed, bit, ref buf, ref cnt, ref pos);
                    if (target == 64)
                        break;
                    Unsafe.Add(ref z0, target) = (short)newCoeff; // behind k from now on: nz needs no update
                    k = target + 1;
                } while (k <= _specEnd);
            }
        }

        _bitBuf = buf;
        _bitCnt = cnt;
        _pos = pos;
    }

    /// <summary>
    /// One correction bit for each coefficient in <paramref name="mask"/> (all non-zero),
    /// in increasing position: a set bit adds <paramref name="bit"/> away from zero unless
    /// that bit of the magnitude is already set. Branchless — the correction bits are close
    /// to random, so a branch on them mispredicts about half the time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Correct(ref short z0, ulong mask, short bit, ref ulong buf, ref int cnt, ref int pos)
    {
        while (mask != 0)
        {
            if (cnt == 0)
                Refill(ref buf, ref cnt, ref pos);
            var i = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            var set = (int)(buf >> 63);
            buf <<= 1;
            --cnt;
            ref var p = ref Unsafe.Add(ref z0, i);
            int v = p;
            var need = set & (((v & bit) - 1) >> 31); // 1 when the bit is read and not yet in v
            p = (short)(v + need * ((v >> 31) | 1) * bit);
        }
    }

    /// <summary>Position bit of the (<paramref name="r"/>+1)-th set bit of <paramref name="zeros"/>, or 0 if there is none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NthSetBit(ulong zeros, int r)
    {
        if (Bmi2.X64.IsSupported)
            return r < 64 ? Bmi2.X64.ParallelBitDeposit(1UL << r, zeros) : 0;
        for (var i = 0; i < r && zeros != 0; ++i)
            zeros &= zeros - 1;
        return zeros & (0 - zeros);
    }
    // ============================== scan orchestration ==============================

    private byte GetMarker()
    {
        byte x;
        if (_marker != 0xff)
        {
            x = _marker;
            _marker = 0xff;
            return x;
        }

        x = Get8();
        if (x != 0xff)
            return 0xff;
        while (x == 0xff)
            x = Get8(); // consume fill bytes (returns 0 at EOF, exiting the loop)

        return x;
    }

    private void Reset()
    {
        _bitCnt = 0;
        _bitBuf = 0;
        _nomore = false;
        _comp[0].DcPred = _comp[1].DcPred = _comp[2].DcPred = _comp[3].DcPred = 0;
        _marker = 0xff;
        _todo = _restartInterval != 0 ? _restartInterval : 0x7fffffff;
        _eobRun = 0;
    }

    /// <summary>
    /// Restart-interval bookkeeping after each MCU (or each block in
    /// non-interleaved scans). Returns false when the scan is over — either the
    /// interval expired without an RSTn marker (next marker belongs to the
    /// caller) or the stream ended.
    /// </summary>
    private bool CheckRestart()
    {
        if (--_todo > 0)
            return true;
        return RestartBoundary();
    }

    /// <summary><see cref="CheckRestart"/> once the interval's MCU count has run out (field bit state).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RestartBoundary()
    {
        // The interval is over: only padding bits remain before the RSTn marker.
        if (!_nomore)
            RefillSlow();
        if (!(_marker >= 0xd0 && _marker <= 0xd7))
            return false;
        Reset();
        return true;
    }

    private void ParseEntropyCodedData()
    {
        Reset();
        if (!_progressive)
        {
            if (_mode == DecodeMode.Streaming)
            {
                if (_streamDone)
                    DecodeBaselineScan(discard: true); // invalid: a component sent twice
                else
                    DecodeStreamingScan();
                _streamDone = true;
            }
            else
            {
                DecodeBaselineScan(discard: false);
            }
        }
        else
        {
            if (_scanN == 1)
            {
                var n = _order[0];
                var c = _comp[n];
                var w = (c.X + 7) >> 3;
                var h = (c.Y + 7) >> 3;
                for (var j = 0; j < h; ++j)
                {
                    for (var i = 0; i < w; ++i)
                    {
                        var coeff = c.Coeff.AsSpan(64 * (i + j * c.CoeffW), 64);
                        if (_specStart == 0)
                            DecodeBlockProgDc(coeff, _huffDc[c.Hd], n);
                        else
                            DecodeBlockProgAc(coeff, _huffAc[c.Ha]);
                        if (!CheckRestart())
                            return;
                    }
                }
            }
            else
            {
                // Interleaved progressive scans carry DC only (spec: AC scans are
                // always single-component).
                for (var j = 0; j < _imgMcuY; ++j)
                {
                    for (var i = 0; i < _imgMcuX; ++i)
                    {
                        for (var k = 0; k < _scanN; ++k)
                        {
                            var n = _order[k];
                            var c = _comp[n];
                            for (var y = 0; y < c.V; ++y)
                            {
                                for (var x = 0; x < c.H; ++x)
                                {
                                    var x2 = i * c.H + x;
                                    var y2 = j * c.V + y;
                                    var coeff = c.Coeff.AsSpan(64 * (x2 + y2 * c.CoeffW), 64);
                                    DecodeBlockProgDc(coeff, _huffDc[c.Hd], n);
                                }
                            }
                        }

                        if (!CheckRestart())
                            return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Streaming mode: the frame's first (and only) scan covers every component.
    /// Each MCU row is entropy-decoded and inverse-transformed into the strips,
    /// then committed, which emits the RGBA rows it completes.
    /// </summary>
    private void DecodeStreamingScan()
    {
        Span<short> data = stackalloc short[64];
        if (_scanN == 1)
        {
            // Single-component frame: its own block grid, no MCU padding; an "MCU
            // row" here is the V block rows one strip half holds.
            var n = _order[0];
            var c = _comp[n];
            var w = (c.X + 7) >> 3;
            var h = (c.Y + 7) >> 3;
            for (var m = 0; m < _imgMcuY; ++m)
            {
                var by0 = m * c.V;
                var by1 = Math.Min(h, by0 + c.V);
                for (var by = by1; by < by0 + c.V; ++by)
                    ZeroBlockRow(c, by);
                var end = DecodeSingleRows(m, n, by0, by1, w, data);
                if (end >= 0)
                {
                    var (eby, ebx) = Math.DivRem(end, w);
                    for (var x = ebx + 1; x < w; ++x)
                        ZeroBlock(c, x, eby);
                    for (var y = eby + 1; y < by1; ++y)
                        ZeroBlockRow(c, y);
                    CommitMcuRow(m);
                    ZeroFillFrom(m + 1);
                    return;
                }

                CommitMcuRow(m);
            }
        }
        else
        {
            // Interleaved: full MCU sweep, h×v blocks per component per MCU, in scan order.
            Span<int> blockComp = stackalloc int[MaxBlocksPerMcu];
            Span<int> blockDx = stackalloc int[MaxBlocksPerMcu];
            Span<int> blockDy = stackalloc int[MaxBlocksPerMcu];
            var blocks = 0;
            for (var k = 0; k < _scanN; ++k)
            {
                var c = _comp[_order[k]];
                for (var y = 0; y < c.V; ++y)
                {
                    for (var x = 0; x < c.H; ++x)
                    {
                        blockComp[blocks] = _order[k];
                        blockDx[blocks] = x;
                        blockDy[blocks++] = y;
                    }
                }
            }

            for (var m = 0; m < _imgMcuY; ++m)
            {
                var i = DecodeInterleavedRow(m, data, blockComp.Slice(0, blocks), blockDx.Slice(0, blocks), blockDy.Slice(0, blocks));
                if (i >= 0)
                {
                    // The scan ended early: the rest of the frame is zero samples.
                    for (var k = 0; k < ImgN; ++k)
                    {
                        var c = _comp[k];
                        for (var by = m * c.V; by < (m + 1) * c.V; ++by)
                            for (var bx = (i + 1) * c.H; bx < _imgMcuX * c.H; ++bx)
                                ZeroBlock(c, bx, by);
                    }

                    CommitMcuRow(m);
                    ZeroFillFrom(m + 1);
                    return;
                }

                CommitMcuRow(m);
            }
        }
    }

    /// <summary>
    /// Baseline coefficient mode (a frame split over several scans): store each
    /// block's dequantized coefficients and mark it delivered. With
    /// <paramref name="discard"/> the blocks are decoded and dropped.
    /// </summary>
    private void DecodeBaselineScan(bool discard)
    {
        Span<short> scratch = stackalloc short[64];
        if (_scanN == 1)
        {
            // Non-interleaved: this component's own block grid, no MCU padding.
            var n = _order[0];
            var c = _comp[n];
            var w = (c.X + 7) >> 3;
            var h = (c.Y + 7) >> 3;
            for (var j = 0; j < h; ++j)
            {
                for (var i = 0; i < w; ++i)
                {
                    var idx = i + j * c.CoeffW;
                    var data = discard ? scratch : c.Coeff.AsSpan(64 * idx, 64);
                    DecodeBlock(data, c);
                    if (!discard)
                        c.Written![idx] = 1;
                    if (!CheckRestart())
                        return;
                }
            }
        }
        else
        {
            for (var j = 0; j < _imgMcuY; ++j)
            {
                for (var i = 0; i < _imgMcuX; ++i)
                {
                    for (var k = 0; k < _scanN; ++k)
                    {
                        var n = _order[k];
                        var c = _comp[n];
                        for (var y = 0; y < c.V; ++y)
                        {
                            for (var x = 0; x < c.H; ++x)
                            {
                                var idx = i * c.H + x + (j * c.V + y) * c.CoeffW;
                                var data = discard ? scratch : c.Coeff.AsSpan(64 * idx, 64);
                                DecodeBlock(data, c);
                                if (!discard)
                                    c.Written![idx] = 1;
                            }
                        }
                    }

                    if (!CheckRestart())
                        return;
                }
            }
        }
    }

    /// <summary>
    /// Coefficient mode: inverse-transform the stored blocks MCU row by MCU row
    /// into the strips and emit. Progressive coefficients are dequantized here
    /// (blocks outside the component's own grid stay zero samples); baseline
    /// coefficients were dequantized on decode, and blocks no scan delivered stay
    /// zero samples.
    /// </summary>
    private void EmitCoefficients()
    {
        if ((Ablation & JpegAblation.SkipBlockSink) != 0)
            return;
        Span<short> block = stackalloc short[64];
        Span<short> quantZ = stackalloc short[64 * 4]; // per component, zig-zag order
        for (var n = 0; n < ImgN; ++n)
        {
            for (var k = 0; k < 64; ++k)
                quantZ[n * 64 + k] = (short)_dequant[_comp[n].Tq][Dezigzag[k]];
        }

        for (var m = 0; m < _imgMcuY; ++m)
        {
            for (var n = 0; n < ImgN; ++n)
            {
                var c = _comp[n];
                var qz = quantZ.Slice(n * 64, 64);
                var w = (c.X + 7) >> 3;
                var h = (c.Y + 7) >> 3;
                for (var by = m * c.V; by < (m + 1) * c.V; ++by)
                {
                    for (var bx = 0; bx < c.CoeffW; ++bx)
                    {
                        var idx = bx + by * c.CoeffW;
                        var coeff = c.Coeff.AsSpan(64 * idx, 64);
                        if (_progressive)
                        {
                            if (bx >= w || by >= h)
                            {
                                ZeroBlock(c, bx, by);
                                continue;
                            }

                            // Progressive coefficients are zig-zag ordered: dequantize the
                            // non-zero ones (16-bit wrap-around, as (short)(coeff × q) always
                            // was) straight into natural order for the IDCT. The bound comes
                            // from the coefficients, so it can only exceed the products' (a
                            // product that wraps to 0): the sparse IDCT paths are exact anyway.
                            var nz = NonZeroMask(ref MemoryMarshal.GetReference(coeff));
                            Unsafe.InitBlockUnaligned(ref Unsafe.As<short, byte>(ref MemoryMarshal.GetReference(block)), 0, 64 * sizeof(short));
                            for (var mask = nz; mask != 0; mask &= mask - 1)
                            {
                                var k = BitOperations.TrailingZeroCount(mask);
                                block[Dezigzag[k]] = (short)(coeff[k] * qz[k]);
                            }

                            IdctToRing(n, bx, by, block, nz == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(nz), m);
                        }
                        else if (c.Written![idx] != 0)
                        {
                            IdctToRing(n, bx, by, coeff, 63, m);
                        }
                        else
                        {
                            ZeroBlock(c, bx, by);
                        }
                    }
                }
            }

            CommitMcuRow(m);
        }
    }

    /// <summary>
    /// Inverse-transforms block (<paramref name="bx"/>, <paramref name="by"/>) of MCU row
    /// <paramref name="m"/> into its strip half. <paramref name="last"/>: zig-zag index
    /// past which every coefficient is zero (selects the sparse IDCT paths).
    /// </summary>
    private void IdctToRing(int n, int bx, int by, scoped ReadOnlySpan<short> data, int last, int m)
    {
        BlockHook?.Invoke(n, bx, by, data);
        if ((Ablation & (JpegAblation.SkipIdct | JpegAblation.SkipBlockSink)) != 0 || (_lumaOnly && n != 0))
            return;
        var c = _comp[n];
        var off = ((m & 1) * c.RowsPerMcu + (by - m * c.V) * _blockSize) * c.W2 + bx * _blockSize;
        if (_blockSize == 8)
            JpegIdct.Idct8x8(c.Ring!, off, c.W2, data, last);
        else
            JpegIdct.IdctReduced(c.Ring!, off, c.W2, data, _blockSize);
    }

    private void ZeroBlockRow(Component c, int by) =>
        c.Ring.AsSpan(RingOffset(c, by * _blockSize), _blockSize * c.W2).Clear();
    // ============================== marker parsing ==============================

    /// <summary>Handles DRI / DQT / DHT / APPn / COM. Returns false on malformed or unknown markers.</summary>
    private bool ProcessMarker(int m)
    {
        int l;
        switch (m)
        {
            case 0xff:
                return false; // expected marker

            case 0xDD: // DRI
                if (Get16() != 4)
                    return false;
                _restartInterval = Get16();
                return true;

            case 0xDB: // DQT
                l = Get16() - 2;
                while (l > 0)
                {
                    int q = Get8();
                    var p = q >> 4;
                    var sixteen = p != 0;
                    var t = q & 15;
                    if (p != 0 && p != 1)
                        return false;
                    if (t > 3)
                        return false;
                    for (var i = 0; i < 64; ++i)
                        _dequant[t][Dezigzag[i]] = (ushort)(sixteen ? Get16() : Get8());
                    l -= sixteen ? 129 : 65;
                }

                return l == 0;

            case 0xC4: // DHT
            {
                l = Get16() - 2;
                Span<int> sizes = stackalloc int[16];
                while (l > 0)
                {
                    int q = Get8();
                    var tc = q >> 4;
                    var th = q & 15;
                    if (tc > 1 || th > 3)
                        return false;
                    var n = 0;
                    for (var i = 0; i < 16; ++i)
                    {
                        sizes[i] = Get8();
                        n += sizes[i];
                    }

                    if (n > 256)
                        return false;
                    l -= 17;

                    var huff = tc == 0 ? _huffDc[th] : _huffAc[th];
                    if (!huff.Build(sizes))
                        return false;
                    for (var i = 0; i < n; ++i)
                        huff.Values[i] = Get8();
                    huff.BuildLut();
                    l -= n;
                }

                return l == 0;
            }
        }

        if ((m >= 0xE0 && m <= 0xEF) || m == 0xFE) // APPn / COM
        {
            l = Get16();
            if (l < 2)
                return false;
            l -= 2;

            if (m == 0xE0 && l >= 5)
            {
                // APP0 JFIF identifier (upstream stb C semantics — see type remarks).
                ReadOnlySpan<byte> tag = "JFIF\0"u8;
                var ok = true;
                for (var i = 0; i < 5; ++i)
                {
                    if (Get8() != tag[i])
                        ok = false;
                }

                l -= 5;
                if (ok)
                    _jfif = true;
            }
            else if (m == 0xE1 && l >= 6 && !_exifSeen && _data.Length - _pos >= l
                     && _data.Slice(_pos, 6).SequenceEqual("Exif\0\0"u8))
            {
                // APP1 EXIF: a TIFF structure after the identifier. Only the first one counts, as in
                // every reader; the segment is skipped below like any other.
                _exifSeen = true;
                _orientation = ExifOrientation.Read(_data.Slice(_pos + 6, l - 6));
            }
            else if (m == 0xEE && l >= 12)
            {
                // APP14 Adobe: "Adobe" + version-hi (0), then version-lo, two
                // 16-bit flag words, and the colour-transform byte.
                ReadOnlySpan<byte> tag = "Adobe\0"u8;
                var ok = true;
                for (var i = 0; i < 6; ++i)
                {
                    if (Get8() != tag[i])
                        ok = false;
                }

                l -= 6;
                if (ok)
                {
                    Get8();  // version low byte
                    Get16(); // flags0
                    Get16(); // flags1
                    _app14ColorTransform = Get8();
                    l -= 6;
                }
            }

            Skip(l);
            return true;
        }

        return false; // unknown marker
    }

    private void ProcessScanHeader()
    {
        var ls = Get16();
        _scanN = Get8();
        if (_scanN < 1 || _scanN > 4 || _scanN > ImgN)
            throw Err("bad SOS component count");
        if (ls != 6 + 2 * _scanN)
            throw Err("bad SOS len");

        for (var i = 0; i < _scanN; ++i)
        {
            int id = Get8();
            int q = Get8();
            int which;
            for (which = 0; which < ImgN; ++which)
            {
                if (_comp[which].Id == id)
                    break;
            }

            if (which == ImgN)
                throw Err("bad SOS component id");
            _comp[which].Hd = q >> 4;
            if (_comp[which].Hd > 3)
                throw Err("bad DC huff");
            _comp[which].Ha = q & 15;
            if (_comp[which].Ha > 3)
                throw Err("bad AC huff");
            _comp[which].DcTable = _huffDc[_comp[which].Hd];
            _comp[which].AcTable = _huffAc[_comp[which].Ha];
            _comp[which].Dequant = _dequant[_comp[which].Tq];
            var zigQ = _comp[which].ZigQ;
            var dqt = _comp[which].Dequant!;
            for (var z = 0; z < zigQ.Length; z++)
                zigQ[z] = Dezigzag[z] << 16 | dqt[Dezigzag[z]];
            _order[i] = which;
        }

        _specStart = Get8();
        _specEnd = Get8();
        var aa = Get8();
        _succHigh = aa >> 4;
        _succLow = aa & 15;
        if (_progressive)
        {
            if (_specStart > 63 || _specEnd > 63 || _specStart > _specEnd || _succHigh > 13 || _succLow > 13)
                throw Err("bad SOS");
        }
        else
        {
            if (_specStart != 0)
                throw Err("bad SOS");
            if (_succHigh != 0 || _succLow != 0)
                throw Err("bad SOS");
            _specEnd = 63;
        }
    }

    private void ProcessFrameHeader(ScanMode scan)
    {
        var lf = Get16();
        if (lf < 11)
            throw Err("bad SOF len");
        var p = Get8();
        if (p != 8)
            throw Err("only 8-bit JPEG is supported");
        ImgY = Get16();
        if (ImgY == 0)
            throw Err("no header height");
        ImgX = Get16();
        if (ImgX == 0)
            throw Err("0 width");
        if (ImgY > 1 << 24 || ImgX > 1 << 24)
            throw Err("too large");
        var c = Get8();
        if (c != 3 && c != 1 && c != 4)
            throw Err("bad component count");
        ImgN = c;
        if (lf != 8 + 3 * ImgN)
            throw Err("bad SOF len");

        _rgbCount = 0;
        ReadOnlySpan<byte> rgbIds = "RGB"u8;
        for (var i = 0; i < ImgN; ++i)
        {
            var comp = _comp[i];
            comp.Id = Get8();
            if (ImgN == 3 && comp.Id == rgbIds[i])
                ++_rgbCount;
            var q = Get8();
            comp.H = q >> 4;
            if (comp.H == 0 || comp.H > 4)
                throw Err("bad H");
            comp.V = q & 15;
            if (comp.V == 0 || comp.V > 4)
                throw Err("bad V");
            comp.Tq = Get8();
            if (comp.Tq > 3)
                throw Err("bad TQ");
        }

        if (scan != ScanMode.Load)
            return;

        if ((long)ImgX * ImgY * ImgN > int.MaxValue)
            throw Err("too large");

        var hMax = 1;
        var vMax = 1;
        for (var i = 0; i < ImgN; ++i)
        {
            if (_comp[i].H > hMax)
                hMax = _comp[i].H;
            if (_comp[i].V > vMax)
                vMax = _comp[i].V;
        }

        for (var i = 0; i < ImgN; ++i)
        {
            if (hMax % _comp[i].H != 0)
                throw Err("bad H");
            if (vMax % _comp[i].V != 0)
                throw Err("bad V");
        }

        _imgHMax = hMax;
        _imgVMax = vMax;

        // MCU geometry is dictated by the bitstream and stays full-resolution;
        // only the IDCT output tile (and therefore the planes) shrink with scale.
        _imgMcuW = hMax * 8;
        _imgMcuH = vMax * 8;
        _imgMcuX = (ImgX + _imgMcuW - 1) / _imgMcuW;
        _imgMcuY = (ImgY + _imgMcuH - 1) / _imgMcuH;

        var b = _blockSize;
        for (var i = 0; i < ImgN; ++i)
        {
            var comp = _comp[i];
            comp.X = (ImgX * comp.H + hMax - 1) / hMax;
            comp.Y = (ImgY * comp.V + vMax - 1) / vMax;
            comp.W2 = _imgMcuX * comp.H * b;
            comp.H2 = _imgMcuY * comp.V * b;
            if ((long)comp.W2 * comp.H2 > int.MaxValue)
                throw Err("too large");

            // Two MCU rows of samples: the row being decoded plus the previous one,
            // whose last line the vertical upsamplers still read. Zeroed so plane
            // padding that no block ever covers is deterministic.
            comp.RowsPerMcu = comp.V * b;
            comp.RingRows = 2 * comp.RowsPerMcu;
            var ringLen = comp.W2 * comp.RingRows;
            comp.Ring = ArrayPool<byte>.Shared.Rent(ringLen);
            comp.Ring.AsSpan(0, ringLen).Clear();

            if (CapturePlanes)
            {
                var planeLen = comp.W2 * comp.H2;
                comp.Data = ArrayPool<byte>.Shared.Rent(planeLen);
                comp.Data.AsSpan(0, planeLen).Clear();
            }

            comp.CoeffW = _imgMcuX * comp.H;
            comp.CoeffH = _imgMcuY * comp.V;
            if (_progressive)
                AllocateCoefficients(comp, written: false);
        }
    }

    private static void AllocateCoefficients(Component comp, bool written)
    {
        var blocks = (long)comp.CoeffW * comp.CoeffH;
        if (blocks * 64 > int.MaxValue)
            throw Err("too large");
        comp.Coeff = ArrayPool<short>.Shared.Rent((int)blocks * 64);
        comp.Coeff.AsSpan(0, (int)blocks * 64).Clear();
        if (written)
        {
            comp.Written = ArrayPool<byte>.Shared.Rent((int)blocks);
            comp.Written.AsSpan(0, (int)blocks).Clear();
        }
    }

    private void DecodeHeader(ScanMode scan)
    {
        _jfif = false;
        _app14ColorTransform = -1;
        _marker = 0xff;
        int m = GetMarker();
        if (m != 0xd8)
            throw Err("no SOI");

        m = GetMarker();
        while (!(m == 0xc0 || m == 0xc1 || m == 0xc2))
        {
            if (!ProcessMarker(m))
                throw Err($"bad or unsupported marker 0x{m:X2} before SOF");
            m = GetMarker();
            while (m == 0xff)
            {
                if (AtEof)
                    throw Err("no SOF");
                m = GetMarker();
            }
        }

        _progressive = m == 0xc2;
        ProcessFrameHeader(scan);
    }

    /// <summary>
    /// Tolerates trailing garbage after entropy data: scans forward for the next
    /// plausible marker byte (stb's stbi__skip_jpeg_junk_at_end).
    /// </summary>
    private byte SkipJunkAtEnd()
    {
        while (!AtEof)
        {
            var x = Get8();
            while (x == 0xff)
            {
                if (AtEof)
                    return 0xff;
                x = Get8();
                if (x != 0x00 && x != 0xff)
                    return x;
            }
        }

        return 0xff;
    }

    /// <summary>
    /// Runs the marker loop after SOF: every scan, DNL, tables between scans.
    /// Returns true when EOI was reached, false when a bad marker ended the image
    /// early (stb salvages what was decoded so far; so do we).
    /// </summary>
    private bool DecodeScans()
    {
        int m = GetMarker();
        while (m != 0xd9) // EOI
        {
            if (m == 0xda) // SOS
            {
                ProcessScanHeader();
                if (_mode == DecodeMode.Undecided)
                {
                    if (_scanN == ImgN)
                    {
                        _mode = DecodeMode.Streaming;
                    }
                    else
                    {
                        _mode = DecodeMode.Coefficients;
                        for (var i = 0; i < ImgN; ++i)
                            AllocateCoefficients(_comp[i], written: true);
                    }
                }

                ParseEntropyCodedData();
                if (_marker == 0xff)
                    _marker = SkipJunkAtEnd();

                m = GetMarker();
                if (m >= 0xd0 && m <= 0xd7) // stray RSTn between scans
                    m = GetMarker();
            }
            else if (m == 0xdc) // DNL
            {
                var ld = Get16();
                var nl = Get16();
                if (ld != 4)
                    throw Err("bad DNL len");
                if (nl != ImgY)
                    throw Err("bad DNL height");
                m = GetMarker();
            }
            else
            {
                if (!ProcessMarker(m))
                    return false;
                m = GetMarker();
            }
        }

        return true;
    }

    // ============================== helpers ==============================

    private static InvalidDataException Err(string message) => new("JPEG: " + message);

    private static bool AddIntsValid(int a, int b)
    {
        if (a >= 0 != b >= 0)
            return true;
        if (a < 0 && b < 0)
            return a >= int.MinValue - b;
        return a <= int.MaxValue - b;
    }

    private static bool Mul2ShortsValid(int a, int b)
    {
        if (b == 0 || b == -1)
            return true;
        if (a >= 0 == b >= 0)
            return a <= short.MaxValue / b;
        if (b < 0)
            return a <= short.MinValue / b;
        return a >= short.MinValue / b;
    }
}
