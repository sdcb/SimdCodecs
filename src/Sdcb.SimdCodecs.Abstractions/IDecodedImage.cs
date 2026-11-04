using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// A codec-neutral decoded raster - the fidelity tier of the codecs facade. It
/// preserves the source bit depth and channel count so a downstream consumer
/// (e.g. an astro pipeline working in 16-bit / float) sees the real sample
/// values rather than a lossy 8-bit projection.
/// <para>
/// <see cref="Pixels"/> is interleaved, row-major, tightly packed (no row
/// padding), in host byte order, with straight (unassociated) alpha. <see cref="TryCopyTo"/>
/// writes any <see cref="PixelFormat"/> into a caller's buffer; <see cref="ToRgba8"/>,
/// <see cref="ToRgba16"/> and <see cref="ToFloats"/> are allocating conveniences.
/// </para>
/// <para>
/// Dispose the image when done with it: an image whose pixels were rented from a pool
/// (<see cref="DecodeOptions.Pool"/>) returns them then, after which reading <see cref="Pixels"/> throws
/// <see cref="ObjectDisposedException"/>. An image in ordinary memory does nothing on dispose.
/// </para>
/// </summary>
public interface IDecodedImage : IDisposable
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
    /// Bits of real precision per sample, as <see cref="ImageInfo.BitDepth"/>: 10 for a 10-bit HEIF decoded into
    /// <see cref="SampleFormat.UInt16"/>, whose samples are scaled to fill the 16 bits (1023 → 65535). Defaults to the
    /// full width of <see cref="SampleFormat"/>.
    /// </summary>
    int BitDepth => SampleFormat switch
    {
        SampleFormat.UInt8 => 8,
        SampleFormat.UInt16 => 16,
        _ => 32,
    };

    /// <summary>
    /// Interleaved, row-major, tightly-packed pixel samples in host byte order.
    /// The row stride is <c>Width * Channels * bytesPerSample</c> with no padding,
    /// where <c>bytesPerSample</c> follows from <see cref="SampleFormat"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The image has been disposed and its pixels returned to a pool.</exception>
    ReadOnlySpan<byte> Pixels { get; }

    /// <summary>
    /// How the sample values encode colour: H.273 primaries + transfer, code
    /// range, and (for float rasters) what the values are referenced to.
    /// Informational like the ICC profile in <see cref="Metadata"/> — never rescales pixels.
    /// Defaults to <see cref="ColorEncoding.AssumedSrgb"/>.
    /// It is not derived from that profile: an ICC-tagged image
    /// (a Display P3 photo) still reports the default unless its format signals
    /// colour some other way (PNG cICP, or gAMA 1.0 for linear). Read the profile for the
    /// colour space an ICC tag declares.
    /// Note <see cref="ToRgba8"/>'s code-value projection is only colour-correct
    /// for display-referred sRGB-ish content — check here before relying on it
    /// (a PQ/HLG-tagged raster needs a colour-managed conversion instead).
    /// </summary>
    ColorEncoding ColorEncoding => ColorEncoding.AssumedSrgb;

    /// <summary>
    /// The file's metadata blocks (Exif, XMP, ICC) for passing on to an encoder. Defaults to
    /// <see cref="ImageMetadata.Empty"/>.
    /// </summary>
    ImageMetadata Metadata => ImageMetadata.Empty;

    /// <summary>
    /// Down-converts to a freshly-allocated, tightly-packed 8-bit RGBA buffer
    /// (row-major, 4 bytes/pixel, R,G,B,A byte order). 16-bit samples are scaled
    /// by <c>&gt;&gt; 8</c>; gray expands across R/G/B; a missing alpha becomes 255.
    /// </summary>
    /// <exception cref="NotSupportedException">The samples are <see cref="SampleFormat.Float32"/>.</exception>
    byte[] ToRgba8();

    /// <summary>
    /// Converts to a freshly-allocated, tightly-packed 16-bit RGBA buffer (row-major, 4 samples/pixel, R,G,B,A).
    /// 8-bit samples are widened by <c>× 257</c>; gray expands across R/G/B; a missing alpha becomes 65535.
    /// </summary>
    /// <exception cref="NotSupportedException">The samples are <see cref="SampleFormat.Float32"/>.</exception>
    ushort[] ToRgba16()
    {
        if (SampleFormat == SampleFormat.Float32)
            throw new NotSupportedException("Float32 rasters have no integer projection without a tone/stretch policy; read Pixels or ToFloats.");
        var result = new ushort[checked(Width * Height * 4)];
        if (!TryCopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.AsSpan()), 0, PixelFormat.Rgba64))
            throw new InvalidOperationException("The image could not be converted to RGBA64.");
        return result;
    }

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

    /// <summary>
    /// Writes the image into <paramref name="destination"/> in <paramref name="format"/>, rows
    /// <paramref name="stride"/> bytes apart (0: tightly packed), converting by <see cref="PixelConverter"/>; bytes
    /// between rows are left untouched. The allocation-free exit for every layout.
    /// </summary>
    /// <returns>False when the destination does not fit (see <see cref="PixelConverter.TryGetLayout"/>) or the samples
    /// are <see cref="SampleFormat.Float32"/>.</returns>
    bool TryCopyTo(Span<byte> destination, int stride, PixelFormat format) =>
        PixelConverter.TryConvert(this, destination, stride, format);
}
