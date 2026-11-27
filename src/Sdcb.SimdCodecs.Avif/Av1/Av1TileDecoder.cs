namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 intra tile decoder (§5.11, §8.3.2): superblock partitioning, intra mode/CFL/palette/filter-intra syntax,
/// transform block coefficient decoding, dequantization, inverse transform, and CDEF/LR unit headers.
/// </summary>
internal sealed class Av1TileDecoder
{
    private static readonly byte[] CoeffBaseCtxOffset = BuildCoeffBaseCtxOffset();

    private static readonly int[,,] SigRefDiffOffset =
    {
        { { 0, 1 }, { 1, 0 }, { 1, 1 }, { 0, 2 }, { 2, 0 } },
        { { 0, 1 }, { 1, 0 }, { 0, 2 }, { 0, 3 }, { 0, 4 } },
        { { 0, 1 }, { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 } },
    };

    private static readonly int[,,] MagRefOffsetWithTxClass =
    {
        { { 0, 1 }, { 1, 0 }, { 1, 1 } },
        { { 0, 1 }, { 1, 0 }, { 0, 2 } },
        { { 0, 1 }, { 1, 0 }, { 2, 0 } },
    };

    private static readonly int[] CoeffBasePosCtxOffset = [26, 31, 36]; // SIG_COEF_CONTEXTS_2D = 26

    private static readonly int[] WienerTapsMid = [3, -7, 15];
    private static readonly int[] WienerTapsMin = [-5, -23, -17];
    private static readonly int[] WienerTapsMax = [10, 8, 46];
    private static readonly int[] WienerTapsK = [1, 2, 3];
    private static readonly int[] SgrprojXqdMid = [-32, 31];
    private static readonly int[] SgrprojXqdMin = [-96, -32];
    private static readonly int[] SgrprojXqdMax = [31, 95];

    private static readonly bool[,] TxTypeInSetIntra =
    {
        { true, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false },
        { true, true, true, true, false, false, false, false, false, true, true, true, false, false, false, false },
        { true, true, true, true, false, false, false, false, false, true, false, false, false, false, false, false },
    };

    private static readonly bool[,] TxTypeInSetInter =
    {
        { true, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false },
        { true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true },
        { true, true, true, true, true, true, true, true, true, true, true, true, false, false, false, false },
        { true, false, false, false, false, false, false, false, false, true, false, false, false, false, false, false },
    };

    private readonly Av1SequenceHeader _seq;
    private readonly Av1FrameHeader _fh;
    private readonly Av1Picture _pic;
    private readonly Av1FrameState _st;
    private readonly Av1DecoderSession _session;
    private readonly Av1InterPredict _interPred;
    private readonly Av1CdfState _cdf = new();

    private int _miRowStart, _miRowEnd, _miColStart, _miColEnd;
    private int _currentQIndex;
    private bool _readDeltas;
    private readonly int[] _deltaLf = new int[4];

    // LR reference values per tile (§5.11.2)
    private readonly int[,,] _refLrWiener = new int[3, 2, 3];
    private readonly int[,] _refSgrXqd = new int[3, 2];

    // Context arrays (per plane, indexed by global x4 / y4)
    private readonly byte[][] _aboveLevelContext;
    private readonly byte[][] _aboveDcContext;
    private readonly byte[][] _leftLevelContext;
    private readonly byte[][] _leftDcContext;
    private readonly byte[] _aboveSegPredContext;
    private readonly byte[] _leftSegPredContext;

    // BlockDecoded[plane][(y+1) * bdStride + (x+1)], max sbSize4 = 32 -> 34x34
    private readonly bool[][] _blockDecoded;
    private const int BdStride = 34;

    // Per-block scratch (TxTypes & Palette state live on Av1FrameState, disjoint per tile)
    private readonly byte[] _txTypes;
    private readonly int[] _quant = new int[1024];
    private readonly byte[] _levels = new byte[36 * 36]; // padded by +4 cols and +4 rows of zeros for branchless coeff ctx
    private readonly int[] _dequant = new int[1024];
    private readonly int[] _residual = new int[4096];

    // Palette state (references shared frame-level arrays; per-block scratch is tile-local)
    private readonly byte[]? _paletteSizesY;
    private readonly byte[]? _paletteSizesUv;
    private readonly ushort[]? _paletteColorsY;
    private readonly ushort[]? _paletteColorsUv;
    private readonly int[] _curPalY = new int[8];
    private readonly int[] _curPalU = new int[8];
    private readonly int[] _curPalV = new int[8];
    private readonly int[] _palCache = new int[16];
    private readonly byte[] _colorMapY = new byte[64 * 64];
    private readonly byte[] _colorMapUv = new byte[64 * 64];

    // Current block state
    private int _miRow, _miCol, _miSize;
    private bool _hasChroma, _availU, _availL, _availUChroma, _availLChroma;
    private bool _skip, _skipMode, _lossless, _isInter, _useIntrabc;
    private int _segmentId;
    private int _yMode, _uvMode;
    private int _angleDeltaY, _angleDeltaUv;
    private int _cflAlphaU, _cflAlphaV;
    private bool _useFilterIntra;
    private int _filterIntraMode;
    private int _paletteSizeY, _paletteSizeUv;
    private int _txSize;
    private int _planeTxType;
    private int _maxLumaW, _maxLumaH;

    // Inter block state
    private int _leftRef0, _leftRef1, _aboveRef0, _aboveRef1;
    private bool _leftIntra, _aboveIntra, _leftSingle, _aboveSingle;
    private int _refFrame0, _refFrame1;
    private int _mv0Y, _mv0X, _mv1Y, _mv1X;
    private int _globalMv0Y, _globalMv0X, _globalMv1Y, _globalMv1X;
    private int _refMvIdx;
    private int _newMvContext, _zeroMvContext, _refMvContext;
    private bool _foundMatch;
    private int _newMvCount;
    private int _motionMode;
    private bool _isInterIntra;
    private int _interIntraMode;
    private bool _wedgeInterIntra;
    private int _wedgeIndex, _wedgeSign;
    private int _compGroupIdx, _compoundIdx, _compoundType, _maskType;
    private int _interpFilterV, _interpFilterH;

    // MV stack & warp sample scratch
    private readonly int[] _refStackMv0Y = new int[Av1Constants.MaxRefMvStackSize];
    private readonly int[] _refStackMv0X = new int[Av1Constants.MaxRefMvStackSize];
    private readonly int[] _refStackMv1Y = new int[Av1Constants.MaxRefMvStackSize];
    private readonly int[] _refStackMv1X = new int[Av1Constants.MaxRefMvStackSize];
    private readonly int[] _weightStack = new int[Av1Constants.MaxRefMvStackSize];
    private readonly int[] _drlCtxStack = new int[Av1Constants.MaxRefMvStackSize];
    private int _numMvFound;
    private readonly int[] _warpCandList = new int[Av1Constants.LeastSquaresSamplesMax * 4];
    private int _numWarpSamples, _numWarpSamplesScanned;

    internal Av1TileDecoder(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState st, Av1DecoderSession session)
    {
        _seq = seq;
        _fh = fh;
        _pic = pic;
        _st = st;
        _session = session;
        _interPred = new Av1InterPredict(seq, fh, pic, st, session);

        int numPlanes = seq.NumPlanes;
        _aboveLevelContext = new byte[numPlanes][];
        _aboveDcContext = new byte[numPlanes][];
        _leftLevelContext = new byte[numPlanes][];
        _leftDcContext = new byte[numPlanes][];
        _blockDecoded = new bool[numPlanes][];

        for (int p = 0; p < numPlanes; p++)
        {
            int sx = p == 0 ? 0 : seq.SubsamplingX;
            int sy = p == 0 ? 0 : seq.SubsamplingY;
            int w4 = (fh.MiCols + sx) >> sx;
            int h4 = (fh.MiRows + sy) >> sy;
            _aboveLevelContext[p] = new byte[w4 + 32];
            _aboveDcContext[p] = new byte[w4 + 32];
            _leftLevelContext[p] = new byte[h4 + 32];
            _leftDcContext[p] = new byte[h4 + 32];
            _blockDecoded[p] = new bool[BdStride * BdStride];
        }

        _aboveSegPredContext = new byte[fh.MiCols + 32];
        _leftSegPredContext = new byte[fh.MiRows + 32];
        _txTypes = st.TxTypes;
        _paletteSizesY = st.PaletteSizesY;
        _paletteSizesUv = st.PaletteSizesUv;
        _paletteColorsY = st.PaletteColorsY;
        _paletteColorsUv = st.PaletteColorsUv;
    }

    internal void DecodeTile(int tileRow, int tileCol, ReadOnlySpan<byte> tileBytes, bool saveCdf, ref Av1CdfState? savedUpdateCdf)
    {
        _miRowStart = _fh.MiRowStarts[tileRow];
        _miRowEnd = _fh.MiRowStarts[tileRow + 1];
        _miColStart = _fh.MiColStarts[tileCol];
        _miColEnd = _fh.MiColStarts[tileCol + 1];

        _currentQIndex = _fh.BaseQIdx;
        _cdf.CopyFrom(_fh.InitialCdfs, resetCounts: false);
        Array.Clear(_deltaLf);

        for (int p = 0; p < _seq.NumPlanes; p++)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                _refSgrXqd[p, pass] = SgrprojXqdMid[pass];
                for (int i = 0; i < 3; i++)
                    _refLrWiener[p, pass, i] = WienerTapsMid[i];
            }
        }

        ClearAboveContext();

        var sym = new Av1SymbolDecoder(tileBytes, _fh.DisableCdfUpdate);

        int sbSize = _seq.Use128x128Superblock ? Av1Constants.Block128x128 : Av1Constants.Block64x64;
        int sbSize4 = Av1Constants.Num4x4BlocksWide[sbSize];

        for (int r = _miRowStart; r < _miRowEnd; r += sbSize4)
        {
            ClearLeftContext();
            for (int c = _miColStart; c < _miColEnd; c += sbSize4)
            {
                _readDeltas = _fh.DeltaQPresent;
                ClearCdef(r, c);
                ClearBlockDecodedFlags(r, c, sbSize4);
                ReadLr(ref sym, r, c, sbSize);
                DecodePartition(ref sym, r, c, sbSize);
            }
        }

        sym.Exit();
        if (saveCdf)
            savedUpdateCdf = _cdf.Clone();
    }

    private void ClearAboveContext()
    {
        for (int p = 0; p < _seq.NumPlanes; p++)
        {
            int sx = p == 0 ? 0 : _seq.SubsamplingX;
            int x0 = _miColStart >> sx;
            int x1 = (_miColEnd + sx) >> sx;
            _aboveLevelContext[p].AsSpan(x0, x1 - x0).Clear();
            _aboveDcContext[p].AsSpan(x0, x1 - x0).Clear();
        }
        _aboveSegPredContext.AsSpan(_miColStart, _miColEnd - _miColStart).Clear();
    }

    private void ClearLeftContext()
    {
        for (int p = 0; p < _seq.NumPlanes; p++)
        {
            int sy = p == 0 ? 0 : _seq.SubsamplingY;
            int y0 = _miRowStart >> sy;
            int y1 = (_miRowEnd + sy) >> sy;
            _leftLevelContext[p].AsSpan(y0, y1 - y0).Clear();
            _leftDcContext[p].AsSpan(y0, y1 - y0).Clear();
        }
        _leftSegPredContext.AsSpan(_miRowStart, _miRowEnd - _miRowStart).Clear();
    }

    private void ClearCdef(int r, int c)
    {
        int cr = r >> 4;
        int cc = c >> 4;
        _st.CdefIdx[cr * _st.CdefCols + cc] = -1;
        if (_seq.Use128x128Superblock)
        {
            if (cc + 1 < _st.CdefCols)
                _st.CdefIdx[cr * _st.CdefCols + (cc + 1)] = -1;
            if (cr + 1 < _st.CdefRows)
                _st.CdefIdx[(cr + 1) * _st.CdefCols + cc] = -1;
            if (cr + 1 < _st.CdefRows && cc + 1 < _st.CdefCols)
                _st.CdefIdx[(cr + 1) * _st.CdefCols + (cc + 1)] = -1;
        }
    }

    private void ClearBlockDecodedFlags(int r, int c, int sbSize4)
    {
        for (int p = 0; p < _seq.NumPlanes; p++)
        {
            int sx = p == 0 ? 0 : _seq.SubsamplingX;
            int sy = p == 0 ? 0 : _seq.SubsamplingY;
            int sbWidth4 = (_miColEnd - c) >> sx;
            int sbHeight4 = (_miRowEnd - r) >> sy;
            int limY = sbSize4 >> sy;
            int limX = sbSize4 >> sx;
            bool[] bd = _blockDecoded[p];
            for (int y = -1; y <= limY; y++)
            {
                int rowOff = (y + 1) * BdStride;
                for (int x = -1; x <= limX; x++)
                {
                    bool v = (y < 0 && x < sbWidth4) || (x < 0 && y < sbHeight4);
                    bd[rowOff + (x + 1)] = v;
                }
            }
            bd[(limY + 1) * BdStride + 0] = false;
        }
    }

    private bool IsInside(int r, int c) =>
        c >= _miColStart && c < _miColEnd && r >= _miRowStart && r < _miRowEnd;

    private void DecodePartition(ref Av1SymbolDecoder sym, int r, int c, int bSize)
    {
        if (r >= _fh.MiRows || c >= _fh.MiCols)
            return;

        bool availU = IsInside(r - 1, c);
        bool availL = IsInside(r, c - 1);
        int num4x4 = Av1Constants.Num4x4BlocksWide[bSize];
        int half = num4x4 >> 1;
        int quarter = half >> 1;
        bool hasRows = (r + half) < _fh.MiRows;
        bool hasCols = (c + half) < _fh.MiCols;

        int partition;
        if (bSize < Av1Constants.Block8x8)
        {
            partition = Av1Constants.PartitionNone;
        }
        else if (hasRows && hasCols)
        {
            partition = sym.ReadSymbol(GetPartitionCdf(r, c, bSize, availU, availL));
        }
        else if (hasCols)
        {
            var partCdf = GetPartitionCdf(r, c, bSize, availU, availL);
            int psum = (partCdf[Av1Constants.PartitionVert] - partCdf[Av1Constants.PartitionVert - 1])
                     + (partCdf[Av1Constants.PartitionSplit] - partCdf[Av1Constants.PartitionSplit - 1])
                     + (partCdf[Av1Constants.PartitionHorzA] - partCdf[Av1Constants.PartitionHorzA - 1])
                     + (partCdf[Av1Constants.PartitionVertA] - partCdf[Av1Constants.PartitionVertA - 1])
                     + (partCdf[Av1Constants.PartitionVertB] - partCdf[Av1Constants.PartitionVertB - 1]);
            if (bSize != Av1Constants.Block128x128)
                psum += partCdf[Av1Constants.PartitionVert4] - partCdf[Av1Constants.PartitionVert4 - 1];
            Span<ushort> tmpCdf = [(ushort)(32768 - psum), 32768, 0];
            int splitOrHorz = sym.ReadSymbolNoUpdate(tmpCdf);
            partition = splitOrHorz != 0 ? Av1Constants.PartitionSplit : Av1Constants.PartitionHorz;
        }
        else if (hasRows)
        {
            var partCdf = GetPartitionCdf(r, c, bSize, availU, availL);
            int psum = (partCdf[Av1Constants.PartitionHorz] - partCdf[Av1Constants.PartitionHorz - 1])
                     + (partCdf[Av1Constants.PartitionSplit] - partCdf[Av1Constants.PartitionSplit - 1])
                     + (partCdf[Av1Constants.PartitionHorzA] - partCdf[Av1Constants.PartitionHorzA - 1])
                     + (partCdf[Av1Constants.PartitionHorzB] - partCdf[Av1Constants.PartitionHorzB - 1])
                     + (partCdf[Av1Constants.PartitionVertA] - partCdf[Av1Constants.PartitionVertA - 1]);
            if (bSize != Av1Constants.Block128x128)
                psum += partCdf[Av1Constants.PartitionHorz4] - partCdf[Av1Constants.PartitionHorz4 - 1];
            Span<ushort> tmpCdf = [(ushort)(32768 - psum), 32768, 0];
            int splitOrVert = sym.ReadSymbolNoUpdate(tmpCdf);
            partition = splitOrVert != 0 ? Av1Constants.PartitionSplit : Av1Constants.PartitionVert;
        }
        else
        {
            partition = Av1Constants.PartitionSplit;
        }

        int subSize = Av1Constants.PartitionSubsize(partition, bSize);
        int splitSize = Av1Constants.PartitionSubsize(Av1Constants.PartitionSplit, bSize);

        switch (partition)
        {
            case Av1Constants.PartitionNone:
                DecodeBlock(ref sym, r, c, subSize);
                break;
            case Av1Constants.PartitionHorz:
                DecodeBlock(ref sym, r, c, subSize);
                if (hasRows) DecodeBlock(ref sym, r + half, c, subSize);
                break;
            case Av1Constants.PartitionVert:
                DecodeBlock(ref sym, r, c, subSize);
                if (hasCols) DecodeBlock(ref sym, r, c + half, subSize);
                break;
            case Av1Constants.PartitionSplit:
                DecodePartition(ref sym, r, c, subSize);
                DecodePartition(ref sym, r, c + half, subSize);
                DecodePartition(ref sym, r + half, c, subSize);
                DecodePartition(ref sym, r + half, c + half, subSize);
                break;
            case Av1Constants.PartitionHorzA:
                DecodeBlock(ref sym, r, c, splitSize);
                DecodeBlock(ref sym, r, c + half, splitSize);
                DecodeBlock(ref sym, r + half, c, subSize);
                break;
            case Av1Constants.PartitionHorzB:
                DecodeBlock(ref sym, r, c, subSize);
                DecodeBlock(ref sym, r + half, c, splitSize);
                DecodeBlock(ref sym, r + half, c + half, splitSize);
                break;
            case Av1Constants.PartitionVertA:
                DecodeBlock(ref sym, r, c, splitSize);
                DecodeBlock(ref sym, r + half, c, splitSize);
                DecodeBlock(ref sym, r, c + half, subSize);
                break;
            case Av1Constants.PartitionVertB:
                DecodeBlock(ref sym, r, c, subSize);
                DecodeBlock(ref sym, r, c + half, splitSize);
                DecodeBlock(ref sym, r + half, c + half, splitSize);
                break;
            case Av1Constants.PartitionHorz4:
                DecodeBlock(ref sym, r, c, subSize);
                DecodeBlock(ref sym, r + quarter, c, subSize);
                DecodeBlock(ref sym, r + quarter * 2, c, subSize);
                if (r + quarter * 3 < _fh.MiRows) DecodeBlock(ref sym, r + quarter * 3, c, subSize);
                break;
            default: // PartitionVert4
                DecodeBlock(ref sym, r, c, subSize);
                DecodeBlock(ref sym, r, c + quarter, subSize);
                DecodeBlock(ref sym, r, c + quarter * 2, subSize);
                if (c + quarter * 3 < _fh.MiCols) DecodeBlock(ref sym, r, c + quarter * 3, subSize);
                break;
        }
    }

    private Span<ushort> GetPartitionCdf(int r, int c, int bSize, bool availU, bool availL)
    {
        int bsl = Av1Constants.MiWidthLog2[bSize];
        int above = availU && Av1Constants.MiWidthLog2[_st.MiSizes[(r - 1) * _st.MiCols + c]] < bsl ? 1 : 0;
        int left = availL && Av1Constants.MiHeightLog2[_st.MiSizes[r * _st.MiCols + (c - 1)]] < bsl ? 1 : 0;
        int ctx = left * 2 + above;
        return bsl switch
        {
            1 => _cdf.PartitionW8.AsSpan(ctx * 5, 5),
            2 => _cdf.PartitionW16.AsSpan(ctx * 11, 11),
            3 => _cdf.PartitionW32.AsSpan(ctx * 11, 11),
            4 => _cdf.PartitionW64.AsSpan(ctx * 11, 11),
            _ => _cdf.PartitionW128.AsSpan(ctx * 9, 9),
        };
    }

    private void DecodeBlock(ref Av1SymbolDecoder sym, int r, int c, int subSize)
    {
        _miRow = r;
        _miCol = c;
        _miSize = subSize;
        int bw4 = Av1Constants.Num4x4BlocksWide[subSize];
        int bh4 = Av1Constants.Num4x4BlocksHigh[subSize];

        if (bh4 == 1 && _seq.SubsamplingY != 0 && (r & 1) == 0)
            _hasChroma = false;
        else if (bw4 == 1 && _seq.SubsamplingX != 0 && (c & 1) == 0)
            _hasChroma = false;
        else
            _hasChroma = _seq.NumPlanes > 1;

        _availU = IsInside(r - 1, c);
        _availL = IsInside(r, c - 1);
        if (_hasChroma)
        {
            _availUChroma = (_seq.SubsamplingY != 0 && bh4 == 1) ? IsInside(r - 2, c) : _availU;
            _availLChroma = (_seq.SubsamplingX != 0 && bw4 == 1) ? IsInside(r, c - 2) : _availL;
        }
        else
        {
            _availUChroma = false;
            _availLChroma = false;
        }

        long t0 = AvifDiagnostics.Timers ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (_fh.FrameIsIntra)
            IntraFrameModeInfo(ref sym);
        else
            InterFrameModeInfo(ref sym);
        PaletteTokens(ref sym);
        ReadBlockTxSize(ref sym, bw4, bh4);
        if (AvifDiagnostics.Timers) { AvifDiagnostics.Add(AvifTimer.ModeInfo, t0); t0 = System.Diagnostics.Stopwatch.GetTimestamp(); }

        if (_skip)
            ResetBlockContext(bw4, bh4);

        // Store per-MI block metadata BEFORE ComputePrediction & Residual (§5.11.5)
        int miCols = _st.MiCols;
        int rEnd = Math.Min(r + bh4, _st.MiRows);
        int cEnd = Math.Min(c + bw4, miCols);
        for (int rr = r; rr < rEnd; rr++)
        {
            int rowBase = rr * miCols;
            for (int cc = c; cc < cEnd; cc++)
            {
                int idx = rowBase + cc;
                _st.IsInters[idx] = _isInter ? (byte)1 : (byte)0;
                _st.SkipModes[idx] = _skipMode ? (byte)1 : (byte)0;
                _st.Skips[idx] = _skip ? (byte)1 : (byte)0;
                _st.MiSizes[idx] = (byte)subSize;
                _st.SegmentIds[idx] = (byte)_segmentId;
                _st.YModes[idx] = (byte)_yMode;
                if (_hasChroma)
                    _st.UvModes[idx] = (byte)_uvMode;
                _st.RefFrames[idx * 2] = (sbyte)_refFrame0;
                _st.RefFrames[idx * 2 + 1] = (sbyte)_refFrame1;
                if (_isInter)
                {
                    _st.Mvs[idx * 4] = (short)_mv0Y;
                    _st.Mvs[idx * 4 + 1] = (short)_mv0X;
                    _st.Mvs[idx * 4 + 2] = (short)_mv1Y;
                    _st.Mvs[idx * 4 + 3] = (short)_mv1X;
                    _st.InterpFilters[idx * 2] = (byte)_interpFilterV;
                    _st.InterpFilters[idx * 2 + 1] = (byte)_interpFilterH;
                    _st.CompGroupIdxs[idx] = (byte)_compGroupIdx;
                    _st.CompoundIdxs[idx] = (byte)_compoundIdx;
                }
                if (_fh.DeltaLfPresent)
                {
                    int lfOff = idx * 4;
                    _st.DeltaLfs[lfOff] = (sbyte)_deltaLf[0];
                    _st.DeltaLfs[lfOff + 1] = (sbyte)_deltaLf[1];
                    _st.DeltaLfs[lfOff + 2] = (sbyte)_deltaLf[2];
                    _st.DeltaLfs[lfOff + 3] = (sbyte)_deltaLf[3];
                }
                if (_paletteSizesY is not null)
                {
                    _paletteSizesY[idx] = (byte)_paletteSizeY;
                    _paletteSizesUv![idx] = (byte)_paletteSizeUv;
                    for (int k = 0; k < _paletteSizeY; k++)
                        _paletteColorsY![idx * 8 + k] = (ushort)_curPalY[k];
                    for (int k = 0; k < _paletteSizeUv; k++)
                        _paletteColorsUv![idx * 8 + k] = (ushort)_curPalU[k];
                }
            }
        }

        if (AvifDiagnostics.Timers) AvifDiagnostics.Add(AvifTimer.MiStore, t0);

        if (_isInter)
            ComputePrediction();

        Residual(ref sym);
    }

    private void ResetBlockContext(int bw4, int bh4)
    {
        int planes = _hasChroma ? 3 : 1;
        for (int p = 0; p < planes; p++)
        {
            int sx = p > 0 ? _seq.SubsamplingX : 0;
            int sy = p > 0 ? _seq.SubsamplingY : 0;
            int x0 = _miCol >> sx;
            int x1 = (_miCol + bw4) >> sx;
            if (x1 > x0)
            {
                _aboveLevelContext[p].AsSpan(x0, x1 - x0).Clear();
                _aboveDcContext[p].AsSpan(x0, x1 - x0).Clear();
            }
            int y0 = _miRow >> sy;
            int y1 = (_miRow + bh4) >> sy;
            if (y1 > y0)
            {
                _leftLevelContext[p].AsSpan(y0, y1 - y0).Clear();
                _leftDcContext[p].AsSpan(y0, y1 - y0).Clear();
            }
        }
    }

    private void IntraFrameModeInfo(ref Av1SymbolDecoder sym)
    {
        _skip = false;
        _skipMode = false;
        _refFrame0 = Av1Constants.IntraFrame;
        _refFrame1 = Av1Constants.NoneFrame;
        _motionMode = Av1Constants.MotionSimple;
        _isInterIntra = false;
        _compoundType = Av1Constants.CompoundAverage;
        _compGroupIdx = 0;
        _compoundIdx = 1;
        _interpFilterV = Av1Constants.Bilinear;
        _interpFilterH = Av1Constants.Bilinear;
        _mv0Y = _mv0X = _mv1Y = _mv1X = 0;

        if (_fh.SegIdPreSkip)
            IntraSegmentId(ref sym);

        ReadSkip(ref sym);

        if (!_fh.SegIdPreSkip)
            IntraSegmentId(ref sym);

        ReadCdef(ref sym);
        ReadDeltaQIndex(ref sym);
        ReadDeltaLf(ref sym);
        _readDeltas = false;

        _useIntrabc = _fh.AllowIntrabc && sym.ReadSymbol(_cdf.Intrabc) != 0;
        if (_useIntrabc)
        {
            _isInter = true;
            _yMode = Av1Constants.DcPred;
            _uvMode = Av1Constants.DcPred;
            _angleDeltaY = 0;
            _angleDeltaUv = 0;
            _cflAlphaU = 0;
            _cflAlphaV = 0;
            _paletteSizeY = 0;
            _paletteSizeUv = 0;
            _useFilterIntra = false;
            _filterIntraMode = 0;
            FindMvStack(isCompound: false);
            AssignMv(ref sym, isCompound: false);
            return;
        }

        _isInter = false;
        int aboveMode = Av1Constants.IntraModeContext[_availU ? _st.YModes[(_miRow - 1) * _st.MiCols + _miCol] : Av1Constants.DcPred];
        int leftMode = Av1Constants.IntraModeContext[_availL ? _st.YModes[_miRow * _st.MiCols + (_miCol - 1)] : Av1Constants.DcPred];
        _yMode = sym.ReadSymbol(_cdf.IntraFrameYMode.AsSpan((aboveMode * 5 + leftMode) * 14, 14));
        ReadIntraBlockTail(ref sym);
    }

    private void ReadIntraBlockTail(ref Av1SymbolDecoder sym)
    {
        _angleDeltaY = 0;
        if (_miSize >= Av1Constants.Block8x8 && IsDirectionalMode(_yMode))
            _angleDeltaY = sym.ReadSymbol(_cdf.AngleDelta.AsSpan((_yMode - Av1Constants.VPred) * 8, 8)) - 3;

        _angleDeltaUv = 0;
        _cflAlphaU = 0;
        _cflAlphaV = 0;
        if (_hasChroma)
        {
            bool cflAllowed = (_lossless && Av1Constants.SubsampledSize(_miSize, _seq.SubsamplingX, _seq.SubsamplingY) == Av1Constants.Block4x4)
                           || (!_lossless && Math.Max(Av1Constants.BlockWidth[_miSize], Av1Constants.BlockHeight[_miSize]) <= 32);
            _uvMode = cflAllowed
                ? sym.ReadSymbol(_cdf.UvModeCflAllowed.AsSpan(_yMode * 15, 15))
                : sym.ReadSymbol(_cdf.UvModeCflNotAllowed.AsSpan(_yMode * 14, 14));

            if (_uvMode == Av1Constants.UvCflPred)
                ReadCflAlphas(ref sym);

            if (_miSize >= Av1Constants.Block8x8 && IsDirectionalMode(_uvMode))
                _angleDeltaUv = sym.ReadSymbol(_cdf.AngleDelta.AsSpan((_uvMode - Av1Constants.VPred) * 8, 8)) - 3;
        }

        _paletteSizeY = 0;
        _paletteSizeUv = 0;
        if (_miSize >= Av1Constants.Block8x8
            && Av1Constants.BlockWidth[_miSize] <= 64
            && Av1Constants.BlockHeight[_miSize] <= 64
            && _fh.AllowScreenContentTools)
        {
            PaletteModeInfo(ref sym);
        }

        _useFilterIntra = false;
        _filterIntraMode = 0;
        if (_seq.EnableFilterIntra
            && _yMode == Av1Constants.DcPred
            && _paletteSizeY == 0
            && Math.Max(Av1Constants.BlockWidth[_miSize], Av1Constants.BlockHeight[_miSize]) <= 32)
        {
            _useFilterIntra = sym.ReadSymbol(_cdf.FilterIntra.AsSpan(_miSize * 3, 3)) != 0;
            if (_useFilterIntra)
                _filterIntraMode = sym.ReadSymbol(_cdf.FilterIntraMode);
        }
    }

    private void IntraSegmentId(ref Av1SymbolDecoder sym)
    {
        if (!_fh.SegmentationEnabled)
            _segmentId = 0;
        else
            ReadSegmentId(ref sym);
        _lossless = _fh.LosslessArray[_segmentId];
    }

    private void ReadSegmentId(ref Av1SymbolDecoder sym)
    {
        int prevUL = (_availU && _availL) ? _st.SegmentIds[(_miRow - 1) * _st.MiCols + (_miCol - 1)] : -1;
        int prevU = _availU ? _st.SegmentIds[(_miRow - 1) * _st.MiCols + _miCol] : -1;
        int prevL = _availL ? _st.SegmentIds[_miRow * _st.MiCols + (_miCol - 1)] : -1;
        int pred = prevU == -1 ? (prevL == -1 ? 0 : prevL)
                 : prevL == -1 ? prevU
                 : (prevUL == prevU ? prevU : prevL);

        if (_skip)
        {
            _segmentId = pred;
        }
        else
        {
            int ctx = prevUL < 0 ? 0
                    : (prevUL == prevU && prevUL == prevL) ? 2
                    : (prevUL == prevU || prevUL == prevL || prevU == prevL) ? 1
                    : 0;
            int diff = sym.ReadSymbol(_cdf.SegmentId.AsSpan(ctx * 9, 9));
            int maxSeg = _fh.LastActiveSegId + 1;
            if ((uint)diff >= (uint)maxSeg)
                throw new InvalidDataException($"AV1 segment_id symbol {diff} exceeds LastActiveSegId {_fh.LastActiveSegId}.");
            int seg = NegDeinterleave(diff, Math.Min(pred, maxSeg - 1), maxSeg);
            if ((uint)seg >= (uint)Av1Constants.MaxSegments)
                throw new InvalidDataException($"AV1 segment_id {seg} is out of range.");
            _segmentId = seg;
        }
    }

    private static int NegDeinterleave(int diff, int r, int max)
    {
        if (r == 0) return diff;
        if (r >= max - 1) return max - diff - 1;
        if (2 * r < max)
        {
            if (diff <= 2 * r)
                return (diff & 1) != 0 ? r + ((diff + 1) >> 1) : r - (diff >> 1);
            return diff;
        }
        if (diff <= 2 * (max - r - 1))
            return (diff & 1) != 0 ? r + ((diff + 1) >> 1) : r - (diff >> 1);
        return max - (diff + 1);
    }

    private void ReadSkip(ref Av1SymbolDecoder sym)
    {
        if (_fh.SegIdPreSkip && _fh.SegmentationEnabled && _fh.FeatureEnabled[_segmentId, Av1Constants.SegLvlSkip])
        {
            _skip = true;
            return;
        }
        int ctx = 0;
        if (_availU) ctx += _st.Skips[(_miRow - 1) * _st.MiCols + _miCol];
        if (_availL) ctx += _st.Skips[_miRow * _st.MiCols + (_miCol - 1)];
        _skip = sym.ReadSymbol(_cdf.Skip.AsSpan(ctx * 3, 3)) != 0;
    }

    private void ReadCdef(ref Av1SymbolDecoder sym)
    {
        if (_skip || _fh.CodedLossless || !_seq.EnableCdef || _fh.AllowIntrabc)
            return;
        int cr = _miRow >> 4;
        int cc = _miCol >> 4;
        if (_st.CdefIdx[cr * _st.CdefCols + cc] != -1)
            return;
        sbyte val = (sbyte)sym.ReadLiteral(_fh.CdefBits);
        int bw4 = Av1Constants.Num4x4BlocksWide[_miSize];
        int bh4 = Av1Constants.Num4x4BlocksHigh[_miSize];
        int r0 = _miRow & ~15;
        int c0 = _miCol & ~15;
        int rLim = Math.Min(r0 + bh4, _st.MiRows);
        int cLim = Math.Min(c0 + bw4, _st.MiCols);
        for (int i = r0; i < rLim; i += 16)
            for (int j = c0; j < cLim; j += 16)
                _st.CdefIdx[(i >> 4) * _st.CdefCols + (j >> 4)] = val;
    }

    private void ReadDeltaQIndex(ref Av1SymbolDecoder sym)
    {
        int sbSize = _seq.Use128x128Superblock ? Av1Constants.Block128x128 : Av1Constants.Block64x64;
        if (_miSize == sbSize && _skip) return;
        if (!_readDeltas) return;

        int abs = sym.ReadSymbol(_cdf.DeltaQ);
        if (abs == 3)
        {
            int remBits = sym.ReadLiteral(3) + 1;
            int absBits = sym.ReadLiteral(remBits);
            abs = absBits + (1 << remBits) + 1;
        }
        if (abs != 0)
        {
            int sign = sym.ReadBool();
            int delta = sign != 0 ? -abs : abs;
            _currentQIndex = Av1Constants.Clip3(1, 255, _currentQIndex + (delta << _fh.DeltaQRes));
        }
    }

    private void ReadDeltaLf(ref Av1SymbolDecoder sym)
    {
        int sbSize = _seq.Use128x128Superblock ? Av1Constants.Block128x128 : Av1Constants.Block64x64;
        if (_miSize == sbSize && _skip) return;
        if (!_readDeltas || !_fh.DeltaLfPresent) return;

        int count = _fh.DeltaLfMulti ? (_seq.NumPlanes > 1 ? 4 : 2) : 1;
        for (int i = 0; i < count; i++)
        {
            Span<ushort> cdf = _fh.DeltaLfMulti ? _cdf.DeltaLfMulti[i] : _cdf.DeltaLf;
            int abs = sym.ReadSymbol(cdf);
            if (abs == 3)
            {
                int remBits = sym.ReadLiteral(3) + 1;
                int absBits = sym.ReadLiteral(remBits);
                abs = absBits + (1 << remBits) + 1;
            }
            if (abs != 0)
            {
                int sign = sym.ReadBool();
                int delta = sign != 0 ? -abs : abs;
                _deltaLf[i] = Av1Constants.Clip3(-63, 63, _deltaLf[i] + (delta << _fh.DeltaLfRes));
            }
        }
    }

    private void ReadCflAlphas(ref Av1SymbolDecoder sym)
    {
        int signs = sym.ReadSymbol(_cdf.CflSign);
        int signU = (signs + 1) / 3;
        int signV = (signs + 1) % 3;
        if (signU != 0)
        {
            int ctx = (signU - 1) * 3 + signV;
            int mag = sym.ReadSymbol(_cdf.CflAlpha.AsSpan(ctx * 17, 17)) + 1;
            _cflAlphaU = signU == 1 ? -mag : mag;
        }
        if (signV != 0)
        {
            int ctx = (signV - 1) * 3 + signU;
            int mag = sym.ReadSymbol(_cdf.CflAlpha.AsSpan(ctx * 17, 17)) + 1;
            _cflAlphaV = signV == 1 ? -mag : mag;
        }
    }

    private void ReadBlockTxSize(ref Av1SymbolDecoder sym, int bw4, int bh4)
    {
        if (_fh.TxMode == Av1Constants.TxModeSelect
            && _miSize > Av1Constants.Block4x4
            && _isInter
            && !_skip
            && !_lossless)
        {
            int maxTxSz = Av1Constants.MaxTxSizeRect[_miSize];
            int txW4 = Av1Constants.TxWidth[maxTxSz] >> 2;
            int txH4 = Av1Constants.TxHeight[maxTxSz] >> 2;
            for (int row = _miRow; row < _miRow + bh4; row += txH4)
                for (int col = _miCol; col < _miCol + bw4; col += txW4)
                    ReadVarTxSize(ref sym, row, col, maxTxSz, 0);
            return;
        }

        if (_lossless)
        {
            _txSize = Av1Constants.Tx4x4;
        }
        else
        {
            int maxRectTxSize = Av1Constants.MaxTxSizeRect[_miSize];
            int maxTxDepth = Av1Constants.MaxTxDepth[_miSize];
            _txSize = maxRectTxSize;
            bool allowSelect = !_skip || !_isInter;
            if (_miSize > Av1Constants.Block4x4 && allowSelect && _fh.TxMode == Av1Constants.TxModeSelect)
            {
                int maxTxW = Av1Constants.TxWidth[maxRectTxSize];
                int maxTxH = Av1Constants.TxHeight[maxRectTxSize];
                int miCols = _st.MiCols;
                int aboveW = !_availU ? 0
                    : _st.IsInters[(_miRow - 1) * miCols + _miCol] != 0
                        ? Av1Constants.BlockWidth[_st.MiSizes[(_miRow - 1) * miCols + _miCol]]
                        : GetAboveTxWidth(_miRow, _miCol);
                int leftH = !_availL ? 0
                    : _st.IsInters[_miRow * miCols + (_miCol - 1)] != 0
                        ? Av1Constants.BlockHeight[_st.MiSizes[_miRow * miCols + (_miCol - 1)]]
                        : GetLeftTxHeight(_miRow, _miCol);
                int ctx = (aboveW >= maxTxW ? 1 : 0) + (leftH >= maxTxH ? 1 : 0);
                Span<ushort> cdf = maxTxDepth switch
                {
                    4 => _cdf.Tx64x64.AsSpan(ctx * 4, 4),
                    3 => _cdf.Tx32x32.AsSpan(ctx * 4, 4),
                    2 => _cdf.Tx16x16.AsSpan(ctx * 4, 4),
                    _ => _cdf.Tx8x8.AsSpan(ctx * 3, 3),
                };
                int depth = sym.ReadSymbol(cdf);
                for (int i = 0; i < depth; i++)
                    _txSize = Av1Constants.SplitTxSize[_txSize];
            }
        }

        int cols = _st.MiCols;
        int rEnd = Math.Min(_miRow + bh4, _st.MiRows);
        int cEnd = Math.Min(_miCol + bw4, cols);
        for (int r = _miRow; r < rEnd; r++)
            for (int c = _miCol; c < cEnd; c++)
                _st.InterTxSizes[r * cols + c] = (byte)_txSize;
    }

    private void ReadVarTxSize(ref Av1SymbolDecoder sym, int row, int col, int txSz, int depth)
    {
        if (row >= _fh.MiRows || col >= _fh.MiCols)
            return;

        int txfmSplit;
        if (txSz == Av1Constants.Tx4x4 || depth == Av1Constants.MaxVarTxDepth)
        {
            txfmSplit = 0;
        }
        else
        {
            int above = GetAboveTxWidth(row, col) < Av1Constants.TxWidth[txSz] ? 1 : 0;
            int left = GetLeftTxHeight(row, col) < Av1Constants.TxHeight[txSz] ? 1 : 0;
            int size = Math.Min(64, Math.Max(Av1Constants.BlockWidth[_miSize], Av1Constants.BlockHeight[_miSize]));
            int maxTxSz = Av1Constants.FindTxSize(size, size);
            int txSzSqrUp = Av1Constants.TxSizeSqrUp[txSz];
            int ctx = (txSzSqrUp != maxTxSz ? 3 : 0) + (Av1Constants.TxSizes - 1 - maxTxSz) * 6 + above + left;
            txfmSplit = sym.ReadSymbol(_cdf.TxfmSplit.AsSpan(ctx * 3, 3));
        }

        int w4 = Av1Constants.TxWidth[txSz] >> 2;
        int h4 = Av1Constants.TxHeight[txSz] >> 2;
        if (txfmSplit != 0)
        {
            int subTxSz = Av1Constants.SplitTxSize[txSz];
            int stepW = Av1Constants.TxWidth[subTxSz] >> 2;
            int stepH = Av1Constants.TxHeight[subTxSz] >> 2;
            for (int i = 0; i < h4; i += stepH)
                for (int j = 0; j < w4; j += stepW)
                    ReadVarTxSize(ref sym, row + i, col + j, subTxSz, depth + 1);
        }
        else
        {
            int miCols = _st.MiCols;
            int rEnd = Math.Min(row + h4, _st.MiRows);
            int cEnd = Math.Min(col + w4, miCols);
            for (int r = row; r < rEnd; r++)
                for (int c = col; c < cEnd; c++)
                    _st.InterTxSizes[r * miCols + c] = (byte)txSz;
            _txSize = txSz;
        }
    }

    private int GetAboveTxWidth(int row, int col)
    {
        if (row == _miRow)
        {
            if (!_availU) return 64;
            int idx = (row - 1) * _st.MiCols + col;
            if (_st.Skips[idx] != 0 && _st.IsInters[idx] != 0)
                return Av1Constants.BlockWidth[_st.MiSizes[idx]];
        }
        return Av1Constants.TxWidth[_st.InterTxSizes[(row - 1) * _st.MiCols + col]];
    }

    private int GetLeftTxHeight(int row, int col)
    {
        if (col == _miCol)
        {
            if (!_availL) return 64;
            int idx = row * _st.MiCols + (col - 1);
            if (_st.Skips[idx] != 0 && _st.IsInters[idx] != 0)
                return Av1Constants.BlockHeight[_st.MiSizes[idx]];
        }
        return Av1Constants.TxHeight[_st.InterTxSizes[row * _st.MiCols + (col - 1)]];
    }

    private void Residual(ref Av1SymbolDecoder sym)
    {
        int widthChunks = Math.Max(1, Av1Constants.BlockWidth[_miSize] >> 6);
        int heightChunks = Math.Max(1, Av1Constants.BlockHeight[_miSize] >> 6);
        int miSizeChunk = (widthChunks > 1 || heightChunks > 1) ? Av1Constants.Block64x64 : _miSize;
        int planes = _hasChroma ? 3 : 1;

        for (int chunkY = 0; chunkY < heightChunks; chunkY++)
        {
            for (int chunkX = 0; chunkX < widthChunks; chunkX++)
            {
                int miRowChunk = _miRow + (chunkY << 4);
                int miColChunk = _miCol + (chunkX << 4);
                for (int plane = 0; plane < planes; plane++)
                {
                    int txSz = _lossless ? Av1Constants.Tx4x4 : GetTxSize(plane, _txSize);
                    int stepX = Av1Constants.TxWidth[txSz] >> 2;
                    int stepY = Av1Constants.TxHeight[txSz] >> 2;
                    int planeSz = GetPlaneResidualSize(miSizeChunk, plane);
                    int num4x4W = Av1Constants.Num4x4BlocksWide[planeSz];
                    int num4x4H = Av1Constants.Num4x4BlocksHigh[planeSz];
                    int subX = plane > 0 ? _seq.SubsamplingX : 0;
                    int subY = plane > 0 ? _seq.SubsamplingY : 0;

                    if (_isInter && !_lossless && plane == 0)
                    {
                        TransformTree(ref sym, miColChunk * 4, miRowChunk * 4, num4x4W * 4, num4x4H * 4);
                    }
                    else
                    {
                        int baseXBlock = (_miCol >> subX) * 4;
                        int baseYBlock = (_miRow >> subY) * 4;
                        int xOff = (chunkX << 4) >> subX;
                        int yOff = (chunkY << 4) >> subY;
                        for (int y = 0; y < num4x4H; y += stepY)
                        {
                            for (int x = 0; x < num4x4W; x += stepX)
                                TransformBlock(ref sym, plane, baseXBlock, baseYBlock, txSz, x + xOff, y + yOff);
                        }
                    }
                }
            }
        }
    }

    private void TransformTree(ref Av1SymbolDecoder sym, int startX, int startY, int w, int h)
    {
        if (startX >= _fh.MiCols * 4 || startY >= _fh.MiRows * 4)
            return;
        int row = startY >> 2;
        int col = startX >> 2;
        int lumaTxSz = _st.InterTxSizes[row * _st.MiCols + col];
        int lumaW = Av1Constants.TxWidth[lumaTxSz];
        int lumaH = Av1Constants.TxHeight[lumaTxSz];
        if (w <= lumaW && h <= lumaH)
        {
            int txSz = Av1Constants.FindTxSize(w, h);
            TransformBlock(ref sym, 0, startX, startY, txSz, 0, 0);
        }
        else if (w > h)
        {
            TransformTree(ref sym, startX, startY, w >> 1, h);
            TransformTree(ref sym, startX + (w >> 1), startY, w >> 1, h);
        }
        else if (w < h)
        {
            TransformTree(ref sym, startX, startY, w, h >> 1);
            TransformTree(ref sym, startX, startY + (h >> 1), w, h >> 1);
        }
        else
        {
            int hw = w >> 1, hh = h >> 1;
            TransformTree(ref sym, startX, startY, hw, hh);
            TransformTree(ref sym, startX + hw, startY, hw, hh);
            TransformTree(ref sym, startX, startY + hh, hw, hh);
            TransformTree(ref sym, startX + hw, startY + hh, hw, hh);
        }
    }

    private void TransformBlock(ref Av1SymbolDecoder sym, int plane, int baseX, int baseY, int txSz, int x, int y)
    {
        int startX = baseX + 4 * x;
        int startY = baseY + 4 * y;
        int subX = plane > 0 ? _seq.SubsamplingX : 0;
        int subY = plane > 0 ? _seq.SubsamplingY : 0;
        int row = (startY << subY) >> 2;
        int col = (startX << subX) >> 2;
        int sbMask = _seq.Use128x128Superblock ? 31 : 15;
        int subBlockMiRow = row & sbMask;
        int subBlockMiCol = col & sbMask;
        int stepX = Av1Constants.TxWidth[txSz] >> 2;
        int stepY = Av1Constants.TxHeight[txSz] >> 2;
        int maxX = (_fh.MiCols * 4) >> subX;
        int maxY = (_fh.MiRows * 4) >> subY;
        if (startX >= maxX || startY >= maxY)
            return;

        if (!_isInter)
        {
            if ((AvifDiagnostics.Ablation & AvifAblation.SkipIntraPred) == 0)
            {
                if ((plane == 0 && _paletteSizeY > 0) || (plane > 0 && _paletteSizeUv > 0))
                {
                    ReadOnlySpan<int> pal = plane == 0 ? _curPalY : plane == 1 ? _curPalU : _curPalV;
                    byte[] cmap = plane == 0 ? _colorMapY : _colorMapUv;
                    Av1IntraPred.PredictPalette(_pic.Planes[plane], _pic.PlaneStrides[plane], startX, startY, x, y, txSz, pal, cmap, 64);
                }
                else
                {
                    bool isCfl = plane > 0 && _uvMode == Av1Constants.UvCflPred;
                    int mode = plane == 0 ? _yMode : isCfl ? Av1Constants.DcPred : _uvMode;
                    int log2W = Av1Constants.TxWidthLog2[txSz];
                    int log2H = Av1Constants.TxHeightLog2[txSz];

                    bool haveLeft = (plane == 0 ? _availL : _availLChroma) || x > 0;
                    bool haveAbove = (plane == 0 ? _availU : _availUChroma) || y > 0;
                    bool[] bd = _blockDecoded[plane];
                    int bdY = subBlockMiRow >> subY;
                    int bdX = subBlockMiCol >> subX;
                    bool haveAboveRight = bd[bdY * BdStride + (bdX + stepX + 1)];
                    bool haveBelowLeft = bd[(bdY + stepY + 1) * BdStride + bdX];

                    Av1IntraPred.PredictIntra(
                        _pic.Planes[plane],
                        _pic.PlaneStrides[plane],
                        _seq.BitDepth,
                        _seq.EnableIntraEdgeFilter,
                        plane,
                        startX,
                        startY,
                        haveLeft,
                        haveAbove,
                        haveAboveRight,
                        haveBelowLeft,
                        mode,
                        plane == 0 ? _angleDeltaY : _angleDeltaUv,
                        plane == 0 && _useFilterIntra,
                        _filterIntraMode,
                        GetIntraFilterType(plane),
                        log2W,
                        log2H,
                        maxX - 1,
                        maxY - 1);

                    if (isCfl)
                    {
                        Av1IntraPred.PredictChromaFromLuma(
                            _pic.Planes[0], _pic.PlaneStrides[0],
                            _pic.Planes[plane], _pic.PlaneStrides[plane],
                            _seq.BitDepth, _seq.SubsamplingX, _seq.SubsamplingY,
                            startX, startY, txSz, plane == 1 ? _cflAlphaU : _cflAlphaV,
                            _maxLumaW, _maxLumaH);
                    }
                }
            }

            if (plane == 0)
            {
                _maxLumaW = startX + stepX * 4;
                _maxLumaH = startY + stepY * 4;
            }
        }

        if (!_skip)
        {
            long tc = AvifDiagnostics.Timers ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            int eob = DecodeCoeffs(ref sym, plane, startX, startY, txSz);
            if (AvifDiagnostics.Timers) AvifDiagnostics.Add(AvifTimer.Coeffs, tc);
            if (eob > 0 && (AvifDiagnostics.Ablation & AvifAblation.SkipTransform) == 0)
            {
                Av1Transform.Reconstruct(
                    _fh, _seq.BitDepth, plane, _segmentId, _currentQIndex,
                    txSz, _planeTxType, _quant, eob,
                    _pic.Planes[plane], _pic.PlaneStrides[plane], startX, startY,
                    _dequant, _residual);
            }
        }

        int lfCols = _st.LfTxCols[plane];
        int lfRows = _st.LoopfilterTxSizes[plane].Length / lfCols;
        byte[] lfTx = _st.LoopfilterTxSizes[plane];
        bool[] bdArr = _blockDecoded[plane];
        int rSub = row >> subY;
        int cSub = col >> subX;
        int bdR = subBlockMiRow >> subY;
        int bdC = subBlockMiCol >> subX;
        for (int i = 0; i < stepY; i++)
        {
            int bdOff = (bdR + i + 1) * BdStride + (bdC + 1);
            bool rowIn = (rSub + i) < lfRows;
            int lfOff = (rSub + i) * lfCols + cSub;
            for (int j = 0; j < stepX; j++)
            {
                if (rowIn && cSub + j < lfCols)
                    lfTx[lfOff + j] = (byte)txSz;
                bdArr[bdOff + j] = true;
            }
        }
    }

    private bool GetIntraFilterType(int plane)
    {
        bool aboveSmooth = false;
        if (plane == 0 ? _availU : _availUChroma)
        {
            int r = _miRow - 1;
            int c = _miCol;
            if (plane > 0)
            {
                if (_seq.SubsamplingX != 0 && (c & 1) == 0) c++;
                if (_seq.SubsamplingY != 0 && (_miRow & 1) != 0) r--;
            }
            aboveSmooth = IsSmooth(r, c, plane);
        }
        bool leftSmooth = false;
        if (plane == 0 ? _availL : _availLChroma)
        {
            int r = _miRow;
            int c = _miCol - 1;
            if (plane > 0)
            {
                if (_seq.SubsamplingX != 0 && (_miCol & 1) != 0) c--;
                if (_seq.SubsamplingY != 0 && (r & 1) == 0) r++;
            }
            leftSmooth = IsSmooth(r, c, plane);
        }
        return aboveSmooth || leftSmooth;
    }

    private bool IsSmooth(int r, int c, int plane)
    {
        int idx = r * _st.MiCols + c;
        if (_st.RefFrames[idx * 2] != Av1Constants.IntraFrame)
            return false;
        int mode = plane == 0 ? _st.YModes[idx] : _st.UvModes[idx];
        return mode is Av1Constants.SmoothPred or Av1Constants.SmoothVPred or Av1Constants.SmoothHPred;
    }

    private int GetTxSize(int plane, int lumaTxSz)
    {
        if (plane == 0) return lumaTxSz;
        int uvTx = Av1Constants.MaxTxSizeRect[GetPlaneResidualSize(_miSize, plane)];
        int w = Av1Constants.TxWidth[uvTx];
        int h = Av1Constants.TxHeight[uvTx];
        if (w == 64 || h == 64)
        {
            if (w == 16) return Av1Constants.Tx16x32;
            if (h == 16) return Av1Constants.Tx32x16;
            return Av1Constants.Tx32x32;
        }
        return uvTx;
    }

    private int GetPlaneResidualSize(int subsize, int plane) =>
        plane == 0 ? subsize : Av1Constants.SubsampledSize(subsize, _seq.SubsamplingX, _seq.SubsamplingY);

    private int DecodeCoeffs(ref Av1SymbolDecoder sym, int plane, int startX, int startY, int txSz)
    {
        int x4 = startX >> 2;
        int y4 = startY >> 2;
        int w4 = Av1Constants.TxWidth[txSz] >> 2;
        int h4 = Av1Constants.TxHeight[txSz] >> 2;
        int txSzCtx = (Av1Constants.TxSizeSqr[txSz] + Av1Constants.TxSizeSqrUp[txSz] + 1) >> 1;
        int ptype = plane > 0 ? 1 : 0;
        int segEob = txSz is Av1Constants.Tx16x64 or Av1Constants.Tx64x16
            ? 512
            : Math.Min(1024, Av1Constants.TxWidth[txSz] * Av1Constants.TxHeight[txSz]);

        int eob = 0;
        int culLevel = 0;
        int dcCategory = 0;

        int txbSkipCtx = GetAllZeroCtx(plane, txSz, x4, y4, w4, h4);
        int allZero = sym.ReadBoolCdf(_cdf.TxbSkip.AsSpan((txSzCtx * 13 + txbSkipCtx) * 3, 3));

        if (allZero != 0)
        {
            if (plane == 0)
            {
                int jEnd = Math.Min(h4, _st.MiRows - y4);
                int iEnd = Math.Min(w4, _st.MiCols - x4);
                for (int j = 0; j < jEnd; j++)
                    for (int i = 0; i < iEnd; i++)
                        _txTypes[(y4 + j) * _st.MiCols + (x4 + i)] = Av1Constants.DctDct;
            }
        }
        else
        {
            if (plane == 0)
                ReadTransformType(ref sym, x4, y4, txSz);
            _planeTxType = ComputeTxType(plane, txSz, x4, y4);
            ushort[] scan = GetScan(txSz, _planeTxType);

            int eobMultiSize = Math.Min(Av1Constants.TxWidthLog2[txSz], 5) + Math.Min(Av1Constants.TxHeightLog2[txSz], 5) - 4;
            int txClass = Av1Constants.GetTxClass(_planeTxType);
            int eobCtx = txClass == Av1Constants.TxClass2D ? 0 : 1;

            int eobPt = eobMultiSize switch
            {
                0 => sym.ReadSymbol(_cdf.EobPt16.AsSpan((ptype * 2 + eobCtx) * 6, 6)) + 1,
                1 => sym.ReadSymbol(_cdf.EobPt32.AsSpan((ptype * 2 + eobCtx) * 7, 7)) + 1,
                2 => sym.ReadSymbol(_cdf.EobPt64.AsSpan((ptype * 2 + eobCtx) * 8, 8)) + 1,
                3 => sym.ReadSymbol(_cdf.EobPt128.AsSpan((ptype * 2 + eobCtx) * 9, 9)) + 1,
                4 => sym.ReadSymbol(_cdf.EobPt256.AsSpan((ptype * 2 + eobCtx) * 10, 10)) + 1,
                5 => sym.ReadSymbol(_cdf.EobPt512.AsSpan(ptype * 11, 11)) + 1,
                _ => sym.ReadSymbol(_cdf.EobPt1024.AsSpan(ptype * 12, 12)) + 1,
            };

            eob = eobPt < 2 ? eobPt : ((1 << (eobPt - 2)) + 1);
            int eobShift = eobPt - 3;
            if (eobShift >= 0)
            {
                int eobExtra = sym.ReadBoolCdf(_cdf.EobExtra.AsSpan(((txSzCtx * 2 + ptype) * 9 + eobShift) * 3, 3));
                if (eobExtra != 0)
                    eob += 1 << eobShift;
                int limit = eobPt - 2;
                for (int i = 1; i < limit; i++)
                {
                    if (sym.ReadBool() != 0)
                        eob += 1 << (limit - 1 - i);
                }
            }

            int adjTxSz = Av1Constants.AdjustedTxSize[txSz];
            int bwl = Av1Constants.TxWidthLog2[adjTxSz];
            int adjW = 1 << bwl;
            int adjH = Av1Constants.TxHeight[adjTxSz];
            int brTxSzCtx = Math.Min(txSzCtx, Av1Constants.Tx32x32);
            int brBaseOff = (brTxSzCtx * 2 + ptype) * 21;
            int cbBaseOff = (txSzCtx * 2 + ptype) * 42;
            int[] quant = _quant;

            if (eob == 1)
            {
                // DC-only fast path (very common): no _quant or _levels clear needed!
                int level = sym.ReadSymbol(_cdf.CoeffBaseEob.AsSpan((txSzCtx * 2 + ptype) * 16, 4)) + 1;
                if (level > 2)
                {
                    Span<ushort> brCdf = _cdf.CoeffBr.AsSpan(brBaseOff * 5, 5);
                    for (int idx = 0; idx < 4; idx++)
                    {
                        int br = sym.ReadSymbol4(brCdf);
                        level += br;
                        if (br < 3) break;
                    }
                }
                int dcSignCtx = GetDcSignCtx(plane, x4, y4, w4, h4);
                int sign = sym.ReadBoolCdf(_cdf.DcSign.AsSpan((ptype * 3 + dcSignCtx) * 3, 3));
                if (level > 14)
                {
                    int length = 0;
                    do
                    {
                        length++;
                        if (length > 20)
                            throw new InvalidDataException("AV1 Golomb coefficient overflow.");
                    } while (sym.ReadBool() == 0);

                    int xVal = 1;
                    for (int i = length - 2; i >= 0; i--)
                        xVal = (xVal << 1) | sym.ReadBool();
                    level = xVal + 14;
                }
                dcCategory = sign != 0 ? 1 : 2;
                level &= 0xFFFFF;
                culLevel = Math.Min(63, level);
                quant[0] = sign != 0 ? -level : level;
            }
            else
            {
                quant.AsSpan(0, segEob).Clear();
                int lvStride = adjW + 4;
                byte[] lv = _levels;
                lv.AsSpan(0, (adjH + 4) * lvStride).Clear();
                byte[] ctxOffTable = CoeffBaseCtxOffset;
                int txSzCtxTableOff = txSz * 25;
                int posMask = adjW - 1;

                // EOB coefficient (c = eob - 1)
                {
                    int c = eob - 1;
                    int pos = scan[c];
                    int row = pos >> bwl;
                    int col = pos & posMask;
                    int eobBaseCtx = c <= ((adjH << bwl) >> 3) ? 1
                        : c <= ((adjH << bwl) >> 2) ? 2
                        : 3;
                    int level = sym.ReadSymbol(_cdf.CoeffBaseEob.AsSpan(((txSzCtx * 2 + ptype) * 4 + eobBaseCtx) * 4, 4)) + 1;
                    if (level > 2)
                    {
                        // No neighbors decoded yet at EOB (mag == 0) and c >= 1 (pos != 0)
                        int brCtx = txClass == Av1Constants.TxClass2D
                            ? (row < 2 && col < 2 ? 7 : 14)
                            : txClass == Av1Constants.TxClassHoriz
                                ? (col == 0 ? 7 : 14)
                                : (row == 0 ? 7 : 14);
                        Span<ushort> brCdf = _cdf.CoeffBr.AsSpan((brBaseOff + brCtx) * 5, 5);
                        for (int idx = 0; idx < 4; idx++)
                        {
                            int br = sym.ReadSymbol4(brCdf);
                            level += br;
                            if (br < 3) break;
                        }
                    }
                    quant[pos] = level;
                    lv[row * lvStride + col] = (byte)(level > 15 ? 15 : level);
                }

                // Remaining coefficients c = eob - 2 .. 0 (branchless zero-padded _levels neighbor lookup!)
                if (txClass == Av1Constants.TxClass2D)
                {
                    for (int c = eob - 2; c >= 0; c--)
                    {
                        int pos = scan[c];
                        int row = pos >> bwl;
                        int col = pos & posMask;
                        int o = row * lvStride + col;

                        int v0 = lv[o + 1];
                        int v1 = lv[o + lvStride];
                        int v2 = lv[o + lvStride + 1];
                        int v3 = lv[o + 2];
                        int v4 = lv[o + 2 * lvStride];
                        int mag = (v0 > 3 ? 3 : v0) + (v1 > 3 ? 3 : v1) + (v2 > 3 ? 3 : v2) + (v3 > 3 ? 3 : v3) + (v4 > 3 ? 3 : v4);
                        int ctx = (mag + 1) >> 1;
                        if (ctx > 4) ctx = 4;
                        int baseCtx = pos == 0
                            ? 0
                            : ctx + ctxOffTable[txSzCtxTableOff + (row > 4 ? 4 : row) * 5 + (col > 4 ? 4 : col)];

                        int level = sym.ReadSymbol4(_cdf.CoeffBase.AsSpan((cbBaseOff + baseCtx) * 5, 5));
                        if (level > 2)
                        {
                            int brMag = (v0 + v1 + v2 + 1) >> 1;
                            if (brMag > 6) brMag = 6;
                            int brCtx = brMag + (pos == 0 ? 0 : (row < 2 && col < 2) ? 7 : 14);
                            Span<ushort> brCdf = _cdf.CoeffBr.AsSpan((brBaseOff + brCtx) * 5, 5);
                            for (int idx = 0; idx < 4; idx++)
                            {
                                int br = sym.ReadSymbol4(brCdf);
                                level += br;
                                if (br < 3) break;
                            }
                        }
                        if (level != 0)
                        {
                            quant[pos] = level;
                            lv[o] = (byte)(level > 15 ? 15 : level);
                        }
                    }
                }
                else
                {
                    int off3 = txClass == Av1Constants.TxClassHoriz ? 2 : 2 * lvStride;
                    int off4 = txClass == Av1Constants.TxClassHoriz ? 3 : 3 * lvStride;
                    int off5 = txClass == Av1Constants.TxClassHoriz ? 4 : 4 * lvStride;
                    for (int c = eob - 2; c >= 0; c--)
                    {
                        int pos = scan[c];
                        int row = pos >> bwl;
                        int col = pos & posMask;
                        int o = row * lvStride + col;

                        int v0 = lv[o + 1];
                        int v1 = lv[o + lvStride];
                        int v2 = lv[o + off3];
                        int v3 = lv[o + off4];
                        int v4 = lv[o + off5];
                        int mag = (v0 > 3 ? 3 : v0) + (v1 > 3 ? 3 : v1) + (v2 > 3 ? 3 : v2) + (v3 > 3 ? 3 : v3) + (v4 > 3 ? 3 : v4);
                        int ctx = (mag + 1) >> 1;
                        if (ctx > 4) ctx = 4;
                        int pIdx = txClass == Av1Constants.TxClassVert ? row : col;
                        int baseCtx = ctx + CoeffBasePosCtxOffset[pIdx > 2 ? 2 : pIdx];

                        int level = sym.ReadSymbol4(_cdf.CoeffBase.AsSpan((cbBaseOff + baseCtx) * 5, 5));
                        if (level > 2)
                        {
                            int brMag = (v0 + v1 + v2 + 1) >> 1;
                            if (brMag > 6) brMag = 6;
                            int brCtx = brMag + (pos == 0 ? 0 : (pIdx == 0 ? 7 : 14));
                            Span<ushort> brCdf = _cdf.CoeffBr.AsSpan((brBaseOff + brCtx) * 5, 5);
                            for (int idx = 0; idx < 4; idx++)
                            {
                                int br = sym.ReadSymbol4(brCdf);
                                level += br;
                                if (br < 3) break;
                            }
                        }
                        if (level != 0)
                        {
                            quant[pos] = level;
                            lv[o] = (byte)(level > 15 ? 15 : level);
                        }
                    }
                }

                // Signs & Golomb-Rice
                for (int c = 0; c < eob; c++)
                {
                    int pos = scan[c];
                    int q = quant[pos];
                    if (q == 0) continue;

                    int sign = c == 0
                        ? sym.ReadBoolCdf(_cdf.DcSign.AsSpan((ptype * 3 + GetDcSignCtx(plane, x4, y4, w4, h4)) * 3, 3))
                        : sym.ReadBool();

                    if (q > 14)
                    {
                        int length = 0;
                        do
                        {
                            length++;
                            if (length > 20)
                                throw new InvalidDataException("AV1 Golomb coefficient overflow.");
                        } while (sym.ReadBool() == 0);

                        int xVal = 1;
                        for (int i = length - 2; i >= 0; i--)
                            xVal = (xVal << 1) | sym.ReadBool();
                        q = xVal + 14;
                    }

                    if (pos == 0)
                        dcCategory = sign != 0 ? 1 : 2;

                    q &= 0xFFFFF;
                    culLevel += q;
                    quant[pos] = sign != 0 ? -q : q;
                }

                culLevel = Math.Min(63, culLevel);
            }
        }

        byte culB = (byte)culLevel;
        byte dcB = (byte)dcCategory;
        for (int i = 0; i < w4; i++)
        {
            _aboveLevelContext[plane][x4 + i] = culB;
            _aboveDcContext[plane][x4 + i] = dcB;
        }
        for (int i = 0; i < h4; i++)
        {
            _leftLevelContext[plane][y4 + i] = culB;
            _leftDcContext[plane][y4 + i] = dcB;
        }
        return eob;
    }

    private int GetAllZeroCtx(int plane, int txSz, int x4, int y4, int w4, int h4)
    {
        int maxX4 = plane > 0 ? (_fh.MiCols >> _seq.SubsamplingX) : _fh.MiCols;
        int maxY4 = plane > 0 ? (_fh.MiRows >> _seq.SubsamplingY) : _fh.MiRows;
        int w = Av1Constants.TxWidth[txSz];
        int h = Av1Constants.TxHeight[txSz];
        int bsize = GetPlaneResidualSize(_miSize, plane);
        int bw = Av1Constants.BlockWidth[bsize];
        int bh = Av1Constants.BlockHeight[bsize];

        if (plane == 0)
        {
            if (bw == w && bh == h) return 0;
            int top = 0, left = 0;
            for (int k = 0; k < w4 && x4 + k < maxX4; k++)
                top = Math.Max(top, _aboveLevelContext[0][x4 + k]);
            for (int k = 0; k < h4 && y4 + k < maxY4; k++)
                left = Math.Max(left, _leftLevelContext[0][y4 + k]);
            if (top == 0 && left == 0) return 1;
            if (top == 0 || left == 0) return 2 + (Math.Max(top, left) > 3 ? 1 : 0);
            if (Math.Max(top, left) <= 3) return 4;
            if (Math.Min(top, left) <= 3) return 5;
            return 6;
        }

        int above = 0, leftC = 0;
        for (int i = 0; i < w4 && x4 + i < maxX4; i++)
            above |= _aboveLevelContext[plane][x4 + i] | _aboveDcContext[plane][x4 + i];
        for (int i = 0; i < h4 && y4 + i < maxY4; i++)
            leftC |= _leftLevelContext[plane][y4 + i] | _leftDcContext[plane][y4 + i];
        int ctx = (above != 0 ? 1 : 0) + (leftC != 0 ? 1 : 0) + 7;
        if (bw * bh > w * h) ctx += 3;
        return ctx;
    }

    private int GetCoeffBaseCtx(int txSz, int txClass, int bwl, int width, int height, int pos)
    {
        int row = pos >> bwl;
        int col = pos - (row << bwl);
        int mag = 0;
        for (int idx = 0; idx < 5; idx++)
        {
            int rr = row + SigRefDiffOffset[txClass, idx, 0];
            int rc = col + SigRefDiffOffset[txClass, idx, 1];
            if ((uint)rr < (uint)height && (uint)rc < (uint)width)
                mag += Math.Min(Math.Abs(_quant[(rr << bwl) + rc]), 3);
        }
        int ctx = Math.Min((mag + 1) >> 1, 4);
        if (txClass == Av1Constants.TxClass2D)
        {
            if (row == 0 && col == 0) return 0;
            return ctx + CoeffBaseCtxOffset[(txSz * 5 + Math.Min(row, 4)) * 5 + Math.Min(col, 4)];
        }
        int pIdx = txClass == Av1Constants.TxClassVert ? row : col;
        return ctx + CoeffBasePosCtxOffset[Math.Min(pIdx, 2)];
    }

    private int GetCoeffBrCtx(int txClass, int bwl, int width, int height, int pos)
    {
        int row = pos >> bwl;
        int col = pos - (row << bwl);
        int mag = 0;
        for (int idx = 0; idx < 3; idx++)
        {
            int rr = row + MagRefOffsetWithTxClass[txClass, idx, 0];
            int rc = col + MagRefOffsetWithTxClass[txClass, idx, 1];
            if ((uint)rr < (uint)height && (uint)rc < (uint)width)
                mag += Math.Min(_quant[(rr << bwl) + rc], 15);
        }
        mag = Math.Min((mag + 1) >> 1, 6);
        if (pos == 0) return mag;
        if (txClass == Av1Constants.TxClass2D)
            return mag + (row < 2 && col < 2 ? 7 : 14);
        if (txClass == Av1Constants.TxClassHoriz)
            return mag + (col == 0 ? 7 : 14);
        return mag + (row == 0 ? 7 : 14);
    }

    private int GetDcSignCtx(int plane, int x4, int y4, int w4, int h4)
    {
        int maxX4 = plane > 0 ? (_fh.MiCols >> _seq.SubsamplingX) : _fh.MiCols;
        int maxY4 = plane > 0 ? (_fh.MiRows >> _seq.SubsamplingY) : _fh.MiRows;
        int dcSign = 0;
        for (int k = 0; k < w4 && x4 + k < maxX4; k++)
        {
            int s = _aboveDcContext[plane][x4 + k];
            if (s == 1) dcSign--;
            else if (s == 2) dcSign++;
        }
        for (int k = 0; k < h4 && y4 + k < maxY4; k++)
        {
            int s = _leftDcContext[plane][y4 + k];
            if (s == 1) dcSign--;
            else if (s == 2) dcSign++;
        }
        return dcSign < 0 ? 1 : dcSign > 0 ? 2 : 0;
    }

    private int GetTxSet(int txSz)
    {
        int sqr = Av1Constants.TxSizeSqr[txSz];
        int sqrUp = Av1Constants.TxSizeSqrUp[txSz];
        if (sqrUp > Av1Constants.Tx32x32)
            return Av1Constants.TxSetDctOnly;
        if (_isInter)
        {
            if (_fh.ReducedTxSet || sqrUp == Av1Constants.Tx32x32) return Av1Constants.TxSetInter3;
            if (sqr == Av1Constants.Tx16x16) return Av1Constants.TxSetInter2;
            return Av1Constants.TxSetInter1;
        }
        if (sqrUp == Av1Constants.Tx32x32)
            return Av1Constants.TxSetDctOnly;
        if (_fh.ReducedTxSet || sqr == Av1Constants.Tx16x16)
            return Av1Constants.TxSetIntra2;
        return Av1Constants.TxSetIntra1;
    }

    private void ReadTransformType(ref Av1SymbolDecoder sym, int x4, int y4, int txSz)
    {
        int set = GetTxSet(txSz);
        int qidx = _fh.SegmentationEnabled ? _fh.GetQIndex(ignoreDeltaQ: true, _segmentId, _currentQIndex) : _fh.BaseQIdx;
        int txType;
        if (set > 0 && qidx > 0)
        {
            int sqr = Av1Constants.TxSizeSqr[txSz];
            if (_isInter)
            {
                if (set == Av1Constants.TxSetInter1)
                {
                    int s = sym.ReadSymbol(_cdf.InterTxTypeSet1.AsSpan(sqr * 17, 17));
                    txType = Av1Constants.TxTypeInterInvSet1[s];
                }
                else if (set == Av1Constants.TxSetInter2)
                {
                    int s = sym.ReadSymbol(_cdf.InterTxTypeSet2);
                    txType = Av1Constants.TxTypeInterInvSet2[s];
                }
                else
                {
                    int s = sym.ReadSymbol(_cdf.InterTxTypeSet3.AsSpan(sqr * 3, 3));
                    txType = Av1Constants.TxTypeInterInvSet3[s];
                }
            }
            else
            {
                int intraDir = _useFilterIntra ? Av1Constants.FilterIntraModeToIntraDir[_filterIntraMode] : _yMode;
                if (set == Av1Constants.TxSetIntra1)
                {
                    int s = sym.ReadSymbol(_cdf.IntraTxTypeSet1.AsSpan((sqr * 13 + intraDir) * 8, 8));
                    txType = Av1Constants.TxTypeIntraInvSet1[s];
                }
                else
                {
                    int s = sym.ReadSymbol(_cdf.IntraTxTypeSet2.AsSpan((sqr * 13 + intraDir) * 6, 6));
                    txType = Av1Constants.TxTypeIntraInvSet2[s];
                }
            }
        }
        else
        {
            txType = Av1Constants.DctDct;
        }

        int w4 = Av1Constants.TxWidth[txSz] >> 2;
        int h4 = Av1Constants.TxHeight[txSz] >> 2;
        int jEnd = Math.Min(h4, _st.MiRows - y4);
        int iEnd = Math.Min(w4, _st.MiCols - x4);
        for (int j = 0; j < jEnd; j++)
            for (int i = 0; i < iEnd; i++)
                _txTypes[(y4 + j) * _st.MiCols + (x4 + i)] = (byte)txType;
    }

    private int ComputeTxType(int plane, int txSz, int x4, int y4)
    {
        if (_lossless || Av1Constants.TxSizeSqrUp[txSz] > Av1Constants.Tx32x32)
            return Av1Constants.DctDct;
        if (plane == 0)
            return _txTypes[y4 * _st.MiCols + x4];
        int txSet = GetTxSet(txSz);
        if (_isInter)
        {
            int x4L = Math.Max(_miCol, x4 << _seq.SubsamplingX);
            int y4L = Math.Max(_miRow, y4 << _seq.SubsamplingY);
            int interTxType = _txTypes[y4L * _st.MiCols + x4L];
            int interRow = txSet switch
            {
                Av1Constants.TxSetInter1 => 1,
                Av1Constants.TxSetInter2 => 2,
                Av1Constants.TxSetInter3 => 3,
                _ => 0,
            };
            return TxTypeInSetInter[interRow, interTxType] ? interTxType : Av1Constants.DctDct;
        }
        int txType = Av1Constants.ModeToTxfm[_uvMode];
        return TxTypeInSetIntra[txSet, txType] ? txType : Av1Constants.DctDct;
    }

    private static ushort[] GetScan(int txSz, int txType)
    {
        if (txSz == Av1Constants.Tx16x64) return Av1Tables.DefaultScan16x32;
        if (txSz == Av1Constants.Tx64x16) return Av1Tables.DefaultScan32x16;
        if (Av1Constants.TxSizeSqrUp[txSz] == Av1Constants.Tx64x64) return Av1Tables.DefaultScan32x32;
        if (txType is Av1Constants.VDct or Av1Constants.VAdst or Av1Constants.VFlipAdst)
        {
            return txSz switch
            {
                Av1Constants.Tx4x4 => Av1Tables.MrowScan4x4,
                Av1Constants.Tx4x8 => Av1Tables.MrowScan4x8,
                Av1Constants.Tx8x4 => Av1Tables.MrowScan8x4,
                Av1Constants.Tx8x8 => Av1Tables.MrowScan8x8,
                Av1Constants.Tx8x16 => Av1Tables.MrowScan8x16,
                Av1Constants.Tx16x8 => Av1Tables.MrowScan16x8,
                Av1Constants.Tx16x16 => Av1Tables.MrowScan16x16,
                Av1Constants.Tx4x16 => Av1Tables.MrowScan4x16,
                _ => Av1Tables.MrowScan16x4,
            };
        }
        if (txType is Av1Constants.HDct or Av1Constants.HAdst or Av1Constants.HFlipAdst)
        {
            return txSz switch
            {
                Av1Constants.Tx4x4 => Av1Tables.McolScan4x4,
                Av1Constants.Tx4x8 => Av1Tables.McolScan4x8,
                Av1Constants.Tx8x4 => Av1Tables.McolScan8x4,
                Av1Constants.Tx8x8 => Av1Tables.McolScan8x8,
                Av1Constants.Tx8x16 => Av1Tables.McolScan8x16,
                Av1Constants.Tx16x8 => Av1Tables.McolScan16x8,
                Av1Constants.Tx16x16 => Av1Tables.McolScan16x16,
                Av1Constants.Tx4x16 => Av1Tables.McolScan4x16,
                _ => Av1Tables.McolScan16x4,
            };
        }
        return txSz switch
        {
            Av1Constants.Tx4x4 => Av1Tables.DefaultScan4x4,
            Av1Constants.Tx4x8 => Av1Tables.DefaultScan4x8,
            Av1Constants.Tx8x4 => Av1Tables.DefaultScan8x4,
            Av1Constants.Tx8x8 => Av1Tables.DefaultScan8x8,
            Av1Constants.Tx8x16 => Av1Tables.DefaultScan8x16,
            Av1Constants.Tx16x8 => Av1Tables.DefaultScan16x8,
            Av1Constants.Tx16x16 => Av1Tables.DefaultScan16x16,
            Av1Constants.Tx16x32 => Av1Tables.DefaultScan16x32,
            Av1Constants.Tx32x16 => Av1Tables.DefaultScan32x16,
            Av1Constants.Tx4x16 => Av1Tables.DefaultScan4x16,
            Av1Constants.Tx16x4 => Av1Tables.DefaultScan16x4,
            Av1Constants.Tx8x32 => Av1Tables.DefaultScan8x32,
            Av1Constants.Tx32x8 => Av1Tables.DefaultScan32x8,
            _ => Av1Tables.DefaultScan32x32,
        };
    }

    // --- Palette (§5.11.46, §5.11.49, §5.11.50) ---

    private void PaletteModeInfo(ref Av1SymbolDecoder sym)
    {
        int bsizeCtx = Av1Constants.MiWidthLog2[_miSize] + Av1Constants.MiHeightLog2[_miSize] - 2;
        int bitDepth = _seq.BitDepth;

        if (_yMode == Av1Constants.DcPred)
        {
            int ctx = 0;
            if (_availU && _paletteSizesY![(_miRow - 1) * _st.MiCols + _miCol] > 0) ctx++;
            if (_availL && _paletteSizesY![_miRow * _st.MiCols + (_miCol - 1)] > 0) ctx++;
            if (sym.ReadSymbol(_cdf.PaletteYMode.AsSpan((bsizeCtx * 3 + ctx) * 3, 3)) != 0)
            {
                _paletteSizeY = sym.ReadSymbol(_cdf.PaletteYSize.AsSpan(bsizeCtx * 8, 8)) + 2;
                int cacheN = GetPaletteCache(isChroma: false);
                int idx = 0;
                for (int i = 0; i < cacheN && idx < _paletteSizeY; i++)
                {
                    if (sym.ReadBool() != 0)
                        _curPalY[idx++] = _palCache[i];
                }
                if (idx < _paletteSizeY)
                    _curPalY[idx++] = sym.ReadLiteral(bitDepth);
                int palBits = 0;
                if (idx < _paletteSizeY)
                    palBits = (bitDepth - 3) + sym.ReadLiteral(2);
                while (idx < _paletteSizeY)
                {
                    int delta = sym.ReadLiteral(palBits) + 1;
                    _curPalY[idx] = Av1Constants.Clip1(_curPalY[idx - 1] + delta, bitDepth);
                    int range = (1 << bitDepth) - _curPalY[idx] - 1;
                    palBits = Math.Min(palBits, Av1Constants.CeilLog2(range));
                    idx++;
                }
                Array.Sort(_curPalY, 0, _paletteSizeY);
            }
        }

        if (_hasChroma && _uvMode == Av1Constants.DcPred)
        {
            int ctx = _paletteSizeY > 0 ? 1 : 0;
            if (sym.ReadSymbol(_cdf.PaletteUvMode.AsSpan(ctx * 3, 3)) != 0)
            {
                _paletteSizeUv = sym.ReadSymbol(_cdf.PaletteUvSize.AsSpan(bsizeCtx * 8, 8)) + 2;
                int cacheN = GetPaletteCache(isChroma: true);
                int idx = 0;
                for (int i = 0; i < cacheN && idx < _paletteSizeUv; i++)
                {
                    if (sym.ReadBool() != 0)
                        _curPalU[idx++] = _palCache[i];
                }
                if (idx < _paletteSizeUv)
                    _curPalU[idx++] = sym.ReadLiteral(bitDepth);
                int palBits = 0;
                if (idx < _paletteSizeUv)
                    palBits = (bitDepth - 3) + sym.ReadLiteral(2);
                while (idx < _paletteSizeUv)
                {
                    int delta = sym.ReadLiteral(palBits);
                    _curPalU[idx] = Av1Constants.Clip1(_curPalU[idx - 1] + delta, bitDepth);
                    int range = (1 << bitDepth) - _curPalU[idx];
                    palBits = Math.Min(palBits, Av1Constants.CeilLog2(range));
                    idx++;
                }
                Array.Sort(_curPalU, 0, _paletteSizeUv);

                if (sym.ReadBool() != 0) // delta_encode_palette_colors_v
                {
                    int maxVal = 1 << bitDepth;
                    int vBits = (bitDepth - 4) + sym.ReadLiteral(2);
                    _curPalV[0] = sym.ReadLiteral(bitDepth);
                    for (int i = 1; i < _paletteSizeUv; i++)
                    {
                        int delta = sym.ReadLiteral(vBits);
                        if (delta != 0 && sym.ReadBool() != 0)
                            delta = -delta;
                        int val = _curPalV[i - 1] + delta;
                        if (val < 0) val += maxVal;
                        if (val >= maxVal) val -= maxVal;
                        _curPalV[i] = Av1Constants.Clip1(val, bitDepth);
                    }
                }
                else
                {
                    for (int i = 0; i < _paletteSizeUv; i++)
                        _curPalV[i] = sym.ReadLiteral(bitDepth);
                }
            }
        }
    }

    private int GetPaletteCache(bool isChroma)
    {
        byte[] sizes = isChroma ? _paletteSizesUv! : _paletteSizesY!;
        ushort[] colors = isChroma ? _paletteColorsUv! : _paletteColorsY!;
        int aboveN = ((_miRow * 4) % 64 != 0) ? sizes[(_miRow - 1) * _st.MiCols + _miCol] : 0;
        int leftN = _availL ? sizes[_miRow * _st.MiCols + (_miCol - 1)] : 0;
        int aboveOff = ((_miRow - 1) * _st.MiCols + _miCol) * 8;
        int leftOff = (_miRow * _st.MiCols + (_miCol - 1)) * 8;

        int ai = 0, li = 0, n = 0;
        while (ai < aboveN && li < leftN)
        {
            int ac = colors[aboveOff + ai];
            int lc = colors[leftOff + li];
            if (lc < ac)
            {
                if (n == 0 || lc != _palCache[n - 1]) _palCache[n++] = lc;
                li++;
            }
            else
            {
                if (n == 0 || ac != _palCache[n - 1]) _palCache[n++] = ac;
                ai++;
                if (lc == ac) li++;
            }
        }
        while (ai < aboveN)
        {
            int v = colors[aboveOff + ai++];
            if (n == 0 || v != _palCache[n - 1]) _palCache[n++] = v;
        }
        while (li < leftN)
        {
            int v = colors[leftOff + li++];
            if (n == 0 || v != _palCache[n - 1]) _palCache[n++] = v;
        }
        return n;
    }

    private void PaletteTokens(ref Av1SymbolDecoder sym)
    {
        if (_paletteSizeY == 0 && _paletteSizeUv == 0) return;

        int blockH = Av1Constants.BlockHeight[_miSize];
        int blockW = Av1Constants.BlockWidth[_miSize];
        int onH = Math.Min(blockH, (_fh.MiRows - _miRow) * 4);
        int onW = Math.Min(blockW, (_fh.MiCols - _miCol) * 4);

        if (_paletteSizeY > 0)
            DecodePaletteMap(ref sym, _colorMapY, blockW, blockH, onW, onH, _paletteSizeY, isChroma: false);

        if (_paletteSizeUv > 0)
        {
            int bh = blockH >> _seq.SubsamplingY;
            int bw = blockW >> _seq.SubsamplingX;
            int oh = onH >> _seq.SubsamplingY;
            int ow = onW >> _seq.SubsamplingX;
            if (bw < 4) { bw += 2; ow += 2; }
            if (bh < 4) { bh += 2; oh += 2; }
            DecodePaletteMap(ref sym, _colorMapUv, bw, bh, ow, oh, _paletteSizeUv, isChroma: true);
        }
    }

    private void DecodePaletteMap(ref Av1SymbolDecoder sym, byte[] map, int blockW, int blockH, int onW, int onH, int palSize, bool isChroma)
    {
        map[0] = (byte)ReadNsBool(ref sym, palSize);
        ushort[] cdfTable = isChroma ? _cdf.PaletteUvColor[palSize - 2] : _cdf.PaletteYColor[palSize - 2];
        int strideCdf = palSize + 1;
        Span<int> colorOrder = stackalloc int[8];
        Span<int> scores = stackalloc int[8];

        for (int i = 1; i < onH + onW - 1; i++)
        {
            int jStart = Math.Min(i, onW - 1);
            int jEnd = Math.Max(0, i - onH + 1);
            for (int j = jStart; j >= jEnd; j--)
            {
                int r = i - j;
                int c = j;
                for (int k = 0; k < palSize; k++) { scores[k] = 0; colorOrder[k] = k; }
                if (c > 0) scores[map[r * 64 + (c - 1)]] += 2;
                if (r > 0 && c > 0) scores[map[(r - 1) * 64 + (c - 1)]] += 1;
                if (r > 0) scores[map[(r - 1) * 64 + c]] += 2;

                for (int p = 0; p < 3; p++)
                {
                    int maxScore = scores[p];
                    int maxIdx = p;
                    for (int q = p + 1; q < palSize; q++)
                    {
                        if (scores[q] > maxScore) { maxScore = scores[q]; maxIdx = q; }
                    }
                    if (maxIdx != p)
                    {
                        int ms = scores[maxIdx];
                        int mo = colorOrder[maxIdx];
                        for (int k = maxIdx; k > p; k--) { scores[k] = scores[k - 1]; colorOrder[k] = colorOrder[k - 1]; }
                        scores[p] = ms;
                        colorOrder[p] = mo;
                    }
                }
                int hash = scores[0] * Av1Tables.PaletteColorHashMultipliers[0]
                         + scores[1] * Av1Tables.PaletteColorHashMultipliers[1]
                         + scores[2] * Av1Tables.PaletteColorHashMultipliers[2];
                int ctx = Av1Tables.PaletteColorContext[hash];
                int idx = sym.ReadSymbol(cdfTable.AsSpan(ctx * strideCdf, strideCdf));
                map[r * 64 + c] = (byte)colorOrder[idx];
            }
        }

        for (int i = 0; i < onH; i++)
        {
            byte last = map[i * 64 + (onW - 1)];
            for (int j = onW; j < blockW; j++) map[i * 64 + j] = last;
        }
        for (int i = onH; i < blockH; i++)
            map.AsSpan((onH - 1) * 64, blockW).CopyTo(map.AsSpan(i * 64, blockW));
    }

    // --- Loop Restoration per-SB syntax (§5.11.57, §5.11.58) ---

    private void ReadLr(ref Av1SymbolDecoder sym, int r, int c, int bSize)
    {
        if (!_fh.UsesLr || _fh.AllowIntrabc) return;
        int w = Av1Constants.Num4x4BlocksWide[bSize];
        int h = Av1Constants.Num4x4BlocksHigh[bSize];

        for (int plane = 0; plane < _seq.NumPlanes; plane++)
        {
            int frameLrType = _fh.FrameRestorationType[plane];
            if (frameLrType == Av1Constants.RestoreNone) continue;

            int subX = plane == 0 ? 0 : _seq.SubsamplingX;
            int subY = plane == 0 ? 0 : _seq.SubsamplingY;
            int unitSize = _fh.LoopRestorationSize[plane];
            int unitRows = _st.LrUnitRows[plane];
            int unitCols = _st.LrUnitCols[plane];

            int stepY = 4 >> subY;
            int stepX = 4 >> subX;
            int uRowStart = (r * stepY + unitSize - 1) / unitSize;
            int uRowEnd = Math.Min(unitRows, ((r + h) * stepY + unitSize - 1) / unitSize);
            int numerator = _fh.UseSuperres ? stepX * _fh.SuperresDenom : stepX;
            int denominator = _fh.UseSuperres ? unitSize * Av1Constants.SuperresNum : unitSize;
            int uColStart = (c * numerator + denominator - 1) / denominator;
            int uColEnd = Math.Min(unitCols, ((c + w) * numerator + denominator - 1) / denominator);

            for (int ur = uRowStart; ur < uRowEnd; ur++)
                for (int uc = uColStart; uc < uColEnd; uc++)
                    ReadLrUnit(ref sym, plane, frameLrType, ur, uc);
        }
    }

    private void ReadLrUnit(ref Av1SymbolDecoder sym, int plane, int frameLrType, int unitRow, int unitCol)
    {
        int restorationType;
        if (frameLrType == Av1Constants.RestoreWiener)
            restorationType = sym.ReadSymbol(_cdf.UseWiener) != 0 ? Av1Constants.RestoreWiener : Av1Constants.RestoreNone;
        else if (frameLrType == Av1Constants.RestoreSgrproj)
            restorationType = sym.ReadSymbol(_cdf.UseSgrproj) != 0 ? Av1Constants.RestoreSgrproj : Av1Constants.RestoreNone;
        else
            restorationType = sym.ReadSymbol(_cdf.RestorationType);

        int uIdx = unitRow * _st.LrUnitCols[plane] + unitCol;
        _st.LrType[plane][uIdx] = (byte)restorationType;

        if (restorationType == Av1Constants.RestoreWiener)
        {
            int wOff = uIdx * 6;
            for (int pass = 0; pass < 2; pass++)
            {
                int firstCoeff = plane > 0 ? 1 : 0;
                if (plane > 0) _st.LrWiener[plane][wOff + pass * 3] = 0;
                for (int j = firstCoeff; j < 3; j++)
                {
                    int v = DecodeSignedSubexpWithRefBool(ref sym, WienerTapsMin[j], WienerTapsMax[j] + 1, WienerTapsK[j], _refLrWiener[plane, pass, j]);
                    _st.LrWiener[plane][wOff + pass * 3 + j] = (sbyte)v;
                    _refLrWiener[plane, pass, j] = v;
                }
            }
        }
        else if (restorationType == Av1Constants.RestoreSgrproj)
        {
            int set = sym.ReadLiteral(4); // SGRPROJ_PARAMS_BITS = 4
            _st.LrSgrSet[plane][uIdx] = (byte)set;
            int xOff = uIdx * 2;
            for (int i = 0; i < 2; i++)
            {
                int radius = Av1Tables.SgrParams[set * 4 + i * 2];
                int min = SgrprojXqdMin[i];
                int max = SgrprojXqdMax[i];
                int v;
                if (radius != 0)
                {
                    v = DecodeSignedSubexpWithRefBool(ref sym, min, max + 1, 4, _refSgrXqd[plane, i]);
                }
                else
                {
                    v = i == 1 ? Av1Constants.Clip3(min, max, (1 << 7) - _refSgrXqd[plane, 0]) : 0;
                }
                _st.LrSgrXqd[plane][xOff + i] = (sbyte)v;
                _refSgrXqd[plane, i] = v;
            }
        }
    }

    private static int DecodeSignedSubexpWithRefBool(ref Av1SymbolDecoder sym, int low, int high, int k, int r)
    {
        int mx = high - low;
        int rr = r - low;
        int v = DecodeSubexpBool(ref sym, mx, k);
        int u = (rr << 1) <= mx ? InverseRecenter(rr, v) : mx - 1 - InverseRecenter(mx - 1 - rr, v);
        return u + low;
    }

    private static int DecodeSubexpBool(ref Av1SymbolDecoder sym, int numSyms, int k)
    {
        int i = 0;
        int mk = 0;
        while (true)
        {
            int b2 = i != 0 ? k + i - 1 : k;
            int a = 1 << b2;
            if (numSyms <= mk + 3 * a)
                return ReadNsBool(ref sym, numSyms - mk) + mk;
            if (sym.ReadBool() != 0)
            {
                i++;
                mk += a;
            }
            else
            {
                return sym.ReadLiteral(b2) + mk;
            }
        }
    }

    private static int InverseRecenter(int r, int v)
    {
        if (v > 2 * r) return v;
        return (v & 1) != 0 ? r - ((v + 1) >> 1) : r + (v >> 1);
    }

    private static int ReadNsBool(ref Av1SymbolDecoder sym, int n)
    {
        if (n <= 1) return 0;
        int w = Av1Constants.FloorLog2(n) + 1;
        int m = (1 << w) - n;
        int v = sym.ReadLiteral(w - 1);
        if (v < m) return v;
        return (v << 1) - m + sym.ReadBool();
    }

    // --- Inter frame mode info, MV stack, decoding & prediction (§5.11.18–33, §7.10, §7.11.3) ---

    private bool SegFeatureActive(int feature) =>
        _fh.SegmentationEnabled && _fh.FeatureEnabled[_segmentId, feature];

    private void InterFrameModeInfo(ref Av1SymbolDecoder sym)
    {
        _useIntrabc = false;
        int miCols = _st.MiCols;
        int leftIdx = _miRow * miCols + (_miCol - 1);
        int aboveIdx = (_miRow - 1) * miCols + _miCol;
        _leftRef0 = _availL ? _st.RefFrames[leftIdx * 2] : Av1Constants.IntraFrame;
        _aboveRef0 = _availU ? _st.RefFrames[aboveIdx * 2] : Av1Constants.IntraFrame;
        _leftRef1 = _availL ? _st.RefFrames[leftIdx * 2 + 1] : Av1Constants.NoneFrame;
        _aboveRef1 = _availU ? _st.RefFrames[aboveIdx * 2 + 1] : Av1Constants.NoneFrame;
        _leftIntra = _leftRef0 <= Av1Constants.IntraFrame;
        _aboveIntra = _aboveRef0 <= Av1Constants.IntraFrame;
        _leftSingle = _leftRef1 <= Av1Constants.IntraFrame;
        _aboveSingle = _aboveRef1 <= Av1Constants.IntraFrame;

        _skip = false;
        _skipMode = false;
        _isInterIntra = false;
        _motionMode = Av1Constants.MotionSimple;
        _compoundType = Av1Constants.CompoundAverage;
        _compGroupIdx = 0;
        _compoundIdx = 1;
        _mv0Y = _mv0X = _mv1Y = _mv1X = 0;

        InterSegmentId(ref sym, preSkip: true);
        ReadSkipMode(ref sym);
        if (_skipMode)
            _skip = true;
        else
            ReadSkip(ref sym);

        if (!_fh.SegIdPreSkip)
            InterSegmentId(ref sym, preSkip: false);

        _lossless = _fh.LosslessArray[_segmentId];
        ReadCdef(ref sym);
        ReadDeltaQIndex(ref sym);
        ReadDeltaLf(ref sym);
        _readDeltas = false;

        ReadIsInter(ref sym);
        if (_isInter)
            InterBlockModeInfo(ref sym);
        else
            IntraBlockModeInfo(ref sym);
    }

    private void InterSegmentId(ref Av1SymbolDecoder sym, bool preSkip)
    {
        if (!_fh.SegmentationEnabled)
        {
            _segmentId = 0;
            return;
        }
        int predictedSegId = GetPredictedSegmentId();
        if (_fh.SegmentationUpdateMap)
        {
            if (preSkip && !_fh.SegIdPreSkip)
            {
                _segmentId = 0;
                return;
            }
            int bw4 = Av1Constants.Num4x4BlocksWide[_miSize];
            int bh4 = Av1Constants.Num4x4BlocksHigh[_miSize];
            if (!preSkip && _skip)
            {
                _aboveSegPredContext.AsSpan(_miCol, bw4).Clear();
                _leftSegPredContext.AsSpan(_miRow, bh4).Clear();
                ReadSegmentId(ref sym);
                return;
            }
            if (_fh.SegmentationTemporalUpdate)
            {
                int ctx = _leftSegPredContext[_miRow] + _aboveSegPredContext[_miCol];
                int segIdPred = sym.ReadSymbol(_cdf.SegmentIdPredicted.AsSpan(ctx * 3, 3));
                if (segIdPred != 0)
                    _segmentId = predictedSegId;
                else
                    ReadSegmentId(ref sym);
                _aboveSegPredContext.AsSpan(_miCol, bw4).Fill((byte)segIdPred);
                _leftSegPredContext.AsSpan(_miRow, bh4).Fill((byte)segIdPred);
            }
            else
            {
                ReadSegmentId(ref sym);
            }
        }
        else
        {
            _segmentId = predictedSegId;
        }
    }

    private int GetPredictedSegmentId()
    {
        int bw4 = Math.Min(_fh.MiCols - _miCol, Av1Constants.Num4x4BlocksWide[_miSize]);
        int bh4 = Math.Min(_fh.MiRows - _miRow, Av1Constants.Num4x4BlocksHigh[_miSize]);
        int seg = 7;
        for (int y = 0; y < bh4; y++)
        {
            int rowOff = (_miRow + y) * _fh.MiCols + _miCol;
            for (int x = 0; x < bw4; x++)
                seg = Math.Min(seg, _fh.PrevSegmentIds[rowOff + x]);
        }
        return seg;
    }

    private void ReadSkipMode(ref Av1SymbolDecoder sym)
    {
        if (SegFeatureActive(Av1Constants.SegLvlSkip)
            || SegFeatureActive(Av1Constants.SegLvlRefFrame)
            || SegFeatureActive(Av1Constants.SegLvlGlobalMv)
            || !_fh.SkipModePresent
            || Av1Constants.BlockWidth[_miSize] < 8
            || Av1Constants.BlockHeight[_miSize] < 8)
        {
            _skipMode = false;
            return;
        }
        int ctx = 0;
        if (_availU) ctx += _st.SkipModes[(_miRow - 1) * _st.MiCols + _miCol];
        if (_availL) ctx += _st.SkipModes[_miRow * _st.MiCols + (_miCol - 1)];
        _skipMode = sym.ReadSymbol(_cdf.SkipMode.AsSpan(ctx * 3, 3)) != 0;
    }

    private void ReadIsInter(ref Av1SymbolDecoder sym)
    {
        if (_skipMode)
        {
            _isInter = true;
        }
        else if (SegFeatureActive(Av1Constants.SegLvlRefFrame))
        {
            _isInter = _fh.FeatureData[_segmentId, Av1Constants.SegLvlRefFrame] != Av1Constants.IntraFrame;
        }
        else if (SegFeatureActive(Av1Constants.SegLvlGlobalMv))
        {
            _isInter = true;
        }
        else
        {
            int ctx;
            if (_availU && _availL)
                ctx = (_leftIntra && _aboveIntra) ? 3 : ((_leftIntra || _aboveIntra) ? 1 : 0);
            else if (_availU || _availL)
                ctx = 2 * ((_availU ? _aboveIntra : _leftIntra) ? 1 : 0);
            else
                ctx = 0;
            _isInter = sym.ReadSymbol(_cdf.IsInter.AsSpan(ctx * 3, 3)) != 0;
        }
    }

    private void IntraBlockModeInfo(ref Av1SymbolDecoder sym)
    {
        _refFrame0 = Av1Constants.IntraFrame;
        _refFrame1 = Av1Constants.NoneFrame;
        int ctx = Av1Constants.SizeGroup[_miSize];
        _yMode = sym.ReadSymbol(_cdf.YMode.AsSpan(ctx * 14, 14));
        ReadIntraBlockTail(ref sym);
    }

    private void InterBlockModeInfo(ref Av1SymbolDecoder sym)
    {
        _paletteSizeY = 0;
        _paletteSizeUv = 0;
        _useFilterIntra = false;
        _filterIntraMode = 0;

        ReadRefFrames(ref sym);
        bool isCompound = _refFrame1 > Av1Constants.IntraFrame;
        FindMvStack(isCompound);

        if (_skipMode)
        {
            _yMode = Av1Constants.NearestNearestMv;
        }
        else if (SegFeatureActive(Av1Constants.SegLvlSkip) || SegFeatureActive(Av1Constants.SegLvlGlobalMv))
        {
            _yMode = Av1Constants.GlobalMv;
        }
        else if (isCompound)
        {
            int ctx = Av1Constants.CompoundModeCtxMap[(_refMvContext >> 1) * 5 + Math.Min(_newMvContext, 4)];
            _yMode = Av1Constants.NearestNearestMv + sym.ReadSymbol(_cdf.CompoundMode.AsSpan(ctx * 9, 9));
        }
        else
        {
            int newMv = sym.ReadSymbol(_cdf.NewMv.AsSpan(_newMvContext * 3, 3));
            if (newMv == 0)
            {
                _yMode = Av1Constants.NewMv;
            }
            else
            {
                int zeroMv = sym.ReadSymbol(_cdf.ZeroMv.AsSpan(_zeroMvContext * 3, 3));
                if (zeroMv == 0)
                {
                    _yMode = Av1Constants.GlobalMv;
                }
                else
                {
                    int refMv = sym.ReadSymbol(_cdf.RefMv.AsSpan(_refMvContext * 3, 3));
                    _yMode = refMv == 0 ? Av1Constants.NearestMv : Av1Constants.NearMv;
                }
            }
        }

        _refMvIdx = 0;
        if (_yMode is Av1Constants.NewMv or Av1Constants.NewNewMv)
        {
            for (int idx = 0; idx < 2; idx++)
            {
                if (_numMvFound > idx + 1)
                {
                    int drl = sym.ReadSymbol(_cdf.DrlMode.AsSpan(_drlCtxStack[idx] * 3, 3));
                    if (drl == 0) { _refMvIdx = idx; break; }
                    _refMvIdx = idx + 1;
                }
            }
        }
        else if (_yMode is Av1Constants.NearMv or Av1Constants.NearNearMv or Av1Constants.NearNewMv or Av1Constants.NewNearMv)
        {
            _refMvIdx = 1;
            for (int idx = 1; idx < 3; idx++)
            {
                if (_numMvFound > idx + 1)
                {
                    int drl = sym.ReadSymbol(_cdf.DrlMode.AsSpan(_drlCtxStack[idx] * 3, 3));
                    if (drl == 0) { _refMvIdx = idx; break; }
                    _refMvIdx = idx + 1;
                }
            }
        }

        AssignMv(ref sym, isCompound);
        ReadInterIntraMode(ref sym, isCompound);
        ReadMotionMode(ref sym, isCompound);
        ReadCompoundType(ref sym, isCompound);

        if (_fh.InterpolationFilter == Av1Constants.Switchable)
        {
            int dirs = _seq.EnableDualFilter ? 2 : 1;
            for (int dir = 0; dir < dirs; dir++)
            {
                int f = NeedsInterpFilter() ? sym.ReadSymbol(GetInterpFilterCdf(dir)) : Av1Constants.EightTap;
                if (dir == 0) _interpFilterV = f;
                else _interpFilterH = f;
            }
            if (!_seq.EnableDualFilter)
                _interpFilterH = _interpFilterV;
        }
        else
        {
            _interpFilterV = _fh.InterpolationFilter;
            _interpFilterH = _fh.InterpolationFilter;
        }
    }

    private bool NeedsInterpFilter()
    {
        bool large = Math.Min(Av1Constants.BlockWidth[_miSize], Av1Constants.BlockHeight[_miSize]) >= 8;
        if (_skipMode || _motionMode == Av1Constants.MotionLocalWarp)
            return false;
        if (large && _yMode == Av1Constants.GlobalMv)
            return _fh.GmType[_refFrame0] == Av1Constants.GmTranslation;
        if (large && _yMode == Av1Constants.GlobalGlobalMv)
            return _fh.GmType[_refFrame0] == Av1Constants.GmTranslation || _fh.GmType[_refFrame1] == Av1Constants.GmTranslation;
        return true;
    }

    private Span<ushort> GetInterpFilterCdf(int dir)
    {
        int ctx = ((dir & 1) * 2 + (_refFrame1 > Av1Constants.IntraFrame ? 1 : 0)) * 4;
        int leftType = 3;
        int aboveType = 3;
        int miCols = _st.MiCols;
        if (_availL)
        {
            int idx = _miRow * miCols + (_miCol - 1);
            if (_st.RefFrames[idx * 2] == _refFrame0 || _st.RefFrames[idx * 2 + 1] == _refFrame0)
                leftType = _st.InterpFilters[idx * 2 + dir];
        }
        if (_availU)
        {
            int idx = (_miRow - 1) * miCols + _miCol;
            if (_st.RefFrames[idx * 2] == _refFrame0 || _st.RefFrames[idx * 2 + 1] == _refFrame0)
                aboveType = _st.InterpFilters[idx * 2 + dir];
        }
        if (leftType == aboveType) ctx += leftType;
        else if (leftType == 3) ctx += aboveType;
        else if (aboveType == 3) ctx += leftType;
        else ctx += 3;
        return _cdf.InterpFilter.AsSpan(ctx * 4, 4);
    }

    // §5.11.25 Ref frames syntax
    private void ReadRefFrames(ref Av1SymbolDecoder sym)
    {
        if (_skipMode)
        {
            _refFrame0 = _fh.SkipModeFrame[0];
            _refFrame1 = _fh.SkipModeFrame[1];
            return;
        }
        if (SegFeatureActive(Av1Constants.SegLvlRefFrame))
        {
            _refFrame0 = _fh.FeatureData[_segmentId, Av1Constants.SegLvlRefFrame];
            _refFrame1 = Av1Constants.NoneFrame;
            return;
        }
        if (SegFeatureActive(Av1Constants.SegLvlSkip) || SegFeatureActive(Av1Constants.SegLvlGlobalMv))
        {
            _refFrame0 = Av1Constants.LastFrame;
            _refFrame1 = Av1Constants.NoneFrame;
            return;
        }

        int bw4 = Av1Constants.Num4x4BlocksWide[_miSize];
        int bh4 = Av1Constants.Num4x4BlocksHigh[_miSize];
        int compMode = (_fh.ReferenceSelect && Math.Min(bw4, bh4) >= 2)
            ? sym.ReadSymbol(GetCompModeCdf())
            : Av1Constants.SingleReference;

        if (compMode == Av1Constants.CompoundReference)
        {
            int compRefType = sym.ReadSymbol(GetCompRefTypeCdf());
            if (compRefType == Av1Constants.UnidirCompReference)
            {
                int uni = sym.ReadSymbol(_cdf.UniCompRef.AsSpan((GetSingleRefP1Ctx() * 3 + 0) * 3, 3));
                if (uni != 0)
                {
                    _refFrame0 = Av1Constants.BwdRefFrame;
                    _refFrame1 = Av1Constants.AltRefFrame;
                }
                else
                {
                    int p1Ctx = RefCountCtx(CountRefs(Av1Constants.Last2Frame), CountRefs(Av1Constants.Last3Frame) + CountRefs(Av1Constants.GoldenFrame));
                    int p1 = sym.ReadSymbol(_cdf.UniCompRef.AsSpan((p1Ctx * 3 + 1) * 3, 3));
                    if (p1 != 0)
                    {
                        int p2 = sym.ReadSymbol(_cdf.UniCompRef.AsSpan((GetCompRefP2Ctx() * 3 + 2) * 3, 3));
                        _refFrame0 = Av1Constants.LastFrame;
                        _refFrame1 = p2 != 0 ? Av1Constants.GoldenFrame : Av1Constants.Last3Frame;
                    }
                    else
                    {
                        _refFrame0 = Av1Constants.LastFrame;
                        _refFrame1 = Av1Constants.Last2Frame;
                    }
                }
            }
            else
            {
                int compRef = sym.ReadSymbol(_cdf.CompRef.AsSpan((GetCompRefCtx() * 3 + 0) * 3, 3));
                if (compRef == 0)
                {
                    int p1 = sym.ReadSymbol(_cdf.CompRef.AsSpan((GetCompRefP1Ctx() * 3 + 1) * 3, 3));
                    _refFrame0 = p1 != 0 ? Av1Constants.Last2Frame : Av1Constants.LastFrame;
                }
                else
                {
                    int p2 = sym.ReadSymbol(_cdf.CompRef.AsSpan((GetCompRefP2Ctx() * 3 + 2) * 3, 3));
                    _refFrame0 = p2 != 0 ? Av1Constants.GoldenFrame : Av1Constants.Last3Frame;
                }

                int bwd = sym.ReadSymbol(_cdf.CompBwdRef.AsSpan((GetCompBwdRefCtx() * 2 + 0) * 3, 3));
                if (bwd == 0)
                {
                    int p1 = sym.ReadSymbol(_cdf.CompBwdRef.AsSpan((GetCompBwdRefP1Ctx() * 2 + 1) * 3, 3));
                    _refFrame1 = p1 != 0 ? Av1Constants.AltRef2Frame : Av1Constants.BwdRefFrame;
                }
                else
                {
                    _refFrame1 = Av1Constants.AltRefFrame;
                }
            }
        }
        else
        {
            int p1 = sym.ReadSymbol(_cdf.SingleRef.AsSpan((GetSingleRefP1Ctx() * 6 + 0) * 3, 3));
            if (p1 != 0)
            {
                int p2 = sym.ReadSymbol(_cdf.SingleRef.AsSpan((GetCompBwdRefCtx() * 6 + 1) * 3, 3));
                if (p2 == 0)
                {
                    int p6 = sym.ReadSymbol(_cdf.SingleRef.AsSpan((GetCompBwdRefP1Ctx() * 6 + 5) * 3, 3));
                    _refFrame0 = p6 != 0 ? Av1Constants.AltRef2Frame : Av1Constants.BwdRefFrame;
                }
                else
                {
                    _refFrame0 = Av1Constants.AltRefFrame;
                }
            }
            else
            {
                int p3 = sym.ReadSymbol(_cdf.SingleRef.AsSpan((GetCompRefCtx() * 6 + 2) * 3, 3));
                if (p3 != 0)
                {
                    int p5 = sym.ReadSymbol(_cdf.SingleRef.AsSpan((GetCompRefP2Ctx() * 6 + 4) * 3, 3));
                    _refFrame0 = p5 != 0 ? Av1Constants.GoldenFrame : Av1Constants.Last3Frame;
                }
                else
                {
                    int p4 = sym.ReadSymbol(_cdf.SingleRef.AsSpan((GetCompRefP1Ctx() * 6 + 3) * 3, 3));
                    _refFrame0 = p4 != 0 ? Av1Constants.Last2Frame : Av1Constants.LastFrame;
                }
            }
            _refFrame1 = Av1Constants.NoneFrame;
        }
    }

    private static bool CheckBackward(int rf) => rf >= Av1Constants.BwdRefFrame && rf <= Av1Constants.AltRefFrame;
    private static bool IsSameDirRefPair(int r0, int r1) => (r0 >= Av1Constants.BwdRefFrame) == (r1 >= Av1Constants.BwdRefFrame);

    private int CountRefs(int rf)
    {
        int c = 0;
        if (_availU) { if (_aboveRef0 == rf) c++; if (_aboveRef1 == rf) c++; }
        if (_availL) { if (_leftRef0 == rf) c++; if (_leftRef1 == rf) c++; }
        return c;
    }

    private static int RefCountCtx(int c0, int c1) => c0 < c1 ? 0 : c0 == c1 ? 1 : 2;
    private int GetCompRefCtx() => RefCountCtx(CountRefs(Av1Constants.LastFrame) + CountRefs(Av1Constants.Last2Frame), CountRefs(Av1Constants.Last3Frame) + CountRefs(Av1Constants.GoldenFrame));
    private int GetCompRefP1Ctx() => RefCountCtx(CountRefs(Av1Constants.LastFrame), CountRefs(Av1Constants.Last2Frame));
    private int GetCompRefP2Ctx() => RefCountCtx(CountRefs(Av1Constants.Last3Frame), CountRefs(Av1Constants.GoldenFrame));
    private int GetCompBwdRefCtx() => RefCountCtx(CountRefs(Av1Constants.BwdRefFrame) + CountRefs(Av1Constants.AltRef2Frame), CountRefs(Av1Constants.AltRefFrame));
    private int GetCompBwdRefP1Ctx() => RefCountCtx(CountRefs(Av1Constants.BwdRefFrame), CountRefs(Av1Constants.AltRef2Frame));
    private int GetSingleRefP1Ctx() => RefCountCtx(
        CountRefs(Av1Constants.LastFrame) + CountRefs(Av1Constants.Last2Frame) + CountRefs(Av1Constants.Last3Frame) + CountRefs(Av1Constants.GoldenFrame),
        CountRefs(Av1Constants.BwdRefFrame) + CountRefs(Av1Constants.AltRef2Frame) + CountRefs(Av1Constants.AltRefFrame));

    private Span<ushort> GetCompModeCdf()
    {
        int ctx;
        if (_availU && _availL)
        {
            if (_aboveSingle && _leftSingle)
                ctx = (CheckBackward(_aboveRef0) ^ CheckBackward(_leftRef0)) ? 1 : 0;
            else if (_aboveSingle)
                ctx = 2 + ((CheckBackward(_aboveRef0) || _aboveIntra) ? 1 : 0);
            else if (_leftSingle)
                ctx = 2 + ((CheckBackward(_leftRef0) || _leftIntra) ? 1 : 0);
            else
                ctx = 4;
        }
        else if (_availU)
        {
            ctx = _aboveSingle ? (CheckBackward(_aboveRef0) ? 1 : 0) : 3;
        }
        else if (_availL)
        {
            ctx = _leftSingle ? (CheckBackward(_leftRef0) ? 1 : 0) : 3;
        }
        else
        {
            ctx = 1;
        }
        return _cdf.CompMode.AsSpan(ctx * 3, 3);
    }

    private Span<ushort> GetCompRefTypeCdf()
    {
        bool aboveCompInter = _availU && !_aboveIntra && !_aboveSingle;
        bool leftCompInter = _availL && !_leftIntra && !_leftSingle;
        bool aboveUniComp = aboveCompInter && IsSameDirRefPair(_aboveRef0, _aboveRef1);
        bool leftUniComp = leftCompInter && IsSameDirRefPair(_leftRef0, _leftRef1);

        int ctx;
        if (_availU && !_aboveIntra && _availL && !_leftIntra)
        {
            int samedir = IsSameDirRefPair(_aboveRef0, _leftRef0) ? 1 : 0;
            if (!aboveCompInter && !leftCompInter)
                ctx = 1 + 2 * samedir;
            else if (!aboveCompInter)
                ctx = !leftUniComp ? 1 : 3 + samedir;
            else if (!leftCompInter)
                ctx = !aboveUniComp ? 1 : 3 + samedir;
            else if (!aboveUniComp && !leftUniComp)
                ctx = 0;
            else if (!aboveUniComp || !leftUniComp)
                ctx = 2;
            else
                ctx = 3 + ((_aboveRef0 == Av1Constants.BwdRefFrame) == (_leftRef0 == Av1Constants.BwdRefFrame) ? 1 : 0);
        }
        else if (_availU && _availL)
        {
            if (aboveCompInter) ctx = 1 + 2 * (aboveUniComp ? 1 : 0);
            else if (leftCompInter) ctx = 1 + 2 * (leftUniComp ? 1 : 0);
            else ctx = 2;
        }
        else if (aboveCompInter)
        {
            ctx = 4 * (aboveUniComp ? 1 : 0);
        }
        else if (leftCompInter)
        {
            ctx = 4 * (leftUniComp ? 1 : 0);
        }
        else
        {
            ctx = 2;
        }
        return _cdf.CompRefType.AsSpan(ctx * 3, 3);
    }

    // §7.10.2 Full Find MV Stack (handles both Inter and IntraBC)
    private void FindMvStack(bool isCompound)
    {
        int bw4 = Av1Constants.Num4x4BlocksWide[_miSize];
        int bh4 = Av1Constants.Num4x4BlocksHigh[_miSize];
        _numMvFound = 0;
        _newMvCount = 0;

        SetupGlobalMv(0, out _globalMv0Y, out _globalMv0X);
        if (isCompound)
            SetupGlobalMv(1, out _globalMv1Y, out _globalMv1X);
        else
            _globalMv1Y = _globalMv1X = 0;

        _foundMatch = false;
        ScanRow(-1, bw4, isCompound);
        int foundAbove = _foundMatch ? 1 : 0;
        _foundMatch = false;
        ScanCol(-1, bh4, isCompound);
        int foundLeft = _foundMatch ? 1 : 0;
        _foundMatch = false;
        if (Math.Max(bw4, bh4) <= 16)
            ScanPoint(-1, bw4, isCompound);
        if (_foundMatch) foundAbove = 1;

        int closeMatches = foundAbove + foundLeft;
        int numNearest = _numMvFound;
        int numNew = _newMvCount;
        if (numNearest > 0)
        {
            for (int idx = 0; idx < numNearest; idx++)
                _weightStack[idx] += Av1Constants.RefCatLevel;
        }

        _zeroMvContext = 0;
        if (_fh.UseRefFrameMvs)
            TemporalScan(bw4, bh4, isCompound);

        _foundMatch = false;
        ScanPoint(-1, -1, isCompound);
        if (_foundMatch) foundAbove = 1;

        _foundMatch = false;
        ScanRow(-3, bw4, isCompound);
        if (_foundMatch) foundAbove = 1;

        _foundMatch = false;
        ScanCol(-3, bh4, isCompound);
        if (_foundMatch) foundLeft = 1;

        _foundMatch = false;
        if (bh4 > 1) ScanRow(-5, bw4, isCompound);
        if (_foundMatch) foundAbove = 1;

        _foundMatch = false;
        if (bw4 > 1) ScanCol(-5, bh4, isCompound);
        if (_foundMatch) foundLeft = 1;

        int totalMatches = foundAbove + foundLeft;
        SortMvStack(0, numNearest, isCompound);
        SortMvStack(numNearest, _numMvFound, isCompound);

        if (_numMvFound < 2)
            ExtraSearch(isCompound);

        // §7.10.2.14 Context & Clamping
        for (int idx = 0; idx < _numMvFound; idx++)
        {
            int z = 0;
            if (idx + 1 < _numMvFound)
            {
                int w0 = _weightStack[idx];
                int w1 = _weightStack[idx + 1];
                if (w0 >= Av1Constants.RefCatLevel)
                {
                    if (w1 < Av1Constants.RefCatLevel) z = 1;
                }
                else
                {
                    z = 2;
                }
            }
            _drlCtxStack[idx] = z;
        }

        int bw = Av1Constants.BlockWidth[_miSize];
        int bh = Av1Constants.BlockHeight[_miSize];
        int borderY = Av1Constants.MvBorder + bh * 8;
        int borderX = Av1Constants.MvBorder + bw * 8;
        int topEdge = -(_miRow * 4 * 8) - borderY;
        int botEdge = ((_fh.MiRows - bh4 - _miRow) * 4 * 8) + borderY;
        int leftEdge = -(_miCol * 4 * 8) - borderX;
        int rightEdge = ((_fh.MiCols - bw4 - _miCol) * 4 * 8) + borderX;

        for (int idx = 0; idx < _numMvFound; idx++)
        {
            _refStackMv0Y[idx] = Av1Constants.Clip3(topEdge, botEdge, _refStackMv0Y[idx]);
            _refStackMv0X[idx] = Av1Constants.Clip3(leftEdge, rightEdge, _refStackMv0X[idx]);
            if (isCompound)
            {
                _refStackMv1Y[idx] = Av1Constants.Clip3(topEdge, botEdge, _refStackMv1Y[idx]);
                _refStackMv1X[idx] = Av1Constants.Clip3(leftEdge, rightEdge, _refStackMv1X[idx]);
            }
        }

        if (closeMatches == 0)
        {
            _newMvContext = Math.Min(totalMatches, 1);
            _refMvContext = totalMatches;
        }
        else if (closeMatches == 1)
        {
            _newMvContext = 3 - Math.Min(numNew, 1);
            _refMvContext = 2 + totalMatches;
        }
        else
        {
            _newMvContext = 5 - Math.Min(numNew, 1);
            _refMvContext = 5;
        }
    }

    // §7.10.2.1 Setup global MV
    private void SetupGlobalMv(int refList, out int mvY, out int mvX)
    {
        int rf = refList == 0 ? _refFrame0 : _refFrame1;
        int typ = rf != Av1Constants.IntraFrame ? _fh.GmType[rf] : Av1Constants.GmIdentity;
        if (rf == Av1Constants.IntraFrame || typ == Av1Constants.GmIdentity)
        {
            mvY = 0;
            mvX = 0;
        }
        else if (typ == Av1Constants.GmTranslation)
        {
            mvY = _fh.GmParams[rf, 0] >> (Av1Constants.WarpedModelPrecBits - 3);
            mvX = _fh.GmParams[rf, 1] >> (Av1Constants.WarpedModelPrecBits - 3);
        }
        else
        {
            int bw = Av1Constants.BlockWidth[_miSize];
            int bh = Av1Constants.BlockHeight[_miSize];
            long x = _miCol * 4 + bw / 2 - 1;
            long y = _miRow * 4 + bh / 2 - 1;
            long xc = (_fh.GmParams[rf, 2] - (1L << Av1Constants.WarpedModelPrecBits)) * x
                    + _fh.GmParams[rf, 3] * y
                    + _fh.GmParams[rf, 0];
            long yc = _fh.GmParams[rf, 4] * x
                    + (_fh.GmParams[rf, 5] - (1L << Av1Constants.WarpedModelPrecBits)) * y
                    + _fh.GmParams[rf, 1];
            if (_fh.AllowHighPrecisionMv)
            {
                mvY = (int)Av1Constants.Round2Signed(yc, Av1Constants.WarpedModelPrecBits - 3);
                mvX = (int)Av1Constants.Round2Signed(xc, Av1Constants.WarpedModelPrecBits - 3);
            }
            else
            {
                mvY = (int)Av1Constants.Round2Signed(yc, Av1Constants.WarpedModelPrecBits - 2) * 2;
                mvX = (int)Av1Constants.Round2Signed(xc, Av1Constants.WarpedModelPrecBits - 2) * 2;
            }
        }
        LowerMvPrecision(ref mvY, ref mvX);
    }

    private void LowerMvPrecision(ref int mvY, ref int mvX)
    {
        if (_fh.AllowHighPrecisionMv) return;
        mvY = LowerMvComp(mvY);
        mvX = LowerMvComp(mvX);
    }

    private int LowerMvComp(int v)
    {
        if (_fh.ForceIntegerMv)
        {
            int a = Math.Abs(v);
            int aInt = (a + 3) >> 3;
            return v > 0 ? (aInt << 3) : -(aInt << 3);
        }
        if ((v & 1) != 0)
            return v > 0 ? v - 1 : v + 1;
        return v;
    }

    private void ScanRow(int deltaRow, int bw4, bool isCompound)
    {
        int end4 = Math.Min(Math.Min(bw4, _fh.MiCols - _miCol), 16);
        int deltaCol = 0;
        bool useStep16 = bw4 >= 16;
        if (Math.Abs(deltaRow) > 1)
        {
            deltaRow += _miRow & 1;
            deltaCol = 1 - (_miCol & 1);
        }
        int i = 0;
        while (i < end4)
        {
            int mvRow = _miRow + deltaRow;
            int mvCol = _miCol + deltaCol + i;
            if (!IsInside(mvRow, mvCol)) break;
            int len = Math.Min(bw4, Av1Constants.Num4x4BlocksWide[_st.MiSizes[mvRow * _st.MiCols + mvCol]]);
            if (Math.Abs(deltaRow) > 1) len = Math.Max(2, len);
            if (useStep16) len = Math.Max(4, len);
            AddRefMvCandidate(mvRow, mvCol, isCompound, len * 2);
            i += len;
        }
    }

    private void ScanCol(int deltaCol, int bh4, bool isCompound)
    {
        int end4 = Math.Min(Math.Min(bh4, _fh.MiRows - _miRow), 16);
        int deltaRow = 0;
        bool useStep16 = bh4 >= 16;
        if (Math.Abs(deltaCol) > 1)
        {
            deltaRow = 1 - (_miRow & 1);
            deltaCol += _miCol & 1;
        }
        int i = 0;
        while (i < end4)
        {
            int mvRow = _miRow + deltaRow + i;
            int mvCol = _miCol + deltaCol;
            if (!IsInside(mvRow, mvCol)) break;
            int len = Math.Min(bh4, Av1Constants.Num4x4BlocksHigh[_st.MiSizes[mvRow * _st.MiCols + mvCol]]);
            if (Math.Abs(deltaCol) > 1) len = Math.Max(2, len);
            if (useStep16) len = Math.Max(4, len);
            AddRefMvCandidate(mvRow, mvCol, isCompound, len * 2);
            i += len;
        }
    }

    private void ScanPoint(int deltaRow, int deltaCol, bool isCompound)
    {
        int mvRow = _miRow + deltaRow;
        int mvCol = _miCol + deltaCol;
        if (IsInside(mvRow, mvCol) && _st.RefFrames[(mvRow * _st.MiCols + mvCol) * 2] != Av1Constants.NoneFrame)
            AddRefMvCandidate(mvRow, mvCol, isCompound, 4);
    }

    // §7.10.2.5–6 Temporal scan
    private void TemporalScan(int bw4, int bh4, bool isCompound)
    {
        int stepW4 = bw4 >= 16 ? 4 : 2;
        int stepH4 = bh4 >= 16 ? 4 : 2;
        int limH = Math.Min(bh4, 16);
        int limW = Math.Min(bw4, 16);
        for (int dr = 0; dr < limH; dr += stepH4)
            for (int dc = 0; dc < limW; dc += stepW4)
                AddTplRefMv(dr, dc, isCompound);

        if (bh4 >= 2 && bh4 < 16 && bw4 >= 2 && bw4 < 16)
        {
            CheckAndAddTpl(bh4, -2, isCompound);
            CheckAndAddTpl(bh4, bw4, isCompound);
            CheckAndAddTpl(bh4 - 2, bw4, isCompound);
        }
    }

    private void CheckAndAddTpl(int dr, int dc, bool isCompound)
    {
        int r = (_miRow & 15) + dr;
        int c = (_miCol & 15) + dc;
        if (r >= 0 && r < 16 && c >= 0 && c < 16)
            AddTplRefMv(dr, dc, isCompound);
    }

    private void AddTplRefMv(int dr, int dc, bool isCompound)
    {
        int mvRow = (_miRow + dr) | 1;
        int mvCol = (_miCol + dc) | 1;
        if (!IsInside(mvRow, mvCol)) return;
        int x8 = mvCol >> 1;
        int y8 = mvRow >> 1;
        int pos = (y8 * _fh.MotionFieldW8 + x8) * 2;
        var mfmv = _fh.MotionFieldMvs!;

        if (dr == 0 && dc == 0)
            _zeroMvContext = 1;

        if (!isCompound)
        {
            int mvY = mfmv[_refFrame0][pos];
            if (mvY == short.MinValue) return;
            int mvX = mfmv[_refFrame0][pos + 1];
            LowerMvPrecision(ref mvY, ref mvX);
            if (dr == 0 && dc == 0)
            {
                _zeroMvContext = (Math.Abs(mvY - _globalMv0Y) >= 16 || Math.Abs(mvX - _globalMv0X) >= 16) ? 1 : 0;
            }
            for (int idx = 0; idx < _numMvFound; idx++)
            {
                if (_refStackMv0Y[idx] == mvY && _refStackMv0X[idx] == mvX)
                {
                    _weightStack[idx] += 2;
                    return;
                }
            }
            if (_numMvFound < Av1Constants.MaxRefMvStackSize)
            {
                _refStackMv0Y[_numMvFound] = mvY;
                _refStackMv0X[_numMvFound] = mvX;
                _weightStack[_numMvFound] = 2;
                _numMvFound++;
            }
        }
        else
        {
            int mv0Y = mfmv[_refFrame0][pos];
            if (mv0Y == short.MinValue) return;
            int mv0X = mfmv[_refFrame0][pos + 1];
            int mv1Y = mfmv[_refFrame1][pos];
            if (mv1Y == short.MinValue) return;
            int mv1X = mfmv[_refFrame1][pos + 1];
            LowerMvPrecision(ref mv0Y, ref mv0X);
            LowerMvPrecision(ref mv1Y, ref mv1X);
            if (dr == 0 && dc == 0)
            {
                _zeroMvContext = (Math.Abs(mv0Y - _globalMv0Y) >= 16 || Math.Abs(mv0X - _globalMv0X) >= 16
                    || Math.Abs(mv1Y - _globalMv1Y) >= 16 || Math.Abs(mv1X - _globalMv1X) >= 16) ? 1 : 0;
            }
            for (int idx = 0; idx < _numMvFound; idx++)
            {
                if (_refStackMv0Y[idx] == mv0Y && _refStackMv0X[idx] == mv0X
                    && _refStackMv1Y[idx] == mv1Y && _refStackMv1X[idx] == mv1X)
                {
                    _weightStack[idx] += 2;
                    return;
                }
            }
            if (_numMvFound < Av1Constants.MaxRefMvStackSize)
            {
                _refStackMv0Y[_numMvFound] = mv0Y;
                _refStackMv0X[_numMvFound] = mv0X;
                _refStackMv1Y[_numMvFound] = mv1Y;
                _refStackMv1X[_numMvFound] = mv1X;
                _weightStack[_numMvFound] = 2;
                _numMvFound++;
            }
        }
    }

    private void AddRefMvCandidate(int mvRow, int mvCol, bool isCompound, int weight)
    {
        int idx = mvRow * _st.MiCols + mvCol;
        if (_st.IsInters[idx] == 0) return;

        if (!isCompound)
        {
            for (int candList = 0; candList < 2; candList++)
            {
                if (_st.RefFrames[idx * 2 + candList] != _refFrame0) continue;
                int candMode = _st.YModes[idx];
                int candSize = _st.MiSizes[idx];
                bool large = Math.Min(Av1Constants.BlockWidth[candSize], Av1Constants.BlockHeight[candSize]) >= 8;
                int mvY, mvX;
                if ((candMode == Av1Constants.GlobalMv || candMode == Av1Constants.GlobalGlobalMv)
                    && _refFrame0 > Av1Constants.IntraFrame
                    && _fh.GmType[_refFrame0] > Av1Constants.GmTranslation
                    && large)
                {
                    mvY = _globalMv0Y;
                    mvX = _globalMv0X;
                }
                else
                {
                    mvY = _st.Mvs[idx * 4 + candList * 2];
                    mvX = _st.Mvs[idx * 4 + candList * 2 + 1];
                }
                LowerMvPrecision(ref mvY, ref mvX);
                if (HasNewMv(candMode)) _newMvCount++;
                _foundMatch = true;

                bool found = false;
                for (int k = 0; k < _numMvFound; k++)
                {
                    if (_refStackMv0Y[k] == mvY && _refStackMv0X[k] == mvX)
                    {
                        _weightStack[k] += weight;
                        found = true;
                        break;
                    }
                }
                if (!found && _numMvFound < Av1Constants.MaxRefMvStackSize)
                {
                    _refStackMv0Y[_numMvFound] = mvY;
                    _refStackMv0X[_numMvFound] = mvX;
                    _weightStack[_numMvFound] = weight;
                    _numMvFound++;
                }
            }
        }
        else
        {
            if (_st.RefFrames[idx * 2] != _refFrame0 || _st.RefFrames[idx * 2 + 1] != _refFrame1)
                return;
            int candMode = _st.YModes[idx];
            int mv0Y = _st.Mvs[idx * 4];
            int mv0X = _st.Mvs[idx * 4 + 1];
            int mv1Y = _st.Mvs[idx * 4 + 2];
            int mv1X = _st.Mvs[idx * 4 + 3];
            if (candMode == Av1Constants.GlobalGlobalMv)
            {
                if (_fh.GmType[_refFrame0] > Av1Constants.GmTranslation) { mv0Y = _globalMv0Y; mv0X = _globalMv0X; }
                if (_fh.GmType[_refFrame1] > Av1Constants.GmTranslation) { mv1Y = _globalMv1Y; mv1X = _globalMv1X; }
            }
            LowerMvPrecision(ref mv0Y, ref mv0X);
            LowerMvPrecision(ref mv1Y, ref mv1X);
            _foundMatch = true;

            bool found = false;
            for (int k = 0; k < _numMvFound; k++)
            {
                if (_refStackMv0Y[k] == mv0Y && _refStackMv0X[k] == mv0X
                    && _refStackMv1Y[k] == mv1Y && _refStackMv1X[k] == mv1X)
                {
                    _weightStack[k] += weight;
                    found = true;
                    break;
                }
            }
            if (!found && _numMvFound < Av1Constants.MaxRefMvStackSize)
            {
                _refStackMv0Y[_numMvFound] = mv0Y;
                _refStackMv0X[_numMvFound] = mv0X;
                _refStackMv1Y[_numMvFound] = mv1Y;
                _refStackMv1X[_numMvFound] = mv1X;
                _weightStack[_numMvFound] = weight;
                _numMvFound++;
            }
            if (HasNewMv(candMode)) _newMvCount++;
        }
    }

    private static bool HasNewMv(int mode) => mode is Av1Constants.NewMv or Av1Constants.NewNewMv
        or Av1Constants.NearNewMv or Av1Constants.NewNearMv or Av1Constants.NearestNewMv or Av1Constants.NewNearestMv;

    private void SortMvStack(int start, int end, bool isCompound)
    {
        while (end > start)
        {
            int newEnd = start;
            for (int i = start + 1; i < end; i++)
            {
                if (_weightStack[i - 1] < _weightStack[i])
                {
                    (_weightStack[i - 1], _weightStack[i]) = (_weightStack[i], _weightStack[i - 1]);
                    (_refStackMv0Y[i - 1], _refStackMv0Y[i]) = (_refStackMv0Y[i], _refStackMv0Y[i - 1]);
                    (_refStackMv0X[i - 1], _refStackMv0X[i]) = (_refStackMv0X[i], _refStackMv0X[i - 1]);
                    if (isCompound)
                    {
                        (_refStackMv1Y[i - 1], _refStackMv1Y[i]) = (_refStackMv1Y[i], _refStackMv1Y[i - 1]);
                        (_refStackMv1X[i - 1], _refStackMv1X[i]) = (_refStackMv1X[i], _refStackMv1X[i - 1]);
                    }
                    newEnd = i;
                }
            }
            end = newEnd;
        }
    }

    // §7.10.2.12–13 Extra search
    private void ExtraSearch(bool isCompound)
    {
        Span<int> refIdCount = stackalloc int[2];
        Span<int> refDiffCount = stackalloc int[2];
        Span<int> refIdY = stackalloc int[4];
        Span<int> refIdX = stackalloc int[4];
        Span<int> refDiffY = stackalloc int[4];
        Span<int> refDiffX = stackalloc int[4];

        int w4 = Math.Min(16, Av1Constants.Num4x4BlocksWide[_miSize]);
        int h4 = Math.Min(16, Av1Constants.Num4x4BlocksHigh[_miSize]);
        w4 = Math.Min(w4, _fh.MiCols - _miCol);
        h4 = Math.Min(h4, _fh.MiRows - _miRow);
        int num4x4 = Math.Min(w4, h4);

        for (int pass = 0; pass < 2; pass++)
        {
            int pIdx = 0;
            while (pIdx < num4x4 && _numMvFound < 2)
            {
                int mvRow = pass == 0 ? _miRow - 1 : _miRow + pIdx;
                int mvCol = pass == 0 ? _miCol + pIdx : _miCol - 1;
                if (!IsInside(mvRow, mvCol)) break;

                int cell = mvRow * _st.MiCols + mvCol;
                if (isCompound)
                {
                    for (int candList = 0; candList < 2; candList++)
                    {
                        int candRef = _st.RefFrames[cell * 2 + candList];
                        if (candRef <= Av1Constants.IntraFrame) continue;
                        for (int list = 0; list < 2; list++)
                        {
                            int my = _st.Mvs[cell * 4 + candList * 2];
                            int mx = _st.Mvs[cell * 4 + candList * 2 + 1];
                            int targetRef = list == 0 ? _refFrame0 : _refFrame1;
                            if (candRef == targetRef && refIdCount[list] < 2)
                            {
                                refIdY[list * 2 + refIdCount[list]] = my;
                                refIdX[list * 2 + refIdCount[list]] = mx;
                                refIdCount[list]++;
                            }
                            else if (refDiffCount[list] < 2)
                            {
                                if (_fh.RefFrameSignBias[candRef] != _fh.RefFrameSignBias[targetRef])
                                {
                                    my = -my;
                                    mx = -mx;
                                }
                                refDiffY[list * 2 + refDiffCount[list]] = my;
                                refDiffX[list * 2 + refDiffCount[list]] = mx;
                                refDiffCount[list]++;
                            }
                        }
                    }
                }
                else
                {
                    for (int candList = 0; candList < 2; candList++)
                    {
                        int candRef = _st.RefFrames[cell * 2 + candList];
                        if (candRef <= Av1Constants.IntraFrame) continue;
                        int my = _st.Mvs[cell * 4 + candList * 2];
                        int mx = _st.Mvs[cell * 4 + candList * 2 + 1];
                        if (_fh.RefFrameSignBias[candRef] != _fh.RefFrameSignBias[_refFrame0])
                        {
                            my = -my;
                            mx = -mx;
                        }
                        bool dup = false;
                        for (int k = 0; k < _numMvFound; k++)
                        {
                            if (_refStackMv0Y[k] == my && _refStackMv0X[k] == mx) { dup = true; break; }
                        }
                        if (!dup && _numMvFound < Av1Constants.MaxRefMvStackSize)
                        {
                            _refStackMv0Y[_numMvFound] = my;
                            _refStackMv0X[_numMvFound] = mx;
                            _weightStack[_numMvFound] = 2;
                            _numMvFound++;
                        }
                    }
                }

                int sz = _st.MiSizes[cell];
                pIdx += pass == 0 ? Av1Constants.Num4x4BlocksWide[sz] : Av1Constants.Num4x4BlocksHigh[sz];
            }
        }

        if (isCompound)
        {
            Span<int> combY = stackalloc int[4];
            Span<int> combX = stackalloc int[4];
            for (int list = 0; list < 2; list++)
            {
                int cc = 0;
                for (int k = 0; k < refIdCount[list]; k++)
                {
                    combY[cc * 2 + list] = refIdY[list * 2 + k];
                    combX[cc * 2 + list] = refIdX[list * 2 + k];
                    cc++;
                }
                for (int k = 0; k < refDiffCount[list] && cc < 2; k++)
                {
                    combY[cc * 2 + list] = refDiffY[list * 2 + k];
                    combX[cc * 2 + list] = refDiffX[list * 2 + k];
                    cc++;
                }
                while (cc < 2)
                {
                    combY[cc * 2 + list] = list == 0 ? _globalMv0Y : _globalMv1Y;
                    combX[cc * 2 + list] = list == 0 ? _globalMv0X : _globalMv1X;
                    cc++;
                }
            }

            if (_numMvFound == 1)
            {
                int pick = (combY[0] == _refStackMv0Y[0] && combX[0] == _refStackMv0X[0]
                         && combY[1] == _refStackMv1Y[0] && combX[1] == _refStackMv1X[0]) ? 1 : 0;
                _refStackMv0Y[1] = combY[pick * 2];
                _refStackMv0X[1] = combX[pick * 2];
                _refStackMv1Y[1] = combY[pick * 2 + 1];
                _refStackMv1X[1] = combX[pick * 2 + 1];
                _weightStack[1] = 2;
                _numMvFound++;
            }
            else
            {
                for (int k = 0; k < 2; k++)
                {
                    _refStackMv0Y[_numMvFound] = combY[k * 2];
                    _refStackMv0X[_numMvFound] = combX[k * 2];
                    _refStackMv1Y[_numMvFound] = combY[k * 2 + 1];
                    _refStackMv1X[_numMvFound] = combX[k * 2 + 1];
                    _weightStack[_numMvFound] = 2;
                    _numMvFound++;
                }
            }
        }
        else
        {
            for (int k = _numMvFound; k < 2; k++)
            {
                _refStackMv0Y[k] = _globalMv0Y;
                _refStackMv0X[k] = _globalMv0X;
            }
        }
    }

    // §5.11.26 Assign MV
    private void AssignMv(ref Av1SymbolDecoder sym, bool isCompound)
    {
        int numLists = isCompound ? 2 : 1;
        for (int i = 0; i < numLists; i++)
        {
            int compMode = _useIntrabc ? Av1Constants.NewMv : GetMode(i);
            int predY, predX;
            if (_useIntrabc)
            {
                predY = _refStackMv0Y[0];
                predX = _refStackMv0X[0];
                if (predY == 0 && predX == 0)
                {
                    predY = _refStackMv0Y[1];
                    predX = _refStackMv0X[1];
                }
                if (predY == 0 && predX == 0)
                {
                    int sbSize4 = _seq.Use128x128Superblock ? 32 : 16;
                    if (_miRow - sbSize4 < _miRowStart)
                    {
                        predY = 0;
                        predX = -(sbSize4 * 4 + Av1Constants.IntrabcDelayPixels) * 8;
                    }
                    else
                    {
                        predY = -(sbSize4 * 4 * 8);
                        predX = 0;
                    }
                }
            }
            else if (compMode == Av1Constants.GlobalMv)
            {
                predY = i == 0 ? _globalMv0Y : _globalMv1Y;
                predX = i == 0 ? _globalMv0X : _globalMv1X;
            }
            else
            {
                int pos = compMode == Av1Constants.NearestMv ? 0 : _refMvIdx;
                if (compMode == Av1Constants.NewMv && _numMvFound <= 1)
                    pos = 0;
                predY = i == 0 ? _refStackMv0Y[pos] : _refStackMv1Y[pos];
                predX = i == 0 ? _refStackMv0X[pos] : _refStackMv1X[pos];
            }

            int outY, outX;
            if (compMode == Av1Constants.NewMv)
            {
                int mvCtx = _useIntrabc ? Av1Constants.MvIntrabcContext : 0;
                ReadMv(ref sym, mvCtx, predY, predX, out outY, out outX);
            }
            else
            {
                outY = predY;
                outX = predX;
            }

            if (i == 0) { _mv0Y = outY; _mv0X = outX; }
            else { _mv1Y = outY; _mv1X = outX; }
        }
    }

    private int GetMode(int refList)
    {
        if (refList == 0)
        {
            if (_yMode < Av1Constants.NearestNearestMv) return _yMode;
            return _yMode switch
            {
                Av1Constants.NewNewMv or Av1Constants.NewNearestMv or Av1Constants.NewNearMv => Av1Constants.NewMv,
                Av1Constants.NearestNearestMv or Av1Constants.NearestNewMv => Av1Constants.NearestMv,
                Av1Constants.NearNearMv or Av1Constants.NearNewMv => Av1Constants.NearMv,
                _ => Av1Constants.GlobalMv,
            };
        }
        return _yMode switch
        {
            Av1Constants.NewNewMv or Av1Constants.NearestNewMv or Av1Constants.NearNewMv => Av1Constants.NewMv,
            Av1Constants.NearestNearestMv or Av1Constants.NewNearestMv => Av1Constants.NearestMv,
            Av1Constants.NearNearMv or Av1Constants.NewNearMv => Av1Constants.NearMv,
            _ => Av1Constants.GlobalMv,
        };
    }

    // §5.11.28 Read interintra mode
    private void ReadInterIntraMode(ref Av1SymbolDecoder sym, bool isCompound)
    {
        _isInterIntra = false;
        _wedgeInterIntra = false;
        if (!_skipMode && _seq.EnableInterIntraCompound && !isCompound
            && _miSize >= Av1Constants.Block8x8 && _miSize <= Av1Constants.Block32x32)
        {
            int ctx = Av1Constants.SizeGroup[_miSize] - 1;
            _isInterIntra = sym.ReadSymbol(_cdf.InterIntra.AsSpan(ctx * 3, 3)) != 0;
            if (_isInterIntra)
            {
                _interIntraMode = sym.ReadSymbol(_cdf.InterIntraMode.AsSpan(ctx * 5, 5));
                _refFrame1 = Av1Constants.IntraFrame;
                _angleDeltaY = 0;
                _angleDeltaUv = 0;
                _useFilterIntra = false;
                _wedgeInterIntra = sym.ReadSymbol(_cdf.WedgeInterIntra.AsSpan(_miSize * 3, 3)) != 0;
                if (_wedgeInterIntra)
                {
                    _wedgeIndex = sym.ReadSymbol(_cdf.WedgeIndex.AsSpan(_miSize * 17, 17));
                    _wedgeSign = 0;
                }
            }
        }
    }

    // §5.11.27 Read motion mode + §7.10.3 + §7.10.4
    private void ReadMotionMode(ref Av1SymbolDecoder sym, bool isCompound)
    {
        if (_skipMode
            || !_fh.IsMotionModeSwitchable
            || Math.Min(Av1Constants.BlockWidth[_miSize], Av1Constants.BlockHeight[_miSize]) < 8)
        {
            _motionMode = Av1Constants.MotionSimple;
            return;
        }
        if (!_fh.ForceIntegerMv && (_yMode is Av1Constants.GlobalMv or Av1Constants.GlobalGlobalMv)
            && _fh.GmType[_refFrame0] > Av1Constants.GmTranslation)
        {
            _motionMode = Av1Constants.MotionSimple;
            return;
        }
        if (isCompound || _refFrame1 == Av1Constants.IntraFrame || !HasOverlappableCandidates())
        {
            _motionMode = Av1Constants.MotionSimple;
            return;
        }

        FindWarpSamples();
        if (_fh.ForceIntegerMv || _numWarpSamples == 0 || !_fh.AllowWarpedMotion || _interPred.IsScaled(_refFrame0))
        {
            int useObmc = sym.ReadSymbol(_cdf.UseObmc.AsSpan(_miSize * 3, 3));
            _motionMode = useObmc != 0 ? Av1Constants.MotionObmc : Av1Constants.MotionSimple;
        }
        else
        {
            _motionMode = sym.ReadSymbol(_cdf.MotionMode.AsSpan(_miSize * 4, 4));
        }
    }

    private bool HasOverlappableCandidates()
    {
        if (_availU)
        {
            int w4 = Av1Constants.Num4x4BlocksWide[_miSize];
            int lim = Math.Min(_fh.MiCols, _miCol + w4);
            for (int x4 = _miCol; x4 < lim; x4 += 2)
                if (_st.RefFrames[((_miRow - 1) * _st.MiCols + (x4 | 1)) * 2] > Av1Constants.IntraFrame)
                    return true;
        }
        if (_availL)
        {
            int h4 = Av1Constants.Num4x4BlocksHigh[_miSize];
            int lim = Math.Min(_fh.MiRows, _miRow + h4);
            for (int y4 = _miRow; y4 < lim; y4 += 2)
                if (_st.RefFrames[((y4 | 1) * _st.MiCols + (_miCol - 1)) * 2] > Av1Constants.IntraFrame)
                    return true;
        }
        return false;
    }

    private void FindWarpSamples()
    {
        _numWarpSamples = 0;
        _numWarpSamplesScanned = 0;
        int w4 = Av1Constants.Num4x4BlocksWide[_miSize];
        int h4 = Av1Constants.Num4x4BlocksHigh[_miSize];
        bool doTopLeft = true;
        bool doTopRight = true;

        if (_availU)
        {
            int srcW = Av1Constants.Num4x4BlocksWide[_st.MiSizes[(_miRow - 1) * _st.MiCols + _miCol]];
            if (w4 <= srcW)
            {
                int colOffset = -(_miCol & (srcW - 1));
                if (colOffset < 0) doTopLeft = false;
                if (colOffset + srcW > w4) doTopRight = false;
                AddWarpSample(-1, 0);
            }
            else
            {
                int lim = Math.Min(w4, _fh.MiCols - _miCol);
                for (int i = 0; i < lim;)
                {
                    int sw = Av1Constants.Num4x4BlocksWide[_st.MiSizes[(_miRow - 1) * _st.MiCols + _miCol + i]];
                    AddWarpSample(-1, i);
                    i += Math.Min(w4, sw);
                }
            }
        }

        if (_availL)
        {
            int srcH = Av1Constants.Num4x4BlocksHigh[_st.MiSizes[_miRow * _st.MiCols + (_miCol - 1)]];
            if (h4 <= srcH)
            {
                int rowOffset = -(_miRow & (srcH - 1));
                if (rowOffset < 0) doTopLeft = false;
                AddWarpSample(0, -1);
            }
            else
            {
                int lim = Math.Min(h4, _fh.MiRows - _miRow);
                for (int i = 0; i < lim;)
                {
                    int sh = Av1Constants.Num4x4BlocksHigh[_st.MiSizes[(_miRow + i) * _st.MiCols + (_miCol - 1)]];
                    AddWarpSample(i, -1);
                    i += Math.Min(h4, sh);
                }
            }
        }

        if (doTopLeft) AddWarpSample(-1, -1);
        if (doTopRight && Math.Max(w4, h4) <= 16) AddWarpSample(-1, w4);

        if (_numWarpSamples == 0 && _numWarpSamplesScanned > 0)
            _numWarpSamples = 1;
    }

    private void AddWarpSample(int deltaRow, int deltaCol)
    {
        if (_numWarpSamplesScanned >= Av1Constants.LeastSquaresSamplesMax) return;
        int mvRow = _miRow + deltaRow;
        int mvCol = _miCol + deltaCol;
        if (!IsInside(mvRow, mvCol)) return;
        int idx = mvRow * _st.MiCols + mvCol;
        if (_st.RefFrames[idx * 2] != _refFrame0) return;
        if (_st.RefFrames[idx * 2 + 1] != Av1Constants.NoneFrame) return;

        int candSz = _st.MiSizes[idx];
        int candW4 = Av1Constants.Num4x4BlocksWide[candSz];
        int candH4 = Av1Constants.Num4x4BlocksHigh[candSz];
        int candRow = mvRow & ~(candH4 - 1);
        int candCol = mvCol & ~(candW4 - 1);
        int midY = candRow * 4 + candH4 * 2 - 1;
        int midX = candCol * 4 + candW4 * 2 - 1;
        int threshold = Av1Constants.Clip3(16, 112, Math.Max(Av1Constants.BlockWidth[_miSize], Av1Constants.BlockHeight[_miSize]));
        int candCell = candRow * _st.MiCols + candCol;
        int candMvY = _st.Mvs[candCell * 4];
        int candMvX = _st.Mvs[candCell * 4 + 1];
        bool valid = (Math.Abs(candMvY - _mv0Y) + Math.Abs(candMvX - _mv0X)) <= threshold;

        _numWarpSamplesScanned++;
        if (!valid && _numWarpSamplesScanned > 1) return;

        int dst = _numWarpSamples * 4;
        _warpCandList[dst] = midY * 8;
        _warpCandList[dst + 1] = midX * 8;
        _warpCandList[dst + 2] = midY * 8 + candMvY;
        _warpCandList[dst + 3] = midX * 8 + candMvX;
        if (valid) _numWarpSamples++;
    }

    // §5.11.29 Read compound type
    private void ReadCompoundType(ref Av1SymbolDecoder sym, bool isCompound)
    {
        _compGroupIdx = 0;
        _compoundIdx = 1;
        if (_skipMode)
        {
            _compoundType = Av1Constants.CompoundAverage;
            return;
        }
        if (isCompound)
        {
            int n = Av1Constants.WedgeBits[_miSize];
            if (_seq.EnableMaskedCompound)
                _compGroupIdx = sym.ReadSymbol(GetCompGroupIdxCdf());

            if (_compGroupIdx == 0)
            {
                if (_seq.EnableJntComp)
                {
                    _compoundIdx = sym.ReadSymbol(GetCompoundIdxCdf());
                    _compoundType = _compoundIdx != 0 ? Av1Constants.CompoundAverage : Av1Constants.CompoundDistance;
                }
                else
                {
                    _compoundType = Av1Constants.CompoundAverage;
                }
            }
            else
            {
                _compoundType = n == 0
                    ? Av1Constants.CompoundDiffWtd
                    : sym.ReadSymbol(_cdf.CompoundType.AsSpan(_miSize * 3, 3));
            }

            if (_compoundType == Av1Constants.CompoundWedge)
            {
                _wedgeIndex = sym.ReadSymbol(_cdf.WedgeIndex.AsSpan(_miSize * 17, 17));
                _wedgeSign = sym.ReadLiteral(1);
            }
            else if (_compoundType == Av1Constants.CompoundDiffWtd)
            {
                _maskType = sym.ReadLiteral(1);
            }
        }
        else
        {
            _compoundType = _isInterIntra
                ? (_wedgeInterIntra ? Av1Constants.CompoundWedge : Av1Constants.CompoundIntra)
                : Av1Constants.CompoundAverage;
        }
    }

    private Span<ushort> GetCompGroupIdxCdf()
    {
        int ctx = 0;
        if (_availU)
        {
            if (!_aboveSingle) ctx += _st.CompGroupIdxs[(_miRow - 1) * _st.MiCols + _miCol];
            else if (_aboveRef0 == Av1Constants.AltRefFrame) ctx += 3;
        }
        if (_availL)
        {
            if (!_leftSingle) ctx += _st.CompGroupIdxs[_miRow * _st.MiCols + (_miCol - 1)];
            else if (_leftRef0 == Av1Constants.AltRefFrame) ctx += 3;
        }
        ctx = Math.Min(5, ctx);
        return _cdf.CompGroupIdx.AsSpan(ctx * 3, 3);
    }

    private Span<ushort> GetCompoundIdxCdf()
    {
        int fwd = Math.Abs(_seq.GetRelativeDist(_fh.OrderHints[_refFrame0], _fh.OrderHint));
        int bck = Math.Abs(_seq.GetRelativeDist(_fh.OrderHints[_refFrame1], _fh.OrderHint));
        int ctx = fwd == bck ? 3 : 0;
        if (_availU)
        {
            if (!_aboveSingle) ctx += _st.CompoundIdxs[(_miRow - 1) * _st.MiCols + _miCol];
            else if (_aboveRef0 == Av1Constants.AltRefFrame) ctx++;
        }
        if (_availL)
        {
            if (!_leftSingle) ctx += _st.CompoundIdxs[_miRow * _st.MiCols + (_miCol - 1)];
            else if (_leftRef0 == Av1Constants.AltRefFrame) ctx++;
        }
        return _cdf.CompoundIdx.AsSpan(ctx * 3, 3);
    }

    private void ReadMv(ref Av1SymbolDecoder sym, int mvCtx, int predY, int predX, out int mvY, out int mvX)
    {
        int joint = sym.ReadSymbol(_cdf.MvJoint.AsSpan(mvCtx * 5, 5));
        int diffY = (joint is Av1Constants.MvJointHzvnz or Av1Constants.MvJointHnzvnz)
            ? ReadMvComponent(ref sym, mvCtx, 0) : 0;
        int diffX = (joint is Av1Constants.MvJointHnzvz or Av1Constants.MvJointHnzvnz)
            ? ReadMvComponent(ref sym, mvCtx, 1) : 0;
        mvY = predY + diffY;
        mvX = predX + diffX;
    }

    private int ReadMvComponent(ref Av1SymbolDecoder sym, int mvCtx, int comp)
    {
        int cc = mvCtx * 2 + comp;
        int sign = sym.ReadSymbol(_cdf.MvSign.AsSpan(cc * 3, 3));
        int mvClass = sym.ReadSymbol(_cdf.MvClass.AsSpan(cc * 12, 12));
        int mag;
        if (mvClass == Av1Constants.MvClass0)
        {
            int bit = sym.ReadSymbol(_cdf.MvClass0Bit.AsSpan(cc * 3, 3));
            int fr = _fh.ForceIntegerMv ? 3 : sym.ReadSymbol(_cdf.MvClass0Fr.AsSpan((cc * 2 + bit) * 5, 5));
            int hp = _fh.AllowHighPrecisionMv ? sym.ReadSymbol(_cdf.MvClass0Hp.AsSpan(cc * 3, 3)) : 1;
            mag = ((bit << 3) | (fr << 1) | hp) + 1;
        }
        else
        {
            int d = 0;
            for (int i = 0; i < mvClass; i++)
            {
                int b = sym.ReadSymbol(_cdf.MvBit.AsSpan((cc * 10 + i) * 3, 3));
                d |= b << i;
            }
            mag = Av1Constants.Class0Size << (mvClass + 2);
            int fr = _fh.ForceIntegerMv ? 3 : sym.ReadSymbol(_cdf.MvFr.AsSpan(cc * 5, 5));
            int hp = _fh.AllowHighPrecisionMv ? sym.ReadSymbol(_cdf.MvHp.AsSpan(cc * 3, 3)) : 1;
            mag += ((d << 3) | (fr << 1) | hp) + 1;
        }
        return sign != 0 ? -mag : mag;
    }

    // §5.11.33 Compute prediction (Inter + InterIntra + IntraBC)
    private void ComputePrediction()
    {
        if ((AvifDiagnostics.Ablation & AvifAblation.SkipInterPred) != 0)
            return;
        int sbMask = _seq.Use128x128Superblock ? 31 : 15;
        int subBlockMiRow = _miRow & sbMask;
        int subBlockMiCol = _miCol & sbMask;
        int planes = _hasChroma ? 3 : 1;

        for (int plane = 0; plane < planes; plane++)
        {
            int planeSz = GetPlaneResidualSize(_miSize, plane);
            int num4x4W = Av1Constants.Num4x4BlocksWide[planeSz];
            int num4x4H = Av1Constants.Num4x4BlocksHigh[planeSz];
            int log2W = 2 + Av1Constants.MiWidthLog2[planeSz];
            int log2H = 2 + Av1Constants.MiHeightLog2[planeSz];
            int subX = plane > 0 ? _seq.SubsamplingX : 0;
            int subY = plane > 0 ? _seq.SubsamplingY : 0;
            int baseX = (_miCol >> subX) * 4;
            int baseY = (_miRow >> subY) * 4;
            int candRow = (_miRow >> subY) << subY;
            int candCol = (_miCol >> subX) << subX;

            if (_isInterIntra)
            {
                int mode = _interIntraMode switch
                {
                    Av1Constants.IiDcPred => Av1Constants.DcPred,
                    Av1Constants.IiVPred => Av1Constants.VPred,
                    Av1Constants.IiHPred => Av1Constants.HPred,
                    _ => Av1Constants.SmoothPred,
                };
                bool[] bd = _blockDecoded[plane];
                int bdY = subBlockMiRow >> subY;
                int bdX = subBlockMiCol >> subX;
                bool haveAboveRight = bd[bdY * BdStride + (bdX + num4x4W + 1)];
                bool haveBelowLeft = bd[(bdY + num4x4H + 1) * BdStride + bdX];
                int maxX = ((_fh.MiCols * 4) >> subX) - 1;
                int maxY = ((_fh.MiRows * 4) >> subY) - 1;

                Av1IntraPred.PredictIntra(
                    _pic.Planes[plane],
                    _pic.PlaneStrides[plane],
                    _seq.BitDepth,
                    _seq.EnableIntraEdgeFilter,
                    plane,
                    baseX,
                    baseY,
                    plane == 0 ? _availL : _availLChroma,
                    plane == 0 ? _availU : _availUChroma,
                    haveAboveRight,
                    haveBelowLeft,
                    mode,
                    angleDelta: 0,
                    useFilterIntra: false,
                    filterIntraMode: 0,
                    GetIntraFilterType(plane),
                    log2W,
                    log2H,
                    maxX,
                    maxY);
            }

            int predW = Av1Constants.BlockWidth[_miSize] >> subX;
            int predH = Av1Constants.BlockHeight[_miSize] >> subY;
            bool someUseIntra = false;
            int rLim = Math.Min(num4x4H << subY, _st.MiRows - candRow);
            int cLim = Math.Min(num4x4W << subX, _st.MiCols - candCol);
            for (int r = 0; r < rLim; r++)
            {
                for (int c = 0; c < cLim; c++)
                {
                    if (_st.RefFrames[((candRow + r) * _st.MiCols + (candCol + c)) * 2] == Av1Constants.IntraFrame)
                        someUseIntra = true;
                }
            }
            if (someUseIntra)
            {
                predW = num4x4W * 4;
                predH = num4x4H * 4;
                candRow = _miRow;
                candCol = _miCol;
            }

            int ry = 0;
            for (int y = 0; y < num4x4H * 4; y += predH)
            {
                int cx = 0;
                for (int x = 0; x < num4x4W * 4; x += predW)
                {
                    _interPred.PredictInter(
                        plane, baseX + x, baseY + y, predW, predH,
                        candRow + ry, candCol + cx,
                        _miRow, _miCol, _miSize, _yMode,
                        _motionMode, _compoundType,
                        _isInterIntra, _interIntraMode, _wedgeInterIntra,
                        _wedgeIndex, _wedgeSign, _maskType,
                        _useIntrabc, _availU, _availL,
                        _numWarpSamples, _warpCandList);
                    cx++;
                }
                ry++;
            }
        }
    }

    private static bool IsDirectionalMode(int mode) => mode is >= Av1Constants.VPred and <= Av1Constants.D67Pred;

    private static byte[] BuildCoeffBaseCtxOffset()
    {
        // §8.3.2 Coeff_Base_Ctx_Offset[19][5][5]
        var t = new byte[19 * 25];
        byte[] sq = [0, 1, 6, 6, 21, 1, 6, 6, 21, 21, 6, 6, 21, 21, 21, 6, 21, 21, 21, 21, 21, 21, 21, 21, 21];
        byte[] sq4 = [0, 1, 6, 6, 0, 1, 6, 6, 21, 0, 6, 6, 21, 21, 0, 6, 21, 21, 21, 0, 0, 0, 0, 0, 0];
        byte[] tall = [0, 11, 11, 11, 11, 11, 11, 11, 11, 11, 6, 6, 21, 21, 21, 6, 21, 21, 21, 21, 21, 21, 21, 21, 21];
        byte[] tall4 = [0, 11, 11, 11, 0, 11, 11, 11, 11, 0, 6, 6, 21, 21, 0, 6, 21, 21, 21, 0, 21, 21, 21, 21, 0];
        byte[] wide = [0, 16, 6, 6, 21, 16, 16, 6, 21, 21, 16, 16, 21, 21, 21, 16, 16, 21, 21, 21, 16, 16, 21, 21, 21];
        byte[] wide4 = [0, 16, 6, 6, 21, 16, 16, 6, 21, 21, 16, 16, 21, 21, 21, 16, 16, 21, 21, 21, 0, 0, 0, 0, 0];

        for (int tx = 0; tx < 19; tx++)
        {
            int w = Av1Constants.TxWidth[tx];
            int h = Av1Constants.TxHeight[tx];
            byte[] src = w == h ? (w == 4 ? sq4 : sq)
                       : h > w ? (w == 4 ? tall4 : tall)
                       : (h == 4 ? wide4 : wide);
            src.CopyTo(t, tx * 25);
        }
        return t;
    }
}
