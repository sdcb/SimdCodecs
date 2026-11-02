using System.Buffers;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// The boolean entropy encoder of RFC 6386 Section 7: the exact counterpart of
/// <see cref="Vp8BoolDecoder"/>. A bool with probability <c>p</c>/256 of being 0 splits the range at
/// <c>1 + ((range − 1) · p >> 8)</c>; a 1 moves the low end up by the split. The low end is a 32-bit
/// window onto the code value: whole bytes leave its top when 8 more bits have been shifted in, and a
/// carry out of the window is added to the bytes already written.
/// </summary>
internal sealed class Vp8BoolEncoder : IDisposable
{
    private byte[] _buffer;
    private int _length;
    private uint _low;
    private uint _range = 255;

    // Shifts left before the top byte of the window is complete.
    private int _bitsToByte = 24;

    public Vp8BoolEncoder(int capacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 64));
    }

    /// <summary>The bytes written so far plus the bits still in the window, as whole bytes.</summary>
    public long ApproximateLength => _length + 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(bool bit, int probability)
    {
        uint split = 1 + (((_range - 1) * (uint)probability) >> 8);
        if (bit)
        {
            _low += split;
            _range -= split;
        }
        else
        {
            _range = split;
        }

        if (_range < 128)
        {
            Normalize();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(int bit, int probability) => Put(bit != 0, probability);

    /// <summary>A bool at probability 1/2: what RFC 6386 calls a literal bit.</summary>
    public void PutBit(int bit) => Put(bit != 0, 128);

    /// <summary>An unsigned <paramref name="bits"/>-bit value, most significant bit first.</summary>
    public void PutLiteral(int value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            Put((value >> i) & 1, 128);
        }
    }

    /// <summary>A magnitude of <paramref name="bits"/> bits followed by a sign bit (1 = negative).</summary>
    public void PutSignedLiteral(int value, int bits)
    {
        PutLiteral(Math.Abs(value), bits);
        PutBit(value < 0 ? 1 : 0);
    }

    /// <summary>Walks a tree (Section 8.1) from <paramref name="start"/> to the leaf <paramref name="value"/>.</summary>
    public void PutTree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities, int value, int start = 0)
    {
        Span<int> path = stackalloc int[16];
        int depth = Vp8EncoderCosts.TreePath(tree, value, start, path, out _);
        for (int i = 0; i < depth; i++)
        {
            int node = path[i] >> 1, bit = path[i] & 1;
            Put(bit, probabilities[node >> 1]);
        }
    }

    private void Normalize()
    {
        do
        {
            _range <<= 1;
            if ((_low & 0x80000000u) != 0)
            {
                PropagateCarry();
            }

            _low <<= 1;
            if (--_bitsToByte == 0)
            {
                Emit((byte)(_low >> 24));
                _low &= 0xFFFFFF;
                _bitsToByte = 8;
            }
        }
        while (_range < 128);
    }

    private void PropagateCarry()
    {
        // A carry out of the window adds one to the bytes already out: trailing 0xFF bytes become 0.
        int i = _length - 1;
        while (i >= 0 && _buffer[i] == 0xFF)
        {
            _buffer[i--] = 0;
        }

        if (i >= 0)
        {
            _buffer[i]++;
        }
    }

    private void Emit(byte value)
    {
        if (_length == _buffer.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            _buffer.AsSpan(0, _length).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }

        _buffer[_length++] = value;
    }

    /// <summary>
    /// Pushes the window out with 32 zero bits at probability 1/2, so that the code value is fixed by
    /// bytes actually written (a decoder need not pad), and returns everything written.
    /// </summary>
    public ReadOnlySpan<byte> Finish()
    {
        for (int i = 0; i < 32; i++)
        {
            Put(false, 128);
        }

        return _buffer.AsSpan(0, _length);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}
