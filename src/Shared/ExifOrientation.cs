using System;
using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;

// Compiled into each package that uses it (see src/Shared/MqDecoder.cs for why it is shared as source),
// under that package's namespace so the copies are distinct types.
#if SIMDCODECS_JPEG
namespace Sdcb.SimdCodecs.Jpeg;
#elif SIMDCODECS_PNG
namespace Sdcb.SimdCodecs.Png;
#elif SIMDCODECS_WEBP
namespace Sdcb.SimdCodecs.Webp;
#elif SIMDCODECS_HEIF
namespace Sdcb.SimdCodecs.Heif;
#elif SIMDCODECS_EXIF
namespace Sdcb.SimdCodecs.Exif;
#elif SIMDCODECS_ABSTRACTIONS
namespace Sdcb.SimdCodecs.Abstractions;
#elif SIMDCODECS_FACADE
namespace Sdcb.SimdCodecs;
#else
#error ExifOrientation.cs is shared source for Sdcb.SimdCodecs.Abstractions, Sdcb.SimdCodecs, Sdcb.SimdCodecs.Jpeg, Sdcb.SimdCodecs.Png, Sdcb.SimdCodecs.Webp, Sdcb.SimdCodecs.Heif and Sdcb.SimdCodecs.Exif only.
#endif

/// <summary>
/// Reads the Orientation tag (0x0112) from an EXIF payload — the TIFF-structured blob a JPEG carries
/// after APP1's <c>Exif\0\0</c>, a PNG in <c>eXIf</c> and a WebP in <c>EXIF</c> — for
/// <see cref="ImageInfo.Orientation"/>. Only IFD0 is read: EXIF records the tag in the 0th IFD. The
/// full EXIF reader is <c>Sdcb.SimdCodecs.Exif</c>; this is the one tag a decoder's header read needs,
/// without depending on that package.
/// </summary>
internal static class ExifOrientation
{
    /// <summary>
    /// The orientation <paramref name="tiff"/> records, or <see cref="ImageOrientation.Normal"/> when the
    /// payload is malformed, lacks the tag, or holds a value outside 1..8 — a header read never fails
    /// on metadata. A leading <c>Exif\0\0</c>, which some writers copy into PNG and WebP from JPEG, is
    /// skipped.
    /// </summary>
    public static ImageOrientation Read(ReadOnlySpan<byte> tiff)
    {
        var at = FindValue(tiff, out var le);
        if (at < 0) return ImageOrientation.Normal;
        var value = U16(tiff[at..], le);
        return value is >= 1 and <= 8 ? (ImageOrientation)value : ImageOrientation.Normal;
    }

    /// <summary>
    /// Writes 1 (<see cref="ImageOrientation.Normal"/>) over the Orientation value in place, in the payload's own
    /// byte order, for pixels that already are display-oriented. Returns whether a byte changed: false when the
    /// payload is malformed, lacks the tag (SHORT, count 1, in IFD0) or already holds 1. Same bounds checks and
    /// leading <c>Exif\0\0</c> handling as <see cref="Read"/>.
    /// </summary>
    public static bool TryReset(Span<byte> tiff)
    {
        var at = FindValue(tiff, out var le);
        if (at < 0) return false;
        var target = tiff.Slice(at, 2);
        if (U16(target, le) == 1) return false;
        if (le) BinaryPrimitives.WriteUInt16LittleEndian(target, 1);
        else BinaryPrimitives.WriteUInt16BigEndian(target, 1);
        return true;
    }

    // The offset of the Orientation value within `tiff` (counting a leading "Exif\0\0"), or -1 when the payload is
    // malformed or the tag is absent or not SHORT with count 1.
    private static int FindValue(ReadOnlySpan<byte> payload, out bool le)
    {
        le = false;
        var skip = payload.StartsWith("Exif\0\0"u8) ? 6 : 0;
        var tiff = payload[skip..];
        // TIFF 6.0 §2: "II" or "MM", 42, then the offset of IFD0.
        if (tiff.Length < 8) return -1;
        if (tiff[0] == 'I' && tiff[1] == 'I') le = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') le = false;
        else return -1;
        if (U16(tiff[2..], le) != 42) return -1;

        var ifd = U32(tiff[4..], le);
        if (ifd < 8 || ifd > (uint)tiff.Length - 2) return -1;
        var count = U16(tiff[(int)ifd..], le);
        var entries = (int)ifd + 2;
        // An entry is 12 bytes: tag, type, count, then the value itself when it fits in 4 bytes.
        for (var i = 0; i < count && entries + (i + 1) * 12 <= tiff.Length; i++)
        {
            var entry = tiff.Slice(entries + i * 12, 12);
            if (U16(entry, le) != 0x0112) continue;
            // SHORT (3), count 1: the value sits in the first two bytes of the value field.
            if (U16(entry[2..], le) != 3 || U32(entry[4..], le) != 1) return -1;
            return skip + entries + i * 12 + 8;
        }

        return -1;
    }

    private static ushort U16(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
}
