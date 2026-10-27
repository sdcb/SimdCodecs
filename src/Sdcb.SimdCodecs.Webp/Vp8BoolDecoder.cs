using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// RFC 6386 §7 boolean entropy decoder with a wide window: <see cref="_value"/> holds the 8-bit
/// comparison window plus <see cref="_bits"/> further input bits below it, so the input is read
/// 7 bytes at a time instead of one bit per renormalisation step. Past the end of the data the
/// input reads as zero bits, as RFC 6386 §7.3's reference behaviour implies.
/// </summary>
internal ref struct Vp8BoolDecoder
{
    private ref byte _next;
    private int _remaining;

    // Invariant whenever _bits >= 0: _value < (_rangeMinus1 + 1) << _bits. When _bits is negative,
    // the low -_bits bits of the window have not been read yet; the next ReadBit refills first.
    // The range is kept minus one: split - 1 = ((range - 1) * p) >> 8 is then one multiply and
    // shift, and the decision is window > split - 1 (RFC 6386 §7.3's arithmetic, rearranged).
    private ulong _value;
    private int _bits;
    private uint _rangeMinus1;

    public Vp8BoolDecoder(ReadOnlySpan<byte> data)
    {
        _next = ref MemoryMarshal.GetReference(data);
        _remaining = data.Length;
        _value = 0;
        _bits = -8;
        _rangeMinus1 = 254;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadBit(int probability)
    {
        if (_bits < 0)
        {
            Refill();
        }

        uint rangeMinus1 = _rangeMinus1;
        uint splitMinus1 = (rangeMinus1 * (uint)probability) >> 8;
        uint range;
        int bit;
        if ((_value >> _bits) > splitMinus1)
        {
            _value -= (ulong)(splitMinus1 + 1) << _bits;
            range = rangeMinus1 - splitMinus1;
            bit = 1;
        }
        else
        {
            range = splitMinus1 + 1;
            bit = 0;
        }

        // range is in [1, 255]; shift it back into [128, 255].
        int shift = BitOperations.LeadingZeroCount(range) - 24;
        _rangeMinus1 = (range << shift) - 1;
        _bits -= shift;
        return bit;
    }

    /// <summary>Copies the arithmetic state out for <see cref="ReadBit(ref Vp8BoolDecoder, ref ulong, ref int, ref uint, int)"/>.</summary>
    public readonly void Load(out ulong value, out int bits, out uint range)
    {
        value = _value;
        bits = _bits;
        range = _rangeMinus1;
    }

    public void Store(ulong value, int bits, uint range)
    {
        _value = value;
        _bits = bits;
        _rangeMinus1 = range;
    }

    /// <summary>
    /// <see cref="ReadBit(int)"/> on state held in the caller's locals (a struct reached by ref
    /// stays in memory, and every bit then waits on a store-to-load round trip). Only the input
    /// cursor lives in <paramref name="source"/>; it is touched once per 56 bits.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadBit(ref Vp8BoolDecoder source, ref ulong value, ref int bits, ref uint range, int probability)
    {
        if (bits < 0)
        {
            if (source._remaining >= 8)
            {
                // _remaining >= 8 proves the 8-byte read is in range; only 7 of them are consumed.
                ulong raw = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref source._next));
                value = (value << 56) | (raw >> 8);
                source._next = ref Unsafe.Add(ref source._next, 7);
                source._remaining -= 7;
                bits += 56;
            }
            else
            {
                source._value = value;
                source._bits = bits;
                source.RefillTail();
                value = source._value;
                bits = source._bits;
            }
        }

        // range holds range - 1 (see _rangeMinus1). The state is updated inside the branch on the
        // decoded bit: when it is predicted, the speculated path skips any select; a branchy
        // tree-walk microbenchmark measured this 15% faster than select forms.
        uint splitMinus1 = (range * (uint)probability) >> 8;
        uint newRange;
        int bit;
        if ((value >> bits) > splitMinus1)
        {
            value -= (ulong)(splitMinus1 + 1) << bits;
            newRange = range - splitMinus1;
            bit = 1;
        }
        else
        {
            newRange = splitMinus1 + 1;
            bit = 0;
        }

        int shift = BitOperations.LeadingZeroCount(newRange) - 24;
        range = (newRange << shift) - 1;
        bits -= shift;
        return bit;
    }

    /// <summary>
    /// Reads a probability-128 bit (a coefficient sign) and returns <paramref name="magnitude"/>
    /// negated when it is set. Arithmetic instead of a branch: a sign is a coin flip and would
    /// mispredict about half the time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadSigned(ref Vp8BoolDecoder source, ref ulong value, ref int bits, ref uint range, int magnitude)
    {
        if (bits < 0)
        {
            if (source._remaining >= 8)
            {
                // _remaining >= 8 proves the 8-byte read is in range; only 7 of them are consumed.
                ulong raw = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref source._next));
                value = (value << 56) | (raw >> 8);
                source._next = ref Unsafe.Add(ref source._next, 7);
                source._remaining -= 7;
                bits += 56;
            }
            else
            {
                source._value = value;
                source._bits = bits;
                source.RefillTail();
                value = source._value;
                bits = source._bits;
            }
        }

        // split - 1 = ((range - 1) * 128) >> 8. The window is below 256, so both fit an int and
        // mask is -1 exactly when the window exceeds split - 1 (bit = 1).
        int splitMinus1 = (int)(range >> 1);
        int mask = (splitMinus1 - (int)(value >> bits)) >> 31;
        value -= (ulong)(uint)((splitMinus1 + 1) & mask) << bits;
        // bit 1: range - 1 - (split - 1) = range - split; bit 0: split.
        uint newRange = (uint)(splitMinus1 + 1 + (((int)range - 2 * splitMinus1 - 1) & mask));
        int shift = BitOperations.LeadingZeroCount(newRange) - 24;
        range = (newRange << shift) - 1;
        bits -= shift;
        return (magnitude ^ mask) - mask;
    }

    public int ReadLiteral(int bitCount)
    {
        int value = 0;
        for (int i = 0; i < bitCount; i++)
        {
            value = (value << 1) | ReadBit(128);
        }

        return value;
    }

    public int ReadSignedLiteral(int bitCount)
    {
        int value = ReadLiteral(bitCount);
        return ReadBit(128) != 0 ? -value : value;
    }

    // Called with _bits in [-8, -1]: the window holds at most 7 valid bits, so 56 more fit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Refill()
    {
        if (_remaining >= 8)
        {
            // _remaining >= 8 proves the 8-byte read is in range; only 7 of them are consumed.
            ulong raw = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref _next));
            _value = (_value << 56) | (raw >> 8);
            _next = ref Unsafe.Add(ref _next, 7);
            _remaining -= 7;
            _bits += 56;
        }
        else
        {
            RefillTail();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RefillTail()
    {
        do
        {
            ulong next = 0;
            if (_remaining > 0)
            {
                next = _next;
                _next = ref Unsafe.Add(ref _next, 1);
                _remaining--;
            }

            _value = (_value << 8) | next;
            _bits += 8;
        }
        while (_bits < 0);
    }
}

internal ref struct Vp8TokenPartitions
{
    private Vp8BoolDecoder _p0;
    private Vp8BoolDecoder _p1;
    private Vp8BoolDecoder _p2;
    private Vp8BoolDecoder _p3;
    private Vp8BoolDecoder _p4;
    private Vp8BoolDecoder _p5;
    private Vp8BoolDecoder _p6;
    private Vp8BoolDecoder _p7;

    public Vp8TokenPartitions(ReadOnlySpan<byte> p0, ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2,
        ReadOnlySpan<byte> p3, ReadOnlySpan<byte> p4, ReadOnlySpan<byte> p5,
        ReadOnlySpan<byte> p6, ReadOnlySpan<byte> p7)
    {
        _p0 = new Vp8BoolDecoder(p0);
        _p1 = new Vp8BoolDecoder(p1);
        _p2 = new Vp8BoolDecoder(p2);
        _p3 = new Vp8BoolDecoder(p3);
        _p4 = new Vp8BoolDecoder(p4);
        _p5 = new Vp8BoolDecoder(p5);
        _p6 = new Vp8BoolDecoder(p6);
        _p7 = new Vp8BoolDecoder(p7);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Vp8BoolDecoder PartitionRef(int partition)
    {
        // Unsafe.AsRef bypasses CS8170 (structs may not return instance fields by
        // ref); safe here because callers can only use the ref while this ref
        // struct is alive.
        switch (partition)
        {
            case 0: return ref Unsafe.AsRef(in _p0);
            case 1: return ref Unsafe.AsRef(in _p1);
            case 2: return ref Unsafe.AsRef(in _p2);
            case 3: return ref Unsafe.AsRef(in _p3);
            case 4: return ref Unsafe.AsRef(in _p4);
            case 5: return ref Unsafe.AsRef(in _p5);
            case 6: return ref Unsafe.AsRef(in _p6);
            case 7: return ref Unsafe.AsRef(in _p7);
            default: throw new ArgumentOutOfRangeException(nameof(partition));
        }
    }
}
