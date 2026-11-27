namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// One slot in the 8-slot AV1 reference frame store (§7.20, §7.21).
/// Holds the reconstructed picture (before film grain), saved CDFs, motion field MVs,
/// segmentation map, global motion parameters, loop filter deltas, and film grain params.
/// </summary>
internal sealed class Av1RefFrame
{
    internal bool Valid;
    internal int FrameId;
    internal int UpscaledWidth;
    internal int FrameWidth;
    internal int FrameHeight;
    internal int RenderWidth;
    internal int RenderHeight;
    internal int MiCols;
    internal int MiRows;
    internal int FrameType;
    internal int SubsamplingX;
    internal int SubsamplingY;
    internal int BitDepth;
    internal int OrderHint;
    internal int[] SavedOrderHints = new int[Av1Constants.TotalRefsPerFrame];

    internal Av1Picture? Picture;
    internal sbyte[] SavedRefFrames = [];
    internal short[] SavedMvs = []; // 2 per MI cell [y, x]
    internal int[,] SavedGmParams = new int[Av1Constants.TotalRefsPerFrame, 6];
    internal byte[] SavedSegmentIds = [];
    internal Av1CdfState? SavedCdfs;
    internal Av1FilmGrainParams? SavedFilmGrain;
    internal int[] SavedLoopFilterRefDeltas = [1, 0, 0, 0, -1, 0, -1, -1];
    internal int[] SavedLoopFilterModeDeltas = [0, 0];
    internal bool[,] SavedFeatureEnabled = new bool[Av1Constants.MaxSegments, Av1Constants.SegLvlMax];
    internal int[,] SavedFeatureData = new int[Av1Constants.MaxSegments, Av1Constants.SegLvlMax];
}

/// <summary>
/// Persistent multi-frame AV1 decoder state across frames in a sequence or layered still item.
/// Manages the 8-slot reference frame buffer (§7.20), order hints, saved CDFs, and motion field projection (§7.9).
/// </summary>
internal sealed class Av1DecoderSession
{
    internal Av1SequenceHeader? SequenceHeader { get; set; }
    internal Av1RefFrame[] RefFrames { get; } = CreateSlots();
    internal int CurrentFrameId { get; set; }

    /// <summary>
    /// True when the session is discarded after its one output picture (a still item): that picture may then own
    /// pooled planes even though a reference slot also holds it, because nothing decodes from the slot after the
    /// caller disposes the picture. A multi-frame session keeps reference pictures out of the pool.
    /// </summary>
    internal bool OneShot { get; init; }

    private static Av1RefFrame[] CreateSlots()
    {
        var arr = new Av1RefFrame[Av1Constants.NumRefFrames];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = new Av1RefFrame();
        return arr;
    }

    internal void Reset()
    {
        for (int i = 0; i < RefFrames.Length; i++)
            RefFrames[i] = new Av1RefFrame();
        CurrentFrameId = 0;
    }

    internal void ParseConfigObus(ReadOnlySpan<byte> configObus)
    {
        if (configObus.IsEmpty) return;
        foreach (var obu in Av1ObuParser.ParseAll(configObus))
        {
            if (obu.Type == Av1Constants.ObuSequenceHeader)
            {
                SequenceHeader = Av1SequenceHeader.Parse(configObus.Slice(obu.PayloadOffset, obu.PayloadLength));
                break;
            }
        }
    }

    /// <summary>
    /// Decodes one temporal unit (one sample in an AVIF sequence or a layered still payload) and returns
    /// the last shown picture (after film grain if enabled). Hidden frames update <see cref="RefFrames"/>.
    /// </summary>
    internal Av1Picture DecodeTemporalUnit(ReadOnlySpan<byte> payload, int maxOperatingPointSpatialId = -1)
    {
        var obus = Av1ObuParser.ParseAll(payload);
        Av1Picture? lastShown = null;
        Av1FrameHeader? fh = null;
        Av1Picture? pic = null;
        Av1FrameState? state = null;
        Av1TileDecoder? tileDecoder = null;
        Av1CdfState? savedUpdateCdf = null;
        int decodedTiles = 0;

        foreach (var obu in obus)
        {
            if (maxOperatingPointSpatialId >= 0 && obu.SpatialId > maxOperatingPointSpatialId)
                continue;

            var obuPayload = payload.Slice(obu.PayloadOffset, obu.PayloadLength);
            switch (obu.Type)
            {
                case Av1Constants.ObuSequenceHeader:
                    SequenceHeader = Av1SequenceHeader.Parse(obuPayload);
                    break;

                case Av1Constants.ObuFrame:
                {
                    if (SequenceHeader is null)
                        throw new InvalidDataException("AV1 frame OBU appeared before any sequence header OBU.");
                    var shown = DecodeFrameObu(obuPayload, obu.TemporalId, obu.SpatialId);
                    if (shown is not null)
                        lastShown = shown;
                    fh = null; pic = null; state = null; tileDecoder = null; savedUpdateCdf = null;
                    break;
                }

                case Av1Constants.ObuFrameHeader:
                {
                    if (SequenceHeader is null)
                        throw new InvalidDataException("AV1 frame header OBU appeared before any sequence header OBU.");
                    var br = new Av1BitReader(obuPayload);
                    fh = Av1FrameHeader.Parse(ref br, SequenceHeader, this, obu.TemporalId, obu.SpatialId);
                    if (fh.ShowExistingFrame)
                    {
                        lastShown = FinishShowExistingFrame(SequenceHeader, fh);
                        fh = null; pic = null; state = null; tileDecoder = null; savedUpdateCdf = null;
                        break;
                    }
                    pic = new Av1Picture(SequenceHeader, fh, pooled: fh.RefreshFrameFlags == 0);
                    state = new Av1FrameState(SequenceHeader, fh);
                    tileDecoder = null;
                    savedUpdateCdf = null;
                    decodedTiles = 0;
                    break;
                }

                case Av1Constants.ObuTileGroup:
                {
                    if (SequenceHeader is null || fh is null || pic is null || state is null)
                        throw new InvalidDataException("AV1 tile group OBU appeared without an active frame header.");
                    long tt = AvifDiagnostics.Timers ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    decodedTiles += Av1FrameDecoder.DecodeTileGroup(obuPayload, SequenceHeader, fh, pic, state, this, ref tileDecoder, ref savedUpdateCdf);
                    if (AvifDiagnostics.Timers) AvifDiagnostics.Add(AvifTimer.Tiles, tt);
                    if (decodedTiles >= fh.TileCols * fh.TileRows)
                    {
                        var shown = FinishDecodedFrame(SequenceHeader, fh, pic, state, savedUpdateCdf);
                        if (shown is not null)
                            lastShown = shown;
                        fh = null; pic = null; state = null; tileDecoder = null; savedUpdateCdf = null;
                    }
                    break;
                }

                case Av1Constants.ObuTemporalDelimiter:
                case Av1Constants.ObuMetadata:
                case Av1Constants.ObuPadding:
                case Av1Constants.ObuRedundantFrameHeader:
                    break;
            }
        }

        if (pic is not null && SequenceHeader is not null && fh is not null && state is not null && decodedTiles > 0)
        {
            var shown = FinishDecodedFrame(SequenceHeader, fh, pic, state, savedUpdateCdf);
            if (shown is not null)
                lastShown = shown;
        }

        return lastShown ?? throw new InvalidDataException("No displayed AV1 frame found in temporal unit.");
    }

    private Av1Picture? DecodeFrameObu(ReadOnlySpan<byte> obuPayload, int temporalId, int spatialId)
    {
        var seq = SequenceHeader!;
        var br = new Av1BitReader(obuPayload);
        var fh = Av1FrameHeader.Parse(ref br, seq, this, temporalId, spatialId);
        if (fh.ShowExistingFrame)
            return FinishShowExistingFrame(seq, fh);

        br.ByteAlign();
        int headerBytes = br.BytePosition;

        var pic = new Av1Picture(seq, fh, pooled: fh.RefreshFrameFlags == 0);
        var state = new Av1FrameState(seq, fh);
        Av1TileDecoder? tileDecoder = null;
        Av1CdfState? savedUpdateCdf = null;

        long tt = AvifDiagnostics.Timers ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Av1FrameDecoder.DecodeTileGroup(obuPayload[headerBytes..], seq, fh, pic, state, this, ref tileDecoder, ref savedUpdateCdf);
        if (AvifDiagnostics.Timers) AvifDiagnostics.Add(AvifTimer.Tiles, tt);
        return FinishDecodedFrame(seq, fh, pic, state, savedUpdateCdf);
    }

    private Av1Picture FinishShowExistingFrame(Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        var slot = RefFrames[fh.FrameToShowMapIdx];
        if (!slot.Valid || slot.Picture is null)
            throw new InvalidDataException($"AV1 show_existing_frame references invalid slot {fh.FrameToShowMapIdx}.");

        // §7.21: if frame_type == KEY_FRAME, refresh_frame_flags = 0xFF (slot copied to all slots)
        if (fh.RefreshFrameFlags != 0)
        {
            for (int i = 0; i < Av1Constants.NumRefFrames; i++)
            {
                if (((fh.RefreshFrameFlags >> i) & 1) != 0 && i != fh.FrameToShowMapIdx)
                    RefFrames[i] = CloneSlotMetadata(slot);
            }
        }

        // Apply saved film grain onto a fresh copy so the stored reference frame stays grain-free.
        if (seq.FilmGrainParamsPresent && slot.SavedFilmGrain is { ApplyGrain: true } fg && (AvifDiagnostics.Ablation & AvifAblation.SkipFilmGrain) == 0)
        {
            var copy = slot.Picture.Clone();
            Av1FilmGrain.Apply(seq, fg, copy);
            return copy;
        }

        return slot.Picture;
    }

    private Av1Picture? FinishDecodedFrame(
        Av1SequenceHeader seq,
        Av1FrameHeader fh,
        Av1Picture pic,
        Av1FrameState state,
        Av1CdfState? savedUpdateCdf)
    {
        // Post-filters up to Loop Restoration (stored in FrameStore BEFORE film grain!)
        long tp = AvifDiagnostics.Timers ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Av1FrameDecoder.RunPostFiltersBeforeGrain(seq, fh, pic, state, pooledOutput: fh.RefreshFrameFlags == 0 || OneShot);
        if (AvifDiagnostics.Timers) AvifDiagnostics.Add(AvifTimer.PostFilters, tp);

        // §7.19 Motion field motion vector storage + §7.20 Reference frame update
        if (fh.RefreshFrameFlags != 0)
        {
            UpdateReferenceFrames(seq, fh, pic, state, savedUpdateCdf);
        }

        if (!fh.ShowFrame)
            return null;

        // §7.18.3 Film grain is applied only to displayed output, never mutating stored reference frames!
        if (fh.ApplyGrain && fh.FilmGrain is not null && (AvifDiagnostics.Ablation & AvifAblation.SkipFilmGrain) == 0)
        {
            var outPic = fh.RefreshFrameFlags != 0 ? pic.Clone() : pic;
            Av1FilmGrain.Apply(seq, fh.FilmGrain, outPic);
            return outPic;
        }

        return pic;
    }

    private void UpdateReferenceFrames(
        Av1SequenceHeader seq,
        Av1FrameHeader fh,
        Av1Picture pic,
        Av1FrameState state,
        Av1CdfState? savedUpdateCdf)
    {
        // §7.19 Motion field motion vector storage process
        int miCount = fh.MiRows * fh.MiCols;
        var mfRefFrames = new sbyte[miCount];
        var mfMvs = new short[miCount * 2];
        Array.Fill(mfRefFrames, (sbyte)Av1Constants.NoneFrame);

        for (int row = 0; row < fh.MiRows; row++)
        {
            for (int col = 0; col < fh.MiCols; col++)
            {
                int idx = row * fh.MiCols + col;
                for (int list = 0; list < 2; list++)
                {
                    int r = state.RefFrames[idx * 2 + list];
                    if (r > Av1Constants.IntraFrame)
                    {
                        int refIdx = fh.RefFrameIdx[r - Av1Constants.LastFrame];
                        int dist = seq.GetRelativeDist(RefFrames[refIdx].OrderHint, fh.OrderHint);
                        if (dist < 0)
                        {
                            int mvRow = state.Mvs[idx * 4 + list * 2];
                            int mvCol = state.Mvs[idx * 4 + list * 2 + 1];
                            if (Math.Abs(mvRow) <= Av1Constants.RefMvsLimit && Math.Abs(mvCol) <= Av1Constants.RefMvsLimit)
                            {
                                mfRefFrames[idx] = (sbyte)r;
                                mfMvs[idx * 2] = (short)mvRow;
                                mfMvs[idx * 2 + 1] = (short)mvCol;
                            }
                        }
                    }
                }
            }
        }

        // §7.20: If disable_frame_end_update_cdf == 1, save the frame-start CDFs (not the tile-updated ones).
        Av1CdfState cdfsToSave = (!fh.DisableFrameEndUpdateCdf && savedUpdateCdf is not null)
            ? savedUpdateCdf
            : fh.InitialCdfs;

        var savedSegIds = (byte[])state.SegmentIds.Clone();
        var savedOrderHints = (int[])fh.OrderHints.Clone();
        var savedGm = (int[,])fh.GmParams.Clone();
        var savedLfRef = (int[])fh.LoopFilterRefDeltas.Clone();
        var savedLfMode = (int[])fh.LoopFilterModeDeltas.Clone();
        var savedFeatEn = (bool[,])fh.FeatureEnabled.Clone();
        var savedFeatData = (int[,])fh.FeatureData.Clone();
        var savedGrain = seq.FilmGrainParamsPresent ? fh.FilmGrain?.Clone() : null;

        for (int i = 0; i < Av1Constants.NumRefFrames; i++)
        {
            if (((fh.RefreshFrameFlags >> i) & 1) == 0)
                continue;

            var slot = new Av1RefFrame
            {
                Valid = true,
                FrameId = fh.CurrentFrameId,
                UpscaledWidth = fh.UpscaledWidth,
                FrameWidth = fh.FrameWidth,
                FrameHeight = fh.FrameHeight,
                RenderWidth = fh.RenderWidth,
                RenderHeight = fh.RenderHeight,
                MiCols = fh.MiCols,
                MiRows = fh.MiRows,
                FrameType = fh.FrameType,
                SubsamplingX = seq.SubsamplingX,
                SubsamplingY = seq.SubsamplingY,
                BitDepth = seq.BitDepth,
                OrderHint = fh.OrderHint,
                SavedOrderHints = savedOrderHints,
                Picture = pic,
                SavedRefFrames = mfRefFrames,
                SavedMvs = mfMvs,
                SavedGmParams = savedGm,
                SavedSegmentIds = savedSegIds,
                SavedCdfs = cdfsToSave,
                SavedFilmGrain = savedGrain,
                SavedLoopFilterRefDeltas = savedLfRef,
                SavedLoopFilterModeDeltas = savedLfMode,
                SavedFeatureEnabled = savedFeatEn,
                SavedFeatureData = savedFeatData,
            };
            RefFrames[i] = slot;
        }
    }

    private static Av1RefFrame CloneSlotMetadata(Av1RefFrame s) => new()
    {
        Valid = s.Valid,
        FrameId = s.FrameId,
        UpscaledWidth = s.UpscaledWidth,
        FrameWidth = s.FrameWidth,
        FrameHeight = s.FrameHeight,
        RenderWidth = s.RenderWidth,
        RenderHeight = s.RenderHeight,
        MiCols = s.MiCols,
        MiRows = s.MiRows,
        FrameType = s.FrameType,
        SubsamplingX = s.SubsamplingX,
        SubsamplingY = s.SubsamplingY,
        BitDepth = s.BitDepth,
        OrderHint = s.OrderHint,
        SavedOrderHints = s.SavedOrderHints,
        Picture = s.Picture,
        SavedRefFrames = s.SavedRefFrames,
        SavedMvs = s.SavedMvs,
        SavedGmParams = s.SavedGmParams,
        SavedSegmentIds = s.SavedSegmentIds,
        SavedCdfs = s.SavedCdfs,
        SavedFilmGrain = s.SavedFilmGrain,
        SavedLoopFilterRefDeltas = s.SavedLoopFilterRefDeltas,
        SavedLoopFilterModeDeltas = s.SavedLoopFilterModeDeltas,
        SavedFeatureEnabled = s.SavedFeatureEnabled,
        SavedFeatureData = s.SavedFeatureData,
    };
}
