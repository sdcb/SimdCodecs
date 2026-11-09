using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

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
    /// <see cref="IImageDecoder.TryDecode"/> by way of the codec's <see cref="IImageDecoder.Decode"/>: false where it
    /// throws <see cref="System.IO.InvalidDataException"/>. Every other exception (a region outside the image, a
    /// cancelled token) passes through.
    /// </summary>
    /// <param name="decode">The codec's <see cref="IImageDecoder.Decode"/>.</param>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="image">The decoded image, or null on false.</param>
    /// <param name="options">The decode options.</param>
    /// <param name="cancellationToken">Passed on to <paramref name="decode"/>.</param>
    public static bool TryDecode(ImageDecoderRegistration.DecodeHandler decode, ReadOnlySpan<byte> data,
        [NotNullWhen(true)] out IDecodedImage? image, DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decode);
        try
        {
            image = decode(data, options, cancellationToken);
            return true;
        }
        catch (System.IO.InvalidDataException)
        {
            image = null;
            return false;
        }
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

    // DecodeOptions.ApplyOrientation, for every codec that reports an orientation. Each codec entry point that takes
    // options starts with `if (options is { ApplyOrientation: true }) return ImageDecoderFallback.XxxOriented(...)`,
    // handing over itself: the helper reads the orientation and calls the codec back with the option off (so the call
    // back takes the codec's ordinary path) and the region mapped to stored coordinates, then turns the result. An
    // image whose orientation is Normal is decoded by that call back alone, without a copy.

    /// <summary>
    /// <see cref="IImageDecoder.Decode"/> with <see cref="DecodeOptions.ApplyOrientation"/>: reads the orientation with
    /// <paramref name="readInfo"/>, decodes the stored pixels with <paramref name="decode"/> (the option off, the region
    /// mapped to stored coordinates), and turns them with <see cref="PixelTransform"/> (the Exif Orientation set to 1).
    /// Without the option, or for <see cref="ImageOrientation.Normal"/>, it is <paramref name="decode"/> alone.
    /// </summary>
    /// <param name="decode">The codec's <see cref="IImageDecoder.Decode"/>; it is called with the option off.</param>
    /// <param name="readInfo">The codec's <see cref="IImageDecoder.TryReadInfo"/>.</param>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="options">The decode options.</param>
    /// <param name="cancellationToken">Passed on to <paramref name="decode"/>.</param>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the displayed image.</exception>
    public static IDecodedImage DecodeOriented(ImageDecoderRegistration.DecodeHandler decode,
        ImageDecoderRegistration.TryReadInfoHandler readInfo, ReadOnlySpan<byte> data, DecodeOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(readInfo);
        if (options is not { ApplyOrientation: true })
            return decode(data, options, cancellationToken);
        if (!readInfo(data, out var info))
            return decode(data, options with { ApplyOrientation = false }, cancellationToken); // throws the codec's reason
        if (!TryPlan(options, info, out var stored))
            return decode(data, stored, cancellationToken);
        using var image = decode(data, stored, cancellationToken);
        return PixelTransform.Orient(image, info.Orientation, options.Pool);
    }

    /// <summary><see cref="IImageDecoder.TryDecode"/> with <see cref="DecodeOptions.ApplyOrientation"/>, as
    /// <see cref="DecodeOriented"/>; false when the header or the decode fails.</summary>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the displayed image.</exception>
    public static bool TryDecodeOriented(ImageDecoderRegistration.TryDecodeHandler decode,
        ImageDecoderRegistration.TryReadInfoHandler readInfo, ReadOnlySpan<byte> data,
        [NotNullWhen(true)] out IDecodedImage? image, DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decode);
        ArgumentNullException.ThrowIfNull(readInfo);
        if (options is not { ApplyOrientation: true })
            return decode(data, out image, options, cancellationToken);
        image = null;
        if (!readInfo(data, out var info))
            return false;
        if (!TryPlan(options, info, out var stored))
            return decode(data, out image, stored, cancellationToken);
        if (!decode(data, out var storedImage, stored, cancellationToken))
            return false;
        using (storedImage)
            image = PixelTransform.Orient(storedImage, info.Orientation, options.Pool);
        return true;
    }

    /// <summary>
    /// <see cref="IImageDecoder.TryDecodeInto"/> with <see cref="DecodeOptions.ApplyOrientation"/>: the destination
    /// holds the displayed size (<see cref="DecodeOptions.GetOutputSize(in ImageInfo)"/>). For an orientation other than
    /// <see cref="ImageOrientation.Normal"/> the stored pixels are decoded with <paramref name="decodeInto"/> into a
    /// buffer rented from <see cref="ArrayPool{T}.Shared"/>, in <paramref name="format"/>, then turned into the
    /// destination; for Normal, <paramref name="decodeInto"/> writes the destination itself.
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the displayed image.</exception>
    public static bool TryDecodeIntoOriented(ImageDecoderRegistration.TryDecodeIntoHandler decodeInto,
        ImageDecoderRegistration.TryReadInfoHandler readInfo, ReadOnlySpan<byte> data, Span<byte> destination, int stride,
        PixelFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decodeInto);
        ArgumentNullException.ThrowIfNull(readInfo);
        if (options is not { ApplyOrientation: true })
            return decodeInto(data, destination, stride, format, options, cancellationToken);
        if (!readInfo(data, out var info))
            return false;
        if (!TryPlan(options, info, out var stored))
            return decodeInto(data, destination, stride, format, stored, cancellationToken);
        if (!TryPlanInto(info, options, stored, destination.Length, stride, format, out var w, out var h, out var rowStride))
            return false;
        var length = w * h * format.BytesPerPixel;
        var temp = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (!decodeInto(data, temp.AsSpan(0, length), 0, format, stored, cancellationToken))
                return false;
            PixelTransform.Orient(temp.AsSpan(0, length), w, h, 0, format, info.Orientation, destination, rowStride);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    /// <summary><see cref="IImageFrameReader.TryDecodeFrame"/> with <see cref="DecodeOptions.ApplyOrientation"/>, as
    /// <see cref="TryDecodeOriented"/>, on the orientation <see cref="IImageFrameReader.TryGetFrameInfo"/> reports.
    /// <paramref name="reader"/>'s <see cref="IImageFrameReader.TryDecodeFrame"/> is called with the option off.</summary>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the displayed frame.</exception>
    public static bool TryDecodeFrameOriented(IImageFrameReader reader, int index, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (options is not { ApplyOrientation: true })
            return reader.TryDecodeFrame(index, out image, options, cancellationToken);
        image = null;
        if (!reader.TryGetFrameInfo(index, out var frame))
            return false;
        if (!TryPlan(options, frame.Info, out var stored))
            return reader.TryDecodeFrame(index, out image, stored, cancellationToken);
        if (!reader.TryDecodeFrame(index, out var storedImage, stored, cancellationToken))
            return false;
        using (storedImage)
            image = PixelTransform.Orient(storedImage, frame.Info.Orientation, options.Pool);
        return true;
    }

    /// <summary><see cref="IImageFrameReader.TryDecodeFrameInto"/> with <see cref="DecodeOptions.ApplyOrientation"/>, as
    /// <see cref="TryDecodeIntoOriented"/>, on the orientation <see cref="IImageFrameReader.TryGetFrameInfo"/> reports.
    /// <paramref name="reader"/>'s <see cref="IImageFrameReader.TryDecodeFrameInto"/> is called with the option off.</summary>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the displayed frame.</exception>
    public static bool TryDecodeFrameIntoOriented(IImageFrameReader reader, int index, Span<byte> destination, int stride,
        PixelFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (options is not { ApplyOrientation: true })
            return reader.TryDecodeFrameInto(index, destination, stride, format, options, cancellationToken);
        if (!reader.TryGetFrameInfo(index, out var frame))
            return false;
        var info = frame.Info;
        if (!TryPlan(options, info, out var stored))
            return reader.TryDecodeFrameInto(index, destination, stride, format, stored, cancellationToken);
        if (!TryPlanInto(info, options, stored, destination.Length, stride, format, out var w, out var h, out var rowStride))
            return false;
        var length = w * h * format.BytesPerPixel;
        var temp = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            if (!reader.TryDecodeFrameInto(index, temp.AsSpan(0, length), 0, format, stored, cancellationToken))
                return false;
            PixelTransform.Orient(temp.AsSpan(0, length), w, h, 0, format, info.Orientation, destination, rowStride);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    // The options that decode the stored pixels for `options` (ApplyOrientation set): the option off and, for an
    // orientation other than Normal, the region mapped from displayed to stored coordinates. False when there is
    // nothing to turn (Normal). Checks the region against the displayed image.
    private static bool TryPlan(DecodeOptions options, in ImageInfo info, out DecodeOptions stored)
    {
        options.Validate(info);
        var orientation = info.Orientation;
        if (orientation == ImageOrientation.Normal)
        {
            stored = options with { ApplyOrientation = false };
            return false;
        }
        var region = options.Region;
        if (region is { } r)
        {
            var (w, h) = options.GetScaledSize(info.Width, info.Height);
            region = PixelTransform.ToStoredRect(r, w, h, orientation);
        }
        stored = options with { ApplyOrientation = false, Region = region };
        return true;
    }

    // The stored size the decode into a temporary produces, and the destination's row stride for the displayed size;
    // false when the destination does not hold the displayed image. The destination then holds the same pixels as the
    // temporary, so its tightly packed length fits an int.
    private static bool TryPlanInto(in ImageInfo info, DecodeOptions options, DecodeOptions stored, int destinationLength,
        int stride, PixelFormat format, out int width, out int height, out int rowStride)
    {
        (width, height) = stored.GetOutputSize(info.Width, info.Height);
        var (dw, dh) = options.GetOutputSize(info);
        return PixelConverter.TryGetLayout(dw, dh, format, stride, destinationLength, out rowStride);
    }

    /// <summary>
    /// <see cref="IImageDecoder.TryGetClosestSize"/> for a codec that does not scale natively: the stored size, at
    /// scale 1, whatever was requested (other scales are accepted, but cost a full decode and the box filter).
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
