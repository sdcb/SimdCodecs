using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

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
/// Memory and mapped files are decoded in place. A stream is read only as far as its header at
/// <see cref="Open(Stream, bool, bool)"/>: when the codec decodes from a stream (<see cref="ImageDecoderRegistration.CreateRowReader"/>),
/// the first full-size decode of frame 0 (<see cref="ImageFrame.Decode"/>, <see cref="ImageFrame.CopyPixels"/> and
/// their async forms) reads its rows straight from the stream as it arrives. Everything else — another decode, a region,
/// a scale, orientation, other frames, the metadata, a thumbnail — needs the whole file and reads it first (a
/// seekable stream by seeking, a non-seekable one from the bytes the session kept as it read them), so every result
/// equals what <see cref="Open(ReadOnlyMemory{byte})"/> over the same bytes gives. A codec that does not decode from a
/// stream reads the whole file at open, as before.
/// </para>
/// </summary>
public sealed partial class ImageDecoder : IDisposable
{
    private IByteSource? _source;
    private ReadOnlyMemory<byte> _data;
    private ImageDecoderRegistration _codec;
    private IImageFrameReader? _frames;
    private ImageMetadata? _metadata;
    private bool _disposed;

    private ImageDecoder(IByteSource source, ReadOnlyMemory<byte> data, ImageDecoderRegistration codec, IImageFrameReader frames)
    {
        _source = source;
        _data = data;
        _codec = codec;
        _frames = frames;
    }

    /// <summary>Opens an image from <paramref name="stream"/>, positioned at its start, reading only its header (see
    /// <see cref="Open(Stream, bool, bool)"/>); a non-seekable stream's bytes are kept as they are read, so every
    /// operation works.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the session is disposed.</param>
    /// <exception cref="InvalidDataException">No registered codec recognises the data (the message names the format <see cref="ImageCodecs.DetectFormat"/> detects, as <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/> does), or the file is malformed (the message gives the codec's reason).</exception>
    public static ImageDecoder Open(Stream stream, bool leaveOpen = false) => Open(stream, leaveOpen, forwardOnly: false);

    /// <summary>Opens an image held in memory, which the caller keeps unchanged while the session is used.</summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the data (the message names the format <see cref="ImageCodecs.DetectFormat"/> detects, as <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/> does), or the file is malformed (the message gives the codec's reason).</exception>
    public static ImageDecoder Open(ReadOnlyMemory<byte> data) => Open(ByteSource.FromMemory(data));

    /// <summary>Opens an image file, memory-mapped.</summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the file (the message names the format <see cref="ImageCodecs.DetectFormat"/> detects), or it is malformed (the message gives the codec's reason).</exception>
    public static ImageDecoder Open(string path) => Open(ByteSource.FromFile(path));

    /// <summary>Opens an image from any <see cref="IByteSource"/>; the session disposes it.</summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the data (the message names the format <see cref="ImageCodecs.DetectFormat"/> detects, as <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/> does), or the file is malformed (the message gives the codec's reason).</exception>
    public static ImageDecoder Open(IByteSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            var data = source.GetMemory();
            var (codec, frames) = OpenFrames(data);
            return new ImageDecoder(source, data, codec, frames);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an image from <paramref name="stream"/>, reading its header asynchronously; a non-seekable stream (an HTTP
    /// request body, a network stream) is then read on in the background into memory the session keeps (see
    /// <see cref="OpenAsync(Stream, bool, bool, CancellationToken)"/>).
    /// </summary>
    /// <exception cref="InvalidDataException">No registered codec recognises the data (the message names the format <see cref="ImageCodecs.DetectFormat"/> detects, as <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/> does), or the file is malformed (the message gives the codec's reason).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while reading.</exception>
    public static ValueTask<ImageDecoder> OpenAsync(Stream stream, bool leaveOpen = false, CancellationToken cancellationToken = default) =>
        OpenAsync(stream, leaveOpen, forwardOnly: false, cancellationToken);

    /// <summary>The file's format.</summary>
    public ImageFormat Format => _codec.Format;

    /// <summary>The frames the session decodes: 1 for a still image.</summary>
    public int FrameCount => _frames?.FrameCount ?? (_codec.TryOpenFrames is null || _streamFrame is not null ? 1 : Frames.FrameCount);

    /// <summary>For an animation, how many times it plays (0: forever); null for pages and stills.</summary>
    public int? LoopCount => _frames is null && _codec.TryOpenFrames is null ? null : Frames.LoopCount;

    /// <summary>The file's metadata blocks (Exif, XMP, ICC); <see cref="ImageMetadata.Empty"/> when it has none. Read
    /// from the file once, on first use.</summary>
    /// <exception cref="InvalidOperationException">A forward-only session's decode has read past bytes it did not keep.</exception>
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
        if (index == 0 && _frames is null && _streamFrame is { } streamed)
            return new ImageFrame(this, 0, streamed);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        if (!Frames.TryGetFrameInfo(index, out var frame))
            throw FrameFailure(index, null, default, $"Frame {index} cannot be read.");
        return new ImageFrame(this, index, frame);
    }

    /// <summary>
    /// Why frame <paramref name="index"/> fails: for a file of one frame, which is the image the codec's
    /// <see cref="ImageDecoderRegistration.Decode"/> decodes, its reason (see <c>ImageCodecs.DecodeFailure</c>);
    /// otherwise, or when that decode succeeds, <paramref name="fallback"/>.
    /// </summary>
    internal InvalidDataException FrameFailure(int index, DecodeOptions? options, CancellationToken cancellationToken, string fallback) =>
        index == 0 && FrameCount == 1
            ? ImageCodecs.DecodeFailure(_codec, Data, options, cancellationToken, fallback)!
            : new InvalidDataException(fallback);

    /// <summary>The whole file, read first when the session was opened over a stream.</summary>
    internal ReadOnlySpan<byte> Data
    {
        get
        {
            EnsureData();
            return _data.Span;
        }
    }

    internal ImageDecoderRegistration Codec => _codec;

    internal IImageFrameReader Frames
    {
        get
        {
            EnsureData();
            return _frames!;
        }
    }

    /// <summary>Releases the file and the decoding state; a session over a stream stops reading it and, unless told to
    /// leave it open, disposes it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _header?.Dispose();
        _header = null;
        _frames?.Dispose();
        _source?.Dispose();
        _tap?.Dispose();
        _data = default;
    }

    // The codec that claims the whole file and its frame reader, or the exception Open throws.
    private static (ImageDecoderRegistration Codec, IImageFrameReader Frames) OpenFrames(ReadOnlyMemory<byte> data)
    {
        if (ImageCodecs.Find(data.Span) is not { } codec)
            throw new InvalidDataException(ImageCodecs.NoCodecMessage(data.Span));
        if (!ImageCodecs.TryOpenFrames(data, out var frames))
        {
            // The frame reader gives no reason; the codec's Decode does (only on this failure path).
            throw ImageCodecs.DecodeFailure(codec, data.Span, null, default,
                $"The {ImageCodecs.FormatName(codec.Format)} file is malformed: its frames cannot be read.")!;
        }
        return (codec, frames);
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

    /// <summary>The frame's layout, colour encoding and resolution, as <see cref="IImageDecoder.TryReadInfo"/> reports. For
    /// frame 0 of a session over a stream, before the whole file has been read, it is what the codec's header reader
    /// reports: a value the file stores after the pixels (such as an orientation in a trailing Exif block) is not in it.</summary>
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

    /// <summary>
    /// Decodes the frame at its full fidelity (see <see cref="IImageDecoder.TryDecode"/>). Dispose the result. The first
    /// full-size decode of frame 0 of a session over a stream reads its rows from the stream as it arrives (see
    /// <see cref="ImageDecoder"/>); on a session from <see cref="ImageDecoder.OpenAsync(Stream, bool, CancellationToken)"/>
    /// this call instead waits for the background read and decodes from memory, never reading the stream synchronously.
    /// </summary>
    /// <exception cref="InvalidDataException">The frame is undecodable.</exception>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the frame.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A forward-only session has already streamed its decode.</exception>
    public IDecodedImage Decode(DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_decoder.TryStreamDecode(_index, options, cancellationToken) is { } streamed)
            return streamed;
        _decoder.EnsureData(cancellationToken);
        if (!_decoder.Frames.TryDecodeFrame(_index, out var image, options, cancellationToken))
        {
            // A codec returns false for an image whose raster does not fit one array; say so, and what to do instead.
            var (w, h) = (options ?? DecodeOptions.Default).GetOutputSize(Info);
            var bytes = (Int128)w * h * Info.Channels * RasterImage.BytesPerSample(Info.SampleFormat);
            if (bytes > Array.MaxLength)
            {
                throw new InvalidDataException(
                    $"Frame {_index} could not be decoded: its raster ({w}x{h}, {Info.Channels} channels, {bytes} bytes) exceeds " +
                    $"the maximum single-buffer size of {Array.MaxLength} bytes (2 GB). Decode it at a reduced scale " +
                    "(DecodeOptions.ScaleDenominator, a power of two; one the codec scales to natively, as JPEG does up to 8, " +
                    "never holds the full size, see TryGetClosestSize) or decode a region of it (DecodeOptions.Region).");
            }
            throw _decoder.FrameFailure(_index, options, cancellationToken, $"Frame {_index} could not be decoded.");
        }
        return image;
    }

    /// <summary>
    /// <see cref="Decode"/>, reading the stream asynchronously: the first full-size decode of frame 0 of a session over a
    /// stream reads its rows as they arrive (taking over from the background read of
    /// <see cref="ImageDecoder.OpenAsync(Stream, bool, CancellationToken)"/> where it has got to); anything else first
    /// reads the whole file asynchronously.
    /// </summary>
    /// <inheritdoc cref="Decode" path="/exception"/>
    public async ValueTask<IDecodedImage> DecodeAsync(DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (await _decoder.TryStreamDecodeAsync(_index, options, cancellationToken).ConfigureAwait(false) is { } streamed)
            return streamed;
        await _decoder.EnsureDataAsync(cancellationToken).ConfigureAwait(false);
        return Decode(options, cancellationToken);
    }

    /// <summary>
    /// Decodes the frame into <paramref name="destination"/> in <paramref name="format"/>, rows <paramref name="stride"/>
    /// bytes apart (0: tightly packed): <paramref name="region"/> of it (null: all of it), at
    /// <paramref name="options"/>' scale. Size the buffer from <see cref="Info"/> and the region. A session over a stream
    /// streams the first full-size copy of frame 0 as <see cref="Decode"/> does.
    /// </summary>
    /// <exception cref="ArgumentException">The destination does not fit, or the region does not lie inside the frame.</exception>
    /// <exception cref="InvalidDataException">The frame is undecodable, or has no integer projection (float samples,
    /// or an HDR transfer a codec refuses to project).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A forward-only session has already streamed its decode.</exception>
    public void CopyPixels(PixelRect? region, PixelFormat format, Span<byte> destination, int stride,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var effective = CheckDestination(region, format, destination.Length, stride, options, cancellationToken);
        if (_decoder.TryStreamInto(_index, destination, stride, format, effective, cancellationToken))
            return;
        _decoder.EnsureData(cancellationToken);
        if (!_decoder.Frames.TryDecodeFrameInto(_index, destination, stride, format, effective, cancellationToken))
            throw _decoder.FrameFailure(_index, effective, cancellationToken, $"Frame {_index} could not be decoded to {format}.");
    }

    /// <summary><see cref="CopyPixels"/>, reading the stream asynchronously (see <see cref="DecodeAsync"/>).</summary>
    /// <inheritdoc cref="CopyPixels" path="/exception"/>
    public async ValueTask CopyPixelsAsync(PixelRect? region, PixelFormat format, Memory<byte> destination, int stride,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!StreamingRowReader.IsFullImage(Effective(region, options)))
            await _decoder.EnsureDataAsync(cancellationToken).ConfigureAwait(false);
        var effective = CheckDestination(region, format, destination.Length, stride, options, cancellationToken);
        if (await _decoder.TryStreamIntoAsync(_index, destination, stride, format, effective, cancellationToken).ConfigureAwait(false))
            return;
        await _decoder.EnsureDataAsync(cancellationToken).ConfigureAwait(false);
        CopyPixels(region, format, destination.Span, stride, options, cancellationToken);
    }

    private static DecodeOptions? Effective(PixelRect? region, DecodeOptions? options) =>
        region is null ? options : (options ?? DecodeOptions.Default) with { Region = region };

    private DecodeOptions? CheckDestination(PixelRect? region, PixelFormat format, int length, int stride, DecodeOptions? options,
        CancellationToken cancellationToken)
    {
        var effective = Effective(region, options);
        // A streamed copy is the header's size. Anything else is sized from the whole file's frame info, which a value
        // stored after the pixels (an orientation in a trailing Exif block) can change.
        var info = StreamingRowReader.IsFullImage(effective) ? Info : _decoder.FileFrameInfo(_index, cancellationToken) ?? Info;
        var (w, h) = (effective ?? DecodeOptions.Default).GetOutputSize(info);
        if (!PixelConverter.TryGetLayout(w, h, format, stride, length, out _))
            throw new ArgumentException($"The destination does not hold a {w}x{h} {format} image at stride {stride}.", "destination");
        return effective;
    }
}
