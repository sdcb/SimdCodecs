using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs;

/// <summary>
/// An encoding session writing its file to a <see cref="Stream"/> (<see cref="ImageCodecs.CreateEncoder(ImageFormat, Stream, EncodeOptions?, bool)"/>):
/// the format's own session over a buffer of about 64 KiB that is written to the stream whenever it fills, so the file
/// reaches the stream as the encoder produces it. The synchronous members write the stream synchronously; the
/// <c>Async</c> ones (<see cref="StreamFrameEncoder.WriteRowsAsync"/>, <see cref="CommitAsync"/>, <see cref="DisposeAsync"/>)
/// only asynchronously, for a stream that forbids synchronous writes.
/// </summary>
public sealed class StreamEncoderSession : IImageEncoderSession, IAsyncDisposable
{
    private readonly IImageEncoderSession _inner;
    private readonly StreamBufferWriter? _writer; // null: the inner session writes the stream itself
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _disposed;

    internal StreamEncoderSession(IImageEncoderSession inner, StreamBufferWriter? writer, Stream stream, bool leaveOpen)
    {
        _inner = inner;
        _writer = writer;
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// True when the format's encoder encodes rows as they are written, so the file reaches the stream before
    /// <see cref="Commit"/>; false when it encodes whole frames at <see cref="Commit"/> (see
    /// <see cref="IImageEncoderSession.IsStreaming"/>).
    /// </summary>
    public bool IsStreaming => _inner.IsStreaming;

    /// <inheritdoc />
    public int MaxFrames => _inner.MaxFrames;

    /// <summary>Starts the next frame (see <see cref="IImageEncoderSession.AddFrame"/>); push its rows with
    /// <see cref="StreamFrameEncoder.WriteRows"/> or <see cref="StreamFrameEncoder.WriteRowsAsync"/>.</summary>
    /// <inheritdoc cref="IImageEncoderSession.AddFrame" path="/param"/>
    /// <inheritdoc cref="IImageEncoderSession.AddFrame" path="/exception"/>
    public StreamFrameEncoder AddFrame(int width, int height, PixelFormat format, ColorEncoding? color = null,
        ImageMetadata? metadata = null, TimeSpan? duration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A header the encoder writes here is written to the stream with the frame's first rows.
        if (_writer is not null)
            _writer.FlushWhenFull = false;
        return new StreamFrameEncoder(this, _inner.AddFrame(width, height, format, color, metadata, duration));
    }

    IImageFrameEncoder IImageEncoderSession.AddFrame(int width, int height, PixelFormat format, ColorEncoding? color,
        ImageMetadata? metadata, TimeSpan? duration) => AddFrame(width, height, format, color, metadata, duration);

    /// <summary>Finishes the file (see <see cref="IImageEncoderSession.Commit"/>), writes the rest of it to the stream and
    /// flushes the stream, synchronously.</summary>
    /// <exception cref="InvalidOperationException">No frame was added, or the last one is incomplete.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public void Commit(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is not null)
            _writer.FlushWhenFull = true;
        _inner.Commit(cancellationToken);
        _writer?.Flush();
        _stream.Flush();
    }

    /// <summary><see cref="Commit"/>, writing and flushing the stream asynchronously.</summary>
    /// <inheritdoc cref="Commit" path="/exception"/>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is not null)
            _writer.FlushWhenFull = false;
        _inner.Commit(cancellationToken);
        if (_writer is not null)
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the session and, unless it was opened to leave it open, disposes the stream. Bytes of an
    /// uncommitted file not yet written are dropped.</summary>
    public void Dispose()
    {
        if (!Release()) return;
        if (!_leaveOpen)
            _stream.Dispose();
    }

    /// <summary><see cref="Dispose"/>, disposing the stream asynchronously.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!Release()) return;
        if (!_leaveOpen)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private bool Release()
    {
        if (_disposed) return false;
        _disposed = true;
        try
        {
            _inner.Dispose();
        }
        finally
        {
            _writer?.Dispose();
        }
        return true;
    }

    internal void WriteRows(IImageFrameEncoder frame, ReadOnlySpan<byte> rows, int rowCount, int stride,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is not null)
            _writer.FlushWhenFull = true;
        frame.WriteRows(rows, rowCount, stride, cancellationToken);
        if (_writer is { ChunkReady: true })
            _writer.Flush();
    }

    internal async ValueTask WriteRowsAsync(IImageFrameEncoder frame, ReadOnlyMemory<byte> rows, int rowCount, int stride,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is not null)
            _writer.FlushWhenFull = false;
        frame.WriteRows(rows.Span, rowCount, stride, cancellationToken);
        if (_writer is { ChunkReady: true })
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The rows of one frame of a <see cref="StreamEncoderSession"/>, pushed top to bottom; what the encoder makes of them
/// is written to the stream about every 64 KiB.
/// </summary>
public sealed class StreamFrameEncoder : IImageFrameEncoder
{
    private readonly StreamEncoderSession _session;
    private readonly IImageFrameEncoder _inner;

    internal StreamFrameEncoder(StreamEncoderSession session, IImageFrameEncoder inner)
    {
        _session = session;
        _inner = inner;
    }

    /// <inheritdoc />
    public int RowsWritten => _inner.RowsWritten;

    /// <summary>
    /// Writes the next <paramref name="rowCount"/> rows, <paramref name="stride"/> bytes apart (0: tightly packed), in the
    /// frame's format; the encoded bytes are written to the stream synchronously once a chunk is ready.
    /// </summary>
    /// <exception cref="ArgumentException">The rows run past the frame or <paramref name="rows"/> is too short.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public void WriteRows(ReadOnlySpan<byte> rows, int rowCount, int stride = 0, CancellationToken cancellationToken = default) =>
        _session.WriteRows(_inner, rows, rowCount, stride, cancellationToken);

    /// <summary><see cref="WriteRows"/>, writing the stream asynchronously (the rows are encoded synchronously, then the
    /// ready bytes written).</summary>
    /// <inheritdoc cref="WriteRows" path="/exception"/>
    public ValueTask WriteRowsAsync(ReadOnlyMemory<byte> rows, int rowCount, int stride = 0, CancellationToken cancellationToken = default) =>
        _session.WriteRowsAsync(_inner, rows, rowCount, stride, cancellationToken);
}
