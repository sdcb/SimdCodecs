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
    int DataLength);

/// <summary>Parsing of T.88 segment headers and the segment-level fields they introduce.</summary>
internal static class Jbig2Segment
{
    /// <summary>
    /// T.88 §7.2.7 unknown-length marker. Legal only on an immediate generic
    /// region, where the data part is instead terminated by a search sequence;
    /// this decoder rejects it rather than guessing.
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
            throw new NotSupportedException(
                "JBIG2: segment with unknown data length (0xFFFFFFFF, T.88 §7.2.7) is not supported.");
        if (length > int.MaxValue || position + (long)length > data.Length)
            throw new InvalidDataException($"JBIG2: segment {number} data length {length} runs past the end of the stream.");

        return new SegmentHeader(number, type, referredTo, page, position, (int)length);
    }

    /// <summary>
    /// The region segment information field (T.88 §7.4.1): 17 bytes of geometry
    /// and the external combination operator, prefixed to every region segment.
    /// </summary>
    public static RegionInfo ReadRegionInfo(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 17 > data.Length)
            throw new InvalidDataException("JBIG2: truncated region segment information field.");

        var width = ReadUInt32(data, ref position);
        var height = ReadUInt32(data, ref position);
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
