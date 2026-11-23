using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tests.Streaming;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The streaming <see cref="PngImageEncoder"/> session: bytes leave as the rows arrive, the file decodes (ours and
/// libpng through Magick.NET) to exactly the input, the bytes do not depend on how the rows were split, the options that
/// need the whole image fall back to the buffered session's unchanged bytes, and memory is a band of rows.
/// </summary>
public sealed class PngStreamingEncodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A column pattern plus a row pattern plus a little noise: every filter wins somewhere (Paeth predicts the sum
    /// exactly, Up wins where the row pattern is flat, Sub where the column pattern is), so a band that filtered against
    /// the wrong row above would decode wrong. Compressible enough to be quick, noisy enough to fill IDATs.
    /// </summary>
    private static byte[] Photo(int width, int height, PixelFormat format, int seed)
    {
        var bpp = format.BytesPerPixel;
        var pixels = new byte[(long)width * height * bpp];
        var random = new Random(seed);
        var columns = new byte[width * bpp];
        random.NextBytes(columns);
        var rows = new byte[height];
        random.NextBytes(rows);
        for (var y = 0; y < height; y++)
        {
            var flatColumns = y / 64 % 3 == 1;
            var flatRows = y / 64 % 3 == 2;
            for (var x = 0; x < width; x++)
            {
                var at = ((long)y * width + x) * bpp;
                for (var b = 0; b < bpp; b++)
                    pixels[at + b] = (byte)((flatColumns ? x : columns[x * bpp + b]) + (flatRows ? 0 : rows[y]) + random.Next(4));
            }
        }
        return pixels;
    }

    private static byte[] Encode(byte[] pixels, int width, int height, PixelFormat format, PngWriteOptions? options,
        Func<int, int>? band = null, int seed = 1, bool expectStreaming = true)
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = PngImageEncoder.Create(output, options);
        session.IsStreaming.ShouldBe(expectStreaming);
        var frame = session.AddFrame(width, height, format);
        var rowBytes = width * format.BytesPerPixel;
        var random = new Random(seed);
        while (frame.RowsWritten < height)
        {
            var n = Math.Min(band?.Invoke(random.Next()) ?? height, height - frame.RowsWritten);
            frame.WriteRows(pixels.AsSpan(frame.RowsWritten * rowBytes, n * rowBytes), n, 0, Ct);
        }
        session.Commit(Ct);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>What the file holds: the input in the encoder's native layout (BGR reordered, alpha unpremultiplied).</summary>
    private static (byte[] Pixels, PixelFormat Format) Expected(byte[] pixels, int width, int height, PixelFormat format)
    {
        var native = PngImageEncoder.GetClosestInputFormat(format);
        return (PixelConverter.ToPacked(pixels, 0, format, native, width, height), native);
    }

    private static byte[] DecodeOurs(byte[] file, int width, int height, PixelFormat format)
    {
        var pixels = new byte[(long)width * height * format.BytesPerPixel];
        PngImageDecoder.Registration.TryDecodeInto(file, pixels, width * format.BytesPerPixel, format, null, Ct).ShouldBeTrue();
        return pixels;
    }

    /// <summary>libpng's reading of the file, as 16-bit samples in the file's channel order.</summary>
    private static ushort[] DecodeMagick(byte[] file, int width, int height, int channels)
    {
        using var image = new MagickImage(file);
        ((int)image.Width, (int)image.Height).ShouldBe((width, height));
        using var pixels = image.GetPixels();
        var map = channels switch { 1 => "R", 2 => "RA", 3 => "RGB", _ => "RGBA" };
        return pixels.ToShortArray(0, 0, (uint)width, (uint)height, map)!;
    }

    private static ushort[] AsSixteenBit(byte[] native, PixelFormat format) => format.Sample == SampleFormat.UInt16
        ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(native).ToArray()
        : [.. native.Select(v => (ushort)(v * 257))];

    private static int Channels(PixelFormat native) => native.Layout switch
    {
        ChannelLayout.Rgba => 4,
        ChannelLayout.Rgb => 3,
        ChannelLayout.GrayAlpha => 2,
        _ => 1,
    };

    public static TheoryData<int> FormatIndices()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < PixelFormat.All.Length; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(FormatIndices))]
    public void EveryInputFormatDecodesToExactlyItsPixels(int formatIndex)
    {
        var format = PixelFormat.All[formatIndex];
        const int w = 97, h = 61;
        var pixels = Photo(w, h, format, formatIndex);
        var (expected, native) = Expected(pixels, w, h, format);
        foreach (var options in new PngWriteOptions?[] { null, new() { CompressionLevel = CompressionLevel.NoCompression }, new() { ParallelFragments = 4 } })
        {
            var file = Encode(pixels, w, h, format, options, band: r => 1 + r % 9);
            DecodeOurs(file, w, h, native).ShouldBe(expected, $"{format} {options}: ours");
            DecodeMagick(file, w, h, Channels(native)).ShouldBe(AsSixteenBit(expected, native), $"{format} {options}: libpng");
        }
    }

    [Theory]
    [MemberData(nameof(FormatIndices))]
    public void EveryInputFormatStreamsAcrossManyIdatsAndParallelBands(int formatIndex)
    {
        var format = PixelFormat.All[formatIndex];
        const int w = 1031, h = 700; // 16-bit RGBA: 5.8 MB of payload, two parallel bands; several IDATs everywhere
        var pixels = Photo(w, h, format, formatIndex + 100);
        var (expected, native) = Expected(pixels, w, h, format);
        foreach (var options in new PngWriteOptions?[] { null, new() { ParallelFragments = 3, CompressionLevel = CompressionLevel.Fastest } })
        {
            var file = Encode(pixels, w, h, format, options, band: r => 1 + r % 50);
            Idats(file).Count.ShouldBeGreaterThan(1, $"{format} {options}");
            DecodeOurs(file, w, h, native).ShouldBe(expected, $"{format} {options}: ours");
            DecodeMagick(file, w, h, Channels(native)).ShouldBe(AsSixteenBit(expected, native), $"{format} {options}: libpng");
        }
    }

    [Fact]
    public void DiscardAlphaAndTransparentColourStream()
    {
        const int w = 50, h = 31;
        var rgba = Photo(w, h, PixelFormat.Rgba64, 3);
        var file = Encode(rgba, w, h, PixelFormat.Rgba64, new PngWriteOptions { DiscardAlpha = true }, band: _ => 4);
        file.ShouldBe(PngWriter.EncodeRgba16(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(rgba), w, h,
            new PngWriteOptions { DiscardAlpha = true }));
        var rgb = Photo(w, h, PixelFormat.Rgb24, 4);
        var keyed = new PngWriteOptions { TransparentColor = [1, 2, 3], Gamma = 0.45455, Text = [new PngText("Title", "streamed")] };
        Encode(rgb, w, h, PixelFormat.Rgb24, keyed, band: _ => 7).ShouldBe(PngWriter.EncodeRgb8(rgb, w, h, keyed));
    }

    [Fact]
    public void ASmallFileIsTheBufferedFileByteForByte()
    {
        const int w = 64, h = 48;
        var pixels = Photo(w, h, PixelFormat.Rgba32, 9);
        var options = new PngWriteOptions
        {
            Exif = [0x49, 0x49, 0x2A, 0, 8, 0, 0, 0, 0, 0], PhysicalDimensions = PngPhysicalDimensions.FromDpi(96),
            Cicp = CicpChunk.Srgb, Text = [new PngText("Comment", new string('x', 3000))],
        };
        Encode(pixels, w, h, PixelFormat.Rgba32, options, band: _ => 5).ShouldBe(PngWriter.Encode(pixels, w, h, options));
        Encode(pixels, w, h, PixelFormat.Rgba32, null).ShouldBe(PngWriter.Encode(pixels, w, h));
    }

    [Fact]
    public void ALargeFileHasTheBufferedZlibStreamInFixedSizeIdats()
    {
        const int w = 900, h = 600;
        var pixels = Photo(w, h, PixelFormat.Rgb24, 11);
        var streamed = Encode(pixels, w, h, PixelFormat.Rgb24, null, band: r => 1 + r % 13);
        var buffered = PngWriter.EncodeRgb8(pixels, w, h);
        var idats = Idats(streamed);
        idats.Count.ShouldBeGreaterThan(2);
        idats.SkipLast(1).ShouldAllBe(c => c.Length == PngStreamingSession.IdatChunkBytes);
        Idats(buffered).Count.ShouldBe(1);
        idats.SelectMany(c => c).ToArray().ShouldBe(Idats(buffered)[0]);
        streamed.Length.ShouldBe(buffered.Length + 12 * (idats.Count - 1));
    }

    [Fact]
    public void ParallelBandsAreTheBufferedFragmentsUpToTheCap()
    {
        // 6 fragments of 117 rows x 3601 bytes: under the cap, so the zlib stream is PngWriter's.
        const int w = 1200, h = 700;
        var pixels = Photo(w, h, PixelFormat.Rgb24, 14);
        var options = new PngWriteOptions { ParallelFragments = 6 };
        var streamed = Encode(pixels, w, h, PixelFormat.Rgb24, options, band: r => 1 + r % 40);
        Idats(streamed).SelectMany(c => c).ToArray().ShouldBe(Idats(PngWriter.EncodeRgb8(pixels, w, h, options))[0]);

        // 2 fragments of 1500 rows x 4097 bytes: over the cap, so bands of 511 rows, and other bytes for the same pixels.
        const int tall = 3000, wide = 1024;
        var more = Photo(wide, tall, PixelFormat.Rgba32, 15);
        var two = new PngWriteOptions { ParallelFragments = 2 };
        var file = Encode(more, wide, tall, PixelFormat.Rgba32, two, band: r => 1 + r % 40);
        var buffered = PngWriter.Encode(more, wide, tall, two);
        Idats(file).SelectMany(c => c).ToArray().ShouldNotBe(Idats(buffered)[0]);
        DecodeOurs(file, wide, tall, PixelFormat.Rgba32).ShouldBe(more);
        Math.Abs(file.Length - buffered.Length).ShouldBeLessThan(buffered.Length / 1000, "within 0.1% of the buffered size");
    }

    [Fact]
    public void TheBytesDoNotDependOnHowTheRowsAreSplit()
    {
        const int w = 1024, h = 700; // RGBA: 2.9 MB of payload
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgr48 })
        {
            var pixels = Photo(w, h, format, 5);
            foreach (var options in new PngWriteOptions?[] { null, new() { ParallelFragments = 4 } })
            {
                var whole = Encode(pixels, w, h, format, options);
                Encode(pixels, w, h, format, options, band: _ => 1).ShouldBe(whole, $"{format} {options}: one row at a time");
                Encode(pixels, w, h, format, options, band: r => 1 + r % 97, seed: 2).ShouldBe(whole, $"{format} {options}: random bands");
                Encode(pixels, w, h, format, options, band: _ => 333).ShouldBe(whole, $"{format} {options}: 333 rows");

                // Strided rows: the gaps are never read.
                var rowBytes = w * format.BytesPerPixel;
                var strided = new byte[(h - 1) * (rowBytes + 13) + rowBytes];
                Random.Shared.NextBytes(strided);
                for (var y = 0; y < h; y++)
                    pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(strided.AsSpan(y * (rowBytes + 13)));
                var output = new ArrayBufferWriter<byte>();
                using (var session = PngImageEncoder.Create(output, options))
                {
                    session.AddFrame(w, h, format).WriteRows(strided, h, rowBytes + 13, Ct);
                    session.Commit(Ct);
                }
                output.WrittenSpan.ToArray().ShouldBe(whole, $"{format} {options}: strided");
            }
        }
    }

    [Fact]
    public void BytesReachTheWriterBeforeCommit()
    {
        const int w = 1024, h = 512;
        var pixels = Photo(w, h, PixelFormat.Rgba32, 6);
        var file = StreamingAssert.EncoderEmitsBeforeCommit(o => PngImageEncoder.Create(o), pixels, w, h, PixelFormat.Rgba32);
        DecodeOurs(file, w, h, PixelFormat.Rgba32).ShouldBe(pixels);

        // Two bands in flight at most: the third waits for the first, whose fragment then leaves.
        const int tall = 6400; // bands of 1023 rows: three are submitted by the half
        var more = Photo(w, tall, PixelFormat.Rgba32, 7);
        var parallel = StreamingAssert.EncoderEmitsBeforeCommit(o => PngImageEncoder.Create(o, new PngWriteOptions { ParallelFragments = 2 }),
            more, w, tall, PixelFormat.Rgba32);
        DecodeOurs(parallel, w, tall, PixelFormat.Rgba32).ShouldBe(more);
    }

    [Fact]
    public void OptionsThatNeedTheWholeImageFallBackWithUnchangedBytes()
    {
        const int w = 300, h = 200;
        var pixels = Photo(w, h, PixelFormat.Rgba32, 8);
        var reduced = new PngWriteOptions { LosslessReduction = true };
        Encode(pixels, w, h, PixelFormat.Rgba32, reduced, band: _ => 7, expectStreaming: false).ShouldBe(PngWriter.Encode(pixels, w, h, reduced));
        var quantized = new PngWriteOptions { Quantize = new PngQuantizeOptions() };
        Encode(pixels, w, h, PixelFormat.Rgba32, quantized, band: _ => 7, expectStreaming: false).ShouldBe(PngWriter.Encode(pixels, w, h, quantized));
        var parallel = new PngWriteOptions { ParallelFragments = 4, Animation = true };
        Encode(pixels, w, h, PixelFormat.Rgba32, parallel, band: _ => 7, expectStreaming: false).ShouldBe(PngWriter.Encode(pixels, w, h, parallel));

        using var still = PngImageEncoder.Create(new ArrayBufferWriter<byte>());
        still.MaxFrames.ShouldBe(1);
        using var reducedStill = PngImageEncoder.Create(new ArrayBufferWriter<byte>(), reduced);
        reducedStill.MaxFrames.ShouldBe(1);
        using var animation = PngImageEncoder.Create(new ArrayBufferWriter<byte>(), new PngWriteOptions { Animation = true });
        animation.MaxFrames.ShouldBe(int.MaxValue);
    }

    [Fact]
    public void ASecondFrameNeedsAnimation()
    {
        using var session = PngImageEncoder.Create(new ArrayBufferWriter<byte>());
        session.AddFrame(2, 2, PixelFormat.Rgba32).WriteRows(new byte[16], 2, 0, Ct);
        Should.Throw<InvalidOperationException>(() => session.AddFrame(2, 2, PixelFormat.Rgba32));
    }

    [Fact]
    public void BadOptionsAreRefusedBeforeAnyByteIsWritten()
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = PngImageEncoder.Create(output, new PngWriteOptions { TransparentColor = [1, 2, 3] });
        Should.Throw<ArgumentException>(() => session.AddFrame(4, 4, PixelFormat.Rgba32));
        using var text = PngImageEncoder.Create(output, new PngWriteOptions { Text = [new PngText(" bad", "x")] });
        Should.Throw<ArgumentException>(() => text.AddFrame(4, 4, PixelFormat.Rgb24));
        using var level = PngImageEncoder.Create(output, new PngWriteOptions { CompressionLevel = (CompressionLevel)7 });
        Should.Throw<ArgumentException>(() => level.AddFrame(4, 4, PixelFormat.Rgb24));
        output.WrittenCount.ShouldBe(0);
    }

    [Fact]
    public void CancellationStopsTheFrameAndDisposeWaitsForTheWorkers()
    {
        const int w = 1024, h = 2000;
        var pixels = Photo(w, h, PixelFormat.Rgba32, 12);
        foreach (var options in new PngWriteOptions?[] { null, new() { ParallelFragments = 4 } })
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var output = new ArrayBufferWriter<byte>();
            var session = PngImageEncoder.Create(output, options);
            var frame = session.AddFrame(w, h, PixelFormat.Rgba32);
            Should.Throw<OperationCanceledException>(() => frame.WriteRows(pixels, h, 0, cancelled.Token));

            // A call whose token is cancelled when it starts consumes nothing: the frame goes on and the file is complete.
            frame.WriteRows(pixels.AsSpan(0, 1500 * w * 4), 1500, 0, Ct);
            Should.Throw<OperationCanceledException>(() => frame.WriteRows(pixels.AsSpan(1500 * w * 4), h - 1500, 0, cancelled.Token));
            frame.WriteRows(pixels.AsSpan(1500 * w * 4), h - 1500, 0, Ct);
            Should.Throw<OperationCanceledException>(() => session.Commit(cancelled.Token));
            session.Commit(Ct);
            session.Dispose();
            DecodeOurs(output.WrittenSpan.ToArray(), w, h, PixelFormat.Rgba32).ShouldBe(pixels);

            // Abandoned with bands in flight: Dispose returns once the workers are done, and nothing more is written.
            var abandoned = new ArrayBufferWriter<byte>();
            var open = PngImageEncoder.Create(abandoned, options);
            open.AddFrame(w, h, PixelFormat.Rgba32).WriteRows(pixels, h - 1, 0, Ct);
            var written = abandoned.WrittenCount;
            open.Dispose();
            abandoned.WrittenCount.ShouldBe(written);
        }
    }

    [Fact]
    public void MemoryIsABandOfRowsNotTheImage()
    {
        const int w = 8192, h = 4096; // 128 MiB of RGBA
        const int band = 64;
        var rows = new byte[w * 4 * band];
        foreach (var options in new[] { new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest }, new PngWriteOptions { CompressionLevel = CompressionLevel.Fastest, ParallelFragments = 4 } })
        {
            var sink = new DiscardingWriter();
            StreamingAssert.AllocatesLessThan(4 << 20, () =>
            {
                using var session = PngImageEncoder.Create(sink, options);
                var frame = session.AddFrame(w, h, PixelFormat.Rgba32);
                for (var y = 0; y < h; y += band)
                {
                    for (var r = 0; r < band; r++)
                        rows.AsSpan(r * w * 4, w * 4).Fill((byte)(y + r));
                    frame.WriteRows(rows, band, 0, Ct);
                }
                session.Commit(Ct);
            }, $"a streaming encode of a 128 MiB image ({options})");
            sink.Written.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void ALargePhotoStreamsToAFileAndDecodesExactly()
    {
        const int w = 4000, h = 3000, band = 37;
        var path = Path.Combine(Path.GetTempPath(), $"png-stream-{Guid.NewGuid():N}.png");
        try
        {
            var pixels = Photo(w, h, PixelFormat.Bgr24, 13);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                var writer = new FileWriter(file);
                using var session = PngImageEncoder.Create(writer);
                var frame = session.AddFrame(w, h, PixelFormat.Bgr24);
                for (var y = 0; y < h; y += band)
                {
                    var n = Math.Min(band, h - y);
                    frame.WriteRows(pixels.AsSpan(y * w * 3, n * w * 3), n, 0, Ct);
                    if (y > h / 2)
                        file.Length.ShouldBeGreaterThan(h, "the file grows while the rows arrive");
                }
                session.Commit(Ct);
                writer.LargestSpan.ShouldBeLessThanOrEqualTo(1 << 20, "the session asks for bands, not the file");
            }
            var (expected, native) = Expected(pixels, w, h, PixelFormat.Bgr24);
            DecodeOurs(File.ReadAllBytes(path), w, h, native).ShouldBe(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The data of each IDAT chunk, in order.</summary>
    private static List<byte[]> Idats(byte[] png)
    {
        var chunks = new List<byte[]>();
        for (var at = 8; at < png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8))
                chunks.Add(png.AsSpan(at + 8, length).ToArray());
            at += length + 12;
        }
        return chunks;
    }

    /// <summary>An output that counts bytes and keeps none: one reused buffer.</summary>
    private sealed class DiscardingWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[1 << 20];

        public long Written { get; private set; }

        public void Advance(int count) => Written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (sizeHint > _buffer.Length)
                _buffer = new byte[sizeHint];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    /// <summary>An output over a file: every <see cref="Advance"/> goes straight to the stream.</summary>
    private sealed class FileWriter(Stream stream) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[4096];

        public int LargestSpan { get; private set; }

        public void Advance(int count) => stream.Write(_buffer, 0, count);

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            LargestSpan = Math.Max(LargestSpan, sizeHint);
            if (sizeHint > _buffer.Length)
                _buffer = new byte[sizeHint];
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
