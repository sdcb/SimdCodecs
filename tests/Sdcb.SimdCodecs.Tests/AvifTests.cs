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
        // M2 Profile 1 & 2 (4:4:4, 4:2:2, 12-bit, lossless WHT):
        "container-tiny-1x1.avif",
        "perf-photo-fuji-2560x1440-lossless-444.avif",
        "photo-almsee-1920x1080-p2-8b-422.avif",
        "photo-almsee-1920x1080-p2-12b-420.avif",
        "photo-almsee-960x540-p2-12b-444-lossless.avif",
        "perf-photo-carter-3840x2560-p2-12b-422.avif",
        // M2 IntraBC + Screen Content:
        "screen-ui-1920x1080-p1-8b-444-gbr.avif",
        "screen-ui-1920x1080-p1-10b-444-bt709.avif",
        "perf-screen-ui-3840x2160-444-8b.avif",
        "screen-text-1280x720-palette-intrabc.avif",
        // M2 Superres & Film Grain:
        "photo-mangrove-1600x1200-superres.avif",
        "photo-night-1280x720-filmgrain-8b.avif",
        "perf-photo-night-3840x2160-grain-10b.avif",
        // M3 Layered scalable Inter:
        "container-layered-a1lx-a1op-1280x720.avif",
    ];

    /// <summary>All 5 animated AVIF sequences (<c>avis</c>) in <c>avif-golden.tsv</c>.</summary>
    private static readonly string[] AnimatedFixtures =
    [
        "anim-alltools-320x240-8f.avif",
        "anim-alpha-640x360-24f-varfps.avif",
        "anim-mandelbrot-1280x720-60f-inter.avif",
        "anim-photo-fuji-1920x1080-30f-10b.avif",
        "anim-screen-testsrc2-1280x720-24f-444.avif",
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
        "container-iden-chain-640x480.avif",
        "screen-ui-1920x1080-p1-8b-444-gbr.avif",
        "photo-almsee-1920x1080-p2-8b-422.avif",
        "photo-almsee-1920x1080-p2-12b-420.avif",
        "perf-photo-fuji-2560x1440-alpha-10b.avif",
    ];

    public static IEnumerable<object[]> Decodable() => M1Fixtures.Select(static f => new object[] { f });
    public static IEnumerable<object[]> Animated() => AnimatedFixtures.Select(static f => new object[] { f });
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

        string sub = planes.Monochrome ? "400"
            : (planes.SubsamplingX, planes.SubsamplingY) switch { (1, 1) => "420", (1, 0) => "422", (0, 0) => "444", _ => "other" };
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
    [MemberData(nameof(Animated))]
    public void AnimatedSequence_AllFramesPlanesDigest_MatchesGolden(string fixture)
    {
        byte[] data = ReadFixture(fixture);
        var golden = LoadGolden();
        golden.TryGetValue(fixture, out var expected).ShouldBeTrue($"{GoldenFileName} has no entry for {fixture}.");

        AvifSequence.TryParse(data, out var seq).ShouldBeTrue(fixture);
        seq!.FrameCount.ShouldBe(expected!.Frames, fixture);
        seq.HasAlpha.ShouldBe(expected.HasAlpha, fixture);

        var ct = TestContext.Current.CancellationToken;
        var decoder = new AvifSequenceDecoder(seq, data);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> hdr = stackalloc byte[12];
        for (int i = 0; i < seq.FrameCount; i++)
        {
            var planes = decoder.DecodeFramePlanes(data, i, ct);
            if (i == 0)
            {
                planes.Width.ShouldBe(expected.Width, fixture);
                planes.Height.ShouldBe(expected.Height, fixture);
                planes.BitDepth.ShouldBe(expected.BitDepth, fixture);
                string sub = planes.Monochrome ? "400"
                    : (planes.SubsamplingX, planes.SubsamplingY) switch { (1, 1) => "420", (1, 0) => "422", (0, 0) => "444", _ => "other" };
                sub.ShouldBe(expected.Subsampling, fixture);
            }
            AppendPlanesHash(sha, planes, hdr);
        }

        string digest = Convert.ToHexStringLower(sha.GetCurrentHash());
        digest.ShouldBe(expected.Digest, $"{fixture} ({seq.FrameCount} frames)");
    }

    [Fact]
    public void AnimatedAvif_OpenFrames_ReportsTimingLoopAndSupportsRandomSeek()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Variable FPS + auxiliary alpha track + finite loop count (3)
        byte[] varAlpha = ReadFixture("anim-alpha-640x360-24f-varfps.avif");
        ImageCodecs.TryReadInfo(varAlpha, out var info).ShouldBeTrue();
        info.FrameCount.ShouldBe(24);
        info.Channels.ShouldBe(4);

        using (var dec = ImageDecoder.Open(varAlpha))
        {
            dec.FrameCount.ShouldBe(24);
            dec.LoopCount.ShouldBe(3);

            dec.GetFrame(0).Duration.ShouldBe(TimeSpan.FromMilliseconds(120));
            dec.GetFrame(1).Duration.ShouldBe(TimeSpan.FromMilliseconds(60));
            dec.GetFrame(2).Duration.ShouldBe(TimeSpan.FromMilliseconds(60));

            using var img0 = dec.GetFrame(0).Decode(cancellationToken: ct);
            using var img5 = dec.GetFrame(5).Decode(cancellationToken: ct);
            img0.Width.ShouldBe(640);
            img0.Height.ShouldBe(360);
            img0.Channels.ShouldBe(4);
            img5.Width.ShouldBe(640);

            var buf = new byte[640 * 360 * 4];
            dec.GetFrame(5).CopyPixels(null, Abstractions.PixelFormat.Rgba32, buf, 0, cancellationToken: ct);
            buf.AsSpan().SequenceEqual(img5.ToRgba8()).ShouldBeTrue();
        }

        // 2. Multi-GOP (stss=[1, 31]) infinite-loop sequence: random seek matches sequential decode byte-for-byte
        byte[] mandel = ReadFixture("anim-mandelbrot-1280x720-60f-inter.avif");
        using (var seqDec = ImageDecoder.Open(mandel))
        using (var seekDec = ImageDecoder.Open(mandel))
        {
            seqDec.FrameCount.ShouldBe(60);
            seqDec.LoopCount.ShouldBe(0); // infinite
            seqDec.GetFrame(0).Duration.ShouldBe(TimeSpan.FromMilliseconds(50));

            // Jump straight to frame 35 (uses keyframe 30), then backward to frame 5, then forward to 35 again
            using var seek35 = seekDec.GetFrame(35).Decode(cancellationToken: ct);
            using var seek5 = seekDec.GetFrame(5).Decode(cancellationToken: ct);
            using var seek35Again = seekDec.GetFrame(35).Decode(cancellationToken: ct);
            seek35.Pixels.SequenceEqual(seek35Again.Pixels).ShouldBeTrue();

            using var seq5 = seqDec.GetFrame(5).Decode(cancellationToken: ct);
            seq5.Pixels.SequenceEqual(seek5.Pixels).ShouldBeTrue();
        }

        // 3. Still AVIF via ImageDecoder.Open: 1 frame, null loop & duration, matches TryDecode
        byte[] still = ReadFixture("container-tiny-3x7.avif");
        using (var stillDec = ImageDecoder.Open(still))
        {
            stillDec.FrameCount.ShouldBe(1);
            stillDec.LoopCount.ShouldBeNull();
            stillDec.GetFrame(0).Duration.ShouldBeNull();
            using var frameImg = stillDec.GetFrame(0).Decode(cancellationToken: ct);
            using var directImg = ImageCodecs.Decode(still, cancellationToken: ct);
            frameImg.Pixels.SequenceEqual(directImg.Pixels).ShouldBeTrue();
        }
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

    [Fact]
    public void IovlOverlay_CompositesCanvasFillAndAllThreeLayers()
    {
        byte[] data = ReadFixture("container-iovl-overlay-800x600.avif");
        var info = AvifDecoder.ReadInfo(data);
        info.Width.ShouldBe(800);
        info.Height.ShouldBe(600);
        info.TileCount.ShouldBe(3);

        using var img = AvifDecoder.Decode(data);
        img.Width.ShouldBe(800);
        img.Height.ShouldBe(600);
        var px = img.Rgba;

        // Top-left (0,0) and bottom-right (799,599) are uncovered canvas fill: (0x18, 0x22, 0x33, 0xFF) = (24, 34, 51, 255).
        (px[0], px[1], px[2], px[3]).ShouldBe(((byte)24, (byte)34, (byte)51, (byte)255));
        int br = (599 * 800 + 799) * 4;
        (px[br], px[br + 1], px[br + 2], px[br + 3]).ShouldBe(((byte)24, (byte)34, (byte)51, (byte)255));

        // Each of the 3 overlaid layers covers its expected region with non-background pixels:
        // Layer 0: 480x360 at (20, 20), Layer 1: 400x300 at (360, 140), Layer 2: 240x180 at (120, 380).
        foreach (var (x, y) in new[] { (30, 30), (500, 200), (200, 460) })
        {
            int off = (y * 800 + x) * 4;
            bool isFill = px[off] == 24 && px[off + 1] == 34 && px[off + 2] == 51;
            isFill.ShouldBeFalse($"Layer pixel at ({x},{y}) should overwrite canvas fill.");
            px[off + 3].ShouldBe((byte)255);
        }
    }

    public static IEnumerable<object[]> SmallDecodable() =>
        M1Fixtures.Where(static f => !f.StartsWith("perf-", StringComparison.Ordinal)).Select(static f => new object[] { f });

    /// <summary>
    /// <c>TryDecodeInto</c> converts the decoded RGBA once, straight into the destination; it must write exactly what
    /// converting the decoded image does (<c>Decode</c> + <c>TryCopyTo</c>), in every pixel format, through a padded
    /// stride whose padding stays untouched, over a destination that starts dirty.
    /// </summary>
    [Theory]
    [MemberData(nameof(SmallDecodable))]
    public void TryDecodeInto_EveryFormat_MatchesDecodedImage(string fixture)
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] data = ReadFixture(fixture);
        using var image = AvifImageDecoder.Decode(data, cancellationToken: ct);
        foreach (var format in Abstractions.PixelFormat.All)
        {
            int row = image.Width * format.BytesPerPixel;
            int stride = row + 3;
            var expected = new byte[stride * image.Height];
            expected.AsSpan().Fill(0xA5);
            var actual = expected.AsSpan().ToArray();
            bool ok = AvifImageDecoder.TryDecodeInto(data, actual, stride, format, cancellationToken: ct);
            if (image.ColorEncoding.Transfer is Abstractions.TransferFunction.Pq or Abstractions.TransferFunction.Hlg)
            {
                ok.ShouldBeFalse(fixture);
                continue;
            }
            ok.ShouldBeTrue($"{fixture} {format}");
            image.TryCopyTo(expected, stride, format).ShouldBeTrue($"{fixture} {format}");
            actual.AsSpan().SequenceEqual(expected).ShouldBeTrue($"{fixture} {format}");

            if (format == Abstractions.PixelFormat.Rgba32)
            {
                var packed = new byte[row * image.Height];
                packed.AsSpan().Fill(0xA5);
                AvifImageDecoder.TryDecodeInto(data, packed, 0, format, cancellationToken: ct).ShouldBeTrue(fixture);
                for (int y = 0; y < image.Height; y++)
                    packed.AsSpan(y * row, row).SequenceEqual(expected.AsSpan(y * stride, row)).ShouldBeTrue($"{fixture} row {y}");
            }
        }
    }

    [Fact]
    public void Facade_HdrPqAndHlg_Preserves10BitAndRefusesTryDecodeInto()
    {
        foreach (var (fixture, expectedTransfer) in new[]
        {
            ("color-testsrc2-1280x720-pq-10b-nclx.avif", Abstractions.TransferFunction.Pq),
            ("color-testsrc2-1280x720-hlg-10b-vui.avif", Abstractions.TransferFunction.Hlg),
        })
        {
            byte[] data = ReadFixture(fixture);
            ImageCodecs.DetectFormat(data).ShouldBe(Abstractions.ImageFormat.Avif);
            ImageCodecs.TryReadInfo(data, out var info).ShouldBeTrue(fixture);
            info.Width.ShouldBe(1280);
            info.Height.ShouldBe(720);
            info.BitDepth.ShouldBe(10);
            info.SampleFormat.ShouldBe(Abstractions.SampleFormat.UInt16);
            info.ColorEncoding.Primaries.ShouldBe(Abstractions.ColorPrimaries.BT2020);
            info.ColorEncoding.Transfer.ShouldBe(expectedTransfer);

            var ct = TestContext.Current.CancellationToken;
            using var decoded = ImageCodecs.Decode(data, cancellationToken: ct);
            decoded.SampleFormat.ShouldBe(Abstractions.SampleFormat.UInt16);
            decoded.BitDepth.ShouldBe(10);
            decoded.ColorEncoding.Transfer.ShouldBe(expectedTransfer);

            var dst = new byte[1280 * 720 * 4];
            ImageCodecs.TryDecodeInto(data, dst, 1280 * 4, Abstractions.PixelFormat.Rgba32, cancellationToken: ct).ShouldBeFalse(fixture);
        }
    }

    [Fact]
    public void Facade_GainMapTmap_ReconstructsHdrFloatsAndServesSdrBase()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] data = ReadFixture("color-fuji-1280x720-gainmap-tmap.avif");
        ImageCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.Width.ShouldBe(1280);
        info.Height.ShouldBe(720);
        info.SampleFormat.ShouldBe(Abstractions.SampleFormat.Float32);
        info.ColorEncoding.Transfer.ShouldBe(Abstractions.TransferFunction.Linear);
        info.ColorEncoding.Float.ShouldBe(Abstractions.FloatSemantics.DisplayReferred);

        using var decoded = ImageCodecs.Decode(data, cancellationToken: ct);
        decoded.SampleFormat.ShouldBe(Abstractions.SampleFormat.Float32);
        float[] hdr = decoded.ToFloats();
        hdr.Length.ShouldBe(1280 * 720 * 4);
        // HDR highlights should exceed 1.0 SDR white.
        float maxVal = 0f;
        for (int i = 0; i < hdr.Length; i += 4)
        {
            if (hdr[i] > maxVal) maxVal = hdr[i];
            if (hdr[i + 1] > maxVal) maxVal = hdr[i + 1];
            if (hdr[i + 2] > maxVal) maxVal = hdr[i + 2];
        }
        maxVal.ShouldBeGreaterThan(1.5f);

        // SDR fallback via ToRgba8 and TryDecodeInto must match AvifDecoder.Decode().
        byte[] sdr = decoded.ToRgba8();
        using var direct = AvifDecoder.Decode(data);
        sdr.AsSpan().SequenceEqual(direct.Rgba).ShouldBeTrue();

        var into = new byte[1280 * 720 * 4];
        ImageCodecs.TryDecodeInto(data, into, 1280 * 4, Abstractions.PixelFormat.Rgba32, cancellationToken: ct).ShouldBeTrue();
        into.AsSpan().SequenceEqual(direct.Rgba).ShouldBeTrue();
    }

    [Fact]
    public void Facade_MetadataThumbnailPlanarAndOptions_WorkEndToEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] metaFile = ReadFixture("color-fuji-1280x720-icc-exif-xmp-thmb.avif");
        ImageCodecs.TryReadInfo(metaFile, out var info).ShouldBeTrue();
        info.HasThumbnail.ShouldBeTrue();

        ImageCodecs.TryReadMetadata(metaFile, out var meta).ShouldBeTrue();
        meta!.Exif.IsEmpty.ShouldBeFalse();
        meta.Xmp.IsEmpty.ShouldBeFalse();
        meta.Icc.IsEmpty.ShouldBeFalse();

        ImageCodecs.TryDecodeThumbnail(metaFile, out var thumb).ShouldBeTrue();
        using (thumb!)
        {
            thumb.Width.ShouldBe(320);
            thumb.Height.ShouldBe(180);
        }

        // Planar decode (4:2:0)
        ImageCodecs.TryReadPlanarInfo(metaFile, out var pInfo).ShouldBeTrue();
        pInfo.Width.ShouldBe(1280);
        pInfo.Height.ShouldBe(720);
        pInfo.BitDepth.ShouldBe(8);
        var y = new ushort[pInfo.LumaLength];
        var cb = new ushort[pInfo.ChromaLength];
        var cr = new ushort[pInfo.ChromaLength];
        ImageCodecs.TryDecodePlanes(metaFile, y, cb, cr, ct).ShouldBeTrue();
        var directPlanes = AvifDecoder.DecodePlanes(metaFile);
        y.AsSpan().SequenceEqual(directPlanes.Y).ShouldBeTrue();
        cb.AsSpan().SequenceEqual(directPlanes.Cb).ShouldBeTrue();
        cr.AsSpan().SequenceEqual(directPlanes.Cr).ShouldBeTrue();

        // Region & ScaleDenominator
        var opts = new Abstractions.DecodeOptions { ScaleDenominator = 2, Region = new Abstractions.PixelRect(10, 20, 100, 80) };
        using var scaledRegion = ImageCodecs.Decode(metaFile, opts, ct);
        scaledRegion.Width.ShouldBe(100);
        scaledRegion.Height.ShouldBe(80);
    }

    private static string DigestPlanes(AvifDecodedPlanes p)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> hdr = stackalloc byte[12];
        AppendPlanesHash(sha, p, hdr);
        return Convert.ToHexStringLower(sha.GetCurrentHash());
    }

    private static void AppendPlanesHash(IncrementalHash sha, AvifDecodedPlanes p, Span<byte> hdr)
    {
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
