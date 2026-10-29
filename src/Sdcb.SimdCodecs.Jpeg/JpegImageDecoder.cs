using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG, bridging <see cref="JpegDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. A greyscale JPEG (one SOF component)
/// decodes to a 1-channel <see cref="SampleFormat.UInt8"/> raster of its samples;
/// every other JPEG (YCbCr, RGB, CMYK, YCCK) to 3-channel RGB - JPEG has no alpha. The
/// embedded APP2 ICC profile, if any, comes back as <see cref="IDecodedImage.IccProfile"/>.
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
        if (!JpegDecoder.TryReadInfo(data, out var jpeg))
            return false;
        info = ToImageInfo(jpeg);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        try
        {
            // A one-component frame keeps its samples as stored (Gray8 output is exactly its Y);
            // every other frame is colour, written as RGB since JPEG carries no alpha.
            var info = JpegDecoder.ReadInfo(data);
            var (channels, format) = info.Components == 1 ? (1, JpegPixelFormat.Gray8) : (3, JpegPixelFormat.Rgb24);
            var pixels = new byte[info.Width * info.Height * channels];
            _ = JpegDecoder.DecodeTo(data, pixels, info.Width * channels, format);
            image = new RasterImage(info.Width, info.Height, channels, SampleFormat.UInt8, pixels, JpegDecoder.ReadIccProfile(data));
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
        new(info.Width, info.Height, info.Components == 1 ? 1 : 3, SampleFormat.UInt8) { Format = ImageFormat.Jpeg, Orientation = info.Orientation };

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
