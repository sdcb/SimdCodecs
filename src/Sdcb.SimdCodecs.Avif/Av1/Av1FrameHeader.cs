namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Parsed AV1 uncompressed frame header (§5.9) for all frame types (KEY, INTER, INTRA_ONLY, SWITCH).
/// </summary>
internal sealed class Av1FrameHeader
{
    internal bool ShowExistingFrame { get; private set; }
    internal int FrameToShowMapIdx { get; private set; }
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
    internal int CurrentFrameId { get; private set; }
    internal int OrderHint { get; private set; }
    internal int PrimaryRefFrame { get; private set; } = Av1Constants.PrimaryRefNone;
    internal int RefreshFrameFlags { get; private set; }

    // Reference frames & Inter flags (§5.9.2)
    internal int[] RefFrameIdx { get; } = new int[Av1Constants.RefsPerFrame];
    internal int[] OrderHints { get; } = new int[Av1Constants.TotalRefsPerFrame];
    internal bool[] RefFrameSignBias { get; } = new bool[Av1Constants.TotalRefsPerFrame];
    internal bool AllowHighPrecisionMv { get; private set; }
    internal int InterpolationFilter { get; private set; }
    internal bool IsMotionModeSwitchable { get; private set; }
    internal bool UseRefFrameMvs { get; private set; }
    internal bool ReferenceSelect { get; private set; }
    internal bool SkipModePresent { get; private set; }
    internal int[] SkipModeFrame { get; } = new int[2];
    internal bool AllowWarpedMotion { get; private set; }

    // Global motion (§5.9.24)
    internal int[] GmType { get; } = new int[Av1Constants.TotalRefsPerFrame];
    internal int[,] GmParams { get; } = new int[Av1Constants.TotalRefsPerFrame, 6];
    internal int[,] PrevGmParams { get; } = new int[Av1Constants.TotalRefsPerFrame, 6];

    // Frame-start CDFs (either default or loaded from primary_ref_frame with counts reset)
    internal Av1CdfState InitialCdfs { get; } = new();

    // Previous segment IDs (loaded from primary_ref_frame for inter_segment_id)
    internal byte[] PrevSegmentIds { get; private set; } = [];

    // Motion field MVs (§7.9): [ref (0..7)][(y8 * w8 + x8) * 2 + comp]
    internal short[][]? MotionFieldMvs { get; private set; }
    internal int MotionFieldW8 { get; private set; }
    internal int MotionFieldH8 { get; private set; }

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
    internal bool SegmentationUpdateMap { get; private set; }
    internal bool SegmentationTemporalUpdate { get; private set; }
    internal bool SegmentationUpdateData { get; private set; }
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
    internal Av1FilmGrainParams? FilmGrain { get; private set; }

    internal static Av1FrameHeader Parse(
        ref Av1BitReader br,
        Av1SequenceHeader seq,
        Av1DecoderSession session,
        int temporalId = 0,
        int spatialId = 0)
    {
        var fh = new Av1FrameHeader();
        int idLen = seq.FrameIdNumbersPresent
            ? seq.AdditionalFrameIdLengthMinus1 + seq.DeltaFrameIdLengthMinus2 + 3
            : 0;
        const int allFrames = (1 << Av1Constants.NumRefFrames) - 1;

        if (seq.ReducedStillPictureHeader)
        {
            fh.ShowExistingFrame = false;
            fh.FrameType = Av1Constants.KeyFrame;
            fh.FrameIsIntra = true;
            fh.ShowFrame = true;
            fh.ShowableFrame = false;
            fh.ErrorResilientMode = true;
        }
        else
        {
            fh.ShowExistingFrame = br.ReadBool();
            if (fh.ShowExistingFrame)
            {
                fh.FrameToShowMapIdx = (int)br.ReadBits(3);
                if (seq.DecoderModelInfoPresent && !seq.EqualPictureInterval)
                    _ = br.ReadBits(seq.FramePresentationTimeLengthMinus1 + 1);
                fh.RefreshFrameFlags = 0;
                if (seq.FrameIdNumbersPresent)
                    _ = br.ReadBits(idLen); // display_frame_id
                var refSlot = session.RefFrames[fh.FrameToShowMapIdx];
                if (!refSlot.Valid)
                    throw new InvalidDataException($"AV1 show_existing_frame references invalid slot {fh.FrameToShowMapIdx}.");
                fh.FrameType = refSlot.FrameType;
                if (fh.FrameType == Av1Constants.KeyFrame)
                    fh.RefreshFrameFlags = allFrames;
                fh.ShowFrame = true;
                return fh;
            }

            fh.FrameType = (int)br.ReadBits(2);
            fh.FrameIsIntra = fh.FrameType is Av1Constants.KeyFrame or Av1Constants.IntraOnlyFrame;
            fh.ShowFrame = br.ReadBool();
            if (fh.ShowFrame && seq.DecoderModelInfoPresent && !seq.EqualPictureInterval)
                _ = br.ReadBits(seq.FramePresentationTimeLengthMinus1 + 1);

            fh.ShowableFrame = fh.ShowFrame ? fh.FrameType != Av1Constants.KeyFrame : br.ReadBool();
            fh.ErrorResilientMode = fh.FrameType == Av1Constants.SwitchFrame
                || (fh.FrameType == Av1Constants.KeyFrame && fh.ShowFrame)
                || br.ReadBool();
        }

        if (fh.FrameType == Av1Constants.KeyFrame && fh.ShowFrame)
        {
            for (int i = 0; i < Av1Constants.NumRefFrames; i++)
            {
                session.RefFrames[i].Valid = false;
                session.RefFrames[i].OrderHint = 0;
            }
            for (int i = 0; i < Av1Constants.RefsPerFrame; i++)
                fh.OrderHints[Av1Constants.LastFrame + i] = 0;
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
        {
            fh.CurrentFrameId = (int)br.ReadBits(idLen);
            session.CurrentFrameId = fh.CurrentFrameId;
            MarkRefFrames(seq, session, fh.CurrentFrameId, idLen);
        }

        bool frameSizeOverride = fh.FrameType == Av1Constants.SwitchFrame
            || (!seq.ReducedStillPictureHeader && br.ReadBool());

        if (seq.OrderHintBits > 0)
            fh.OrderHint = (int)br.ReadBits(seq.OrderHintBits);

        fh.PrimaryRefFrame = (fh.FrameIsIntra || fh.ErrorResilientMode)
            ? Av1Constants.PrimaryRefNone
            : (int)br.ReadBits(3);

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

        fh.RefreshFrameFlags = (fh.FrameType == Av1Constants.SwitchFrame || (fh.FrameType == Av1Constants.KeyFrame && fh.ShowFrame))
            ? allFrames
            : (int)br.ReadBits(8);

        if (!fh.FrameIsIntra || fh.RefreshFrameFlags != allFrames)
        {
            if (fh.ErrorResilientMode && seq.EnableOrderHint)
            {
                for (int i = 0; i < Av1Constants.NumRefFrames; i++)
                {
                    int refOh = (int)br.ReadBits(seq.OrderHintBits);
                    if (refOh != session.RefFrames[i].OrderHint)
                        session.RefFrames[i].Valid = false;
                }
            }
        }

        if (fh.FrameIsIntra)
        {
            ParseFrameSize(ref br, seq, fh, frameSizeOverride);
            ParseRenderSize(ref br, fh);
            if (fh.AllowScreenContentTools && fh.UpscaledWidth == fh.FrameWidth)
                fh.AllowIntrabc = br.ReadBool();
        }
        else
        {
            bool shortSig = seq.EnableOrderHint && br.ReadBool();
            if (shortSig)
            {
                int lastIdx = (int)br.ReadBits(3);
                int goldIdx = (int)br.ReadBits(3);
                SetFrameRefs(seq, session, fh, lastIdx, goldIdx);
            }
            for (int i = 0; i < Av1Constants.RefsPerFrame; i++)
            {
                if (!shortSig)
                    fh.RefFrameIdx[i] = (int)br.ReadBits(3);
                if (seq.FrameIdNumbersPresent)
                    _ = br.ReadBits(seq.DeltaFrameIdLengthMinus2 + 2);
            }

            if (frameSizeOverride && !fh.ErrorResilientMode)
                ParseFrameSizeWithRefs(ref br, seq, session, fh);
            else
            {
                ParseFrameSize(ref br, seq, fh, frameSizeOverride);
                ParseRenderSize(ref br, fh);
            }

            fh.AllowHighPrecisionMv = !fh.ForceIntegerMv && br.ReadBool();
            fh.InterpolationFilter = br.ReadBool() ? Av1Constants.Switchable : (int)br.ReadBits(2);
            fh.IsMotionModeSwitchable = br.ReadBool();
            fh.UseRefFrameMvs = !fh.ErrorResilientMode && seq.EnableRefFrameMvs && br.ReadBool();

            for (int i = 0; i < Av1Constants.RefsPerFrame; i++)
            {
                int refFrame = Av1Constants.LastFrame + i;
                int hint = session.RefFrames[fh.RefFrameIdx[i]].OrderHint;
                fh.OrderHints[refFrame] = hint;
                fh.RefFrameSignBias[refFrame] = seq.EnableOrderHint && seq.GetRelativeDist(hint, fh.OrderHint) > 0;
            }
        }

        fh.DisableFrameEndUpdateCdf = seq.ReducedStillPictureHeader || fh.DisableCdfUpdate || br.ReadBool();

        if (fh.PrimaryRefFrame == Av1Constants.PrimaryRefNone)
        {
            SetupPastIndependence(fh);
        }
        else
        {
            int prevFrameIdx = fh.RefFrameIdx[fh.PrimaryRefFrame];
            var prev = session.RefFrames[prevFrameIdx];
            if (!prev.Valid || prev.SavedCdfs is null)
                throw new InvalidDataException($"AV1 primary_ref_frame references invalid slot {prevFrameIdx}.");
            fh.InitialCdfs.CopyFrom(prev.SavedCdfs, resetCounts: true);
            LoadPrevious(fh, prev);
        }

        if (fh.UseRefFrameMvs)
            ComputeMotionFieldEstimation(seq, session, fh);

        ParseTileInfo(ref br, seq, fh);
        ParseQuantizationParams(ref br, seq, fh);
        ParseSegmentationParams(ref br, fh);
        ParseDeltaQAndLfParams(ref br, fh);

        if (fh.PrimaryRefFrame == Av1Constants.PrimaryRefNone)
        {
            fh.InitialCdfs.InitDefault(fh.BaseQIdx);
            fh.PrevSegmentIds = new byte[fh.MiRows * fh.MiCols];
        }
        else
        {
            int prevFrameIdx = fh.RefFrameIdx[fh.PrimaryRefFrame];
            var prev = session.RefFrames[prevFrameIdx];
            fh.PrevSegmentIds = new byte[fh.MiRows * fh.MiCols];
            if (fh.SegmentationEnabled && prev.MiCols == fh.MiCols && prev.MiRows == fh.MiRows
                && prev.SavedSegmentIds.Length == fh.PrevSegmentIds.Length)
            {
                prev.SavedSegmentIds.CopyTo(fh.PrevSegmentIds, 0);
            }
        }

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

        fh.ReferenceSelect = !fh.FrameIsIntra && br.ReadBool();
        ParseSkipModeParams(ref br, seq, session, fh);
        fh.AllowWarpedMotion = !fh.FrameIsIntra && !fh.ErrorResilientMode && seq.EnableWarpedMotion && br.ReadBool();
        fh.ReducedTxSet = br.ReadBool();
        ParseGlobalMotionParams(ref br, fh);

        // Film grain params (§5.9.30)
        if (seq.FilmGrainParamsPresent && (fh.ShowFrame || fh.ShowableFrame))
            ParseFilmGrainParams(ref br, seq, session, fh);

        return fh;
    }

    private static void MarkRefFrames(Av1SequenceHeader seq, Av1DecoderSession session, int currentFrameId, int idLen)
    {
        int diffLen = seq.DeltaFrameIdLengthMinus2 + 2;
        for (int i = 0; i < Av1Constants.NumRefFrames; i++)
        {
            int refId = session.RefFrames[i].FrameId;
            if (currentFrameId > (1 << diffLen))
            {
                if (refId > currentFrameId || refId < currentFrameId - (1 << diffLen))
                    session.RefFrames[i].Valid = false;
            }
            else
            {
                if (refId > currentFrameId && refId < (1 << idLen) + currentFrameId - (1 << diffLen))
                    session.RefFrames[i].Valid = false;
            }
        }
    }

    private static void ParseFrameSize(ref Av1BitReader br, Av1SequenceHeader seq, Av1FrameHeader fh, bool frameSizeOverride)
    {
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
        ParseSuperresAndComputeSize(ref br, seq, fh);
    }

    private static void ParseFrameSizeWithRefs(ref Av1BitReader br, Av1SequenceHeader seq, Av1DecoderSession session, Av1FrameHeader fh)
    {
        bool foundRef = false;
        for (int i = 0; i < Av1Constants.RefsPerFrame; i++)
        {
            if (br.ReadBool())
            {
                var r = session.RefFrames[fh.RefFrameIdx[i]];
                if (!r.Valid)
                    throw new InvalidDataException("AV1 frame_size_with_refs references an invalid reference slot.");
                fh.UpscaledWidth = r.UpscaledWidth;
                fh.FrameWidth = r.UpscaledWidth;
                fh.FrameHeight = r.FrameHeight;
                fh.RenderWidth = r.RenderWidth;
                fh.RenderHeight = r.RenderHeight;
                foundRef = true;
                break;
            }
        }

        if (!foundRef)
        {
            ParseFrameSize(ref br, seq, fh, frameSizeOverride: true);
            ParseRenderSize(ref br, fh);
        }
        else
        {
            ParseSuperresAndComputeSize(ref br, seq, fh);
        }
    }

    private static void ParseSuperresAndComputeSize(ref Av1BitReader br, Av1SequenceHeader seq, Av1FrameHeader fh)
    {
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
        if (fh.UpscaledWidth <= 0 || fh.FrameHeight <= 0
            || fh.UpscaledWidth > AvifContainer.MaxDimension || fh.FrameHeight > AvifContainer.MaxDimension
            || (long)fh.UpscaledWidth * fh.FrameHeight > AvifContainer.MaxPixels)
        {
            throw new InvalidDataException($"AV1 frame dimensions {fh.UpscaledWidth}x{fh.FrameHeight} exceed decoder limits.");
        }
        fh.MiCols = 2 * ((fh.FrameWidth + 7) >> 3);
        fh.MiRows = 2 * ((fh.FrameHeight + 7) >> 3);
    }

    private static void ParseRenderSize(ref Av1BitReader br, Av1FrameHeader fh)
    {
        if (br.ReadBool())
        {
            fh.RenderWidth = (int)br.ReadBits(16) + 1;
            fh.RenderHeight = (int)br.ReadBits(16) + 1;
        }
        else
        {
            fh.RenderWidth = fh.UpscaledWidth;
            fh.RenderHeight = fh.FrameHeight;
        }
    }

    // §7.8 Set frame refs process
    private static void SetFrameRefs(Av1SequenceHeader seq, Av1DecoderSession session, Av1FrameHeader fh, int lastIdx, int goldIdx)
    {
        Array.Fill(fh.RefFrameIdx, -1);
        fh.RefFrameIdx[Av1Constants.LastFrame - Av1Constants.LastFrame] = lastIdx;
        fh.RefFrameIdx[Av1Constants.GoldenFrame - Av1Constants.LastFrame] = goldIdx;

        Span<bool> usedFrame = stackalloc bool[Av1Constants.NumRefFrames];
        usedFrame[lastIdx] = true;
        usedFrame[goldIdx] = true;

        int curFrameHint = 1 << (seq.OrderHintBits - 1);
        Span<int> shifted = stackalloc int[Av1Constants.NumRefFrames];
        for (int i = 0; i < Av1Constants.NumRefFrames; i++)
            shifted[i] = curFrameHint + seq.GetRelativeDist(session.RefFrames[i].OrderHint, fh.OrderHint);

        // ALTREF_FRAME = latest backward
        int r = FindLatestBackward(shifted, usedFrame, curFrameHint);
        if (r >= 0) { fh.RefFrameIdx[Av1Constants.AltRefFrame - Av1Constants.LastFrame] = r; usedFrame[r] = true; }

        // BWDREF_FRAME = earliest backward
        r = FindEarliestBackward(shifted, usedFrame, curFrameHint);
        if (r >= 0) { fh.RefFrameIdx[Av1Constants.BwdRefFrame - Av1Constants.LastFrame] = r; usedFrame[r] = true; }

        // ALTREF2_FRAME = next earliest backward
        r = FindEarliestBackward(shifted, usedFrame, curFrameHint);
        if (r >= 0) { fh.RefFrameIdx[Av1Constants.AltRef2Frame - Av1Constants.LastFrame] = r; usedFrame[r] = true; }

        ReadOnlySpan<int> refList =
        [
            Av1Constants.Last2Frame, Av1Constants.Last3Frame,
            Av1Constants.BwdRefFrame, Av1Constants.AltRef2Frame, Av1Constants.AltRefFrame,
        ];
        foreach (int rf in refList)
        {
            if (fh.RefFrameIdx[rf - Av1Constants.LastFrame] < 0)
            {
                r = FindLatestForward(shifted, usedFrame, curFrameHint);
                if (r >= 0) { fh.RefFrameIdx[rf - Av1Constants.LastFrame] = r; usedFrame[r] = true; }
            }
        }

        int earliest = -1, earliestHint = 0;
        for (int i = 0; i < Av1Constants.NumRefFrames; i++)
        {
            if (earliest < 0 || shifted[i] < earliestHint) { earliest = i; earliestHint = shifted[i]; }
        }
        for (int i = 0; i < Av1Constants.RefsPerFrame; i++)
        {
            if (fh.RefFrameIdx[i] < 0) fh.RefFrameIdx[i] = earliest;
        }
    }

    private static int FindLatestBackward(ReadOnlySpan<int> shifted, ReadOnlySpan<bool> used, int cur)
    {
        int r = -1, latest = 0;
        for (int i = 0; i < Av1Constants.NumRefFrames; i++)
        {
            if (!used[i] && shifted[i] >= cur && (r < 0 || shifted[i] >= latest)) { r = i; latest = shifted[i]; }
        }
        return r;
    }

    private static int FindEarliestBackward(ReadOnlySpan<int> shifted, ReadOnlySpan<bool> used, int cur)
    {
        int r = -1, earliest = 0;
        for (int i = 0; i < Av1Constants.NumRefFrames; i++)
        {
            if (!used[i] && shifted[i] >= cur && (r < 0 || shifted[i] < earliest)) { r = i; earliest = shifted[i]; }
        }
        return r;
    }

    private static int FindLatestForward(ReadOnlySpan<int> shifted, ReadOnlySpan<bool> used, int cur)
    {
        int r = -1, latest = 0;
        for (int i = 0; i < Av1Constants.NumRefFrames; i++)
        {
            if (!used[i] && shifted[i] < cur && (r < 0 || shifted[i] >= latest)) { r = i; latest = shifted[i]; }
        }
        return r;
    }

    private static void SetupPastIndependence(Av1FrameHeader fh)
    {
        for (int r = Av1Constants.LastFrame; r <= Av1Constants.AltRefFrame; r++)
        {
            for (int j = 0; j < 6; j++)
                fh.PrevGmParams[r, j] = (j % 3 == 2) ? (1 << Av1Constants.WarpedModelPrecBits) : 0;
        }
        fh.LoopFilterRefDeltas[Av1Constants.IntraFrame] = 1;
        fh.LoopFilterRefDeltas[Av1Constants.LastFrame] = 0;
        fh.LoopFilterRefDeltas[Av1Constants.Last2Frame] = 0;
        fh.LoopFilterRefDeltas[Av1Constants.Last3Frame] = 0;
        fh.LoopFilterRefDeltas[Av1Constants.BwdRefFrame] = 0;
        fh.LoopFilterRefDeltas[Av1Constants.GoldenFrame] = -1;
        fh.LoopFilterRefDeltas[Av1Constants.AltRef2Frame] = -1;
        fh.LoopFilterRefDeltas[Av1Constants.AltRefFrame] = -1;
        fh.LoopFilterModeDeltas[0] = 0;
        fh.LoopFilterModeDeltas[1] = 0;
    }

    private static void LoadPrevious(Av1FrameHeader fh, Av1RefFrame prev)
    {
        Array.Copy(prev.SavedGmParams, fh.PrevGmParams, prev.SavedGmParams.Length);
        Array.Copy(prev.SavedLoopFilterRefDeltas, fh.LoopFilterRefDeltas, Av1Constants.TotalRefsPerFrame);
        Array.Copy(prev.SavedLoopFilterModeDeltas, fh.LoopFilterModeDeltas, 2);
        Array.Copy(prev.SavedFeatureEnabled, fh.FeatureEnabled, prev.SavedFeatureEnabled.Length);
        Array.Copy(prev.SavedFeatureData, fh.FeatureData, prev.SavedFeatureData.Length);
    }

    // §7.9 Motion field estimation process
    private static void ComputeMotionFieldEstimation(Av1SequenceHeader seq, Av1DecoderSession session, Av1FrameHeader fh)
    {
        int w8 = fh.MiCols >> 1;
        int h8 = fh.MiRows >> 1;
        fh.MotionFieldW8 = w8;
        fh.MotionFieldH8 = h8;
        var mfmv = new short[Av1Constants.TotalRefsPerFrame][];
        for (int r = Av1Constants.LastFrame; r <= Av1Constants.AltRefFrame; r++)
        {
            mfmv[r] = new short[w8 * h8 * 2];
            Array.Fill(mfmv[r], short.MinValue);
        }
        fh.MotionFieldMvs = mfmv;

        int lastIdx = fh.RefFrameIdx[0];
        int curGoldOh = fh.OrderHints[Av1Constants.GoldenFrame];
        int lastAltOh = session.RefFrames[lastIdx].SavedOrderHints[Av1Constants.AltRefFrame];
        if (lastAltOh != curGoldOh)
            ProjectMotionField(seq, session, fh, Av1Constants.LastFrame, -1);

        int refStamp = Av1Constants.MfmvStackSize - 2;
        if (seq.GetRelativeDist(fh.OrderHints[Av1Constants.BwdRefFrame], fh.OrderHint) > 0)
        {
            if (ProjectMotionField(seq, session, fh, Av1Constants.BwdRefFrame, 1))
                refStamp--;
        }
        if (seq.GetRelativeDist(fh.OrderHints[Av1Constants.AltRef2Frame], fh.OrderHint) > 0)
        {
            if (ProjectMotionField(seq, session, fh, Av1Constants.AltRef2Frame, 1))
                refStamp--;
        }
        if (seq.GetRelativeDist(fh.OrderHints[Av1Constants.AltRefFrame], fh.OrderHint) > 0 && refStamp >= 0)
        {
            if (ProjectMotionField(seq, session, fh, Av1Constants.AltRefFrame, 1))
                refStamp--;
        }
        if (refStamp >= 0)
            ProjectMotionField(seq, session, fh, Av1Constants.Last2Frame, -1);
    }

    private static bool ProjectMotionField(Av1SequenceHeader seq, Av1DecoderSession session, Av1FrameHeader fh, int src, int dstSign)
    {
        int srcIdx = fh.RefFrameIdx[src - Av1Constants.LastFrame];
        var srcSlot = session.RefFrames[srcIdx];
        if (!srcSlot.Valid || srcSlot.MiRows != fh.MiRows || srcSlot.MiCols != fh.MiCols
            || srcSlot.SavedRefFrames.Length < fh.MiRows * fh.MiCols
            || srcSlot.FrameType is Av1Constants.IntraOnlyFrame or Av1Constants.KeyFrame)
        {
            return false;
        }

        int w8 = fh.MotionFieldW8;
        int h8 = fh.MotionFieldH8;
        int miCols = fh.MiCols;
        var mfmv = fh.MotionFieldMvs!;

        for (int y8 = 0; y8 < h8; y8++)
        {
            for (int x8 = 0; x8 < w8; x8++)
            {
                int row = 2 * y8 + 1;
                int col = 2 * x8 + 1;
                int cell = row * miCols + col;
                int srcRef = srcSlot.SavedRefFrames[cell];
                if (srcRef <= Av1Constants.IntraFrame) continue;

                int refToCur = seq.GetRelativeDist(fh.OrderHints[src], fh.OrderHint);
                int refOffset = seq.GetRelativeDist(fh.OrderHints[src], srcSlot.SavedOrderHints[srcRef]);
                if (Math.Abs(refToCur) > Av1Constants.MaxFrameDistance
                    || Math.Abs(refOffset) > Av1Constants.MaxFrameDistance
                    || refOffset <= 0)
                {
                    continue;
                }

                int mvY = srcSlot.SavedMvs[cell * 2];
                int mvX = srcSlot.SavedMvs[cell * 2 + 1];
                GetMvProjection(mvY, mvX, refToCur * dstSign, refOffset, out int projY, out int projX);

                if (GetBlockPosition(x8, y8, dstSign, projY, projX, w8, h8, out int posX8, out int posY8))
                {
                    int posIdx = (posY8 * w8 + posX8) * 2;
                    for (int dst = Av1Constants.LastFrame; dst <= Av1Constants.AltRefFrame; dst++)
                    {
                        int refToDst = seq.GetRelativeDist(fh.OrderHint, fh.OrderHints[dst]);
                        GetMvProjection(mvY, mvX, refToDst, refOffset, out int dY, out int dX);
                        mfmv[dst][posIdx] = (short)dY;
                        mfmv[dst][posIdx + 1] = (short)dX;
                    }
                }
            }
        }
        return true;
    }

    private static void GetMvProjection(int mvY, int mvX, int num, int den, out int projY, out int projX)
    {
        int cDen = Math.Min(Av1Constants.MaxFrameDistance, den);
        int cNum = Av1Constants.Clip3(-Av1Constants.MaxFrameDistance, Av1Constants.MaxFrameDistance, num);
        int mult = Av1Constants.DivMult[cDen];
        projY = Av1Constants.Clip3(-(1 << 14) + 1, (1 << 14) - 1, Av1Constants.Round2Signed(mvY * cNum * mult, 14));
        projX = Av1Constants.Clip3(-(1 << 14) + 1, (1 << 14) - 1, Av1Constants.Round2Signed(mvX * cNum * mult, 14));
    }

    private static bool GetBlockPosition(int x8, int y8, int dstSign, int projY, int projX, int w8, int h8, out int posX8, out int posY8)
    {
        bool valid = true;
        posY8 = ProjectCoord(y8, projY, dstSign, h8, Av1Constants.MaxOffsetHeight, ref valid);
        posX8 = ProjectCoord(x8, projX, dstSign, w8, Av1Constants.MaxOffsetWidth, ref valid);
        return valid;
    }

    private static int ProjectCoord(int v8, int delta, int dstSign, int max8, int maxOff8, ref bool valid)
    {
        int base8 = (v8 >> 3) << 3;
        int offset8 = delta >= 0 ? (delta >> 6) : -((-delta) >> 6);
        v8 += dstSign * offset8;
        if (v8 < 0 || v8 >= max8 || v8 < base8 - maxOff8 || v8 >= base8 + 8 + maxOff8)
            valid = false;
        return v8;
    }

    // §5.9.22 Skip mode params
    private static void ParseSkipModeParams(ref Av1BitReader br, Av1SequenceHeader seq, Av1DecoderSession session, Av1FrameHeader fh)
    {
        bool allowed = false;
        if (!fh.FrameIsIntra && fh.ReferenceSelect && seq.EnableOrderHint)
        {
            int fwdIdx = -1, fwdHint = 0;
            int bwdIdx = -1, bwdHint = 0;
            for (int i = 0; i < Av1Constants.RefsPerFrame; i++)
            {
                int refHint = session.RefFrames[fh.RefFrameIdx[i]].OrderHint;
                int d = seq.GetRelativeDist(refHint, fh.OrderHint);
                if (d < 0)
                {
                    if (fwdIdx < 0 || seq.GetRelativeDist(refHint, fwdHint) > 0) { fwdIdx = i; fwdHint = refHint; }
                }
                else if (d > 0)
                {
                    if (bwdIdx < 0 || seq.GetRelativeDist(refHint, bwdHint) < 0) { bwdIdx = i; bwdHint = refHint; }
                }
            }
            if (fwdIdx >= 0 && bwdIdx >= 0)
            {
                allowed = true;
                fh.SkipModeFrame[0] = Av1Constants.LastFrame + Math.Min(fwdIdx, bwdIdx);
                fh.SkipModeFrame[1] = Av1Constants.LastFrame + Math.Max(fwdIdx, bwdIdx);
            }
            else if (fwdIdx >= 0)
            {
                int secIdx = -1, secHint = 0;
                for (int i = 0; i < Av1Constants.RefsPerFrame; i++)
                {
                    int refHint = session.RefFrames[fh.RefFrameIdx[i]].OrderHint;
                    if (seq.GetRelativeDist(refHint, fwdHint) < 0)
                    {
                        if (secIdx < 0 || seq.GetRelativeDist(refHint, secHint) > 0) { secIdx = i; secHint = refHint; }
                    }
                }
                if (secIdx >= 0)
                {
                    allowed = true;
                    fh.SkipModeFrame[0] = Av1Constants.LastFrame + Math.Min(fwdIdx, secIdx);
                    fh.SkipModeFrame[1] = Av1Constants.LastFrame + Math.Max(fwdIdx, secIdx);
                }
            }
        }
        fh.SkipModePresent = allowed && br.ReadBool();
    }

    // §5.9.24–§5.9.29 Global motion params
    private static void ParseGlobalMotionParams(ref Av1BitReader br, Av1FrameHeader fh)
    {
        for (int r = Av1Constants.LastFrame; r <= Av1Constants.AltRefFrame; r++)
        {
            fh.GmType[r] = Av1Constants.GmIdentity;
            for (int i = 0; i < 6; i++)
                fh.GmParams[r, i] = (i % 3 == 2) ? (1 << Av1Constants.WarpedModelPrecBits) : 0;
        }
        if (fh.FrameIsIntra) return;

        for (int r = Av1Constants.LastFrame; r <= Av1Constants.AltRefFrame; r++)
        {
            int type;
            if (br.ReadBool()) // is_global
            {
                if (br.ReadBool()) // is_rot_zoom
                    type = Av1Constants.GmRotZoom;
                else
                    type = br.ReadBool() ? Av1Constants.GmTranslation : Av1Constants.GmAffine;
            }
            else
            {
                type = Av1Constants.GmIdentity;
            }
            fh.GmType[r] = type;
            if (type >= Av1Constants.GmRotZoom)
            {
                ReadGlobalParam(ref br, fh, type, r, 2);
                ReadGlobalParam(ref br, fh, type, r, 3);
                if (type == Av1Constants.GmAffine)
                {
                    ReadGlobalParam(ref br, fh, type, r, 4);
                    ReadGlobalParam(ref br, fh, type, r, 5);
                }
                else
                {
                    fh.GmParams[r, 4] = -fh.GmParams[r, 3];
                    fh.GmParams[r, 5] = fh.GmParams[r, 2];
                }
            }
            if (type >= Av1Constants.GmTranslation)
            {
                ReadGlobalParam(ref br, fh, type, r, 0);
                ReadGlobalParam(ref br, fh, type, r, 1);
            }
        }
    }

    private static void ReadGlobalParam(ref Av1BitReader br, Av1FrameHeader fh, int type, int r, int idx)
    {
        int absBits = Av1Constants.GmAbsAlphaBits;
        int precBits = Av1Constants.GmAlphaPrecBits;
        if (idx < 2)
        {
            if (type == Av1Constants.GmTranslation)
            {
                absBits = Av1Constants.GmAbsTransOnlyBits - (fh.AllowHighPrecisionMv ? 0 : 1);
                precBits = Av1Constants.GmTransOnlyPrecBits - (fh.AllowHighPrecisionMv ? 0 : 1);
            }
            else
            {
                absBits = Av1Constants.GmAbsTransBits;
                precBits = Av1Constants.GmTransPrecBits;
            }
        }
        int precDiff = Av1Constants.WarpedModelPrecBits - precBits;
        int round = (idx % 3 == 2) ? (1 << Av1Constants.WarpedModelPrecBits) : 0;
        int sub = (idx % 3 == 2) ? (1 << precBits) : 0;
        int mx = 1 << absBits;
        int prev = (fh.PrevGmParams[r, idx] >> precDiff) - sub;
        fh.GmParams[r, idx] = (DecodeSignedSubexpWithRef(ref br, -mx, mx + 1, prev) << precDiff) + round;
    }

    private static int DecodeSignedSubexpWithRef(ref Av1BitReader br, int low, int high, int r)
        => DecodeUnsignedSubexpWithRef(ref br, high - low, r - low) + low;

    private static int DecodeUnsignedSubexpWithRef(ref Av1BitReader br, int mx, int r)
    {
        int v = DecodeSubexp(ref br, mx);
        return (r << 1) <= mx
            ? InverseRecenter(r, v)
            : mx - 1 - InverseRecenter(mx - 1 - r, v);
    }

    private static int DecodeSubexp(ref Av1BitReader br, int numSyms)
    {
        int i = 0, mk = 0;
        const int k = 3;
        while (true)
        {
            int b2 = i != 0 ? k + i - 1 : k;
            int a = 1 << b2;
            if (numSyms <= mk + 3 * a)
                return br.ReadNs(numSyms - mk) + mk;
            if (br.ReadBool())
            {
                i++;
                mk += a;
            }
            else
            {
                return (int)br.ReadBits(b2) + mk;
            }
        }
    }

    private static int InverseRecenter(int r, int v)
    {
        if (v > 2 * r) return v;
        return (v & 1) != 0 ? r - ((v + 1) >> 1) : r + (v >> 1);
    }

    private static void ParseFilmGrainParams(ref Av1BitReader br, Av1SequenceHeader seq, Av1DecoderSession session, Av1FrameHeader fh)
    {
        fh.ApplyGrain = br.ReadBool();
        if (!fh.ApplyGrain)
            return;

        ushort grainSeed = (ushort)br.ReadBits(16);
        bool updateGrain = fh.FrameType == Av1Constants.InterFrame ? br.ReadBool() : true;
        if (!updateGrain)
        {
            int refIdx = (int)br.ReadBits(3);
            var refSlot = session.RefFrames[refIdx];
            if (!refSlot.Valid || refSlot.SavedFilmGrain is null)
                throw new InvalidDataException($"AV1 film_grain_params_ref_idx references invalid slot {refIdx}.");
            var loaded = refSlot.SavedFilmGrain.Clone();
            loaded.GrainSeed = grainSeed;
            loaded.ApplyGrain = true;
            fh.FilmGrain = loaded;
            return;
        }

        var fg = new Av1FilmGrainParams
        {
            ApplyGrain = true,
            GrainSeed = grainSeed,
            UpdateGrain = true,
        };

        fg.NumYPoints = (int)br.ReadBits(4);
        if (fg.NumYPoints > 14)
            throw new InvalidDataException($"Invalid AV1 film grain num_y_points {fg.NumYPoints}.");
        for (int i = 0; i < fg.NumYPoints; i++)
        {
            fg.PointYValue[i] = (byte)br.ReadBits(8);
            fg.PointYScaling[i] = (byte)br.ReadBits(8);
            if (i > 0 && fg.PointYValue[i] <= fg.PointYValue[i - 1])
                throw new InvalidDataException("AV1 film grain point_y_value is not strictly increasing.");
        }

        fg.ChromaScalingFromLuma = !seq.Monochrome && br.ReadBool();
        if (seq.Monochrome || fg.ChromaScalingFromLuma || (seq.SubsamplingX == 1 && seq.SubsamplingY == 1 && fg.NumYPoints == 0))
        {
            fg.NumCbPoints = 0;
            fg.NumCrPoints = 0;
        }
        else
        {
            fg.NumCbPoints = (int)br.ReadBits(4);
            if (fg.NumCbPoints > 10)
                throw new InvalidDataException($"Invalid AV1 film grain num_cb_points {fg.NumCbPoints}.");
            for (int i = 0; i < fg.NumCbPoints; i++)
            {
                fg.PointCbValue[i] = (byte)br.ReadBits(8);
                fg.PointCbScaling[i] = (byte)br.ReadBits(8);
                if (i > 0 && fg.PointCbValue[i] <= fg.PointCbValue[i - 1])
                    throw new InvalidDataException("AV1 film grain point_cb_value is not strictly increasing.");
            }

            fg.NumCrPoints = (int)br.ReadBits(4);
            if (fg.NumCrPoints > 10)
                throw new InvalidDataException($"Invalid AV1 film grain num_cr_points {fg.NumCrPoints}.");
            for (int i = 0; i < fg.NumCrPoints; i++)
            {
                fg.PointCrValue[i] = (byte)br.ReadBits(8);
                fg.PointCrScaling[i] = (byte)br.ReadBits(8);
                if (i > 0 && fg.PointCrValue[i] <= fg.PointCrValue[i - 1])
                    throw new InvalidDataException("AV1 film grain point_cr_value is not strictly increasing.");
            }
        }

        fg.GrainScalingMinus8 = (int)br.ReadBits(2);
        fg.ArCoeffLag = (int)br.ReadBits(2);
        int numPosLuma = 2 * fg.ArCoeffLag * (fg.ArCoeffLag + 1);
        int numPosChroma = fg.NumYPoints > 0 ? numPosLuma + 1 : numPosLuma;
        if (fg.NumYPoints > 0)
        {
            for (int i = 0; i < numPosLuma; i++)
                fg.ArCoeffsYPlus128[i] = (byte)br.ReadBits(8);
        }
        if (fg.ChromaScalingFromLuma || fg.NumCbPoints > 0)
        {
            for (int i = 0; i < numPosChroma; i++)
                fg.ArCoeffsCbPlus128[i] = (byte)br.ReadBits(8);
        }
        if (fg.ChromaScalingFromLuma || fg.NumCrPoints > 0)
        {
            for (int i = 0; i < numPosChroma; i++)
                fg.ArCoeffsCrPlus128[i] = (byte)br.ReadBits(8);
        }
        fg.ArCoeffShiftMinus6 = (int)br.ReadBits(2);
        fg.GrainScaleShift = (int)br.ReadBits(2);
        if (fg.NumCbPoints > 0)
        {
            fg.CbMult = (int)br.ReadBits(8);
            fg.CbLumaMult = (int)br.ReadBits(8);
            fg.CbOffset = (int)br.ReadBits(9);
        }
        if (fg.NumCrPoints > 0)
        {
            fg.CrMult = (int)br.ReadBits(8);
            fg.CrLumaMult = (int)br.ReadBits(8);
            fg.CrOffset = (int)br.ReadBits(9);
        }
        fg.OverlapFlag = br.ReadBool();
        fg.ClipToRestrictedRange = br.ReadBool();
        fh.FilmGrain = fg;
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
            if (fh.PrimaryRefFrame == Av1Constants.PrimaryRefNone)
            {
                fh.SegmentationUpdateMap = true;
                fh.SegmentationTemporalUpdate = false;
                fh.SegmentationUpdateData = true;
            }
            else
            {
                fh.SegmentationUpdateMap = br.ReadBool();
                fh.SegmentationTemporalUpdate = fh.SegmentationUpdateMap && br.ReadBool();
                fh.SegmentationUpdateData = br.ReadBool();
            }

            if (fh.SegmentationUpdateData)
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
                        else
                        {
                            fh.FeatureData[i, j] = 0;
                        }
                    }
                }
            }
        }
        else
        {
            Array.Clear(fh.FeatureEnabled);
            Array.Clear(fh.FeatureData);
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
        {
            fh.LoopFilterLevel[0] = 0;
            fh.LoopFilterLevel[1] = 0;
            fh.LoopFilterRefDeltas[Av1Constants.IntraFrame] = 1;
            fh.LoopFilterRefDeltas[Av1Constants.LastFrame] = 0;
            fh.LoopFilterRefDeltas[Av1Constants.Last2Frame] = 0;
            fh.LoopFilterRefDeltas[Av1Constants.Last3Frame] = 0;
            fh.LoopFilterRefDeltas[Av1Constants.BwdRefFrame] = 0;
            fh.LoopFilterRefDeltas[Av1Constants.GoldenFrame] = -1;
            fh.LoopFilterRefDeltas[Av1Constants.AltRef2Frame] = -1;
            fh.LoopFilterRefDeltas[Av1Constants.AltRefFrame] = -1;
            fh.LoopFilterModeDeltas[0] = 0;
            fh.LoopFilterModeDeltas[1] = 0;
            return;
        }

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
