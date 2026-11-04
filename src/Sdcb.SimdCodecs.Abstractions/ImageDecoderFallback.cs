using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The generic paths an <see cref="IImageDecoder"/> implementation falls back on for what it has no native code
/// for: a pixel format its decode-into path does not write, a region it cannot decode on its own, a size it does
/// not scale to. Each gives exactly the result the contract defines, only without a speed-up.
/// </summary>
public static class ImageDecoderFallback
{
    /// <summary>
    /// <see cref="IImageDecoder.TryDecodeInto"/> by way of a full <see cref="IImageDecoder.TryDecode"/> with the same
    /// options, then <see cref="PixelConverter.TryConvert"/>. The decoded image is disposed.
    /// </summary>
    /// <param name="decode">The codec's <see cref="IImageDecoder.TryDecode"/>.</param>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="destination">The caller's buffer.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="format">The layout to write.</param>
    /// <param name="options">The decode options (the pool is ignored).</param>
    /// <param name="cancellationToken">Passed on to <paramref name="decode"/>.</param>
    public static bool TryDecodeInto(ImageDecoderRegistration.TryDecodeHandler decode, ReadOnlySpan<byte> data,
        Span<byte> destination, int stride, PixelFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decode);
        if (options?.Pool is not null)
            options = options with { Pool = null };
        if (!decode(data, out var image, options, cancellationToken))
            return false;
        using (image)
            return PixelConverter.TryConvert(image, destination, stride, format);
    }

    /// <summary>
    /// <see cref="IImageFrameReader.TryDecodeFrameInto"/> by way of <see cref="IImageFrameReader.TryDecodeFrame"/>,
    /// then <see cref="PixelConverter.TryConvert"/>.
    /// </summary>
    public static bool TryDecodeFrameInto(IImageFrameReader reader, int index, Span<byte> destination, int stride,
        PixelFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (options?.Pool is not null)
            options = options with { Pool = null };
        if (!reader.TryDecodeFrame(index, out var image, options, cancellationToken))
            return false;
        using (image)
            return PixelConverter.TryConvert(image, destination, stride, format);
    }

    /// <summary>
    /// <see cref="IImageDecoder.TryGetClosestSize"/> for a codec that does not scale natively: the stored size, at
    /// scale 1, whatever was requested.
    /// </summary>
    /// <param name="readInfo">The codec's <see cref="IImageDecoder.TryReadInfo"/>.</param>
    /// <param name="data">The encoded image.</param>
    /// <param name="width">The stored width.</param>
    /// <param name="height">The stored height.</param>
    /// <param name="scaleDenominator">Always 1.</param>
    public static bool TryGetStoredSize(ImageDecoderRegistration.TryReadInfoHandler readInfo, ReadOnlySpan<byte> data,
        out int width, out int height, out int scaleDenominator)
    {
        ArgumentNullException.ThrowIfNull(readInfo);
        scaleDenominator = 1;
        if (!readInfo(data, out var info))
        {
            width = height = 0;
            return false;
        }
        (width, height) = (info.Width, info.Height);
        return true;
    }

    /// <summary>
    /// <see cref="IImageDecoder.TryReadMetadata"/> for a format that carries no metadata blocks: <see cref="ImageMetadata.Empty"/>
    /// for a readable header (or one with just the ICC profile, when given), false otherwise.
    /// </summary>
    public static bool TryReadNoMetadata(ImageDecoderRegistration.TryReadInfoHandler readInfo, ReadOnlySpan<byte> data,
        [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(readInfo);
        metadata = readInfo(data, out _) ? ImageMetadata.Empty : null;
        return metadata is not null;
    }

    /// <summary><see cref="IImageDecoder.TryDecodeThumbnail"/> for a format without embedded previews: always false.</summary>
    public static bool NoThumbnail([NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        thumbnail = null;
        return false;
    }
}
