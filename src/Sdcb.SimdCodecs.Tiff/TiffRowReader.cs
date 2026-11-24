using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// <see cref="TiffImageDecoder.CreateRowReader"/>: the first page of a TIFF from a seekable stream, a band (one strip,
/// or one row of tiles) at a time.
/// <para>
/// A TIFF is read by offset, so the reader moves its <see cref="StreamInput"/> itself with <see cref="StreamInput.Seek"/>,
/// back or forward: nothing when the bytes are still buffered, otherwise a seek, so the bytes between the ranges are
/// never read and a page whose planes are stored one after another streams too. Each step asks for one range; when the input does not hold it yet, the step returns
/// <see cref="System.Buffers.OperationStatus.NeedMoreData"/> and the driver reads more, synchronously or asynchronously,
/// so neither path blocks the other. A range is never asked for past the stream's length (it is checked against
/// <see cref="StreamInput.Length"/> first), so the input never reaches its end and a stale end-of-stream flag cannot
/// meet a rewind.
/// </para>
/// <para>
/// The directory parser is the span decoder's (<see cref="TiffReader.ReadLayoutCore{T}"/>), run over the ranges read
/// so far: a read outside them stops it, the range is fetched (with a little read-ahead) and it runs again. That
/// repeats a few times for a real file (header, directory, each tag array kept out of line) and is capped for a hostile
/// one. The bands are decoded by <see cref="TiffImageDecoder.DecodeBand{T}"/> over the band's segments, the code
/// <see cref="TiffImageDecoder.TryDecodeInto"/> runs, and converted by the same <see cref="PixelConverter.ConvertRow(ReadOnlySpan{byte}, int, SampleFormat, Span{byte}, PixelFormat, int)"/>
/// call, so the rows are its rows.
/// </para>
/// </summary>
internal sealed class TiffRowReader(StreamInput input, PixelFormat format, DecodeOptions? options)
    : StreamingRowReader(input, format)
{
    /// <summary>Bytes read past what the directory parser asked for, so that neighbouring tag arrays come in one fetch.</summary>
    private const int ReadAhead = 4096;

    /// <summary>Fetches one directory may take. A real one takes a handful (one per tag array kept out of line).</summary>
    private const int MaxDirectoryFetches = 256;

    private readonly WindowBytes _windows = new();
    private int _fetches;
    private TiffPageLayout? _layout;
    private TiffColor? _plan;

    // The IFD chain walk behind ImageInfo.FrameCount, resumable between fetches.
    private HashSet<long>? _visited;
    private long _walkIfd;
    private ulong? _walkCount;  // the entry count of _walkIfd, once read (its next pointer may need another seek)
    private int _pages;

    // Rows: the band being handed out and the coded segments of the next one.
    private byte[]? _band;
    private int _bandStride;
    private int _bandRows;
    private int _bandCursor;
    private int _down;
    private readonly List<(long Offset, int Count)> _ranges = [];
    private int _rangesFetched;
    private readonly List<byte[]?> _copies = [];
    private BandBytes? _bandBytes;

    protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = 0;
        info = default;
        width = height = 0;
        var length = Input.Length;
        if (length < 0)
        {
            // Read whole and decoded as a span: a stream that cannot seek (the directory may be anywhere).
            if (!isFinal)
                return OperationStatus.NeedMoreData;
            if (!TiffImageDecoder.TryReadInfo(input, out info))
                throw new InvalidDataException("The TIFF's first page could not be read.");
            return Fallback(info, out width, out height);
        }

        _windows.Length = length;
        if (_layout is null)
        {
            while (true)
            {
                try
                {
                    var windows = _windows;
                    _layout = TiffReader.ReadLayoutCore(ref windows, 0);
                    break;
                }
                catch (MissingBytesException missing)
                {
                    if (!FetchWindow(missing.Offset, missing.Count))
                        return OperationStatus.NeedMoreData;
                }
                catch (ArgumentOutOfRangeException e) when (e.ParamName == "pageIndex")
                {
                    throw new InvalidDataException("The TIFF has no pages.", e);
                }
                catch (Exception e) when (e is NotSupportedException or ArgumentException)
                {
                    throw TiffImageDecoder.Undecodable(e);
                }
            }
            if (!TiffColor.TryCreate(_layout, out var plan))
                throw TiffImageDecoder.Unpresentable(_layout, 0);
            _plan = plan;
            _windows.Clear();
            _visited = [];
            _walkIfd = _layout.IfdOffset;
        }

        // TiffReader.CountPages one directory at a time, reading only each entry count and next pointer (the checks of
        // TiffIfdParser.ReadNextIfdOffset): a directory it cannot read ends the chain uncounted.
        var f = _layout.Format;
        while (_walkIfd != 0 && !_visited!.Contains(_walkIfd))
        {
            if (_walkIfd <= 0 || _walkIfd > length - f.CountSize)
                break;
            if (_walkCount is not { } count)
            {
                if (!Fetch(_walkIfd, f.CountSize))
                    return OperationStatus.NeedMoreData;
                var countBytes = Input.Buffered[..f.CountSize];
                count = f.BigTiff ? TiffIfdParser.U64(countBytes, f.LittleEndian) : TiffIfdParser.U16(countBytes, f.LittleEndian);
                _walkCount = count;
            }
            // count <= MaxEntries, so the directory's extent cannot overflow a long.
            if (count > TiffIfdParser.MaxEntries
                || _walkIfd + f.CountSize + ((long)count * f.EntrySize) + f.OffsetSize > length)
                break;
            var nextAt = _walkIfd + f.CountSize + ((long)count * f.EntrySize);
            if (!Fetch(nextAt, f.OffsetSize))
                return OperationStatus.NeedMoreData;
            long next;
            try
            {
                next = TiffIfdParser.Offset(Input.Buffered[..f.OffsetSize], f);
            }
            catch (InvalidDataException)
            {
                break;
            }
            _visited.Add(_walkIfd);
            _pages++;
            _walkIfd = next;
            _walkCount = null;
        }
        _walkIfd = 0;

        var layout = _layout;
        info = TiffImageDecoder.InfoOf(layout, _plan!) with { FrameCount = Math.Max(1, _pages) };
        if (_plan!.OutFormat == SampleFormat.Float32 || !IsFullImage(options))
        {
            // Float output is defined through the decoded image, and options other than the whole image are the
            // span decoder's. Decode the file whole, from its first byte.
            Input.Rewind(0);
            return Fallback(info, out width, out height);
        }

        var bandRows = Math.Min(layout.Geometry.SegmentHeight, layout.Height);
        var bandStride = (long)layout.Width * _plan.OutBytesPerPixel;
        if (bandStride * bandRows > Array.MaxLength)
            throw new InvalidDataException($"A {layout.Width}-pixel band of {bandRows} rows is larger than an array can hold.");
        _bandStride = (int)bandStride;
        _band = ArrayPool<byte>.Shared.Rent(_bandStride * bandRows);
        _bandBytes = new BandBytes(length);
        width = layout.Width;
        height = layout.Height;
        return OperationStatus.Done;
    }

    protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        Span<byte> destination, int stride, int rowCount, out int rowsWritten)
    {
        consumed = 0;
        rowsWritten = 0;
        var layout = _layout!;
        var plan = _plan!;
        while (rowsWritten < rowCount)
        {
            if (_bandCursor == _bandRows)
            {
                if (!ReadBand(layout, plan))
                    return OperationStatus.NeedMoreData;
            }
            var n = Math.Min(rowCount - rowsWritten, _bandRows - _bandCursor);
            for (var r = 0; r < n; r++)
            {
                PixelConverter.ConvertRow(_band.AsSpan((_bandCursor + r) * _bandStride, _bandStride), plan.OutChannels,
                    plan.OutFormat, destination[((rowsWritten + r) * stride)..], Format, layout.Width);
            }
            _bandCursor += n;
            rowsWritten += n;
        }
        return OperationStatus.Done;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_band is not null) ArrayPool<byte>.Shared.Return(_band);
            _band = null;
            for (var i = 0; i < _copies.Count; i++)
            {
                if (_copies[i] is { } copy) ArrayPool<byte>.Shared.Return(copy);
                _copies[i] = null;
            }
        }
        base.Dispose(disposing);
    }

    private OperationStatus Fallback(ImageInfo info, out int width, out int height)
    {
        UseBufferedFallback(TiffImageDecoder.TryDecodeInto, options);
        (width, height) = (options ?? DecodeOptions.Default).GetOutputSize(info);
        return OperationStatus.Done;
    }

    /// <summary>
    /// Fetches the coded segments of segment row <see cref="_down"/> and decodes it into the band. False when the input
    /// does not hold the next range yet (the ranges fetched so far are kept).
    /// </summary>
    private bool ReadBand(TiffPageLayout layout, TiffColor plan)
    {
        var bandBytes = _bandBytes!;
        if (_ranges.Count == 0 && _rangesFetched == 0)
            CollectRanges(layout, _down, _ranges);
        while (_rangesFetched < _ranges.Count)
        {
            var (offset, count) = _ranges[_rangesFetched];
            if (!Fetch(offset, count))
                return false;
            if (_ranges.Count == 1)
            {
                // The band's only segment: read in place, before the input moves again.
                bandBytes.Add(offset, Input.BufferedMemory[..count]);
            }
            else
            {
                while (_copies.Count <= _rangesFetched) _copies.Add(null);
                var copy = _copies[_rangesFetched];
                var span = TiffBuffers.Ensure(ref copy, count);
                _copies[_rangesFetched] = copy;
                Input.Buffered[..count].CopyTo(span);
                bandBytes.Add(offset, copy.AsMemory(0, count));
            }
            _rangesFetched++;
        }

        var rect = new PixelRect(0, 0, layout.Width, layout.Height);
        var (_, rows) = TiffImageDecoder.BandRows(layout, rect, _down);
        try
        {
            TiffImageDecoder.DecodeBand(ref bandBytes, layout, plan, rect, _down,
                _band.AsSpan(0, rows * _bandStride), _bandStride);
        }
        catch (NotSupportedException e)
        {
            throw TiffImageDecoder.Undecodable(e);
        }
        finally
        {
            // Whether it decoded or not, the band's ranges are spent: a read after a failure fetches them again
            // (an in-place range is valid only until the input moves).
            _ranges.Clear();
            _rangesFetched = 0;
            bandBytes.Clear();
        }
        _bandRows = rows;
        _bandCursor = 0;
        _down++;
        return true;
    }

    /// <summary>
    /// The coded byte ranges <see cref="TiffReader.ReadSegmentCore{T}"/> reads for segment row <paramref name="down"/>,
    /// in file order, into <paramref name="ranges"/>. A segment it refuses before reading (not listed, out of the file)
    /// is left out: it throws for it, as over a span.
    /// </summary>
    private void CollectRanges(TiffPageLayout layout, int down, List<(long Offset, int Count)> ranges)
    {
        var g = layout.Geometry;
        var length = Input.Length;
        for (var across = 0; across < g.SegmentsAcross; across++)
        {
            var index = (down * g.SegmentsAcross) + across;
            if (!layout.IsTiled && !layout.IsPlanar && index >= layout.Offsets.Length)
                continue; // a short strip list: DecodeBand zero-fills, reading nothing
            for (var plane = 0; plane < g.PlaneCount; plane++)
            {
                var segment = g.SegmentIndex(plane, down, across);
                if (segment >= layout.Offsets.Length || segment >= layout.ByteCounts.Length)
                    break;
                var offset = layout.Offsets[segment];
                var count = layout.ByteCounts[segment];
                if (count > Array.MaxLength || offset > length || count > length - offset)
                    break;
                if (count > 0)
                    ranges.Add((offset, (int)count));
            }
        }
        ranges.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
    }

    /// <summary>Fetches at least <paramref name="count"/> bytes from <paramref name="offset"/> into the directory windows.</summary>
    private bool FetchWindow(long offset, int count)
    {
        // WindowBytes refused the range before it reported it missing unless it lies inside the file.
        var want = (int)Math.Min(_windows.Length - offset, Math.Max(count, ReadAhead));
        if (!Fetch(offset, want))
            return false;
        // Each fetch reruns the parser over the directory, so a directory spread over many places is refused.
        if (++_fetches > MaxDirectoryFetches)
            throw new InvalidDataException($"A TIFF directory is spread over more than {MaxDirectoryFetches} places.");
        // The windows hold what one directory reads; overlapping read-ahead aside, never more than the file.
        if (_windows.Total + want > _windows.Length + (MaxDirectoryFetches * (long)ReadAhead))
            throw new InvalidDataException("A TIFF directory reads more than the file holds.");
        _windows.Add(offset, Input.Buffered[..want].ToArray());
        return true;
    }

    /// <summary>
    /// Moves the input to <paramref name="offset"/> and reports whether <paramref name="count"/> bytes from there are
    /// buffered; false means the driver must read more first. The range lies inside the stream.
    /// </summary>
    private bool Fetch(long offset, int count) =>
        // The stream seeks (the reader streams no other kind): a range before or after the input is reached without
        // reading what lies between.
        Input.Seek(offset, count) && Input.Buffered.Length >= count;

    /// <summary>A read outside the bytes fetched so far: what the directory parser needs next.</summary>
    private sealed class MissingBytesException(long offset, int count) : Exception
    {
        public long Offset { get; } = offset;
        public int Count { get; } = count;
    }

    /// <summary>The directory ranges fetched so far, as TIFF bytes; a read outside them throws <see cref="MissingBytesException"/>.</summary>
    private sealed class WindowBytes : ITiffBytes
    {
        private readonly List<(long Offset, byte[] Bytes)> _windows = [];

        public long Length { get; set; }

        /// <summary>Bytes held.</summary>
        public long Total { get; private set; }

        public void Add(long offset, byte[] bytes)
        {
            _windows.Add((offset, bytes));
            Total += bytes.Length;
        }

        public void Clear()
        {
            _windows.Clear();
            Total = 0;
        }

        public void Read(long offset, scoped Span<byte> destination)
        {
            // The checks of SpanBytes, so the parser fails exactly where it fails over the whole file.
            if (offset < 0 || offset + destination.Length > Length)
                throw new InvalidDataException("TIFF data extends past the end of the file");
            if (destination.Length == 0)
                return;
            foreach (var (start, bytes) in _windows)
            {
                if (offset >= start && offset + destination.Length <= start + bytes.Length)
                {
                    bytes.AsSpan((int)(offset - start), destination.Length).CopyTo(destination);
                    return;
                }
            }
            throw new MissingBytesException(offset, destination.Length);
        }

        public ReadOnlySpan<byte> View(long offset, int length, ref byte[]? buffer)
        {
            var span = TiffBuffers.Ensure(ref buffer, length);
            Read(offset, span);
            return span;
        }
    }

    /// <summary>The coded segments of one band, as TIFF bytes.</summary>
    private sealed class BandBytes(long length) : ITiffBytes
    {
        private readonly List<(long Offset, ReadOnlyMemory<byte> Bytes)> _ranges = [];

        public long Length { get; } = length;

        public void Add(long offset, ReadOnlyMemory<byte> bytes) => _ranges.Add((offset, bytes));

        public void Clear() => _ranges.Clear();

        public void Read(long offset, scoped Span<byte> destination)
        {
            if (offset < 0 || offset + destination.Length > Length)
                throw new InvalidDataException("TIFF data extends past the end of the file");
            if (destination.Length == 0)
                return;
            foreach (var (start, bytes) in _ranges)
            {
                if (offset >= start && offset + destination.Length <= start + bytes.Length)
                {
                    bytes.Span.Slice((int)(offset - start), destination.Length).CopyTo(destination);
                    return;
                }
            }
            throw new InvalidOperationException($"Segment bytes at {offset} were not fetched for this band.");
        }

        public ReadOnlySpan<byte> View(long offset, int length, ref byte[]? buffer)
        {
            var span = TiffBuffers.Ensure(ref buffer, length);
            Read(offset, span);
            return span;
        }
    }
}
