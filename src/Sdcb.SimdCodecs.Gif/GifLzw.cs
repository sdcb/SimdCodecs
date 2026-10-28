using System.Buffers;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>
/// The variable-length-code LZW of GIF89a Appendix F: codes packed least-significant bit first, starting at
/// <c>minimum code size + 1</c> bits and growing to at most 12, with a clear code (<c>2^min</c>) that resets
/// the table and an end-of-information code (<c>2^min + 1</c>).
/// <para>
/// Decoding is lenient, as browsers are: it stops at the end-of-information code, at the end of the data,
/// at a code that is not yet defined, or once the output is full, and reports how many pixels it wrote. A
/// full table (4096 codes) without a clear code keeps decoding with the table frozen ("deferred clear").
/// </para>
/// </summary>
internal static class GifLzw
{
    private const int MaxCodes = 4096;

    /// <summary>Gathers an image's data sub-blocks, from <paramref name="offset"/>, into one contiguous code stream.</summary>
    /// <returns>A buffer rented from <see cref="ArrayPool{T}.Shared"/>; the caller returns it.</returns>
    public static byte[] GatherSubBlocks(ReadOnlySpan<byte> data, int offset, out int length)
    {
        var total = 0;
        for (var pos = offset; pos < data.Length;)
        {
            int size = data[pos];
            if (size == 0) break;
            total += Math.Min(size, data.Length - pos - 1);
            pos += 1 + size;
        }

        var codes = ArrayPool<byte>.Shared.Rent(Math.Max(total, 1));
        length = 0;
        for (var pos = offset; pos < data.Length;)
        {
            int size = data[pos];
            if (size == 0) break;
            var take = Math.Min(size, data.Length - pos - 1);
            data.Slice(pos + 1, take).CopyTo(codes.AsSpan(length));
            length += take;
            pos += 1 + size;
        }
        return codes;
    }

    /// <summary>Decodes <paramref name="codes"/> into palette indices; returns how many of <paramref name="output"/> it filled.</summary>
    /// <exception cref="InvalidDataException">The minimum code size is outside 1..11, which no table of 12-bit codes can hold.</exception>
    public static int Decode(ReadOnlySpan<byte> codes, int minCodeSize, Span<byte> output)
    {
        if (minCodeSize is < 1 or > 11)
            throw new InvalidDataException($"GIF LZW minimum code size {minCodeSize} is outside 1..11.");

        var table = Table.Rent();
        var prefix = table.Prefix;
        var suffix = table.Suffix;
        var first = table.First;
        var length = table.Length;

        int clear = 1 << minCodeSize, end = clear + 1;
        for (var c = 0; c < clear; c++)
        {
            prefix[c] = 0;
            suffix[c] = (byte)c;
            first[c] = (byte)c;
            length[c] = 1;
        }

        var codeSize = minCodeSize + 1;
        var next = clear + 2;
        var previous = -1;
        var written = 0;
        ulong bits = 0;
        var bitCount = 0;
        var position = 0;

        while (written < output.Length)
        {
            while (bitCount < codeSize)
            {
                if (position == codes.Length) return written;
                bits |= (ulong)codes[position++] << bitCount;
                bitCount += 8;
            }
            var code = (int)(bits & ((1u << codeSize) - 1));
            bits >>= codeSize;
            bitCount -= codeSize;

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = clear + 2;
                previous = -1;
                continue;
            }
            if (code == end) return written;

            if (previous < 0)
            {
                // The first code after a clear (or of the stream) is a single pixel.
                if (code > clear) return written;
            }
            else if (next < MaxCodes)
            {
                // The new entry is the previous string plus the first pixel of this one; for a code not yet
                // in the table (the "KwKwK" case, code == next) that first pixel is the previous string's own.
                if (code > next) return written;
                prefix[next] = (ushort)previous;
                first[next] = first[previous];
                suffix[next] = code == next ? first[previous] : first[code];
                length[next] = (ushort)(length[previous] + 1);
                next++;
                if (next == 1 << codeSize && codeSize < 12) codeSize++;
            }
            else if (code >= next)
            {
                return written; // a frozen table has no code to define
            }

            // Emit the string backwards from its last pixel; only what fits the output is written.
            int count = length[code], at = written + count - 1, k = code;
            for (var i = 0; i < count; i++, at--)
            {
                if (at < output.Length) output[at] = suffix[k];
                k = prefix[k];
            }
            written = Math.Min(written + count, output.Length);
            previous = code;
        }
        return written;
    }

    /// <summary>The 4096-entry string table, cached per thread so a frame allocates nothing for it.</summary>
    private sealed class Table
    {
        public readonly ushort[] Prefix = new ushort[MaxCodes];
        public readonly byte[] Suffix = new byte[MaxCodes];
        public readonly byte[] First = new byte[MaxCodes];
        public readonly ushort[] Length = new ushort[MaxCodes];

        [ThreadStatic]
        private static Table? t_cached;

        // Every entry a decode reads is written first (literals at the start, the rest as they are
        // defined), so a table needs no reset between uses.
        public static Table Rent() => t_cached ??= new Table();
    }
}
