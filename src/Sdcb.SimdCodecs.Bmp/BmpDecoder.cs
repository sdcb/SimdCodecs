using System.Numerics;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// Pure-managed BMP decoder, derived from Microsoft's documentation of the Windows DIB and IBM's of the OS/2
/// bitmap. It reads every header (core, OS/2 2.x, <c>BITMAPINFOHEADER</c>, V2 to V5), every depth (1, 2, 4,
/// 8, 16, 24, 32 bits), uncompressed rows either way up, channel masks (<c>BI_BITFIELDS</c>,
/// <c>BI_ALPHABITFIELDS</c>), <c>BI_RLE4</c> / <c>BI_RLE8</c> with deltas, OS/2 RLE24 and Huffman 1D,
/// <c>BI_PNG</c>, a V5 header's embedded ICC profile, and the first bitmap of an OS/2 bitmap array.
/// <c>BI_JPEG</c> and 64-bit bitmaps are refused. The decisions the documentation leaves open are listed in
/// the package README.
/// <para>
/// The <c>Dib</c> methods read a packed DIB, the same structures without the 14-byte file header, as the
/// clipboard (<c>CF_DIB</c>, <c>CF_DIBV5</c>) and icon resources hold them; an icon image's AND mask becomes
/// alpha.
/// </para>
/// </summary>
public static class BmpDecoder
{
    /// <summary>Reads the size, depth, compression and layout from the headers.</summary>
    /// <exception cref="InvalidDataException">The data is not a bitmap this decoder reads, or is truncated.</exception>
    public static BmpInfo ReadInfo(ReadOnlySpan<byte> data) => Info(data, BmpHeader.ReadFile(data));

    /// <summary><see cref="ReadInfo"/> that returns false instead of throwing.</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out BmpInfo info)
    {
        try
        {
            info = ReadInfo(data);
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    /// <summary>Decodes to a new image: RGB, or RGBA when <see cref="BmpInfo.HasAlpha"/>.</summary>
    /// <exception cref="InvalidDataException">The data is not a decodable bitmap.</exception>
    public static BmpImage Decode(ReadOnlySpan<byte> data) => ToImage(data, BmpHeader.ReadFile(data));

    /// <summary>
    /// Decodes into <paramref name="destination"/> as <paramref name="format"/>, top row first, rows
    /// <paramref name="stride"/> bytes apart (0: tightly packed). Size it with <see cref="ReadInfo"/>. Bytes
    /// between rows are left untouched.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a decodable bitmap.</exception>
    /// <exception cref="ArgumentException">The destination is too small, or the stride shorter than a row.</exception>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> destination, ImagePixelFormat format = ImagePixelFormat.Rgba32, int stride = 0) =>
        Into(data, BmpHeader.ReadFile(data), destination, format, stride);

    /// <summary><see cref="ReadInfo"/> for a packed DIB (no file header).</summary>
    /// <param name="dib">The bitmap header, masks, palette and pixels.</param>
    /// <param name="iconImage">An icon or cursor image: the header's height counts the colour bitmap and the
    /// 1-bit AND mask after it, and the result has alpha.</param>
    /// <exception cref="InvalidDataException">The data is not a DIB this decoder reads, or is truncated.</exception>
    public static BmpInfo ReadDibInfo(ReadOnlySpan<byte> dib, bool iconImage = false) => Info(dib, BmpHeader.ReadDib(dib, iconImage));

    /// <summary><see cref="Decode"/> for a packed DIB (no file header).</summary>
    /// <param name="dib">The bitmap header, masks, palette and pixels.</param>
    /// <param name="iconImage">An icon or cursor image: see <see cref="ReadDibInfo"/>. A 32-bit image's alpha
    /// is used when any pixel's is non-zero, the AND mask otherwise.</param>
    /// <exception cref="InvalidDataException">The data is not a decodable DIB.</exception>
    public static BmpImage DecodeDib(ReadOnlySpan<byte> dib, bool iconImage = false) => ToImage(dib, BmpHeader.ReadDib(dib, iconImage));

    /// <summary><see cref="DecodeTo"/> for a packed DIB (no file header).</summary>
    /// <param name="dib">The bitmap header, masks, palette and pixels.</param>
    /// <param name="destination">The output, sized with <see cref="ReadDibInfo"/>.</param>
    /// <param name="format">The output layout.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="iconImage">An icon or cursor image: see <see cref="DecodeDib"/>.</param>
    /// <exception cref="InvalidDataException">The data is not a decodable DIB.</exception>
    /// <exception cref="ArgumentException">The destination is too small, or the stride shorter than a row.</exception>
    public static void DecodeDibTo(ReadOnlySpan<byte> dib, Span<byte> destination, ImagePixelFormat format = ImagePixelFormat.Rgba32,
        int stride = 0, bool iconImage = false) =>
        Into(dib, BmpHeader.ReadDib(dib, iconImage), destination, format, stride);

    internal static BmpInfo Info(ReadOnlySpan<byte> data, BmpHeader header)
    {
        var info = header.ToInfo();
        if (header.Compression == BmpCompression.Png)
            info = info with { HasAlpha = BmpPixels.ReadPngInfo(data, header).Channels is 2 or 4 };
        return info;
    }

    internal static BmpImage ToImage(ReadOnlySpan<byte> data, BmpHeader header)
    {
        var info = Info(data, header);
        var channels = info.Channels;
        var pixels = new byte[(long)header.Width * header.Height * channels];
        BmpPixels.Decode(data, header, pixels, header.Width * channels, channels == 4 ? ImagePixelFormat.Rgba32 : ImagePixelFormat.Rgb24);
        return new BmpImage(header.Width, header.Height, channels, pixels, Icc(data, header));
    }

    internal static byte[]? Icc(ReadOnlySpan<byte> data, BmpHeader header) =>
        header.IccLength > 0 ? data.Slice(header.IccOffset, header.IccLength).ToArray() : null;

    private static void Into(ReadOnlySpan<byte> data, BmpHeader header, Span<byte> destination, ImagePixelFormat format, int stride)
    {
        if (!PixelConverter.TryGetLayout(header.Width, header.Height, format, stride, destination.Length, out var rowStride))
            throw new ArgumentException($"The destination does not hold a {header.Width}x{header.Height} {format} image with stride {stride}.", nameof(destination));
        BmpPixels.Decode(data, header, destination, rowStride, format);
    }

    /// <summary>The bits of precision per channel, at most 8: a palette's entries are 8-bit.</summary>
    internal static int BitDepth(BmpHeader header)
    {
        if (header.BitsPerPixel is <= 8 or 24 || header.Compression is BmpCompression.Png or BmpCompression.Rle24) return 8;
        var widest = Math.Max(Math.Max(BitOperations.PopCount(header.RedMask), BitOperations.PopCount(header.GreenMask)),
            Math.Max(BitOperations.PopCount(header.BlueMask), BitOperations.PopCount(header.AlphaMask)));
        return Math.Clamp(widest, 1, 8);
    }
}
