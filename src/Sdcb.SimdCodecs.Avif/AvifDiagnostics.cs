namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// Diagnostic flags for stage-by-stage ablation benchmarking. Never set in production.
/// </summary>
[Flags]
internal enum AvifAblation
{
    None = 0,
    SkipIntraPred = 1 << 0,
    SkipInterPred = 1 << 1,
    SkipTransform = 1 << 2,
    SkipLoopFilter = 1 << 3,
    SkipCdef = 1 << 4,
    SkipSuperres = 1 << 5,
    SkipLoopRestoration = 1 << 6,
    SkipFilmGrain = 1 << 7,
    SkipColor = 1 << 8,
}

internal static class AvifDiagnostics
{
    internal static AvifAblation Ablation;
}
