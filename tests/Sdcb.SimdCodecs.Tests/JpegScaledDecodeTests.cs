using System.Buffers;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Scaled-decode (<see cref="JpegScale.Half"/>/<see cref="JpegScale.Quarter"/>/
/// <see cref="JpegScale.Eighth"/>) validation. There is no byte-exact oracle for
/// this path — the reduced IDCT is a clean-room DCT-domain decimation, not a
/// port — so correctness is established by properties instead:
/// flat inputs decode to the flat colour at every scale, smooth content matches
/// a full-decode-then-box-downsample reference within a tight tolerance, and
/// dimensions follow the ceil(dim/factor) contract for awkward sizes.
/// </summary>
public sealed class JpegScaledDecodeTests
{
    private static byte[] MakeSmoothRgb(int w, int h)
    {
        // Strictly smooth content (no checker, no noise): keeps the spectral
        // truncation vs box-average comparison tight.
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                rgb[i + 0] = (byte)(x * 255 / Math.Max(1, w - 1));
                rgb[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                rgb[i + 2] = (byte)(128 + 90 * Math.Sin(x * 0.10) * Math.Cos(y * 0.13));
            }
        }

        return rgb;
    }

    private static byte[] BoxDownsampleRgba(byte[] rgba, int w, int h, int s)
    {
        var ow = (w + s - 1) / s;
        var oh = (h + s - 1) / s;
        var outPx = new byte[ow * oh * 4];
        for (var oy = 0; oy < oh; oy++)
        {
            for (var ox = 0; ox < ow; ox++)
            {
                for (var ch = 0; ch < 4; ch++)
                {
                    var sum = 0;
                    var n = 0;
                    for (var dy = 0; dy < s; dy++)
                    {
                        for (var dx = 0; dx < s; dx++)
                        {
                            var sx = ox * s + dx;
                            var sy = oy * s + dy;
                            if (sx >= w || sy >= h)
                                continue;
                            sum += rgba[(sy * w + sx) * 4 + ch];
                            n++;
                        }
                    }

                    outPx[(oy * ow + ox) * 4 + ch] = (byte)((sum + n / 2) / n);
                }
            }
        }

        return outPx;
    }

    [Theory]
    [InlineData(JpegScale.Half)]
    [InlineData(JpegScale.Quarter)]
    [InlineData(JpegScale.Eighth)]
    public void FlatColour_DecodesFlatAtEveryScale(JpegScale scale)
    {
        const int w = 64, h = 64;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3 + 0] = 180;
            rgb[i * 3 + 1] = 90;
            rgb[i * 3 + 2] = 30;
        }

        var jpeg = JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 95, "4:4:4");
        var full = JpegDecoder.Decode(jpeg);
        var scaled = JpegDecoder.Decode(jpeg, scale);

        var s = (int)scale;
        scaled.Width.ShouldBe(w / s);
        scaled.Height.ShouldBe(h / s);

        // Every scaled pixel within ±2 of the full decode's flat value; alpha exact.
        for (var i = 0; i < scaled.Width * scaled.Height; i++)
        {
            for (var ch = 0; ch < 3; ch++)
                Math.Abs(scaled.Pixels[i * 4 + ch] - full.Pixels[ch]).ShouldBeLessThanOrEqualTo(2,
                    $"{scale}: pixel {i} channel {ch}");
            scaled.Pixels[i * 4 + 3].ShouldBe((byte)255);
        }
    }

    [Theory]
    [InlineData(JpegScale.Half, "4:4:4", false, 2.5, 24)]
    [InlineData(JpegScale.Quarter, "4:4:4", false, 2.5, 24)]
    [InlineData(JpegScale.Eighth, "4:4:4", false, 2.5, 24)]
    [InlineData(JpegScale.Half, "4:2:0", false, 2.5, 24)]
    [InlineData(JpegScale.Quarter, "4:2:0", false, 2.5, 24)]
    // At 1/8 with 4:2:0 each chroma sample spans a 16×16 source footprint before
    // triangular upsampling, so scaled output is intrinsically smoother than a
    // box reference — same trade-off as libjpeg's scaled decode. Looser bars.
    [InlineData(JpegScale.Eighth, "4:2:0", false, 8.0, 64)]
    [InlineData(JpegScale.Quarter, "4:2:0", true, 2.5, 24)] // progressive exercises the Finish() reduced path
    public void SmoothContent_MatchesBoxDownsampleOfFullDecode(JpegScale scale, string sampling, bool progressive, double meanTol, int maxTol)
    {
        const int w = 96, h = 64;
        var jpeg = JpegDecoderOracleTests.EncodeJpeg(MakeSmoothRgb(w, h), w, h, 95, sampling, progressive);

        var full = JpegDecoder.Decode(jpeg);
        var reference = BoxDownsampleRgba(full.Pixels, w, h, (int)scale);
        var scaled = JpegDecoder.Decode(jpeg, scale);

        scaled.Pixels.Length.ShouldBe(reference.Length);

        var sum = 0.0;
        var max = 0;
        for (var i = 0; i < scaled.Pixels.Length; i++)
        {
            var diff = Math.Abs(scaled.Pixels[i] - reference[i]);
            sum += diff;
            max = Math.Max(max, diff);
        }

        var mean = sum / scaled.Pixels.Length;
        mean.ShouldBeLessThanOrEqualTo(meanTol, $"{scale} {sampling}: mean |diff| vs box reference");
        max.ShouldBeLessThanOrEqualTo(maxTol, $"{scale} {sampling}: max |diff| vs box reference");
    }

    [Theory]
    [InlineData(JpegScale.Half, 34, 23)]
    [InlineData(JpegScale.Quarter, 17, 12)]
    [InlineData(JpegScale.Eighth, 9, 6)]
    public void OddDimensions_FollowCeilContract(JpegScale scale, int expectedW, int expectedH)
    {
        const int w = 67, h = 45;
        var jpeg = JpegDecoderOracleTests.EncodeJpeg(MakeSmoothRgb(w, h), w, h, 90, "4:2:0");

        JpegDecoder.ReadInfo(jpeg).ScaledSize(scale).ShouldBe((expectedW, expectedH));

        var img = JpegDecoder.Decode(jpeg, scale);
        img.Width.ShouldBe(expectedW);
        img.Height.ShouldBe(expectedH);
        img.Pixels.Length.ShouldBe(expectedW * expectedH * 4);
    }

    [Fact]
    public void DecodeTo_PooledBuffer_MatchesDecode()
    {
        const int w = 67, h = 45;
        var jpeg = JpegDecoderOracleTests.EncodeJpeg(MakeSmoothRgb(w, h), w, h, 90, "4:2:0");

        var info = JpegDecoder.ReadInfo(jpeg);
        var (sw, sh) = info.ScaledSize(JpegScale.Quarter);
        var rented = ArrayPool<byte>.Shared.Rent(sw * sh * 4);
        try
        {
            var (dw, dh) = JpegDecoder.DecodeTo(jpeg, rented.AsSpan(0, sw * sh * 4), JpegScale.Quarter);
            (dw, dh).ShouldBe((sw, sh));

            var reference = JpegDecoder.Decode(jpeg, JpegScale.Quarter);
            rented.AsSpan(0, sw * sh * 4).SequenceEqual(reference.Pixels).ShouldBeTrue();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Fact]
    public void DecodeTo_TooSmallBuffer_Throws()
    {
        var jpeg = JpegDecoderOracleTests.EncodeJpeg(MakeSmoothRgb(16, 16), 16, 16, 90);
        var tiny = new byte[16 * 16 * 4 - 1];
        Should.Throw<ArgumentException>(() => JpegDecoder.DecodeTo(jpeg, tiny));
    }
}
