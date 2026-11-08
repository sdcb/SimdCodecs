using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>
/// <see cref="IImageFrameReader"/> for GIF (<see cref="GifImageDecoder.TryOpenFrames"/>): every frame is the
/// composited canvas of a <see cref="GifCanvas"/>, converted from it a row at a time, in the file's channel
/// count (RGB when every pixel is opaque, else RGBA). A still GIF is one frame with no loop count.
/// </summary>
internal sealed class GifFrameReader(ReadOnlyMemory<byte> data, GifCanvas canvas, GifContainer container, ImageInfo frameInfo)
    : IImageFrameReader
{
    private bool _disposed;

    public int FrameCount => container.Frames.Length;

    /// <summary>
    /// For an animation, how many times it plays: the NETSCAPE2.0 count is the repetitions after the first
    /// play, as browsers and ImageMagick read it, so a stored <c>n</c> plays <c>n + 1</c> times; 0 is
    /// forever; without the extension it plays once.
    /// </summary>
    public int? LoopCount => FrameCount <= 1 ? null : container.LoopCount switch
    {
        null => 1,
        0 => 0,
        var n => n + 1,
    };

    public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
    {
        Check(index);
        frame = new ImageFrameInfo(frameInfo,
            FrameCount > 1 ? TimeSpan.FromMilliseconds(container.Frames[index].DelayCentiseconds * 10) : null);
        return true;
    }

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        cancellationToken.ThrowIfCancellationRequested();
        image = null;
        options?.Validate(canvas.Width, canvas.Height);
        if (!TryComposite(index, cancellationToken)) return false;
        var raster = ToImage(canvas, frameInfo.Channels, options?.Pool);
        image = DecodeOptions.Apply(raster, options);
        return true;
    }

    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        cancellationToken.ThrowIfCancellationRequested();
        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeFrameInto(this, index, destination, stride, format, options, cancellationToken);
        if (!PixelConverter.TryGetLayout(canvas.Width, canvas.Height, format, stride, destination.Length, out var rowStride))
            return false;
        if (!TryComposite(index, cancellationToken)) return false;
        Convert(canvas, destination, rowStride, format, cancellationToken);
        return true;
    }

    public void Dispose() => _disposed = true;

    /// <summary>The canvas as a raster of <paramref name="channels"/> (3: opaque, alpha dropped; 4: RGBA).</summary>
    internal static RasterImage ToImage(GifCanvas canvas, int channels, ArrayPool<byte>? pool = null)
    {
        int width = canvas.Width, height = canvas.Height;
        var raster = RasterImage.Create(width, height, channels, SampleFormat.UInt8, pool, bitDepth: 8);
        var pixels = raster.WritablePixels;
        if (channels == 4)
        {
            canvas.Pixels.CopyTo(pixels);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                PixelConverter.ConvertRow(canvas.Pixels.Slice(y * width * 4, width * 4), 4, SampleFormat.UInt8,
                    pixels.Slice(y * width * 3, width * 3), PixelFormat.Rgb24, width);
            }
        }
        return raster;
    }

    /// <summary>The canvas into <paramref name="destination"/>, laid out by <see cref="PixelConverter.TryGetLayout"/>.</summary>
    internal static void Convert(GifCanvas canvas, Span<byte> destination, int rowStride, PixelFormat format,
        CancellationToken cancellationToken = default)
    {
        // The canvas is RGBA either way; for an opaque GIF its alpha is 255 everywhere (see GifCanvas), as
        // ToRgba8 of the RGB frame gives.
        int width = canvas.Width, height = canvas.Height;
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            PixelConverter.ConvertRow(canvas.Pixels.Slice(y * width * 4, width * 4), 4, SampleFormat.UInt8,
                destination.Slice(y * rowStride), format, width);
        }
    }

    private void Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool TryComposite(int index, CancellationToken cancellationToken)
    {
        try
        {
            canvas.DecodeFrame(data.Span, index, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
