using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Round-trip tests for the PNG ancillary chunks the writer learned to emit
/// in Phase 19 — sRGB, gAMA, cHRM, eXIf, and the PNG-3 HDR trio (cICP, mDCv,
/// cLLI). Each test encodes a tiny image with one chunk populated and
/// verifies <see cref="PngReader"/> extracts the same values back.
/// </summary>
public sealed class PngMetadataRoundTripTests
{
    private static readonly byte[] TinyRgba = new byte[4 * 4 * 4];

    [Fact]
    public void Srgb_RoundTrip()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { SrgbRenderingIntent = 1 });
        var img = PngReader.Decode(png);
        img.SrgbRenderingIntent.ShouldBe((byte)1);
    }

    [Fact]
    public void Gamma_RoundTrip()
    {
        // sRGB-style 1/2.2 ≈ 0.45455 — encoded as round(0.45455 × 100000) = 45455.
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Gamma = 0.45455 });
        var img = PngReader.Decode(png);
        img.Gamma.ShouldNotBeNull();
        img.Gamma!.Value.ShouldBe(0.45455, tolerance: 1e-5);
    }

    [Fact]
    public void Chromaticity_RoundTrip_PreservesPrimaries()
    {
        // BT.709 / sRGB primaries (per PNG spec example).
        var chrm = new ChromaticityChunk(
            WhiteX: 0.3127, WhiteY: 0.3290,
            RedX:   0.64,   RedY:   0.33,
            GreenX: 0.30,   GreenY: 0.60,
            BlueX:  0.15,   BlueY:  0.06);
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Chromaticity = chrm });
        var img = PngReader.Decode(png);
        img.Chromaticity.ShouldNotBeNull();
        img.Chromaticity!.WhiteX.ShouldBe(chrm.WhiteX, tolerance: 1e-5);
        img.Chromaticity.WhiteY.ShouldBe(chrm.WhiteY, tolerance: 1e-5);
        img.Chromaticity.RedX.ShouldBe(chrm.RedX, tolerance: 1e-5);
        img.Chromaticity.GreenY.ShouldBe(chrm.GreenY, tolerance: 1e-5);
        img.Chromaticity.BlueX.ShouldBe(chrm.BlueX, tolerance: 1e-5);
    }

    [Fact]
    public void Exif_RoundTrip_PreservesBlob()
    {
        var exif = new byte[64];
        for (var i = 0; i < exif.Length; i++) exif[i] = (byte)(0x42 + i);

        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Exif = exif });
        var img = PngReader.Decode(png);
        img.Exif.ShouldNotBeNull();
        img.Exif!.ShouldBe(exif);
    }

    [Fact]
    public void IccProfile_WithCustomKeyword()
    {
        var icc = new byte[100];
        for (var i = 0; i < icc.Length; i++) icc[i] = (byte)(0xC0 + (i & 0x3F));
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions
        {
            IccProfile = icc,
            IccProfileName = "Display-P3",
        });
        var img = PngReader.Decode(png);
        img.IccProfile!.ShouldBe(icc);
        img.IccProfileName.ShouldBe("Display-P3");
    }

    // ----------------------------------------------------------------------
    // PNG-3 HDR chunks
    // ----------------------------------------------------------------------

    [Fact]
    public void Cicp_Hdr10Pq_RoundTrip()
    {
        // The full HDR10 signal: BT.2020 + PQ + RGB + full-range.
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq });
        var img = PngReader.Decode(png);
        img.Cicp.ShouldBe(CicpChunk.Hdr10Pq);
        img.HasHdrSignaling.ShouldBeTrue();
    }

    [Fact]
    public void Cicp_Hlg_RoundTrip()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Cicp = CicpChunk.Bt2020Hlg });
        var img = PngReader.Decode(png);
        img.Cicp.ShouldBe(CicpChunk.Bt2020Hlg);
    }

    [Fact]
    public void Cicp_CustomValues()
    {
        var cicp = new CicpChunk(
            ColorPrimaries:     Sdcb.SimdCodecs.Abstractions.ColorPrimaries.BT2020,
            TransferFunction:   Sdcb.SimdCodecs.Abstractions.TransferFunction.Pq,
            MatrixCoefficients: Sdcb.SimdCodecs.Abstractions.MatrixCoefficients.Identity,
            VideoFullRangeFlag: true);
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Cicp = cicp });
        var img = PngReader.Decode(png);
        img.Cicp.ShouldBe(cicp);
    }

    [Fact]
    public void Mdcv_RoundTrip_HevcCanonicalValues()
    {
        // Typical "BT.2020 P3-D65 1000 cd/m² mastering display" values.
        var mdcv = new MdcvChunk(
            RedX:   34000, RedY:    16000,
            GreenX: 13250, GreenY:  34500,
            BlueX:  7500,  BlueY:    3000,
            WhitePointX: 15635, WhitePointY: 16450,
            MaxLuminanceUnits: 10_000_000u,  // 1000 cd/m²
            MinLuminanceUnits: 50u);          // 0.005 cd/m²
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Mdcv = mdcv });
        var img = PngReader.Decode(png);
        img.Mdcv.ShouldBe(mdcv);
    }

    [Fact]
    public void Clli_RoundTrip()
    {
        var clli = new ClliChunk(MaxCllUnits: 4_000_000u, MaxFallUnits: 800_000u);
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Clli = clli });
        var img = PngReader.Decode(png);
        img.Clli.ShouldBe(clli);
    }

    [Fact]
    public void Hdr10_FullSignaling_RoundTrip()
    {
        // The full HDR10 signaling triplet — what a real HDR10 PNG would carry.
        var options = new PngWriteOptions
        {
            Cicp = CicpChunk.Hdr10Pq,
            Mdcv = new MdcvChunk(34000, 16000, 13250, 34500, 7500, 3000, 15635, 16450,
                                 MaxLuminanceUnits: 10_000_000u, MinLuminanceUnits: 50u),
            Clli = new ClliChunk(4_000_000u, 800_000u),
        };

        // Use 16-bit RGBA to look like a real HDR PNG.
        var rgba16 = new ushort[16 * 16 * 4];
        var rng = new Random(unchecked((int)0x1607_2026));
        for (var i = 0; i < rgba16.Length; i++) rgba16[i] = (ushort)rng.Next(0, 65536);

        var png = PngWriter.EncodeRgba16(rgba16, 16, 16, options);
        var img = PngReader.Decode(png);

        img.BitDepth.ShouldBe(16);
        img.HasHdrSignaling.ShouldBeTrue();
        img.Cicp.ShouldBe(options.Cicp);
        img.Mdcv.ShouldBe(options.Mdcv);
        img.Clli.ShouldBe(options.Clli);
        img.AsUInt16Samples().ShouldBe(rgba16); // pixels survive the full HDR encode
    }

    [Fact]
    public void IccAndSrgb_OnlyIccpEmitted()
    {
        // PNG spec mandates iCCP and sRGB are mutually exclusive; when both
        // are set in the options, the encoder prefers iCCP (richer info).
        var icc = new byte[40];
        for (var i = 0; i < icc.Length; i++) icc[i] = (byte)i;
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions
        {
            IccProfile = icc,
            SrgbRenderingIntent = 0,
        });
        var img = PngReader.Decode(png);
        img.IccProfile.ShouldNotBeNull();
        img.SrgbRenderingIntent.ShouldBeNull(); // sRGB was suppressed
    }
}
