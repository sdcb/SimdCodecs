using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// <see cref="IImageFrameReader"/> for WebP (<see cref="WebpImageDecoder.TryOpenFrames"/>). A still image is
/// one frame, decoded exactly as <see cref="WebpImageDecoder"/> decodes it. An animation's frames are the
/// composited canvas of a <see cref="WebpAnimationDecoder"/>, converted from its working canvas a row at a
/// time; each has the canvas's size and the container's channel count (RGB, or RGBA when the container
/// declares alpha), as frame 0 has through <see cref="WebpImageDecoder.TryDecode"/>.
/// </summary>
internal sealed class WebpFrameReader : IImageFrameReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ImageInfo _frameInfo;
    private readonly byte[]? _icc;
    private WebpAnimationDecoder? _animation; // null for a still image, and once disposed
    private bool _disposed;

    private WebpFrameReader(ReadOnlyMemory<byte> data, ImageInfo info, WebpAnimationDecoder? animation, byte[]? icc)
    {
        _data = data;
        _frameInfo = info with { FrameCount = 1 };
        _animation = animation;
        _icc = icc;
        FrameCount = animation?.FrameCount ?? 1;
        LoopCount = animation?.LoopCount;
    }

    /// <summary>Opens <paramref name="data"/>; throws for a malformed or unsupported container.</summary>
    public static WebpFrameReader Open(ReadOnlyMemory<byte> data)
    {
        if (!WebpImageDecoder.TryReadInfo(data.Span, out var info))
            throw new InvalidDataException("The WebP container is malformed or unsupported.");
        var container = WebpContainer.Read(data.Span);
        if (container.Info.Format != WebpFormat.Animated)
            return new WebpFrameReader(data, info, null, null);
        return new WebpFrameReader(data, info, new WebpAnimationDecoder(data),
            container.HasIccProfile ? container.IccProfile.ToArray() : null);
    }

    public int FrameCount { get; }

    public int? LoopCount { get; }

    public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
    {
        var animation = Check(index);
        frame = new ImageFrameInfo(_frameInfo,
            animation is null ? null : TimeSpan.FromMilliseconds(animation.Frames[index].DurationMs));
        return true;
    }

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image)
    {
        var animation = Check(index);
        if (animation is null)
            return WebpImageDecoder.TryDecode(_data.Span, out image);

        image = null;
        if (!TryComposite(animation, index)) return false;
        var canvas = animation.Canvas;
        int width = _frameInfo.Width, height = _frameInfo.Height, channels = _frameInfo.Channels;
        var pixels = new byte[width * height * channels];
        if (channels == 4)
        {
            canvas.CopyTo(pixels);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                PixelConverter.ConvertRow(canvas.Slice(y * width * 4, width * 4), 4, SampleFormat.UInt8,
                    pixels.AsSpan(y * width * 3, width * 3), ImagePixelFormat.Rgb24, width);
            }
        }
        image = new RasterImage(width, height, channels, SampleFormat.UInt8, pixels, _icc, ColorEncoding.AssumedSrgb);
        return true;
    }

    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        var animation = Check(index);
        if (animation is null)
            return WebpImageDecoder.TryDecodeInto(_data.Span, destination, stride, format);

        int width = _frameInfo.Width, height = _frameInfo.Height;
        if (!PixelConverter.TryGetLayout(width, height, format, stride, destination.Length, out var rowStride))
            return false;
        if (!TryComposite(animation, index)) return false;
        // The canvas is RGBA whatever the declared channels: opaque where the container says it has no
        // alpha, which is what ToRgba8 of the RGB frame gives too.
        var canvas = animation.Canvas;
        for (var y = 0; y < height; y++)
        {
            PixelConverter.ConvertRow(canvas.Slice(y * width * 4, width * 4), 4, SampleFormat.UInt8,
                destination.Slice(y * rowStride), format, width);
        }
        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        _animation = null; // the canvas goes with it
    }

    // The animation decoder, or null for a still image, after the index and disposal checks.
    private WebpAnimationDecoder? Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _animation;
    }

    // A frame that fails leaves the decoder consistent: its bitstream is decoded before it touches the
    // canvas, and the previous frame's disposal it may already have applied is idempotent, so asking for
    // the same frame again, or any other, starts from a canvas that matches the decoder's position.
    private static bool TryComposite(WebpAnimationDecoder animation, int index)
    {
        try
        {
            animation.DecodeFrame(index);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
