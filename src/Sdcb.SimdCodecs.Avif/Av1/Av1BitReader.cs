namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// MSB-first bitstream reader for AV1 OBU headers and uncompressed frame headers (§4.10, §8.1).
/// </summary>
internal ref struct Av1BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;
    private readonly int _bitLength;

    internal Av1BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPos = 0;
        _bitLength = data.Length * 8;
    }

    internal readonly int BitPosition => _bitPos;
    internal readonly int BitsRemaining => _bitLength - _bitPos;
    internal readonly int BytePosition => _bitPos >> 3;

    internal void EnsureBits(int n)
    {
        if (n < 0 || _bitPos + n > _bitLength)
            throw new InvalidDataException("Unexpected end of AV1 bitstream.");
    }

    internal uint ReadBits(int n)
    {
        if (n == 0) return 0;
        if (n > 32) throw new ArgumentOutOfRangeException(nameof(n));
        EnsureBits(n);
        uint value = 0;
        int remaining = n;
        while (remaining > 0)
        {
            int byteIdx = _bitPos >> 3;
            int bitOff = _bitPos & 7;
            int avail = 8 - bitOff;
            int take = Math.Min(remaining, avail);
            int shift = avail - take;
            uint mask = (1u << take) - 1u;
            value = (value << take) | (((uint)_data[byteIdx] >> shift) & mask);
            _bitPos += take;
            remaining -= take;
        }
        return value;
    }

    internal int ReadBit() => (int)ReadBits(1);
    internal bool ReadBool() => ReadBits(1) != 0;

    /// <summary>Signed n-bit integer: su(n) (§4.10.6).</summary>
    internal int ReadSu(int n)
    {
        int value = (int)ReadBits(n);
        int signMask = 1 << (n - 1);
        if ((value & signMask) != 0)
            value -= 2 * signMask;
        return value;
    }

    /// <summary>Variable-length unsigned integer: uvlc() (§4.10.3).</summary>
    internal uint ReadUvlc()
    {
        int leadingZeros = 0;
        while (true)
        {
            if (ReadBool()) break;
            leadingZeros++;
            if (leadingZeros >= 32) return uint.MaxValue;
        }
        uint value = ReadBits(leadingZeros);
        return value + (1u << leadingZeros) - 1u;
    }

    /// <summary>Non-symmetric unsigned encoded integer: ns(n) (§4.10.7).</summary>
    internal int ReadNs(int n)
    {
        if (n <= 1) return 0;
        int w = Av1Constants.FloorLog2(n) + 1;
        int m = (1 << w) - n;
        int v = (int)ReadBits(w - 1);
        if (v < m) return v;
        int extraBit = ReadBit();
        return (v << 1) - m + extraBit;
    }

    /// <summary>Unsigned little-endian n-byte integer: le(n) (§4.10.4).</summary>
    internal uint ReadLe(int nBytes)
    {
        if ((_bitPos & 7) != 0) throw new InvalidDataException("le(n) requires byte alignment.");
        uint t = 0;
        for (int i = 0; i < nBytes; i++)
            t |= ReadBits(8) << (i * 8);
        return t;
    }

    /// <summary>Unsigned LEB128 integer: leb128() (§4.10.5).</summary>
    internal static ulong ReadLeb128(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (offset >= data.Length)
                throw new InvalidDataException("Truncated LEB128 in AV1 OBU.");
            byte b = data[offset++];
            value |= (ulong)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0)
            {
                if (value > uint.MaxValue)
                    throw new InvalidDataException("AV1 LEB128 exceeds 32-bit limit.");
                return value;
            }
        }
        throw new InvalidDataException("AV1 LEB128 exceeds 8 bytes.");
    }

    internal void ByteAlign()
    {
        int rem = _bitPos & 7;
        if (rem != 0)
            ReadBits(8 - rem);
    }

    internal void SkipBits(int n)
    {
        EnsureBits(n);
        _bitPos += n;
    }

    internal readonly ReadOnlySpan<byte> RemainingBytes()
    {
        int bytePos = (_bitPos + 7) >> 3;
        return bytePos >= _data.Length ? ReadOnlySpan<byte>.Empty : _data[bytePos..];
    }
}
