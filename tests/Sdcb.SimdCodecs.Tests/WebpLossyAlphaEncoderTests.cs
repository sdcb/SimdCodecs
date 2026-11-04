using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Lossy WebP with transparency (VP8 + <c>ALPH</c>): the alpha plane must come back exactly at the
/// default alpha quality, from our decoder, libwebp's <c>dwebp</c> and ImageMagick alike, for every
/// filter and for the raw fallback; <c>webpinfo</c> must accept the container.
/// </summary>
public sealed class WebpLossyAlphaEncoderTests
{
    private static readonly (int W, int H)[] Sizes = [(1, 1), (2, 3), (15, 17), (33, 19), (67, 41), (131, 75)];

    public static TheoryData<string> Kinds() => ["radial", "binary", "pattern-alpha", "noise-alpha"];

    /// <summary>RGBA with transparency: <c>radial</c> a smooth alpha ramp over a pattern, <c>binary</c> 0 / 255
    /// blocks with noise in the invisible RGB, <c>pattern-alpha</c> and <c>noise-alpha</c> busier planes.</summary>
    internal static byte[] Content(string kind, int width, int height, int seed)
    {
        var random = new Random(seed);
        byte[] rgba = kind == "pattern-alpha"
            ? WebpLosslessEncoderTests.Content("pattern-alpha", width, height, seed)
            : WebpLossyEncoderTests.Content("pattern", width, height, seed);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = 4 * (y * width + x);
                switch (kind)
                {
                    case "radial":
                        double dx = (x - width / 2.0) / Math.Max(1, width / 2.0), dy = (y - height / 2.0) / Math.Max(1, height / 2.0);
                        rgba[i + 3] = (byte)Math.Round(255 * Math.Clamp(1.3 - Math.Sqrt(dx * dx + dy * dy), 0, 1));
                        break;
                    case "binary":
                        bool visible = ((x >> 3) + (y >> 2)) % 3 != 0;
                        rgba[i + 3] = visible ? (byte)255 : (byte)0;
                        if (!visible)
                        {
                            rgba[i] = (byte)random.Next(256);
                            rgba[i + 1] = (byte)random.Next(256);
                            rgba[i + 2] = (byte)random.Next(256);
                        }

                        break;
                    case "noise-alpha":
                        rgba[i + 3] = (byte)random.Next(256);
                        break;
                }
            }
        }

        rgba[3] = 0; // at least one transparent pixel, so even 1×1 has an alpha plane
        return rgba;
    }

    private static byte[] Alpha(byte[] rgba) => [.. rgba.Where((_, i) => (i & 3) == 3)];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Alpha_is_exact_and_every_decoder_agrees(string kind)
    {
        foreach ((int w, int h) in Sizes)
        {
            byte[] source = Content(kind, w, h, w * 13 + h);
            foreach (int effort in new[] { 0, 4, 6 })
            {
                string because = $"{kind} {w}x{h} effort {effort}";
                byte[] webp = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32, new WebpLossyEncodeOptions { Effort = effort, Quality = 60 });
                WebpInfo info = WebpDecoder.ReadInfo(webp);
                info.Format.ShouldBe(WebpFormat.Lossy, because);
                info.HasAlpha.ShouldBeTrue(because);
                var ours = new byte[w * h * 4];
                WebpDecoder.DecodeTo(webp, ours);
                Alpha(ours).ShouldBe(Alpha(source), because);
                WebpLossyEncoderTests.MagickRgba(webp).ShouldBe(ours, because);
            }
        }
    }

    [Fact]
    public void Dwebp_and_webpinfo_accept_every_filter_and_the_raw_plane()
    {
        string dwebp = WebpOracle.RequireDwebp();
        string webpinfo = WebpOracle.RequireWebpinfo();
        string directory = Path.Combine(Path.GetTempPath(), "simdcodecs-webp-lossy-alpha");
        Directory.CreateDirectory(directory);
        var seen = new HashSet<int>(); // ALPH header bytes written
        foreach (string kind in new[] { "radial", "noise-alpha" })
        {
            foreach ((int w, int h) in new[] { (1, 1), (67, 41) })
            {
                byte[] source = Content(kind, w, h, 5);
                for (int filter = 0; filter < 4; filter++)
                {
                    string because = $"{kind} {w}x{h} filter {filter}";
                    byte[] webp = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32,
                        Vp8EncoderConfig.For(75, 4) with { AlphaFilter = filter });
                    (string _, int offset, int _) = WebpEncoderContainerTests.Chunks(webp).Single(c => c.FourCc == "ALPH");
                    seen.Add(webp[offset]);
                    string file = Path.Combine(directory, $"{Guid.NewGuid():N}.webp");
                    File.WriteAllBytes(file, webp);
                    try
                    {
                        var ours = new byte[w * h * 4];
                        WebpDecoder.DecodeTo(webp, ours);
                        Alpha(ours).ShouldBe(Alpha(source), because);
                        WebpOracleTests.RunDwebp(dwebp, file, yuv: false, noFancy: false).ShouldBe(ours, because);
                        (int exit, string output) = WebpEncoderContainerTests.Run(webpinfo, "-diag", file);
                        exit.ShouldBe(0, $"{because}: {output}");
                        output.ShouldContain("No error detected", customMessage: $"{because}: {output}");
                    }
                    finally
                    {
                        File.Delete(file);
                    }
                }
            }
        }

        // Compressed with each of the four filters (C = 1, F = 0..3), and raw (C = 0) for the 1×1 plane.
        for (int filter = 0; filter < 4; filter++)
        {
            seen.ShouldContain(filter << 2 | 1);
        }

        seen.ShouldContain(0);
    }

    [Fact]
    public void Opaque_images_have_no_alpha_chunk_and_stay_in_the_simple_format()
    {
        byte[] source = WebpLossyEncoderTests.Content("pattern", 40, 30, 1);
        byte[] webp = WebpEncoder.EncodeLossy(source, 40, 30, PixelFormat.Rgba32);
        WebpEncoderContainerTests.Chunks(webp).Select(c => c.FourCc).ShouldBe(["VP8 "]);
        WebpDecoder.ReadInfo(webp).HasAlpha.ShouldBeFalse();
    }

    [Fact]
    public void Transparent_images_are_vp8x_with_the_alpha_flag_and_alph_before_vp8()
    {
        byte[] source = Content("radial", 40, 30, 1);
        byte[] webp = WebpEncoder.EncodeLossy(source, 40, 30, PixelFormat.Rgba32, new WebpLossyEncodeOptions { Xmp = "<x/>"u8.ToArray() });
        List<(string FourCc, int Offset, int Length)> chunks = WebpEncoderContainerTests.Chunks(webp);
        chunks.Select(c => c.FourCc).ShouldBe(["VP8X", "ALPH", "VP8 ", "XMP "]);
        (webp[chunks[0].Offset] & 0x10).ShouldBe(0x10, "VP8X alpha flag");
    }

    [Fact]
    public void Lower_alpha_quality_reduces_levels_and_size_or_keeps_the_exact_plane()
    {
        const int w = 160, h = 120;
        byte[] source = Content("radial", w, h, 2);
        byte[] exact = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32);
        foreach (int alphaQuality in new[] { 0, 50, 90 })
        {
            byte[] webp = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32, new WebpLossyEncodeOptions { AlphaQuality = alphaQuality });
            (string _, int offset, int _) = WebpEncoderContainerTests.Chunks(webp).Single(c => c.FourCc == "ALPH");
            webp.Length.ShouldBeLessThanOrEqualTo(exact.Length, $"alpha quality {alphaQuality}: never larger than exact");
            if (((webp[offset] >> 4) & 3) == 0)
            {
                // The exact plane was no larger, so it was kept.
                alphaQuality.ShouldBe(90, "only a near-lossless reduction may lose to the exact plane here");
                webp.ShouldBe(exact);
                continue;
            }

            webp.Length.ShouldBeLessThan(exact.Length, $"alpha quality {alphaQuality}");

            var decoded = new byte[w * h * 4];
            WebpDecoder.DecodeTo(webp, decoded);
            byte[] a = Alpha(decoded), s = Alpha(source);
            int levels = 4 + (alphaQuality * 252 + 50) / 100;
            int halfStep = (255 + levels - 2) / (levels - 1) / 2 + 1;
            a.Distinct().Count().ShouldBeLessThanOrEqualTo(levels);
            for (int i = 0; i < a.Length; i++)
            {
                Math.Abs(a[i] - s[i]).ShouldBeLessThanOrEqualTo(halfStep, $"alpha quality {alphaQuality} pixel {i}");
                if (s[i] is 0 or 255)
                {
                    a[i].ShouldBe(s[i], "0 and 255 are always levels");
                }
            }
        }
    }

    [Fact]
    public void Exact_keeps_the_invisible_colour_and_the_default_does_not_pay_for_it()
    {
        const int w = 128, h = 96;
        byte[] source = Content("binary", w, h, 3);
        byte[] lean = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32);
        byte[] exact = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32, new WebpLossyEncodeOptions { Exact = true });
        lean.Length.ShouldBeLessThan(exact.Length * 9 / 10, "noise under alpha 0 is not coded by default");

        var decoded = new byte[w * h * 4];
        WebpDecoder.DecodeTo(exact, decoded);
        Alpha(decoded).ShouldBe(Alpha(source));
        double invisibleError = 0, sourceSpread = 0;
        int count = 0;
        for (int i = 0; i < source.Length; i += 4)
        {
            if (source[i + 3] != 0)
            {
                continue;
            }

            for (int c = 0; c < 3; c++)
            {
                invisibleError += Math.Abs(decoded[i + c] - source[i + c]);
                sourceSpread += Math.Abs(source[i + c] - 128);
            }

            count += 3;
        }

        // Kept, though lossily: the error is well below that of a flat fill.
        (invisibleError / count).ShouldBeLessThan(0.75 * sourceSpread / count);
    }

    [Fact]
    public void Flattening_touches_only_fully_transparent_pixels()
    {
        foreach ((int w, int h) in Sizes)
        {
            byte[] source = Content("noise-alpha", w, h, 7);
            var argb = new uint[w * h];
            for (int i = 0; i < argb.Length; i++)
            {
                argb[i] = (uint)source[4 * i + 3] << 24 | (uint)source[4 * i] << 16 | (uint)source[4 * i + 1] << 8 | source[4 * i + 2];
            }

            uint[] before = (uint[])argb.Clone();
            WebpAlphaEncoder.FlattenInvisible(argb, w, h);
            for (int i = 0; i < argb.Length; i++)
            {
                if (before[i] >= 0x01000000u)
                {
                    argb[i].ShouldBe(before[i], $"{w}x{h} visible pixel {i}");
                }
                else
                {
                    (argb[i] >> 24).ShouldBe(0u, $"{w}x{h} pixel {i} stays transparent");
                }
            }
        }
    }

    /// <summary>
    /// The alpha plane's predictor search costs green alone: its green predictions must be the green byte of
    /// the full four-channel predictor for every mode, on planes where only green varies.
    /// </summary>
    [Fact]
    public void Green_only_predictions_are_the_full_predictors_green()
    {
        var random = new Random(9);
        Span<int> green = stackalloc int[14];
        for (int trial = 0; trial < 200_000; trial++)
        {
            int l = random.Next(256), t = random.Next(256), tl = random.Next(256), tr = random.Next(256);
            if (trial % 4 == 0)
            {
                (t, tl) = (l, l); // ties in Select
            }

            Vp8LEncoder.GreenPredictions(l, t, tl, tr, green);
            for (int m = 0; m < 14; m++)
            {
                uint full = Vp8LEncoder.Predict(m, Word(l), Word(t), Word(tl), Word(tr));
                ((int)(full >> 8) & 0xFF).ShouldBe(green[m], $"mode {m}: {l} {t} {tl} {tr}");
            }
        }

        static uint Word(int g) => 0xFF000000u | (uint)g << 8;
    }

    [Fact]
    public void Reduce_levels_keeps_the_ends_and_the_order()
    {
        foreach (int quality in new[] { 0, 1, 37, 99, 100 })
        {
            byte[] values = [.. Enumerable.Range(0, 256).Select(v => (byte)v)];
            WebpAlphaEncoder.ReduceLevels(values, quality);
            values[0].ShouldBe((byte)0);
            values[255].ShouldBe((byte)255);
            for (int v = 1; v < 256; v++)
            {
                values[v].ShouldBeGreaterThanOrEqualTo(values[v - 1], $"quality {quality} value {v}");
            }

            if (quality == 100)
            {
                values.ShouldBe([.. Enumerable.Range(0, 256).Select(v => (byte)v)]);
            }
        }
    }

    [Fact]
    public void Bad_alpha_quality_is_rejected()
    {
        var pixels = new byte[16 * 4];
        Should.Throw<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossy(pixels, 4, 4, PixelFormat.Rgba32,
            new WebpLossyEncodeOptions { AlphaQuality = 101 }));
        Should.Throw<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossy(pixels, 4, 4, PixelFormat.Rgba32,
            new WebpLossyEncodeOptions { AlphaQuality = -1 }));
    }
}
