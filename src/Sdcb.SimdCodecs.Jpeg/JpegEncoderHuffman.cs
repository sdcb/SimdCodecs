using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// One Huffman table for encoding: the BITS / HUFFVAL lists that go into a DHT segment and
/// the derived code of every symbol (T.81 Annex C), packed as <c>(code &lt;&lt; 8) | length</c>
/// (0 for a symbol the table does not contain).
/// </summary>
internal sealed class JpegHuffmanEncodeTable
{
    public readonly byte[] Bits = new byte[16];
    public readonly byte[] Values;
    public readonly uint[] Codes = new uint[256];

    private JpegHuffmanEncodeTable(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        bits.CopyTo(Bits);
        Values = values.ToArray();

        // Annex C.1 / C.2: code lengths in HUFFVAL order, canonical codes counting up.
        int code = 0, k = 0;
        for (var len = 1; len <= 16; len++)
        {
            for (var i = 0; i < Bits[len - 1]; i++, k++, code++)
                Codes[Values[k]] = ((uint)code << 8) | (uint)len;
            code <<= 1;
        }
    }

    public static JpegHuffmanEncodeTable FromSpec(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values) => new(bits, values);

    /// <summary>
    /// Builds an optimal length-limited table for the symbol frequencies <paramref name="freq"/>
    /// (256 entries) by the procedure of T.81 Annex K.2: Huffman code sizes with a reserved
    /// one-occurrence pseudo-symbol (so no code is all ones), lengths over 16 folded back by
    /// Figure K.3, and HUFFVAL sorted by code length then symbol value.
    /// </summary>
    public static JpegHuffmanEncodeTable FromFrequencies(ReadOnlySpan<long> freq)
    {
        const int n = 257;
        Span<long> f = stackalloc long[n];
        Span<int> codeSize = stackalloc int[n];
        Span<int> others = stackalloc int[n];
        freq[..256].CopyTo(f);
        f[256] = 1;
        codeSize.Clear();
        others.Fill(-1);

        while (true)
        {
            // V1: least non-zero frequency; V2: the next least. Ties go to the larger symbol,
            // which keeps the reserved symbol 256 among the longest codes.
            int v1 = -1, v2 = -1;
            for (var i = 0; i < n; i++)
            {
                if (f[i] == 0) continue;
                if (v1 < 0 || f[i] <= f[v1]) { v2 = v1; v1 = i; }
                else if (v2 < 0 || f[i] <= f[v2]) v2 = i;
            }

            if (v2 < 0) break;

            f[v1] += f[v2];
            f[v2] = 0;
            codeSize[v1]++;
            while (others[v1] >= 0) { v1 = others[v1]; codeSize[v1]++; }
            others[v1] = v2;
            codeSize[v2]++;
            while (others[v2] >= 0) { v2 = others[v2]; codeSize[v2]++; }
        }

        // Figure K.2 / K.3: count lengths, then fold everything longer than 16 bits.
        Span<int> bits = stackalloc int[n + 1];
        bits.Clear();
        var maxLen = 0;
        for (var i = 0; i < n; i++)
        {
            if (codeSize[i] == 0) continue;
            bits[codeSize[i]]++;
            if (codeSize[i] > maxLen) maxLen = codeSize[i];
        }

        for (var i = maxLen; i > 16;)
        {
            if (bits[i] > 0)
            {
                var j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
            else
            {
                i--;
            }
        }

        // Drop the reserved pseudo-symbol's code (it has the longest length).
        var last = 16;
        while (bits[last] == 0) last--;
        bits[last]--;

        Span<byte> outBits = stackalloc byte[16];
        for (var i = 0; i < 16; i++) outBits[i] = (byte)bits[i + 1];

        // Figure K.4: HUFFVAL by code size, then symbol order (symbol 256 excluded).
        Span<byte> values = stackalloc byte[256];
        var count = 0;
        for (var len = 1; len <= maxLen; len++)
        {
            for (var s = 0; s < 256; s++)
            {
                if (codeSize[s] == len) values[count++] = (byte)s;
            }
        }

        return new JpegHuffmanEncodeTable(outBits, values[..count]);
    }
}

/// <summary>
/// The entropy-coded output: a pooled byte buffer and a 64-bit MSB-first bit accumulator.
/// Whole 32-bit words are flushed at once; the 0xFF → 0xFF 0x00 stuffing of T.81 F.1.2.3 is
/// a slow path taken only when a word actually contains 0xFF.
/// </summary>
internal struct JpegBitWriter
{
    /// <summary>
    /// Room guaranteed before one block is entropy coded. A block emits at most 1 DC symbol
    /// (≤ 16 + 11 bits), 63 AC symbols (≤ 16 + 10 bits each), 3 ZRL and one EOB (≤ 16 bits each):
    /// under 1800 bits, plus up to 31 bits already pending — at most 57 words of 4 bytes, each at
    /// most 8 bytes after stuffing, i.e. 456 bytes.
    /// </summary>
    public const int BlockReserve = 1024;

    public byte[] Buffer;
    public int Position;
    public ulong Acc;
    public int Bits;

    public JpegBitWriter(int capacity)
    {
        Buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, capacity));
        Position = 0;
        Acc = 0;
        Bits = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Ensure(int bytes)
    {
        if (Buffer.Length - Position < bytes) Grow(bytes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int bytes)
    {
        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(Buffer.Length * 2, Position + bytes));
        Buffer.AsSpan(0, Position).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(Buffer);
        Buffer = bigger;
    }

    /// <summary>Appends the low <paramref name="n"/> (≤ 32) bits of <paramref name="value"/>, which must have no higher bits set.</summary>
    /// <remarks>Unchecked: the caller has reserved room (<see cref="BlockReserve"/> per block, or <see cref="PutChecked"/>).</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(uint value, int n)
    {
        Acc = (Acc << n) | value;
        Bits += n;
        if (Bits >= 32)
        {
            Bits -= 32;
            var word = (uint)(Acc >> Bits);
            // No byte of word is 0xFF exactly when no byte of ~word is zero.
            if (((~word - 0x01010101u) & word & 0x80808080u) == 0)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Buffer), Position),
                    BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(word) : word);
                Position += 4;
            }
            else
            {
                Position = PutStuffed(Buffer, Position, word);
            }
        }
    }

    public void PutChecked(uint value, int n)
    {
        Ensure(16);
        Put(value, n);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PutStuffed(byte[] buffer, int position, uint word)
    {
        for (var s = 24; s >= 0; s -= 8)
        {
            var b = (byte)(word >> s);
            buffer[position++] = b;
            if (b == 0xFF) buffer[position++] = 0;
        }

        return position;
    }

    /// <summary>Pads the entropy-coded segment to a byte boundary with 1-bits (T.81 F.1.2.3) and flushes it.</summary>
    public void FlushBits()
    {
        Ensure(16);
        while (Bits >= 8)
        {
            Bits -= 8;
            PutByteStuffed((byte)(Acc >> Bits));
        }

        if (Bits > 0)
        {
            var pad = 8 - Bits;
            PutByteStuffed((byte)((Acc << pad) | ((1u << pad) - 1)));
            Bits = 0;
        }

        Acc = 0;
    }

    private void PutByteStuffed(byte b)
    {
        Buffer[Position++] = b;
        if (b == 0xFF) Buffer[Position++] = 0;
    }

    /// <summary>Raw (marker-segment) bytes; only valid on a byte boundary.</summary>
    public void WriteByte(byte b)
    {
        Ensure(1);
        Buffer[Position++] = b;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(Buffer.AsSpan(Position));
        Position += bytes.Length;
    }

    public void WriteUInt16(int v)
    {
        Ensure(2);
        Buffer[Position++] = (byte)(v >> 8);
        Buffer[Position++] = (byte)v;
    }

    public byte[] ToArrayAndReturn()
    {
        var result = Buffer.AsSpan(0, Position).ToArray();
        ArrayPool<byte>.Shared.Return(Buffer);
        Buffer = [];
        return result;
    }

    /// <summary>Number of bits needed for <paramref name="magnitude"/> (the JPEG size category), 0 for 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Category(uint magnitude) => 32 - BitOperations.LeadingZeroCount(magnitude);
}
