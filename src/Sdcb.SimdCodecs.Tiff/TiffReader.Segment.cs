using System.Buffers;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>The pixels one segment covers, clipped to the image: an edge tile's padding is not part of it.</summary>
public readonly record struct TiffSegmentRect(int X, int Y, int Width, int Height);

/// <summary>
/// How one page of a TIFF is cut into strips or tiles, read without decoding a pixel. Pass it back to
/// <see cref="TiffReader.ReadSegment(ReadOnlySpan{byte}, TiffPageLayout, int, Span{byte})"/> to read
/// segments of the SAME file without walking the IFD chain and re-parsing the directory each time.
/// </summary>
/// <remarks>
/// A segment is identified by a single index, row-major over the page's strips or tiles. For a page
/// stored as separate planes (PlanarConfig=2) one segment still means one spatial strip or tile: reading
/// it gathers that segment's entry from every plane and interleaves them, exactly as the whole-page read
/// does, so the caller never sees planes.
/// </remarks>
public sealed class TiffPageLayout
{
    private readonly TiffSegmentGeometry _geometry;
    internal readonly uint[] Offsets;
    internal readonly uint[] ByteCounts;

    internal TiffPageLayout(int pageIndex, bool fileIsLittleEndian, ParsedIfd ifd)
    {
        PageIndex = pageIndex;
        FileIsLittleEndian = fileIsLittleEndian;
        _geometry = ifd.Geometry;
        Offsets = ifd.SegmentOffsets;
        ByteCounts = ifd.SegmentByteCounts;
        Width = ifd.Width;
        Height = ifd.Height;
        SamplesPerPixel = ifd.SamplesPerPixel;
        BitsPerSample = ifd.BitsPerSample;
        SampleFormat = ifd.SampleFormat;
        Compression = ifd.Compression;
        Predictor = ifd.Predictor;
        IsTiled = ifd.IsTiled;
        TileWidth = ifd.IsTiled ? ifd.TileWidth : 0;
        TileHeight = ifd.IsTiled ? ifd.TileHeight : 0;
    }

    public int PageIndex { get; }
    public bool FileIsLittleEndian { get; }
    public int Width { get; }
    public int Height { get; }
    public int SamplesPerPixel { get; }
    public int BitsPerSample { get; }
    public TiffSampleFormat SampleFormat { get; }
    public TiffCompression Compression { get; }
    public TiffPredictor Predictor { get; }
    public bool IsTiled { get; }

    /// <summary>Rows in a full strip (the last may be shorter); 0 for a tiled page.</summary>
    public int RowsPerStrip => IsTiled ? 0 : _geometry.SegmentHeight;

    /// <summary>Tile size as declared by the file, or 0 for a stripped page.</summary>
    public int TileWidth { get; }
    public int TileHeight { get; }

    /// <summary>True when the file stores each sample in its own set of segments (PlanarConfig=Separate, more than one sample).</summary>
    public bool IsPlanar => _geometry.IsPlanar;

    /// <summary>Tiles per row of tiles; 1 for strips.</summary>
    public int SegmentsAcross => _geometry.SegmentsAcross;

    /// <summary>Strips, or rows of tiles.</summary>
    public int SegmentsDown => _geometry.SegmentsDown;

    /// <summary>Segments to choose from, <c>SegmentsAcross * SegmentsDown</c>; planes are not counted.</summary>
    public int SegmentCount => _geometry.SegmentsPerPlane;

    public int BytesPerPixel => SamplesPerPixel * (BitsPerSample / 8);

    /// <summary>The index of the strip or tile covering pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public int SegmentAt(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return (_geometry.DownIndexForRow(y) * _geometry.SegmentsAcross) + _geometry.AcrossIndexForColumn(x);
    }

    /// <summary>The rectangle of the image a segment covers, clipped to the image.</summary>
    public TiffSegmentRect SegmentRectangle(int segmentIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(segmentIndex, SegmentCount);
        var down = segmentIndex / _geometry.SegmentsAcross;
        var across = segmentIndex % _geometry.SegmentsAcross;
        var x = IsTiled ? across * TileWidth : 0;
        var w = IsTiled ? Math.Min(TileWidth, Width - x) : Width;
        return new TiffSegmentRect(x, _geometry.FirstRow(down), w, _geometry.RowCount(down));
    }

    /// <summary>Bytes <c>ReadSegment</c> writes for a segment: its clipped rectangle, tightly packed.</summary>
    public int SegmentBytes(int segmentIndex)
    {
        var r = SegmentRectangle(segmentIndex);
        return checked(r.Width * r.Height * BytesPerPixel);
    }

    internal TiffSegmentGeometry Geometry => _geometry;
}

public static partial class TiffReader
{
    /// <summary>Largest decoded segment this reader will size a buffer for; segment size comes from the file.</summary>
    private const long MaxSegmentBytes = 1L << 30;

    /// <summary>Reads how page <paramref name="pageIndex"/> is cut into segments, touching nothing but the header and the IFDs on the way.</summary>
    public static TiffPageLayout ReadLayout(ReadOnlySpan<byte> tiff, int pageIndex = 0)
    {
        var src = new SpanBytes(tiff);
        return ReadLayoutCore(ref src, pageIndex);
    }

    /// <inheritdoc cref="ReadLayout(ReadOnlySpan{byte}, int)"/>
    /// <remarks>The stream must be seekable; only the header, the directories walked and their tag arrays are read.</remarks>
    public static TiffPageLayout ReadLayout(Stream stream, int pageIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var src = new StreamBytes(stream);
        return ReadLayoutCore(ref src, pageIndex);
    }

    /// <inheritdoc cref="ReadLayout(ReadOnlySpan{byte}, int)"/>
    public static TiffPageLayout ReadLayout(string path, int pageIndex = 0)
    {
        using var file = OpenForSegments(path);
        return ReadLayout(file, pageIndex);
    }

    /// <summary>
    /// Decodes ONE strip or tile of page <paramref name="pageIndex"/> into <paramref name="destination"/>:
    /// the segment's clipped rectangle as host-order interleaved samples, tightly packed at the clipped
    /// width. Returns that rectangle. Only that segment's bytes are read (every plane's, for a planar
    /// page), and they go through the same decode, byte-order swap, predictor and interleave as
    /// <see cref="ReadInto{TSink}"/>, so the result equals the same rows and columns of the whole page.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The segment index or page index is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is smaller than the segment needs.</exception>
    public static TiffSegmentRect ReadSegment(ReadOnlySpan<byte> tiff, int pageIndex, int segmentIndex, Span<byte> destination)
    {
        var src = new SpanBytes(tiff);
        var layout = ReadLayoutCore(ref src, pageIndex);
        return ReadSegmentCore(ref src, layout, segmentIndex, destination);
    }

    /// <summary>As above, with a layout from <see cref="ReadLayout(ReadOnlySpan{byte}, int)"/> of the same file, so no directory is parsed again.</summary>
    public static TiffSegmentRect ReadSegment(ReadOnlySpan<byte> tiff, TiffPageLayout layout, int segmentIndex, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var src = new SpanBytes(tiff);
        return ReadSegmentCore(ref src, layout, segmentIndex, destination);
    }

    /// <inheritdoc cref="ReadSegment(ReadOnlySpan{byte}, int, int, Span{byte})"/>
    /// <remarks>The stream must be seekable; the file is never read whole.</remarks>
    public static TiffSegmentRect ReadSegment(Stream stream, int pageIndex, int segmentIndex, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var src = new StreamBytes(stream);
        var layout = ReadLayoutCore(ref src, pageIndex);
        return ReadSegmentCore(ref src, layout, segmentIndex, destination);
    }

    /// <inheritdoc cref="ReadSegment(ReadOnlySpan{byte}, TiffPageLayout, int, Span{byte})"/>
    public static TiffSegmentRect ReadSegment(Stream stream, TiffPageLayout layout, int segmentIndex, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(layout);
        var src = new StreamBytes(stream);
        return ReadSegmentCore(ref src, layout, segmentIndex, destination);
    }

    /// <inheritdoc cref="ReadSegment(ReadOnlySpan{byte}, int, int, Span{byte})"/>
    public static TiffSegmentRect ReadSegment(string path, int pageIndex, int segmentIndex, Span<byte> destination)
    {
        using var file = OpenForSegments(path);
        return ReadSegment(file, pageIndex, segmentIndex, destination);
    }

    /// <inheritdoc cref="ReadSegment(ReadOnlySpan{byte}, TiffPageLayout, int, Span{byte})"/>
    public static TiffSegmentRect ReadSegment(string path, TiffPageLayout layout, int segmentIndex, Span<byte> destination)
    {
        using var file = OpenForSegments(path);
        return ReadSegment(file, layout, segmentIndex, destination);
    }

    // bufferSize 1 turns the FileStream's own read-ahead off, so the bytes the OS is asked for are the bytes wanted.
    private static FileStream OpenForSegments(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);

    private static TiffPageLayout ReadLayoutCore<T>(ref T src, int pageIndex) where T : ITiffBytes, allows ref struct
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        var (fileIsLE, ifdOffset) = TiffIfdParser.ReadHeader(ref src);

        // Pages before the wanted one are walked by their next-IFD pointer alone, never interpreted.
        for (var i = 0; i < pageIndex; i++)
        {
            if (ifdOffset == 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), "The file has fewer pages");
            ifdOffset = TiffIfdParser.ReadNextIfdOffset(ref src, ifdOffset, fileIsLE);
        }
        if (ifdOffset == 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), "The file has fewer pages");

        return new TiffPageLayout(pageIndex, fileIsLE, TiffIfdParser.Parse(ref src, ifdOffset, fileIsLE));
    }

    private static TiffSegmentRect ReadSegmentCore<T>(ref T src, TiffPageLayout layout, int segmentIndex,
        Span<byte> destination) where T : ITiffBytes, allows ref struct
    {
        var rect = layout.SegmentRectangle(segmentIndex);   // throws ArgumentOutOfRangeException
        var bytesPerPixel = layout.BytesPerPixel;
        var needed = checked(rect.Width * rect.Height * bytesPerPixel);
        if (destination.Length < needed)
            throw new ArgumentException(
                $"Destination holds {destination.Length} bytes but segment {segmentIndex} ({rect.Width}x{rect.Height}) needs {needed}",
                nameof(destination));

        var geometry = layout.Geometry;
        var planes = geometry.PlaneCount;
        var bitsPerSample = layout.BitsPerSample;
        var bytesPerSample = bitsPerSample / 8;
        var segBytesPerPixel = geometry.IsPlanar ? bytesPerSample : bytesPerPixel;
        var segSamplesPerPixel = geometry.SegmentSamplesPerPixel;
        var down = segmentIndex / geometry.SegmentsAcross;
        var across = segmentIndex % geometry.SegmentsAcross;

        // A tile is stored at the tile's width; a strip at the image's. Rows past the image are padding,
        // so only the rows the rectangle holds are decoded.
        var storedWidth = layout.IsTiled ? layout.TileWidth : layout.Width;
        var storedRowBytes = (long)storedWidth * segBytesPerPixel;
        var capacityLong = storedRowBytes * rect.Height;
        if (capacityLong > MaxSegmentBytes)
            throw new InvalidDataException($"Segment would decode to {capacityLong} bytes, over the {MaxSegmentBytes} limit");
        var capacity = (int)capacityLong;

        var compression = layout.Compression;
        var needsSwap = layout.FileIsLittleEndian != BitConverter.IsLittleEndian && bitsPerSample > 8;
        var needsPredictor = layout.Predictor == TiffPredictor.HorizontalDifferencing;

        var scratch = ArrayPool<byte>.Shared.Rent(capacity);
        byte[]? raw = null;
        byte[]? inflateSource = null;
        MemoryStream? scratchStream = null;
        try
        {
            for (var plane = 0; plane < planes; plane++)
            {
                var index = geometry.SegmentIndex(plane, down, across);
                if (index >= layout.Offsets.Length)
                    throw new InvalidDataException($"Segment {index} is not listed in the page's offset array");
                var offset = layout.Offsets[index];
                var count = layout.ByteCounts[index];
                // Declared, attacker-chosen: refuse before sizing a buffer off it.
                if (count > src.Length || offset + (long)count > src.Length)
                    throw new InvalidDataException($"Segment {index} extents out of bounds");

                if (raw is null || raw.Length < count)
                {
                    if (raw is not null) ArrayPool<byte>.Shared.Return(raw);
                    raw = ArrayPool<byte>.Shared.Rent((int)count);
                }
                src.Read(offset, raw.AsSpan(0, (int)count));

                if (compression is TiffCompression.Deflate or TiffCompression.ZlibPkzip && scratchStream is null)
                {
                    inflateSource = ArrayPool<byte>.Shared.Rent((int)count);
                    scratchStream = new MemoryStream(inflateSource, 0, inflateSource.Length, writable: true,
                        publiclyVisible: true);
                }
                else if (scratchStream is not null && inflateSource!.Length < count)
                {
                    scratchStream.Dispose();
                    ArrayPool<byte>.Shared.Return(inflateSource);
                    inflateSource = ArrayPool<byte>.Shared.Rent((int)count);
                    scratchStream = new MemoryStream(inflateSource, 0, inflateSource.Length, writable: true,
                        publiclyVisible: true);
                }

                var samples = DecodeSegment(raw, 0, count, index, compression, false, scratch, inflateSource,
                    scratchStream, capacity);

                // The same normalisation ReadInto applies, at the same widths.
                if (needsSwap)
                    SwapPixelsToHostOrder(scratch.AsSpan(0, samples.Length), bitsPerSample);
                if (needsPredictor)
                    UndoHorizontalDifferencing(scratch.AsSpan(0, samples.Length), storedWidth, segSamplesPerPixel, bitsPerSample);

                PlaceRows(samples, destination, rect.Height, (int)storedRowBytes, rect.Width * bytesPerPixel, 0,
                    rect.Width, segBytesPerPixel, bytesPerPixel, bytesPerSample, plane);
            }
        }
        finally
        {
            scratchStream?.Dispose();
            if (inflateSource is not null) ArrayPool<byte>.Shared.Return(inflateSource);
            if (raw is not null) ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(scratch);
        }

        return rect;
    }
}
