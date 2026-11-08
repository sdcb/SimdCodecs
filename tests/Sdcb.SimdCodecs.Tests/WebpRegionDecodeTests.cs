using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// WebP's native region and scale decode for lossy stills (<see cref="DecodeOptions.Region"/>,
/// <see cref="DecodeOptions.ScaleDenominator"/>): every output must be exactly what the generic path gives — the whole
/// decode (which the libwebp goldens pin), box-filtered, cropped, then converted to the requested format — for every
/// pixel format, region-only, scale-only and both, with and without alpha.
/// </summary>
public sealed class WebpRegionDecodeTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");

    public static TheoryData<string> LossyFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(FixtureDirectory, "*.webp").Order())
        {
            if (WebpDecoder.TryReadInfo(File.ReadAllBytes(path), out var info) && info.Format == WebpFormat.Lossy)
                data.Add(Path.GetFileName(path));
        }
        data.Add("magick:rgb");
        data.Add("magick:rgba");
        data.Add("magick:tall");
        return data;
    }

    private static byte[] Load(string name) => name switch
    {
        "magick:rgb" => MagickLossy(197, 133, alpha: false),
        "magick:rgba" => MagickLossy(171, 99, alpha: true),
        "magick:tall" => MagickLossy(37, 530, alpha: false),
        _ => File.ReadAllBytes(Path.Combine(FixtureDirectory, name)),
    };

    // Foreign bytes: a lossy WebP written by ImageMagick (libwebp's encoder inside) from a gradient with noise.
    private static byte[] MagickLossy(int width, int height, bool alpha)
    {
        var channels = alpha ? 4 : 3;
        var pixels = new byte[width * height * channels];
        var rng = new Random(width * 7919 + height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * channels;
                pixels[i] = (byte)(x * 255 / width ^ rng.Next(0, 24));
                pixels[i + 1] = (byte)(y * 255 / height + rng.Next(0, 32));
                pixels[i + 2] = (byte)((x + y) * 3 + rng.Next(0, 16));
                if (alpha)
                    pixels[i + 3] = (byte)(x < width / 3 ? 255 : (x * y) % 256);
            }
        }
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, alpha ? "RGBA" : "RGB"));
        image.Quality = 70;
        image.Settings.SetDefine(MagickFormat.WebP, "lossless", false);
        var webp = image.ToByteArray(MagickFormat.WebP);
        WebpDecoder.ReadInfo(webp).Format.ShouldBe(WebpFormat.Lossy);
        WebpDecoder.ReadInfo(webp).HasAlpha.ShouldBe(alpha);
        return webp;
    }

    // Regions of the w × h scaled image: whole, corners, single pixels, the last row and column, and rows around the
    // macroblock boundaries (16 source rows: 16 / d scaled rows), plus a few random ones.
    private static List<PixelRect> Regions(int w, int h, int d, Random rng)
    {
        var list = new List<PixelRect>
        {
            new(0, 0, w, h), new(0, 0, 1, 1), new(w - 1, h - 1, 1, 1), new(0, h - 1, w, 1), new(w - 1, 0, 1, h),
            new(w / 2, h / 2, w - w / 2, h - h / 2), new(0, 0, w, (h + 1) / 2),
        };
        var mb = Math.Max(1, 16 / d);
        for (var boundary = mb; boundary < h; boundary += mb * 3)
        {
            foreach (var y in new[] { boundary - 1, boundary, boundary + 1 })
            {
                if (y >= 0 && y < h)
                    list.Add(new PixelRect(0, y, w, Math.Min(h - y, 2)));
            }
        }
        for (var i = 0; i < 4; i++)
        {
            int x = rng.Next(0, w), y = rng.Next(0, h);
            list.Add(new PixelRect(x, y, rng.Next(1, w - x + 1), rng.Next(1, h - y + 1)));
        }
        return list;
    }

    private static IDecodedImage Generic(IDecodedImage whole, DecodeOptions options)
    {
        // The generic path, on a copy so that the whole decode can be reused.
        var copy = RasterImage.Crop(whole, new PixelRect(0, 0, whole.Width, whole.Height));
        return DecodeOptions.Apply(copy, options);
    }

    [Theory]
    [MemberData(nameof(LossyFixtures))]
    public void Native_region_and_scale_equal_the_generic_path(string fixture)
    {
        var data = Load(fixture);
        var ct = TestContext.Current.CancellationToken;
        WebpImageDecoder.TryDecode(data, out var whole, cancellationToken: ct).ShouldBeTrue();
        using (whole)
        {
            var rng = new Random(fixture.Length * 104729);
            foreach (var d in new[] { 1, 2, 4, 8, 16 })
            {
                var (w, h) = new DecodeOptions { ScaleDenominator = d }.GetScaledSize(whole.Width, whole.Height);
                var options = new List<DecodeOptions>();
                if (d > 1)
                    options.Add(new DecodeOptions { ScaleDenominator = d });
                options.AddRange(Regions(w, h, d, rng).Select(r => new DecodeOptions { ScaleDenominator = d, Region = r }));
                foreach (var option in options)
                {
                    using var expected = Generic(whole, option);
                    WebpImageDecoder.TryDecode(data, out var native, option, ct).ShouldBeTrue();
                    using (native)
                    {
                        (native.Width, native.Height, native.Channels).ShouldBe((expected.Width, expected.Height, expected.Channels));
                        if (!native.Pixels.SequenceEqual(expected.Pixels))
                            Assert.Fail($"{fixture} d{d} {option.Region}: TryDecode differs from the generic path.");
                    }
                }

                // TryDecodeInto, every pixel format, on a subset of the options (with a padded stride).
                foreach (var option in options.Where((_, i) => i % 3 == 0))
                {
                    using var expected = Generic(whole, option);
                    foreach (var format in PixelFormat.All)
                    {
                        var stride = expected.Width * format.BytesPerPixel + 3;
                        var want = new byte[stride * expected.Height];
                        var got = new byte[stride * expected.Height];
                        PixelConverter.TryConvert(expected, want, stride, format).ShouldBeTrue();
                        WebpImageDecoder.TryDecodeInto(data, got, stride, format, option, ct).ShouldBeTrue();
                        if (!got.AsSpan().SequenceEqual(want))
                            Assert.Fail($"{fixture} d{d} {option.Region} {format}: TryDecodeInto differs from the generic path.");
                    }
                }
            }
        }
    }

    /// <summary>Nearest-neighbour chroma (the internal no-fancy path) through the region sink equals its whole decode, box-filtered.</summary>
    [Theory]
    [InlineData("testsrc2-320x240-q75.webp")]
    [InlineData("odd-33x19-libvpx.webp")]
    [InlineData("lossy-alpha-q50.webp")]
    public void Nearest_upsampling_region_equals_the_whole_nearest_decode(string fixture)
    {
        var data = Load(fixture);
        var info = WebpDecoder.ReadInfo(data);
        var rgba = new byte[info.Width * info.Height * 4];
        WebpDecoder.DecodeToNoFancy(data, rgba);
        var channels = info.HasAlpha ? 4 : 3;
        using var whole = RasterImage.Create(info.Width, info.Height, channels, SampleFormat.UInt8, null);
        PixelConverter.Convert(rgba, 0, PixelFormat.Rgba32, whole.WritablePixels, 0,
            channels == 4 ? PixelFormat.Rgba32 : PixelFormat.Rgb24, info.Width, info.Height);
        var rng = new Random(17);
        foreach (var d in new[] { 1, 2, 4 })
        {
            var (w, h) = new DecodeOptions { ScaleDenominator = d }.GetScaledSize(info.Width, info.Height);
            foreach (var region in Regions(w, h, d, rng))
            {
                using var expected = Generic(whole, new DecodeOptions { ScaleDenominator = d, Region = region });
                var got = new byte[region.Width * region.Height * 4];
                DecodeRegion(data, channels, d, region, got, fancy: false);
                var want = new byte[got.Length];
                PixelConverter.TryConvert(expected, want, 0, PixelFormat.Rgba32).ShouldBeTrue();
                got.ShouldBe(want, $"d{d} {region}");
            }
        }
    }

    private static void DecodeRegion(byte[] data, int channels, int d, PixelRect region, byte[] rgba, bool fancy)
    {
        // The VP8 / ALPH payloads, found the way a reader of the container would (RFC 9649 chunk layout).
        var (vp8, alph) = Chunks(data);
        var info = WebpDecoder.ReadInfo(data);
        WebpDecoder.DecodeLossyRegion(vp8, alph, alph.Length > 0, info.Width, info.Height, channels, d, region, rgba,
            region.Width * 4, PixelFormat.Rgba32, fancy);
    }

    private static (byte[] Vp8, byte[] Alph) Chunks(byte[] data)
    {
        byte[] vp8 = [], alph = [];
        for (var at = 12; at + 8 <= data.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            var tag = System.Text.Encoding.ASCII.GetString(data, at, 4);
            if (tag == "VP8 ") vp8 = data.AsSpan(at + 8, size).ToArray();
            if (tag == "ALPH") alph = data.AsSpan(at + 8, size).ToArray();
            at += 8 + size + (size & 1);
        }
        return (vp8, alph);
    }

    // Records the furthest row a decode emitted.
    private struct CountingSink : IVp8RowSink
    {
        public int End;

        public void Emit(ref byte y, int yStride, int yRow0, ref byte u, ref byte v, int uvStride, int uvRow0, int first, int end) =>
            End = Math.Max(End, end);
    }

    [Fact]
    public void A_row_limit_stops_after_the_macroblock_row_that_reaches_it()
    {
        var (vp8, _) = Chunks(Load("testsrc2-320x240-q75.webp"));
        var ct = TestContext.Current.CancellationToken;
        // Output lags by 8 rows: macroblock row k emits up to 16k + 8 (and the last one to the bottom).
        foreach (var (limit, end) in new[] { (1, 8), (8, 8), (9, 24), (24, 24), (25, 40), (233, 240), (240, 240), (int.MaxValue, 240) })
        {
            var sink = new CountingSink();
            Vp8Decoder.DecodeFrame(vp8, 320, 240, WebpAblation.None, ref sink, ct, limit);
            sink.End.ShouldBe(end, $"limit {limit}");
        }
    }

    // Rewrites the VP8 chunk (and the RIFF size) of a simple lossy file, keeping everything else.
    private static byte[] WithVp8(byte[] data, Func<byte[], byte[]> change)
    {
        var (vp8, alph) = Chunks(data);
        alph.Length.ShouldBe(0);
        WebpDecoder.ReadInfo(data).Format.ShouldBe(WebpFormat.Lossy);
        var payload = change(vp8);
        var output = new List<byte>();
        output.AddRange("RIFF"u8.ToArray());
        output.AddRange(new byte[4]);
        output.AddRange("WEBPVP8 "u8.ToArray());
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)payload.Length);
        output.AddRange(size);
        output.AddRange(payload);
        if ((payload.Length & 1) != 0)
            output.Add(0);
        var file = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        return file;
    }

    [Fact]
    public void Damage_below_the_region_is_not_decoded()
    {
        var data = MagickLossy(64, 512, alpha: false);
        var ct = TestContext.Current.CancellationToken;
        WebpImageDecoder.TryDecode(data, out var whole, cancellationToken: ct).ShouldBeTrue();
        using (whole)
        {
            // The frame truncated to three quarters (the token partition loses its end): the bottom macroblock rows read zero bits, so the whole
            // decode changes there, but a region from the top half stops before them and is exactly the clean region.
            var truncated = WithVp8(data, static v => v.AsSpan(0, v.Length * 3 / 4).ToArray());
            var top = new DecodeOptions { Region = new PixelRect(0, 0, 64, 64) };
            WebpImageDecoder.TryDecode(truncated, out var damagedWhole, cancellationToken: ct).ShouldBeTrue();
            using (damagedWhole)
                damagedWhole.Pixels.SequenceEqual(whole.Pixels).ShouldBeFalse("the truncation must change the bottom");
            foreach (var option in new[] { top, new DecodeOptions { ScaleDenominator = 4, Region = new PixelRect(0, 0, 16, 16) } })
            {
                using var expected = Generic(whole, option);
                WebpImageDecoder.TryDecode(truncated, out var part, option, ct).ShouldBeTrue();
                using (part)
                    part.Pixels.ToArray().ShouldBe(expected.Pixels.ToArray());
            }

            // Damage above every row: a broken start code, a first partition longer than the frame.
            var badStartCode = WithVp8(data, static v => { var c = v.ToArray(); c[3] = 0; return c; });
            var badPartition = WithVp8(data, static v =>
            {
                var c = v.ToArray();
                var tag = (uint)(c[0] | c[1] << 8 | c[2] << 16) | (0x7ffffu << 5);
                (c[0], c[1], c[2]) = ((byte)tag, (byte)(tag >> 8), (byte)(tag >> 16));
                return c;
            });
            foreach (var bad in new[] { badStartCode, badPartition })
            {
                WebpImageDecoder.TryDecode(bad, out _, top, ct).ShouldBeFalse();
                WebpImageDecoder.TryDecodeInto(bad, new byte[64 * 64 * 4], 0, PixelFormat.Rgba32, top, ct).ShouldBeFalse();
                Should.Throw<InvalidDataException>(() => DecodeRegion(bad, 3, 1, top.Region!.Value, new byte[64 * 64 * 4], true));
            }
        }
    }

    [Fact]
    public void A_region_outside_the_image_and_a_short_destination_are_refused()
    {
        var data = Load("testsrc2-320x240-q75.webp");
        var ct = TestContext.Current.CancellationToken;
        Should.Throw<ArgumentException>(() =>
            WebpImageDecoder.TryDecode(data, out _, new DecodeOptions { ScaleDenominator = 2, Region = new PixelRect(150, 0, 20, 10) }, ct));
        WebpImageDecoder.TryDecodeInto(data, new byte[20 * 10 * 4 - 1], 0, PixelFormat.Rgba32,
            new DecodeOptions { Region = new PixelRect(0, 0, 20, 10) }, ct).ShouldBeFalse();
    }
}
