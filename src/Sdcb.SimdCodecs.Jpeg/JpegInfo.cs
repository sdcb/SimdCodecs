using System;
using System.Collections.Generic;
using System.Linq;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Header-level facts about a JPEG stream, read without decoding any entropy data.
/// <paramref name="Components"/> is the actual SOF component count (1 = grayscale,
/// 3 = YCbCr/RGB, 4 = Adobe CMYK/YCCK) — decoded output is always RGBA regardless.
/// </summary>
public sealed record JpegInfo(int Width, int Height, int Components, bool Progressive)
{
    /// <summary>
    /// True when any component is stored at lower resolution than the frame
    /// (chroma subsampling — 4:2:0 / 4:2:2 / etc.). Relevant for scaled decode:
    /// at <see cref="JpegScale.Eighth"/> a subsampled chroma sample already covers
    /// a 16×16 source footprint, so quality-sensitive callers may want to cap the
    /// scale for such streams.
    /// </summary>
    public bool ChromaSubsampled { get; init; }

    /// <summary>
    /// Every frame component in SOF order, with its sampling factors. Empty only on an instance built
    /// without them.
    /// </summary>
    public IReadOnlyList<JpegComponentInfo> ComponentInfo { get; init; } = [];

    /// <summary>
    /// The J:a:b notation of a three-component frame whose two chroma components share their
    /// sampling factors and divide the luma's: <c>"4:4:4"</c>, <c>"4:2:2"</c>, <c>"4:2:0"</c>,
    /// <c>"4:4:0"</c>, <c>"4:1:1"</c> or <c>"4:1:0"</c>. Null for a greyscale or four-component
    /// frame and for any other layout; <see cref="ComponentInfo"/> has the factors either way.
    /// </summary>
    public string? ChromaSubsamplingRatio
    {
        get
        {
            if (ComponentInfo.Count != 3) return null;
            var (y, cb, cr) = (ComponentInfo[0], ComponentInfo[1], ComponentInfo[2]);
            if (cb.HorizontalSampling != cr.HorizontalSampling || cb.VerticalSampling != cr.VerticalSampling
                || y.HorizontalSampling % cb.HorizontalSampling != 0 || y.VerticalSampling % cb.VerticalSampling != 0)
                return null;
            return (y.HorizontalSampling / cb.HorizontalSampling, y.VerticalSampling / cb.VerticalSampling) switch
            {
                (1, 1) => "4:4:4",
                (2, 1) => "4:2:2",
                (2, 2) => "4:2:0",
                (1, 2) => "4:4:0",
                (4, 1) => "4:1:1",
                (4, 2) => "4:1:0",
                _ => null,
            };
        }
    }

    /// <summary>
    /// The EXIF orientation from the first APP1 <c>Exif</c> segment, <see cref="ImageOrientation.Normal"/>
    /// when there is none. The decoder does not apply it: pixels come out as stored.
    /// </summary>
    public ImageOrientation Orientation { get; init; } = ImageOrientation.Normal;

    /// <summary>Compares every property, <see cref="ComponentInfo"/> element by element.</summary>
    public bool Equals(JpegInfo? other) =>
        other is not null && Width == other.Width && Height == other.Height && Components == other.Components
        && Progressive == other.Progressive && ChromaSubsampled == other.ChromaSubsampled
        && Orientation == other.Orientation && ComponentInfo.SequenceEqual(other.ComponentInfo);

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Width, Height, Components, Progressive, ChromaSubsampled, Orientation, ComponentInfo.Count);

    /// <summary>
    /// Output dimensions when decoding at <paramref name="scale"/> —
    /// <c>ceil(dim / factor)</c>, the libjpeg scaled-decode convention.
    /// </summary>
    public (int Width, int Height) ScaledSize(JpegScale scale)
    {
        var s = (int)scale;
        return ((Width + s - 1) / s, (Height + s - 1) / s);
    }
}
