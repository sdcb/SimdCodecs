namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// Reads the fixed-length and Exp-Golomb fields of an HEVC RBSP (H.265 §7.2, §9.2). The input is an RBSP
/// already stripped of emulation-prevention bytes (<see cref="Nal.Unescape"/>). Reading past the end
/// throws <see cref="InvalidDataException"/>, so a truncated parameter set cannot be mistaken for zeros.
/// </summary>
internal ref struct HevcBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _bit;

    internal HevcBitReader(ReadOnlySpan<byte> data, int byteOffset = 0)
    {
        _data = data;
        _bit = (long)byteOffset * 8;
    }

    internal readonly long BitPosition => _bit;

    internal readonly bool ByteAligned => (_bit & 7) == 0;

    internal readonly int BytePosition => (int)((_bit + 7) >> 3);

    internal readonly long BitsLeft => (long)_data.Length * 8 - _bit;

    internal uint ReadBit()
    {
        if (_bit >= (long)_data.Length * 8) throw new InvalidDataException("HEVC parameter set or header is truncated.");
        var value = (uint)(_data[(int)(_bit >> 3)] >> (7 - (int)(_bit & 7))) & 1;
        _bit++;
        return value;
    }

    internal bool ReadFlag() => ReadBit() != 0;

    /// <summary>u(n), 0 ≤ n ≤ 32.</summary>
    internal uint ReadBits(int count)
    {
        uint value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | ReadBit();
        return value;
    }

    internal void Skip(int count)
    {
        if (count < 0 || count > BitsLeft) throw new InvalidDataException("HEVC parameter set or header is truncated.");
        _bit += count;
    }

    /// <summary>ue(v). Values above 2^32 - 2 cannot occur in a conforming stream and are rejected.</summary>
    internal uint ReadUe()
    {
        var leadingZeros = 0;
        while (ReadBit() == 0)
        {
            if (++leadingZeros > 31) throw new InvalidDataException("Exp-Golomb code is too long.");
        }
        if (leadingZeros == 0) return 0;
        return (uint)(((1UL << leadingZeros) - 1) + ReadBits(leadingZeros));
    }

    /// <summary>ue(v) that must not exceed <paramref name="max"/>.</summary>
    internal int ReadUe(int max, string name)
    {
        var value = ReadUe();
        if (value > (uint)max) throw new InvalidDataException($"HEVC {name} = {value} is out of range (max {max}).");
        return (int)value;
    }

    /// <summary>se(v).</summary>
    internal int ReadSe()
    {
        var k = ReadUe();
        // k is at most 2^32 - 2, so the magnitude fits in 31 bits after the halving.
        var magnitude = (long)((k + 1UL) >> 1);
        return (int)((k & 1) != 0 ? magnitude : -magnitude);
    }

    internal int ReadSe(int min, int max, string name)
    {
        var value = ReadSe();
        if (value < min || value > max) throw new InvalidDataException($"HEVC {name} = {value} is out of range [{min}, {max}].");
        return value;
    }

    /// <summary>more_rbsp_data(): true when anything other than the rbsp_trailing_bits remains.</summary>
    internal readonly bool MoreRbspData()
    {
        var totalBits = (long)_data.Length * 8;
        if (_bit >= totalBits) return false;
        // Find the last 1 bit (the rbsp_stop_one_bit); more data exists iff it lies after the current position.
        var last = _data.Length - 1;
        while (last >= 0 && _data[last] == 0) last--;
        if (last < 0) return false;
        var b = _data[last];
        var trailing = System.Numerics.BitOperations.TrailingZeroCount(b);
        var stopBit = (long)last * 8 + (7 - trailing);
        return _bit < stopBit;
    }
}

/// <summary>NAL unit helpers (H.265 §7.3.1).</summary>
internal static class Nal
{
    internal const int VpsNut = 32;
    internal const int SpsNut = 33;
    internal const int PpsNut = 34;

    /// <summary>
    /// Removes emulation_prevention_three_byte from a NAL unit payload (§7.4.2): every 0x000003 becomes
    /// 0x0000. The output is never longer than the input.
    /// </summary>
    internal static int Unescape(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var written = 0;
        var zeros = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }
            destination[written++] = b;
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return written;
    }

    internal static byte[] UnescapeToArray(ReadOnlySpan<byte> source)
    {
        var buffer = new byte[source.Length];
        var length = Unescape(source, buffer);
        return length == buffer.Length ? buffer : buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>nal_unit_type of a NAL unit whose two-byte header starts at <paramref name="nal"/>[0].</summary>
    internal static int Type(ReadOnlySpan<byte> nal) => (nal[0] >> 1) & 0x3F;
}
