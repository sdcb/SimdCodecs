using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The <see cref="IDecodedImage"/> a gain-map ("Ultra HDR") JPEG surfaces through
/// the <c>Sdcb.SimdCodecs</c> facade. It is deliberately dual-natured, matching
/// what the format is for:
/// <list type="bullet">
/// <item><b>The float path is HDR.</b> <see cref="Pixels"/> / <see cref="ToFloats"/>
/// reconstruct the full authored HDR (linear, display-referred, headroom =
/// <see cref="GainMapMetadata.HdrCapacityMax"/>) — asking the facade for floats
/// "just works" as HDR.</item>
/// <item><b>The int path is SDR.</b> <see cref="ToRgba8"/> (and the facade's
/// zero-copy <c>TryDecodeIntoRgba8</c>) return the authored SDR base rendition —
/// the same image a gain-map-unaware viewer shows, i.e. the format's own graceful
/// fallback rather than a lossy tone-map of the HDR floats.</item>
/// </list>
/// HDR reconstruction is lazy: it happens on first <see cref="Pixels"/> /
/// <see cref="ToFloats"/> access, so a consumer that only wants the 8-bit image
/// never pays for it. For a display-adaptive headroom (rather than the full
/// authored HDR), reach through <see cref="Source"/> and call
/// <see cref="GainMapImage.ReconstructHdr"/> directly.
/// </summary>
public sealed class GainMapDecodedImage : IDecodedImage
{
    private readonly double _displayHeadroom;
    private RasterImage? _hdr;

    /// <param name="source">The decoded gain-map pair + metadata.</param>
    /// <param name="displayHeadroom">The linear display headroom to reconstruct the
    /// float path at (the facade uses <see cref="GainMapMetadata.HdrCapacityMax"/>,
    /// the full authored HDR).</param>
    /// <param name="primaries">The SDR base's colour primaries, which the reconstructed HDR
    /// shares: the gain map is applied in the base image's colour space. The facade reads them
    /// from the base's ICC profile (<see cref="GainMapImageDecoder.TryDecode"/>).</param>
    public GainMapDecodedImage(GainMapImage source, double displayHeadroom, ColorPrimaries primaries = ColorPrimaries.BT709)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeadroom);
        Source = source;
        _displayHeadroom = displayHeadroom;
        ColorEncoding = new() { Primaries = primaries, Transfer = TransferFunction.Linear, Float = FloatSemantics.DisplayReferred };
    }

    /// <summary>The underlying decoded gain-map pair — the escape hatch to the full
    /// gain-map API (e.g. reconstructing at a different display headroom).</summary>
    public GainMapImage Source { get; }

    // Reconstructed lazily and cached: the fidelity tier IS the HDR image, but a
    // caller that only takes the int (SDR) path must not trigger the work.
    private RasterImage Hdr => _hdr ??= Source.ReconstructHdr(_displayHeadroom);

    /// <inheritdoc />
    public int Width => Source.Base.Width;

    /// <inheritdoc />
    public int Height => Source.Base.Height;

    /// <summary>3 — the reconstructed HDR raster is linear RGB (the SDR fallback on
    /// the int path is expanded to RGBA there).</summary>
    public int Channels => 3;

    /// <inheritdoc />
    public SampleFormat SampleFormat => SampleFormat.Float32;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Pixels => Hdr.Pixels;

    /// <summary>Empty: reconstructed linear-light floats carry no ICC profile (the
    /// base's display-referred profile does not describe them).</summary>
    public ReadOnlySpan<byte> IccProfile => default;

    /// <summary>Linear, display-referred (1.0 = SDR white) — what
    /// <see cref="GainMapImage.ReconstructHdr"/> produces — in the base image's primaries.
    /// Stated without forcing reconstruction.</summary>
    public ColorEncoding ColorEncoding { get; }

    /// <summary>The full authored HDR as RGBA float32 (linear, display-referred).
    /// This is the "just works" float path.</summary>
    public float[] ToFloats() => Hdr.ToFloats();

    /// <summary>The authored SDR base rendition as 8-bit RGBA — the format's
    /// graceful fallback for the display/int path, not a tone-map of the HDR.</summary>
    public byte[] ToRgba8() => Source.Base.ToRgba8();
}
