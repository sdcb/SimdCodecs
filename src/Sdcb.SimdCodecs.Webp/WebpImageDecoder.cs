using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

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
/// <para>
/// <see cref="DecodeOptions.Region"/> and <see cref="DecodeOptions.ScaleDenominator"/> of a lossy still decode natively:
/// only the rows the region's boxes cover are converted and box-filtered as they are decoded, and decoding stops after
/// the macroblock row holding the last of them, so the rest of the bitstream is not read or validated. The output is
/// exactly the generic path's (whole decode, box filter, crop). Lossless and animated input take the generic path.
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

    // WebP has no colour signalling besides its ICCP chunk: that profile's primaries, when H.273 names them.
    private static ColorEncoding EncodingOf(ReadOnlySpan<byte> iccProfile) =>
        IccPrimaries.Encoding(iccProfile) ?? ColorEncoding.AssumedSrgb;

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
                ColorEncoding = EncodingOf(container.HasIccProfile ? container.IccProfile : default),
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
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        ImageDecoderFallback.TryDecode(Decode, data, out image, options, cancellationToken);

    /// <inheritdoc />
    public static IDecodedImage Decode(ReadOnlySpan<byte> data, DecodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        WebpContainer container;
        try
        {
            container = WebpContainer.Read(data);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            throw Undecodable(e);
        }

        var webpInfo = container.Info;
        options?.Validate(webpInfo.Width, webpInfo.Height);

        int channels = webpInfo.HasAlpha ? 4 : 3;
        ImageMetadata? metadata = (container.HasExif || container.HasXmp || container.HasIccProfile)
            ? new ImageMetadata
            {
                Exif = container.HasExif ? container.Exif.ToArray() : default,
                Xmp = container.HasXmp ? container.Xmp.ToArray() : default,
                Icc = container.HasIccProfile ? container.IccProfile.ToArray() : default,
            }
            : null;

        var colorEncoding = EncodingOf(container.HasIccProfile ? container.IccProfile : default);
        if (options is { IsFullImage: false } && webpInfo.Format == WebpFormat.Lossy)
        {
            // A lossy still decodes the region and scale natively: only the rows its boxes cover, streamed through
            // the box filter, into a raster of the output's size.
            var (outputWidth, outputHeight) = options.GetOutputSize(webpInfo.Width, webpInfo.Height);
            var region = options.Region ?? new PixelRect(0, 0, outputWidth, outputHeight);
            var output = RasterImage.Create(outputWidth, outputHeight, channels, SampleFormat.UInt8, options.Pool,
                colorEncoding: colorEncoding, bitDepth: 8, metadata: metadata);
            try
            {
                WebpDecoder.DecodeLossyRegion(container.Vp8Bitstream, container.AlphaData, container.HasAlphaChunk,
                    webpInfo.Width, webpInfo.Height, channels, options.ScaleDenominator, region, output.WritablePixels,
                    outputWidth * channels, channels == 4 ? PixelFormat.Rgba32 : PixelFormat.Rgb24, fancy: true, cancellationToken);
            }
            catch (Exception e)
            {
                output.Dispose();
                if (e is ArgumentException or NotSupportedException)
                    throw Undecodable(e);
                throw;
            }

            return output;
        }

        var raster = RasterImage.Create(webpInfo.Width, webpInfo.Height, channels, SampleFormat.UInt8, options?.Pool,
            colorEncoding: colorEncoding, bitDepth: 8, metadata: metadata);

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
            if (e is ArgumentException or NotSupportedException)
                throw Undecodable(e);
            throw;
        }

        return DecodeOptions.Apply(raster, options);
    }

    // A decoder exception other than InvalidDataException (an argument the stream's numbers make invalid, a variant the
    // decoder does not support) as the InvalidDataException Decode throws for untrusted input.
    private static InvalidDataException Undecodable(Exception e) =>
        new(e is NotSupportedException ? $"The WebP uses a feature this decoder does not support: {e.Message}"
            : $"The WebP data is invalid: {e.Message}", e);

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options is { IsFullImage: false })
            return TryDecodeRegionInto(data, destination, stride, format, options, cancellationToken);

        if (!WebpDecoder.TryReadInfo(data, out WebpInfo info))
        {
            return false;
        }

        options?.Validate(info.Width, info.Height);

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

    // A region or scale: native for a lossy still, the generic path (whole decode, box filter, crop) otherwise. The
    // native rows are box-filtered in TryDecode's layout (RGB, or RGBA with alpha) and only then converted to the format,
    // as the generic path does.
    private static bool TryDecodeRegionInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions options, CancellationToken cancellationToken)
    {
        WebpContainer container;
        try
        {
            container = WebpContainer.Read(data);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        WebpInfo info = container.Info;
        if (info.Format != WebpFormat.Lossy)
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);

        options.Validate(info.Width, info.Height);
        var (outputWidth, outputHeight) = options.GetOutputSize(info.Width, info.Height);
        if (!PixelConverter.TryGetLayout(outputWidth, outputHeight, format, stride, destination.Length, out int rowStride))
            return false;

        int channels = info.HasAlpha ? 4 : 3;
        try
        {
            WebpDecoder.DecodeLossyRegion(container.Vp8Bitstream, container.AlphaData, container.HasAlphaChunk,
                info.Width, info.Height, channels, options.ScaleDenominator,
                options.Region ?? new PixelRect(0, 0, outputWidth, outputHeight), destination, rowStride, format,
                fancy: true, cancellationToken);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
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

            metadata = new ImageMetadata
            {
                Exif = container.HasExif ? container.Exif.ToArray() : default,
                Xmp = container.HasXmp ? container.Xmp.ToArray() : default,
                Icc = container.HasIccProfile ? container.IccProfile.ToArray() : default,
            };
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

    /// <summary>The registration for <c>ImageCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Webp,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        Decode = Decode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        TryOpenFrames = TryOpenFrames,
    };

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
