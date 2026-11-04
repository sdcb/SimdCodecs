using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// A bit reader over Huffman-coded JBIG2 data, most significant bit first
/// (T.88 §6.2.2, §7.4.13). Unlike <see cref="MqDecoder"/>, which T.88 lets read
/// past its data as 0xFF, a Huffman stream has no such convention, so running out
/// is an error rather than an invented value.
/// </summary>
internal ref struct Jbig2BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private long _bitPosition;

    public Jbig2BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    /// <summary>The data this reader reads from.</summary>
    public readonly ReadOnlySpan<byte> Data => _data;

    /// <summary>The byte holding the next unread bit, or the next byte once aligned.</summary>
    public readonly int BytePosition => (int)((_bitPosition + 7) >> 3);

    public int ReadBit()
    {
        var index = _bitPosition >> 3;
        if (index >= _data.Length)
            throw new InvalidDataException("JBIG2: Huffman-coded data ends early.");

        var bit = (_data[(int)index] >> (7 - (int)(_bitPosition & 7))) & 1;
        _bitPosition++;
        return bit;
    }

    /// <summary>Reads <paramref name="count"/> (0 to 32) bits as an unsigned value.</summary>
    public uint ReadBits(int count)
    {
        if ((uint)count > 32) throw new ArgumentOutOfRangeException(nameof(count));
        if (_bitPosition + count > (long)_data.Length * 8)
            throw new InvalidDataException("JBIG2: Huffman-coded data ends early.");

        uint value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | (uint)ReadBit();
        return value;
    }

    /// <summary>Skips to the next byte boundary (a no-op when already on one).</summary>
    public void AlignToByte() => _bitPosition = (_bitPosition + 7) & ~7L;

    /// <summary>Moves to byte <paramref name="position"/>, which must lie within the data.</summary>
    public void SeekToByte(long position)
    {
        if (position < 0 || position > _data.Length)
            throw new InvalidDataException("JBIG2: Huffman-coded data ends early.");
        _bitPosition = position * 8;
    }
}

/// <summary>
/// A Huffman table in the sense of T.88 Annex B: a list of lines, each a prefix
/// code length (PREFLEN), a range length (RANGELEN) and the bottom of its range
/// (RANGELOW), plus optionally a lower-range line (values from RANGELOW down to
/// minus infinity), an upper-range line (RANGELOW up) and an out-of-band line.
/// Prefix codes are assigned from the lengths alone by the canonical procedure of
/// B.3, in line order, so a table is fully described by its lines.
/// </summary>
internal sealed class HuffmanTable
{
    /// <summary>What a line's value means.</summary>
    internal enum LineKind : byte
    {
        Normal,
        Lower,
        Upper,
        OutOfBand,
    }

    /// <summary>One table line (T.88 B.2).</summary>
    internal readonly record struct Line(int PrefixLength, int RangeLength, int RangeLow, LineKind Kind = LineKind.Normal);

    /// <summary>The value <see cref="Decode"/> returns for an OOB line; the same sentinel as the arithmetic decoder's.</summary>
    public const int OutOfBand = ArithIntDecoder.OutOfBand;

    /// <summary>
    /// Prefix lengths beyond this are refused. B.2 allows PREFLEN up to 255 in a
    /// custom table, but a complete code over 2^32 values never needs more than 32.
    /// </summary>
    private const int MaxPrefixLength = 32;

    // Canonical decoding tables: for each prefix length, the first code of that
    // length and the lines that have it, in assignment order.
    private readonly long[] _firstCode;
    private readonly Line[][] _byLength;

    /// <summary>Builds a table from its lines (B.3).</summary>
    /// <exception cref="InvalidDataException">The lengths do not form a prefix code.</exception>
    public HuffmanTable(IReadOnlyList<Line> lines)
    {
        var maxLength = 0;
        foreach (var line in lines)
        {
            if (line.PrefixLength is < 0 or > MaxPrefixLength)
                throw new InvalidDataException($"JBIG2: Huffman table line has prefix length {line.PrefixLength}.");
            if (line.RangeLength is < 0 or > 32)
                throw new InvalidDataException($"JBIG2: Huffman table line has range length {line.RangeLength}.");
            maxLength = Math.Max(maxLength, line.PrefixLength);
        }

        var counts = new int[maxLength + 1];
        foreach (var line in lines)
            if (line.PrefixLength > 0) counts[line.PrefixLength]++;

        _firstCode = new long[maxLength + 1];
        _byLength = new Line[maxLength + 1][];
        var lists = new List<Line>[maxLength + 1];
        for (var l = 1; l <= maxLength; l++) lists[l] = new List<Line>(counts[l]);
        foreach (var line in lines)
            if (line.PrefixLength > 0) lists[line.PrefixLength].Add(line);

        // B.3: FIRSTCODE[len] = (FIRSTCODE[len - 1] + LENCOUNT[len - 1]) * 2, with
        // LENCOUNT[0] taken as 0. A set of lengths that over-subscribes the code
        // space (Kraft sum above 1) would hand out a code wider than its length,
        // which is not a prefix code; that is malformed data, not a table.
        long first = 0;
        var previousCount = 0;
        for (var l = 1; l <= maxLength; l++)
        {
            first = (first + previousCount) << 1;
            if (first + counts[l] > 1L << l)
                throw new InvalidDataException("JBIG2: Huffman table prefix lengths do not form a prefix code.");
            _firstCode[l] = first;
            _byLength[l] = [.. lists[l]];
            previousCount = counts[l];
        }

        _byLength[0] = [];
    }

    /// <summary>Decodes one value (B.4), or <see cref="OutOfBand"/>.</summary>
    public int Decode(ref Jbig2BitReader reader)
    {
        long code = 0;
        for (var length = 1; length < _firstCode.Length; length++)
        {
            code = (code << 1) | (uint)reader.ReadBit();
            var index = code - _firstCode[length];
            var group = _byLength[length];
            if (index < 0 || index >= group.Length) continue;

            var line = group[index];
            switch (line.Kind)
            {
                case LineKind.OutOfBand:
                    return OutOfBand;
                case LineKind.Lower:
                    return Checked(line.RangeLow - (long)reader.ReadBits(line.RangeLength));
                case LineKind.Upper:
                    return Checked(line.RangeLow + (long)reader.ReadBits(line.RangeLength));
                default:
                    return Checked(line.RangeLow + (long)reader.ReadBits(line.RangeLength));
            }
        }

        throw new InvalidDataException("JBIG2: Huffman code is not in the table.");
    }

    private static int Checked(long value) => value is >= int.MinValue + 1 and <= int.MaxValue
        ? (int)value
        : throw new InvalidDataException("JBIG2: Huffman-decoded value overflows a 32-bit integer.");

    /// <summary>
    /// A table whose lines all have range length zero and values 0, 1, 2, ... in
    /// order: the shape of the symbol ID tables of §7.4.3.1.7 and of the run code
    /// table that precedes them.
    /// </summary>
    public static HuffmanTable FromCodeLengths(ReadOnlySpan<int> lengths)
    {
        var lines = new Line[lengths.Length];
        for (var i = 0; i < lengths.Length; i++) lines[i] = new Line(lengths[i], 0, i);
        return new HuffmanTable(lines);
    }

    /// <summary>
    /// Reads a table segment's data part (T.88 §7.4.13, B.2): flags, HTLOW,
    /// HTHIGH, then the lines packed at HTPS / HTRS bits each.
    /// </summary>
    public static HuffmanTable ReadSegment(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) throw new InvalidDataException("JBIG2: truncated Huffman table segment.");

        var flags = data[0];
        var hasOob = (flags & 0x01) != 0;
        var prefixBits = ((flags >> 1) & 0x07) + 1;
        var rangeBits = ((flags >> 4) & 0x07) + 1;
        var low = BinaryPrimitives.ReadInt32BigEndian(data[1..]);
        var high = BinaryPrimitives.ReadInt32BigEndian(data[5..]);
        if (low >= high)
            throw new InvalidDataException($"JBIG2: Huffman table segment has HTLOW {low} not below HTHIGH {high}.");

        var reader = new Jbig2BitReader(data[9..]);
        var lines = new List<Line>();

        // Each line costs at least prefixBits + rangeBits >= 2 bits of the segment,
        // and ReadBits refuses to run past it, so the loop is bounded by the data.
        long current = low;
        while (current < high)
        {
            var prefix = (int)reader.ReadBits(prefixBits);
            var range = (int)reader.ReadBits(rangeBits);
            if (range > 32)
                throw new InvalidDataException($"JBIG2: Huffman table segment line has range length {range}.");
            lines.Add(new Line(prefix, range, (int)current));
            current += 1L << range;
        }

        lines.Add(new Line((int)reader.ReadBits(prefixBits), 32, low - 1, LineKind.Lower));
        lines.Add(new Line((int)reader.ReadBits(prefixBits), 32, high, LineKind.Upper));
        if (hasOob) lines.Add(new Line((int)reader.ReadBits(prefixBits), 0, 0, LineKind.OutOfBand));

        return new HuffmanTable(lines);
    }

    // ---- the standard tables of Annex B.5 (Tables B.1 to B.15) --------------------

    private static readonly HuffmanTable?[] Standard = new HuffmanTable?[16];

    /// <summary>Standard table B.<paramref name="number"/>, 1 to 15.</summary>
    public static HuffmanTable Get(int number)
    {
        lock (Standard)
            return Standard[number] ??= new HuffmanTable(StandardLines(number));
    }

    /// <summary>The lines of standard table B.<paramref name="number"/>, in the order the table lists them.</summary>
    internal static Line[] StandardLines(int number) => number switch
    {
        1 => [N(1, 4, 0), N(2, 8, 16), N(3, 16, 272), U(3, 65808)],
        2 => [N(1, 0, 0), N(2, 0, 1), N(3, 0, 2), N(4, 3, 3), N(5, 6, 11), U(6, 75), O(6)],
        3 => [N(8, 8, -256), N(1, 0, 0), N(2, 0, 1), N(3, 0, 2), N(4, 3, 3), N(5, 6, 11), L(8, -257), U(7, 75), O(6)],
        4 => [N(1, 0, 1), N(2, 0, 2), N(3, 0, 3), N(4, 3, 4), N(5, 6, 12), U(5, 76)],
        5 => [N(7, 8, -255), N(1, 0, 1), N(2, 0, 2), N(3, 0, 3), N(4, 3, 4), N(5, 6, 12), L(7, -256), U(6, 76)],
        6 =>
        [
            N(5, 10, -2048), N(4, 9, -1024), N(4, 8, -512), N(4, 7, -256), N(5, 6, -128), N(5, 5, -64), N(4, 5, -32),
            N(2, 7, 0), N(3, 7, 128), N(3, 8, 256), N(4, 9, 512), N(4, 10, 1024), L(6, -2049), U(6, 2048),
        ],
        7 =>
        [
            N(4, 9, -1024), N(3, 8, -512), N(4, 7, -256), N(5, 6, -128), N(5, 5, -64), N(4, 5, -32), N(4, 5, 0),
            N(5, 5, 32), N(5, 6, 64), N(4, 7, 128), N(3, 8, 256), N(3, 9, 512), N(4, 10, 1024), L(5, -1025), U(5, 2048),
        ],
        8 =>
        [
            N(8, 3, -15), N(9, 1, -7), N(8, 1, -5), N(9, 0, -3), N(7, 0, -2), N(4, 0, -1), N(2, 1, 0), N(5, 0, 2),
            N(6, 0, 3), N(3, 4, 4), N(6, 1, 20), N(4, 4, 22), N(4, 5, 38), N(5, 6, 70), N(5, 7, 134), N(6, 7, 262),
            N(7, 8, 390), N(6, 10, 646), L(9, -16), U(9, 1670), O(2),
        ],
        9 =>
        [
            N(8, 4, -31), N(9, 2, -15), N(8, 2, -11), N(9, 1, -7), N(7, 1, -5), N(4, 1, -3), N(3, 1, -1), N(3, 1, 1),
            N(5, 1, 3), N(6, 1, 5), N(3, 5, 7), N(6, 2, 39), N(4, 5, 43), N(4, 6, 75), N(5, 7, 139), N(5, 8, 267),
            N(6, 8, 523), N(7, 9, 779), N(6, 11, 1291), L(9, -32), U(9, 3339), O(2),
        ],
        10 =>
        [
            N(7, 4, -21), N(8, 0, -5), N(7, 0, -4), N(5, 0, -3), N(2, 2, -2), N(5, 0, 2), N(6, 0, 3), N(7, 0, 4),
            N(8, 0, 5), N(2, 6, 6), N(5, 5, 70), N(6, 5, 102), N(6, 6, 134), N(6, 7, 198), N(6, 8, 326),
            N(6, 9, 582), N(6, 10, 1094), N(7, 11, 2118), L(8, -22), U(8, 4166), O(2),
        ],
        11 =>
        [
            N(1, 0, 1), N(2, 1, 2), N(4, 0, 4), N(4, 1, 5), N(5, 1, 7), N(5, 2, 9), N(6, 2, 13), N(7, 2, 17),
            N(7, 3, 21), N(7, 4, 29), N(7, 5, 45), N(7, 6, 77), U(7, 141),
        ],
        12 =>
        [
            N(1, 0, 1), N(2, 0, 2), N(3, 1, 3), N(5, 0, 5), N(5, 1, 6), N(6, 1, 8), N(7, 0, 10), N(7, 1, 11),
            N(7, 2, 13), N(7, 3, 17), N(7, 4, 25), N(8, 5, 41), U(8, 73),
        ],
        13 =>
        [
            N(1, 0, 1), N(3, 0, 2), N(4, 0, 3), N(5, 0, 4), N(4, 1, 5), N(3, 3, 7), N(6, 1, 15), N(6, 2, 17),
            N(6, 3, 21), N(6, 4, 29), N(6, 5, 45), N(7, 6, 77), U(7, 141),
        ],
        14 => [N(3, 0, -2), N(3, 0, -1), N(1, 0, 0), N(3, 0, 1), N(3, 0, 2)],
        15 =>
        [
            N(7, 4, -24), N(6, 2, -8), N(5, 1, -4), N(4, 0, -2), N(3, 0, -1), N(1, 0, 0), N(3, 0, 1), N(4, 0, 2),
            N(5, 1, 3), N(6, 2, 5), N(7, 4, 9), L(7, -25), U(7, 25),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(number), number, "Standard tables are B.1 to B.15."),
    };

    private static Line N(int prefix, int range, int low) => new(prefix, range, low);
    private static Line L(int prefix, int low) => new(prefix, 32, low, LineKind.Lower);
    private static Line U(int prefix, int low) => new(prefix, 32, low, LineKind.Upper);
    private static Line O(int prefix) => new(prefix, 0, 0, LineKind.OutOfBand);
}
