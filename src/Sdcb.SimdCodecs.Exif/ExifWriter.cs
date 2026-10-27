using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Sdcb.SimdCodecs.Exif;

/// <summary>
/// The text fields <see cref="ExifWriter.Build"/> can write. Every field is optional; <c>null</c> leaves the tag out.
/// </summary>
/// <param name="Software">0x0131 Software (IFD0). 7-bit ASCII only; see <see cref="ExifWriter"/>.</param>
/// <param name="ImageDescription">0x010E ImageDescription (IFD0). 7-bit ASCII only.</param>
/// <param name="Artist">0x013B Artist (IFD0). 7-bit ASCII only.</param>
/// <param name="DateTime">0x0132 DateTime (IFD0), written as <c>YYYY:MM:DD HH:MM:SS</c> to the second; the
/// <see cref="System.DateTime.Kind"/> is not converted, EXIF's DateTime carries no zone.</param>
/// <param name="UserComment">0x9286 UserComment (Exif sub-IFD). Any text; the only field that may be non-ASCII.</param>
public sealed record ExifWriteData(
    string? Software = null,
    string? ImageDescription = null,
    string? Artist = null,
    DateTime? DateTime = null,
    string? UserComment = null);

/// <summary>
/// Builds a standalone EXIF blob: a little-endian (<c>II</c>) TIFF structure whose IFD0 holds
/// ImageDescription, Software, DateTime and Artist (in tag order), and, when there is a
/// <see cref="ExifWriteData.UserComment"/>, an ExifIFD pointer (0x8769) to a sub-IFD holding it. This is the payload
/// <see cref="ExifReader.FromTiff"/> reads, the <c>eXIf</c> chunk of a PNG, and, behind the <c>Exif\0\0</c> signature,
/// a JPEG APP1 segment (which is what <c>JpegEncodeOptions.Exif</c> does with it).
///
/// <para>
/// <b>IFD0 strings are ASCII.</b> The TIFF/EXIF ASCII type is 7-bit, and the reader's tolerance for UTF-8 there is
/// leniency towards non-conforming cameras, not something to produce. Text with a character above U+007F or an
/// embedded NUL in <c>Software</c>, <c>ImageDescription</c> or <c>Artist</c> is refused with an
/// <see cref="ArgumentException"/> rather than written as bytes another reader may take for Latin-1. Text that needs
/// Unicode goes in <c>UserComment</c>.
/// </para>
/// <para>
/// <b>UserComment's character code.</b> Its first eight bytes name the encoding (EXIF 2.3, 4.6.6). Pure 7-bit text is
/// written as <c>ASCII\0\0\0</c> followed by the bytes, which every reader understands. Anything else is written as
/// <c>UNICODE\0</c> followed by UTF-16 in the blob's byte order (little-endian, matching the <c>II</c> header), the
/// code EXIF defines for it. The character code is part of the value (<see cref="ExifMetadata.RawTags"/> shows it);
/// the type is UNDEFINED, so there is no terminator.
/// </para>
/// </summary>
public static class ExifWriter
{
    private const ushort TypeAscii = 2;
    private const ushort TypeLong = 4;
    private const ushort TypeUndefined = 7;

    private const int HeaderSize = 8;
    private const int EntrySize = 12;

    /// <summary>The character code that starts a UserComment holding 7-bit text.</summary>
    public static ReadOnlySpan<byte> AsciiCharacterCode => "ASCII\0\0\0"u8;

    /// <summary>The character code that starts a UserComment holding UTF-16 text.</summary>
    public static ReadOnlySpan<byte> UnicodeCharacterCode => "UNICODE\0"u8;

    private readonly record struct Entry(ushort Tag, ushort Type, int Count, byte[] Value);

    /// <summary>
    /// Builds the EXIF blob (TIFF header onwards, without the <c>Exif\0\0</c> signature). With nothing set the result
    /// is a valid blob with an empty IFD0.
    /// </summary>
    /// <exception cref="ArgumentException">An IFD0 string is not 7-bit ASCII, or a string holds an unpaired surrogate.</exception>
    public static byte[] Build(ExifWriteData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        // IFD0 entries must be sorted by tag; 0x8769 sorts last.
        var ifd0 = new List<Entry>(5);
        AddAscii(ifd0, ExifTag.ImageDescription, data.ImageDescription, nameof(data.ImageDescription));
        AddAscii(ifd0, ExifTag.Software, data.Software, nameof(data.Software));
        if (data.DateTime is { } dt)
            AddAscii(ifd0, ExifTag.DateTime, dt.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture), nameof(data.DateTime));
        AddAscii(ifd0, ExifTag.Artist, data.Artist, nameof(data.Artist));

        var exif = new List<Entry>(1);
        if (data.UserComment is { } comment)
        {
            var value = EncodeUserComment(comment);
            exif.Add(new Entry(ExifTag.UserComment, TypeUndefined, value.Length, value));
        }

        var ifd0Count = ifd0.Count + (exif.Count > 0 ? 1 : 0);
        var ifd0DirSize = DirectorySize(ifd0Count);
        var exifOffset = HeaderSize + ifd0DirSize + DataSize(ifd0);
        if (exif.Count > 0)
            ifd0.Add(new Entry(ExifTag.ExifIfdPointer, TypeLong, 1, UInt32Le(exifOffset)));

        var total = exif.Count > 0 ? exifOffset + DirectorySize(exif.Count) + DataSize(exif) : exifOffset;
        var blob = new byte[total];
        blob[0] = (byte)'I';
        blob[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), HeaderSize);

        WriteIfd(blob, HeaderSize, ifd0, HeaderSize + ifd0DirSize);
        if (exif.Count > 0)
            WriteIfd(blob, exifOffset, exif, exifOffset + DirectorySize(exif.Count));
        return blob;
    }

    private static void AddAscii(List<Entry> entries, ushort tag, string? text, string paramName)
    {
        if (text is null) return;
        var bytes = new byte[text.Length + 1]; // NUL-terminated, the terminator counted
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\0' or > '\x7F')
                throw new ArgumentException($"{paramName} must be 7-bit ASCII without NUL (found U+{(int)text[i]:X4} at index {i}); use UserComment for other text.", "data");
            bytes[i] = (byte)text[i];
        }
        entries.Add(new Entry(tag, TypeAscii, bytes.Length, bytes));
    }

    private static byte[] EncodeUserComment(string text)
    {
        var ascii = true;
        foreach (var c in text)
        {
            if (c > '\x7F') { ascii = false; break; }
        }

        if (ascii)
        {
            var bytes = new byte[8 + text.Length];
            AsciiCharacterCode.CopyTo(bytes);
            for (var i = 0; i < text.Length; i++) bytes[8 + i] = (byte)text[i];
            return bytes;
        }

        // Strict encoder: an unpaired surrogate would otherwise be replaced silently.
        var utf16 = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
        byte[] encoded;
        try { encoded = utf16.GetBytes(text); }
        catch (EncoderFallbackException ex) { throw new ArgumentException("UserComment holds an unpaired surrogate.", "data", ex); }
        var result = new byte[8 + encoded.Length];
        UnicodeCharacterCode.CopyTo(result);
        encoded.CopyTo(result.AsSpan(8));
        return result;
    }

    private static int DirectorySize(int entryCount) => 2 + entryCount * EntrySize + 4;

    /// <summary>Bytes the out-of-line values take, each padded to a word boundary.</summary>
    private static int DataSize(List<Entry> entries)
    {
        var size = 0;
        foreach (var e in entries)
            if (e.Value.Length > 4) size += e.Value.Length + (e.Value.Length & 1);
        return size;
    }

    private static void WriteIfd(byte[] blob, int ifdPos, List<Entry> entries, int dataPos)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(ifdPos), (ushort)entries.Count);
        var pos = ifdPos + 2;
        foreach (var e in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(pos), e.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(pos + 2), e.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(pos + 4), (uint)e.Count);
            if (e.Value.Length <= 4)
            {
                e.Value.CopyTo(blob.AsSpan(pos + 8)); // left-justified, the rest stays zero
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(pos + 8), (uint)dataPos);
                e.Value.CopyTo(blob.AsSpan(dataPos));
                dataPos += e.Value.Length + (e.Value.Length & 1);
            }
            pos += EntrySize;
        }
        // The next-IFD offset is already zero: neither IFD chains on.
    }

    private static byte[] UInt32Le(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)value);
        return b;
    }
}
