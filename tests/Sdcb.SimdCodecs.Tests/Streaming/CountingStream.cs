namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>A read-only, seekable <see cref="MemoryStream"/> that counts the bytes its reads hand out.</summary>
internal sealed class CountingStream(byte[] data) : MemoryStream(data, writable: false)
{
    public long BytesRead { get; private set; }

    // MemoryStream's span and memory reads route a derived type's reads through this overload, so it alone counts.
    public override int Read(byte[] buffer, int offset, int count) => Count(base.Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    private int Count(int n)
    {
        BytesRead += n;
        return n;
    }
}
