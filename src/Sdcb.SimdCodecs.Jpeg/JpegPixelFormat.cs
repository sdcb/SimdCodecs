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

    /// <summary>3 bytes per pixel: B, G, R — OpenCV's default.</summary>
    Bgr24,

    /// <summary>
    /// 1 byte per pixel: the image's luma as the stream stores it. A greyscale JPEG writes its samples
    /// and a YCbCr JPEG its Y plane (JFIF's Y is the BT.601 luma of the encoder's RGB), without
    /// inverse-transforming, upsampling or colour-converting the chroma — as libjpeg-turbo's and
    /// OpenCV's grey decodes do. Y can differ by a code value or two from the luma of the decoded RGB,
    /// which carries the rounding of the colour conversion. An RGB-coded, CMYK or YCCK JPEG has no luma
    /// plane; it writes the BT.601 luma of its RGB output, <c>(77·R + 150·G + 29·B + 128) &gt;&gt; 8</c>.
    /// </summary>
    Gray8,
}
