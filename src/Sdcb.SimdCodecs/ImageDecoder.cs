using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs;

/// <summary>
/// A decoding session over one image file, in the manner of WIC's decoder: open it from a <see cref="Stream"/>, memory
/// or a file path, read its format and frames, then decode each <see cref="ImageFrame"/> — whole, scaled, a region of
/// it, or into a caller's buffer in any <see cref="PixelFormat"/>.
/// <para>
/// A still image has one frame; an animation's frames are its composited canvases, a document's frames its pages
/// (<see cref="IImageFrameReader"/> semantics). The session keeps decoding state (an animation's canvas), so, like a
/// frame reader, it is not safe to share between threads. Dispose it to release the file and that state.
/// </para>
/// <para>
/// In 2.0 the file is read into memory once at <see cref="Open(Stream, bool)"/> (memory and mapped files are used in
/// place); codecs that can decode incrementally from the source will do so in a later release, behind the same API.
/// </para>
/// </summary>
public sealed class ImageDecoder : IDisposable
{
    private readonly IByteSource _source;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ImageDecoderRegistration _codec;
    private readonly IImageFrameReader _frames;
    private ImageMetadata? _metadata;
    private bool _disposed;

    private ImageDecoder(IByteSource source, ReadOnlyMemory<byte> data, ImageDecoderRegistration codec, IImageFrameReader frames)
    {
        _source = source;
        _data = data;
        _codec = codec;
        _frames = frames;
    }

    /// <summary>Opens an image from <paramref name="stream"/>, positioned at its start.</summary>
    /// <param name="stream">The stream; a non-seekable one is buffered whole.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the session is disposed.</param>
    /// <exception cref="InvalidDataException">No registered codec recognises the data, or the file is malformed.</exception>
    public static ImageDecoder Open(Stream stream, bool leaveOpen = false) => Open(ByteSource.FromStream(stream, leaveOpen));

    /// <summary>Opens an image held in memory, which the caller keeps unchanged while the session is used.</summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the data, or the file is malformed.</exception>
    public static ImageDecoder Open(ReadOnlyMemory<byte> data) => Open(ByteSource.FromMemory(data));

    /// <summary>Opens an image file, memory-mapped.</summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the file, or it is malformed.</exception>
    public static ImageDecoder Open(string path) => Open(ByteSource.FromFile(path));

    /// <summary>Opens an image from any <see cref="IByteSource"/>; the session disposes it.</summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the data, or the file is malformed.</exception>
    public static ImageDecoder Open(IByteSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var data = source.GetMemory();
            if (SimdCodecs.Find(data.Span) is not { } codec)
                throw new InvalidDataException($"No registered codec recognises the data (detected format: {SimdCodecs.DetectFormat(data.Span)}).");
            if (!SimdCodecs.TryOpenFrames(data, out var frames))
                throw new InvalidDataException($"The {codec.Format} file is malformed.");
            return new ImageDecoder(source, data, codec, frames);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an image from <paramref name="stream"/>, reading a non-seekable stream (an HTTP request body, a network
    /// stream) asynchronously into memory first.
    /// </summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the data, or the file is malformed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while reading.</exception>
    public static async ValueTask<ImageDecoder> OpenAsync(Stream stream, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
            return Open(stream, leaveOpen);
        var buffer = new MemoryStream();
        try
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!leaveOpen)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
        return Open(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
    }

    /// <summary>The file's format.</summary>
    public ImageFormat Format => _codec.Format;

    /// <summary>The frames the session decodes: 1 for a still image.</summary>
    public int FrameCount => _frames.FrameCount;

    /// <summary>For an animation, how many times it plays (0: forever); null for pages and stills.</summary>
    public int? LoopCount => _frames.LoopCount;

    /// <summary>The file's metadata blocks (Exif, XMP, ICC); <see cref="ImageMetadata.Empty"/> when it has none. Read
    /// from the file once, on first use.</summary>
    public ImageMetadata Metadata
    {
        get
        {
            var data = Data; // throws once disposed, cached or not
            return _metadata ??= _codec.TryReadMetadata(data, out var metadata) ? metadata : ImageMetadata.Empty;
        }
    }

    /// <summary>Frame <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="FrameCount"/>.</exception>
    /// <exception cref="InvalidDataException">The frame's header is malformed.</exception>
    public ImageFrame GetFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        if (!_frames.TryGetFrameInfo(index, out var frame))
            throw new InvalidDataException($"Frame {index} cannot be read.");
        return new ImageFrame(this, index, frame);
    }

    internal ReadOnlySpan<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _data.Span;
        }
    }

    internal ImageDecoderRegistration Codec => _codec;

    internal IImageFrameReader Frames
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _frames;
        }
    }

    /// <summary>Releases the file and the decoding state.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frames.Dispose();
        _source.Dispose();
    }
}

/// <summary>
/// One frame of an <see cref="ImageDecoder"/> session: its layout, metadata and timing, and the ways to decode it.
/// Valid while the session is.
/// </summary>
public sealed class ImageFrame
{
    private readonly ImageDecoder _decoder;
    private readonly int _index;

    internal ImageFrame(ImageDecoder decoder, int index, ImageFrameInfo frame)
    {
        _decoder = decoder;
        _index = index;
        Info = frame.Info;
        Duration = frame.Duration;
    }

    /// <summary>The frame's index in the session.</summary>
    public int Index => _index;

    /// <summary>The frame's layout, colour encoding and resolution, as <see cref="IImageDecoder.TryReadInfo"/> reports.</summary>
    public ImageInfo Info { get; }

    /// <summary>How long an animation shows the frame; null for a page or a still.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>The file's metadata blocks; frames of one file share them.</summary>
    public ImageMetadata Metadata => _decoder.Metadata;

    /// <summary>The reduced-size preview the file embeds, for frame 0 (see <see cref="ImageInfo.HasThumbnail"/>).</summary>
    public bool TryGetThumbnail([NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        thumbnail = null;
        return _index == 0 && _decoder.Codec.TryDecodeThumbnail(_decoder.Data, out thumbnail);
    }

    /// <summary>The size closest to the requested one that this frame decodes to natively, and its scale (see
    /// <see cref="IImageDecoder.TryGetClosestSize"/>). Frames after the first, and every frame of a multi-frame file,
    /// report their own size at scale 1.</summary>
    public bool TryGetClosestSize(int width, int height, out int closestWidth, out int closestHeight, out int scaleDenominator)
    {
        if (_index == 0 && _decoder.FrameCount == 1)
            return _decoder.Codec.TryGetClosestSize(_decoder.Data, width, height, out closestWidth, out closestHeight, out scaleDenominator);
        (closestWidth, closestHeight, scaleDenominator) = (Info.Width, Info.Height, 1);
        return true;
    }

    /// <summary>Decodes the frame at its full fidelity (see <see cref="IImageDecoder.TryDecode"/>). Dispose the result.</summary>
    /// <exception cref="InvalidDataException">The frame is undecodable.</exception>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the frame.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public IDecodedImage Decode(DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!_decoder.Frames.TryDecodeFrame(_index, out var image, options, cancellationToken))
        {
            // A codec returns false for an image whose raster does not fit one array; say so, and what to do instead.
            var (w, h) = (options ?? DecodeOptions.Default).GetOutputSize(Info.Width, Info.Height);
            var bytes = (Int128)w * h * Info.Channels * RasterImage.BytesPerSample(Info.SampleFormat);
            if (bytes > Array.MaxLength)
            {
                throw new InvalidDataException(
                    $"Frame {_index} could not be decoded: its raster ({w}x{h}, {Info.Channels} channels, {bytes} bytes) exceeds " +
                    $"the maximum single-buffer size of {Array.MaxLength} bytes (2 GB). Decode it at a reduced scale " +
                    "(DecodeOptions.ScaleDenominator, a power of two; one the codec scales to natively, as JPEG does up to 8, " +
                    "never holds the full size, see TryGetClosestSize) or decode a region of it (DecodeOptions.Region).");
            }
            throw new InvalidDataException($"Frame {_index} could not be decoded.");
        }
        return image;
    }

    /// <summary>
    /// Decodes the frame into <paramref name="destination"/> in <paramref name="format"/>, rows <paramref name="stride"/>
    /// bytes apart (0: tightly packed): <paramref name="region"/> of it (null: all of it), at
    /// <paramref name="options"/>' scale. Size the buffer from <see cref="Info"/> and the region.
    /// </summary>
    /// <exception cref="ArgumentException">The destination does not fit, or the region does not lie inside the frame.</exception>
    /// <exception cref="InvalidDataException">The frame is undecodable, or has no integer projection (float samples,
    /// or an HDR transfer a codec refuses to project).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public void CopyPixels(PixelRect? region, PixelFormat format, Span<byte> destination, int stride,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var effective = region is null ? options : (options ?? DecodeOptions.Default) with { Region = region };
        var (w, h) = (effective ?? DecodeOptions.Default).GetOutputSize(Info.Width, Info.Height);
        if (!PixelConverter.TryGetLayout(w, h, format, stride, destination.Length, out _))
            throw new ArgumentException($"The destination does not hold a {w}x{h} {format} image at stride {stride}.", nameof(destination));
        if (!_decoder.Frames.TryDecodeFrameInto(_index, destination, stride, format, effective, cancellationToken))
            throw new InvalidDataException($"Frame {_index} could not be decoded to {format}.");
    }
}
