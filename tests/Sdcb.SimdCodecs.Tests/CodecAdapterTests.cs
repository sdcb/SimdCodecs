using System;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// High-bit-depth and float TIFF decode through
/// <see cref="SimdCodecs"/> with correct layout, pixel fidelity, and
/// <see cref="IDecodedImage.ColorEncoding"/>; float-sample content refuses the
/// 8-bit display path (<see cref="SimdCodecs.TryDecodeIntoRgba8"/>) instead of
/// projecting HDR values wrongly.
/// </summary>
public sealed class CodecAdapterTests
{
    // ---------------------------------------------------------------- TIFF

    [Fact]
    public void Tiff_uint16_gray_decodes_through_facade()
    {
        int[] y = [0, 1000, 30000, 65535];
        var tiff = HdrTiff.Uint16Gray(2, 2, y);

        SimdCodecs.CanDecode(tiff).ShouldBeTrue();
        SimdCodecs.TryReadInfo(tiff, out var info).ShouldBeTrue();
        info.ShouldBe(new ImageInfo(2, 2, 1, SampleFormat.UInt16) { Format = ImageFormat.Tiff });

        SimdCodecs.TryDecode(tiff, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
        var f = img.ToFloats();
        f[0].ShouldBe(0f);
        f[4].ShouldBe(1000 / 65535f);
        f[12].ShouldBe(1f);
    }

    [Fact]
    public void Tiff_float32_gray_is_verbatim_scene_linear_and_refuses_rgba8()
    {
        float[] y = [0f, 0.5f, 2.5f, -0.25f];
        var tiff = HdrTiff.Float32Gray(2, 2, y);

        SimdCodecs.TryDecode(tiff, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.SampleFormat.ShouldBe(SampleFormat.Float32);
        img.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        img.ColorEncoding.Float.ShouldBe(FloatSemantics.SceneReferred);

        var f = img.ToFloats();
        f[4].ShouldBe(0.5f);
        f[8].ShouldBe(2.5f);    // HDR highlight, unclamped
        f[12].ShouldBe(-0.25f); // negative survives

        SimdCodecs.TryDecodeIntoRgba8(tiff, new byte[2 * 2 * 4]).ShouldBeFalse();
    }

    [Fact]
    public void Tiff_half_gray_widens_losslessly_to_float32()
    {
        Half[] y = [(Half)0.5f, (Half)1.5f, (Half)(-2f), (Half)0f];
        var tiff = HdrTiff.HalfGray(2, 2, y);

        SimdCodecs.TryDecode(tiff, out var img).ShouldBeTrue();
        img.ShouldNotBeNull();
        img.SampleFormat.ShouldBe(SampleFormat.Float32);

        var f = img.ToFloats();
        f[0].ShouldBe(0.5f);
        f[4].ShouldBe(1.5f);
        f[8].ShouldBe(-2f);
    }
}
