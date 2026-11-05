using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>One IFD entry copied out of a TIFF structure: its value bytes in host byte order, never an offset.</summary>
internal readonly record struct TiffExifEntry(ushort Tag, TiffFieldType Type, uint Count, byte[] Value);

/// <summary>
/// The directories of an Exif block (CIPA DC-008, a TIFF 6.0 structure): IFD0's descriptive tags, the Exif IFD
/// (tag 34665), the Interoperability IFD it points to (tag 40965) and the GPS IFD (tag 34853). Values are held in
/// host byte order with no file offsets, so the directories can be written anywhere: into a TIFF page
/// (<see cref="TiffWriter"/>) or back into a standalone block (<see cref="TiffExif.Build"/>).
/// </summary>
internal sealed class TiffExifDirectories
{
    public List<TiffExifEntry> Ifd0 { get; } = [];
    public List<TiffExifEntry> Exif { get; } = [];
    public List<TiffExifEntry> Interop { get; } = [];
    public List<TiffExifEntry> Gps { get; } = [];
    public bool HasExifIfd { get; set; }
    public bool HasGpsIfd { get; set; }
}

/// <summary>
/// Moves Exif between a standalone block (<see cref="Abstractions.ImageMetadata.Exif"/>: TIFF header, IFD0, Exif IFD, ...)
/// and a TIFF page, whose own IFD is the Exif IFD0. Only entries whose meaning does not depend on where they sit are
/// copied: value types 1-12 (TIFF 6.0 §2, "Types"), minus the tags that describe or locate image data, which belong to
/// the page being written. Everything else is skipped: pointers this class does not follow, IFD1 (the thumbnail),
/// unknown types and entries whose value lies outside the input.
/// </summary>
/// <remarks>
/// The input may be hostile. Every read is checked against the span; at most <see cref="MaxEntries"/> entries are copied
/// from a directory; the value bytes copied in total never exceed the input's length (entries of a valid block never
/// overlap), so overlapping entries cannot amplify a small input; and the only pointers followed are IFD0 → Exif / GPS
/// and Exif → Interop, so there is no recursion and no loop. A malformed entry is skipped, a malformed directory is
/// absent, and nothing here throws.
/// </remarks>
internal static class TiffExif
{
    private const int EntrySize = 12;
    public const ushort InteropIfd = 40965;

    /// <summary>
    /// Entries copied per directory: far more than any real Exif IFD holds, and low enough that the page's own tags
    /// still fit beside them in an IFD, whose entry count is 16 bits.
    /// </summary>
    internal const int MaxEntries = 4096;

    /// <summary>The directories of a standalone Exif block (an <c>Exif\0\0</c> prefix is skipped); null without a TIFF header.</summary>
    public static TiffExifDirectories? ParseBlock(ReadOnlySpan<byte> block)
    {
        if (block.StartsWith("Exif\0\0"u8))
            block = block[6..];
        if (block.Length < 8)
            return null;
        bool le;
        if (block[0] == 'I' && block[1] == 'I') le = true;
        else if (block[0] == 'M' && block[1] == 'M') le = false;
        else return null;
        if (U16(block[2..], le) != 42)
            return null;
        return Parse(block, new TiffFormat(le, BigTiff: false), U32(block[4..], le));
    }

    /// <summary>
    /// The Exif block of the TIFF page whose IFD is at <paramref name="ifdOffset"/>: its descriptive tags as IFD0 plus its
    /// Exif and GPS IFDs. Null when the page has neither an Exif nor a GPS IFD. The file may be a BigTIFF, whose
    /// directories (20-byte entries, 64-bit counts and offsets) are read the same way; the block built is always a
    /// classic one.
    /// </summary>
    public static byte[]? Extract(ReadOnlySpan<byte> tiff, TiffFormat format, long ifdOffset)
    {
        var dirs = Parse(tiff, format, ifdOffset);
        return dirs is { } d && (d.HasExifIfd || d.HasGpsIfd) ? Build(d) : null;
    }

    private static TiffExifDirectories? Parse(ReadOnlySpan<byte> tiff, TiffFormat format, long ifd0)
    {
        var dirs = new TiffExifDirectories();
        long budget = tiff.Length;
        if (!ReadIfd(tiff, format, ifd0, dirs.Ifd0, ref budget, out var exifAt, out var gpsAt, out _, ifd0: true))
            return null;
        if (exifAt is { } e && ReadIfd(tiff, format, e, dirs.Exif, ref budget, out _, out _, out var interopAt, ifd0: false))
        {
            dirs.HasExifIfd = true;
            if (interopAt is { } i)
                ReadIfd(tiff, format, i, dirs.Interop, ref budget, out _, out _, out _, ifd0: false);
        }
        if (gpsAt is { } g && ReadIfd(tiff, format, g, dirs.Gps, ref budget, out _, out _, out _, ifd0: false))
            dirs.HasGpsIfd = true;
        return dirs;
    }

    private static bool ReadIfd(ReadOnlySpan<byte> tiff, TiffFormat format, long offset, List<TiffExifEntry> into, ref long budget,
        out long? exifAt, out long? gpsAt, out long? interopAt, bool ifd0)
    {
        exifAt = gpsAt = interopAt = null;
        var le = format.LittleEndian;
        // Classic: 2-byte count, 12-byte entries, 4-byte value slot. BigTIFF: 8-byte count, 20-byte entries, 8-byte slot.
        var entrySize = format.EntrySize;
        var slot = format.OffsetSize;
        if (offset < 8 || offset > tiff.Length - format.CountSize)
            return false;
        var declared = format.BigTiff ? U64(tiff[(int)offset..], le) : U16(tiff[(int)offset..], le);
        // offset <= Length - CountSize and the count is bounded by the input, so this cannot overflow a long; the
        // directory must lie inside the input.
        if (declared > (ulong)tiff.Length || offset + format.CountSize + ((long)declared * entrySize) > tiff.Length)
            return false;
        var count = (int)declared;
        for (var n = 0; n < count && into.Count < MaxEntries; n++)
        {
            var entry = tiff.Slice((int)offset + format.CountSize + (n * entrySize), entrySize);
            var tag = U16(entry, le);
            var type = U16(entry[2..], le);
            var wideCount = format.BigTiff ? U64(entry[4..], le) : U32(entry[4..], le);
            var slotBytes = entry[(entrySize - slot)..];
            switch (tag)
            {
                case TiffTag.ExifIfd when ifd0: exifAt = Pointer(type, wideCount, slotBytes, format); continue;
                case TiffTag.GpsInfoIfd when ifd0: gpsAt = Pointer(type, wideCount, slotBytes, format); continue;
                case InteropIfd when !ifd0: interopAt = Pointer(type, wideCount, slotBytes, format); continue;
            }
            if (Skipped(tag) || type is < 1 or > 12 || wideCount > uint.MaxValue)
                continue;
            var valueCount = (uint)wideCount;
            var unit = UnitSize((TiffFieldType)type);
            var parts = type is 5 or 10 ? 2L : 1L; // a RATIONAL is two LONGs
            var length = valueCount * unit * parts;
            if (valueCount == 0 || length > budget)
                continue;
            ReadOnlySpan<byte> source;
            if (length <= slot)
            {
                source = slotBytes[..(int)length];
            }
            else
            {
                var wideAt = format.BigTiff ? U64(slotBytes, le) : U32(slotBytes, le);
                if (wideAt > (ulong)tiff.Length || (long)wideAt + length > tiff.Length)
                    continue;
                source = tiff.Slice((int)wideAt, (int)length);
            }
            budget -= length;
            var value = source.ToArray();
            if (le != BitConverter.IsLittleEndian)
                Swap(value, unit);
            into.Add(new TiffExifEntry(tag, (TiffFieldType)type, valueCount, value));
        }
        return true;
    }

    /// <summary>
    /// The offset an IFD pointer holds (one LONG or IFD, TIFF 6.0 / TIFF Technical Note 1; in BigTIFF also LONG8 or
    /// IFD8); null when it is not one.
    /// </summary>
    private static long? Pointer(ushort type, ulong count, ReadOnlySpan<byte> value, TiffFormat format)
    {
        if (count != 1) return null;
        if (type is 4 or 13) return U32(value, format.LittleEndian);
        if (format.BigTiff && type is 16 or 18 && U64(value, format.LittleEndian) <= long.MaxValue)
            return (long)U64(value, format.LittleEndian);
        return null;
    }

    /// <summary>
    /// Tags never copied between an Exif block and a page: the ones that describe or locate the image data (TIFF 6.0
    /// baseline and extensions), the thumbnail's JPEG pointers, the pointers handled by name (34665, 34853, 40965 and
    /// SubIFDs), and the blocks a page carries in their own <see cref="Abstractions.ImageMetadata"/> fields (XMP, ICC).
    /// </summary>
    internal static bool Skipped(ushort tag) => tag switch
    {
        >= 254 and <= 259 => true,   // NewSubfileType, SubfileType, ImageWidth, ImageLength, BitsPerSample, Compression
        >= 262 and <= 266 => true,   // PhotometricInterpretation, Threshholding, CellWidth, CellLength, FillOrder
        273 or 277 or 278 or 279 => true, // StripOffsets, SamplesPerPixel, RowsPerStrip, StripByteCounts
        280 or 281 or 284 => true,   // MinSampleValue, MaxSampleValue, PlanarConfiguration
        >= 288 and <= 293 => true,   // FreeOffsets, FreeByteCounts, GrayResponseUnit/Curve, T4Options, T6Options
        297 => true,                 // PageNumber
        317 or 320 or 321 => true,   // Predictor, ColorMap, HalftoneHints
        >= 322 and <= 325 => true,   // TileWidth, TileLength, TileOffsets, TileByteCounts
        330 or 332 => true,          // SubIFDs, InkSet
        >= 338 and <= 341 => true,   // ExtraSamples, SampleFormat, SMinSampleValue, SMaxSampleValue
        347 => true,                 // JPEGTables
        >= 512 and <= 521 => true,   // JPEGProc ... JPEGACTables, incl. JPEGInterchangeFormat(Length)
        >= 529 and <= 532 => true,   // YCbCrCoefficients, YCbCrSubSampling, YCbCrPositioning, ReferenceBlackWhite
        TiffTag.XmpPacket or TiffTag.IccProfile => true,
        TiffTag.ExifIfd or TiffTag.GpsInfoIfd or InteropIfd => true,
        37724 => true,               // ImageSourceData (Photoshop layers): image data, not metadata
        _ => false,
    };

    /// <summary>A standalone Exif block of <paramref name="dirs"/> in host byte order: header, IFD0, Exif, Interop, GPS.</summary>
    public static byte[] Build(TiffExifDirectories dirs)
    {
        var ifd0 = new List<TiffExifEntry>(dirs.Ifd0);
        var exif = new List<TiffExifEntry>(dirs.Exif);
        var hasInterop = dirs.HasExifIfd && dirs.Interop.Count > 0;
        // Placeholders for the pointers, so every directory's size is known before any offset is.
        if (dirs.HasExifIfd) ifd0.Add(PointerEntry(TiffTag.ExifIfd, 0));
        if (dirs.HasGpsIfd) ifd0.Add(PointerEntry(TiffTag.GpsInfoIfd, 0));
        if (hasInterop) exif.Add(PointerEntry(InteropIfd, 0));

        long at = 8;
        var ifd0At = at; at += Size(ifd0);
        var exifAt = at; if (dirs.HasExifIfd) at += Size(exif);
        var interopAt = at; if (hasInterop) at += Size(dirs.Interop);
        var gpsAt = at; if (dirs.HasGpsIfd) at += Size(dirs.Gps);
        if (at > int.MaxValue)
            throw new InvalidDataException("The Exif block is too large");

        Replace(ifd0, TiffTag.ExifIfd, exifAt);
        Replace(ifd0, TiffTag.GpsInfoIfd, gpsAt);
        Replace(exif, InteropIfd, interopAt);

        var block = new byte[at];
        var le = BitConverter.IsLittleEndian;
        block[0] = block[1] = (byte)(le ? 'I' : 'M');
        W16(block.AsSpan(2), 42);
        W32(block.AsSpan(4), (uint)ifd0At);
        Write(block, ifd0At, ifd0);
        if (dirs.HasExifIfd) Write(block, exifAt, exif);
        if (hasInterop) Write(block, interopAt, dirs.Interop);
        if (dirs.HasGpsIfd) Write(block, gpsAt, dirs.Gps);
        return block;
    }

    private static TiffExifEntry PointerEntry(ushort tag, long offset)
    {
        var value = new byte[4];
        W32(value, (uint)offset);
        return new TiffExifEntry(tag, TiffFieldType.Long, 1, value);
    }

    private static void Replace(List<TiffExifEntry> entries, ushort tag, long offset)
    {
        var i = entries.FindIndex(e => e.Tag == tag);
        if (i >= 0) entries[i] = PointerEntry(tag, offset);
    }

    // Directory (count, entries, next-IFD) then the values over 4 bytes, each starting on a word boundary.
    private static long Size(List<TiffExifEntry> entries)
    {
        long size = 2 + ((long)entries.Count * EntrySize) + 4;
        foreach (var e in entries)
            if (e.Value.Length > 4) size += (e.Value.Length + 1) & ~1;
        return size;
    }

    private static void Write(byte[] block, long at, List<TiffExifEntry> entries)
    {
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag)); // TIFF 6.0: entries sorted by tag, ascending
        var pos = (int)at;
        W16(block.AsSpan(pos), (ushort)entries.Count);
        var data = pos + 2 + (entries.Count * EntrySize) + 4;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var entry = block.AsSpan(pos + 2 + (i * EntrySize), EntrySize);
            W16(entry, e.Tag);
            W16(entry[2..], (ushort)e.Type);
            W32(entry[4..], e.Count);
            if (e.Value.Length <= 4)
            {
                e.Value.CopyTo(entry[8..]);
            }
            else
            {
                W32(entry[8..], (uint)data);
                e.Value.CopyTo(block.AsSpan(data));
                data += (e.Value.Length + 1) & ~1;
            }
        }
        // The next-IFD offset stays 0: IFD1 (the thumbnail) is not carried.
    }

    /// <summary>Bytes per component that byte order applies to: a RATIONAL is two LONGs, so its unit is 4.</summary>
    private static int UnitSize(TiffFieldType type) => type switch
    {
        TiffFieldType.Short or TiffFieldType.SShort => 2,
        TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float
            or TiffFieldType.Rational or TiffFieldType.SRational => 4,
        TiffFieldType.Double => 8,
        _ => 1,
    };

    private static void Swap(byte[] value, int unit)
    {
        if (unit == 1) return;
        for (var i = 0; i + unit <= value.Length; i += unit)
            value.AsSpan(i, unit).Reverse();
    }

    private static ushort U16(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);

    private static ulong U64(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt64LittleEndian(b) : BinaryPrimitives.ReadUInt64BigEndian(b);

    private static void W16(Span<byte> b, ushort v)
    {
        if (BitConverter.IsLittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        else BinaryPrimitives.WriteUInt16BigEndian(b, v);
    }

    private static void W32(Span<byte> b, uint v)
    {
        if (BitConverter.IsLittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        else BinaryPrimitives.WriteUInt32BigEndian(b, v);
    }
}
