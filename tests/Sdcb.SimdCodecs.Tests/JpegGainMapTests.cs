using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The gain-map feature end to end: reconstruction math properties (the roadmap's
/// spine: W=0 ⇒ base exactly, W=1 ⇒ full boost, monotone in W), the
/// Compute→Assemble→TryRead→ReconstructHdr round-trip, and structural checks on
/// the assembled bytes that mirror how Chromium/Skia actually locates the gain
/// map (MPF offsets AND the GContainer trailing-Length convention — both paths).
/// The base and gain-map JPEGs are encoded with our own <see cref="JpegEncoder"/>,
/// so the write path is fully in-family — Magick.NET appears only as an
/// independent decode oracle (proving the assembled file is a real JPEG).
/// </summary>
public sealed class JpegGainMapTests
{
    private static readonly GainMapMetadata Boost4X = new()
    {
        GainMapMax = 4.0,
        HdrCapacityMax = 4.0,
    };

    // ------------------------------------------------------------ helpers

    /// <summary>Encodes packed RGB8 as a baseline JPEG with our own encoder
    /// (quality 100 ⇒ 4:4:4 — determinism and fidelity for round-trip bounds).</summary>
    private static byte[] EncodeJpeg(byte[] rgb, int width, int height) =>
        JpegEncoder.Encode(rgb, width, height, 3, new JpegEncodeOptions { Quality = 100 });

    private static byte[] GrayToRgb(byte[] gray)
    {
        var rgb = new byte[gray.Length * 3];
        for (var i = 0; i < gray.Length; i++)
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = gray[i];
        return rgb;
    }

    /// <summary>A horizontal gray ramp as packed RGB8 — smooth content JPEG keeps intact.</summary>
    private static byte[] GrayRampRgb(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var v = (byte)(16 + 224 * x / (width - 1));
            var i = (y * width + x) * 3;
            rgb[i] = rgb[i + 1] = rgb[i + 2] = v;
        }
        return rgb;
    }

    private static double SrgbEotf(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    private static RasterImage Raster(byte[] pixels, int width, int height, int channels) =>
        new(width, height, channels, SampleFormat.UInt8, pixels);

    // --------------------------------------------- reconstruction properties

    [Fact]
    public void ReconstructHdr_At_Sdr_Headroom_Is_The_Linearized_Base_Exactly()
    {
        var baseRgb = GrayRampRgb(16, 8);
        var map = new byte[16 * 8];
        Random.Shared.NextBytes(map); // arbitrary map — W=0 must ignore it entirely
        var image = new GainMapImage(Raster(baseRgb, 16, 8, 3), Raster(map, 16, 8, 1), Boost4X);

        var hdr = image.ReconstructHdr(1.0);

        hdr.SampleFormat.ShouldBe(SampleFormat.Float32);
        hdr.Channels.ShouldBe(3);
        hdr.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        hdr.ColorEncoding.Float.ShouldBe(FloatSemantics.DisplayReferred);
        var floats = hdr.ToFloats(); // RGBA-widened
        for (var p = 0; p < 16 * 8; p++)
            floats[p * 4].ShouldBe((float)SrgbEotf(baseRgb[p * 3] / 255.0), $"pixel {p}");
    }

    [Fact]
    public void ReconstructHdr_At_Full_Headroom_Applies_The_Full_Gain()
    {
        // Uniform map at code 255 → every pixel gets exactly GainMapMax.
        var baseRgb = GrayRampRgb(8, 4);
        var map = new byte[8 * 4];
        Array.Fill(map, (byte)255);
        var image = new GainMapImage(Raster(baseRgb, 8, 4, 3), Raster(map, 8, 4, 1), Boost4X);

        var floats = image.ReconstructHdr(Boost4X.HdrCapacityMax).ToFloats();

        for (var p = 0; p < 8 * 4; p++)
        {
            var sdrLinear = SrgbEotf(baseRgb[p * 3] / 255.0);
            var expected = (sdrLinear + Boost4X.OffsetSdr) * 4.0 - Boost4X.OffsetHdr;
            ((double)floats[p * 4]).ShouldBe(expected, tolerance: 1e-6, $"pixel {p}");
        }
    }

    [Fact]
    public void ReconstructHdr_Is_Monotone_In_Headroom()
    {
        var baseRgb = GrayRampRgb(12, 6);
        var map = new byte[12 * 6];
        for (var i = 0; i < map.Length; i++) map[i] = (byte)(i * 255 / (map.Length - 1));
        var image = new GainMapImage(Raster(baseRgb, 12, 6, 3), Raster(map, 12, 6, 1), Boost4X);

        float[]? previous = null;
        foreach (var headroom in new[] { 1.0, 1.4, 2.0, 3.0, 4.0, 8.0 })
        {
            var current = image.ReconstructHdr(headroom).ToFloats();
            if (previous is not null)
            {
                for (var p = 0; p < 12 * 6; p++)
                    current[p * 4].ShouldBeGreaterThanOrEqualTo(previous[p * 4], $"headroom {headroom}, pixel {p}");
            }
            previous = current;
        }
    }

    [Fact]
    public void Weight_Ramps_Log_Space_And_Clamps()
    {
        var image = new GainMapImage(
            Raster(new byte[4 * 1 * 3], 4, 1, 3), Raster(new byte[4], 4, 1, 1), Boost4X);

        image.Weight(0.5).ShouldBe(0);
        image.Weight(1.0).ShouldBe(0);
        image.Weight(2.0).ShouldBe(0.5, tolerance: 1e-12); // halfway in stops: log2(2)/log2(4)
        image.Weight(4.0).ShouldBe(1);
        image.Weight(100).ShouldBe(1);
    }

    [Fact]
    public void ReconstructHdr_Refuses_The_Hdr_Base_Form()
    {
        var image = new GainMapImage(
            Raster(new byte[4 * 1 * 3], 4, 1, 3), Raster(new byte[4], 4, 1, 1),
            Boost4X with { BaseRenditionIsHdr = true });

        Should.Throw<NotSupportedException>(() => image.ReconstructHdr(4.0));
    }

    // ------------------------------------------------------------- Compute

    [Fact]
    public void Compute_Then_Reconstruct_Recovers_The_Hdr_Input()
    {
        // Gray scene, per-pixel boost field 1→4: on gray content the luminance
        // ratio math is exact, so the only loss is the map's 8-bit quantization.
        const int w = 32, h = 16;
        var sdrRgb = GrayRampRgb(w, h);
        var hdr = new float[w * h * 3];
        for (var p = 0; p < w * h; p++)
        {
            var boost = 1.0 + 3.0 * (p % w) / (w - 1);
            var linear = (float)(SrgbEotf(sdrRgb[p * 3] / 255.0) * boost);
            hdr[p * 3] = hdr[p * 3 + 1] = hdr[p * 3 + 2] = linear;
        }

        var result = JpegGainMap.Compute(hdr, sdrRgb, w, h, downscale: 1);

        result.Width.ShouldBe(w);
        result.Height.ShouldBe(h);
        result.Metadata.HdrCapacityMax.ShouldBeGreaterThan(1.0);

        var image = new GainMapImage(
            Raster(sdrRgb, w, h, 3),
            Raster(result.GainMapGray8, result.Width, result.Height, 1),
            result.Metadata);
        var reconstructed = image.ReconstructHdr(result.Metadata.HdrCapacityMax).ToFloats();

        for (var p = 0; p < w * h; p++)
        {
            var expected = hdr[p * 3];
            var actual = reconstructed[p * 4];
            // 8-bit code over the fitted log2 range → ~0.3% gain step at 2 stops;
            // 1% relative leaves margin without hiding real math errors.
            Math.Abs(actual - expected).ShouldBeLessThanOrEqualTo(0.01f * expected + 1e-4f, $"pixel {p}");
        }
    }

    [Fact]
    public void Compute_Handles_Flat_Content()
    {
        // HDR == SDR: degenerate gain range must still produce valid metadata.
        const int w = 8, h = 8;
        var sdrRgb = GrayRampRgb(w, h);
        var hdr = new float[w * h * 3];
        for (var p = 0; p < w * h * 3; p++) hdr[p] = (float)SrgbEotf(sdrRgb[p] / 255.0);

        var result = JpegGainMap.Compute(hdr, sdrRgb, w, h);

        result.Metadata.Validate(); // must not throw
        result.Width.ShouldBe(2);
        result.Height.ShouldBe(2);
    }

    [Fact]
    public void Compute_Accepts_Rgba_Strides()
    {
        const int w = 4, h = 4;
        var rgb = GrayRampRgb(w, h);
        var rgba = new byte[w * h * 4];
        var hdrRgba = new float[w * h * 4];
        for (var p = 0; p < w * h; p++)
        {
            for (var c = 0; c < 3; c++)
            {
                rgba[p * 4 + c] = rgb[p * 3 + c];
                hdrRgba[p * 4 + c] = (float)(2 * SrgbEotf(rgb[p * 3 + c] / 255.0));
            }
            rgba[p * 4 + 3] = 255;
            hdrRgba[p * 4 + 3] = 1f;
        }

        var fromRgba = JpegGainMap.Compute(hdrRgba, rgba, w, h, downscale: 1);
        fromRgba.Metadata.GainMapMax.ShouldBeGreaterThan(1.5);
    }

    // ------------------------------------------- Assemble / TrySplit / TryRead

    [Fact]
    public void Assemble_TryRead_RoundTrips_Renditions_And_Metadata()
    {
        var baseJpeg = EncodeJpeg(GrayRampRgb(64, 32), 64, 32);
        var mapJpeg = EncodeJpeg(GrayToRgb(new byte[16 * 8]), 16, 8);

        var assembled = JpegGainMap.Assemble(baseJpeg, mapJpeg, Boost4X);

        JpegGainMap.TryRead(assembled, out var image).ShouldBeTrue();
        image.ShouldNotBeNull();
        image.Base.Width.ShouldBe(64);
        image.Base.Height.ShouldBe(32);
        image.GainMap.Width.ShouldBe(16);
        image.GainMap.Height.ShouldBe(8);
        image.Metadata.GainMapMax.ShouldBe(4.0);
        image.Metadata.HdrCapacityMax.ShouldBe(4.0);
        image.Metadata.BaseRenditionIsHdr.ShouldBeFalse();
    }

    [Fact]
    public void Assembled_File_Still_Decodes_As_A_Plain_Jpeg()
    {
        // The legacy-viewer guarantee: splicing must not disturb the base image's
        // decode in any way.
        var baseJpeg = EncodeJpeg(GrayRampRgb(48, 24), 48, 24);
        var assembled = JpegGainMap.Assemble(baseJpeg, EncodeJpeg(GrayRampRgb(12, 6), 12, 6), Boost4X);

        var original = JpegDecoder.Decode(baseJpeg);
        var spliced = JpegDecoder.Decode(assembled);

        spliced.Width.ShouldBe(original.Width);
        spliced.Height.ShouldBe(original.Height);
        spliced.Pixels.ShouldBe(original.Pixels); // byte-exact

        // And a third-party decoder accepts the whole container.
        using var magick = new MagickImage(assembled);
        magick.Width.ShouldBe(48u);
    }

    [Fact]
    public void Assembled_File_Satisfies_Both_Chromium_Locator_Paths()
    {
        var baseJpeg = EncodeJpeg(GrayRampRgb(64, 32), 64, 32);
        var mapJpeg = EncodeJpeg(GrayToRgb(new byte[16 * 8]), 16, 8);
        var assembled = JpegGainMap.Assemble(baseJpeg, mapJpeg, Boost4X);

        // Path 1 — GContainer XMP: the primary's directory declares Item:Length,
        // and the gain map must be exactly the trailing Length bytes (Skia's
        // MPF-less convention).
        JpegSegmentScanner.TryFindAppPayload(assembled, 0xE1, GainMapXmp.App1Identifier, out var xmp).ShouldBeTrue();
        GainMapXmp.TryParseContainerGainMapLength(assembled.AsSpan()[xmp], out var declaredLength).ShouldBeTrue();
        var gainMapStart = assembled.Length - declaredLength;
        assembled[gainMapStart].ShouldBe((byte)0xFF);
        assembled[gainMapStart + 1].ShouldBe((byte)0xD8);

        // Path 2 — MPF: entry offsets (relative to the endian field on the wire)
        // must resolve to the same place, with consistent sizes.
        JpegSegmentScanner.TryFindAppPayload(assembled, 0xE2, MpfSegment.Identifier, out var mpf).ShouldBeTrue();
        var (mpfOffset, mpfLength) = mpf.GetOffsetAndLength(assembled.Length);
        MpfSegment.TryParse(assembled.AsSpan(mpfOffset, mpfLength), mpfOffset, out var entries).ShouldBeTrue();
        entries.Length.ShouldBe(2);
        entries[0].ImageOffset.ShouldBe(0);
        entries[0].ImageLength.ShouldBe((uint)gainMapStart);
        entries[1].ImageOffset.ShouldBe(gainMapStart);
        entries[1].ImageLength.ShouldBe((uint)declaredLength);
        ((long)entries[0].ImageLength + entries[1].ImageLength).ShouldBe(assembled.Length);

        // The gain map rendition carries its own hdrgm XMP — required by every
        // reader before it will treat the second image as a gain map.
        var gainMap = assembled.AsSpan(gainMapStart, declaredLength);
        JpegSegmentScanner.TryFindAppPayload(gainMap, 0xE1, GainMapXmp.App1Identifier, out var gmXmp).ShouldBeTrue();
        GainMapXmp.TryParseGainMapMetadata(gainMap[gmXmp], out var meta).ShouldBeTrue();
        meta.ShouldNotBeNull();
        meta.GainMapMax.ShouldBe(4.0, tolerance: 1e-9);
    }

    [Fact]
    public void TrySplit_Falls_Back_To_The_Container_Path_Without_Mpf()
    {
        var baseJpeg = EncodeJpeg(GrayRampRgb(32, 16), 32, 16);
        var mapJpeg = EncodeJpeg(GrayToRgb(new byte[8 * 4]), 8, 4);
        var assembled = JpegGainMap.Assemble(baseJpeg, mapJpeg, Boost4X);

        // Surgically remove the 90-byte MPF APP2 — GContainer-only files exist
        // in the wild (Adobe dialect) and must still split.
        var segments = JpegSegmentScanner.Scan(assembled);
        var mpfSegment = segments.Single(s =>
            s.Marker == 0xE2 && s.PayloadLength >= 4 && s.Payload(assembled)[..4].SequenceEqual("MPF\0"u8));
        var withoutMpf = new byte[assembled.Length - mpfSegment.TotalLength];
        assembled.AsSpan(0, mpfSegment.SegmentOffset).CopyTo(withoutMpf);
        assembled.AsSpan(mpfSegment.SegmentOffset + mpfSegment.TotalLength).CopyTo(withoutMpf.AsSpan(mpfSegment.SegmentOffset));

        JpegGainMap.TrySplit(withoutMpf, out _, out var gainMapRange, out var metadata).ShouldBeTrue();
        metadata.ShouldNotBeNull();
        metadata.GainMapMax.ShouldBe(4.0, tolerance: 1e-9);
        var (gmOffset, gmLength) = gainMapRange.GetOffsetAndLength(withoutMpf.Length);
        withoutMpf[gmOffset].ShouldBe((byte)0xFF);
        withoutMpf[gmOffset + 1].ShouldBe((byte)0xD8);
        gmLength.ShouldBeGreaterThan(mapJpeg.Length); // map + its injected XMP
    }

    [Fact]
    public void TrySplit_Rejects_Plain_Jpegs_And_Garbage()
    {
        JpegGainMap.TrySplit(EncodeJpeg(GrayRampRgb(8, 8), 8, 8), out _, out _, out _).ShouldBeFalse();
        JpegGainMap.TrySplit("not a jpeg"u8, out _, out _, out _).ShouldBeFalse();
        JpegGainMap.TrySplit([], out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Full_Pipeline_Compute_Assemble_TryRead_Reconstruct()
    {
        // The astro publishing tier end to end, JPEG loss included: HDR/SDR pair
        // in, one Ultra HDR file out, HDR floats back.
        const int w = 64, h = 32;
        var sdrRgb = GrayRampRgb(w, h);
        var hdr = new float[w * h * 3];
        for (var p = 0; p < w * h; p++)
        {
            var boost = p % w < w / 2 ? 1.0 : 4.0; // SDR left half, +2 stops right half
            var linear = (float)(SrgbEotf(sdrRgb[p * 3] / 255.0) * boost);
            hdr[p * 3] = hdr[p * 3 + 1] = hdr[p * 3 + 2] = linear;
        }

        var computed = JpegGainMap.Compute(hdr, sdrRgb, w, h);
        var assembled = JpegGainMap.Assemble(
            EncodeJpeg(sdrRgb, w, h),
            EncodeJpeg(GrayToRgb(computed.GainMapGray8), computed.Width, computed.Height),
            computed.Metadata);

        JpegGainMap.TryRead(assembled, out var image).ShouldBeTrue();
        image.ShouldNotBeNull();
        var reconstructed = image.ReconstructHdr(image.Metadata.HdrCapacityMax).ToFloats();

        // Two JPEG encodes and a quarter-scale map sit between input and output,
        // so the bound is loose — but the half-image split must clearly survive:
        // sample well inside each half, away from the boundary blur.
        for (var y = 0; y < h; y += 5)
        {
            var left = reconstructed[(y * w + 8) * 4];
            var leftExpected = hdr[(y * w + 8) * 3];
            var right = reconstructed[(y * w + w - 8) * 4];
            var rightExpected = hdr[(y * w + w - 8) * 3];
            Math.Abs(left - leftExpected).ShouldBeLessThanOrEqualTo(0.10f * leftExpected + 0.01f, $"left, row {y}");
            Math.Abs(right - rightExpected).ShouldBeLessThanOrEqualTo(0.10f * rightExpected + 0.01f, $"right, row {y}");
        }
    }
}
