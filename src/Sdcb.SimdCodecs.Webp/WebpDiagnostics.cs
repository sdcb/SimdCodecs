namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// Measurement-only stage switches. A stage's cost is the time that disappears when it is
/// skipped (attribution by ablation, see AGENTS.md); the output of an ablated decode is garbage.
/// </summary>
[Flags]
internal enum WebpAblation
{
    None = 0,

    /// <summary>VP8: no loop filter.</summary>
    SkipLoopFilter = 1 << 0,

    /// <summary>VP8: coefficients are parsed but not dequantised, transformed or added.</summary>
    SkipResidual = 1 << 1,

    /// <summary>VP8: no intra prediction (modes are still parsed).</summary>
    SkipPrediction = 1 << 2,

    /// <summary>VP8: no upsampling / YUV→RGBA conversion. VP8L: no ARGB→RGBA conversion.</summary>
    SkipOutput = 1 << 3,

    /// <summary>The ALPH chunk is not decoded (output alpha is left as it is).</summary>
    SkipAlpha = 1 << 4,

    /// <summary>VP8L: the inverse transforms are not applied.</summary>
    SkipTransforms = 1 << 5,

    /// <summary>VP8: no scratch borders, no copy of macroblocks into the row cache, no cache slide.</summary>
    SkipMacroblockCopy = 1 << 6,

    /// <summary>VP8L: the inverse predictor transform alone is not applied.</summary>
    SkipPredictorTransform = 1 << 7,

    /// <summary>VP8: everything after mode and token parsing.</summary>
    EntropyOnly = SkipLoopFilter | SkipResidual | SkipPrediction | SkipOutput | SkipMacroblockCopy,
}

/// <summary>Entry points for the benchmark's ablation runs.</summary>
internal static class WebpDiagnostics
{
    /// <summary>
    /// <see cref="WebpDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/> with stages switched off.
    /// With <see cref="WebpAblation.None"/> it is exactly the production path.
    /// </summary>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, WebpAblation ablation) =>
        WebpDecoder.DecodeTo(data, rgba, 0, fancy: true, ablation);

    /// <summary>The opt-in multithreaded decode, for the benchmark's <c>quick --mt</c>.</summary>
    public static void DecodeToMultithreaded(ReadOnlySpan<byte> data, Span<byte> rgba) =>
        WebpDecoder.DecodeTo(data, rgba, 0, MultithreadedOptions);

    private static readonly WebpDecodeOptions MultithreadedOptions = new() { Multithreaded = true };
}
