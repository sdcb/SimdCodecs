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
        Decode(data, info, pixels, info.Width * info.Channels, info.Channels == 4 ? PixelFormat.Rgba32 : PixelFormat.Rgb24);
        return new QoiImage(info.Width, info.Height, info.Channels, info.ColorSpace, pixels);
    }

    /// <summary>
    /// Decodes into <paramref name="destination"/> as <paramref name="format"/>, top row first, rows
    /// <paramref name="stride"/> bytes apart (0: tightly packed). Size it with <see cref="ReadInfo"/>. Bytes
    /// between rows are left untouched; nothing is allocated.
    /// </summary>
    /// <param name="data">The QOI file.</param>
    /// <param name="destination">The pixels.</param>
    /// <param name="format">The layout to write; any <see cref="PixelFormat"/> (null: <see cref="PixelFormat.Rgba32"/>).</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <exception cref="InvalidDataException">The data is not a decodable QOI image.</exception>
    /// <exception cref="ArgumentException">The destination is too small, or the stride shorter than a row.</exception>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> destination, PixelFormat? format = null, int stride = 0)
    {
        var target = format ?? PixelFormat.Rgba32;
        var info = QoiCodec.ReadHeader(data);
        if (!PixelConverter.TryGetLayout(info.Width, info.Height, target, stride, destination.Length, out var rowStride))
            throw new ArgumentException($"The destination does not hold a {info.Width}x{info.Height} {target} image with stride {stride}.", nameof(destination));
        Decode(data, info, destination, rowStride, target);
    }

    internal static void Decode(ReadOnlySpan<byte> data, QoiInfo info, Span<byte> destination, int rowStride, PixelFormat format,
        CancellationToken cancellationToken = default)
    {
        var reader = new QoiCodec.Reader(info.Channels == 3);
        int width = info.Width, height = info.Height;
        if (format == PixelFormat.Rgba32 || format == PixelFormat.Bgra32)
        {
            // RGBA pixels land in place; BGRA swaps them where they are.
            var rowBytes = width * 4;
            if (rowStride == rowBytes && format == PixelFormat.Rgba32 && !cancellationToken.CanBeCanceled)
            {
                reader.Read(data, MemoryMarshal.Cast<byte, uint>(destination[..(rowBytes * height)]));
                return;
            }
            for (var y = 0; y < height; y++)
            {
                if ((y & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var row = destination.Slice(y * rowStride, rowBytes);
                reader.Read(data, MemoryMarshal.Cast<byte, uint>(row));
                if (format == PixelFormat.Bgra32)
                    PixelConverter.ConvertRow(row, PixelFormat.Rgba32, row, PixelFormat.Bgra32, width);
            }
            return;
        }

        const int Chunk = 512;
        Span<uint> chunk = stackalloc uint[Chunk];
        var bpp = format.BytesPerPixel;
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var row = destination.Slice(y * rowStride, width * bpp);
            for (var x = 0; x < width; x += Chunk)
            {
                var n = Math.Min(Chunk, width - x);
                reader.Read(data, chunk[..n]);
                PixelConverter.ConvertRow(MemoryMarshal.AsBytes(chunk[..n]), PixelFormat.Rgba32, row[(x * bpp)..], format, n);
            }
        }
    }
}
