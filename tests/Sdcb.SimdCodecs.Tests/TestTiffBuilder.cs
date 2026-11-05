using System.Buffers.Binary;
using Sdcb.SimdCodecs.Tiff;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Hand-assembles TIFF files from tags and coded segments, independently of <see cref="TiffWriter"/>: classic TIFF
/// (TIFF 6.0 §2) or BigTIFF (version 43, 8-byte offsets, 20-byte entries), either byte order, any number of pages.
/// Values that fit the entry's value slot (4 bytes, or 8 in BigTIFF) are stored there, as the spec requires;
/// everything else goes after the segments, word-aligned. Entries are written sorted by tag.
/// </summary>
internal sealed class TestTiffBuilder(bool littleEndian = true, bool bigTiff = false)
{
    // BigTIFF's LONG8 and TIFF Technical Note 1's IFD field types.
    internal const TiffFieldType Long8Type = (TiffFieldType)16;
    internal const TiffFieldType IfdType = (TiffFieldType)13;

    private readonly List<Page> _pages = [];

    public bool LittleEndian { get; } = littleEndian;

    public bool BigTiff { get; } = bigTiff;

    /// <summary>A tag of one of the numeric types, with its values; or raw bytes (ASCII, UNDEFINED, BYTE).</summary>
    internal sealed record Entry(ushort Tag, TiffFieldType Type, ulong[] Values, byte[]? Raw = null);

    private sealed record Page(List<Entry> Entries, byte[][] Segments, bool Tiled);

    /// <summary>Adds a page whose segments become StripOffsets / StripByteCounts (or the tile pair when <paramref name="tiled"/>).</summary>
    public TestTiffBuilder AddPage(IEnumerable<Entry> entries, byte[][] segments, bool tiled = false)
    {
        _pages.Add(new Page([.. entries], segments, tiled));
        return this;
    }

    public static Entry Short(ushort tag, params int[] values) => new(tag, TiffFieldType.Short, [.. values.Select(v => (ulong)v)]);

    public static Entry Long(ushort tag, params long[] values) => new(tag, TiffFieldType.Long, [.. values.Select(v => (ulong)v)]);

    public static Entry Rational(ushort tag, params (uint Num, uint Den)[] values) =>
        new(tag, TiffFieldType.Rational, [.. values.Select(v => ((ulong)v.Den << 32) | v.Num)]);

    public static Entry Bytes(ushort tag, TiffFieldType type, byte[] raw) => new(tag, type, [], raw);

    /// <summary>The usual page tags: size, samples, bits, compression, photometric, rows per strip (0 leaves it out).</summary>
    public static List<Entry> Basic(int width, int height, int spp, int bits, TiffCompression compression,
        TiffPhotometric photometric, int rowsPerStrip = 0)
    {
        var list = new List<Entry>
        {
            Long(TiffTag.ImageWidth, width),
            Long(TiffTag.ImageLength, height),
            Short(TiffTag.BitsPerSample, [.. Enumerable.Repeat(bits, spp)]),
            Short(TiffTag.Compression, (int)compression),
            Short(TiffTag.PhotometricInterp, (int)photometric),
            Short(TiffTag.SamplesPerPixel, spp),
        };
        if (rowsPerStrip > 0) list.Add(Long(TiffTag.RowsPerStrip, rowsPerStrip));
        return list;
    }

    public byte[] Build()
    {
        var ms = new MemoryStream();
        var headerSize = BigTiff ? 16 : 8;
        ms.Write(new byte[headerSize]);

        // Segments first, then each page's out-of-line values and its directory.
        var segmentOffsets = new List<long[]>();
        foreach (var page in _pages)
        {
            var offsets = new long[page.Segments.Length];
            for (var i = 0; i < page.Segments.Length; i++)
            {
                Align(ms);
                offsets[i] = ms.Position;
                ms.Write(page.Segments[i]);
            }
            segmentOffsets.Add(offsets);
        }

        var ifdOffsets = new long[_pages.Count];
        var nextPointerAt = new long[_pages.Count];
        for (var p = 0; p < _pages.Count; p++)
        {
            var page = _pages[p];
            var entries = new List<Entry>(page.Entries);
            var offType = BigTiff ? Long8Type : TiffFieldType.Long;
            entries.Add(new Entry(page.Tiled ? TiffTag.TileOffsets : TiffTag.StripOffsets, offType,
                [.. segmentOffsets[p].Select(o => (ulong)o)]));
            entries.Add(new Entry(page.Tiled ? TiffTag.TileByteCounts : TiffTag.StripByteCounts, offType,
                [.. page.Segments.Select(s => (ulong)s.Length)]));
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));

            var slot = BigTiff ? 8 : 4;
            var encoded = entries.Select(Encode).ToArray();
            var valueAt = new long[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                if (encoded[i].Length <= slot) continue;
                Align(ms);
                valueAt[i] = ms.Position;
                ms.Write(encoded[i]);
            }

            Align(ms);
            ifdOffsets[p] = ms.Position;
            var entrySize = BigTiff ? 20 : 12;
            var dir = new byte[(BigTiff ? 8 : 2) + (entries.Count * entrySize) + (BigTiff ? 8 : 4)];
            if (BigTiff) W64(dir, 0, (ulong)entries.Count); else W16(dir, 0, (ushort)entries.Count);
            var at = BigTiff ? 8 : 2;
            for (var i = 0; i < entries.Count; i++, at += entrySize)
            {
                var e = entries[i];
                W16(dir, at, e.Tag);
                W16(dir, at + 2, (ushort)e.Type);
                var count = (ulong)(e.Raw?.Length ?? e.Values.Length);
                if (BigTiff) W64(dir, at + 4, count); else W32(dir, at + 4, (uint)count);
                var valueSlot = at + (BigTiff ? 12 : 8);
                if (encoded[i].Length <= slot) encoded[i].CopyTo(dir, valueSlot);
                else if (BigTiff) W64(dir, valueSlot, (ulong)valueAt[i]);
                else W32(dir, valueSlot, (uint)valueAt[i]);
            }
            nextPointerAt[p] = ifdOffsets[p] + at;
            ms.Write(dir);
        }

        var bytes = ms.ToArray();
        bytes[0] = bytes[1] = (byte)(LittleEndian ? 'I' : 'M');
        if (BigTiff)
        {
            W16(bytes, 2, 43);
            W16(bytes, 4, 8);
            W16(bytes, 6, 0);
            W64(bytes, 8, (ulong)(ifdOffsets.Length > 0 ? ifdOffsets[0] : 0));
        }
        else
        {
            W16(bytes, 2, 42);
            W32(bytes, 4, (uint)(ifdOffsets.Length > 0 ? ifdOffsets[0] : 0));
        }
        for (var p = 0; p + 1 < _pages.Count; p++)
        {
            if (BigTiff) W64(bytes, (int)nextPointerAt[p], (ulong)ifdOffsets[p + 1]);
            else W32(bytes, (int)nextPointerAt[p], (uint)ifdOffsets[p + 1]);
        }
        return bytes;
    }

    private byte[] Encode(Entry e)
    {
        if (e.Raw is not null) return e.Raw;
        var size = e.Type switch
        {
            TiffFieldType.Byte or TiffFieldType.SByte or TiffFieldType.Undefined or TiffFieldType.Ascii => 1,
            TiffFieldType.Short or TiffFieldType.SShort => 2,
            TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float or IfdType => 4,
            _ => 8,
        };
        var bytes = new byte[e.Values.Length * size];
        for (var i = 0; i < e.Values.Length; i++)
        {
            var v = e.Values[i];
            switch (e.Type)
            {
                case TiffFieldType.Rational or TiffFieldType.SRational:
                    W32(bytes, i * 8, (uint)v);
                    W32(bytes, (i * 8) + 4, (uint)(v >> 32));
                    break;
                default:
                    switch (size)
                    {
                        case 1: bytes[i] = (byte)v; break;
                        case 2: W16(bytes, i * 2, (ushort)v); break;
                        case 4: W32(bytes, i * 4, (uint)v); break;
                        default: W64(bytes, i * 8, v); break;
                    }
                    break;
            }
        }
        return bytes;
    }

    private static void Align(MemoryStream ms)
    {
        if ((ms.Position & 1) != 0) ms.WriteByte(0);
    }

    private void W16(byte[] b, int at, ushort v)
    {
        if (LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
        else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v);
    }

    private void W32(byte[] b, int at, uint v)
    {
        if (LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
        else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
    }

    private void W64(byte[] b, int at, ulong v)
    {
        if (LittleEndian) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), v);
        else BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(at), v);
    }
}
