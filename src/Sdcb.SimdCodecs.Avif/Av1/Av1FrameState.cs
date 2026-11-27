namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Per-frame block metadata produced during tile decoding and consumed by deblocking, CDEF, and loop restoration.
/// </summary>
internal sealed class Av1FrameState
{
    internal readonly int MiRows;

    /// <summary>
    /// Set when the frame's tiles are parsed ahead of their reconstruction: one record set per tile (null for a tile
    /// not decoded yet), reconstructed by <c>Av1FrameDecoder.ReconstructDeferred</c> before the post-filters.
    /// </summary>
    internal Av1TileRecords?[]? DeferredTiles;
    internal readonly int MiCols;
    internal readonly int NumPlanes;
    internal readonly int SubX;
    internal readonly int SubY;

    internal readonly byte[] MiSizes;
    internal readonly byte[] YModes;
    internal readonly byte[] UvModes;
    internal readonly byte[] Skips;
    internal readonly byte[] SkipModes;
    internal readonly byte[] CompGroupIdxs;
    internal readonly byte[] CompoundIdxs;
    internal readonly byte[] InterpFilters; // 2 per MI cell: [vert, horiz]
    internal readonly byte[] SegmentIds;
    internal readonly byte[] InterTxSizes;
    internal readonly byte[] IsInters;
    internal readonly sbyte[] RefFrames; // 2 per MI cell (-1 = NONE, 0 = INTRA_FRAME)
    internal readonly short[] Mvs; // 4 per MI cell: [ref0_y, ref0_x, ref1_y, ref1_x]
    internal readonly sbyte[] DeltaLfs; // 4 per MI cell (when DeltaLfPresent)
    internal readonly byte[] TxTypes; // MiRows * MiCols (disjoint per tile)
    internal readonly byte[]? PaletteSizesY;
    internal readonly byte[]? PaletteSizesUv;
    internal readonly ushort[]? PaletteColorsY;
    internal readonly ushort[]? PaletteColorsUv;

    // LoopfilterTxSizes[plane][planeRow * planeMiCols + planeCol]
    internal readonly byte[][] LoopfilterTxSizes;
    internal readonly int[] LfTxCols;

    // CDEF: one entry per 64x64 block (16x16 MI cells)
    internal readonly int CdefCols;
    internal readonly int CdefRows;
    internal readonly sbyte[] CdefIdx;

    // Loop Restoration unit grids per plane
    internal readonly int[] LrUnitRows = new int[3];
    internal readonly int[] LrUnitCols = new int[3];
    internal readonly byte[][] LrType = new byte[3][];
    internal readonly sbyte[][] LrWiener = new sbyte[3][]; // 6 per unit (2 passes * 3 taps)
    internal readonly byte[][] LrSgrSet = new byte[3][];
    internal readonly sbyte[][] LrSgrXqd = new sbyte[3][]; // 2 per unit

    internal Av1FrameState(Av1SequenceHeader seq, Av1FrameHeader fh)
    {
        MiRows = fh.MiRows;
        MiCols = fh.MiCols;
        NumPlanes = seq.NumPlanes;
        SubX = seq.SubsamplingX;
        SubY = seq.SubsamplingY;

        int miCount = MiRows * MiCols;
        MiSizes = new byte[miCount];
        YModes = new byte[miCount];
        UvModes = new byte[miCount];
        Skips = new byte[miCount];
        SkipModes = new byte[miCount];
        CompGroupIdxs = new byte[miCount];
        CompoundIdxs = new byte[miCount];
        InterpFilters = new byte[miCount * 2];
        SegmentIds = new byte[miCount];
        InterTxSizes = new byte[miCount];
        IsInters = new byte[miCount];
        RefFrames = new sbyte[miCount * 2];
        Array.Fill(RefFrames, (sbyte)Av1Constants.NoneFrame);
        Mvs = new short[miCount * 4];
        DeltaLfs = fh.DeltaLfPresent ? new sbyte[miCount * 4] : [];
        TxTypes = new byte[miCount];
        if (fh.AllowScreenContentTools)
        {
            PaletteSizesY = new byte[miCount];
            PaletteSizesUv = new byte[miCount];
            PaletteColorsY = new ushort[miCount * 8];
            PaletteColorsUv = new ushort[miCount * 8];
        }

        LoopfilterTxSizes = new byte[NumPlanes][];
        LfTxCols = new int[NumPlanes];
        for (int p = 0; p < NumPlanes; p++)
        {
            int sx = p == 0 ? 0 : SubX;
            int sy = p == 0 ? 0 : SubY;
            int cols = (MiCols + sx) >> sx;
            int rows = (MiRows + sy) >> sy;
            LfTxCols[p] = cols;
            LoopfilterTxSizes[p] = new byte[rows * cols];
        }

        CdefCols = (MiCols + 15) >> 4;
        CdefRows = (MiRows + 15) >> 4;
        CdefIdx = new sbyte[CdefRows * CdefCols];
        Array.Fill(CdefIdx, (sbyte)-1);

        if (fh.UsesLr)
        {
            for (int p = 0; p < NumPlanes; p++)
            {
                if (fh.FrameRestorationType[p] == Av1Constants.RestoreNone) continue;
                int sx = p == 0 ? 0 : SubX;
                int sy = p == 0 ? 0 : SubY;
                int unitSize = fh.LoopRestorationSize[p];
                int planeH = Av1Constants.Round2(fh.FrameHeight, sy);
                int planeW = Av1Constants.Round2(fh.UpscaledWidth, sx);
                int uRows = Math.Max((planeH + (unitSize >> 1)) / unitSize, 1);
                int uCols = Math.Max((planeW + (unitSize >> 1)) / unitSize, 1);
                LrUnitRows[p] = uRows;
                LrUnitCols[p] = uCols;
                int uCount = uRows * uCols;
                LrType[p] = new byte[uCount];
                LrWiener[p] = new sbyte[uCount * 6];
                LrSgrSet[p] = new byte[uCount];
                LrSgrXqd[p] = new sbyte[uCount * 2];
            }
        }
    }
}
