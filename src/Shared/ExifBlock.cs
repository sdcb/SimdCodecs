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
#else
#error ExifBlock.cs is shared source for Sdcb.SimdCodecs.Jpeg, Sdcb.SimdCodecs.Png, Sdcb.SimdCodecs.Webp and Sdcb.SimdCodecs.Heif only.
#endif

/// <summary>
/// Reads the little that the codec packages need from an Exif block (TIFF 6.0 structure, from its header): IFD0's
/// resolution tags and the IFD1 thumbnail's location. Every read is bounds-checked against the block; malformed
/// data reads as "absent", never throws. Full Exif parsing is the <c>Sdcb.SimdCodecs.Exif</c> package's job.
/// </summary>
internal static class ExifBlock
{
    private const ushort XResolution = 0x011A;
    private const ushort YResolution = 0x011B;
    private const ushort ResolutionUnitTag = 0x0128;
    private const ushort JpegInterchangeFormat = 0x0201;
    private const ushort JpegInterchangeFormatLength = 0x0202;
    private const int MaxEntries = 4096; // more than any real IFD; bounds a hostile count

    /// <summary>The block without a leading <c>Exif\0\0</c> (which some writers copy into PNG and WebP from JPEG).</summary>
    public static ReadOnlySpan<byte> StripPrefix(ReadOnlySpan<byte> exif) =>
        exif.StartsWith("Exif\0\0"u8) ? exif[6..] : exif;

    /// <summary>IFD0's XResolution / YResolution / ResolutionUnit; null when absent or malformed.</summary>
    public static Resolution? ReadResolution(ReadOnlySpan<byte> exif)
    {
        var tiff = StripPrefix(exif);
        if (!TryHeader(tiff, out var le, out var ifd0))
            return null;
        double? x = null, y = null;
        var unit = 2; // TIFF default: inch
        foreach (var (tag, type, count, value) in Entries(tiff, le, ifd0))
        {
            switch (tag)
            {
                case XResolution: x = Rational(tiff, le, type, count, value); break;
                case YResolution: y = Rational(tiff, le, type, count, value); break;
                case ResolutionUnitTag when type == 3 && count == 1: unit = (int)(value & 0xFFFF); break;
            }
        }
        if (x is not { } rx || rx <= 0)
            return null;
        var ry = y is { } v && v > 0 ? v : rx;
        return unit switch
        {
            1 => new Resolution(rx, ry, ResolutionUnit.AspectRatio),
            3 => new Resolution(rx, ry, ResolutionUnit.PerCentimeter),
            _ => new Resolution(rx, ry, ResolutionUnit.PerInch),
        };
    }

    /// <summary>
    /// The IFD1 thumbnail's JPEG stream (JPEGInterchangeFormat / Length, offsets relative to the TIFF header), when
    /// it lies inside the block and starts with SOI.
    /// </summary>
    public static bool TryGetThumbnail(ReadOnlySpan<byte> exif, out Range thumbnail)
    {
        thumbnail = default;
        var prefix = exif.StartsWith("Exif\0\0"u8) ? 6 : 0;
        var tiff = exif[prefix..];
        if (!TryHeader(tiff, out var le, out var ifd0) || !TryNextIfd(tiff, le, ifd0, out var ifd1) || ifd1 == 0)
            return false;
        uint offset = 0, length = 0;
        foreach (var (tag, type, count, value) in Entries(tiff, le, ifd1))
        {
            if (count != 1 || type is not (3 or 4))
                continue;
            if (tag == JpegInterchangeFormat) offset = value;
            else if (tag == JpegInterchangeFormatLength) length = value;
        }
        if (offset == 0 || length < 4 || offset > (uint)tiff.Length || length > (uint)tiff.Length - offset)
            return false;
        if (tiff[(int)offset] != 0xFF || tiff[(int)offset + 1] != 0xD8)
            return false;
        thumbnail = (prefix + (int)offset)..(prefix + (int)(offset + length));
        return true;
    }

    private static bool TryHeader(ReadOnlySpan<byte> tiff, out bool le, out uint ifd0)
    {
        le = false;
        ifd0 = 0;
        if (tiff.Length < 8)
            return false;
        if (tiff[0] == 'I' && tiff[1] == 'I') le = true;
        else if (!(tiff[0] == 'M' && tiff[1] == 'M')) return false;
        if (U16(tiff[2..], le) != 42)
            return false;
        ifd0 = U32(tiff[4..], le);
        return ifd0 >= 8 && ifd0 <= (uint)tiff.Length - 2;
    }

    private static bool TryNextIfd(ReadOnlySpan<byte> tiff, bool le, uint ifd, out uint next)
    {
        next = 0;
        var count = U16(tiff[(int)ifd..], le);
        var at = (long)ifd + 2 + count * 12L;
        if (count > MaxEntries || at + 4 > tiff.Length)
            return false;
        next = U32(tiff[(int)at..], le);
        return next == 0 || (next >= 8 && next <= (uint)tiff.Length - 2 && next != ifd);
    }

    // The entries of the IFD at `ifd` that lie inside the block: (tag, type, count, value-or-offset field).
    private static EntryEnumerable Entries(ReadOnlySpan<byte> tiff, bool le, uint ifd) => new(tiff, le, ifd);

    private static double? Rational(ReadOnlySpan<byte> tiff, bool le, ushort type, uint count, uint offset)
    {
        // RATIONAL (5): two LONGs, always out of line (8 bytes > the 4-byte value field).
        if (type != 5 || count != 1 || offset > (uint)tiff.Length - 8)
            return null;
        var num = U32(tiff[(int)offset..], le);
        var den = U32(tiff[((int)offset + 4)..], le);
        return den == 0 ? null : (double)num / den;
    }

    private static ushort U16(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

    private static uint U32(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);

    internal readonly ref struct EntryEnumerable(ReadOnlySpan<byte> tiff, bool le, uint ifd)
    {
        private readonly ReadOnlySpan<byte> _tiff = tiff;

        public EntryEnumerator GetEnumerator() => new(_tiff, le, ifd);
    }

    internal ref struct EntryEnumerator
    {
        private readonly ReadOnlySpan<byte> _tiff;
        private readonly bool _le;
        private readonly int _start;
        private readonly int _count;
        private int _i;

        public EntryEnumerator(ReadOnlySpan<byte> tiff, bool le, uint ifd)
        {
            _tiff = tiff;
            _le = le;
            _start = (int)ifd + 2;
            // Only the entries that lie wholly inside the block, at most MaxEntries.
            var declared = Math.Min(U16(tiff[(int)ifd..], le), (ushort)MaxEntries);
            _count = Math.Max(0, Math.Min(declared, (tiff.Length - _start) / 12));
            _i = -1;
        }

        public bool MoveNext() => ++_i < _count;

        public (ushort Tag, ushort Type, uint Count, uint Value) Current
        {
            get
            {
                var e = _tiff.Slice(_start + _i * 12, 12);
                var type = U16(e[2..], _le);
                // A SHORT value sits in the first two bytes of the field, whatever the byte order.
                var value = type == 3 ? U16(e[8..], _le) : U32(e[8..], _le);
                return (U16(e, _le), type, U32(e[4..], _le), value);
            }
        }
    }
}
