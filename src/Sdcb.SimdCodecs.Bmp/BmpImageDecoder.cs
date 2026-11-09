using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for BMP, bridging <see cref="BmpDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade. A bitmap decodes as 8-bit RGB, or RGBA when it can carry alpha
/// (<see cref="BmpInfo.HasAlpha"/>), with a V5 header's embedded ICC profile. <see cref="TryDecodeInto"/>
/// writes the caller's buffer directly. Malformed data makes the <c>Try</c> methods return false rather
/// than throw.
/// </summary>
public sealed class BmpImageDecoder : IImageDecoder
{
    /// <summary>The registration for <c>ImageCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Bmp,
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
    /// <remarks>The 14-byte file header and the bitmap header's size field.</remarks>
    public static int SignatureLength => 18;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => BmpHeader.HasSignature(header);

    // A V5 header's embedded ICC profile is the only colour signalling a BMP has: its primaries, when H.273 names them.
    private static ColorEncoding ColorOf(ReadOnlySpan<byte> data, BmpHeader header) =>
        BmpDecoder.Icc(data, header) is { } icc ? IccPrimaries.Encoding(icc) ?? ColorEncoding.AssumedSrgb
            : ColorEncoding.AssumedSrgb;

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            var header = BmpHeader.ReadFile(data);
            var bmp = BmpDecoder.Info(data, header);
            info = new ImageInfo(bmp.Width, bmp.Height, bmp.Channels, SampleFormat.UInt8)
            {
                Format = ImageFormat.Bmp,
                ColorEncoding = ColorOf(data, header),
                BitDepth = BmpDecoder.BitDepth(header),
                Resolution = header.XPelsPerMeter == 0 && header.YPelsPerMeter == 0 ? null
                    : new Resolution(header.XPelsPerMeter, header.YPelsPerMeter, ResolutionUnit.PerMeter),
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
        var header = BmpHeader.ReadFile(data);
        var bmp = BmpDecoder.Info(data, header);
        options?.Validate(bmp.Width, bmp.Height);
        var raster = RasterImage.Create(bmp.Width, bmp.Height, bmp.Channels, SampleFormat.UInt8, options?.Pool,
            colorEncoding: ColorOf(data, header),
            metadata: BmpDecoder.Icc(data, header) is { } icc ? new ImageMetadata { Icc = icc } : null);
        try
        {
            BmpPixels.Decode(data, header, raster.WritablePixels, bmp.Width * bmp.Channels,
                bmp.Channels == 4 ? PixelFormat.Rgba32 : PixelFormat.Rgb24, cancellationToken);
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
        if (options is { IsFullImage: false } || !format.IsBasic8)
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);
        try
        {
            var header = BmpHeader.ReadFile(data);
            if (!PixelConverter.TryGetLayout(header.Width, header.Height, format, stride, destination.Length, out var rowStride))
                return false;
            BmpPixels.Decode(data, header, destination, rowStride, format, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>The ICC profile a V5 header embeds; a bitmap without one gives <see cref="ImageMetadata.Empty"/>.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        try
        {
            var icc = BmpDecoder.Icc(data, BmpHeader.ReadFile(data));
            metadata = icc is null ? ImageMetadata.Empty : new ImageMetadata { Icc = icc };
            return true;
        }
        catch (InvalidDataException)
        {
            metadata = null;
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>BMP has no thumbnails: always false.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);
}
