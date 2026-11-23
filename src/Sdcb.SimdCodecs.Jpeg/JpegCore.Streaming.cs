using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Streaming decode (<see cref="JpegRowReader"/>): the same engine run over the input buffered so far, a call at a time.
/// <para>
/// Between calls the engine's state lives in a <see cref="Suspended"/>; each call rebuilds the engine over the new
/// window of input (<see cref="JpegCore(ReadOnlySpan{byte}, Suspended)"/>) and suspends it again. The unit of work is the
/// MCU row of the frame's one streaming scan, then the markers after it: before each, the entropy state is checkpointed,
/// and when the unit read past the end of the window (<see cref="_overrun"/>) and the stream has not ended, it is rolled
/// back and the call asks for more input. Only whole units are consumed, so the output is the whole decode's bit for bit:
/// a unit either sees every byte it reads exactly as a decode of the complete file does, or it is undone. Once the stream
/// has ended, reads past the end are the zeros a decode of the complete (short) file reads.
/// </para>
/// <para>
/// Rows go to the caller's band (<see cref="_dest"/> from output row <see cref="_destY0"/>, at most up to
/// <see cref="_emitLimit"/>). Rows the strips could give past the band stay pending there; the next MCU row is decoded
/// only once nothing more can be emitted without it, which is exactly when a whole decode decodes it, so the strips hold
/// what they would. The last row is held back until the markers after the scan have been read, so a stream the whole
/// decode rejects there fails before its last row is handed out.
/// </para>
/// </summary>
internal ref partial struct JpegCore
{
    private const int TailRows = 0;       // MCU rows of the streaming scan still to decode
    private const int TailAfterScan = 1;  // the scan's data is over: skip junk, read the next marker
    private const int TailMarker = 2;     // _sTailMarker is the next marker to handle
    private const int TailDone = 3;       // EOI, or a marker that ends the image

    /// <summary>True when a read went past the end of the data since the last checkpoint.</summary>
    internal readonly bool Overrun => _overrun;

    /// <summary>The read position in the data.</summary>
    internal readonly int Position => _pos;

    /// <summary>
    /// Parses the header up to the first scan's header: SOI → SOF (<paramref name="info"/>, as <see cref="ParseInfo"/>
    /// reads it), then the markers before the first SOS, then that SOS. True when the frame streams: sequential, and its
    /// first scan interleaves every component. False for anything a whole decode handles otherwise (progressive,
    /// non-interleaved, or a frame with no scan at all); the position is then meaningless.
    /// </summary>
    internal bool StreamHeader(out JpegInfo info)
    {
        _restartInterval = 0;
        DecodeHeader(ScanMode.Load);
        info = FrameInfo();
        OutX = (ImgX + _scale - 1) / _scale;
        OutY = (ImgY + _scale - 1) / _scale;
        if (_progressive)
            return false;

        // A whole decode picks the colour transform (PrepareOutput) from the markers before SOF, before it reads the ones
        // after it: a JFIF or Adobe segment between SOF and SOS must not change it here either.
        var (jfif, app14) = (_jfif, _app14ColorTransform);

        // The markers between SOF and the first scan, as DecodeScans reads them.
        int m = GetMarker();
        while (m != 0xda)
        {
            if (m == 0xd9)
                return false; // no scan: zero samples
            if (m == 0xdc)
            {
                ReadDnl();
            }
            else if (!ProcessMarker(m))
            {
                return false; // salvaged as zero samples
            }

            m = GetMarker();
        }

        ProcessScanHeader();
        (_jfif, _app14ColorTransform) = (jfif, app14);
        return _scanN == ImgN;
    }

    /// <summary>After a true <see cref="StreamHeader"/>: sets up the output and the scan's entropy state.</summary>
    internal void BeginStreaming(JpegPixelFormat format)
    {
        _format = format;
        _outRow = 0;
        _outBpp = BytesPerPixel(format);
        (_winX0, _winY0, _winW, _winY1) = (0, 0, OutX, OutY);
        _destY0 = 0;
        _emitLimit = 0;
        _mode = DecodeMode.Streaming;
        _streamDone = true; // a later scan sending a component again is decoded and dropped, as in DecodeScans
        PrepareFormat(format);
        Reset();
        _sNextMcu = 0;
        _sZeroFill = false;
        _sTail = TailRows;
        _sMinInput = 0;
    }

    /// <summary>
    /// Writes up to <paramref name="rowCount"/> rows (never past the image) into <paramref name="dest"/>, row
    /// <c>i</c> at <c>i × <paramref name="stride"/></c>, decoding what they need from the data. Done when it wrote
    /// them all; NeedMoreData when the data ran out first (<see cref="Position"/> is then at the start of the unit to
    /// retry). Not called after the last row.
    /// </summary>
    internal OperationStatus StreamRows(bool isFinal, Span<byte> dest, int stride, int rowCount, out int rowsWritten)
    {
        _dest = dest;
        _stride = stride;
        _destY0 = _outRow;
        var start = _outRow;
        var status = StreamUntil(Math.Min(OutY, _outRow + rowCount), isFinal);
        rowsWritten = _outRow - start;
        _dest = default;
        return status;
    }

    private OperationStatus StreamUntil(int target, bool isFinal)
    {
        Span<short> data = stackalloc short[64];
        Span<int> blockComp = stackalloc int[MaxBlocksPerMcu];
        Span<int> blockDx = stackalloc int[MaxBlocksPerMcu];
        Span<int> blockDy = stackalloc int[MaxBlocksPerMcu];
        // The scan's MCU layout; the markers after the scan may change the scan fields, but only once its rows are in.
        var blocks = _sNextMcu < _imgMcuY ? BlockOrder(blockComp, blockDx, blockDy) : 0;
        while (true)
        {
            _emitLimit = _sTail == TailDone ? target : Math.Min(target, OutY - 1);
            EmitRows();
            if (_outRow >= target)
                return OperationStatus.Done;

            if (_sNextMcu < _imgMcuY)
            {
                var m = _sNextMcu;
                if (_sZeroFill)
                {
                    // ZeroFillFrom, one MCU row at a time.
                    for (var k = 0; k < ImgN; ++k)
                    {
                        var c = _comp[k];
                        c.Ring.AsSpan(RingOffset(c, m * c.RowsPerMcu), c.RowsPerMcu * c.W2).Clear();
                    }
                }
                else
                {
                    if (!isFinal && _data.Length - _pos < _sMinInput)
                        return OperationStatus.NeedMoreData;
                    var cp = Checkpoint();
                    bool ended;
                    try
                    {
                        ended = DecodeStreamingRow(m, data, blockComp.Slice(0, blocks), blockDx.Slice(0, blocks), blockDy.Slice(0, blocks));
                    }
                    catch (InvalidDataException) when (_overrun && !isFinal)
                    {
                        Rollback(cp);
                        return OperationStatus.NeedMoreData;
                    }

                    if (_overrun && !isFinal)
                    {
                        Rollback(cp);
                        return OperationStatus.NeedMoreData;
                    }

                    _sMinInput = 0;
                    _sZeroFill = ended;
                }

                _sNextMcu = m + 1;
                CommitMcuRow(m);
                continue;
            }

            if (_sTail == TailRows)
                _sTail = TailAfterScan;
            if (_sTail == TailDone)
                throw new InvalidOperationException("JPEG: the streaming decode ran out of rows."); // unreachable
            if (TailStep(isFinal) == OperationStatus.NeedMoreData)
                return OperationStatus.NeedMoreData;
        }
    }

    /// <summary>One marker step after the streaming scan, as <see cref="DecodeScans"/> takes it.</summary>
    private OperationStatus TailStep(bool isFinal)
    {
        if (!isFinal && _data.Length - _pos < _sMinInput)
            return OperationStatus.NeedMoreData;
        var cp = Checkpoint();
        try
        {
            if (_sTail == TailAfterScan)
            {
                if (_marker == 0xff)
                    _marker = SkipJunkAtEnd();
                int m = GetMarker();
                if (m >= 0xd0 && m <= 0xd7) // stray RSTn between scans
                    m = GetMarker();
                _sTailMarker = m;
                _sTail = TailMarker;
            }
            else
            {
                var m = _sTailMarker;
                if (m == 0xd9)
                {
                    _sTail = TailDone;
                }
                else if (!isFinal && HasLengthField(m) && !SegmentBuffered())
                {
                    // A table segment is applied only whole: one cut short would write the zeros past the window into
                    // whichever table they name, and a retry would not undo that.
                    _sMinInput = _data.Length - _pos < 2 ? 2 : Math.Max(2, (_data[_pos] << 8) | _data[_pos + 1]);
                    return OperationStatus.NeedMoreData;
                }
                else if (m == 0xda)
                {
                    // A scan after the streaming one sends a component again (invalid): decoded and dropped.
                    ProcessScanHeader();
                    Reset();
                    DecodeBaselineScan(discard: true);
                    _sTail = TailAfterScan;
                }
                else if (m == 0xdc)
                {
                    ReadDnl();
                    _sTailMarker = GetMarker();
                }
                else if (!ProcessMarker(m))
                {
                    _sTail = TailDone; // salvage what was decoded
                }
                else
                {
                    _sTailMarker = GetMarker();
                }
            }
        }
        catch (InvalidDataException) when (_overrun && !isFinal)
        {
            Rollback(cp);
            return OperationStatus.NeedMoreData;
        }

        if (_overrun && !isFinal)
        {
            Rollback(cp);
            return OperationStatus.NeedMoreData;
        }

        _sMinInput = 0;
        return OperationStatus.Done;
    }

    private void ReadDnl()
    {
        var ld = Get16();
        var nl = Get16();
        if (ld != 4)
            throw Err("bad DNL len");
        if (nl != ImgY)
            throw Err("bad DNL height");
    }

    /// <summary>Markers whose segment <see cref="ProcessMarker"/> or <see cref="ProcessScanHeader"/> reads by its length.</summary>
    private static bool HasLengthField(int m) => m is 0xda or 0xdb or 0xc4 or 0xdd or 0xfe or (>= 0xe0 and <= 0xef);

    /// <summary>True when the segment at the read position (its 16-bit length first) is in the data whole.</summary>
    private readonly bool SegmentBuffered()
    {
        var left = _data.Length - _pos;
        return left >= 2 && left >= ((_data[_pos] << 8) | _data[_pos + 1]);
    }

    private static int BytesPerPixel(JpegPixelFormat format) => format switch
    {
        JpegPixelFormat.Rgba32 or JpegPixelFormat.Bgra32 => 4,
        JpegPixelFormat.Rgb24 or JpegPixelFormat.Bgr24 => 3,
        _ => 1,
    };

    // ------------------------------------------------------------ checkpoints

    /// <summary>Everything a unit of the streaming decode changes, besides strip samples it rewrites on a retry.</summary>
    private struct Mark
    {
        public int Pos, BitCnt, Todo, EobRun, RestartInterval, Dc0, Dc1, Dc2, Dc3, Tail, TailMarker;
        public ulong BitBuf;
        public byte Marker;
        public bool NoMore;
    }

    private Mark Checkpoint()
    {
        _overrun = false;
        return new Mark
        {
            Pos = _pos, BitCnt = _bitCnt, BitBuf = _bitBuf, Todo = _todo, EobRun = _eobRun, RestartInterval = _restartInterval,
            Dc0 = _comp[0].DcPred, Dc1 = _comp[1].DcPred, Dc2 = _comp[2].DcPred, Dc3 = _comp[3].DcPred,
            Tail = _sTail, TailMarker = _sTailMarker, Marker = _marker, NoMore = _nomore,
        };
    }

    /// <summary>Undoes the unit since <paramref name="cp"/>, and waits for a quarter more input before retrying it, so a
    /// stream arriving a byte at a time costs a bounded number of attempts per unit rather than one per byte.</summary>
    private void Rollback(in Mark cp)
    {
        (_pos, _bitCnt, _bitBuf, _todo, _eobRun, _restartInterval) = (cp.Pos, cp.BitCnt, cp.BitBuf, cp.Todo, cp.EobRun, cp.RestartInterval);
        (_comp[0].DcPred, _comp[1].DcPred, _comp[2].DcPred, _comp[3].DcPred) = (cp.Dc0, cp.Dc1, cp.Dc2, cp.Dc3);
        (_sTail, _sTailMarker, _marker, _nomore) = (cp.Tail, cp.TailMarker, cp.Marker, cp.NoMore);
        _overrun = false;
        var avail = (long)_data.Length - _pos;
        _sMinInput = (int)Math.Min(Array.MaxLength, avail + avail / 4 + 1);
    }

    // ------------------------------------------------------------ suspension

    /// <summary>
    /// The engine's state between two streaming calls: every field but the spans (<see cref="_data"/>,
    /// <see cref="Tables"/>, <see cref="_dest"/>), which a call supplies. <c>JpegStreamingDecodeTests</c> checks by
    /// reflection that every other field of <see cref="JpegCore"/> has its namesake here.
    /// </summary>
    internal sealed class Suspended
    {
        internal int _pos;
        internal JpegColorTransform ColorTransform;
        internal byte _marker;
        internal bool _nomore;
        internal int ImgX, ImgY, ImgN;
        internal int _imgHMax, _imgVMax;
        internal int _imgMcuX, _imgMcuY, _imgMcuW, _imgMcuH;
        internal bool _progressive, _jfif;
        internal int _app14ColorTransform;
        internal ImageOrientation _orientation;
        internal bool _exifSeen;
        internal int _rgbCount, _restartInterval, _todo, _eobRun;
        internal int _scanN, _specStart, _specEnd, _succHigh, _succLow;
        internal DecoderState? _state;
        internal int _scale;
        internal int OutX, OutY;
        internal JpegBlockHook? BlockHook;
        internal JpegAblation Ablation;
        internal CancellationToken CancellationToken;
        internal DecodeMode _mode;
        internal bool _streamDone;
        internal int _stride;
        internal JpegPixelFormat _format;
        internal byte[]? _packScratch;
        internal bool _lumaOnly;
        internal int _outRow, _outBpp;
        internal bool _hasWindow;
        internal int _winX0, _winY0, _winW, _winY1, _destY0, _emitLimit;
        internal bool _overrun;
        internal byte[]? _winScratch;
        internal bool _isRgb, _chroma16;
        internal int _fourTransform;
        internal bool CapturePlanes;
        internal ulong _bitBuf;
        internal JpegHuffman? _acSlowTable;
        internal int _bitCnt;
        internal int _sNextMcu;
        internal bool _sZeroFill;
        internal int _sTail, _sTailMarker, _sMinInput;
    }

    /// <summary>The engine suspended in <paramref name="s"/>, over <paramref name="data"/>: the input from the position it
    /// was suspended at.</summary>
    internal JpegCore(ReadOnlySpan<byte> data, Suspended s)
    {
        _data = data;
        _state = s._state!;
        _order = _state.Order;
        _huffDc = _state.HuffDc;
        _huffAc = _state.HuffAc;
        _dequant = _state.Dequant;
        _comp = _state.Comp;
        _scale = s._scale;
        _blockSize = 8 / _scale;

        _pos = s._pos;
        ColorTransform = s.ColorTransform;
        _marker = s._marker;
        _nomore = s._nomore;
        (ImgX, ImgY, ImgN) = (s.ImgX, s.ImgY, s.ImgN);
        (_imgHMax, _imgVMax) = (s._imgHMax, s._imgVMax);
        (_imgMcuX, _imgMcuY, _imgMcuW, _imgMcuH) = (s._imgMcuX, s._imgMcuY, s._imgMcuW, s._imgMcuH);
        (_progressive, _jfif) = (s._progressive, s._jfif);
        _app14ColorTransform = s._app14ColorTransform;
        _orientation = s._orientation;
        _exifSeen = s._exifSeen;
        (_rgbCount, _restartInterval, _todo, _eobRun) = (s._rgbCount, s._restartInterval, s._todo, s._eobRun);
        (_scanN, _specStart, _specEnd, _succHigh, _succLow) = (s._scanN, s._specStart, s._specEnd, s._succHigh, s._succLow);
        (OutX, OutY) = (s.OutX, s.OutY);
        BlockHook = s.BlockHook;
        Ablation = s.Ablation;
        CancellationToken = s.CancellationToken;
        _mode = s._mode;
        _streamDone = s._streamDone;
        _stride = s._stride;
        _format = s._format;
        _packScratch = s._packScratch;
        _lumaOnly = s._lumaOnly;
        (_outRow, _outBpp) = (s._outRow, s._outBpp);
        _hasWindow = s._hasWindow;
        (_winX0, _winY0, _winW, _winY1, _destY0, _emitLimit) = (s._winX0, s._winY0, s._winW, s._winY1, s._destY0, s._emitLimit);
        _overrun = s._overrun;
        _winScratch = s._winScratch;
        (_isRgb, _chroma16) = (s._isRgb, s._chroma16);
        _fourTransform = s._fourTransform;
        CapturePlanes = s.CapturePlanes;
        _bitBuf = s._bitBuf;
        _acSlowTable = s._acSlowTable;
        _bitCnt = s._bitCnt;
        _sNextMcu = s._sNextMcu;
        _sZeroFill = s._sZeroFill;
        (_sTail, _sTailMarker, _sMinInput) = (s._sTail, s._sTailMarker, s._sMinInput);
    }

    /// <summary>Saves the engine into <paramref name="s"/>, its position made relative to <paramref name="consumed"/>
    /// (where the next call's data starts).</summary>
    internal readonly void Suspend(Suspended s, int consumed)
    {
        s._pos = _pos - consumed;
        s.ColorTransform = ColorTransform;
        s._marker = _marker;
        s._nomore = _nomore;
        (s.ImgX, s.ImgY, s.ImgN) = (ImgX, ImgY, ImgN);
        (s._imgHMax, s._imgVMax) = (_imgHMax, _imgVMax);
        (s._imgMcuX, s._imgMcuY, s._imgMcuW, s._imgMcuH) = (_imgMcuX, _imgMcuY, _imgMcuW, _imgMcuH);
        (s._progressive, s._jfif) = (_progressive, _jfif);
        s._app14ColorTransform = _app14ColorTransform;
        s._orientation = _orientation;
        s._exifSeen = _exifSeen;
        (s._rgbCount, s._restartInterval, s._todo, s._eobRun) = (_rgbCount, _restartInterval, _todo, _eobRun);
        (s._scanN, s._specStart, s._specEnd, s._succHigh, s._succLow) = (_scanN, _specStart, _specEnd, _succHigh, _succLow);
        s._state = _state;
        s._scale = _scale;
        (s.OutX, s.OutY) = (OutX, OutY);
        s.BlockHook = BlockHook;
        s.Ablation = Ablation;
        s.CancellationToken = CancellationToken;
        s._mode = _mode;
        s._streamDone = _streamDone;
        s._stride = _stride;
        s._format = _format;
        s._packScratch = _packScratch;
        s._lumaOnly = _lumaOnly;
        (s._outRow, s._outBpp) = (_outRow, _outBpp);
        s._hasWindow = _hasWindow;
        (s._winX0, s._winY0, s._winW, s._winY1, s._destY0, s._emitLimit) = (_winX0, _winY0, _winW, _winY1, _destY0, _emitLimit);
        s._overrun = _overrun;
        s._winScratch = _winScratch;
        (s._isRgb, s._chroma16) = (_isRgb, _chroma16);
        s._fourTransform = _fourTransform;
        s.CapturePlanes = CapturePlanes;
        s._bitBuf = _bitBuf;
        s._acSlowTable = _acSlowTable;
        s._bitCnt = _bitCnt;
        s._sNextMcu = _sNextMcu;
        s._sZeroFill = _sZeroFill;
        (s._sTail, s._sTailMarker, s._sMinInput) = (_sTail, _sTailMarker, _sMinInput);
    }
}
