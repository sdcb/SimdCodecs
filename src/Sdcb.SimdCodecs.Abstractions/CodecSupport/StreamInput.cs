using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>
/// A forward-only, refillable window over a <see cref="Stream"/>: what a streaming decoder reads its input through.
/// <see cref="Buffered"/> is the bytes read but not yet <see cref="Consume">consumed</see>; <see cref="Fill"/> /
/// <see cref="FillAsync"/> read more. The buffer grows only by doubling as data actually arrives, never to a size a
/// header declares, so a malformed length cannot make it allocate without bound.
/// <para>
/// With <c>retainConsumed</c> every byte read is kept, so <see cref="Rewind"/> can go back to any offset of a stream that
/// cannot seek (the memory a whole-file copy would take, but filled as the decode goes). A seekable stream rewinds by
/// seeking instead.
/// </para>
/// </summary>
public sealed class StreamInput : IDisposable
{
    /// <summary>The buffer size a new input starts with.</summary>
    public const int DefaultCapacity = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _retain;
    private readonly long _origin;
    private byte[] _buffer;
    private long _bufferOffset;   // stream offset (from the image start) of _buffer[0]
    private int _start;           // first unconsumed byte in _buffer
    private int _end;             // one past the last byte read into _buffer
    private bool _completed;
    private bool _disposed;

    /// <summary>An input over <paramref name="stream"/>, positioned at the start of the image.</summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the input is disposed.</param>
    /// <param name="retainConsumed">Keep consumed bytes so that <see cref="Rewind"/> works without seeking.</param>
    /// <param name="initialCapacity">The first buffer's size in bytes.</param>
    public StreamInput(Stream stream, bool leaveOpen = false, bool retainConsumed = false, int initialCapacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 16);
        if (!stream.CanRead)
            throw new ArgumentException("The stream cannot be read.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
        _retain = retainConsumed;
        _origin = stream.CanSeek ? stream.Position : 0;
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    /// <summary>The bytes read and not yet consumed.</summary>
    public ReadOnlySpan<byte> Buffered => _buffer.AsSpan(_start, _end - _start);

    /// <summary><see cref="Buffered"/> as memory; valid until the next fill, consume or rewind.</summary>
    public ReadOnlyMemory<byte> BufferedMemory => _buffer.AsMemory(_start, _end - _start);

    /// <summary>The offset, from the start of the image, of <see cref="Buffered"/>'s first byte.</summary>
    public long Position => _bufferOffset + _start;

    /// <summary>True once the stream has ended: <see cref="Buffered"/> is everything that is left.</summary>
    public bool IsCompleted => _completed;

    /// <summary>The image's length in bytes when the stream can tell (it can seek); −1 otherwise.</summary>
    public long Length => _stream.CanSeek ? _stream.Length - _origin : -1;

    /// <summary>True when <see cref="Rewind"/> can go back before <see cref="Position"/>.</summary>
    public bool CanRewind => _retain || _stream.CanSeek;

    /// <summary>Marks the first <paramref name="count"/> bytes of <see cref="Buffered"/> as read.</summary>
    public void Consume(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _end - _start);
        _start += count;
    }

    /// <summary>
    /// Reads until <see cref="Buffered"/> holds at least <paramref name="minimumBytes"/> bytes or the stream ends; returns
    /// whether it does. One read fills as much of the buffer as the stream returns, so asking for one byte more than is
    /// buffered is how a decoder asks for "more".
    /// </summary>
    /// <exception cref="InvalidDataException"><paramref name="minimumBytes"/> is more than an array can hold.</exception>
    public bool Fill(int minimumBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckMinimum(minimumBytes);
        while (_end - _start < minimumBytes && !_completed)
        {
            var free = PrepareSpace();
            var n = _stream.Read(_buffer, _end, free);
            Advance(n);
        }
        return _end - _start >= minimumBytes;
    }

    /// <summary><see cref="Fill"/>, reading the stream asynchronously.</summary>
    /// <exception cref="InvalidDataException"><paramref name="minimumBytes"/> is more than an array can hold.</exception>
    public async ValueTask<bool> FillAsync(int minimumBytes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckMinimum(minimumBytes);
        while (_end - _start < minimumBytes && !_completed)
        {
            var free = PrepareSpace();
            var n = await _stream.ReadAsync(_buffer.AsMemory(_end, free), cancellationToken).ConfigureAwait(false);
            Advance(n);
        }
        return _end - _start >= minimumBytes;
    }

    /// <summary>Reads the rest of the stream into <see cref="Buffered"/> and returns it.</summary>
    /// <exception cref="InvalidDataException">The rest is longer than an array can hold.</exception>
    public ReadOnlyMemory<byte> ReadToEnd()
    {
        while (!_completed)
            Fill(checked(_end - _start + 1));
        return BufferedMemory;
    }

    /// <summary><see cref="ReadToEnd"/>, reading the stream asynchronously.</summary>
    /// <exception cref="InvalidDataException">The rest is longer than an array can hold.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadToEndAsync(CancellationToken cancellationToken = default)
    {
        while (!_completed)
            await FillAsync(checked(_end - _start + 1), cancellationToken).ConfigureAwait(false);
        return BufferedMemory;
    }

    /// <summary>
    /// Moves <see cref="Position"/> back to <paramref name="position"/>: within the buffer when it is still there (always,
    /// with <c>retainConsumed</c>), otherwise by seeking the stream.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative or past <see cref="Position"/>.</exception>
    /// <exception cref="NotSupportedException">The bytes are gone and the stream cannot seek.</exception>
    public void Rewind(long position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Position);
        if (position >= _bufferOffset)
        {
            _start = (int)(position - _bufferOffset);
            return;
        }
        if (!_stream.CanSeek)
            throw new NotSupportedException("The stream cannot seek and the bytes asked for were not retained.");
        _stream.Position = _origin + position;
        _bufferOffset = position;
        _start = _end = 0;
        _completed = false;
    }

    /// <summary>Returns the buffer to the pool and, unless asked to leave it open, disposes the stream.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _start = _end = 0;
        if (!_leaveOpen)
            _stream.Dispose();
    }

    // Makes room after _end for at least one more byte: compacts consumed bytes away (unless retained), then doubles the
    // buffer when it is still full. Returns the free bytes after _end.
    private int PrepareSpace()
    {
        if (_end == _buffer.Length && _start > 0 && !_retain)
        {
            var live = _end - _start;
            _buffer.AsSpan(_start, live).CopyTo(_buffer);
            _bufferOffset += _start;
            _start = 0;
            _end = live;
        }
        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= Array.MaxLength)
                throw new InvalidDataException("The image is longer than an array can hold.");
            var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)_buffer.Length * 2, Array.MaxLength));
            var keep = _retain ? 0 : _start;
            _buffer.AsSpan(keep, _end - keep).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
            _bufferOffset += keep;
            _start -= keep;
            _end -= keep;
        }
        return _buffer.Length - _end;
    }

    private static void CheckMinimum(int minimumBytes)
    {
        if (minimumBytes > Array.MaxLength)
            throw new InvalidDataException($"The image needs {minimumBytes} bytes buffered, more than an array can hold.");
    }

    private void Advance(int read)
    {
        if (read <= 0)
            _completed = true;
        else
            _end += read;
    }
}
