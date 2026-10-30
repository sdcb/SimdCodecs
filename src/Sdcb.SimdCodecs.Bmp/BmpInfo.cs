namespace Sdcb.SimdCodecs.Bmp;

/// <summary>How a bitmap's pixels are stored: the header's compression field, read with its bit depth.</summary>
public enum BmpCompression
{
    /// <summary><c>BI_RGB</c>: uncompressed. 16-bit is 5-5-5, 32-bit is B, G, R and a byte that is alpha when any pixel sets it.</summary>
    Rgb = 0,

    /// <summary><c>BI_RLE8</c>: run-length coded 8-bit indices.</summary>
    Rle8 = 1,

    /// <summary><c>BI_RLE4</c>: run-length coded 4-bit indices.</summary>
    Rle4 = 2,

    /// <summary><c>BI_BITFIELDS</c>: uncompressed 16- or 32-bit pixels with red, green and blue masks (and an
    /// alpha mask in a V3, V4 or V5 header).</summary>
    Bitfields = 3,

    /// <summary><c>BI_JPEG</c>: the pixel data is a JPEG stream. Recognised, not decoded.</summary>
    Jpeg = 4,

    /// <summary><c>BI_PNG</c>: the pixel data is a PNG stream.</summary>
    Png = 5,

    /// <summary><c>BI_ALPHABITFIELDS</c>: <see cref="Bitfields"/> with an alpha mask after the colour masks.</summary>
    AlphaBitfields = 6,

    /// <summary>OS/2 <c>BCA_HUFFMAN1D</c> (compression 3 on a 1-bit bitmap): Modified Huffman run lengths,
    /// the one-dimensional code of ITU-T T.4.</summary>
    Huffman1D = 100,

    /// <summary>OS/2 <c>BCA_RLE24</c> (compression 4 on a 24-bit bitmap): run-length coded B, G, R triples.</summary>
    Rle24 = 101,
}

/// <summary>The bitmap header layout, told apart by its size field.</summary>
public enum BmpHeaderKind
{
    /// <summary><c>BITMAPCOREHEADER</c> (12 bytes, OS/2 1.x / Windows 2.x): 16-bit sizes, 3-byte palette entries.</summary>
    Core,

    /// <summary>OS/2 2.x <c>BITMAPINFOHEADER2</c> (16 to 64 bytes): the Windows fields it shares, then OS/2's own.</summary>
    Os2,

    /// <summary><c>BITMAPINFOHEADER</c> (40 bytes, Windows 3.x).</summary>
    Info,

    /// <summary>The 52-byte <c>BITMAPV2INFOHEADER</c>: <see cref="Info"/> with red, green and blue masks.</summary>
    InfoV2,

    /// <summary>The 56-byte <c>BITMAPV3INFOHEADER</c>: <see cref="InfoV2"/> with an alpha mask.</summary>
    InfoV3,

    /// <summary><c>BITMAPV4HEADER</c> (108 bytes): masks, colour space, endpoints and gamma.</summary>
    V4,

    /// <summary><c>BITMAPV5HEADER</c> (124 bytes, or longer): <see cref="V4"/> with an intent and an ICC profile.</summary>
    V5,
}

/// <summary>What a bitmap's headers say, read without decoding a pixel.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels: positive, whichever way the rows are stored (and, for an icon
/// image, the colour bitmap's height, half the header's).</param>
/// <param name="BitsPerPixel">The stored bits per pixel: 1, 2, 4, 8, 16, 24 or 32 (0 for <see cref="BmpCompression.Png"/>
/// and <see cref="BmpCompression.Jpeg"/>, whose streams carry their own).</param>
/// <param name="Compression">How the pixels are stored.</param>
/// <param name="HeaderKind">The bitmap header layout.</param>
/// <param name="TopDown">True when the first stored row is the top one (a negative header height).</param>
/// <param name="HasAlpha">True when the decoded image has an alpha channel: a 32-bit <see cref="BmpCompression.Rgb"/>
/// bitmap, a non-zero alpha mask, or an icon image (its AND mask).</param>
/// <param name="PaletteSize">The palette entries the bitmap carries for its indices (0 above 8 bits per pixel).</param>
public readonly record struct BmpInfo(int Width, int Height, int BitsPerPixel, BmpCompression Compression,
    BmpHeaderKind HeaderKind, bool TopDown, bool HasAlpha, int PaletteSize)
{
    /// <summary>Samples per decoded pixel: 4 with <see cref="HasAlpha"/>, otherwise 3.</summary>
    public int Channels => HasAlpha ? 4 : 3;
}

/// <summary>A decoded bitmap: tightly packed 8-bit RGB, or RGBA when <see cref="BmpInfo.HasAlpha"/>.</summary>
public sealed class BmpImage
{
    internal BmpImage(int width, int height, int channels, byte[] pixels, byte[]? iccProfile)
    {
        Width = width;
        Height = height;
        Channels = channels;
        Pixels = pixels;
        IccProfile = iccProfile;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>3 (RGB) or 4 (non-premultiplied RGBA).</summary>
    public int Channels { get; }

    /// <summary>The pixels, top row first, exactly <c>Width * Height * Channels</c> bytes.</summary>
    public byte[] Pixels { get; }

    /// <summary>The ICC profile a V5 header embeds, or null.</summary>
    public byte[]? IccProfile { get; }
}
