using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for WebP, bridging <see cref="WebpDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// Lossy (VP8), lossless (VP8L) and animated input are all accepted; an animation is presented as
/// its first composited frame, and <see cref="TryOpenFrames"/> reads all of them. Images with alpha decode as 8-bit RGBA, opaque ones as 8-bit RGB, an
/// ICCP profile is passed through, and the colour encoding is reported as assumed sRGB. Malformed or
/// unsupported data makes the <c>Try</c> methods return false rather than throw.
/// </para>
/// </summary>
public sealed class WebpImageDecoder : IMultiFrameImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 12;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= SignatureLength &&
        data[..4].SequenceEqual("RIFF"u8) &&
        data[8..12].SequenceEqual("WEBP"u8);

    /// <inheritdoc />
    /// <remarks>A still image is one frame. An animation's frames are its composited canvas, in the
    /// container's channel count, streamed through one <see cref="WebpAnimationDecoder"/>: decoding them in
    /// order composites each once, and any other order replays from the nearest independent frame.</remarks>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        try
        {
            reader = WebpFrameReader.Open(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            reader = null;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            WebpContainer container = WebpContainer.Read(data);
            WebpInfo webpInfo = container.Info;
            info = new ImageInfo(webpInfo.Width, webpInfo.Height, webpInfo.HasAlpha ? 4 : 3, SampleFormat.UInt8)
            {
                Format = ImageFormat.Webp,
                Orientation = container.HasExif ? ExifOrientation.Read(container.Exif) : ImageOrientation.Normal,
                FrameCount = Math.Max(1, webpInfo.FrameCount),
                ColorEncoding = ColorEncoding.AssumedSrgb,
            };
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
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
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        image = null;
        WebpContainer container;
        try
        {
            container = WebpContainer.Read(data);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        var webpInfo = container.Info;
        options?.Validate(webpInfo.Width, webpInfo.Height, supportsScale: false);

        int channels = webpInfo.HasAlpha ? 4 : 3;
        ImageMetadata? metadata = (container.HasExif || container.HasXmp || container.HasIccProfile)
            ? new ImageMetadata(
                container.HasExif ? container.Exif.ToArray() : default,
                container.HasXmp ? container.Xmp.ToArray() : default,
                container.HasIccProfile ? container.IccProfile.ToArray() : default)
            : null;
        byte[]? icc = container.HasIccProfile ? container.IccProfile.ToArray() : null;

        var raster = RasterImage.Create(webpInfo.Width, webpInfo.Height, channels, SampleFormat.UInt8, options?.Pool,
            iccProfile: icc, colorEncoding: ColorEncoding.AssumedSrgb, bitDepth: 8, metadata: metadata);

        try
        {
            if (channels == 4)
            {
                WebpDecoder.DecodeTo(data, raster.WritablePixels, stride: 0, fancy: true,
                    ablation: WebpAblation.None, multithreaded: false, cancellationToken: cancellationToken);
            }
            else
            {
                int rgbaRow = checked(webpInfo.Width * 4);
                byte[] rented = ArrayPool<byte>.Shared.Rent(checked(rgbaRow * webpInfo.Height));
                try
                {
                    WebpDecoder.DecodeTo(data, rented, rgbaRow, fancy: true,
                        ablation: WebpAblation.None, multithreaded: false, cancellationToken: cancellationToken);
                    int rgbRow = checked(webpInfo.Width * 3);
                    for (int y = 0; y < webpInfo.Height; y++)
                    {
                        PixelConverter.ConvertRow(rented.AsSpan(y * rgbaRow, rgbaRow), PixelFormat.Rgba32,
                            raster.WritablePixels.Slice(y * rgbRow, rgbRow), PixelFormat.Rgb24, webpInfo.Width);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }
        catch (Exception e)
        {
            raster.Dispose();
            if (e is InvalidDataException or ArgumentException or NotSupportedException)
                return false;
            throw;
        }

        image = DecodeOptions.Apply(raster, options);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);

        if (!WebpDecoder.TryReadInfo(data, out WebpInfo info))
        {
            return false;
        }

        options?.Validate(info.Width, info.Height, supportsScale: false);

        if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out int rowStride))
        {
            return false;
        }

        byte[]? rented = null;
        try
        {
            int rgbaRow = checked(info.Width * 4);
            if (format == PixelFormat.Rgba32 || format == PixelFormat.Bgra32)
            {
                WebpDecoder.DecodeTo(data, destination, rowStride, fancy: true,
                    ablation: WebpAblation.None, multithreaded: false, cancellationToken: cancellationToken);
                if (format == PixelFormat.Bgra32)
                {
                    for (int y = 0; y < info.Height; y++)
                    {
                        Span<byte> row = destination.Slice(y * rowStride, rgbaRow);
                        PixelConverter.ConvertRow(row, PixelFormat.Rgba32, row, PixelFormat.Bgra32, info.Width);
                    }
                }

                return true;
            }

            rented = ArrayPool<byte>.Shared.Rent(checked(rgbaRow * info.Height));
            WebpDecoder.DecodeTo(data, rented, rgbaRow, fancy: true,
                ablation: WebpAblation.None, multithreaded: false, cancellationToken: cancellationToken);
            int rowBytes = checked(info.Width * format.BytesPerPixel);
            for (int y = 0; y < info.Height; y++)
            {
                PixelConverter.ConvertRow(rented.AsSpan(y * rgbaRow, rgbaRow), PixelFormat.Rgba32,
                    destination.Slice(y * rowStride, rowBytes), format, info.Width);
            }

            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <inheritdoc />
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        try
        {
            var container = WebpContainer.Read(data);
            if (!container.HasIccProfile && !container.HasExif && !container.HasXmp)
            {
                metadata = ImageMetadata.Empty;
                return true;
            }

            metadata = new ImageMetadata(
                container.HasExif ? container.Exif.ToArray() : default,
                container.HasXmp ? container.Xmp.ToArray() : default,
                container.HasIccProfile ? container.IccProfile.ToArray() : default);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            metadata = null;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    /// <summary>The registration for <c>SimdCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new(ImageFormat.Webp, CanDecode, TryReadInfo,
        TryGetClosestSize, TryDecode, TryDecodeInto, TryReadMetadata, TryDecodeThumbnail, TryOpenFrames);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA (an animation's first frame for animated
    /// input). Returns false, without throwing, for malformed or unsupported data or a destination
    /// shorter than width × height × 4 bytes.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="destination">At least width × height × 4 bytes.</param>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> destination) =>
        TryDecodeInto(data, destination, 0, PixelFormat.Rgba32);
}
