using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// The streaming <see cref="IImageEncoderSession"/> behind <see cref="TiffImageEncoder.Create(Stream, EncodeOptions?, bool)"/>:
/// it writes the file <see cref="TiffWriter"/> writes for the same pixels and options, byte for byte, in the same order
/// (header, then each page's segments, Exif sub-IFDs and IFD), but as the rows arrive.
/// <para>
/// A strip goes to the stream as its rows come in: uncompressed rows are written as they are, deflated ones pass through
/// one <see cref="TiffWriter.DeflateChunk"/>-byte slice (the deflater's input pieces, the same ones
/// <see cref="TiffWriter.DeflateZlib"/> uses). A tiled page holds one row of tiles. The offsets and byte counts are known
/// once a page's segments are written, so its IFD follows them; the previous IFD's NextIFD pointer and, at
/// <see cref="IImageEncoderSession.Commit"/>, the header's first-IFD offset are patched by seeking, which is why the
/// stream must be seekable. Offsets count from where the stream was when the session started.
/// </para>
/// </summary>
internal sealed class TiffStreamingEncoderSession : StreamingEncoderSession
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly long _origin;
    private readonly TiffEncodeOptions _options;
    private bool _headerWritten;
    private bool _faulted;
    private long _firstIfdOffset = -1;
    private long _prevNextIfdPatchOffset = -1;

    // The current frame.
    private int _width, _height;
    private PixelFormat _format, _native;
    private TiffPageOptions _page = TiffPageOptions.Default;
    private TiffExifDirectories? _exif;
    private int _rowBytes;
    private int _rowsDone;
    private uint[] _offsets = [];
    private uint[] _byteCounts = [];
    private int _segment;
    private byte[]? _converted;

    // Strips.
    private int _rowsPerStrip;
    private int _rowInStrip;

    // Tiles: one row of tiles is collected in _band, each tile cut into _tile.
    private int _tileWidth, _tileHeight, _tilesX;
    private byte[]? _band;
    private int _bandRows;
    private byte[]? _tile;

    // The open segment's deflater and its input slice.
    private ZLibStream? _deflater;
    private byte[]? _chunk;
    private int _chunkFill;

    private TiffStreamingEncoderSession(Stream stream, StreamWriter writer, TiffEncodeOptions options, bool leaveOpen)
        : base(writer)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _options = options;
        _origin = stream.Position;
    }

    public static TiffStreamingEncoderSession Create(Stream stream, TiffEncodeOptions options, bool leaveOpen) =>
        new(stream, new StreamWriter(stream), options, leaveOpen);

    public override int MaxFrames => int.MaxValue;

    /// <summary>Where the file stands, counted from its first byte.</summary>
    private long Position => _stream.Position - _origin;

    private bool Deflate => _page.Compression is TiffCompression.Deflate or TiffCompression.ZlibPkzip;

    /// <summary>
    /// Set when a write failed partway (an I/O error, cancellation between segments, a file past 4 GiB): the stream then
    /// holds part of a band the base session does not count as written, so the session refuses to go on rather than
    /// write a file whose offsets disagree with its data.
    /// </summary>
    private void ThrowIfFaulted()
    {
        if (_faulted)
            throw new InvalidOperationException("An earlier write failed partway; the TIFF file is incomplete and the session cannot continue.");
    }

    protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
        ImageMetadata? metadata, TimeSpan? duration)
    {
        ThrowIfFaulted();
        var native = TiffImageEncoder.Native(format);
        var page = TiffImageEncoder.PageOptions(_options, native, metadata);

        // Everything that can refuse the page is checked before a byte of it is written.
        if (page.Compression is not (TiffCompression.Uncompressed or TiffCompression.Deflate or TiffCompression.ZlibPkzip))
            throw new NotSupportedException($"TiffWriter cannot encode {page.Compression}. Supported: Uncompressed, Deflate, ZlibPkzip.");
        TiffExifDirectories? exif = null;
        if (!page.Exif.IsEmpty)
        {
            exif = TiffExif.ParseBlock(page.Exif.Span)
                ?? throw new InvalidDataException("TiffPageOptions.Exif is not a TIFF structure (no II*\\0 or MM\\0* header)");
        }
        var rowBytes = width * native.BytesPerPixel;
        long segments;
        if (page.Layout == TiffLayout.Tiled)
        {
            if (page.TileWidth <= 0 || page.TileHeight <= 0)
                throw new ArgumentException($"Tiles are at least 1x1 pixel, not {page.TileWidth}x{page.TileHeight}.", "options");
            var tilesX = ((long)width + page.TileWidth - 1) / page.TileWidth;
            segments = tilesX * (((long)height + page.TileHeight - 1) / page.TileHeight);
            if ((long)page.TileHeight * rowBytes > Array.MaxLength || (long)page.TileHeight * page.TileWidth * native.BytesPerPixel > Array.MaxLength)
                throw new ArgumentException($"A row of {page.TileWidth}x{page.TileHeight} tiles {width} pixels wide does not fit in memory.", "options");
            _tileWidth = page.TileWidth;
            _tileHeight = page.TileHeight;
            _tilesX = (int)tilesX;
        }
        else
        {
            _rowsPerStrip = page.RowsPerStrip > 0 ? page.RowsPerStrip : height;
            segments = ((long)height + _rowsPerStrip - 1) / _rowsPerStrip;
        }
        if (segments > Array.MaxLength)
            throw new ArgumentException($"A {width}x{height} page has more segments than fit in memory.", "options");

        try
        {
            StartFrame(width, height, format, native, page, exif, rowBytes, segments);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void StartFrame(int width, int height, PixelFormat format, PixelFormat native, TiffPageOptions page,
        TiffExifDirectories? exif, int rowBytes, long segments)
    {
        if (!_headerWritten)
        {
            // As TiffWriter: the host's byte order, magic 42, and the first IFD's offset (patched at Commit).
            _headerWritten = true;
            var order = (byte)(BitConverter.IsLittleEndian ? 'I' : 'M');
            Span<byte> header = [order, order, 0, 0, 0, 0, 0, 0];
            MemoryMarshal.Write(header[2..], (ushort)42);
            _stream.Write(header);
        }

        (_width, _height, _format, _native, _page, _exif, _rowBytes) = (width, height, format, native, page, exif, rowBytes);
        _rowsDone = 0;
        _segment = 0;
        _rowInStrip = 0;
        _bandRows = 0;
        _offsets = new uint[segments];
        _byteCounts = new uint[segments];
        if (format != native)
            _converted = ArrayPool<byte>.Shared.Rent(rowBytes);
        if (page.Layout == TiffLayout.Tiled)
        {
            _band = ArrayPool<byte>.Shared.Rent(_tileHeight * rowBytes);
            _tile = ArrayPool<byte>.Shared.Rent(_tileHeight * _tileWidth * native.BytesPerPixel);
        }
        if (Deflate)
            _chunk ??= ArrayPool<byte>.Shared.Rent(TiffWriter.DeflateChunk);
    }

    protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
    {
        ThrowIfFaulted();
        try
        {
            Encode(rows, rowCount, stride, cancellationToken);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void Encode(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
    {
        var tiled = _page.Layout == TiffLayout.Tiled;
        var y = 0;
        while (y < rowCount)
        {
            if (_format == _native && !tiled && stride == _rowBytes)
            {
                // Packed rows in the stored format: hand the strip's share of the band over in one piece.
                if (_rowInStrip == 0)
                    BeginSegment(cancellationToken);
                var n = Math.Min(rowCount - y, _rowsPerStrip - _rowInStrip);
                AppendSegment(rows.Slice(y * stride, n * _rowBytes));
                y += n;
                AdvanceStrip(n);
                continue;
            }

            var row = rows.Slice(y * stride, _format.BytesPerPixel * _width);
            if (_converted is not null)
            {
                PixelConverter.ConvertRow(row, _format, _converted, _native, _width);
                row = _converted.AsSpan(0, _rowBytes);
            }
            if (tiled)
            {
                row.CopyTo(_band.AsSpan(_bandRows * _rowBytes));
                _bandRows++;
                _rowsDone++;
                if (_bandRows == _tileHeight || _rowsDone == _height)
                    EmitTileRow(cancellationToken);
            }
            else
            {
                if (_rowInStrip == 0)
                    BeginSegment(cancellationToken);
                AppendSegment(row);
                AdvanceStrip(1);
            }
            y++;
        }
    }

    private void AdvanceStrip(int rows)
    {
        _rowInStrip += rows;
        _rowsDone += rows;
        if (_rowInStrip == _rowsPerStrip || _rowsDone == _height)
        {
            EndSegment();
            _rowInStrip = 0;
        }
    }

    /// <summary>Cuts the collected row of tiles into tiles as <see cref="PixelSource"/> does (edges zero-padded) and
    /// writes each.</summary>
    private void EmitTileRow(CancellationToken cancellationToken)
    {
        var bpp = _native.BytesPerPixel;
        var tileRowBytes = _tileWidth * bpp;
        var tile = _tile.AsSpan(0, tileRowBytes * _tileHeight);
        var band = _band.AsSpan(0, _bandRows * _rowBytes);
        for (var tx = 0; tx < _tilesX; tx++)
        {
            var startCol = tx * _tileWidth;
            var colBytes = Math.Min(_tileWidth, _width - startCol) * bpp;
            if (colBytes < tileRowBytes || _bandRows < _tileHeight)
                tile.Clear();
            for (var r = 0; r < _bandRows; r++)
                band.Slice(r * _rowBytes + startCol * bpp, colBytes).CopyTo(tile[(r * tileRowBytes)..]);
            BeginSegment(cancellationToken);
            AppendSegment(tile);
            EndSegment();
        }
        _bandRows = 0;
    }

    private void BeginSegment(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Align();
        _offsets[_segment] = CheckedOffset(Position);
        if (Deflate)
        {
            _deflater = TiffWriter.NewDeflater(_stream);
            _chunkFill = 0;
        }
    }

    private void AppendSegment(ReadOnlySpan<byte> data)
    {
        if (_deflater is null)
        {
            _stream.Write(data);
            return;
        }
        // The deflater sees the segment in whole DeflateChunk-byte slices and then the rest, exactly as DeflateZlib
        // feeds it, however the rows arrive: a full slice goes straight from the caller's span when the buffer is empty,
        // anything shorter waits in _chunk.
        var chunk = _chunk.AsSpan(0, TiffWriter.DeflateChunk);
        while (!data.IsEmpty)
        {
            if (_chunkFill == 0 && data.Length >= chunk.Length)
            {
                _deflater.Write(data[..chunk.Length]);
                data = data[chunk.Length..];
                continue;
            }
            var n = Math.Min(chunk.Length - _chunkFill, data.Length);
            data[..n].CopyTo(chunk[_chunkFill..]);
            _chunkFill += n;
            data = data[n..];
            if (_chunkFill == chunk.Length)
            {
                _deflater.Write(chunk);
                _chunkFill = 0;
            }
        }
    }

    private void EndSegment()
    {
        if (_deflater is not null)
        {
            if (_chunkFill > 0)
                _deflater.Write(_chunk.AsSpan(0, _chunkFill));
            _chunkFill = 0;
            _deflater.Dispose(); // writes the stream's tail and Adler-32
            _deflater = null;
        }
        _byteCounts[_segment] = CheckedOffset(Position - _offsets[_segment]);
        _segment++;
    }

    protected override void EndFrame(CancellationToken cancellationToken)
    {
        try
        {
            WriteIfd();
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void WriteIfd()
    {
        // As TiffWriter.AddPageAsync: the Exif block's sub-IFDs, then the page's IFD, chained to the previous one.
        long? exifIfdOffset = null, gpsIfdOffset = null;
        if (_exif is { } exif)
        {
            long? interopOffset = exif.HasExifIfd && exif.Interop.Count > 0 ? WriteSubIfd(exif.Interop, null) : null;
            if (exif.HasExifIfd)
                exifIfdOffset = WriteSubIfd(exif.Exif, interopOffset);
            if (exif.HasGpsIfd)
                gpsIfdOffset = WriteSubIfd(exif.Gps, null);
        }
        var ifd = TiffWriter.BuildIfd(_width, _height, _page.Compression, _page.Photometric, _page, _offsets, _byteCounts, _exif?.Ifd0);
        if (exifIfdOffset is { } e) ifd.SetLong(TiffTag.ExifIfd, (uint)e);
        if (gpsIfdOffset is { } g) ifd.SetLong(TiffTag.GpsInfoIfd, (uint)g);

        Align();
        var ifdOffset = Position;
        _stream.Write(ifd.Serialize(ifdOffset, out var nextIfdField));
        if (_prevNextIfdPatchOffset >= 0)
            Patch(_prevNextIfdPatchOffset, (uint)ifdOffset);
        if (_firstIfdOffset < 0)
            _firstIfdOffset = ifdOffset;
        _prevNextIfdPatchOffset = ifdOffset + nextIfdField;
        ReleaseFrame();
    }

    private long WriteSubIfd(List<TiffExifEntry> entries, long? interopOffset)
    {
        var builder = new IfdBuilder();
        foreach (var entry in entries)
            builder.Set(entry);
        if (interopOffset is { } i)
            builder.SetLong(TiffExif.InteropIfd, (uint)i);
        Align();
        var at = Position;
        _stream.Write(builder.Serialize(at, out _));
        return at;
    }

    protected override void Finish(CancellationToken cancellationToken)
    {
        ThrowIfFaulted();
        if (_firstIfdOffset < 0)
            throw new InvalidOperationException("No page was written.");
        Patch(4, (uint)_firstIfdOffset);
        _stream.Flush();
    }

    /// <summary>Pads to a word boundary, as TIFF 6.0 asks of every offset.</summary>
    private void Align()
    {
        if ((Position & 1) != 0)
            _stream.WriteByte(0);
    }

    private void Patch(long offset, uint value)
    {
        var end = _stream.Position;
        Span<byte> bytes = stackalloc byte[4];
        MemoryMarshal.Write(bytes, value);
        _stream.Seek(_origin + offset, SeekOrigin.Begin);
        _stream.Write(bytes);
        _stream.Seek(end, SeekOrigin.Begin);
    }

    private static uint CheckedOffset(long value) => value <= uint.MaxValue
        ? (uint)value
        : throw new IOException("The TIFF file would pass 4 GiB, the most a classic TIFF offset addresses.");

    private void ReleaseFrame()
    {
        Return(ref _converted);
        Return(ref _band);
        Return(ref _tile);
    }

    private static void Return(ref byte[]? array)
    {
        if (array is not null)
            ArrayPool<byte>.Shared.Return(array);
        array = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        try
        {
            // Only a segment left open by an unfinished or failed session has a deflater; its tail is of no use to a
            // truncated file, and a failure writing it must not replace the exception that ended the session.
            _deflater?.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException)
        {
        }
        finally
        {
            _deflater = null;
            ReleaseFrame();
            Return(ref _chunk);
            if (!_leaveOpen)
                _stream.Dispose();
        }
    }

    /// <summary>The base session's <see cref="StreamingEncoderSession.Output"/>: each advanced span goes straight to the
    /// stream.</summary>
    private sealed class StreamWriter(Stream stream) : IBufferWriter<byte>
    {
        private byte[] _buffer = [];

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length);
            stream.Write(_buffer, 0, count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var size = Math.Max(sizeHint, 1);
            if (_buffer.Length < size)
                _buffer = new byte[Math.Max(size, 256)];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
