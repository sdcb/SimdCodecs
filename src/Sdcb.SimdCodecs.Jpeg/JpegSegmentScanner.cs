using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// One marker segment in a JPEG header. <see cref="SegmentOffset"/> is the
/// position of the segment's <c>0xFF</c> marker byte (after any fill bytes);
/// <see cref="PayloadOffset"/>/<see cref="PayloadLength"/> locate the payload
/// — the bytes after the 2-byte big-endian length field. Standalone markers
/// (SOI, EOI, RSTn, TEM) have an empty payload.
/// </summary>
public readonly record struct JpegSegment(byte Marker, int SegmentOffset, int PayloadOffset, int PayloadLength)
{
    /// <summary>Total bytes the segment occupies, marker and length field included.</summary>
    public int TotalLength => PayloadOffset - SegmentOffset + PayloadLength;

    /// <summary>Slices this segment's payload out of the stream it was scanned from.</summary>
    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> jpeg) => jpeg.Slice(PayloadOffset, PayloadLength);
}

/// <summary>
/// Enumerates the marker segments of a JPEG header — everything from SOI up to
/// and including SOS, where entropy-coded data begins and the marker-and-length
/// rhythm stops. This is the byte-level access point for APPn metadata (JFIF,
/// EXIF, XMP, ICC, MPF) that <see cref="JpegDecoder"/> deliberately skips.
/// </summary>
public static class JpegSegmentScanner
{
    /// <summary>Start of Scan — entropy-coded data follows; scanning stops here.</summary>
    public const byte Sos = 0xDA;

    /// <summary>
    /// Scans the header segments of <paramref name="jpeg"/>: SOI first, then every
    /// marker segment up to and including SOS (or EOI for a scanless stream).
    /// </summary>
    /// <exception cref="InvalidDataException">The stream does not start with SOI or a
    /// segment is structurally malformed (bad marker byte, length running past EOF).</exception>
    public static List<JpegSegment> Scan(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Not a JPEG stream (missing SOI marker).");

        var segments = new List<JpegSegment> { new(0xD8, 0, 2, 0) };
        var pos = 2;
        while (true)
        {
            if (pos >= jpeg.Length)
                throw new InvalidDataException("Reached end of stream without finding SOS or EOI.");
            if (jpeg[pos] != 0xFF)
                throw new InvalidDataException($"Expected marker byte 0xFF at offset {pos}, got 0x{jpeg[pos]:X2}.");

            // Runs of 0xFF are legal fill before the marker code (ITU-T T.81 B.1.1.2);
            // the segment starts at the last fill byte, directly before the code.
            while (pos + 1 < jpeg.Length && jpeg[pos + 1] == 0xFF)
                pos++;
            if (pos + 1 >= jpeg.Length)
                throw new InvalidDataException("Ran off the end while scanning for a marker code.");

            var marker = jpeg[pos + 1];

            // Standalone markers carry no length field: EOI/TEM/RSTn (SOI repeats are
            // malformed but harmless to report as standalone).
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                segments.Add(new JpegSegment(marker, pos, pos + 2, 0));
                if (marker == 0xD9)
                    return segments; // EOI before any SOS: a scanless stream, but structurally complete
                pos += 2;
                continue;
            }

            if (pos + 4 > jpeg.Length)
                throw new InvalidDataException($"Segment length field at offset {pos + 2} runs past EOF.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos + 2, 2));
            if (length < 2 || pos + 2 + length > jpeg.Length)
                throw new InvalidDataException($"Invalid segment length {length} at offset {pos + 2}.");

            segments.Add(new JpegSegment(marker, pos, pos + 4, length - 2));
            if (marker == Sos)
                return segments; // entropy-coded data follows; no more marker rhythm

            pos += 2 + length;
        }
    }

    /// <summary>
    /// Finds the first APPn segment whose payload starts with
    /// <paramref name="identifier"/> (e.g. <c>"Exif\0\0"</c>, <c>"ICC_PROFILE\0"</c>,
    /// <c>"MPF\0"</c>, or the XMP URI) and returns the payload range <em>after</em>
    /// the identifier. Returns false when absent — or when the stream is not a
    /// structurally valid JPEG at all.
    /// </summary>
    public static bool TryFindAppPayload(ReadOnlySpan<byte> jpeg, byte appMarker, ReadOnlySpan<byte> identifier, out Range payload)
    {
        payload = default;
        List<JpegSegment> segments;
        try
        {
            segments = Scan(jpeg);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Marker != appMarker || segment.PayloadLength < identifier.Length)
                continue;
            if (!segment.Payload(jpeg)[..identifier.Length].SequenceEqual(identifier))
                continue;
            payload = (segment.PayloadOffset + identifier.Length)..(segment.PayloadOffset + segment.PayloadLength);
            return true;
        }

        return false;
    }
}
