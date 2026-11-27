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
    internal readonly ushort[] CflSign = new ushort[Av1CdfTables.DefaultCflSignCdf.Length];
    internal readonly ushort[] CflAlpha = new ushort[Av1CdfTables.DefaultCflAlphaCdf.Length];
    internal readonly ushort[] UseWiener = new ushort[Av1CdfTables.DefaultUseWienerCdf.Length];
    internal readonly ushort[] UseSgrproj = new ushort[Av1CdfTables.DefaultUseSgrprojCdf.Length];
    internal readonly ushort[] RestorationType = new ushort[Av1CdfTables.DefaultRestorationTypeCdf.Length];

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
        Av1CdfTables.DefaultCflSignCdf.CopyTo(CflSign, 0);
        Av1CdfTables.DefaultCflAlphaCdf.CopyTo(CflAlpha, 0);
        Av1CdfTables.DefaultUseWienerCdf.CopyTo(UseWiener, 0);
        Av1CdfTables.DefaultUseSgrprojCdf.CopyTo(UseSgrproj, 0);
        Av1CdfTables.DefaultRestorationTypeCdf.CopyTo(RestorationType, 0);

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
}
