using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Random access to the bytes of a TIFF, whether they sit in a span or behind a seekable stream. IFD
/// parsing is written once against this, so the whole-page read over a span and the on-demand read over
/// a stream cannot come to disagree about what a valid directory is.
/// </summary>
internal interface ITiffBytes
{
    long Length { get; }

    /// <summary>Fills <paramref name="destination"/> from <paramref name="offset"/>; throws <see cref="InvalidDataException"/> when the range is not inside the file.</summary>
    void Read(long offset, scoped Span<byte> destination);
}

internal readonly ref struct SpanBytes(ReadOnlySpan<byte> bytes) : ITiffBytes
{
    private readonly ReadOnlySpan<byte> _bytes = bytes;

    public long Length => _bytes.Length;

    public void Read(long offset, scoped Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > _bytes.Length)
            throw new InvalidDataException("TIFF data extends past the end of the file");
        _bytes.Slice((int)offset, destination.Length).CopyTo(destination);
    }
}

/// <summary>A seekable stream as TIFF bytes. Every read seeks, so only what is asked for is touched.</summary>
internal sealed class StreamBytes : ITiffBytes
{
    private readonly Stream _stream;

    public StreamBytes(Stream stream)
    {
        if (!stream.CanSeek) throw new ArgumentException("The stream must be seekable", nameof(stream));
        _stream = stream;
        Length = stream.Length;
    }

    public long Length { get; }

    public void Read(long offset, scoped Span<byte> destination)
    {
        if (offset < 0 || offset + destination.Length > Length)
            throw new InvalidDataException("TIFF data extends past the end of the file");
        _stream.Position = offset;
        var done = 0;
        while (done < destination.Length)
        {
            var n = _stream.Read(destination[done..]);
            if (n == 0) throw new InvalidDataException("TIFF stream ended early");
            done += n;
        }
    }
}

/// <summary>One IFD, parsed and validated, before any pixel is touched.</summary>
internal sealed record ParsedIfd(
    int Width, int Height, int SamplesPerPixel, int BitsPerSample,
    TiffCompression Compression, TiffPhotometric Photometric, TiffSampleFormat SampleFormat,
    TiffPlanarConfig PlanarConfig, TiffPredictor Predictor, int RowsPerStrip,
    bool IsTiled, int TileWidth, int TileHeight,
    uint[] SegmentOffsets, uint[] SegmentByteCounts, TiffSegmentGeometry Geometry,
    float? SMin, float? SMax, byte[]? Icc, int? ExifIfdOffset, int? GpsInfoIfdOffset,
    long NextIfdOffset);

internal static class TiffIfdParser
{
    private const int EntrySize = 12;

    /// <summary>Parses the 8-byte header at the start of <paramref name="src"/>; returns byte order and the first IFD's offset.</summary>
    public static (bool FileIsLE, long FirstIfd) ReadHeader<T>(ref T src) where T : ITiffBytes, allows ref struct
    {
        if (src.Length < 8) throw new InvalidDataException("TIFF too small for header");
        Span<byte> h = stackalloc byte[8];
        src.Read(0, h);

        bool fileIsLE;
        if (h[0] == (byte)'I' && h[1] == (byte)'I') fileIsLE = true;
        else if (h[0] == (byte)'M' && h[1] == (byte)'M') fileIsLE = false;
        else throw new InvalidDataException($"Unknown TIFF byte order tag {h[0]:X2}{h[1]:X2}");

        if (U16(h.Slice(2, 2), fileIsLE) != 42) throw new InvalidDataException("TIFF magic mismatch");
        return (fileIsLE, U32(h.Slice(4, 4), fileIsLE));
    }

    /// <summary>The next-IFD offset of the directory at <paramref name="ifdOffset"/>, without interpreting a single tag.</summary>
    public static long ReadNextIfdOffset<T>(ref T src, long ifdOffset, bool fileIsLE) where T : ITiffBytes, allows ref struct
    {
        Span<byte> countBytes = stackalloc byte[2];
        if (ifdOffset <= 0 || ifdOffset + 2 > src.Length) throw new InvalidDataException("IFD offset out of bounds");
        src.Read(ifdOffset, countBytes);
        var end = ifdOffset + 2 + (U16(countBytes, fileIsLE) * (long)EntrySize);
        if (end + 4 > src.Length) throw new InvalidDataException("IFD truncated");
        Span<byte> next = stackalloc byte[4];
        src.Read(end, next);
        return U32(next, fileIsLE);
    }

    public static ParsedIfd Parse<T>(ref T src, long ifdOffset, bool fileIsLE) where T : ITiffBytes, allows ref struct
    {
        if (ifdOffset <= 0 || ifdOffset + 2 > src.Length) throw new InvalidDataException("IFD offset out of bounds");
        Span<byte> countBytes = stackalloc byte[2];
        src.Read(ifdOffset, countBytes);
        var entryCount = U16(countBytes, fileIsLE);

        var directoryEnd = ifdOffset + 2 + (entryCount * (long)EntrySize);
        if (directoryEnd + 4 > src.Length) throw new InvalidDataException("IFD truncated");
        var directory = new byte[(entryCount * EntrySize) + 4];
        src.Read(ifdOffset + 2, directory);

        var width = 0;
        var height = 0;
        var samplesPerPixel = 1;
        var bitsPerSample = 1;
        var compression = TiffCompression.Uncompressed;
        var photometric = TiffPhotometric.MinIsBlack;
        var sampleFormat = TiffSampleFormat.Uint;
        var planarConfig = TiffPlanarConfig.Contig;
        var predictor = TiffPredictor.None;
        var rowsPerStrip = 0;
        uint[]? stripOffsets = null;
        uint[]? stripByteCounts = null;
        var tileWidth = 0;
        var tileHeight = 0;
        uint[]? tileOffsets = null;
        uint[]? tileByteCounts = null;
        float? sMin = null;
        float? sMax = null;
        byte[]? icc = null;
        int? exifIfdOffset = null;
        int? gpsIfdOffset = null;

        for (var i = 0; i < entryCount; i++)
        {
            var entry = directory.AsSpan(i * EntrySize, EntrySize);
            var tag = U16(entry[..2], fileIsLE);
            var type = (TiffFieldType)U16(entry.Slice(2, 2), fileIsLE);
            var count = U32(entry.Slice(4, 4), fileIsLE);
            var valueSpan = entry.Slice(8, 4);

            switch (tag)
            {
                case TiffTag.ImageWidth: width = (int)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.ImageLength: height = (int)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.SamplesPerPixel: samplesPerPixel = (int)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.BitsPerSample:
                    bitsPerSample = (int)ShortArray(ref src, type, count, valueSpan, fileIsLE)[0];
                    break;
                case TiffTag.Compression: compression = (TiffCompression)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.PhotometricInterp: photometric = (TiffPhotometric)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.Predictor: predictor = (TiffPredictor)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.PlanarConfig: planarConfig = (TiffPlanarConfig)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.RowsPerStrip: rowsPerStrip = (int)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.StripOffsets: stripOffsets = LongOrShortArray(ref src, type, count, valueSpan, fileIsLE); break;
                case TiffTag.StripByteCounts: stripByteCounts = LongOrShortArray(ref src, type, count, valueSpan, fileIsLE); break;
                case TiffTag.TileWidth: tileWidth = (int)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.TileLength: tileHeight = (int)Scalar(type, valueSpan, fileIsLE); break;
                case TiffTag.TileOffsets: tileOffsets = LongOrShortArray(ref src, type, count, valueSpan, fileIsLE); break;
                case TiffTag.TileByteCounts: tileByteCounts = LongOrShortArray(ref src, type, count, valueSpan, fileIsLE); break;
                case TiffTag.SampleFormat:
                    sampleFormat = (TiffSampleFormat)ShortArray(ref src, type, count, valueSpan, fileIsLE)[0];
                    break;
                case TiffTag.SMinSampleValue: sMin = FloatArray(ref src, type, count, valueSpan, fileIsLE)[0]; break;
                case TiffTag.SMaxSampleValue: sMax = FloatArray(ref src, type, count, valueSpan, fileIsLE)[0]; break;
                case TiffTag.IccProfile: icc = ByteArray(ref src, type, count, valueSpan, fileIsLE); break;
                case TiffTag.ExifIfd:
                    // Sub-IFD pointer: capture the offset so a caller can pass it to
                    // Sdcb.SimdCodecs.Exif.ExifReader.FromIfd without re-walking.
                    exifIfdOffset = (int)Scalar(type, valueSpan, fileIsLE);
                    break;
                case TiffTag.GpsInfoIfd: gpsIfdOffset = (int)Scalar(type, valueSpan, fileIsLE); break;
                default:
                    // Unrecognised tag: TIFF spec says skip unknown tags.
                    break;
            }
        }

        var nextIfdOffset = U32(directory.AsSpan(entryCount * EntrySize, 4), fileIsLE);

        // ---- Validate the layout we're willing to handle ------------------
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("ImageWidth/ImageLength missing or invalid");
        if (planarConfig is not (TiffPlanarConfig.Contig or TiffPlanarConfig.Separate))
            throw new NotSupportedException($"PlanarConfig={(ushort)planarConfig} not supported");

        // Which layout a page uses is decided by the tags PRESENT, never by a preference. TIFF 6.0
        // p.68 makes strips and tiles mutually exclusive, so a page carrying both is malformed and is
        // refused here rather than read as whichever kind this reader happens to look for first.
        var isTiled = tileOffsets is not null || tileByteCounts is not null || tileWidth > 0 || tileHeight > 0;
        if (isTiled && (stripOffsets is not null || stripByteCounts is not null))
            throw new InvalidDataException("Page carries both strip and tile tags; TIFF 6.0 makes the two layouts exclusive");

        var segmentOffsets = isTiled ? tileOffsets : stripOffsets;
        var segmentByteCounts = isTiled ? tileByteCounts : stripByteCounts;
        if (segmentOffsets is null || segmentByteCounts is null)
            throw new NotSupportedException(isTiled
                ? "Tiled page is missing TileOffsets or TileByteCounts"
                : "Page has neither strip nor tile offsets");
        if (segmentOffsets.Length != segmentByteCounts.Length)
            throw new InvalidDataException("Segment offset / byte-count arrays differ in length");

        if (isTiled && (tileWidth <= 0 || tileHeight <= 0))
            throw new InvalidDataException("TileWidth/TileLength missing or invalid on a tiled page");

        // Deliberately NOT checked: the spec's "multiple of 16" rule. That constrains writers; a
        // file breaking it still states its own geometry unambiguously, and refusing to read one
        // would lose data for a rule the reader does not depend on.
        var geometry = new TiffSegmentGeometry(width, height, samplesPerPixel,
            planarConfig == TiffPlanarConfig.Separate, isTiled, rowsPerStrip, tileWidth, tileHeight);

        // Tiled pages and planar strips are checked exactly; a chunky strip list stays lenient (a short
        // one delivers the rows it covers), as it always has.
        if ((isTiled || geometry.IsPlanar) && segmentOffsets.Length < geometry.TotalSegments)
            throw new InvalidDataException(isTiled
                ? $"Tiled page lists {segmentOffsets.Length} tiles, but {geometry.SegmentsAcross}x{geometry.SegmentsDown}x{geometry.PlaneCount} plane(s) are needed to cover {width}x{height}"
                : $"Planar page lists {segmentOffsets.Length} strips, but {geometry.SegmentsDown}x{geometry.PlaneCount} planes are needed");
        if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 32)
            throw new NotSupportedException($"BitsPerSample={bitsPerSample} not supported (expected 8/16/32)");
        if (sampleFormat is not (TiffSampleFormat.Uint or TiffSampleFormat.Int or TiffSampleFormat.IeeeFloat))
            throw new NotSupportedException($"SampleFormat={sampleFormat} not supported");
        // Refuse an unhandled predictor rather than decoding past it. An un-inverted predictor
        // yields a full-size, entirely plausible buffer of WRONG pixels -- no exception, no
        // short read, correct dimensions -- so staying quiet here is indistinguishable from
        // success to every caller downstream.
        if (predictor is not (TiffPredictor.None or TiffPredictor.HorizontalDifferencing))
            throw new NotSupportedException($"Predictor={predictor} not supported in this reader");

        return new ParsedIfd(width, height, samplesPerPixel, bitsPerSample, compression, photometric,
            sampleFormat, planarConfig, predictor, rowsPerStrip, isTiled, tileWidth, tileHeight,
            segmentOffsets, segmentByteCounts, geometry, sMin, sMax, icc, exifIfdOffset, gpsIfdOffset,
            nextIfdOffset);
    }

    public static ushort U16(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

    public static uint U32(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);

    private static uint Scalar(TiffFieldType type, ReadOnlySpan<byte> v, bool le) => type switch
    {
        TiffFieldType.Byte => v[0],
        TiffFieldType.Short => U16(v[..2], le),
        TiffFieldType.Long => U32(v[..4], le),
        _ => throw new NotSupportedException($"Scalar tag has unexpected type {type}"),
    };

    /// <summary>
    /// The raw bytes of a tag's value: inline when it fits the entry's 4 bytes, else fetched from the
    /// offset it holds. The declared size is attacker-chosen, so it is checked against what the file
    /// can actually hold BEFORE anything is allocated.
    /// </summary>
    private static byte[] Value<T>(ref T src, uint count, int elemSize, ReadOnlySpan<byte> valueSpan, bool le)
        where T : ITiffBytes, allows ref struct
    {
        var total = count * (long)elemSize;
        if (total <= 4) return valueSpan[..(int)total].ToArray();
        var offset = U32(valueSpan, le);
        if (total > src.Length || offset + total > src.Length)
            throw new InvalidDataException("Tag value extends past the end of the file");
        var buffer = new byte[total];
        src.Read(offset, buffer);
        return buffer;
    }

    private static ushort[] ShortArray<T>(ref T src, TiffFieldType type, uint count, ReadOnlySpan<byte> valueSpan, bool le)
        where T : ITiffBytes, allows ref struct
    {
        if (type != TiffFieldType.Short) throw new NotSupportedException($"Expected SHORT, got {type}");
        if (count == 0) throw new InvalidDataException("Empty SHORT array");
        var data = Value(ref src, count, 2, valueSpan, le);
        var result = new ushort[count];
        for (var i = 0; i < result.Length; i++) result[i] = U16(data.AsSpan(i * 2, 2), le);
        return result;
    }

    private static uint[] LongOrShortArray<T>(ref T src, TiffFieldType type, uint count, ReadOnlySpan<byte> valueSpan, bool le)
        where T : ITiffBytes, allows ref struct
    {
        var elemSize = type switch
        {
            TiffFieldType.Short => 2,
            TiffFieldType.Long => 4,
            _ => throw new NotSupportedException($"Expected SHORT/LONG, got {type}"),
        };
        var data = Value(ref src, count, elemSize, valueSpan, le);
        var result = new uint[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = elemSize == 2 ? U16(data.AsSpan(i * 2, 2), le) : U32(data.AsSpan(i * 4, 4), le);
        return result;
    }

    private static float[] FloatArray<T>(ref T src, TiffFieldType type, uint count, ReadOnlySpan<byte> valueSpan, bool le)
        where T : ITiffBytes, allows ref struct
    {
        if (type != TiffFieldType.Float) throw new NotSupportedException($"Expected FLOAT, got {type}");
        if (count == 0) throw new InvalidDataException("Empty FLOAT array");
        var data = Value(ref src, count, 4, valueSpan, le);
        var result = new float[count];
        for (var i = 0; i < result.Length; i++)
            result[i] = BitConverter.Int32BitsToSingle((int)U32(data.AsSpan(i * 4, 4), le));
        return result;
    }

    private static byte[] ByteArray<T>(ref T src, TiffFieldType type, uint count, ReadOnlySpan<byte> valueSpan, bool le)
        where T : ITiffBytes, allows ref struct
    {
        if (type != TiffFieldType.Undefined && type != TiffFieldType.Byte)
            throw new NotSupportedException($"Expected UNDEFINED/BYTE, got {type}");
        return Value(ref src, count, 1, valueSpan, le);
    }
}
