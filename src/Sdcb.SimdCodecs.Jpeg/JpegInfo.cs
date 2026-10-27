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
    /// Output dimensions when decoding at <paramref name="scale"/> —
    /// <c>ceil(dim / factor)</c>, the libjpeg scaled-decode convention.
    /// </summary>
    public (int Width, int Height) ScaledSize(JpegScale scale)
    {
        var s = (int)scale;
        return ((Width + s - 1) / s, (Height + s - 1) / s);
    }
}
