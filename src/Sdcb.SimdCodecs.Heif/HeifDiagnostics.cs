namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// Measurement-only stage switches for the benchmark's ablation runs: each flag switches one stage of the decode
/// off, so the time that disappears is that stage's share. Output is wrong with any flag set; the production path
/// is <see cref="None"/>.
/// </summary>
[Flags]
internal enum HeifAblation
{
    None = 0,

    /// <summary>No scaling, inverse transform or residual add (the coefficients are still parsed).</summary>
    SkipResidual = 1,

    /// <summary>No intra sample prediction (reference gathering, filtering and prediction).</summary>
    SkipIntraPred = 2,

    /// <summary>No deblocking.</summary>
    SkipDeblock = 4,

    /// <summary>No sample adaptive offset.</summary>
    SkipSao = 8,

    /// <summary>No Y′CbCr → RGBA conversion (the canvas is left untouched).</summary>
    SkipColor = 16,
}

/// <summary>Holds the process-wide <see cref="HeifAblation"/> setting; set only by the benchmark.</summary>
internal static class HeifDiagnostics
{
    internal static HeifAblation Ablation;

    internal static bool Skips(HeifAblation stage) => (Ablation & stage) != 0;
}
