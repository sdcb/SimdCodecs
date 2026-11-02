using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// The VP8L bit packer (RFC 9649 Section 3.1): values are written least significant bit first,
/// filling each byte from its low bit. Bytes go to a pooled, growing buffer.
/// </summary>
internal sealed class Vp8LBitWriter : IDisposable
{
    private byte[] _buffer;
    private int _length;
    private ulong _bits;
    private int _count;

    public Vp8LBitWriter(int capacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 256));
    }

    /// <summary>Bits written so far, including those not yet flushed to bytes.</summary>
    public long BitLength => (long)_length * 8 + _count;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>; count is 0..32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBits(uint value, int count)
    {
        _bits |= (ulong)value << _count;
        _count += count;
        if (_count >= 32)
        {
            Flush32();
        }
    }

    private void Flush32()
    {
        if (_length + 8 > _buffer.Length)
        {
            Grow(8);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), (uint)_bits);
        _length += 4;
        _bits >>= 32;
        _count -= 32;
    }

    private void Grow(int extra)
    {
        byte[] larger = ArrayPool<byte>.Shared.Rent(checked(Math.Max(_buffer.Length * 2, _length + extra)));
        _buffer.AsSpan(0, _length).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    /// <summary>Pads the last byte with zero bits and returns everything written.</summary>
    public ReadOnlySpan<byte> Finish()
    {
        while (_count > 0)
        {
            if (_length + 1 > _buffer.Length)
            {
                Grow(1);
            }

            _buffer[_length++] = (byte)_bits;
            _bits >>= 8;
            _count = Math.Max(0, _count - 8);
        }

        return _buffer.AsSpan(0, _length);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
