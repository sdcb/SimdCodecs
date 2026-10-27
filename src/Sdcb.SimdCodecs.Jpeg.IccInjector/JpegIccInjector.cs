using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Inserts an ICC profile (APP2 segment) into a JPEG byte stream produced by
/// any encoder. The encoder itself never needs to know about colour management:
/// callers run their preferred JPEG encoder, then post-process the bytes
/// through <see cref="EmbedIccProfile"/> to add the profile tag.
///
/// <para>
/// JPEG ICC carriage follows the ICC.1:2004-10 spec annex B: one or more APP2
/// segments, each starting with the null-terminated marker string
/// <c>"ICC_PROFILE\0"</c> followed by a 1-based sequence number and the total
/// segment count, then a slice of the profile bytes. This implementation only
/// emits a single segment (sequence 1 of 1), sufficient for any profile up to
/// 65519 bytes — covers all common display profiles including the bundled
/// 588-byte sRGB v4. Multi-segment carriage can be added when a use case for
/// larger profiles surfaces.
/// </para>
/// </summary>
public static class JpegIccInjector
{
    // "ICC_PROFILE" + NUL terminator — 12 bytes. The string is the standard
    // identifier specified by the ICC for APP2-carried profiles; readers
    // (browsers, Photoshop, libjpeg-turbo, etc.) scan APP2 segments looking
    // for exactly this prefix.
    private static ReadOnlySpan<byte> IccMarker => "ICC_PROFILE\0"u8;

    // APP2 segment overhead: 2-byte length field + 12-byte ICC marker +
    // 1-byte sequence number + 1-byte total count = 16 bytes.
    private const int IccSegmentOverhead = 16;

    // Maximum APP2 payload (the length field is uint16). Subtract the
    // per-segment overhead to get the per-segment ICC payload capacity.
    private const int MaxSingleSegmentProfileBytes = 0xFFFF - IccSegmentOverhead;

    /// <summary>
    /// Returns a new JPEG byte buffer with an APP2 ICC profile segment inserted
    /// immediately after any existing APP markers (or right after SOI if there
    /// are none). The input is not mutated.
    /// </summary>
    /// <param name="jpeg">A complete JPEG byte stream starting with SOI (FF D8).</param>
    /// <param name="profile">Raw ICC profile bytes — for example
    /// <c>Sdcb.SimdCodecs.Color.Icc.IccProfiles.SRgbV4</c>.</param>
    /// <exception cref="ArgumentException">If <paramref name="jpeg"/> doesn't start with
    /// SOI, ends before reaching a non-APP marker, or contains a malformed segment.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If the profile is empty or larger
    /// than 65519 bytes (single-segment limit — multi-segment ICC carriage is not
    /// implemented in this version).</exception>
    public static byte[] EmbedIccProfile(ReadOnlySpan<byte> jpeg, ReadOnlyMemory<byte> profile)
    {
        if (profile.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(profile), "Profile must be non-empty.");
        if (profile.Length > MaxSingleSegmentProfileBytes)
            throw new ArgumentOutOfRangeException(nameof(profile),
                $"Profile too large for single-segment APP2 ({profile.Length} > {MaxSingleSegmentProfileBytes} bytes). " +
                "Multi-segment carriage is not implemented in this version.");

        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new ArgumentException("Input is not a JPEG (missing SOI marker).", nameof(jpeg));

        // Walk markers from just past the SOI. APPn segments (FF E0..EF) are
        // skipped so the new APP2 lands after JFIF / EXIF / any other existing
        // APP markers — this matches what colour-managed encoders emit natively
        // and avoids producing a file where two APPn markers have the same
        // sequence number but different roles.
        var insertOffset = FindInsertionPoint(jpeg);

        var segmentLength = IccSegmentOverhead + profile.Length;
        var output = new byte[jpeg.Length + 2 + segmentLength]; // +2 for the FF E2 marker bytes

        // [SOI .. last APPn (if any)]
        jpeg[..insertOffset].CopyTo(output);

        // FF E2 (APP2 marker)
        var dst = output.AsSpan(insertOffset);
        dst[0] = 0xFF;
        dst[1] = 0xE2;

        // 2-byte big-endian length (includes the length field itself but NOT the marker bytes)
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..4], (ushort)segmentLength);

        // "ICC_PROFILE\0"
        IccMarker.CopyTo(dst[4..]);

        // Sequence number (1-based) and total segment count. Single segment = 1 of 1.
        dst[16] = 0x01;
        dst[17] = 0x01;

        // Profile payload
        profile.Span.CopyTo(dst[18..]);

        // Remainder of the JPEG (everything from the insertion point onward in the original)
        jpeg[insertOffset..].CopyTo(output.AsSpan(insertOffset + 2 + segmentLength));

        return output;
    }

    private static int FindInsertionPoint(ReadOnlySpan<byte> jpeg)
    {
        var pos = 2; // skip SOI
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                throw new ArgumentException($"Malformed JPEG: expected marker byte 0xFF at offset {pos}, got 0x{jpeg[pos]:X2}.", nameof(jpeg));

            // Skip fill bytes (0xFF padding between markers is legal per ITU-T T.81 B.1.1.2)
            while (pos < jpeg.Length && jpeg[pos] == 0xFF)
                pos++;
            if (pos >= jpeg.Length)
                throw new ArgumentException("Malformed JPEG: ran off the end while scanning for marker code.", nameof(jpeg));

            var marker = jpeg[pos];
            pos++; // step past the marker code

            // Standalone markers with no payload: SOI/EOI/RSTn/TEM. SOI shouldn't appear here
            // but we handle the family anyway. We never want to walk past SOS — that begins
            // entropy-coded data which doesn't follow the marker-and-length rhythm.
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                return pos - 2; // back up to the 0xFF; caller wants the position of the marker, not past it

            // APP0..APP15 (0xE0..0xEF) — skip past the segment and keep walking.
            // Anything else (DQT/DHT/SOFn/SOS/...) is where we insert.
            if (marker < 0xE0 || marker > 0xEF)
                return pos - 2;

            if (pos + 2 > jpeg.Length)
                throw new ArgumentException($"Malformed JPEG: segment length field at offset {pos} runs past EOF.", nameof(jpeg));

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(pos, 2));
            if (segmentLength < 2)
                throw new ArgumentException($"Malformed JPEG: invalid segment length {segmentLength} at offset {pos}.", nameof(jpeg));

            pos += segmentLength; // length is inclusive of the 2 length bytes themselves
        }

        throw new ArgumentException("Malformed JPEG: reached end of stream without finding a non-APP marker.", nameof(jpeg));
    }
}
