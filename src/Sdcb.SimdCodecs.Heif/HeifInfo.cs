using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// Metadata read from a HEIF container without decoding any HEVC payload.
/// </summary>
public sealed class HeifInfo
{
    /// <summary>Width of the primary image in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height of the primary image in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Bit depth of the HEVC-coded samples (8 or 10 for Main / Main 10).</summary>
    public int BitDepth { get; init; }

    /// <summary>
    /// Number of grid tiles the primary image is composed of, or 1 for a non-tiled image.
    /// </summary>
    public int TileCount { get; init; }

    /// <summary>
    /// ICC profile bytes embedded in the <c>colr</c> box, or <see langword="null"/> when absent.
    /// </summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>
    /// Orientation derived from the <c>irot</c> / <c>imir</c> transformative properties,
    /// expressed as an Exif orientation value (1–8).
    /// </summary>
    public ImageOrientation Orientation { get; init; }
}
