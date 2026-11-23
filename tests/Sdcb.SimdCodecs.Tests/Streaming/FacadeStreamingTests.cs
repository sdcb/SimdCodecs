using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>
/// The facade's stream entry points: <see cref="ImageCodecs.OpenRowReader"/> / <see cref="ImageCodecs.OpenRowReaderAsync"/>
/// sniff and dispatch to a codec's streaming decoder or the buffered fallback, with <see cref="ImageCodecs.TryDecodeInto"/>'s
/// rows; <see cref="ImageCodecs.CreateEncoder(ImageFormat, Stream, EncodeOptions?, bool)"/> writes the buffer-writer
/// session's bytes to a stream as they come, synchronously or asynchronously only; <c>Encode(…, Stream)</c> writes the
/// bytes it always wrote.
/// </summary>
[Collection(FacadeRegistryCollection.Name)]
public sealed class FacadeStreamingTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ImageFormat[] EncoderFormats =
        [ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Tiff, ImageFormat.Webp, ImageFormat.Gif, ImageFormat.Bmp,
         ImageFormat.Ico, ImageFormat.Qoi, ImageFormat.Pnm];

    private readonly ImageDecoderRegistration[] _builtIn = ImageCodecs.RegistrySnapshot;
    private readonly ImageEncoderRegistration[] _encoders = ImageCodecs.EncoderSnapshot;

    public void Dispose()
    {
        ImageCodecs.ExchangeRegistry(_builtIn);
        ImageCodecs.ExchangeEncoders(_encoders);
    }

    private void UseRegistry(ImageDecoderRegistration first) => ImageCodecs.ExchangeRegistry([first, .. _builtIn]);

    private static byte[] Pattern(int w, int h, int channels, int seed)
    {
        var pixels = new byte[w * h * channels];
        var random = new Random(seed);
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i % (w * channels)) * 3 + (i / (w * channels)) * 5 + random.Next(0, 8));
        return pixels;
    }

    private static byte[] Fixture(string directory, string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", directory, name));

    private static Stream Feed(byte[] file, StreamingAssert.Feed feed, int seed = 1) => feed switch
    {
        StreamingAssert.Feed.OneByte => new ChunkedStream(file, maxChunk: 1, seed),
        StreamingAssert.Feed.RandomChunks => new ChunkedStream(file, maxChunk: 4096, seed),
        StreamingAssert.Feed.AsyncOnly => new ChunkedStream(file, maxChunk: 997, seed, asyncOnly: true),
        _ => new MemoryStream(file, writable: false),
    };

    /// <summary>Opens <paramref name="file"/> through the facade fed as <paramref name="feed"/> says and reads every row.</summary>
    private static async Task<(byte[] Rows, bool Streaming, int Width, int Height)> ReadAllAsync(byte[] file,
        StreamingAssert.Feed feed, PixelFormat format, DecodeOptions? options = null)
    {
        var async = feed == StreamingAssert.Feed.AsyncOnly;
        await using var reader = async
            ? await ImageCodecs.OpenRowReaderAsync(Feed(file, feed), format, options, cancellationToken: Ct)
            : ImageCodecs.OpenRowReader(Feed(file, feed), format, options, cancellationToken: Ct);
        reader.Format.ShouldBe(format);
        var rowBytes = reader.Width * format.BytesPerPixel;
        var rows = new byte[rowBytes * reader.Height];
        var random = new Random(7);
        while (reader.RowsRead < reader.Height)
        {
            var band = feed switch
            {
                StreamingAssert.Feed.OneByte => 1,
                StreamingAssert.Feed.Whole => reader.Height,
                _ => random.Next(1, 17),
            };
            var at = reader.RowsRead * rowBytes;
            var n = async
                ? await reader.ReadRowsAsync(rows.AsMemory(at), band, 0, Ct)
                : reader.ReadRows(rows.AsSpan(at), band, 0, Ct);
            n.ShouldBeGreaterThan(0, $"{feed}: no rows at {reader.RowsRead} of {reader.Height}");
        }
        (async ? await reader.ReadRowsAsync(Memory<byte>.Empty, 1, 0, Ct) : reader.ReadRows([], 1, 0, Ct)).ShouldBe(0);
        return (rows, reader.IsStreaming, reader.Width, reader.Height);
    }

    /// <summary>Asserts every feed's rows equal <see cref="ImageCodecs.TryDecodeInto"/>'s; returns whether the reader streamed.</summary>
    private static async Task<bool> RowsEqualDecodeIntoAsync(byte[] file, PixelFormat format, DecodeOptions? options = null,
        StreamingAssert.Feed[]? feeds = null, string? label = null)
    {
        ImageCodecs.TryReadInfo(file, out var info).ShouldBeTrue(label);
        var (w, h) = (options ?? DecodeOptions.Default).GetOutputSize(info);
        var expected = new byte[w * h * format.BytesPerPixel];
        ImageCodecs.TryDecodeInto(file, expected, 0, format, options, Ct).ShouldBeTrue(label);
        bool? streaming = null;
        foreach (var feed in feeds ?? StreamingAssert.AllFeeds)
        {
            var (rows, s, rw, rh) = await ReadAllAsync(file, feed, format, options);
            (rw, rh).ShouldBe((w, h), $"{label} {feed}: size");
            if (!rows.AsSpan().SequenceEqual(expected))
                throw new ShouldAssertException($"{label} {feed}: rows differ from TryDecodeInto at byte {rows.AsSpan().CommonPrefixLength(expected)}.");
            streaming ??= s;
            s.ShouldBe(streaming.Value, $"{label} {feed}: IsStreaming depends on the feed");
        }
        return streaming!.Value;
    }

    [Fact]
    public async Task OpenRowReader_DispatchesToTheStreamingDecoder()
    {
        UseRegistry(RawStreamCodec.Registration);
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(37, 23, 3), 37, 23);
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgr24, PixelFormat.Gray8, PixelFormat.Rgba64 })
            (await RowsEqualDecodeIntoAsync(file, format, label: format.ToString())).ShouldBeTrue();

        // The codec's own fallback (a variant it cannot stream) and an option it does not stream: buffered, same rows.
        var fallback = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(41, 17, 5), 41, 17, fallback: true);
        (await RowsEqualDecodeIntoAsync(fallback, PixelFormat.Bgra32)).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenRowReader_ReadsTheStreamOnlyAsFarAsTheRowsNeed()
    {
        UseRegistry(RawStreamCodec.Registration);
        const int w = 256, h = 256;
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(w, h, 3), w, h);
        var stream = new ChunkedStream(file, maxChunk: 1024, asyncOnly: true);
        await using var reader = await ImageCodecs.OpenRowReaderAsync(stream, PixelFormat.Rgba32, cancellationToken: Ct);
        (await reader.ReadRowsAsync(new byte[w * 4], 1, 0, Ct)).ShouldBe(1);
        reader.IsStreaming.ShouldBeTrue();
        stream.BytesRead.ShouldBeLessThan(file.Length / 2);
    }

    [Fact]
    public async Task OpenRowReader_ACodecWithoutAStreamingDecoderIsBuffered()
    {
        UseRegistry(RawStreamCodec.NonStreamingRegistration);
        var file = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(29, 13, 9), 29, 13);
        foreach (var format in new[] { PixelFormat.Rgb24, PixelFormat.Rgba32 })
            (await RowsEqualDecodeIntoAsync(file, format)).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenRowReader_EveryBuiltInFormatEqualsDecodeInto()
    {
        const int w = 37, h = 23;
        var rgb = Pattern(w, h, 3, 1);
        var files = EncoderFormats.Select(f => (f.ToString(), ImageCodecs.Encode(rgb, w, h, PixelFormat.Rgb24, f))).ToList();
        files.Add(("webp-lossy", Fixture("webp", "astronaut-192-q20-normal.webp")));
        files.Add(("webp-lossless", Fixture("webp", "lossless-palette-16.webp")));
        files.Add(("jbig2", Fixture("jbig2", "sym.jb2")));
        files.Add(("jp2", Fixture("jpeg2000", "jp2-rgb-struct64.jp2")));
        foreach (var (label, file) in files)
        {
            await RowsEqualDecodeIntoAsync(file, PixelFormat.Rgba32, label: label,
                feeds: [StreamingAssert.Feed.RandomChunks, StreamingAssert.Feed.AsyncOnly, StreamingAssert.Feed.Whole]);
            await RowsEqualDecodeIntoAsync(file, PixelFormat.Bgr24, label: label, feeds: [StreamingAssert.Feed.OneByte]);
            // Options a codec may not stream: the rows are still TryDecodeInto's.
            await RowsEqualDecodeIntoAsync(file, PixelFormat.Rgba32, new DecodeOptions { ScaleDenominator = 2 }, label: label + " 1/2",
                feeds: [StreamingAssert.Feed.RandomChunks]);
            await RowsEqualDecodeIntoAsync(file, PixelFormat.Gray8, new DecodeOptions { Region = new PixelRect(3, 2, 11, 9) },
                label: label + " region", feeds: [StreamingAssert.Feed.AsyncOnly]);
        }
    }

    [Fact]
    public async Task OpenRowReader_UnknownOrUndecodableInputThrowsInvalidData()
    {
        foreach (var file in new[] { "not an image at all"u8.ToArray(), [], [0x89] })
        {
            var e = Should.Throw<InvalidDataException>(() => ImageCodecs.OpenRowReader(new ChunkedStream(file), PixelFormat.Rgba32));
            e.Message.ShouldBe(ImageCodecs.NoCodecMessage(file));
            await Should.ThrowAsync<InvalidDataException>(async () =>
                await ImageCodecs.OpenRowReaderAsync(new ChunkedStream(file, asyncOnly: true), PixelFormat.Rgba32, cancellationToken: Ct));
        }

        // HEIF is recognised but not registered: the message says how to enable it.
        byte[] heif = [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8];
        Should.Throw<InvalidDataException>(() => ImageCodecs.OpenRowReader(new MemoryStream(heif), PixelFormat.Rgba32))
            .Message.ShouldContain("Sdcb.SimdCodecs.Heif");

        // An ftyp box longer than 64 bytes is sniffed whole: its 'avif' brand past byte 64 makes it AVIF, as for DetectFormat.
        var avif = new byte[100];
        avif[3] = 100;
        "ftypmif1"u8.CopyTo(avif.AsSpan(4));
        for (var at = 16; at < 96; at += 4)
            "abcd"u8.CopyTo(avif.AsSpan(at));
        "mif1"u8.CopyTo(avif.AsSpan(16));
        "avif"u8.CopyTo(avif.AsSpan(96));
        ImageCodecs.DetectFormat(avif).ShouldBe(ImageFormat.Avif);
        Should.Throw<InvalidDataException>(() => ImageCodecs.OpenRowReader(new ChunkedStream(avif), PixelFormat.Rgba32))
            .Message.ShouldBe(ImageCodecs.NoCodecMessage(avif));
        (await Should.ThrowAsync<InvalidDataException>(async () =>
                await ImageCodecs.OpenRowReaderAsync(new ChunkedStream(avif, asyncOnly: true), PixelFormat.Rgba32, cancellationToken: Ct)))
            .Message.ShouldContain("AVIF");

        // A truncated or corrupt file of a known format: the codec's reason, prefixed with the format.
        var png = ImageCodecs.Encode(Pattern(40, 30, 3, 2), 40, 30, PixelFormat.Rgb24, ImageFormat.Png, cancellationToken: TestContext.Current.CancellationToken);
        foreach (var cut in new[] { 8, 30, png.Length / 2, png.Length - 1 })
        {
            foreach (var feed in StreamingAssert.AllFeeds)
            {
                await Should.ThrowAsync<InvalidDataException>(async () => await ReadAllAsync(png[..cut], feed, PixelFormat.Rgba32),
                    $"cut at {cut}, {feed}");
            }
        }
        var corrupt = (byte[])png.Clone();
        corrupt[^20] ^= 0xFF;
        Should.Throw<InvalidDataException>(() => ImageCodecs.OpenRowReader(new MemoryStream(corrupt), PixelFormat.Rgba32))
            .Message.ShouldStartWith("The PNG file cannot be decoded");
    }

    [Fact]
    public void OpenRowReader_FloatImagesHaveNoIntegerRows()
    {
        var pfm = Sdcb.SimdCodecs.Pnm.PnmEncoder.EncodePfm([.. Pattern(8, 4, 3, 3).Select(b => b / 255f)], 8, 4, 3);
        Should.Throw<NotSupportedException>(() => ImageCodecs.Decode(pfm, PixelFormat.Rgba32));
        Should.Throw<NotSupportedException>(() => ImageCodecs.OpenRowReader(new MemoryStream(pfm), PixelFormat.Rgba32));
    }

    [Fact]
    public async Task OpenRowReader_DisposesTheStreamUnlessLeftOpen()
    {
        var file = ImageCodecs.Encode(Pattern(9, 7, 3, 4), 9, 7, PixelFormat.Rgb24, ImageFormat.Bmp, cancellationToken: TestContext.Current.CancellationToken);
        foreach (var leaveOpen in new[] { false, true })
        {
            var stream = new ChunkedStream(file);
            using (var reader = ImageCodecs.OpenRowReader(stream, PixelFormat.Rgba32, leaveOpen: leaveOpen, cancellationToken: TestContext.Current.CancellationToken))
                stream.WasDisposed.ShouldBeFalse();
            stream.WasDisposed.ShouldBe(!leaveOpen, $"leaveOpen {leaveOpen}");

            var asyncStream = new ChunkedStream(file, asyncOnly: true);
            await using (var reader = await ImageCodecs.OpenRowReaderAsync(asyncStream, PixelFormat.Rgba32, leaveOpen: leaveOpen, cancellationToken: Ct))
                asyncStream.WasDisposed.ShouldBeFalse();
            asyncStream.WasDisposed.ShouldBe(!leaveOpen);

            // When the open fails, the stream is disposed as the reader would have disposed it.
            var garbage = new ChunkedStream("garbage, not an image"u8.ToArray());
            Should.Throw<InvalidDataException>(() => ImageCodecs.OpenRowReader(garbage, PixelFormat.Rgba32, leaveOpen: leaveOpen));
            garbage.WasDisposed.ShouldBe(!leaveOpen);
        }

        Should.Throw<ArgumentNullException>(() => ImageCodecs.OpenRowReader(null!, PixelFormat.Rgba32));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await ImageCodecs.OpenRowReaderAsync(new ChunkedStream(file, asyncOnly: true), PixelFormat.Rgba32, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task CreateEncoderStream_WritesTheBufferWriterSessionsBytes()
    {
        const int w = 256, h = 200; // ICO's largest width; large enough that most formats write more than one 64 KiB chunk
        var rgb = Pattern(w, h, 3, 5);
        foreach (var format in EncoderFormats)
        {
            var expected = new ArrayBufferWriter<byte>();
            bool streaming;
            using (var session = ImageCodecs.CreateEncoder(format, expected))
            {
                streaming = session.IsStreaming;
                session.AddFrame(w, h, PixelFormat.Rgb24).WriteRows(rgb, h, 0, Ct);
                session.Commit(Ct);
            }

            // Synchronously, in bands.
            var sink = new WriteLog(asyncOnly: false);
            using (var session = ImageCodecs.CreateEncoder(format, sink, leaveOpen: true))
            {
                session.IsStreaming.ShouldBe(streaming);
                session.MaxFrames.ShouldBeGreaterThanOrEqualTo(1);
                var frame = session.AddFrame(w, h, PixelFormat.Rgb24);
                for (var y = 0; y < h; y += 17)
                    frame.WriteRows(rgb.AsSpan(y * w * 3), Math.Min(17, h - y), 0, Ct);
                frame.RowsWritten.ShouldBe(h);
                session.Commit(Ct);
            }
            sink.WasDisposed.ShouldBeFalse();
            sink.ToArray().ShouldBe(expected.WrittenSpan.ToArray(), $"{format} sync");
            sink.Flushes.ShouldBe(1);

            // Asynchronously only, into a stream whose synchronous members throw; disposing it disposes the stream.
            var asyncSink = new WriteLog(asyncOnly: true);
            await using (var session = ImageCodecs.CreateEncoder(format, asyncSink))
            {
                var frame = session.AddFrame(w, h, PixelFormat.Rgb24);
                for (var y = 0; y < h; y += 23)
                    await frame.WriteRowsAsync(rgb.AsMemory(y * w * 3), Math.Min(23, h - y), 0, Ct);
                await session.CommitAsync(Ct);
            }
            asyncSink.WasDisposed.ShouldBeTrue();
            asyncSink.ToArray().ShouldBe(expected.WrittenSpan.ToArray(), $"{format} async");
        }
    }

    [Fact]
    public async Task CreateEncoderStream_AStreamingEncodersBytesReachTheStreamBeforeCommit()
    {
        ImageCodecs.ExchangeEncoders([RawEncoderSession.Registration, .. _encoders]);
        const int w = 512, h = 256; // 512 KiB of RGBA: eight chunks
        var rgba = RawStreamCodec.RandomRgba(w, h, 6);
        var expected = RawStreamCodec.Encode(rgba, w, h);
        foreach (var async in new[] { false, true })
        {
            var sink = new WriteLog(asyncOnly: async);
            await using (var session = ImageCodecs.CreateEncoder(ImageFormat.Qoi, sink))
            {
                session.IsStreaming.ShouldBeTrue();
                var frame = session.AddFrame(w, h, PixelFormat.Rgba32);
                for (var y = 0; y < h; y += 16)
                {
                    if (async)
                        await frame.WriteRowsAsync(rgba.AsMemory(y * w * 4, 16 * w * 4), 16, 0, Ct);
                    else
                        frame.WriteRows(rgba.AsSpan(y * w * 4, 16 * w * 4), 16, 0, Ct);
                    // Never more than a chunk (and one band) held back.
                    (RawStreamCodec.HeaderLength + (y + 16) * w * 4 - sink.Length).ShouldBeLessThan(StreamBufferWriter.ChunkSize + 16 * w * 4);
                }
                sink.Length.ShouldBeGreaterThan(expected.Length / 2, "bytes reached the stream before Commit");
                if (async) await session.CommitAsync(Ct);
                else session.Commit(Ct);
            }
            sink.ToArray().ShouldBe(expected, async ? "async" : "sync");
            sink.Writes.ShouldBeGreaterThan(4);
        }
    }

    [Fact]
    public void CreateEncoderStream_ChecksItsArguments()
    {
        Should.Throw<ArgumentNullException>(() => ImageCodecs.CreateEncoder(ImageFormat.Png, (Stream)null!));
        Should.Throw<ArgumentException>(() => ImageCodecs.CreateEncoder(ImageFormat.Png, new MemoryStream([], writable: false)));
        Should.Throw<NotSupportedException>(() => ImageCodecs.CreateEncoder(ImageFormat.Avif, new MemoryStream()));
        var stream = new WriteLog(asyncOnly: false);
        Should.Throw<ArgumentException>(() => ImageCodecs.CreateEncoder(ImageFormat.Png, stream, new Sdcb.SimdCodecs.Jpeg.JpegEncodeOptions(), leaveOpen: true));
        stream.WasDisposed.ShouldBeFalse();
        var refused = new WriteLog(asyncOnly: false);
        Should.Throw<ArgumentException>(() => ImageCodecs.CreateEncoder(ImageFormat.Png, refused, new Sdcb.SimdCodecs.Jpeg.JpegEncodeOptions()));
        refused.WasDisposed.ShouldBeTrue("ownership passes with leaveOpen false, as for OpenRowReader");

        var session = ImageCodecs.CreateEncoder(ImageFormat.Png, stream);
        Should.Throw<InvalidOperationException>(() => session.Commit(Ct));
        var frame = session.AddFrame(2, 2, PixelFormat.Rgb24);
        Should.Throw<ArgumentException>(() => frame.WriteRows(new byte[5], 2, 0, Ct));
        session.Dispose();
        session.Dispose();
        stream.WasDisposed.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => frame.WriteRows(new byte[12], 2, 0, Ct));
        Should.Throw<ObjectDisposedException>(() => session.Commit(Ct));
    }

    [Fact]
    public void EncodeStream_WritesTheSameBytesInChunks()
    {
        const int w = 256, h = 256; // ICO's largest; files of several 64 KiB chunks
        var rgb = Pattern(w, h, 3, 8);
        foreach (var format in EncoderFormats)
        {
            var expected = ImageCodecs.Encode(rgb, w, h, PixelFormat.Rgb24, format, cancellationToken: TestContext.Current.CancellationToken);
            var sink = new WriteLog(asyncOnly: false);
            ImageCodecs.Encode(rgb, w, h, PixelFormat.Rgb24, format, sink, cancellationToken: TestContext.Current.CancellationToken);
            sink.ToArray().ShouldBe(expected, $"{format} raw pixels");
            sink.Flushes.ShouldBe(0, "Encode(Stream) does not flush the stream");

            using var image = ImageCodecs.Decode(expected, cancellationToken: TestContext.Current.CancellationToken);
            var fromImage = new ArrayBufferWriter<byte>();
            ImageCodecs.Encode(image, format, fromImage, cancellationToken: TestContext.Current.CancellationToken);
            var imageSink = new MemoryStream();
            ImageCodecs.Encode(image, format, imageSink, cancellationToken: TestContext.Current.CancellationToken);
            imageSink.ToArray().ShouldBe(fromImage.WrittenSpan.ToArray(), $"{format} image");
        }
    }

    [Fact]
    public async Task StreamToStream_AsyncOnlyEndToEnd()
    {
        var sources = new[]
        {
            ImageCodecs.Encode(Pattern(333, 211, 3, 9), 333, 211, PixelFormat.Rgb24, ImageFormat.Jpeg, cancellationToken: TestContext.Current.CancellationToken),
            ImageCodecs.Encode(Pattern(129, 64, 4, 10), 129, 64, PixelFormat.Rgba32, ImageFormat.Png, cancellationToken: TestContext.Current.CancellationToken),
            Fixture("webp", "astronaut-192-q20-normal.webp"),
        };
        foreach (var source in sources)
        {
            var output = new WriteLog(asyncOnly: true);
            await using (var reader = await ImageCodecs.OpenRowReaderAsync(new ChunkedStream(source, maxChunk: 997, asyncOnly: true),
                             PixelFormat.Rgba32, cancellationToken: Ct))
            await using (var session = ImageCodecs.CreateEncoder(ImageFormat.Png, output, leaveOpen: true))
            {
                var frame = session.AddFrame(reader.Width, reader.Height, PixelFormat.Rgba32);
                var band = new byte[reader.Width * 4 * 8];
                int n;
                while ((n = await reader.ReadRowsAsync(band, 8, 0, Ct)) > 0)
                    await frame.WriteRowsAsync(band.AsMemory(0, n * reader.Width * 4), n, 0, Ct);
                await session.CommitAsync(Ct);
            }

            ImageCodecs.TryReadInfo(source, out var info).ShouldBeTrue();
            var expected = new byte[info.Width * info.Height * 4];
            ImageCodecs.TryDecodeInto(source, expected, 0, PixelFormat.Rgba32, null, Ct).ShouldBeTrue();
            var actual = new byte[expected.Length];
            ImageCodecs.TryDecodeInto(output.ToArray(), actual, 0, PixelFormat.Rgba32, null, Ct).ShouldBeTrue();
            actual.ShouldBe(expected);
        }
    }

    /// <summary>A write-only stream that records what it is given; with <c>asyncOnly</c> its synchronous members throw, as
    /// an ASP.NET Core response body's do by default.</summary>
    private sealed class WriteLog(bool asyncOnly) : Stream
    {
        private readonly MemoryStream _data = new();

        public int Writes { get; private set; }
        public int Flushes { get; private set; }
        public bool WasDisposed { get; private set; }

        public byte[] ToArray() => _data.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !WasDisposed;
        public override long Length => _data.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush()
        {
            if (asyncOnly) throw new InvalidOperationException("Synchronous operations are disallowed.");
            Flushes++;
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            Flushes++;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (asyncOnly) throw new InvalidOperationException("Synchronous operations are disallowed.");
            Append(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Append(buffer.Span);
        }

        private void Append(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(WasDisposed, this);
            Writes++;
            _data.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The <see cref="RawStreamCodec"/> format as a streaming encoder: header at the frame, rows as they come.</summary>
    private sealed class RawEncoderSession(IBufferWriter<byte> output) : StreamingEncoderSession(output)
    {
        public static ImageEncoderRegistration Registration { get; } = new()
        {
            Format = ImageFormat.Qoi,
            GetClosestInputFormat = _ => PixelFormat.Rgba32,
            Create = (output, _) => new RawEncoderSession(output),
        };

        private int _width;

        public override int MaxFrames => 1;

        protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
            ImageMetadata? metadata, TimeSpan? duration)
        {
            _width = width;
            WriteOutput(RawStreamCodec.Encode([], width, height).AsSpan(0, RawStreamCodec.HeaderLength));
        }

        protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
        {
            for (var y = 0; y < rowCount; y++)
                WriteOutput(rows.Slice(y * stride, _width * 4));
        }

        protected override void Finish(CancellationToken cancellationToken) => WriteOutput("END!"u8);
    }
}
