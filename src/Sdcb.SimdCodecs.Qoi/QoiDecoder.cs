using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Qoi;

/// <summary>
/// Pure-managed QOI decoder, written from the specification at qoiformat.org. It reads 3- and 4-channel images
/// of up to 2^28 pixels; the colour space byte is reported, never applied. The decisions the specification
/// leaves open are listed in the package README.
/// </summary>
public static class QoiDecoder
{
    /// <summary>Reads the size, channels and colour space from the header.</summary>
    /// <exception cref="InvalidDataException">The data is not a QOI image, or its header is invalid.</exception>
    public static QoiInfo ReadInfo(ReadOnlySpan<byte> data) => QoiCodec.ReadHeader(data);

    /// <summary><see cref="ReadInfo"/> that returns false instead of throwing.</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out QoiInfo info)
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

    /// <summary>Decodes to a new image: RGB, or RGBA when the header declares 4 channels.</summary>
    /// <exception cref="InvalidDataException">The data is not a decodable QOI image.</exception>
    public static QoiImage Decode(ReadOnlySpan<byte> data)
    {
        var info = QoiCodec.ReadHeader(data);
        var pixels = new byte[(long)info.Width * info.Height * info.Channels];
        Decode(data, info, pixels, info.Width * info.Channels, info.Channels == 4 ? ImagePixelFormat.Rgba32 : ImagePixelFormat.Rgb24);
        return new QoiImage(info.Width, info.Height, info.Channels, info.ColorSpace, pixels);
    }

    /// <summary>
    /// Decodes into <paramref name="destination"/> as <paramref name="format"/>, top row first, rows
    /// <paramref name="stride"/> bytes apart (0: tightly packed). Size it with <see cref="ReadInfo"/>. Bytes
    /// between rows are left untouched; nothing is allocated.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a decodable QOI image.</exception>
    /// <exception cref="ArgumentException">The destination is too small, or the stride shorter than a row.</exception>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> destination, ImagePixelFormat format = ImagePixelFormat.Rgba32, int stride = 0)
    {
        var info = QoiCodec.ReadHeader(data);
        if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride))
            throw new ArgumentException($"The destination does not hold a {info.Width}x{info.Height} {format} image with stride {stride}.", nameof(destination));
        Decode(data, info, destination, rowStride, format);
    }

    internal static void Decode(ReadOnlySpan<byte> data, QoiInfo info, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        var reader = new QoiCodec.Reader(info.Channels == 3);
        int width = info.Width, height = info.Height;
        if (format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32)
        {
            // RGBA pixels land in place; BGRA swaps them where they are.
            var rowBytes = width * 4;
            if (rowStride == rowBytes && format == ImagePixelFormat.Rgba32)
            {
                reader.Read(data, MemoryMarshal.Cast<byte, uint>(destination[..(rowBytes * height)]));
                return;
            }
            for (var y = 0; y < height; y++)
            {
                var row = destination.Slice(y * rowStride, rowBytes);
                reader.Read(data, MemoryMarshal.Cast<byte, uint>(row));
                if (format == ImagePixelFormat.Bgra32)
                    PixelConverter.ConvertRow(row, 4, SampleFormat.UInt8, row, ImagePixelFormat.Bgra32, width);
            }
            return;
        }

        const int Chunk = 512;
        Span<uint> chunk = stackalloc uint[Chunk];
        var bpp = PixelConverter.BytesPerPixel(format);
        for (var y = 0; y < height; y++)
        {
            var row = destination.Slice(y * rowStride, width * bpp);
            for (var x = 0; x < width; x += Chunk)
            {
                var n = Math.Min(Chunk, width - x);
                reader.Read(data, chunk[..n]);
                PixelConverter.ConvertRow(MemoryMarshal.AsBytes(chunk[..n]), 4, SampleFormat.UInt8, row[(x * bpp)..], format, n);
            }
        }
    }
}
