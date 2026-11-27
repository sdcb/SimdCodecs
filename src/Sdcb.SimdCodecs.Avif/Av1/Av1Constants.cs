namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Constants and lookup tables from the AOM AV1 Bitstream &amp; Decoding Process Specification (§3, §5, §6, §9).
/// </summary>
internal static class Av1Constants
{
    // OBU types (§6.2.2)
    internal const int ObuSequenceHeader = 1;
    internal const int ObuTemporalDelimiter = 2;
    internal const int ObuFrameHeader = 3;
    internal const int ObuTileGroup = 4;
    internal const int ObuMetadata = 5;
    internal const int ObuFrame = 6;
    internal const int ObuRedundantFrameHeader = 7;
    internal const int ObuTileList = 8;
    internal const int ObuPadding = 15;

    // Frame types (§6.8.2)
    internal const int KeyFrame = 0;
    internal const int InterFrame = 1;
    internal const int IntraOnlyFrame = 2;
    internal const int SwitchFrame = 3;

    // Reference frames
    internal const int NumRefFrames = 8;
    internal const int RefsPerFrame = 7;
    internal const int TotalRefsPerFrame = 8;
    internal const int PrimaryRefNone = 7;
    internal const int IntraFrame = 0;
    internal const int LastFrame = 1;
    internal const int Last2Frame = 2;
    internal const int Last3Frame = 3;
    internal const int GoldenFrame = 4;
    internal const int BwdRefFrame = 5;
    internal const int AltRef2Frame = 6;
    internal const int AltRefFrame = 7;

    // Inter prediction modes (§6.10.22)
    internal const int NearestMv = 14;
    internal const int NearMv = 15;
    internal const int GlobalMv = 16;
    internal const int NewMv = 17;
    internal const int NearestNearestMv = 18;
    internal const int NearNearMv = 19;
    internal const int NearestNewMv = 20;
    internal const int NewNearestMv = 21;
    internal const int NearNewMv = 22;
    internal const int NewNearMv = 23;
    internal const int GlobalGlobalMv = 24;
    internal const int NewNewMv = 25;

    // Interpolation filters (§6.8.9)
    internal const int EightTap = 0;
    internal const int EightTapSmooth = 1;
    internal const int EightTapSharp = 2;
    internal const int Bilinear = 3;
    internal const int Switchable = 4;

    // Reference / compound / motion / interintra / global-motion enums (§6.10)
    internal const int SingleReference = 0;
    internal const int CompoundReference = 1;
    internal const int UnidirCompReference = 0;
    internal const int BidirCompReference = 1;

    internal const int MotionSimple = 0;
    internal const int MotionObmc = 1;
    internal const int MotionLocalWarp = 2;

    internal const int IiDcPred = 0;
    internal const int IiVPred = 1;
    internal const int IiHPred = 2;
    internal const int IiSmoothPred = 3;

    internal const int CompoundWedge = 0;
    internal const int CompoundDiffWtd = 1;
    internal const int CompoundAverage = 2;
    internal const int CompoundIntra = 3;
    internal const int CompoundDistance = 4;

    internal const int GmIdentity = 0;
    internal const int GmTranslation = 1;
    internal const int GmRotZoom = 2;
    internal const int GmAffine = 3;

    // Inter prediction & warped motion precision constants (§3)
    internal const int RefScaleShift = 14;
    internal const int SubpelBits = 4;
    internal const int SubpelMask = 15;
    internal const int ScaleSubpelBits = 10;
    internal const int FilterBits = 7;
    internal const int RefMvsLimit = (1 << 12) - 1;
    internal const int MaxFrameDistance = 31;
    internal const int MaxOffsetWidth = 8;
    internal const int MaxOffsetHeight = 0;
    internal const int MfmvStackSize = 3;
    internal const int WarpedModelPrecBits = 16;
    internal const int WarpParamReduceBits = 6;
    internal const int WarpedPixelPrecShifts = 1 << 6;
    internal const int WarpedDiffPrecBits = 10;
    internal const int WarpedModelTransClamp = 1 << 23;
    internal const int WarpedModelNonDiagAffineClamp = 1 << 13;
    internal const int GmAbsTransBits = 12;
    internal const int GmAbsTransOnlyBits = 9;
    internal const int GmAbsAlphaBits = 12;
    internal const int GmAlphaPrecBits = 15;
    internal const int GmTransPrecBits = 6;
    internal const int GmTransOnlyPrecBits = 3;
    internal const int DivLutPrecBits = 14;
    internal const int DivLutBits = 8;
    internal const int LeastSquaresSamplesMax = 8;
    internal const int LsMvMax = 256;
    internal const int MaskMasterSize = 64;
    internal const int WedgeTypes = 16;
    internal const int WedgeHorizontal = 0;
    internal const int WedgeVertical = 1;
    internal const int WedgeOblique27 = 2;
    internal const int WedgeOblique63 = 3;
    internal const int WedgeOblique117 = 4;
    internal const int WedgeOblique153 = 5;

    // Block sizes (§3)
    internal const int Block4x4 = 0;
    internal const int Block4x8 = 1;
    internal const int Block8x4 = 2;
    internal const int Block8x8 = 3;
    internal const int Block8x16 = 4;
    internal const int Block16x8 = 5;
    internal const int Block16x16 = 6;
    internal const int Block16x32 = 7;
    internal const int Block32x16 = 8;
    internal const int Block32x32 = 9;
    internal const int Block32x64 = 10;
    internal const int Block64x32 = 11;
    internal const int Block64x64 = 12;
    internal const int Block64x128 = 13;
    internal const int Block128x64 = 14;
    internal const int Block128x128 = 15;
    internal const int Block4x16 = 16;
    internal const int Block16x4 = 17;
    internal const int Block8x32 = 18;
    internal const int Block32x8 = 19;
    internal const int Block16x64 = 20;
    internal const int Block64x16 = 21;
    internal const int BlockInvalid = 22;
    internal const int BlockSizes = 22;

    // Partitions (§3)
    internal const int PartitionNone = 0;
    internal const int PartitionHorz = 1;
    internal const int PartitionVert = 2;
    internal const int PartitionSplit = 3;
    internal const int PartitionHorzA = 4;
    internal const int PartitionHorzB = 5;
    internal const int PartitionVertA = 6;
    internal const int PartitionVertB = 7;
    internal const int PartitionHorz4 = 8;
    internal const int PartitionVert4 = 9;

    // Intra modes (§6.10.24)
    internal const int DcPred = 0;
    internal const int VPred = 1;
    internal const int HPred = 2;
    internal const int D45Pred = 3;
    internal const int D135Pred = 4;
    internal const int D113Pred = 5;
    internal const int D157Pred = 6;
    internal const int D203Pred = 7;
    internal const int D67Pred = 8;
    internal const int SmoothPred = 9;
    internal const int SmoothVPred = 10;
    internal const int SmoothHPred = 11;
    internal const int PaethPred = 12;
    internal const int UvCflPred = 13;
    internal const int IntraModes = 13;

    // Tx sizes (§3)
    internal const int Tx4x4 = 0;
    internal const int Tx8x8 = 1;
    internal const int Tx16x16 = 2;
    internal const int Tx32x32 = 3;
    internal const int Tx64x64 = 4;
    internal const int Tx4x8 = 5;
    internal const int Tx8x4 = 6;
    internal const int Tx8x16 = 7;
    internal const int Tx16x8 = 8;
    internal const int Tx16x32 = 9;
    internal const int Tx32x16 = 10;
    internal const int Tx32x64 = 11;
    internal const int Tx64x32 = 12;
    internal const int Tx4x16 = 13;
    internal const int Tx16x4 = 14;
    internal const int Tx8x32 = 15;
    internal const int Tx32x8 = 16;
    internal const int Tx16x64 = 17;
    internal const int Tx64x16 = 18;
    internal const int TxSizes = 5;
    internal const int TxSizesAll = 19;

    // Tx modes (§6.8.21)
    internal const int Only4x4 = 0;
    internal const int TxModeLargest = 1;
    internal const int TxModeSelect = 2;

    // Tx types (§3)
    internal const int DctDct = 0;
    internal const int AdstDct = 1;
    internal const int DctAdst = 2;
    internal const int AdstAdst = 3;
    internal const int FlipAdstDct = 4;
    internal const int DctFlipAdst = 5;
    internal const int FlipAdstFlipAdst = 6;
    internal const int AdstFlipAdst = 7;
    internal const int FlipAdstAdst = 8;
    internal const int Idtx = 9;
    internal const int VDct = 10;
    internal const int HDct = 11;
    internal const int VAdst = 12;
    internal const int HAdst = 13;
    internal const int VFlipAdst = 14;
    internal const int HFlipAdst = 15;
    internal const int TxTypes = 16;

    // Tx classes (§7.12.2)
    internal const int TxClass2D = 0;
    internal const int TxClassHoriz = 1;
    internal const int TxClassVert = 2;

    // Tx sets (§5.11.45)
    internal const int TxSetDctOnly = 0;
    internal const int TxSetIntra1 = 1;
    internal const int TxSetIntra2 = 2;

    // Motion vectors & IntraBC constants (§3 / §7.10)
    internal const int IntrabcDelayPixels = 256;
    internal const int MvBorder = 128;
    internal const int RefCatLevel = 640;
    internal const int MaxRefMvStackSize = 8;
    internal const int MaxVarTxDepth = 2;
    internal const int MvContexts = 2;
    internal const int MvIntrabcContext = 1;
    internal const int MvJointZero = 0;
    internal const int MvJointHnzvz = 1;
    internal const int MvJointHzvnz = 2;
    internal const int MvJointHnzvnz = 3;
    internal const int MvClass0 = 0;
    internal const int Class0Size = 2;
    internal const int MvOffsetBits = 10;
    internal const int NoneFrame = -1;
    internal const int TxSetDctIdtx = 3;
    internal const int TxSetInter1 = 4;
    internal const int TxSetInter2 = 5;
    internal const int TxSetInter3 = 6;

    // Segmentation features (§6.8.13)
    internal const int MaxSegments = 8;
    internal const int SegLvlAltQ = 0;
    internal const int SegLvlAltLfYV = 1;
    internal const int SegLvlAltLfYH = 2;
    internal const int SegLvlAltLfU = 3;
    internal const int SegLvlAltLfV = 4;
    internal const int SegLvlRefFrame = 5;
    internal const int SegLvlSkip = 6;
    internal const int SegLvlGlobalMv = 7;
    internal const int SegLvlMax = 8;

    internal static readonly int[] SegmentationFeatureBits = [8, 6, 6, 6, 6, 3, 0, 0];
    internal static readonly bool[] SegmentationFeatureSigned = [true, true, true, true, true, false, false, false];
    internal static readonly int[] SegmentationFeatureMax = [255, 63, 63, 63, 63, 7, 0, 0];

    // Loop restoration types (§6.8.20)
    internal const int RestoreNone = 0;
    internal const int RestoreWiener = 1;
    internal const int RestoreSgrproj = 2;
    internal const int RestoreSwitchable = 3;

    // Superres
    internal const int SuperresDenomBits = 3;
    internal const int SuperresDenomMin = 9;
    internal const int SuperresNum = 8;

    // Entropy coder
    internal const int EcProbShift = 6;
    internal const int EcMinProb = 4;

    // Tiles
    internal const int MaxTileWidth = 4096;
    internal const int MaxTileArea = 4096 * 2304;
    internal const int MaxTileCols = 64;
    internal const int MaxTileRows = 64;

    // Palette
    internal const int PaletteMinSize = 2;
    internal const int PaletteMaxSize = 8;

    // Block geometry (§9.3)
    internal static readonly int[] MiWidthLog2 = [0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 0, 2, 1, 3, 2, 4];
    internal static readonly int[] MiHeightLog2 = [0, 1, 0, 1, 2, 1, 2, 3, 2, 3, 4, 3, 4, 5, 4, 5, 2, 0, 3, 1, 4, 2];
    internal static readonly int[] BlockWidth = [4, 4, 8, 8, 8, 16, 16, 16, 32, 32, 32, 64, 64, 64, 128, 128, 4, 16, 8, 32, 16, 64];
    internal static readonly int[] BlockHeight = [4, 8, 4, 8, 16, 8, 16, 32, 16, 32, 64, 32, 64, 128, 64, 128, 16, 4, 32, 8, 64, 16];
    internal static readonly int[] Num4x4BlocksWide = [1, 1, 2, 2, 2, 4, 4, 4, 8, 8, 8, 16, 16, 16, 32, 32, 1, 4, 2, 8, 4, 16];
    internal static readonly int[] Num4x4BlocksHigh = [1, 2, 1, 2, 4, 2, 4, 8, 4, 8, 16, 8, 16, 32, 16, 32, 4, 1, 8, 2, 16, 4];
    internal static readonly int[] SizeGroup = [0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 0, 0, 1, 1, 2, 2];
    internal static readonly int[] WedgeBits = [0, 0, 0, 4, 4, 4, 4, 4, 4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 0, 0];

    internal static readonly int[] DivMult =
    [
        0, 16384, 8192, 5461, 4096, 3276, 2730, 2340, 2048, 1820, 1638,
        1489, 1365, 1260, 1170, 1092, 1024, 963, 910, 862, 819, 780,
        744, 712, 682, 655, 630, 606, 585, 564, 546, 528,
    ];

    internal static readonly int[] CompoundModeCtxMap =
    [
        0, 1, 1, 1, 1,
        1, 2, 3, 4, 4,
        4, 4, 5, 6, 7,
    ];

    internal static readonly int[] MaxTxSizeRect =
    [
        Tx4x4, Tx4x8, Tx8x4, Tx8x8,
        Tx8x16, Tx16x8, Tx16x16, Tx16x32,
        Tx32x16, Tx32x32, Tx32x64, Tx64x32,
        Tx64x64, Tx64x64, Tx64x64, Tx64x64,
        Tx4x16, Tx16x4, Tx8x32, Tx32x8,
        Tx16x64, Tx64x16,
    ];

    internal static readonly int[] TxWidth = [4, 8, 16, 32, 64, 4, 8, 8, 16, 16, 32, 32, 64, 4, 16, 8, 32, 16, 64];
    internal static readonly int[] TxHeight = [4, 8, 16, 32, 64, 8, 4, 16, 8, 32, 16, 64, 32, 16, 4, 32, 8, 64, 16];
    internal static readonly int[] TxWidthLog2 = [2, 3, 4, 5, 6, 2, 3, 3, 4, 4, 5, 5, 6, 2, 4, 3, 5, 4, 6];
    internal static readonly int[] TxHeightLog2 = [2, 3, 4, 5, 6, 3, 2, 4, 3, 5, 4, 6, 5, 4, 2, 5, 3, 6, 4];

    internal static readonly int[] TxSizeSqr =
    [
        Tx4x4, Tx8x8, Tx16x16, Tx32x32, Tx64x64,
        Tx4x4, Tx4x4, Tx8x8, Tx8x8, Tx16x16, Tx16x16, Tx32x32, Tx32x32,
        Tx4x4, Tx4x4, Tx8x8, Tx8x8, Tx16x16, Tx16x16,
    ];

    internal static readonly int[] TxSizeSqrUp =
    [
        Tx4x4, Tx8x8, Tx16x16, Tx32x32, Tx64x64,
        Tx8x8, Tx8x8, Tx16x16, Tx16x16, Tx32x32, Tx32x32, Tx64x64, Tx64x64,
        Tx16x16, Tx16x16, Tx32x32, Tx32x32, Tx64x64, Tx64x64,
    ];

    internal static readonly int[] SplitTxSize =
    [
        Tx4x4, Tx4x4, Tx8x8, Tx16x16, Tx32x32,
        Tx4x4, Tx4x4, Tx8x8, Tx8x8, Tx16x16, Tx16x16, Tx32x32, Tx32x32,
        Tx4x8, Tx8x4, Tx8x16, Tx16x8, Tx16x32, Tx32x16,
    ];

    internal static readonly int[] AdjustedTxSize =
    [
        Tx4x4, Tx8x8, Tx16x16, Tx32x32, Tx32x32,
        Tx4x8, Tx8x4, Tx8x16, Tx16x8, Tx16x32, Tx32x16, Tx32x32, Tx32x32,
        Tx4x16, Tx16x4, Tx8x32, Tx32x8, Tx16x32, Tx32x16,
    ];

    // Partition_Subsize[partition][squareBlockSize] (§9.3)
    internal static int PartitionSubsize(int partition, int bSize) => (partition, bSize) switch
    {
        (PartitionNone, _) => bSize,
        (PartitionHorz, Block8x8) => Block8x4,
        (PartitionHorz, Block16x16) => Block16x8,
        (PartitionHorz, Block32x32) => Block32x16,
        (PartitionHorz, Block64x64) => Block64x32,
        (PartitionHorz, Block128x128) => Block128x64,
        (PartitionVert, Block8x8) => Block4x8,
        (PartitionVert, Block16x16) => Block8x16,
        (PartitionVert, Block32x32) => Block16x32,
        (PartitionVert, Block64x64) => Block32x64,
        (PartitionVert, Block128x128) => Block64x128,
        (PartitionSplit, Block8x8) => Block4x4,
        (PartitionSplit, Block16x16) => Block8x8,
        (PartitionSplit, Block32x32) => Block16x16,
        (PartitionSplit, Block64x64) => Block32x32,
        (PartitionSplit, Block128x128) => Block64x64,
        (PartitionHorzA, Block16x16) => Block16x8,
        (PartitionHorzA, Block32x32) => Block32x16,
        (PartitionHorzA, Block64x64) => Block64x32,
        (PartitionHorzA, Block128x128) => Block128x64,
        (PartitionHorzB, Block16x16) => Block16x8,
        (PartitionHorzB, Block32x32) => Block32x16,
        (PartitionHorzB, Block64x64) => Block64x32,
        (PartitionHorzB, Block128x128) => Block128x64,
        (PartitionVertA, Block16x16) => Block8x16,
        (PartitionVertA, Block32x32) => Block16x32,
        (PartitionVertA, Block64x64) => Block32x64,
        (PartitionVertA, Block128x128) => Block64x128,
        (PartitionVertB, Block16x16) => Block8x16,
        (PartitionVertB, Block32x32) => Block16x32,
        (PartitionVertB, Block64x64) => Block32x64,
        (PartitionVertB, Block128x128) => Block64x128,
        (PartitionHorz4, Block16x16) => Block16x4,
        (PartitionHorz4, Block32x32) => Block32x8,
        (PartitionHorz4, Block64x64) => Block64x16,
        (PartitionVert4, Block16x16) => Block4x16,
        (PartitionVert4, Block32x32) => Block8x32,
        (PartitionVert4, Block64x64) => Block16x64,
        _ => BlockInvalid,
    };

    // Subsampled_Size[bSize][subsamplingX][subsamplingY] (§5.11.5)
    internal static int SubsampledSize(int bSize, int subX, int subY)
    {
        if (subX == 0 && subY == 0) return bSize;
        int w = Math.Max(4, BlockWidth[bSize] >> subX);
        int h = Math.Max(4, BlockHeight[bSize] >> subY);
        return (w, h) switch
        {
            (4, 4) => Block4x4,
            (4, 8) => Block4x8,
            (8, 4) => Block8x4,
            (8, 8) => Block8x8,
            (8, 16) => Block8x16,
            (16, 8) => Block16x8,
            (16, 16) => Block16x16,
            (16, 32) => Block16x32,
            (32, 16) => Block32x16,
            (32, 32) => Block32x32,
            (32, 64) => Block32x64,
            (64, 32) => Block64x32,
            (64, 64) => Block64x64,
            (64, 128) => Block64x128,
            (128, 64) => Block128x64,
            (128, 128) => Block128x128,
            (4, 16) => Block4x16,
            (16, 4) => Block16x4,
            (8, 32) => Block8x32,
            (32, 8) => Block32x8,
            (16, 64) => Block16x64,
            (64, 16) => Block64x16,
            _ => BlockInvalid,
        };
    }

    internal static readonly int[] IntraModeContext = [0, 1, 2, 3, 4, 4, 4, 4, 3, 0, 1, 2, 0];

    internal static readonly int[] ModeToAngle = [0, 90, 180, 45, 135, 113, 157, 203, 67, 0, 0, 0, 0];

    internal static readonly int[] ModeToTxfm =
    [
        DctDct,   // DC_PRED
        AdstDct,  // V_PRED
        DctAdst,  // H_PRED
        DctDct,   // D45_PRED
        AdstAdst, // D135_PRED
        AdstDct,  // D113_PRED
        DctAdst,  // D157_PRED
        DctAdst,  // D203_PRED
        AdstDct,  // D67_PRED
        AdstAdst, // SMOOTH_PRED
        AdstDct,  // SMOOTH_V_PRED
        DctAdst,  // SMOOTH_H_PRED
        AdstAdst, // PAETH_PRED
        DctDct,   // UV_CFL_PRED
    ];

    internal static readonly int[] TxTypeIntraInvSet1 = [Idtx, DctDct, VDct, HDct, AdstAdst, AdstDct, DctAdst];
    internal static readonly int[] TxTypeIntraInvSet2 = [Idtx, DctDct, AdstAdst, AdstDct, DctAdst];
    internal static readonly int[] TxTypeInterInvSet1 =
    [
        Idtx, VDct, HDct, VAdst, HAdst, VFlipAdst, HFlipAdst,
        DctDct, AdstDct, DctAdst, FlipAdstDct, DctFlipAdst, AdstAdst,
        FlipAdstFlipAdst, AdstFlipAdst, FlipAdstAdst,
    ];
    internal static readonly int[] TxTypeInterInvSet2 =
    [
        Idtx, VDct, HDct, DctDct, AdstDct, DctAdst, FlipAdstDct,
        DctFlipAdst, AdstAdst, FlipAdstFlipAdst, AdstFlipAdst, FlipAdstAdst,
    ];
    internal static readonly int[] TxTypeInterInvSet3 = [Idtx, DctDct];

    internal static int FindTxSize(int w, int h)
    {
        for (int txSz = 0; txSz < TxWidth.Length; txSz++)
        {
            if (TxWidth[txSz] == w && TxHeight[txSz] == h)
                return txSz;
        }
        return Tx4x4;
    }

    internal static readonly int[] FilterIntraModeToIntraDir = [DcPred, VPred, HPred, D157Pred, DcPred];

    internal static readonly int[] MaxTxDepth =
    [
        0, 1, 1, 1,
        2, 2, 2, 3,
        3, 3, 4, 4,
        4, 4, 4, 4,
        2, 2, 3, 3,
        4, 4,
    ];
    internal static int GetTxClass(int txType) => txType switch
    {
        HDct or HAdst or HFlipAdst => TxClassHoriz,
        VDct or VAdst or VFlipAdst => TxClassVert,
        _ => TxClass2D,
    };

    internal static int FloorLog2(int x)
    {
        int s = 0;
        while (x != 0)
        {
            x >>= 1;
            s++;
        }
        return s - 1;
    }

    internal static int FloorLog2(long x)
    {
        int s = 0;
        while (x != 0)
        {
            x >>= 1;
            s++;
        }
        return s - 1;
    }

    internal static int CeilLog2(int x)
    {
        if (x < 2) return 0;
        int i = 1, p = 2;
        while (p < x)
        {
            i++;
            p <<= 1;
        }
        return i;
    }

    internal static int Round2(int x, int n) => n == 0 ? x : (x + (1 << (n - 1))) >> n;
    internal static long Round2(long x, int n) => n == 0 ? x : (x + (1L << (n - 1))) >> n;

    internal static int Round2Signed(int x, int n) => x >= 0 ? Round2(x, n) : -Round2(-x, n);
    internal static long Round2Signed(long x, int n) => x >= 0 ? Round2(x, n) : -Round2(-x, n);

    internal static int Clip1(int x, int bitDepth) => Math.Clamp(x, 0, (1 << bitDepth) - 1);
    internal static int Clip3(int lo, int hi, int x) => x < lo ? lo : x > hi ? hi : x;
    internal static long Clip3(long lo, long hi, long x) => x < lo ? lo : x > hi ? hi : x;
}
