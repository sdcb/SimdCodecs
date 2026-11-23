using System.Buffers;
using System.IO;

namespace Sdcb.SimdCodecs;

/// <summary>
/// The <see cref="IBufferWriter{T}"/> an encoder writes into when its file goes to a <see cref="Stream"/>: a pooled chunk
/// of <see cref="ChunkSize"/> bytes that is written to the stream as it fills. With <see cref="FlushWhenFull"/> (the
/// synchronous path) a request for more space than is left writes the pending bytes synchronously first, so the buffer
/// stays at one chunk unless a single request is larger. Without it (the asynchronous path, which cannot write from
/// inside <see cref="GetSpan"/>) the buffer grows, and the caller writes the pending bytes with <see cref="FlushAsync"/>
/// between the encoder's calls.
/// </summary>
internal sealed class StreamBufferWriter : IBufferWriter<byte>, IDisposable
{
    /// <summary>The pending bytes at which a caller writes them to the stream, and the buffer's first size.</summary>
    public const int ChunkSize = 64 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _count;

    public StreamBufferWriter(Stream stream)
    {
        _stream = stream;
        _buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
    }

    /// <summary>Write the pending bytes synchronously when a request does not fit.</summary>
    public bool FlushWhenFull { get; set; } = true;

    /// <summary>Bytes the encoder wrote that have not reached the stream yet.</summary>
    public int Pending => _count;

    /// <summary>True once <see cref="Pending"/> is a chunk or more: time to write them.</summary>
    public bool ChunkReady => _count >= ChunkSize;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _count)
            throw new InvalidOperationException($"Advanced {count} bytes past the {_buffer.Length - _count} the buffer has left.");
        _count += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsMemory(_count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsSpan(_count);
    }

    /// <summary>Writes the pending bytes to the stream, synchronously.</summary>
    public void Flush()
    {
        if (_count == 0) return;
        _stream.Write(_buffer, 0, _count);
        _count = 0;
        Shrink();
    }

    /// <summary>Writes the pending bytes to the stream, asynchronously.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_count == 0) return;
        await _stream.WriteAsync(_buffer.AsMemory(0, _count), cancellationToken).ConfigureAwait(false);
        _count = 0;
        Shrink();
    }

    // Once a large call's bytes are written, the buffer goes back to one chunk, so the session holds no more than a
    // band's output between calls.
    private void Shrink()
    {
        if (_buffer.Length <= 2 * ChunkSize) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
    }

    /// <summary>Returns the buffer to the pool; pending bytes are dropped.</summary>
    public void Dispose()
    {
        if (_buffer.Length == 0) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _count = 0;
    }

    private void Reserve(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var need = Math.Max(sizeHint, 1);
        if (_buffer.Length - _count >= need)
            return;
        if (FlushWhenFull && _count > 0)
        {
            Flush();
            if (_buffer.Length >= need)
                return;
        }
        var size = Math.Max((long)_buffer.Length * 2, (long)_count + need);
        if ((long)_count + need > Array.MaxLength)
            throw new InvalidOperationException($"The encoder asked for {need} more bytes than one buffer holds.");
        var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(size, Array.MaxLength));
        _buffer.AsSpan(0, _count).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }
}
