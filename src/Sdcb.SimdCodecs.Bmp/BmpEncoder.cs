using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>The pixel layout <see cref="BmpEncoder"/> writes.</summary>
public enum BmpEncodeFormat
{
    /// <summary>32-bit with alpha when the input has an alpha channel that is not all 255, an 8-bit grey palette
    /// for grey input, 24-bit otherwise. An icon image is always 32-bit with alpha.</summary>
    Auto,

    /// <summary>1-bit palette (2 colours).</summary>
    Indexed1,

    /// <summary>4-bit palette (16 colours), <c>BI_RLE4</c> with <see cref="BmpEncodeOptions.Rle"/>.</summary>
    Indexed4,

    /// <summary>8-bit palette (256 colours), <c>BI_RLE8</c> with <see cref="BmpEncodeOptions.Rle"/>.</summary>
    Indexed8,

    /// <summary>16-bit x-5-5-5 (<c>BI_RGB</c>).</summary>
    Rgb555,

    /// <summary>16-bit 5-6-5 (<c>BI_BITFIELDS</c>).</summary>
    Rgb565,

    /// <summary>24-bit B, G, R.</summary>
    Rgb24,

    /// <summary>32-bit B, G, R, 255, opaque (<c>BI_RGB</c>).</summary>
    Rgb32,

    /// <summary>32-bit B, G, R, A: <c>BI_BITFIELDS</c> with an alpha mask in a V4 / V5 header, or <c>BI_RGB</c>
    /// with alpha in the fourth byte under a 40-byte header (as icons and most writers store it).</summary>
    Argb32,
}

/// <summary>The bitmap header <see cref="BmpEncoder"/> writes.</summary>
public enum BmpHeaderVersion
{
    /// <summary>V5 with an ICC profile, V4 for 32-bit alpha, <see cref="Info"/> otherwise (and always for an icon image).</summary>
    Auto,

    /// <summary><c>BITMAPCOREHEADER</c> (OS/2 1.x): palettes and 24-bit only, uncompressed, bottom-up, at most 65535 pixels a side.</summary>
    Core,

    /// <summary><c>BITMAPINFOHEADER</c> (40 bytes), the most widely read.</summary>
    Info,

    /// <summary><c>BITMAPV4HEADER</c> (108 bytes), tagged sRGB.</summary>
    V4,

    /// <summary><c>BITMAPV5HEADER</c> (124 bytes), tagged sRGB or carrying <see cref="BmpEncodeOptions.IccProfile"/>.</summary>
    V5,
}

/// <summary>Options for <see cref="BmpEncoder"/>.</summary>
public sealed record BmpEncodeOptions
{
    /// <summary>The pixel layout. A palette layout is exact when the colours fit it, otherwise built by Wu's
    /// quantizer (with <see cref="Dither"/>).</summary>
    public BmpEncodeFormat Format { get; init; }

    /// <summary>Run-length code a 4- or 8-bit palette (<c>BI_RLE4</c> / <c>BI_RLE8</c>). Not with
    /// <see cref="TopDown"/>, a core header or an icon image.</summary>
    public bool Rle { get; init; }

    /// <summary>The header version.</summary>
    public BmpHeaderVersion Header { get; init; }

    /// <summary>Store the top row first (a negative height). Not with <see cref="Rle"/> or a core header.</summary>
    public bool TopDown { get; init; }

    /// <summary>Floyd–Steinberg error diffusion when a palette has to be quantized.</summary>
    public bool Dither { get; init; }

    /// <summary>An ICC profile to embed; it needs a V5 header (chosen by <see cref="BmpHeaderVersion.Auto"/>).</summary>
    public ReadOnlyMemory<byte> IccProfile { get; init; }

    /// <summary>Horizontal resolution in pixels per metre (2835 is 72 dpi).</summary>
    public int PixelsPerMeterX { get; init; } = 2835;

    /// <summary>Vertical resolution in pixels per metre.</summary>
    public int PixelsPerMeterY { get; init; } = 2835;
}

/// <summary>
/// Pure-managed BMP encoder: 1/4/8-bit palettes (optionally RLE4 / RLE8), 16-bit 5-5-5 and 5-6-5, 24-bit,
/// and 32-bit with or without alpha, under a core, 40-byte, V4 or V5 header, bottom-up or top-down.
/// Input is any <see cref="ImagePixelFormat"/>, or 1 / 3 / 4 channels (grey, RGB, RGBA); B, G, R input
/// written as 24- or 32-bit is copied, not converted.
/// <para>
/// <see cref="EncodeDib(ReadOnlySpan{byte}, int, int, ImagePixelFormat, BmpEncodeOptions?, int, bool)"/> writes a
/// packed DIB, without the file header: the clipboard's layout, and an icon or cursor image's, with its AND
/// mask (alpha below 128 is transparent).
/// </para>
/// </summary>
public static class BmpEncoder
{
    private const uint SrgbTag = 0x73524742;        // 'sRGB', LCS_sRGB
    private const uint ProfileEmbedded = 0x4D424544; // 'MBED'
    private const int ImagesIntent = 4;              // LCS_GM_IMAGES

    /// <summary>Encodes a bitmap file.</summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The input layout.</param>
    /// <param name="options">The output layout; null for <see cref="BmpEncodeFormat.Auto"/>.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size, buffer or options do not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        BmpEncodeOptions? options = null, int stride = 0) =>
        Write(pixels, width, height, format, options ?? new BmpEncodeOptions(), stride, file: true, iconImage: false);

    /// <summary>
    /// Encodes a bitmap file into <paramref name="output"/> a row at a time, through one pooled row: no buffer of the
    /// whole file is allocated (an RLE or palette layout still holds the image's indices).
    /// </summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The input layout.</param>
    /// <param name="output">Where the file is written, from its current position.</param>
    /// <param name="options">The output layout; null for <see cref="BmpEncodeFormat.Auto"/>.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size, buffer or options do not fit.</exception>
    public static void Encode(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format, Stream output,
        BmpEncodeOptions? options = null, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        Write(pixels, width, height, format, options ?? new BmpEncodeOptions(), stride, file: true, iconImage: false, output);
    }

    /// <summary>Encodes a bitmap file from 1 (grey), 3 (RGB) or 4 (RGBA) channels, tightly packed.</summary>
    /// <exception cref="ArgumentException">The size, channel count, buffer or options do not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, BmpEncodeOptions? options = null) =>
        Encode(pixels, width, height, FormatOf(channels), options);

    /// <summary>Encodes a packed DIB: the bitmap header, masks, palette and pixels, without the file header.</summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The input layout.</param>
    /// <param name="options">The output layout; null for <see cref="BmpEncodeFormat.Auto"/>.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <param name="iconImage">An icon or cursor image: a 40-byte header whose height counts the colour bitmap
    /// and the 1-bit AND mask written after it. A palette or 24-bit image's transparent pixels are written black,
    /// as the mask needs.</param>
    /// <exception cref="ArgumentException">The size, buffer or options do not fit.</exception>
    public static byte[] EncodeDib(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        BmpEncodeOptions? options = null, int stride = 0, bool iconImage = false) =>
        Write(pixels, width, height, format, options ?? new BmpEncodeOptions(), stride, file: false, iconImage);

    private static ImagePixelFormat FormatOf(int channels) => channels switch
    {
        1 => ImagePixelFormat.Gray8,
        3 => ImagePixelFormat.Rgb24,
        4 => ImagePixelFormat.Rgba32,
        _ => throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1 (grey), 3 (RGB) or 4 (RGBA)."),
    };

    private static byte[] Write(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        BmpEncodeOptions options, int stride, bool file, bool iconImage) =>
        Write(pixels, width, height, format, options, stride, file, iconImage, stream: null)!;

    /// <summary>
    /// The encoder: plans the layout, then writes the headers and palette, the rows (or the RLE stream), an icon
    /// image's AND mask and a V5 profile, in file order — into a new array (<paramref name="stream"/> null; it is not
    /// zeroed first, as every byte is written), or into <paramref name="stream"/> a row at a time through a pooled
    /// row, with no buffer of the whole file.
    /// </summary>
    private static byte[]? Write(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        BmpEncodeOptions options, int stride, bool file, bool iconImage, Stream? stream)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width), $"A bitmap is at least 1x1, not {width}x{height}.");
        if ((long)width * height > BmpHeader.MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height} is above the limit of {BmpHeader.MaximumPixels:N0} pixels.");
        if (!PixelConverter.TryGetLayout(width, height, format, stride, pixels.Length, out var rowStride))
            throw new ArgumentException($"The pixels do not hold a {width}x{height} {format} image with stride {stride}.", nameof(pixels));
        var input = new Input(pixels, width, height, format, rowStride);

        var hasAlphaChannel = format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32;
        var layout = options.Format;
        if (layout == BmpEncodeFormat.Auto)
        {
            layout = iconImage || (hasAlphaChannel && !input.IsOpaque()) ? BmpEncodeFormat.Argb32
                : format == ImagePixelFormat.Gray8 ? BmpEncodeFormat.Indexed8
                : BmpEncodeFormat.Rgb24;
        }
        var header = options.Header;
        if (header == BmpHeaderVersion.Auto)
        {
            header = iconImage ? BmpHeaderVersion.Info
                : !options.IccProfile.IsEmpty ? BmpHeaderVersion.V5
                : layout == BmpEncodeFormat.Argb32 ? BmpHeaderVersion.V4
                : BmpHeaderVersion.Info;
        }
        Validate(options, layout, header, width, height, iconImage);

        var bpp = layout switch
        {
            BmpEncodeFormat.Indexed1 => 1,
            BmpEncodeFormat.Indexed4 => 4,
            BmpEncodeFormat.Indexed8 => 8,
            BmpEncodeFormat.Rgb555 or BmpEncodeFormat.Rgb565 => 16,
            BmpEncodeFormat.Rgb24 => 24,
            _ => 32,
        };
        var indexed = bpp <= 8;
        var bitfields = layout == BmpEncodeFormat.Rgb565 || (layout == BmpEncodeFormat.Argb32 && header >= BmpHeaderVersion.V4);
        var compression = options.Rle ? (bpp == 8 ? 1u : 2u) : bitfields ? 3u : 0u;
        var headerSize = header switch
        {
            BmpHeaderVersion.Core => 12,
            BmpHeaderVersion.Info => 40,
            BmpHeaderVersion.V4 => 108,
            _ => 124,
        };
        var masksAfterHeader = bitfields && header == BmpHeaderVersion.Info ? 12 : 0;

        // Palette and indices, or nothing for direct colour.
        int[] palette = [];
        byte[]? indices = null;
        if (indexed) (palette, indices) = Palettize(input, 1 << bpp, options.Dither, blackTransparent: iconImage);
        // A core header has no colour count, so its palette is always full; direct colour has none.
        var paletteEntries = header == BmpHeaderVersion.Core && indexed ? 1 << bpp : palette.Length;
        var entrySize = header == BmpHeaderVersion.Core ? 3 : 4;

        // The pixel bytes: rows of whole 32-bit words, or the RLE stream.
        var rowBytes = (int)(((long)width * bpp + 31) / 32 * 4);
        var usedBytes = (int)(((long)width * bpp + 7) / 8);
        byte[]? rle = options.Rle ? Rle.Encode(indices!, width, height, bpp) : null;
        var pixelBytes = rle?.Length ?? (long)rowBytes * height;
        var maskStride = (width + 31) / 32 * 4;
        var maskBytes = iconImage ? (long)maskStride * height : 0;
        var icc = header == BmpHeaderVersion.V5 ? options.IccProfile.Span : default;

        var fileHeader = file ? 14 : 0;
        var pixelOffset = fileHeader + headerSize + masksAfterHeader + paletteEntries * entrySize; // at most 1174
        var total = pixelOffset + pixelBytes + maskBytes + icc.Length;
        if (total > (stream is null ? Array.MaxLength : uint.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(width), "The bitmap would be larger than the format or an array can hold.");

        // Headers, masks and palette.
        Span<byte> o = stackalloc byte[pixelOffset];
        o.Clear();
        if (file)
        {
            o[0] = (byte)'B';
            o[1] = (byte)'M';
            BinaryPrimitives.WriteUInt32LittleEndian(o[2..], (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(o[10..], (uint)pixelOffset);
        }

        var h = o.Slice(fileHeader, headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h, (uint)headerSize);
        var storedHeight = iconImage ? 2 * height : options.TopDown ? -height : height;
        if (header == BmpHeaderVersion.Core)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(h[4..], (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(h[6..], (ushort)height);
            BinaryPrimitives.WriteUInt16LittleEndian(h[8..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(h[10..], (ushort)bpp);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(h[4..], width);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], storedHeight);
            BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(h[14..], (ushort)bpp);
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], compression);
            BinaryPrimitives.WriteUInt32LittleEndian(h[20..], (uint)(pixelBytes + maskBytes));
            BinaryPrimitives.WriteInt32LittleEndian(h[24..], options.PixelsPerMeterX);
            BinaryPrimitives.WriteInt32LittleEndian(h[28..], options.PixelsPerMeterY);
            BinaryPrimitives.WriteUInt32LittleEndian(h[32..], indexed ? (uint)palette.Length : 0);
        }

        // Masks: in a V4 / V5 header, or after a 40-byte one.
        if (bitfields)
        {
            var masks = header >= BmpHeaderVersion.V4 ? h[40..] : o.Slice(fileHeader + headerSize, 12);
            (uint r, uint g, uint b, uint a) = layout == BmpEncodeFormat.Rgb565
                ? (0xF800u, 0x07E0u, 0x001Fu, 0u)
                : (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u);
            BinaryPrimitives.WriteUInt32LittleEndian(masks, r);
            BinaryPrimitives.WriteUInt32LittleEndian(masks[4..], g);
            BinaryPrimitives.WriteUInt32LittleEndian(masks[8..], b);
            if (header >= BmpHeaderVersion.V4) BinaryPrimitives.WriteUInt32LittleEndian(masks[12..], a);
        }
        if (header >= BmpHeaderVersion.V4)
        {
            // Endpoints and gamma stay 0: they are read only for LCS_CALIBRATED_RGB.
            BinaryPrimitives.WriteUInt32LittleEndian(h[56..], icc.IsEmpty ? SrgbTag : ProfileEmbedded);
            if (header == BmpHeaderVersion.V5)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h[108..], ImagesIntent);
                if (!icc.IsEmpty)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(h[112..], (uint)(total - icc.Length - fileHeader));
                    BinaryPrimitives.WriteUInt32LittleEndian(h[116..], (uint)icc.Length);
                }
            }
        }

        var pal = o.Slice(fileHeader + headerSize + masksAfterHeader, paletteEntries * entrySize);
        for (var i = 0; i < palette.Length; i++)
        {
            pal[i * entrySize] = (byte)palette[i];
            pal[i * entrySize + 1] = (byte)(palette[i] >> 8);
            pal[i * entrySize + 2] = (byte)(palette[i] >> 16);
        }

        var output = stream is null ? GC.AllocateUninitializedArray<byte>((int)total) : null;
        byte[]? pooled = stream is null ? null : ArrayPool<byte>.Shared.Rent(Math.Max(Math.Max(rowBytes, maskStride), pixelOffset));
        try
        {
            var at = 0L;
            // The next `length` bytes of the file: in the array, or in the pooled row until Flush writes it out.
            Span<byte> Next(int length) => output is not null ? output.AsSpan((int)at, length) : pooled.AsSpan(0, length);
            void Flush(int length)
            {
                if (stream is not null) stream.Write(pooled!, 0, length);
                at += length;
            }

            o.CopyTo(Next(pixelOffset));
            Flush(pixelOffset);

            if (rle is not null)
            {
                if (output is not null) rle.CopyTo(Next(rle.Length));
                else stream!.Write(rle);
                at += rle.Length;
            }
            else
            {
                for (var r = 0; r < height; r++)
                {
                    var y = options.TopDown ? r : height - 1 - r;
                    var row = Next(rowBytes);
                    if (indexed)
                    {
                        row.Clear(); // PackIndices ORs bits in
                        PackIndices(indices.AsSpan(y * width, width), row, bpp);
                    }
                    else
                    {
                        row[usedBytes..].Clear(); // the padding to a 32-bit word
                        WriteDirect(input, y, row, layout, iconImage);
                    }
                    Flush(rowBytes);
                }
            }

            if (iconImage)
            {
                for (var r = 0; r < height; r++)
                {
                    var y = height - 1 - r;
                    var row = Next(maskStride);
                    row.Clear();
                    for (var x = 0; x < width; x++)
                    {
                        if (input.Alpha(x, y) < 128) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                    }
                    Flush(maskStride);
                }
            }

            if (output is not null) icc.CopyTo(output.AsSpan((int)at));
            else stream!.Write(icc);
            return output;
        }
        finally
        {
            if (pooled is not null) ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    private static void Validate(BmpEncodeOptions options, BmpEncodeFormat layout, BmpHeaderVersion header, int width, int height, bool iconImage)
    {
        if (layout is < BmpEncodeFormat.Auto or > BmpEncodeFormat.Argb32)
            throw new ArgumentOutOfRangeException(nameof(options), layout, "Unknown BmpEncodeFormat.");
        if (header is < BmpHeaderVersion.Auto or > BmpHeaderVersion.V5)
            throw new ArgumentOutOfRangeException(nameof(options), header, "Unknown BmpHeaderVersion.");
        if (options.Rle && layout is not (BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8))
            throw new ArgumentException("RLE codes only a 4- or 8-bit palette.", nameof(options));
        if (options.Rle && options.TopDown)
            throw new ArgumentException("A run-length coded bitmap cannot be top-down.", nameof(options));
        if (!options.IccProfile.IsEmpty && header != BmpHeaderVersion.V5)
            throw new ArgumentException("An ICC profile needs a V5 header.", nameof(options));
        if (header == BmpHeaderVersion.Core)
        {
            if (layout is not (BmpEncodeFormat.Indexed1 or BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8 or BmpEncodeFormat.Rgb24))
                throw new ArgumentException($"A core header holds palettes and 24-bit only, not {layout}.", nameof(options));
            if (options.Rle || options.TopDown)
                throw new ArgumentException("A core header is uncompressed and bottom-up.", nameof(options));
            if (width > 65535 || height > 65535)
                throw new ArgumentOutOfRangeException(nameof(width), $"A core header holds at most 65535 pixels a side, not {width}x{height}.");
        }
        if (iconImage)
        {
            if (header != BmpHeaderVersion.Info)
                throw new ArgumentException("An icon image has a 40-byte header.", nameof(options));
            if (options.Rle || options.TopDown)
                throw new ArgumentException("An icon image is uncompressed and bottom-up.", nameof(options));
            if (height > int.MaxValue / 2)
                throw new ArgumentOutOfRangeException(nameof(height), "An icon image's doubled height must fit the header.");
        }
    }

    // ---- Input -------------------------------------------------------------------------------------------------

    /// <summary>The caller's pixels: any layout, rows <c>Stride</c> apart.</summary>
    private readonly ref struct Input(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format, int stride)
    {
        public readonly ReadOnlySpan<byte> Pixels = pixels;
        public readonly int Width = width, Height = height, Stride = stride;
        public readonly ImagePixelFormat Format = format;

        public ReadOnlySpan<byte> Row(int y) => Pixels.Slice(y * Stride, Width * PixelConverter.BytesPerPixel(Format));

        public int Alpha(int x, int y) => Format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32 ? Pixels[y * Stride + 4 * x + 3] : 255;

        public bool IsOpaque()
        {
            for (var y = 0; y < Height; y++)
            {
                var row = Row(y);
                for (var x = 3; x < row.Length; x += 4)
                {
                    if (row[x] != 255) return false;
                }
            }
            return true;
        }

        /// <summary>Row <paramref name="y"/> as R, G, B, A.</summary>
        public void ToRgba(int y, Span<byte> rgba)
        {
            var row = Row(y);
            switch (Format)
            {
                case ImagePixelFormat.Rgba32:
                    row.CopyTo(rgba);
                    break;
                case ImagePixelFormat.Bgra32:
                    BmpKernels.Shuffle32(row, rgba, Width, [2, 1, 0, 3], 0, BmpKernels.BestTier);
                    break;
                case ImagePixelFormat.Rgb24:
                    BmpKernels.Expand24(row, rgba, Width, swap: false, BmpKernels.BestTier);
                    break;
                case ImagePixelFormat.Bgr24:
                    BmpKernels.Expand24(row, rgba, Width, swap: true, BmpKernels.BestTier);
                    break;
                default:
                    for (var x = 0; x < Width; x++)
                    {
                        rgba[4 * x] = rgba[4 * x + 1] = rgba[4 * x + 2] = row[x];
                        rgba[4 * x + 3] = 255;
                    }
                    break;
            }
        }
    }

    // ---- Direct colour -----------------------------------------------------------------------------------------

    private static void WriteDirect(Input input, int y, Span<byte> row, BmpEncodeFormat layout, bool iconImage)
    {
        var width = input.Width;
        var source = input.Row(y);
        var tier = BmpKernels.BestTier;
        // A 24-bit icon's transparent pixels go through RGBA, where they are blacked out for the AND mask.
        var blackOut = iconImage && layout != BmpEncodeFormat.Argb32 && input.Format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32;
        if (layout == BmpEncodeFormat.Rgb24 && !blackOut)
        {
            switch (input.Format)
            {
                case ImagePixelFormat.Bgr24:
                    source.CopyTo(row);
                    return;
                case ImagePixelFormat.Rgb24:
                    BmpKernels.Swap24(source, row, width, tier);
                    return;
                case ImagePixelFormat.Rgba32:
                    BmpKernels.Pack32To24(source, row, width, [2, 1, 0], tier);
                    return;
                case ImagePixelFormat.Bgra32:
                    BmpKernels.Pack32To24(source, row, width, [0, 1, 2], tier);
                    return;
            }
        }
        if (layout is BmpEncodeFormat.Rgb32 or BmpEncodeFormat.Argb32)
        {
            var keepAlpha = layout == BmpEncodeFormat.Argb32;
            switch (input.Format)
            {
                case ImagePixelFormat.Bgra32 when keepAlpha:
                    source.CopyTo(row);
                    return;
                case ImagePixelFormat.Bgra32:
                    BmpKernels.Shuffle32(source, row, width, [0, 1, 2, 0x80], 0xFF000000u, tier);
                    return;
                case ImagePixelFormat.Rgba32:
                    BmpKernels.Shuffle32(source, row, width, [2, 1, 0, (byte)(keepAlpha ? 3 : 0x80)], keepAlpha ? 0 : 0xFF000000u, tier);
                    return;
                case ImagePixelFormat.Rgb24:
                    BmpKernels.Expand24(source, row, width, swap: true, tier);
                    return;
                case ImagePixelFormat.Bgr24:
                    BmpKernels.Expand24(source, row, width, swap: false, tier);
                    return;
            }
        }

        // Everything else a pixel at a time, straight from the input: its channel offsets and pixel size.
        var (ri, gi, bi, ai, step) = input.Format switch
        {
            ImagePixelFormat.Rgba32 => (0, 1, 2, 3, 4),
            ImagePixelFormat.Bgra32 => (2, 1, 0, 3, 4),
            ImagePixelFormat.Rgb24 => (0, 1, 2, -1, 3),
            ImagePixelFormat.Bgr24 => (2, 1, 0, -1, 3),
            _ => (0, 0, 0, -1, 1),
        };
        if (layout is BmpEncodeFormat.Rgb555 or BmpEncodeFormat.Rgb565 && !blackOut)
        {
            Pack16(source, row, width, step, ri, gi, bi, green6: layout == BmpEncodeFormat.Rgb565);
            return;
        }
        for (var x = 0; x < width; x++)
        {
            var p = source.Slice(step * x, step);
            int r = p[ri], g = p[gi], b = p[bi], a = ai < 0 ? 255 : p[ai];
            if (blackOut && a < 128) r = g = b = 0;
            switch (layout)
            {
                case BmpEncodeFormat.Rgb565:
                    BinaryPrimitives.WriteUInt16LittleEndian(row[(2 * x)..], (ushort)(Five[r] << 11 | Six[g] << 5 | Five[b]));
                    break;
                case BmpEncodeFormat.Rgb555:
                    BinaryPrimitives.WriteUInt16LittleEndian(row[(2 * x)..], (ushort)(Five[r] << 10 | Five[g] << 5 | Five[b]));
                    break;
                case BmpEncodeFormat.Rgb24:
                    (row[3 * x], row[3 * x + 1], row[3 * x + 2]) = ((byte)b, (byte)g, (byte)r);
                    break;
                default:
                    (row[4 * x], row[4 * x + 1], row[4 * x + 2]) = ((byte)b, (byte)g, (byte)r);
                    row[4 * x + 3] = layout == BmpEncodeFormat.Argb32 ? (byte)a : (byte)255;
                    break;
            }
        }
    }

    /// <summary>A row to 16-bit 5-6-5 or x-5-5-5, each channel narrowed to the nearest value by table.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Pack16(ReadOnlySpan<byte> source, Span<byte> row, int width, int step, int ri, int gi, int bi, bool green6)
    {
        if (source.Length < (long)width * step || row.Length < 2L * width)
            throw new ArgumentException("The spans are shorter than the row.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(row);
        ref var five = ref MemoryMarshal.GetArrayDataReference(Five);
        ref var green = ref MemoryMarshal.GetArrayDataReference(green6 ? Six : Five);
        var redShift = green6 ? 11 : 10;
        // Bounds: pixel x reads source bytes step * x + 0..2 (channel offsets below step) and writes row bytes
        // 2x, 2x + 1, all inside the lengths checked above; table indices are bytes, the tables 256 long.
        for (var x = 0; x < width; x++)
        {
            ref var p = ref Unsafe.Add(ref s, step * x);
            var v = Unsafe.Add(ref five, Unsafe.Add(ref p, ri)) << redShift
                | Unsafe.Add(ref green, Unsafe.Add(ref p, gi)) << 5
                | Unsafe.Add(ref five, Unsafe.Add(ref p, bi));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 2 * x), (ushort)v);
        }
    }

    // An 8-bit value to the nearest 5- or 6-bit one.
    private static readonly byte[] Five = Narrowing(5), Six = Narrowing(6);

    private static byte[] Narrowing(int bits)
    {
        var table = new byte[256];
        for (var v = 0; v < 256; v++) table[v] = (byte)((v * ((1 << bits) - 1) + 127) / 255);
        return table;
    }

    // ---- Palettes ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The palette (0x00RRGGBB, as stored: blue in the low byte) and one index per pixel. Exact when the colours
    /// fit <paramref name="size"/>, otherwise Wu-quantized. Alpha is ignored, except that an icon image's
    /// transparent pixels (<paramref name="blackTransparent"/>) count as black.
    /// </summary>
    private static (int[] Palette, byte[] Indices) Palettize(Input input, int size, bool dither, bool blackTransparent)
    {
        int width = input.Width, height = input.Height;
        var argb = new int[width * height];
        var rgba = ArrayPool<byte>.Shared.Rent(width * 4);
        try
        {
            for (var y = 0; y < height; y++)
            {
                input.ToRgba(y, rgba);
                for (var x = 0; x < width; x++)
                {
                    int r = rgba[4 * x], g = rgba[4 * x + 1], b = rgba[4 * x + 2];
                    if (blackTransparent && rgba[4 * x + 3] < 128) r = g = b = 0;
                    argb[y * width + x] = unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgba);
        }

        var indices = new byte[width * height];
        var colours = new HashSet<int>();
        foreach (var p in argb)
        {
            if (colours.Add(p & 0xFFFFFF) && colours.Count > size) goto quantize;
        }
        int[] exact = [.. colours.Order()];
        var lookup = new Dictionary<int, byte>(exact.Length);
        for (var i = 0; i < exact.Length; i++) lookup[exact[i]] = (byte)i;
        for (var i = 0; i < argb.Length; i++) indices[i] = lookup[argb[i] & 0xFFFFFF];
        return (exact, indices);

    quantize:
        var quantizer = new PaletteQuantizer();
        foreach (var p in argb) quantizer.Add(p & 0xFFFFFF);
        var palette = quantizer.BuildPalette(size);
        new PaletteQuantizer.Mapper(palette).Map(argb, width, height, indices, 0, dither);
        return (palette, indices);
    }

    private static void PackIndices(ReadOnlySpan<byte> indices, Span<byte> row, int bpp)
    {
        if (bpp == 8)
        {
            indices.CopyTo(row);
            return;
        }
        var perByte = 8 / bpp;
        for (var x = 0; x < indices.Length; x++)
            row[x / perByte] |= (byte)(indices[x] << (8 - bpp - bpp * (x % perByte)));
    }

    // ---- RLE ---------------------------------------------------------------------------------------------------

    /// <summary>BI_RLE8 / BI_RLE4: runs of one index as (count, index), anything else in absolute mode.</summary>
    private static class Rle
    {
        public static byte[] Encode(byte[] indices, int width, int height, int bpp)
        {
            var output = new ArrayBufferWriter<byte>(Math.Max(256, width * height / 2));
            for (var r = 0; r < height; r++)
            {
                var row = indices.AsSpan((height - 1 - r) * width, width);
                EncodeRow(row, bpp, output);
                // End of line after each row but the last, which the end of bitmap closes.
                Put(output, 0, r == height - 1 ? (byte)1 : (byte)0);
            }
            return output.WrittenSpan.ToArray();
        }

        private static void EncodeRow(ReadOnlySpan<byte> row, int bpp, ArrayBufferWriter<byte> output)
        {
            var x = 0;
            while (x < row.Length)
            {
                var run = RunLength(row, x);
                if (run >= 2)
                {
                    Put(output, (byte)run, bpp == 8 ? row[x] : (byte)(row[x] << 4 | row[x]));
                    x += run;
                    continue;
                }
                // Literals until a run of three or more begins: absolute mode needs at least three pixels, and
                // a run of two costs no more coded than literal.
                var end = x + 1;
                while (end < row.Length && end - x < 255 && RunLength(row, end) < 3) end++;
                var count = end - x;
                if (count < 3)
                {
                    for (; x < end; x++) Put(output, 1, bpp == 8 ? row[x] : (byte)(row[x] << 4));
                    continue;
                }
                var bytes = bpp == 8 ? count : (count + 1) / 2;
                var span = output.GetSpan(2 + bytes + 1);
                span[0] = 0;
                span[1] = (byte)count;
                if (bpp == 8)
                {
                    row.Slice(x, count).CopyTo(span[2..]);
                }
                else
                {
                    span.Slice(2, bytes).Clear();
                    for (var i = 0; i < count; i++) span[2 + i / 2] |= (byte)(row[x + i] << ((i & 1) == 0 ? 4 : 0));
                }
                var padded = bytes + (bytes & 1);
                if ((bytes & 1) != 0) span[2 + bytes] = 0;
                output.Advance(2 + padded);
                x = end;
            }
        }

        private static int RunLength(ReadOnlySpan<byte> row, int x)
        {
            var end = x + 1;
            while (end < row.Length && end - x < 255 && row[end] == row[x]) end++;
            return end - x;
        }

        private static void Put(ArrayBufferWriter<byte> output, byte count, byte value)
        {
            var span = output.GetSpan(2);
            span[0] = count;
            span[1] = value;
            output.Advance(2);
        }
    }
}
