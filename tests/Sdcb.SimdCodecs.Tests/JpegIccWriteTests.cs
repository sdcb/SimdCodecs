using System;
using ImageMagick;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegEncodeOptions.IccProfile"/>: libjpeg (Magick.NET) reads back the profile we embed, in one
/// APP2 segment and in many; the pixels and, without the option, the bytes are those of a plain encode.
/// </summary>
public sealed class JpegIccWriteTests
{
    private static readonly byte[] Rgb = SyntheticPhoto.Rgb(64, 48);

    private static byte[] Profile(string name) =>
        ((ColorProfile)typeof(ColorProfiles).GetProperty(name)!.GetValue(null)!).ToByteArray();

    [Theory]
    [InlineData("AdobeRGB1998")]   // one segment
    [InlineData("USWebCoatedSWOP")] // several hundred KB: many segments
    public void Libjpeg_reads_back_the_embedded_profile(string name)
    {
        var profile = Profile(name);
        var jpeg = JpegEncoder.Encode(Rgb, 64, 48, 3, new JpegEncodeOptions { IccProfile = profile });

        using var image = new MagickImage(jpeg);
        image.GetColorProfile().ShouldNotBeNull().ToByteArray().ShouldBe(profile);
        JpegDecoder.ReadIccProfile(jpeg).ShouldBe(profile);
    }

    [Fact]
    public void Profile_changes_no_pixel_and_no_option_changes_no_byte()
    {
        var plain = JpegEncoder.Encode(Rgb, 64, 48, 3, new JpegEncodeOptions { Quality = 85 });
        var tagged = JpegEncoder.Encode(Rgb, 64, 48, 3, new JpegEncodeOptions { Quality = 85, IccProfile = Profile("USWebCoatedSWOP") });

        JpegDecoder.Decode(tagged).Pixels.ShouldBe(JpegDecoder.Decode(plain).Pixels);
        JpegEncoder.Encode(Rgb, 64, 48, 3, new JpegEncodeOptions { Quality = 85, IccProfile = null }).ShouldBe(plain);
    }

    [Fact]
    public void Exactly_full_segments_and_the_limits()
    {
        var two = new byte[2 * JpegEncodeOptions.MaxIccChunkBytes];
        new Random(5).NextBytes(two);
        var jpeg = JpegEncoder.Encode(Rgb, 64, 48, 3, new JpegEncodeOptions { IccProfile = two });
        JpegDecoder.ReadIccProfile(jpeg).ShouldBe(two);

        Should.Throw<ArgumentException>(() => JpegEncoder.Encode(Rgb, 64, 48, 3, new JpegEncodeOptions { IccProfile = Array.Empty<byte>() }));
        Should.Throw<ArgumentException>(() => JpegEncoder.Encode(Rgb, 64, 48, 3,
            new JpegEncodeOptions { IccProfile = new byte[JpegEncodeOptions.MaxIccProfileBytes + 1] }));
    }
}
