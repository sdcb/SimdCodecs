using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for gain-map ("Ultra HDR") JPEGs, so the
/// <c>Sdcb.SimdCodecs</c> facade decodes them transparently. Register it
/// <em>ahead of</em> the plain <c>JpegImageDecoder</c>: its <see cref="CanDecode"/>
/// matches only a JPEG that actually carries a locatable, hdrgm-tagged gain map,
/// so ordinary JPEGs fall through to the base decoder unchanged.
/// <para>
/// The decode is split by tier, mirroring how the format is meant to be consumed
/// (see <see cref="GainMapDecodedImage"/>): the fidelity/float tier
/// (<see cref="TryDecode"/>) reconstructs the full authored HDR, while the 8-bit
/// display tier (<see cref="TryDecodeInto"/>) returns the SDR base rendition.
/// HDR-base files and any gain map that fails to reconstruct degrade gracefully to
/// the plain SDR primary rather than failing the facade.
/// </para>
/// </summary>
public sealed class GainMapImageDecoder : IImageDecoder
{
    /// <summary>The registration for <c>SimdCodecs.Register</c>, built from this type's method groups.</summary>
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
    /// <remarks>Beyond the JPEG magic this walks the APP segments to confirm a
    /// locatable gain map (<see cref="JpegGainMap.TrySplit"/>), so it needs the whole
    /// file — the facade passes it. Given only a short header prefix it returns false
    /// and the stream is handled as a plain JPEG.</remarks>
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF
        && JpegGainMap.TrySplit(header, out _, out _, out _);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        // A reconstructable gain map presents as the HDR float fidelity tier;
        // the HDR-base form (unsupported reconstruction) reports its SDR primary.
        if (!JpegImageDecoder.TryReadInfo(data, out info)) return false;
        if (JpegGainMap.TrySplit(data, out _, out _, out var metadata) && !metadata.BaseRenditionIsHdr)
        {
            info = new ImageInfo(info.Width, info.Height, 3, SampleFormat.Float32)
            {
                Format = ImageFormat.Jpeg,
                Orientation = info.Orientation,
                Resolution = info.Resolution,
                HasThumbnail = info.HasThumbnail,
                ColorEncoding = HdrEncoding(BasePrimaries(data)),
            };
        }
        return true;
    }

    /// <inheritdoc />
    /// <remarks>The reconstructed HDR is not scaled natively; the SDR base is a plain JPEG, which is (see
    /// <c>JpegImageDecoder.TryGetClosestSize</c>). A file whose fidelity tier is the HDR reports its stored size.</remarks>
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator)
    {
        if (JpegGainMap.TrySplit(data, out _, out _, out var metadata) && !metadata.BaseRenditionIsHdr)
            return ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);
        return JpegImageDecoder.TryGetClosestSize(data, requestedWidth, requestedHeight, out width, out height, out scaleDenominator);
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        image = null;

        // Full HDR when the gain map is present and reconstructable; the HDR-base
        // form is not (ReconstructHdr rejects it), so fall through to the SDR base.
        if (JpegGainMap.TrySplit(data, out _, out _, out var split) && !split.BaseRenditionIsHdr)
        {
            if (!JpegImageDecoder.TryReadInfo(data, out var info))
                return false;
            options?.Validate(info.Width, info.Height, supportsScale: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!JpegGainMap.TryRead(data, out var gainMap))
                return false;
            cancellationToken.ThrowIfCancellationRequested();
            IDecodedImage hdr = new GainMapDecodedImage(gainMap, gainMap.Metadata.HdrCapacityMax, BasePrimaries(data))
            {
                Metadata = JpegImageDecoder.TryReadMetadata(data, out var blocks) ? blocks : ImageMetadata.Empty,
            };
            image = DecodeOptions.Apply(hdr, options);
            return true;
        }

        // Graceful degradation: decode the SDR primary as a plain JPEG. This is the
        // same image a gain-map-unaware viewer sees, so nothing is lost visually.
        return JpegImageDecoder.TryDecode(data, out image, options, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>The base JPEG's blocks: its APP1 Exif and XMP (the XMP carries the gain-map description) and ICC.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata) =>
        JpegImageDecoder.TryReadMetadata(data, out metadata);

    /// <inheritdoc />
    /// <remarks>The base JPEG's Exif IFD1 thumbnail, an SDR image.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        JpegImageDecoder.TryDecodeThumbnail(data, out thumbnail);

    private static ColorEncoding HdrEncoding(ColorPrimaries primaries) =>
        new() { Primaries = primaries, Transfer = TransferFunction.Linear, Float = FloatSemantics.DisplayReferred };

    // The base's primaries from its ICC profile: untagged is sRGB (BT.709), a profile that names
    // no H.273 primaries (Adobe RGB, a display's own) is Unspecified rather than a wrong BT.709.
    private static ColorPrimaries BasePrimaries(ReadOnlySpan<byte> data)
    {
        if (!JpegDecoder.TryReadIccProfile(data, out var icc))
            return ColorPrimaries.BT709;
        return IccProfiles.TryGetPrimaries(icc, out var primaries) ? primaries : ColorPrimaries.Unspecified;
    }

    /// <inheritdoc />
    /// <remarks>The SDR base rendition: a gain-map JPEG's primary image is the standalone SDR
    /// rendition, so the plain decoder writes it directly — no gain-map parsing or HDR
    /// reconstruction. These are the pixels of <see cref="IDecodedImage.ToRgba8"/> on the image
    /// <see cref="TryDecode"/> returns. Options scale or crop the SDR base, as for a plain JPEG.</remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        JpegImageDecoder.TryDecodeInto(data, destination, stride, format, options, cancellationToken);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);
}
