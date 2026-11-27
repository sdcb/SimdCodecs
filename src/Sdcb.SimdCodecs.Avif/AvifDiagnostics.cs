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

    /// <summary>
    /// The options every <c>Parallel.For</c> in the decoder runs under. Benchmarks set
    /// <see cref="ParallelOptions.MaxDegreeOfParallelism"/> to 1 for a truly serial decode (stage timers then add up).
    /// </summary>
    internal static readonly ParallelOptions ParallelOptions = new();

    /// <summary>
    /// Stage timers inside tile decoding (benchmark instrumentation). Read once from <c>AVIF_STAGE_TIMERS=1</c>; as a
    /// static readonly false the JIT drops every timer site.
    /// </summary>
    internal static readonly bool Timers = Environment.GetEnvironmentVariable("AVIF_STAGE_TIMERS") == "1";

    /// <summary>Accumulated <see cref="System.Diagnostics.Stopwatch"/> ticks per <see cref="AvifTimer"/>.</summary>
    internal static readonly long[] Ticks = new long[(int)AvifTimer.Count];

    internal static void Add(AvifTimer timer, long start) =>
        Interlocked.Add(ref Ticks[(int)timer], System.Diagnostics.Stopwatch.GetTimestamp() - start);
}

internal enum AvifTimer
{
    ModeInfo,
    MiStore,
    Coeffs,
    Tiles,
    PostFilters,
    Count,
}
