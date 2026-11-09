using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Qoi;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for QOI, bridging <see cref="QoiDecoder"/> into the <c>Sdcb.SimdCodecs</c>
/// facade. An image decodes as 8-bit RGB or RGBA, as its header declares; a linear colour space is reported as
/// a linear transfer in <see cref="IDecodedImage.ColorEncoding"/>. <see cref="TryDecodeInto"/> writes the
/// caller's buffer directly. Malformed data makes the <c>Try</c> methods return false rather than throw. QOI carries
/// no metadata and no thumbnail, and does not scale natively.
/// </summary>
public sealed class QoiImageDecoder : IImageDecoder
{
    private static readonly ColorEncoding LinearSrgb = new() { Transfer = TransferFunction.Linear };

    /// <summary>The registration for <c>ImageCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Qoi,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        Decode = Decode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
    };

    /// <inheritdoc />
    /// <remarks>The magic <c>qoif</c>.</remarks>
    public static int SignatureLength => 4;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => QoiCodec.HasSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            var qoi = QoiCodec.ReadHeader(data);
            info = new ImageInfo(qoi.Width, qoi.Height, qoi.Channels, SampleFormat.UInt8)
            {
                Format = ImageFormat.Qoi,
                BitDepth = 8,
                ColorEncoding = ColorOf(qoi.ColorSpace),
            };
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator) =>
        ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        ImageDecoderFallback.TryDecode(Decode, data, out image, options, cancellationToken);

    /// <inheritdoc />
    public static IDecodedImage Decode(ReadOnlySpan<byte> data, DecodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var info = QoiCodec.ReadHeader(data);
        options?.Validate(info.Width, info.Height);
        var raster = RasterImage.Create(info.Width, info.Height, info.Channels, SampleFormat.UInt8, options?.Pool,
            colorEncoding: ColorOf(info.ColorSpace), bitDepth: 8);
        try
        {
            QoiDecoder.Decode(data, info, raster.WritablePixels, info.Width * info.Channels,
                info.Channels == 4 ? PixelFormat.Rgba32 : PixelFormat.Rgb24, cancellationToken);
        }
        catch
        {
            raster.Dispose();
            throw;
        }
        return DecodeOptions.Apply(raster, options);
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);
        try
        {
            var info = QoiCodec.ReadHeader(data);
            if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride))
                return false;
            QoiDecoder.Decode(data, info, destination, rowStride, format, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>QOI has no metadata blocks: a readable file gives <see cref="ImageMetadata.Empty"/>.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata) =>
        ImageDecoderFallback.TryReadNoMetadata(TryReadInfo, data, out metadata);

    /// <inheritdoc />
    /// <remarks>QOI has no thumbnails: always false.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be at
    /// least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    private static ColorEncoding ColorOf(QoiColorSpace space) =>
        space == QoiColorSpace.Linear ? LinearSrgb : ColorEncoding.AssumedSrgb;
}
