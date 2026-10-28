using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG, bridging <see cref="JpegDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. JPEG always decodes to 8-bit RGBA
/// regardless of the SOF component count, so both the fidelity and display tiers
/// yield the same 4-channel <see cref="SampleFormat.UInt8"/> raster;
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
            var ji = JpegDecoder.ReadInfo(data);
            info = new ImageInfo(ji.Width, ji.Height, 4, SampleFormat.UInt8);
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
