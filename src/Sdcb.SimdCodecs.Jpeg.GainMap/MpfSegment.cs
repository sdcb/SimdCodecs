using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The Multi-Picture Format APP2 segment (CIPA DC-007) in the exact two-image
/// shape Ultra HDR uses: a big-endian MP header whose index IFD carries the
/// version, the image count (2), and two 16-byte MP entries — the primary and
/// the gain map. All offsets on the wire are relative to the start of the MP
/// endian field (segment start + 8), the convention both libultrahdr writes and
/// Chromium/Skia's <c>GetAbsoluteOffset</c> assumes; getting this wrong is the
/// classic way a gain-map file silently degrades to SDR-only.
/// </summary>
public static class MpfSegment
{
    /// <summary>The APP2 identifier: <c>"MPF\0"</c>.</summary>
    public static ReadOnlySpan<byte> Identifier => "MPF\0"u8;

    /// <summary>
    /// The full segment is fixed-size: FF E2 + length + "MPF\0" + endian(4) +
    /// IFD offset(4) + count(2) + 3 tags(36) + next-IFD(4) + 2 entries(32) = 90.
    /// </summary>
    public const int TotalLength = 90;

    // MP Index IFD tag ids (CIPA DC-007 §5.2.3).
    private const ushort VersionTag = 0xB000;
    private const ushort NumberOfImagesTag = 0xB001;
    private const ushort MpEntryTag = 0xB002;
    private const ushort TypeUndefined = 7;
    private const ushort TypeLong = 4;

    // MP entry attribute words: bits 24..26 = data format (0 = JPEG),
    // bits 0..23 = type code (0x030000 = Baseline MP Primary Image).
    private const uint AttributePrimaryJpeg = 0x00030000;
    private const uint AttributeUndefinedJpeg = 0x00000000;

    /// <summary>One MP entry, offsets already made absolute within the scanned file.
    /// The primary image's offset is 0 by spec.</summary>
    public readonly record struct Entry(uint Attribute, uint ImageLength, long ImageOffset);

    /// <summary>
    /// Builds the complete 90-byte APP2 MPF segment for a two-image file where the
    /// gain map immediately follows the primary. <paramref name="segmentOffset"/> is
    /// where this segment itself will sit in the assembled file — needed because the
    /// gain map's wire offset is measured from the endian field inside this segment.
    /// </summary>
    public static byte[] Write(int segmentOffset, int primaryImageLength, int gainMapImageLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(primaryImageLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gainMapImageLength);
        var endianFieldOffset = segmentOffset + 8;
        if (primaryImageLength <= endianFieldOffset)
            throw new ArgumentException("The MPF segment must sit inside the primary image.", nameof(segmentOffset));

        var segment = new byte[TotalLength];
        segment[0] = 0xFF;
        segment[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), TotalLength - 2);
        Identifier.CopyTo(segment.AsSpan(4));

        // MP header: big-endian marker (libultrahdr's default) + offset to the
        // index IFD, measured — like every offset below — from the endian field.
        segment[8] = 0x4D; segment[9] = 0x4D; segment[10] = 0x00; segment[11] = 0x2A;
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(12), 8);

        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(16), 3); // tag count

        // 0xB000 MPFVersion: UNDEFINED × 4, "0100" inline.
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(18), VersionTag);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(20), TypeUndefined);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(22), 4);
        "0100"u8.CopyTo(segment.AsSpan(26));

        // 0xB001 NumberOfImages: LONG × 1 = 2.
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(30), NumberOfImagesTag);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(32), TypeLong);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(38), 2);

        // 0xB002 MPEntry: UNDEFINED × 32, stored past the IFD at relative offset 50.
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(42), MpEntryTag);
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(44), TypeUndefined);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(46), 32);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(50), 50);

        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(54), 0); // no next IFD

        // Entry 1 — primary: offset 0 by spec, length = the whole primary JPEG
        // (base + spliced XMP + this segment). Trailing dependency fields stay 0.
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(58), AttributePrimaryJpeg);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(62), (uint)primaryImageLength);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(66), 0);

        // Entry 2 — gain map: starts right after the primary.
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(74), AttributeUndefinedJpeg);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(78), (uint)gainMapImageLength);
        BinaryPrimitives.WriteUInt32BigEndian(segment.AsSpan(82), (uint)(primaryImageLength - endianFieldOffset));

        return segment;
    }

    /// <summary>
    /// Parses an MPF payload (the bytes after <see cref="Identifier"/>, i.e. starting
    /// at the endian field). <paramref name="payloadOffset"/> is that payload's
    /// absolute position in the scanned file, used to convert the wire's
    /// endian-field-relative offsets into absolute ones. Accepts either endianness
    /// and any image count — callers pick the entries they care about.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, long payloadOffset, out Entry[] entries)
    {
        entries = [];
        if (payload.Length < 16)
            return false;

        bool bigEndian;
        if (payload[0] == 0x4D && payload[1] == 0x4D && payload[2] == 0x00 && payload[3] == 0x2A)
            bigEndian = true;
        else if (payload[0] == 0x49 && payload[1] == 0x49 && payload[2] == 0x2A && payload[3] == 0x00)
            bigEndian = false;
        else
            return false;

        var ifdOffset = ReadU32(payload, 4, bigEndian);
        if (ifdOffset + 2 > (uint)payload.Length)
            return false;

        var pos = (int)ifdOffset;
        int tagCount = ReadU16(payload, pos, bigEndian);
        pos += 2;
        if (pos + tagCount * 12 + 4 > payload.Length)
            return false;

        uint imageCount = 0, entryOffset = 0;
        for (var i = 0; i < tagCount; i++, pos += 12)
        {
            var tag = ReadU16(payload, pos, bigEndian);
            var value = ReadU32(payload, pos + 8, bigEndian);
            if (tag == NumberOfImagesTag) imageCount = value;
            else if (tag == MpEntryTag) entryOffset = value;
        }

        if (imageCount == 0 || imageCount > 16 || entryOffset == 0
            || entryOffset + imageCount * 16 > (uint)payload.Length)
            return false;

        entries = new Entry[imageCount];
        for (var i = 0; i < imageCount; i++)
        {
            var at = (int)entryOffset + i * 16;
            var attribute = ReadU32(payload, at, bigEndian);
            var length = ReadU32(payload, at + 4, bigEndian);
            var offset = ReadU32(payload, at + 8, bigEndian);
            entries[i] = new Entry(attribute, length, offset == 0 ? 0 : payloadOffset + offset);
        }

        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..])
                  : BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
                  : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
}
