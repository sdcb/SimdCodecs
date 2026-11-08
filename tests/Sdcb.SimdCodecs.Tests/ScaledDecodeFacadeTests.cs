using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Every codec accepts every power-of-two <see cref="DecodeOptions.ScaleDenominator"/>: the decode at the codec's largest
/// native scale n that does not exceed d (<see cref="SimdCodecs.TryGetClosestSize"/> reports the largest), box-filtered by
/// d / n (<see cref="BoxDownscaleTests.Reference"/>), then cropped to the region; and <c>TryDecodeInto</c> is that image
/// converted (<see cref="PixelConverterTests.ReferenceConvert"/>). One or more fixtures of every format.
/// </summary>
public sealed class ScaledDecodeFacadeTests
{
    private static readonly Lazy<Dictionary<string, byte[]>> CorpusCache = new(BuildCorpus);

    private static Dictionary<string, byte[]> BuildCorpus()
    {
        var all = DecodeIntoTests.Corpus();
        var corpus = new Dictionary<string, byte[]>();
        foreach (var name in new[]
        {
            "png ct0 d8 70x9", "png ct2 d16 70x9 adam7", "png ct4 d16 37x5", "png ct6 d8 70x9", "png ct6 d16 70x9", "png ct3 d8 trns 37x5",
            "jpeg grey 70x33", "jpeg 4:2:0 70x33", "jpeg 4:4:4 progressive 37x19", "jpeg gain map",
            "tiff grey16 23x7", "tiff rgb16 23x7", "tiff rgba8 31x6", "jbig2",
            "gif still transparent 23x11", "gif anim mixed", "bmp rgb24 37x19", "bmp argb 31x6", "ico 3 images", "cur 1 image",
            "qoi rgba 31x9", "qoi rgb 19x7", "pnm ppm 37x19", "pnm pam rgba 31x6", "pnm pgm16 19x7", "pnm pbm plain 19x7",
            "webp testsrc2-320x240-q75.webp", "webp odd-33x19-libvpx.webp", "webp lossy-alpha-q50.webp", "webp lossless-exact-alpha.webp",
            "webp lossless-3x7.webp", "webp anim-img2webp-mixed.webp",
        })
        {
            corpus[name] = all[name];
        }
        foreach (var name in all.Keys.Where(k => k.StartsWith("j2k ", StringComparison.Ordinal)))
            corpus[name] = all[name];

        var random = new Random(11);
        corpus["tiff float32 grey 23x7"] = HdrTiff.Float32Gray(23, 7, [.. Enumerable.Range(0, 23 * 7).Select(_ => (float)(random.NextDouble() * 4 - 1))]);
        corpus["tiff half rgb 13x9"] = HdrTiff.HalfRgb(13, 9, [.. Enumerable.Range(0, 13 * 9 * 3).Select(_ => (Half)random.NextDouble())]);
        float[] pfm = [.. Enumerable.Range(0, 19 * 7 * 3).Select(_ => (float)(random.NextDouble() * 2))];
        corpus["pfm rgb 19x7"] = Sdcb.SimdCodecs.Pnm.PnmEncoder.EncodePfm(pfm, 19, 7, 3);
        return corpus;
    }

    public static TheoryData<string> Inputs() => [.. CorpusCache.Value.Keys];

    private static readonly int[] Scales = [2, 4, 16, 64];

    private static IEnumerable<PixelRect?> Regions(int w, int h) =>
    [
        null, new PixelRect(0, 0, 1, 1), new PixelRect(w - 1, h - 1, 1, 1), new PixelRect(w / 3, h / 4, Math.Max(1, w / 2), Math.Max(1, h / 2)),
    ];

    private static byte[] Crop(byte[] pixels, int width, int bpp, PixelRect r)
    {
        var result = new byte[r.Width * r.Height * bpp];
        for (var y = 0; y < r.Height; y++)
            pixels.AsSpan(((r.Y + y) * width + r.X) * bpp, r.Width * bpp).CopyTo(result.AsSpan(y * r.Width * bpp));
        return result;
    }

    // The decode at native scale n, box-filtered by d / n: the whole scaled image.
    private static (byte[] Pixels, int Width, int Height, IDecodedImage Native) Expected(ReadOnlySpan<byte> data, int d,
        Func<DecodeOptions, IDecodedImage> decode, int maxNative = 0)
    {
        if (maxNative == 0)
            SimdCodecs.TryGetClosestSize(data, 1, 1, out _, out _, out maxNative).ShouldBeTrue();
        var n = Math.Min(d, maxNative);
        var native = decode(new DecodeOptions { ScaleDenominator = n });
        var factor = d / n;
        var pixels = factor == 1
            ? native.Pixels.ToArray()
            : BoxDownscaleTests.Reference(native.Pixels, native.Width, native.Height, native.Channels, native.SampleFormat, factor);
        return (pixels, (native.Width + factor - 1) / factor, (native.Height + factor - 1) / factor, native);
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void AScaledDecodeIsTheBoxFilterOfTheNativeDecode(string name)
    {
        var data = CorpusCache.Value[name];
        var ct = TestContext.Current.CancellationToken;
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue(name);
        foreach (var d in Scales)
        {
            var (expected, w, h, native) = Expected(data, d, options =>
            {
                SimdCodecs.TryDecode(data, out var image, options, ct).ShouldBeTrue($"{name} native");
                return image;
            });
            using (native)
            {
                new DecodeOptions { ScaleDenominator = d }.GetScaledSize(info.Width, info.Height).ShouldBe((w, h), $"{name} d{d}");
                var bpp = native.Channels * RasterImage.BytesPerSample(native.SampleFormat);
                foreach (var region in Regions(w, h))
                {
                    var label = $"{name} d{d} {region}";
                    var options = new DecodeOptions { ScaleDenominator = d, Region = region };
                    var r = region ?? new PixelRect(0, 0, w, h);
                    var wanted = region is null ? expected : Crop(expected, w, bpp, r);
                    SimdCodecs.TryDecode(data, out var image, options, ct).ShouldBeTrue(label);
                    using (image)
                    {
                        (image.Width, image.Height, image.Channels, image.SampleFormat, image.BitDepth)
                            .ShouldBe((r.Width, r.Height, native.Channels, native.SampleFormat, native.BitDepth), label);
                        image.ColorEncoding.ShouldBe(native.ColorEncoding, label);
                        image.Pixels.ToArray().ShouldBe(wanted, label);
                    }

                    // TryDecodeInto is that image converted. A float image has no integer projection, except a gain-map
                    // JPEG's, whose display tier is its SDR base: that base's scaled decode.
                    var (display, displayFormat) = (wanted, PixelFormat.ForChannels(native.Channels, native.SampleFormat == SampleFormat.Float32 ? SampleFormat.UInt8 : native.SampleFormat));
                    if (native.SampleFormat == SampleFormat.Float32 && name == "jpeg gain map")
                    {
                        var (sdr, _, _, sdrNative) = Expected(data, d, o =>
                        {
                            Jpeg.JpegImageDecoder.TryDecode(data, out var image, o, ct).ShouldBeTrue();
                            return image;
                        }, maxNative: 8);
                        using (sdrNative)
                            (display, displayFormat) = (region is null ? sdr : Crop(sdr, w, sdrNative.Channels, r), PixelFormat.ForChannels(sdrNative.Channels, SampleFormat.UInt8));
                    }
                    foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgra64Premultiplied })
                    {
                        var into = new byte[r.Width * r.Height * format.BytesPerPixel];
                        var decoded = SimdCodecs.TryDecodeInto(data, into, 0, format, options, ct);
                        if (native.SampleFormat == SampleFormat.Float32 && name != "jpeg gain map")
                        {
                            decoded.ShouldBeFalse(label);
                            continue;
                        }
                        decoded.ShouldBeTrue($"{label} into {format}");
                        into.ShouldBe(PixelConverterTests.ReferenceConvert(display, displayFormat, format, r.Width * r.Height), $"{label} into {format}");
                    }
                }
            }
        }
    }

    public static TheoryData<string> Animations() =>
        [.. CorpusCache.Value.Keys.Where(k => k.Contains("anim", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Animations))]
    public void AScaledFrameIsTheBoxFilterOfTheFrame(string name)
    {
        var data = CorpusCache.Value[name];
        var ct = TestContext.Current.CancellationToken;
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue(name);
        using (reader)
        {
            reader.FrameCount.ShouldBeGreaterThan(1);
            for (var index = reader.FrameCount - 1; index >= 0; index--)
            {
                reader.TryDecodeFrame(index, out var full, cancellationToken: ct).ShouldBeTrue();
                using (full)
                {
                    foreach (var d in new[] { 2, 8 })
                    {
                        var expected = BoxDownscaleTests.Reference(full.Pixels, full.Width, full.Height, full.Channels, full.SampleFormat, d);
                        var (w, h) = new DecodeOptions { ScaleDenominator = d }.GetScaledSize(full.Width, full.Height);
                        reader.TryDecodeFrame(index, out var scaled, new DecodeOptions { ScaleDenominator = d }, ct).ShouldBeTrue();
                        using (scaled)
                            scaled.Pixels.ToArray().ShouldBe(expected, $"{name} frame {index} d{d}");
                        var into = new byte[w * h * 4];
                        reader.TryDecodeFrameInto(index, into, 0, PixelFormat.Rgba32, new DecodeOptions { ScaleDenominator = d }, ct).ShouldBeTrue();
                        into.ShouldBe(PixelConverterTests.ReferenceConvert(expected, PixelFormat.ForChannels(full.Channels, full.SampleFormat), PixelFormat.Rgba32, w * h));
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("synth-screen-testsrc2-1280x720-main8.heic")]
    [InlineData("synth-photo-mangrove-1599x1199-main8.heic")]
    public void AScaledHeifIsTheBoxFilterOfTheFullDecode(string fixture)
    {
        var data = HeifTests.ReadFixture(fixture);
        var ct = TestContext.Current.CancellationToken;
        // Through the codec itself: the facade does not register HEIF unless asked to (see HeifFacadeTests).
        Heif.HeifImageDecoder.TryDecode(data, out var full, cancellationToken: ct).ShouldBeTrue();
        using (full)
        {
            var bpp = full.Channels * RasterImage.BytesPerSample(full.SampleFormat);
            foreach (var d in new[] { 4, 64 })
            {
                var expected = BoxDownscaleTests.Reference(full.Pixels, full.Width, full.Height, full.Channels, full.SampleFormat, d);
                var (w, h) = new DecodeOptions { ScaleDenominator = d }.GetScaledSize(full.Width, full.Height);
                foreach (var region in Regions(w, h))
                {
                    var r = region ?? new PixelRect(0, 0, w, h);
                    var options = new DecodeOptions { ScaleDenominator = d, Region = region };
                    var wanted = region is null ? expected : Crop(expected, w, bpp, r);
                    Heif.HeifImageDecoder.TryDecode(data, out var image, options, ct).ShouldBeTrue();
                    using (image)
                    {
                        (image.Width, image.Height, image.BitDepth).ShouldBe((r.Width, r.Height, full.BitDepth));
                        image.Pixels.ToArray().ShouldBe(wanted, $"{fixture} d{d} {region}");
                    }
                    var into = new byte[r.Width * r.Height * 4];
                    Heif.HeifImageDecoder.TryDecodeInto(data, into, 0, PixelFormat.Bgra32, options, ct).ShouldBeTrue();
                    into.ShouldBe(PixelConverterTests.ReferenceConvert(wanted, PixelFormat.ForChannels(full.Channels, full.SampleFormat), PixelFormat.Bgra32, r.Width * r.Height));
                }
            }
        }
    }
}
