using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG, bridging <see cref="JpegDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. A greyscale JPEG (one SOF component)
/// decodes to a 1-channel <see cref="SampleFormat.UInt8"/> raster of its samples;
/// every other JPEG to 4-channel RGBA (JPEG has no alpha, so it is 255).
/// <see cref="TryDecodeInto"/> is a genuine zero-copy decode in every
/// <see cref="ImagePixelFormat"/> via
/// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, JpegPixelFormat, JpegScale)"/>.
/// </summary>
public sealed class JpegImageDecoder : IImageDecoder
{
    private static ReadOnlySpan<byte> Signature => [0xFF, 0xD8, 0xFF];

    /// <inheritdoc />
    public static int SignatureLength => 3;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[..3].SequenceEqual(Signature);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        try
        {
            info = ToImageInfo(JpegDecoder.ReadInfo(data));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        try
        {
            if (JpegDecoder.ReadInfo(data) is { Components: 1 } grey)
            {
                // The samples as stored: Gray8 output of a one-component frame is exactly its Y.
                var samples = new byte[grey.Width * grey.Height];
                _ = JpegDecoder.DecodeTo(data, samples, grey.Width, JpegPixelFormat.Gray8);
                image = new RasterImage(grey.Width, grey.Height, 1, SampleFormat.UInt8, samples);
                return true;
            }

            var jpeg = JpegDecoder.Decode(data);
            image = new RasterImage(jpeg.Width, jpeg.Height, 4, SampleFormat.UInt8, jpeg.Pixels);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        try
        {
            var info = JpegDecoder.ReadInfo(data);
            if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride))
                return false;
            _ = JpegDecoder.DecodeTo(data, destination, rowStride, ToJpegFormat(format));
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);

    /// <summary>The <see cref="ImageInfo"/> of what <see cref="TryDecode"/> returns for a stream with header <paramref name="info"/>.</summary>
    internal static ImageInfo ToImageInfo(JpegInfo info) =>
        new(info.Width, info.Height, info.Components == 1 ? 1 : 4, SampleFormat.UInt8) { Orientation = info.Orientation };

    /// <summary>The <see cref="JpegPixelFormat"/> writing the same bytes as <paramref name="format"/>.</summary>
    internal static JpegPixelFormat ToJpegFormat(ImagePixelFormat format) => format switch
    {
        ImagePixelFormat.Rgba32 => JpegPixelFormat.Rgba32,
        ImagePixelFormat.Bgra32 => JpegPixelFormat.Bgra32,
        ImagePixelFormat.Rgb24 => JpegPixelFormat.Rgb24,
        ImagePixelFormat.Bgr24 => JpegPixelFormat.Bgr24,
        ImagePixelFormat.Gray8 => JpegPixelFormat.Gray8,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
    };
}
