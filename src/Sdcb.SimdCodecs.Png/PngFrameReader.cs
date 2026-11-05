using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// <see cref="IImageFrameReader"/> for PNG (<see cref="PngImageDecoder.TryOpenFrames"/>). A PNG that is not animated is
/// one frame, decoded exactly as <see cref="PngImageDecoder"/> decodes it. An APNG's frames are the composited canvas of
/// a <see cref="PngAnimationDecoder"/>, converted from its working canvas a row at a time: RGBA, or RGB when no pixel of the
/// animation is ever transparent (<see cref="PngAnimationDecoder.HasAlpha"/>), at 8 bits, or 16 for a 16-bit image. When the
/// default image is not part of the animation the reader's frames are the <c>fcTL</c> frames only, and
/// <see cref="PngImageDecoder.TryDecode"/> keeps returning the default image.
/// </summary>
internal sealed class PngFrameReader : IImageFrameReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ImageInfo _frameInfo;
    private readonly bool _displayReferred;
    private readonly ImageMetadata _metadata;
    private PngAnimationDecoder? _animation; // null for a still image, and once disposed
    private bool _disposed;

    private PngFrameReader(ReadOnlyMemory<byte> data, PngAnimationDecoder animation)
    {
        _data = data;
        _animation = animation;
        var header = animation.Header;
        var channels = animation.HasAlpha ? 4 : 3;
        _frameInfo = new ImageInfo(animation.CanvasWidth, animation.CanvasHeight, channels,
            animation.BitDepth == 16 ? SampleFormat.UInt16 : SampleFormat.UInt8)
        {
            Format = ImageFormat.Png,
            BitDepth = animation.BitDepth,
            Orientation = header.Exif is { } exif ? ExifOrientation.Read(exif) : ImageOrientation.Normal,
            ColorEncoding = PngImageDecoder.MapColorEncoding(header.Cicp, header.Gamma),
            FrameCount = 1,
        };
        _displayReferred = header.Cicp is not { TransferFunction: TransferFunction.Pq or TransferFunction.Hlg };
        _metadata = PngImageDecoder.MetadataOf(header);
        FrameCount = animation.FrameCount;
        LoopCount = animation.IsAnimated ? animation.LoopCount : null;
    }

    /// <summary>Opens <paramref name="data"/>; throws <see cref="InvalidDataException"/> for a malformed file.</summary>
    public static PngFrameReader Open(ReadOnlyMemory<byte> data)
    {
        var animation = new PngAnimationDecoder(data);
        return new PngFrameReader(data, animation);
    }

    public int FrameCount { get; }

    public int? LoopCount { get; }

    public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
    {
        var animation = Check(index);
        if (!animation.IsAnimated)
        {
            if (!PngImageDecoder.TryReadInfo(_data.Span, out var info))
            {
                frame = default;
                return false;
            }
            frame = new ImageFrameInfo(info with { FrameCount = 1 }, null);
            return true;
        }
        frame = new ImageFrameInfo(_frameInfo, animation.Frames[index].Delay);
        return true;
    }

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var animation = Check(index);
        if (!animation.IsAnimated)
            return PngImageDecoder.TryDecode(_data.Span, out image, options, cancellationToken);

        image = null;
        options?.Validate(_frameInfo.Width, _frameInfo.Height, supportsScale: false);
        if (!TryComposite(animation, index, cancellationToken)) return false;

        int width = _frameInfo.Width, height = _frameInfo.Height;
        var source = animation.BitDepth == 16 ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        var target = ImageFormatOf(source);
        var raster = RasterImage.Create(width, height, _frameInfo.Channels, _frameInfo.SampleFormat, options?.Pool,
            colorEncoding: _frameInfo.ColorEncoding, bitDepth: _frameInfo.BitDepth, metadata: _metadata);
        try
        {
            var canvas = animation.Canvas;
            int srcRow = checked(width * source.BytesPerPixel), dstRow = checked(width * target.BytesPerPixel);
            for (var y = 0; y < height; y++)
            {
                if ((y & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                PixelConverter.ConvertRow(canvas.Slice(y * srcRow, srcRow), source,
                    raster.WritablePixels.Slice(y * dstRow, dstRow), target, width);
            }
        }
        catch (Exception)
        {
            raster.Dispose();
            throw;
        }

        image = DecodeOptions.Apply(raster, options);
        return true;
    }

    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var animation = Check(index);
        if (!animation.IsAnimated)
            return PngImageDecoder.TryDecodeInto(_data.Span, destination, stride, format, options, cancellationToken);

        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeFrameInto(this, index, destination, stride, format, options, cancellationToken);
        options?.Validate(_frameInfo.Width, _frameInfo.Height, supportsScale: false);

        // As for a still: 8-bit samples are a display projection, which an HDR-encoded file has none of.
        if (format.Sample == SampleFormat.UInt8 && !_displayReferred) return false;
        int width = _frameInfo.Width, height = _frameInfo.Height;
        if (!PixelConverter.TryGetLayout(width, height, format, stride, destination.Length, out var rowStride))
            return false;
        if (!TryComposite(animation, index, cancellationToken)) return false;

        var source = animation.BitDepth == 16 ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        var canvas = animation.Canvas;
        int srcRow = checked(width * source.BytesPerPixel), dstRow = checked(width * format.BytesPerPixel);
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            PixelConverter.ConvertRow(canvas.Slice(y * srcRow, srcRow), source,
                destination.Slice(y * rowStride, dstRow), format, width);
        }
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _animation = null; // the canvas goes with it
    }

    // The raster layout of the canvas: RGB when the animation never shows transparency.
    private PixelFormat ImageFormatOf(PixelFormat rgba) =>
        _frameInfo.Channels == 4 ? rgba : new PixelFormat(ChannelLayout.Rgb, rgba.Sample);

    private PngAnimationDecoder Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _animation!;
    }

    // A frame that fails leaves the decoder where it was (its pixels are decoded before the canvas is touched).
    private static bool TryComposite(PngAnimationDecoder animation, int index, CancellationToken cancellationToken)
    {
        try
        {
            animation.DecodeFrame(index, cancellationToken);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
