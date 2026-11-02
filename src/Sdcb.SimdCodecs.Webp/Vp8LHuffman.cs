namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// Encoder side of the VP8L prefix codes (RFC 9649 Section 3.7.2): length-limited Huffman code
/// lengths from symbol counts, canonical codes, and the two ways of writing a code (the "simple"
/// one- or two-symbol form and the "normal" form whose lengths are themselves run-length and
/// prefix coded).
/// </summary>
internal static class Vp8LHuffman
{
    public const int MaxLength = 15;
    private const int MaxCodeLengthCodeLength = 7;

    private static readonly int[] CodeLengthCodeOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    /// <summary>
    /// Huffman code lengths for <paramref name="counts"/>, no longer than <paramref name="limit"/>.
    /// Symbols with a zero count get length 0. When the plain Huffman code is too deep, small counts
    /// are raised to a floor that doubles until it fits, which flattens only the deep end of the tree.
    /// </summary>
    public static void BuildLengths(ReadOnlySpan<uint> counts, Span<byte> lengths, int limit)
    {
        lengths.Clear();
        int used = 0;
        foreach (uint c in counts)
        {
            if (c != 0)
            {
                used++;
            }
        }

        if (used == 0)
        {
            return;
        }

        if (used == 1)
        {
            lengths[counts.IndexOfAnyExcept(0u)] = 1;
            return;
        }

        // Node arrays: leaves first (sorted by count), then internal nodes in creation order. The
        // two-queue construction needs no heap because internal nodes are created in count order.
        var weight = new ulong[2 * used];
        var parent = new int[2 * used];
        var symbolOf = new int[used];
        var keys = new ulong[used];
        for (uint floor = 1; ; floor *= 2)
        {
            // Sorted by (raised) count, then symbol (alphabets are under 4096 symbols), so equal
            // inputs give equal codes.
            int n = 0;
            for (int s = 0; s < counts.Length; s++)
            {
                if (counts[s] != 0)
                {
                    keys[n++] = (ulong)Math.Max(counts[s], floor) << 12 | (uint)s;
                }
            }

            Array.Sort(keys);
            for (int i = 0; i < used; i++)
            {
                symbolOf[i] = (int)(keys[i] & 0xFFF);
                weight[i] = keys[i] >> 12;
            }

            int leaf = 0, inner = used, next = used;
            for (int k = 0; k < used - 1; k++)
            {
                int a = TakeSmallest(weight, ref leaf, used, ref inner, next);
                int b = TakeSmallest(weight, ref leaf, used, ref inner, next);
                weight[next] = weight[a] + weight[b];
                parent[a] = next;
                parent[b] = next;
                next++;
            }

            // Depths from the root (the last node) down.
            var depth = new int[next];
            int deepest = 0;
            for (int node = next - 2; node >= 0; node--)
            {
                depth[node] = depth[parent[node]] + 1;
                if (node < used)
                {
                    deepest = Math.Max(deepest, depth[node]);
                }
            }

            if (deepest <= limit)
            {
                for (int i = 0; i < used; i++)
                {
                    lengths[symbolOf[i]] = (byte)depth[i];
                }

                return;
            }
        }
    }

    private static int TakeSmallest(ulong[] weight, ref int leaf, int leafEnd, ref int inner, int innerEnd)
    {
        if (leaf < leafEnd && (inner >= innerEnd || weight[leaf] <= weight[inner]))
        {
            return leaf++;
        }

        return inner++;
    }

    /// <summary>Canonical codes for <paramref name="lengths"/>, bit-reversed for the LSB-first writer.</summary>
    public static void BuildCodes(ReadOnlySpan<byte> lengths, Span<ushort> codes)
    {
        Span<int> count = stackalloc int[MaxLength + 1];
        Span<int> next = stackalloc int[MaxLength + 1];
        count.Clear();
        foreach (byte l in lengths)
        {
            count[l]++;
        }

        count[0] = 0;
        int code = 0;
        for (int l = 1; l <= MaxLength; l++)
        {
            code = (code + count[l - 1]) << 1;
            next[l] = code;
        }

        for (int s = 0; s < lengths.Length; s++)
        {
            int l = lengths[s];
            codes[s] = l == 0 ? (ushort)0 : (ushort)Reverse(next[l]++, l);
        }
    }

    private static int Reverse(int value, int bits)
    {
        int r = 0;
        for (int i = 0; i < bits; i++)
        {
            r = (r << 1) | ((value >> i) & 1);
        }

        return r;
    }

    /// <summary>
    /// Builds the code for <paramref name="counts"/> and writes its description. On return
    /// <paramref name="lengths"/> / <paramref name="codes"/> are what each symbol must be written
    /// with: a code that a decoder reads as a single symbol takes no bits at all.
    /// </summary>
    public static void WriteCode(Vp8LBitWriter writer, ReadOnlySpan<uint> counts, Span<byte> lengths, Span<ushort> codes)
    {
        int alphabet = counts.Length;
        int first = -1, second = -1, used = 0;
        for (int s = 0; s < alphabet; s++)
        {
            if (counts[s] != 0)
            {
                if (used == 0) first = s;
                else if (used == 1) second = s;
                used++;
            }
        }

        lengths.Clear();
        codes.Clear();
        if (used == 0 || (used == 1 && first < 256))
        {
            // Simple code, one symbol: it is read with zero bits.
            int symbol = Math.Max(first, 0);
            writer.WriteBits(1, 1);
            writer.WriteBits(0, 1);
            if (symbol < 2)
            {
                writer.WriteBits(0, 1);
                writer.WriteBits((uint)symbol, 1);
            }
            else
            {
                writer.WriteBits(1, 1);
                writer.WriteBits((uint)symbol, 8);
            }

            return;
        }

        if (used == 2 && second < 256)
        {
            // Simple code, two symbols of one bit each.
            writer.WriteBits(1, 1);
            writer.WriteBits(1, 1);
            if (first < 2)
            {
                writer.WriteBits(0, 1);
                writer.WriteBits((uint)first, 1);
            }
            else
            {
                writer.WriteBits(1, 1);
                writer.WriteBits((uint)first, 8);
            }

            writer.WriteBits((uint)second, 8);
            lengths[first] = 1;
            lengths[second] = 1;
            BuildCodes(lengths, codes);
            return;
        }

        if (used == 1)
        {
            // One symbol the simple form cannot name (>= 256): give it a one-bit code next to a
            // never-used partner, so the code is complete for every decoder.
            lengths[first] = 1;
            lengths[first == 0 ? 1 : 0] = 1;
        }
        else
        {
            BuildLengths(counts, lengths, MaxLength);
        }

        BuildCodes(lengths, codes);
        WriteNormal(writer, lengths);
    }

    private static void WriteNormal(Vp8LBitWriter writer, ReadOnlySpan<byte> lengths)
    {
        int end = lengths.Length;
        while (lengths[end - 1] == 0)
        {
            end--;
        }

        Span<byte> tokens = stackalloc byte[lengths.Length];
        Span<byte> extras = stackalloc byte[lengths.Length];
        int tokenCount = Tokenize(lengths[..end], tokens, extras);

        Span<uint> tokenCounts = stackalloc uint[19];
        tokenCounts.Clear();
        for (int i = 0; i < tokenCount; i++)
        {
            tokenCounts[tokens[i]]++;
        }

        Span<byte> clLengths = stackalloc byte[19];
        Span<ushort> clCodes = stackalloc ushort[19];
        int distinct = 0, only = 0;
        for (int i = 0; i < 19; i++)
        {
            if (tokenCounts[i] != 0)
            {
                distinct++;
                only = i;
            }
        }

        if (distinct == 1)
        {
            // A one-symbol code-length code; pair it as above.
            clLengths.Clear();
            clLengths[only] = 1;
            clLengths[only == 0 ? 1 : 0] = 1;
        }
        else
        {
            BuildLengths(tokenCounts, clLengths, MaxCodeLengthCodeLength);
        }

        BuildCodes(clLengths, clCodes);
        int written = 19;
        while (written > 4 && clLengths[CodeLengthCodeOrder[written - 1]] == 0)
        {
            written--;
        }

        writer.WriteBits(0, 1); // normal code
        writer.WriteBits((uint)(written - 4), 4);
        for (int i = 0; i < written; i++)
        {
            writer.WriteBits(clLengths[CodeLengthCodeOrder[i]], 3);
        }

        if (end == lengths.Length)
        {
            writer.WriteBits(0, 1);
        }
        else
        {
            // max_symbol: the number of tokens to read; the lengths after them are zero.
            int maxSymbol = tokenCount;
            int nbits = 2;
            while (maxSymbol - 2 >= 1 << nbits)
            {
                nbits += 2;
            }

            writer.WriteBits(1, 1);
            writer.WriteBits((uint)((nbits - 2) / 2), 3);
            writer.WriteBits((uint)(maxSymbol - 2), nbits);
        }

        for (int i = 0; i < tokenCount; i++)
        {
            int t = tokens[i];
            writer.WriteBits(clCodes[t], clLengths[t]);
            switch (t)
            {
                case 16: writer.WriteBits(extras[i], 2); break;
                case 17: writer.WriteBits(extras[i], 3); break;
                case 18: writer.WriteBits(extras[i], 7); break;
            }
        }
    }

    /// <summary>Run-length tokens of the code-length alphabet: 0..15 literal, 16 repeat previous, 17/18 zeros.</summary>
    private static int Tokenize(ReadOnlySpan<byte> lengths, Span<byte> tokens, Span<byte> extras)
    {
        int n = 0, i = 0, previous = 8;
        while (i < lengths.Length)
        {
            int value = lengths[i];
            int run = 1;
            while (i + run < lengths.Length && lengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run > 0)
                {
                    if (run >= 11)
                    {
                        int r = Math.Min(run, 138);
                        tokens[n] = 18; extras[n++] = (byte)(r - 11); run -= r;
                    }
                    else if (run >= 3)
                    {
                        int r = Math.Min(run, 10);
                        tokens[n] = 17; extras[n++] = (byte)(r - 3); run -= r;
                    }
                    else
                    {
                        tokens[n] = 0; extras[n++] = 0; run--;
                    }
                }

                continue;
            }

            if (value != previous)
            {
                tokens[n] = (byte)value; extras[n++] = 0;
                previous = value;
                run--;
            }

            while (run > 0)
            {
                if (run >= 3)
                {
                    int r = Math.Min(run, 6);
                    tokens[n] = 16; extras[n++] = (byte)(r - 3); run -= r;
                }
                else
                {
                    tokens[n] = (byte)value; extras[n++] = 0; run--;
                }
            }
        }

        return n;
    }
}
