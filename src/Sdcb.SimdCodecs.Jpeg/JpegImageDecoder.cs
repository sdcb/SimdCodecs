using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG, bridging <see cref="JpegDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. A greyscale JPEG (one SOF component)
/// decodes to a 1-channel <see cref="SampleFormat.UInt8"/> raster of its samples;
/// every other JPEG (YCbCr, RGB, CMYK, YCCK) to 3-channel RGB - JPEG has no alpha. The
/// embedded APP2 ICC profile, if any, comes back as <see cref="IDecodedImage.Metadata"/>.<see cref="ImageMetadata.Icc"/>.
/// <para>
/// <see cref="TryDecodeInto"/> is a genuine zero-copy decode for the five basic 8-bit
/// <see cref="PixelFormat"/>s (<see cref="PixelFormat.IsBasic8"/>) via
/// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, JpegPixelFormat, JpegScale)"/>; the other
/// formats (16-bit, premultiplied, grey + alpha) are the 8-bit decode widened by <see cref="PixelConverter"/>.
/// JPEG scales natively in the DCT domain: <see cref="DecodeOptions.ScaleDenominator"/> 2, 4 and 8 decode a
/// smaller image at a fraction of the cost (<see cref="TryGetClosestSize"/>); 16 and above decode at 1/8, then apply
/// the box filter of <see cref="BoxDownscaler"/> for the rest.
/// </para>
/// </summary>
public sealed class JpegImageDecoder : IImageDecoder
{
    private static ReadOnlySpan<byte> Signature => [0xFF, 0xD8, 0xFF];

    /// <summary>The registration for <c>ImageCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Jpeg,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
    };

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
    /// directions (the stored size when the request is larger). Smaller scales are accepted too, but cost the 1/8
    /// decode and a box filter.</remarks>
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
        options?.Validate(info.Width, info.Height);
        // Scales up to 1/8 come from the DCT domain; beyond that the 1/8 decode is box-filtered by the rest.
        var native = options?.GetNativeScale(8) ?? 1;
        var factor = (options?.ScaleDenominator ?? 1) / native;
        var scale = (JpegScale)native;
        var (width, height) = info.ScaledSize(scale);

        // A one-component frame keeps its samples as stored (Gray8 output is exactly its Y);
        // every other frame is colour, written as RGB since JPEG carries no alpha.
        var (channels, format) = info.Components == 1 ? (1, JpegPixelFormat.Gray8) : (3, JpegPixelFormat.Rgb24);
        // A region decodes natively (only the region's pixels are ever held), so the raster is the output's size (at a
        // box-filtered scale, the native pixels the region's boxes cover). One that does not fit a single array (a
        // gigapixel JPEG at 1/1) is refused before anything is allocated.
        PixelRect? nativeRegion = options?.Region is { } scaled
            ? factor == 1 ? scaled : BoxDownscaler.GetSourceRect(scaled, width, height, factor)
            : null;
        var region = nativeRegion ?? new PixelRect(0, 0, width, height);
        if ((long)region.Width * region.Height * channels > Array.MaxLength)
            return false;
        var metadata = ReadMetadata(data);
        var raster = RasterImage.Create(region.Width, region.Height, channels, SampleFormat.UInt8, factor == 1 ? options?.Pool : null,
            colorEncoding: IccPrimaries.Encoding(metadata.Icc.Span), metadata: metadata);
        try
        {
            if (nativeRegion is not null)
            {
                JpegDecoder.DecodeRegionTo(data, raster.WritablePixels, region.Width * channels, format, scale,
                    region.X, region.Y, region.Width, region.Height, cancellationToken);
            }
            else
            {
                _ = JpegDecoder.DecodeTo(data, raster.WritablePixels, width * channels, format, scale, cancellationToken);
            }
        }
        catch (Exception e)
        {
            raster.Dispose();
            if (e is InvalidDataException)
                return false;
            throw;
        }

        if (factor == 1)
        {
            image = raster;
            return true;
        }
        using (raster)
            image = BoxDownscaler.Downscale(raster, factor, pool: options!.Pool);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!format.IsBasic8 || options is { ScaleDenominator: > 8 })
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);
        if (!JpegDecoder.TryReadInfo(data, out var info))
            return false;
        options?.Validate(info.Width, info.Height);
        var scale = (JpegScale)(options?.ScaleDenominator ?? 1);
        var (width, height) = info.ScaledSize(scale);
        if (options?.Region is { } region)
            return TryDecodeRegionInto(data, destination, stride, format, scale, region, cancellationToken);
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

    // A region: decoded natively straight into the destination. The decoder still entropy-decodes every MCU (a
    // baseline stream has no index to seek by) but writes, upsamples and colour-converts only the region's rows and
    // keeps only MCU-row strips, so a region of a gigapixel image needs no whole-image buffer. Its pixels are exactly
    // those of the whole decode, Gray8 included (still the stored Y plane).
    private static bool TryDecodeRegionInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        JpegScale scale, PixelRect region, CancellationToken cancellationToken)
    {
        if (!PixelConverter.TryGetLayout(region.Width, region.Height, format, stride, destination.Length, out var rowStride))
            return false;
        try
        {
            JpegDecoder.DecodeRegionTo(data, destination, rowStride, ToJpegFormat(format), scale,
                region.X, region.Y, region.Width, region.Height, cancellationToken);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>The APP1 Exif block (from its TIFF header, without the <c>Exif\0\0</c> prefix), the APP1 XMP packet with
    /// any Extended XMP it names joined in (the extended <c>rdf:RDF</c> content inserted before the standard packet's
    /// <c>&lt;/rdf:RDF&gt;</c>; the standard packet alone when the extension is incomplete or fails its MD5), and the
    /// APP2 ICC profile, reassembled from its chunks.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = null;
        if (!JpegDecoder.TryReadInfo(data, out _))
            return false;
        metadata = ReadMetadata(data);
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
    internal static ImageInfo ToImageInfo(JpegInfo info, ReadOnlySpan<byte> data)
    {
        var imageInfo = new ImageInfo(info.Width, info.Height, info.Components == 1 ? 1 : 3, SampleFormat.UInt8)
        {
            Format = ImageFormat.Jpeg,
            Orientation = info.Orientation,
            Resolution = JpegMetadata.ReadResolution(data),
            HasThumbnail = JpegMetadata.HasExifThumbnail(data),
        };
        // JPEG has no colour signalling of its own: the ICC profile's primaries, when it declares ones H.273 names.
        return JpegDecoder.TryReadIccProfile(data, out var icc) && IccPrimaries.Encoding(icc) is { } encoding
            ? imageInfo with { ColorEncoding = encoding }
            : imageInfo;
    }

    /// <summary>The <see cref="JpegPixelFormat"/> writing the same bytes as <paramref name="format"/>, one of the five
    /// <see cref="PixelFormat.IsBasic8"/> formats.</summary>
    internal static JpegPixelFormat ToJpegFormat(PixelFormat format) =>
        format == PixelFormat.Rgba32 ? JpegPixelFormat.Rgba32 :
        format == PixelFormat.Bgra32 ? JpegPixelFormat.Bgra32 :
        format == PixelFormat.Rgb24 ? JpegPixelFormat.Rgb24 :
        format == PixelFormat.Bgr24 ? JpegPixelFormat.Bgr24 :
        format == PixelFormat.Gray8 ? JpegPixelFormat.Gray8 :
        throw new ArgumentOutOfRangeException(nameof(format), format, "JPEG decodes natively to the five basic 8-bit formats only.");

    private static ImageMetadata ReadMetadata(ReadOnlySpan<byte> data) => new()
    {
        Exif = JpegMetadata.ReadExif(data),
        Xmp = JpegMetadata.ReadXmp(data),
        Icc = JpegDecoder.TryReadIccProfile(data, out var icc) ? icc : default,
    };
}
