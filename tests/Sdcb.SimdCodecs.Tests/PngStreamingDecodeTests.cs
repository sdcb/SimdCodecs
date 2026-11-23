using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PngImageDecoder.CreateRowReader"/>: a non-interlaced PNG decodes from a forward-only stream as it arrives,
/// its rows exactly <see cref="PngImageDecoder.TryDecodeInto"/>'s for every colour type, depth and pixel format, under
/// every feed; interlaced and animated files and non-full options fall back with the same rows; truncated and corrupt
/// streams fail with <see cref="InvalidDataException"/> exactly where the span decode refuses them; memory stays a few
/// rows whatever the height.
/// </summary>
public sealed class PngStreamingDecodeTests
{
    private static readonly PixelFormat[] Formats =
    [
        PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24, PixelFormat.Gray8, PixelFormat.GrayAlpha16,
        PixelFormat.Rgba64, PixelFormat.Rgb48, PixelFormat.Gray16, PixelFormat.GrayAlpha32, PixelFormat.Rgba32Premultiplied,
        PixelFormat.Bgra64Premultiplied,
    ];

    private static readonly (int ColorType, int Depth)[] Layouts =
    [
        (0, 1), (0, 2), (0, 4), (0, 8), (0, 16), (2, 8), (2, 16), (3, 1), (3, 2), (3, 4), (3, 8), (4, 8), (4, 16), (6, 8), (6, 16),
    ];

    private static ImageDecoderRegistration Codec => PngImageDecoder.Registration;

    public static TheoryData<int, int, int> LayoutCases()
    {
        var data = new TheoryData<int, int, int>();
        foreach (var (colorType, depth) in Layouts)
        {
            data.Add(colorType, depth, 0);   // plain
            if (colorType is 0 or 2 or 3)
                data.Add(colorType, depth, 1); // tRNS
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void HandBuilt_RowsEqualDecodeInto_EveryFormatAndFeed(int colorType, int depth, int trns)
    {
        // Odd sizes, every filter type, the zlib stream split into small IDATs.
        var png = HandBuilt(colorType, depth, 13, 11, seed: colorType * 100 + depth + trns, idatBytes: 37, trns: trns == 1);
        foreach (var format in Formats)
            StreamingAssert.RowsEqualDecodeInto(Codec, png, format, expectStreaming: true);
    }

    [Fact]
    public void ImageMagickFiles_RowsEqualDecodeInto()
    {
        foreach (var (colorType, depth) in Layouts)
        {
            var png = MagickPng(colorType, depth, interlace: false, 37, 23);
            (png[24], png[25]).ShouldBe(((byte)depth, (byte)colorType));
            foreach (var format in Formats)
                StreamingAssert.RowsEqualDecodeInto(Codec, png, format, expectStreaming: true,
                    feeds: [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly, StreamingAssert.Feed.Whole]);
            StreamingAssert.RowsEqualDecodeInto(Codec, png, PixelFormat.Rgba32, expectStreaming: true, feeds: [StreamingAssert.Feed.OneByte]);
        }
    }

    [Fact]
    public void OurEncoder_RowsEqualDecodeInto()
    {
        const int w = 301, h = 97;
        var rgba = Gradient(w, h, alpha: true);
        foreach (var png in new[]
                 {
                     PngWriter.Encode(rgba, w, h),
                     PngWriter.Encode(rgba, w, h, new PngWriteOptions { LosslessReduction = true }),
                     PngWriter.EncodeRgb8(Gradient(w, h, alpha: false), w, h),
                 })
        {
            foreach (var format in Formats)
                StreamingAssert.RowsEqualDecodeInto(Codec, png, format, expectStreaming: true,
                    feeds: [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly, StreamingAssert.Feed.Whole]);
        }
    }

    [Fact]
    public void IdatsOfOneByte_AndChunksBetweenAndAfterThem_Stream()
    {
        var png = HandBuilt(6, 8, 29, 17, seed: 5, idatBytes: 1);
        StreamingAssert.RowsEqualDecodeInto(Codec, png, PixelFormat.Rgba32, expectStreaming: true);
        StreamingAssert.RowsEqualDecodeInto(Codec, png, PixelFormat.Rgb48, expectStreaming: true);

        // IDATs interleaved with an unknown chunk and a text chunk; metadata, an unknown chunk and no IEND after the data.
        var mixed = HandBuilt(2, 8, 29, 17, seed: 6, idatBytes: 50, between: s =>
        {
            PngRgba8ExpansionTests.Chunk(s, "prVt"u8, new byte[300]);
            PngRgba8ExpansionTests.Chunk(s, "tEXt"u8, "Comment\0between"u8);
        }, after: s =>
        {
            PngRgba8ExpansionTests.Chunk(s, "tEXt"u8, "Comment\0after"u8);
            PngRgba8ExpansionTests.Chunk(s, "eXIf"u8, new byte[20]);
            PngRgba8ExpansionTests.Chunk(s, "zzZz"u8, new byte[5000]);
        }, iend: false);
        StreamingAssert.RowsEqualDecodeInto(Codec, mixed, PixelFormat.Rgba32, expectStreaming: true);
        StreamingAssert.RowsEqualDecodeInto(Codec, mixed, PixelFormat.Gray8, expectStreaming: true);
    }

    [Fact]
    public void InterlacedAnimatedAndOptions_FallBack_WithTheSameRows()
    {
        foreach (var (colorType, depth) in Layouts)
        {
            var png = MagickPng(colorType, depth, interlace: true, 19, 13);
            png[28].ShouldBe((byte)1);
            foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Gray8, PixelFormat.Rgba64 })
                StreamingAssert.RowsEqualDecodeInto(Codec, png, format, expectStreaming: false,
                    feeds: [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly]);
        }

        foreach (var (name, apng) in ApngRobustnessTests.Seeds().Take(3))
            StreamingAssert.RowsEqualDecodeInto(Codec, apng, PixelFormat.Rgba32, expectStreaming: false);

        var plain = HandBuilt(6, 8, 40, 30, seed: 9);
        DecodeOptions[] options =
        [
            new() { Region = new PixelRect(3, 4, 20, 10) },
            new() { ScaleDenominator = 2 },
            new() { ApplyOrientation = true },
        ];
        foreach (var o in options)
            StreamingAssert.RowsEqualDecodeInto(Codec, plain, PixelFormat.Rgba32, o, expectStreaming: false);
    }

    [Fact]
    public void HdrTagged_FallsBackForEightBitSamples_StreamsSixteen()
    {
        var cicp = new byte[] { 9, 16, 0, 1 }; // BT.2020, PQ, full range
        var png = PngTransparentColorTests.WithChunk(HandBuilt(2, 16, 9, 7, seed: 3), "cICP"u8, cicp);
        PngImageDecoder.TryDecodeInto(png, new byte[9 * 7 * 4], 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => ReadAll(png, PixelFormat.Rgba32, out _));
        StreamingAssert.RowsEqualDecodeInto(Codec, png, PixelFormat.Rgba64, expectStreaming: true);
    }

    [Fact]
    public void PaletteAfterIdat_FallsBack()
    {
        // PLTE after the data: the span decoder takes it, so the reader reads the file whole.
        var palette = new byte[12];
        for (var i = 0; i < palette.Length; i++)
            palette[i] = (byte)(i * 21);
        var png = HandBuilt(3, 2, 11, 6, seed: 4, palette: false, after: s => PngRgba8ExpansionTests.Chunk(s, "PLTE"u8, palette));
        StreamingAssert.RowsEqualDecodeInto(Codec, png, PixelFormat.Rgba32, expectStreaming: false);
    }

    [Fact]
    public void ChunksAfterIdatThatChangeWrittenRows_AreRefused()
    {
        // The specification puts tRNS before IDAT; the span decoder honours one after it, the stream has written its
        // rows by then and refuses the file rather than return other pixels.
        var png = HandBuilt(2, 8, 9, 5, seed: 8, after: s => PngRgba8ExpansionTests.Chunk(s, "tRNS"u8, [0, 1, 0, 2, 0, 3]));
        PngImageDecoder.TryDecodeInto(png, new byte[9 * 5 * 4], 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        Should.Throw<InvalidDataException>(() => ReadAll(png, PixelFormat.Rgba32, out _)).Message.ShouldStartWith(PngReader.LateChunkMessage);

        // A late tRNS changes only alpha: a format without alpha streams it, with TryDecodeInto's rows.
        foreach (var format in new[] { PixelFormat.Rgb24, PixelFormat.Gray8, PixelFormat.Bgr48, PixelFormat.Gray16 })
            StreamingAssert.RowsEqualDecodeInto(Codec, png, format, expectStreaming: true);
        var indexed = HandBuilt(3, 4, 9, 5, seed: 8, after: s => PngRgba8ExpansionTests.Chunk(s, "tRNS"u8, [0, 10, 20, 30]));
        Should.Throw<InvalidDataException>(() => ReadAll(indexed, PixelFormat.Rgba32, out _)).Message.ShouldStartWith(PngReader.LateChunkMessage);
        foreach (var format in new[] { PixelFormat.Rgb24, PixelFormat.Gray8, PixelFormat.Rgb48 })
            StreamingAssert.RowsEqualDecodeInto(Codec, indexed, format, expectStreaming: true);

        var hdr = HandBuilt(2, 8, 9, 5, seed: 8, after: s => PngRgba8ExpansionTests.Chunk(s, "cICP"u8, [9, 16, 0, 1]));
        PngImageDecoder.TryDecodeInto(hdr, new byte[9 * 5 * 4], 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => ReadAll(hdr, PixelFormat.Rgba32, out _)).Message.ShouldStartWith(PngReader.LateChunkMessage);
        StreamingAssert.RowsEqualDecodeInto(Codec, hdr, PixelFormat.Rgba64, expectStreaming: true);
    }

    [Fact]
    public void InfoIsTheChunksBeforeTheImageData()
    {
        var png = HandBuilt(0, 8, 8, 4, seed: 2, trns: true);
        using var reader = PngImageDecoder.OpenRowReader(new ChunkedStream(png, maxChunk: 3), PixelFormat.Rgba32);
        PngImageDecoder.TryReadInfo(png, out var info).ShouldBeTrue();
        reader.Info.ShouldBe(info);
        reader.IsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void EveryTruncation_FailsExactlyWhereTheSpanDecodeDoes()
    {
        var files = new[]
        {
            HandBuilt(6, 8, 7, 5, seed: 1, idatBytes: 20),
            HandBuilt(3, 4, 9, 4, seed: 2, idatBytes: 9, trns: true, after: s => PngRgba8ExpansionTests.Chunk(s, "tEXt"u8, "a\0b"u8)),
            HandBuilt(0, 16, 5, 6, seed: 3, iend: false),
        };
        foreach (var png in files)
        {
            for (var cut = 0; cut <= png.Length; cut++)
                SameVerdict(png[..cut], PixelFormat.Rgba32, $"cut to {cut} of {png.Length}");
        }
    }

    [Fact]
    public void BadCrcs_AndCorruptData_Fail()
    {
        var png = HandBuilt(6, 8, 7, 5, seed: 1, idatBytes: 20, after: s => PngRgba8ExpansionTests.Chunk(s, "zzZz"u8, [1, 2, 3]));
        foreach (var chunk in ChunkStarts(png))
        {
            // The last CRC byte of each chunk, then a payload byte.
            var crc = (byte[])png.Clone();
            crc[chunk.End - 1] ^= 0x40;
            SameVerdict(crc, PixelFormat.Rgba32, $"CRC of {chunk.Type}").ShouldBeFalse();
            if (chunk.Length > 0)
            {
                var payload = (byte[])png.Clone();
                payload[chunk.Offset + 8] ^= 0x01;
                SameVerdict(payload, PixelFormat.Rgba32, $"payload of {chunk.Type}");
            }
        }
    }

    [Fact]
    public void ReadingTheFirstRows_ReadsOnlyAPrefix()
    {
        const int w = 1024, h = 1024;
        var png = PngWriter.EncodeRgb8(Noise(w, h, 3, seed: 11), w, h);
        var stream = new ChunkedStream(png, maxChunk: 4096);
        using var reader = PngImageDecoder.OpenRowReader(stream, PixelFormat.Rgb24);
        var rows = new byte[w * 3 * 16];
        reader.ReadRows(rows, 16, cancellationToken: TestContext.Current.CancellationToken).ShouldBe(16);
        reader.IsStreaming.ShouldBeTrue();
        stream.BytesRead.ShouldBeLessThan(png.Length / 8, $"{stream.BytesRead} of {png.Length} bytes read for 16 of {h} rows");
    }

    [Fact]
    public void ARowComesOutOnceItsBytesArrive()
    {
        // 100-byte rows trickling in a byte at a time: the first row needs a few hundred bytes, not a buffer's worth.
        var png = HandBuilt(2, 8, 33, 400, seed: 13);
        var stream = new ChunkedStream(png, maxChunk: 1);
        using var reader = PngImageDecoder.OpenRowReader(stream, PixelFormat.Rgb24);
        var row = new byte[33 * 3];
        reader.ReadRows(row, 1, cancellationToken: TestContext.Current.CancellationToken).ShouldBe(1);
        stream.BytesRead.ShouldBeLessThan(1000);
        png.Length.ShouldBeGreaterThan(20_000);
    }

    [Fact]
    public void TheFallbackSaysWhy()
    {
        var png = PngTransparentColorTests.WithChunk(MagickPng(2, 8, interlace: true, 9, 7), "cICP"u8, [9, 16, 0, 1]);
        Should.Throw<InvalidDataException>(() => ReadAll(png, PixelFormat.Rgba32, out _)).Message.ShouldContain("PQ or HLG");
    }

    [Fact]
    public void MemoryIsAFewRows_NotTheImage()
    {
        // 8192 × 4096 RGB: 96 MiB of pixels, a file of several megabytes; the reader holds a few rows.
        const int w = 8192, h = 4096;
        var pixels = Noise(w, h, 3, seed: 12, bits: 2);
        var png = PngWriter.EncodeRgb8(pixels, w, h);
        png.Length.ShouldBeGreaterThan(4 << 20);
        var row = new byte[w * 3];
        var rowsRead = 0;
        StreamingAssert.AllocatesLessThan(64 * 1024, () =>
        {
            using var reader = PngImageDecoder.OpenRowReader(new ChunkedStream(png, maxChunk: 65536), PixelFormat.Rgb24);
            for (var y = 0; y < h; y++)
                rowsRead += reader.ReadRows(row, 1); // no assertion per row: Shouldly allocates
        }, "a streamed 8192x4096 decode");
        rowsRead.ShouldBe(2 * h);
        // And the rows are right: the last one equals the source.
        using var check = PngImageDecoder.OpenRowReader(new MemoryStream(png), PixelFormat.Rgb24);
        for (var y = 0; y < h; y++)
            check.ReadRows(row, 1, cancellationToken: TestContext.Current.CancellationToken);
        row.AsSpan().SequenceEqual(pixels.AsSpan((h - 1) * w * 3, w * 3)).ShouldBeTrue();
    }

    [Fact]
    public async Task EndToEnd_AsyncFileStream_ReencodedRowsDecodeTheSame()
    {
        const int w = 211, h = 157;
        var source = PngWriter.Encode(Gradient(w, h, alpha: true), w, h);
        var path = Path.Combine(Path.GetTempPath(), $"png-stream-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, source, TestContext.Current.CancellationToken);
        try
        {
            byte[] rows;
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            await using (var reader = await PngImageDecoder.OpenRowReaderAsync(new AsyncOnlyStream(file), PixelFormat.Rgba32,
                             cancellationToken: TestContext.Current.CancellationToken))
            {
                reader.IsStreaming.ShouldBeTrue();
                rows = new byte[w * h * 4];
                var done = 0;
                while (done < h)
                    done += await reader.ReadRowsAsync(rows.AsMemory(done * w * 4), 7, cancellationToken: TestContext.Current.CancellationToken);
            }
            var expected = new byte[w * h * 4];
            PngImageDecoder.TryDecodeInto(source, expected, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            rows.ShouldBe(expected);
            var reencoded = PngWriter.Encode(rows, w, h);
            var again = new byte[w * h * 4];
            PngImageDecoder.TryDecodeInto(reencoded, again, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            again.ShouldBe(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Asserts the streaming reader and <see cref="PngImageDecoder.TryDecodeInto"/> agree on <paramref name="data"/>: the
    /// same rows, or the reader throws <see cref="InvalidDataException"/> where the span decode returns false (and only
    /// there, but for the documented late-chunk refusal). Returns the span decode's verdict.
    /// </summary>
    internal static bool SameVerdict(byte[] data, PixelFormat format, string what, int seed = 1)
    {
        var ok = PngImageDecoder.TryReadInfo(data, out var info);
        byte[]? expected = null;
        if (ok)
        {
            expected = new byte[(long)info.Width * info.Height * format.BytesPerPixel];
            ok = PngImageDecoder.TryDecodeInto(data, expected, 0, format);
        }
        byte[] actual;
        try
        {
            actual = StreamingAssert.ReadAll(data, StreamingAssert.Feed.RandomChunks,
                input => PngImageDecoder.CreateRowReader(input, format, null), out _, out _, out _, seed);
        }
        catch (InvalidDataException e)
        {
            if (ok && !e.Message.StartsWith(PngReader.LateChunkMessage, StringComparison.Ordinal))
                throw new ShouldAssertException($"{what}: the span decode accepts it, the stream threw: {e.Message}");
            return ok;
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{what}: the stream threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
        if (!ok)
            throw new ShouldAssertException($"{what}: the span decode refuses it, the stream decoded it.");
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new ShouldAssertException($"{what}: the rows differ from TryDecodeInto.");
        return true;
    }

    private static byte[] ReadAll(byte[] png, PixelFormat format, out bool streaming) =>
        StreamingAssert.ReadAll(png, StreamingAssert.Feed.RandomChunks, input => PngImageDecoder.CreateRowReader(input, format, null),
            out streaming, out _, out _);

    internal readonly record struct ChunkRef(int Offset, string Type, int Length)
    {
        public int End => Offset + 12 + Length;
    }

    internal static List<ChunkRef> ChunkStarts(byte[] png)
    {
        var list = new List<ChunkRef>();
        for (var pos = 8; pos + 12 <= png.Length;)
        {
            var length = (long)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            if (pos + 12 + length > png.Length)
                break;
            list.Add(new ChunkRef(pos, System.Text.Encoding.Latin1.GetString(png, pos + 4, 4), (int)length));
            pos += 12 + (int)length;
        }
        return list;
    }

    /// <summary>
    /// A PNG built here byte by byte: seeded random samples (indices inside the palette), each row with a filter type of
    /// its own (cycling through all five), the zlib stream cut into IDATs of <paramref name="idatBytes"/>.
    /// </summary>
    internal static byte[] HandBuilt(int colorType, int depth, int width, int height, int seed, int idatBytes = 8192,
        bool trns = false, bool palette = true, Action<Stream>? between = null, Action<Stream>? after = null, bool iend = true)
    {
        var random = new Random(seed);
        var spp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var entries = colorType == 3 ? Math.Min(1 << depth, 200) : 0;
        var rowBytes = (width * spp * depth + 7) / 8;
        var bpp = Math.Max(1, spp * depth / 8);
        var rows = new byte[height][];
        for (var y = 0; y < height; y++)
        {
            var row = new byte[rowBytes];
            for (var i = 0; i < width * spp; i++)
            {
                var max = colorType == 3 ? entries : 1 << Math.Min(depth, 8);
                var v = random.Next(max);
                if (depth == 16)
                {
                    row[2 * i] = (byte)random.Next(256);
                    row[2 * i + 1] = (byte)random.Next(256);
                }
                else if (depth == 8)
                    row[i] = (byte)v;
                else
                    row[i * depth / 8] |= (byte)(v << (8 - depth - i * depth % 8));
            }
            rows[y] = row;
        }
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            var filter = (byte)((y + seed) % 5);
            raw.WriteByte(filter);
            var above = y > 0 ? rows[y - 1] : new byte[rowBytes];
            for (var i = 0; i < rowBytes; i++)
            {
                int a = i >= bpp ? rows[y][i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
                var p = filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
                raw.WriteByte((byte)(rows[y][i] - p));
            }
        }
        byte[] zlib;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(raw.ToArray());
            zlib = z.ToArray();
        }

        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        PngRgba8ExpansionTests.Chunk(ms, "IHDR"u8, ihdr);
        PngRgba8ExpansionTests.Chunk(ms, "gAMA"u8, [0, 0, 0xB1, 0x8F]);
        if (colorType == 3 && palette)
        {
            var plte = new byte[entries * 3];
            random.NextBytes(plte);
            PngRgba8ExpansionTests.Chunk(ms, "PLTE"u8, plte);
        }
        if (trns)
        {
            byte[] t = colorType switch
            {
                3 => [.. Enumerable.Range(0, entries / 2).Select(i => (byte)(i * 37))],
                0 => [0, (byte)(depth == 16 ? rows[0][1] : 1)],
                _ => [0, rows[0][0], 0, rows[0][1], 0, rows[0][2]],
            };
            if (colorType == 0 && depth == 16)
                t = [rows[0][0], rows[0][1]];
            PngRgba8ExpansionTests.Chunk(ms, "tRNS"u8, t);
        }
        for (var o = 0; o < zlib.Length; o += idatBytes)
        {
            PngRgba8ExpansionTests.Chunk(ms, "IDAT"u8, zlib.AsSpan(o, Math.Min(idatBytes, zlib.Length - o)));
            if (o == 0 && between is not null && zlib.Length > idatBytes)
                between(ms);
        }
        after?.Invoke(ms);
        if (iend)
            PngRgba8ExpansionTests.Chunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>An ImageMagick (libpng) PNG of the colour type and depth: foreign bytes.</summary>
    internal static byte[] MagickPng(int colorType, int depth, bool interlace, int w, int h)
    {
        var levels = 1 << Math.Min(depth, 8);
        using var image = new MagickImage(MagickColors.Black, (uint)w, (uint)h);
        if (colorType is 4 or 6)
            image.Alpha(AlphaOption.Set);
        using (var pixels = image.GetPixels())
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var k = (x * 7 + y * 3) % (colorType == 3 ? Math.Max(1, Math.Min(levels - 1, 200)) : levels);
                    var v = (float)(k * (double)Quantum.Max / (levels - 1));
                    float[] px = colorType switch
                    {
                        0 => [v, v, v],
                        4 => [v, v, v, (float)((x * 13 % 256) * (double)Quantum.Max / 255)],
                        3 => [(float)(k * 37 % 256 * (double)Quantum.Max / 255), (float)((255 - k * 53 % 256) * (double)Quantum.Max / 255), (float)(k * 91 % 256 * (double)Quantum.Max / 255)],
                        2 => [v, (float)((x * 9 % 256) * (double)Quantum.Max / 255), (float)((y * 5 % 256) * (double)Quantum.Max / 255)],
                        _ => [v, (float)((x * 9 % 256) * (double)Quantum.Max / 255), (float)((y * 5 % 256) * (double)Quantum.Max / 255), (float)((x * y % 256) * (double)Quantum.Max / 255)],
                    };
                    pixels.SetPixel(x, y, px);
                }
            }
        }
        if (colorType == 3)
            image.ColorType = ColorType.Palette;
        if (interlace)
            image.Settings.Interlace = Interlace.Png;
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        image.Settings.SetDefine(MagickFormat.Png, "color-type", colorType.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return image.ToByteArray(MagickFormat.Png);
    }

    internal static byte[] Gradient(int w, int h, bool alpha)
    {
        var c = alpha ? 4 : 3;
        var p = new byte[w * h * c];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * c;
            p[i] = (byte)(x * 255 / w);
            p[i + 1] = (byte)(y * 255 / h);
            p[i + 2] = (byte)((x ^ y) & 0xFF);
            if (alpha)
                p[i + 3] = (byte)((x + y) % 256);
        }
        return p;
    }

    // A smooth gradient with `bits` low bits of seeded noise per sample: compresses, but not to nothing.
    internal static byte[] Noise(int w, int h, int channels, int seed, int bits = 8)
    {
        var p = new byte[(long)w * h * channels];
        new Random(seed).NextBytes(p);
        var mask = (1 << bits) - 1;
        for (long i = 0; i < p.Length; i++)
        {
            var x = (int)(i / channels % w);
            p[i] = (byte)(((x >> 5) & ~mask) | (p[i] & mask));
        }
        return p;
    }

    /// <summary>A stream whose synchronous reads throw, as an ASP.NET Core request body's do.</summary>
    private sealed class AsyncOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1500)], cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The streaming PNG reader on untrusted input: seeded mutations of a corpus (hand-built files of every layout,
/// ImageMagick's, ours) — random bytes, bytes with CRCs put right, IHDR fields set to values that break arithmetic,
/// chunks deleted, repeated, swapped, moved past IDAT — read through the stream either give exactly
/// <see cref="PngImageDecoder.TryDecodeInto"/>'s rows or fail with <see cref="InvalidDataException"/> where it returns
/// false; never another exception or a hang. Iterations: <c>PNG_STREAM_FUZZ_ITERATIONS</c> (default 2000), seed:
/// <c>PNG_STREAM_FUZZ_SEED</c>.
/// </summary>
public sealed class PngStreamingRobustnessTests
{
    private const long MaxPixels = 1L << 20;

    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("PNG_STREAM_FUZZ_ITERATIONS"), out var n) ? n : 2000;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("PNG_STREAM_FUZZ_SEED"), out var n) ? n : 20261123;

    private static readonly uint[] Interesting = [0, 1, 2, 3, 7, 8, 255, 256, 65535, 65536, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF];

    private static List<byte[]> Seeds()
    {
        var seeds = new List<byte[]>();
        var k = 0;
        foreach (var (colorType, depth) in new[] { (0, 1), (0, 4), (0, 16), (2, 8), (2, 16), (3, 2), (3, 8), (4, 8), (6, 8), (6, 16) })
        {
            seeds.Add(PngStreamingDecodeTests.HandBuilt(colorType, depth, 11, 7, seed: ++k, idatBytes: 23, trns: colorType is 0 or 2 or 3,
                after: s => PngRgba8ExpansionTests.Chunk(s, "tEXt"u8, "k\0v"u8)));
        }
        seeds.Add(PngStreamingDecodeTests.MagickPng(2, 8, interlace: false, 17, 9));
        seeds.Add(PngStreamingDecodeTests.MagickPng(3, 4, interlace: true, 9, 9));
        seeds.Add(PngWriter.Encode(PngStreamingDecodeTests.Gradient(23, 13, alpha: true), 23, 13));
        return seeds;
    }

    [Fact]
    public void Seeds_stream_and_survive_mutation()
    {
        var seeds = Seeds();
        foreach (var seed in seeds)
            PngStreamingDecodeTests.SameVerdict(seed, PixelFormat.Rgba32, "seed").ShouldBeTrue();

        var rng = new Random(Seed);
        var clock = Stopwatch.StartNew();
        PixelFormat[] formats = [PixelFormat.Rgba32, PixelFormat.Rgb24, PixelFormat.Rgba64, PixelFormat.GrayAlpha16];
        for (var i = 0; i < Iterations; i++)
        {
            var data = Mutate(seeds[rng.Next(seeds.Count)], rng, out var how);
            if (data.Length >= 24)
            {
                var w = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
                var h = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20));
                if ((long)w * h > MaxPixels)
                {
                    // Too large for the span decode's buffer here; the stream must still fail cleanly or decode rows.
                    StreamOnly(data, $"{how}, iteration {i}, seed {Seed}");
                    continue;
                }
            }
            PngStreamingDecodeTests.SameVerdict(data, formats[rng.Next(formats.Length)], $"{how}, iteration {i}, seed {Seed}", seed: i);
        }
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(30));
    }

    // A header too large to decode whole: the reader may only throw InvalidDataException, reading a few rows.
    private static void StreamOnly(byte[] data, string what)
    {
        try
        {
            using var reader = PngImageDecoder.CreateRowReader(new StreamInput(new ChunkedStream(data, maxChunk: 64)), PixelFormat.Rgba32, null);
            reader.Initialize(TestContext.Current.CancellationToken);
            if ((long)reader.Width * 4 > 1 << 24)
                return;
            var row = new byte[reader.Width * 4];
            for (var y = 0; y < Math.Min(reader.Height, 4); y++)
                reader.ReadRows(row, 1, cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e)
        {
            throw new ShouldAssertException($"{what}: the stream threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }

    private static byte[] Mutate(byte[] seed, Random rng, out string how)
    {
        var data = (byte[])seed.Clone();
        var chunks = PngStreamingDecodeTests.ChunkStarts(data);
        switch (rng.Next(8))
        {
            case 0:
                how = "random bytes";
                for (var k = rng.Next(1, 5); k > 0; k--)
                    data[rng.Next(data.Length)] = (byte)rng.Next(256);
                return data;
            case 1:
                how = "random bytes, CRCs fixed";
                for (var k = rng.Next(1, 5); k > 0; k--)
                    data[rng.Next(8, data.Length)] = (byte)rng.Next(256);
                FixCrcs(data);
                return data;
            case 2:
            {
                how = "IHDR field";
                var at = 16 + 4 * rng.Next(3);
                if (at == 24)
                    data[24 + rng.Next(5)] = (byte)rng.Next(rng.Next(2) == 0 ? 20 : 256);
                else
                    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), rng.Next(3) == 0 ? (uint)rng.Next(70) : Interesting[rng.Next(Interesting.Length)]);
                FixCrcs(data);
                return data;
            }
            case 3:
            {
                how = "chunk deleted";
                var c = chunks[rng.Next(chunks.Count)];
                return [.. data[..c.Offset], .. data[c.End..]];
            }
            case 4:
            {
                how = "chunk repeated";
                var c = chunks[rng.Next(chunks.Count)];
                return [.. data[..c.End], .. data[c.Offset..c.End], .. data[c.End..]];
            }
            case 5:
            {
                how = "chunks swapped";
                if (chunks.Count < 3)
                    goto case 0;
                var i = rng.Next(chunks.Count - 1);
                var (a, b) = (chunks[i], chunks[i + 1]);
                return [.. data[..a.Offset], .. data[b.Offset..b.End], .. data[a.Offset..b.Offset], .. data[b.End..]];
            }
            case 6:
            {
                how = "chunk moved to the end";
                var c = chunks[rng.Next(chunks.Count)];
                var rest = (byte[])[.. data[..c.Offset], .. data[c.End..]];
                var last = PngStreamingDecodeTests.ChunkStarts(rest).LastOrDefault();
                var at = last.Type == "IEND" ? last.Offset : rest.Length;
                return [.. rest[..at], .. data[c.Offset..c.End], .. rest[at..]];
            }
            default:
                how = "cut";
                return data[..rng.Next(data.Length)];
        }
    }

    private static void FixCrcs(byte[] data)
    {
        foreach (var c in PngStreamingDecodeTests.ChunkStarts(data))
        {
            var crc = PngCrc32.Compute(data.AsSpan(c.Offset + 4, 4), data.AsSpan(c.Offset + 8, c.Length));
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(c.Offset + 8 + c.Length), crc);
        }
    }
}
