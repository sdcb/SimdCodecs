using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A token that is already cancelled makes the facade throw <see cref="OperationCanceledException"/> (never return
/// <c>false</c>, which means "not decodable") from <see cref="SimdCodecs.TryDecode"/> and
/// <see cref="SimdCodecs.TryDecodeInto"/>, for every codec; a live token changes nothing.
/// </summary>
[Collection(FacadeRegistryCollection.Name)]
public sealed class CancellationTests : IDisposable
{
    private const int W = 64, H = 48;
    private const string HeifFixture = "synth-photo-mangrove-1599x1199-main8.heic";

    private static readonly CancellationToken Cancelled = new(canceled: true);

    private readonly ImageDecoderRegistration[] _builtIn = SimdCodecs.RegistrySnapshot;

    public void Dispose() => SimdCodecs.ExchangeRegistry(_builtIn);

    private static byte[] Rgba()
    {
        var pixels = new byte[W * H * 4];
        new Random(11).NextBytes(pixels);
        return pixels;
    }

    private static byte[] Rgb()
    {
        var pixels = new byte[W * H * 3];
        new Random(12).NextBytes(pixels);
        return pixels;
    }

    private static readonly Dictionary<string, Func<byte[]>> Builders = new()
    {
        ["jpeg"] = () => Jpeg.JpegEncoder.Encode(Rgb(), W, H, 3),
        ["jpeg progressive"] = () => Jpeg.JpegEncoder.Encode(Rgb(), W, H, 3, new Jpeg.JpegEncodeOptions { Progressive = true }),
        ["png"] = () => Png.PngWriter.Encode(Rgba(), W, H),
        ["png rgb8"] = () => Png.PngWriter.EncodeRgb8(Rgb(), W, H),
        ["webp lossless"] = () => Webp.WebpEncoder.EncodeLossless(Rgba(), W, H, PixelFormat.Rgba32),
        ["webp lossy"] = () => Webp.WebpEncoder.EncodeLossy(Rgb(), W, H, PixelFormat.Rgb24),
        ["tiff"] = () => HdrTiff.Uint16Rgb(W, H, [.. Enumerable.Range(0, W * H).Select(i => i & 0xFFFF)],
            [.. Enumerable.Range(0, W * H).Select(i => (i * 3) & 0xFFFF)], [.. Enumerable.Range(0, W * H).Select(i => (i * 7) & 0xFFFF)]),
        ["gif"] = () => Gif.GifEncoder.Encode(Rgb(), W, H, 3),
        ["qoi"] = () => Qoi.QoiEncoder.Encode(Rgba(), W, H, PixelFormat.Rgba32),
        ["bmp"] = () => Bmp.BmpEncoder.Encode(Rgba(), W, H, PixelFormat.Rgba32),
        ["pnm"] = () => Sdcb.SimdCodecs.Pnm.PnmEncoder.Encode(Rgb(), W, H, 3),
    };

    private static readonly Lazy<Dictionary<string, byte[]>> Corpus = new(() => Builders.ToDictionary(b => b.Key, b => b.Value()));

    public static TheoryData<string> Names() => [.. Builders.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public void AnAlreadyCancelledTokenMakesTryDecodeThrow(string name)
    {
        var data = Corpus.Value[name];
        Should.Throw<OperationCanceledException>(() => SimdCodecs.TryDecode(data, out _, cancellationToken: Cancelled));
        Should.Throw<OperationCanceledException>(() => SimdCodecs.TryDecode(data, out _, DecodeOptions.Default, Cancelled));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void AnAlreadyCancelledTokenMakesTryDecodeIntoThrow(string name)
    {
        var data = Corpus.Value[name];
        // A native layout and ones that go through the generic path.
        foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgr24, PixelFormat.Gray8, PixelFormat.Rgba64, PixelFormat.Bgra32Premultiplied })
        {
            var destination = new byte[W * H * format.BytesPerPixel];
            Should.Throw<OperationCanceledException>(
                () => SimdCodecs.TryDecodeInto(data, destination, 0, format, cancellationToken: Cancelled), $"{name} {format}");
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ALiveTokenChangesNothing(string name)
    {
        var data = Corpus.Value[name];
        SimdCodecs.TryDecode(data, out var plain, cancellationToken: CancellationToken.None).ShouldBeTrue(name);
        SimdCodecs.TryDecode(data, out var withToken, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue(name);
        using (plain)
        using (withToken)
        {
            (withToken.Width, withToken.Height, withToken.Channels, withToken.SampleFormat)
                .ShouldBe((plain.Width, plain.Height, plain.Channels, plain.SampleFormat));
            withToken.Pixels.ToArray().ShouldBe(plain.Pixels.ToArray(), name);
        }

        var a = new byte[W * H * 4];
        var b = new byte[W * H * 4];
        SimdCodecs.TryDecodeInto(data, a, 0, PixelFormat.Rgba32, cancellationToken: CancellationToken.None).ShouldBeTrue(name);
        SimdCodecs.TryDecodeInto(data, b, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue(name);
        b.ShouldBe(a, name);
    }

    [Fact]
    public void ACancelledTokenDoesNotMakeGarbageLookDecodable()
    {
        // Not an image: the answer is "no", with or without a cancelled token (nothing to cancel).
        SimdCodecs.TryDecode(new byte[100], out _, cancellationToken: Cancelled).ShouldBeFalse();
        SimdCodecs.TryDecodeInto(new byte[100], new byte[1000], 0, PixelFormat.Rgba32, cancellationToken: Cancelled).ShouldBeFalse();
    }

    [Fact]
    public void AnAlreadyCancelledTokenStopsFrameDecoding()
    {
        var animation = Webp.WebpEncoder.EncodeAnimation(
            [new Webp.WebpAnimationCanvasFrame(Rgba(), TimeSpan.FromMilliseconds(40)), new Webp.WebpAnimationCanvasFrame(Rgba().Select(v => (byte)~v).ToArray(), TimeSpan.FromMilliseconds(40))],
            W, H, PixelFormat.Rgba32, new Webp.WebpAnimationEncodeOptions { Mode = Webp.WebpAnimationMode.Lossless });
        SimdCodecs.TryOpenFrames(animation, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(2);
            Should.Throw<OperationCanceledException>(() => reader.TryDecodeFrame(1, out _, cancellationToken: Cancelled));
            Should.Throw<OperationCanceledException>(
                () => reader.TryDecodeFrameInto(0, new byte[W * H * 4], 0, PixelFormat.Rgba32, cancellationToken: Cancelled));
        }
    }

    [Fact]
    public void HeifAnAlreadyCancelledTokenThrows()
    {
        var data = HeifTests.ReadFixture(HeifFixture);
        SimdCodecs.Register(HeifImageDecoder.Registration);

        Should.Throw<OperationCanceledException>(() => SimdCodecs.TryDecode(data, out _, cancellationToken: Cancelled));
        Should.Throw<OperationCanceledException>(() =>
            SimdCodecs.TryDecodeInto(data, new byte[1599 * 1199 * 4], 0, PixelFormat.Rgba32, cancellationToken: Cancelled));
        Should.Throw<OperationCanceledException>(() =>
            SimdCodecs.TryDecodeInto(data, new byte[1599 * 1199 * 8], 0, PixelFormat.Bgra64Premultiplied, cancellationToken: Cancelled));

        // A live token still decodes.
        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        (image.Width, image.Height).ShouldBe((1599, 1199));
        image.Dispose();
    }
}
