namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>Pixel layout written by <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, JpegPixelFormat, JpegScale)"/>.</summary>
public enum JpegPixelFormat
{
    /// <summary>4 bytes per pixel: R, G, B, A (A = 255).</summary>
    Rgba32,

    /// <summary>4 bytes per pixel: B, G, R, A (A = 255) — the native layout of most Windows / Skia surfaces.</summary>
    Bgra32,

    /// <summary>3 bytes per pixel: R, G, B.</summary>
    Rgb24,
}
