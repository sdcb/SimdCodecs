#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Tiff;

namespace Sdcb.SimdCodecs.Exif;

/// <summary>
/// Reads EXIF metadata from JPEG / TIFF / PNG / WebP containers. EXIF is
/// structurally a TIFF-IFD chain regardless of where it's embedded:
/// <list type="bullet">
/// <item>JPEG carries it in the APP1 segment (<c>FF E1</c>) prefixed with
///   the magic "Exif\0\0".</item>
/// <item>TIFF carries it in a sub-IFD pointed at by tag 34665 (ExifIFD).</item>
/// <item>PNG carries it in the optional <c>eXIf</c> ancillary chunk
///   (introduced in the 2017 PNG 1.2 errata).</item>
/// <item>WebP carries it in the extended format's <c>EXIF</c> RIFF chunk.</item>
/// </list>
/// All four are handled by stripping the container framing and feeding
/// the raw "TIFF-with-IFDs" payload to the same IFD walker.
///
/// Returns <c>null</c> when no EXIF blob is found — that's the common case
/// for files that were never tagged (rendered TIFFs, screenshots, etc.).
/// Returns an <see cref="ExifMetadata"/> with every well-known field
/// populated where possible (best-effort) and the long tail accessible
/// via <see cref="ExifMetadata.RawTags"/>.
/// </summary>
public static class ExifReader
{
    /// <summary>
    /// Auto-detect the container and decode EXIF. Sniffs the first few
    /// bytes for JPEG (<c>FF D8</c>), TIFF (<c>II</c>/<c>MM</c>+42),
    /// PNG (89 50 4E 47 …) or WebP (<c>RIFF</c> … <c>WEBP</c>) and dispatches.
    /// </summary>
    public static ExifMetadata? FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return null;
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return FromJpeg(bytes);
        if ((bytes[0] == 'I' && bytes[1] == 'I') || (bytes[0] == 'M' && bytes[1] == 'M'))
            return FromTiff(bytes);
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return FromPng(bytes);
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return FromWebp(bytes);
        return null;
    }

    /// <summary>
    /// Extract the EXIF segment from a JPEG (APP1 with "Exif\0\0" signature)
    /// and decode it.
    /// </summary>
    public static ExifMetadata? FromJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;

        var pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            // Marker prefix can be a run of 0xFF fill bytes.
            while (pos < jpeg.Length && jpeg[pos] == 0xFF) pos++;
            if (pos >= jpeg.Length) return null;
            var marker = jpeg[pos++];
            if (marker == 0xD9 /*EOI*/ || marker == 0xDA /*SOS*/) return null;
            if (marker >= 0xD0 && marker <= 0xD7) continue; // RSTn = no payload
            if (pos + 2 > jpeg.Length) return null;
            var segLen = (jpeg[pos] << 8) | jpeg[pos + 1];
            if (segLen < 2 || pos + segLen > jpeg.Length) return null;
            if (marker == 0xE1 /*APP1*/ && segLen >= 8)
            {
                var payload = jpeg.Slice(pos + 2, segLen - 2);
                // APP1 may also be used for XMP — check the magic.
                if (payload.Length >= 6
                    && payload[0] == (byte)'E' && payload[1] == (byte)'x' && payload[2] == (byte)'i'
                    && payload[3] == (byte)'f' && payload[4] == 0 && payload[5] == 0)
                {
                    var tiff = payload[6..];
                    return FromTiff(tiff);
                }
            }
            pos += segLen;
        }
        return null;
    }

    /// <summary>
    /// Walk a top-level TIFF, find the ExifIFD sub-IFD pointer (tag 34665)
    /// in IFD0, and decode it. Returns <c>null</c> if the file isn't a TIFF
    /// or doesn't carry an ExifIFD. The TIFF byte order ("II"/"MM") is
    /// honoured.
    /// </summary>
    public static ExifMetadata? FromTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return null;
        bool fileIsLE;
        if (tiff[0] == 'I' && tiff[1] == 'I') fileIsLE = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') fileIsLE = false;
        else return null;
        if (ReadUInt16(tiff.Slice(2, 2), fileIsLE) != 42) return null;

        var ifd0Offset = (int)ReadUInt32(tiff.Slice(4, 4), fileIsLE);
        var ifd0 = ParseIfd(tiff, ifd0Offset, fileIsLE, out _);

        // GPS sub-IFD pointer lives in IFD0 (tag 34853), separately from the
        // ExifIFD. Parse it if present regardless of whether ExifIFD exists.
        Dictionary<ushort, RawEntry>? gpsIfd = null;
        if (ifd0.TryGetValue(TiffTag.GpsInfoIfd, out var gpsPtr))
        {
            var gpsOffset = (int)ScalarFromRaw(gpsPtr, fileIsLE);
            gpsIfd = ParseIfd(tiff, gpsOffset, fileIsLE, out _);
        }

        // Some TIFF blobs (notably the ones JPEG APP1 carries) put EXIF tags
        // directly in IFD0 rather than via the 34665 sub-IFD pointer. Try the
        // sub-IFD first; if absent, parse IFD0 itself as if it were EXIF.
        if (ifd0.TryGetValue(TiffTag.ExifIfd, out var exifPtr))
        {
            var exifOffset = (int)ScalarFromRaw(exifPtr, fileIsLE);
            return FromIfd(tiff, exifOffset, fileIsLE, ifd0, gpsIfd);
        }

        return BuildMetadata(ifd0, tiff, fileIsLE, gpsIfd);
    }

    /// <summary>
    /// Locate and decode EXIF from a PNG <c>eXIf</c> ancillary chunk.
    /// Returns <c>null</c> if the chunk isn't present.
    /// </summary>
    public static ExifMetadata? FromPng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8) return null;
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!png[..8].SequenceEqual(sig)) return null;

        var pos = 8;
        while (pos + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos, 4));
            if (pos + 12L + length > png.Length) return null;
            var type = png.Slice(pos + 4, 4);
            if (type[0] == (byte)'e' && type[1] == (byte)'X' && type[2] == (byte)'I' && type[3] == (byte)'f')
            {
                // The chunk payload is the raw TIFF (including the II/MM header).
                return FromTiff(png.Slice(pos + 8, (int)length));
            }
            if (type[0] == (byte)'I' && type[1] == (byte)'E' && type[2] == (byte)'N' && type[3] == (byte)'D')
                return null;
            pos += 12 + (int)length; // length(4) + type(4) + data + crc(4)
        }
        return null;
    }

    /// <summary>
    /// Locate and decode EXIF from a WebP file's <c>EXIF</c> chunk (RFC 9649, the extended
    /// file format). The chunk holds the TIFF-format EXIF data; a leading <c>Exif\0\0</c>, which some
    /// writers copy over from JPEG's APP1, is skipped. Returns <c>null</c> if the file isn't a WebP
    /// or carries no <c>EXIF</c> chunk.
    /// </summary>
    public static ExifMetadata? FromWebp(ReadOnlySpan<byte> webp)
    {
        if (webp.Length < 12 || !webp[..4].SequenceEqual("RIFF"u8) || !webp.Slice(8, 4).SequenceEqual("WEBP"u8))
            return null;

        // The RIFF size counts from offset 8; a file cut short is scanned as far as it goes.
        var end = (int)Math.Min(8L + BinaryPrimitives.ReadUInt32LittleEndian(webp.Slice(4, 4)), webp.Length);
        var pos = 12;
        while (pos + 8 <= end)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(webp.Slice(pos + 4, 4));
            if (size > (uint)(end - pos - 8)) return null;
            if (webp.Slice(pos, 4).SequenceEqual("EXIF"u8))
            {
                var payload = webp.Slice(pos + 8, (int)size);
                if (payload.StartsWith("Exif\0\0"u8)) payload = payload[6..];
                return FromTiff(payload);
            }
            pos += 8 + (int)size + (int)(size & 1); // chunks are padded to an even length
        }
        return null;
    }

    /// <summary>
    /// Decode an EXIF IFD at the given offset inside a TIFF-format span.
    /// Used by <see cref="TiffReader"/> consumers who already have the
    /// <c>ExifIfdOffset</c> from <see cref="Tiff.TiffPage"/> and don't want
    /// to re-walk the TIFF header.
    /// </summary>
    public static ExifMetadata FromIfd(ReadOnlySpan<byte> tiff, int ifdOffset, bool fileIsLittleEndian)
        => FromIfd(tiff, ifdOffset, fileIsLittleEndian, mergeFromIfd0: null, gpsIfd: null);

    private static ExifMetadata FromIfd(
        ReadOnlySpan<byte> tiff,
        int ifdOffset,
        bool fileIsLE,
        Dictionary<ushort, RawEntry>? mergeFromIfd0,
        Dictionary<ushort, RawEntry>? gpsIfd)
    {
        var exifIfd = ParseIfd(tiff, ifdOffset, fileIsLE, out _);

        // Merge IFD0's general tags (Make, Model, Orientation, DateTime, etc.)
        // into the EXIF tag map so all the strongly-typed properties can be
        // populated regardless of which IFD a given tag lives in.
        var merged = new Dictionary<ushort, RawEntry>(exifIfd);
        if (mergeFromIfd0 is not null)
        {
            foreach (var (tag, value) in mergeFromIfd0)
                merged.TryAdd(tag, value);
        }
        return BuildMetadata(merged, tiff, fileIsLE, gpsIfd);
    }

    // -----------------------------------------------------------------
    // IFD walker — duplicate of TiffReader's helpers (intentional: this
    // is the boundary where EXIF parsing becomes a standalone concern,
    // so we don't want it to drag in the full TIFF pixel decode surface).
    // -----------------------------------------------------------------

    private readonly record struct RawEntry(TiffFieldType Type, int Count, byte[] Data);

    private static Dictionary<ushort, RawEntry> ParseIfd(
        ReadOnlySpan<byte> tiff, int ifdOffset, bool fileIsLE, out int nextIfdOffset)
    {
        var result = new Dictionary<ushort, RawEntry>();
        // Offsets come from untrusted uint fields cast to int, so they may be negative.
        if (ifdOffset < 0 || ifdOffset + 2L > tiff.Length) { nextIfdOffset = 0; return result; }
        var entryCount = ReadUInt16(tiff.Slice(ifdOffset, 2), fileIsLE);
        const int entrySize = 12;
        var directoryEnd = ifdOffset + 2 + entryCount * entrySize;
        if (directoryEnd + 4L > tiff.Length) { nextIfdOffset = 0; return result; }

        for (var i = 0; i < entryCount; i++)
        {
            var entryStart = ifdOffset + 2 + i * entrySize;
            var tag = ReadUInt16(tiff.Slice(entryStart, 2), fileIsLE);
            var type = (TiffFieldType)ReadUInt16(tiff.Slice(entryStart + 2, 2), fileIsLE);
            var count = ReadUInt32(tiff.Slice(entryStart + 4, 4), fileIsLE);
            var valueSpan = tiff.Slice(entryStart + 8, 4);
            var typeSize = FieldTypeSize(type);
            if (typeSize == 0) continue; // unsupported field type — skip gracefully
            var totalBytes = (long)typeSize * count;
            ReadOnlySpan<byte> data;
            if (totalBytes <= 4)
            {
                data = valueSpan[..(int)totalBytes];
            }
            else
            {
                var off = ReadUInt32(valueSpan, fileIsLE);
                if (off + totalBytes > tiff.Length) continue; // out of bounds — skip
                data = tiff.Slice((int)off, (int)totalBytes);
            }
            result[tag] = new RawEntry(type, (int)count, data.ToArray());
        }
        nextIfdOffset = (int)ReadUInt32(tiff.Slice(directoryEnd, 4), fileIsLE);
        return result;
    }

    private static int FieldTypeSize(TiffFieldType type) => type switch
    {
        TiffFieldType.Byte or TiffFieldType.Ascii or TiffFieldType.SByte or TiffFieldType.Undefined => 1,
        TiffFieldType.Short or TiffFieldType.SShort => 2,
        TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float => 4,
        TiffFieldType.Rational or TiffFieldType.SRational or TiffFieldType.Double => 8,
        _ => 0,
    };

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static int ReadInt32(ReadOnlySpan<byte> bytes, bool fileIsLE) => fileIsLE
        ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadInt32BigEndian(bytes);

    // A zero-count pointer entry has no data; 0 is never a valid IFD offset (it is the header).
    private static uint ScalarFromRaw(RawEntry entry, bool fileIsLE) => entry.Type switch
    {
        TiffFieldType.Byte when entry.Data.Length >= 1  => entry.Data[0],
        TiffFieldType.Short when entry.Data.Length >= 2 => ReadUInt16(entry.Data, fileIsLE),
        TiffFieldType.Long when entry.Data.Length >= 4  => ReadUInt32(entry.Data, fileIsLE),
        _ => 0u,
    };

    // -----------------------------------------------------------------
    // Strongly-typed extraction
    // -----------------------------------------------------------------

    private static ExifMetadata BuildMetadata(
        Dictionary<ushort, RawEntry> entries,
        ReadOnlySpan<byte> tiff,
        bool fileIsLE,
        Dictionary<ushort, RawEntry>? gpsIfd)
    {
        // Project raw entries → public ExifTagValue map for the long tail.
        var raw = new Dictionary<ushort, ExifTagValue>(entries.Count);
        foreach (var (tag, e) in entries)
            raw[tag] = new ExifTagValue(tag, e.Type, e.Count, e.Data);

        return new ExifMetadata(
            Make:               GetAscii(entries, ExifTag.Make),
            Model:              GetAscii(entries, ExifTag.Model),
            CaptureTime:        GetDateTime(entries, ExifTag.DateTimeOriginal)
                                ?? GetDateTime(entries, ExifTag.DateTime),
            FileTime:           GetDateTime(entries, ExifTag.DateTime),
            ExposureTime:       GetRational(entries, ExifTag.ExposureTime, fileIsLE),
            FNumber:            GetRational(entries, ExifTag.FNumber, fileIsLE),
            Iso:                GetUShortFirst(entries, ExifTag.IsoSpeedRatings, fileIsLE),
            FocalLength:        GetRational(entries, ExifTag.FocalLength, fileIsLE),
            FocalLengthIn35mm:  GetUShortFirst(entries, ExifTag.FocalLengthIn35mm, fileIsLE),
            Software:           GetAscii(entries, ExifTag.Software),
            Artist:             GetAscii(entries, ExifTag.Artist),
            Copyright:          GetAscii(entries, ExifTag.Copyright),
            Orientation:        (ushort?)GetUShortFirst(entries, ExifTag.Orientation, fileIsLE),
            PixelWidth:         (int?)GetScalar(entries, ExifTag.PixelXDimension, fileIsLE),
            PixelHeight:        (int?)GetScalar(entries, ExifTag.PixelYDimension, fileIsLE),
            Gps:                gpsIfd is null ? null : BuildGps(gpsIfd, fileIsLE),
            RawTags:            raw);
    }

    private static GpsCoordinates? BuildGps(Dictionary<ushort, RawEntry> gpsIfd, bool fileIsLE)
    {
        var lat = ReadGpsDms(gpsIfd, ExifTag.GpsLatitude, ExifTag.GpsLatitudeRef, "S", fileIsLE);
        var lon = ReadGpsDms(gpsIfd, ExifTag.GpsLongitude, ExifTag.GpsLongitudeRef, "W", fileIsLE);
        if (lat is null && lon is null) return null;

        double? altitude = null;
        if (gpsIfd.TryGetValue(ExifTag.GpsAltitude, out var altE) && altE.Type == TiffFieldType.Rational
            && altE.Data.Length >= 8)
        {
            var num = ReadUInt32(altE.Data.AsSpan(0, 4), fileIsLE);
            var den = ReadUInt32(altE.Data.AsSpan(4, 4), fileIsLE);
            if (den != 0)
            {
                altitude = (double)num / den;
                // AltitudeRef byte: 0 = above sea level, 1 = below.
                if (gpsIfd.TryGetValue(ExifTag.GpsAltitudeRef, out var refE) && refE.Data.Length >= 1
                    && refE.Data[0] == 1)
                    altitude = -altitude;
            }
        }

        DateTime? timestamp = null;
        if (gpsIfd.TryGetValue(ExifTag.GpsDateStamp, out var dateE)
            && gpsIfd.TryGetValue(ExifTag.GpsTimeStamp, out var timeE)
            && timeE.Type == TiffFieldType.Rational && timeE.Count >= 3 && timeE.Data.Length >= 24)
        {
            var dateStr = AsciiToString(dateE.Data);
            if (DateTime.TryParseExact(dateStr, "yyyy:MM:dd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var date))
            {
                var hNum = ReadUInt32(timeE.Data.AsSpan(0, 4), fileIsLE);
                var hDen = ReadUInt32(timeE.Data.AsSpan(4, 4), fileIsLE);
                var mNum = ReadUInt32(timeE.Data.AsSpan(8, 4), fileIsLE);
                var mDen = ReadUInt32(timeE.Data.AsSpan(12, 4), fileIsLE);
                var sNum = ReadUInt32(timeE.Data.AsSpan(16, 4), fileIsLE);
                var sDen = ReadUInt32(timeE.Data.AsSpan(20, 4), fileIsLE);
                if (hDen != 0 && mDen != 0 && sDen != 0)
                {
                    var seconds = (double)hNum / hDen * 3600 + (double)mNum / mDen * 60 + (double)sNum / sDen;
                    timestamp = DateTime.SpecifyKind(date.AddSeconds(seconds), DateTimeKind.Utc);
                }
            }
        }

        return new GpsCoordinates(lat ?? 0, lon ?? 0, altitude, timestamp);
    }

    private static double? ReadGpsDms(
        Dictionary<ushort, RawEntry> gpsIfd, ushort coordTag, ushort refTag, string negativeRefCode, bool fileIsLE)
    {
        if (!gpsIfd.TryGetValue(coordTag, out var coord)
            || coord.Type != TiffFieldType.Rational || coord.Count < 3 || coord.Data.Length < 24) return null;
        var dNum = ReadUInt32(coord.Data.AsSpan(0, 4), fileIsLE);
        var dDen = ReadUInt32(coord.Data.AsSpan(4, 4), fileIsLE);
        var mNum = ReadUInt32(coord.Data.AsSpan(8, 4), fileIsLE);
        var mDen = ReadUInt32(coord.Data.AsSpan(12, 4), fileIsLE);
        var sNum = ReadUInt32(coord.Data.AsSpan(16, 4), fileIsLE);
        var sDen = ReadUInt32(coord.Data.AsSpan(20, 4), fileIsLE);
        if (dDen == 0 || mDen == 0 || sDen == 0) return null;
        var deg = (double)dNum / dDen + (double)mNum / mDen / 60 + (double)sNum / sDen / 3600;
        if (gpsIfd.TryGetValue(refTag, out var refE)
            && AsciiToString(refE.Data) == negativeRefCode)
            deg = -deg;
        return deg;
    }

    private static string? GetAscii(Dictionary<ushort, RawEntry> entries, ushort tag)
    {
        if (!entries.TryGetValue(tag, out var e) || e.Type != TiffFieldType.Ascii) return null;
        return AsciiToString(e.Data);
    }

    private static string AsciiToString(byte[] data)
    {
        // ASCII fields are null-terminated per TIFF 6.0 — strip trailing nulls + whitespace.
        // The TIFF/EXIF spec mandates 7-bit ASCII, but many cameras (Sony α-series,
        // Canon kanji firmware, phone vendors) sneak Latin-1 or UTF-8 into Make /
        // Model / Software / Artist strings anyway. UTF-8 decodes valid ASCII bytes
        // identically to ASCII and also handles the non-conforming-but-common cases
        // without losing characters.
        var len = data.Length;
        while (len > 0 && (data[len - 1] == 0 || data[len - 1] == ' ')) len--;
        return Encoding.UTF8.GetString(data, 0, len);
    }

    private static DateTime? GetDateTime(Dictionary<ushort, RawEntry> entries, ushort tag)
    {
        var s = GetAscii(entries, tag);
        if (string.IsNullOrEmpty(s) || s == "0000:00:00 00:00:00") return null;
        // EXIF date format: "YYYY:MM:DD HH:MM:SS". Local time (kind unspecified).
        return DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    private static Rational? GetRational(Dictionary<ushort, RawEntry> entries, ushort tag, bool fileIsLE)
    {
        if (!entries.TryGetValue(tag, out var e) || e.Type != TiffFieldType.Rational || e.Data.Length < 8)
            return null;
        var num = ReadUInt32(e.Data.AsSpan(0, 4), fileIsLE);
        var den = ReadUInt32(e.Data.AsSpan(4, 4), fileIsLE);
        return new Rational(num, den);
    }

    private static int? GetUShortFirst(Dictionary<ushort, RawEntry> entries, ushort tag, bool fileIsLE)
    {
        if (!entries.TryGetValue(tag, out var e)) return null;
        if (e.Type == TiffFieldType.Short && e.Data.Length >= 2)
            return ReadUInt16(e.Data.AsSpan(0, 2), fileIsLE);
        if (e.Type == TiffFieldType.Long && e.Data.Length >= 4)
            return (int)ReadUInt32(e.Data.AsSpan(0, 4), fileIsLE);
        return null;
    }

    private static uint? GetScalar(Dictionary<ushort, RawEntry> entries, ushort tag, bool fileIsLE)
    {
        if (!entries.TryGetValue(tag, out var e)) return null;
        if (e.Type == TiffFieldType.Short && e.Data.Length >= 2)
            return ReadUInt16(e.Data.AsSpan(0, 2), fileIsLE);
        if (e.Type == TiffFieldType.Long && e.Data.Length >= 4)
            return ReadUInt32(e.Data.AsSpan(0, 4), fileIsLE);
        return null;
    }
}
