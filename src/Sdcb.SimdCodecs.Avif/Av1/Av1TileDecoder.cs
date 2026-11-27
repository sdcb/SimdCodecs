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

    private readonly Av1SequenceHeader _seq;
    private readonly Av1FrameHeader _fh;
    private readonly Av1Picture _pic;
    private readonly Av1FrameState _st;
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

    // BlockDecoded[plane][(y+1) * bdStride + (x+1)], max sbSize4 = 32 -> 34x34
    private readonly bool[][] _blockDecoded;
    private const int BdStride = 34;

    // Per-block scratch
    private readonly byte[] _txTypes; // MiRows * MiCols (or per-tile, allocate per-tile)
    private readonly int[] _quant = new int[1024];
    private readonly int[] _dequant = new int[1024];
    private readonly int[] _residual = new int[4096];

    // Palette state (per tile, only allocated/used if AllowScreenContentTools)
    private readonly byte[]? _paletteSizesY;
    private readonly byte[]? _paletteSizesUv;
    private readonly ushort[]? _paletteColorsY; // MiRows*MiCols*8
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
    private bool _skip, _lossless;
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

    internal Av1TileDecoder(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState st)
    {
        _seq = seq;
        _fh = fh;
        _pic = pic;
        _st = st;

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

        _txTypes = new byte[fh.MiRows * fh.MiCols];

        if (fh.AllowScreenContentTools)
        {
            int n = fh.MiRows * fh.MiCols;
            _paletteSizesY = new byte[n];
            _paletteSizesUv = new byte[n];
            _paletteColorsY = new ushort[n * 8];
            _paletteColorsUv = new ushort[n * 8];
        }
    }

    internal void DecodeTile(int tileRow, int tileCol, ReadOnlySpan<byte> tileBytes)
    {
        _miRowStart = _fh.MiRowStarts[tileRow];
        _miRowEnd = _fh.MiRowStarts[tileRow + 1];
        _miColStart = _fh.MiColStarts[tileCol];
        _miColEnd = _fh.MiColStarts[tileCol + 1];

        _currentQIndex = _fh.BaseQIdx;
        _cdf.InitDefault(_fh.BaseQIdx);
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

        IntraFrameModeInfo(ref sym);
        PaletteTokens(ref sym);
        ReadBlockTxSize(ref sym, bw4, bh4);

        if (_skip)
            ResetBlockContext(bw4, bh4);

        // Store YMode / UVMode before residual (needed by intra edge filter type for subsequent blocks)
        int miCols = _st.MiCols;
        int rEnd = Math.Min(r + bh4, _st.MiRows);
        int cEnd = Math.Min(c + bw4, miCols);
        for (int rr = r; rr < rEnd; rr++)
        {
            int rowBase = rr * miCols;
            for (int cc = c; cc < cEnd; cc++)
            {
                _st.YModes[rowBase + cc] = (byte)_yMode;
                if (_hasChroma)
                    _st.UvModes[rowBase + cc] = (byte)_uvMode;
            }
        }

        Residual(ref sym);

        // Store remaining per-MI state
        for (int rr = r; rr < rEnd; rr++)
        {
            int rowBase = rr * miCols;
            for (int cc = c; cc < cEnd; cc++)
            {
                int idx = rowBase + cc;
                _st.Skips[idx] = _skip ? (byte)1 : (byte)0;
                _st.MiSizes[idx] = (byte)subSize;
                _st.SegmentIds[idx] = (byte)_segmentId;
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
        if (_fh.SegIdPreSkip)
            IntraSegmentId(ref sym);

        ReadSkip(ref sym);

        if (!_fh.SegIdPreSkip)
            IntraSegmentId(ref sym);

        ReadCdef(ref sym);
        ReadDeltaQIndex(ref sym);
        ReadDeltaLf(ref sym);
        _readDeltas = false;

        int aboveMode = Av1Constants.IntraModeContext[_availU ? _st.YModes[(_miRow - 1) * _st.MiCols + _miCol] : Av1Constants.DcPred];
        int leftMode = Av1Constants.IntraModeContext[_availL ? _st.YModes[_miRow * _st.MiCols + (_miCol - 1)] : Av1Constants.DcPred];
        _yMode = sym.ReadSymbol(_cdf.IntraFrameYMode.AsSpan((aboveMode * 5 + leftMode) * 14, 14));

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
        {
            _segmentId = 0;
        }
        else
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
                _segmentId = NegDeinterleave(diff, pred, _fh.LastActiveSegId + 1);
            }
        }
        _lossless = _fh.LosslessArray[_segmentId];
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
        if (_lossless)
        {
            _txSize = Av1Constants.Tx4x4;
        }
        else
        {
            int maxRectTxSize = Av1Constants.MaxTxSizeRect[_miSize];
            int maxTxDepth = Av1Constants.MaxTxDepth[_miSize];
            _txSize = maxRectTxSize;
            if (_miSize > Av1Constants.Block4x4 && _fh.TxMode == Av1Constants.TxModeSelect)
            {
                int maxTxW = Av1Constants.TxWidth[maxRectTxSize];
                int maxTxH = Av1Constants.TxHeight[maxRectTxSize];
                int aboveW = _availU ? Av1Constants.TxWidth[_st.InterTxSizes[(_miRow - 1) * _st.MiCols + _miCol]] : 0;
                int leftH = _availL ? Av1Constants.TxHeight[_st.InterTxSizes[_miRow * _st.MiCols + (_miCol - 1)]] : 0;
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

        int miCols = _st.MiCols;
        int rEnd = Math.Min(_miRow + bh4, _st.MiRows);
        int cEnd = Math.Min(_miCol + bw4, miCols);
        for (int r = _miRow; r < rEnd; r++)
            for (int c = _miCol; c < cEnd; c++)
                _st.InterTxSizes[r * miCols + c] = (byte)_txSize;
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

        if (plane == 0)
        {
            _maxLumaW = startX + stepX * 4;
            _maxLumaH = startY + stepY * 4;
        }

        if (!_skip)
        {
            int eob = DecodeCoeffs(ref sym, plane, startX, startY, txSz);
            if (eob > 0)
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
        int mode = plane == 0 ? _st.YModes[r * _st.MiCols + c] : _st.UvModes[r * _st.MiCols + c];
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

        _quant.AsSpan(0, segEob).Clear();

        int eob = 0;
        int culLevel = 0;
        int dcCategory = 0;

        int txbSkipCtx = GetAllZeroCtx(plane, txSz, x4, y4, w4, h4);
        int allZero = sym.ReadSymbol(_cdf.TxbSkip.AsSpan((txSzCtx * 13 + txbSkipCtx) * 3, 3));

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
            int eobShift = Math.Max(-1, eobPt - 3);
            if (eobShift >= 0)
            {
                int eobExtra = sym.ReadSymbol(_cdf.EobExtra.AsSpan(((txSzCtx * 2 + ptype) * 9 + (eobPt - 3)) * 3, 3));
                if (eobExtra != 0)
                    eob += 1 << eobShift;
                int limit = Math.Max(0, eobPt - 2);
                for (int i = 1; i < limit; i++)
                {
                    int sh = limit - 1 - i;
                    if (sym.ReadBool() != 0)
                        eob += 1 << sh;
                }
            }

            int adjTxSz = Av1Constants.AdjustedTxSize[txSz];
            int bwl = Av1Constants.TxWidthLog2[adjTxSz];
            int adjW = 1 << bwl;
            int adjH = Av1Constants.TxHeight[adjTxSz];
            int brTxSzCtx = Math.Min(txSzCtx, Av1Constants.Tx32x32);

            for (int c = eob - 1; c >= 0; c--)
            {
                int pos = scan[c];
                int level;
                if (c == eob - 1)
                {
                    int eobBaseCtx = c == 0 ? 0
                        : c <= ((adjH << bwl) >> 3) ? 1
                        : c <= ((adjH << bwl) >> 2) ? 2
                        : 3;
                    level = sym.ReadSymbol(_cdf.CoeffBaseEob.AsSpan(((txSzCtx * 2 + ptype) * 4 + eobBaseCtx) * 4, 4)) + 1;
                }
                else
                {
                    int baseCtx = GetCoeffBaseCtx(txSz, txClass, bwl, adjW, adjH, pos);
                    level = sym.ReadSymbol(_cdf.CoeffBase.AsSpan(((txSzCtx * 2 + ptype) * 42 + baseCtx) * 5, 5));
                }

                if (level > 2) // NUM_BASE_LEVELS = 2
                {
                    int brCtx = GetCoeffBrCtx(txClass, bwl, adjW, adjH, pos);
                    Span<ushort> brCdf = _cdf.CoeffBr.AsSpan(((brTxSzCtx * 2 + ptype) * 21 + brCtx) * 5, 5);
                    for (int idx = 0; idx < 4; idx++) // COEFF_BASE_RANGE(12) / (BR_CDF_SIZE(4) - 1) = 4
                    {
                        int br = sym.ReadSymbol(brCdf);
                        level += br;
                        if (br < 3) break;
                    }
                }
                _quant[pos] = level;
            }

            for (int c = 0; c < eob; c++)
            {
                int pos = scan[c];
                int q = _quant[pos];
                int sign = 0;
                if (q != 0)
                {
                    if (c == 0)
                    {
                        int dcSignCtx = GetDcSignCtx(plane, x4, y4, w4, h4);
                        sign = sym.ReadSymbol(_cdf.DcSign.AsSpan((ptype * 3 + dcSignCtx) * 3, 3));
                    }
                    else
                    {
                        sign = sym.ReadBool();
                    }
                }

                if (q > 14) // NUM_BASE_LEVELS(2) + COEFF_BASE_RANGE(12) = 14
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

                if (pos == 0 && q > 0)
                    dcCategory = sign != 0 ? 1 : 2;

                q &= 0xFFFFF;
                culLevel += q;
                _quant[pos] = sign != 0 ? -q : q;
            }

            culLevel = Math.Min(63, culLevel);
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
        if (sqrUp >= Av1Constants.Tx32x32)
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
            int uColStart = (c * stepX + unitSize - 1) / unitSize;
            int uColEnd = Math.Min(unitCols, ((c + w) * stepX + unitSize - 1) / unitSize);

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
