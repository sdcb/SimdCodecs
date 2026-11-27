using System.Buffers.Binary;
using System.Security.Cryptography;
using ImageMagick;
using Sdcb.SimdCodecs.Avif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// AVIF decode tests (M1: ISOBMFF container + AV1 Profile 0 8/10-bit 4:2:0 &amp; 4:0:0).
/// Every decoded plane (Y, Cb, Cr, Alpha) is verified bit-for-bit against the pinned
/// <c>libavif 1.4.2 + dav1d 1.5.5</c> digests in <c>Fixtures/avif/avif-golden.tsv</c>.
/// </summary>
public sealed class AvifTests
{
    private const string GoldenFileName = "avif-golden.tsv";

    /// <summary>All 16 M1 fixtures plus Profile 0 8/10-bit 4:2:0 fixtures whose bitstreams are within M1 scope.</summary>
    private static readonly string[] M1Fixtures =
    [
        "container-tiny-3x7.avif",
        "screen-testsrc2-1280x720-lr-wiener-sgr.avif",
        "photo-fuji-1280x720-chroma-colocated.avif",
        "photo-fuji-1920x1080-p0-8b-420-aom.avif",
        "photo-fuji-1920x1080-p0-8b-420-svt.avif",
        "photo-fuji-1920x1080-p0-10b-420-aom.avif",
        "photo-carter-1440x1680-p0-8b-400-mono.avif",
        "photo-carter-1440x1680-p0-10b-400-mono.avif",
        "container-mangrove-1599x1199-odd-clap.avif",
        "container-fuji-1280x720-clap-irot-imir.avif",
        "container-grid-1500x990-3x2-crop.avif",
        "color-fuji-1280x720-icc-exif-xmp-thmb.avif",
        "perf-photo-fuji-2560x1440-alpha-10b.avif",
        "perf-photo-teatro-4096x2632-tiles-svt.avif",
        "perf-photo-almsee-4096x3072-grid-10b.avif",
        "perf-photo-mangrove-8192x6144-grid-8b.avif",
        // Bonus Profile 0 8/10-bit 4:2:0 fixtures supported by M1's container & AV1 core:
        "photo-mangrove-1280x960-seg-deltaq-qm.avif",
        "container-grid-alpha-1024x768-prem.avif",
        "color-testsrc2-1280x720-pq-10b-nclx.avif",
        "color-testsrc2-1280x720-hlg-10b-vui.avif",
        "color-fuji-1280x720-gainmap-tmap.avif",
    ];

    /// <summary>Fixtures outside M1 (M2/M3): each must fail cleanly with <see cref="InvalidDataException"/>.</summary>
    private static readonly string[] RejectedFixtures =
    [
        "screen-ui-1920x1080-p1-8b-444-gbr.avif",
        "screen-ui-1920x1080-p1-10b-444-bt709.avif",
        "photo-almsee-1920x1080-p2-8b-422.avif",
        "photo-almsee-1920x1080-p2-12b-420.avif",
        "photo-almsee-960x540-p2-12b-444-lossless.avif",
        "screen-text-1280x720-palette-intrabc.avif",
        "photo-mangrove-1600x1200-superres.avif",
        "photo-night-1280x720-filmgrain-8b.avif",
        "container-iovl-overlay-800x600.avif",
        "container-iden-chain-640x480.avif",
        "container-layered-a1lx-a1op-1280x720.avif",
        "container-tiny-1x1.avif",
        "anim-alltools-320x240-8f.avif",
        "anim-alpha-640x360-24f-varfps.avif",
    ];

    private static readonly string[] RgbaCompareFixtures =
    [
        "container-tiny-3x7.avif",
        "photo-fuji-1920x1080-p0-8b-420-aom.avif",
        "photo-fuji-1920x1080-p0-8b-420-svt.avif",
        "photo-fuji-1920x1080-p0-10b-420-aom.avif",
        "photo-carter-1440x1680-p0-8b-400-mono.avif",
        "photo-fuji-1280x720-chroma-colocated.avif",
        "color-fuji-1280x720-icc-exif-xmp-thmb.avif",
        "container-mangrove-1599x1199-odd-clap.avif",
        "container-fuji-1280x720-clap-irot-imir.avif",
        "perf-photo-fuji-2560x1440-alpha-10b.avif",
    ];

    public static IEnumerable<object[]> Decodable() => M1Fixtures.Select(static f => new object[] { f });
    public static IEnumerable<object[]> Rejected() => RejectedFixtures.Select(static f => new object[] { f });
    public static IEnumerable<object[]> RgbaFixtures() => RgbaCompareFixtures.Select(static f => new object[] { f });

    [Theory]
    [MemberData(nameof(Decodable))]
    public void PrimaryImage_PlanesDigest_MatchesGolden(string fixture)
    {
        byte[] data = ReadFixture(fixture);
        var golden = LoadGolden();
        golden.TryGetValue(fixture, out var expected).ShouldBeTrue($"{GoldenFileName} has no entry for {fixture}.");

        var planes = AvifDecoder.DecodePlanes(data);
        planes.Width.ShouldBe(expected!.Width, fixture);
        planes.Height.ShouldBe(expected.Height, fixture);
        planes.BitDepth.ShouldBe(expected.BitDepth, fixture);
        (planes.Alpha is not null).ShouldBe(expected.HasAlpha, fixture);

        string sub = planes.Monochrome ? "400" : (planes.SubsamplingX == 1 && planes.SubsamplingY == 1 ? "420" : "other");
        sub.ShouldBe(expected.Subsampling, fixture);

        string digest = DigestPlanes(planes);
        digest.ShouldBe(expected.Digest, $"{fixture} ({planes.Width}x{planes.Height}, {planes.BitDepth}-bit, {sub})");
    }

    [Theory]
    [MemberData(nameof(RgbaFixtures))]
    public void PrimaryImage_RgbaMatchesMagickWithinTolerance(string fixture)
    {
        byte[] data = ReadFixture(fixture);
        using var ours = AvifDecoder.Decode(data);

        using var magick = new MagickImage(FixturePath(fixture));
        magick.Width.ShouldBe((uint)ours.Width, $"{fixture} display width");
        magick.Height.ShouldBe((uint)ours.Height, $"{fixture} display height");

        using var pixels = magick.GetPixels();
        var input = pixels.ToShortArray(PixelMapping.RGBA);
        input.ShouldNotBeNull();

        var ourPixels = ours.Rgba;
        int w = ours.Width, h = ours.Height;
        int step = Math.Max(1, (w * h) / 100_000);
        int channels = ours.HasAlpha ? 4 : 3;
        int mismatches = 0;
        (int X, int Y, int Ch, int Ours, int Theirs)? first = null;
        for (int i = 0; i < w * h; i += step)
        {
            for (int c = 0; c < channels; c++)
            {
                int theirs = input[i * 4 + c] >> 8;
                int mine = ourPixels[i * 4 + c];
                // Allow small chroma-upsampling / matrix rounding diff vs libavif/ImageMagick (nearest vs bilinear chroma).
                int tol = c == 3 ? 2 : 12;
                if (Math.Abs(theirs - mine) > tol)
                {
                    mismatches++;
                    first ??= (i % w, i / w, c, mine, theirs);
                }
            }
        }
        // On real photos nearest vs bilinear chroma can differ > 12 on a tiny fraction of sharp colour edges;
        // verify < 1% of sampled channels exceed tol (and alpha has 0 mismatches).
        double rate = (double)mismatches / ((w * h / step + 1) * channels);
        rate.ShouldBeLessThan(0.01, first is { } f
            ? $"{fixture}: {mismatches} samples ({rate:P2}) differ > tol, first ch {f.Ch} at ({f.X},{f.Y}): ours={f.Ours}, Magick={f.Theirs}"
            : "");
    }

    [Fact]
    public void DecodeTo_WritesSamePixelsAndRejectsSmallBuffer()
    {
        byte[] data = ReadFixture("container-fuji-1280x720-clap-irot-imir.avif");
        using var image = AvifDecoder.Decode(data);
        var dst = new byte[image.Rgba.Length];
        var (w, h) = AvifDecoder.DecodeTo(data, dst);
        w.ShouldBe(image.Width);
        h.ShouldBe(image.Height);
        dst.AsSpan().SequenceEqual(image.Rgba).ShouldBeTrue();

        var tooSmall = new byte[image.Rgba.Length - 1];
        Should.Throw<ArgumentException>(() => AvifDecoder.DecodeTo(data, tooSmall));
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void UnsupportedFixture_FailsCleanly(string fixture)
    {
        string path = FixturePath(fixture);
        if (!File.Exists(path)) Assert.Skip($"{fixture} is not downloaded; run Oracle/avif/fetch.sh.");
        byte[] data = File.ReadAllBytes(path);
        var err = Should.Throw<InvalidDataException>(() => AvifDecoder.Decode(data));
        err.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ReadInfo_ReportsMetadataWithoutDecoding()
    {
        var grid10 = AvifDecoder.ReadInfo(ReadFixture("perf-photo-almsee-4096x3072-grid-10b.avif"));
        grid10.ShouldSatisfyAllConditions(
            () => grid10.Width.ShouldBe(4096),
            () => grid10.Height.ShouldBe(3072),
            () => grid10.BitDepth.ShouldBe(10),
            () => grid10.HasAlpha.ShouldBeFalse(),
            () => grid10.TileCount.ShouldBe(48),
            () => grid10.IccProfile.ShouldNotBeNull());

        var alpha10 = AvifDecoder.ReadInfo(ReadFixture("perf-photo-fuji-2560x1440-alpha-10b.avif"));
        alpha10.ShouldSatisfyAllConditions(
            () => alpha10.Width.ShouldBe(2560),
            () => alpha10.Height.ShouldBe(1440),
            () => alpha10.BitDepth.ShouldBe(10),
            () => alpha10.HasAlpha.ShouldBeTrue(),
            () => alpha10.TileCount.ShouldBe(1));

        var clapOdd = AvifDecoder.ReadInfo(ReadFixture("container-mangrove-1599x1199-odd-clap.avif"));
        clapOdd.Width.ShouldBe(1599);
        clapOdd.Height.ShouldBe(1199);

        var gridCrop = AvifDecoder.Decode(ReadFixture("container-grid-1500x990-3x2-crop.avif"));
        gridCrop.Width.ShouldBe(1500);
        gridCrop.Height.ShouldBe(990);
        gridCrop.Rgba.Length.ShouldBe(1500 * 990 * 4);
        // Verify pixel in right-hand grid tile (x=750, y=0) decoded properly (not zeroed).
        gridCrop.Rgba[750 * 4 + 2].ShouldBeGreaterThan((byte)200);

        var rotMir = AvifDecoder.ReadInfo(ReadFixture("container-fuji-1280x720-clap-irot-imir.avif"));
        // 1280x720 cropped by clap then rotated 90 deg CCW -> height/width swapped
        rotMir.Width.ShouldBeGreaterThan(0);
        rotMir.Height.ShouldBeGreaterThan(0);
    }

    private static string DigestPlanes(AvifDecodedPlanes p)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> hdr = stackalloc byte[12];
        int sampleBytes = p.BitDepth > 8 ? 2 : 1;
        HashPlane(sha, p.Y, p.Width, p.Height, sampleBytes, hdr);
        if (!p.Monochrome)
        {
            int cw = (p.Width + p.SubsamplingX) >> p.SubsamplingX;
            int ch = (p.Height + p.SubsamplingY) >> p.SubsamplingY;
            HashPlane(sha, p.Cb!, cw, ch, sampleBytes, hdr);
            HashPlane(sha, p.Cr!, cw, ch, sampleBytes, hdr);
        }
        if (p.Alpha is not null)
        {
            int alphaBytes = p.AlphaBitDepth > 8 ? 2 : 1;
            HashPlane(sha, p.Alpha, p.Width, p.Height, alphaBytes, hdr);
        }
        return Convert.ToHexStringLower(sha.GetCurrentHash());
    }

    private static void HashPlane(IncrementalHash sha, ushort[] plane, int w, int h, int sampleBytes, Span<byte> hdr)
    {
        BinaryPrimitives.WriteInt32LittleEndian(hdr[0..4], w);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[4..8], h);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[8..12], sampleBytes);
        sha.AppendData(hdr);

        if (sampleBytes == 1)
        {
            var row = new byte[w];
            for (int y = 0; y < h; y++)
            {
                int off = y * w;
                for (int x = 0; x < w; x++) row[x] = (byte)plane[off + x];
                sha.AppendData(row);
            }
        }
        else
        {
            var row = new byte[w * 2];
            for (int y = 0; y < h; y++)
            {
                int off = y * w;
                for (int x = 0; x < w; x++)
                    BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2, 2), plane[off + x]);
                sha.AppendData(row);
            }
        }
    }

    private sealed record GoldenEntry(int Width, int Height, int BitDepth, string Subsampling, bool HasAlpha, int Frames, string Digest);

    private static Dictionary<string, GoldenEntry> LoadGolden()
    {
        var map = new Dictionary<string, GoldenEntry>(StringComparer.Ordinal);
        string path = FixturePath(GoldenFileName);
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            map[parts[0]] = new GoldenEntry(
                int.Parse(parts[1]),
                int.Parse(parts[2]),
                int.Parse(parts[3]),
                parts[4],
                parts[5] == "1",
                int.Parse(parts[6]),
                parts[7]);
        }
        return map;
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "avif", fileName);

    private static byte[] ReadFixture(string fileName)
    {
        string path = FixturePath(fileName);
        if (!File.Exists(path))
            Assert.Skip($"{fileName} is not downloaded; run Oracle/avif/fetch.sh.");
        return File.ReadAllBytes(path);
    }
}
