using System;
using System.IO;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Facade sniff + dispatch tests. PNG cases round-trip through <see cref="PngWriter"/>
/// (encode -> facade decode); JPEG cases decode a synthetic photo encoded in-test with Magick.NET.
/// </summary>
public sealed class SimdCodecsTests
{
    private static readonly Lazy<byte[]> JpegFixture = new(() =>
        JpegDecoderOracleTests.EncodeJpeg(SyntheticPhoto.Rgb(609, 406), 609, 406, quality: 90, sampling: "4:2:0"));

    // --- sniffing ---

    [Fact]
    public void CanDecode_recognises_png_and_jpeg_signatures()
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF, 0xE0];

        SimdCodecs.CanDecode(png).ShouldBeTrue();
        SimdCodecs.CanDecode(jpeg).ShouldBeTrue();
    }

    [Fact]
    public void Unknown_format_is_rejected_by_every_entry_point()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        SimdCodecs.CanDecode(garbage).ShouldBeFalse();
        SimdCodecs.TryDecode(garbage, out var img).ShouldBeFalse();
        img.ShouldBeNull();
        SimdCodecs.TryReadInfo(garbage, out _).ShouldBeFalse();
        SimdCodecs.TryDecodeIntoRgba8(garbage, new byte[64]).ShouldBeFalse();
    }

    // --- PNG round-trips ---

    [Fact]
    public void Png_rgba8_round_trips_via_facade()
    {
        byte[] rgba = [10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 128, 100, 110, 120, 255];
        var png = PngWriter.Encode(rgba, 2, 2);

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.Width.ShouldBe(2);
        img.Height.ShouldBe(2);
        img.Channels.ShouldBe(4);
        img.SampleFormat.ShouldBe(SampleFormat.UInt8);
        img.Pixels.ToArray().ShouldBe(rgba);
        img.ToRgba8().ShouldBe(rgba);
    }

    [Fact]
    public void Png_gray8_decodes_as_single_channel()
    {
        byte[] gray = [0, 64, 128, 255];
        var png = PngWriter.EncodeGray8(gray, 2, 2);

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.Channels.ShouldBe(1);
        img.SampleFormat.ShouldBe(SampleFormat.UInt8);
        img.Pixels.ToArray().ShouldBe(gray);
        img.ToRgba8().ShouldBe([0, 0, 0, 255, 64, 64, 64, 255, 128, 128, 128, 255, 255, 255, 255, 255]);
    }

    [Fact]
    public void Png_gray16_preserves_host_order_and_truncates_on_rgba8()
    {
        ushort[] gray = [0x1234, 0xABCD, 0x00FF, 0xFFFF];
        var png = PngWriter.EncodeGray16(gray, 2, 2);

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.Channels.ShouldBe(1);
        img.SampleFormat.ShouldBe(SampleFormat.UInt16);
        MemoryMarshal.Cast<byte, ushort>(img.Pixels).ToArray().ShouldBe(gray); // host-order round-trip
        img.ToRgba8().ShouldBe([0x12, 0x12, 0x12, 255, 0xAB, 0xAB, 0xAB, 255, 0x00, 0x00, 0x00, 255, 0xFF, 0xFF, 0xFF, 255]);
    }

    [Fact]
    public void Png_rgba16_round_trips_as_4ch_u16()
    {
        ushort[] rgba = [0x1111, 0x2222, 0x3333, 0xFFFF, 0x4444, 0x5555, 0x6666, 0x7777];
        var png = PngWriter.EncodeRgba16(rgba, 2, 1);

        SimdCodecs.TryDecode(png, out var img).ShouldBeTrue();
        img!.Channels.ShouldBe(4);
        img.SampleFormat.ShouldBe(SampleFormat.UInt16);
        MemoryMarshal.Cast<byte, ushort>(img.Pixels).ToArray().ShouldBe(rgba);
    }

    [Fact]
    public void Png_TryReadInfo_reads_dimensions_without_decoding()
    {
        var png = PngWriter.EncodeGray16(new ushort[6], 3, 2);

        SimdCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
        info.Width.ShouldBe(3);
        info.Height.ShouldBe(2);
        info.Channels.ShouldBe(1);
        info.SampleFormat.ShouldBe(SampleFormat.UInt16);
    }

    [Fact]
    public void Png_TryDecodeIntoRgba8_matches_ToRgba8()
    {
        byte[] rgba = [10, 20, 30, 255, 40, 50, 60, 128];
        var png = PngWriter.Encode(rgba, 2, 1);
        SimdCodecs.TryReadInfo(png, out var info).ShouldBeTrue();

        var dst = new byte[info.Width * info.Height * 4];
        SimdCodecs.TryDecodeIntoRgba8(png, dst).ShouldBeTrue();
        dst.ShouldBe(rgba);
    }

    [Fact]
    public void Png_TryDecodeIntoRgba8_rejects_undersized_destination()
    {
        var png = PngWriter.Encode(new byte[2 * 2 * 4], 2, 2);

        SimdCodecs.TryDecodeIntoRgba8(png, new byte[8]).ShouldBeFalse(); // needs 16
    }

    // --- JPEG (synthetic photo) ---

    [Fact]
    public void Jpeg_decodes_as_rgba8_matching_ReadInfo()
    {
        var bytes = JpegFixture.Value;
        var expected = JpegDecoder.ReadInfo(bytes);

        SimdCodecs.TryDecode(bytes, out var img).ShouldBeTrue();
        img!.Width.ShouldBe(expected.Width);
        img.Height.ShouldBe(expected.Height);
        img.Channels.ShouldBe(4);
        img.SampleFormat.ShouldBe(SampleFormat.UInt8);
        img.Pixels.Length.ShouldBe(expected.Width * expected.Height * 4);
    }

    [Fact]
    public void Jpeg_TryDecodeIntoRgba8_matches_DecodeTo()
    {
        var bytes = JpegFixture.Value;
        SimdCodecs.TryReadInfo(bytes, out var info).ShouldBeTrue();

        var viaFacade = new byte[info.Width * info.Height * 4];
        SimdCodecs.TryDecodeIntoRgba8(bytes, viaFacade).ShouldBeTrue();

        var direct = new byte[info.Width * info.Height * 4];
        JpegDecoder.DecodeTo(bytes, direct);

        viaFacade.ShouldBe(direct);
    }
}
