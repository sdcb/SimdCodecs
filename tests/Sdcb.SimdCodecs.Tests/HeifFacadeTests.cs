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
/// <see cref="SimdCodecs.Register"/> with the HEIF package: before registration the facade recognises HEIF and
/// decodes none of it; after it every entry point dispatches to <see cref="HeifImageDecoder"/> and returns exactly
/// what the adapter does. Each test leaves the registry as it found it.
/// </summary>
[Collection(FacadeRegistryCollection.Name)]
public sealed class HeifFacadeTests : IDisposable
{
    private const string Fixture = "synth-photo-mangrove-1599x1199-main8.heic"; // odd size and a clap crop

    private static readonly ImagePixelFormat[] AllFormats =
        [ImagePixelFormat.Rgba32, ImagePixelFormat.Bgra32, ImagePixelFormat.Rgb24, ImagePixelFormat.Bgr24, ImagePixelFormat.Gray8];

    private readonly ImageDecoderRegistration[] _builtIn;

    public HeifFacadeTests() => _builtIn = SimdCodecs.RegistrySnapshot;

    public void Dispose() => SimdCodecs.ExchangeRegistry(_builtIn);

    [Fact]
    public void Unregistered_HeifIsRecognisedButNotDecoded()
    {
        byte[] data = HeifTests.ReadFixture(Fixture);
        SimdCodecs.DetectFormat(data).ShouldBe(ImageFormat.Heif);
        SimdCodecs.IsDecodable(ImageFormat.Heif).ShouldBeFalse();
        SimdCodecs.CanDecode(data).ShouldBeFalse();
        SimdCodecs.TryReadInfo(data, out _).ShouldBeFalse();
        SimdCodecs.TryDecode(data, out var image).ShouldBeFalse();
        image.ShouldBeNull();
        SimdCodecs.TryDecodeInto(data, new byte[1599 * 1199 * 4], 0, ImagePixelFormat.Rgba32).ShouldBeFalse();
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeFalse();
        reader.ShouldBeNull();
    }

    [Fact]
    public void Registered_EveryEntryPointDecodes()
    {
        byte[] data = HeifTests.ReadFixture(Fixture);
        SimdCodecs.Register(HeifImageDecoder.Registration);

        SimdCodecs.IsDecodable(ImageFormat.Heif).ShouldBeTrue();
        SimdCodecs.CanDecode(data).ShouldBeTrue();
        SimdCodecs.DetectFormat(data).ShouldBe(ImageFormat.Heif);

        HeifImageDecoder.TryReadInfo(data, out var expectedInfo).ShouldBeTrue();
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.ShouldBe(expectedInfo);
        (info.Width, info.Height).ShouldBe((1599, 1199));

        HeifImageDecoder.TryDecode(data, out var expected).ShouldBeTrue();
        SimdCodecs.TryDecode(data, out var image).ShouldBeTrue();
        image.Pixels.SequenceEqual(expected.Pixels).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(expected.ColorEncoding);

        foreach (var format in AllFormats)
        {
            int bytes = info.Width * info.Height * PixelConverter.BytesPerPixel(format);
            byte[] mine = new byte[bytes], theirs = new byte[bytes];
            HeifImageDecoder.TryDecodeInto(data, theirs, 0, format).ShouldBeTrue();
            SimdCodecs.TryDecodeInto(data, mine, 0, format).ShouldBeTrue($"{format}");
            mine.ShouldBe(theirs, $"{format}");
        }

        var rgba = new byte[info.Width * info.Height * 4];
        SimdCodecs.TryDecodeIntoRgba8(data, rgba).ShouldBeTrue();
        rgba.ShouldBe(expected.ToRgba8());

        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader.FrameCount.ShouldBe(1);
            reader.LoopCount.ShouldBeNull();
            reader.TryGetFrameInfo(0, out var frame).ShouldBeTrue();
            frame.Info.ShouldBe(info);
            reader.TryDecodeFrame(0, out var first).ShouldBeTrue();
            first.Pixels.SequenceEqual(expected.Pixels).ShouldBeTrue();
            var into = new byte[rgba.Length];
            reader.TryDecodeFrameInto(0, into, 0, ImagePixelFormat.Rgba32).ShouldBeTrue();
            into.ShouldBe(rgba);
            Should.Throw<ArgumentOutOfRangeException>(() => reader.TryDecodeFrame(1, out _));
        }
    }

    [Fact]
    public void Registered_UndecodableHeifReturnsFalse()
    {
        SimdCodecs.Register(HeifImageDecoder.Registration);
        foreach (string fixture in new[] { "synth-screen-testsrc2-1280x720-444.heic", "synth-anim-testsrc2-640x360-8frames.heics" })
        {
            byte[] data = HeifTests.ReadFixture(fixture);
            SimdCodecs.DetectFormat(data).ShouldBe(ImageFormat.Heif);
            SimdCodecs.TryDecode(data, out _).ShouldBeFalse(fixture);
            SimdCodecs.TryDecodeInto(data, new byte[1280 * 720 * 4], 0, ImagePixelFormat.Rgba32).ShouldBeFalse(fixture);
        }

        byte[] pq = HeifTests.ReadFixture("synth-screen-testsrc2-640x360-pq-vui.heic");
        SimdCodecs.TryDecode(pq, out var hdr).ShouldBeTrue();
        hdr.ColorEncoding.Transfer.ShouldBe(TransferFunction.Pq);
        SimdCodecs.TryDecodeInto(pq, new byte[640 * 360 * 4], 0, ImagePixelFormat.Rgba32).ShouldBeFalse();
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        int before = SimdCodecs.RegisteredCount;
        SimdCodecs.Register(HeifImageDecoder.Registration);
        SimdCodecs.RegisteredCount.ShouldBe(before + 1);
        SimdCodecs.Register(HeifImageDecoder.Registration);
        SimdCodecs.RegisteredCount.ShouldBe(before + 1);

        // A second registration object built from the same method groups is the same codec.
        SimdCodecs.Register(new ImageDecoderRegistration(ImageFormat.Heif, HeifImageDecoder.CanDecode, HeifImageDecoder.TryReadInfo,
            HeifImageDecoder.TryDecode, HeifImageDecoder.TryDecodeInto));
        SimdCodecs.RegisteredCount.ShouldBe(before + 1);

        // A different codec is not.
        SimdCodecs.Register(Fake("FAKE"u8.ToArray()));
        SimdCodecs.RegisteredCount.ShouldBe(before + 2);
    }

    /// <summary>A registered codec is sniffed after every built-in one: it cannot take a PNG away from the PNG
    /// decoder even when its signature test accepts the PNG signature. (Its signatures are specific: other test
    /// classes use the facade in parallel.)</summary>
    [Fact]
    public void RegisteredCodecs_AreSniffedAfterTheBuiltInOnes()
    {
        SimdCodecs.Register(Fake([0x89, (byte)'P', (byte)'N', (byte)'G']));
        SimdCodecs.Register(Fake("Q4Q4fake"u8.ToArray()));

        byte[] png = PngWriter.Encode(new byte[4 * 4 * 4], 4, 4);
        SimdCodecs.DetectFormat(png).ShouldBe(ImageFormat.Png);
        SimdCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
        info.Format.ShouldBe(ImageFormat.Png);

        byte[] other = "Q4Q4fake stream"u8.ToArray();
        SimdCodecs.DetectFormat(other).ShouldBe(ImageFormat.Avif);
        SimdCodecs.IsDecodable(ImageFormat.Avif).ShouldBeTrue();
        SimdCodecs.TryReadInfo(other, out info).ShouldBeTrue();
        (info.Width, info.Format).ShouldBe((1, ImageFormat.Avif));
    }

    [Fact]
    public void Register_ValidatesItsArgument()
    {
        Should.Throw<ArgumentNullException>(() => SimdCodecs.Register(null!));
        Should.Throw<ArgumentOutOfRangeException>(() => new ImageDecoderRegistration(ImageFormat.Unknown, HeifImageDecoder.CanDecode,
            HeifImageDecoder.TryReadInfo, HeifImageDecoder.TryDecode, HeifImageDecoder.TryDecodeInto));
        Should.Throw<ArgumentOutOfRangeException>(() => new ImageDecoderRegistration((ImageFormat)200, HeifImageDecoder.CanDecode,
            HeifImageDecoder.TryReadInfo, HeifImageDecoder.TryDecode, HeifImageDecoder.TryDecodeInto));
        Should.Throw<ArgumentNullException>(() => new ImageDecoderRegistration(ImageFormat.Heif, null!,
            HeifImageDecoder.TryReadInfo, HeifImageDecoder.TryDecode, HeifImageDecoder.TryDecodeInto));
        Should.Throw<ArgumentNullException>(() => new ImageDecoderRegistration(ImageFormat.Heif, HeifImageDecoder.CanDecode,
            HeifImageDecoder.TryReadInfo, HeifImageDecoder.TryDecode, null!));
    }

    /// <summary>Registration while other threads decode: no decode fails or sees a torn registry, and every
    /// distinct codec ends up registered exactly once.</summary>
    [Fact]
    public async Task ConcurrentRegistrationAndDecoding()
    {
        byte[] heif = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        byte[] png = PngWriter.Encode(Enumerable.Range(0, 16 * 16 * 4).Select(i => (byte)i).ToArray(), 16, 16);
        SimdCodecs.Register(HeifImageDecoder.Registration);
        HeifImageDecoder.TryDecode(heif, out var expected).ShouldBeTrue();
        byte[] expectedPixels = expected.Pixels.ToArray();
        int before = SimdCodecs.RegisteredCount;

        const int Codecs = 64;
        var fakes = Enumerable.Range(0, Codecs).Select(i => Fake([(byte)'Z', (byte)i, 0x7F, 0x7E])).ToArray();
        using var stop = new CancellationTokenSource();
        var decoders = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            int decodes = 0;
            while (!stop.IsCancellationRequested || decodes < 2)
            {
                SimdCodecs.TryDecode(heif, out var image).ShouldBeTrue();
                image.Pixels.SequenceEqual(expectedPixels).ShouldBeTrue();
                SimdCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
                info.Format.ShouldBe(ImageFormat.Png);
                decodes++;
            }
        })).ToArray();
        var registrars = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            // Every thread registers every fake (and HEIF again): duplicates must collapse.
            for (int i = 0; i < Codecs; i++)
            {
                SimdCodecs.Register(fakes[(i + t * 17) % Codecs]);
                SimdCodecs.Register(HeifImageDecoder.Registration);
            }
        })).ToArray();

        await Task.WhenAll(registrars);
        await stop.CancelAsync();
        await Task.WhenAll(decoders);
        SimdCodecs.RegisteredCount.ShouldBe(before + Codecs);
        for (int i = 0; i < Codecs; i++)
        {
            byte[] stream = [(byte)'Z', (byte)i, 0x7F, 0x7E, 0, 0];
            SimdCodecs.TryReadInfo(stream, out var info).ShouldBeTrue();
            info.Width.ShouldBe(1);
        }
    }

    /// <summary>A stand-in codec that claims streams starting with <paramref name="magic"/> and reports a 1×1
    /// AVIF; its signature test captures <paramref name="magic"/>, so each call is a distinct codec.</summary>
    private static ImageDecoderRegistration Fake(byte[] magic) =>
        new(ImageFormat.Avif, header => header.StartsWith(magic), FakeInfo, FakeDecode, (_, _, _, _) => false);

    private static bool FakeInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = new ImageInfo(1, 1, 3, SampleFormat.UInt8) { Format = ImageFormat.Avif };
        return true;
    }

    private static bool FakeDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0]);
        return true;
    }
}
