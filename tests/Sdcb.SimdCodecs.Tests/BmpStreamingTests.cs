using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// BMP streaming: <see cref="BmpImageDecoder.CreateRowReader"/> hands out <c>TryDecodeInto</c>'s rows for every
/// uncompressed layout — top-down from any stream, bottom-up from a seekable one (read forward, then seeked back a
/// band at a time), a 32-bit <c>BI_RGB</c> alpha scan — and falls back for what it cannot stream; the streaming
/// encoder writes exactly the buffered session's bytes before <c>Commit</c>.
/// </summary>
public sealed class BmpStreamingTests
{
    private static readonly StreamingAssert.Feed[] Seekable = [StreamingAssert.Feed.Whole];
    private static readonly StreamingAssert.Feed[] Unseekable =
        [StreamingAssert.Feed.OneByte, StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly];

    private static readonly PixelFormat[] Formats =
        [PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24, PixelFormat.Gray8];

    private static readonly PixelFormat[] OtherFormats = [PixelFormat.Rgba64, PixelFormat.GrayAlpha16];

    private static ImageDecoderRegistration Decoder => BmpImageDecoder.Registration;

    private static bool Basic(PixelFormat format) => format.IsBasic8;

    /// <summary>A seeded pseudo-random pixel for (x, y) in <paramref name="bitCount"/> bits, its channels staying in
    /// range for the masks the layouts below use.</summary>
    private static uint Value(int x, int y, int bitCount)
    {
        var v = (uint)(x * 7919 + y * 104729 + 12345);
        v ^= v >> 13;
        return bitCount switch
        {
            1 or 2 or 4 or 8 => v & ((1u << bitCount) - 1),
            16 => v & 0xFFFF,
            _ => bitCount == 24 ? v & 0xFFFFFF : v,
        };
    }

    private static int[] Palette(int colors) => [.. Enumerable.Range(0, colors).Select(i => (i * 0x010307 * 37) & 0xFFFFFF)];

    /// <summary>The layouts streamed, each a whole file: name, file, whether a stream that cannot seek can stream it
    /// (top-down, and no alpha scan for a 32-bit <c>BI_RGB</c>).</summary>
    public static TheoryData<string> Variants() =>
        ["rgb24", "rgb32", "bitfields32", "alphabitfields32", "rgb555", "rgb565", "bitfields16", "p8", "p4", "p2", "p1", "core24", "core8"];

    [Theory]
    [MemberData(nameof(Variants))]
    public void Rows_equal_decode_into_every_feed(string variant)
    {
        foreach (var (file, topDown, alphaAmbiguous) in Files(variant))
        {
            foreach (var format in AllFormats)
            {
                var because = $"{variant} {file.Length} bytes to {format}";
                var streaming = Basic(format) && topDown && !(alphaAmbiguous && format.HasAlpha);
                if (Basic(format))
                    StreamingAssert.RowsEqualDecodeInto(Decoder, file, format, expectStreaming: true, feeds: Seekable);
                StreamingAssert.RowsEqualDecodeInto(Decoder, file, format, expectStreaming: streaming, feeds: Unseekable);
                InfoMatches(file, format, because);
            }
        }
    }

    private static PixelFormat[] AllFormats => [.. Formats, .. OtherFormats];

    private static void InfoMatches(byte[] file, PixelFormat format, string because)
    {
        var create = Decoder.CreateRowReader!;
        using var reader = create(new StreamInput(new MemoryStream(file, writable: false)), format, null);
        reader.Initialize(TestContext.Current.CancellationToken);
        BmpImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue(because);
        reader.Width.ShouldBe(info.Width, because);
        reader.Height.ShouldBe(info.Height, because);
        reader.Info.Channels.ShouldBe(info.Channels, because);
        reader.Info.BitDepth.ShouldBe(info.BitDepth, because);
        reader.Info.Resolution.ShouldBe(info.Resolution, because);
        reader.Info.ColorEncoding.ShouldBe(info.ColorEncoding, because);
    }

    /// <summary>Small files of each layout, top-down and bottom-up (pixels at least 3×3 where a palette needs a few).</summary>
    private static (byte[] File, bool TopDown, bool AlphaAmbiguous)[] Files(string variant)
    {
        const int width = 37, height = 21;
        var random = new Random(variant.Length * 31 + 7);
        byte[] Build(bool topDown) => variant switch
        {
            "rgb24" => TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 24, 0),
                [], TestBmpWriter.Rows(width, height, 24, (x, y) => Value(x, y, 24), topDown)),
            "rgb32" => TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 32, 0),
                [], TestBmpWriter.Rows(width, height, 32, (x, y) => Value(x, y, 32), topDown)),
            "bitfields32" => TestBmpWriter.File(
                TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 32, 3),
                TestBmpWriter.Masks(0x00FF0000, 0x0000FF00, 0x000000FF),
                TestBmpWriter.Rows(width, height, 32, (x, y) => Value(x, y, 32), topDown)),
            "alphabitfields32" => TestBmpWriter.File(
                TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 32, 6),
                TestBmpWriter.Masks(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
                TestBmpWriter.Rows(width, height, 32, (x, y) => Value(x, y, 32), topDown)),
            "rgb555" => TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 16, 0),
                [], TestBmpWriter.Rows(width, height, 16, (x, y) => Value(x, y, 16) & 0x7FFF, topDown)),
            "rgb565" => TestBmpWriter.File(
                TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 16, 3),
                TestBmpWriter.Masks(0xF800, 0x07E0, 0x001F),
                TestBmpWriter.Rows(width, height, 16, (x, y) => Value(x, y, 16), topDown)),
            "bitfields16" => TestBmpWriter.File(
                TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 16, 3),
                TestBmpWriter.Masks(0x7C00, 0x03E0, 0x001F),
                TestBmpWriter.Rows(width, height, 16, (x, y) => Value(x, y, 16) & 0x7FFF, topDown)),
            "p8" or "p4" or "p2" or "p1" => PaletteFile(width, height, topDown, variant[1] - '0'),
            "core24" => TestBmpWriter.File(TestBmpWriter.CoreHeader(width, height, 24), [],
                TestBmpWriter.Rows(width, height, 24, (x, y) => Value(x, y, 24), topDown)),
            "core8" => TestBmpWriter.File(TestBmpWriter.CoreHeader(width, height, 8),
                TestBmpWriter.Palette(Palette(256), entrySize: 3),
                TestBmpWriter.Rows(width, height, 8, (x, y) => Value(x, y, 8), topDown)),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var alphaAmbiguous = variant == "rgb32";
        var core = variant.StartsWith("core", StringComparison.Ordinal); // a core header cannot be top-down
        return [(Build(topDown: false), false, alphaAmbiguous), (Build(topDown: !core), !core, alphaAmbiguous)];
    }

    private static byte[] PaletteFile(int width, int height, bool topDown, int bits)
    {
        var colors = 1 << bits;
        return TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, bits, 0),
            TestBmpWriter.Palette(Palette(colors)),
            TestBmpWriter.Rows(width, height, bits, (x, y) => Value(x, y, bits), topDown));
    }

    [Fact]
    public void Alpha_scan_matches_the_whole_image_decision()
    {
        const int width = 33, height = 9;
        // Fourth byte 0 everywhere: opaque, as a whole-image decode decides.
        var zero = TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, height, 32, 0), [],
            TestBmpWriter.Rows(width, height, 32, (x, y) => Value(x, y, 24)));
        // One pixel with alpha: the fourth byte is alpha, kept.
        var one = (byte[])zero.Clone();
        var pixelOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(zero.AsSpan(10));
        one[pixelOffset + 4 * (height * width - 1) + 3] = 0x40;
        foreach (var file in new[] { zero, one })
        {
            StreamingAssert.RowsEqualDecodeInto(Decoder, file, PixelFormat.Rgba32, expectStreaming: true, feeds: Seekable);
            StreamingAssert.RowsEqualDecodeInto(Decoder, file, PixelFormat.Rgba32, expectStreaming: false, feeds: Unseekable);
        }
        var rows = new byte[width * height * 4];
        BmpImageDecoder.TryDecodeInto(zero, rows, width * 4, PixelFormat.Rgba32, null, TestContext.Current.CancellationToken).ShouldBeTrue();
        for (var i = 3; i < rows.Length; i += 4) rows[i].ShouldBe((byte)255, "a zero fourth byte is not alpha");
    }

    [Fact]
    public void Last_rows_missing_padding_stream()
    {
        const int width = 19, height = 5;
        var stride = (width * 24 + 31) / 32 * 4;
        var rows = TestBmpWriter.Rows(width, height, 24, (x, y) => Value(x, y, 24));
        // The file stops after the last row's pixels: 1 byte of its 3 padding bytes missing.
        var shortFile = TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, height, 24, 0), [],
            rows[..^1]);
        StreamingAssert.RowsEqualDecodeInto(Decoder, shortFile, PixelFormat.Rgba32, expectStreaming: true, feeds: Seekable);
        StreamingAssert.RowsEqualDecodeInto(Decoder, shortFile, PixelFormat.Rgba32, expectStreaming: false, feeds: Unseekable);
        var topDown = TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, -height, 24, 0), [], rows[..^1]);
        StreamingAssert.RowsEqualDecodeInto(Decoder, topDown, PixelFormat.Bgr24, expectStreaming: true);
        stride.ShouldBeGreaterThan(3 * width); // the case is the padding, not the pixels
    }

    [Fact]
    public void Unstreamable_variants_fall_back()
    {
        const int width = 17, height = 6;
        var rle = TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, height, 8, 1), TestBmpWriter.Palette(Palette(256)),
            new TestBmpWriter.Rle(width, height, 8, Palette(256)).Run(width * height, 3).Stream);
        StreamingAssert.RowsEqualDecodeInto(Decoder, rle, PixelFormat.Rgba32, expectStreaming: false);

        var topDown = TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, -height, 24, 0), [],
            TestBmpWriter.Rows(width, height, 24, (x, y) => Value(x, y, 24), topDown: true));

        // Decode options other than the whole image.
        var scaled = new DecodeOptions { ScaleDenominator = 2 };
        StreamingAssert.RowsEqualDecodeInto(Decoder, topDown, PixelFormat.Rgba32, scaled, expectStreaming: false);
        var region = new DecodeOptions { Region = new PixelRect(0, 0, 8, 4) };
        StreamingAssert.RowsEqualDecodeInto(Decoder, topDown, PixelFormat.Rgba32, region, expectStreaming: false);

        // Output formats other than the 8-bit basic ones.
        StreamingAssert.RowsEqualDecodeInto(Decoder, topDown, PixelFormat.Rgba64, expectStreaming: false);
        StreamingAssert.RowsEqualDecodeInto(Decoder, topDown, PixelFormat.GrayAlpha16, expectStreaming: false);

        // A pixel offset into the headers: the whole-file parser derives the pixels' place from the file's length.
        var intoHeader = TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, -height, 24, 0), [],
            TestBmpWriter.Rows(width, height, 24, (x, y) => Value(x, y, 24), topDown: true), offBits: 20);
        StreamingAssert.RowsEqualDecodeInto(Decoder, intoHeader, PixelFormat.Rgba32, expectStreaming: false);

        // A V5 profile stored after the pixels: what ColorEncoding says depends on it.
        var v5 = V5Profile(width, height, profileBeforePixels: false);
        StreamingAssert.RowsEqualDecodeInto(Decoder, v5, PixelFormat.Rgba32, expectStreaming: false);

        // A BI_PNG bitmap.
        using var image = BmpMagickTests.Source(width, height, 4, false, 3);
        var png = BmpFromPng(image);
        StreamingAssert.RowsEqualDecodeInto(Decoder, png, PixelFormat.Rgba32, expectStreaming: false);
    }

    private static byte[] BmpFromPng(MagickImage image)
    {
        var png = image.ToByteArray(MagickFormat.Png32);
        var header = TestBmpWriter.InfoHeader(40, (int)image.Width, (int)image.Height, 0, 5);
        return TestBmpWriter.File(header, [], png);
    }

    private static readonly byte[] Profile = [.. Enumerable.Range(0, 40).Select(i => (byte)(i * 7 + 1))];

    private static byte[] V5Profile(int width, int height, bool profileBeforePixels)
    {
        const int headerSize = 124;
        var rows = TestBmpWriter.Rows(width, height, 24, (x, y) => Value(x, y, 24), topDown: true);
        // bV5ProfileData counts from the bitmap header's start; the mask of a V5 header is inside it.
        var profileOffset = profileBeforePixels ? headerSize : headerSize + rows.Length;
        var header = TestBmpWriter.InfoHeader(headerSize, width, -height, 24, 0, colorSpace: 0x4D424544,
            profileOffset: (uint)profileOffset, profileSize: (uint)Profile.Length);
        return profileBeforePixels
            ? TestBmpWriter.File(header, Profile, rows)
            : TestBmpWriter.File(header, [], TestBmpWriter.Concat(rows, Profile));
    }

    [Fact]
    public void Profile_before_the_pixels_streams_with_its_colour()
    {
        var file = V5Profile(21, 13, profileBeforePixels: true);
        var rows = StreamingAssert.RowsEqualDecodeInto(Decoder, file, PixelFormat.Rgba32, expectStreaming: true, feeds: Seekable);
        rows.Length.ShouldBe(21 * 13 * 4);
        BmpImageDecoder.TryReadMetadata(file, out var metadata).ShouldBeTrue();
        metadata!.Icc.ToArray().ShouldBe(Profile);
    }

    [Fact]
    public void Truncated_pixels_throw_invalid_data()
    {
        const int width = 41, height = 11;
        foreach (var topDown in new[] { false, true })
        {
            var file = TestBmpWriter.File(TestBmpWriter.InfoHeader(40, width, topDown ? -height : height, 24, 0), [],
                TestBmpWriter.Rows(width, height, 24, (x, y) => Value(x, y, 24), topDown));
            foreach (var cut in new[] { file.Length - 3 * width * height / 2, file.Length - 2, file.Length - 2 * width * 3 })
            {
                if (cut <= 54) continue;
                var truncated = file[..cut];
                foreach (var feed in StreamingAssert.AllFeeds)
                {
                    Stream stream = feed == StreamingAssert.Feed.Whole
                        ? new MemoryStream(truncated, writable: false) as Stream
                        : new ChunkedStream(truncated, maxChunk: feed == StreamingAssert.Feed.OneByte ? 1 : 4096,
                            asyncOnly: feed == StreamingAssert.Feed.AsyncOnly);
                    using var input = new StreamInput(stream, initialCapacity: 16);
                    using var reader = Decoder.CreateRowReader!(input, PixelFormat.Rgba32, null);
                    var because = $"topDown {topDown}, cut at {cut} of {file.Length}, {feed}";
                    if (feed == StreamingAssert.Feed.AsyncOnly)
                        Should.Throw<InvalidDataException>(() => DrainAsync(reader).GetAwaiter().GetResult(), because);
                    else
                        Should.Throw<InvalidDataException>(() => Drain(reader), because);
                }
            }
        }
    }

    private static async Task DrainAsync(StreamingRowReader reader)
    {
        await reader.InitializeAsync(TestContext.Current.CancellationToken);
        var row = new byte[reader.Width * reader.Format.BytesPerPixel];
        while (reader.RowsRead < reader.Height)
            await reader.ReadRowsAsync(row, 1, 0, TestContext.Current.CancellationToken);
    }

    private static void Drain(StreamingRowReader reader)
    {
        reader.Initialize(TestContext.Current.CancellationToken);
        var row = new byte[reader.Width * reader.Format.BytesPerPixel];
        while (reader.RowsRead < reader.Height)
            reader.ReadRows(row, 1, 0, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Decode_streams_bounded_memory()
    {
        const int width = 8192, height = 4096;
        var file = TopDown(width, height, 24);
        StreamingAssert.AllocatesLessThan(8 * 1024 * 1024, () =>
        {
            using var input = new StreamInput(new ChunkedStream(file, maxChunk: 4001, seed: 5), initialCapacity: 64 * 1024);
            using var reader = (StreamingRowReader)Decoder.CreateRowReader!(input, PixelFormat.Bgra32, null);
            reader.Initialize(TestContext.Current.CancellationToken);
            reader.IsStreaming.ShouldBeTrue();
            reader.ReadRows(new byte[width * 4], 1, 0, TestContext.Current.CancellationToken);
        }, "streaming a 8192x4096 bitmap");
    }

    private static byte[] TopDown(int width, int height, int bits)
    {
        var stride = (int)(((long)width * bits + 31) / 32 * 4);
        var file = new byte[54 + (long)stride * height];
        TestBmpWriter.InfoHeader(40, width, -height, bits, 0).CopyTo(file, 14);
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), 54);
        for (var i = 54; i < file.Length; i++) file[i] = (byte)(i * 31 + (i >> 8));
        return file;
    }

    // ---- Encoder -----------------------------------------------------------------------------------------------

    private static IImageEncoderSession CreateSession(IBufferWriter<byte> output, EncodeOptions? options) =>
        BmpImageEncoder.Create(output, options);

    public static TheoryData<string> EncodeLayouts() => ["rgb24", "rgb32", "argb32", "rgb565", "rgb555", "argb32-v5"];

    [Theory]
    [MemberData(nameof(EncodeLayouts))]
    public void Encoder_streams_the_buffered_bytes(string layout)
    {
        const int width = 43, height = 17;
        var options = layout switch
        {
            "rgb24" => new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb24, TopDown = true },
            "rgb32" => new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb32, TopDown = true },
            "argb32" => new BmpEncodeOptions { Format = BmpEncodeFormat.Argb32, TopDown = true },
            "rgb565" => new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb565, TopDown = true },
            "rgb555" => new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb555, TopDown = true, Header = BmpHeaderVersion.V5 },
            _ => new BmpEncodeOptions { Format = BmpEncodeFormat.Argb32, TopDown = true, IccProfile = Profile },
        };
        foreach (var format in AllFormats)
        {
            var pixels = RandomPixels(width, height, format, layout.Length);
            var streamed = StreamingAssert.EncoderEmitsBeforeCommit(o => CreateSession(o, options), pixels, width, height, format);
            // What the buffered session writes: the whole image, converted to the 8-bit layout BMP takes, encoded.
            var native = BmpImageEncoder.GetClosestInputFormat(format);
            var whole = native == format ? pixels : PixelConverter.ToPacked(pixels, 0, format, native, width, height);
            var buffered = BmpEncoder.Encode(whole, width, height, native, options);
            streamed.ShouldBe(buffered, $"streamed {layout} from {format}");
            // The same bytes written in random bands with padded strides.
            ChunkedEncode(pixels, width, height, format, options, layout.Length).ShouldBe(buffered, $"banded {layout} from {format}");
            InfoDecodes(streamed, width, height, layout);
        }
    }

    private static byte[] ChunkedEncode(byte[] pixels, int width, int height, PixelFormat format, BmpEncodeOptions options, int seed)
    {
        var output = new ArrayBufferWriter<byte>(16);
        using var session = BmpImageEncoder.Create(output, options);
        session.IsStreaming.ShouldBeTrue();
        var frame = session.AddFrame(width, height, format);
        var random = new Random(seed);
        var rowBytes = width * format.BytesPerPixel;
        for (var y = 0; y < height;)
        {
            var n = Math.Min(height - y, random.Next(1, 6));
            var stride = rowBytes + random.Next(0, 9);
            var band = new byte[stride * n];
            for (var i = 0; i < n; i++) pixels.AsSpan((y + i) * rowBytes, rowBytes).CopyTo(band.AsSpan(i * stride));
            frame.WriteRows(band, n, stride, TestContext.Current.CancellationToken);
            y += n;
        }
        session.Commit(TestContext.Current.CancellationToken);
        return output.WrittenSpan.ToArray();
    }

    private static void InfoDecodes(byte[] file, int width, int height, string layout)
    {
        BmpImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue(layout);
        (info.Width, info.Height).ShouldBe((width, height), layout);
        var back = new byte[width * height * 4];
        BmpImageDecoder.TryDecodeInto(file, back, width * 4, PixelFormat.Rgba32).ShouldBeTrue(layout);
    }

    private static byte[] RandomPixels(int width, int height, PixelFormat format, int seed)
    {
        var pixels = new byte[width * height * format.BytesPerPixel];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    [Fact]
    public void Encoder_does_not_stream_what_it_cannot()
    {
        foreach (var options in new EncodeOptions[]
        {
            new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb24 },
            new BmpEncodeOptions { Format = BmpEncodeFormat.Auto, TopDown = true },
            new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed8, TopDown = true },
            new BmpEncodeOptions { Format = BmpEncodeFormat.Indexed8, Rle = true },
        })
        {
            using var session = BmpImageEncoder.Create(new ArrayBufferWriter<byte>(), options);
            session.IsStreaming.ShouldBeFalse(options.ToString());
        }
        foreach (var options in new EncodeOptions[]
        {
            new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb24, TopDown = true },
            new BmpEncodeOptions { Format = BmpEncodeFormat.Argb32, TopDown = true },
        })
        {
            using var session = BmpImageEncoder.Create(new ArrayBufferWriter<byte>(), options);
            session.IsStreaming.ShouldBeTrue(options.ToString());
        }
    }

    [Fact]
    public void Encoder_writes_bounded_memory()
    {
        const int width = 8192, height = 4096;
        var pixels = new byte[width * height * 4];
        var options = new BmpEncodeOptions { Format = BmpEncodeFormat.Argb32, TopDown = true };
        var sink = new CountingWriter();
        StreamingAssert.AllocatesLessThan(4 * 1024 * 1024, () =>
        {
            sink.Reset();
            using var session = BmpImageEncoder.Create(sink, options);
            session.IsStreaming.ShouldBeTrue();
            var frame = session.AddFrame(width, height, PixelFormat.Rgba32);
            frame.WriteRows(pixels, height, 0, TestContext.Current.CancellationToken);
            session.Commit(TestContext.Current.CancellationToken);
        }, "streaming a 8192x4096 bitmap");
        sink.Count.ShouldBe(14 + 108 + 8192 * 4 * 4096); // a V4 header for alpha
    }

    /// <summary>An output that keeps only the count, so a streaming write allocates nothing new.</summary>
    private sealed class CountingWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[64 * 1024];

        public long Count { get; private set; }

        public void Reset() => Count = 0;

        public void Advance(int count) => Count += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (_buffer.Length < sizeHint) _buffer = new byte[sizeHint];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    // ---- End to end --------------------------------------------------------------------------------------------

    [Fact]
    public void Magick_bottom_up_file_streams_from_a_file_and_falls_back_from_a_body()
    {
        using var image = BmpMagickTests.Source(64, 40, 500, false, 11);
        var file = image.ToByteArray(MagickFormat.Bmp3);
        BmpDecoder.ReadInfo(file).TopDown.ShouldBeFalse();
        var name = Path.Combine(Path.GetTempPath(), $"bmp-stream-{Environment.ProcessId}.bmp");
        try
        {
            File.WriteAllBytes(name, file);
            using var actual = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var reader = Decoder.CreateRowReader!(new StreamInput(actual), PixelFormat.Rgba32, null);
            reader.Initialize(TestContext.Current.CancellationToken);
            reader.IsStreaming.ShouldBeTrue();
            var rows = new byte[64 * 40 * 4];
            while (reader.RowsRead < reader.Height)
                reader.ReadRows(rows.AsSpan(reader.RowsRead * 64 * 4), reader.Height - reader.RowsRead, 0,
                    TestContext.Current.CancellationToken);
            var expected = new byte[rows.Length];
            BmpImageDecoder.TryDecodeInto(file, expected, 64 * 4, PixelFormat.Rgba32, null, TestContext.Current.CancellationToken).ShouldBeTrue();
            rows.ShouldBe(expected);
        }
        finally
        {
            File.Delete(name);
        }
        StreamingAssert.RowsEqualDecodeInto(Decoder, file, PixelFormat.Rgba32, expectStreaming: false, feeds: Unseekable);
    }

    [Fact]
    public void Top_down_file_round_trips_through_the_streaming_encoder()
    {
        const int width = 61, height = 23;
        var pixels = RandomPixels(width, height, PixelFormat.Bgra32, 9);
        var options = new BmpEncodeOptions { Format = BmpEncodeFormat.Argb32, TopDown = true };
        var encoded = StreamingAssert.EncoderEmitsBeforeCommit(o => BmpImageEncoder.Create(o, options), pixels, width, height, PixelFormat.Bgra32);
        BmpDecoder.ReadInfo(encoded).TopDown.ShouldBeTrue();
        var name = Path.Combine(Path.GetTempPath(), $"bmp-rt-{Environment.ProcessId}.bmp");
        try
        {
            File.WriteAllBytes(name, encoded);
            using var file = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            using var reader = Decoder.CreateRowReader!(new StreamInput(file), PixelFormat.Bgra32, null);
            reader.Initialize(TestContext.Current.CancellationToken);
            reader.IsStreaming.ShouldBeTrue();
            var rows = new byte[pixels.Length];
            while (reader.RowsRead < reader.Height)
                reader.ReadRows(rows.AsSpan(reader.RowsRead * width * 4), reader.Height - reader.RowsRead, 0,
                    TestContext.Current.CancellationToken);
            rows.ShouldBe(pixels);
        }
        finally
        {
            File.Delete(name);
        }
    }

    /// <summary>Mutations of seed bitmaps, fed every way: <c>BMP_STREAM_FUZZ_ITERATIONS</c> (default 600; soak at 300000).</summary>
    private static int FuzzIterations =>
        int.TryParse(Environment.GetEnvironmentVariable("BMP_STREAM_FUZZ_ITERATIONS"), out var n) ? n : 600;

    [Fact]
    public void Bottom_up_bands_from_a_seekable_stream()
    {
        // Several bands (BmpRowReader.BandBytes each), a band boundary inside a row band the caller asks for, and short
        // reads, so that seeking back meets a part-filled buffer.
        foreach (var (width, height, bits) in new[] { (1000, 300, 24), (700, 211, 32), (3001, 97, 8), (90000, 5, 24) })
        {
            var rows = TestBmpWriter.Rows(width, height, bits, (x, y) => Value(x, y, bits));
            var header = TestBmpWriter.InfoHeader(40, width, height, bits, 0);
            var file = bits == 8
                ? TestBmpWriter.File(header, TestBmpWriter.Palette(Palette(256)), rows)
                : TestBmpWriter.File(header, [], rows);
            foreach (var format in Formats)
            {
                var expected = StreamingAssert.RowsEqualDecodeInto(Decoder, file, format, expectStreaming: true, feeds: Seekable);
                foreach (var seed in new[] { 1, 2 })
                {
                    var actual = StreamingAssert.ReadAll(file, StreamingAssert.Feed.RandomChunks,
                        input => Decoder.CreateRowReader!(new StreamInput(new SeekableChunkedStream(file, 70000, seed), initialCapacity: 16), format, null),
                        out var streaming, out _, out _, seed);
                    streaming.ShouldBeTrue();
                    actual.AsSpan().SequenceEqual(expected).ShouldBeTrue($"{width}x{height}x{bits} to {format}, seed {seed}");
                }
            }
        }
    }

    [Fact]
    public void Bottom_up_streams_bounded_memory()
    {
        const int width = 8192, height = 4096;
        var file = TopDown(width, height, 32);
        // Bottom-up, 32-bit BI_RGB to RGBA: the forward pass also scans the alpha bytes.
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), height);
        StreamingAssert.AllocatesLessThan(8 * 1024 * 1024, () =>
        {
            using var input = new StreamInput(new MemoryStream(file, writable: false));
            using var reader = Decoder.CreateRowReader!(input, PixelFormat.Rgba32, null);
            reader.Initialize(TestContext.Current.CancellationToken);
            reader.IsStreaming.ShouldBeTrue();
            var band = new byte[width * 4 * 16];
            while (reader.RowsRead < reader.Height)
                reader.ReadRows(band, 16, 0, TestContext.Current.CancellationToken);
        }, "streaming a bottom-up 8192x4096 bitmap");
    }

    [Fact]
    public void Retained_input_streams_bottom_up_without_seeking()
    {
        // An input that keeps every byte read can go back without seeking: no output frame is buffered.
        var (file, _, _) = Files("rgb32")[0];
        var expected = new byte[37 * 21 * 4];
        BmpImageDecoder.TryDecodeInto(file, expected, 0, PixelFormat.Rgba32, null, TestContext.Current.CancellationToken).ShouldBeTrue();
        foreach (var feed in new[] { StreamingAssert.Feed.OneByte, StreamingAssert.Feed.RandomChunks })
        {
            var rows = StreamingAssert.ReadAll(file, feed,
                input => Decoder.CreateRowReader!(new StreamInput(new ChunkedStream(file, feed == StreamingAssert.Feed.OneByte ? 1 : 300),
                    retainConsumed: true, initialCapacity: 16), PixelFormat.Rgba32, null), out var streaming, out _, out _);
            streaming.ShouldBeTrue(feed.ToString());
            rows.ShouldBe(expected, feed.ToString());
        }
    }

    [Fact]
    public void Alpha_or_matches_a_byte_at_a_time()
    {
        var random = new Random(77);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            if (random.Next(3) > 0)
            {
                // Mostly zero fourth bytes, with now and then one set, so that a missed byte shows.
                random.NextBytes(bytes);
                var offset0 = random.Next(4);
                for (var k = (3 - offset0 + 4) % 4; k < bytes.Length; k += 4) bytes[k] = 0;
                if (bytes.Length > 0 && random.Next(2) == 0) bytes[random.Next(bytes.Length)] = (byte)random.Next(1, 256);
                Check(bytes, offset0 + 4L * random.Next(1000));
            }
            else
            {
                random.NextBytes(bytes);
                Check(bytes, random.Next(1 << 20));
            }
        }

        static void Check(byte[] bytes, long offset)
        {
            uint expected = 0;
            for (var k = 0; k < bytes.Length; k++)
                if (((offset + k) & 3) == 3) expected |= bytes[k];
            BmpRowReader.AlphaOr(bytes, offset).ShouldBe(expected, $"{bytes.Length} bytes at {offset}");
        }
    }

    /// <summary>A seekable stream that returns 1..<c>maxChunk</c> bytes per read.</summary>
    private sealed class SeekableChunkedStream(byte[] data, int maxChunk, int seed) : MemoryStream(data, writable: false)
    {
        private readonly Random _random = new(seed);

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, _random.Next(1, maxChunk + 1)));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, _random.Next(1, maxChunk + 1))]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, _random.Next(1, maxChunk + 1))], cancellationToken);
    }

    [Fact]
    public void Nothing_but_invalid_data_from_malformed_streams()
    {
        var seeds = new List<byte[]>();
        foreach (var variant in new[] { "rgb24", "p8", "rgb32", "alphabitfields32" })
        {
            var (file, _, _) = Files(variant)[0];
            seeds.Add(file);
            seeds.Add(Files(variant)[1].File);
        }
        seeds.Add(V5Profile(9, 5, profileBeforePixels: true));
        var random = new Random(20261123);
        for (var i = 0; i < FuzzIterations; i++)
        {
            var file = (byte[])seeds[random.Next(seeds.Count)].Clone();
            var edits = random.Next(1, 6);
            for (var e = 0; e < edits && file.Length > 0; e++)
                file[random.Next(file.Length)] = (byte)random.Next(256);
            if (random.Next(4) == 0 && file.Length > 60)
                file = file[..random.Next(40, file.Length)];
            var feed = random.Next(4);
            CheckStreaming(file, feed);
        }
    }

    private static void CheckStreaming(byte[] file, int feed)
    {
        var format = feed == 2 ? PixelFormat.Bgra32 : PixelFormat.Rgba32;
        Stream stream = feed switch
        {
            0 => new ChunkedStream(file, maxChunk: 1 + (file.Length % 97), seed: 3),
            1 => new MemoryStream(file, writable: false),
            3 => new SeekableChunkedStream(file, maxChunk: 1 + (file.Length % 61), seed: 5),
            _ => new ChunkedStream(file, maxChunk: 4096, seed: 4, asyncOnly: true),
        };
        var expected = new byte[(long)4 * 1 << 16];
        var decoded = BmpImageDecoder.TryReadInfo(file, out var info)
            && PixelConverter.TryGetLayout(info.Width, info.Height, format, 0, expected.Length, out var stride)
            && BmpImageDecoder.TryDecodeInto(file, expected, stride, format, null, default);
        try
        {
            using var input = new StreamInput(stream, initialCapacity: 16);
            using var reader = BmpImageDecoder.CreateRowReader(input, format, null);
            var ct = TestContext.Current.CancellationToken;
            if (feed == 2) reader.InitializeAsync(ct).AsTask().GetAwaiter().GetResult();
            else reader.Initialize(ct);
            var rowBytes = reader.Width * format.BytesPerPixel;
            var rows = ArrayPool<byte>.Shared.Rent(rowBytes * reader.Height);
            try
            {
                while (reader.RowsRead < reader.Height)
                {
                    var at = reader.RowsRead * rowBytes;
                    var n = Math.Min(7, reader.Height - reader.RowsRead);
                    if (feed == 2) reader.ReadRowsAsync(rows.AsMemory(at), n, 0, ct).AsTask().GetAwaiter().GetResult();
                    else reader.ReadRows(rows.AsSpan(at), n, 0, ct);
                }
                decoded.ShouldBeTrue("streaming decoded what TryDecodeInto refused");
                rows.AsSpan(0, rowBytes * reader.Height).SequenceEqual(expected.AsSpan(0, rowBytes * reader.Height)).ShouldBeTrue(
                    "the mutated file streamed to different rows");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rows);
            }
        }
        catch (InvalidDataException)
        {
        }
    }
}
