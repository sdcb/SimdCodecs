using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The throwing entry points say why: <see cref="ImageCodecs.Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/>
/// on a corrupt file of each built-in format throws <see cref="InvalidDataException"/> with the format and the codec's
/// own reason (while <see cref="ImageCodecs.TryDecode"/> still returns false), data no codec takes throws
/// <see cref="NotSupportedException"/> naming what <see cref="ImageCodecs.DetectFormat"/> saw, and
/// <see cref="ImageDecoder.Open(ReadOnlyMemory{byte})"/> carries the codec's reason too. The files are foreign
/// (Magick.NET, or the repository's third-party fixtures), cut short or overwritten where the format's structure breaks.
/// </summary>
[Collection(FacadeRegistryCollection.Name)]
public sealed class DecodeErrorTests : IDisposable
{
    private const int W = 48, H = 32;

    private readonly ImageDecoderRegistration[] _builtIn = ImageCodecs.RegistrySnapshot;

    public void Dispose() => ImageCodecs.ExchangeRegistry(_builtIn);

    private static byte[] Magick(MagickFormat format, bool alpha = true)
    {
        var pixels = new byte[W * H * 4];
        new Random(W * H).NextBytes(pixels);
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings(W, H, StorageType.Char, "RGBA"));
        if (!alpha)
            image.Alpha(AlphaOption.Off);
        return image.ToByteArray(format);
    }

    private static byte[] Fixture(params string[] path) =>
        File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, "Fixtures", .. path]));

    private static byte[] Cut(byte[] data, int length) => data.AsSpan(0, length).ToArray();

    /// <summary>
    /// A corrupt file of each format, the name the facade gives the format, and a piece of the codec's reason. The
    /// reasons are the codecs' own messages: an adapter that replaced them with a generic one fails here.
    /// </summary>
    public static TheoryData<string, string, string> Corrupt() => new()
    {
        { "png", "PNG", "extends past end of file" },
        { "jpeg", "JPEG", "SOF" },
        { "tiff", "TIFF", "IFD offset out of bounds" },
        { "jbig2", "JBIG2", "runs past the end" },
        { "jpeg2000", "JPEG 2000", "runs past the end of the codestream" },
        { "webp", "WebP", "RIFF size exceeds" },
        { "gif", "GIF", "ends inside its global colour table" },
        { "bmp", "BMP", "pixel data is truncated" },
        { "ico", "ICO", "pixel data is truncated" },
        { "cur", "CUR", "pixel data is truncated" },
        { "qoi", "QOI", "ends before the last pixel" },
        { "pnm", "Netpbm", "raster is truncated" },
        { "pfm", "PFM", "raster is truncated" },
    };

    private static byte[] CorruptFile(string name) => name switch
    {
        // Cut inside the IDAT chunk: its length runs past the end.
        "png" => Cut(Magick(MagickFormat.Png), 200),
        // Cut inside the frame header.
        "jpeg" => Cut(Magick(MagickFormat.Jpeg, alpha: false), 160),
        // Magick writes the directory after the strips: cut short, the header points past the end.
        "tiff" => Cut(Magick(MagickFormat.Tiff), 2000),
        "jbig2" => Cut(Fixture("jbig2", "sym.jb2"), 40),
        // Cut inside the SIZ segment.
        "jpeg2000" => Cut(Fixture("jpeg2000", "ramp64.j2k"), 30),
        "webp" => Cut(Magick(MagickFormat.WebP), 100),
        "gif" => Cut(Magick(MagickFormat.Gif), 400),
        "bmp" => Cut(Magick(MagickFormat.Bmp), 1000),
        "ico" => Cut(Magick(MagickFormat.Ico), 400),
        // Magick.NET writes a cursor as an icon (directory type 1), so this one is our encoder's.
        "cur" => Cut(Ico.IcoEncoder.EncodeCursor([new Ico.IcoFrame(new byte[W * H * 4], W, H, HotspotX: 1, HotspotY: 2)]), 400),
        "qoi" => Cut(Magick(MagickFormat.Qoi), 300),
        "pnm" => Cut(Magick(MagickFormat.Ppm, alpha: false), 300),
        "pfm" => Cut(Magick(MagickFormat.Pfm, alpha: false), 300),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Corrupt))]
    public void CorruptFile_DecodeThrowsTheCodecsReason(string name, string format, string reason)
    {
        var data = CorruptFile(name);
        var ct = TestContext.Current.CancellationToken;
        ImageCodecs.TryDecode(data, out var image, cancellationToken: ct).ShouldBeFalse(name);
        image.ShouldBeNull();

        var e = Should.Throw<InvalidDataException>(() => ImageCodecs.Decode(data, cancellationToken: ct));
        // The codec's exception, in full, behind the format.
        var inner = e.InnerException.ShouldBeOfType<InvalidDataException>();
        var codecReason = inner.Message.StartsWith(format + ": ", StringComparison.Ordinal) ? inner.Message[(format.Length + 2)..] : inner.Message;
        e.Message.ShouldBe($"The {format} file cannot be decoded: {codecReason}");
        codecReason.Length.ShouldBeGreaterThan(10);
        codecReason.ShouldContain(reason, Case.Insensitive);

        // The registration's Decode is what the facade reports, and its TryDecode agrees with it.
        var entry = ImageCodecs.Find(data).ShouldNotBeNull();
        entry.Decode.ShouldNotBeNull();
        Should.Throw<InvalidDataException>(() => entry.Decode(data, null, ct)).Message.ShouldBe(inner.Message);

        // The one-call pixel form reports the same reason.
        Should.Throw<InvalidDataException>(() => ImageCodecs.Decode(data, PixelFormat.Rgba32, cancellationToken: ct))
            .Message.ShouldBe(e.Message);

        // So does a session: at Open when the frames cannot be listed, otherwise when the frame is read or decoded (a
        // still image's session reads its header only).
        var session = SessionFailure(data, ct);
        session.Message.ShouldBe(e.Message);
        session.InnerException.ShouldBeOfType<InvalidDataException>();
    }

    private static InvalidDataException SessionFailure(byte[] data, CancellationToken ct) =>
        Should.Throw<InvalidDataException>(() =>
        {
            using var session = ImageDecoder.Open(data.AsMemory());
            using var image = session.GetFrame(0).Decode(cancellationToken: ct);
        });

    [Fact]
    public void UnknownData_IsNotSupported()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        var ct = TestContext.Current.CancellationToken;
        Should.Throw<NotSupportedException>(() => ImageCodecs.Decode(garbage, cancellationToken: ct))
            .Message.ShouldBe("The data is not a recognised image format.");
        Should.Throw<NotSupportedException>(() => ImageCodecs.Decode(garbage, PixelFormat.Rgba32, cancellationToken: ct))
            .Message.ShouldBe("The data is not a recognised image format.");
        // A session keeps its documented exception type, with the same message.
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(garbage.AsMemory()))
            .Message.ShouldBe("The data is not a recognised image format.");
    }

    // An ISO-BMFF 'ftyp' box: size, 'ftyp', major brand, minor version, compatible brands.
    private static byte[] Ftyp(params string[] brands)
    {
        var box = new List<byte> { 0, 0, 0, (byte)(16 + 4 * (brands.Length - 1)) };
        box.AddRange("ftyp"u8.ToArray());
        box.AddRange(System.Text.Encoding.ASCII.GetBytes(brands[0]));
        box.AddRange([0, 0, 0, 0]);
        foreach (var brand in brands[1..])
            box.AddRange(System.Text.Encoding.ASCII.GetBytes(brand));
        box.AddRange(new byte[32]);
        return [.. box];
    }

    [Theory]
    [InlineData("avif", "AVIF")]
    [InlineData("jxl", "JPEG XL")]
    [InlineData("psd", "PSD")]
    public void RecognisedButUndecodableFormat_IsNamed(string kind, string name)
    {
        byte[] data = kind switch
        {
            "avif" => Ftyp("avif", "mif1", "miaf"),
            "jxl" => [0xFF, 0x0A, 0xFA, 0x7F, 0x01, 0x90, 0x08, 0x06, 0x01, 0x00, 0x48, 0x00],
            _ => [.. "8BPS"u8, 0, 1, 0, 0, 0, 0, 0, 0, 0, 3],
        };
        Should.Throw<NotSupportedException>(() => ImageCodecs.Decode(data, cancellationToken: TestContext.Current.CancellationToken))
            .Message.ShouldBe($"The data is {name}, which Sdcb.SimdCodecs recognises but does not decode.");
    }

    [Fact]
    public void UnregisteredHeif_NamesThePackage()
    {
        var heic = Ftyp("heic", "mif1", "heic");
        ImageCodecs.DetectFormat(heic).ShouldBe(ImageFormat.Heif);
        var message = Should.Throw<NotSupportedException>(() => ImageCodecs.Decode(heic, cancellationToken: TestContext.Current.CancellationToken)).Message;
        message.ShouldBe("The data is HEIF, which no registered codec decodes: install the Sdcb.SimdCodecs.Heif package and " +
            "register it once at start-up with ImageCodecs.Register(HeifImageDecoder.Registration).");
        Should.Throw<InvalidDataException>(() => ImageDecoder.Open(heic.AsMemory())).Message.ShouldBe(message);
    }

    [Fact]
    public void RegisteredHeif_CorruptFileGivesTheReason()
    {
        ImageCodecs.Register(HeifImageDecoder.Registration);
        // A HEIF brand and nothing after the 'ftyp' box: no 'meta' box to find the primary image in.
        var heic = Ftyp("heic", "mif1", "heic");
        ImageCodecs.TryDecode(heic, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
        var e = Should.Throw<InvalidDataException>(() => ImageCodecs.Decode(heic, cancellationToken: TestContext.Current.CancellationToken));
        e.Message.ShouldStartWith("The HEIF file cannot be decoded: ");
        e.InnerException.ShouldBeOfType<InvalidDataException>();
        e.Message.ShouldBe($"The HEIF file cannot be decoded: {e.InnerException!.Message}");
    }

    [Fact]
    public void ThirdPartyRegistrationWithoutDecode_FallsBackToTryDecode()
    {
        // A registration built before IImageDecoder.Decode existed: no Decode delegate.
        var png = Png.PngImageDecoder.Registration;
        var legacy = new ImageDecoderRegistration
        {
            Format = ImageFormat.Png,
            CanDecode = png.CanDecode,
            TryReadInfo = png.TryReadInfo,
            TryGetClosestSize = png.TryGetClosestSize,
            TryDecode = png.TryDecode,
            TryDecodeInto = png.TryDecodeInto,
            TryReadMetadata = png.TryReadMetadata,
            TryDecodeThumbnail = png.TryDecodeThumbnail,
        };
        legacy.Decode.ShouldBeNull();
        ImageCodecs.ExchangeRegistry([legacy]);

        var ct = TestContext.Current.CancellationToken;
        var good = Magick(MagickFormat.Png);
        using (var image = ImageCodecs.Decode(good, cancellationToken: ct))
            image.Width.ShouldBe(W);
        Should.Throw<InvalidDataException>(() => ImageCodecs.Decode(CorruptFile("png"), cancellationToken: ct))
            .Message.ShouldBe("The PNG file cannot be decoded.");
    }

    [Fact]
    public void Register_TellsCodecsApartByTheirDecodeDelegate()
    {
        // The built-in PNG entry's delegates, without its Decode: a different codec as far as Register can tell.
        static ImageDecoderRegistration WithoutDecode()
        {
            var png = Png.PngImageDecoder.Registration;
            return new ImageDecoderRegistration
            {
                Format = ImageFormat.Png,
                CanDecode = png.CanDecode,
                TryReadInfo = png.TryReadInfo,
                TryGetClosestSize = png.TryGetClosestSize,
                TryDecode = png.TryDecode,
                TryDecodeInto = png.TryDecodeInto,
                TryReadMetadata = png.TryReadMetadata,
                TryDecodeThumbnail = png.TryDecodeThumbnail,
                TryOpenFrames = png.TryOpenFrames,
            };
        }

        var count = ImageCodecs.RegisteredCount;
        ImageCodecs.Register(WithoutDecode());
        ImageCodecs.RegisteredCount.ShouldBe(count + 1);
        ImageCodecs.Register(WithoutDecode()); // the same delegates again: the same codec
        ImageCodecs.RegisteredCount.ShouldBe(count + 1);
        ImageCodecs.Register(Png.PngImageDecoder.Registration); // built in
        ImageCodecs.RegisteredCount.ShouldBe(count + 1);
    }

    [Fact]
    public void FloatImage_ToIntegerFormat_IsNotSupported()
    {
        var pfm = Magick(MagickFormat.Pfm, alpha: false);
        var ct = TestContext.Current.CancellationToken;
        using (var image = ImageCodecs.Decode(pfm, cancellationToken: ct))
            image.SampleFormat.ShouldBe(SampleFormat.Float32);
        Should.Throw<NotSupportedException>(() => ImageCodecs.Decode(pfm, PixelFormat.Rgba32, cancellationToken: ct))
            .Message.ShouldStartWith("The PFM image has float samples, which have no Rgba32 form");
    }

    [Fact]
    public void RegionOutsideTheImage_IsAnArgumentException()
    {
        var png = Magick(MagickFormat.Png);
        var outside = new DecodeOptions { Region = new PixelRect(40, 0, 20, 10) };
        var ct = TestContext.Current.CancellationToken;
        Should.Throw<ArgumentException>(() => ImageCodecs.Decode(png, outside, ct));
        Should.Throw<ArgumentException>(() => ImageCodecs.Decode(png, PixelFormat.Rgba32, outside, ct));
    }

    [Fact]
    public void CancelledToken_Throws()
    {
        var jpeg = Magick(MagickFormat.Jpeg, alpha: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Should.Throw<OperationCanceledException>(() => ImageCodecs.Decode(jpeg, cancellationToken: cts.Token));
        Should.Throw<OperationCanceledException>(() => ImageCodecs.Decode(jpeg, PixelFormat.Bgr24, cancellationToken: cts.Token));
    }
}
