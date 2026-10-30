using System.Buffers;

// Compiled into each package that uses it (see MqDecoder.cs for why it is shared as source), under
// that package's namespace so the copies are distinct types.
#if SIMDCODECS_QOI
namespace Sdcb.SimdCodecs.Qoi;
#elif SIMDCODECS_PNM
namespace Sdcb.SimdCodecs.Pnm;
#else
#error StreamBufferWriter.cs is shared source for Sdcb.SimdCodecs.Qoi and Sdcb.SimdCodecs.Pnm only.
#endif

/// <summary>An <see cref="IBufferWriter{T}"/> over a stream, through one pooled 64 KiB buffer.</summary>
internal sealed class StreamBufferWriter(Stream stream) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
    private int _used;

    public void Advance(int count) => _used += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsMemory(_used);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _buffer.AsSpan(_used);
    }

    private void Reserve(int sizeHint)
    {
        if (_buffer.Length - _used >= Math.Max(sizeHint, 1)) return;
        Flush();
        if (_buffer.Length < sizeHint)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        }
    }

    public void Flush()
    {
        stream.Write(_buffer, 0, _used);
        _used = 0;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
