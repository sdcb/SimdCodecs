using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Png;

namespace Sdcb.SimdCodecs.Ico;

/// <summary>How <see cref="IcoEncoder"/> stores one image.</summary>
public enum IcoImageEncoding
{
    /// <summary>PNG for an image 256 pixels or more on a side (what Windows Vista and later expect), 32-bit BMP
    /// otherwise (what every reader handles).</summary>
    Auto,

    /// <summary>A PNG stream.</summary>
    Png,

    /// <summary>A 32-bit DIB with alpha, and an AND mask from it.</summary>
    Bmp32,

    /// <summary>A 24-bit DIB, transparency in the AND mask only.</summary>
    Bmp24,

    /// <summary>An 8-bit palette DIB (exact when 256 colours fit, otherwise quantized), transparency in the AND mask.</summary>
    Bmp8,

    /// <summary>A 4-bit palette DIB (16 colours), transparency in the AND mask.</summary>
    Bmp4,

    /// <summary>A 1-bit palette DIB (2 colours), transparency in the AND mask.</summary>
    Bmp1,
}

/// <summary>One image to store in an icon or cursor.</summary>
/// <param name="Pixels">The image, top row first, tightly packed in <paramref name="Format"/>.</param>
/// <param name="Width">Width in pixels, 1 to 256.</param>
/// <param name="Height">Height in pixels, 1 to 256.</param>
/// <param name="Format">The pixel layout of <paramref name="Pixels"/>.</param>
/// <param name="Encoding">How the image is stored.</param>
/// <param name="HotspotX">For a cursor, the hotspot's column; ignored for an icon.</param>
/// <param name="HotspotY">For a cursor, the hotspot's row; ignored for an icon.</param>
public readonly record struct IcoFrame(ReadOnlyMemory<byte> Pixels, int Width, int Height,
    ImagePixelFormat Format = ImagePixelFormat.Rgba32, IcoImageEncoding Encoding = IcoImageEncoding.Auto,
    int HotspotX = 0, int HotspotY = 0);

/// <summary>
/// Pure-managed encoder for Windows icons and cursors: any number of images, each stored as PNG (by
/// <c>Sdcb.SimdCodecs.Png</c>) or as a DIB with an AND mask (by <c>Sdcb.SimdCodecs.Bmp</c>; alpha below 128 is
/// transparent in the mask). Images are written in the order given; a reader picks among them by size and depth.
/// </summary>
public static class IcoEncoder
{
    private const int DirectorySize = 6, EntrySize = 16;

    /// <summary>Encodes an icon (<c>.ico</c>).</summary>
    /// <exception cref="ArgumentException">No images, or an image whose size, buffer or encoding does not fit.</exception>
    public static byte[] Encode(IReadOnlyList<IcoFrame> images) => Write(images, IcoKind.Icon);

    /// <summary>Encodes a cursor (<c>.cur</c>), each image with its hotspot.</summary>
    /// <exception cref="ArgumentException">No images, a hotspot outside its image, or an image whose size, buffer or
    /// encoding does not fit.</exception>
    public static byte[] EncodeCursor(IReadOnlyList<IcoFrame> images) => Write(images, IcoKind.Cursor);

    /// <summary>Encodes an icon of one image.</summary>
    /// <exception cref="ArgumentException">The size, buffer or encoding does not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format = ImagePixelFormat.Rgba32,
        IcoImageEncoding encoding = IcoImageEncoding.Auto) =>
        Encode([new IcoFrame(pixels.ToArray(), width, height, format, encoding)]);

    private static byte[] Write(IReadOnlyList<IcoFrame> images, IcoKind kind)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count is 0 or > 65535)
            throw new ArgumentException("An icon holds 1 to 65535 images.", nameof(images));

        var payloads = new byte[images.Count][];
        var depths = new int[images.Count];
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            if (image.Width is < 1 or > 256 || image.Height is < 1 or > 256)
                throw new ArgumentOutOfRangeException(nameof(images), $"Image {i} is {image.Width}x{image.Height}; an icon image is 1 to 256 pixels a side.");
            if (kind == IcoKind.Cursor && (image.HotspotX < 0 || image.HotspotX >= image.Width || image.HotspotY < 0 || image.HotspotY >= image.Height))
                throw new ArgumentOutOfRangeException(nameof(images), $"Image {i}'s hotspot ({image.HotspotX}, {image.HotspotY}) is outside it.");
            if (image.Encoding is < IcoImageEncoding.Auto or > IcoImageEncoding.Bmp1)
                throw new ArgumentOutOfRangeException(nameof(images), image.Encoding, "Unknown IcoImageEncoding.");
            if (!PixelConverter.TryGetLayout(image.Width, image.Height, image.Format, 0, image.Pixels.Length, out _))
                throw new ArgumentException($"Image {i}'s pixels do not hold a {image.Width}x{image.Height} {image.Format} image.", nameof(images));

            var encoding = image.Encoding == IcoImageEncoding.Auto
                ? image.Width >= 256 || image.Height >= 256 ? IcoImageEncoding.Png : IcoImageEncoding.Bmp32
                : image.Encoding;
            (payloads[i], depths[i]) = encoding switch
            {
                IcoImageEncoding.Png => (PngWriter.Encode(ToRgba(image), image.Width, image.Height), 32),
                _ => Dib(image, encoding),
            };
        }

        var total = DirectorySize + (long)EntrySize * images.Count + payloads.Sum(p => (long)p.Length);
        if (total > Array.MaxLength) throw new ArgumentException("The icon would be larger than an array can hold.", nameof(images));
        var output = new byte[total];
        var o = output.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(o[2..], (ushort)kind);
        BinaryPrimitives.WriteUInt16LittleEndian(o[4..], (ushort)images.Count);
        var offset = DirectorySize + EntrySize * images.Count;
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var e = o.Slice(DirectorySize + EntrySize * i, EntrySize);
            e[0] = (byte)(image.Width & 255);   // 256 is stored as 0
            e[1] = (byte)(image.Height & 255);
            e[2] = depths[i] <= 4 ? (byte)(1 << depths[i]) : (byte)0; // palette colours, 0 for 256 or more
            if (kind == IcoKind.Cursor)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(e[4..], (ushort)image.HotspotX);
                BinaryPrimitives.WriteUInt16LittleEndian(e[6..], (ushort)image.HotspotY);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);
                BinaryPrimitives.WriteUInt16LittleEndian(e[6..], (ushort)depths[i]);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)payloads[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)offset);
            payloads[i].CopyTo(o[offset..]);
            offset += payloads[i].Length;
        }
        return output;
    }

    private static (byte[] Payload, int Depth) Dib(IcoFrame image, IcoImageEncoding encoding)
    {
        var (format, depth) = encoding switch
        {
            IcoImageEncoding.Bmp24 => (BmpEncodeFormat.Rgb24, 24),
            IcoImageEncoding.Bmp8 => (BmpEncodeFormat.Indexed8, 8),
            IcoImageEncoding.Bmp4 => (BmpEncodeFormat.Indexed4, 4),
            IcoImageEncoding.Bmp1 => (BmpEncodeFormat.Indexed1, 1),
            _ => (BmpEncodeFormat.Argb32, 32),
        };
        var dib = BmpEncoder.EncodeDib(image.Pixels.Span, image.Width, image.Height, image.Format,
            new BmpEncodeOptions { Format = format }, iconImage: true);
        return (dib, depth);
    }

    // The image as tightly packed RGBA, for the PNG writer.
    private static byte[] ToRgba(IcoFrame image)
    {
        int width = image.Width, height = image.Height;
        if (image.Format == ImagePixelFormat.Rgba32) return image.Pixels[..(width * height * 4)].ToArray();
        var rgba = new byte[width * height * 4];
        var source = image.Pixels.Span;
        for (var p = 0; p < width * height; p++)
        {
            byte r, g, b, a = 255;
            switch (image.Format)
            {
                case ImagePixelFormat.Bgra32:
                    (b, g, r, a) = (source[4 * p], source[4 * p + 1], source[4 * p + 2], source[4 * p + 3]);
                    break;
                case ImagePixelFormat.Rgb24:
                    (r, g, b) = (source[3 * p], source[3 * p + 1], source[3 * p + 2]);
                    break;
                case ImagePixelFormat.Bgr24:
                    (b, g, r) = (source[3 * p], source[3 * p + 1], source[3 * p + 2]);
                    break;
                default:
                    r = g = b = source[p];
                    break;
            }
            (rgba[4 * p], rgba[4 * p + 1], rgba[4 * p + 2], rgba[4 * p + 3]) = (r, g, b, a);
        }
        return rgba;
    }
}
