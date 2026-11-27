using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// Metadata read from an AVIF container without decoding any AV1 payload.
/// </summary>
public sealed class AvifInfo
{
    /// <summary>Width of the primary image in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height of the primary image in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Bit depth of the AV1-coded samples (8, 10 or 12).</summary>
    public int BitDepth { get; init; }

    /// <summary>Whether the image declares an alpha plane (<c>auxl</c> item or alpha track).</summary>
    public bool HasAlpha { get; init; }

    /// <summary>Number of frames in an image sequence (<c>avis</c>), or 1 for a still image.</summary>
    public int FrameCount { get; init; } = 1;

    /// <summary>Number of grid tiles the primary image is composed of, or 1 for a non-tiled image.</summary>
    public int TileCount { get; init; } = 1;

    /// <summary>ICC profile bytes embedded in the <c>colr</c> box, or <see langword="null"/> when absent.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Orientation derived from the <c>irot</c> / <c>imir</c> transformative properties, expressed as an Exif orientation value (1–8).</summary>
    public ImageOrientation Orientation { get; init; }
}
