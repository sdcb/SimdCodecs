using System.Diagnostics;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Tests.Streaming;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using B = Sdcb.SimdCodecs.Tests.TestTiffBuilder;
using Feed = Sdcb.SimdCodecs.Tests.Streaming.StreamingAssert.Feed;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="TiffImageDecoder.CreateRowReader"/>: rows equal <see cref="TiffImageDecoder.TryDecodeInto"/> byte for byte
/// for every layout the reader handles; a seekable stream streams (bounded memory), anything else falls back; malformed
/// input throws <see cref="InvalidDataException"/> only. Also <see cref="TiffReader.Read(Stream)"/>, which reads a
/// seekable stream by offset and must return what <see cref="TiffReader.Read(ReadOnlySpan{byte})"/> returns.
/// The fuzz loop's length scales with <c>TIFF_STREAM_FUZZ_ITERATIONS</c> (default 200).
/// </summary>
public sealed class TiffStreamingDecodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PixelFormat[] Formats =
    [
        PixelFormat.Rgba32, PixelFormat.Bgr24, PixelFormat.Gray8, PixelFormat.GrayAlpha16, PixelFormat.Rgba64,
        PixelFormat.Gray16, PixelFormat.Rgba32Premultiplied,
    ];

    /// <summary>True when <see cref="TiffImageDecoder.TryDecodeInto"/> writes <paramref name="data"/> in <paramref name="format"/> (a float page has no integer projection, so it never does).</summary>
    private static bool Decodes(byte[] data, PixelFormat format) =>
        TiffImageDecoder.TryReadInfo(data, out var info)
        && TiffImageDecoder.TryDecodeInto(data, new byte[info.Width * info.Height * format.BytesPerPixel], 0, format, null, Ct);

    private static readonly Feed[] NonSeekable = [Feed.OneByte, Feed.RandomChunks, Feed.AsyncOnly];

    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("TIFF_STREAM_FUZZ_ITERATIONS"), out var n) ? n : 200;

    /// <summary>The robustness corpus (libtiff-written and hand-assembled) plus layouts it lacks.</summary>
    private static List<(string Name, byte[] Data)> Corpus()
    {
        var corpus = new List<(string, byte[])>(TiffRobustnessTests.Seeds());
        var random = new Random(11);
        const int w = 41, h = 29;

        // Planar tiles, big-endian, 16-bit, hand-assembled: one tile row needs every plane's tiles.
        var tiles = new List<byte[]>();
        for (var p = 0; p < 3; p++)
        {
            for (var ty = 0; ty < 2; ty++)
            for (var tx = 0; tx < 3; tx++)
            {
                var tile = new byte[16 * 16 * 2];
                random.NextBytes(tile);
                tiles.Add(tile);
            }
        }
        var tags = B.Basic(w, h, 3, 16, TiffCompression.Uncompressed, TiffPhotometric.Rgb);
        tags.Add(B.Short(TiffTag.PlanarConfig, 2));
        tags.Add(B.Long(TiffTag.TileWidth, 16));
        tags.Add(B.Long(TiffTag.TileLength, 16));
        corpus.Add(("hand MM planar tiled 16-bit", new B(littleEndian: false).AddPage(tags, [.. tiles], tiled: true).Build()));

        // Strips listed out of file order, and two strips sharing their bytes.
        var gray = new byte[w * h];
        random.NextBytes(gray);
        var strips = TiffRobustnessTests.Strips(gray, w, h, 4);
        var file = new B().AddPage(B.Basic(w, h, 1, 8, TiffCompression.Uncompressed, TiffPhotometric.MinIsBlack, rowsPerStrip: 4), strips).Build();
        corpus.Add(("strips reversed", PermuteStrips(file, strips.Length, reverse: true)));
        corpus.Add(("strips shared", PermuteStrips(file, strips.Length, reverse: false)));

        // A short strip list: the rows past it decode as zeros.
        var tagsShort = B.Basic(w, h, 1, 8, TiffCompression.Uncompressed, TiffPhotometric.MinIsBlack, rowsPerStrip: 4);
        corpus.Add(("short strip list", new B().AddPage(tagsShort, strips[..3]).Build()));

        // BigTIFF and a single-strip page.
        corpus.Add(("BigTIFF single strip", new B(bigTiff: true).AddPage(
            B.Basic(w, h, 1, 8, TiffCompression.Uncompressed, TiffPhotometric.MinIsWhite, rowsPerStrip: h), [gray]).Build()));
        return corpus;
    }

    /// <summary>Rewrites the StripOffsets array of a file <see cref="B"/> built: reversed, or every strip pointing at the first's bytes (with its byte count).</summary>
    private static byte[] PermuteStrips(byte[] file, int count, bool reverse)
    {
        var layout = TiffReader.ReadLayout(file);
        var offsets = layout.Offsets.ToArray();
        var counts = layout.ByteCounts.ToArray();
        var data = file.ToArray();
        // Find the StripOffsets array (LONGs, out of line) by its contents.
        var needle = offsets.SelectMany(o => BitConverter.GetBytes((uint)o)).ToArray();
        var at = data.AsSpan().IndexOf(needle);
        at.ShouldBeGreaterThan(0);
        var countNeedle = counts.SelectMany(c => BitConverter.GetBytes((uint)c)).ToArray();
        var countAt = data.AsSpan().IndexOf(countNeedle);
        countAt.ShouldBeGreaterThan(0);
        for (var i = 0; i < count; i++)
        {
            var source = reverse ? count - 1 - i : 0;
            BitConverter.GetBytes((uint)offsets[source]).CopyTo(data, at + (i * 4));
            BitConverter.GetBytes((uint)counts[source]).CopyTo(data, countAt + (i * 4));
        }
        return data;
    }

    private static bool IsFloat(byte[] file) =>
        TiffImageDecoder.TryReadInfo(file, out var info) && info.SampleFormat == SampleFormat.Float32;

    [Fact]
    public void Rows_equal_TryDecodeInto_streaming_on_seekable_streams_only()
    {
        foreach (var (name, data) in Corpus())
        {
            var streams = !IsFloat(data);
            var tested = 0;
            foreach (var format in Formats)
            {
                if (!Decodes(data, format))
                    continue;
                tested++;
                try
                {
                    StreamingAssert.RowsEqualDecodeInto(TiffImageDecoder.Registration, data, format,
                        expectStreaming: streams, feeds: [Feed.Whole]);
                    StreamingAssert.RowsEqualDecodeInto(TiffImageDecoder.Registration, data, format,
                        expectStreaming: false, feeds: NonSeekable);
                    if (streams)
                    {
                        foreach (var async in new[] { false, true })
                            SeekableRowsEqual(data, format, async, seed: format.GetHashCode());
                    }
                }
                catch (Exception e)
                {
                    throw new ShouldAssertException($"{name} as {format}: {e.Message}", e);
                }
            }
            if (streams)
                tested.ShouldBeGreaterThan(0, name);
        }
    }

    /// <summary>
    /// A seekable stream that returns 1..997 bytes per read, read in random bands of 1..17 rows through a 16-byte first
    /// buffer (synchronously or asynchronously): every fetch, rewind and partial band of the streaming path.
    /// </summary>
    private static void SeekableRowsEqual(byte[] data, PixelFormat format, bool async, int seed)
    {
        TiffImageDecoder.TryReadInfo(data, out var info).ShouldBeTrue();
        var rowBytes = info.Width * format.BytesPerPixel;
        var expected = new byte[rowBytes * info.Height];
        TiffImageDecoder.TryDecodeInto(data, expected, 0, format, null, Ct).ShouldBeTrue();
        var stream = new SeekableChunkedStream(data, maxChunk: 997, seed);
        using var reader = TiffImageDecoder.CreateRowReader(new StreamInput(stream, initialCapacity: 16), format, null);
        var random = new Random(seed);
        var actual = new byte[expected.Length];
        while (reader.RowsRead < info.Height)
        {
            var at = reader.RowsRead * rowBytes;
            var band = random.Next(1, 18);
            var n = async
                ? reader.ReadRowsAsync(actual.AsMemory(at), band, 0, Ct).AsTask().GetAwaiter().GetResult()
                : reader.ReadRows(actual.AsSpan(at), band, 0, Ct);
            n.ShouldBeGreaterThan(0);
        }
        reader.IsStreaming.ShouldBeTrue("seekable: IsStreaming");
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            var first = actual.AsSpan().CommonPrefixLength(expected);
            throw new ShouldAssertException($"seekable chunked (async {async}): rows differ at byte {first} (row {first / rowBytes}).");
        }
    }

    /// <summary>A seekable <see cref="MemoryStream"/> whose reads return a seeded-random 1..<c>maxChunk</c> bytes, synchronously or after a yield.</summary>
    private sealed class SeekableChunkedStream(byte[] data, int maxChunk, int seed) : MemoryStream(data, writable: false)
    {
        private readonly Random _random = new(seed);

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, _random.Next(1, maxChunk + 1)));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, _random.Next(1, maxChunk + 1))]);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            return Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// libtiff's separate planes put each plane's strips after the previous plane's, so every band of rows needs bytes
    /// from three far-apart places. The reader seeks to each (StreamInput.Seek with the segment's length), so it streams
    /// and reads the file about once, never through the gaps per band.
    /// </summary>
    [Fact]
    public void Planes_stored_far_apart_stream_by_seeking()
    {
        const int w = 1500, h = 1200;
        var rgb = new byte[w * h * 3];
        new Random(9).NextBytes(rgb);
        using var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings(w, h, StorageType.Char, "RGB"));
        image.Settings.Interlace = Interlace.Plane;
        image.Settings.Compression = CompressionMethod.NoCompression;
        image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "8");
        var file = image.ToByteArray(MagickFormat.Tiff);
        TiffReader.ReadLayout(file).IsPlanar.ShouldBeTrue();
        var expected = StreamingAssert.RowsEqualDecodeInto(TiffImageDecoder.Registration, file, PixelFormat.Rgb24,
            expectStreaming: true, feeds: [Feed.Whole]);
        var (rows, read) = ReadCounting(file, PixelFormat.Rgb24);
        rows.ShouldBe(expected);
        read.ShouldBeLessThan(file.Length + (128 << 10), $"bytes read for a {file.Length}-byte file");
    }

    // Reads every row from a seekable stream that counts the bytes it hands out.
    private static (byte[] Rows, long Read) ReadCounting(byte[] file, PixelFormat format)
    {
        var stream = new CountingStream(file);
        using var reader = TiffImageDecoder.CreateRowReader(new StreamInput(stream), format, null);
        reader.Initialize(Ct);
        reader.IsStreaming.ShouldBeTrue();
        var rows = new byte[reader.Width * format.BytesPerPixel * reader.Height];
        reader.ReadRows(rows, reader.Height, 0, Ct).ShouldBe(reader.Height);
        return (rows, stream.BytesRead);
    }

    /// <summary>
    /// An IFD chain that jumps between the start and the end of the file: the reader seeks over the gap between, so it
    /// counts the pages and streams without reading the gap (the 300 KB between the two halves).
    /// </summary>
    [Fact]
    public void A_chain_jumping_back_and_forth_seeks_over_the_gap()
    {
        const int pages = 20, spacing = 128, gap = 300_000;
        long IfdAt(int k) => k % 2 == 0 ? 64 + (k / 2 * spacing) : 64 + (pages * spacing) + gap + (k / 2 * spacing);
        var file = new byte[IfdAt(pages - 1) + spacing];
        file[0] = file[1] = (byte)'I';
        BitConverter.GetBytes((ushort)42).CopyTo(file, 2);
        BitConverter.GetBytes((uint)IfdAt(0)).CopyTo(file, 4);
        file[8] = 200; // the one pixel every page points at
        for (var k = 0; k < pages; k++)
        {
            var at = (int)IfdAt(k);
            (ushort Tag, ushort Type, uint Value)[] entries =
            [
                (TiffTag.ImageWidth, 4, 1), (TiffTag.ImageLength, 4, 1), (TiffTag.BitsPerSample, 3, 8),
                (TiffTag.Compression, 3, 1), (TiffTag.PhotometricInterp, 3, 1), (TiffTag.StripOffsets, 4, 8),
                (TiffTag.RowsPerStrip, 4, 1), (TiffTag.StripByteCounts, 4, 1),
            ];
            BitConverter.GetBytes((ushort)entries.Length).CopyTo(file, at);
            for (var i = 0; i < entries.Length; i++)
            {
                var e = at + 2 + (i * 12);
                BitConverter.GetBytes(entries[i].Tag).CopyTo(file, e);
                BitConverter.GetBytes(entries[i].Type).CopyTo(file, e + 2);
                BitConverter.GetBytes(1u).CopyTo(file, e + 4);
                if (entries[i].Type == 3) BitConverter.GetBytes((ushort)entries[i].Value).CopyTo(file, e + 8);
                else BitConverter.GetBytes(entries[i].Value).CopyTo(file, e + 8);
            }
            BitConverter.GetBytes(k + 1 < pages ? (uint)IfdAt(k + 1) : 0u).CopyTo(file, at + 2 + (entries.Length * 12));
        }
        TiffReader.CountPages(file).ShouldBe(pages);
        StreamingAssert.RowsEqualDecodeInto(TiffImageDecoder.Registration, file, PixelFormat.Gray8,
            expectStreaming: true, feeds: [Feed.Whole]);
        var stream = new CountingStream(file);
        using var reader = TiffImageDecoder.CreateRowReader(new StreamInput(stream, initialCapacity: 4096),
            PixelFormat.Gray8, null);
        reader.Initialize(Ct);
        reader.Info.FrameCount.ShouldBe(pages);
        reader.IsStreaming.ShouldBeTrue();
        stream.BytesRead.ShouldBeLessThan(gap, "the reader read through the gap");
    }

    [Fact]
    public void Options_other_than_the_whole_image_fall_back()
    {
        var (_, data) = TiffRobustnessTests.Seeds()[0];
        foreach (var options in new[]
                 {
                     new DecodeOptions { Region = new PixelRect(3, 2, 20, 11) },
                     new DecodeOptions { ScaleDenominator = 2 },
                     new DecodeOptions { ApplyOrientation = true },
                 })
        {
            StreamingAssert.RowsEqualDecodeInto(TiffImageDecoder.Registration, data, PixelFormat.Rgba32, options,
                expectStreaming: false, feeds: [Feed.Whole, Feed.RandomChunks]);
        }
    }

    [Fact]
    public void Info_equals_TryReadInfo()
    {
        foreach (var (name, data) in Corpus())
        {
            TiffImageDecoder.TryReadInfo(data, out var expected).ShouldBeTrue(name);
            if (IsFloat(data))
                continue; // no integer format to read it in
            foreach (var seekable in new[] { true, false })
            {
                Stream stream = seekable ? new MemoryStream(data, writable: false) : new ChunkedStream(data, maxChunk: 777, seed: 3);
                var format = Formats.First(f => Decodes(data, f));
                using var reader = TiffImageDecoder.CreateRowReader(new StreamInput(stream), format, null);
                reader.Initialize(Ct);
                reader.Info.ShouldBe(expected, $"{name} seekable {seekable}");
            }
        }
    }

    [Fact]
    public void Multi_page_reads_page_zero()
    {
        var (_, data) = TiffRobustnessTests.Seeds().Single(s => s.Name == "multi-page");
        var rows = StreamingAssert.RowsEqualDecodeInto(TiffImageDecoder.Registration, data, PixelFormat.Rgb24,
            expectStreaming: true, feeds: [Feed.Whole]);
        using var reader = TiffImageDecoder.CreateRowReader(new StreamInput(new MemoryStream(data)), PixelFormat.Rgb24, null);
        reader.Initialize(Ct);
        reader.Info.FrameCount.ShouldBe(3);
        rows.Length.ShouldBe(reader.Width * reader.Height * 3);
    }

    [Fact]
    public void A_stream_not_at_its_start_is_read_from_its_position()
    {
        var (_, data) = TiffRobustnessTests.Seeds().First(s => s.Name.StartsWith("LZW tiled True"));
        var prefixed = new byte[100 + data.Length];
        data.CopyTo(prefixed, 100);
        var expected = new byte[37 * 23 * 4];
        TiffImageDecoder.TryDecodeInto(data, expected, 0, PixelFormat.Rgba32, null, Ct).ShouldBeTrue();
        var stream = new MemoryStream(prefixed, writable: false) { Position = 100 };
        using var reader = TiffImageDecoder.CreateRowReader(new StreamInput(stream), PixelFormat.Rgba32, null);
        var actual = new byte[expected.Length];
        reader.ReadRows(actual, 23, 0, Ct).ShouldBe(23);
        reader.IsStreaming.ShouldBeTrue();
        actual.ShouldBe(expected);

        var document = TiffReader.Read(new MemoryStream(prefixed, writable: false) { Position = 100 });
        document.Pages[0].Pixels.ShouldBe(TiffReader.Read(data).Pages[0].Pixels);
    }

    [Fact]
    public void Segments_past_the_end_throw_invalid_data()
    {
        var gray = new byte[37 * 23];
        new Random(5).NextBytes(gray);
        var strips = TiffRobustnessTests.Strips(gray, 37, 23, 4);
        var file = new B().AddPage(B.Basic(37, 23, 1, 8, TiffCompression.Uncompressed, TiffPhotometric.MinIsBlack, rowsPerStrip: 4), strips).Build();
        var layout = TiffReader.ReadLayout(file);
        var needle = layout.Offsets.SelectMany(o => BitConverter.GetBytes((uint)o)).ToArray();
        var at = file.AsSpan().IndexOf(needle);
        foreach (var bad in new uint[] { (uint)file.Length, (uint)file.Length - 3, uint.MaxValue })
        {
            var broken = file.ToArray();
            BitConverter.GetBytes(bad).CopyTo(broken, at + (3 * 4));
            TiffImageDecoder.TryDecodeInto(broken, new byte[37 * 23], 0, PixelFormat.Gray8, null, Ct).ShouldBeFalse();
            foreach (var feed in new[] { Feed.Whole, Feed.RandomChunks })
            {
                var e = Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(broken, feed,
                    input => TiffImageDecoder.CreateRowReader(input, PixelFormat.Gray8, null), out _, out _, out _));
                e.ShouldNotBeNull();
            }
        }

        // The first IFD past the end, and a file cut inside its directory.
        var noIfd = file.ToArray();
        BitConverter.GetBytes((uint)file.Length + 10).CopyTo(noIfd, 4);
        foreach (var broken in new[] { noIfd, file[..^7], file[..5], Array.Empty<byte>() })
        {
            foreach (var feed in new[] { Feed.Whole, Feed.RandomChunks })
            {
                Should.Throw<InvalidDataException>(() => StreamingAssert.ReadAll(broken, feed,
                    input => TiffImageDecoder.CreateRowReader(input, PixelFormat.Gray8, null), out _, out _, out _));
            }
        }
    }

    [Fact]
    public void Read_stream_equals_read_span()
    {
        foreach (var (name, data) in Corpus())
        {
            var expected = TiffReader.Read(data);
            var actual = TiffReader.Read(new MemoryStream(data, writable: false));
            AssertSameDocument(expected, actual, name);
            var slurped = TiffReader.Read(new ChunkedStream(data, maxChunk: 4096, seed: 1));
            AssertSameDocument(expected, slurped, name);
        }
    }

    private static void AssertSameDocument(TiffDocument expected, TiffDocument actual, string name)
    {
        actual.Pages.Count.ShouldBe(expected.Pages.Count, name);
        for (var p = 0; p < expected.Pages.Count; p++)
        {
            var (e, a) = (expected.Pages[p], actual.Pages[p]);
            a.Pixels.ShouldBe(e.Pixels, $"{name} page {p}");
            (a with { Pixels = [], IccProfile = null }).ShouldBe(e with { Pixels = [], IccProfile = null }, $"{name} page {p}");
            a.IccProfile.ShouldBe(e.IccProfile, $"{name} page {p}");
        }
    }

    [Fact]
    public async Task Mutations_stream_like_the_span_decoder()
    {
        var seeds = Corpus();
        var iterations = Iterations;
        var budget = TimeSpan.FromSeconds(Math.Max(60, iterations / 20));
        var current = "";
        var run = Task.Run(() =>
        {
            var random = new Random(20261123);
            for (var i = 0; i < iterations; i++)
            {
                var (name, seed) = seeds[random.Next(seeds.Count)];
                var data = TiffRobustnessTests.Mutate(seed, random, out var description);
                current = $"#{i} {name}: {description}";
                StreamOne(data, current, random.Next(4) == 0 ? Feed.RandomChunks : Feed.Whole);
            }
        }, Ct);
        if (await Task.WhenAny(run, Task.Delay(budget, Ct)) != run)
            throw new ShouldAssertException($"The fuzz loop is stuck at {current}");
        await run;
    }

    /// <summary>
    /// The streaming reader on <paramref name="data"/> succeeds exactly when <see cref="TiffImageDecoder.TryDecodeInto"/>
    /// does, with its rows; otherwise it throws <see cref="InvalidDataException"/>. <see cref="TiffReader.Read(Stream)"/>
    /// over a seekable stream matches the span read the same way.
    /// </summary>
    private static void StreamOne(byte[] data, string context, Feed feed)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            byte[]? expected = null;
            var size = 0L;
            if (TiffImageDecoder.TryReadInfo(data, out var info))
            {
                size = (long)info.Width * info.Height * 4;
                if (size > 1 << 24) return; // the harness's memory, not the reader's
                expected = new byte[size];
                if (!TiffImageDecoder.TryDecodeInto(data, expected, 0, PixelFormat.Rgba32, null, Ct))
                    expected = null;
            }
            else
            {
                // TryReadInfo refuses a page the span decoder would also refuse; it may still be large.
                try
                {
                    var layout = TiffReader.ReadLayout(data);
                    if ((long)layout.Width * layout.Height * 4 > 1 << 24) return;
                }
                catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
                {
                }
            }

            byte[]? actual;
            try
            {
                actual = StreamingAssert.ReadAll(data, feed,
                    input => TiffImageDecoder.CreateRowReader(input, PixelFormat.Rgba32, null), out _, out _, out _);
            }
            catch (InvalidDataException)
            {
                actual = null;
            }
            if (expected is null)
                actual.ShouldBeNull("streaming decoded what TryDecodeInto refused");
            else
                (actual is not null && actual.AsSpan().SequenceEqual(expected)).ShouldBeTrue("rows differ from TryDecodeInto");

            if (TiffReaderPagesFit(data))
            {
                Exception? spanError = null, streamError = null;
                TiffDocument? fromSpan = null, fromStream = null;
                try { fromSpan = TiffReader.Read(data); } catch (Exception e) when (e is InvalidDataException or NotSupportedException) { spanError = e; }
                try { fromStream = TiffReader.Read(new MemoryStream(data, writable: false)); } catch (Exception e) when (e is InvalidDataException or NotSupportedException) { streamError = e; }
                (streamError?.GetType()).ShouldBe(spanError?.GetType(), "Read(Stream) and Read(span) disagree");
                if (fromSpan is not null)
                    AssertSameDocument(fromSpan, fromStream!, "Read(Stream)");
            }
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{context} ({feed}): {e.GetType().Name}: {e.Message}", e);
        }
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), context);
    }

    // Every page small enough for the harness to hold as a raster.
    private static bool TiffReaderPagesFit(byte[] data)
    {
        int pages;
        try { pages = TiffReader.CountPages(data); }
        catch (InvalidDataException) { return false; }
        for (var p = 0; p < pages; p++)
        {
            try
            {
                var layout = TiffReader.ReadLayout(data, p);
                if ((long)layout.Width * layout.Height * layout.BytesPerPixel > 1 << 24) return false;
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or ArgumentException)
            {
            }
        }
        return true;
    }

    /// <summary>
    /// End to end: a large libtiff-written (Magick.NET) multi-strip TIFF in a file, its directory after the pixels,
    /// read from a <see cref="FileStream"/> asynchronously equals <see cref="TiffImageDecoder.TryDecodeInto"/>, and read
    /// synchronously allocates far less than the file or the page.
    /// </summary>
    [Theory]
    [InlineData("zip")]
    [InlineData("none")]
    public async Task Large_file_streams_with_bounded_memory(string compression)
    {
        const int w = 8192, h = 4096;
        var dir = Directory.CreateTempSubdirectory("sdcb-tiffstream-");
        try
        {
            var path = Path.Combine(dir.FullName, "large.tif");
            var gray = new byte[w * h];
            var random = new Random(7);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                gray[(y * w) + x] = (byte)((x / 3) + (y / 5) + random.Next(16));
            using (var image = new MagickImage(gray, new MagickReadSettings { Format = MagickFormat.Gray, Width = w, Height = h, Depth = 8 }))
            {
                image.Settings.Compression = compression == "zip" ? CompressionMethod.Zip : CompressionMethod.NoCompression;
                image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "16");
                image.Write(path, MagickFormat.Tiff);
            }
            var file = await File.ReadAllBytesAsync(path, Ct);
            TiffReader.ReadLayout(file).IfdOffset.ShouldBeGreaterThan(file.Length / 2, "libtiff writes the directory after the pixels");
            var expected = new byte[w * h];
            TiffImageDecoder.TryDecodeInto(file, expected, 0, PixelFormat.Gray8, null, Ct).ShouldBeTrue();

            var band = new byte[w * 100];
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            await using (var reader = TiffImageDecoder.CreateRowReader(new StreamInput(stream), PixelFormat.Gray8, null))
            {
                await reader.InitializeAsync(Ct);
                reader.IsStreaming.ShouldBeTrue();
                while (reader.RowsRead < h)
                {
                    var at = reader.RowsRead;
                    var n = await reader.ReadRowsAsync(band, 100, 0, Ct);
                    n.ShouldBeGreaterThan(0);
                    if (!band.AsSpan(0, n * w).SequenceEqual(expected.AsSpan(at * w, n * w)))
                        throw new ShouldAssertException($"rows {at}..{at + n} differ from TryDecodeInto");
                }
            }

            const long bound = 4 << 20;
            ((long)file.Length).ShouldBeGreaterThan(2 * bound);
            StreamingAssert.AllocatesLessThan(bound, () =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                using var reader = TiffImageDecoder.CreateRowReader(new StreamInput(stream), PixelFormat.Gray8, null);
                while (reader.RowsRead < h)
                    reader.ReadRows(band, 100, 0, Ct);
            }, "streaming a large TIFF");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
