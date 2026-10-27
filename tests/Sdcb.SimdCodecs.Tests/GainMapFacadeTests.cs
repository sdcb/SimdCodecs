using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The <c>Sdcb.SimdCodecs</c> facade's gain-map ("Ultra HDR") handling: asking
/// for floats reconstructs the authored HDR, while the 8-bit path returns the SDR
/// base rendition — the split the format is designed around. Plain JPEGs must be
/// completely unaffected by the extra registry entry. The Ultra HDR fixtures are
/// built fully in-family (Compute → encode → Assemble) so the test has no external
/// codec dependency.
/// </summary>
public sealed class GainMapFacadeTests
{
    private const int W = 64, H = 32;

    private static byte[] EncodeJpeg(byte[] rgb, int width, int height) =>
        JpegEncoder.Encode(rgb, width, height, 3, new JpegEncodeOptions { Quality = 100 });

    private static byte[] GrayToRgb(byte[] gray)
    {
        var rgb = new byte[gray.Length * 3];
        for (var i = 0; i < gray.Length; i++)
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = gray[i];
        return rgb;
    }

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

    /// <summary>An Ultra HDR file whose right half is boosted +2 stops over the SDR
    /// base — enough that the reconstructed HDR clearly exceeds SDR white (1.0).</summary>
    private static byte[] BuildUltraHdr(out byte[] sdrRgb)
    {
        sdrRgb = GrayRampRgb(W, H);
        var hdr = new float[W * H * 3];
        for (var p = 0; p < W * H; p++)
        {
            var boost = p % W < W / 2 ? 1.0 : 4.0;
            var linear = (float)(SrgbEotf(sdrRgb[p * 3] / 255.0) * boost);
            hdr[p * 3] = hdr[p * 3 + 1] = hdr[p * 3 + 2] = linear;
        }

        var computed = JpegGainMap.Compute(hdr, sdrRgb, W, H);
        return JpegGainMap.Assemble(
            EncodeJpeg(sdrRgb, W, H),
            EncodeJpeg(GrayToRgb(computed.GainMapGray8), computed.Width, computed.Height),
            computed.Metadata);
    }

    // ------------------------------------------------------------- float path

    [Fact]
    public void TryDecode_On_Ultra_Hdr_Presents_The_Hdr_Float_Tier()
    {
        var ultraHdr = BuildUltraHdr(out _);

        SimdCodecs.TryDecode(ultraHdr, out var image).ShouldBeTrue();
        image.ShouldNotBeNull();
        image.ShouldBeOfType<GainMapDecodedImage>();

        image.Width.ShouldBe(W);
        image.Height.ShouldBe(H);
        image.SampleFormat.ShouldBe(SampleFormat.Float32);
        image.Channels.ShouldBe(3);
        image.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        image.ColorEncoding.Float.ShouldBe(FloatSemantics.DisplayReferred);
    }

    [Fact]
    public void ToFloats_Reconstructs_Hdr_Beyond_Sdr_White()
    {
        var ultraHdr = BuildUltraHdr(out _);
        SimdCodecs.TryDecode(ultraHdr, out var image).ShouldBeTrue();

        var floats = image!.ToFloats();

        // The boosted right half must push highlights above 1.0 — the whole point
        // of asking for floats. A plain-JPEG decode (normalized [0,1]) never could.
        floats.Max().ShouldBeGreaterThan(1.0f);
    }

    [Fact]
    public void Facade_ToFloats_Equals_Reconstruct_At_Full_Authored_Headroom()
    {
        var ultraHdr = BuildUltraHdr(out _);
        SimdCodecs.TryDecode(ultraHdr, out var image).ShouldBeTrue();

        // The facade must reconstruct at HdrCapacityMax (the full authored HDR),
        // identical to going through the gain-map API by hand.
        JpegGainMap.TryRead(ultraHdr, out var gm).ShouldBeTrue();
        var expected = gm!.ReconstructHdr(gm.Metadata.HdrCapacityMax).ToFloats();

        image!.ToFloats().ShouldBe(expected);
    }

    [Fact]
    public void Source_Escape_Hatch_Exposes_The_Full_Gain_Map_Api()
    {
        var ultraHdr = BuildUltraHdr(out _);
        SimdCodecs.TryDecode(ultraHdr, out var image).ShouldBeTrue();

        var gainMap = image.ShouldBeOfType<GainMapDecodedImage>().Source;
        gainMap.Metadata.HdrCapacityMax.ShouldBeGreaterThan(1.0);
        // A different display headroom is reachable without re-parsing the file.
        gainMap.ReconstructHdr(1.0).SampleFormat.ShouldBe(SampleFormat.Float32);
    }

    // --------------------------------------------------------------- int path

    [Fact]
    public void ToRgba8_Returns_The_Sdr_Base_Not_A_Tonemap()
    {
        var ultraHdr = BuildUltraHdr(out _);
        SimdCodecs.TryDecode(ultraHdr, out var image).ShouldBeTrue();

        // The 8-bit path is the SDR base — byte-identical to decoding the primary
        // image as a plain JPEG (what any gain-map-unaware viewer shows).
        var sdrBase = JpegDecoder.Decode(ultraHdr);
        image!.ToRgba8().ShouldBe(sdrBase.Pixels.ToArray());
    }

    [Fact]
    public void TryDecodeIntoRgba8_Fills_The_Sdr_Base()
    {
        var ultraHdr = BuildUltraHdr(out _);
        SimdCodecs.TryReadInfo(ultraHdr, out var info).ShouldBeTrue();

        var buffer = new byte[info.Width * info.Height * 4];
        SimdCodecs.TryDecodeIntoRgba8(ultraHdr, buffer).ShouldBeTrue();

        var sdrBase = JpegDecoder.Decode(ultraHdr);
        buffer.ShouldBe(sdrBase.Pixels.ToArray());
    }

    [Fact]
    public void TryReadInfo_Reports_The_Hdr_Float_Tier()
    {
        var ultraHdr = BuildUltraHdr(out _);

        SimdCodecs.TryReadInfo(ultraHdr, out var info).ShouldBeTrue();

        info.Width.ShouldBe(W);
        info.Height.ShouldBe(H);
        info.Channels.ShouldBe(3);
        info.SampleFormat.ShouldBe(SampleFormat.Float32);
    }

    // ----------------------------------------------------- plain-JPEG regression

    [Fact]
    public void Plain_Jpeg_Is_Unaffected_By_The_Gain_Map_Entry()
    {
        var plain = EncodeJpeg(GrayRampRgb(48, 24), 48, 24);

        SimdCodecs.TryDecode(plain, out var image).ShouldBeTrue();

        // Falls through to the base JPEG decoder: ordinary 8-bit RGBA, not HDR.
        image.ShouldBeOfType<RasterImage>();
        image!.SampleFormat.ShouldBe(SampleFormat.UInt8);
        image.Channels.ShouldBe(4);

        SimdCodecs.TryReadInfo(plain, out var info).ShouldBeTrue();
        info.SampleFormat.ShouldBe(SampleFormat.UInt8);
        info.Channels.ShouldBe(4);
    }

    [Fact]
    public void Plain_Jpeg_Still_Decodes_Into_Rgba8()
    {
        var plain = EncodeJpeg(GrayRampRgb(48, 24), 48, 24);
        SimdCodecs.TryReadInfo(plain, out var info).ShouldBeTrue();

        var buffer = new byte[info.Width * info.Height * 4];
        SimdCodecs.TryDecodeIntoRgba8(plain, buffer).ShouldBeTrue();

        buffer.ShouldBe(JpegDecoder.Decode(plain).Pixels.ToArray());
    }
}
