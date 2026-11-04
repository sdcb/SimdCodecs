using System;
using System.Buffers.Binary;
using System.IO;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// Segment type codes from ITU-T T.88 §7.3. Only the values this decoder can
/// name are listed; anything else is skipped by data length, which is what §7.2.3
/// requires of a conforming decoder.
/// </summary>
internal enum SegmentType
{
    SymbolDictionary = 0,
    IntermediateTextRegion = 4,
    ImmediateTextRegion = 6,
    ImmediateLosslessTextRegion = 7,
    PatternDictionary = 16,
    IntermediateHalftoneRegion = 20,
    ImmediateHalftoneRegion = 22,
    ImmediateLosslessHalftoneRegion = 23,
    IntermediateGenericRegion = 36,
    ImmediateGenericRegion = 38,
    ImmediateLosslessGenericRegion = 39,
    IntermediateRefinementRegion = 40,
    ImmediateRefinementRegion = 42,
    ImmediateLosslessRefinementRegion = 43,
    PageInformation = 48,
    EndOfPage = 49,
    EndOfStripe = 50,
    EndOfFile = 51,
    Profiles = 52,
    Tables = 53,
    Extension = 62,
}

/// <summary>
/// A parsed segment header (ITU-T T.88 §7.2). <see cref="DataStart"/> and
/// <see cref="DataLength"/> locate the segment's data part within the stream the
/// header was read from — for the sequential and PDF-embedded organizations that
/// is the bytes immediately following the header, and for the random-access
/// organization the data parts follow all of the headers in the same order.
/// </summary>
internal readonly record struct SegmentHeader(
    uint Number,
    SegmentType Type,
    uint[] ReferredTo,
    uint Page,
    int DataStart,
    int DataLength)
{
    /// <summary>
    /// For an immediate generic region whose header declared an unknown data length
    /// (T.88 §7.2.7), the row count that follows its end sequence, which replaces the
    /// region's declared height (§7.4.6.4). Null for every other segment.
    /// </summary>
    public uint? RowCount { get; init; }

    /// <summary>Whether the header declared 0xFFFFFFFF and the length is still to be found.</summary>
    public bool LengthUnknown => DataLength < 0;
}

/// <summary>Parsing of T.88 segment headers and the segment-level fields they introduce.</summary>
internal static class Jbig2Segment
{
    /// <summary>
    /// T.88 §7.2.7 unknown-length marker. Legal only on an immediate generic
    /// region in a sequential or embedded stream, where the data part is instead
    /// terminated by an end sequence and a row count. <see cref="ReadHeader"/>
    /// reports it as a negative <see cref="SegmentHeader.DataLength"/>, and the
    /// sequential reader resolves it with <see cref="ResolveUnknownLength"/>.
    /// </summary>
    public const uint UnknownDataLength = 0xFFFFFFFF;

    /// <summary>
    /// Reads one segment header starting at <paramref name="position"/>,
    /// advancing it past the header. <see cref="SegmentHeader.DataStart"/> is set
    /// to the position right after the header — correct as-is for sequential and
    /// embedded streams; the random-access reader overwrites it.
    /// </summary>
    public static SegmentHeader ReadHeader(ReadOnlySpan<byte> data, ref int position)
    {
        var number = ReadUInt32(data, ref position);
        var flags = ReadByte(data, ref position);

        var type = (SegmentType)(flags & 0x3F);
        var pageAssociationIsFourBytes = (flags & 0x40) != 0;

        // §7.2.4: referred-to segment count and retain flags. The top three bits
        // of the first byte hold a count of 0..4 inline; the escape value 7 means
        // a four-byte count follows, then a bitmap of retain flags.
        var countByte = PeekByte(data, position);
        int referredCount;
        if ((countByte >> 5) == 7)
        {
            referredCount = (int)(ReadUInt32(data, ref position) & 0x1FFFFFFF);
            if (referredCount < 0)
                throw new InvalidDataException("JBIG2: implausible referred-to segment count.");
            // One retain bit per referred-to segment plus one for the segment itself.
            position += (referredCount + 8) / 8;
        }
        else
        {
            referredCount = countByte >> 5;
            position++;
        }

        // §7.2.5: referred-to numbers are sized by this segment's own number,
        // since a segment can only refer to lower-numbered ones.
        var referredSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        // Bound the count by the bytes actually present before allocating for it.
        if (position + (long)referredCount * referredSize > data.Length)
            throw new InvalidDataException(
                $"JBIG2: segment {number} claims {referredCount} referred-to segments, past the end of the stream.");
        var referredTo = referredCount == 0 ? [] : new uint[referredCount];
        for (var i = 0; i < referredCount; i++)
        {
            referredTo[i] = referredSize switch
            {
                1 => ReadByte(data, ref position),
                2 => ReadUInt16(data, ref position),
                _ => ReadUInt32(data, ref position),
            };
        }

        var page = pageAssociationIsFourBytes ? ReadUInt32(data, ref position) : ReadByte(data, ref position);

        var length = ReadUInt32(data, ref position);
        if (length == UnknownDataLength)
        {
            if (type is not (SegmentType.ImmediateGenericRegion or SegmentType.ImmediateLosslessGenericRegion))
                throw new InvalidDataException(
                    $"JBIG2: segment {number} of type {(int)type} declares an unknown data length, which " +
                    "T.88 §7.2.7 allows only for an immediate generic region.");
            return new SegmentHeader(number, type, referredTo, page, position, -1);
        }
        if (length > int.MaxValue || position + (long)length > data.Length)
            throw new InvalidDataException($"JBIG2: segment {number} data length {length} runs past the end of the stream.");

        return new SegmentHeader(number, type, referredTo, page, position, (int)length);
    }

    /// <summary>
    /// Finds the end of an immediate generic region whose data length is unknown
    /// (T.88 §7.2.7, §7.4.6.4). After the 18 bytes of region info and flags (and the
    /// AT pixels, for arithmetic coding) the coded data runs until an end sequence,
    /// <c>00 00</c> for MMR and <c>FF AC</c> for arithmetic coding, followed by a
    /// four-byte row count. Neither sequence can occur inside its own kind of coded
    /// data: MQ output never has <c>FF</c> followed by a byte above <c>8F</c>, and
    /// T.6 codes never hold sixteen zero bits in a row. The resolved data length
    /// covers the sequence and the row count.
    /// </summary>
    public static SegmentHeader ResolveUnknownLength(ReadOnlySpan<byte> stream, SegmentHeader header)
    {
        var start = header.DataStart;
        if (start + 18 > stream.Length)
            throw new InvalidDataException("JBIG2: truncated generic region of unknown length.");

        var flags = stream[start + 17];
        var mmr = (flags & 0x01) != 0;
        var codedStart = start + 18;
        if (!mmr) codedStart += ((flags >> 1) & 0x03) == 0 ? 8 : 2;

        if (codedStart > stream.Length)
            throw new InvalidDataException("JBIG2: truncated generic region of unknown length.");

        var (first, second) = mmr ? ((byte)0x00, (byte)0x00) : ((byte)0xFF, (byte)0xAC);
        var at = stream[codedStart..].IndexOf([first, second]);
        // The search is linear in the stream, once per segment, so a hostile stream
        // costs no more than reading it.
        if (at < 0 || codedStart + at + 6 > stream.Length)
            throw new InvalidDataException(
                $"JBIG2: generic region segment {header.Number} has an unknown data length and no end sequence.");

        var end = codedStart + at;
        var rows = BinaryPrimitives.ReadUInt32BigEndian(stream[(end + 2)..]);
        return header with { DataLength = end + 6 - start, RowCount = rows };
    }

    /// <summary>
    /// The region segment information field (T.88 §7.4.1): 17 bytes of geometry
    /// and the external combination operator, prefixed to every region segment.
    /// </summary>
    /// <param name="data">The segment data.</param>
    /// <param name="position">Where the field starts; advanced past it.</param>
    /// <param name="rowCount">
    /// The row count of an unknown-length generic region, which replaces the declared
    /// height (§7.4.6.4), or null.
    /// </param>
    public static RegionInfo ReadRegionInfo(ReadOnlySpan<byte> data, ref int position, uint? rowCount = null)
    {
        if (position + 17 > data.Length)
            throw new InvalidDataException("JBIG2: truncated region segment information field.");

        var width = ReadUInt32(data, ref position);
        var height = ReadUInt32(data, ref position);
        if (rowCount is { } rows) height = rows;
        var x = ReadUInt32(data, ref position);
        var y = ReadUInt32(data, ref position);
        var flags = ReadByte(data, ref position);

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            throw new InvalidDataException($"JBIG2: implausible region size {width}x{height}.");
        if ((long)width * height > Jbig2Limits.MaxBitmapPixels)
            throw new InvalidDataException(
                $"JBIG2: region {width}x{height} is {(long)width * height:N0} pixels, past the " +
                $"{Jbig2Limits.MaxBitmapPixels:N0} pixel ceiling for one bitmap.");

        var op = (CombinationOperator)(flags & 0x07);
        if (op > CombinationOperator.Replace)
            throw new InvalidDataException($"JBIG2: reserved external combination operator {flags & 0x07}.");

        return new RegionInfo((int)width, (int)height, (int)x, (int)y, op);
    }

    private static byte PeekByte(ReadOnlySpan<byte> data, int position)
    {
        if (position >= data.Length) throw new InvalidDataException("JBIG2: truncated segment header.");
        return data[position];
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int position)
    {
        var b = PeekByte(data, position);
        position++;
        return b;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 2 > data.Length) throw new InvalidDataException("JBIG2: truncated segment header.");
        var v = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        position += 2;
        return v;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 4 > data.Length) throw new InvalidDataException("JBIG2: truncated segment header.");
        var v = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += 4;
        return v;
    }
}

/// <summary>Geometry and placement of a region segment (T.88 §7.4.1).</summary>
internal readonly record struct RegionInfo(int Width, int Height, int X, int Y, CombinationOperator Operator);
