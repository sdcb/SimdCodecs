namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// What a <see cref="SampleFormat.Float32"/> raster's sample values are referenced
/// to — the piece of meaning H.273 codepoints alone don't carry for float pixels
/// (a linear transfer says the curve, not what <c>1.0f</c> means). Integer rasters
/// use <see cref="NotApplicable"/>.
/// </summary>
public enum FloatSemantics : byte
{
    /// <summary>Integer raster — samples span the code range, not a float scale.</summary>
    NotApplicable = 0,

    /// <summary><c>1.0f</c> means display / diffuse white (e.g. scRGB, JXR float
    /// formats). Values above 1.0 are HDR highlights; below 0, wide-gamut chroma.</summary>
    DisplayReferred,

    /// <summary>Open-ended linear light with no fixed white point (e.g. OpenEXR
    /// scene captures, astro masters). Any display projection needs a
    /// consumer-chosen tone/stretch policy.</summary>
    SceneReferred,
}

/// <summary>
/// How a decoded raster's sample values encode colour — the meaning tier that sits
/// beside the container tier (<see cref="IDecodedImage.Channels"/> /
/// <see cref="IDecodedImage.SampleFormat"/>). Uses the ITU-T H.273 vocabulary
/// (<see cref="ColorPrimaries"/> / <see cref="TransferFunction"/>) shared with
/// PNG-3 cICP signalling and ICC v4.4 <c>cicp</c> tags. Like
/// <see cref="IDecodedImage.IccProfile"/> this is informational — it never rescales
/// pixels; it tells a consumer what transform the samples are already in.
/// <para>
/// There is deliberately no matrix-coefficients slot:
/// <see cref="IDecodedImage.Pixels"/> is RGB/grey by contract (codecs undo any
/// YCbCr before handing samples over, and PNG-3 / ICC cicp require
/// <see cref="MatrixCoefficients.Identity"/>), so the matrix is always Identity here.
/// </para>
/// </summary>
public sealed record ColorEncoding
{
    /// <summary>H.273 §8.1 colour primaries. Default: BT.709 / sRGB.</summary>
    public ColorPrimaries Primaries { get; init; } = ColorPrimaries.BT709;

    /// <summary>H.273 §8.2 transfer characteristics. Default: sRGB.</summary>
    public TransferFunction Transfer { get; init; } = TransferFunction.Srgb;

    /// <summary>Whether samples span the full code range 0..2^N-1 (PNG / JPEG:
    /// true). False is video-style limited range.</summary>
    public bool FullRange { get; init; } = true;

    /// <summary>Float-raster reference (display- vs scene-referred);
    /// <see cref="FloatSemantics.NotApplicable"/> for integer rasters.</summary>
    public FloatSemantics Float { get; init; } = FloatSemantics.NotApplicable;

    /// <summary>
    /// Display-referred sRGB — the assumption every consumer of the pre-3.5
    /// contract already baked in, and the correct reading for untagged
    /// display-referred content. This is the <see cref="IDecodedImage.ColorEncoding"/>
    /// default, so absence-of-signalling and explicit-sRGB read identically.
    /// </summary>
    public static ColorEncoding AssumedSrgb { get; } = new();
}
