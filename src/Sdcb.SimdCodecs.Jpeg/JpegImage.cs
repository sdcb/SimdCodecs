namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// A decoded JPEG raster: tightly-packed 8-bit RGBA rows, top-down
/// (stride = <c>Width * 4</c>). For allocation-free decoding into a pooled or
/// reused buffer, use <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, JpegScale)"/> instead.
/// </summary>
public sealed class JpegImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA pixel data, exactly <c>Width * Height * 4</c> bytes.</summary>
    public byte[] Pixels { get; }

    internal JpegImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}
