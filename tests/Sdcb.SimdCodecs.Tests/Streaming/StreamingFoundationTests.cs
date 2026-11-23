using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>
/// The streaming plumbing in Abstractions — <see cref="StreamInput"/>, <see cref="StreamingRowReader"/>'s sync / async
/// driver and fallback, <see cref="BufferedRowReader"/>, <see cref="StreamingEncoderSession"/>'s contract checks — over a
/// made-up format (<see cref="RawStreamCodec"/>), so that a codec's own tests only have to prove its decoder.
/// </summary>
public sealed class StreamingFoundationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i * 7 + 3);
        return b;
    }

    [Fact]
    public void StreamInput_FillsConsumesAndGrows()
    {
        var data = Bytes(1000);
        using var input = new StreamInput(new ChunkedStream(data, maxChunk: 5), initialCapacity: 16);
        input.Fill(3).ShouldBeTrue();
        input.Buffered.Length.ShouldBeGreaterThanOrEqualTo(3);
        input.Buffered[0].ShouldBe(data[0]);
        input.Consume(2);
        input.Position.ShouldBe(2);
        input.Fill(300).ShouldBeTrue(); // grows past 16 by doubling
        input.Buffered[..300].SequenceEqual(data.AsSpan(2, 300)).ShouldBeTrue();
        input.Consume(300);
        input.Fill(10_000).ShouldBeFalse();
        input.IsCompleted.ShouldBeTrue();
        input.Buffered.SequenceEqual(data.AsSpan(302)).ShouldBeTrue();
        input.Length.ShouldBe(-1);
    }

    [Fact]
    public async Task StreamInput_FillAsyncAndReadToEndAsync_NeverReadSynchronously()
    {
        var data = Bytes(5000);
        using var input = new StreamInput(new ChunkedStream(data, maxChunk: 97, asyncOnly: true), initialCapacity: 16);
        (await input.FillAsync(10, Ct)).ShouldBeTrue();
        input.Consume(10);
        var rest = await input.ReadToEndAsync(Ct);
        rest.Span.SequenceEqual(data.AsSpan(10)).ShouldBeTrue();
    }

    [Fact]
    public void StreamInput_AHugeRequestGrowsOnlyWithTheData()
    {
        // A header declaring 1 GiB must not make the input allocate 1 GiB for a 50-byte stream.
        using var input = new StreamInput(new ChunkedStream(Bytes(50), maxChunk: 50), initialCapacity: 16);
        var before = GC.GetAllocatedBytesForCurrentThread();
        input.Fill(1 << 30).ShouldBeFalse();
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(1 << 20);
        Should.Throw<InvalidDataException>(() => input.Fill(int.MaxValue));
    }

    [Fact]
    public void StreamInput_RewindsRetainedSeekableAndRefusesOtherwise()
    {
        var data = Bytes(3000);

        using (var retained = new StreamInput(new ChunkedStream(data, maxChunk: 100), retainConsumed: true, initialCapacity: 16))
        {
            retained.CanRewind.ShouldBeTrue();
            retained.Fill(2000);
            retained.Consume(1500);
            retained.Fill(1400);
            retained.Rewind(10);
            retained.Position.ShouldBe(10);
            retained.ReadToEnd().Span.SequenceEqual(data.AsSpan(10)).ShouldBeTrue();
        }

        var seekable = new MemoryStream(data);
        seekable.Position = 0;
        using (var seeking = new StreamInput(new OffsetStream(seekable, 100), initialCapacity: 16))
        {
            seeking.Length.ShouldBe(data.Length - 100);
            seeking.Fill(1000);
            seeking.Consume(900);
            seeking.Fill(1000); // compacts: the first bytes leave the buffer
            seeking.Rewind(5);
            seeking.Position.ShouldBe(5);
            seeking.Fill(10);
            seeking.Buffered[..10].SequenceEqual(data.AsSpan(105, 10)).ShouldBeTrue();
        }

        using var forward = new StreamInput(new ChunkedStream(data, maxChunk: 100), initialCapacity: 16);
        forward.CanRewind.ShouldBeFalse();
        forward.Fill(100);
        forward.Consume(50);
        forward.Fill(2000);
        forward.Consume(1000);
        forward.Fill(2000);
        Should.Throw<NotSupportedException>(() => forward.Rewind(0));
        Should.Throw<ArgumentOutOfRangeException>(() => forward.Rewind(forward.Position + 1));
    }

    [Fact]
    public void StreamInput_DisposesTheStreamUnlessLeftOpen()
    {
        var a = new ChunkedStream(Bytes(10));
        new StreamInput(a).Dispose();
        a.WasDisposed.ShouldBeTrue();
        var b = new ChunkedStream(Bytes(10));
        new StreamInput(b, leaveOpen: true).Dispose();
        b.WasDisposed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 23)]
    [InlineData(300, 5)]
    public void RowReader_EveryFeedEqualsDecodeInto(int width, int height)
    {
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(width, height, width * 31 + height), width, height);
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgr24, PixelFormat.Gray8, PixelFormat.Rgba64 })
            StreamingAssert.RowsEqualDecodeInto(RawStreamCodec.Registration, file, format, expectStreaming: true);
    }

    [Fact]
    public void RowReader_AFallbackVariantIsBufferedWithTheSameRows()
    {
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(41, 17, 5), 41, 17, fallback: true);
        StreamingAssert.RowsEqualDecodeInto(RawStreamCodec.Registration, file, PixelFormat.Bgra32, expectStreaming: false);
    }

    [Fact]
    public void BufferedRowReader_EqualsDecodeInto()
    {
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(29, 13, 9), 29, 13);
        var codec = RawStreamCodec.NonStreamingRegistration;
        var expected = new byte[29 * 13 * 3];
        codec.TryDecodeInto(file, expected, 29 * 3, PixelFormat.Rgb24, null, Ct).ShouldBeTrue();
        foreach (var feed in StreamingAssert.AllFeeds)
        {
            var rows = StreamingAssert.ReadAll(file, feed, input => new BufferedRowReader(input, PixelFormat.Rgb24, null, codec),
                out var streaming, out _, out _);
            streaming.ShouldBeFalse();
            rows.ShouldBe(expected, feed.ToString());
        }
    }

    [Fact]
    public void RowReader_ReadsTheStreamOnlyAsFarAsTheRowsNeed()
    {
        const int w = 256, h = 256;
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(w, h, 3), w, h);
        var stream = new ChunkedStream(file, maxChunk: 1024);
        using var reader = RawStreamCodec.CreateRowReader(new StreamInput(stream, initialCapacity: 4096), PixelFormat.Rgba32, null);
        var row = new byte[w * 4 * 2];
        reader.ReadRows(row, 2, 0, Ct).ShouldBe(2);
        reader.IsStreaming.ShouldBeTrue();
        stream.BytesRead.ShouldBeLessThan(file.Length / 8, "a streaming reader reads ahead only a buffer's worth");
        row.AsSpan().SequenceEqual(file.AsSpan(RawStreamCodec.HeaderLength, w * 4 * 2)).ShouldBeTrue();
    }

    [Fact]
    public void RowReader_TruncatedOrCorruptStreamsThrowInvalidData()
    {
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(16, 8, 1), 16, 8);
        foreach (var cut in new[] { 0, 3, RawStreamCodec.HeaderLength - 1, RawStreamCodec.HeaderLength + 70, file.Length - 1 })
            foreach (var feed in StreamingAssert.AllFeeds)
                Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(file[..cut], feed,
                    input => RawStreamCodec.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _), $"{feed} cut at {cut}");
        var corrupt = (byte[])file.Clone();
        corrupt[^1] ^= 0xFF;
        Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(corrupt, StreamingAssert.Feed.RandomChunks,
            input => RawStreamCodec.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _));
    }

    [Fact]
    public void RowReader_ChecksArgumentsAndState()
    {
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(10, 4, 1), 10, 4);
        using var reader = RawStreamCodec.CreateRowReader(new StreamInput(new MemoryStream(file)), PixelFormat.Rgba32, null);
        Should.Throw<InvalidOperationException>(() => reader.Info);
        reader.Initialize(Ct);
        reader.Info.Width.ShouldBe(10);
        Should.Throw<ArgumentException>(() => reader.ReadRows(new byte[100], 1, stride: 39, Ct));
        Should.Throw<ArgumentException>(() => reader.ReadRows(new byte[79], 2, 0, Ct));
        reader.ReadRows(new byte[40 * 4], 99, 0, Ct).ShouldBe(4); // clamps to the image
        reader.ReadRows(new byte[40], 1, 0, Ct).ShouldBe(0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var other = RawStreamCodec.CreateRowReader(new StreamInput(new MemoryStream(file)), PixelFormat.Rgba32, null);
        Should.Throw<OperationCanceledException>(() => other.ReadRows(new byte[40], 1, 0, cancelled.Token));
    }

    [Fact]
    public void RowReader_StridedRowsLeaveTheGapsAlone()
    {
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(5, 3, 2), 5, 3);
        using var reader = RawStreamCodec.CreateRowReader(new StreamInput(new ChunkedStream(file, maxChunk: 3)), PixelFormat.Rgb24, null);
        var dest = new byte[24 * 2 + 15];
        dest.AsSpan().Fill(0xAB);
        reader.ReadRows(dest, 3, stride: 24, Ct).ShouldBe(3);
        for (var y = 0; y < 2; y++)
            dest.AsSpan(y * 24 + 15, 9).ToArray().ShouldAllBe(b => b == 0xAB);
    }

    [Fact]
    public void RowReader_BoundedMemoryOnALargeImage()
    {
        const int w = 4096, h = 1024; // 16 MiB of RGBA
        var file = RawStreamCodec.Encode(new byte[w * h * 4], w, h);
        var row = new byte[w * 4];
        StreamingAssert.AllocatesLessThan(1 << 20, () =>
        {
            using var reader = RawStreamCodec.CreateRowReader(new StreamInput(new MemoryStream(file, false)), PixelFormat.Rgba32, null);
            while (reader.ReadRows(row, 1, 0, Ct) > 0) { }
        }, "a streaming read of a 16 MiB image");
    }

    [Fact]
    public void EncoderSession_StreamsAndChecksTheContract()
    {
        const int w = 64, h = 32;
        var pixels = RawStreamCodec.RandomRgba(w, h, 4);
        var file = StreamingAssert.EncoderEmitsBeforeCommit(output => new RawEncoderSession(output), pixels, w, h, PixelFormat.Rgba32);
        file.ShouldBe(RawStreamCodec.Encode(pixels, w, h));

        using var session = new RawEncoderSession(new ArrayBufferWriter<byte>());
        Should.Throw<InvalidOperationException>(() => session.Commit(Ct));
        Should.Throw<ArgumentException>(() => session.AddFrame(0, 1, PixelFormat.Rgba32));
        var frame = session.AddFrame(2, 2, PixelFormat.Rgba32);
        Should.Throw<ArgumentException>(() => frame.WriteRows(new byte[24], 3, 0, Ct));
        Should.Throw<ArgumentException>(() => frame.WriteRows(new byte[15], 2, 0, Ct));
        frame.WriteRows(new byte[8], 1, 0, Ct);
        Should.Throw<InvalidOperationException>(() => session.Commit(Ct));
        Should.Throw<InvalidOperationException>(() => session.AddFrame(2, 2, PixelFormat.Rgba32));
        frame.WriteRows(new byte[8], 1, 0, Ct);
        Should.Throw<InvalidOperationException>(() => session.AddFrame(2, 2, PixelFormat.Rgba32)); // MaxFrames 1
        session.Commit(Ct);
        Should.Throw<InvalidOperationException>(() => session.Commit(Ct));
        Should.Throw<InvalidOperationException>(() => frame.WriteRows(new byte[8], 0, 0, Ct));
    }

    /// <summary>The <see cref="RawStreamCodec"/> encoder as a streaming session: header at the frame, rows as they come.</summary>
    private sealed class RawEncoderSession(IBufferWriter<byte> output) : StreamingEncoderSession(output)
    {
        private int _width;
        private PixelFormat _format;

        public override int MaxFrames => 1;

        protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
            ImageMetadata? metadata, TimeSpan? duration)
        {
            _width = width;
            _format = format;
            WriteOutput(RawStreamCodec.Encode([], width, height).AsSpan(0, RawStreamCodec.HeaderLength));
        }

        protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
        {
            Span<byte> rgba = stackalloc byte[_width * 4];
            for (var y = 0; y < rowCount; y++)
            {
                PixelConverter.ConvertRow(rows.Slice(y * stride, _width * _format.BytesPerPixel), _format, rgba, PixelFormat.Rgba32, _width);
                WriteOutput(rgba);
            }
        }

        protected override void Finish(CancellationToken cancellationToken) => WriteOutput("END!"u8);
    }

    /// <summary>A seekable stream whose image starts <c>origin</c> bytes in (the position a <see cref="StreamInput"/> starts from).</summary>
    private sealed class OffsetStream : Stream
    {
        private readonly Stream _inner;

        public OffsetStream(Stream inner, int origin)
        {
            _inner = inner;
            _inner.Position = origin;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 64));
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
