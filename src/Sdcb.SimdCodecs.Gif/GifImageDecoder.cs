using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for GIF, bridging <see cref="GifDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade. A GIF decodes as 8-bit RGB when every frame covers the canvas with no
/// transparent index (<see cref="GifInfo.HasAlpha"/> false), otherwise as 8-bit RGBA. <see cref="TryDecode"/>
/// is the first frame; <see cref="TryOpenFrames"/> reads all of them. Malformed data makes the
/// <c>Try</c> methods return false rather than throw.
/// </summary>
public sealed class GifImageDecoder : IMultiFrameImageDecoder
{
    /// <summary>The registration for <c>SimdCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Gif,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        TryOpenFrames = TryOpenFrames,
    };

    /// <inheritdoc />
    public static int SignatureLength => 6;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => GifContainer.HasSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            info = ToImageInfo(GifContainer.Read(data));
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
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        image = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryFirstFrame(data, out var canvas, out var info, cancellationToken)) return false;
        options?.Validate(info.Width, info.Height, supportsScale: false);
        var raster = GifFrameReader.ToImage(canvas, info.Channels, options?.Pool);
        image = DecodeOptions.Apply(raster, options);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);
        if (!TryReadInfo(data, out var info)
            || !PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride)
            || !TryFirstFrame(data, out var canvas, out _, cancellationToken))
            return false;
        GifFrameReader.Convert(canvas, destination, rowStride, format, cancellationToken);
        return true;
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    /// <inheritdoc />
    /// <remarks>Frames are the composited canvas. Decoding them in order draws each once; any other order
    /// replays from the nearest frame that does not depend on the ones before it.</remarks>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        try
        {
            var container = GifContainer.Read(data.Span);
            reader = new GifFrameReader(data, new GifCanvas(container), container, ToImageInfo(container) with { FrameCount = 1 });
            return true;
        }
        catch (InvalidDataException)
        {
            reader = null;
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>GIF has no metadata blocks: a readable file gives <see cref="ImageMetadata.Empty"/>.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata) =>
        ImageDecoderFallback.TryReadNoMetadata(TryReadInfo, data, out metadata);

    /// <inheritdoc />
    /// <remarks>GIF has no embedded previews: always false.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    // The first frame, composited from the span itself: a still decode keeps nothing, so nothing is copied.
    private static bool TryFirstFrame(ReadOnlySpan<byte> data, [NotNullWhen(true)] out GifCanvas? canvas, out ImageInfo info,
        CancellationToken cancellationToken = default)
    {
        canvas = null;
        info = default;
        try
        {
            var container = GifContainer.Read(data);
            info = ToImageInfo(container);
            canvas = new GifCanvas(container);
            canvas.DecodeFrame(data, 0, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // Palette entries are 8-bit, whatever the index depth.
    internal static ImageInfo ToImageInfo(GifContainer container) =>
        new(container.Width, container.Height, container.IsOpaque ? 3 : 4, SampleFormat.UInt8)
        {
            Format = ImageFormat.Gif,
            BitDepth = 8,
            FrameCount = container.Frames.Length,
            ColorEncoding = ColorEncoding.AssumedSrgb,
        };
}
