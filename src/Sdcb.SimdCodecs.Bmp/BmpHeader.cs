using System.Buffers.Binary;
using System.Numerics;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// A bitmap's headers, parsed and checked: everything the pixel decoders need, with every offset proved to
/// lie inside the data. Built from the structures Microsoft documents for the Windows DIB
/// (<c>BITMAPFILEHEADER</c>, <c>BITMAPCOREHEADER</c>, <c>BITMAPINFOHEADER</c>, <c>BITMAPV4HEADER</c>,
/// <c>BITMAPV5HEADER</c>) and IBM for OS/2 2.x (<c>BITMAPINFOHEADER2</c>, <c>BITMAPARRAYFILEHEADER2</c>).
/// </summary>
internal readonly struct BmpHeader
{
    /// <summary>The largest image decoded, in pixels: 2^28, a 1 GiB RGBA raster.</summary>
    public const long MaximumPixels = 1L << 28;

    private const uint ProfileEmbedded = 0x4D424544; // 'MBED', LCS_PROFILE_EMBEDDED

    public int Width { get; private init; }
    public int Height { get; private init; }
    public bool TopDown { get; private init; }
    public int BitsPerPixel { get; private init; }
    public BmpCompression Compression { get; private init; }
    public BmpHeaderKind Kind { get; private init; }

    /// <summary>Channel masks for 16- and 32-bit pixels (0: the channel is absent).</summary>
    public uint RedMask { get; private init; }
    public uint GreenMask { get; private init; }
    public uint BlueMask { get; private init; }
    public uint AlphaMask { get; private init; }

    /// <summary>
    /// A 32-bit <see cref="BmpCompression.Rgb"/> bitmap: its fourth byte is alpha unless every pixel's is 0,
    /// when the bitmap is opaque (the byte was "reserved", as older writers leave it).
    /// </summary>
    public bool AlphaUnlessAllZero { get; private init; }

    /// <summary>Palette: offset into the data, entries, and bytes per entry (3 for a core header, else 4).</summary>
    public int PaletteOffset { get; private init; }
    public int PaletteCount { get; private init; }
    public int PaletteEntrySize { get; private init; }

    /// <summary>The pixel data: offset into the data and its length (to the end of the data).</summary>
    public int PixelOffset { get; private init; }
    public int PixelLength { get; private init; }

    /// <summary>An icon image: the AND mask's offset, or -1 when it is missing (then every pixel is opaque).</summary>
    public bool IconMask { get; private init; }
    public int AndMaskOffset { get; private init; }

    /// <summary>An embedded ICC profile (V5, <c>PROFILE_EMBEDDED</c>): offset and length, or 0 length for none.</summary>
    public int IccOffset { get; private init; }
    public int IccLength { get; private init; }

    /// <summary>Bytes per stored row of an uncompressed bitmap: whole 32-bit words.</summary>
    public int Stride => (int)(((long)Width * BitsPerPixel + 31) / 32 * 4);

    public bool HasAlpha => AlphaMask != 0 || IconMask;

    public BmpInfo ToInfo() => new(Width, Height, BitsPerPixel, Compression, Kind, TopDown, HasAlpha,
        BitsPerPixel <= 8 && Compression is not (BmpCompression.Png or BmpCompression.Jpeg) ? PaletteCount : 0);

    /// <summary>True when <paramref name="header"/> starts like a bitmap file: <c>BM</c> or an OS/2 <c>BA</c>
    /// array, then a header size this reader knows. Needs 18 bytes.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> header)
    {
        if (header.Length < 18) return false;
        if (header[0] == 'B' && header[1] == 'M') return IsHeaderSize(BinaryPrimitives.ReadUInt32LittleEndian(header[14..]));
        // An OS/2 bitmap array: its first entry's own file header follows at 14.
        return header[0] == 'B' && header[1] == 'A' && header[14] == 'B' && header[15] == 'M';
    }

    // Core (12); OS/2 2.x, cut after any of its fields (16 to 40 in 4-byte steps, then 42, 44, 46, 48, 52, 56,
    // 60 and 64), which also covers the 40-, 52- and 56-byte Windows headers; V4 (108); V5 (124, or longer).
    private static bool IsHeaderSize(uint size) =>
        size is 12 or 42 or 46 or 108 or >= 124 and <= 4096 || (size is >= 16 and <= 64 && size % 4 == 0);

    /// <summary>Parses a bitmap file: a <c>BITMAPFILEHEADER</c> and the DIB after it, or the first bitmap of an
    /// OS/2 bitmap array.</summary>
    /// <exception cref="InvalidDataException">The data is not a bitmap this reader decodes.</exception>
    public static BmpHeader ReadFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18 || data[0] != 'B')
            throw new InvalidDataException("Not a BMP file: no 'BM' signature.");
        var fileHeader = 0;
        if (data[1] == 'A')
        {
            // BITMAPARRAYFILEHEADER2: type, cbSize, offNext, cxDisplay, cyDisplay (14 bytes), then the first
            // bitmap's BITMAPFILEHEADER2, whose offBits counts from the start of the file. The entries are one
            // image for several displays; the first is the one decoded.
            fileHeader = 14;
            if (data.Length < fileHeader + 18 || data[fileHeader] != 'B' || data[fileHeader + 1] != 'M')
                throw new InvalidDataException("An OS/2 bitmap array whose first entry is not a bitmap ('BM').");
        }
        else if (data[1] != 'M')
        {
            throw new InvalidDataException("Not a BMP file: no 'BM' signature.");
        }
        var offBits = BinaryPrimitives.ReadUInt32LittleEndian(data[(fileHeader + 10)..]);
        return Parse(data, fileHeader + 14, offBits, iconMask: false);
    }

    /// <summary>Parses a packed DIB: a bitmap header, its masks and palette, then the pixels, as the clipboard
    /// (<c>CF_DIB</c>, <c>CF_DIBV5</c>) and icon resources store it.</summary>
    /// <param name="iconMask">An icon image: the header's height counts the colour bitmap and the 1-bit AND
    /// mask after it, which says which pixels are transparent.</param>
    /// <exception cref="InvalidDataException">The data is not a DIB this reader decodes.</exception>
    public static BmpHeader ReadDib(ReadOnlySpan<byte> data, bool iconMask) => Parse(data, 0, null, iconMask);

    private static BmpHeader Parse(ReadOnlySpan<byte> data, int dib, uint? offBits, bool iconMask)
    {
        if (data.Length - dib < 4) throw Truncated();
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[dib..]);
        if (!IsHeaderSize(size)) throw new InvalidDataException($"Unknown bitmap header size {size}.");
        if (data.Length - dib < size) throw Truncated();
        var h = data.Slice(dib, (int)size);

        BmpHeaderKind kind;
        long width, height;
        int bitCount;
        uint compression = 0, colorsUsed = 0;
        if (size == 12)
        {
            kind = BmpHeaderKind.Core;
            width = BinaryPrimitives.ReadUInt16LittleEndian(h[4..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(h[10..]);
        }
        else
        {
            kind = size switch
            {
                40 => BmpHeaderKind.Info,
                52 => BmpHeaderKind.InfoV2,
                56 => BmpHeaderKind.InfoV3,
                108 => BmpHeaderKind.V4,
                >= 124 => BmpHeaderKind.V5,
                _ => BmpHeaderKind.Os2,
            };
            // An OS/2 2.x header may stop after any field; the fields it leaves out are 0.
            width = BinaryPrimitives.ReadInt32LittleEndian(h[4..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(h[8..]);
            bitCount = BinaryPrimitives.ReadUInt16LittleEndian(h[14..]);
            if (size >= 20) compression = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
            if (size >= 36) colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(h[32..]);
        }

        var topDown = height < 0;
        height = Math.Abs(height);
        if (iconMask) height /= 2; // the colour bitmap, then the AND mask, each this tall
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Bitmap size {width}x{height} is empty.");
        if (width * height > MaximumPixels)
            throw new InvalidDataException($"Bitmap size {width}x{height} is above the limit of {MaximumPixels:N0} pixels.");

        var format = Classify(compression, bitCount, kind);
        if (format == BmpCompression.Jpeg)
            throw new InvalidDataException("BI_JPEG bitmaps (a JPEG stream inside a BMP) are not decoded.");
        if (topDown && format is BmpCompression.Rle4 or BmpCompression.Rle8 or BmpCompression.Rle24 or BmpCompression.Huffman1D)
            throw new InvalidDataException("A compressed bitmap cannot be top-down.");
        if (iconMask && format is not (BmpCompression.Rgb or BmpCompression.Bitfields or BmpCompression.AlphaBitfields))
            throw new InvalidDataException($"An icon image must be uncompressed, not {format}.");

        // Masks: inside a V2..V5 header, after a 40-byte (or OS/2) one.
        var position = (long)dib + size;
        uint red = 0, green = 0, blue = 0, alpha = 0;
        var alphaUnlessAllZero = false;
        if (format is BmpCompression.Bitfields or BmpCompression.AlphaBitfields)
        {
            ReadOnlySpan<byte> masks;
            if (size >= 52)
            {
                masks = h[40..];
            }
            else
            {
                var count = format == BmpCompression.AlphaBitfields ? 16 : 12;
                if (data.Length - position < count) throw Truncated();
                masks = data.Slice((int)position, count);
                position += count;
            }
            red = BinaryPrimitives.ReadUInt32LittleEndian(masks);
            green = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]);
            blue = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]);
            if (masks.Length >= 16 && (format == BmpCompression.AlphaBitfields || size >= 56))
                alpha = BinaryPrimitives.ReadUInt32LittleEndian(masks[12..]);
            CheckMask(red, bitCount);
            CheckMask(green, bitCount);
            CheckMask(blue, bitCount);
            CheckMask(alpha, bitCount);
        }
        else if (format == BmpCompression.Rgb && bitCount == 16)
        {
            (red, green, blue) = (0x7C00, 0x03E0, 0x001F);
        }
        else if (format == BmpCompression.Rgb && bitCount == 32)
        {
            (red, green, blue, alpha) = (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000);
            alphaUnlessAllZero = true;
        }

        // The palette: the entries the header asks for (all of them when it says 0), never more than the
        // indices can reach, and never into the pixel data.
        var entrySize = kind == BmpHeaderKind.Core ? 3 : 4;
        var paletteOffset = position;
        long paletteCount = 0, paletteSpan = 0;
        if (bitCount <= 8 && format is not (BmpCompression.Png or BmpCompression.Jpeg))
        {
            var full = 1L << bitCount;
            paletteSpan = colorsUsed == 0 || colorsUsed > full ? full : colorsUsed;
            paletteCount = paletteSpan;
        }
        else if (format is not (BmpCompression.Png or BmpCompression.Jpeg))
        {
            // A colour table on a direct-colour bitmap is an optional optimisation hint, skipped.
            paletteSpan = Math.Min(colorsUsed, 1u << 16);
        }

        long pixels;
        if (offBits is { } bits && bits >= paletteOffset && bits <= data.Length)
        {
            pixels = bits;
            paletteCount = Math.Min(paletteCount, (pixels - paletteOffset) / entrySize);
        }
        else
        {
            // A packed DIB, or a file whose offset points into its own headers (or past its end): the pixels
            // follow the palette.
            pixels = paletteOffset + paletteSpan * entrySize;
            paletteCount = Math.Min(paletteCount, Math.Max(0, data.Length - paletteOffset) / entrySize);
            if (pixels > data.Length) pixels = data.Length;
        }

        var header = new BmpHeader
        {
            Width = (int)width,
            Height = (int)height,
            TopDown = topDown,
            BitsPerPixel = bitCount,
            Compression = format,
            Kind = kind,
            RedMask = red,
            GreenMask = green,
            BlueMask = blue,
            AlphaMask = alpha,
            AlphaUnlessAllZero = alphaUnlessAllZero,
            PaletteOffset = (int)paletteOffset,
            PaletteCount = (int)paletteCount,
            PaletteEntrySize = entrySize,
            PixelOffset = (int)pixels,
            PixelLength = data.Length - (int)pixels,
            IconMask = iconMask,
            AndMaskOffset = -1,
        };

        if (format is BmpCompression.Rgb or BmpCompression.Bitfields or BmpCompression.AlphaBitfields)
        {
            // Every row must be there; the last may stop short of its padding, which some writers leave out.
            var stride = (long)header.Stride;
            var needed = stride * (height - 1) + (width * bitCount + 7) / 8;
            if (header.PixelLength < needed)
                throw new InvalidDataException($"The pixel data is truncated: {header.PixelLength} of {needed} bytes.");
            if (iconMask)
            {
                // The AND mask follows the colour bitmap: 1 bit per pixel, rows in whole 32-bit words.
                var maskStride = (width + 31) / 32 * 4;
                var maskOffset = pixels + stride * height;
                var maskNeeded = maskStride * (height - 1) + (width + 7) / 8;
                header = header.WithAndMask(maskOffset + maskNeeded <= data.Length ? (int)maskOffset : -1);
            }
        }

        if (kind == BmpHeaderKind.V5 && BinaryPrimitives.ReadUInt32LittleEndian(h[56..]) == ProfileEmbedded)
        {
            // bV5ProfileData counts from the start of the header. A profile that does not fit is ignored: it is
            // metadata, and the pixels are still good.
            var offset = dib + (long)BinaryPrimitives.ReadUInt32LittleEndian(h[112..]);
            var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(h[116..]);
            if (length > 0 && offset >= dib + size && offset + length <= data.Length)
                header = header.WithIcc((int)offset, (int)length);
        }
        return header;
    }

    private BmpHeader WithAndMask(int offset) => this with { AndMaskOffset = offset };

    private BmpHeader WithIcc(int offset, int length) => this with { IccOffset = offset, IccLength = length };

    // The compression field means different things on different depths: 3 is BI_BITFIELDS on 16 and 32 bits
    // but OS/2's Huffman 1D on 1 bit, and 4 is BI_JPEG (depth 0) but OS/2's RLE24 on 24 bits.
    private static BmpCompression Classify(uint compression, int bitCount, BmpHeaderKind kind)
    {
        var format = (compression, bitCount) switch
        {
            (0, 1 or 2 or 4 or 8 or 16 or 24 or 32) => BmpCompression.Rgb,
            (1, 8) => BmpCompression.Rle8,
            (2, 4) => BmpCompression.Rle4,
            (3, 16 or 32) => BmpCompression.Bitfields,
            (3, 1) => BmpCompression.Huffman1D,
            (4, 24) => BmpCompression.Rle24,
            (4, _) => BmpCompression.Jpeg,
            (5, _) => BmpCompression.Png,
            (6, 16 or 32) => BmpCompression.AlphaBitfields,
            _ => throw new InvalidDataException(bitCount == 64
                ? "64-bit bitmaps are not decoded."
                : $"Compression {compression} with {bitCount} bits per pixel is not a bitmap this reader decodes."),
        };
        if (kind == BmpHeaderKind.Core && format != BmpCompression.Rgb)
            throw new InvalidDataException("A core (OS/2 1.x) header has no compression field.");
        if (kind == BmpHeaderKind.Core && bitCount is 2 or 16 or 32)
            throw new InvalidDataException($"A core (OS/2 1.x) header cannot hold {bitCount} bits per pixel.");
        return format;
    }

    // A mask is one run of set bits inside the pixel; zero leaves the channel out.
    private static void CheckMask(uint mask, int bitCount)
    {
        if (mask == 0) return;
        if (bitCount < 32 && mask >> bitCount != 0)
            throw new InvalidDataException($"Channel mask 0x{mask:X8} does not fit a {bitCount}-bit pixel.");
        var shifted = mask >> BitOperations.TrailingZeroCount(mask);
        if ((shifted & (shifted + 1)) != 0)
            throw new InvalidDataException($"Channel mask 0x{mask:X8} is not one run of bits.");
    }

    private static InvalidDataException Truncated() => new("The bitmap headers are truncated.");
}
