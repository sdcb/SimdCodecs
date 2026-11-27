namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Parsed AV1 uncompressed frame header (§5.9) for intra still images (M1: KEY_FRAME / INTRA_ONLY_FRAME).
/// </summary>
internal sealed class Av1FrameHeader
{
    internal int FrameType { get; private set; }
    internal bool FrameIsIntra { get; private set; }
    internal bool ShowFrame { get; private set; }
    internal bool ShowableFrame { get; private set; }
    internal bool ErrorResilientMode { get; private set; }
    internal bool DisableCdfUpdate { get; private set; }
    internal bool DisableFrameEndUpdateCdf { get; private set; }
    internal bool AllowScreenContentTools { get; private set; }
    internal bool ForceIntegerMv { get; private set; }
    internal bool AllowIntrabc { get; private set; }

    internal int FrameWidth { get; private set; }
    internal int FrameHeight { get; private set; }
    internal int UpscaledWidth { get; private set; }
    internal int RenderWidth { get; private set; }
    internal int RenderHeight { get; private set; }
    internal bool UseSuperres { get; private set; }
    internal int SuperresDenom { get; private set; } = Av1Constants.SuperresNum;
    internal int MiCols { get; private set; }
    internal int MiRows { get; private set; }

    // Tiles (§5.9.15)
    internal int TileCols { get; private set; } = 1;
    internal int TileRows { get; private set; } = 1;
    internal int TileColsLog2 { get; private set; }
    internal int TileRowsLog2 { get; private set; }
    internal int[] MiColStarts { get; private set; } = [];
    internal int[] MiRowStarts { get; private set; } = [];
    internal int ContextUpdateTileId { get; private set; }
    internal int TileSizeBytes { get; private set; }

    // Quantization (§5.9.12)
    internal int BaseQIdx { get; private set; }
    internal int DeltaQYDc { get; private set; }
    internal int DeltaQUDc { get; private set; }
    internal int DeltaQUAc { get; private set; }
    internal int DeltaQVDc { get; private set; }
    internal int DeltaQVAc { get; private set; }
    internal bool UsingQMatrix { get; private set; }
    internal int QmY { get; private set; } = 15;
    internal int QmU { get; private set; } = 15;
    internal int QmV { get; private set; } = 15;

    // Segmentation (§5.9.14)
    internal bool SegmentationEnabled { get; private set; }
    internal bool[,] FeatureEnabled { get; } = new bool[Av1Constants.MaxSegments, Av1Constants.SegLvlMax];
    internal int[,] FeatureData { get; } = new int[Av1Constants.MaxSegments, Av1Constants.SegLvlMax];
    internal bool SegIdPreSkip { get; private set; }
    internal int LastActiveSegId { get; private set; }
    internal bool[] LosslessArray { get; } = new bool[Av1Constants.MaxSegments];
    internal int[,] SegQmLevel { get; } = new int[3, Av1Constants.MaxSegments];
    internal bool CodedLossless { get; private set; }
    internal bool AllLossless { get; private set; }

    // Delta Q & Delta LF (§5.9.17, §5.9.18)
    internal bool DeltaQPresent { get; private set; }
    internal int DeltaQRes { get; private set; }
    internal bool DeltaLfPresent { get; private set; }
    internal int DeltaLfRes { get; private set; }
    internal bool DeltaLfMulti { get; private set; }

    // Loop filter (§5.9.11)
    internal int[] LoopFilterLevel { get; } = new int[4];
    internal int LoopFilterSharpness { get; private set; }
    internal bool LoopFilterDeltaEnabled { get; private set; }
    internal int[] LoopFilterRefDeltas { get; } = [1, 0, 0, 0, -1, 0, -1, -1];
    internal int[] LoopFilterModeDeltas { get; } = [0, 0];

    // CDEF (§5.9.19)
    internal int CdefDamping { get; private set; } = 3;
    internal int CdefBits { get; private set; }
    internal int[] CdefYPriStrength { get; } = new int[8];
    internal int[] CdefYSecStrength { get; } = new int[8];
    internal int[] CdefUvPriStrength { get; } = new int[8];
    internal int[] CdefUvSecStrength { get; } = new int[8];

    // Loop Restoration (§5.9.20)
    internal int[] FrameRestorationType { get; } = new int[3];
    internal int[] LoopRestorationSize { get; } = new int[3];
    internal bool UsesLr { get; private set; }
    internal bool UsesChromaLr { get; private set; }

    // Tx mode & misc
    internal int TxMode { get; private set; }
    internal bool ReducedTxSet { get; private set; }
    internal bool ApplyGrain { get; private set; }

    internal static Av1FrameHeader Parse(ref Av1BitReader br, Av1SequenceHeader seq, int temporalId = 0, int spatialId = 0)
    {
        var fh = new Av1FrameHeader();
        int idLen = seq.FrameIdNumbersPresent
            ? seq.AdditionalFrameIdLengthMinus1 + seq.DeltaFrameIdLengthMinus2 + 3
            : 0;

        if (seq.ReducedStillPictureHeader)
        {
            fh.FrameType = Av1Constants.KeyFrame;
            fh.FrameIsIntra = true;
            fh.ShowFrame = true;
            fh.ShowableFrame = false;
            fh.ErrorResilientMode = true;
        }
        else
        {
            if (br.ReadBool()) // show_existing_frame
                throw new InvalidDataException("AV1 show_existing_frame is not supported in M1 still-image decode.");

            fh.FrameType = (int)br.ReadBits(2);
            fh.FrameIsIntra = fh.FrameType is Av1Constants.KeyFrame or Av1Constants.IntraOnlyFrame;
            if (!fh.FrameIsIntra)
                throw new InvalidDataException($"AV1 frame type {fh.FrameType} is not supported in M1 (intra-only).");

            fh.ShowFrame = br.ReadBool();
            if (fh.ShowFrame && seq.DecoderModelInfoPresent && !seq.EqualPictureInterval)
                _ = br.ReadBits(seq.FramePresentationTimeLengthMinus1 + 1);

            fh.ShowableFrame = fh.ShowFrame ? fh.FrameType != Av1Constants.KeyFrame : br.ReadBool();
            fh.ErrorResilientMode = (fh.FrameType == Av1Constants.KeyFrame && fh.ShowFrame) || br.ReadBool();
        }

        fh.DisableCdfUpdate = br.ReadBool();
        fh.AllowScreenContentTools = seq.SeqForceScreenContentTools == Av1SequenceHeader.SelectScreenContentTools
            ? br.ReadBool()
            : seq.SeqForceScreenContentTools != 0;

        if (fh.AllowScreenContentTools)
        {
            fh.ForceIntegerMv = seq.SeqForceIntegerMv == Av1SequenceHeader.SelectIntegerMv
                ? br.ReadBool()
                : seq.SeqForceIntegerMv != 0;
        }
        if (fh.FrameIsIntra)
            fh.ForceIntegerMv = true;

        if (seq.FrameIdNumbersPresent)
            _ = br.ReadBits(idLen); // current_frame_id

        bool frameSizeOverride = !seq.ReducedStillPictureHeader && br.ReadBool();

        if (seq.OrderHintBits > 0)
            _ = br.ReadBits(seq.OrderHintBits); // order_hint

        if (seq.DecoderModelInfoPresent)
        {
            if (br.ReadBool()) // buffer_removal_time_present_flag
            {
                int n = seq.BufferRemovalTimeLengthMinus1 + 1;
                for (int op = 0; op <= seq.OperatingPointsCntMinus1; op++)
                {
                    if (!seq.DecoderModelPresentForOp[op]) continue;
                    int opPtIdc = seq.OperatingPointIdc[op];
                    bool inT = ((opPtIdc >> temporalId) & 1) != 0;
                    bool inS = ((opPtIdc >> (spatialId + 8)) & 1) != 0;
                    if (opPtIdc == 0 || (inT && inS))
                        _ = br.ReadBits(n);
                }
            }
        }

        int refreshFrameFlags = (fh.FrameType == Av1Constants.KeyFrame && fh.ShowFrame)
            ? 0xFF
            : (int)br.ReadBits(8);

        if (refreshFrameFlags != 0xFF && fh.ErrorResilientMode && seq.EnableOrderHint)
        {
            for (int i = 0; i < Av1Constants.NumRefFrames; i++)
                _ = br.ReadBits(seq.OrderHintBits);
        }

        // Frame size + Superres + Render size (§5.9.5, §5.9.6, §5.9.8)
        if (frameSizeOverride)
        {
            fh.FrameWidth = (int)br.ReadBits(seq.FrameWidthBitsMinus1 + 1) + 1;
            fh.FrameHeight = (int)br.ReadBits(seq.FrameHeightBitsMinus1 + 1) + 1;
        }
        else
        {
            fh.FrameWidth = seq.MaxFrameWidth;
            fh.FrameHeight = seq.MaxFrameHeight;
        }

        fh.UseSuperres = seq.EnableSuperres && br.ReadBool();
        if (fh.UseSuperres)
        {
            int codedDenom = (int)br.ReadBits(Av1Constants.SuperresDenomBits);
            fh.SuperresDenom = codedDenom + Av1Constants.SuperresDenomMin;
        }
        else
        {
            fh.SuperresDenom = Av1Constants.SuperresNum;
        }

        fh.UpscaledWidth = fh.FrameWidth;
        fh.FrameWidth = (fh.UpscaledWidth * Av1Constants.SuperresNum + (fh.SuperresDenom / 2)) / fh.SuperresDenom;
        fh.MiCols = 2 * ((fh.FrameWidth + 7) >> 3);
        fh.MiRows = 2 * ((fh.FrameHeight + 7) >> 3);

        if (br.ReadBool()) // render_and_frame_size_different
        {
            fh.RenderWidth = (int)br.ReadBits(16) + 1;
            fh.RenderHeight = (int)br.ReadBits(16) + 1;
        }
        else
        {
            fh.RenderWidth = fh.UpscaledWidth;
            fh.RenderHeight = fh.FrameHeight;
        }

        if (fh.AllowScreenContentTools && fh.UpscaledWidth == fh.FrameWidth)
            fh.AllowIntrabc = br.ReadBool();

        if (fh.UseSuperres)
            throw new InvalidDataException("AV1 Superres is not supported in M1 (lands in M2).");
        if (fh.AllowIntrabc)
            throw new InvalidDataException("AV1 IntraBC is not supported in M1 (lands in M2).");

        fh.DisableFrameEndUpdateCdf = seq.ReducedStillPictureHeader || fh.DisableCdfUpdate || br.ReadBool();

        ParseTileInfo(ref br, seq, fh);
        ParseQuantizationParams(ref br, seq, fh);
        ParseSegmentationParams(ref br, fh);
        ParseDeltaQAndLfParams(ref br, fh);

        // Compute CodedLossless / LosslessArray / SegQmLevel (§5.9.2)
        fh.CodedLossless = true;
        for (int seg = 0; seg < Av1Constants.MaxSegments; seg++)
        {
            int qindex = fh.GetQIndex(ignoreDeltaQ: true, seg, 0);
            bool lossless = qindex == 0 && fh.DeltaQYDc == 0 && fh.DeltaQUAc == 0 && fh.DeltaQUDc == 0 &&
                            fh.DeltaQVAc == 0 && fh.DeltaQVDc == 0;
            fh.LosslessArray[seg] = lossless;
            if (!lossless) fh.CodedLossless = false;
            if (fh.UsingQMatrix)
            {
                fh.SegQmLevel[0, seg] = lossless ? 15 : fh.QmY;
                fh.SegQmLevel[1, seg] = lossless ? 15 : fh.QmU;
                fh.SegQmLevel[2, seg] = lossless ? 15 : fh.QmV;
            }
        }
        fh.AllLossless = fh.CodedLossless && (fh.FrameWidth == fh.UpscaledWidth);

        ParseLoopFilterParams(ref br, seq, fh);
        ParseCdefParams(ref br, seq, fh);
        ParseLrParams(ref br, seq, fh);

        fh.TxMode = fh.CodedLossless
            ? Av1Constants.Only4x4
            : br.ReadBool() ? Av1Constants.TxModeSelect : Av1Constants.TxModeLargest;

        fh.ReducedTxSet = br.ReadBool();

        // Film grain params (§5.9.30)
        if (seq.FilmGrainParamsPresent && (fh.ShowFrame || fh.ShowableFrame))
        {
            fh.ApplyGrain = br.ReadBool();
            if (fh.ApplyGrain)
                throw new InvalidDataException("AV1 Film Grain synthesis is not supported in M1 (lands in M2).");
        }

        return fh;
    }

    internal int GetQIndex(bool ignoreDeltaQ, int segmentId, int currentQIndex)
    {
        if (SegmentationEnabled && FeatureEnabled[segmentId, Av1Constants.SegLvlAltQ])
        {
            int data = FeatureData[segmentId, Av1Constants.SegLvlAltQ];
            int qindex = BaseQIdx + data;
            if (!ignoreDeltaQ && DeltaQPresent)
                qindex = currentQIndex + data;
            return Av1Constants.Clip3(0, 255, qindex);
        }
        return (!ignoreDeltaQ && DeltaQPresent) ? currentQIndex : BaseQIdx;
    }

    private static int TileLog2(int blkSize, int target)
    {
        int k = 0;
        while ((blkSize << k) < target) k++;
        return k;
    }

    private static void ParseTileInfo(ref Av1BitReader br, Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        int sbCols = seq.Use128x128Superblock ? ((fh.MiCols + 31) >> 5) : ((fh.MiCols + 15) >> 4);
        int sbRows = seq.Use128x128Superblock ? ((fh.MiRows + 31) >> 5) : ((fh.MiRows + 15) >> 4);
        int sbShift = seq.Use128x128Superblock ? 5 : 4;
        int sbSize = sbShift + 2;
        int maxTileWidthSb = Av1Constants.MaxTileWidth >> sbSize;
        int maxTileAreaSb = Av1Constants.MaxTileArea >> (2 * sbSize);
        int minLog2TileCols = TileLog2(maxTileWidthSb, sbCols);
        int maxLog2TileCols = TileLog2(1, Math.Min(sbCols, Av1Constants.MaxTileCols));
        int maxLog2TileRows = TileLog2(1, Math.Min(sbRows, Av1Constants.MaxTileRows));
        int minLog2Tiles = Math.Max(minLog2TileCols, TileLog2(maxTileAreaSb, sbRows * sbCols));

        var colStarts = new List<int>(8);
        var rowStarts = new List<int>(8);

        if (br.ReadBool()) // uniform_tile_spacing_flag
        {
            fh.TileColsLog2 = minLog2TileCols;
            while (fh.TileColsLog2 < maxLog2TileCols)
            {
                if (br.ReadBool()) fh.TileColsLog2++;
                else break;
            }
            int tileWidthSb = (sbCols + (1 << fh.TileColsLog2) - 1) >> fh.TileColsLog2;
            for (int startSb = 0; startSb < sbCols; startSb += tileWidthSb)
                colStarts.Add(startSb << sbShift);
            colStarts.Add(fh.MiCols);
            fh.TileCols = colStarts.Count - 1;

            int minLog2TileRows = Math.Max(minLog2Tiles - fh.TileColsLog2, 0);
            fh.TileRowsLog2 = minLog2TileRows;
            while (fh.TileRowsLog2 < maxLog2TileRows)
            {
                if (br.ReadBool()) fh.TileRowsLog2++;
                else break;
            }
            int tileHeightSb = (sbRows + (1 << fh.TileRowsLog2) - 1) >> fh.TileRowsLog2;
            for (int startSb = 0; startSb < sbRows; startSb += tileHeightSb)
                rowStarts.Add(startSb << sbShift);
            rowStarts.Add(fh.MiRows);
            fh.TileRows = rowStarts.Count - 1;
        }
        else
        {
            int widestTileSb = 0;
            for (int startSb = 0; startSb < sbCols;)
            {
                colStarts.Add(startSb << sbShift);
                int maxWidth = Math.Min(sbCols - startSb, maxTileWidthSb);
                int sizeSb = br.ReadNs(maxWidth) + 1;
                widestTileSb = Math.Max(sizeSb, widestTileSb);
                startSb += sizeSb;
            }
            colStarts.Add(fh.MiCols);
            fh.TileCols = colStarts.Count - 1;
            fh.TileColsLog2 = TileLog2(1, fh.TileCols);

            int maxAreaSb = minLog2Tiles > 0 ? (sbRows * sbCols) >> (minLog2Tiles + 1) : sbRows * sbCols;
            int maxTileHeightSb = Math.Max(maxAreaSb / Math.Max(1, widestTileSb), 1);
            for (int startSb = 0; startSb < sbRows;)
            {
                rowStarts.Add(startSb << sbShift);
                int maxHeight = Math.Min(sbRows - startSb, maxTileHeightSb);
                int sizeSb = br.ReadNs(maxHeight) + 1;
                startSb += sizeSb;
            }
            rowStarts.Add(fh.MiRows);
            fh.TileRows = rowStarts.Count - 1;
            fh.TileRowsLog2 = TileLog2(1, fh.TileRows);
        }

        fh.MiColStarts = [.. colStarts];
        fh.MiRowStarts = [.. rowStarts];

        if (fh.TileColsLog2 > 0 || fh.TileRowsLog2 > 0)
        {
            fh.ContextUpdateTileId = (int)br.ReadBits(fh.TileRowsLog2 + fh.TileColsLog2);
            fh.TileSizeBytes = (int)br.ReadBits(2) + 1;
        }
    }

    private static int ReadDeltaQ(ref Av1BitReader br) => br.ReadBool() ? br.ReadSu(7) : 0;

    private static void ParseQuantizationParams(ref Av1BitReader br, Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        fh.BaseQIdx = (int)br.ReadBits(8);
        fh.DeltaQYDc = ReadDeltaQ(ref br);
        if (seq.NumPlanes > 1)
        {
            bool diffUv = seq.SeparateUvDeltaQ && br.ReadBool();
            fh.DeltaQUDc = ReadDeltaQ(ref br);
            fh.DeltaQUAc = ReadDeltaQ(ref br);
            if (diffUv)
            {
                fh.DeltaQVDc = ReadDeltaQ(ref br);
                fh.DeltaQVAc = ReadDeltaQ(ref br);
            }
            else
            {
                fh.DeltaQVDc = fh.DeltaQUDc;
                fh.DeltaQVAc = fh.DeltaQUAc;
            }
        }

        fh.UsingQMatrix = br.ReadBool();
        if (fh.UsingQMatrix)
        {
            fh.QmY = (int)br.ReadBits(4);
            fh.QmU = (int)br.ReadBits(4);
            fh.QmV = seq.SeparateUvDeltaQ ? (int)br.ReadBits(4) : fh.QmU;
        }
    }

    private static void ParseSegmentationParams(ref Av1BitReader br, Av1FrameHeader fh)
    {
        fh.SegmentationEnabled = br.ReadBool();
        if (fh.SegmentationEnabled)
        {
            for (int i = 0; i < Av1Constants.MaxSegments; i++)
            {
                for (int j = 0; j < Av1Constants.SegLvlMax; j++)
                {
                    bool enabled = br.ReadBool();
                    fh.FeatureEnabled[i, j] = enabled;
                    if (enabled)
                    {
                        int bits = Av1Constants.SegmentationFeatureBits[j];
                        int limit = Av1Constants.SegmentationFeatureMax[j];
                        int val = bits == 0 ? 0
                            : Av1Constants.SegmentationFeatureSigned[j]
                                ? Av1Constants.Clip3(-limit, limit, br.ReadSu(1 + bits))
                                : Av1Constants.Clip3(0, limit, (int)br.ReadBits(bits));
                        fh.FeatureData[i, j] = val;
                    }
                }
            }
        }

        fh.SegIdPreSkip = false;
        fh.LastActiveSegId = 0;
        for (int i = 0; i < Av1Constants.MaxSegments; i++)
        {
            for (int j = 0; j < Av1Constants.SegLvlMax; j++)
            {
                if (fh.FeatureEnabled[i, j])
                {
                    fh.LastActiveSegId = i;
                    if (j >= Av1Constants.SegLvlRefFrame)
                        fh.SegIdPreSkip = true;
                }
            }
        }
    }

    private static void ParseDeltaQAndLfParams(ref Av1BitReader br, Av1FrameHeader fh)
    {
        if (fh.BaseQIdx > 0)
            fh.DeltaQPresent = br.ReadBool();
        if (fh.DeltaQPresent)
        {
            fh.DeltaQRes = (int)br.ReadBits(2);
            if (!fh.AllowIntrabc)
                fh.DeltaLfPresent = br.ReadBool();
            if (fh.DeltaLfPresent)
            {
                fh.DeltaLfRes = (int)br.ReadBits(2);
                fh.DeltaLfMulti = br.ReadBool();
            }
        }
    }

    private static void ParseLoopFilterParams(ref Av1BitReader br, Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        if (fh.CodedLossless || fh.AllowIntrabc)
            return;

        fh.LoopFilterLevel[0] = (int)br.ReadBits(6);
        fh.LoopFilterLevel[1] = (int)br.ReadBits(6);
        if (seq.NumPlanes > 1 && (fh.LoopFilterLevel[0] != 0 || fh.LoopFilterLevel[1] != 0))
        {
            fh.LoopFilterLevel[2] = (int)br.ReadBits(6);
            fh.LoopFilterLevel[3] = (int)br.ReadBits(6);
        }
        fh.LoopFilterSharpness = (int)br.ReadBits(3);
        fh.LoopFilterDeltaEnabled = br.ReadBool();
        if (fh.LoopFilterDeltaEnabled && br.ReadBool()) // loop_filter_delta_update
        {
            for (int i = 0; i < 8; i++)
            {
                if (br.ReadBool())
                    fh.LoopFilterRefDeltas[i] = br.ReadSu(7);
            }
            for (int i = 0; i < 2; i++)
            {
                if (br.ReadBool())
                    fh.LoopFilterModeDeltas[i] = br.ReadSu(7);
            }
        }
    }

    private static void ParseCdefParams(ref Av1BitReader br, Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        if (fh.CodedLossless || fh.AllowIntrabc || !seq.EnableCdef)
            return;

        fh.CdefDamping = (int)br.ReadBits(2) + 3;
        fh.CdefBits = (int)br.ReadBits(2);
        int count = 1 << fh.CdefBits;
        for (int i = 0; i < count; i++)
        {
            fh.CdefYPriStrength[i] = (int)br.ReadBits(4);
            int ySec = (int)br.ReadBits(2);
            fh.CdefYSecStrength[i] = ySec == 3 ? 4 : ySec;
            if (seq.NumPlanes > 1)
            {
                fh.CdefUvPriStrength[i] = (int)br.ReadBits(4);
                int uvSec = (int)br.ReadBits(2);
                fh.CdefUvSecStrength[i] = uvSec == 3 ? 4 : uvSec;
            }
        }
    }

    private static readonly int[] RemapLrType =
    [
        Av1Constants.RestoreNone,
        Av1Constants.RestoreSwitchable,
        Av1Constants.RestoreWiener,
        Av1Constants.RestoreSgrproj,
    ];

    private static void ParseLrParams(ref Av1BitReader br, Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        if (fh.AllLossless || fh.AllowIntrabc || !seq.EnableRestoration)
            return;

        for (int i = 0; i < seq.NumPlanes; i++)
        {
            int lrType = (int)br.ReadBits(2);
            fh.FrameRestorationType[i] = RemapLrType[lrType];
            if (fh.FrameRestorationType[i] != Av1Constants.RestoreNone)
            {
                fh.UsesLr = true;
                if (i > 0) fh.UsesChromaLr = true;
            }
        }

        if (fh.UsesLr)
        {
            int lrUnitShift;
            if (seq.Use128x128Superblock)
            {
                lrUnitShift = br.ReadBit() + 1;
            }
            else
            {
                lrUnitShift = br.ReadBit();
                if (lrUnitShift != 0)
                    lrUnitShift += br.ReadBit();
            }

            fh.LoopRestorationSize[0] = 256 >> (2 - lrUnitShift);
            int lrUvShift = (seq.SubsamplingX != 0 && seq.SubsamplingY != 0 && fh.UsesChromaLr) ? br.ReadBit() : 0;
            fh.LoopRestorationSize[1] = fh.LoopRestorationSize[0] >> lrUvShift;
            fh.LoopRestorationSize[2] = fh.LoopRestorationSize[0] >> lrUvShift;
        }
    }
}
