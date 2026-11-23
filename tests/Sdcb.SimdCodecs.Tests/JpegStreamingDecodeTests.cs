using System.Reflection;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The streaming JPEG reader: its rows are <see cref="JpegImageDecoder.TryDecodeInto"/>'s byte for byte, fed a stream every
/// way a network hands one over, at full size and at the DCT-domain scales; the variants it cannot stream fall back
/// transparently; a truncated stream fails where the whole decode fails; and its memory stays at about two MCU rows.
/// </summary>
public sealed class JpegStreamingDecodeTests
{
    private const long MaxPixels = 1 << 22;

    private static ImageDecoderRegistration Codec => JpegImageDecoder.Registration;

    private static DecodeOptions Scale(int denominator) => new() { ScaleDenominator = denominator };

    private static readonly PixelFormat[] Formats =
    [
        PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24,
        PixelFormat.Gray8, PixelFormat.GrayAlpha16, PixelFormat.Rgba64, PixelFormat.Rgba32Premultiplied,
    ];

    public static IEnumerable<object[]> Cases() => JpegDecoderOracleTests.CaseLabels();

    /// <summary>Every case of the decode oracle matrix — every sampling, grayscale, CMYK / YCCK, restarts, non-interleaved,
    /// the truncated salvage cases — through every feed and format, at full size and at 1/2.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Rows_EqualDecodeInto(string label)
    {
        var (jpeg, _) = JpegDecoderOracleTests.BuildCase(label);
        if (!Codec.TryReadInfo(jpeg, out var info))
            return; // not decodable at all: nothing to compare
        if ((long)info.Width * info.Height > MaxPixels)
            return;
        var streams = FirstScanInterleavesAll(jpeg);
        foreach (var format in Formats)
        {
            StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, format, null, streams);
            if (streams)
                StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, format, Scale(2), true);
        }
    }

    /// <summary>The streaming rule, read independently: a sequential frame whose first SOS names every component.</summary>
    private static bool FirstScanInterleavesAll(byte[] jpeg)
    {
        var info = JpegDecoder.ReadInfo(jpeg);
        if (info.Progressive)
            return false;
        var segments = JpegSegmentScanner.Scan(jpeg);
        var sos = segments[^1];
        return sos.Marker == JpegSegmentScanner.Sos && sos.Payload(jpeg)[0] == info.Components;
    }
    /// <summary>Encoded variants the encoder writes, restart intervals and optimized Huffman tables among them.</summary>
    public static TheoryData<string> EncodedShapes() =>
    [
        "4:2:0 restart 1", "4:2:0 restart 7", "4:4:4 optimized", "4:2:0 optimized restart 4", "gray restart 2",
        "4:2:0 single mcu", "4:2:0 one row", "4:2:2 magick", "4:1:1 writer restart 2",
    ];

    [Theory]
    [MemberData(nameof(EncodedShapes))]
    public void EncodedVariants_StreamExactly(string shape)
    {
        var (w, h) = shape switch
        {
            "4:2:0 single mcu" => (16, 16),
            "4:2:0 one row" => (67, 1),
            _ => (67, 45),
        };
        var jpeg = Encode(shape, w, h);
        foreach (var format in Formats)
        {
            StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, format, null, expectStreaming: true);
            StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, format, Scale(4), true);
            StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, format, Scale(8), true);
        }
    }

    private static byte[] Encode(string shape, int w, int h)
    {
        var rgb = JpegDecoderOracleTests.MakeRgbPattern(w, h);
        if (shape.Contains("magick"))
            return JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 75, "4:2:2");
        if (shape.Contains("writer"))
        {
            return TestJpegWriter.Encode(w, h, [new(1, 4, 1), new(2, 1, 1), new(3, 1, 1)], (k, x, y) => rgb[(y * w + x) * 3 + k],
                new TestJpegWriter.Options { RestartInterval = 2 });
        }

        var restart = shape.Contains("restart ") ? int.Parse(shape[(shape.LastIndexOf(' ') + 1)..]) : 0;
        var options = new JpegEncodeOptions
        {
            Quality = 85,
            Subsampling = shape.StartsWith("4:2:0") ? JpegSubsampling.Chroma420 : JpegSubsampling.Chroma444,
            RestartInterval = restart,
            OptimizeHuffman = shape.Contains("optimized"),
        };
        if (!shape.StartsWith("gray"))
            return JpegEncoder.Encode(rgb, w, h, 3, options);
        var gray = new byte[w * h];
        for (var i = 0; i < gray.Length; i++)
            gray[i] = rgb[i * 3 + 1];
        return JpegEncoder.Encode(gray, w, h, 1, options);
    }
    /// <summary>
    /// An Adobe APP14 or JFIF APP0 segment between SOF and SOS does not choose the colour transform in a whole decode (it
    /// is chosen from the markers before SOF); the streaming reader, which reads up to SOS first, must not let it either.
    /// </summary>
    [Theory]
    [InlineData(0xEE)]
    [InlineData(0xE0)]
    public void ColourSegmentAfterSof_IsIgnoredAsInTheWholeDecode(int marker)
    {
        var rgb = JpegDecoderOracleTests.MakeRgbPattern(37, 29);
        var jpeg = TestJpegWriter.Encode(37, 29, [new(1, 1, 1), new(2, 1, 1), new(3, 1, 1)], (k, x, y) => rgb[(y * 37 + x) * 3 + k],
            new TestJpegWriter.Options { AdobeTransform = 0, Jfif = marker == 0xE0 });
        var moved = MoveAfterSof(jpeg, (byte)marker);
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Gray8 })
            StreamingAssert.RowsEqualDecodeInto(Codec, moved, format, null, expectStreaming: true);
    }

    /// <summary><paramref name="jpeg"/> with its first <paramref name="marker"/> segment moved to just after SOF.</summary>
    private static byte[] MoveAfterSof(byte[] jpeg, byte marker)
    {
        var segments = JpegSegmentScanner.Scan(jpeg);
        var segment = segments.First(s => s.Marker == marker);
        var sof = segments.First(s => s.Marker == 0xC0);
        segment.SegmentOffset.ShouldBeLessThan(sof.SegmentOffset);
        var bytes = jpeg.AsSpan(segment.SegmentOffset, segment.TotalLength).ToArray();
        var without = jpeg.Take(segment.SegmentOffset).Concat(jpeg.Skip(segment.SegmentOffset + segment.TotalLength)).ToList();
        without.InsertRange(sof.SegmentOffset - segment.TotalLength + sof.TotalLength, bytes);
        return [.. without];
    }

    /// <summary>The variants that cannot stream say so and still give the whole decode's rows.</summary>
    [Fact]
    public void ProgressiveAndMultiScan_FallBack()
    {
        var rgb = JpegDecoderOracleTests.MakeRgbPattern(67, 45);
        var progressive = JpegEncoder.Encode(rgb, 67, 45, 3, new JpegEncodeOptions { Quality = 80, Progressive = true });
        var luma = new byte[67 * 45];
        for (var i = 0; i < luma.Length; i++)
            luma[i] = rgb[i * 3];
        var gray = JpegEncoder.Encode(luma, 67, 45, 1, new JpegEncodeOptions { Quality = 80, Progressive = true });
        var optimisedFinally = JpegDecoderOracleTests.EncodeJpeg(rgb, 67, 45, 75, "4:2:0");
        foreach (var jpeg in new[] { progressive, gray, optimisedFinally })
        {
            var expect = jpeg == optimisedFinally;
            foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Gray8, PixelFormat.Rgb48 })
            {
                StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, format, null, expectStreaming: expect);
                StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, format, Scale(2), expect);
            }
        }

        // Options the streaming path does not honour on the fly take the fallback too, with the same rows.
        var jpeg2 = JpegDecoderOracleTests.EncodeJpeg(rgb, 67, 45, 75, "4:2:0");
        StreamingAssert.RowsEqualDecodeInto(Codec, jpeg2, PixelFormat.Rgba32, new DecodeOptions { Region = new PixelRect(5, 7, 20, 13) }, false);
        StreamingAssert.RowsEqualDecodeInto(Codec, jpeg2, PixelFormat.Rgba32, new DecodeOptions { ScaleDenominator = 16 }, false);
        StreamingAssert.RowsEqualDecodeInto(Codec, jpeg2, PixelFormat.Rgba32, new DecodeOptions { ApplyOrientation = true }, false);
    }

    /// <summary>An Exif orientation is in <see cref="ImageInfo"/> from the header, and with orientation applied the reader
    /// falls back and gives the displayed rows.</summary>
    [Fact]
    public void ExifOrientation_IsReportedAndApplied()
    {
        var rgb = JpegDecoderOracleTests.MakeRgbPattern(37, 29);
        var jpeg = JpegEncoder.Encode(rgb, 37, 29, 3, new JpegEncodeOptions
        {
            Quality = 80,
            Exif = MetadataTests.ExifBlock(orientation: 6),
        });
        var info = JpegDecoder.ReadInfo(jpeg);
        info.Orientation.ShouldNotBe(ImageOrientation.Normal);

        using var reader = JpegImageDecoder.OpenRowReader(new ChunkedStream(jpeg, 64), PixelFormat.Rgba32);
        reader.Initialize(TestContext.Current.CancellationToken);
        reader.Info.ShouldBe(JpegImageDecoder.Registration.TryReadInfo(jpeg, out var fresh) ? fresh : default);
        reader.IsStreaming.ShouldBeTrue();

        StreamingAssert.RowsEqualDecodeInto(Codec, jpeg, PixelFormat.Rgba32, new DecodeOptions { ApplyOrientation = true }, expectStreaming: false);
    }

    /// <summary>Reading only the first rows reads only a prefix of the stream: the reader never asks for bytes the rows do
    /// not need.</summary>
    [Fact]
    public void FirstRows_ReadOnlyAPrefix()
    {
        var rgb = JpegDecoderOracleTests.MakeRgbPattern(640, 480);
        var jpeg = JpegEncoder.Encode(rgb, 640, 480, 3, new JpegEncodeOptions { Quality = 90, Subsampling = JpegSubsampling.Chroma420 });
        var stream = new ChunkedStream(jpeg, maxChunk: 4096);
        using var reader = JpegImageDecoder.OpenRowReader(stream, PixelFormat.Rgba32);
        reader.Initialize(TestContext.Current.CancellationToken);
        var row = new byte[reader.Width * 4];
        reader.ReadRows(row, 1, 0, TestContext.Current.CancellationToken).ShouldBe(1);
        stream.BytesRead.ShouldBeLessThan(jpeg.Length / 2, "the first row must not need the whole stream");
    }

    /// <summary>Truncating anywhere — inside every MCU row, in the markers after the scan — fails exactly where the whole
    /// decode fails, and before then gives exactly its rows.</summary>
    [Theory]
    [InlineData("4:2:0 restart 0")]
    [InlineData("4:2:0 restart 3")]
    [InlineData("gray restart 0")]
    [InlineData("4:4:4 restart 2")]
    [InlineData("4:1:1 writer restart 2")]
    public void Truncation_FailsWhereTheWholeDecodeFails(string shape)
    {
        var jpeg = Encode(shape, 31, 23);
        var info = JpegDecoder.ReadInfo(jpeg);
        var rowBytes = info.Width * 4;
        var scratch = new byte[rowBytes * info.Height];
        var accepted = 0;
        for (var cut = 0; cut <= jpeg.Length; cut++)
        {
            var truncated = jpeg.AsSpan(0, cut).ToArray();
            var context = $"{shape} cut at {cut} of {jpeg.Length}";
            if (Codec.TryDecodeInto(truncated, scratch, rowBytes, PixelFormat.Rgba32, null, TestContext.Current.CancellationToken))
            {
                // Accepted (a missing EOI, a scan cut short): the reader gives exactly the salvaged rows.
                StreamingAssert.RowsEqualDecodeInto(Codec, truncated, PixelFormat.Rgba32, null, null,
                    [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly]);
                accepted++;
                continue;
            }

            foreach (var feed in new[] { StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.Whole })
            {
                Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(truncated, feed,
                    input => JpegImageDecoder.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _, seed: cut + 1),
                    $"{context}, {feed}: the whole decode rejects it");
            }
        }

        accepted.ShouldBeGreaterThan(0, "cuts inside the scan decode as salvage");
    }
    /// <summary>Reading a 8000-row image band by band allocates far less than the image: the reader holds two MCU rows.</summary>
    [Fact]
    public void LargeImage_AllocatesBounded()
    {
        const int w = 5_000, h = 8_000;
        var jpeg = TallJpeg(w, h);
        var imageBytes = (long)w * h * 4;
        var stream = new MemoryStream(jpeg, writable: false);
        var band = new byte[16 * w * 4];

        void Read()
        {
            stream.Position = 0;
            using var reader = JpegImageDecoder.OpenRowReader(stream, PixelFormat.Rgba32, null, leaveOpen: true);
            reader.Initialize(TestContext.Current.CancellationToken);
            reader.IsStreaming.ShouldBeTrue();
            reader.Width.ShouldBe(w);
            reader.Height.ShouldBe(h);
            while (reader.RowsRead < h)
                reader.ReadRows(band, 16).ShouldBeGreaterThan(0);
        }

        StreamingAssert.AllocatesLessThan(imageBytes / 8, Read, "streaming a tall image");
    }

    /// <summary>A JPEG of <paramref name="w"/> × <paramref name="h"/> whose MCU rows are one-input-byte-like: a gradient at
    /// high quality, so the file is small enough to build.</summary>
    private static byte[] TallJpeg(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                rgb[i] = (byte)(x >> 3);
                rgb[i + 1] = (byte)(y >> 3);
                rgb[i + 2] = (byte)((x ^ y) >> 4);
            }

        return JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 40, Subsampling = JpegSubsampling.Chroma420 });
    }

    /// <summary>End to end: a file read through an async-only stream, its rows equal the whole decode of its bytes.</summary>
    [Fact]
    public async Task FileStream_AsyncOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"simdcodecs-stream-{Guid.NewGuid():N}.jpg");
        try
        {
            var rgb = JpegDecoderOracleTests.MakeRgbPattern(211, 137);
            var jpeg = JpegEncoder.Encode(rgb, 211, 137, 3, new JpegEncodeOptions { Quality = 88 });
            File.WriteAllBytes(path, jpeg);

            var expected = new byte[211 * 137 * 4];
            Codec.TryDecodeInto(jpeg, expected, 211 * 4, PixelFormat.Rgba32, null, TestContext.Current.CancellationToken).ShouldBeTrue();

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var stream = new AsyncOnlyStream(file, maxChunk: 997);
            await using var reader = await JpegImageDecoder.OpenRowReaderAsync(stream, PixelFormat.Rgba32, null, leaveOpen: true,
                TestContext.Current.CancellationToken);
            reader.IsStreaming.ShouldBeTrue();
            reader.Info.Width.ShouldBe(211);
            var actual = new byte[expected.Length];
            while (reader.RowsRead < reader.Height)
            {
                var at = reader.RowsRead * 211 * 4;
                (await reader.ReadRowsAsync(actual.AsMemory(at), 7, 0, TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);
            }

            actual.ShouldBe(expected);
            stream.SyncReads.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Read-only fields the resuming constructor derives from the saved decoder state and scale.</summary>
    private static readonly HashSet<string> Derived = ["_order", "_huffDc", "_huffAc", "_dequant", "_comp", "_blockSize"];

    /// <summary>The suspension mirrors the engine: every field of <c>JpegCore</c> that is not a span has a namesake in
    /// <c>JpegCore.Suspended</c>, so a field added to the engine cannot silently go unsaved.</summary>
    [Fact]
    public void Suspension_MirrorsTheEngine()
    {
        var core = typeof(JpegDecoder).Assembly.GetType("Sdcb.SimdCodecs.Jpeg.JpegCore")!;
        var suspended = core.GetNestedType("Suspended", BindingFlags.NonPublic | BindingFlags.Public)!;
        var saved = suspended.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.Name).ToHashSet();
        var spans = core.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType.IsByRefLike || f.FieldType == typeof(ReadOnlySpan<byte>) || f.FieldType == typeof(Span<byte>))
            .Select(f => f.Name).ToHashSet();
        spans.ShouldContain("_data");
        var missing = core.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.Name.StartsWith('<') ? f.Name[1..f.Name.IndexOf('>')] : f.Name) // auto-property backing fields
            .Where(name => !spans.Contains(name) && !saved.Contains(name) && !Derived.Contains(name))
            .ToArray();
        missing.ShouldBeEmpty("fields of JpegCore that JpegCore.Suspended does not save");
    }

    /// <summary>A forward-only view of a stream that refuses synchronous reads (as a request body does) and returns at
    /// most <c>maxChunk</c> bytes per read.</summary>
    private sealed class AsyncOnlyStream(Stream inner, int maxChunk) : Stream
    {
        public int SyncReads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            SyncReads++;
            throw new InvalidOperationException("Synchronous operations are disallowed.");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maxChunk)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}