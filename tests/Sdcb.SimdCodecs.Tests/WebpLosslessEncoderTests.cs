using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The VP8L encoder against foreign decoders: every file it writes is read back by ImageMagick (libwebp
/// inside) and, when present, by libwebp's own <c>dwebp</c>, and must give the source pixels — all of them
/// with <see cref="WebpLosslessEncodeOptions.Exact"/>, otherwise every alpha value and every visible pixel.
/// The content is chosen to drive each encoder path: noise (literals, cache), runs and repeats (copies),
/// 2/4/16/256-colour images (every palette packing), smooth gradients (predictors, cross-colour) and
/// translucency.
/// </summary>
public sealed class WebpLosslessEncoderTests
{
    private static readonly int[] Widths = [1, 2, 3, 7, 16, 33, 67, 300];
    private static readonly int[] Efforts = [0, 1, 3, 6, 9];

    public static TheoryData<string> Contents() =>
        ["noise", "pattern", "pattern-alpha", "colors2", "colors4", "colors16", "colors256", "gradient", "gradient-alpha", "flat"];

    internal static byte[] Content(string kind, int width, int height, int seed)
    {
        var random = new Random(seed);
        var rgba = new byte[width * height * 4];
        switch (kind)
        {
            case "noise":
                random.NextBytes(rgba);
                for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
                return rgba;
            case "pattern":
                return QoiTests.Pattern(width, height, alpha: false, seed);
            case "pattern-alpha":
                return QoiTests.Pattern(width, height, alpha: true, seed);
            case "flat":
                for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 12; rgba[i + 1] = 200; rgba[i + 2] = 77; rgba[i + 3] = 255; }
                return rgba;
        }

        if (kind.StartsWith("colors", StringComparison.Ordinal))
        {
            int colors = int.Parse(kind["colors".Length..]);
            var palette = Enumerable.Range(0, colors).Select(_ => (uint)random.Next() | (uint)random.Next(2) << 31).ToArray();
            for (int i = 0; i < width * height; i++)
            {
                // Runs of one colour, so copies and the palette both get exercised.
                uint c = palette[(i / (1 + random.Next(4)) + random.Next(2)) % colors];
                rgba[4 * i] = (byte)c; rgba[4 * i + 1] = (byte)(c >> 8); rgba[4 * i + 2] = (byte)(c >> 16); rgba[4 * i + 3] = (byte)(c >> 24);
            }

            return rgba;
        }

        bool alpha = kind == "gradient-alpha";
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = 4 * (y * width + x);
                rgba[i] = (byte)(x * 3 + y + random.Next(3));
                rgba[i + 1] = (byte)(x + y * 2 + random.Next(2));
                rgba[i + 2] = (byte)(255 - x - y + random.Next(4));
                rgba[i + 3] = alpha ? (byte)Math.Clamp((x - width / 3) * 8, 0, 255) : (byte)255;
            }
        }

        return rgba;
    }

    private static byte[] MagickRgba(byte[] webp)
    {
        using var image = new MagickImage(webp, MagickFormat.WebP);
        image.Alpha(AlphaOption.Set);
        using var pixels = image.GetPixels();
        return [.. pixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8))];
    }

    /// <summary>Exact equality, or with <paramref name="exact"/> false: alpha everywhere, RGB where alpha &gt; 0.</summary>
    internal static void ShouldMatch(byte[] actual, byte[] expected, bool exact, string because)
    {
        actual.Length.ShouldBe(expected.Length, because);
        if (exact)
        {
            actual.ShouldBe(expected, because);
            return;
        }

        for (int i = 0; i < expected.Length; i += 4)
        {
            bool same = actual[i + 3] == expected[i + 3] && (expected[i + 3] == 0 ||
                (actual[i] == expected[i] && actual[i + 1] == expected[i + 1] && actual[i + 2] == expected[i + 2]));
            same.ShouldBeTrue($"{because}: pixel {i / 4} is {Convert.ToHexString(actual, i, 4)}, expected {Convert.ToHexString(expected, i, 4)}");
        }
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public void Every_effort_reads_back_in_imagemagick_as_the_source(string kind)
    {
        foreach (int width in Widths)
        {
            int height = width == 300 ? 41 : 5;
            byte[] source = Content(kind, width, height, width * 7 + kind.Length);
            foreach (int effort in Efforts)
            {
                foreach (bool exact in new[] { false, true })
                {
                    string because = $"{kind} {width}x{height} effort {effort} exact {exact}";
                    byte[] webp = WebpEncoder.EncodeLossless(source, width, height, PixelFormat.Rgba32,
                        new WebpLosslessEncodeOptions { Effort = effort, Exact = exact });
                    ShouldMatch(MagickRgba(webp), source, exact, because);

                    WebpInfo info = WebpDecoder.ReadInfo(webp);
                    info.Format.ShouldBe(WebpFormat.Lossless, because);
                    var ours = new byte[source.Length];
                    WebpDecoder.DecodeTo(webp, ours);
                    ShouldMatch(ours, source, exact, because);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public void Dwebp_decodes_the_output_to_the_source(string kind)
    {
        string dwebp = WebpOracle.RequireDwebp();
        string directory = Path.Combine(Path.GetTempPath(), "simdcodecs-webp-encode");
        Directory.CreateDirectory(directory);
        foreach (int effort in new[] { 0, 6, 9 })
        {
            byte[] source = Content(kind, 129, 67, effort + kind.Length);
            byte[] webp = WebpEncoder.EncodeLossless(source, 129, 67, PixelFormat.Rgba32,
                new WebpLosslessEncodeOptions { Effort = effort, Exact = true });
            string input = Path.Combine(directory, $"{Guid.NewGuid():N}.webp");
            File.WriteAllBytes(input, webp);
            try
            {
                WebpOracleTests.RunDwebp(dwebp, input, yuv: false, noFancy: false).ShouldBe(source, $"{kind} effort {effort}");
            }
            finally
            {
                File.Delete(input);
            }
        }
    }

    [Fact]
    public void A_larger_photo_like_image_round_trips_with_meta_prefix_codes()
    {
        // Big enough for several histogram tiles and predictor blocks, with regions of different texture.
        const int width = 523, height = 389;
        var random = new Random(5);
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = 4 * (y * width + x);
                bool noisy = (x / 64 + y / 64) % 3 == 0;
                int n = noisy ? random.Next(40) : random.Next(3);
                rgba[i] = (byte)(x / 2 + n);
                rgba[i + 1] = (byte)(y / 2 + n);
                rgba[i + 2] = (byte)((x + y) / 4 + n);
                rgba[i + 3] = y > height - 40 ? (byte)(x & 0xFF) : (byte)255;
            }
        }

        foreach (int effort in Efforts)
        {
            byte[] webp = WebpEncoder.EncodeLossless(rgba, width, height, PixelFormat.Rgba32,
                new WebpLosslessEncodeOptions { Effort = effort, Exact = true });
            MagickRgba(webp).ShouldBe(rgba, $"effort {effort}");
        }
    }

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Rgb24")]
    [InlineData("Bgr24")]
    [InlineData("Gray8")]
    public void Every_input_layout_is_read_with_its_own_channel_order(string formatName)
    {
        var format = PixelFormatByName(formatName);
        const int width = 37, height = 11, stride = 37 * 4 + 5;
        byte[] rgba = Content("pattern-alpha", width, height, 3);
        int bpp = format.BytesPerPixel;
        var input = new byte[stride * height];
        var expected = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int s = 4 * (y * width + x), d = y * stride + x * bpp;
                byte r = rgba[s], g = rgba[s + 1], b = rgba[s + 2], a = rgba[s + 3];
                switch (format.Layout)
                {
                    case ChannelLayout.Bgra: input[d] = b; input[d + 1] = g; input[d + 2] = r; input[d + 3] = a; break;
                    case ChannelLayout.Rgb: input[d] = r; input[d + 1] = g; input[d + 2] = b; a = 255; break;
                    case ChannelLayout.Bgr: input[d] = b; input[d + 1] = g; input[d + 2] = r; a = 255; break;
                    default: input[d] = g; r = b = g; a = 255; break;
                }

                expected[s] = r; expected[s + 1] = g; expected[s + 2] = b; expected[s + 3] = a;
            }
        }

        byte[] webp = WebpEncoder.EncodeLossless(input, width, height, format, new WebpLosslessEncodeOptions { Exact = true }, stride);
        MagickRgba(webp).ShouldBe(expected);

        using var stream = new MemoryStream();
        WebpEncoder.EncodeLossless(input, width, height, format, stream, new WebpLosslessEncodeOptions { Exact = true }, stride);
        stream.ToArray().ShouldBe(webp);
    }

    [Fact]
    public void Without_exact_only_the_colour_under_alpha_zero_changes()
    {
        byte[] source = Content("pattern-alpha", 64, 9, 11);
        for (int i = 0; i < 64; i++)
        {
            source[4 * i + 3] = 0; // a fully transparent first row with arbitrary colour
        }

        byte[] loose = WebpEncoder.EncodeLossless(source, 64, 9, PixelFormat.Rgba32);
        byte[] exact = WebpEncoder.EncodeLossless(source, 64, 9, PixelFormat.Rgba32, new WebpLosslessEncodeOptions { Exact = true });
        ShouldMatch(MagickRgba(loose), source, exact: false, "loose");
        MagickRgba(exact).ShouldBe(source);
        loose.Length.ShouldBeLessThanOrEqualTo(exact.Length);
    }

    [Fact]
    public void Bad_arguments_are_rejected()
    {
        var pixels = new byte[16 * 4];
        Should.Throw<ArgumentException>(() => WebpEncoder.EncodeLossless(pixels, 0, 4, PixelFormat.Rgba32));
        Should.Throw<ArgumentException>(() => WebpEncoder.EncodeLossless(pixels, 16385, 1, PixelFormat.Rgba32));
        Should.Throw<ArgumentException>(() => WebpEncoder.EncodeLossless(pixels, 5, 4, PixelFormat.Rgba32));
        Should.Throw<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossless(pixels, 4, 4, PixelFormat.Rgba32,
            new WebpLosslessEncodeOptions { Effort = 10 }));
    }

    private static PixelFormat PixelFormatByName(string name) =>
        (PixelFormat)typeof(PixelFormat).GetProperty(name)!.GetValue(null)!;
}
