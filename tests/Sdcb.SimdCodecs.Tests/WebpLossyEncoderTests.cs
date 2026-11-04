using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The VP8 encoder against foreign decoders and against itself: libwebp's <c>dwebp</c> and ImageMagick
/// must decode every file to exactly what our decoder (byte-exact with libwebp 1.6.0) decodes; the
/// encoder's own reconstruction must be the decoder's frame before the loop filter; and the forward
/// transforms must invert the decoder's inverse transforms.
/// </summary>
public sealed class WebpLossyEncoderTests
{
    private static readonly (int W, int H)[] Sizes = [(1, 1), (2, 3), (15, 17), (16, 16), (33, 19), (67, 41), (200, 129)];

    [Fact]
    public void Forward_dct_inverts_the_decoders_inverse_dct()
    {
        var random = new Random(1);
        Span<int> residual = stackalloc int[16], coefficients = stackalloc int[16];
        Span<short> back = stackalloc short[16];
        for (int trial = 0; trial < 2000; trial++)
        {
            int range = trial % 3 == 0 ? 255 : trial % 3 == 1 ? 32 : 4;
            for (int i = 0; i < 16; i++) residual[i] = random.Next(-range, range + 1);
            Vp8Fdct.Forward(residual, coefficients);
            var input = new short[16];
            for (int i = 0; i < 16; i++) input[i] = (short)coefficients[i];
            Vp8Decoder.InverseDctShort(ref input[0], back, hasAcCoefficients: true);
            for (int i = 0; i < 16; i++)
            {
                Math.Abs(back[i] - residual[i]).ShouldBeLessThanOrEqualTo(1, $"trial {trial} sample {i}");
            }
        }
    }

    [Fact]
    public void Forward_walsh_inverts_the_decoders_inverse_walsh()
    {
        var random = new Random(2);
        Span<int> dc = stackalloc int[16], coefficients = stackalloc int[16];
        for (int trial = 0; trial < 2000; trial++)
        {
            for (int i = 0; i < 16; i++) dc[i] = random.Next(-2040, 2041);
            Vp8Fdct.ForwardWalsh(dc, coefficients);
            var input = new short[16];
            for (int i = 0; i < 16; i++) input[i] = (short)coefficients[i];
            var blocks = new short[256];
            Vp8Decoder.InverseWalshToBlocks(ref input[0], ref blocks[0], hasAc: true);
            for (int i = 0; i < 16; i++)
            {
                Math.Abs(blocks[i * 16] - dc[i]).ShouldBeLessThanOrEqualTo(1, $"trial {trial} block {i}");
            }
        }
    }

    internal static byte[] Content(string kind, int width, int height, int seed)
    {
        byte[] rgba = WebpLosslessEncoderTests.Content(kind, width, height, seed);
        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255;
        }

        return rgba;
    }

    public static TheoryData<string> Contents() => ["noise", "pattern", "gradient", "flat", "colors16"];

    [Theory]
    [MemberData(nameof(Contents))]
    public void Every_size_and_effort_decodes_identically_in_imagemagick_and_our_decoder(string kind)
    {
        foreach ((int w, int h) in Sizes)
        {
            byte[] source = Content(kind, w, h, w * 31 + h);
            foreach (int effort in new[] { 0, 2, 4, 6 })
            {
                foreach (int quality in new[] { 5, 50, 90, 100 })
                {
                    string because = $"{kind} {w}x{h} effort {effort} q{quality}";
                    byte[] webp = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32,
                        new WebpLossyEncodeOptions { Effort = effort, Quality = quality });
                    WebpDecoder.ReadInfo(webp).Format.ShouldBe(WebpFormat.Lossy, because);
                    var ours = new byte[w * h * 4];
                    WebpDecoder.DecodeTo(webp, ours);
                    MagickRgba(webp).ShouldBe(ours, because);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public void Dwebp_decodes_the_output_to_what_our_decoder_gives(string kind)
    {
        string dwebp = WebpOracle.RequireDwebp();
        string directory = Path.Combine(Path.GetTempPath(), "simdcodecs-webp-lossy-encode");
        Directory.CreateDirectory(directory);
        foreach (int effort in new[] { 0, 4, 6 })
        {
            byte[] source = Content(kind, 131, 75, effort + kind.Length);
            byte[] webp = WebpEncoder.EncodeLossy(source, 131, 75, PixelFormat.Rgba32, new WebpLossyEncodeOptions { Effort = effort });
            string input = Path.Combine(directory, $"{Guid.NewGuid():N}.webp");
            File.WriteAllBytes(input, webp);
            try
            {
                var ours = new byte[source.Length];
                WebpDecoder.DecodeTo(webp, ours);
                WebpOracleTests.RunDwebp(dwebp, input, yuv: false, noFancy: false).ShouldBe(ours, $"{kind} effort {effort}");
            }
            finally
            {
                File.Delete(input);
            }
        }
    }

    /// <summary>
    /// 4×4 modes that read above-right pixels at the right edge of widths that are not a multiple of 16:
    /// libwebp takes them from the reconstructed padding (and repeats the last padded pixel past the
    /// last macroblock); our decoder once clamped them to the image's last column instead.
    /// </summary>
    [Fact]
    public void Right_edge_above_right_pixels_decode_like_dwebp()
    {
        string dwebp = WebpOracle.RequireDwebp();
        string directory = Path.Combine(Path.GetTempPath(), "simdcodecs-webp-lossy-edge");
        Directory.CreateDirectory(directory);
        foreach (int w in new[] { 17, 31, 47, 63, 67 })
        {
            foreach (int h in new[] { 23, 40 })
            {
                foreach (int quality in new[] { 60, 100 })
                {
                    byte[] source = Content("noise", w, h, w * 31 + h + quality);
                    byte[] webp = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32,
                        new WebpLossyEncodeOptions { Effort = 6, Quality = quality });
                    string input = Path.Combine(directory, $"{Guid.NewGuid():N}.webp");
                    File.WriteAllBytes(input, webp);
                    try
                    {
                        var ours = new byte[source.Length];
                        WebpDecoder.DecodeTo(webp, ours);
                        WebpOracleTests.RunDwebp(dwebp, input, yuv: false, noFancy: false).ShouldBe(ours, $"{w}x{h} q{quality}");
                    }
                    finally
                    {
                        File.Delete(input);
                    }
                }
            }
        }
    }

    [Fact]
    public void The_encoders_reconstruction_is_the_decoders_frame_before_the_loop_filter()
    {
        foreach ((int w, int h) in Sizes)
        {
            byte[] source = Content("pattern", w, h, 9);
            foreach (int effort in new[] { 0, 4, 6 })
            {
                var argb = new uint[w * h];
                for (int i = 0; i < argb.Length; i++)
                {
                    argb[i] = 0xFF000000u | (uint)source[4 * i] << 16 | (uint)source[4 * i + 1] << 8 | source[4 * i + 2];
                }

                using var encoder = new Vp8Encoder(w, h, Vp8EncoderConfig.For(60, effort));
                encoder.SetArgb(argb);
                byte[] frame = encoder.Encode();
                using Vp8Yuv decoded = Vp8Decoder.Decode(frame, w, h, applyLoopFilter: false);
                encoder.Reconstruction(out byte[] y, out byte[] u, out byte[] v);
                y.ShouldBe(decoded.Y, $"{w}x{h} effort {effort} luma");
                u.ShouldBe(decoded.U, $"{w}x{h} effort {effort} U");
                v.ShouldBe(decoded.V, $"{w}x{h} effort {effort} V");
            }
        }
    }

    [Fact]
    public void Quality_raises_fidelity_and_size()
    {
        // A smooth gradient: no colour edge that 4:2:0 chroma could not hold.
        const int w = 160, h = 120;
        var source = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = 4 * (y * w + x);
                (source[i], source[i + 1], source[i + 2], source[i + 3]) = ((byte)(40 + x), (byte)(60 + y), (byte)(200 - (x + y) / 2), 255);
            }
        }

        double previousPsnr = 0;
        int previousSize = 0;
        foreach (int quality in new[] { 10, 40, 70, 95 })
        {
            byte[] webp = WebpEncoder.EncodeLossy(source, w, h, PixelFormat.Rgba32, new WebpLossyEncodeOptions { Quality = quality });
            var decoded = new byte[source.Length];
            WebpDecoder.DecodeTo(webp, decoded);
            double se = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if ((i & 3) != 3) se += (source[i] - decoded[i]) * (double)(source[i] - decoded[i]);
            }

            double psnr = 10 * Math.Log10(255.0 * 255 / Math.Max(se / (w * h * 3), 1e-9));
            psnr.ShouldBeGreaterThan(previousPsnr, $"q{quality}");
            webp.Length.ShouldBeGreaterThan(previousSize, $"q{quality}");
            previousPsnr = psnr;
            previousSize = webp.Length;
        }

        previousPsnr.ShouldBeGreaterThan(38);
    }

    [Theory]
    [InlineData("Bgra32")]
    [InlineData("Rgb24")]
    [InlineData("Bgr24")]
    [InlineData("Gray8")]
    public void Every_input_layout_gives_the_file_of_the_same_rgba(string formatName)
    {
        var format = PixelFormatByName(formatName);
        const int width = 37, height = 11, stride = 37 * 4 + 5;
        byte[] rgba = Content(format == PixelFormat.Gray8 ? "flat" : "pattern", width, height, 3);
        if (format == PixelFormat.Gray8)
        {
            for (int i = 0; i < rgba.Length; i += 4) rgba[i] = rgba[i + 2] = rgba[i + 1] = (byte)(i * 7 / 4);
        }

        int bpp = format.BytesPerPixel;
        var input = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int s = 4 * (y * width + x), d = y * stride + x * bpp;
                byte r = rgba[s], g = rgba[s + 1], b = rgba[s + 2];
                switch (format.Layout)
                {
                    case ChannelLayout.Bgra: input[d] = b; input[d + 1] = g; input[d + 2] = r; input[d + 3] = 255; break;
                    case ChannelLayout.Rgb: input[d] = r; input[d + 1] = g; input[d + 2] = b; break;
                    case ChannelLayout.Bgr: input[d] = b; input[d + 1] = g; input[d + 2] = r; break;
                    default: input[d] = g; break;
                }
            }
        }

        byte[] expected = WebpEncoder.EncodeLossy(rgba, width, height, PixelFormat.Rgba32);
        WebpEncoder.EncodeLossy(input, width, height, format, null, stride).ShouldBe(expected);
        using var stream = new MemoryStream();
        WebpEncoder.EncodeLossy(input, width, height, format, stream, null, stride);
        stream.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void Bad_arguments_are_rejected()
    {
        var pixels = new byte[16 * 4];
        Should.Throw<ArgumentException>(() => WebpEncoder.EncodeLossy(pixels, 0, 4, PixelFormat.Rgba32));
        Should.Throw<ArgumentException>(() => WebpEncoder.EncodeLossy(pixels, 16384, 1, PixelFormat.Rgba32));
        Should.Throw<ArgumentException>(() => WebpEncoder.EncodeLossy(pixels, 5, 4, PixelFormat.Rgba32));
        Should.Throw<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossy(pixels, 4, 4, PixelFormat.Rgba32,
            new WebpLossyEncodeOptions { Quality = 101 }));
        Should.Throw<ArgumentOutOfRangeException>(() => WebpEncoder.EncodeLossy(pixels, 4, 4, PixelFormat.Rgba32,
            new WebpLossyEncodeOptions { Effort = 7 }));
    }

    internal static byte[] MagickRgba(byte[] webp)
    {
        using var image = new MagickImage(webp, MagickFormat.WebP);
        image.Alpha(AlphaOption.Set);
        using var pixels = image.GetPixels();
        return [.. pixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8))];
    }

    private static PixelFormat PixelFormatByName(string name) =>
        (PixelFormat)typeof(PixelFormat).GetProperty(name)!.GetValue(null)!;
}
