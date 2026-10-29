using System;
using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="IccProfiles.TryGetPrimaries"/> on the profiles Magick.NET ships and on the bundled one,
/// on colorants as published for Display P3 and BT.2020 profiles, and its use by the gain-map decode,
/// whose reconstructed HDR shares the SDR base's primaries.
/// </summary>
public sealed class IccPrimariesTests
{
    private static byte[] Magick(string name) =>
        ((ColorProfile)typeof(ColorProfiles).GetProperty(name)!.GetValue(null)!).ToByteArray();

    [Fact]
    public void Recognises_srgb_profiles_from_their_colorants()
    {
        IccProfiles.TryGetPrimaries(Magick("SRGB"), out var magick).ShouldBeTrue();
        magick.ShouldBe(ColorPrimaries.BT709);
        IccProfiles.TryGetPrimaries(IccProfiles.SRgbV4.Span, out var bundled).ShouldBeTrue();
        bundled.ShouldBe(ColorPrimaries.BT709);
    }

    [Theory]
    [InlineData("AdobeRGB1998")]
    [InlineData("AppleRGB")]
    [InlineData("ColorMatchRGB")]
    [InlineData("CoatedFOGRA39")] // CMYK
    public void Other_or_non_rgb_profiles_are_unrecognised(string name)
    {
        IccProfiles.TryGetPrimaries(Magick(name), out var primaries).ShouldBeFalse();
        primaries.ShouldBe(ColorPrimaries.Unspecified);
    }

    [Fact]
    public void Recognises_display_p3_and_bt2020_colorants()
    {
        // D50-adapted colorants as Apple's Display P3 profile and common Rec. 2020 profiles store them.
        IccProfiles.TryGetPrimaries(DisplayP3, out var p3).ShouldBeTrue();
        p3.ShouldBe(ColorPrimaries.DisplayP3);
        IccProfiles.TryGetPrimaries(
            WithColorants(IccProfiles.SRgbV4.ToArray(), (0.673459, 0.279033, -0.001931), (0.165661, 0.675338, 0.029979), (0.125100, 0.045629, 0.797162)),
            out var bt2020).ShouldBeTrue();
        bt2020.ShouldBe(ColorPrimaries.BT2020);
    }

    [Fact]
    public void A_cicp_tag_is_taken_as_it_stands()
    {
        var tagged = IccProfiles.WithCicp(IccProfiles.SRgbV4.Span, ColorPrimaries.BT2020, TransferFunction.Pq);
        IccProfiles.TryGetPrimaries(tagged, out var primaries).ShouldBeTrue();
        primaries.ShouldBe(ColorPrimaries.BT2020);
    }

    [Fact]
    public void Malformed_input_is_false_and_never_throws()
    {
        IccProfiles.TryGetPrimaries([], out _).ShouldBeFalse();
        IccProfiles.TryGetPrimaries(new byte[200], out _).ShouldBeFalse();
        var random = new Random(77);
        var seed = DisplayP3;
        for (var i = 0; i < 20000; i++)
        {
            var bytes = (byte[])seed.Clone();
            for (var m = random.Next(1, 8); m > 0; m--)
                bytes[random.Next(0, bytes.Length)] = (byte)random.Next(256);
            IccProfiles.TryGetPrimaries(bytes.AsSpan(0, random.Next(bytes.Length + 1)), out _);
        }
    }

    [Fact]
    public void Gain_map_hdr_reports_the_base_primaries()
    {
        GainMapPrimaries(icc: null).ShouldBe(ColorPrimaries.BT709);
        GainMapPrimaries(IccProfiles.SRgbV4.ToArray()).ShouldBe(ColorPrimaries.BT709);
        GainMapPrimaries(DisplayP3).ShouldBe(ColorPrimaries.DisplayP3);
        GainMapPrimaries(Magick("AdobeRGB1998")).ShouldBe(ColorPrimaries.Unspecified);
    }

    private static ColorPrimaries GainMapPrimaries(byte[]? icc)
    {
        var ultraHdr = GainMapFacadeTests.BuildUltraHdr(out _);
        if (icc is not null)
        {
            // The ICC profile goes into the primary (SDR base) image, ahead of its gain-map XMP.
            ultraHdr = JpegIccInjector.EmbedIccProfile(ultraHdr, icc);
        }

        SimdCodecs.TryDecode(ultraHdr, out var image).ShouldBeTrue();
        image.SampleFormat.ShouldBe(SampleFormat.Float32);
        image.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        return image.ColorEncoding.Primaries;
    }

    private static byte[] DisplayP3 { get; } =
        WithColorants(IccProfiles.SRgbV4.ToArray(), (0.515121, 0.241196, -0.001053), (0.291977, 0.692245, 0.041885), (0.157104, 0.066574, 0.784073));

    // The bundled sRGB profile with its rXYZ / gXYZ / bXYZ values replaced.
    private static byte[] WithColorants(byte[] profile, (double X, double Y, double Z) r, (double X, double Y, double Z) g, (double X, double Y, double Z) b)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var entry = profile.AsSpan(132 + 12 * i, 12);
            (double X, double Y, double Z)? value = System.Text.Encoding.ASCII.GetString(entry[..4]) switch
            {
                "rXYZ" => r,
                "gXYZ" => g,
                "bXYZ" => b,
                _ => null,
            };
            if (value is not { } v)
                continue;
            var offset = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + 8), (int)Math.Round(v.X * 65536));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + 12), (int)Math.Round(v.Y * 65536));
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + 16), (int)Math.Round(v.Z * 65536));
        }

        return profile;
    }
}
