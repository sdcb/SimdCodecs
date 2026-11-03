using System.Security.Cryptography;
using ImageMagick;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// HEIF / HEIC decode tests. The output contract (AGENTS.md) is byte-exact: the Y, Cb and Cr planes of every
/// coded image equal a pinned reference decoder's samples, and the composed primary image equals the reference
/// decode of the whole file. The reference here is <b>ffmpeg 8.1.2</b>, run as a black box
/// (<see cref="HeifOracle"/>); with the tool present the test compares samples live, and without it compares
/// SHA-256 digests of those samples against the committed goldens in <c>Fixtures/heif/*-golden.tsv</c>.
/// </summary>
/// <remarks>
/// The fixtures are CC0 photographs fetched by <c>Oracle/heif/fetch.sh</c> and never committed. The three phone
/// images are Realme GT5 Pro output: 48-tile Main, 48-tile Main 10 and 192-tile 50 MP Main, all 512×512 tiles.
/// The three synthetic images (from <c>generate-synth.sh</c>, x265 via libheif) are single-item Main Still
/// Picture files that exercise features the phone encoder never emits — WPP, sign data hiding, strong intra
/// smoothing, AMP — and, for the mangrove photograph, a <c>clap</c> clean aperture and the edge-dimension case.
/// </remarks>
public sealed class HeifTests
{
    private const string GoldenFileName = "heif-golden.tsv";

    /// <summary>Fixtures that M1 must decode; every one is compared sample for sample.</summary>
    private static readonly string[] DecodableFixtures =
    [
        "hevc-main-3072x4096-grid.heic",
        "hevc-main10-3072x4096-grid.heic",
        "hevc-main-8192x6144-grid.heic",
        "synth-photo-fuji-1600x900-main8.heic",
        "synth-photo-mangrove-1599x1199-main8.heic",
        "synth-screen-testsrc2-1280x720-main8.heic",
    ];

    /// <summary>Fixtures outside M1: each must fail cleanly, never hang or decode silently.</summary>
    private static readonly string[] RejectedFixtures =
    [
        "synth-photo-fuji-1600x900-main10intra.heic", // RExt Main 10 Intra: profile_idc 4
        "synth-photo-fuji-1600x900-main12.heic",      // Main 12: 12-bit
        "synth-screen-testsrc2-1280x720-444.heic",    // 4:4:4 chroma
        "synth-anim-testsrc2-640x360-8frames.heics",  // image sequence: no 'meta', not a still image
    ];

    public static IEnumerable<object[]> Decodable() => DecodableFixtures.Select(static f => new object[] { f });

    public static IEnumerable<object[]> Rejected() => RejectedFixtures.Select(static f => new object[] { f });

    [Theory]
    [MemberData(nameof(Decodable))]
    public void PrimaryImage_YuvPlanes_AreByteExact(string fixture)
    {
        string? ffmpeg = HeifOracle.Ffmpeg;
        OracleGate.RequireOrSkip(ffmpeg is not null, $"ffmpeg {HeifOracle.PinnedVersion}", HeifOracle.FfmpegReason);
        byte[] data = ReadFixture(fixture);

        // Dimensions first: a wrong size makes every later comparison meaningless.
        var info = HeifDecoder.ReadInfo(data);
        var (y, cb, cr, width, height, bitDepth) = HeifDecoder.DecodeYuv(data);
        info.Width.ShouldBe(DisplayWidth(fixture));
        info.Height.ShouldBe(DisplayHeight(fixture));

        // ffmpeg decodes the whole file and applies the clap crop itself. On the mangrove photograph it crops
        // to 1598×1198 where the clap rectangle is 1599×1199 (a rounding of the half-sample offset), so
        // ComparePlanes compares exactly the 1598×1198 region ffmpeg emits; every other fixture is compared
        // over the whole canvas.
        byte[] oracle = HeifOracle.DecodeToYuv(ffmpeg!, FixturePath(fixture));
        ComparePlanes(oracle, y, cb, cr, width, height, bitDepth, fixture);
    }

    [Theory]
    [MemberData(nameof(Decodable))]
    public void PrimaryImage_Digest_MatchesGolden(string fixture)
    {
        byte[] data = ReadFixture(fixture);
        var (y, cb, cr, width, height, bitDepth) = HeifDecoder.DecodeYuv(data);
        string digest = Digest(y, cb, cr);
        var golden = LoadGolden();

        if (System.Environment.GetEnvironmentVariable("REGEN_HEIF_ORACLE") == "1")
        {
            // Deliberately strict: a golden only changes with a reviewed, intentional output change.
            golden[fixture] = new GoldenEntry(width, height, bitDepth, digest);
            SaveGolden(golden, DecodableFixtures);
            return;
        }
        golden.TryGetValue(fixture, out var expected).ShouldBeTrue(
            $"{GoldenFileName} has no entry for {fixture}; record it with REGEN_HEIF_ORACLE=1 from a passing oracle run.");
        digest.ShouldBe(expected!.Digest, $"{fixture} ({width}×{height}, {bitDepth}-bit)");
        width.ShouldBe(expected.Width);
        height.ShouldBe(expected.Height);
        bitDepth.ShouldBe(expected.BitDepth);
    }

    [Theory]
    [MemberData(nameof(Decodable))]
    public void PrimaryImage_RgbaMatchesMagickWithinTolerance(string fixture)
    {
        // Colour conversion is defined in-repo (HeifColor) while Magick uses libheif's; the two agree within a
        // couple of code values when both are right, and this guards against pinning a systematically wrong
        // conversion. Sample a stride of pixels to keep the comparison cheap on the 50 MP image.
        byte[] data = ReadFixture(fixture);
        HeifImage ours = HeifDecoder.Decode(data);

        using var magick = new MagickImage(FixturePath(fixture));
        magick.Width.ShouldBe((uint)ours.Width);
        magick.Height.ShouldBe((uint)ours.Height);
        using var pixels = magick.GetPixels();
        var input = pixels.ToShortArray(PixelMapping.RGBA);
        input.ShouldNotBeNull();

        var ourPixels = ours.Rgba;
        int w = ours.Width, h = ours.Height;
        int step = Math.Max(1, (w * h) / 200_000);
        int mismatches = 0;
        (int X, int Y, int Ch, int Ours, int Theirs)? first = null;
        for (int i = 0; i < w * h; i += step)
        {
            for (int c = 0; c < 3; c++)
            {
                int theirs = input[i * 4 + c] >> 8; // Q16 → 8-bit
                int mine = ourPixels[i * 4 + c];
                if (Math.Abs(theirs - mine) > 2)
                {
                    mismatches++;
                    first ??= (i % w, i / w, c, mine, theirs);
                }
            }
        }
        mismatches.ShouldBe(0, first is { } f
            ? $"{fixture}: {mismatches} samples differ by more than 2, first channel {f.Ch} at ({f.X},{f.Y}): ours {f.Ours}, Magick {f.Theirs}"
            : "");
    }

    [Theory]
    [MemberData(nameof(Decodable))]
    public void DecodeTo_WritesTheSamePixelsAsDecode(string fixture)
    {
        byte[] data = ReadFixture(fixture);
        using HeifImage image = HeifDecoder.Decode(data);
        var destination = new byte[image.Rgba.Length];
        var (width, height) = HeifDecoder.DecodeTo(data, destination);
        width.ShouldBe(image.Width);
        height.ShouldBe(image.Height);
        destination.AsSpan().SequenceEqual(image.Rgba).ShouldBeTrue();
    }

    [Fact]
    public void DecodeTo_RejectsATooSmallDestination()
    {
        byte[] data = ReadFixture(DecodableFixtures[3]);
        var info = HeifDecoder.ReadInfo(data);
        var tooSmall = new byte[(long)info.Width * info.Height * 4 - 1];
        Should.Throw<ArgumentException>(() => HeifDecoder.DecodeTo(data, tooSmall));
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void UnsupportedFixture_FailsCleanly(string fixture)
    {
        string path = FixturePath(fixture);
        if (!File.Exists(path)) Assert.Skip($"{fixture} is not downloaded; run Oracle/heif/fetch.sh.");
        byte[] data = File.ReadAllBytes(path);

        var error = Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(data));
        error.Message.ShouldNotBeNullOrWhiteSpace();
        Should.Throw<InvalidDataException>(() => HeifDecoder.ReadInfo(data));
    }

    [Fact]
    public void ReadInfo_ReportsTheGridLayoutAndColour()
    {
        byte[] data = ReadFixture("hevc-main-3072x4096-grid.heic");
        var info = HeifDecoder.ReadInfo(data);
        info.ShouldSatisfyAllConditions(
            () => info.Width.ShouldBe(3072),
            () => info.Height.ShouldBe(4096),
            () => info.BitDepth.ShouldBe(8),
            () => info.TileCount.ShouldBe(48),
            () => info.IccProfile.ShouldNotBeNull(),
            () => info.IccProfile!.Length.ShouldBeGreaterThan(100));
    }

    [Fact]
    public void ReadInfo_ReportsTenBit()
    {
        byte[] data = ReadFixture("hevc-main10-3072x4096-grid.heic");
        HeifDecoder.ReadInfo(data).BitDepth.ShouldBe(10);
    }

    [Fact]
    public void ReadInfo_ReportsTheFiftyMegapixelGrid()
    {
        var info = HeifDecoder.ReadInfo(ReadFixture("hevc-main-8192x6144-grid.heic"));
        info.ShouldSatisfyAllConditions(
            () => info.Width.ShouldBe(8192),
            () => info.Height.ShouldBe(6144),
            () => info.TileCount.ShouldBe(192));
    }

    [Fact]
    public void SingleItemImage_ReportsOneTile()
    {
        var info = HeifDecoder.ReadInfo(ReadFixture("synth-photo-fuji-1600x900-main8.heic"));
        info.TileCount.ShouldBe(1);
    }

    [Fact]
    public void Clap_KeepsTheCleanApertureSize()
    {
        // 1600×1200 coded, a 1599×1199 clean aperture at half-sample horizontal offset: one column and one row
        // of edge samples are dropped, from the right and bottom.
        var info = HeifDecoder.ReadInfo(ReadFixture("synth-photo-mangrove-1599x1199-main8.heic"));
        (info.Width, info.Height).ShouldBe((1599, 1199));
        var (_, _, _, width, height, _) = HeifDecoder.DecodeYuv(ReadFixture("synth-photo-mangrove-1599x1199-main8.heic"));
        (width, height).ShouldBe((1600, 1200)); // the canvas before the transform
    }

    [Fact]
    public void AlphaFixture_DecodesTheColourPrimaryAndIgnoresTheAuxiliaryImage()
    {
        // The AuxiliaryImage alpha item ('auxl' from the primary) is out of M1 scope: the colour image decodes and
        // its RGBA has opaque alpha.
        byte[] data = ReadFixture("synth-photo-fuji-1600x900-alpha.heic");
        using var image = HeifDecoder.Decode(data);
        image.Width.ShouldBe(1600);
        image.Height.ShouldBe(900);
        var rgba = image.Rgba;
        for (int i = 3; i < rgba.Length; i += 4) rgba[i].ShouldBe((byte)255);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    internal static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "heif", name);

    internal static byte[] ReadFixture(string name)
    {
        string path = FixturePath(name);
        if (!File.Exists(path)) Assert.Skip($"{name} is not downloaded; run Oracle/heif/fetch.sh.");
        return File.ReadAllBytes(path);
    }

    private static int DisplayWidth(string fixture) => fixture switch
    {
        "synth-photo-mangrove-1599x1199-main8.heic" => 1599,
        "synth-photo-fuji-1600x900-main8.heic" => 1600,
        "synth-screen-testsrc2-1280x720-main8.heic" => 1280,
        _ => fixture.Contains("8192x6144") ? 8192 : 3072,
    };

    private static int DisplayHeight(string fixture) => fixture switch
    {
        "synth-photo-mangrove-1599x1199-main8.heic" => 1199,
        "synth-photo-fuji-1600x900-main8.heic" => 900,
        "synth-screen-testsrc2-1280x720-main8.heic" => 720,
        _ => fixture.Contains("8192x6144") ? 6144 : 4096,
    };

    /// <summary>Compares the composed canvas against ffmpeg's whole-file decode (both are pre-transform).</summary>
    private static void ComparePlanes(byte[] oracle, ushort[] y, ushort[] cb, ushort[] cr, int width, int height, int bitDepth, string fixture)
    {
        var planes = new[] { y, cb, cr };
        int bytesPerSample = bitDepth > 8 ? 2 : 1;
        long offset = 0;
        // ffmpeg output for the mangrove photograph is the clap-cropped display image, so the comparison
        // starts at the clap rectangle; for every other fixture the crop is the whole canvas.
        int x0 = 0, y0 = 0, cropW = width, cropH = height;
        if (fixture.Contains("mangrove"))
        {
            (x0, y0, cropW, cropH) = (0, 0, 1598, 1198);
        }
        for (int c = 0; c < 3; c++)
        {
            int shift = c == 0 ? 0 : 1;
            int planeWidth = c == 0 ? width : (width + 1) / 2;
            int pw = c == 0 ? cropW : (cropW + 1) / 2;
            int ph = c == 0 ? cropH : (cropH + 1) / 2;
            int ox = x0 >> shift, oy = y0 >> shift;
            for (int row = 0; row < ph; row++)
            {
                for (int col = 0; col < pw; col++)
                {
                    int theirs = bytesPerSample == 1 ? oracle[offset] : oracle[offset] | (oracle[offset + 1] << 8);
                    offset += bytesPerSample;
                    int mine = planes[c][(row + oy) * planeWidth + col + ox];
                    if (mine != theirs)
                    {
                        Assert.Fail($"{fixture}: plane {c} differs at ({col},{row}): {mine} vs ffmpeg {theirs}");
                    }
                }
            }
        }
        offset.ShouldBe((long)oracle.Length);
    }

    /// <summary>SHA-256 over the three planes, with each plane's dimensions in the hash so a layout change shows.</summary>
    internal static string Digest(ushort[] y, ushort[] cb, ushort[] cr)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var plane in new[] { y, cb, cr })
        {
            hash.AppendData(BitConverter.GetBytes(plane.Length));
            foreach (var sample in plane) hash.AppendData(BitConverter.GetBytes(sample));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record GoldenEntry(int Width, int Height, int BitDepth, string Digest);

    private static void SaveGolden(Dictionary<string, GoldenEntry> golden, IReadOnlyList<string> order)
    {
        string path = Path.Combine(RepoFixtureDirectory(), GoldenFileName);
        using var writer = new StreamWriter(path);
        writer.WriteLine("# HEIF decode golden: SHA-256 of the Y, Cb, Cr planes (each plane length-prefixed, samples little-endian).");
        writer.WriteLine("# Recorded from a passing ffmpeg " + HeifOracle.PinnedVersion + " oracle run; see HeifTests.Digest.");
        foreach (string name in order)
        {
            if (!golden.TryGetValue(name, out var e)) continue;
            writer.WriteLine($"{name}	{e.Width}	{e.Height}	{e.BitDepth}	{e.Digest}");
        }
    }

    /// <summary>tests/Sdcb.SimdCodecs.Tests/Fixtures/heif in the source tree, located from the test binary.</summary>
    private static string RepoFixtureDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "heif");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("The HEIF fixture directory was not found from the test output directory.");
    }

    private static Dictionary<string, GoldenEntry> LoadGolden()
    {
        var result = new Dictionary<string, GoldenEntry>(StringComparer.Ordinal);
        string path = Path.Combine(RepoFixtureDirectory(), GoldenFileName);
        if (!File.Exists(path)) return result;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 5) continue;
            result[parts[0]] = new GoldenEntry(int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]), parts[4]);
        }
        return result;
    }
}
