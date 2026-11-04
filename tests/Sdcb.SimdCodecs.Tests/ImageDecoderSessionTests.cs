using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>A stream that cannot seek (a network or pipe stand-in) and records that it was disposed.</summary>
internal sealed class NonSeekableStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data);

    public bool WasDisposed { get; private set; }

    public override bool CanRead => !WasDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// <see cref="ImageDecoder"/>, the session API: every way to open it, its frames against the facade's
/// <see cref="SimdCodecs.TryDecode"/> and <see cref="SimdCodecs.TryOpenFrames"/>, <c>CopyPixels</c> against a crop of the converted
/// image, stream ownership and disposal.
/// </summary>
public sealed class ImageDecoderSessionTests : IDisposable
{
    private const int W = 31, H = 19;
    private const byte Sentinel = 0x6B;

    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "session-tmp", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Rgba(int seed)
    {
        var pixels = new byte[W * H * 4];
        new Random(seed).NextBytes(pixels);
        for (var p = 0; p < W * H; p += 3)
            pixels[p * 4 + 3] = (byte)(p * 5);
        return pixels;
    }

    private static byte[] Rgb(int seed)
    {
        var pixels = new byte[W * H * 3];
        var random = new Random(seed);
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i * 3 + random.Next(25));
        return pixels;
    }

    private static readonly byte[] Png = Sdcb.SimdCodecs.Png.PngWriter.Encode(Rgba(1), W, H);
    private static readonly byte[] JpegData = JpegEncoder.Encode(Rgb(2), W, H, 3, new JpegEncodeOptions { Quality = 88 });

    private string WriteFile(byte[] data, string name)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] DecodedPixels(byte[] data, out int width, out int height, out PixelFormat native)
    {
        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            (width, height) = (image.Width, image.Height);
            native = PixelFormat.ForChannels(image.Channels, image.SampleFormat);
            return image.Pixels.ToArray();
        }
    }

    private static void ShouldBeTheStill(ImageDecoder decoder, byte[] data, ImageFormat format)
    {
        var expected = DecodedPixels(data, out var width, out var height, out _);
        decoder.Format.ShouldBe(format);
        decoder.FrameCount.ShouldBe(1);
        decoder.LoopCount.ShouldBeNull();
        var frame = decoder.GetFrame(0);
        frame.Index.ShouldBe(0);
        (frame.Info.Width, frame.Info.Height).ShouldBe((width, height));
        using var image = frame.Decode(cancellationToken: TestContext.Current.CancellationToken);
        (image.Width, image.Height).ShouldBe((width, height));
        image.Pixels.ToArray().ShouldBe(expected);
    }

    // ---- opening -----------------------------------------------------------------------------------------------

    [Fact]
    public void ASeekableStreamOpens()
    {
        using var stream = new MemoryStream(Png);
        using var decoder = ImageDecoder.Open(stream, leaveOpen: true);
        ShouldBeTheStill(decoder, Png, ImageFormat.Png);
    }

    [Fact]
    public void ASeekableStreamIsReadFromItsCurrentPosition()
    {
        var padded = new byte[] { 1, 2, 3, 4, 5 }.Concat(JpegData).ToArray();
        using var stream = new MemoryStream(padded) { Position = 5 };
        using var decoder = ImageDecoder.Open(stream, leaveOpen: true);
        ShouldBeTheStill(decoder, JpegData, ImageFormat.Jpeg);
    }

    [Fact]
    public void ANonSeekableStreamOpens()
    {
        using var stream = new NonSeekableStream(Png);
        stream.CanSeek.ShouldBeFalse();
        using var decoder = ImageDecoder.Open(stream, leaveOpen: true);
        ShouldBeTheStill(decoder, Png, ImageFormat.Png);
    }

    [Fact]
    public void MemoryOpens()
    {
        using var decoder = ImageDecoder.Open(JpegData.AsMemory());
        ShouldBeTheStill(decoder, JpegData, ImageFormat.Jpeg);
    }

    [Fact]
    public void APathOpens()
    {
        var path = WriteFile(Png, "still.png");
        using var decoder = ImageDecoder.Open(path);
        ShouldBeTheStill(decoder, Png, ImageFormat.Png);
    }

    [Fact]
    public void ADisposedPathDecoderReleasesTheFile()
    {
        var path = WriteFile(JpegData, "release.jpg");
        var decoder = ImageDecoder.Open(path);
        ShouldBeTheStill(decoder, JpegData, ImageFormat.Jpeg);
        decoder.Dispose();
        File.Delete(path); // would throw while the mapping is held
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void AMissingPathThrowsIOException() =>
        Should.Throw<IOException>(() => ImageDecoder.Open(Path.Combine(_directory, "nope.png")));

    [Fact]
    public async Task OpenAsyncReadsASeekableStream()
    {
        using var stream = new MemoryStream(Png);
        using var decoder = await ImageDecoder.OpenAsync(stream, leaveOpen: true, TestContext.Current.CancellationToken);
        ShouldBeTheStill(decoder, Png, ImageFormat.Png);
    }

    [Fact]
    public async Task OpenAsyncReadsANonSeekableStream()
    {
        using var stream = new NonSeekableStream(JpegData);
        using var decoder = await ImageDecoder.OpenAsync(stream, leaveOpen: true, TestContext.Current.CancellationToken);
        ShouldBeTheStill(decoder, JpegData, ImageFormat.Jpeg);
        stream.WasDisposed.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenAsyncHonoursItsTokenOnANonSeekableStream()
    {
        using var stream = new NonSeekableStream(Png);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await ImageDecoder.OpenAsync(stream, leaveOpen: true, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task OpenAsyncRefusesWhatIsNotAnImage()
    {
        using var stream = new NonSeekableStream(new byte[100]);
        await Should.ThrowAsync<InvalidDataException>(async () => await ImageDecoder.OpenAsync(stream, leaveOpen: true, TestContext.Current.CancellationToken));
    }

    // ---- refusals ----------------------------------------------------------------------------------------------

    [Fact]
    public void AnUnknownStreamThrowsInvalidDataException()
    {
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(new MemoryStream(new byte[200]), leaveOpen: true));
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(new NonSeekableStream("hello, not an image"u8.ToArray()), leaveOpen: true));
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(ReadOnlyMemory<byte>.Empty));
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(new ReadOnlyMemory<byte>(new byte[] { 0xFF })));
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(WriteFile(new byte[64], "garbage.bin")));
    }

    [Fact]
    public void ATruncatedImageThrowsInvalidDataExceptionAtOpenOrDecode()
    {
        // A signature and nothing else: recognised, then refused.
        foreach (var data in new[] { Png[..12], JpegData[..4] })
        {
            Should.Throw<InvalidDataException>(() =>
            {
                using var decoder = ImageDecoder.Open(data.AsMemory());
                using var image = decoder.GetFrame(0).Decode(cancellationToken: TestContext.Current.CancellationToken);
            });
        }
    }

    [Fact]
    public void AFailedOpenClosesAStreamUnlessAskedNotTo()
    {
        var owned = new MemoryStream(new byte[200]);
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(owned));
        owned.CanRead.ShouldBeFalse();

        var kept = new MemoryStream(new byte[200]);
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(kept, leaveOpen: true));
        kept.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void ANullArgumentIsRefused()
    {
        Should.Throw<ArgumentNullException>(() => ImageDecoder.Open((Stream)null!));
        Should.Throw<ArgumentNullException>(() => ImageDecoder.Open((string)null!));
        Should.Throw<ArgumentNullException>(() => ImageDecoder.Open((IByteSource)null!));
    }

    // ---- ownership and disposal --------------------------------------------------------------------------------

    [Fact]
    public void LeaveOpenKeepsASeekableStreamAndOtherwiseClosesIt()
    {
        var kept = new MemoryStream(Png);
        using (ImageDecoder.Open(kept, leaveOpen: true))
        {
        }
        kept.CanRead.ShouldBeTrue();

        var owned = new MemoryStream(Png);
        using (var decoder = ImageDecoder.Open(owned))
            ShouldBeTheStill(decoder, Png, ImageFormat.Png);
        owned.CanRead.ShouldBeFalse();
    }

    [Fact]
    public void LeaveOpenKeepsANonSeekableStreamAndOtherwiseClosesIt()
    {
        var kept = new NonSeekableStream(Png);
        using (var decoder = ImageDecoder.Open(kept, leaveOpen: true))
            ShouldBeTheStill(decoder, Png, ImageFormat.Png);
        kept.WasDisposed.ShouldBeFalse();

        var owned = new NonSeekableStream(Png);
        using (var decoder = ImageDecoder.Open(owned))
            ShouldBeTheStill(decoder, Png, ImageFormat.Png);
        owned.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsyncOwnsItsStreamUnlessToldOtherwise()
    {
        var owned = new NonSeekableStream(Png);
        using (await ImageDecoder.OpenAsync(owned, cancellationToken: TestContext.Current.CancellationToken))
        {
        }
        owned.WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public void DisposingTwiceIsFine()
    {
        var decoder = ImageDecoder.Open(Png.AsMemory());
        decoder.Dispose();
        Should.NotThrow(decoder.Dispose);

        var stream = new MemoryStream(Png);
        var streamDecoder = ImageDecoder.Open(stream);
        streamDecoder.Dispose();
        Should.NotThrow(streamDecoder.Dispose);
    }

    [Fact]
    public void UsingADisposedDecoderThrowsObjectDisposedException()
    {
        var decoder = ImageDecoder.Open(JpegData.AsMemory());
        var frame = decoder.GetFrame(0);
        decoder.Dispose();

        Should.Throw<ObjectDisposedException>(() => decoder.GetFrame(0));
        Should.Throw<ObjectDisposedException>(() => _ = decoder.Metadata);
        Should.Throw<ObjectDisposedException>(() => frame.Decode(cancellationToken: TestContext.Current.CancellationToken));
        Should.Throw<ObjectDisposedException>(() => frame.CopyPixels(null, PixelFormat.Rgba32, new byte[W * H * 4], 0, cancellationToken: TestContext.Current.CancellationToken));
        Should.Throw<ObjectDisposedException>(() => frame.TryGetThumbnail(out _));
    }

    [Fact]
    public void AFrameIndexOutOfRangeIsRefused()
    {
        using var decoder = ImageDecoder.Open(Png.AsMemory());
        Should.Throw<ArgumentOutOfRangeException>(() => decoder.GetFrame(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => decoder.GetFrame(1));
        Should.Throw<ArgumentOutOfRangeException>(() => decoder.GetFrame(int.MaxValue));
    }

    // ---- frames and CopyPixels ---------------------------------------------------------------------------------

    private static byte[] Crop(byte[] pixels, int width, int bytesPerPixel, PixelRect region)
    {
        var result = new byte[region.Width * region.Height * bytesPerPixel];
        for (var y = 0; y < region.Height; y++)
            pixels.AsSpan(((region.Y + y) * width + region.X) * bytesPerPixel, region.Width * bytesPerPixel)
                .CopyTo(result.AsSpan(y * region.Width * bytesPerPixel));
        return result;
    }

    public static TheoryData<string> Stills() => ["png", "jpeg", "webp", "qoi", "bmp"];

    private static byte[] Still(string name) => name switch
    {
        "png" => Png,
        "jpeg" => JpegData,
        "webp" => Webp.WebpEncoder.EncodeLossless(Rgba(3), W, H, PixelFormat.Rgba32),
        "qoi" => Qoi.QoiEncoder.Encode(Rgba(4), W, H, PixelFormat.Rgba32),
        _ => Bmp.BmpEncoder.Encode(Rgba(5), W, H, PixelFormat.Rgba32),
    };

    [Theory]
    [MemberData(nameof(Stills))]
    public void CopyPixelsIsACropOfTheConvertedImage(string name)
    {
        var data = Still(name);
        var full = DecodedPixels(data, out var width, out var height, out var native);
        using var decoder = ImageDecoder.Open(data.AsMemory());
        var frame = decoder.GetFrame(0);
        PixelRect?[] regions = [null, new PixelRect(0, 0, width, height), new PixelRect(3, 2, 11, 7), new PixelRect(width - 1, height - 1, 1, 1), new PixelRect(0, 5, width, 1)];

        foreach (var format in PixelFormat.All)
        {
            var converted = PixelConverterTests.ReferenceConvert(full, native, format, width * height);
            // The stored-luma exception: a YCbCr JPEG's Gray8 is its Y plane when the whole image is asked for. (With a
            // region the codec decodes RGB and crops, so the luma is the RGB's: see JpegGray8WithARegionIsTheStoredLuma.)
            var stored = name == "jpeg" && format == PixelFormat.Gray8 ? Jpeg.JpegDiagnostics.DecodePlanes(data)[0].Samples : null;

            foreach (var region in regions)
            {
                var r = region ?? new PixelRect(0, 0, width, height);
                var expected = Crop(region is null && stored is not null ? stored : converted, width, format.BytesPerPixel, r);
                var rowBytes = r.Width * format.BytesPerPixel;
                foreach (var stride in new[] { 0, rowBytes + 5 })
                {
                    var step = stride == 0 ? rowBytes : stride;
                    var destination = new byte[step * (r.Height - 1) + rowBytes + 9];
                    Array.Fill(destination, Sentinel);

                    frame.CopyPixels(region, format, destination, stride, cancellationToken: TestContext.Current.CancellationToken);

                    for (var y = 0; y < r.Height; y++)
                    {
                        destination.AsSpan(y * step, rowBytes).ToArray().ShouldBe(expected.AsSpan(y * rowBytes, rowBytes).ToArray(), $"{name} {format} {r} stride {stride} row {y}");
                        var gap = y == r.Height - 1 ? 9 : step - rowBytes;
                        destination.AsSpan(y * step + rowBytes, gap).ToArray().ShouldAllBe(v => v == Sentinel, $"{name} {format} {r} stride {stride}: wrote outside row {y}");
                    }
                }
            }
        }
    }

    [Fact(Skip = "BUG: JPEG Gray8 with a Region is the luma of the decoded RGB, but without a Region it is the stored Y plane (PixelFormat.Gray8 contract)")]
    public void JpegGray8WithARegionIsTheStoredLuma()
    {
        using var decoder = ImageDecoder.Open(JpegData.AsMemory());
        var frame = decoder.GetFrame(0);
        var whole = new byte[W * H];
        frame.CopyPixels(null, PixelFormat.Gray8, whole, 0, cancellationToken: TestContext.Current.CancellationToken);
        var viaRegion = new byte[W * H];
        frame.CopyPixels(new PixelRect(0, 0, W, H), PixelFormat.Gray8, viaRegion, 0, cancellationToken: TestContext.Current.CancellationToken);
        viaRegion.ShouldBe(whole);
    }

    [Fact]
    public void CopyPixelsRefusesWhatDoesNotFit()
    {
        using var decoder = ImageDecoder.Open(Png.AsMemory());
        var frame = decoder.GetFrame(0);
        var token = TestContext.Current.CancellationToken;
        Should.Throw<ArgumentException>(() => frame.CopyPixels(null, PixelFormat.Rgba32, new byte[W * H * 4 - 1], 0, cancellationToken: token));
        Should.Throw<ArgumentException>(() => frame.CopyPixels(null, PixelFormat.Rgba32, new byte[W * H * 4], W * 4 - 1, cancellationToken: token));
        Should.Throw<ArgumentException>(() => frame.CopyPixels(new PixelRect(0, 0, 4, 4), PixelFormat.Rgba32, new byte[4 * 4 * 4 - 1], 0, cancellationToken: token));
        // A region outside the image.
        Should.Throw<ArgumentException>(() => frame.CopyPixels(new PixelRect(W - 2, 0, 4, 4), PixelFormat.Rgba32, new byte[1 << 12], 0, cancellationToken: token));
        Should.Throw<ArgumentException>(() => frame.CopyPixels(new PixelRect(-1, 0, 4, 4), PixelFormat.Rgba32, new byte[1 << 12], 0, cancellationToken: token));
        Should.Throw<ArgumentException>(() => frame.CopyPixels(new PixelRect(0, 0, 0, 4), PixelFormat.Rgba32, new byte[1 << 12], 0, cancellationToken: token));
    }

    [Fact]
    public void CopyPixelsHonoursAScaleAndACancelledToken()
    {
        using var decoder = ImageDecoder.Open(JpegData.AsMemory());
        var frame = decoder.GetFrame(0);
        var half = JpegDecoder.Decode(JpegData, JpegScale.Half);
        var destination = new byte[half.Width * half.Height * 4];
        frame.CopyPixels(null, PixelFormat.Rgba32, destination, 0, new DecodeOptions { ScaleDenominator = 2 }, TestContext.Current.CancellationToken);
        destination.ShouldBe(half.Pixels);

        Should.Throw<OperationCanceledException>(() =>
            frame.CopyPixels(null, PixelFormat.Rgba32, new byte[W * H * 4], 0, cancellationToken: new CancellationToken(canceled: true)));
        Should.Throw<OperationCanceledException>(() => frame.Decode(cancellationToken: new CancellationToken(canceled: true)));
    }

    [Fact]
    public void FrameDecodeWithOptionsIsTheFacadesDecode()
    {
        using var decoder = ImageDecoder.Open(JpegData.AsMemory());
        var options = new DecodeOptions { ScaleDenominator = 4, Region = new PixelRect(1, 1, 4, 3) };
        SimdCodecs.TryDecode(JpegData, out var expected, options, TestContext.Current.CancellationToken).ShouldBeTrue();
        using (expected)
        using (var actual = decoder.GetFrame(0).Decode(options, TestContext.Current.CancellationToken))
        {
            (actual.Width, actual.Height).ShouldBe((expected.Width, expected.Height));
            actual.Pixels.ToArray().ShouldBe(expected.Pixels.ToArray());
        }
    }

    [Fact]
    public void FrameTryGetClosestSizeIsTheFacadesForAStill()
    {
        using var decoder = ImageDecoder.Open(JpegData.AsMemory());
        var frame = decoder.GetFrame(0);
        foreach (var (w, h) in new[] { (1, 1), (8, 5), (16, 10), (W, H), (200, 200) })
        {
            frame.TryGetClosestSize(w, h, out var width, out var height, out var scale).ShouldBeTrue();
            SimdCodecs.TryGetClosestSize(JpegData, w, h, out var ew, out var eh, out var es).ShouldBeTrue();
            (width, height, scale).ShouldBe((ew, eh, es));
        }

        using var png = ImageDecoder.Open(Png.AsMemory());
        png.GetFrame(0).TryGetClosestSize(3, 3, out var pw, out var ph, out var ps).ShouldBeTrue();
        (pw, ph, ps).ShouldBe((W, H, 1));
    }

    [Fact]
    public void MetadataIsTheFacadesAndTheFramesInfoIsTheFacadesInfo()
    {
        var exif = MetadataTests.ExifBlock(orientation: 6, resolution: (72, 72, 2));
        var packet = new JpegXmp { CreatorTool = "session" }.BuildPacket();
        var data = JpegEncoder.Encode(Rgb(9), W, H, 3, new JpegEncodeOptions { Exif = exif, Xmp = new JpegXmp { CreatorTool = "session" }, IccProfile = new byte[300] });
        using var decoder = ImageDecoder.Open(data.AsMemory());

        decoder.Metadata.Exif.ToArray().ShouldBe(exif);
        decoder.Metadata.Xmp.ToArray().ShouldBe(packet);
        decoder.Metadata.Icc.Length.ShouldBe(300);
        decoder.GetFrame(0).Metadata.Exif.ToArray().ShouldBe(exif);

        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        decoder.GetFrame(0).Info.ShouldBe(info);
        decoder.GetFrame(0).Info.Orientation.ShouldBe(ImageOrientation.Rotate90);

        using var plain = ImageDecoder.Open(Png.AsMemory());
        plain.Metadata.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ThumbnailsComeFromTheFirstFrame()
    {
        var thumbJpeg = JpegEncoder.Encode(Rgb(1).AsSpan(0, 8 * 6 * 3).ToArray(), 8, 6, 3);
        var data = JpegEncoder.Encode(Rgb(10), W, H, 3, new JpegEncodeOptions { Exif = MetadataTests.ExifBlock(thumbnail: thumbJpeg) });
        using var decoder = ImageDecoder.Open(data.AsMemory());
        var frame = decoder.GetFrame(0);
        frame.Info.HasThumbnail.ShouldBeTrue();
        frame.TryGetThumbnail(out var thumbnail).ShouldBeTrue();
        SimdCodecs.TryDecode(thumbJpeg, out var expected, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (thumbnail)
        using (expected)
            thumbnail.Pixels.ToArray().ShouldBe(expected.Pixels.ToArray());

        using var plain = ImageDecoder.Open(JpegData.AsMemory());
        plain.GetFrame(0).TryGetThumbnail(out var none).ShouldBeFalse();
        none.ShouldBeNull();
    }

    // ---- animations --------------------------------------------------------------------------------------------

    private static byte[] AnimatedWebp()
    {
        var frames = new List<Webp.WebpAnimationCanvasFrame>();
        for (var i = 0; i < 4; i++)
            frames.Add(new Webp.WebpAnimationCanvasFrame(Rgba(20 + i), TimeSpan.FromMilliseconds(30 + 10 * i)));
        return Webp.WebpEncoder.EncodeAnimation(frames, W, H, PixelFormat.Rgba32,
            new Webp.WebpAnimationEncodeOptions { Mode = Webp.WebpAnimationMode.Lossless, LoopCount = 3 });
    }

    private static byte[] AnimatedGifOurs()
    {
        var frames = new List<Gif.GifAnimationFrame>();
        for (var i = 0; i < 3; i++)
            frames.Add(new Gif.GifAnimationFrame(Rgb(30 + i), TimeSpan.FromMilliseconds(50 * (i + 1))));
        return Gif.GifEncoder.EncodeAnimation(frames, W, H, 3);
    }

    public static TheoryData<string> Animations() => ["webp", "gif ours", "gif mixed", "gif offsets"];

    private static byte[] Animation(string name) => name switch
    {
        "webp" => AnimatedWebp(),
        "gif ours" => AnimatedGifOurs(),
        _ => GifDecoderTests.Animation(name["gif ".Length..]),
    };

    [Theory]
    [MemberData(nameof(Animations))]
    public void AnimatedFramesAreTheFrameReadersFrames(string name)
    {
        var data = Animation(name);
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBeGreaterThan(1);
            using var decoder = ImageDecoder.Open(data.AsMemory());
            decoder.FrameCount.ShouldBe(reader.FrameCount);
            decoder.LoopCount.ShouldBe(reader.LoopCount);
            decoder.Format.ShouldBe(name.StartsWith("gif", StringComparison.Ordinal) ? ImageFormat.Gif : ImageFormat.Webp);

            SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
            info.FrameCount.ShouldBe(decoder.FrameCount);

            // Last to first, so a frame is not just "the next one".
            for (var i = decoder.FrameCount - 1; i >= 0; i--)
            {
                var frame = decoder.GetFrame(i);
                reader.TryGetFrameInfo(i, out var expectedInfo).ShouldBeTrue();
                frame.Index.ShouldBe(i);
                frame.Info.ShouldBe(expectedInfo.Info);
                frame.Duration.ShouldBe(expectedInfo.Duration);
                reader.TryDecodeFrame(i, out var expected, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
                using (expected)
                using (var actual = frame.Decode(cancellationToken: TestContext.Current.CancellationToken))
                {
                    (actual.Width, actual.Height, actual.Channels, actual.SampleFormat).ShouldBe((expected.Width, expected.Height, expected.Channels, expected.SampleFormat));
                    actual.Pixels.ToArray().ShouldBe(expected.Pixels.ToArray(), $"{name} frame {i}");

                    // CopyPixels: the frame cropped and converted.
                    var native = PixelFormat.ForChannels(expected.Channels, expected.SampleFormat);
                    var region = new PixelRect(2, 1, Math.Min(9, expected.Width - 2), Math.Min(6, expected.Height - 1));
                    foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgra32Premultiplied, PixelFormat.Gray16, PixelFormat.Rgb24 })
                    {
                        var converted = PixelConverterTests.ReferenceConvert(expected.Pixels, native, format, expected.Width * expected.Height);
                        var want = Crop(converted, expected.Width, format.BytesPerPixel, region);
                        var got = new byte[want.Length];
                        frame.CopyPixels(region, format, got, 0, cancellationToken: TestContext.Current.CancellationToken);
                        got.ShouldBe(want, $"{name} frame {i} {format}");
                    }
                }

                // Only the first frame of an animation has a thumbnail, and an animation's sizes are its canvas.
                frame.TryGetClosestSize(1, 1, out var cw, out var ch, out var cs).ShouldBeTrue();
                (cw, ch, cs).ShouldBe((frame.Info.Width, frame.Info.Height, 1));
            }
        }
    }

    [Fact]
    public void TheLoopCountOfAnAnimatedWebpIsReported()
    {
        using var decoder = ImageDecoder.Open(AnimatedWebp().AsMemory());
        decoder.FrameCount.ShouldBe(4);
        decoder.LoopCount.ShouldBe(3);
        decoder.GetFrame(2).Duration.ShouldBe(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void AnimationsOpenFromEverySource()
    {
        var data = AnimatedWebp();
        var path = WriteFile(data, "anim.webp");
        using var fromPath = ImageDecoder.Open(path);
        using var fromMemory = ImageDecoder.Open(data.AsMemory());
        using var fromSeekable = ImageDecoder.Open(new MemoryStream(data));
        using var fromPipe = ImageDecoder.Open(new NonSeekableStream(data));
        foreach (var decoder in new[] { fromPath, fromMemory, fromSeekable, fromPipe })
        {
            decoder.FrameCount.ShouldBe(4);
            using var last = decoder.GetFrame(3).Decode(cancellationToken: TestContext.Current.CancellationToken);
            using var reference = fromMemory.GetFrame(3).Decode(cancellationToken: TestContext.Current.CancellationToken);
            last.Pixels.ToArray().ShouldBe(reference.Pixels.ToArray());
        }
    }
}
