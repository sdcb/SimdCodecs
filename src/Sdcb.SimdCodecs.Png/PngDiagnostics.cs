namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// Measurement-only stage switches for the RGBA8 decode. A stage's cost is the time that disappears
/// when it is skipped (attribution by ablation, see AGENTS.md); the output of an ablated decode is garbage.
/// </summary>
[Flags]
internal enum PngAblation
{
    None = 0,

    /// <summary>No chunk CRC is computed or checked.</summary>
    SkipCrc = 1 << 0,

    /// <summary>Rows are inflated but not unfiltered.</summary>
    SkipUnfilter = 1 << 1,

    /// <summary>Rows are not expanded to RGBA (8-bit RGBA, unfiltered in place, has no expansion).</summary>
    SkipExpand = 1 << 2,

    /// <summary>Chunk walk and inflate only.</summary>
    InflateOnly = SkipCrc | SkipUnfilter | SkipExpand,
}

/// <summary>Entry points for the benchmark's ablation runs.</summary>
internal static class PngDiagnostics
{
    /// <summary>
    /// <see cref="PngImageDecoder.TryDecodeIntoRgba8"/> with stages switched off; with
    /// <see cref="PngAblation.None"/> it is exactly the production path (minus the cICP refusal).
    /// </summary>
    public static void DecodeRgba8(ReadOnlySpan<byte> data, Span<byte> rgba, PngAblation ablation)
    {
        var header = PngReader.ReadHeader(data, out var idat, verifyCrc: (ablation & PngAblation.SkipCrc) == 0);
        PngReader.DecodeRgba8(data, header, idat, rgba, ablation);
    }
}
