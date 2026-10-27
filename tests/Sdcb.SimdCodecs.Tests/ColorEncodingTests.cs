using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="IDecodedImage.ColorEncoding"/> — the meaning tier beside the
/// container tier. The key property under test: PNG-3 colour signalling (cICP,
/// gAMA 1.0) survives the codec-neutral <see cref="SimdCodecs"/> facade instead
/// of being dropped at the adapter boundary, and untagged content lands on the
/// same sRGB assumption consumers already baked in.
/// </summary>
public sealed class ColorEncodingTests
{
    private static readonly byte[] TinyRgba = new byte[4 * 4 * 4];

    [Fact]
    public void RasterImage_defaults_to_the_assumed_srgb_encoding()
    {
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0]);

        img.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
        img.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT709);
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Srgb);
        img.ColorEncoding.FullRange.ShouldBeTrue();
        img.ColorEncoding.Float.ShouldBe(FloatSemantics.NotApplicable);
    }

    [Fact]
    public void RasterImage_carries_an_explicit_encoding_verbatim()
    {
        var pq = new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Pq };
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0], iccProfile: null, colorEncoding: pq);

        img.ColorEncoding.ShouldBeSameAs(pq);
    }

    [Fact]
    public void Png_cicp_hdr10_survives_the_facade_decode()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq });

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT2020);
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Pq);
        img.ColorEncoding.FullRange.ShouldBeTrue();
    }

    [Fact]
    public void Untagged_png_lands_on_the_assumed_srgb_encoding()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions());

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
    }

    [Fact]
    public void Png_gamma_one_maps_to_the_linear_transfer_codepoint()
    {
        // gAMA 1.0 = code value proportional to light intensity (e.g. a
        // linear-light astro master stored as 16-bit PNG).
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Gamma = 1.0 });

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        img.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT709);
    }

    [Fact]
    public void Hdr_tagged_png_refuses_the_display_path_but_keeps_fidelity()
    {
        // PQ code values through >>8 would render crushed/wrong-gamut - the
        // display path refuses (same contract as the float codecs) while the
        // fidelity path decodes with the tag attached.
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq });

        SimdCodecs.TryDecodeIntoRgba8(png, new byte[4 * 4 * 4]).ShouldBeFalse();
        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Pq);
    }

    [Fact]
    public void Srgb_and_linear_tagged_pngs_keep_the_display_path()
    {
        var untagged = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions());
        SimdCodecs.TryDecodeIntoRgba8(untagged, new byte[4 * 4 * 4]).ShouldBeTrue();

        var explicitSrgb = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Cicp = CicpChunk.Srgb });
        SimdCodecs.TryDecodeIntoRgba8(explicitSrgb, new byte[4 * 4 * 4]).ShouldBeTrue();

        // gAMA 1.0 (linear astro master): monotone code dump stays available.
        var linear = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Gamma = 1.0 });
        SimdCodecs.TryDecodeIntoRgba8(linear, new byte[4 * 4 * 4]).ShouldBeTrue();
    }

    [Fact]
    public void Png_cicp_takes_precedence_over_gamma()
    {
        // PNG-3 gives cICP precedence over gAMA when both are present.
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions
        {
            Cicp = CicpChunk.Bt2020Hlg,
            Gamma = 1.0,
        });

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Hlg);
        img.ColorEncoding.Primaries.ShouldBe(ColorPrimaries.BT2020);
    }
}
