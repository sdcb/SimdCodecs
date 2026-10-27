using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

    /// <summary>
    /// Per symbol s, with e = s &amp; 15 its count of extra bits (the size category for DC, the
    /// low nibble for AC): <c>((code &lt;&lt; e) &lt;&lt; 5) | (length + e)</c>, so a symbol and its
    /// extra bits go out as one <c>Put((entry &gt;&gt; 5) | extra, entry &amp; 31)</c>. At most
    /// 16 + 11 bits, so the shifted code fits the 27 high bits.
    /// </summary>
    public readonly uint[] Packed = new uint[256];

    private JpegHuffmanEncodeTable(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        bits.CopyTo(Bits);
        Values = values.ToArray();

        // Annex C.1 / C.2: code lengths in HUFFVAL order, canonical codes counting up.
        int code = 0, k = 0;
        for (var len = 1; len <= 16; len++)
        {
            for (var i = 0; i < Bits[len - 1]; i++, k++, code++)
            {
                var s = Values[k];
                Codes[s] = ((uint)code << 8) | (uint)len;
                var e = s & 15;
                Packed[s] = (((uint)code << e) << 5) | (uint)(len + e);
            }

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
/// Whole 32-bit words are flushed without branching on the data. The 0xFF → 0xFF 0x00 stuffing
/// of T.81 F.1.2.3 is deferred: <see cref="Stuff"/> (after every block, and before any marker)
/// scans the bytes written since the last call and expands them in place when one is 0xFF,
/// which keeps all stuffing work out of the per-symbol path.
/// </summary>
internal struct JpegBitWriter
{
    /// <summary>
    /// Room guaranteed before one block is entropy coded. A block emits at most 1 DC symbol
    /// (≤ 16 + 11 bits), 63 AC symbols (≤ 16 + 10 bits each), 3 ZRL and one EOB (≤ 16 bits each):
    /// under 1800 bits (225 bytes), plus the 8-byte speculative store, and at most twice that
    /// after stuffing: under 470 bytes.
    /// </summary>
    public const int BlockReserve = 1024;

    public byte[] Buffer;
    public int Position;
    public ulong Acc;
    public int Bits;

    /// <summary>Start of the bytes not yet checked for stuffing.</summary>
    public int Mark;

    public JpegBitWriter(int capacity)
    {
        Buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, capacity));
        Position = 0;
        Acc = 0;
        Bits = 0;
        Mark = 0;
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
    /// <remarks>
    /// Unchecked: the caller has reserved room (<see cref="BlockReserve"/> per block, or
    /// <see cref="PutChecked"/>) and calls <see cref="Stuff"/> before the bytes are final.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(uint value, int n)
    {
        // Branch-free, byte-granular flush: the pending bits (under 8 before, at most 8 + 32 - 1
        // after) are stored left-aligned as 8 big-endian bytes at Position on every call, and
        // Position advances by the whole bytes among them. No branch depends on the data, and
        // the loop-carried state is a 2-instruction chain.
        var acc = (Acc << n) | value;
        var bits = Bits + n;
        var aligned = acc << (64 - bits);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Buffer), Position),
            BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(aligned) : aligned);
        Position += bits >> 3;
        Bits = bits & 7;
        Acc = acc;
    }

    public void PutChecked(uint value, int n)
    {
        Ensure(32);
        Put(value, n);
        Stuff();
    }

    /// <summary>Inserts the stuffing zero after every 0xFF written since the last call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Stuff()
    {
        if (HasMarkerByte(Buffer, Mark, Position - Mark))
            Position = StuffRange(Buffer, Mark, Position);
        Mark = Position;
    }

    /// <summary>
    /// Whether <c>buffer[start .. start + length)</c> holds a 0xFF. Reads whole 16-byte vectors,
    /// up to 15 bytes past the range, which every caller has reserved (a block's
    /// <see cref="BlockReserve"/>, or <see cref="PutChecked"/>'s 32 bytes).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasMarkerByte(byte[] buffer, int start, int length)
    {
        if (!Vector128.IsHardwareAccelerated) return buffer.AsSpan(start, length).Contains((byte)0xFF);
        if (length <= 0) return false;
        _ = buffer[start + length + 15];
        ref var p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), start);
        var ff = Vector128.Create((byte)0xFF);
        uint found = 0;
        var i = 0;
        for (; i + 16 <= length; i += 16)
            found |= Vector128.Equals(Vector128.LoadUnsafe(ref p, (nuint)i), ff).ExtractMostSignificantBits();
        if (i < length)
            found |= Vector128.Equals(Vector128.LoadUnsafe(ref p, (nuint)i), ff).ExtractMostSignificantBits() & ((1u << (length - i)) - 1);
        return found != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int StuffRange(byte[] buffer, int start, int end)
    {
        var span = buffer.AsSpan(start, end - start);
        var count = span.Count((byte)0xFF);
        var total = count;
        // Expand from the back so every byte moves at most once (the reserve leaves room for it).
        var dst = end + count;
        for (var i = end - 1; i >= start && count > 0; i--)
        {
            var b = buffer[i];
            if (b == 0xFF)
            {
                buffer[--dst] = 0;
                count--;
            }

            buffer[--dst] = b;
        }

        return end + total;
    }

    /// <summary>Pads the entropy-coded segment to a byte boundary with 1-bits (T.81 F.1.2.3) and flushes it.</summary>
    public void FlushBits()
    {
        Ensure(32);
        Stuff();
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
        Mark = Position;
    }

    private void PutByteStuffed(byte b)
    {
        Buffer[Position++] = b;
        if (b == 0xFF) Buffer[Position++] = 0;
    }

    /// <summary>Raw (marker-segment) bytes; only valid on a byte boundary, after <see cref="FlushBits"/>.</summary>
    public void WriteByte(byte b)
    {
        Ensure(1);
        Buffer[Position++] = b;
        Mark = Position;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(Buffer.AsSpan(Position));
        Position += bytes.Length;
        Mark = Position;
    }

    public void WriteUInt16(int v)
    {
        Ensure(2);
        Buffer[Position++] = (byte)(v >> 8);
        Buffer[Position++] = (byte)v;
        Mark = Position;
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