using System.Buffers.Binary;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The <see cref="HeifImageDecoder"/> adapter against the 1.x <see cref="IImageDecoder"/> contract: header info
/// agrees with the decode, every <see cref="ImagePixelFormat"/> at every stride is exactly
/// <see cref="IDecodedImage.ToRgba8"/> through <see cref="PixelConverter.ConvertRow"/> (Gray8 is the stored luma,
/// pinned by its own golden), malformed or unsupported input and an HDR transfer function make the <c>Try</c>
/// methods return false.
/// </summary>
/// <remarks>
/// <c>Fixtures/heif/heif-output-golden.tsv</c> pins the 8-bit outputs: the RGBA8 of <see cref="HeifDecoder.Decode"/>
/// (frozen <c>HeifColor</c> arithmetic plus the clap / irot / imir transforms) and the Gray8 of
/// <see cref="HeifImageDecoder.TryDecodeInto"/>. The RGBA rows were recorded from the M1 code before the M2 adapter
/// touched the composition step, so they prove that step unchanged. Record a missing row with
/// <c>REGEN_HEIF_OUTPUT=1</c>; a golden moves only for a reviewed, intentional output change (AGENTS.md).
/// </remarks>
public sealed class HeifImageDecoderTests
{
    private const string GoldenFileName = "heif-output-golden.tsv";

    private static readonly string[] DecodableFixtures =
    [
        "hevc-main-3072x4096-grid.heic",
        "hevc-main10-3072x4096-grid.heic",
        "hevc-main-8192x6144-grid.heic",
        "synth-photo-fuji-1600x900-main8.heic",
        "synth-photo-mangrove-1599x1199-main8.heic",
        "synth-screen-testsrc2-1280x720-main8.heic",
    ];

    /// <summary>The cheaper fixtures for the format × stride matrix: the three single-item files and one 10-bit grid.</summary>
    private static readonly string[] LayoutFixtures =
    [
        "synth-photo-fuji-1600x900-main8.heic",
        "synth-photo-mangrove-1599x1199-main8.heic",
        "synth-screen-testsrc2-1280x720-main8.heic",
        "hevc-main10-3072x4096-grid.heic",
    ];

    private static readonly string[] RejectedFixtures =
    [
        "synth-photo-fuji-1600x900-main10intra.heic",
        "synth-photo-fuji-1600x900-main12.heic",
        "synth-screen-testsrc2-1280x720-444.heic",
        "synth-anim-testsrc2-640x360-8frames.heics",
    ];

    private const string PqFixture = "synth-screen-testsrc2-640x360-pq-vui.heic";
    private const string HlgFixture = "synth-screen-testsrc2-640x360-hlg-nclx.heic";

    private static readonly ImagePixelFormat[] AllFormats =
        [ImagePixelFormat.Rgba32, ImagePixelFormat.Bgra32, ImagePixelFormat.Rgb24, ImagePixelFormat.Bgr24, ImagePixelFormat.Gray8];

    public static IEnumerable<object[]> Decodable() => DecodableFixtures.Select(static f => new object[] { f });

    public static IEnumerable<object[]> Rejected() => RejectedFixtures.Select(static f => new object[] { f });

    public static IEnumerable<object[]> Layouts() =>
        from f in LayoutFixtures
        from format in AllFormats
        from padded in new[] { false, true }
        select new object[] { f, format, padded };

    // ───────────────────────────── goldens ─────────────────────────────

    [Theory]
    [MemberData(nameof(Decodable))]
    public void Rgba_MatchesGolden(string fixture)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        using var image = HeifDecoder.Decode(data);
        CheckGolden(fixture, "rgba", image.Width, image.Height, image.Rgba);
    }

    [Theory]
    [MemberData(nameof(Decodable))]
    public void Gray8_MatchesGolden(string fixture)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        HeifImageDecoder.TryReadInfo(data, out var info).ShouldBeTrue();
        var gray = new byte[info.Width * info.Height];
        HeifImageDecoder.TryDecodeInto(data, gray, 0, ImagePixelFormat.Gray8).ShouldBeTrue();
        CheckGolden(fixture, "gray8", info.Width, info.Height, gray);
    }

    /// <summary>
    /// Gray8 is the stored luma, not the luma of the RGB, so it may differ by a code value or two (chroma
    /// replication, the source matrix's weights); a wrong offset or scale would move every pixel.
    /// </summary>
    [Theory]
    [MemberData(nameof(Decodable))]
    public void Gray8_IsCloseToTheLumaOfTheRgb(string fixture)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        HeifImageDecoder.TryDecode(data, out var image).ShouldBeTrue();
        int w = image.Width, h = image.Height;
        var expected = new byte[w * h];
        var rgba = image.ToRgba8();
        for (int y = 0; y < h; y++)
        {
            PixelConverter.ConvertRow(rgba.AsSpan(y * w * 4, w * 4), 4, SampleFormat.UInt8, expected.AsSpan(y * w, w), ImagePixelFormat.Gray8, w);
        }

        var gray = new byte[w * h];
        HeifImageDecoder.TryDecodeInto(data, gray, 0, ImagePixelFormat.Gray8).ShouldBeTrue();
        long sum = 0;
        for (int i = 0; i < gray.Length; i++) sum += Math.Abs(gray[i] - expected[i]);
        ((double)sum / gray.Length).ShouldBeLessThan(2.0, $"{fixture}: mean |Gray8 - luma(RGB)|");
    }

    // ───────────────────────────── contract ─────────────────────────────

    [Theory]
    [MemberData(nameof(Decodable))]
    public void ReadInfo_DescribesTheDecodedImage(string fixture)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        HeifImageDecoder.CanDecode(data).ShouldBeTrue();
        HeifImageDecoder.TryReadInfo(data, out var info).ShouldBeTrue();
        HeifImageDecoder.TryDecode(data, out var image).ShouldBeTrue();
        using var _ = image as IDisposable;

        info.Format.ShouldBe(ImageFormat.Heif);
        info.Channels.ShouldBe(3);
        info.SampleFormat.ShouldBe(SampleFormat.UInt8);
        info.BitDepth.ShouldBe(8);
        info.Orientation.ShouldBe(ImageOrientation.Normal);
        info.FrameCount.ShouldBe(1);
        (image.Width, image.Height, image.Channels, image.SampleFormat).ShouldBe((info.Width, info.Height, 3, SampleFormat.UInt8));

        var native = HeifDecoder.ReadInfo(data);
        (native.Width, native.Height).ShouldBe((info.Width, info.Height));
        image.IccProfile.ToArray().ShouldBe(native.IccProfile ?? []);
        image.ColorEncoding.FullRange.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void TryDecodeInto_IsToRgba8ThroughConvertRow(string fixture, ImagePixelFormat format, bool padded)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        HeifImageDecoder.TryDecode(data, out var image).ShouldBeTrue();
        int w = image.Width, h = image.Height, bpp = PixelConverter.BytesPerPixel(format);
        int stride = padded ? w * bpp + 13 : 0;
        int rowStride = padded ? stride : w * bpp;
        const byte Sentinel = 0xA5;
        var destination = new byte[rowStride * (h - 1) + w * bpp];
        destination.AsSpan().Fill(Sentinel);
        HeifImageDecoder.TryDecodeInto(data, destination, stride, format).ShouldBeTrue();

        byte[] rgba = image.ToRgba8();
        var expectedRow = new byte[w * bpp];
        var gray = format == ImagePixelFormat.Gray8 ? GrayGolden(data, w, h) : null;
        for (int y = 0; y < h; y++)
        {
            if (gray is null)
            {
                PixelConverter.ConvertRow(rgba.AsSpan(y * w * 4, w * 4), 4, SampleFormat.UInt8, expectedRow, format, w);
            }
            else
            {
                gray.AsSpan(y * w, w).CopyTo(expectedRow);
            }

            if (!destination.AsSpan(y * rowStride, w * bpp).SequenceEqual(expectedRow))
            {
                Assert.Fail($"{fixture} {format} stride {stride}: row {y} differs.");
            }

            if (padded && y < h - 1)
            {
                destination.AsSpan(y * rowStride + w * bpp, rowStride - w * bpp).ContainsAnyExcept(Sentinel)
                    .ShouldBeFalse($"row {y}: the padding between rows was written");
            }
        }
    }

    [Fact]
    public void TryDecodeInto_RefusesADestinationThatDoesNotFit()
    {
        byte[] data = HeifTests.ReadFixture("synth-screen-testsrc2-1280x720-main8.heic");
        HeifImageDecoder.TryReadInfo(data, out var info).ShouldBeTrue();
        foreach (var format in AllFormats)
        {
            int bpp = PixelConverter.BytesPerPixel(format);
            int exact = info.Width * info.Height * bpp;
            HeifImageDecoder.TryDecodeInto(data, new byte[exact - 1], 0, format).ShouldBeFalse($"{format}: one byte short");
            HeifImageDecoder.TryDecodeInto(data, new byte[exact], info.Width * bpp - 1, format).ShouldBeFalse($"{format}: stride below a row");
            HeifImageDecoder.TryDecodeInto(data, new byte[exact], -1, format).ShouldBeFalse($"{format}: negative stride");
            HeifImageDecoder.TryDecodeInto(data, new byte[exact * 2], info.Width * bpp * 2, format).ShouldBeTrue($"{format}: doubled stride");
        }
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void UnsupportedFiles_AreRecognisedButNotDecoded(string fixture)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        HeifImageDecoder.CanDecode(data).ShouldBeTrue();
        HeifImageDecoder.TryDecode(data, out var image).ShouldBeFalse();
        image.ShouldBeNull();
        HeifImageDecoder.TryDecodeInto(data, new byte[64 << 20], 0, ImagePixelFormat.Rgba32).ShouldBeFalse();
        HeifImageDecoder.TryDecodeInto(data, new byte[16 << 20], 0, ImagePixelFormat.Gray8).ShouldBeFalse();
        if (fixture.EndsWith(".heics", StringComparison.Ordinal))
        {
            HeifImageDecoder.TryReadInfo(data, out _).ShouldBeFalse();
        }
    }

    [Fact]
    public void TruncatedAndEmptyInput_ReturnFalse()
    {
        byte[] data = HeifTests.ReadFixture("synth-photo-fuji-1600x900-main8.heic");
        foreach (int length in new[] { 0, 8, 16, 64, 300, data.Length / 2, data.Length - 1 })
        {
            var slice = data.AsSpan(0, length);
            HeifImageDecoder.TryDecode(slice, out _).ShouldBeFalse($"length {length}");
            HeifImageDecoder.TryDecodeInto(slice, new byte[1600 * 900 * 4], 0, ImagePixelFormat.Rgba32).ShouldBeFalse($"length {length}");
        }
    }

    // ───────────────────────────── colour ─────────────────────────────

    [Theory]
    [InlineData(PqFixture, TransferFunction.Pq)]
    [InlineData(HlgFixture, TransferFunction.Hlg)]
    public void HdrTransfer_DecodesButHasNoDisplayProjection(string fixture, TransferFunction transfer)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        HeifImageDecoder.TryReadInfo(data, out var info).ShouldBeTrue();
        (info.Width, info.Height).ShouldBe((640, 360));
        HeifImageDecoder.TryDecode(data, out var image).ShouldBeTrue();
        image.ColorEncoding.Transfer.ShouldBe(transfer);
        image.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT2020);
        image.ColorEncoding.FullRange.ShouldBeTrue("limited-range Y'CbCr is expanded to full-range RGB");
        foreach (var format in AllFormats)
        {
            HeifImageDecoder.TryDecodeInto(data, new byte[640 * 360 * 4], 0, format).ShouldBeFalse($"{format}");
        }
    }

    /// <summary>
    /// Which colour signalling wins: the phone photos carry an ICC profile and a VUI that says BT.601 primaries —
    /// the <c>colr</c> box overrides the VUI and the encoding is not derived from the profile, so assumed sRGB; the
    /// synthetic files have no <c>colr</c> box and a VUI of BT.709 primaries with the sRGB transfer.
    /// </summary>
    [Theory]
    [InlineData("hevc-main-3072x4096-grid.heic", ColorPrimaries.BT709, TransferFunction.Srgb)]
    [InlineData("synth-photo-fuji-1600x900-main8.heic", ColorPrimaries.BT709, TransferFunction.Srgb)]
    public void ColourSignalling_Precedence(string fixture, ColorPrimaries primaries, TransferFunction transfer)
    {
        byte[] data = HeifTests.ReadFixture(fixture);
        HeifImageDecoder.TryDecode(data, out var image).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(new ColorEncoding { Primaries = primaries, Transfer = transfer, FullRange = true });
        if (image.IccProfile.Length > 0) image.ColorEncoding.ShouldBeSameAs(ColorEncoding.AssumedSrgb);
    }

    /// <summary>Rewrites the nclx box in memory: each code point is reported as is, the transfer decides the refusal.</summary>
    [Theory]
    [InlineData(12, 13, true)]
    [InlineData(9, 16, false)]
    [InlineData(9, 18, false)]
    [InlineData(1, 8, true)]
    [InlineData(300, 300, true)] // beyond H.273's 8-bit code points: unspecified
    public void NclxCodePoints_MapOntoColorEncoding(int primaries, int transfer, bool displayable)
    {
        byte[] data = HeifTests.ReadFixture(HlgFixture);
        int at = data.AsSpan().IndexOf("colrnclx"u8);
        at.ShouldBeGreaterThan(0, "the fixture has an nclx box");
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at + 8), (ushort)primaries);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at + 10), (ushort)transfer);

        HeifImageDecoder.TryDecode(data, out var image).ShouldBeTrue();
        image.ColorEncoding.Primaries.ShouldBe((ColorPrimaries)(primaries > 255 ? 2 : primaries));
        image.ColorEncoding.Transfer.ShouldBe((TransferFunction)(transfer > 255 ? 2 : transfer));
        HeifImageDecoder.TryDecodeInto(data, new byte[640 * 360 * 4], 0, ImagePixelFormat.Rgba32).ShouldBe(displayable);
    }

    // ───────────────────────────── sniffing ─────────────────────────────

    public static IEnumerable<object[]> FtypHeaders()
    {
        static byte[] Ftyp(string major, params string[] compatible)
        {
            var box = new byte[16 + 4 * compatible.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            "ftyp"u8.CopyTo(box.AsSpan(4));
            System.Text.Encoding.ASCII.GetBytes(major).CopyTo(box, 8);
            for (int i = 0; i < compatible.Length; i++) System.Text.Encoding.ASCII.GetBytes(compatible[i]).CopyTo(box, 16 + 4 * i);
            return [.. box, .. new byte[32]];
        }

        yield return [Ftyp("heic", "mif1", "heic")];
        yield return [Ftyp("mif1", "heic")];
        yield return [Ftyp("heix", "mif1")];
        yield return [Ftyp("msf1", "hevc")];
        yield return [Ftyp("hevc", "msf1")];
        yield return [Ftyp("avif", "mif1", "miaf")];
        yield return [Ftyp("mif1", "avif")];
        yield return [Ftyp("mif1", "miaf", "avis")];
        yield return [Ftyp("isom", "mp41")];
        yield return [Ftyp("mp42")];
        yield return [Ftyp("crx ", "mif1")];
        yield return ["\0\0\0\x0cftypheic"u8.ToArray()];
    }

    /// <summary>The adapter's sniff and the facade's <c>DetectFormat</c> are separate code (the facade cannot
    /// reference this package); they must reach the same verdict.</summary>
    [Theory]
    [MemberData(nameof(FtypHeaders))]
    public void CanDecode_AgreesWithDetectFormat(byte[] header)
    {
        bool heif = SimdCodecs.DetectFormat(header) == ImageFormat.Heif;
        HeifImageDecoder.CanDecode(header).ShouldBe(heif);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static byte[] GrayGolden(byte[] data, int width, int height)
    {
        var gray = new byte[width * height];
        HeifImageDecoder.TryDecodeInto(data, gray, 0, ImagePixelFormat.Gray8).ShouldBeTrue();
        return gray;
    }

    private static string Digest(int width, int height, ReadOnlySpan<byte> pixels)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(width));
        hash.AppendData(BitConverter.GetBytes(height));
        hash.AppendData(pixels);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static readonly Lock GoldenLock = new();

    private static void CheckGolden(string fixture, string kind, int width, int height, ReadOnlySpan<byte> pixels)
    {
        string digest = Digest(width, height, pixels);
        string key = $"{fixture}\t{kind}";
        string path = Path.Combine(HeifTests.RepoFixtureDirectory(), GoldenFileName);
        lock (GoldenLock)
        {
            var golden = LoadGolden(path);
            if (!golden.ContainsKey(key) && Environment.GetEnvironmentVariable("REGEN_HEIF_OUTPUT") == "1")
            {
                // Only a missing row is recorded: an existing one moves by hand, with the diff reviewed.
                golden[key] = $"{width}\t{height}\t{digest}";
                SaveGolden(path, golden);
            }

            golden.TryGetValue(key, out var expected).ShouldBeTrue(
                $"{GoldenFileName} has no {kind} row for {fixture}; record it with REGEN_HEIF_OUTPUT=1.");
            expected.ShouldBe($"{width}\t{height}\t{digest}", $"{fixture} {kind}");
        }
    }

    private static Dictionary<string, string> LoadGolden(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return result;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 5) continue;
            result[$"{parts[0]}\t{parts[1]}"] = $"{parts[2]}\t{parts[3]}\t{parts[4]}";
        }

        return result;
    }

    private static void SaveGolden(string path, Dictionary<string, string> golden)
    {
        using var writer = new StreamWriter(path) { NewLine = "\n" };
        writer.WriteLine("# HEIF 8-bit output golden: fixture, output, width, height, SHA-256 of (width, height as int32 LE, pixels).");
        writer.WriteLine("# rgba: HeifDecoder.Decode (frozen HeifColor + clap/irot/imir). gray8: HeifImageDecoder.TryDecodeInto Gray8 (HeifColor.ToGray).");
        foreach (var (key, value) in golden.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            writer.WriteLine($"{key}\t{value}");
        }
    }
}
