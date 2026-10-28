using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The fidelity tier of a greyscale JPEG (one SOF component): <see cref="IImageDecoder.TryDecode"/> keeps
/// its one channel, the samples ImageMagick (libjpeg) encoded, rather than widening them to RGBA; the
/// display tier is unchanged.
/// </summary>
public sealed class JpegGreyFidelityTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 19)]
    [InlineData(70, 33)]
    public void GreyJpegDecodesToItsOneChannel(int width, int height)
    {
        var jpeg = JpegDecoderOracleTests.EncodeJpeg(JpegDecoderOracleTests.MakeRgbPattern(width, height), width, height, gray: true);
        JpegDecoder.ReadInfo(jpeg).Components.ShouldBe(1);

        SimdCodecs.TryReadInfo(jpeg, out var info).ShouldBeTrue();
        (info.Channels, info.SampleFormat).ShouldBe((1, SampleFormat.UInt8));
        SimdCodecs.TryDecode(jpeg, out var image).ShouldBeTrue();
        (image.Channels, image.SampleFormat).ShouldBe((1, SampleFormat.UInt8));
        // Its samples are the frame's one plane, and the display projection is the RGBA decode.
        image.Pixels.ToArray().ShouldBe(JpegDiagnostics.DecodePlanes(jpeg)[0].Samples);
        image.ToRgba8().ShouldBe(JpegDecoder.Decode(jpeg).Pixels);
    }
}
