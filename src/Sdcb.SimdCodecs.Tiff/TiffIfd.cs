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

/// <summary>
/// The two things the header fixes for the whole file: byte order, and classic TIFF (TIFF 6.0 §2: 2-byte entry
/// counts, 12-byte entries, 4-byte offsets) or BigTIFF (version 43: 8-byte entry counts, 20-byte entries, 8-byte
/// offsets and value slot).
/// </summary>
internal readonly record struct TiffFormat(bool LittleEndian, bool BigTiff)
{
    /// <summary>Bytes of the entry count that starts a directory.</summary>
    public int CountSize => BigTiff ? 8 : 2;

    /// <summary>Bytes of one directory entry.</summary>
    public int EntrySize => BigTiff ? 20 : 12;

    /// <summary>Bytes of an offset: the next-IFD pointer, and an entry's value slot.</summary>
    public int OffsetSize => BigTiff ? 8 : 4;
}

/// <summary>One IFD, parsed and validated, before any pixel is touched.</summary>
internal sealed record ParsedIfd(
    int Width, int Height, int SamplesPerPixel, int BitsPerSample,
    TiffCompression Compression, TiffPhotometric Photometric, TiffSampleFormat SampleFormat,
    TiffPlanarConfig PlanarConfig, TiffPredictor Predictor, int RowsPerStrip,
    bool IsTiled, int TileWidth, int TileHeight,
    long[] SegmentOffsets, long[] SegmentByteCounts, TiffSegmentGeometry Geometry,
    float? SMin, float? SMax, byte[]? Icc, long? ExifIfdOffset, long? GpsInfoIfdOffset,
    long NextIfdOffset, bool LsbFirst = false, uint FaxOptions = 0, int Orientation = 1,
    byte[]? Xmp = null, double XResolution = 0, double YResolution = 0, int ResolutionUnit = 2)
{
    /// <summary>Bits per sample of <see cref="TiffPage.Pixels"/>: a 1/2/4-bit page is expanded to 8.</summary>
    public int OutputBitsPerSample => BitsPerSample < 8 ? 8 : BitsPerSample;
}

internal static class TiffIfdParser
{
    /// <summary>
    /// Samples per pixel a page may declare. Far above any real layout (RGB plus a few extra channels, or a
    /// multispectral stack), and low enough that a pixel's bytes are a small number.
    /// </summary>
    internal const int MaxSamplesPerPixel = 256;

    /// <summary>Entries a BigTIFF directory may declare (its count is 64-bit); a classic one holds at most 65535.</summary>
    private const long MaxEntries = 1 << 20;

    /// <summary>
    /// Parses the header at the start of <paramref name="src"/>: classic TIFF (TIFF 6.0 §2, 8 bytes) or BigTIFF (the
    /// BigTIFF specification: version 43, then the offset size, which must be 8, a reserved 0, and an 8-byte first-IFD
    /// offset). Returns the file's format and the first IFD's offset.
    /// </summary>
    public static (TiffFormat Format, long FirstIfd) ReadHeader<T>(ref T src) where T : ITiffBytes, allows ref struct
    {
        if (src.Length < 8) throw new InvalidDataException("TIFF too small for header");
        Span<byte> h = stackalloc byte[16];
        src.Read(0, h[..8]);

        bool le;
        if (h[0] == (byte)'I' && h[1] == (byte)'I') le = true;
        else if (h[0] == (byte)'M' && h[1] == (byte)'M') le = false;
        else throw new InvalidDataException($"Unknown TIFF byte order tag {h[0]:X2}{h[1]:X2}");

        switch (U16(h.Slice(2, 2), le))
        {
            case 42:
                return (new TiffFormat(le, BigTiff: false), U32(h.Slice(4, 4), le));
            case 43:
                if (U16(h.Slice(4, 2), le) != 8 || U16(h.Slice(6, 2), le) != 0)
                    throw new InvalidDataException("BigTIFF header: the offset size must be 8 and the reserved field 0");
                if (src.Length < 16) throw new InvalidDataException("BigTIFF too small for header");
                src.Read(8, h[8..]);
                var format = new TiffFormat(le, BigTiff: true);
                return (format, Offset(h.Slice(8, 8), format));
            default:
                throw new InvalidDataException("TIFF magic mismatch");
        }
    }

    /// <summary>The entry count of the directory at <paramref name="ifdOffset"/>, checked to lie inside the file with its next-IFD pointer.</summary>
    private static long EntryCount<T>(ref T src, long ifdOffset, TiffFormat f) where T : ITiffBytes, allows ref struct
    {
        if (ifdOffset <= 0 || ifdOffset > src.Length - f.CountSize) throw new InvalidDataException("IFD offset out of bounds");
        Span<byte> countBytes = stackalloc byte[8];
        countBytes = countBytes[..f.CountSize];
        src.Read(ifdOffset, countBytes);
        var count = f.BigTiff ? U64(countBytes, f.LittleEndian) : U16(countBytes, f.LittleEndian);
        // count <= MaxEntries, so the directory's extent cannot overflow a long.
        if (count > MaxEntries
            || ifdOffset + f.CountSize + ((long)count * f.EntrySize) + f.OffsetSize > src.Length)
            throw new InvalidDataException("IFD truncated");
        return (long)count;
    }

    /// <summary>The next-IFD offset of the directory at <paramref name="ifdOffset"/>, without interpreting a single tag.</summary>
    public static long ReadNextIfdOffset<T>(ref T src, long ifdOffset, TiffFormat f) where T : ITiffBytes, allows ref struct
    {
        var count = EntryCount(ref src, ifdOffset, f);
        Span<byte> next = stackalloc byte[8];
        next = next[..f.OffsetSize];
        src.Read(ifdOffset + f.CountSize + (count * f.EntrySize), next);
        return Offset(next, f);
    }

    public static ParsedIfd Parse<T>(ref T src, long ifdOffset, TiffFormat f) where T : ITiffBytes, allows ref struct
    {
        var entryCount = (int)EntryCount(ref src, ifdOffset, f);
        var entrySize = f.EntrySize;
        var fileIsLE = f.LittleEndian;
        var directory = new byte[(entryCount * entrySize) + f.OffsetSize];
        src.Read(ifdOffset + f.CountSize, directory);

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
        long[]? stripOffsets = null;
        long[]? stripByteCounts = null;
        var tileWidth = 0;
        var tileHeight = 0;
        long[]? tileOffsets = null;
        long[]? tileByteCounts = null;
        float? sMin = null;
        float? sMax = null;
        byte[]? icc = null;
        long? exifIfdOffset = null;
        long? gpsIfdOffset = null;
        var fillOrder = 1;
        uint t4Options = 0;
        uint t6Options = 0;
        var orientation = 1;
        byte[]? xmp = null;
        double xRes = 0, yRes = 0;
        var resUnit = 2;

        for (var i = 0; i < entryCount; i++)
        {
            // Classic: tag, type, 4-byte count, 4-byte value slot. BigTIFF: tag, type, 8-byte count, 8-byte slot.
            var entry = directory.AsSpan(i * entrySize, entrySize);
            var tag = U16(entry[..2], fileIsLE);
            var type = (TiffFieldType)U16(entry.Slice(2, 2), fileIsLE);
            var count = f.BigTiff ? U64(entry.Slice(4, 8), fileIsLE) : U32(entry.Slice(4, 4), fileIsLE);
            var valueSpan = entry.Slice(f.BigTiff ? 12 : 8, f.OffsetSize);

            switch (tag)
            {
                case TiffTag.ImageWidth: width = (int)Scalar(type, valueSpan, f); break;
                case TiffTag.ImageLength: height = (int)Scalar(type, valueSpan, f); break;
                case TiffTag.SamplesPerPixel: samplesPerPixel = (int)Scalar(type, valueSpan, f); break;
                case TiffTag.BitsPerSample:
                    bitsPerSample = (int)ShortArray(ref src, type, count, valueSpan, f)[0];
                    break;
                case TiffTag.Compression: compression = (TiffCompression)Scalar(type, valueSpan, f); break;
                case TiffTag.PhotometricInterp: photometric = (TiffPhotometric)Scalar(type, valueSpan, f); break;
                case TiffTag.Predictor: predictor = (TiffPredictor)Scalar(type, valueSpan, f); break;
                case TiffTag.PlanarConfig: planarConfig = (TiffPlanarConfig)Scalar(type, valueSpan, f); break;
                case TiffTag.RowsPerStrip: rowsPerStrip = (int)Scalar(type, valueSpan, f); break;
                case TiffTag.StripOffsets: stripOffsets = LongOrShortArray(ref src, type, count, valueSpan, f); break;
                case TiffTag.StripByteCounts: stripByteCounts = LongOrShortArray(ref src, type, count, valueSpan, f); break;
                case TiffTag.TileWidth: tileWidth = (int)Scalar(type, valueSpan, f); break;
                case TiffTag.TileLength: tileHeight = (int)Scalar(type, valueSpan, f); break;
                case TiffTag.TileOffsets: tileOffsets = LongOrShortArray(ref src, type, count, valueSpan, f); break;
                case TiffTag.TileByteCounts: tileByteCounts = LongOrShortArray(ref src, type, count, valueSpan, f); break;
                case TiffTag.SampleFormat:
                    sampleFormat = (TiffSampleFormat)ShortArray(ref src, type, count, valueSpan, f)[0];
                    break;
                case TiffTag.SMinSampleValue: sMin = FloatArray(ref src, type, count, valueSpan, f)[0]; break;
                case TiffTag.SMaxSampleValue: sMax = FloatArray(ref src, type, count, valueSpan, f)[0]; break;
                case TiffTag.IccProfile: icc = ByteArray(ref src, type, count, valueSpan, f); break;
                case TiffTag.ExifIfd:
                    // Sub-IFD pointer: capture the offset so a caller can pass it to
                    // Sdcb.SimdCodecs.Exif.ExifReader.FromIfd without re-walking.
                    exifIfdOffset = PointerValue(type, count, valueSpan, f);
                    break;
                case TiffTag.GpsInfoIfd: gpsIfdOffset = PointerValue(type, count, valueSpan, f); break;
                case TiffTag.FillOrder: fillOrder = (int)Scalar(type, valueSpan, f); break;
                case TiffTag.T4Options: t4Options = Scalar(type, valueSpan, f); break;
                case TiffTag.T6Options: t6Options = Scalar(type, valueSpan, f); break;
                case TiffTag.Orientation:
                    // Metadata, never a reason to refuse a page: anything but one SHORT in 1..8 means the default.
                    var o = type == TiffFieldType.Short && count == 1 ? U16(valueSpan[..2], fileIsLE) : 0;
                    orientation = o is >= 1 and <= 8 ? o : 1;
                    break;
                case TiffTag.XmpPacket:
                    // Metadata, never a reason to refuse a page: an unreadable packet is simply absent.
                    try { xmp = ByteArray(ref src, type, count, valueSpan, f); }
                    catch (Exception e) when (e is InvalidDataException or NotSupportedException) { xmp = null; }
                    break;
                case TiffTag.XResolution:
                    xRes = LenientRational(ref src, type, count, valueSpan, f);
                    break;
                case TiffTag.YResolution:
                    yRes = LenientRational(ref src, type, count, valueSpan, f);
                    break;
                case TiffTag.ResolutionUnit:
                    // Anything but one SHORT in 1..3 means the default (inch).
                    var ru = type == TiffFieldType.Short && count == 1 ? U16(valueSpan[..2], fileIsLE) : 0;
                    resUnit = ru is >= 1 and <= 3 ? ru : 2;
                    break;
                default:
                    // Unrecognised tag: TIFF spec says skip unknown tags.
                    break;
            }
        }

        var nextIfdOffset = Offset(directory.AsSpan(entryCount * entrySize, f.OffsetSize), f);

        // ---- Validate the layout we're willing to handle ------------------
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("ImageWidth/ImageLength missing or invalid");
        if (samplesPerPixel is < 1 or > MaxSamplesPerPixel)
            throw new InvalidDataException($"SamplesPerPixel={samplesPerPixel} is outside 1..{MaxSamplesPerPixel}");
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
        // one delivers the rows it covers), as it always has. In long: a tiny TileWidth on a wide page
        // makes the int product wrap.
        if ((isTiled || geometry.IsPlanar) && segmentOffsets.Length < geometry.TotalSegmentsLong)
            throw new InvalidDataException(isTiled
                ? $"Tiled page lists {segmentOffsets.Length} tiles, but {geometry.SegmentsAcross}x{geometry.SegmentsDown}x{geometry.PlaneCount} plane(s) are needed to cover {width}x{height}"
                : $"Planar page lists {segmentOffsets.Length} strips, but {geometry.SegmentsDown}x{geometry.PlaneCount} planes are needed");
        // 1/2/4-bit samples (bilevel and low-depth grey) are expanded to 8 bits on read; with more than
        // one sample per pixel they are rare enough that the reader refuses them instead.
        var subByte = bitsPerSample is 1 or 2 or 4;
        if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 32 && !subByte)
            throw new NotSupportedException($"BitsPerSample={bitsPerSample} not supported (expected 1/2/4/8/16/32)");
        if (subByte && (samplesPerPixel != 1 || sampleFormat != TiffSampleFormat.Uint))
            throw new NotSupportedException($"BitsPerSample={bitsPerSample} is supported for one unsigned sample per pixel only");
        if (sampleFormat is not (TiffSampleFormat.Uint or TiffSampleFormat.Int or TiffSampleFormat.IeeeFloat))
            throw new NotSupportedException($"SampleFormat={sampleFormat} not supported");
        if (compression is TiffCompression.CcittRle or TiffCompression.CcittT4 or TiffCompression.CcittT6
            && (bitsPerSample != 1 || samplesPerPixel != 1))
            throw new InvalidDataException($"Compression {compression} codes bilevel pages, but this one has {samplesPerPixel}x{bitsPerSample}-bit samples");
        if (fillOrder is not (1 or 2))
            throw new InvalidDataException($"FillOrder={fillOrder} is not 1 or 2");

        // A predictor is a transform of the SAMPLES before a lossless byte coder. TIFF 6.0 section 14 defines it
        // for LZW and the Adobe Deflate technical note for Deflate; it is also honoured for uncompressed and
        // PackBits data, which are byte-transparent, so a writer that differenced before them meant it. For the
        // codings that never see samples as bytes -- CCITT fax (bilevel runs) and JPEG (DCT coefficients) -- the
        // tag has no meaning: it is ignored, as are whatever values a writer left in it (seen in the wild:
        // Predictor=24016 on a JPEG page).
        if (compression is not (TiffCompression.Uncompressed or TiffCompression.Lzw or TiffCompression.Deflate
            or TiffCompression.ZlibPkzip or TiffCompression.PackBits))
            predictor = TiffPredictor.None;

        // Refuse an unhandled predictor rather than decoding past it. An un-inverted predictor
        // yields a full-size, entirely plausible buffer of WRONG pixels -- no exception, no
        // short read, correct dimensions -- so staying quiet here is indistinguishable from
        // success to every caller downstream. The same goes for a predictor paired with samples it
        // is not defined on.
        switch (predictor)
        {
            case TiffPredictor.None:
                break;
            case TiffPredictor.HorizontalDifferencing when !subByte:
                break;
            case TiffPredictor.FloatingPoint when sampleFormat == TiffSampleFormat.IeeeFloat && bitsPerSample is 16 or 32:
                break;
            default:
                throw new NotSupportedException(
                    $"Predictor={predictor} not supported for {bitsPerSample}-bit {sampleFormat} samples");
        }

        // Every buffer a reader sizes from this page, bounded once, in long arithmetic: width, height, tile size
        // and samples per pixel are all file values, and their int products wrap. A decoded segment and a band of
        // whole rows (what a tiled or planar page is assembled in) must each fit one .NET array.
        var outBytesPerSample = (bitsPerSample < 8 ? 8 : bitsPerSample) / 8;
        var bytesPerPixel = (long)samplesPerPixel * outBytesPerSample;
        var segmentBytesPerPixel = geometry.IsPlanar ? outBytesPerSample : bytesPerPixel;
        var segmentBytes = (isTiled ? tileWidth : (long)width) * segmentBytesPerPixel * geometry.SegmentHeight;
        var bandBytes = width * bytesPerPixel * geometry.SegmentHeight;
        if (segmentBytes > Array.MaxLength || bandBytes > Array.MaxLength)
            throw new InvalidDataException(
                $"A {(isTiled ? "tile" : "strip")} of this {width}x{height} page decodes to more than {Array.MaxLength} bytes");

        return new ParsedIfd(width, height, samplesPerPixel, bitsPerSample, compression, photometric,
            sampleFormat, planarConfig, predictor, rowsPerStrip, isTiled, tileWidth, tileHeight,
            segmentOffsets, segmentByteCounts, geometry, sMin, sMax, icc, exifIfdOffset, gpsIfdOffset,
            nextIfdOffset, LsbFirst: fillOrder == 2,
            FaxOptions: compression == TiffCompression.CcittT6 ? t6Options : t4Options, Orientation: orientation,
            Xmp: xmp, XResolution: xRes, YResolution: yRes, ResolutionUnit: resUnit);
    }

    public static ushort U16(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);

    public static uint U32(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);

    public static ulong U64(ReadOnlySpan<byte> b, bool le) =>
        le ? BinaryPrimitives.ReadUInt64LittleEndian(b) : BinaryPrimitives.ReadUInt64BigEndian(b);

    /// <summary>A file offset (4 bytes, or 8 in BigTIFF), refused when it cannot address a byte of any file.</summary>
    public static long Offset(ReadOnlySpan<byte> b, TiffFormat f)
    {
        var value = f.BigTiff ? U64(b, f.LittleEndian) : U32(b, f.LittleEndian);
        if (value > long.MaxValue) throw new InvalidDataException("Offset out of range");
        return (long)value;
    }

    /// <summary>An IFD pointer (tag 34665, 34853): one LONG / IFD, or in BigTIFF also LONG8 / IFD8. Null when it is not one.</summary>
    private static long? PointerValue(TiffFieldType type, ulong count, ReadOnlySpan<byte> v, TiffFormat f)
    {
        if (count != 1) return null;
        return type switch
        {
            TiffFieldType.Long or TiffFieldType.Ifd => U32(v[..4], f.LittleEndian),
            TiffFieldType.Long8 or TiffFieldType.Ifd8 when f.BigTiff && U64(v[..8], f.LittleEndian) <= long.MaxValue
                => (long)U64(v[..8], f.LittleEndian),
            _ => null,
        };
    }

    /// <summary>One RATIONAL as a non-negative double, or 0 when the tag is not a usable RATIONAL (resolution is metadata: never a reason to refuse a page).</summary>
    private static double LenientRational<T>(ref T src, TiffFieldType type, ulong count, ReadOnlySpan<byte> valueSpan, TiffFormat f)
        where T : ITiffBytes, allows ref struct
    {
        if (type != TiffFieldType.Rational || count < 1) return 0;
        try
        {
            var data = Value(ref src, 1, 8, valueSpan, f);
            var num = U32(data.AsSpan(0, 4), f.LittleEndian);
            var den = U32(data.AsSpan(4, 4), f.LittleEndian);
            return den == 0 ? 0 : (double)num / den;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    /// <summary>The first value of a SHORT / LONG (or BigTIFF LONG8) tag, refused past <see cref="uint.MaxValue"/>.</summary>
    private static uint Scalar(TiffFieldType type, ReadOnlySpan<byte> v, TiffFormat f) => type switch
    {
        TiffFieldType.Byte => v[0],
        TiffFieldType.Short => U16(v[..2], f.LittleEndian),
        TiffFieldType.Long => U32(v[..4], f.LittleEndian),
        TiffFieldType.Long8 when f.BigTiff => U64(v[..8], f.LittleEndian) <= uint.MaxValue
            ? (uint)U64(v[..8], f.LittleEndian)
            : throw new InvalidDataException("A LONG8 value is out of range for its tag"),
        _ => throw new NotSupportedException($"Scalar tag has unexpected type {type}"),
    };

    /// <summary>
    /// The raw bytes of a tag's value: inline when it fits the entry's value slot (4 bytes, 8 in BigTIFF), else
    /// fetched from the offset it holds. The declared size is attacker-chosen, so it is checked against what the file
    /// can actually hold BEFORE anything is allocated.
    /// </summary>
    private static byte[] Value<T>(ref T src, ulong count, int elemSize, ReadOnlySpan<byte> valueSpan, TiffFormat f)
        where T : ITiffBytes, allows ref struct
    {
        // count <= Length (or the slot size) once this passes, so the product below cannot overflow.
        if (count > (ulong)Math.Max(src.Length, f.OffsetSize))
            throw new InvalidDataException("Tag value extends past the end of the file");
        var total = (long)count * elemSize;
        if (total <= f.OffsetSize) return valueSpan[..(int)total].ToArray();
        var offset = Offset(valueSpan, f);
        if (total > src.Length || offset > src.Length - total)
            throw new InvalidDataException("Tag value extends past the end of the file");
        var buffer = new byte[total];
        src.Read(offset, buffer);
        return buffer;
    }

    private static ushort[] ShortArray<T>(ref T src, TiffFieldType type, ulong count, ReadOnlySpan<byte> valueSpan, TiffFormat f)
        where T : ITiffBytes, allows ref struct
    {
        if (type != TiffFieldType.Short) throw new NotSupportedException($"Expected SHORT, got {type}");
        if (count == 0) throw new InvalidDataException("Empty SHORT array");
        var data = Value(ref src, count, 2, valueSpan, f);
        var result = new ushort[data.Length / 2];
        for (var i = 0; i < result.Length; i++) result[i] = U16(data.AsSpan(i * 2, 2), f.LittleEndian);
        return result;
    }

    /// <summary>A SHORT / LONG array (StripOffsets and the like), or in BigTIFF also LONG8, as longs.</summary>
    private static long[] LongOrShortArray<T>(ref T src, TiffFieldType type, ulong count, ReadOnlySpan<byte> valueSpan, TiffFormat f)
        where T : ITiffBytes, allows ref struct
    {
        var elemSize = type switch
        {
            TiffFieldType.Short => 2,
            TiffFieldType.Long => 4,
            TiffFieldType.Long8 when f.BigTiff => 8,
            _ => throw new NotSupportedException($"Expected SHORT/LONG{(f.BigTiff ? "/LONG8" : "")}, got {type}"),
        };
        var data = Value(ref src, count, elemSize, valueSpan, f);
        var le = f.LittleEndian;
        var result = new long[data.Length / elemSize];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = elemSize switch
            {
                2 => U16(data.AsSpan(i * 2, 2), le),
                4 => U32(data.AsSpan(i * 4, 4), le),
                _ => U64(data.AsSpan(i * 8, 8), le) <= long.MaxValue
                    ? (long)U64(data.AsSpan(i * 8, 8), le)
                    : throw new InvalidDataException("A LONG8 offset or count is out of range"),
            };
        }
        return result;
    }

    private static float[] FloatArray<T>(ref T src, TiffFieldType type, ulong count, ReadOnlySpan<byte> valueSpan, TiffFormat f)
        where T : ITiffBytes, allows ref struct
    {
        if (type != TiffFieldType.Float) throw new NotSupportedException($"Expected FLOAT, got {type}");
        if (count == 0) throw new InvalidDataException("Empty FLOAT array");
        var data = Value(ref src, count, 4, valueSpan, f);
        var result = new float[data.Length / 4];
        for (var i = 0; i < result.Length; i++)
            result[i] = BitConverter.Int32BitsToSingle((int)U32(data.AsSpan(i * 4, 4), f.LittleEndian));
        return result;
    }

    private static byte[] ByteArray<T>(ref T src, TiffFieldType type, ulong count, ReadOnlySpan<byte> valueSpan, TiffFormat f)
        where T : ITiffBytes, allows ref struct
    {
        if (type != TiffFieldType.Undefined && type != TiffFieldType.Byte)
            throw new NotSupportedException($"Expected UNDEFINED/BYTE, got {type}");
        return Value(ref src, count, 1, valueSpan, f);
    }
}