using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// A codec-neutral decoded raster - the fidelity tier of the codecs facade. It
/// preserves the source bit depth and channel count so a downstream consumer
/// (e.g. an astro pipeline working in 16-bit / float) sees the real sample
/// values rather than a lossy 8-bit projection.
/// <para>
/// <see cref="Pixels"/> is interleaved, row-major, tightly packed (no row
/// padding), in host byte order. The convenience <see cref="ToRgba8"/>
/// down-converts to 8-bit RGBA for the display tier (e.g. a terminal / UI raster).
/// </para>
/// </summary>
public interface IDecodedImage
{
    /// <summary>Pixel width.</summary>
    int Width { get; }

    /// <summary>Pixel height.</summary>
    int Height { get; }

    /// <summary>Samples per pixel: 1 (gray), 2 (gray+alpha), 3 (RGB), 4 (RGBA).</summary>
    int Channels { get; }

    /// <summary>The per-channel sample type in <see cref="Pixels"/>.</summary>
    SampleFormat SampleFormat { get; }

    /// <summary>
    /// Interleaved, row-major, tightly-packed pixel samples in host byte order.
    /// The row stride is <c>Width * Channels * bytesPerSample</c> with no padding,
    /// where <c>bytesPerSample</c> follows from <see cref="SampleFormat"/>.
    /// </summary>
    ReadOnlySpan<byte> Pixels { get; }

    /// <summary>
    /// The embedded ICC colour profile, or an empty span when the source carried
    /// none. Informational for a colour-managed consumer; never rescales pixels.
    /// </summary>
    ReadOnlySpan<byte> IccProfile { get; }

    /// <summary>
    /// How the sample values encode colour: H.273 primaries + transfer, code
    /// range, and (for float rasters) what the values are referenced to.
    /// Informational like <see cref="IccProfile"/> — never rescales pixels.
    /// Defaults to <see cref="ColorEncoding.AssumedSrgb"/> so implementations
    /// (and consumers) that predate colour signalling keep today's meaning.
    /// Note <see cref="ToRgba8"/>'s code-value projection is only colour-correct
    /// for display-referred sRGB-ish content — check here before relying on it
    /// (a PQ/HLG-tagged raster needs a colour-managed conversion instead).
    /// </summary>
    ColorEncoding ColorEncoding => ColorEncoding.AssumedSrgb;

    /// <summary>
    /// Down-converts to a freshly-allocated, tightly-packed 8-bit RGBA buffer
    /// (row-major, 4 bytes/pixel, R,G,B,A byte order). 16-bit samples are scaled
    /// by <c>&gt;&gt; 8</c>; gray expands across R/G/B; a missing alpha becomes 255.
    /// </summary>
    byte[] ToRgba8();

    /// <summary>
    /// Widens to a freshly-allocated RGBA float32 buffer (row-major, 4
    /// floats/pixel, R,G,B,A order). Container-only and meaning-preserving:
    /// UInt8/UInt16 samples normalize to <c>[0,1]</c> (0 → 0f, max code → exactly
    /// 1f), Float32 samples copy verbatim (no clamp, no rescale), gray broadcasts
    /// across R/G/B, a missing alpha becomes 1f. The result stays in
    /// <see cref="ColorEncoding"/> — a PQ-encoded raster is still PQ-encoded
    /// afterwards; this is NOT a linearization or display conversion.
    /// </summary>
    float[] ToFloats();
}
