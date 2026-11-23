namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>
/// A stream that cannot seek and returns its bytes in small, seeded-random pieces (1..<c>maxChunk</c> per read): the
/// network or request-body stand-in streaming decoders are tested against. With <c>asyncOnly</c> a synchronous read
/// throws, as Kestrel's request body does by default. It counts the bytes handed out, so a test can see how far a
/// decoder has read.
/// </summary>
internal sealed class ChunkedStream(byte[] data, int maxChunk = 7, int seed = 1, bool asyncOnly = false) : Stream
{
    private readonly Random _random = new(seed);
    private int _position;

    /// <summary>Bytes handed out so far.</summary>
    public int BytesRead => _position;

    /// <summary>Reads made so far (synchronous and asynchronous).</summary>
    public int ReadCalls { get; private set; }

    public bool WasDisposed { get; private set; }

    public override bool CanRead => !WasDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (asyncOnly)
            throw new InvalidOperationException("Synchronous operations are disallowed.");
        return Next(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        return Next(buffer.Span);
    }

    private int Next(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(WasDisposed, this);
        ReadCalls++;
        if (buffer.IsEmpty) return 0;
        var n = Math.Min(Math.Min(buffer.Length, data.Length - _position), _random.Next(1, maxChunk + 1));
        data.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}
