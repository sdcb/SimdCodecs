using System.Buffers;
using System.IO.Compression;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// The streaming <see cref="PngImageEncoder"/> session: one still frame, written as its rows arrive. The signature, IHDR
/// and every chunk before the image data go out at <c>AddFrame</c>; the rows are filtered and deflated a band at a time
/// and leave as complete <c>IDAT</c> chunks of <see cref="IdatChunkBytes"/>; the trailing chunks and IEND at
/// <c>Commit</c>. Memory is a band of rows, never the image.
/// <para>
/// <b>Bytes.</b> With <see cref="PngWriteOptions.ParallelFragments"/> at 1 (the default) the zlib stream is exactly the
/// one <see cref="PngWriter"/> writes, since the row filter and the deflate calls see the same rows in the same order;
/// only its packaging differs: <see cref="PngWriter"/> writes one <c>IDAT</c>, whose length it back-patches, and this
/// session one per <see cref="IdatChunkBytes"/> of zlib stream (12 bytes each). A file whose zlib stream fits one chunk is
/// the buffered file byte for byte. With parallel fragments each band is one of <see cref="PngWriter"/>'s fragments, so
/// the zlib stream is again the buffered one, as long as a fragment holds at most
/// <see cref="MaxFragmentPayloadBytes"/> of filtered rows; a larger image is cut into bands of that size instead, more
/// fragments than <see cref="PngWriter"/> makes, so its bytes differ (the pixels never do). At most
/// <c>min(ParallelFragments, ProcessorCount)</c> bands are compressed at once, which bounds the memory.
/// </para>
/// <para>
/// Band boundaries are fixed by the image's size and the options, never by how the caller splits its
/// <c>WriteRows</c> calls, so the file is the same whether the rows come one at a time or all at once.
/// </para>
/// <para>
/// A <c>WriteRows</c> whose token is already cancelled consumes nothing. One cancelled while it runs (checked at each
/// band) or failing part-way has written part of the image, and the session refuses everything after it but
/// <c>Dispose</c>.
/// </para>
/// </summary>
internal sealed class PngStreamingSession(IBufferWriter<byte> output, PngWriteOptions options) : StreamingEncoderSession(output)
{
    /// <summary>The zlib bytes each <c>IDAT</c> carries (the last one carries the rest).</summary>
    internal const int IdatChunkBytes = 64 * 1024;

    /// <summary>Filtered-row bytes per band in the single-stream path: what the rows are gathered into before filtering.</summary>
    private const int SerialBandBytes = 128 * 1024;

    /// <summary>
    /// The most filtered-row bytes an independently deflated band holds in the parallel path: a band is
    /// <see cref="PngWriter"/>'s fragment when that is smaller, so the two write the same zlib stream.
    /// </summary>
    internal const int MaxFragmentPayloadBytes = 2 * 1024 * 1024;

    private readonly PngWriteOptions _given = options;
    private PngWriteOptions _options = options; // the frame's: AddFrame merges its metadata into _given
    private PngEncodePlan? _plan;
    private PixelFormat _format, _native;
    private int _width, _height, _sourceRowBytes, _payloadRowBytes;
    private IdatWriter? _idat;
    private ZLibStream? _zlib;           // the single-stream path
    private Fragments? _parallel;         // the fragmented path
    private Band? _band;                 // the rows being gathered
    private int _bandFirstRow;           // image row of the band's first row
    private bool _faulted;

    public override int MaxFrames => 1;

    protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
        ImageMetadata? metadata, TimeSpan? duration)
    {
        // Everything that can refuse the frame runs before the first byte reaches the output, and nothing of the session
        // changes until it has: a refused AddFrame leaves the session as it was.
        var o = PngImageEncoder.WithFrame(_given, metadata, color);
        PngWriter.ValidateText(o);
        if (!Enum.IsDefined(o.CompressionLevel))
            throw new ArgumentOutOfRangeException(nameof(PngWriteOptions.CompressionLevel), o.CompressionLevel, "Not a CompressionLevel.");
        var native = PngImageEncoder.Native(format);
        var sampleBytes = native.Sample == SampleFormat.UInt16 ? 2 : 1;
        var srcChannels = native.Layout switch
        {
            ChannelLayout.Rgba => 4,
            ChannelLayout.Rgb => 3,
            ChannelLayout.GrayAlpha => 2,
            _ => 1,
        };
        var dstChannels = o.DiscardAlpha && srcChannels is 2 or 4 ? srcChannels - 1 : srcChannels;
        var plan = PngEncodePlan.Direct(dstChannels, srcChannels, sampleBytes, o.TransparentColor);
        var sourceRowBytes = plan.SourceRowBytes(width);
        var payloadRowBytes = checked(plan.RowBytes(width) + 1);
        using var head = new MemoryStream();
        head.Write(PngWriter.Signature);
        PngWriter.WritePreImageChunks(head, width, height, plan, o);

        _options = o;
        _plan = plan;
        _native = native;
        _format = format;
        _width = width;
        _height = height;
        _sourceRowBytes = sourceRowBytes;
        _payloadRowBytes = payloadRowBytes;
        WriteOutput(head.GetBuffer().AsSpan(0, (int)head.Length));

        _idat = new IdatWriter(Output);
        var fragments = PngWriter.EffectiveFragmentCount(o.ParallelFragments, height, payloadRowBytes - 1);
        var parallel = fragments > 1;
        var bandRows = parallel
            ? Math.Min((height + fragments - 1) / fragments, Math.Max(2, MaxFragmentPayloadBytes / payloadRowBytes))
            : Math.Min(height, Math.Max(1, SerialBandBytes / payloadRowBytes));
        if (parallel)
        {
            _parallel = new Fragments(Math.Max(1, Math.Min(o.ParallelFragments, Environment.ProcessorCount)), bandRows, sourceRowBytes);
            PngWriter.WriteZlibHeader(_idat, o.CompressionLevel);
            _band = _parallel.Take();
        }
        else
        {
            _zlib = new ZLibStream(_idat, o.CompressionLevel, leaveOpen: true);
            _band = new Band(bandRows, sourceRowBytes);
        }
    }

    protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_band is null, this);
        if (_faulted)
            throw new InvalidOperationException("An earlier failure left the session unusable.");
        try
        {
            var inRowBytes = _width * _format.BytesPerPixel;
            var band = _band;
            for (var y = 0; y < rowCount; y++)
            {
                var source = rows.Slice(y * stride, inRowBytes);
                var destination = band.Row(band.Count);
                if (_format == _native)
                    source.CopyTo(destination);
                else
                    PixelConverter.ConvertRow(source, _format, destination, _native, _width);
                band.Count++;
                if (band.Count == band.Capacity)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    band = FlushBand();
                }
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    protected override void EndFrame(CancellationToken cancellationToken)
    {
        try
        {
            if (_band!.Count > 0)
                FlushBand();
            if (_parallel is { } parallel)
            {
                parallel.DrainAll(_idat!);
                // A final empty fixed-Huffman block, then the Adler-32 of every band, as PngWriter's fragmented stream ends.
                Span<byte> tail = stackalloc byte[6];
                tail[0] = 0x03;
                tail[1] = 0x00;
                PngWriter.WriteBE(tail[2..], parallel.Adler);
                _idat!.Write(tail);
            }
            else
            {
                _zlib!.Dispose();   // the final block and the Adler-32
                _zlib = null;
            }
            _idat!.Complete();
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    protected override void Finish(CancellationToken cancellationToken)
    {
        if (_faulted)
            throw new InvalidOperationException("An earlier failure left the session unusable.");
        using var tail = new MemoryStream();
        PngWriter.WritePostImageChunks(tail, _options);
        WriteOutput(tail.GetBuffer().AsSpan(0, (int)tail.Length));
    }

    /// <summary>
    /// Filters and deflates the gathered band (serially, or hands it to a worker) and returns the band the next rows go
    /// into, whose row above is the last row of this one.
    /// </summary>
    private Band FlushBand()
    {
        var band = _band!;
        var first = _bandFirstRow == 0;
        if (_parallel is { } parallel)
        {
            var next = parallel.Take();
            band.Row(band.Count - 1).CopyTo(next.Row(-1));
            parallel.Submit(band, first, _width, _plan!, _options.CompressionLevel, _payloadRowBytes, _idat!);
            _band = next;
        }
        else
        {
            PngWriter.CompressRows(_zlib!, band.Samples(first, band.Count), first ? 0 : 1, band.Count, _width, _plan!,
                _options.CompressionLevel, checksum: false);
            band.Row(band.Count - 1).CopyTo(band.Row(-1));
        }
        _bandFirstRow += band.Count;
        if (_parallel is null)
            band.Count = 0;      // a submitted band is reset when it is taken again
        return _band!;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _idat?.Abandon();            // nothing more reaches the output, whatever is disposed below
        _parallel?.Dispose();        // waits for the workers before their memory goes back to the pool
        _parallel = null;
        try
        {
            _zlib?.Dispose();
        }
        catch (Exception)
        {
            // An abandoned stream: its last bytes have nowhere to go.
        }
        _zlib = null;
        if (_band is { Pooled: false })
            _band.Release();
        _band = null;
        _idat?.Release();
        _idat = null;
    }

    /// <summary>
    /// A band of source rows in the native layout, with the row above it in front: row <c>-1</c> is the last row of the
    /// previous band (unused for the image's first band, whose row above is zeros).
    /// </summary>
    private sealed class Band(int capacity, int rowBytes)
    {
        private byte[]? _rows = ArrayPool<byte>.Shared.Rent(checked((capacity + 1) * rowBytes));

        public int Capacity => capacity;

        public int Count { get; set; }

        /// <summary>Owned by a <see cref="Fragments"/>, which returns the memory.</summary>
        public bool Pooled => Owner is not null;

        /// <summary>The fragment job this band belongs to, in the fragmented path.</summary>
        public object? Owner { get; set; }

        public Span<byte> Row(int index) => _rows.AsSpan((index + 1) * rowBytes, rowBytes);

        /// <summary>What <see cref="PngWriter.CompressRows"/> reads: from row 0 for the image's first band (firstRow 0),
        /// else from row -1 (firstRow 1).</summary>
        public ReadOnlySpan<byte> Samples(bool first, int count) =>
            first ? _rows.AsSpan(rowBytes, count * rowBytes) : _rows.AsSpan(0, (count + 1) * rowBytes);

        public void Release()
        {
            if (_rows is not null)
                ArrayPool<byte>.Shared.Return(_rows);
            _rows = null;
        }
    }

    /// <summary>
    /// The fragmented path: each full band is filtered and deflated on a worker into an independent, sync-flushed deflate
    /// fragment, as <see cref="PngWriter"/>'s parallel fragments are, and the fragments reach the output in band order.
    /// At most <c>limit</c> bands are compressed at once; the caller waits for the oldest when that many are.
    /// </summary>
    private sealed class Fragments(int limit, int bandRows, int sourceRowBytes) : IDisposable
    {
        private readonly Queue<Job> _running = new();
        private readonly Stack<Job> _idle = new();
        private readonly List<Job> _all = [];
        private bool _started;

        /// <summary>The Adler-32 of every band emitted so far.</summary>
        public uint Adler { get; private set; } = 1;

        /// <summary>A free band (and its output buffer) for the next rows.</summary>
        public Band Take()
        {
            if (!_idle.TryPop(out var job))
            {
                job = new Job(new Band(bandRows, sourceRowBytes));
                _all.Add(job);
            }
            job.Band.Count = 0;
            return job.Band;
        }

        public void Submit(Band band, bool first, int width, PngEncodePlan plan, CompressionLevel level, int payloadRowBytes,
            IdatWriter idat)
        {
            var job = (Job)band.Owner!;
            job.First = first;
            job.Rows = band.Count;
            job.Payload = (long)band.Count * payloadRowBytes;
            job.Work = Task.Run(() => job.Compress(width, plan, level));
            _running.Enqueue(job);
            while (_running.Count > 0 && (_running.Count >= limit || _running.Peek().Work!.IsCompleted))
                Emit(idat);
        }

        public void DrainAll(IdatWriter idat)
        {
            while (_running.Count > 0)
                Emit(idat);
        }

        private void Emit(IdatWriter idat)
        {
            var job = _running.Dequeue();
            job.Work!.GetAwaiter().GetResult();
            job.Work = null;
            idat.Write(job.Output.GetBuffer().AsSpan(0, job.Length));
            Adler = _started ? PngWriter.AdlerCombine(Adler, job.Checksum, job.Payload) : job.Checksum;
            _started = true;
            _idle.Push(job);
        }

        public void Dispose()
        {
            foreach (var job in _all)
            {
                try
                {
                    job.Work?.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Already reported to the caller, or the session is abandoned.
                }
                job.Band.Release();
            }
            _all.Clear();
            _running.Clear();
            _idle.Clear();
        }

        private sealed class Job
        {
            public Job(Band band)
            {
                Band = band;
                band.Owner = this;
            }

            public Band Band { get; }
            public MemoryStream Output { get; } = new();
            public Task? Work { get; set; }
            public bool First { get; set; }
            public int Rows { get; set; }
            public long Payload { get; set; }
            public int Length { get; private set; }
            public uint Checksum { get; private set; }

            public void Compress(int width, PngEncodePlan plan, CompressionLevel level)
            {
                Output.SetLength(0);
                var deflate = new DeflateStream(Output, level, leaveOpen: true);
                Checksum = PngWriter.CompressRows(deflate, Band.Samples(First, Rows), First ? 0 : 1, Rows, width, plan, level, checksum: true);
                deflate.Flush();                      // a sync flush: byte-aligned, not final
                Length = checked((int)Output.Length); // before Dispose appends a final block
                deflate.Dispose();
            }
        }
    }

    /// <summary>
    /// The sink the zlib stream is written into: it cuts the stream into <c>IDAT</c> chunks of
    /// <see cref="IdatChunkBytes"/> and hands each to the output as soon as it is full.
    /// </summary>
    internal sealed class IdatWriter(IBufferWriter<byte> output) : Stream
    {
        private byte[]? _chunk = ArrayPool<byte>.Shared.Rent(IdatChunkBytes);
        private int _count;
        private bool _abandoned;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void WriteByte(byte value) => Write([value]);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_abandoned)
                return;
            ObjectDisposedException.ThrowIf(_chunk is null, this);
            while (!buffer.IsEmpty)
            {
                var n = Math.Min(buffer.Length, IdatChunkBytes - _count);
                buffer[..n].CopyTo(_chunk.AsSpan(_count));
                _count += n;
                buffer = buffer[n..];
                if (_count == IdatChunkBytes)
                    Emit();
            }
        }

        /// <summary>Writes the last, partial chunk.</summary>
        public void Complete()
        {
            if (_count > 0)
                Emit();
        }

        /// <summary>Drops everything written from now on: the session is being disposed unfinished.</summary>
        public void Abandon() => _abandoned = true;

        public void Release()
        {
            if (_chunk is not null)
                ArrayPool<byte>.Shared.Return(_chunk);
            _chunk = null;
        }

        private void Emit()
        {
            var data = _chunk.AsSpan(0, _count);
            var span = output.GetSpan(_count + 12);
            PngWriter.WriteBE(span, (uint)_count);
            "IDAT"u8.CopyTo(span[4..]);
            data.CopyTo(span[8..]);
            PngWriter.WriteBE(span[(8 + _count)..], PngCrc32.Compute("IDAT"u8, data));
            output.Advance(_count + 12);
            _count = 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
