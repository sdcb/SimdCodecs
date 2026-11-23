using System.Buffers;
using System.Text;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;
using static Sdcb.SimdCodecs.Tests.Streaming.StreamingAssert;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Netpbm from a forward-only stream (<see cref="PnmImageDecoder.CreateRowReader"/>) and into one
/// (<see cref="PnmImageEncoder"/>'s streaming session): rows equal <see cref="PnmImageDecoder.TryDecodeInto"/>'s for
/// every P1 to P7 variant, depth and output layout under every feed; acceptance and rejection equal the span decoder's;
/// encoded bytes equal the whole-image encode however the rows are split, and reach the writer before Commit; memory
/// stays a few rows.
/// </summary>
public sealed class PnmStreamingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static ImageDecoderRegistration Codec => PnmImageDecoder.Registration;

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Random8(int width, int height, int channels, int seed)
    {
        var pixels = new byte[width * height * channels];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    /// <summary>Synthetic files of every format and depth our encoder writes.</summary>
    public static IEnumerable<(string Name, byte[] File)> Synthetic(int width, int height)
    {
        var rgba = Random8(width, height, 4, width * 31 + height);
        foreach (var format in new[] { PnmFormat.PlainBitmap, PnmFormat.PlainGraymap, PnmFormat.PlainPixmap, PnmFormat.Bitmap, PnmFormat.Graymap, PnmFormat.Pixmap, PnmFormat.Arbitrary })
        foreach (var max in new[] { 0, 7, 1000, 65535 })
        {
            if (max != 0 && format is PnmFormat.Bitmap or PnmFormat.PlainBitmap) continue;
            yield return ($"{format} max {max}", PnmEncoder.Encode(rgba, width, height, PixelFormat.Rgba32, new PnmEncodeOptions { Format = format, MaxValue = max }));
        }
        // 16-bit input: every sample value, not only 257 × v.
        var wide = new ushort[width * height * 4];
        var random = new Random(width + 7 * height);
        for (var i = 0; i < wide.Length; i++) wide[i] = (ushort)random.Next(65536);
        foreach (var (format, channels) in new[] { (PnmFormat.Graymap, 1), (PnmFormat.Pixmap, 3), (PnmFormat.PlainGraymap, 1), (PnmFormat.PlainPixmap, 3), (PnmFormat.Arbitrary, 2), (PnmFormat.Arbitrary, 4) })
        foreach (var max in new[] { 0, 300, 4095 })
            yield return ($"{format} {channels}ch 16-bit max {max}", PnmEncoder.Encode(wide.AsSpan(0, width * height * channels), width, height, channels, new PnmEncodeOptions { Format = format, MaxValue = max }));
    }

    /// <summary>Hand-written files: comments and odd whitespace everywhere, odd maxima, samples above the maximum,
    /// trailing data and a second image, CR line ends, numbers with leading zeros, a raw row of 0x20 and 0x23 bytes.</summary>
    public static IEnumerable<(string Name, byte[] File)> Hand()
    {
        yield return ("P1 comments", Ascii("P1\n# c1\n 3 # c2\n2\n1 0 1#x\n0#y\n1\r0 trailing"));
        yield return ("P1 packed", Ascii("P1 5 2 1010101010"));
        yield return ("P2 odd max", Ascii("P2 #a\r3\t2 # b\n 5 \n0 1 2\n3 4 9999999 ignored after"));
        yield return ("P2 leading zeros", Ascii("P2\n2 2\n00000255\n0255 000 1 00\n"));
        yield return ("P2 16-bit", Ascii("P2 2 2 1023 0 1023 512\n#mid\n70000"));
        yield return ("P2 last sample at end", Ascii("P2 2 1 255 12 34"));
        yield return ("P3 comments", Ascii("P3\n# header\n2 1\n# max\n255\n# raster\n255 0 0 # red\n0 0 255 # blue"));
        yield return ("P4 bits", [.. Ascii("P4\n# c\n10 2\n"), 0b1010_1010, 0b1100_0000, 0b0101_0101, 0b0011_1111, 9, 9]);
        yield return ("P5 space and hash raster", [.. Ascii("P5 4 2 255\n"), 0x20, 0x23, 0x0A, 0x30, 0x23, 0x20, 0x0D, 0x39]);
        yield return ("P5 odd max", [.. Ascii("P5 3 1 100 "), 0, 50, 200]);
        yield return ("P5 16-bit odd max", [.. Ascii("P5 2 1 1000\n"), 0x03, 0xE8, 0xFF, 0xFF]);
        yield return ("P6 second image", [.. Ascii("P6 1 1 255\n"), 1, 2, 3, .. Ascii("P6 1 1 255\n"), 4, 5, 6]);
        yield return ("P7 comments", [.. Ascii("P7\n# c\nWIDTH 2\n#d\nHEIGHT 1\nDEPTH 2 #e\nMAXVAL 255\nTUPLTYPE GRAYSCALE_ALPHA\nTUPLTYPE more\nENDHDR  \n"), 1, 2, 3, 4]);
        yield return ("P7 rgba 16", [.. Ascii("P7\nWIDTH 1\nHEIGHT 2\nDEPTH 4\nMAXVAL 65535\nENDHDR\n"), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        yield return ("P5 long comment", [.. Ascii("P5\n#" + new string('x', 5000) + "\n2 1 255\n"), 7, 8]);
    }

    private static PixelFormat[] AllFormats => PixelFormat.All.ToArray();

    [Fact]
    public void Small_synthetic_files_stream_to_the_rows_of_decode_into_every_feed_every_format()
    {
        foreach (var (name, file) in Synthetic(9, 5))
        foreach (var format in AllFormats)
            Run(name, () => RowsEqualDecodeInto(Codec, file, format, expectStreaming: true), format);
    }

    [Fact]
    public void Hand_written_files_stream_to_the_rows_of_decode_into()
    {
        foreach (var (name, file) in Hand())
        foreach (var format in AllFormats)
            Run(name, () => RowsEqualDecodeInto(Codec, file, format, expectStreaming: true), format);
    }

    [Fact]
    public void Wide_files_stream_across_chunks_of_a_row()
    {
        // 1100 pixels: past the 1024-pixel chunk, and a plain row longer than any read.
        foreach (var (name, file) in Synthetic(1100, 3))
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Gray8, PixelFormat.Rgb48, PixelFormat.Bgra64 })
            Run(name, () => RowsEqualDecodeInto(Codec, file, format, expectStreaming: true, feeds: [Feed.RandomChunks, Feed.AsyncOnly, Feed.Whole]), format);
    }

    private static void Run(string name, Action check, PixelFormat format)
    {
        try
        {
            check();
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{name} into {format}: {e.Message}", e);
        }
    }

    [Fact]
    public void Imagemagick_files_stream_as_the_span_decoder_reads_them()
    {
        var rgba = Random8(37, 6, 4, 5);
        foreach (var (format, depth, plain, channels) in new[]
        {
            (MagickFormat.Pbm, 8u, false, 1), (MagickFormat.Pbm, 8u, true, 1), (MagickFormat.Pgm, 8u, false, 1), (MagickFormat.Pgm, 16u, true, 1),
            (MagickFormat.Ppm, 8u, true, 3), (MagickFormat.Ppm, 16u, false, 3), (MagickFormat.Pam, 8u, false, 2), (MagickFormat.Pam, 16u, false, 4),
        })
        {
            using var image = new MagickImage();
            image.ReadPixels(rgba, new PixelReadSettings(37, 6, StorageType.Char, "RGBA"));
            if (channels is 1 or 2) image.ColorType = channels == 1 ? ColorType.Grayscale : ColorType.GrayscaleAlpha;
            if (channels is 1 or 3) image.Alpha(AlphaOption.Off);
            image.Depth = depth;
            if (plain) image.Settings.Compression = CompressionMethod.NoCompression;
            var file = image.ToByteArray(format);
            PnmDecoder.ReadInfo(file).IsPlain.ShouldBe(plain);
            foreach (var target in new[] { PixelFormat.Rgba32, PixelFormat.Gray16, PixelFormat.Rgba64Premultiplied })
                Run($"{format} {depth} plain {plain}", () => RowsEqualDecodeInto(Codec, file, target, expectStreaming: true), target);
        }
    }

    [Fact]
    public void Every_truncation_is_accepted_or_refused_as_the_span_decoder_does()
    {
        var files = Synthetic(5, 3).Concat(Hand().Where(h => h.File.Length < 400)).ToList();
        foreach (var (name, file) in files)
        for (var length = 0; length <= file.Length; length++)
            SameOutcome(file[..length], $"{name} cut to {length}", PixelFormat.Rgba32, seed: length);
    }

    /// <summary>
    /// Asserts the streaming reader does what the span decoder does with <paramref name="data"/>: the same rows, or
    /// <see cref="InvalidDataException"/> where <see cref="PnmImageDecoder.TryDecodeInto"/> is false.
    /// </summary>
    internal static void SameOutcome(byte[] data, string context, PixelFormat format, int seed)
    {
        byte[]? expected = null;
        if (PnmImageDecoder.TryReadInfo(data, out var info) && (long)info.Width * info.Height <= 1 << 20)
        {
            var buffer = new byte[info.Width * info.Height * format.BytesPerPixel];
            if (PnmImageDecoder.TryDecodeInto(data, buffer, 0, format))
                expected = buffer;
        }
        else if (PnmImageDecoder.TryReadInfo(data, out _))
        {
            return; // too large to decode here
        }
        foreach (var feed in new[] { Feed.RandomChunks, Feed.AsyncOnly, Feed.OneByte })
        {
            byte[] actual;
            try
            {
                actual = ReadAll(data, feed, input => PnmImageDecoder.CreateRowReader(input, format, null), out _, out _, out _, seed);
            }
            catch (InvalidDataException)
            {
                if (expected is not null)
                    throw new ShouldAssertException($"{context} ({feed}): the stream is refused but TryDecodeInto decodes it.");
                continue;
            }
            catch (Exception e)
            {
                throw new ShouldAssertException($"{context} ({feed}): {e.GetType().Name}: {e.Message}", e);
            }
            if (expected is null)
                throw new ShouldAssertException($"{context} ({feed}): the stream decodes but TryDecodeInto refuses it.");
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new ShouldAssertException($"{context} ({feed}): rows differ from TryDecodeInto.");
        }
    }

    [Fact]
    public void A_short_raster_throws_invalid_data_from_read_rows()
    {
        var file = PnmEncoder.Encode(Random8(16, 8, 3, 1), 16, 8, 3);
        using var reader = PnmImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(file[..^5], maxChunk: 50)), PixelFormat.Rgb24, null);
        var rows = new byte[16 * 3 * 8];
        reader.ReadRows(rows, 7, 0, Ct).ShouldBe(7);
        Should.Throw<InvalidDataException>(() => reader.ReadRows(rows, 1, 0, Ct));
    }

    [Theory]
    [InlineData("Pf 1 1 -1.0\n\0\0\0\0")]
    [InlineData("P8 1 1 255\n\0")]
    [InlineData("Q5 1 1 255\n\0")]
    [InlineData("P5x1 1 255\n\0")]
    [InlineData("P5 1 1 65536 \0\0")]
    [InlineData("P5 1 1 255")]
    [InlineData("P5 0 1 255 ")]
    [InlineData("P5 1 1 255#\n\0")]
    [InlineData("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 5\nMAXVAL 255\nENDHDR\n\0")]
    [InlineData("P7\nWIDTH 1\nHEIGHTS 1\nENDHDR\n\0")]
    [InlineData("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nENDHDR x\n\0")]
    [InlineData("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nENDHDR")]
    [InlineData("P2 2 1 255 1 x")]
    [InlineData("P1 2 1 1 2")]
    public void Malformed_headers_and_rasters_throw_invalid_data(string text)
    {
        var file = Ascii(text);
        PnmImageDecoder.TryDecodeInto(file, new byte[64], 0, PixelFormat.Rgba32, null, Ct).ShouldBeFalse();
        foreach (var feed in new[] { Feed.OneByte, Feed.AsyncOnly, Feed.Whole })
            Should.Throw<InvalidDataException>(() => ReadAll(file, feed, input => PnmImageDecoder.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _));
    }

    [Fact]
    public void A_row_too_wide_for_a_buffer_throws_invalid_data()
    {
        // 2^28 pixels of 8-byte RGBA64 is a 2 GiB row: not an OverflowException.
        foreach (var feed in new[] { Feed.RandomChunks, Feed.Whole })
            Should.Throw<InvalidDataException>(() => ReadAll(Ascii("P5 268435456 1 255\n\0"), feed,
                input => PnmImageDecoder.CreateRowReader(input, PixelFormat.Rgba64, null), out _, out _, out _));
    }

    [Fact]
    public void A_wide_plain_header_alone_allocates_no_row()
    {
        // The sample scratch grows with the samples that arrive, not with the width the header declares.
        const int width = 1 << 24;
        var file = Ascii($"P3 {width} 1 65535\n1 2 3 4");
        var row = new byte[width * 6];
        using var reader = (PnmRowReader)PnmImageDecoder.CreateRowReader(new StreamInput(new MemoryStream(file)), PixelFormat.Rgb48, null);
        Should.Throw<InvalidDataException>(() => reader.ReadRows(row, 1, 0, Ct));
        reader.ScratchBytes.ShouldBeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public void A_header_past_the_streaming_limit_is_refused_after_reading_only_that_much()
    {
        var file = Ascii("P5\n#" + new string('x', 2 << 20) + "\n1 1 255\n\0");
        var stream = new ChunkedStream(file, maxChunk: 65536);
        using var reader = PnmImageDecoder.CreateRowReader(new StreamInput(stream), PixelFormat.Gray8, null);
        Should.Throw<InvalidDataException>(() => reader.Initialize(Ct));
        stream.BytesRead.ShouldBeLessThan((1 << 20) + 2 * 65536 + 1);
    }

    [Fact]
    public void A_long_comment_fed_a_byte_at_a_time_is_scanned_once()
    {
        // The header scan resumes where it stopped: 200 KB arriving a byte per read takes moments, not minutes.
        var file = Ascii("P5\n#" + new string('x', 200_000) + "\n2 1 255\n\u0001\u0002");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ReadAll(file, Feed.OneByte, input => PnmImageDecoder.CreateRowReader(input, PixelFormat.Gray8, null), out _, out _, out _)
            .ShouldBe(new byte[] { 1, 2 });
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Options_other_than_the_full_image_take_the_buffered_fallback()
    {
        var file = PnmEncoder.Encode(Random8(40, 30, 3, 2), 40, 30, 3);
        RowsEqualDecodeInto(Codec, file, PixelFormat.Rgba32, new DecodeOptions { Region = new PixelRect(3, 4, 20, 10) }, expectStreaming: false);
        RowsEqualDecodeInto(Codec, file, PixelFormat.Rgb24, new DecodeOptions { ScaleDenominator = 2 }, expectStreaming: false);
        RowsEqualDecodeInto(Codec, file, PixelFormat.Rgb24, new DecodeOptions(), expectStreaming: true);
    }

    [Fact]
    public void The_reader_reads_no_further_than_the_rows_asked_for()
    {
        var file = PnmEncoder.Encode(Random8(100, 100, 1, 3), 100, 100, 1);
        var stream = new ChunkedStream(file, maxChunk: 1);
        using var reader = PnmImageDecoder.CreateRowReader(new StreamInput(stream, initialCapacity: 16), PixelFormat.Gray8, null);
        var rows = new byte[100 * 10];
        reader.ReadRows(rows, 10, 0, Ct).ShouldBe(10);
        stream.BytesRead.ShouldBe(file.Length - 90 * 100);
        rows.ShouldBe(file.AsSpan(file.Length - 100 * 100, 1000).ToArray());
    }

    [Fact]
    public void A_large_raw_image_streams_in_a_few_rows_of_memory()
    {
        // 8192 x 4096 grey (32 MiB) and RGB (96 MiB) read a row at a time.
        foreach (var (channels, format) in new[] { (1, PixelFormat.Gray8), (3, PixelFormat.Rgba32) })
        {
            const int width = 8192, height = 4096;
            var header = Ascii($"P{(channels == 1 ? 5 : 6)}\n{width} {height}\n255\n");
            var file = new byte[header.Length + (long)width * height * channels];
            header.CopyTo(file, 0);
            new Random(channels).NextBytes(file.AsSpan(header.Length, 1 << 16));
            var row = new byte[width * format.BytesPerPixel];
            AllocatesLessThan(256 * 1024, () =>
            {
                using var reader = PnmImageDecoder.CreateRowReader(new StreamInput(new MemoryStream(file, writable: false)), format, null);
                while (reader.ReadRows(row, 1, 0, Ct) == 1) { }
                reader.RowsRead.ShouldBe(height);
            }, $"{channels} channels");
        }
    }

    [Fact]
    public void A_large_plain_image_streams_in_a_few_rows_of_memory()
    {
        const int width = 2048, height = 512;
        var file = PnmEncoder.Encode(Random8(width, height, 3, 4), width, height, 3, new PnmEncodeOptions { Format = PnmFormat.PlainPixmap });
        var row = new byte[width * 4];
        AllocatesLessThan(256 * 1024, () =>
        {
            using var reader = PnmImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(file, maxChunk: 4096)), PixelFormat.Rgba32, null);
            while (reader.ReadRows(row, 1, 0, Ct) == 1) { }
        });
    }

    // ---- the encoder ----------------------------------------------------------------------------------------------

    public static IEnumerable<PnmEncodeOptions> EncoderOptions()
    {
        foreach (var format in new[] { PnmFormat.Auto, PnmFormat.PlainBitmap, PnmFormat.PlainGraymap, PnmFormat.PlainPixmap, PnmFormat.Bitmap, PnmFormat.Graymap, PnmFormat.Pixmap, PnmFormat.Arbitrary })
        foreach (var max in new[] { 0, 7, 1000 })
        {
            if (max != 0 && format is PnmFormat.Bitmap or PnmFormat.PlainBitmap) continue;
            yield return new PnmEncodeOptions { Format = format, MaxValue = max };
        }
        yield return new PnmEncodeOptions { Format = PnmFormat.Arbitrary, TupleType = "CUSTOM" };
    }

    // What the session encoded before it streamed: the frame converted to the encoder's input layout, encoded whole.
    private static byte[]? Buffered(byte[] pixels, int width, int height, PixelFormat format, PnmEncodeOptions options)
    {
        var native = PnmImageEncoder.GetClosestInputFormat(format);
        try
        {
            return PnmEncoder.Encode(PixelConverter.ToPacked(pixels, 0, format, native, width, height), width, height, native, options);
        }
        catch (ArgumentException)
        {
            return null; // an option this input cannot be written with
        }
    }

    [Fact]
    public void The_session_streams_the_bytes_of_the_whole_image_encode_for_any_bands()
    {
        const int width = 1037, height = 9;
        var random = new Random(11);
        foreach (var format in AllFormats)
        {
            var pixels = new byte[width * height * format.BytesPerPixel];
            random.NextBytes(pixels);
            foreach (var options in EncoderOptions())
            {
                var expected = Buffered(pixels, width, height, format, options);
                var output = new ArrayBufferWriter<byte>();
                using var session = PnmImageEncoder.Create(output, options);
                session.IsStreaming.ShouldBeTrue();
                if (expected is null)
                {
                    Should.Throw<ArgumentException>(() => session.AddFrame(width, height, format), $"{format} {options}");
                    continue;
                }
                var frame = session.AddFrame(width, height, format);
                output.WrittenCount.ShouldBeGreaterThan(0, "the header is written at AddFrame");
                // Random bands with a stride wider than a row.
                var rowBytes = width * format.BytesPerPixel;
                var stride = rowBytes + 5;
                var strided = new byte[stride * height];
                for (var y = 0; y < height; y++) pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(strided.AsSpan(y * stride));
                var row = 0;
                while (row < height)
                {
                    var n = Math.Min(random.Next(1, 4), height - row);
                    frame.WriteRows(strided.AsSpan(row * stride), n, stride, Ct);
                    row += n;
                }
                session.Commit(Ct);
                output.WrittenSpan.SequenceEqual(expected).ShouldBeTrue($"{format} {options}");
            }
        }
    }

    [Fact]
    public void The_encoder_emits_before_commit()
    {
        var pixels = Random8(64, 32, 4, 6);
        foreach (var options in EncoderOptions())
        {
            var file = EncoderEmitsBeforeCommit(o => PnmImageEncoder.Create(o, options), pixels, 64, 32, PixelFormat.Rgba32);
            file.ShouldBe(Buffered(pixels, 64, 32, PixelFormat.Rgba32, options), options.ToString());
        }
    }

    private sealed class CountingWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buffer = new byte[1 << 20];
        public long Count { get; private set; }
        public void Advance(int count) => Count += count;
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer;
        public Span<byte> GetSpan(int sizeHint = 0) => _buffer;
    }

    [Fact]
    public void A_large_image_encodes_in_a_band_of_memory()
    {
        const int width = 8192, height = 4096;
        var band = Random8(width, 16, 4, 7);
        foreach (var options in new[] { new PnmEncodeOptions(), new PnmEncodeOptions { Format = PnmFormat.Pixmap, MaxValue = 1000 }, new PnmEncodeOptions { Format = PnmFormat.PlainGraymap } })
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgra32 })
        {
            var writer = new CountingWriter();
            AllocatesLessThan(256 * 1024, () =>
            {
                using var session = PnmImageEncoder.Create(writer, options);
                var frame = session.AddFrame(width, height, format);
                for (var y = 0; y < height; y += 16)
                    frame.WriteRows(band, 16, 0, Ct);
                session.Commit(Ct);
            }, $"{options} {format}");
            writer.Count.ShouldBeGreaterThan((long)width * height / 2);
        }
    }

    // ---- end to end -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_async_only_stream_decodes_and_reencodes_row_by_row_to_the_same_file()
    {
        foreach (var (name, file, format) in new[]
        {
            ("ppm", PnmEncoder.Encode(Random8(300, 41, 3, 8), 300, 41, 3), PixelFormat.Rgb24),
            ("pgm 16", PnmEncoder.Encode(Enumerable.Range(0, 300 * 41).Select(i => (ushort)(i * 37)).ToArray(), 300, 41, 1), PixelFormat.Gray16),
            ("plain ppm", PnmEncoder.Encode(Random8(300, 41, 3, 9), 300, 41, 3, new PnmEncodeOptions { Format = PnmFormat.PlainPixmap }), PixelFormat.Rgb24),
        })
        {
            var options = new PnmEncodeOptions { Format = PnmDecoder.ReadInfo(file).Format };
            var output = new ArrayBufferWriter<byte>();
            await using (var reader = PnmImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(file, maxChunk: 997, asyncOnly: true)), format, null))
            {
                await reader.InitializeAsync(Ct);
                using var session = PnmImageEncoder.Create(output, options);
                var frame = session.AddFrame(reader.Width, reader.Height, format);
                var band = new byte[reader.Width * format.BytesPerPixel * 7];
                int n;
                while ((n = await reader.ReadRowsAsync(band, 7, 0, Ct)) > 0)
                    frame.WriteRows(band, n, 0, Ct);
                session.Commit(Ct);
            }
            output.WrittenSpan.SequenceEqual(file).ShouldBeTrue(name);
            RowsEqualDecodeInto(Codec, output.WrittenSpan.ToArray(), format, expectStreaming: true, feeds: [Feed.AsyncOnly]);
        }
    }
}

/// <summary>
/// Hostile input for the streaming Netpbm reader: random mutations of seed files, read with random feeds, either give
/// exactly the span decoder's rows or throw <see cref="InvalidDataException"/> where it refuses, within seconds.
/// <c>PNM_STREAM_FUZZ_ITERATIONS</c> (default 500) and <c>PNM_STREAM_FUZZ_SEED</c> set the soak.
/// </summary>
public sealed class PnmStreamingRobustnessTests
{
    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("PNM_STREAM_FUZZ_ITERATIONS"), out var n) ? n : 500;

    private static int Seed =>
        int.TryParse(Environment.GetEnvironmentVariable("PNM_STREAM_FUZZ_SEED"), out var n) ? n : 20261123;

    [Fact]
    public async Task Mutations_stream_as_the_span_decoder_decodes_them()
    {
        var seeds = PnmStreamingTests.Synthetic(7, 3).Concat(PnmStreamingTests.Hand().Where(h => h.File.Length < 400)).ToList();
        var formats = new[] { PixelFormat.Rgba32, PixelFormat.Gray8, PixelFormat.Rgb48 };
        var iterations = Iterations;
        var budget = TimeSpan.FromSeconds(Math.Max(60, iterations / 20));
        var current = "";
        var run = Task.Run(() =>
        {
            var random = new Random(Seed);
            for (var i = 0; i < iterations; i++)
            {
                var (name, seed) = seeds[random.Next(seeds.Count)];
                var data = Mutate(seed, random, out var description);
                current = $"#{i} {name}: {description}";
                var timer = System.Diagnostics.Stopwatch.StartNew();
                PnmStreamingTests.SameOutcome(data, current, formats[random.Next(formats.Length)], i);
                timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), current);
            }
        }, TestContext.Current.CancellationToken);
        if (await Task.WhenAny(run, Task.Delay(budget, TestContext.Current.CancellationToken)) != run)
            throw new ShouldAssertException($"The fuzz loop is stuck at {current}");
        await run;
    }

    private static byte[] Mutate(byte[] seed, Random random, out string description)
    {
        var data = seed.ToArray();
        var span = random.Next(2) == 0 ? Math.Min(64, data.Length) : data.Length;
        var at = random.Next(Math.Max(1, span));
        switch (random.Next(7))
        {
            case 0:
                data[at] ^= (byte)(1 << random.Next(8));
                description = $"bit flip at {at}";
                break;
            case 1:
                data[at] = (byte)random.Next(256);
                description = $"byte {at} = {data[at]}";
                break;
            case 2:
                data[at] = "0123456789 \n#9\r"u8[random.Next(15)];
                description = $"header character at {at}";
                break;
            case 3:
            {
                var n = random.Next(2, 9);
                for (var k = 0; k < n; k++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                description = $"{n} flips";
                break;
            }
            case 4:
                description = $"cut to {at}";
                return data[..at];
            case 5:
                description = $"bytes inserted at {at}";
                return [.. data[..at], .. "99 #c\n"u8, .. data[at..]];
            default:
                var length = Math.Min(random.Next(1, 16), data.Length - at);
                description = $"{length} bytes deleted at {at}";
                return [.. data[..at], .. data[(at + length)..]];
        }
        return data;
    }
}
