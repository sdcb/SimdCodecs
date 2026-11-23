using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>
/// <see cref="ImageDecoder.Open(Stream, bool, bool)"/> and <see cref="ImageDecoder.OpenAsync(Stream, bool, bool, CancellationToken)"/>
/// over every kind of stream: frame 0's first full-size decode streams through the codec's row reader (the fake
/// <see cref="RawStreamCodec"/>; real codecs take the whole-file path until they have one), and every operation
/// sequence gives exactly what <see cref="ImageDecoder.Open(ReadOnlyMemory{byte})"/> over the same bytes gives.
/// </summary>
[Collection(FacadeRegistryCollection.Name)]
public sealed class ImageDecoderStreamingTests : IDisposable
{
    private const int W = 37, H = 23;

    private readonly ImageDecoderRegistration[] _builtIn = ImageCodecs.RegistrySnapshot;

    public ImageDecoderStreamingTests() => ImageCodecs.ExchangeRegistry([RawStreamCodec.Registration, .. _builtIn]);

    public void Dispose() => ImageCodecs.ExchangeRegistry(_builtIn);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly byte[] Raw = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(W, H, 7), W, H);
    private static readonly byte[] Big = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(256, 64, 11), 256, 64);
    private static readonly byte[] RawFallback = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(W, H, 8), W, H, fallback: true);

    public enum Source { OneByte, Chunks, Seekable, File }

    private string? _file;

    private Stream Make(Source source, byte[] data, int seed = 1) => source switch
    {
        Source.OneByte => new ChunkedStream(data, maxChunk: 1, seed),
        Source.Chunks => new ChunkedStream(data, maxChunk: 4096, seed),
        Source.Seekable => new MemoryStream(data, writable: false),
        _ => new FileStream(_file = WriteTemp(data), FileMode.Open, FileAccess.Read, FileShare.Read),
    };

    private static string WriteTemp(byte[] data)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "streaming-tmp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, data);
        return path;
    }

    // ---- what a session over memory gives ------------------------------------------------------------------------

    private static void ShouldBeSame(IDecodedImage actual, IDecodedImage expected)
    {
        (actual.Width, actual.Height, actual.Channels, actual.SampleFormat, actual.BitDepth)
            .ShouldBe((expected.Width, expected.Height, expected.Channels, expected.SampleFormat, expected.BitDepth));
        actual.ColorEncoding.ShouldBe(expected.ColorEncoding);
        actual.Metadata.Exif.ToArray().ShouldBe(expected.Metadata.Exif.ToArray());
        actual.Metadata.Xmp.ToArray().ShouldBe(expected.Metadata.Xmp.ToArray());
        actual.Metadata.Icc.ToArray().ShouldBe(expected.Metadata.Icc.ToArray());
        actual.Pixels.SequenceEqual(expected.Pixels).ShouldBeTrue("pixels");
    }

    // Every operation a caller makes, in an order that has the first decode stream and the rest revisit.
    private static List<object> Sequence(ImageDecoder decoder, PixelFormat first)
    {
        var results = new List<object> { decoder.Format, decoder.FrameCount };
        var frame = decoder.GetFrame(0);
        results.Add(frame.Info);
        var w = frame.Info.Width;
        var h = frame.Info.Height;
        var pixels = new byte[w * h * first.BytesPerPixel];
        frame.CopyPixels(null, first, pixels, 0, cancellationToken: Token);
        results.Add(pixels);
        using (var image = frame.Decode(cancellationToken: Token))
            results.Add(image.Pixels.ToArray());
        using (var image = frame.Decode(cancellationToken: Token))
            results.Add(image.Pixels.ToArray());
        // The fake codec decodes no regions: then the failure is what must match.
        results.Add(Outcome(() =>
        {
            var region = new byte[5 * 4 * 3];
            frame.CopyPixels(new PixelRect(3, 2, 5, 4), PixelFormat.Bgr24, region, 0, cancellationToken: Token);
            return region;
        }));
        results.Add(decoder.Metadata.IsEmpty);
        results.Add(decoder.LoopCount ?? -1);
        results.Add(frame.TryGetThumbnail(out var thumbnail));
        thumbnail?.Dispose();
        results.Add(frame.TryGetClosestSize(w / 2, h / 2, out var cw, out var ch, out var scale));
        results.Add((cw, ch, scale));
        return results;
    }

    private static object Outcome(Func<object> step)
    {
        try
        {
            return step();
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static void ShouldMatchMemory(ImageDecoder decoder, byte[] data, PixelFormat first)
    {
        using var expected = ImageDecoder.Open(data.AsMemory());
        var actual = Sequence(decoder, first);
        var wanted = Sequence(expected, first);
        actual.Count.ShouldBe(wanted.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            if (wanted[i] is byte[] bytes)
                ((byte[])actual[i]).ShouldBe(bytes, $"step {i}");
            else
                actual[i].ShouldBe(wanted[i], $"step {i}");
        }
    }

    public static TheoryData<Source, bool> Sources()
    {
        var data = new TheoryData<Source, bool>();
        foreach (var source in Enum.GetValues<Source>())
        {
            data.Add(source, false);
            data.Add(source, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void EveryOperationEqualsMemory_Streaming(Source source, bool fallbackVariant)
    {
        var data = fallbackVariant ? RawFallback : Raw;
        foreach (var first in new[] { PixelFormat.Rgba32, PixelFormat.Gray8, PixelFormat.Bgra32Premultiplied, PixelFormat.Rgba64 })
        {
            using var decoder = ImageDecoder.Open(Make(source, data));
            ShouldMatchMemory(decoder, data, first);
        }
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void DecodeFirstEqualsMemory(Source source, bool fallbackVariant)
    {
        var data = fallbackVariant ? RawFallback : Raw;
        using var expected = ImageDecoder.Open(data.AsMemory());
        using var wanted = expected.GetFrame(0).Decode(cancellationToken: Token);
        using var decoder = ImageDecoder.Open(Make(source, data));
        using (var image = decoder.GetFrame(0).Decode(cancellationToken: Token))
            ShouldBeSame(image, wanted);
        using (var again = decoder.GetFrame(0).Decode(cancellationToken: Token))
            ShouldBeSame(again, wanted);
    }

    [Fact]
    public void APooledDecodeReturnsItsPixels()
    {
        var pool = new CountingPool();
        using var decoder = ImageDecoder.Open(new ChunkedStream(Raw, maxChunk: 300));
        var image = decoder.GetFrame(0).Decode(new DecodeOptions { Pool = pool }, Token);
        using var expected = ImageDecoder.Open(Raw.AsMemory());
        using (var wanted = expected.GetFrame(0).Decode(cancellationToken: Token))
            ShouldBeSame(image, wanted);
        pool.Rented.ShouldBe(1);
        image.Dispose();
        pool.Returned.ShouldBe(1);
    }

    private sealed class CountingPool : System.Buffers.ArrayPool<byte>
    {
        public int Rented, Returned;
        public override byte[] Rent(int minimumLength) { Rented++; return new byte[minimumLength]; }
        public override void Return(byte[] array, bool clearArray = false) => Returned++;
    }

    [Fact]
    public void ANonStreamingCodecIsReadWholeAtOpen()
    {
        ImageCodecs.ExchangeRegistry([RawStreamCodec.NonStreamingRegistration, .. _builtIn]);
        var stream = new ChunkedStream(Raw, maxChunk: 64);
        using var decoder = ImageDecoder.Open(stream, leaveOpen: true, forwardOnly: true);
        stream.BytesRead.ShouldBe(Raw.Length);
        ShouldMatchMemory(decoder, Raw, PixelFormat.Rgba32);
    }

    [Fact]
    public void OpenReadsOnlyTheHeader()
    {
        var large = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(512, 512, 3), 512, 512);
        var stream = new ChunkedStream(large, maxChunk: 1000);
        using var decoder = ImageDecoder.Open(stream, leaveOpen: true);
        stream.BytesRead.ShouldBeLessThan(64 * 1024);
        decoder.GetFrame(0).Info.Width.ShouldBe(512);
        stream.BytesRead.ShouldBeLessThan(64 * 1024);
    }

    // ---- real codecs: the whole-file path behind the same API ---------------------------------------------------

    private static byte[] Rgb(int seed)
    {
        var pixels = new byte[W * H * 3];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    public static TheoryData<string> RealFiles() => ["png", "jpeg", "jpeg-gray"];

    private static byte[] Real(string name) => name switch
    {
        "png" => Sdcb.SimdCodecs.Png.PngWriter.Encode(RawStreamCodec.RandomRgba(W, H, 4), W, H),
        "jpeg" => JpegEncoder.Encode(Rgb(5), W, H, 3, new JpegEncodeOptions { Quality = 90 }),
        _ => JpegEncoder.Encode(Rgb(6)[..(W * H)], W, H, 1, new JpegEncodeOptions { Quality = 90 }),
    };

    [Theory]
    [MemberData(nameof(RealFiles))]
    public void RealCodecsEqualMemory(string name)
    {
        var data = Real(name);
        foreach (var source in Enum.GetValues<Source>())
        {
            foreach (var forwardOnly in new[] { false, true })
            {
                using var decoder = ImageDecoder.Open(Make(source, data), leaveOpen: false, forwardOnly);
                ShouldMatchMemory(decoder, data, PixelFormat.Rgba32);
            }
        }
    }

    /// <summary>
    /// The layout a streamed <c>Decode</c> asks the row reader for, <see cref="PixelFormat.ForChannels"/> of the header's
    /// channels and sample format, is the one <see cref="IImageDecoder.TryDecode"/> returns, and TryDecodeInto in it
    /// writes TryDecode's pixels; the header's bit depth and colour encoding are the image's.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealFiles))]
    public void TheNativeLayoutIsTryDecodes(string name)
    {
        var data = Real(name);
        var codec = ImageCodecs.Find(data)!;
        codec.TryReadInfo(data, out var info).ShouldBeTrue();
        codec.TryDecode(data, out var image, null, Token).ShouldBeTrue();
        using (image)
        {
            var format = PixelFormat.ForChannels(info.Channels, info.SampleFormat);
            (image!.Channels, image.SampleFormat, image.BitDepth).ShouldBe((info.Channels, info.SampleFormat, info.BitDepth));
            image.ColorEncoding.ShouldBe(info.ColorEncoding);
            var pixels = new byte[info.Width * info.Height * format.BytesPerPixel];
            codec.TryDecodeInto(data, pixels, 0, format, null, Token).ShouldBeTrue();
            pixels.ShouldBe(image.Pixels.ToArray());
            if (!codec.TryReadMetadata(data, out var metadata)) metadata = ImageMetadata.Empty;
            image.Metadata.Exif.ToArray().ShouldBe(metadata!.Exif.ToArray());
            image.Metadata.Icc.ToArray().ShouldBe(metadata.Icc.ToArray());
            image.Metadata.Xmp.ToArray().ShouldBe(metadata.Xmp.ToArray());
        }
    }

    // ---- forward-only --------------------------------------------------------------------------------------------

    [Fact]
    public void ForwardOnlyRefusesToRevisitAfterItsStreamedDecode()
    {
        using var expected = ImageDecoder.Open(Raw.AsMemory());
        using var wanted = expected.GetFrame(0).Decode(cancellationToken: Token);
        using var decoder = ImageDecoder.Open(new ChunkedStream(Raw, maxChunk: 100), leaveOpen: false, forwardOnly: true);
        var frame = decoder.GetFrame(0);
        using (var image = frame.Decode(cancellationToken: Token))
            ShouldBeSame(image, wanted);
        Should.Throw<InvalidOperationException>(() => frame.Decode(cancellationToken: Token));
        Should.Throw<InvalidOperationException>(() => _ = decoder.Metadata);
        Should.Throw<InvalidOperationException>(() => frame.CopyPixels(null, PixelFormat.Rgba32, new byte[W * H * 4], 0, cancellationToken: Token));
        decoder.GetFrame(0).Info.ShouldBe(frame.Info); // the header is still known
    }

    [Fact]
    public void ForwardOnlyReadsTheWholeFileForAnOperationBeforeItsDecode()
    {
        using var decoder = ImageDecoder.Open(new ChunkedStream(Raw, maxChunk: 100), leaveOpen: false, forwardOnly: true);
        decoder.Metadata.IsEmpty.ShouldBeTrue();
        ShouldMatchMemory(decoder, Raw, PixelFormat.Bgr24);
    }

    [Fact]
    public void ForwardOnlyOverASeekableStreamStillRevisits()
    {
        using var decoder = ImageDecoder.Open(new MemoryStream(Raw), leaveOpen: false, forwardOnly: true);
        ShouldMatchMemory(decoder, Raw, PixelFormat.Rgba32);
    }

    [Fact]
    public async Task ForwardOnlyAsyncStreamsWithoutASyncRead()
    {
        using var expected = ImageDecoder.Open(Raw.AsMemory());
        using var wanted = expected.GetFrame(0).Decode(cancellationToken: Token);
        using (var decoder = await ImageDecoder.OpenAsync(new ChunkedStream(Raw, 97, asyncOnly: true), false, true, Token))
        {
            using var image = await decoder.GetFrame(0).DecodeAsync(cancellationToken: Token);
            ShouldBeSame(image, wanted);
            await Should.ThrowAsync<InvalidOperationException>(async () => await decoder.GetFrame(0).DecodeAsync(cancellationToken: Token));
        }
        // The synchronous call blocks on asynchronous reads.
        using (var decoder = await ImageDecoder.OpenAsync(new ChunkedStream(Raw, 97, asyncOnly: true), false, true, Token))
        using (var image = decoder.GetFrame(0).Decode(cancellationToken: Token))
            ShouldBeSame(image, wanted);
    }

    // ---- memory ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AStreamedCopyDoesNotHoldTheFile()
    {
        const int size = 1024;
        var large = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(size, size, 9), size, size);
        var destination = new byte[size * size * 4];
        var expected = new byte[destination.Length];
        using (var memory = ImageDecoder.Open(large.AsMemory()))
            memory.GetFrame(0).CopyPixels(null, PixelFormat.Bgra32, expected, 0, cancellationToken: Token);

        var seekable = new MemoryStream(large, writable: false);
        StreamingAssert.AllocatesLessThan(large.Length / 8, () =>
        {
            seekable.Position = 0;
            using var decoder = ImageDecoder.Open(seekable, leaveOpen: true);
            decoder.GetFrame(0).CopyPixels(null, PixelFormat.Bgra32, destination, 0, cancellationToken: Token);
        }, "seekable");
        destination.ShouldBe(expected);

        Array.Clear(destination);
        StreamingAssert.AllocatesLessThan(large.Length / 8, () =>
        {
            using var decoder = ImageDecoder.Open(new ChunkedStream(large, maxChunk: 65536), leaveOpen: false, forwardOnly: true);
            decoder.GetFrame(0).CopyPixels(null, PixelFormat.Bgra32, destination, 0, cancellationToken: Token);
        }, "forward-only");
        destination.ShouldBe(expected);
    }

    // ---- OpenAsync -------------------------------------------------------------------------------------------------

    /// <summary>An ASP.NET request body: async reads only. Both the new async decode and the legacy one give the
    /// memory session's image, and neither reads the stream synchronously (it would throw).</summary>
    [Fact]
    public async Task ARequestBodyDecodesAsyncAndLegacy()
    {
        using var expected = ImageDecoder.Open(Raw.AsMemory());
        using var wanted = expected.GetFrame(0).Decode(cancellationToken: Token);

        using (var decoder = await ImageDecoder.OpenAsync(new ChunkedStream(Raw, 997, asyncOnly: true), cancellationToken: Token))
        using (var image = await decoder.GetFrame(0).DecodeAsync(cancellationToken: Token))
            ShouldBeSame(image, wanted);

        using (var decoder = await ImageDecoder.OpenAsync(new ChunkedStream(Raw, 997, asyncOnly: true), cancellationToken: Token))
        using (var image = decoder.GetFrame(0).Decode(cancellationToken: Token))
            ShouldBeSame(image, wanted);

        using (var decoder = await ImageDecoder.OpenAsync(new ChunkedStream(Raw, 997, asyncOnly: true), cancellationToken: Token))
            ShouldMatchMemory(decoder, Raw, PixelFormat.Rgb24);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncSequencesEqualMemory(bool fallbackVariant)
    {
        var data = fallbackVariant ? RawFallback : Raw;
        using var expected = ImageDecoder.Open(data.AsMemory());
        var wanted = new byte[W * H * 2];
        expected.GetFrame(0).CopyPixels(null, PixelFormat.GrayAlpha16, wanted, 0, cancellationToken: Token);
        using var wantedImage = expected.GetFrame(0).Decode(cancellationToken: Token);
        foreach (var source in Enum.GetValues<Source>())
        {
            var stream = source is Source.OneByte or Source.Chunks
                ? new ChunkedStream(data, source == Source.OneByte ? 1 : 4096, asyncOnly: true)
                : Make(source, data);
            using var decoder = await ImageDecoder.OpenAsync(stream, cancellationToken: Token);
            var frame = decoder.GetFrame(0);
            var pixels = new byte[wanted.Length];
            await frame.CopyPixelsAsync(null, PixelFormat.GrayAlpha16, pixels, 0, cancellationToken: Token);
            pixels.ShouldBe(wanted, source.ToString());
            using (var image = await frame.DecodeAsync(cancellationToken: Token))
                ShouldBeSame(image, wantedImage);
            var region = new byte[4 * 4 * 4];
            var message = (await Should.ThrowAsync<InvalidDataException>(async () =>
                await frame.CopyPixelsAsync(new PixelRect(1, 1, 4, 4), PixelFormat.Rgba32, region, 0, cancellationToken: Token))).Message;
            message.ShouldBe(Should.Throw<InvalidDataException>(() =>
                expected.GetFrame(0).CopyPixels(new PixelRect(1, 1, 4, 4), PixelFormat.Rgba32, region, 0, cancellationToken: Token)).Message);
            using (var again = await frame.DecodeAsync(cancellationToken: Token))
                ShouldBeSame(again, wantedImage);
        }
    }

    [Fact]
    public async Task ThePrefetchRacesAStreamedDecode()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var data = RawStreamCodec.Encode(RawStreamCodec.RandomRgba(W, H, seed), W, H);
            using var expected = ImageDecoder.Open(data.AsMemory());
            using var wanted = expected.GetFrame(0).Decode(cancellationToken: Token);
            using var decoder = await ImageDecoder.OpenAsync(new ChunkedStream(data, 1 + seed * 13, seed, asyncOnly: true), cancellationToken: Token);
            if (seed % 2 == 0)
                await Task.Delay(seed % 4, Token);
            using (var image = await decoder.GetFrame(0).DecodeAsync(cancellationToken: Token))
                ShouldBeSame(image, wanted);
            using (var again = await decoder.GetFrame(0).DecodeAsync(cancellationToken: Token))
                ShouldBeSame(again, wanted);
            decoder.Metadata.IsEmpty.ShouldBeTrue();
        }
    }

    /// <summary>Serves the header, then blocks every read until it is cancelled or the stream is disposed.</summary>
    private sealed class StallingStream(byte[] data, int serve) : Stream
    {
        private int _position;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasDisposed => _disposed.Task.IsCompleted;
        private int _stalled;
        public int Stalled => Volatile.Read(ref _stalled);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < serve)
            {
                var n = Math.Min(buffer.Length, serve - _position);
                data.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            Interlocked.Increment(ref _stalled);
            await _disposed.Task.WaitAsync(cancellationToken);
            throw new ObjectDisposedException(nameof(StallingStream));
        }

        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingDuringThePrefetchStopsIt(bool leaveOpen)
    {
        var stream = new StallingStream(Big, 8192);
        var decoder = await ImageDecoder.OpenAsync(stream, leaveOpen, Token);
        decoder.GetFrame(0).Info.Width.ShouldBe(256);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (stream.Stalled == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(1, Token);
        stream.Stalled.ShouldBeGreaterThan(0, "the prefetch reads on in the background");
        decoder.Dispose();
        stream.WasDisposed.ShouldBe(!leaveOpen);
        Should.Throw<ObjectDisposedException>(() => decoder.GetFrame(0));
    }

    [Fact]
    public async Task ADecodeWaitingOnAStalledStreamIsCancelled()
    {
        var stream = new StallingStream(Big, 8192);
        using var decoder = await ImageDecoder.OpenAsync(stream, leaveOpen: false, forwardOnly: true, Token);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(async () => await decoder.GetFrame(0).DecodeAsync(cancellationToken: cancel.Token));

        using var retained = await ImageDecoder.OpenAsync(new StallingStream(Big, 8192), cancellationToken: Token);
        using var cancel2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await retained.GetFrame(0).CopyPixelsAsync(null, PixelFormat.Rgba32, new byte[256 * 64 * 4], 0, cancellationToken: cancel2.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await ImageDecoder.OpenAsync(new ChunkedStream(Raw, asyncOnly: true), false, false, new CancellationToken(canceled: true)));

        // A synchronous call on an async-opened session blocks on asynchronous reads, and its token still cancels them.
        foreach (var forwardOnly in new[] { false, true })
        {
            using var session = await ImageDecoder.OpenAsync(new StallingStream(Big, 8192), false, forwardOnly, Token);
            using var cancel3 = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            Should.Throw<OperationCanceledException>(() => session.GetFrame(0).Decode(cancellationToken: cancel3.Token));
        }
    }

    // ---- bad data and ownership ------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task ATruncatedStreamThrowsInvalidDataException(Source source, bool forwardOnly)
    {
        foreach (var cut in new[] { 3, RawStreamCodec.HeaderLength - 1, RawStreamCodec.HeaderLength + W * 4 * 5 + 3, Raw.Length - 2 })
        {
            var data = Raw[..cut];
            Should.Throw<InvalidDataException>(() =>
            {
                using var decoder = ImageDecoder.Open(Make(source, data), leaveOpen: false, forwardOnly);
                decoder.GetFrame(0).CopyPixels(null, PixelFormat.Rgba32, new byte[W * H * 4], 0, cancellationToken: Token);
            }, $"{cut}");
            Should.Throw<InvalidDataException>(() =>
            {
                using var decoder = ImageDecoder.Open(Make(source, data), leaveOpen: false, forwardOnly);
                using var image = decoder.GetFrame(0).Decode(cancellationToken: Token);
            }, $"{cut}");
            await Should.ThrowAsync<InvalidDataException>(async () =>
            {
                var stream = source is Source.OneByte or Source.Chunks ? new ChunkedStream(data, 50, asyncOnly: true) : Make(source, data);
                using var decoder = await ImageDecoder.OpenAsync(stream, false, forwardOnly, Token);
                using var image = await decoder.GetFrame(0).DecodeAsync(cancellationToken: Token);
            }, $"{cut}");
        }
    }

    [Fact]
    public void ACorruptStreamFailsAsFromMemory()
    {
        var corrupt = (byte[])Raw.Clone();
        corrupt[^1] ^= 0xFF; // the trailer
        var fromMemory = Should.Throw<InvalidDataException>(() =>
        {
            using var decoder = ImageDecoder.Open(corrupt.AsMemory());
            using var image = decoder.GetFrame(0).Decode(cancellationToken: Token);
        });
        var fromStream = Should.Throw<InvalidDataException>(() =>
        {
            using var decoder = ImageDecoder.Open(new ChunkedStream(corrupt, 40));
            using var image = decoder.GetFrame(0).Decode(cancellationToken: Token);
        });
        fromStream.Message.ShouldBe(fromMemory.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LeaveOpenDecidesWhoClosesTheStream(bool leaveOpen, bool streaming)
    {
        if (!streaming)
            ImageCodecs.ExchangeRegistry([RawStreamCodec.NonStreamingRegistration, .. _builtIn]);
        var stream = new ChunkedStream(Raw, 500);
        using (var decoder = ImageDecoder.Open(stream, leaveOpen))
        {
            using var image = decoder.GetFrame(0).Decode(cancellationToken: Token);
            stream.WasDisposed.ShouldBeFalse();
        }
        stream.WasDisposed.ShouldBe(!leaveOpen);

        var asyncStream = new ChunkedStream(Raw, 500, asyncOnly: true);
        using (var decoder = await ImageDecoder.OpenAsync(asyncStream, leaveOpen, Token))
        {
            using var image = await decoder.GetFrame(0).DecodeAsync(cancellationToken: Token);
        }
        asyncStream.WasDisposed.ShouldBe(!leaveOpen);

        var failed = new ChunkedStream("SRAW"u8.ToArray(), 2);
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(failed, leaveOpen));
        failed.WasDisposed.ShouldBe(!leaveOpen);
    }
}
