using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>
/// <see cref="StreamInput.Seek"/> (forward by seeking or by reading through, back, with a read length),
/// <see cref="StreamingRowReader.ReadHeader"/> (the header without the fallback decode) and the driver's bound on the
/// rows one step decodes (what makes a large read cancellable).
/// </summary>
public sealed class StreamingSeekAndHeaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)((i * 7) + (i >> 9));
        return b;
    }

    [Fact]
    public void Seek_forward_on_a_seekable_stream_reads_only_the_length_asked_for()
    {
        var data = Bytes(1 << 20);
        var stream = new CountingStream(data);
        using var input = new StreamInput(stream, leaveOpen: true);
        input.Fill(16).ShouldBeTrue();
        var first = stream.BytesRead;

        input.Seek(500_000, 100).ShouldBeTrue();
        input.Position.ShouldBe(500_000);
        input.Fill(100).ShouldBeTrue();
        input.Buffered[..100].SequenceEqual(data.AsSpan(500_000, 100)).ShouldBeTrue();
        stream.BytesRead.ShouldBe(first + 100, "the read after the seek stops at the length");

        // Past the length the input reads ahead as usual.
        input.Fill(101).ShouldBeTrue();
        stream.BytesRead.ShouldBeGreaterThan(first + 1000);

        // Back, beyond the buffer: a seek again.
        input.Seek(10).ShouldBeTrue();
        input.Fill(1).ShouldBeTrue();
        input.Buffered[0].ShouldBe(data[10]);
        input.Position.ShouldBe(10);
    }

    [Fact]
    public void Seek_within_the_buffer_moves_without_reading()
    {
        var data = Bytes(4096);
        var stream = new CountingStream(data);
        using var input = new StreamInput(stream, leaveOpen: true);
        input.Fill(4096).ShouldBeTrue();
        var read = stream.BytesRead;
        input.Seek(3000).ShouldBeTrue();
        input.Buffered[0].ShouldBe(data[3000]);
        input.Seek(5).ShouldBeTrue();
        input.Buffered[0].ShouldBe(data[5]);
        stream.BytesRead.ShouldBe(read);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seek_forward_reads_through_a_stream_that_cannot_seek(bool async)
    {
        var data = Bytes(20_000);
        using var input = new StreamInput(new ChunkedStream(data, maxChunk: 333, seed: 3, asyncOnly: async), initialCapacity: 16);
        var fills = 0;
        while (!input.Seek(15_000))
        {
            var more = async
                ? await input.FillAsync(input.Buffered.Length + 1, Ct)
                : input.Fill(input.Buffered.Length + 1);
            more.ShouldBeTrue();
            fills++;
        }
        fills.ShouldBeGreaterThan(0);
        input.Position.ShouldBe(15_000);
        var filled = async ? await input.FillAsync(1, Ct) : input.Fill(1);
        filled.ShouldBeTrue();
        input.Buffered[0].ShouldBe(data[15_000]);
    }

    [Fact]
    public void Seek_forward_with_retained_bytes_reads_through_and_can_rewind()
    {
        var data = Bytes(20_000);
        using var input = new StreamInput(new MemoryStream(data), retainConsumed: true, initialCapacity: 16);
        while (!input.Seek(12_345))
            input.Fill(input.Buffered.Length + 1).ShouldBeTrue();
        input.Fill(1).ShouldBeTrue();
        input.Buffered[0].ShouldBe(data[12_345]);
        input.Seek(7).ShouldBeTrue();
        input.Buffered[0].ShouldBe(data[7]);
    }

    [Fact]
    public void Seek_past_the_end_of_a_stream_that_cannot_seek_reports_it()
    {
        var data = Bytes(1000);
        using var input = new StreamInput(new ChunkedStream(data, maxChunk: 100), initialCapacity: 16);
        while (!input.Seek(5000))
        {
            if (!input.Fill(input.Buffered.Length + 1))
                break;
        }
        input.IsCompleted.ShouldBeTrue();
        input.Seek(5000).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => input.Seek(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => input.Seek(0, -1));
    }

    [Fact]
    public void ReadHeader_leaves_a_fallback_undecoded_until_the_rows_are_read()
    {
        const int w = 64, h = 48;
        var rgba = RawStreamCodec.RandomRgba(w, h, seed: 4);
        var file = RawStreamCodec.Encode(rgba, w, h, fallback: true);
        var stream = new CountingStream(file);
        using var reader = RawStreamCodec.CreateRowReader(new StreamInput(stream, initialCapacity: 16), PixelFormat.Rgba32, null);
        reader.ReadHeader(Ct);
        reader.IsInitialized.ShouldBeTrue();
        reader.IsStreaming.ShouldBeFalse();
        (reader.Width, reader.Height).ShouldBe((w, h));
        stream.BytesRead.ShouldBeLessThan(file.Length, "ReadHeader read the whole file");

        var rows = new byte[rgba.Length];
        reader.ReadRows(rows, h, 0, Ct).ShouldBe(h);
        rows.ShouldBe(rgba);
        stream.BytesRead.ShouldBe(file.Length);
    }

    [Fact]
    public async Task ReadHeaderAsync_then_rows_asynchronously()
    {
        const int w = 30, h = 20;
        var rgba = RawStreamCodec.RandomRgba(w, h, seed: 5);
        var file = RawStreamCodec.Encode(rgba, w, h, fallback: true);
        await using var reader = RawStreamCodec.CreateRowReader(
            new StreamInput(new ChunkedStream(file, maxChunk: 97, seed: 2, asyncOnly: true), initialCapacity: 16),
            PixelFormat.Rgba32, null);
        await reader.ReadHeaderAsync(Ct);
        reader.IsStreaming.ShouldBeFalse();
        var rows = new byte[rgba.Length];
        (await reader.ReadRowsAsync(rows, h, 0, Ct)).ShouldBe(h);
        rows.ShouldBe(rgba);
    }

    [Fact]
    public void A_large_read_is_decoded_in_bounded_steps()
    {
        const int w = 1024, h = 1000; // 4 KiB rows: 256 rows a step
        var asks = new List<int>();
        using var reader = new SyntheticReader(w, h, asks.Add);
        reader.Initialize(Ct);
        var rows = new byte[w * 4 * h];
        reader.ReadRows(rows, h, 0, Ct).ShouldBe(h);
        asks.Max().ShouldBe(256);
        asks.Sum().ShouldBe(h);
    }

    [Fact]
    public void Cancellation_stops_a_large_read_between_steps()
    {
        const int w = 1024, h = 1000;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var steps = 0;
        using var reader = new SyntheticReader(w, h, _ =>
        {
            if (++steps == 1) cts.Cancel();
        });
        reader.Initialize(Ct);
        var rows = new byte[w * 4 * h];
        Should.Throw<OperationCanceledException>(() => reader.ReadRows(rows, h, 0, cts.Token));
        steps.ShouldBe(1);
        reader.RowsRead.ShouldBe(256);
    }

    /// <summary>A reader of a header-less image: every step writes the rows it is asked for and reports the count.</summary>
    private sealed class SyntheticReader(int w, int h, Action<int> onStep)
        : StreamingRowReader(new StreamInput(new MemoryStream([])), PixelFormat.Rgba32)
    {
        protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            out ImageInfo info, out int width, out int height)
        {
            consumed = 0;
            info = new ImageInfo(w, h, 4, SampleFormat.UInt8);
            (width, height) = (w, h);
            return OperationStatus.Done;
        }

        protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            Span<byte> destination, int stride, int rowCount, out int rowsWritten)
        {
            onStep(rowCount);
            consumed = 0;
            rowsWritten = rowCount;
            return OperationStatus.Done;
        }
    }
}
