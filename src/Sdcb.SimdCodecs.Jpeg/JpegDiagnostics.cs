namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>Receives one block's dequantized coefficients (natural order) before its IDCT.</summary>
/// <param name="component">Index of the component in frame order.</param>
/// <param name="blockX">Block column within the component's block grid.</param>
/// <param name="blockY">Block row within the component's block grid.</param>
/// <param name="dequantized">64 coefficients, natural (row-major) order, already multiplied by the quantization table.</param>
internal delegate void JpegBlockHook(int component, int blockX, int blockY, ReadOnlySpan<short> dequantized);

/// <summary>A component's decoded samples before upsampling and colour conversion.</summary>
/// <param name="Id">Component identifier from the frame header.</param>
/// <param name="H">Horizontal sampling factor.</param>
/// <param name="V">Vertical sampling factor.</param>
/// <param name="Width">Plane width in samples (the component's own resolution).</param>
/// <param name="Height">Plane height in samples.</param>
/// <param name="Quant">The quantization table this component uses, natural order.</param>
/// <param name="Samples">Row-major samples, <paramref name="Width"/> per row.</param>
internal sealed record JpegComponentPlane(int Id, int H, int V, int Width, int Height, ushort[] Quant, byte[] Samples);

/// <summary>
/// Stages the benchmark may switch off to attribute decode time by ablation (time that
/// disappears when a stage is skipped). Output is meaningless when any flag is set.
/// </summary>
[Flags]
internal enum JpegAblation
{
    None = 0,
    /// <summary>Entropy-decode every block but never run the inverse DCT.</summary>
    SkipIdct = 1,
    /// <summary>Do not upsample subsampled components (the row buffers keep stale data).</summary>
    SkipUpsample = 2,
    /// <summary>Do not colour-convert or write the RGBA destination.</summary>
    SkipColor = 4,
    /// <summary>
    /// Drop every decoded block at once (no IDCT call, no strip addressing) and emit no
    /// output rows: what is left is header parsing plus entropy decoding.
    /// </summary>
    SkipBlockSink = 8,
}

/// <summary>
/// Measurement-only access to decoder internals, for the accuracy tooling in
/// <c>benchmarks/</c> and for tests. Not part of the public API and not on any
/// production path: a null <see cref="JpegCore.BlockHook"/> costs one branch per block.
/// </summary>
internal static class JpegDiagnostics
{
    /// <summary>
    /// Full-scale decode up to (not including) upsampling and colour conversion.
    /// Returns the component planes; <paramref name="blockHook"/>, if given, sees
    /// every block's dequantized coefficients.
    /// </summary>
    public static JpegComponentPlane[] DecodePlanes(ReadOnlySpan<byte> data, JpegBlockHook? blockHook = null)
    {
        var core = new JpegCore(data, 1) { BlockHook = blockHook, CapturePlanes = true };
        try
        {
            core.Begin();
            core.DecodeInto(new byte[core.OutX * core.OutY * 4], core.OutX * 4);
            return core.ExportPlanes();
        }
        finally
        {
            core.ReturnBuffers();
        }
    }

    /// <summary>
    /// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, JpegScale)"/> at full scale with the given stages switched
    /// off. With <see cref="JpegAblation.None"/> it is exactly the production path.
    /// </summary>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination, JpegAblation ablation)
    {
        var core = new JpegCore(data, 1) { Ablation = ablation };
        try
        {
            core.Begin();
            if (rgbaDestination.Length < (long)core.OutX * core.OutY * 4)
                throw new ArgumentException("Destination too small.", nameof(rgbaDestination));
            core.DecodeInto(rgbaDestination, core.OutX * 4);
            return (core.OutX, core.OutY);
        }
        finally
        {
            core.ReturnBuffers();
        }
    }
}
