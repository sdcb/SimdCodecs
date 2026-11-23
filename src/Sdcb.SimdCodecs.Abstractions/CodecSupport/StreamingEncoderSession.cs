using System;
using System.Buffers;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>
/// The encoder session for a codec that encodes rows as they arrive: it checks the session contract (frame order,
/// sizes, row counts, strides) and hands each validated band of rows to the codec, which writes finished bytes to
/// <see cref="Output"/> a band at a time. Memory is the codec's, a band of rows; nothing is collected whole.
/// <para>
/// A codec whose options need the whole image (palette quantisation, optimised Huffman tables, lossless reduction)
/// returns a <see cref="BufferedEncoderSession"/> for those options instead: the same contract, with
/// <see cref="IImageEncoderSession.IsStreaming"/> false.
/// </para>
/// </summary>
public abstract class StreamingEncoderSession : IImageEncoderSession
{
    private FrameWriter? _current;
    private int _frameCount;
    private bool _committed;
    private bool _disposed;

    /// <summary>Starts a session that writes to <paramref name="output"/>.</summary>
    protected StreamingEncoderSession(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Output = output;
    }

    /// <summary>Where the file is written.</summary>
    protected IBufferWriter<byte> Output { get; }

    /// <inheritdoc />
    public bool IsStreaming => true;

    /// <inheritdoc />
    public abstract int MaxFrames { get; }

    /// <summary>The largest width and height the format stores (checked by <see cref="AddFrame"/>).</summary>
    protected virtual int MaxDimension => int.MaxValue;

    /// <inheritdoc />
    public IImageFrameEncoder AddFrame(int width, int height, PixelFormat format, ColorEncoding? color = null,
        ImageMetadata? metadata = null, TimeSpan? duration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
            throw new InvalidOperationException("The session is committed.");
        if (_current is { Complete: false })
            throw new InvalidOperationException($"Frame {_frameCount - 1} is incomplete: {_current.RowsWritten} of {_current.Height} rows written.");
        if (_frameCount >= MaxFrames)
            throw new InvalidOperationException($"The format holds at most {MaxFrames} frame(s).");
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension)
            throw new ArgumentException($"A frame is 1..{MaxDimension} pixels in each direction, not {width}x{height}.");
        if ((long)width * format.BytesPerPixel > Array.MaxLength)
            throw new ArgumentException($"A {width}-pixel {format} row does not fit in memory.");
        BeginFrame(_frameCount, width, height, format, color, metadata, duration);
        _current = new FrameWriter(this, width, height, format);
        _frameCount++;
        return _current;
    }

    /// <inheritdoc />
    public void Commit(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
            throw new InvalidOperationException("The session is already committed.");
        if (_current is null)
            throw new InvalidOperationException("No frame was added.");
        if (!_current.Complete)
            throw new InvalidOperationException($"Frame {_frameCount - 1} is incomplete: {_current.RowsWritten} of {_current.Height} rows written.");
        cancellationToken.ThrowIfCancellationRequested();
        _committed = true;
        Finish(cancellationToken);
    }

    /// <summary>Starts frame <paramref name="index"/>: what a codec writes before the rows (a header, a frame control).</summary>
    protected abstract void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
        ImageMetadata? metadata, TimeSpan? duration);

    /// <summary>
    /// Encodes the next <paramref name="rowCount"/> rows (at least one, never past the frame), <paramref name="stride"/>
    /// bytes apart (at least a row), in the frame's format.
    /// </summary>
    protected abstract void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken);

    /// <summary>Called once the frame's last row is encoded: what a codec writes after its rows (a flush, an end marker).</summary>
    protected virtual void EndFrame(CancellationToken cancellationToken) { }

    /// <summary>Writes what the file still needs after its last frame (a trailer).</summary>
    protected abstract void Finish(CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="bytes"/> to <see cref="Output"/>.</summary>
    protected void WriteOutput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        var span = Output.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        Output.Advance(bytes.Length);
    }

    /// <summary>Releases the codec's resources.</summary>
    protected virtual void Dispose(bool disposing) { }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current = null;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private sealed class FrameWriter(StreamingEncoderSession session, int width, int height, PixelFormat format) : IImageFrameEncoder
    {
        public int Height => height;

        public int RowsWritten { get; private set; }

        public bool Complete => RowsWritten == height;

        public void WriteRows(ReadOnlySpan<byte> rows, int rowCount, int stride = 0, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(session._disposed, session);
            if (session._current != this || session._committed)
                throw new InvalidOperationException("The frame is no longer the session's current frame.");
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
            ArgumentOutOfRangeException.ThrowIfNegative(stride);
            if (rowCount > height - RowsWritten)
                throw new ArgumentException($"{rowCount} rows run past the frame: {RowsWritten} of {height} are written.", nameof(rowCount));
            if (rowCount == 0)
                return;
            var rowBytes = width * format.BytesPerPixel;
            var step = stride == 0 ? rowBytes : stride;
            if (step < rowBytes)
                throw new ArgumentException($"A stride of {stride} is shorter than a {rowBytes}-byte row.", nameof(stride));
            if (rows.Length < (long)step * (rowCount - 1) + rowBytes)
                throw new ArgumentException($"{rowCount} rows at stride {step} need more than {rows.Length} bytes.", nameof(rows));
            session.EncodeRows(rows, rowCount, step, cancellationToken);
            RowsWritten += rowCount;
            if (Complete)
                session.EndFrame(cancellationToken);
        }
    }
}
