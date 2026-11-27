namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// Per-tile mutable CDF tables initialized from the spec defaults (§6.8.2, §8.2.2).
/// Flat arrays allow zero-overhead Span slicing for every context lookup.
/// </summary>
internal sealed class Av1CdfState
{
    internal readonly ushort[] IntraFrameYMode = new ushort[Av1CdfTables.DefaultIntraFrameYModeCdf.Length];
    internal readonly ushort[] YMode = new ushort[Av1CdfTables.DefaultYModeCdf.Length];
    internal readonly ushort[] UvModeCflNotAllowed = new ushort[Av1CdfTables.DefaultUvModeCflNotAllowedCdf.Length];
    internal readonly ushort[] UvModeCflAllowed = new ushort[Av1CdfTables.DefaultUvModeCflAllowedCdf.Length];
    internal readonly ushort[] AngleDelta = new ushort[Av1CdfTables.DefaultAngleDeltaCdf.Length];
    internal readonly ushort[] Intrabc = new ushort[Av1CdfTables.DefaultIntrabcCdf.Length];
    internal readonly ushort[] PartitionW8 = new ushort[Av1CdfTables.DefaultPartitionW8Cdf.Length];
    internal readonly ushort[] PartitionW16 = new ushort[Av1CdfTables.DefaultPartitionW16Cdf.Length];
    internal readonly ushort[] PartitionW32 = new ushort[Av1CdfTables.DefaultPartitionW32Cdf.Length];
    internal readonly ushort[] PartitionW64 = new ushort[Av1CdfTables.DefaultPartitionW64Cdf.Length];
    internal readonly ushort[] PartitionW128 = new ushort[Av1CdfTables.DefaultPartitionW128Cdf.Length];
    internal readonly ushort[] Tx8x8 = new ushort[Av1CdfTables.DefaultTx8x8Cdf.Length];
    internal readonly ushort[] Tx16x16 = new ushort[Av1CdfTables.DefaultTx16x16Cdf.Length];
    internal readonly ushort[] Tx32x32 = new ushort[Av1CdfTables.DefaultTx32x32Cdf.Length];
    internal readonly ushort[] Tx64x64 = new ushort[Av1CdfTables.DefaultTx64x64Cdf.Length];
    internal readonly ushort[] TxfmSplit = new ushort[Av1CdfTables.DefaultTxfmSplitCdf.Length];
    internal readonly ushort[] FilterIntraMode = new ushort[Av1CdfTables.DefaultFilterIntraModeCdf.Length];
    internal readonly ushort[] FilterIntra = new ushort[Av1CdfTables.DefaultFilterIntraCdf.Length];
    internal readonly ushort[] SegmentId = new ushort[Av1CdfTables.DefaultSegmentIdCdf.Length];
    internal readonly ushort[] Skip = new ushort[Av1CdfTables.DefaultSkipCdf.Length];
    internal readonly ushort[] PaletteYSize = new ushort[Av1CdfTables.DefaultPaletteYSizeCdf.Length];
    internal readonly ushort[] PaletteUvSize = new ushort[Av1CdfTables.DefaultPaletteUvSizeCdf.Length];
    internal readonly ushort[][] PaletteYColor =
    [
        new ushort[Av1CdfTables.DefaultPaletteSize2YColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize3YColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize4YColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize5YColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize6YColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize7YColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize8YColorCdf.Length],
    ];
    internal readonly ushort[][] PaletteUvColor =
    [
        new ushort[Av1CdfTables.DefaultPaletteSize2UvColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize3UvColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize4UvColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize5UvColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize6UvColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize7UvColorCdf.Length],
        new ushort[Av1CdfTables.DefaultPaletteSize8UvColorCdf.Length],
    ];
    internal readonly ushort[] PaletteYMode = new ushort[Av1CdfTables.DefaultPaletteYModeCdf.Length];
    internal readonly ushort[] PaletteUvMode = new ushort[Av1CdfTables.DefaultPaletteUvModeCdf.Length];
    internal readonly ushort[] DeltaQ = new ushort[Av1CdfTables.DefaultDeltaQCdf.Length];
    internal readonly ushort[] DeltaLf = new ushort[Av1CdfTables.DefaultDeltaLfCdf.Length];
    internal readonly ushort[][] DeltaLfMulti =
    [
        new ushort[Av1CdfTables.DefaultDeltaLfCdf.Length],
        new ushort[Av1CdfTables.DefaultDeltaLfCdf.Length],
        new ushort[Av1CdfTables.DefaultDeltaLfCdf.Length],
        new ushort[Av1CdfTables.DefaultDeltaLfCdf.Length],
    ];
    internal readonly ushort[] IntraTxTypeSet1 = new ushort[Av1CdfTables.DefaultIntraTxTypeSet1Cdf.Length];
    internal readonly ushort[] IntraTxTypeSet2 = new ushort[Av1CdfTables.DefaultIntraTxTypeSet2Cdf.Length];
    internal readonly ushort[] InterTxTypeSet1 = new ushort[Av1CdfTables.DefaultInterTxTypeSet1Cdf.Length];
    internal readonly ushort[] InterTxTypeSet2 = new ushort[Av1CdfTables.DefaultInterTxTypeSet2Cdf.Length];
    internal readonly ushort[] InterTxTypeSet3 = new ushort[Av1CdfTables.DefaultInterTxTypeSet3Cdf.Length];
    internal readonly ushort[] CflSign = new ushort[Av1CdfTables.DefaultCflSignCdf.Length];
    internal readonly ushort[] CflAlpha = new ushort[Av1CdfTables.DefaultCflAlphaCdf.Length];
    internal readonly ushort[] UseWiener = new ushort[Av1CdfTables.DefaultUseWienerCdf.Length];
    internal readonly ushort[] UseSgrproj = new ushort[Av1CdfTables.DefaultUseSgrprojCdf.Length];
    internal readonly ushort[] RestorationType = new ushort[Av1CdfTables.DefaultRestorationTypeCdf.Length];

    // Inter mode / reference / compound / motion CDFs (§9.4)
    internal readonly ushort[] SegmentIdPredicted = new ushort[Av1CdfTables.DefaultSegmentIdPredictedCdf.Length];
    internal readonly ushort[] NewMv = new ushort[Av1CdfTables.DefaultNewMvCdf.Length];
    internal readonly ushort[] ZeroMv = new ushort[Av1CdfTables.DefaultZeroMvCdf.Length];
    internal readonly ushort[] RefMv = new ushort[Av1CdfTables.DefaultRefMvCdf.Length];
    internal readonly ushort[] DrlMode = new ushort[Av1CdfTables.DefaultDrlModeCdf.Length];
    internal readonly ushort[] IsInter = new ushort[Av1CdfTables.DefaultIsInterCdf.Length];
    internal readonly ushort[] CompMode = new ushort[Av1CdfTables.DefaultCompModeCdf.Length];
    internal readonly ushort[] SkipMode = new ushort[Av1CdfTables.DefaultSkipModeCdf.Length];
    internal readonly ushort[] CompRef = new ushort[Av1CdfTables.DefaultCompRefCdf.Length];
    internal readonly ushort[] CompBwdRef = new ushort[Av1CdfTables.DefaultCompBwdRefCdf.Length];
    internal readonly ushort[] SingleRef = new ushort[Av1CdfTables.DefaultSingleRefCdf.Length];
    internal readonly ushort[] CompoundMode = new ushort[Av1CdfTables.DefaultCompoundModeCdf.Length];
    internal readonly ushort[] InterpFilter = new ushort[Av1CdfTables.DefaultInterpFilterCdf.Length];
    internal readonly ushort[] MotionMode = new ushort[Av1CdfTables.DefaultMotionModeCdf.Length];
    internal readonly ushort[] CompoundIdx = new ushort[Av1CdfTables.DefaultCompoundIdxCdf.Length];
    internal readonly ushort[] CompGroupIdx = new ushort[Av1CdfTables.DefaultCompGroupIdxCdf.Length];
    internal readonly ushort[] CompoundType = new ushort[Av1CdfTables.DefaultCompoundTypeCdf.Length];
    internal readonly ushort[] InterIntra = new ushort[Av1CdfTables.DefaultInterIntraCdf.Length];
    internal readonly ushort[] InterIntraMode = new ushort[Av1CdfTables.DefaultInterIntraModeCdf.Length];
    internal readonly ushort[] WedgeIndex = new ushort[Av1CdfTables.DefaultWedgeIndexCdf.Length];
    internal readonly ushort[] WedgeInterIntra = new ushort[Av1CdfTables.DefaultWedgeInterIntraCdf.Length];
    internal readonly ushort[] UseObmc = new ushort[Av1CdfTables.DefaultUseObmcCdf.Length];
    internal readonly ushort[] CompRefType = new ushort[Av1CdfTables.DefaultCompRefTypeCdf.Length];
    internal readonly ushort[] UniCompRef = new ushort[Av1CdfTables.DefaultUniCompRefCdf.Length];

    // Motion Vector CDFs (indexed by mvCtx in 0..1: 0=inter, 1=intrabc)
    internal readonly ushort[] MvJoint = new ushort[Av1Constants.MvContexts * 5];
    internal readonly ushort[] MvClass = new ushort[Av1Constants.MvContexts * 2 * 12];
    internal readonly ushort[] MvClass0Bit = new ushort[Av1Constants.MvContexts * 2 * 3];
    internal readonly ushort[] MvClass0Fr = new ushort[Av1Constants.MvContexts * 2 * 2 * 5];
    internal readonly ushort[] MvClass0Hp = new ushort[Av1Constants.MvContexts * 2 * 3];
    internal readonly ushort[] MvSign = new ushort[Av1Constants.MvContexts * 2 * 3];
    internal readonly ushort[] MvBit = new ushort[Av1Constants.MvContexts * 2 * 10 * 3];
    internal readonly ushort[] MvFr = new ushort[Av1Constants.MvContexts * 2 * 5];
    internal readonly ushort[] MvHp = new ushort[Av1Constants.MvContexts * 2 * 3];

    // Coefficient CDFs (sliced for the frame's qCtx in 0..3)
    internal readonly ushort[] TxbSkip = new ushort[5 * 13 * 3];
    internal readonly ushort[] EobPt16 = new ushort[2 * 2 * 6];
    internal readonly ushort[] EobPt32 = new ushort[2 * 2 * 7];
    internal readonly ushort[] EobPt64 = new ushort[2 * 2 * 8];
    internal readonly ushort[] EobPt128 = new ushort[2 * 2 * 9];
    internal readonly ushort[] EobPt256 = new ushort[2 * 2 * 10];
    internal readonly ushort[] EobPt512 = new ushort[2 * 11];
    internal readonly ushort[] EobPt1024 = new ushort[2 * 12];
    internal readonly ushort[] EobExtra = new ushort[5 * 2 * 9 * 3];
    internal readonly ushort[] DcSign = new ushort[2 * 3 * 3];
    internal readonly ushort[] CoeffBaseEob = new ushort[5 * 2 * 4 * 4];
    internal readonly ushort[] CoeffBase = new ushort[5 * 2 * 42 * 5];
    internal readonly ushort[] CoeffBr = new ushort[5 * 2 * 21 * 5];

    internal void InitDefault(int baseQIdx)
    {
        Av1CdfTables.DefaultIntraFrameYModeCdf.CopyTo(IntraFrameYMode, 0);
        Av1CdfTables.DefaultYModeCdf.CopyTo(YMode, 0);
        Av1CdfTables.DefaultUvModeCflNotAllowedCdf.CopyTo(UvModeCflNotAllowed, 0);
        Av1CdfTables.DefaultUvModeCflAllowedCdf.CopyTo(UvModeCflAllowed, 0);
        Av1CdfTables.DefaultAngleDeltaCdf.CopyTo(AngleDelta, 0);
        Av1CdfTables.DefaultIntrabcCdf.CopyTo(Intrabc, 0);
        Av1CdfTables.DefaultPartitionW8Cdf.CopyTo(PartitionW8, 0);
        Av1CdfTables.DefaultPartitionW16Cdf.CopyTo(PartitionW16, 0);
        Av1CdfTables.DefaultPartitionW32Cdf.CopyTo(PartitionW32, 0);
        Av1CdfTables.DefaultPartitionW64Cdf.CopyTo(PartitionW64, 0);
        Av1CdfTables.DefaultPartitionW128Cdf.CopyTo(PartitionW128, 0);
        Av1CdfTables.DefaultTx8x8Cdf.CopyTo(Tx8x8, 0);
        Av1CdfTables.DefaultTx16x16Cdf.CopyTo(Tx16x16, 0);
        Av1CdfTables.DefaultTx32x32Cdf.CopyTo(Tx32x32, 0);
        Av1CdfTables.DefaultTx64x64Cdf.CopyTo(Tx64x64, 0);
        Av1CdfTables.DefaultTxfmSplitCdf.CopyTo(TxfmSplit, 0);
        Av1CdfTables.DefaultFilterIntraModeCdf.CopyTo(FilterIntraMode, 0);
        Av1CdfTables.DefaultFilterIntraCdf.CopyTo(FilterIntra, 0);
        Av1CdfTables.DefaultSegmentIdCdf.CopyTo(SegmentId, 0);
        Av1CdfTables.DefaultSkipCdf.CopyTo(Skip, 0);
        Av1CdfTables.DefaultPaletteYSizeCdf.CopyTo(PaletteYSize, 0);
        Av1CdfTables.DefaultPaletteUvSizeCdf.CopyTo(PaletteUvSize, 0);
        Av1CdfTables.DefaultPaletteSize2YColorCdf.CopyTo(PaletteYColor[0], 0);
        Av1CdfTables.DefaultPaletteSize3YColorCdf.CopyTo(PaletteYColor[1], 0);
        Av1CdfTables.DefaultPaletteSize4YColorCdf.CopyTo(PaletteYColor[2], 0);
        Av1CdfTables.DefaultPaletteSize5YColorCdf.CopyTo(PaletteYColor[3], 0);
        Av1CdfTables.DefaultPaletteSize6YColorCdf.CopyTo(PaletteYColor[4], 0);
        Av1CdfTables.DefaultPaletteSize7YColorCdf.CopyTo(PaletteYColor[5], 0);
        Av1CdfTables.DefaultPaletteSize8YColorCdf.CopyTo(PaletteYColor[6], 0);
        Av1CdfTables.DefaultPaletteSize2UvColorCdf.CopyTo(PaletteUvColor[0], 0);
        Av1CdfTables.DefaultPaletteSize3UvColorCdf.CopyTo(PaletteUvColor[1], 0);
        Av1CdfTables.DefaultPaletteSize4UvColorCdf.CopyTo(PaletteUvColor[2], 0);
        Av1CdfTables.DefaultPaletteSize5UvColorCdf.CopyTo(PaletteUvColor[3], 0);
        Av1CdfTables.DefaultPaletteSize6UvColorCdf.CopyTo(PaletteUvColor[4], 0);
        Av1CdfTables.DefaultPaletteSize7UvColorCdf.CopyTo(PaletteUvColor[5], 0);
        Av1CdfTables.DefaultPaletteSize8UvColorCdf.CopyTo(PaletteUvColor[6], 0);
        Av1CdfTables.DefaultPaletteYModeCdf.CopyTo(PaletteYMode, 0);
        Av1CdfTables.DefaultPaletteUvModeCdf.CopyTo(PaletteUvMode, 0);
        Av1CdfTables.DefaultDeltaQCdf.CopyTo(DeltaQ, 0);
        Av1CdfTables.DefaultDeltaLfCdf.CopyTo(DeltaLf, 0);
        for (int i = 0; i < 4; i++)
            Av1CdfTables.DefaultDeltaLfCdf.CopyTo(DeltaLfMulti[i], 0);
        Av1CdfTables.DefaultIntraTxTypeSet1Cdf.CopyTo(IntraTxTypeSet1, 0);
        Av1CdfTables.DefaultIntraTxTypeSet2Cdf.CopyTo(IntraTxTypeSet2, 0);
        Av1CdfTables.DefaultInterTxTypeSet1Cdf.CopyTo(InterTxTypeSet1, 0);
        Av1CdfTables.DefaultInterTxTypeSet2Cdf.CopyTo(InterTxTypeSet2, 0);
        Av1CdfTables.DefaultInterTxTypeSet3Cdf.CopyTo(InterTxTypeSet3, 0);
        Av1CdfTables.DefaultCflSignCdf.CopyTo(CflSign, 0);
        Av1CdfTables.DefaultCflAlphaCdf.CopyTo(CflAlpha, 0);
        Av1CdfTables.DefaultUseWienerCdf.CopyTo(UseWiener, 0);
        Av1CdfTables.DefaultUseSgrprojCdf.CopyTo(UseSgrproj, 0);
        Av1CdfTables.DefaultRestorationTypeCdf.CopyTo(RestorationType, 0);

        Av1CdfTables.DefaultSegmentIdPredictedCdf.CopyTo(SegmentIdPredicted, 0);
        Av1CdfTables.DefaultNewMvCdf.CopyTo(NewMv, 0);
        Av1CdfTables.DefaultZeroMvCdf.CopyTo(ZeroMv, 0);
        Av1CdfTables.DefaultRefMvCdf.CopyTo(RefMv, 0);
        Av1CdfTables.DefaultDrlModeCdf.CopyTo(DrlMode, 0);
        Av1CdfTables.DefaultIsInterCdf.CopyTo(IsInter, 0);
        Av1CdfTables.DefaultCompModeCdf.CopyTo(CompMode, 0);
        Av1CdfTables.DefaultSkipModeCdf.CopyTo(SkipMode, 0);
        Av1CdfTables.DefaultCompRefCdf.CopyTo(CompRef, 0);
        Av1CdfTables.DefaultCompBwdRefCdf.CopyTo(CompBwdRef, 0);
        Av1CdfTables.DefaultSingleRefCdf.CopyTo(SingleRef, 0);
        Av1CdfTables.DefaultCompoundModeCdf.CopyTo(CompoundMode, 0);
        Av1CdfTables.DefaultInterpFilterCdf.CopyTo(InterpFilter, 0);
        Av1CdfTables.DefaultMotionModeCdf.CopyTo(MotionMode, 0);
        Av1CdfTables.DefaultCompoundIdxCdf.CopyTo(CompoundIdx, 0);
        Av1CdfTables.DefaultCompGroupIdxCdf.CopyTo(CompGroupIdx, 0);
        Av1CdfTables.DefaultCompoundTypeCdf.CopyTo(CompoundType, 0);
        Av1CdfTables.DefaultInterIntraCdf.CopyTo(InterIntra, 0);
        Av1CdfTables.DefaultInterIntraModeCdf.CopyTo(InterIntraMode, 0);
        Av1CdfTables.DefaultWedgeIndexCdf.CopyTo(WedgeIndex, 0);
        Av1CdfTables.DefaultWedgeInterIntraCdf.CopyTo(WedgeInterIntra, 0);
        Av1CdfTables.DefaultUseObmcCdf.CopyTo(UseObmc, 0);
        Av1CdfTables.DefaultCompRefTypeCdf.CopyTo(CompRefType, 0);
        Av1CdfTables.DefaultUniCompRefCdf.CopyTo(UniCompRef, 0);

        for (int ctx = 0; ctx < Av1Constants.MvContexts; ctx++)
        {
            Av1CdfTables.DefaultMvJointCdf.CopyTo(MvJoint, ctx * 5);
            Av1CdfTables.DefaultMvClassCdf.CopyTo(MvClass, ctx * 24);
            Av1CdfTables.DefaultMvClass0FrCdf.CopyTo(MvClass0Fr, ctx * 20);
            Av1CdfTables.DefaultMvFrCdf.CopyTo(MvFr, ctx * 10);
            for (int comp = 0; comp < 2; comp++)
            {
                int cc = (ctx * 2 + comp);
                Av1CdfTables.DefaultMvClass0BitCdf.CopyTo(MvClass0Bit, cc * 3);
                Av1CdfTables.DefaultMvClass0HpCdf.CopyTo(MvClass0Hp, cc * 3);
                Av1CdfTables.DefaultMvSignCdf.CopyTo(MvSign, cc * 3);
                Av1CdfTables.DefaultMvBitCdf.CopyTo(MvBit, cc * 30);
                Av1CdfTables.DefaultMvHpCdf.CopyTo(MvHp, cc * 3);
            }
        }

        int qCtx = baseQIdx <= 20 ? 0 : baseQIdx <= 60 ? 1 : baseQIdx <= 120 ? 2 : 3;
        SliceCopy(Av1CdfTables.DefaultTxbSkipCdf, qCtx, TxbSkip);
        SliceCopy(Av1CdfTables.DefaultEobPt16Cdf, qCtx, EobPt16);
        SliceCopy(Av1CdfTables.DefaultEobPt32Cdf, qCtx, EobPt32);
        SliceCopy(Av1CdfTables.DefaultEobPt64Cdf, qCtx, EobPt64);
        SliceCopy(Av1CdfTables.DefaultEobPt128Cdf, qCtx, EobPt128);
        SliceCopy(Av1CdfTables.DefaultEobPt256Cdf, qCtx, EobPt256);
        SliceCopy(Av1CdfTables.DefaultEobPt512Cdf, qCtx, EobPt512);
        SliceCopy(Av1CdfTables.DefaultEobPt1024Cdf, qCtx, EobPt1024);
        SliceCopy(Av1CdfTables.DefaultEobExtraCdf, qCtx, EobExtra);
        SliceCopy(Av1CdfTables.DefaultDcSignCdf, qCtx, DcSign);
        SliceCopy(Av1CdfTables.DefaultCoeffBaseEobCdf, qCtx, CoeffBaseEob);
        SliceCopy(Av1CdfTables.DefaultCoeffBaseCdf, qCtx, CoeffBase);
        SliceCopy(Av1CdfTables.DefaultCoeffBrCdf, qCtx, CoeffBr);
    }

    private static void SliceCopy(ushort[] src, int qCtx, ushort[] dst)
    {
        src.AsSpan(qCtx * dst.Length, dst.Length).CopyTo(dst);
    }

    internal Av1CdfState Clone(bool resetCounts = false)
    {
        var copy = new Av1CdfState();
        copy.CopyFrom(this, resetCounts);
        return copy;
    }

    internal void CopyFrom(Av1CdfState src, bool resetCounts)
    {
        CopyArr(src.IntraFrameYMode, IntraFrameYMode, 14, resetCounts);
        CopyArr(src.YMode, YMode, 14, resetCounts);
        CopyArr(src.UvModeCflNotAllowed, UvModeCflNotAllowed, 14, resetCounts);
        CopyArr(src.UvModeCflAllowed, UvModeCflAllowed, 15, resetCounts);
        CopyArr(src.AngleDelta, AngleDelta, 8, resetCounts);
        CopyArr(src.Intrabc, Intrabc, 3, resetCounts);
        CopyArr(src.PartitionW8, PartitionW8, 5, resetCounts);
        CopyArr(src.PartitionW16, PartitionW16, 11, resetCounts);
        CopyArr(src.PartitionW32, PartitionW32, 11, resetCounts);
        CopyArr(src.PartitionW64, PartitionW64, 11, resetCounts);
        CopyArr(src.PartitionW128, PartitionW128, 9, resetCounts);
        CopyArr(src.Tx8x8, Tx8x8, 3, resetCounts);
        CopyArr(src.Tx16x16, Tx16x16, 4, resetCounts);
        CopyArr(src.Tx32x32, Tx32x32, 4, resetCounts);
        CopyArr(src.Tx64x64, Tx64x64, 4, resetCounts);
        CopyArr(src.TxfmSplit, TxfmSplit, 3, resetCounts);
        CopyArr(src.FilterIntraMode, FilterIntraMode, 6, resetCounts);
        CopyArr(src.FilterIntra, FilterIntra, 3, resetCounts);
        CopyArr(src.SegmentId, SegmentId, 9, resetCounts);
        CopyArr(src.Skip, Skip, 3, resetCounts);
        CopyArr(src.PaletteYSize, PaletteYSize, 8, resetCounts);
        CopyArr(src.PaletteUvSize, PaletteUvSize, 8, resetCounts);
        for (int i = 0; i < 7; i++)
        {
            CopyArr(src.PaletteYColor[i], PaletteYColor[i], i + 3, resetCounts);
            CopyArr(src.PaletteUvColor[i], PaletteUvColor[i], i + 3, resetCounts);
        }
        CopyArr(src.PaletteYMode, PaletteYMode, 3, resetCounts);
        CopyArr(src.PaletteUvMode, PaletteUvMode, 3, resetCounts);
        CopyArr(src.DeltaQ, DeltaQ, 5, resetCounts);
        CopyArr(src.DeltaLf, DeltaLf, 5, resetCounts);
        for (int i = 0; i < 4; i++)
            CopyArr(src.DeltaLfMulti[i], DeltaLfMulti[i], 5, resetCounts);
        CopyArr(src.IntraTxTypeSet1, IntraTxTypeSet1, 8, resetCounts);
        CopyArr(src.IntraTxTypeSet2, IntraTxTypeSet2, 6, resetCounts);
        CopyArr(src.InterTxTypeSet1, InterTxTypeSet1, 17, resetCounts);
        CopyArr(src.InterTxTypeSet2, InterTxTypeSet2, 13, resetCounts);
        CopyArr(src.InterTxTypeSet3, InterTxTypeSet3, 3, resetCounts);
        CopyArr(src.CflSign, CflSign, 9, resetCounts);
        CopyArr(src.CflAlpha, CflAlpha, 17, resetCounts);
        CopyArr(src.UseWiener, UseWiener, 3, resetCounts);
        CopyArr(src.UseSgrproj, UseSgrproj, 3, resetCounts);
        CopyArr(src.RestorationType, RestorationType, 4, resetCounts);

        CopyArr(src.SegmentIdPredicted, SegmentIdPredicted, 3, resetCounts);
        CopyArr(src.NewMv, NewMv, 3, resetCounts);
        CopyArr(src.ZeroMv, ZeroMv, 3, resetCounts);
        CopyArr(src.RefMv, RefMv, 3, resetCounts);
        CopyArr(src.DrlMode, DrlMode, 3, resetCounts);
        CopyArr(src.IsInter, IsInter, 3, resetCounts);
        CopyArr(src.CompMode, CompMode, 3, resetCounts);
        CopyArr(src.SkipMode, SkipMode, 3, resetCounts);
        CopyArr(src.CompRef, CompRef, 3, resetCounts);
        CopyArr(src.CompBwdRef, CompBwdRef, 3, resetCounts);
        CopyArr(src.SingleRef, SingleRef, 3, resetCounts);
        CopyArr(src.CompoundMode, CompoundMode, 9, resetCounts);
        CopyArr(src.InterpFilter, InterpFilter, 4, resetCounts);
        CopyArr(src.MotionMode, MotionMode, 4, resetCounts);
        CopyArr(src.CompoundIdx, CompoundIdx, 3, resetCounts);
        CopyArr(src.CompGroupIdx, CompGroupIdx, 3, resetCounts);
        CopyArr(src.CompoundType, CompoundType, 3, resetCounts);
        CopyArr(src.InterIntra, InterIntra, 3, resetCounts);
        CopyArr(src.InterIntraMode, InterIntraMode, 5, resetCounts);
        CopyArr(src.WedgeIndex, WedgeIndex, 17, resetCounts);
        CopyArr(src.WedgeInterIntra, WedgeInterIntra, 3, resetCounts);
        CopyArr(src.UseObmc, UseObmc, 3, resetCounts);
        CopyArr(src.CompRefType, CompRefType, 3, resetCounts);
        CopyArr(src.UniCompRef, UniCompRef, 3, resetCounts);

        CopyArr(src.MvJoint, MvJoint, 5, resetCounts);
        CopyArr(src.MvClass, MvClass, 12, resetCounts);
        CopyArr(src.MvClass0Bit, MvClass0Bit, 3, resetCounts);
        CopyArr(src.MvClass0Fr, MvClass0Fr, 5, resetCounts);
        CopyArr(src.MvClass0Hp, MvClass0Hp, 3, resetCounts);
        CopyArr(src.MvSign, MvSign, 3, resetCounts);
        CopyArr(src.MvBit, MvBit, 3, resetCounts);
        CopyArr(src.MvFr, MvFr, 5, resetCounts);
        CopyArr(src.MvHp, MvHp, 3, resetCounts);

        CopyArr(src.TxbSkip, TxbSkip, 3, resetCounts);
        CopyArr(src.EobPt16, EobPt16, 6, resetCounts);
        CopyArr(src.EobPt32, EobPt32, 7, resetCounts);
        CopyArr(src.EobPt64, EobPt64, 8, resetCounts);
        CopyArr(src.EobPt128, EobPt128, 9, resetCounts);
        CopyArr(src.EobPt256, EobPt256, 10, resetCounts);
        CopyArr(src.EobPt512, EobPt512, 11, resetCounts);
        CopyArr(src.EobPt1024, EobPt1024, 12, resetCounts);
        CopyArr(src.EobExtra, EobExtra, 3, resetCounts);
        CopyArr(src.DcSign, DcSign, 3, resetCounts);
        CopyArr(src.CoeffBaseEob, CoeffBaseEob, 4, resetCounts);
        CopyArr(src.CoeffBase, CoeffBase, 5, resetCounts);
        CopyArr(src.CoeffBr, CoeffBr, 5, resetCounts);
    }

    private static void CopyArr(ushort[] src, ushort[] dst, int stride, bool resetCounts)
    {
        src.CopyTo(dst, 0);
        if (resetCounts)
        {
            for (int i = stride - 1; i < dst.Length; i += stride)
                dst[i] = 0;
        }
    }
}
