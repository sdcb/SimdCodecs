using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG, bridging <see cref="JpegDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. A greyscale JPEG (one SOF component)
/// decodes to a 1-channel <see cref="SampleFormat.UInt8"/> raster of its samples;
/// every other JPEG (YCbCr, RGB, CMYK, YCCK) to 3-channel RGB - JPEG has no alpha. The
/// embedded APP2 ICC profile, if any, comes back as <see cref="IDecodedImage.IccProfile"/>.
/// <para>
/// <see cref="TryDecodeInto"/> is a genuine zero-copy decode for the five basic 8-bit
/// <see cref="PixelFormat"/>s (<see cref="PixelFormat.IsBasic8"/>) via
/// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, JpegPixelFormat, JpegScale)"/>; the other
/// formats (16-bit, premultiplied, grey + alpha) are the 8-bit decode widened by <see cref="PixelConverter"/>.
/// JPEG scales natively in the DCT domain: <see cref="DecodeOptions.ScaleDenominator"/> 2, 4 and 8 decode a
/// smaller image at a fraction of the cost (<see cref="TryGetClosestSize"/>).
/// </para>
/// </summary>
public sealed class JpegImageDecoder : IImageDecoder
{
    private static ReadOnlySpan<byte> Signature => [0xFF, 0xD8, 0xFF];

    /// <summary>The registration for <c>SimdCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new(ImageFormat.Jpeg, CanDecode, TryReadInfo,
        TryGetClosestSize, TryDecode, TryDecodeInto, TryReadMetadata, TryDecodeThumbnail);

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
        info = ToImageInfo(jpeg, data);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>The smallest of 1/1, 1/2, 1/4 and 1/8 of the stored size that is at least the requested size in both
    /// directions (the stored size when the request is larger).</remarks>
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator)
    {
        width = height = 0;
        scaleDenominator = 1;
        if (!JpegDecoder.TryReadInfo(data, out var jpeg))
            return false;
        (width, height) = (jpeg.Width, jpeg.Height);
        foreach (var d in (ReadOnlySpan<int>)[8, 4, 2])
        {
            var (w, h) = jpeg.ScaledSize((JpegScale)d);
            if (w >= requestedWidth && h >= requestedHeight)
            {
                (width, height, scaleDenominator) = (w, h, d);
                break;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        image = null;
        if (!JpegDecoder.TryReadInfo(data, out var info))
            return false;
        options?.Validate(info.Width, info.Height, supportsScale: true);
        var scale = (JpegScale)(options?.ScaleDenominator ?? 1);
        var (width, height) = info.ScaledSize(scale);

        // A one-component frame keeps its samples as stored (Gray8 output is exactly its Y);
        // every other frame is colour, written as RGB since JPEG carries no alpha.
        var (channels, format) = info.Components == 1 ? (1, JpegPixelFormat.Gray8) : (3, JpegPixelFormat.Rgb24);
        var raster = RasterImage.Create(width, height, channels, SampleFormat.UInt8, options?.Pool,
            JpegDecoder.TryReadIccProfile(data, out var icc) ? icc : null, metadata: ReadMetadata(data));
        try
        {
            _ = JpegDecoder.DecodeTo(data, raster.WritablePixels, width * channels, format, scale, cancellationToken);
        }
        catch (Exception e)
        {
            raster.Dispose();
            if (e is InvalidDataException)
                return false;
            throw;
        }

        image = options?.Region is { } region ? Crop(raster, region, options.Pool) : raster;
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!format.IsBasic8 || options?.Region is not null)
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);
        if (!JpegDecoder.TryReadInfo(data, out var info))
            return false;
        options?.Validate(info.Width, info.Height, supportsScale: true);
        var scale = (JpegScale)(options?.ScaleDenominator ?? 1);
        var (width, height) = info.ScaledSize(scale);
        if (!PixelConverter.TryGetLayout(width, height, format, stride, destination.Length, out var rowStride))
            return false;
        try
        {
            _ = JpegDecoder.DecodeTo(data, destination, rowStride, ToJpegFormat(format), scale, cancellationToken);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>The APP1 Exif block (from its TIFF header, without the <c>Exif\0\0</c> prefix), the APP1 XMP packet and
    /// the APP2 ICC profile, reassembled from its chunks.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = null;
        if (!JpegDecoder.TryReadInfo(data, out _))
            return false;
        metadata = ReadMetadata(data);
        if (JpegDecoder.TryReadIccProfile(data, out var icc))
            metadata = metadata.WithIcc(icc);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        JpegMetadata.TryDecodeExifThumbnail(data, out thumbnail);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    /// <summary>The <see cref="ImageInfo"/> of what <see cref="TryDecode"/> returns for a stream with header <paramref name="info"/>.</summary>
    internal static ImageInfo ToImageInfo(JpegInfo info, ReadOnlySpan<byte> data) =>
        new(info.Width, info.Height, info.Components == 1 ? 1 : 3, SampleFormat.UInt8)
        {
            Format = ImageFormat.Jpeg,
            Orientation = info.Orientation,
            Resolution = JpegMetadata.ReadResolution(data),
            HasThumbnail = JpegMetadata.HasExifThumbnail(data),
        };

    /// <summary>The <see cref="JpegPixelFormat"/> writing the same bytes as <paramref name="format"/>, one of the five
    /// <see cref="PixelFormat.IsBasic8"/> formats.</summary>
    internal static JpegPixelFormat ToJpegFormat(PixelFormat format) =>
        format == PixelFormat.Rgba32 ? JpegPixelFormat.Rgba32 :
        format == PixelFormat.Bgra32 ? JpegPixelFormat.Bgra32 :
        format == PixelFormat.Rgb24 ? JpegPixelFormat.Rgb24 :
        format == PixelFormat.Bgr24 ? JpegPixelFormat.Bgr24 :
        format == PixelFormat.Gray8 ? JpegPixelFormat.Gray8 :
        throw new ArgumentOutOfRangeException(nameof(format), format, "JPEG decodes natively to the five basic 8-bit formats only.");

    private static ImageMetadata ReadMetadata(ReadOnlySpan<byte> data) =>
        new(JpegMetadata.ReadExif(data), JpegMetadata.ReadXmp(data));

    private static RasterImage Crop(RasterImage raster, PixelRect region, System.Buffers.ArrayPool<byte>? pool)
    {
        using (raster)
            return RasterImage.Crop(raster, region, pool);
    }
}
