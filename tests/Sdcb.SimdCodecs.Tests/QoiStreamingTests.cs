using System.Buffers;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Qoi;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// QOI streaming: <see cref="QoiImageDecoder.CreateRowReader"/>'s rows equal <see cref="QoiImageDecoder.TryDecodeInto"/>'s
/// under every feed and pixel format, a cut stream fails exactly where the span decoder fails, and
/// <see cref="QoiImageEncoder"/>'s streaming session writes the whole-image encoder's bytes however the rows are banded,
/// handing them over before <c>Commit</c>, in bounded memory.
/// </summary>
public sealed class QoiStreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImageDecoderRegistration Codec => QoiImageDecoder.Registration;

    internal static readonly PixelFormat[] Formats =
    [
        PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24, PixelFormat.Gray8,
        PixelFormat.GrayAlpha16, PixelFormat.Rgba64, PixelFormat.Rgb48, PixelFormat.Gray16, PixelFormat.Rgba32Premultiplied,
    ];

    private static byte[] Rgb(byte[] rgba) => [.. rgba.Where((_, i) => i % 4 != 3)];

    private static byte[] File(int width, int height, bool alpha, int seed)
    {
        var rgba = QoiTests.Pattern(width, height, alpha, seed);
        return alpha ? QoiEncoder.Encode(rgba, width, height, PixelFormat.Rgba32) : QoiEncoder.Encode(Rgb(rgba), width, height, PixelFormat.Rgb24);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rows_equal_decode_into_for_every_feed_and_format(bool alpha)
    {
        foreach (var width in new[] { 1, 2, 3, 7, 16, 33, 67, 600, 1100 })
        {
            var height = width >= 600 ? 9 : 13;
            var file = File(width, height, alpha, width * 7 + 1);
            foreach (var format in Formats)
            {
                // One byte a read costs a few row decodes a byte; keep it to the narrow images.
                StreamingAssert.RowsEqualDecodeInto(Codec, file, format, expectStreaming: true,
                    feeds: width <= 67 ? StreamingAssert.AllFeeds : [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly, StreamingAssert.Feed.Whole]);
            }
        }
    }

    [Fact]
    public void Foreign_and_hand_assembled_streams_stream()
    {
        StreamingAssert.RowsEqualDecodeInto(Codec, QoiTests.HandStream().Stream, PixelFormat.Rgba32, expectStreaming: true);
        foreach (var alpha in new[] { true, false })
        {
            const int w = 45, h = 11;
            using var image = new MagickImage();
            image.ReadPixels(QoiTests.Pattern(w, h, alpha, 3), new PixelReadSettings(w, h, StorageType.Char, "RGBA"));
            if (!alpha) image.Alpha(AlphaOption.Off);
            var qoi = image.ToByteArray(MagickFormat.Qoi);
            foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgr24, PixelFormat.Rgba64 })
                StreamingAssert.RowsEqualDecodeInto(Codec, qoi, format, expectStreaming: true);
        }
    }

    [Fact]
    public void Runs_across_rows_and_a_run_past_the_last_pixel_stream()
    {
        // A flat image is one run after another, each crossing row boundaries.
        var flat = QoiEncoder.Encode(new byte[5 * 40 * 4], 5, 40, PixelFormat.Rgba32);
        StreamingAssert.RowsEqualDecodeInto(Codec, flat, PixelFormat.Rgb24, expectStreaming: true);

        // 2x2 pixels, one run of 62 (the rest is ignored), no end marker: the span decoder reads it, so must the reader.
        byte[] overrun = [.. QoiTests.Header(2, 2, 4), QoiCodec.OpRgba, 1, 2, 3, 4, QoiCodec.OpRun | 61];
        StreamingAssert.RowsEqualDecodeInto(Codec, overrun, PixelFormat.Rgba32, expectStreaming: true);
    }

    [Fact]
    public void Options_other_than_the_full_image_fall_back_with_the_same_rows()
    {
        var file = File(37, 21, true, 5);
        foreach (var options in new[]
                 {
                     new DecodeOptions { ScaleDenominator = 2 },
                     new DecodeOptions { Region = new PixelRect(3, 4, 20, 9) },
                     new DecodeOptions { ApplyOrientation = true },
                 })
            StreamingAssert.RowsEqualDecodeInto(Codec, file, PixelFormat.Bgra32, options, expectStreaming: false);
        StreamingAssert.RowsEqualDecodeInto(Codec, file, PixelFormat.Bgra32, DecodeOptions.Default, expectStreaming: true);
    }

    [Fact]
    public void Every_cut_fails_where_the_span_decoder_fails()
    {
        foreach (var alpha in new[] { true, false })
        {
            var file = File(9, 5, alpha, 11);
            var expected = new byte[9 * 5 * 4];
            for (var cut = 0; cut <= file.Length; cut++)
            {
                var data = file[..cut];
                var spanOk = QoiImageDecoder.TryDecodeInto(data, expected, 0, PixelFormat.Rgba32, null, Ct);
                foreach (var feed in new[] { StreamingAssert.Feed.OneByte, StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly })
                {
                    if (spanOk)
                    {
                        // A cut inside the end marker: the span decoder does not check it, nor does the reader.
                        StreamingAssert.ReadAll(data, feed, Create(PixelFormat.Rgba32), out _, out _, out _).ShouldBe(expected, $"{feed} cut at {cut}");
                    }
                    else
                    {
                        Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(data, feed, Create(PixelFormat.Rgba32), out _, out _, out _),
                            $"{feed} cut at {cut}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Malformed_headers_throw_invalid_data_before_any_row()
    {
        foreach (var bad in new[]
                 {
                     "qoix"u8.ToArray(), "QOIF0000000000"u8.ToArray(), QoiTests.Header(0, 4, 4), QoiTests.Header(4, 4, 5),
                     QoiTests.Header(4, 4, 4, colorSpace: 2), QoiTests.Header(1 << 15, 1 << 15, 4),
                 })
        {
            using var reader = QoiImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(bad, maxChunk: 2)), PixelFormat.Rgba32, null);
            Should.Throw<InvalidDataException>(() => reader.Initialize(Ct));
        }
    }

    [Fact]
    public void Reads_the_stream_only_as_far_as_the_rows_need()
    {
        const int w = 512, h = 512;
        var file = File(w, h, true, 9);
        var stream = new ChunkedStream(file, maxChunk: 1024);
        using var reader = QoiImageDecoder.CreateRowReader(new StreamInput(stream, initialCapacity: 4096), PixelFormat.Rgba32, null);
        var rows = new byte[w * 4 * 4];
        reader.ReadRows(rows, 4, 0, Ct).ShouldBe(4);
        reader.IsStreaming.ShouldBeTrue();
        stream.BytesRead.ShouldBeLessThan(file.Length / 8, "a streaming reader reads ahead about a row");
        var expected = new byte[w * h * 4];
        QoiImageDecoder.TryDecodeInto(file, expected, 0, PixelFormat.Rgba32, null, Ct).ShouldBeTrue();
        rows.ShouldBe(expected.AsSpan(0, rows.Length).ToArray());
    }

    [Fact]
    public void Streaming_encode_bytes_equal_the_whole_image_encoder_for_any_banding()
    {
        var random = new Random(17);
        foreach (var (width, height) in new[] { (1, 200), (3, 70), (61, 9), (600, 7), (1100, 3) })
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Gray8, PixelFormat.Rgba64 })
        foreach (var linear in new[] { false, true })
        {
            var rgba = QoiTests.Pattern(width, height, format.HasAlpha, width + height);
            var pixels = PixelConverter.ToPacked(rgba, 0, PixelFormat.Rgba32, format, width, height).ToArray();
            var colorSpace = linear ? QoiColorSpace.Linear : QoiColorSpace.Srgb;
            var expected = QoiEncoder.Encode(pixels, width, height, format, colorSpace);
            var rowBytes = width * format.BytesPerPixel;
            for (var trial = 0; trial < 6; trial++)
            {
                // Bands of random height at a random stride (trial 0: one row at a time; trial 1: all at once).
                var output = new ArrayBufferWriter<byte>(trial == 2 ? 1 : 256);
                using var session = QoiImageEncoder.Create(output);
                session.IsStreaming.ShouldBeTrue();
                var frame = session.AddFrame(width, height, format, linear ? new ColorEncoding { Transfer = TransferFunction.Linear } : null);
                var y = 0;
                while (y < height)
                {
                    var band = trial switch { 0 => 1, 1 => height, _ => Math.Min(height - y, random.Next(1, 9)) };
                    var stride = rowBytes + (trial >= 3 ? random.Next(0, 9) : 0);
                    var buffer = new byte[stride * band];
                    for (var k = 0; k < band; k++)
                        pixels.AsSpan((y + k) * rowBytes, rowBytes).CopyTo(buffer.AsSpan(k * stride));
                    frame.WriteRows(buffer, band, stride, Ct);
                    y += band;
                }
                session.Commit(Ct);
                output.WrittenSpan.ToArray().ShouldBe(expected, $"{width}x{height} {format} linear {linear} trial {trial}");
            }
        }
    }

    [Fact]
    public void Encoder_emits_before_commit_and_decodes_back()
    {
        const int w = 300, h = 40;
        var rgba = QoiTests.Pattern(w, h, true, 2);
        var file = StreamingAssert.EncoderEmitsBeforeCommit(output => QoiImageEncoder.Create(output), rgba, w, h, PixelFormat.Rgba32);
        file.ShouldBe(QoiEncoder.Encode(rgba, w, h, PixelFormat.Rgba32));
        QoiDecoder.Decode(file).Pixels.ShouldBe(rgba);
    }

    [Fact]
    public void Encoder_session_refuses_what_qoi_cannot_store()
    {
        using var session = QoiImageEncoder.Create(new ArrayBufferWriter<byte>());
        Should.Throw<ArgumentException>(() => session.AddFrame(1 << 15, 1 << 14, PixelFormat.Rgb24));
        var frame = session.AddFrame(2, 1, PixelFormat.Rgb24);
        frame.WriteRows(new byte[6], 1, 0, Ct);
        Should.Throw<InvalidOperationException>(() => session.AddFrame(2, 1, PixelFormat.Rgb24));
        session.Commit(Ct);
    }

    [Fact]
    public void Decode_and_encode_of_a_large_image_stay_in_bounded_memory()
    {
        const int w = 8192, h = 4096;
        // Rows that change from one to the next (diffs, lumas, index hits, runs), made a band at a time.
        const int band = 16;
        var rows = new byte[w * 4 * band];
        void Fill(int y0)
        {
            for (var k = 0; k < band; k++)
            for (var x = 0; x < w; x++)
            {
                var i = (k * w + x) * 4;
                var y = y0 + k;
                rows[i] = (byte)(x >> 3); rows[i + 1] = (byte)y; rows[i + 2] = (byte)((x ^ y) >> 5); rows[i + 3] = (byte)(x < 64 ? x * 4 : 255);
            }
        }

        var sink = new CountingWriter();
        void Encode()
        {
            sink.Reset();
            using var session = QoiImageEncoder.Create(sink);
            var frame = session.AddFrame(w, h, PixelFormat.Rgba32);
            for (var y = 0; y < h; y += band)
            {
                Fill(y);
                frame.WriteRows(rows, band, 0, Ct);
            }
            session.Commit(Ct);
        }
        StreamingAssert.AllocatesLessThan(1 << 20, Encode, "a streaming encode of a 128 MiB image");

        var file = new ArrayBufferWriter<byte>(1 << 20);
        using (var session = QoiImageEncoder.Create(file))
        {
            var frame = session.AddFrame(w, h, PixelFormat.Rgba32);
            for (var y = 0; y < h; y += band)
            {
                Fill(y);
                frame.WriteRows(rows, band, 0, Ct);
            }
            session.Commit(Ct);
        }
        sink.Count.ShouldBe(file.WrittenCount);
        var bytes = file.WrittenSpan.ToArray();

        var row = new byte[w * 3];
        StreamingAssert.AllocatesLessThan(1 << 20, () =>
        {
            using var reader = QoiImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(bytes, maxChunk: 1 << 16)), PixelFormat.Rgb24, null);
            var y = 0;
            while (reader.ReadRows(row, 1, 0, Ct) > 0)
            {
                if ((y & 1023) == 7)
                {
                    // Spot-check a row against the source.
                    Fill(y & ~(band - 1));
                    var k = y & (band - 1);
                    for (var x = 0; x < w; x += 997)
                        (row[x * 3], row[x * 3 + 1], row[x * 3 + 2]).ShouldBe((rows[(k * w + x) * 4], rows[(k * w + x) * 4 + 1], rows[(k * w + x) * 4 + 2]));
                }
                y++;
            }
            y.ShouldBe(h);
        }, "a streaming decode of a 128 MiB image");
    }

    [Fact]
    public async Task Async_only_stream_pipes_into_the_streaming_encoder()
    {
        foreach (var alpha in new[] { true, false })
        {
            const int w = 333, h = 77;
            var file = File(w, h, alpha, 23);
            var format = alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
            var output = new ArrayBufferWriter<byte>();
            await using (var reader = QoiImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(file, maxChunk: 997, seed: 5, asyncOnly: true), initialCapacity: 16), format, null))
            {
                await reader.InitializeAsync(Ct);
                reader.IsStreaming.ShouldBeTrue();
                using var session = QoiImageEncoder.Create(output);
                var frame = session.AddFrame(reader.Width, reader.Height, format, reader.Info.ColorEncoding);
                var band = new byte[w * format.BytesPerPixel * 10];
                var random = new Random(3);
                int n;
                while ((n = await reader.ReadRowsAsync(band, random.Next(1, 11), 0, Ct)) > 0)
                    frame.WriteRows(band, n, 0, Ct);
                session.Commit(Ct);
            }
            output.WrittenSpan.ToArray().ShouldBe(file, "our encoder's file comes back byte for byte");
            var expected = new byte[w * h * 4];
            var actual = new byte[w * h * 4];
            QoiImageDecoder.TryDecodeInto(file, expected, 0, PixelFormat.Rgba32, null, Ct).ShouldBeTrue();
            QoiImageDecoder.TryDecodeInto(output.WrittenSpan, actual, 0, PixelFormat.Rgba32, null, Ct).ShouldBeTrue();
            actual.ShouldBe(expected);
        }
    }

    private static Func<StreamInput, StreamingRowReader> Create(PixelFormat format) =>
        input => QoiImageDecoder.CreateRowReader(input, format, null);

    /// <summary>A writer that keeps nothing: one reused buffer, the bytes counted.</summary>
    private sealed class CountingWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[1 << 16];

        public long Count { get; private set; }

        public void Reset() => Count = 0;

        public void Advance(int count) => Count += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer;

        public Span<byte> GetSpan(int sizeHint = 0) => _buffer;
    }
}
