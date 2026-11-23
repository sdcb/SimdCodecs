using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs;

// The stream side of the session: open reads the header only, frame 0's first full-size decode streams its rows, and
// everything else reads the whole file first (EnsureData) and takes the span path.
public sealed partial class ImageDecoder
{
    internal const string ForwardOnlyMessage =
        "The session was opened forward-only and its decode has read past bytes it did not keep: this operation needs the " +
        "whole file. Open it without forwardOnly (the bytes are then kept as they are read), or from memory.";

    private readonly StreamTap? _tap;
    private readonly bool _forwardOnly;
    private StreamingRowReader? _header;   // the header reader, until a decode takes it over
    private ImageFrameInfo? _streamFrame;  // frame 0 as the header reader saw it

    private ImageDecoder(StreamTap tap, bool forwardOnly)
    {
        _tap = tap;
        _forwardOnly = forwardOnly;
        _codec = null!; // set by Start or SetData before the session is handed out
    }

    /// <summary>
    /// Opens an image from <paramref name="stream"/>, positioned at its start, reading only as much as the codec's
    /// header takes. When the codec decodes from a stream, frame 0's first full-size decode reads its rows from the
    /// stream as they arrive; any other operation reads the whole file first (see <see cref="ImageDecoder"/>). A codec
    /// that does not decode from a stream has the whole file read here.
    /// </summary>
    /// <param name="stream">The stream. A seekable one is read where needed by seeking and never held in memory whole
    /// for a streamed decode.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the session is disposed.</param>
    /// <param name="forwardOnly">For a non-seekable stream: do not keep the bytes a streamed decode reads, so the session
    /// never holds the whole file. Once that decode has started, an operation that needs the file again (another decode,
    /// <see cref="Metadata"/>, a thumbnail, another frame) throws <see cref="InvalidOperationException"/>, and the decoded
    /// image's <see cref="IDecodedImage.Metadata"/> holds only the blocks before the pixels. No effect on a seekable
    /// stream.</param>
    /// <exception cref="InvalidDataException">No registered codec recognises the data (the message names the format <see cref="ImageCodecs.DetectFormat"/> detects, as <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/> does), or the file is malformed (the message gives the codec's reason).</exception>
    public static ImageDecoder Open(Stream stream, bool leaveOpen, bool forwardOnly)
    {
        var decoder = new ImageDecoder(CreateTap(stream, leaveOpen, asyncOnly: false), forwardOnly);
        try
        {
            decoder.Start();
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an image from <paramref name="stream"/>, reading its header asynchronously, and never reads the stream
    /// synchronously afterwards. A non-seekable stream (an HTTP request body, a network stream) is then read on in the
    /// background into memory the session keeps, unless <paramref name="forwardOnly"/>: <see cref="ImageFrame.DecodeAsync"/>
    /// and <see cref="ImageFrame.CopyPixelsAsync"/> take over from that read and decode the rows as they arrive, while the
    /// synchronous <see cref="ImageFrame.Decode"/> and <see cref="ImageFrame.CopyPixels"/> wait for it and decode from
    /// memory. Disposing the session stops the background read.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the session is disposed.</param>
    /// <param name="forwardOnly">As for <see cref="Open(Stream, bool, bool)"/>; there is no background read then, and a
    /// synchronous call blocks on the asynchronous reads it needs.</param>
    /// <param name="cancellationToken">Cancels the header read (not the background read, which the session's disposal stops).</param>
    /// <exception cref="InvalidDataException">No registered codec recognises the data (the message names the format <see cref="ImageCodecs.DetectFormat"/> detects, as <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/> does), or the file is malformed (the message gives the codec's reason).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled while reading.</exception>
    public static async ValueTask<ImageDecoder> OpenAsync(Stream stream, bool leaveOpen, bool forwardOnly,
        CancellationToken cancellationToken = default)
    {
        var decoder = new ImageDecoder(CreateTap(stream, leaveOpen, asyncOnly: true), forwardOnly);
        try
        {
            await decoder.StartAsync(cancellationToken).ConfigureAwait(false);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    private static StreamTap CreateTap(Stream stream, bool leaveOpen, bool asyncOnly)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            return new StreamTap(stream, leaveOpen, asyncOnly);
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
    }

    private void Start()
    {
        _tap!.Sniff();
        if (Probe() is { } codec)
        {
            var reader = codec.CreateRowReader!(HeaderInput(), PixelFormat.Rgba32, null);
            try
            {
                reader.Initialize();
            }
            catch (InvalidDataException)
            {
                // The span path decides, so a malformed file fails (or a lenient codec succeeds) as from memory.
                reader.Dispose();
                reader = null;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
            if (KeepHeader(codec, reader))
                return;
        }
        SetData(_tap.ReadAll());
    }

    private async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await _tap!.SniffAsync(cancellationToken).ConfigureAwait(false);
        if (Probe() is { } codec)
        {
            var reader = codec.CreateRowReader!(HeaderInput(), PixelFormat.Rgba32, null);
            try
            {
                await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                reader.Dispose();
                reader = null;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
            if (KeepHeader(codec, reader))
            {
                // Without threads a synchronous call could not block on an asynchronous read: read it all now.
                if (OperatingSystem.IsBrowser() || OperatingSystem.IsWasi())
                    await EnsureDataAsync(cancellationToken).ConfigureAwait(false);
                else if (!_tap.CanSeek && !_forwardOnly)
                    _tap.StartPrefetch();
                return;
            }
        }
        SetData(await _tap.ReadAllAsync(cancellationToken).ConfigureAwait(false));
    }

    // The codec whose streaming reader takes the stream, judged from the sniffed bytes: the first that claims them, if
    // it has a row reader and no earlier codec has its format. Two codecs of one format (a gain-map JPEG before the
    // plain one) tell their files apart by what lies beyond the sniffed bytes, so that file is sniffed whole instead.
    private ImageDecoderRegistration? Probe()
    {
        var prefix = _tap!.Recorded;
        var registry = ImageCodecs.RegistrySnapshot;
        for (var i = 0; i < registry.Length; i++)
        {
            if (!registry[i].CanDecode(prefix))
                continue;
            if (registry[i].CreateRowReader is null)
                return null;
            for (var j = 0; j < i; j++)
            {
                if (registry[j].Format == registry[i].Format)
                    return null;
            }
            return registry[i];
        }
        return null;
    }

    // Keeps a header reader that streams a file of one frame; false (and the reader disposed) otherwise.
    private bool KeepHeader(ImageDecoderRegistration codec, StreamingRowReader? reader)
    {
        if (reader is null)
            return false;
        // A reader that fell back has read and decoded the whole file already; the span path takes it from there.
        if (!reader.IsStreaming || (codec.TryOpenFrames is not null && reader.Info.FrameCount != 1))
        {
            reader.Dispose();
            return false;
        }
        _codec = codec;
        _header = reader;
        _streamFrame = new ImageFrameInfo(codec.TryOpenFrames is null ? reader.Info with { FrameCount = 1 } : reader.Info, null);
        return true;
    }

    private StreamInput HeaderInput() => new(new TapStream(_tap!), initialCapacity: StreamTap.SniffLength);

    private void EnsureData() => EnsureData(CancellationToken.None);

    /// <summary>Makes the whole file available for the span path (on an async-opened session, by waiting for the
    /// asynchronous reads, observing <paramref name="cancellationToken"/>).</summary>
    internal void EnsureData(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frames is not null)
            return;
        _tap!.SyncCancellation = cancellationToken;
        try
        {
            SetData(_tap.ReadAll());
        }
        finally
        {
            _tap.SyncCancellation = default;
        }
    }

    /// <summary>Frame <paramref name="index"/>'s info from the whole file, when it may differ from the header's (frame 0 of
    /// a stream session); null otherwise.</summary>
    internal ImageInfo? FileFrameInfo(int index, CancellationToken cancellationToken)
    {
        if (index != 0 || _streamFrame is null)
            return null;
        EnsureData(cancellationToken);
        return _frames!.TryGetFrameInfo(0, out var frame) ? frame.Info : null;
    }

    /// <summary>Makes the whole file available (reading the rest of the stream asynchronously) for the span path.</summary>
    internal async ValueTask EnsureDataAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frames is null)
            SetData(await _tap!.ReadAllAsync(cancellationToken).ConfigureAwait(false));
    }

    private void SetData(ReadOnlyMemory<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (codec, frames) = OpenFrames(data);
        _header?.Dispose();
        _header = null;
        _codec = codec;
        _data = data;
        _frames = frames;
        _source = ByteSource.FromMemory(data);
    }

    // Frame 0, the whole image at full size, before any other decode, from a codec that streams this file.
    private bool CanStream(int index, DecodeOptions? options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return index == 0 && _frames is null && _header is not null && StreamingRowReader.IsFullImage(options);
    }

    /// <summary>Streams frame 0 into <paramref name="destination"/>; false when the span path must decode it.</summary>
    internal bool TryStreamInto(int index, Span<byte> destination, int stride, PixelFormat format, DecodeOptions? options,
        CancellationToken cancellationToken)
    {
        // An async-opened session's synchronous call waits for the background read instead (EnsureData).
        if (!CanStream(index, options) || _tap!.HasPrefetch)
            return false;
        StreamingRowReader? reader = null;
        _tap.SyncCancellation = cancellationToken;
        try
        {
            reader = TakeReader(format, cancellationToken);
            reader.ReadRows(destination, reader.Height, stride, cancellationToken);
            return true;
        }
        catch (InvalidDataException e)
        {
            return _tap.CanRevisit ? false : throw StreamFailure(e);
        }
        finally
        {
            reader?.Dispose();
            _tap.SyncCancellation = default;
        }
    }

    /// <summary><see cref="TryStreamInto"/>, reading the stream asynchronously.</summary>
    internal async ValueTask<bool> TryStreamIntoAsync(int index, Memory<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options, CancellationToken cancellationToken)
    {
        if (!CanStream(index, options))
            return false;
        StreamingRowReader? reader = null;
        try
        {
            reader = await TakeReaderAsync(format, cancellationToken).ConfigureAwait(false);
            await reader.ReadRowsAsync(destination, reader.Height, stride, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidDataException e)
        {
            return _tap!.CanRevisit ? false : throw StreamFailure(e);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>
    /// Streams frame 0 into a new image in the codec's own layout, equal to what <see cref="IImageDecoder.TryDecode"/>
    /// returns; null when the span path must decode it. Not for a seekable stream: the image's metadata needs the
    /// whole file, which the span path reads once instead of twice.
    /// </summary>
    internal IDecodedImage? TryStreamDecode(int index, DecodeOptions? options, CancellationToken cancellationToken)
    {
        if (!CanStream(index, options) || _tap!.HasPrefetch || _tap.CanSeek || !TryGetLayout(out var info, out var format, out var length))
            return null;
        var pool = options?.Pool;
        var pixels = pool is null ? GC.AllocateUninitializedArray<byte>(length) : pool.Rent(length);
        StreamingRowReader? reader = null;
        var decoded = false;
        IDecodedImage? image = null;
        _tap.SyncCancellation = cancellationToken;
        try
        {
            reader = TakeReader(format, cancellationToken);
            reader.ReadRows(pixels.AsSpan(0, length), reader.Height, 0, cancellationToken);
            reader.Dispose();
            reader = null;
            ImageMetadata metadata;
            if (_tap.CanRevisit)
            {
                EnsureData(cancellationToken);
                metadata = Metadata;
            }
            else
            {
                metadata = HeaderMetadata();
            }
            decoded = true;
            return image = Wrap(info, pixels, length, pool, metadata);
        }
        catch (InvalidDataException e) when (!decoded)
        {
            return _tap.CanRevisit ? null : throw StreamFailure(e);
        }
        finally
        {
            reader?.Dispose();
            if (image is null)
                pool?.Return(pixels);
            _tap.SyncCancellation = default;
        }
    }

    /// <summary><see cref="TryStreamDecode"/>, reading the stream asynchronously.</summary>
    internal async ValueTask<IDecodedImage?> TryStreamDecodeAsync(int index, DecodeOptions? options, CancellationToken cancellationToken)
    {
        if (!CanStream(index, options) || _tap!.CanSeek || !TryGetLayout(out var info, out var format, out var length))
            return null;
        var pool = options?.Pool;
        var pixels = pool is null ? GC.AllocateUninitializedArray<byte>(length) : pool.Rent(length);
        StreamingRowReader? reader = null;
        var decoded = false;
        IDecodedImage? image = null;
        try
        {
            reader = await TakeReaderAsync(format, cancellationToken).ConfigureAwait(false);
            await reader.ReadRowsAsync(pixels.AsMemory(0, length), reader.Height, 0, cancellationToken).ConfigureAwait(false);
            reader.Dispose();
            reader = null;
            ImageMetadata metadata;
            if (_tap.CanRevisit)
            {
                await EnsureDataAsync(cancellationToken).ConfigureAwait(false);
                metadata = Metadata;
            }
            else
            {
                metadata = HeaderMetadata();
            }
            decoded = true;
            return image = Wrap(info, pixels, length, pool, metadata);
        }
        catch (InvalidDataException e) when (!decoded)
        {
            return _tap.CanRevisit ? null : throw StreamFailure(e);
        }
        finally
        {
            reader?.Dispose();
            if (image is null)
                pool?.Return(pixels);
        }
    }

    // Frame 0's own layout (what TryDecode returns): its channels at its sample width. False for float samples, which
    // no row reader writes, and for a raster too large for one array (the span path says so).
    private bool TryGetLayout(out ImageInfo info, out PixelFormat format, out int length)
    {
        info = _streamFrame!.Value.Info;
        format = default;
        length = 0;
        if (info.SampleFormat is not (SampleFormat.UInt8 or SampleFormat.UInt16) || info.Channels is < 1 or > 4)
            return false;
        format = PixelFormat.ForChannels(info.Channels, info.SampleFormat);
        var bytes = (long)info.Width * info.Height * format.BytesPerPixel;
        if (bytes > Array.MaxLength)
            return false;
        length = (int)bytes;
        return true;
    }

    private static RasterImage Wrap(in ImageInfo info, byte[] pixels, int length, ArrayPool<byte>? pool, ImageMetadata metadata)
    {
        var bitDepth = info.BitDepth == RasterImage.BytesPerSample(info.SampleFormat) * 8 ? 0 : info.BitDepth;
        return pool is null
            ? RasterImage.Wrap(info.Width, info.Height, info.Channels, info.SampleFormat, pixels, info.ColorEncoding, bitDepth, metadata)
            : RasterImage.Wrap(info.Width, info.Height, info.Channels, info.SampleFormat, new PooledPixels(pool, pixels, length),
                info.ColorEncoding, bitDepth, metadata);
    }

    // A forward-only decode's metadata: what the codec finds in the bytes read before the pixels.
    private ImageMetadata HeaderMetadata()
    {
        try
        {
            return _codec.TryReadMetadata(_tap!.Recorded, out var metadata) ? metadata : ImageMetadata.Empty;
        }
        catch (InvalidDataException)
        {
            return ImageMetadata.Empty;
        }
    }

    // The reader a decode in `format` uses: the header reader when it writes that format, otherwise a new one that
    // reads the header again (from the record, or by seeking). The header reader is used up either way.
    private StreamingRowReader TakeReader(PixelFormat format, CancellationToken cancellationToken)
    {
        if (TakeHeader(format) is { } header)
            return header;
        var reader = _codec.CreateRowReader!(new StreamInput(new TapStream(_tap!)), format, null);
        try
        {
            reader.Initialize(cancellationToken);
            return CheckSize(reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private async ValueTask<StreamingRowReader> TakeReaderAsync(PixelFormat format, CancellationToken cancellationToken)
    {
        if (TakeHeader(format) is { } header)
            return header;
        var reader = _codec.CreateRowReader!(new StreamInput(new TapStream(_tap!)), format, null);
        try
        {
            await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return CheckSize(reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private StreamingRowReader? TakeHeader(PixelFormat format)
    {
        var header = _header!;
        _header = null;
        if (_forwardOnly)
            _tap!.StopRecording(); // what is recorded so far (the header) stays readable for a new reader
        if (header.Format == format && header.RowsRead == 0)
            return header;
        header.Dispose();
        return null;
    }

    private StreamingRowReader CheckSize(StreamingRowReader reader)
    {
        var info = _streamFrame!.Value.Info;
        if (!reader.IsStreaming || reader.Width != info.Width || reader.Height != info.Height)
            throw new InvalidDataException($"The {ImageCodecs.FormatName(_codec.Format)} header reads differently the second time.");
        return reader;
    }

    private InvalidDataException StreamFailure(InvalidDataException e) =>
        new($"The {ImageCodecs.FormatName(_codec.Format)} file cannot be decoded: {e.Message}", e);

    // Pixels rented from a decode's pool, returned when the image is disposed.
    private sealed class PooledPixels(ArrayPool<byte> pool, byte[] array, int length) : IMemoryOwner<byte>
    {
        private byte[]? _array = array;

        public Memory<byte> Memory => (_array ?? throw new ObjectDisposedException(nameof(PooledPixels))).AsMemory(0, length);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _array, null) is { } a)
                pool.Return(a);
        }
    }
}

/// <summary>A forward-only view of a <see cref="StreamTap"/> from offset 0: what a row reader's input reads.</summary>
internal sealed class TapStream(StreamTap tap) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = tap.Read(_position, buffer);
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await tap.ReadAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
        _position += n;
        return n;
    }
}
