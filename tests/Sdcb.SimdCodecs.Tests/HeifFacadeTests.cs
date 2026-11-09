using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Tests that change or depend on the facade's process-wide codec registry run one at a time in this collection,
/// so a test that registers HEIF never overlaps one that asserts it is not registered.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FacadeRegistryCollection
{
    public const string Name = "SimdCodecs registry";
}

/// <summary>
/// <see cref="ImageCodecs.Register"/> with the HEIF package: before registration the facade recognises HEIF and
/// decodes none of it; after it every entry point dispatches to <see cref="HeifImageDecoder"/> and returns exactly
/// what the adapter does. Each test leaves the registry as it found it.
/// </summary>
[Collection(FacadeRegistryCollection.Name)]
public sealed class HeifFacadeTests : IDisposable
{
    private const string Fixture = "synth-photo-mangrove-1599x1199-main8.heic"; // odd size and a clap crop

    private static readonly PixelFormat[] AllFormats =
        [PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24, PixelFormat.Gray8];

    private readonly ImageDecoderRegistration[] _builtIn;

    public HeifFacadeTests() => _builtIn = ImageCodecs.RegistrySnapshot;

    public void Dispose() => ImageCodecs.ExchangeRegistry(_builtIn);

    [Fact]
    public void Unregistered_HeifIsRecognisedButNotDecoded()
    {
        byte[] data = HeifTests.ReadFixture(Fixture);
        ImageCodecs.DetectFormat(data).ShouldBe(ImageFormat.Heif);
        ImageCodecs.IsDecodable(ImageFormat.Heif).ShouldBeFalse();
        ImageCodecs.CanDecode(data).ShouldBeFalse();
        ImageCodecs.TryReadInfo(data, out _).ShouldBeFalse();
        ImageCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        image.ShouldBeNull();
        ImageCodecs.TryDecodeInto(data, new byte[1599 * 1199 * 4], 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        ImageCodecs.TryOpenFrames(data, out var reader).ShouldBeFalse();
        reader.ShouldBeNull();
    }

    [Fact]
    public void Registered_EveryEntryPointDecodes()
    {
        byte[] data = HeifTests.ReadFixture(Fixture);
        ImageCodecs.Register(HeifImageDecoder.Registration);

        ImageCodecs.IsDecodable(ImageFormat.Heif).ShouldBeTrue();
        ImageCodecs.CanDecode(data).ShouldBeTrue();
        ImageCodecs.DetectFormat(data).ShouldBe(ImageFormat.Heif);

        HeifImageDecoder.TryReadInfo(data, out var expectedInfo).ShouldBeTrue();
        ImageCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.ShouldBe(expectedInfo);
        (info.Width, info.Height).ShouldBe((1599, 1199));

        HeifImageDecoder.TryDecode(data, out var expected, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        ImageCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Pixels.SequenceEqual(expected.Pixels).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(expected.ColorEncoding);

        foreach (var format in AllFormats)
        {
            int bytes = info.Width * info.Height * format.BytesPerPixel;
            byte[] mine = new byte[bytes], theirs = new byte[bytes];
            HeifImageDecoder.TryDecodeInto(data, theirs, 0, format, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            ImageCodecs.TryDecodeInto(data, mine, 0, format, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue($"{format}");
            mine.ShouldBe(theirs, $"{format}");
        }

        var rgba = new byte[info.Width * info.Height * 4];
        ImageCodecs.TryDecodeIntoRgba8(data, rgba).ShouldBeTrue();
        rgba.ShouldBe(expected.ToRgba8());

        ImageCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(1);
            reader.LoopCount.ShouldBeNull();
            reader.TryGetFrameInfo(0, out var frame).ShouldBeTrue();
            frame.Info.ShouldBe(info);
            reader.TryDecodeFrame(0, out var first, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            first.Pixels.SequenceEqual(expected.Pixels).ShouldBeTrue();
            var into = new byte[rgba.Length];
            reader.TryDecodeFrameInto(0, into, 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            into.ShouldBe(rgba);
            Should.Throw<ArgumentOutOfRangeException>(() => reader.TryDecodeFrame(1, out _));
        }
    }

    [Fact]
    public void Registered_UndecodableHeifReturnsFalse()
    {
        ImageCodecs.Register(HeifImageDecoder.Registration);
        foreach (string fixture in new[] { "synth-screen-testsrc2-1280x720-444.heic", "synth-anim-testsrc2-640x360-8frames.heics" })
        {
            byte[] data = HeifTests.ReadFixture(fixture);
            ImageCodecs.DetectFormat(data).ShouldBe(ImageFormat.Heif);
            ImageCodecs.TryDecode(data, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse(fixture);
            ImageCodecs.TryDecodeInto(data, new byte[1280 * 720 * 4], 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse(fixture);
        }

        byte[] pq = HeifTests.ReadFixture("synth-screen-testsrc2-640x360-pq-vui.heic");
        ImageCodecs.TryDecode(pq, out var hdr, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        hdr.ColorEncoding.Transfer.ShouldBe(TransferFunction.Pq);
        ImageCodecs.TryDecodeInto(pq, new byte[640 * 360 * 4], 0, PixelFormat.Rgba32, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        int before = ImageCodecs.RegisteredCount;
        ImageCodecs.Register(HeifImageDecoder.Registration);
        ImageCodecs.RegisteredCount.ShouldBe(before + 1);
        ImageCodecs.Register(HeifImageDecoder.Registration);
        ImageCodecs.RegisteredCount.ShouldBe(before + 1);

        // A second registration object built from the same method groups is the same codec.
        ImageCodecs.Register(new ImageDecoderRegistration
        {
            Format = ImageFormat.Heif,
            CanDecode = HeifImageDecoder.CanDecode,
            TryReadInfo = HeifImageDecoder.TryReadInfo,
            TryGetClosestSize = HeifImageDecoder.TryGetClosestSize,
            TryDecode = HeifImageDecoder.TryDecode,
            Decode = HeifImageDecoder.Decode,
            TryDecodeInto = HeifImageDecoder.TryDecodeInto,
            TryReadMetadata = HeifImageDecoder.TryReadMetadata,
            TryDecodeThumbnail = HeifImageDecoder.TryDecodeThumbnail,
            TryReadPlanarInfo = HeifImageDecoder.TryReadPlanarInfo,
            TryDecodePlanes = HeifImageDecoder.TryDecodePlanes,
        });
        ImageCodecs.RegisteredCount.ShouldBe(before + 1);

        // A different codec is not.
        ImageCodecs.Register(Fake("FAKE"u8.ToArray()));
        ImageCodecs.RegisteredCount.ShouldBe(before + 2);
    }

    /// <summary>A registered codec is sniffed after every built-in one: it cannot take a PNG away from the PNG
    /// decoder even when its signature test accepts the PNG signature. (Its signatures are specific: other test
    /// classes use the facade in parallel.)</summary>
    [Fact]
    public void RegisteredCodecs_AreSniffedAfterTheBuiltInOnes()
    {
        ImageCodecs.Register(Fake([0x89, (byte)'P', (byte)'N', (byte)'G']));
        ImageCodecs.Register(Fake("Q4Q4fake"u8.ToArray()));

        byte[] png = PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);
        ImageCodecs.DetectFormat(png).ShouldBe(ImageFormat.Png);
        ImageCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
        info.Format.ShouldBe(ImageFormat.Png);

        byte[] other = "Q4Q4fake stream"u8.ToArray();
        ImageCodecs.DetectFormat(other).ShouldBe(ImageFormat.Avif);
        ImageCodecs.IsDecodable(ImageFormat.Avif).ShouldBeTrue();
        ImageCodecs.TryReadInfo(other, out info).ShouldBeTrue();
        (info.Width, info.Format).ShouldBe((1, ImageFormat.Avif));
    }

    [Fact]
    public void Register_ValidatesItsArgument()
    {
        Should.Throw<ArgumentNullException>(() => ImageCodecs.Register(null!));
        Should.Throw<ArgumentOutOfRangeException>(() => HeifLike(ImageFormat.Unknown));
        Should.Throw<ArgumentOutOfRangeException>(() => HeifLike((ImageFormat)200));
        Should.Throw<ArgumentNullException>(() => HeifLike(ImageFormat.Heif, nullCanDecode: true));
        Should.Throw<ArgumentNullException>(() => HeifLike(ImageFormat.Heif, nullDecodeInto: true));

        // Only one planar delegate: the initializer cannot see the pair, so Register checks it.
        var halfPlanar = new ImageDecoderRegistration
        {
            Format = ImageFormat.Heif,
            CanDecode = HeifImageDecoder.CanDecode,
            TryReadInfo = HeifImageDecoder.TryReadInfo,
            TryGetClosestSize = HeifImageDecoder.TryGetClosestSize,
            TryDecode = HeifImageDecoder.TryDecode,
            TryDecodeInto = HeifImageDecoder.TryDecodeInto,
            TryReadMetadata = HeifImageDecoder.TryReadMetadata,
            TryDecodeThumbnail = HeifImageDecoder.TryDecodeThumbnail,
            TryReadPlanarInfo = HeifImageDecoder.TryReadPlanarInfo,
        };
        Should.Throw<ArgumentException>(() => ImageCodecs.Register(halfPlanar));
    }

    private static ImageDecoderRegistration HeifLike(ImageFormat format, bool nullCanDecode = false, bool nullDecodeInto = false) => new()
    {
        Format = format,
        CanDecode = nullCanDecode ? null! : HeifImageDecoder.CanDecode,
        TryReadInfo = HeifImageDecoder.TryReadInfo,
        TryGetClosestSize = HeifImageDecoder.TryGetClosestSize,
        TryDecode = HeifImageDecoder.TryDecode,
        TryDecodeInto = nullDecodeInto ? null! : HeifImageDecoder.TryDecodeInto,
        TryReadMetadata = HeifImageDecoder.TryReadMetadata,
        TryDecodeThumbnail = HeifImageDecoder.TryDecodeThumbnail,
    };

    /// <summary>Registration while other threads decode: no decode fails or sees a torn registry, and every
    /// distinct codec ends up registered exactly once.</summary>
    [Fact]
    public async Task ConcurrentRegistrationAndDecoding()
    {
        byte[] heif = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        byte[] png = PngWriter.Encode(Enumerable.Range(0, 16 * 16 * 4).Select(i => (byte)i).ToArray(), 16, 16);
        ImageCodecs.Register(HeifImageDecoder.Registration);
        HeifImageDecoder.TryDecode(heif, out var expected, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        byte[] expectedPixels = expected.Pixels.ToArray();
        int before = ImageCodecs.RegisteredCount;

        const int Codecs = 64;
        var fakes = Enumerable.Range(0, Codecs).Select(i => Fake([(byte)'Z', (byte)i, 0x7F, 0x7E])).ToArray();
        using var stop = new CancellationTokenSource();
        var decoders = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            int decodes = 0;
            while (!stop.IsCancellationRequested || decodes < 2)
            {
                ImageCodecs.TryDecode(heif, out var image).ShouldBeTrue();
                image.Pixels.SequenceEqual(expectedPixels).ShouldBeTrue();
                ImageCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
                info.Format.ShouldBe(ImageFormat.Png);
                decodes++;
            }
        })).ToArray();
        var registrars = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            // Every thread registers every fake (and HEIF again): duplicates must collapse.
            for (int i = 0; i < Codecs; i++)
            {
                ImageCodecs.Register(fakes[(i + t * 17) % Codecs]);
                ImageCodecs.Register(HeifImageDecoder.Registration);
            }
        })).ToArray();

        await Task.WhenAll(registrars);
        await stop.CancelAsync();
        await Task.WhenAll(decoders);
        ImageCodecs.RegisteredCount.ShouldBe(before + Codecs);
        for (int i = 0; i < Codecs; i++)
        {
            byte[] stream = [(byte)'Z', (byte)i, 0x7F, 0x7E, 0, 0];
            ImageCodecs.TryReadInfo(stream, out var info).ShouldBeTrue();
            info.Width.ShouldBe(1);
        }
    }

    /// <summary>A stand-in codec that claims streams starting with <paramref name="magic"/> and reports a 1×1
    /// AVIF; its signature test captures <paramref name="magic"/>, so each call is a distinct codec.</summary>
    private static ImageDecoderRegistration Fake(byte[] magic) =>
        new()
        {
            Format = ImageFormat.Avif,
            CanDecode = header => header.StartsWith(magic),
            TryReadInfo = FakeInfo,
            TryGetClosestSize = FakeClosestSize,
            TryDecode = FakeDecode,
            TryDecodeInto = (_, _, _, _, _, _) => false,
            TryReadMetadata = FakeMetadata,
            TryDecodeThumbnail = FakeThumbnail,
        };

    private static bool FakeClosestSize(ReadOnlySpan<byte> data, int w, int h, out int width, out int height, out int scale)
    {
        (width, height, scale) = (1, 1, 1);
        return true;
    }

    private static bool FakeMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = ImageMetadata.Empty;
        return true;
    }

    private static bool FakeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        thumbnail = null;
        return false;
    }

    private static bool FakeInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = new ImageInfo(1, 1, 3, SampleFormat.UInt8) { Format = ImageFormat.Avif };
        return true;
    }

    private static bool FakeDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options, CancellationToken cancellationToken)
    {
        image = RasterImage.Wrap(1, 1, 3, SampleFormat.UInt8, [0, 0, 0]);
        return true;
    }
}
