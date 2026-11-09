using System;
using System.Buffers;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// Pixels in a caller-chosen <see cref="PixelFormat"/>, tightly packed, with the file's colour signalling and metadata:
/// what <c>ImageCodecs.Decode(data, format)</c> returns, the one-call form of reading the header, allocating and
/// decoding into a buffer. Unlike an <see cref="IDecodedImage"/>, whose samples are always RGB-ordered with straight
/// alpha, the bytes here are in <see cref="Format"/>'s layout: <see cref="PixelFormat.Bgr24"/> for an OpenCV <c>Mat</c>,
/// <see cref="PixelFormat.Bgra32Premultiplied"/> for a compositor.
/// <para>
/// Dispose it when done: a buffer rented from a pool (<see cref="DecodeOptions.Pool"/>) returns its memory then, after
/// which <see cref="Pixels"/> throws <see cref="ObjectDisposedException"/>.
/// </para>
/// </summary>
public sealed class PixelBuffer : IDisposable
{
    private readonly int _length;
    private byte[]? _array;
    private ArrayPool<byte>? _pool;
    private int _disposed;

    private PixelBuffer(int width, int height, PixelFormat format, ColorEncoding? colorEncoding, ImageMetadata? metadata)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        Width = width;
        Height = height;
        Format = format;
        _length = RasterSize.Elements(width, height, format.BytesPerPixel);
        Stride = width * format.BytesPerPixel; // fits: _length does
        ColorEncoding = colorEncoding ?? ColorEncoding.AssumedSrgb;
        Metadata = metadata ?? ImageMetadata.Empty;
    }

    /// <summary>
    /// Creates a buffer to fill through <see cref="WritablePixels"/>: rented from <paramref name="pool"/> (not cleared)
    /// when it is not null, otherwise new, zeroed memory.
    /// </summary>
    /// <param name="width">Pixel width (&gt; 0).</param>
    /// <param name="height">Pixel height (&gt; 0).</param>
    /// <param name="format">The layout of the pixels.</param>
    /// <param name="pool">The pool to rent from; null for new memory.</param>
    /// <param name="colorEncoding">How the samples encode colour; null means <see cref="ColorEncoding.AssumedSrgb"/>.</param>
    /// <param name="metadata">The file's metadata blocks (Exif, XMP, ICC profile).</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, or the pixels need more than
    /// <see cref="Array.MaxLength"/> bytes (2 GB), the most one array holds.</exception>
    public static PixelBuffer Create(int width, int height, PixelFormat format, ArrayPool<byte>? pool = null,
        ColorEncoding? colorEncoding = null, ImageMetadata? metadata = null)
    {
        var buffer = new PixelBuffer(width, height, format, colorEncoding, metadata);
        if (pool is null)
        {
            buffer._array = new byte[buffer._length];
        }
        else
        {
            buffer._pool = pool;
            buffer._array = pool.Rent(buffer._length);
        }
        return buffer;
    }

    /// <summary>Pixel width.</summary>
    public int Width { get; }

    /// <summary>Pixel height.</summary>
    public int Height { get; }

    /// <summary>The layout of <see cref="Pixels"/>.</summary>
    public PixelFormat Format { get; }

    /// <summary>Bytes from one row to the next: <c>Width × Format.BytesPerPixel</c>, no padding.</summary>
    public int Stride { get; }

    /// <summary>The pixels, row-major, <see cref="Stride"/> bytes per row, in <see cref="Format"/>'s layout and host
    /// byte order (16-bit samples).</summary>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public ReadOnlySpan<byte> Pixels => WritablePixels;

    /// <summary>The pixels, for the code that fills the buffer. A buffer handed to someone else is theirs to read.</summary>
    /// <exception cref="ObjectDisposedException">The buffer has been disposed.</exception>
    public Span<byte> WritablePixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _array.AsSpan(0, _length);
        }
    }

    /// <summary>How the samples encode colour, as <see cref="IDecodedImage.ColorEncoding"/>.</summary>
    public ColorEncoding ColorEncoding { get; }

    /// <summary>The file's metadata blocks (Exif, XMP, ICC); <see cref="ImageMetadata.Empty"/> when it has none.</summary>
    public ImageMetadata Metadata { get; }

    /// <summary>Returns rented memory; afterwards <see cref="Pixels"/> throws <see cref="ObjectDisposedException"/>.
    /// Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_pool is { } pool && _array is { } array)
            pool.Return(array);
        _array = null;
        _pool = null;
    }
}
