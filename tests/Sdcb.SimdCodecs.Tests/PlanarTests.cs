using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The planar entry points of the facade: <see cref="SimdCodecs.TryReadPlanarInfo"/> and
/// <see cref="SimdCodecs.TryDecodePlanes"/> dispatch to a codec that offers its coded Y / Cb / Cr planes (HEIF, once registered),
/// and say no for every codec that does not, and for a HEIF image with a transformative property.
/// </summary>
[Collection(FacadeRegistryCollection.Name)]
public sealed class PlanarTests : IDisposable
{
    private readonly ImageDecoderRegistration[] _builtIn = SimdCodecs.RegistrySnapshot;

    public void Dispose() => SimdCodecs.ExchangeRegistry(_builtIn);

    // Single-item or grid images without clap / irot / imir, small enough to decode in a moment.
    public static TheoryData<string> PlanarFixtures() =>
    [
        "synth-photo-fuji-1600x900-main8.heic",
        "synth-screen-testsrc2-1280x720-main8.heic",
        "hevc-main10-3072x4096-grid.heic",
    ];

    [Theory]
    [MemberData(nameof(PlanarFixtures))]
    public void HeifPlanesAreTheDecodersCanvasPlanes(string fixture)
    {
        var data = HeifTests.ReadFixture(fixture);
        SimdCodecs.Register(HeifImageDecoder.Registration);

        var (y, cb, cr, width, height, bitDepth) = HeifDecoder.DecodeYuv(data);

        SimdCodecs.TryReadPlanarInfo(data, out var info).ShouldBeTrue(fixture);
        (info.Width, info.Height).ShouldBe((width, height), fixture);
        (info.ChromaSubsamplingX, info.ChromaSubsamplingY).ShouldBe((2, 2), fixture);
        info.BitDepth.ShouldBe(bitDepth, fixture);
        info.ChromaWidth.ShouldBe((width + 1) / 2);
        info.ChromaHeight.ShouldBe((height + 1) / 2);
        info.LumaLength.ShouldBe(width * height);
        info.ChromaLength.ShouldBe(info.ChromaWidth * info.ChromaHeight);
        Enum.IsDefined(info.ChromaLocation).ShouldBeTrue(fixture);
        // Deterministic: the same answer twice.
        SimdCodecs.TryReadPlanarInfo(data, out var again).ShouldBeTrue();
        again.ShouldBe(info);
        // The adapter's own answer is the facade's.
        HeifImageDecoder.TryReadPlanarInfo(data, out var direct).ShouldBeTrue();
        direct.ShouldBe(info);

        var oy = new ushort[info.LumaLength];
        var ocb = new ushort[info.ChromaLength];
        var ocr = new ushort[info.ChromaLength];
        SimdCodecs.TryDecodePlanes(data, oy, ocb, ocr, TestContext.Current.CancellationToken).ShouldBeTrue(fixture);
        oy.ShouldBe(y.AsSpan(0, info.LumaLength).ToArray(), fixture);
        ocb.ShouldBe(cb.AsSpan(0, info.ChromaLength).ToArray(), fixture);
        ocr.ShouldBe(cr.AsSpan(0, info.ChromaLength).ToArray(), fixture);

        // Samples are within the coded bit depth.
        var max = (1 << bitDepth) - 1;
        oy.Max().ShouldBeLessThanOrEqualTo((ushort)max);
        ocb.Max().ShouldBeLessThanOrEqualTo((ushort)max);
        ocr.Max().ShouldBeLessThanOrEqualTo((ushort)max);

        // Larger buffers are fine and only the plane lengths are written.
        var by = new ushort[info.LumaLength + 11];
        var bcb = new ushort[info.ChromaLength + 7];
        var bcr = new ushort[info.ChromaLength + 3];
        Array.Fill(by, (ushort)0xBEEF);
        Array.Fill(bcb, (ushort)0xBEEF);
        Array.Fill(bcr, (ushort)0xBEEF);
        SimdCodecs.TryDecodePlanes(data, by, bcb, bcr, TestContext.Current.CancellationToken).ShouldBeTrue();
        by.AsSpan(0, info.LumaLength).ToArray().ShouldBe(oy);
        by.AsSpan(info.LumaLength).ToArray().ShouldAllBe(v => v == 0xBEEF);
        bcb.AsSpan(info.ChromaLength).ToArray().ShouldAllBe(v => v == 0xBEEF);
        bcr.AsSpan(info.ChromaLength).ToArray().ShouldAllBe(v => v == 0xBEEF);
    }

    [Fact]
    public void ABufferThatIsTooSmallIsRefused()
    {
        var data = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        SimdCodecs.Register(HeifImageDecoder.Registration);
        SimdCodecs.TryReadPlanarInfo(data, out var info).ShouldBeTrue();
        var token = TestContext.Current.CancellationToken;

        SimdCodecs.TryDecodePlanes(data, new ushort[info.LumaLength - 1], new ushort[info.ChromaLength], new ushort[info.ChromaLength], token).ShouldBeFalse();
        SimdCodecs.TryDecodePlanes(data, new ushort[info.LumaLength], new ushort[info.ChromaLength - 1], new ushort[info.ChromaLength], token).ShouldBeFalse();
        SimdCodecs.TryDecodePlanes(data, new ushort[info.LumaLength], new ushort[info.ChromaLength], new ushort[info.ChromaLength - 1], token).ShouldBeFalse();
        SimdCodecs.TryDecodePlanes(data, [], [], [], token).ShouldBeFalse();
    }

    [Fact]
    public void ACancelledTokenStopsPlanarDecoding()
    {
        var data = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        SimdCodecs.Register(HeifImageDecoder.Registration);
        SimdCodecs.TryReadPlanarInfo(data, out var info).ShouldBeTrue();
        Should.Throw<OperationCanceledException>(() => SimdCodecs.TryDecodePlanes(data,
            new ushort[info.LumaLength], new ushort[info.ChromaLength], new ushort[info.ChromaLength], new CancellationToken(canceled: true)));
    }

    [Fact]
    public void AHeifImageWithATransformHasNoPlanes()
    {
        // This fixture has a clap clean aperture: the planes would not be the displayed image.
        var data = HeifTests.ReadFixture("synth-photo-mangrove-1599x1199-main8.heic");
        SimdCodecs.Register(HeifImageDecoder.Registration);

        SimdCodecs.TryReadPlanarInfo(data, out var info).ShouldBeFalse();
        info.ShouldBe(default);
        SimdCodecs.TryDecodePlanes(data, new ushort[1599 * 1199], new ushort[800 * 600], new ushort[800 * 600], TestContext.Current.CancellationToken).ShouldBeFalse();
        // It still decodes as an image.
        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Dispose();
    }

    [Fact]
    public void WithoutRegistrationHeifHasNoPlanes()
    {
        var data = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        SimdCodecs.TryReadPlanarInfo(data, out var info).ShouldBeFalse();
        info.ShouldBe(default);
        SimdCodecs.TryDecodePlanes(data, new ushort[1280 * 720], new ushort[640 * 360], new ushort[640 * 360], TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    private static byte[] Rgba()
    {
        var pixels = new byte[32 * 32 * 4];
        new Random(5).NextBytes(pixels);
        return pixels;
    }

    public static TheoryData<string> NonPlanar() => ["png", "jpeg", "qoi", "bmp", "webp", "gif", "pnm", "garbage"];

    [Theory]
    [MemberData(nameof(NonPlanar))]
    public void ACodecWithoutPlanesSaysNo(string name)
    {
        var rgba = Rgba();
        var data = name switch
        {
            "png" => Png.PngWriter.Encode(rgba, 32, 32),
            "jpeg" => Jpeg.JpegEncoder.Encode(rgba.Where((_, i) => i % 4 != 3).ToArray(), 32, 32, 3),
            "qoi" => Qoi.QoiEncoder.Encode(rgba, 32, 32, PixelFormat.Rgba32),
            "bmp" => Bmp.BmpEncoder.Encode(rgba, 32, 32, PixelFormat.Rgba32),
            "webp" => Webp.WebpEncoder.EncodeLossless(rgba, 32, 32, PixelFormat.Rgba32),
            "gif" => Gif.GifEncoder.Encode(rgba, 32, 32, 4),
            "pnm" => Sdcb.SimdCodecs.Pnm.PnmEncoder.Encode(rgba.Where((_, i) => i % 4 != 3).ToArray(), 32, 32, 3),
            _ => new byte[100],
        };
        if (name != "garbage")
            SimdCodecs.CanDecode(data).ShouldBeTrue(name);

        SimdCodecs.TryReadPlanarInfo(data, out var info).ShouldBeFalse(name);
        info.ShouldBe(default, name);
        SimdCodecs.TryDecodePlanes(data, new ushort[32 * 32], new ushort[16 * 16], new ushort[16 * 16], TestContext.Current.CancellationToken).ShouldBeFalse(name);
        SimdCodecs.TryDecodePlanes(data, new ushort[1 << 20], new ushort[1 << 20], new ushort[1 << 20], TestContext.Current.CancellationToken).ShouldBeFalse(name);
    }
}
