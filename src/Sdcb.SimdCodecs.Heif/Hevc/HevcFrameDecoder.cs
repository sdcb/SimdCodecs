namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// Decodes one intra-coded HEVC picture (all its slice segments) into a <see cref="HevcPicture"/>:
/// slice data parsing (§7.3.8), intra reconstruction (§8.4, §8.6) and the in-loop filters (§8.7, in
/// <c>HevcLoopFilter.cs</c>).
/// </summary>
/// <remarks>
/// Per-picture side information is kept at 4×4 luma granularity (index <c>(y &gt;&gt; 2) * _w4 + (x &gt;&gt; 2)</c>):
/// coding-tree depth, luma intra mode, QpY, the "no loop filter" flag of transquant-bypass CUs and the
/// deblocking edge strengths. Tiles and PCM are not supported and are rejected up front.
/// </remarks>
internal sealed partial class HevcFrameDecoder
{
    private const int MaxTbSize = 32;

    private readonly HevcSps _sps;
    private readonly HevcPps _pps;
    private readonly HevcPicture _pic;

    private readonly int _w4, _h4;
    private readonly int _blocks;
    private readonly byte[] _ctDepth;
    private readonly byte[] _intraMode;
    private readonly sbyte[] _qpY;
    private readonly byte[] _noFilter;
    private readonly int[] _decoded;    // SliceAddrRs of each reconstructed 4x4 luma block, -1 before (intra reference availability)
    private readonly byte[] _edgeVer;   // bS of the vertical edge on the left of each 4x4 block
    private readonly byte[] _edgeHor;   // bS of the horizontal edge on top of each 4x4 block
    private readonly int[] _ctbSliceAddr;  // SliceAddrRs of the slice containing each CTB, -1 while undecoded
    private readonly HevcSliceHeader?[] _ctbSlice;
    private readonly SaoParams[] _sao;
    private readonly int[] _zscan;      // morton order of the minimum transform blocks inside a CTB
    private readonly int _zscanShift;
    private readonly int _qpBdOffsetY, _qpBdOffsetC;
    private readonly int _log2MinCuQpDeltaSize;
    private readonly bool _parallelFilters; // the picture is decoded alone: CTB rows and filter passes may run in parallel

    // Current slice / CTU / CU state.
    private HevcSliceHeader _slice = null!;
    private HevcCabac _cabac = null!;
    private int _ctbAddrRs;
    private bool _cuTransquantBypass;
    private int _intraChromaMode; // IntraPredModeC of the current CU
    private int _qpYPred;
    private int _qpYCurrent;
    private int _lastCuQpY;
    private bool _firstQgInSlice;
    private bool _firstQgInCtbRow;
    private bool _isCuQpDeltaCoded;
    private int _cuQpDeltaVal;

    // Scratch buffers.
    private readonly int[] _coeffs = new int[MaxTbSize * MaxTbSize];
    private readonly int[] _residual = new int[MaxTbSize * MaxTbSize];
    private readonly int[] _refs = new int[4 * MaxTbSize + 1];
    private readonly bool[] _refAvailable = new bool[4 * MaxTbSize + 1];
    private readonly byte[] _csbf = new byte[64];
    private readonly byte[] _wppContexts = new byte[HevcCabacTables.Count];
    private readonly byte[] _dsContexts = new byte[HevcCabacTables.Count];
    private bool _wppSaved;

    internal struct SaoParams
    {
        internal byte TypeY, TypeCb, TypeCr;
        internal byte BandY, BandCb, BandCr;
        internal byte ClassY, ClassCb, ClassCr;
        internal sbyte O0, O1, O2, O3, O4, O5, O6, O7, O8, O9, O10, O11; // SaoOffsetVal[c][1..4]

        internal readonly int Type(int c) => c == 0 ? TypeY : c == 1 ? TypeCb : TypeCr;
        internal readonly int Band(int c) => c == 0 ? BandY : c == 1 ? BandCb : BandCr;
        internal readonly int Class(int c) => c == 0 ? ClassY : c == 1 ? ClassCb : ClassCr;

        internal readonly int Offset(int c, int i) => (c * 4 + i) switch
        {
            0 => O0, 1 => O1, 2 => O2, 3 => O3, 4 => O4, 5 => O5,
            6 => O6, 7 => O7, 8 => O8, 9 => O9, 10 => O10, _ => O11,
        };

        internal void Set(int c, int type, int band, int eoClass, ReadOnlySpan<int> offsets)
        {
            switch (c)
            {
                case 0: TypeY = (byte)type; BandY = (byte)band; ClassY = (byte)eoClass; O0 = (sbyte)offsets[0]; O1 = (sbyte)offsets[1]; O2 = (sbyte)offsets[2]; O3 = (sbyte)offsets[3]; break;
                case 1: TypeCb = (byte)type; BandCb = (byte)band; ClassCb = (byte)eoClass; O4 = (sbyte)offsets[0]; O5 = (sbyte)offsets[1]; O6 = (sbyte)offsets[2]; O7 = (sbyte)offsets[3]; break;
                default: TypeCr = (byte)type; BandCr = (byte)band; ClassCr = (byte)eoClass; O8 = (sbyte)offsets[0]; O9 = (sbyte)offsets[1]; O10 = (sbyte)offsets[2]; O11 = (sbyte)offsets[3]; break;
            }
        }
    }

    internal HevcFrameDecoder(HevcSps sps, HevcPps pps, bool pooled = false, bool parallel = false)
    {
        if (pps.TilesEnabled) throw new InvalidDataException("HEVC tiles are not supported.");
        if (sps.PcmEnabled) throw new InvalidDataException("HEVC PCM coding is not supported.");
        if (sps.ChromaFormatIdc != 1) throw new InvalidDataException($"HEVC chroma_format_idc {sps.ChromaFormatIdc} is not supported; only 4:2:0 is.");
        if (sps.BitDepthY > 10 || sps.BitDepthC > 10) throw new InvalidDataException("HEVC bit depths above 10 are not supported.");

        _sps = sps;
        _pps = pps;
        _parallelFilters = parallel;
        _pic = new HevcPicture(sps.Width, sps.Height, sps.ChromaFormatIdc, sps.BitDepthY, sps.BitDepthC, pooled)
        {
            CropLeft = sps.ConfLeft,
            CropRight = sps.ConfRight,
            CropTop = sps.ConfTop,
            CropBottom = sps.ConfBottom,
        };
        _w4 = (sps.Width + 3) >> 2;
        _h4 = (sps.Height + 3) >> 2;
        var blocks = _w4 * _h4;
        _blocks = blocks;
        // The 4x4 side information is rented (a grid decodes one picture per tile) and cleared: rented arrays may be
        // longer than the picture and hold earlier contents. ReleaseScratch returns them.
        _ctDepth = RentCleared<byte>(blocks);
        _intraMode = RentCleared<byte>(blocks);
        _qpY = RentCleared<sbyte>(blocks);
        _noFilter = RentCleared<byte>(blocks);
        _decoded = RentCleared<int>(blocks);
        _decoded.AsSpan().Fill(-1);
        _edgeVer = RentCleared<byte>(blocks);
        _edgeHor = RentCleared<byte>(blocks);
        var ctbs = sps.PicWidthInCtbs * sps.PicHeightInCtbs;
        _ctbSliceAddr = new int[ctbs];
        Array.Fill(_ctbSliceAddr, -1);
        _ctbSlice = new HevcSliceHeader?[ctbs];
        _sao = new SaoParams[ctbs];
        _qpBdOffsetY = 6 * (sps.BitDepthY - 8);
        _qpBdOffsetC = 6 * (sps.BitDepthC - 8);
        _log2MinCuQpDeltaSize = sps.CtbLog2SizeY - pps.DiffCuQpDeltaDepth;
        if (_log2MinCuQpDeltaSize < sps.MinCbLog2SizeY) throw new InvalidDataException("diff_cu_qp_delta_depth is out of range.");

        // 6.5.2: z-scan order of the minimum transform blocks inside one CTB.
        _zscanShift = sps.CtbLog2SizeY - sps.MinTbLog2SizeY;
        var side = 1 << _zscanShift;
        _zscan = new int[side * side];
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var z = 0;
                for (var bit = 0; bit < _zscanShift; bit++)
                {
                    z |= ((x >> bit) & 1) << (2 * bit);
                    z |= ((y >> bit) & 1) << (2 * bit + 1);
                }
                _zscan[y * side + x] = z;
            }
        }
    }

    internal HevcPicture Picture => _pic;

    private static T[] RentCleared<T>(int length)
    {
        var array = System.Buffers.ArrayPool<T>.Shared.Rent(length);
        array.AsSpan().Clear();
        return array;
    }

    /// <summary>Returns the rented side information; the decoder must not be used afterwards (the picture may).</summary>
    internal void ReleaseScratch()
    {
        System.Buffers.ArrayPool<byte>.Shared.Return(_ctDepth);
        System.Buffers.ArrayPool<byte>.Shared.Return(_intraMode);
        System.Buffers.ArrayPool<sbyte>.Shared.Return(_qpY);
        System.Buffers.ArrayPool<byte>.Shared.Return(_noFilter);
        System.Buffers.ArrayPool<int>.Shared.Return(_decoded);
        System.Buffers.ArrayPool<byte>.Shared.Return(_edgeVer);
        System.Buffers.ArrayPool<byte>.Shared.Return(_edgeHor);
    }

    /// <summary>Decodes the slice segments (unescaped RBSPs with their NAL types) of one picture.</summary>
    internal HevcPicture Decode(IReadOnlyList<(int NalType, byte[] Rbsp)> slices)
    {
        HevcSliceHeader? previous = null;
        var decodedCtbs = 0;
        foreach (var (nalType, rbsp) in slices)
        {
            var header = HevcSliceHeader.Parse(rbsp, nalType, _sps, _pps, previous);
            if (previous is null && !header.FirstSliceSegmentInPic) throw new InvalidDataException("The first slice segment of the picture is missing.");
            decodedCtbs += DecodeSliceSegment(header, rbsp);
            previous = header;
        }
        if (decodedCtbs != _ctbSliceAddr.Length || Array.IndexOf(_ctbSliceAddr, -1) >= 0)
        {
            throw new InvalidDataException("The picture's slices do not cover every CTB.");
        }

        FilterPicture();
        return _pic;
    }

    /// <summary>The in-loop filters over the whole picture: deblocking, then SAO (8.7).</summary>
    private void FilterPicture()
    {
        if (!HeifDiagnostics.Skips(HeifAblation.SkipDeblock)) Deblock();
        if (!HeifDiagnostics.Skips(HeifAblation.SkipSao)) ApplySao();
    }

    /// <summary>Runs <paramref name="body"/> for each CTB row, in parallel when the picture is decoded alone.</summary>
    private void ForEachCtbRow(Action<int> body)
    {
        var rows = _sps.PicHeightInCtbs;
        if (_parallelFilters && rows > 1 && Environment.ProcessorCount > 1) Parallel.For(0, rows, body);
        else for (var r = 0; r < rows; r++) body(r);
    }

    // ───────────────────────────── slice data (7.3.8.1) ─────────────────────────────

    private int DecodeSliceSegment(HevcSliceHeader header, byte[] rbsp)
    {
        _slice = header;
        _ctbAddrRs = header.SegmentAddress;
        if (_ctbSliceAddr[_ctbAddrRs] != -1) throw new InvalidDataException("Slice segments overlap.");
        _cabac = new HevcCabac(rbsp, header.DataOffset);
        if (!header.DependentSliceSegment)
        {
            _firstQgInSlice = true;
            _lastCuQpY = header.SliceQpY;
        }

        var widthInCtbs = _sps.PicWidthInCtbs;
        var total = _ctbSliceAddr.Length;
        var count = 0;
        var first = true;
        while (true)
        {
            if (_ctbAddrRs >= total) throw new InvalidDataException("Slice data runs past the last CTB.");
            if (_ctbSliceAddr[_ctbAddrRs] != -1) throw new InvalidDataException("Slice segments overlap.");
            var ctbX = _ctbAddrRs % widthInCtbs;
            var ctbY = _ctbAddrRs / widthInCtbs;

            // 9.3.1 / 9.3.2.1: context initialisation at the start of the segment and of each WPP row.
            if (first || (_pps.EntropyCodingSyncEnabled && ctbX == 0))
            {
                InitializeContextsForCtu(ctbX, ctbY, first);
            }
            if (_pps.EntropyCodingSyncEnabled && ctbX == 0) _firstQgInCtbRow = true;
            first = false;

            _ctbSliceAddr[_ctbAddrRs] = header.SliceAddrRs;
            _ctbSlice[_ctbAddrRs] = header;
            CodingTreeUnit(ctbX, ctbY);
            count++;

            // 9.3.2.4: WPP storage after the second CTB of a row.
            if (_pps.EntropyCodingSyncEnabled && ctbX == 1)
            {
                _cabac.SaveContexts(_wppContexts);
                _wppSaved = true;
            }

            var endOfSlice = _cabac.DecodeTerminate() == 1;
            _ctbAddrRs++;
            if (endOfSlice)
            {
                if (_pps.DependentSliceSegmentsEnabled) _cabac.SaveContexts(_dsContexts);
                return count;
            }
            if (_pps.EntropyCodingSyncEnabled && _ctbAddrRs % widthInCtbs == 0)
            {
                if (_cabac.DecodeTerminate() != 1) throw new InvalidDataException("end_of_subset_one_bit is not 1.");
                // byte_alignment(): the terminating bin consumed the 1 bit; skip to the byte boundary and restart.
                _cabac.InitEngine((int)((_cabac.BitPosition + 7) >> 3));
            }
        }
    }

    private void InitializeContextsForCtu(int ctbX, int ctbY, bool sliceSegmentStart)
    {
        var ctbSize = _sps.CtbSizeY;
        if (_ctbAddrRs == 0)
        {
            _cabac.InitContexts(_slice.SliceQpY, 0);
            return;
        }
        if (_pps.EntropyCodingSyncEnabled && ctbX == 0)
        {
            // Spatial neighbour T = (x0 + CtbSizeY, y0 - CtbSizeY).
            var xT = (ctbX + 1) * ctbSize;
            var yT = ctbY * ctbSize - ctbSize;
            var available = _wppSaved && xT < _sps.Width && yT >= 0 &&
                            _ctbSliceAddr[(ctbY - 1) * _sps.PicWidthInCtbs + ctbX + 1] == _slice.SliceAddrRs;
            if (available) _cabac.LoadContexts(_wppContexts);
            else _cabac.InitContexts(_slice.SliceQpY, 0);
            return;
        }
        if (sliceSegmentStart && _slice.DependentSliceSegment)
        {
            _cabac.LoadContexts(_dsContexts);
            return;
        }
        if (sliceSegmentStart) _cabac.InitContexts(_slice.SliceQpY, 0);
    }

    // ───────────────────────────── availability (6.4.1) ─────────────────────────────

    private int ZAddress(int x, int y)
    {
        var ctbLog2 = _sps.CtbLog2SizeY;
        var ctbAddr = (y >> ctbLog2) * _sps.PicWidthInCtbs + (x >> ctbLog2);
        var mask = (1 << ctbLog2) - 1;
        var minTb = _sps.MinTbLog2SizeY;
        var inner = _zscan[(((y & mask) >> minTb) << _zscanShift) + ((x & mask) >> minTb)];
        return (ctbAddr << (2 * _zscanShift)) + inner;
    }

    private bool Available(int xCurr, int yCurr, int xN, int yN)
    {
        if (xN < 0 || yN < 0 || xN >= _sps.Width || yN >= _sps.Height) return false;
        if (ZAddress(xN, yN) > ZAddress(xCurr, yCurr)) return false;
        var ctbLog2 = _sps.CtbLog2SizeY;
        var ctb = (yN >> ctbLog2) * _sps.PicWidthInCtbs + (xN >> ctbLog2);
        return _ctbSliceAddr[ctb] == _slice.SliceAddrRs;
    }

    private int Block(int x, int y) => (y >> 2) * _w4 + (x >> 2);

    // ───────────────────────────── CTU / SAO syntax (7.3.8.2, 7.3.8.3) ─────────────────────────────

    private void CodingTreeUnit(int ctbX, int ctbY)
    {
        if (_slice.SaoLuma || _slice.SaoChroma) ParseSao(ctbX, ctbY);
        CodingQuadtree(ctbX << _sps.CtbLog2SizeY, ctbY << _sps.CtbLog2SizeY, _sps.CtbLog2SizeY, 0);
    }

    private void ParseSao(int rx, int ry)
    {
        var width = _sps.PicWidthInCtbs;
        var mergeLeft = false;
        var mergeUp = false;
        if (rx > 0 && _ctbAddrRs - 1 >= _slice.SliceAddrRs)
        {
            mergeLeft = _cabac.DecodeBin(HevcCabacTables.SaoMerge) == 1;
        }
        if (ry > 0 && !mergeLeft && _ctbAddrRs - width >= _slice.SliceAddrRs)
        {
            mergeUp = _cabac.DecodeBin(HevcCabacTables.SaoMerge) == 1;
        }
        if (mergeLeft)
        {
            _sao[_ctbAddrRs] = _sao[_ctbAddrRs - 1];
            return;
        }
        if (mergeUp)
        {
            _sao[_ctbAddrRs] = _sao[_ctbAddrRs - width];
            return;
        }

        var p = new SaoParams();
        Span<int> offsets = stackalloc int[4];
        var chromaType = 0;
        var chromaClass = 0;
        for (var c = 0; c < 3; c++)
        {
            offsets.Clear();
            if ((c == 0 && !_slice.SaoLuma) || (c > 0 && !_slice.SaoChroma))
            {
                p.Set(c, 0, 0, 0, offsets);
                continue;
            }
            int type;
            if (c == 2)
            {
                type = chromaType;
            }
            else
            {
                // sao_type_idx: TR, cMax = 2, first bin context-coded, second bypass.
                type = _cabac.DecodeBin(HevcCabacTables.SaoType) == 0 ? 0 : _cabac.DecodeBypass() == 0 ? 1 : 2;
                if (c == 1) chromaType = type;
            }
            if (type == 0)
            {
                p.Set(c, 0, 0, 0, offsets);
                continue;
            }

            var bitDepth = c == 0 ? _sps.BitDepthY : _sps.BitDepthC;
            var cMax = (1 << (Math.Min(bitDepth, 10) - 5)) - 1;
            for (var i = 0; i < 4; i++)
            {
                var abs = 0;
                while (abs < cMax && _cabac.DecodeBypass() == 1) abs++;
                offsets[i] = abs;
            }

            var band = 0;
            var eoClass = 0;
            if (type == 1)
            {
                for (var i = 0; i < 4; i++)
                {
                    if (offsets[i] != 0 && _cabac.DecodeBypass() == 1) offsets[i] = -offsets[i];
                }
                band = (int)_cabac.DecodeBypassBits(5);
            }
            else
            {
                // Edge offset: categories 1, 2 positive, 3, 4 negative (7.4.9.3.2).
                offsets[2] = -offsets[2];
                offsets[3] = -offsets[3];
                if (c == 0) eoClass = (int)_cabac.DecodeBypassBits(2);
                else if (c == 1) eoClass = chromaClass = (int)_cabac.DecodeBypassBits(2);
                else eoClass = chromaClass;
            }
            p.Set(c, type, band, eoClass, offsets);
        }
        _sao[_ctbAddrRs] = p;
    }

    // ───────────────────────────── coding quadtree / unit (7.3.8.4, 7.3.8.5) ─────────────────────────────

    private void CodingQuadtree(int x0, int y0, int log2CbSize, int depth)
    {
        var size = 1 << log2CbSize;
        bool split;
        if (x0 + size <= _sps.Width && y0 + size <= _sps.Height && log2CbSize > _sps.MinCbLog2SizeY)
        {
            var ctxInc = 0;
            if (Available(x0, y0, x0 - 1, y0) && _ctDepth[Block(x0 - 1, y0)] > depth) ctxInc++;
            if (Available(x0, y0, x0, y0 - 1) && _ctDepth[Block(x0, y0 - 1)] > depth) ctxInc++;
            split = _cabac.DecodeBin(HevcCabacTables.SplitCu + ctxInc) == 1;
        }
        else
        {
            split = log2CbSize > _sps.MinCbLog2SizeY;
        }

        if (log2CbSize >= _log2MinCuQpDeltaSize)
        {
            _isCuQpDeltaCoded = false;
            _cuQpDeltaVal = 0;
            StartQuantizationGroup(x0, y0);
        }

        if (split)
        {
            var half = size >> 1;
            var x1 = x0 + half;
            var y1 = y0 + half;
            CodingQuadtree(x0, y0, log2CbSize - 1, depth + 1);
            if (x1 < _sps.Width) CodingQuadtree(x1, y0, log2CbSize - 1, depth + 1);
            if (y1 < _sps.Height) CodingQuadtree(x0, y1, log2CbSize - 1, depth + 1);
            if (x1 < _sps.Width && y1 < _sps.Height) CodingQuadtree(x1, y1, log2CbSize - 1, depth + 1);
        }
        else
        {
            CodingUnit(x0, y0, log2CbSize, depth);
        }
    }

    /// <summary>qPY_PRED of the quantization group starting at (xQg, yQg), §8.6.1.</summary>
    private void StartQuantizationGroup(int xQg, int yQg)
    {
        int prev;
        // The flags are cleared by the first CU decoded, so every quadtree level that opens the first
        // quantization group of a slice or WPP row sees qPY_PREV = SliceQpY.
        if (_firstQgInSlice || _firstQgInCtbRow)
        {
            prev = _slice.SliceQpY;
        }
        else
        {
            prev = _lastCuQpY;
        }

        var ctbMask = _sps.CtbSizeY - 1;
        // qPY_A / qPY_B: the neighbour's QpY when it is available and in the same CTB, else qPY_PREV.
        var qpA = (xQg & ctbMask) != 0 && Available(xQg, yQg, xQg - 1, yQg) ? _qpY[Block(xQg - 1, yQg)] : prev;
        var qpB = (yQg & ctbMask) != 0 && Available(xQg, yQg, xQg, yQg - 1) ? _qpY[Block(xQg, yQg - 1)] : prev;
        _qpYPred = (qpA + qpB + 1) >> 1;
    }

    private void UpdateCuQp()
    {
        // (8-283).
        _qpYCurrent = ((_qpYPred + _cuQpDeltaVal + 52 + 2 * _qpBdOffsetY) % (52 + _qpBdOffsetY)) - _qpBdOffsetY;
    }

    private void CodingUnit(int x0, int y0, int log2CbSize, int depth)
    {
        var size = 1 << log2CbSize;
        _cuTransquantBypass = _pps.TransquantBypassEnabled && _cabac.DecodeBin(HevcCabacTables.TransquantBypass) == 1;
        UpdateCuQp();

        // Intra CU in an I slice: part_mode only at the minimum CB size, 1 → PART_2Nx2N, 0 → PART_NxN.
        var partNxN = log2CbSize == _sps.MinCbLog2SizeY && _cabac.DecodeBin(HevcCabacTables.PartMode) == 0;

        // Fill the CU's side information first: the luma-mode derivation of later PUs reads it.
        var pbOffset = partNxN ? size >> 1 : size;
        var xEnd = Math.Min(x0 + size, _sps.Width);
        var yEnd = Math.Min(y0 + size, _sps.Height);
        for (var y = y0; y < yEnd; y += 4)
        {
            for (var x = x0; x < xEnd; x += 4)
            {
                var b = Block(x, y);
                _ctDepth[b] = (byte)depth;
                _noFilter[b] = (byte)(_cuTransquantBypass ? 1 : 0);
            }
        }

        // prev_intra_luma_pred_flag for every PB, then mpm_idx / rem_intra_luma_pred_mode (7.3.8.5).
        Span<bool> prevFlags = stackalloc bool[4];
        var pbCount = partNxN ? 4 : 1;
        for (var i = 0; i < pbCount; i++) prevFlags[i] = _cabac.DecodeBin(HevcCabacTables.PrevIntraLuma) == 1;
        for (var i = 0; i < pbCount; i++)
        {
            var xPb = x0 + (i & 1) * pbOffset;
            var yPb = y0 + (i >> 1) * pbOffset;
            int mode;
            if (prevFlags[i])
            {
                var mpmIdx = _cabac.DecodeBypass() == 0 ? 0 : _cabac.DecodeBypass() == 0 ? 1 : 2;
                mode = DeriveLumaMode(xPb, yPb, mpmIdx, -1);
            }
            else
            {
                mode = DeriveLumaMode(xPb, yPb, -1, (int)_cabac.DecodeBypassBits(5));
            }
            var pbEndX = Math.Min(xPb + pbOffset, _sps.Width);
            var pbEndY = Math.Min(yPb + pbOffset, _sps.Height);
            for (var y = yPb; y < pbEndY; y += 4)
                for (var x = xPb; x < pbEndX; x += 4)
                    _intraMode[Block(x, y)] = (byte)mode;
        }

        // intra_chroma_pred_mode (4:2:0: one per CU) → IntraPredModeC (Table 8-2).
        int chromaSyntax = _cabac.DecodeBin(HevcCabacTables.IntraChroma) == 0 ? 4 : (int)_cabac.DecodeBypassBits(2);
        var lumaMode = _intraMode[Block(x0, y0)];
        if (chromaSyntax == 4)
        {
            _intraChromaMode = lumaMode;
        }
        else
        {
            var candidate = chromaSyntax switch { 0 => 0, 1 => 26, 2 => 10, _ => 1 };
            _intraChromaMode = candidate == lumaMode ? 34 : candidate;
        }

        var maxTrafoDepth = _sps.MaxTransformHierarchyDepthIntra + (partNxN ? 1 : 0);
        TransformTree(x0, y0, x0, y0, log2CbSize, 0, 0, maxTrafoDepth, partNxN, true, true);

        // QpY of the CU (8-283, with the CuQpDeltaVal decoded in it, if any) for prediction and deblocking.
        UpdateCuQp();
        for (var y = y0; y < yEnd; y += 4)
            for (var x = x0; x < xEnd; x += 4)
                _qpY[Block(x, y)] = (sbyte)_qpYCurrent;
        _lastCuQpY = _qpYCurrent;
        _firstQgInSlice = false;
        _firstQgInCtbRow = false;
    }

    /// <summary>8.4.2: IntraPredModeY from the candidate list (<paramref name="mpmIdx"/> ≥ 0) or rem_intra_luma_pred_mode.</summary>
    private int DeriveLumaMode(int xPb, int yPb, int mpmIdx, int remMode)
    {
        var candA = Available(xPb, yPb, xPb - 1, yPb) ? _intraMode[Block(xPb - 1, yPb)] : 1;
        var candB = 1;
        var ctbTop = (yPb >> _sps.CtbLog2SizeY) << _sps.CtbLog2SizeY;
        if (yPb - 1 >= ctbTop && Available(xPb, yPb, xPb, yPb - 1)) candB = _intraMode[Block(xPb, yPb - 1)];

        Span<int> list = stackalloc int[3];
        if (candA == candB)
        {
            if (candA < 2)
            {
                list[0] = 0;
                list[1] = 1;
                list[2] = 26;
            }
            else
            {
                list[0] = candA;
                list[1] = 2 + ((candA + 29) % 32);
                list[2] = 2 + ((candA - 2 + 1) % 32);
            }
        }
        else
        {
            list[0] = candA;
            list[1] = candB;
            list[2] = candA != 0 && candB != 0 ? 0 : candA != 1 && candB != 1 ? 1 : 26;
        }

        if (mpmIdx >= 0) return list[mpmIdx];

        if (list[0] > list[1]) (list[0], list[1]) = (list[1], list[0]);
        if (list[0] > list[2]) (list[0], list[2]) = (list[2], list[0]);
        if (list[1] > list[2]) (list[1], list[2]) = (list[2], list[1]);
        var mode = remMode;
        for (var i = 0; i < 3; i++)
        {
            if (mode >= list[i]) mode++;
        }
        return mode;
    }

    // ───────────────────────────── transform tree / unit (7.3.8.8, 7.3.8.10) ─────────────────────────────

    private void TransformTree(int x0, int y0, int xBase, int yBase, int log2Size, int depth, int blkIdx,
        int maxTrafoDepth, bool intraSplit, bool parentCbfCb, bool parentCbfCr)
    {
        bool split;
        if (log2Size <= _sps.MaxTbLog2SizeY && log2Size > _sps.MinTbLog2SizeY && depth < maxTrafoDepth && !(intraSplit && depth == 0))
        {
            split = _cabac.DecodeBin(HevcCabacTables.SplitTransform + 5 - log2Size) == 1;
        }
        else
        {
            split = log2Size > _sps.MaxTbLog2SizeY || (intraSplit && depth == 0);
        }

        bool cbfCb, cbfCr;
        if (log2Size > 2)
        {
            var ctx = HevcCabacTables.CbfChroma + Math.Min(depth, 4);
            cbfCb = (depth == 0 || parentCbfCb) && _cabac.DecodeBin(ctx) == 1;
            cbfCr = (depth == 0 || parentCbfCr) && _cabac.DecodeBin(ctx) == 1;
        }
        else
        {
            // 4:2:0 4x4 luma blocks: the chroma block belongs to the parent (cbf taken from depth - 1).
            cbfCb = parentCbfCb;
            cbfCr = parentCbfCr;
        }

        if (split)
        {
            var half = 1 << (log2Size - 1);
            TransformTree(x0, y0, x0, y0, log2Size - 1, depth + 1, 0, maxTrafoDepth, intraSplit, cbfCb, cbfCr);
            TransformTree(x0 + half, y0, x0, y0, log2Size - 1, depth + 1, 1, maxTrafoDepth, intraSplit, cbfCb, cbfCr);
            TransformTree(x0, y0 + half, x0, y0, log2Size - 1, depth + 1, 2, maxTrafoDepth, intraSplit, cbfCb, cbfCr);
            TransformTree(x0 + half, y0 + half, x0, y0, log2Size - 1, depth + 1, 3, maxTrafoDepth, intraSplit, cbfCb, cbfCr);
            return;
        }

        var cbfLuma = _cabac.DecodeBin(HevcCabacTables.CbfLuma + (depth == 0 ? 1 : 0)) == 1;
        TransformUnit(x0, y0, xBase, yBase, log2Size, blkIdx, cbfLuma, cbfCb, cbfCr);
    }

    private void TransformUnit(int x0, int y0, int xBase, int yBase, int log2Size, int blkIdx, bool cbfLuma, bool cbfCb, bool cbfCr)
    {
        if ((cbfLuma || cbfCb || cbfCr) && _pps.CuQpDeltaEnabled && !_isCuQpDeltaCoded)
        {
            // cu_qp_delta_abs: prefix TU (cMax 5, ctxInc 0 then 1), suffix EG0 bypass; then the sign.
            var abs = 0;
            if (_cabac.DecodeBin(HevcCabacTables.CuQpDeltaAbs) == 1)
            {
                abs = 1;
                while (abs < 5 && _cabac.DecodeBin(HevcCabacTables.CuQpDeltaAbs + 1) == 1) abs++;
                if (abs == 5) abs += (int)Math.Min(_cabac.DecodeExpGolomb(0), 1000u);
            }
            _isCuQpDeltaCoded = true;
            var delta = abs != 0 && _cabac.DecodeBypass() == 1 ? -abs : abs;
            // 7.4.9.14: CuQpDeltaVal lies in [-(26 + QpBdOffsetY / 2), 25 + QpBdOffsetY / 2].
            var limit = 26 + _qpBdOffsetY / 2;
            if (delta < -limit || delta > limit - 1) throw new InvalidDataException("CuQpDeltaVal is out of range.");
            _cuQpDeltaVal = delta;
            UpdateCuQp();
        }

        // Luma: predict, then add the residual.
        var lumaMode = _intraMode[Block(x0, y0)];
        PredictIntra(0, x0, y0, log2Size, lumaMode);
        if (cbfLuma) ResidualAndReconstruct(0, x0, y0, log2Size, lumaMode);
        MarkTransformEdges(x0, y0, 1 << log2Size);
        MarkDecoded(x0, y0, 1 << log2Size);

        if (log2Size > 2)
        {
            var xC = x0 >> 1;
            var yC = y0 >> 1;
            PredictIntra(1, xC, yC, log2Size - 1, _intraChromaMode);
            if (cbfCb) ResidualAndReconstruct(1, xC, yC, log2Size - 1, _intraChromaMode);
            PredictIntra(2, xC, yC, log2Size - 1, _intraChromaMode);
            if (cbfCr) ResidualAndReconstruct(2, xC, yC, log2Size - 1, _intraChromaMode);
        }
        else if (blkIdx == 3)
        {
            var xC = xBase >> 1;
            var yC = yBase >> 1;
            PredictIntra(1, xC, yC, 2, _intraChromaMode);
            if (cbfCb) ResidualAndReconstruct(1, xC, yC, 2, _intraChromaMode);
            PredictIntra(2, xC, yC, 2, _intraChromaMode);
            if (cbfCr) ResidualAndReconstruct(2, xC, yC, 2, _intraChromaMode);
        }
    }

    /// <summary>Records the bS = 2 transform edges of a luma TB on the 8×8 grid (8.7.2.2 – 8.7.2.4; every CU is intra).</summary>
    private void MarkTransformEdges(int x0, int y0, int size)
    {
        if (_slice.DeblockingDisabled) return;
        var xEnd = Math.Min(x0 + size, _sps.Width);
        var yEnd = Math.Min(y0 + size, _sps.Height);
        if ((x0 & 7) == 0 && x0 > 0 && EdgeAllowed(x0 - 1, y0))
        {
            for (var y = y0; y < yEnd; y += 4) _edgeVer[Block(x0, y)] = 2;
        }
        if ((y0 & 7) == 0 && y0 > 0 && EdgeAllowed(x0, y0 - 1))
        {
            for (var x = x0; x < xEnd; x += 4) _edgeHor[Block(x, y0)] = 2;
        }
    }

    /// <summary>False when (xP, yP) lies in another slice and the current slice does not filter across slice boundaries.</summary>
    private bool EdgeAllowed(int xP, int yP)
    {
        if (_slice.LoopFilterAcrossSlices) return true;
        var ctbLog2 = _sps.CtbLog2SizeY;
        return _ctbSliceAddr[(yP >> ctbLog2) * _sps.PicWidthInCtbs + (xP >> ctbLog2)] == _slice.SliceAddrRs;
    }

    // ───────────────────────────── intra prediction (8.4.4.2) ─────────────────────────────

    /// <summary>
    /// Marks the luma TB at (x0, y0) reconstructed. Intra prediction reads a neighbouring sample only once it is
    /// reconstructed: in decoding order (CTBs in raster order, z-scan inside them) a sample outside the current
    /// block precedes it in z-scan order (6.4.1) exactly when it has been reconstructed, so "marked with the
    /// current slice's address" is the availability of 6.4.1 for intra reference samples.
    /// </summary>
    private void MarkDecoded(int x0, int y0, int size)
    {
        var xEnd = Math.Min(x0 + size, _sps.Width);
        var yEnd = Math.Min(y0 + size, _sps.Height);
        var sliceAddr = _slice.SliceAddrRs;
        for (var y = y0; y < yEnd; y += 4)
        {
            _decoded.AsSpan(Block(x0, y), (xEnd - x0 + 3) >> 2).Fill(sliceAddr);
        }
    }

    /// <summary>Availability (6.4.1) of the luma sample (xN, yN) as an intra reference; see <see cref="MarkDecoded"/>.</summary>
    private bool ReferenceAvailable(int xN, int yN) =>
        (uint)xN < (uint)_sps.Width && (uint)yN < (uint)_sps.Height && _decoded[(yN >> 2) * _w4 + (xN >> 2)] == _slice.SliceAddrRs;

    /// <summary>Predicts the block of component <paramref name="cIdx"/> at (xTb, yTb) in that component's samples.</summary>
    private void PredictIntra(int cIdx, int xTb, int yTb, int log2Size, int mode)
    {
        if (HeifDiagnostics.Skips(HeifAblation.SkipIntraPred)) return;
        var n = 1 << log2Size;
        var plane = _pic.Planes[cIdx];
        var stride = _pic.PlaneWidth(cIdx);
        var shift = cIdx == 0 ? 0 : 1; // component → luma coordinates (4:2:0)
        var bitDepth = cIdx == 0 ? _sps.BitDepthY : _sps.BitDepthC;
        var unit = cIdx == 0 ? 4 : 2;  // samples per 4x4 luma availability unit
        var refs = _refs.AsSpan(0, 4 * n + 1);
        var available = _refAvailable.AsSpan(0, 4 * n + 1);
        var corner = 2 * n;
        var count = 0;

        // Left column, bottom (index 0 = p[-1][2N-1]) to top (index 2N-1 = p[-1][0]).
        var xLeft = xTb - 1;
        for (var y = 0; y < 2 * n; y += unit)
        {
            var ok = ReferenceAvailable(xLeft << shift, (yTb + y) << shift);
            var at = (yTb + y) * stride + xLeft;
            for (var k = 0; k < unit; k++)
            {
                var index = corner - 1 - (y + k);
                available[index] = ok;
                if (ok) refs[index] = plane[at + k * stride];
            }
            if (ok) count += unit;
        }

        // Corner.
        var cornerOk = ReferenceAvailable(xLeft << shift, (yTb - 1) << shift);
        available[corner] = cornerOk;
        if (cornerOk)
        {
            refs[corner] = plane[(yTb - 1) * stride + xLeft];
            count++;
        }

        // Top row, left to right.
        var top = (yTb - 1) * stride + xTb;
        for (var x = 0; x < 2 * n; x += unit)
        {
            var ok = ReferenceAvailable((xTb + x) << shift, (yTb - 1) << shift);
            var index = corner + 1 + x;
            available.Slice(index, unit).Fill(ok);
            if (ok)
            {
                for (var k = 0; k < unit; k++) refs[index + k] = plane[top + x + k];
                count += unit;
            }
        }

        if (count != refs.Length) HevcIntraPred.Substitute(refs, available, count > 0, bitDepth);
        HevcIntraPred.Predict(refs, log2Size, mode, cIdx == 0, cIdx == 0, _sps.StrongIntraSmoothingEnabled, bitDepth,
            plane.AsSpan(yTb * stride + xTb), stride);
    }

    // ───────────────────────────── residual coding (7.3.8.11) and reconstruction ─────────────────────────────

    private void ResidualAndReconstruct(int cIdx, int xTb, int yTb, int log2Size, int predModeIntra)
    {
        var n = 1 << log2Size;
        // _coeffs is all zero between blocks: each block clears the rectangle it wrote.
        var coeffs = _coeffs.AsSpan(0, n * n);
        var packed = ResidualCoding(cIdx, log2Size, predModeIntra, coeffs);
        var transformSkip = (packed & 1) != 0;
        var maxX = (packed >> 8) & 0xFF;
        var maxY = packed >> 16;

        if (!HeifDiagnostics.Skips(HeifAblation.SkipResidual))
        {
            var bitDepth = cIdx == 0 ? _sps.BitDepthY : _sps.BitDepthC;
            var stride = _pic.PlaneWidth(cIdx);
            var dst = _pic.Planes[cIdx].AsSpan(yTb * stride + xTb);
            if (_cuTransquantBypass)
            {
                HevcTransform.AddResidual(coeffs, log2Size, bitDepth, dst, stride);
            }
            else if (transformSkip)
            {
                HevcTransform.TransformSkip(coeffs, log2Size, bitDepth);
                HevcTransform.AddResidual(coeffs, log2Size, bitDepth, dst, stride);
            }
            else
            {
                HevcTransform.InverseAdd(coeffs, _residual, log2Size, cIdx == 0 && n == 4, maxX, maxY, bitDepth, dst, stride);
            }
        }

        // Transform skip maps zero to zero, so only the rectangle of coded coefficients is non-zero.
        for (var y = 0; y <= maxY; y++) coeffs.Slice(y * n, maxX + 1).Clear();
    }

    /// <summary>qP of (8-291 .. 8-293) for the current CU.</summary>
    private int ComponentQp(int cIdx)
    {
        if (cIdx == 0) return _qpYCurrent + _qpBdOffsetY;
        var offset = cIdx == 1 ? _pps.CbQpOffset + _slice.CbQpOffset : _pps.CrQpOffset + _slice.CrQpOffset;
        var qPi = Math.Clamp(_qpYCurrent + offset, -_qpBdOffsetC, 57);
        return ChromaQpTable(qPi) + _qpBdOffsetC;
    }

    /// <summary>Table 8-10 (ChromaArrayType 1).</summary>
    internal static int ChromaQpTable(int qPi)
    {
        if (qPi < 30) return qPi;
        if (qPi > 43) return qPi - 6;
        ReadOnlySpan<byte> table = [29, 30, 31, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37];
        return table[qPi - 30];
    }

    private static ReadOnlySpan<byte> CtxIdxMap => [0, 1, 4, 5, 2, 3, 4, 5, 6, 6, 8, 8, 7, 7, 8, 8];

    /// <summary>
    /// Parses residual_coding() into <paramref name="coeffs"/> (row-major N×N), scaled per (8-309) unless the CU
    /// is transquant-bypassed. Returns transform_skip_flag in bit 0 and the largest x and y of a non-zero
    /// coefficient in bits 8..15 and 16..23.
    /// </summary>
    private int ResidualCoding(int cIdx, int log2Size, int predModeIntra, Span<int> coeffs)
    {
        var n = 1 << log2Size;
        var cabac = _cabac;
        var transformSkip = false;
        if (_pps.TransformSkipEnabled && !_cuTransquantBypass && log2Size <= _pps.Log2MaxTransformSkipSize)
        {
            transformSkip = cabac.DecodeBin(HevcCabacTables.TransformSkip + (cIdx > 0 ? 1 : 0)) == 1;
        }

        // Scaling (8.6.3), applied as each coefficient is decoded.
        var bypass = _cuTransquantBypass;
        HevcTransform.Scaler scaler = default;
        ReadOnlySpan<byte> factors = default;
        if (!bypass)
        {
            var bitDepth = cIdx == 0 ? _sps.BitDepthY : _sps.BitDepthC;
            scaler = new HevcTransform.Scaler(log2Size, ComponentQp(cIdx), bitDepth);
            var scalingList = _pps.ScalingList ?? _sps.ScalingList;
            if (_sps.ScalingListEnabled && scalingList is not null && !(transformSkip && n > 4))
            {
                factors = scalingList.Factors[log2Size - 2][cIdx];
            }
        }
        var maxX = 0;
        var maxY = 0;

        // last_sig_coeff_{x,y}_prefix (9.3.4.2.3), then the suffixes.
        int ctxOffset, ctxShift;
        if (cIdx == 0)
        {
            ctxOffset = 3 * (log2Size - 2) + ((log2Size - 1) >> 2);
            ctxShift = (log2Size + 1) >> 2;
        }
        else
        {
            ctxOffset = 15;
            ctxShift = log2Size - 2;
        }
        var cMax = (log2Size << 1) - 1;
        var prefixX = 0;
        while (prefixX < cMax && cabac.DecodeBin(HevcCabacTables.LastX + ctxOffset + (prefixX >> ctxShift)) == 1) prefixX++;
        var prefixY = 0;
        while (prefixY < cMax && cabac.DecodeBin(HevcCabacTables.LastY + ctxOffset + (prefixY >> ctxShift)) == 1) prefixY++;
        var lastX = prefixX;
        if (prefixX > 3)
        {
            var bits = (prefixX >> 1) - 1;
            lastX = (1 << bits) * (2 + (prefixX & 1)) + (int)cabac.DecodeBypassBits(bits);
        }
        var lastY = prefixY;
        if (prefixY > 3)
        {
            var bits = (prefixY >> 1) - 1;
            lastY = (1 << bits) * (2 + (prefixY & 1)) + (int)cabac.DecodeBypassBits(bits);
        }

        // scanIdx (7.4.9.11): mode-dependent scans for 4x4 blocks and 8x8 luma blocks.
        var scanIdx = 0;
        if (log2Size == 2 || (log2Size == 3 && cIdx == 0))
        {
            if (predModeIntra is >= 6 and <= 14) scanIdx = 2;
            else if (predModeIntra is >= 22 and <= 30) scanIdx = 1;
        }
        if (scanIdx == 2) (lastX, lastY) = (lastY, lastX);
        if (lastX >= n || lastY >= n) throw new InvalidDataException("The last significant coefficient lies outside the transform block.");

        var log2Sb = log2Size - 2;
        var sbWidth = 1 << log2Sb;
        var subScan = HevcScan.Order[log2Sb][scanIdx];
        var scan4 = HevcScan.Order[2][scanIdx];

        // Locate the last sub-block and the last position inside it.
        var lastSubBlock = sbWidth * sbWidth - 1;
        var lastSbPacked = (lastX >> 2) | ((lastY >> 2) << 4);
        while (subScan[lastSubBlock] != lastSbPacked) lastSubBlock--;
        var lastInner = (lastX & 3) | ((lastY & 3) << 4);
        var lastScanPos = 15;
        while (scan4[lastScanPos] != lastInner) lastScanPos--;

        var csbf = _csbf.AsSpan(0, sbWidth * sbWidth);
        csbf.Clear();
        var signHidingEnabled = _pps.SignDataHidingEnabled && !_cuTransquantBypass;
        var greater1Base = HevcCabacTables.Greater1 + (cIdx > 0 ? 16 : 0);
        var greater2Base = HevcCabacTables.Greater2 + (cIdx > 0 ? 4 : 0);
        var sigBase = HevcCabacTables.Sig + (cIdx > 0 ? 27 : 0);

        Span<int> sigPos = stackalloc int[16]; // scan positions n of the significant coefficients, descending
        Span<int> absLevel = stackalloc int[16];
        var anySubBlockProcessed = false;
        var greater1Ctx = 1;

        for (var i = lastSubBlock; i >= 0; i--)
        {
            int xS = subScan[i] & 15, yS = subScan[i] >> 4;
            var inferSbDc = false;
            var right = xS < sbWidth - 1 ? csbf[yS * sbWidth + xS + 1] : 0;
            var below = yS < sbWidth - 1 ? csbf[(yS + 1) * sbWidth + xS] : 0;
            if (i < lastSubBlock && i > 0)
            {
                var ctxInc = Math.Min(right + below, 1) + (cIdx > 0 ? 2 : 0);
                csbf[yS * sbWidth + xS] = (byte)cabac.DecodeBin(HevcCabacTables.CodedSubBlock + ctxInc);
                inferSbDc = true;
            }
            else
            {
                csbf[yS * sbWidth + xS] = 1;
            }
            var coded = csbf[yS * sbWidth + xS] != 0;

            // sig_coeff_flag.
            var count = 0;
            var startN = 15;
            if (i == lastSubBlock)
            {
                sigPos[count++] = lastScanPos;
                startN = lastScanPos - 1;
            }
            if (coded)
            {
                var prevCsbf = right | (below << 1);
                for (var pos = startN; pos >= 0; pos--)
                {
                    var xP = scan4[pos] & 15;
                    var yP = scan4[pos] >> 4;
                    if (pos > 0 || !inferSbDc)
                    {
                        var xC = (xS << 2) + xP;
                        var yC = (yS << 2) + yP;
                        int sigCtx;
                        if (log2Size == 2)
                        {
                            sigCtx = CtxIdxMap[(yC << 2) + xC];
                        }
                        else if (xC + yC == 0)
                        {
                            sigCtx = 0;
                        }
                        else
                        {
                            sigCtx = prevCsbf switch
                            {
                                0 => xP + yP == 0 ? 2 : xP + yP < 3 ? 1 : 0,
                                1 => yP == 0 ? 2 : yP == 1 ? 1 : 0,
                                2 => xP == 0 ? 2 : xP == 1 ? 1 : 0,
                                _ => 2,
                            };
                            if (cIdx == 0)
                            {
                                if (xS + yS > 0) sigCtx += 3;
                                sigCtx += log2Size == 3 ? (scanIdx == 0 ? 9 : 15) : 21;
                            }
                            else
                            {
                                sigCtx += log2Size == 3 ? 9 : 12;
                            }
                        }
                        if (cabac.DecodeBin(sigBase + sigCtx) == 1)
                        {
                            sigPos[count++] = pos;
                            inferSbDc = false;
                        }
                    }
                    else
                    {
                        // DC of a coded sub-block whose other 15 flags were all 0: inferred significant.
                        sigPos[count++] = 0;
                    }
                }
            }
            if (count == 0) continue;

            // coeff_abs_level_greater1_flag (9.3.4.2.6).
            var ctxSet = i == 0 || cIdx > 0 ? 0 : 2;
            if (anySubBlockProcessed && greater1Ctx == 0) ctxSet++;
            anySubBlockProcessed = true;
            greater1Ctx = 1;
            var firstG1 = -1;
            var g1Count = Math.Min(count, 8);
            for (var k = 0; k < count; k++) absLevel[k] = 1;
            for (var k = 0; k < g1Count; k++)
            {
                var flag = cabac.DecodeBin(greater1Base + ctxSet * 4 + Math.Min(3, greater1Ctx));
                if (flag == 1)
                {
                    absLevel[k] = 2;
                    if (firstG1 < 0) firstG1 = k;
                    greater1Ctx = 0;
                }
                else if (greater1Ctx > 0)
                {
                    greater1Ctx++;
                }
            }
            if (firstG1 >= 0 && cabac.DecodeBin(greater2Base + ctxSet) == 1) absLevel[firstG1] = 3;

            // Signs (bypass), with sign data hiding of the last coefficient in scan order.
            var lastSigScanPos = sigPos[0];
            var firstSigScanPos = sigPos[count - 1];
            var signHidden = signHidingEnabled && lastSigScanPos - firstSigScanPos > 3;
            var signBits = signHidden ? count - 1 : count;
            var signs = cabac.DecodeBypassBits(signBits) << (32 - signBits);
            if (signBits == 0) signs = 0;

            // coeff_abs_level_remaining.
            var numSigCoeff = 0;
            var riceParam = 0;
            var sumAbs = 0;
            for (var k = 0; k < count; k++)
            {
                var baseLevel = absLevel[k];
                var threshold = numSigCoeff < 8 ? (k == firstG1 ? 3 : 2) : 1;
                var level = baseLevel;
                if (baseLevel == threshold)
                {
                    var remaining = DecodeAbsLevelRemaining(riceParam);
                    level = (int)Math.Min(baseLevel + remaining, 32767);
                    if (level > 3 * (1 << riceParam)) riceParam = Math.Min(riceParam + 1, 4);
                }
                numSigCoeff++;

                var pos = sigPos[k];
                var xC = (xS << 2) + (scan4[pos] & 15);
                var yC = (yS << 2) + (scan4[pos] >> 4);
                int value;
                if (signHidden && k == count - 1)
                {
                    sumAbs += level;
                    value = (sumAbs & 1) != 0 ? -level : level;
                }
                else
                {
                    sumAbs += level;
                    value = (signs & 0x8000_0000u) != 0 ? -level : level;
                    signs <<= 1;
                }
                var index = yC * n + xC;
                coeffs[index] = bypass ? value : scaler.Scale(value, factors.IsEmpty ? 16 : factors[index]);
                maxX = Math.Max(maxX, xC);
                maxY = Math.Max(maxY, yC);
            }
        }
        return (transformSkip ? 1 : 0) | (maxX << 8) | (maxY << 16);
    }

    /// <summary>coeff_abs_level_remaining with Rice parameter k (9.3.3.11): TR prefix (cMax 4 &lt;&lt; k), then EG(k + 1).</summary>
    private long DecodeAbsLevelRemaining(int k)
    {
        var prefix = 0;
        while (prefix < 4 && _cabac.DecodeBypass() == 1) prefix++;
        if (prefix < 4) return (prefix << k) + (k > 0 ? _cabac.DecodeBypassBits(k) : 0);

        // Suffix: k-th order Exp-Golomb with order k + 1 of (value - cMax).
        long value = 0;
        var order = k + 1;
        while (_cabac.DecodeBypass() == 1)
        {
            value += 1L << order;
            if (++order > 31) throw new InvalidDataException("coeff_abs_level_remaining escape code is too long.");
        }
        value += _cabac.DecodeBypassBits(order);
        return (4L << k) + value;
    }
}
