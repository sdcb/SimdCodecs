using System.Buffers;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegImageEncoder"/>'s streaming session (baseline, Annex K tables): its bytes equal the buffered encoder's
/// for the same pixels and options however the rows are split, they reach the writer before <c>Commit</c>, and memory
/// stays one MCU row. Optimised and progressive encoding fall back to the buffered session.
/// </summary>
public sealed class JpegStreamingEncodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A smooth photo-like field (gradients, a few edges) with noise, so every coefficient band gets used.</summary>
    private static void FillPhoto(Span<byte> rows, int width, int y0, int rowCount, int bpp, int seed)
    {
        var random = new Random(seed * 7919 + y0);
        for (var r = 0; r < rowCount; r++)
        {
            var y = y0 + r;
            for (var x = 0; x < width; x++)
            {
                var o = (r * width + x) * bpp;
                var edge = ((x / 37) + (y / 23)) % 3 == 0 ? 60 : 0;
                for (var c = 0; c < bpp; c++)
                {
                    var v = (x * (c + 1) * 3 + y * (5 - c) + edge) % 256 + random.Next(-12, 13);
                    rows[o + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }
    }

    private static byte[] Photo(int width, int height, int bpp, int seed)
    {
        var data = new byte[width * height * bpp];
        FillPhoto(data, width, 0, height, bpp, seed);
        return data;
    }

    private static byte[] Buffered(byte[] pixels, int width, int height, PixelFormat format, JpegEncodeOptions options)
    {
        var native = JpegImageEncoder.GetClosestInputFormat(format);
        var converted = format == native ? pixels : PixelConverter.ToPacked(pixels, 0, format, native, width, height);
        return JpegEncoder.Encode(converted, width, height, native.Channels, options);
    }

    /// <summary>Streams <paramref name="pixels"/> in bands of the given sizes (cycled), at a padded stride.</summary>
    private static byte[] Streamed(byte[] pixels, int width, int height, PixelFormat format, JpegEncodeOptions? options,
        int[] bands, int pad = 0, ImageMetadata? metadata = null)
    {
        var rowBytes = width * format.BytesPerPixel;
        var stride = rowBytes + pad;
        var padded = new byte[stride * height];
        for (var y = 0; y < height; y++)
            pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(padded.AsSpan(y * stride));
        var output = new ArrayBufferWriter<byte>();
        using var session = JpegImageEncoder.Create(output, options);
        session.IsStreaming.ShouldBe(options is not { Progressive: true } and not { OptimizeHuffman: true });
        var frame = session.AddFrame(width, height, format, null, metadata);
        for (int y = 0, i = 0; y < height; i++)
        {
            var n = Math.Min(bands[i % bands.Length], height - y);
            frame.WriteRows(padded.AsSpan(y * stride), n, stride, Ct);
            y += n;
        }
        session.Commit(Ct);
        return output.WrittenSpan.ToArray();
    }

    private static readonly int[][] Splits = [[1], [3, 7, 1, 16], [8], [16], [5, 100], [int.MaxValue]];

    public static TheoryData<int, int> Sizes => new() { { 1, 1 }, { 17, 9 }, { 33, 17 }, { 40, 31 }, { 16, 16 }, { 2049, 19 }, { 4113, 33 } };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Bytes_equal_the_buffered_encoder_for_every_split_subsampling_and_quality(int width, int height)
    {
        foreach (var subsampling in new[] { JpegSubsampling.Chroma420, JpegSubsampling.Chroma444, JpegSubsampling.Auto })
        foreach (var quality in new[] { 1, 50, 91, 100 })
        {
            var options = new JpegEncodeOptions { Quality = quality, Subsampling = subsampling };
            var pixels = Photo(width, height, 3, quality);
            var expected = Buffered(pixels, width, height, PixelFormat.Rgb24, options);
            foreach (var split in Splits)
            {
                Streamed(pixels, width, height, PixelFormat.Rgb24, options, split, pad: split[0] % 5)
                    .ShouldBe(expected, $"{width}x{height} {subsampling} q{quality} split [{string.Join(',', split)}]");
            }
        }
    }

    [Fact]
    public void Bytes_equal_the_buffered_encoder_for_every_input_format()
    {
        const int width = 37, height = 21;
        foreach (var format in PixelFormat.All)
        foreach (var subsampling in new[] { JpegSubsampling.Chroma420, JpegSubsampling.Chroma444 })
        {
            var options = new JpegEncodeOptions { Quality = 80, Subsampling = subsampling };
            var pixels = new byte[width * height * format.BytesPerPixel];
            new Random(format.GetHashCode()).NextBytes(pixels);
            var expected = Buffered(pixels, width, height, format, options);
            foreach (var split in new[] { [1], [5, 16, 2], new[] { height } })
                Streamed(pixels, width, height, format, options, split, pad: 3).ShouldBe(expected, $"{format} {subsampling}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(130)]
    public void Restart_intervals_stream_to_the_same_bytes(int interval)
    {
        foreach (var (width, height) in new[] { (17, 9), (300, 41), (2100, 20) })
        foreach (var subsampling in new[] { JpegSubsampling.Chroma420, JpegSubsampling.Chroma444 })
        {
            var options = new JpegEncodeOptions { Quality = 85, Subsampling = subsampling, RestartInterval = interval };
            var pixels = Photo(width, height, 3, interval);
            var expected = Buffered(pixels, width, height, PixelFormat.Rgb24, options);
            foreach (var split in new[] { [1], [7, 9], new[] { height } })
                Streamed(pixels, width, height, PixelFormat.Rgb24, options, split).ShouldBe(expected, $"{width}x{height} {subsampling} Ri {interval}");
        }
    }

    [Fact]
    public void Grey_input_streams_to_the_same_bytes()
    {
        foreach (var (width, height) in new[] { (1, 1), (17, 9), (100, 77) })
        {
            var options = new JpegEncodeOptions { Quality = 70, RestartInterval = 4 };
            var pixels = Photo(width, height, 1, 3);
            var expected = Buffered(pixels, width, height, PixelFormat.Gray8, options);
            foreach (var split in Splits)
                Streamed(pixels, width, height, PixelFormat.Gray8, options, split).ShouldBe(expected, $"{width}x{height}");
        }
    }

    [Fact]
    public void Metadata_from_options_and_from_the_frame_stream_to_the_same_bytes()
    {
        const int width = 45, height = 30;
        byte[] exif = [(byte)'M', (byte)'M', 0, (byte)'*', 0, 0, 0, 8, 0, 0, 0, 0];
        var icc = new byte[JpegEncodeOptions.MaxIccChunkBytes + 1000]; // two APP2 chunks
        new Random(9).NextBytes(icc);
        var pixels = Photo(width, height, 4, 1);
        var options = new JpegEncodeOptions
        {
            Quality = 88,
            Exif = exif,
            IccProfile = icc,
            Xmp = new JpegXmp { Description = "streamed" },
        };
        var expected = Buffered(pixels, width, height, PixelFormat.Rgba32, options);
        Streamed(pixels, width, height, PixelFormat.Rgba32, options, [4]).ShouldBe(expected);

        // The frame's metadata overrides the options', as in the buffered session.
        var frameIcc = new byte[500];
        new Random(10).NextBytes(frameIcc);
        var metadata = new ImageMetadata { Exif = exif, Icc = frameIcc };
        var bare = new JpegEncodeOptions { Quality = 88 };
        Streamed(pixels, width, height, PixelFormat.Rgba32, bare, [3], metadata: metadata)
            .ShouldBe(Buffered(pixels, width, height, PixelFormat.Rgba32, bare with { Exif = exif, IccProfile = frameIcc }));
    }

    [Fact]
    public void Optimised_and_progressive_fall_back_to_the_buffered_session_with_unchanged_bytes()
    {
        const int width = 50, height = 35;
        var pixels = Photo(width, height, 3, 4);
        foreach (var options in new[]
        {
            new JpegEncodeOptions { OptimizeHuffman = true },
            new JpegEncodeOptions { Progressive = true },
            new JpegEncodeOptions { Progressive = true, OptimizeHuffman = true, RestartInterval = 2 },
        })
        {
            using (var session = JpegImageEncoder.Create(new ArrayBufferWriter<byte>(), options))
                session.IsStreaming.ShouldBeFalse();
            Streamed(pixels, width, height, PixelFormat.Rgb24, options, [3, 8])
                .ShouldBe(Buffered(pixels, width, height, PixelFormat.Rgb24, options));
        }
        using (var session = JpegImageEncoder.Create(new ArrayBufferWriter<byte>()))
            session.IsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void Bytes_reach_the_writer_before_commit()
    {
        const int width = 64, height = 64;
        var pixels = Photo(width, height, 3, 5);
        var file = StreamingAssert.EncoderEmitsBeforeCommit(o => JpegImageEncoder.Create(o), pixels, width, height, PixelFormat.Rgb24);
        file.ShouldBe(Buffered(pixels, width, height, PixelFormat.Rgb24, new JpegEncodeOptions()));
    }

    [Fact]
    public void Each_mcu_row_is_written_as_soon_as_its_last_row_arrives()
    {
        const int width = 40, height = 40;
        var pixels = Photo(width, height, 3, 6);
        var output = new ArrayBufferWriter<byte>();
        using var session = JpegImageEncoder.Create(output, new JpegEncodeOptions { Subsampling = JpegSubsampling.Chroma420 });
        var frame = session.AddFrame(width, height, PixelFormat.Rgb24);
        var headers = output.WrittenCount;
        headers.ShouldBeGreaterThan(0, "headers at AddFrame");
        var rowBytes = width * 3;
        frame.WriteRows(pixels, 15, 0, Ct);
        output.WrittenCount.ShouldBe(headers, "15 of 16 rows: the MCU row is not complete");
        frame.WriteRows(pixels.AsSpan(15 * rowBytes), 1, 0, Ct);
        output.WrittenCount.ShouldBeGreaterThan(headers, "the 16th row completes the first MCU row");
        frame.WriteRows(pixels.AsSpan(16 * rowBytes), height - 16, 0, Ct);
        session.Commit(Ct);
        output.WrittenSpan.ToArray().ShouldBe(Buffered(pixels, width, height, PixelFormat.Rgb24, new JpegEncodeOptions { Subsampling = JpegSubsampling.Chroma420 }));
    }

    [Fact]
    public void Invalid_options_throw_at_add_frame()
    {
        using var session = JpegImageEncoder.Create(new ArrayBufferWriter<byte>(), new JpegEncodeOptions { Quality = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => session.AddFrame(8, 8, PixelFormat.Rgb24));
    }

    [Fact]
    public void Cancellation_stops_the_session_before_the_next_mcu_row()
    {
        // The token is cancelled while one WriteRows call is coding: once the first MCU row reaches the writer.
        const int width = 32, height = 32;
        var pixels = Photo(width, height, 3, 7);
        using var cts = new CancellationTokenSource();
        var output = new CancellingWriter(cts);
        using var session = JpegImageEncoder.Create(output, new JpegEncodeOptions { Subsampling = JpegSubsampling.Chroma444 });
        var frame = session.AddFrame(width, height, PixelFormat.Rgb24);
        output.Armed = true;
        Should.Throw<OperationCanceledException>(() => frame.WriteRows(pixels, height, 0, cts.Token));
        output.Advances.ShouldBe(1, "one MCU row was coded, then the session stopped");
        frame.RowsWritten.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => frame.WriteRows(pixels, height, 0, Ct));
        Should.Throw<InvalidOperationException>(() => session.Commit(Ct));
    }

    private sealed class CancellingWriter(CancellationTokenSource cts) : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[1 << 16];

        public bool Armed { get; set; }

        public int Advances { get; private set; }

        public void Advance(int count)
        {
            if (!Armed) return;
            Advances++;
            cts.Cancel();
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer;

        public Span<byte> GetSpan(int sizeHint = 0) => _buffer;
    }

    /// <summary>An <see cref="IBufferWriter{T}"/> that counts what it is given and keeps none of it.</summary>
    private sealed class CountingWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[1 << 16];

        public long Count { get; private set; }

        public void Advance(int count) => Count += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[sizeHint];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    [Fact]
    public void A_large_frame_streams_in_bounded_memory()
    {
        // 8192 × 8192 RGB is 192 MiB of pixels; the session holds one 16-row band of it.
        const int width = 8192, height = 8192, bandRows = 64;
        var band = new byte[width * bandRows * 3];
        FillPhoto(band, width, 0, bandRows, 3, 1);
        var writer = new CountingWriter();
        var options = new JpegEncodeOptions { Quality = 75, Subsampling = JpegSubsampling.Chroma420 };
        StreamingAssert.AllocatesLessThan(1 << 20, () =>
        {
            using var session = JpegImageEncoder.Create(writer, options);
            var frame = session.AddFrame(width, height, PixelFormat.Rgb24);
            for (var y = 0; y < height; y += bandRows)
                frame.WriteRows(band, bandRows, 0, Ct);
            session.Commit(Ct);
        }, "8192x8192 streamed");
        writer.Count.ShouldBeGreaterThan(1 << 20);
    }

    /// <summary>A writer that hands every advanced span straight to a <see cref="FileStream"/>.</summary>
    private sealed class FileWriter(FileStream file) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[4096];

        public void Advance(int count)
        {
            file.Write(_buffer, 0, count);
            file.Flush();
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length) _buffer = new byte[sizeHint];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    [Fact]
    public void A_photo_streamed_to_a_file_decodes_like_the_buffered_encode()
    {
        const int width = 3001, height = 2003, bandRows = 37;
        var options = new JpegEncodeOptions { Quality = 85, RestartInterval = 64 };
        var pixels = Photo(width, height, 3, 8);
        var path = Path.Combine(Path.GetTempPath(), $"jpeg-stream-{Guid.NewGuid():N}.jpg");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew))
            {
                using var session = JpegImageEncoder.Create(new FileWriter(file), options);
                var frame = session.AddFrame(width, height, PixelFormat.Rgb24);
                for (var y = 0; y < height; y += bandRows)
                {
                    var n = Math.Min(bandRows, height - y);
                    frame.WriteRows(pixels.AsSpan(y * width * 3), n, 0, Ct);
                    if (y == 0) file.Length.ShouldBeGreaterThan(0, "the first band is on disk before the rest arrives");
                }
                session.Commit(Ct);
            }

            var streamed = File.ReadAllBytes(path);
            var buffered = JpegEncoder.Encode(pixels, width, height, 3, options);
            JpegDecoder.Decode(streamed).Pixels.ShouldBe(JpegDecoder.Decode(buffered).Pixels, "our decoder");
            MagickRgb(streamed).ShouldBe(MagickRgb(buffered), "Magick.NET");
            streamed.ShouldBe(buffered);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] MagickRgb(byte[] jpeg)
    {
        using var image = new MagickImage(jpeg, MagickFormat.Jpeg);
        using var pixels = image.GetPixels();
        return pixels.ToByteArray(PixelMapping.RGB)!;
    }
}
