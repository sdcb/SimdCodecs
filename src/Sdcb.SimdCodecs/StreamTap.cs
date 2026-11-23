using System;
using System.Buffers;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Sdcb.SimdCodecs;

/// <summary>
/// An <see cref="ImageDecoder"/> session's one hold on its stream: every read of the stream goes through it, by offset
/// from the start of the image, so the header reader, a decode's row reader and the whole-file fallback can each read
/// from offset 0 without the stream being able to seek.
/// <para>
/// A seekable stream is read where asked by seeking. A non-seekable one is read forward only; while the tap
/// <em>records</em>, every byte read is kept (<see cref="Recorded"/>) and an earlier offset is served from the record.
/// A session that retains its input records for its whole life; a forward-only one stops when its decode starts
/// streaming (<see cref="StopRecording"/>), and from then on only the live position and what was recorded before can be
/// read (<see cref="CanRevisit"/> is false).
/// </para>
/// <para>
/// Concurrency: an async-opened session runs a background prefetch (<see cref="StartPrefetch"/>) beside the session's
/// own reads. One stream read is in flight at a time (<see cref="_reading"/>); the record and the counters change only
/// under <see cref="_gate"/>, and a read copies out of the record under it too. The record only grows by reallocating,
/// never in place, so a reader never sees a half-copied buffer. With <c>asyncOnly</c> (a session from
/// <c>OpenAsync</c>) a synchronous read is served by the asynchronous path, so the stream is never read synchronously.
/// </para>
/// </summary>
internal sealed class StreamTap : IDisposable
{
    /// <summary>How many leading bytes the session reads before it picks a codec.</summary>
    public const int SniffLength = 4096;

    private const int ChunkLength = 64 * 1024;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private byte[] _record = [];      // non-seekable: the bytes recorded, from offset 0
    private int _length;              // bytes in _record
    private long _live;               // non-seekable: bytes read from the stream so far
    private bool _recording;
    private bool _ended;              // non-seekable: the stream has returned 0
    private ExceptionDispatchInfo? _fault; // the prefetch's failure, rethrown to whoever needs bytes it did not get
    private byte[] _prefix = [];      // seekable: the sniffed bytes
    private Task? _prefetch;
    private bool _disposed;

    /// <param name="stream">The stream, positioned at the start of the image.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the tap is disposed.</param>
    /// <param name="asyncOnly">Never read <paramref name="stream"/> synchronously.</param>
    public StreamTap(Stream stream, bool leaveOpen, bool asyncOnly)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("The stream cannot be read.", nameof(stream));
        _stream = stream;
        _leaveOpen = leaveOpen;
        AsyncOnly = asyncOnly;
        CanSeek = stream.CanSeek;
        _origin = CanSeek ? stream.Position : 0;
        _recording = !CanSeek;
    }

    /// <summary>True when the stream seeks: every offset can be read again, nothing is recorded.</summary>
    public bool CanSeek { get; }

    /// <summary>True for a session from <c>OpenAsync</c>: the stream is only read asynchronously.</summary>
    public bool AsyncOnly { get; }

    /// <summary>True while every byte from offset 0 can be read again (the stream seeks, or the tap still records).</summary>
    public bool CanRevisit
    {
        get
        {
            lock (_gate)
                return CanSeek || _recording;
        }
    }

    /// <summary>The token a synchronous call on an async-only tap waits with (the caller's, set around the call).</summary>
    public CancellationToken SyncCancellation { get; set; }

    /// <summary>True when a background prefetch has been started.</summary>
    public bool HasPrefetch => _prefetch is not null;

    /// <summary>The leading bytes read so far (at least <see cref="SniffLength"/> after a sniff, unless the stream is shorter).
    /// For a non-seekable stream, everything recorded.</summary>
    public ReadOnlySpan<byte> Recorded
    {
        get
        {
            lock (_gate)
                return CanSeek ? _prefix : _record.AsSpan(0, _length);
        }
    }

    /// <summary>Stops recording a non-seekable stream: what is recorded stays readable, later bytes are not kept.</summary>
    public void StopRecording()
    {
        lock (_gate)
            _recording = false;
    }

    /// <summary>Reads the first <see cref="SniffLength"/> bytes (fewer when the stream is shorter).</summary>
    public void Sniff()
    {
        if (AsyncOnly)
        {
            Wait(SniffAsync(CancellationToken.None));
            return;
        }
        if (CanSeek)
        {
            _prefix = ReadPrefix(sync: true, CancellationToken.None).GetAwaiter().GetResult();
            return;
        }
        var chunk = new byte[SniffLength];
        while (RecordedLength < SniffLength && Read(LivePosition, chunk) > 0)
        {
        }
    }

    /// <summary><see cref="Sniff"/>, reading asynchronously.</summary>
    public async ValueTask SniffAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanSeek)
        {
            _prefix = await ReadPrefix(sync: false, cancellationToken).ConfigureAwait(false);
            return;
        }
        var chunk = new byte[SniffLength];
        while (RecordedLength < SniffLength && await ReadAsync(LivePosition, chunk, cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    /// <summary>
    /// Reads up to <c>destination.Length</c> bytes at <paramref name="offset"/>; 0 at the end of the stream.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bytes at <paramref name="offset"/> were read and not kept.</exception>
    public int Read(long offset, Span<byte> destination)
    {
        if (destination.IsEmpty)
            return 0;
        if (AsyncOnly)
        {
            // Served by the asynchronous path, so the stream is never read synchronously: a request body that forbids it
            // still works behind the synchronous API.
            var rented = ArrayPool<byte>.Shared.Rent(destination.Length);
            try
            {
                var n = Wait(ReadAsync(offset, rented.AsMemory(0, destination.Length), SyncCancellation));
                rented.AsSpan(0, n).CopyTo(destination);
                return n;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        if (CanSeek)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Position = _origin + offset;
            return _stream.Read(destination);
        }
        if (TryServe(offset, destination, out var served))
            return served;
        // Synchronous sessions have no prefetch, so nothing else reads the stream.
        var read = _stream.Read(destination);
        Complete(destination[..read]);
        return read;
    }

    /// <summary><see cref="Read(long, Span{byte})"/>, reading the stream asynchronously.</summary>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
            return 0;
        if (CanSeek)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Position = _origin + offset;
            return await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        while (true)
        {
            if (TryServe(offset, destination.Span, out var served))
                return served;
            await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The prefetch may have read past the offset while this read waited.
                if (TryServe(offset, destination.Span, out served))
                    return served;
                var read = await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
                Complete(destination.Span[..read]);
                return read;
            }
            finally
            {
                _reading.Release();
            }
        }
    }

    /// <summary>
    /// Starts reading the rest of a non-seekable stream into the record in the background. It stops at the end of the
    /// stream, on a failure (rethrown to whoever later needs the bytes it did not get) or when the tap is disposed.
    /// </summary>
    public void StartPrefetch()
    {
        if (CanSeek || _prefetch is not null)
            throw new InvalidOperationException("Only a non-seekable stream is prefetched, once.");
        var token = _lifetime.Token;
        _prefetch = Task.Run(() => PrefetchAsync(token), CancellationToken.None);
    }

    /// <summary>The whole image, reading what is left of the stream (or waiting for the prefetch to have read it).</summary>
    /// <exception cref="InvalidOperationException">The tap has stopped recording a non-seekable stream.</exception>
    /// <exception cref="InvalidDataException">The image is longer than an array can hold.</exception>
    public ReadOnlyMemory<byte> ReadAll()
    {
        if (AsyncOnly)
            return Wait(ReadAllAsync(SyncCancellation));
        if (CanSeek)
            return ReadSeekable(sync: true, CancellationToken.None).GetAwaiter().GetResult();
        CheckRevisit();
        var chunk = new byte[ChunkLength];
        while (Read(LivePosition, chunk) > 0)
        {
        }
        return RecordedMemory();
    }

    /// <summary><see cref="ReadAll"/>, reading asynchronously.</summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (CanSeek)
            return await ReadSeekable(sync: false, cancellationToken).ConfigureAwait(false);
        CheckRevisit();
        if (_prefetch is { } prefetch)
        {
            await prefetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var chunk = new byte[ChunkLength];
            while (await ReadAsync(LivePosition, chunk, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        return RecordedMemory();
    }

    /// <summary>Cancels the prefetch and, unless asked to leave it open, disposes the stream (otherwise waits for the cancelled prefetch to stop).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _record = [];
            _length = 0;
        }
        _lifetime.Cancel();
        if (!_leaveOpen)
        {
            // Disposing the stream ends a read in flight; the prefetch then stops on its own.
            _stream.Dispose();
        }
        else if (_prefetch is { } prefetch)
        {
            // The caller keeps the stream: let the cancelled read finish before handing it back, so the prefetch never
            // reads it concurrently with the caller. The prefetch catches everything, so this does not throw.
            prefetch.GetAwaiter().GetResult();
        }
    }

    private int RecordedLength
    {
        get
        {
            lock (_gate)
                return _length;
        }
    }

    private long LivePosition
    {
        get
        {
            lock (_gate)
                return _live;
        }
    }

    private void CheckRevisit()
    {
        if (!CanRevisit)
            throw new InvalidOperationException(ImageDecoder.ForwardOnlyMessage);
    }

    private ReadOnlyMemory<byte> RecordedMemory()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _fault?.Throw();
            return _record.AsMemory(0, _length);
        }
    }

    // Serves a read from the record, or the end of the stream; false when the stream must be read at the live position.
    private bool TryServe(long offset, Span<byte> destination, out int count)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < _length)
            {
                count = (int)Math.Min(destination.Length, _length - offset);
                _record.AsSpan((int)offset, count).CopyTo(destination);
                return true;
            }
            _fault?.Throw();
            if (offset != _live)
                throw new InvalidOperationException(ImageDecoder.ForwardOnlyMessage);
            count = 0;
            return _ended;
        }
    }

    // Accounts for bytes just read from the stream (none: its end).
    private void Complete(ReadOnlySpan<byte> read)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (read.IsEmpty)
            {
                _ended = true;
                return;
            }
            if (_recording)
            {
                if (read.Length > Array.MaxLength - _length)
                    throw new InvalidDataException("The image is longer than an array can hold.");
                if (_length + read.Length > _record.Length)
                {
                    // Doubling, in new memory rather than a pool's: the record becomes the session's data, which its
                    // frame reader keeps, so it is never handed back for reuse.
                    var capacity = (int)Math.Min(Array.MaxLength, Math.Max((long)_length + read.Length, Math.Max(2L * _record.Length, SniffLength)));
                    var grown = GC.AllocateUninitializedArray<byte>(capacity);
                    _record.AsSpan(0, _length).CopyTo(grown);
                    _record = grown;
                }
                read.CopyTo(_record.AsSpan(_length));
                _length += read.Length;
            }
            _live += read.Length;
        }
    }

    private async Task PrefetchAsync(CancellationToken cancellationToken)
    {
        var chunk = new byte[ChunkLength];
        try
        {
            while (true)
            {
                await _reading.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (_ended || _disposed)
                            return;
                    }
                    var read = await _stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    Complete(chunk.AsSpan(0, read));
                    if (read == 0)
                        return;
                }
                finally
                {
                    _reading.Release();
                }
            }
        }
        catch (Exception e)
        {
            // Kept for whoever needs the bytes; a prefetch stopped by Dispose is never asked.
            lock (_gate)
                _fault ??= ExceptionDispatchInfo.Capture(e);
        }
    }

    private async ValueTask<byte[]> ReadPrefix(bool sync, CancellationToken cancellationToken)
    {
        var prefix = new byte[(int)Math.Clamp(_stream.Length - _origin, 0, SniffLength)];
        var n = await FillSeekable(prefix, sync, cancellationToken).ConfigureAwait(false);
        return n == prefix.Length ? prefix : prefix[..n];
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadSeekable(bool sync, CancellationToken cancellationToken)
    {
        var length = _stream.Length - _origin;
        if (length > Array.MaxLength)
            throw new InvalidDataException($"The image is {length} bytes, more than an array can hold.");
        var all = new byte[Math.Max(0, length)];
        if (await FillSeekable(all, sync, cancellationToken).ConfigureAwait(false) != all.Length)
            throw new InvalidDataException("The stream ended before its declared length.");
        return all;
    }

    private async ValueTask<int> FillSeekable(byte[] buffer, bool sync, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = sync
                ? Read(total, buffer.AsSpan(total))
                : await ReadAsync(total, buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // Blocks on an asynchronous read already in flight: how a synchronous call on an async-opened session waits without
    // reading the stream synchronously.
    private static T Wait<T>(ValueTask<T> task) => task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

    private static void Wait(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
            task.AsTask().GetAwaiter().GetResult();
    }
}
