using System;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

public sealed class RasterImageTests
{
    [Fact]
    public void Gray8_expands_across_rgb_opaque()
    {
        // 2x1 gray: [10, 200]
        var img = new RasterImage(2, 1, 1, SampleFormat.UInt8, [10, 200]);

        var rgba = img.ToRgba8();

        rgba.Length.ShouldBe(2 * 1 * 4);
        rgba.ShouldBe([10, 10, 10, 255, 200, 200, 200, 255]);
    }

    [Fact]
    public void GrayAlpha8_maps_second_channel_to_alpha()
    {
        // 1x1 gray+alpha: gray 128, alpha 64
        var img = new RasterImage(1, 1, 2, SampleFormat.UInt8, [128, 64]);

        img.ToRgba8().ShouldBe([128, 128, 128, 64]);
    }

    [Fact]
    public void Rgb8_copies_and_sets_opaque_alpha()
    {
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [1, 2, 3]);

        img.ToRgba8().ShouldBe([1, 2, 3, 255]);
    }

    [Fact]
    public void Rgba8_round_trips_verbatim()
    {
        var img = new RasterImage(1, 1, 4, SampleFormat.UInt8, [1, 2, 3, 4]);

        img.ToRgba8().ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public void UInt16_gray_scales_high_byte()
    {
        // one 16-bit gray sample = 0xABCD (little-endian bytes CD AB); >> 8 -> 0xAB
        var img = new RasterImage(1, 1, 1, SampleFormat.UInt16, [0xCD, 0xAB]);

        img.ToRgba8().ShouldBe([0xAB, 0xAB, 0xAB, 255]);
    }

    [Fact]
    public void UInt16_rgb_scales_each_channel()
    {
        // R=0x1234, G=0x5678, B=0x9ABC  ->  0x12, 0x56, 0x9A
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt16, [0x34, 0x12, 0x78, 0x56, 0xBC, 0x9A]);

        img.ToRgba8().ShouldBe([0x12, 0x56, 0x9A, 255]);
    }

    [Fact]
    public void ExpandToRgba8_writes_into_caller_buffer_zero_copy_style()
    {
        var img = new RasterImage(1, 1, 3, SampleFormat.UInt8, [7, 8, 9]);
        Span<byte> dst = stackalloc byte[4];

        img.ExpandToRgba8(dst);

        dst.ToArray().ShouldBe([7, 8, 9, 255]);
    }

    [Fact]
    public void Float32_has_no_canonical_rgba_mapping()
    {
        var img = new RasterImage(1, 1, 1, SampleFormat.Float32, new byte[4]);

        Should.Throw<NotSupportedException>(() => img.ToRgba8());
    }

    [Fact]
    public void IccProfile_defaults_empty_and_round_trips_when_present()
    {
        new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0]).IccProfile.IsEmpty.ShouldBeTrue();

        byte[] icc = [1, 2, 3];
        new RasterImage(1, 1, 3, SampleFormat.UInt8, [0, 0, 0], icc).IccProfile.ToArray().ShouldBe(icc);
    }

    [Theory]
    [InlineData(SampleFormat.UInt8, 1)]
    [InlineData(SampleFormat.UInt16, 2)]
    [InlineData(SampleFormat.Float32, 4)]
    public void BytesPerSample_matches_format(SampleFormat format, int expected)
        => RasterImage.BytesPerSample(format).ShouldBe(expected);

    [Fact]
    public void Ctor_rejects_undersized_pixel_buffer()
        // 2x2 RGB needs 12 bytes; give it 3
        => Should.Throw<ArgumentException>(() => new RasterImage(2, 2, 3, SampleFormat.UInt8, [0, 0, 0]));

    [Theory]
    [InlineData(0, 1, 3)]
    [InlineData(1, 0, 3)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 5)]
    public void Ctor_rejects_bad_dimensions(int w, int h, int channels)
        => Should.Throw<ArgumentOutOfRangeException>(() => new RasterImage(w, h, channels, SampleFormat.UInt8, new byte[64]));

    [Fact]
    public void ExpandToRgba8_rejects_undersized_destination()
    {
        var img = new RasterImage(2, 2, 3, SampleFormat.UInt8, new byte[12]);

        Should.Throw<ArgumentException>(() =>
        {
            var tooSmall = new byte[8]; // needs 2*2*4 = 16
            img.ExpandToRgba8(tooSmall);
        });
    }

    // ------------------------------------------------------------- ToFloats
    // Container-only widening: meaning (ColorEncoding) is untouched — ints
    // normalize to [0,1] endpoint-exact, floats pass through unclamped.

    [Fact]
    public void ToFloats_gray8_broadcasts_and_normalizes_endpoint_exact()
    {
        var img = new RasterImage(2, 1, 1, SampleFormat.UInt8, [0, 255]);

        img.ToFloats().ShouldBe([0f, 0f, 0f, 1f, 1f, 1f, 1f, 1f]);
    }

    [Fact]
    public void ToFloats_rgba8_normalizes_each_channel()
    {
        var img = new RasterImage(1, 1, 4, SampleFormat.UInt8, [51, 102, 153, 204]);

        img.ToFloats().ShouldBe([51 / 255f, 102 / 255f, 153 / 255f, 204 / 255f]);
    }

    [Fact]
    public void ToFloats_grayalpha8_maps_second_channel_to_alpha()
    {
        var img = new RasterImage(1, 1, 2, SampleFormat.UInt8, [255, 51]);

        img.ToFloats().ShouldBe([1f, 1f, 1f, 51 / 255f]);
    }

    [Fact]
    public void ToFloats_uint16_normalizes_endpoint_exact()
    {
        // gray samples 0x0000 and 0xFFFF (host little-endian bytes)
        var img = new RasterImage(2, 1, 1, SampleFormat.UInt16, [0x00, 0x00, 0xFF, 0xFF]);

        var f = img.ToFloats();

        f[0].ShouldBe(0f);
        f[3].ShouldBe(1f);  // synthesized alpha
        f[4].ShouldBe(1f);  // 65535/65535f is exactly 1f
    }

    [Fact]
    public void ToFloats_float32_passes_hdr_values_through_unclamped()
    {
        // gray float pixels: 4.5 (HDR highlight) and -0.25 (wide-gamut negative)
        var px = new byte[8];
        BitConverter.TryWriteBytes(px.AsSpan(0), 4.5f);
        BitConverter.TryWriteBytes(px.AsSpan(4), -0.25f);
        var img = new RasterImage(2, 1, 1, SampleFormat.Float32, px);

        var f = img.ToFloats();

        f[0].ShouldBe(4.5f);    // broadcast across RGB
        f[2].ShouldBe(4.5f);
        f[3].ShouldBe(1f);      // synthesized alpha
        f[4].ShouldBe(-0.25f);  // NOT clamped
    }

    [Fact]
    public void ToFloats_rgb_float32_copies_verbatim_with_opaque_alpha()
    {
        var px = new byte[12];
        BitConverter.TryWriteBytes(px.AsSpan(0), 0.5f);
        BitConverter.TryWriteBytes(px.AsSpan(4), 2f);
        BitConverter.TryWriteBytes(px.AsSpan(8), 100f);
        var img = new RasterImage(1, 1, 3, SampleFormat.Float32, px);

        img.ToFloats().ShouldBe([0.5f, 2f, 100f, 1f]);
    }

    [Fact]
    public void ExpandToFloats_rejects_undersized_destination()
    {
        var img = new RasterImage(2, 2, 3, SampleFormat.UInt8, new byte[12]);

        Should.Throw<ArgumentException>(() =>
        {
            var tooSmall = new float[8]; // needs 2*2*4 = 16
            img.ExpandToFloats(tooSmall);
        });
    }
}
