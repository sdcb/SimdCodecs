using System;
using System.Linq;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PixelConverter.StretchToRgba8(IDecodedImage)"/> and its range overloads: exact bytes for
/// hand-picked values, the edges of the range, non-finite samples and the degenerate-range fallback.
/// </summary>
public sealed class PixelStretchTests
{
    private static RasterImage FloatImage(int width, int height, params float[] rgba) =>
        new(width, height, 4, SampleFormat.Float32, MemoryMarshal.AsBytes(rgba.AsSpan()).ToArray());

    [Fact]
    public void Auto_range_maps_the_smallest_colour_to_0_and_the_largest_to_255()
    {
        // Colour spans -2..6 (alpha is outside it and does not count); one scale for all three channels.
        var image = FloatImage(3, 1,
            -2f, 0f, 2f, 1f,
            4f, 6f, 6f, 0.5f,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity, 2f);

        PixelConverter.TryGetColorRange(image.ToFloats(), out var min, out var max).ShouldBeTrue();
        (min, max).ShouldBe((-2f, 6f));

        PixelConverter.StretchToRgba8(image).ShouldBe(new byte[]
        {
            0, 64, 128, 255,    // (v + 2) × 255 / 8, rounded: 0, 63.75, 127.5
            191, 255, 255, 128, // 191.25; alpha 0.5 → 127.5 → 128
            0, 255, 0, 255,     // NaN → 0, +∞ → 255, −∞ → 0; alpha 2 clamps to 255
        });
    }

    [Fact]
    public void Explicit_range_clamps_values_outside_it()
    {
        var image = FloatImage(2, 1, -1f, 0f, 0.25f, 1f, 1f, 2f, 0.75f, -1f);

        PixelConverter.StretchToRgba8(image, 0f, 1f).ShouldBe(new byte[] { 0, 0, 64, 255, 255, 255, 191, 0 });
    }

    [Fact]
    public void Constant_or_non_finite_image_falls_back_to_0_to_1()
    {
        PixelConverter.StretchToRgba8(FloatImage(1, 1, 0.5f, 0.5f, 0.5f, 1f)).ShouldBe(new byte[] { 128, 128, 128, 255 });

        var nonFinite = FloatImage(1, 1, float.NaN, float.PositiveInfinity, float.NaN, 1f);
        PixelConverter.TryGetColorRange(nonFinite.ToFloats(), out var min, out var max).ShouldBeFalse();
        (min, max).ShouldBe((0f, 0f));
        PixelConverter.StretchToRgba8(nonFinite).ShouldBe(new byte[] { 0, 255, 0, 255 });
    }

    [Fact]
    public void Range_spanning_the_float_line_does_not_overflow()
    {
        var rgba = new[] { -float.MaxValue, float.MaxValue / 2, float.MaxValue, 1f };
        var destination = new byte[4];

        PixelConverter.StretchToRgba8(rgba, destination, -float.MaxValue, float.MaxValue);

        destination.ShouldBe(new byte[] { 0, 191, 255, 255 }); // 3/4 of the way: 191.25
    }

    [Fact]
    public void Integer_images_stretch_their_normalised_values()
    {
        // 8-bit grey 64..192 normalises to 0.25..0.75 and stretches onto the full range.
        var grey = new RasterImage(2, 1, 1, SampleFormat.UInt8, [64, 192]);

        PixelConverter.StretchToRgba8(grey).ShouldBe(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 });
    }

    [Fact]
    public void Srgb_encoding_applies_the_IEC_61966_2_1_curve_to_colour_only()
    {
        // Linear 0.5 is sRGB 0.7354 (188), 18% grey 0.4614 (118), 0.001 is on the linear toe (12.92 × 0.001 → 3).
        var image = FloatImage(3, 1, 0f, 0.5f, 1f, 0.5f, 0.18f, 0.001f, 2f, 1f, float.NaN, float.PositiveInfinity, -1f, 0.25f);

        PixelConverter.StretchToRgba8(image, 0f, 1f, srgbEncode: true).ShouldBe(new byte[]
        {
            0, 188, 255, 128, // alpha is never encoded: 0.5 → 128
            118, 3, 255, 255,
            0, 255, 0, 64,
        });
        PixelConverter.StretchToRgba8(image, 0f, 1f).ShouldBe(new byte[]
        {
            0, 128, 255, 128,
            46, 0, 255, 255,
            0, 255, 0, 64,
        });
    }

    [Fact]
    public void Srgb_encoding_matches_the_curve_on_every_16_bit_step()
    {
        var rgba = new float[65536 * 4];
        for (var i = 0; i < 65536; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = i / 65535f;
            rgba[i * 4 + 3] = 1f;
        }

        var destination = new byte[rgba.Length];
        PixelConverter.StretchToRgba8(rgba, destination, 0f, 1f, srgbEncode: true);

        for (var i = 0; i < 65536; i++)
        {
            var t = Math.Round((double)(i / 65535f) * 65535) / 65535;
            var encoded = t <= 0.0031308 ? 12.92 * t : 1.055 * Math.Pow(t, 1 / 2.4) - 0.055;
            destination[i * 4].ShouldBe((byte)Math.Round(encoded * 255, MidpointRounding.AwayFromZero), $"step {i}");
        }
    }

    [Fact]
    public void Srgb_encoding_brightens_a_reconstructed_gain_map_hdr()
    {
        // The reconstructed HDR is linear light: stretched as is, its mid-tones sit far below the SDR base's.
        SimdCodecs.TryDecode(GainMapFacadeTests.BuildUltraHdr(out _), out var hdr).ShouldBeTrue();
        hdr.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);

        var plain = PixelConverter.StretchToRgba8(hdr);
        var encoded = PixelConverter.StretchToRgba8(hdr, srgbEncode: true);

        Mean(encoded).ShouldBeGreaterThan(Mean(plain) + 30);
    }

    private static double Mean(byte[] rgba) =>
        Enumerable.Range(0, rgba.Length / 4).Average(p => (rgba[p * 4] + rgba[p * 4 + 1] + rgba[p * 4 + 2]) / 3.0);

    [Fact]
    public void Bad_arguments_throw()
    {
        var destination = new byte[4];
        Should.Throw<ArgumentException>(() => PixelConverter.StretchToRgba8(new float[4], destination, 1f, 1f));
        Should.Throw<ArgumentException>(() => PixelConverter.StretchToRgba8(new float[4], destination, 0f, float.NaN));
        Should.Throw<ArgumentException>(() => PixelConverter.StretchToRgba8(new float[4], destination, 0f, float.PositiveInfinity));
        Should.Throw<ArgumentException>(() => PixelConverter.StretchToRgba8(new float[3], destination, 0f, 1f));
        Should.Throw<ArgumentException>(() => PixelConverter.StretchToRgba8(new float[8], destination, 0f, 1f));
        Should.Throw<ArgumentException>(() => PixelConverter.TryGetColorRange(new float[5], out _, out _));
    }
}
