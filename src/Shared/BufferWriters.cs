using System.Buffers;

// Compiled into each package that uses it (see MqDecoder.cs for why it is shared as source), under
// that package's namespace so the copies are distinct types.
#if SIMDCODECS_QOI
namespace Sdcb.SimdCodecs.Qoi;
#elif SIMDCODECS_PNM
namespace Sdcb.SimdCodecs.Pnm;
#else
#error BufferWriters.cs is shared source for Sdcb.SimdCodecs.Qoi and Sdcb.SimdCodecs.Pnm only.
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

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over one array of a size known up front (or a worst case): no growth and no
/// copy when the output fills it exactly.
/// </summary>
internal sealed class ArrayWriter(byte[] array) : IBufferWriter<byte>
{
    public int Written { get; private set; }

    public byte[] Array => array;

    public void Advance(int count) => Written += count;

    public Memory<byte> GetMemory(int sizeHint = 0) => Reserve(sizeHint) ? array.AsMemory(Written) : default;

    public Span<byte> GetSpan(int sizeHint = 0) => Reserve(sizeHint) ? array.AsSpan(Written) : default;

    private bool Reserve(int sizeHint) =>
        array.Length - Written >= Math.Max(sizeHint, 1) ? true : throw new InvalidOperationException("The output is larger than its array.");

    /// <summary>The output: the array itself when it is full, else a copy of what was written.</summary>
    public byte[] ToArray() => Written == array.Length ? array : array.AsSpan(0, Written).ToArray();
}
