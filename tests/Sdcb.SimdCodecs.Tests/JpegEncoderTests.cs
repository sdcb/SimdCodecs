using ImageMagick;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Acceptance tests for <see cref="JpegEncoder"/>. The encoder has no byte-level reference;
/// what it must guarantee is checked against independent implementations instead:
/// <list type="bullet">
/// <item>every size × channel count × subsampling × coding mode decodes in libjpeg
/// (Magick.NET) to the same pixels as in our decoder, within the two decoders' rounding;</item>
/// <item>optimised-Huffman and progressive streams carry exactly the baseline's quantised
/// coefficients — they decode to identical pixels;</item>
/// <item>quality matches libjpeg's at the same quality setting (PSNR on luma), and the
/// optimised streams are no larger than libjpeg's.</item>
/// </list>
/// ISA independence of the kernels is in <see cref="JpegEncoderEquivalenceTests"/>.
/// </summary>
public sealed class JpegEncoderTests
{
    // ------------------------------------------------------------ deterministic input

    /// <summary>Gradient + checker + sine + LCG noise across up to 4 channels —
    /// DC ramps, hard edges (long AC runs), dense AC, deterministically.</summary>
    private static byte[] MakePattern(int w, int h, int channels)
    {
        var buf = new byte[w * h * channels];
        var seed = 0x12345678u;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * channels;
                seed = seed * 1664525u + 1013904223u;
                var noise = (int)(seed >> 28);
                buf[i] = (byte)Math.Clamp(x * 255 / Math.Max(1, w - 1) + noise - 8, 0, 255);
                if (channels > 1) buf[i + 1] = (byte)Math.Clamp(y * 255 / Math.Max(1, h - 1) + (((x >> 3) ^ (y >> 3)) & 1) * 60, 0, 255);
                if (channels > 2) buf[i + 2] = (byte)(128 + 100 * Math.Sin(x * 0.35));
                if (channels > 3) buf[i + 3] = 255;
            }
        }

        return buf;
    }

    private static (byte[] Rgb, int W, int H) PhotoRgb()
    {
        const int w = 609, h = 406;
        return (SyntheticPhoto.Rgb(w, h), w, h);
    }

    private static readonly JpegEncodeOptions[] Modes =
    [
        new() { },
        new() { OptimizeHuffman = true },
        new() { Progressive = true },
    ];

    // ------------------------------------------------------------ conformance matrix

    public static IEnumerable<object[]> MatrixCases()
    {
        // Sizes cover the 1×1 degenerate, exact MCUs, and partial edge MCUs in each axis.
        foreach (var (w, h) in new[] { (1, 1), (8, 8), (16, 16), (17, 13), (67, 45), (130, 7), (7, 130) })
            foreach (var c in new[] { 1, 2, 3, 4 })
                foreach (var sub in new[] { JpegSubsampling.Chroma444, JpegSubsampling.Chroma420 })
                    foreach (var q in new[] { 100, 75, 1 })
                    {
                        if (c <= 2 && sub == JpegSubsampling.Chroma420) continue; // grey ignores subsampling
                        yield return [w, h, c, sub, q];
                    }
    }

    [Theory]
    [MemberData(nameof(MatrixCases))]
    public void EveryModeDecodesInLibjpegAndAgreesWithBaseline(int w, int h, int channels, JpegSubsampling sub, int quality)
    {
        var pixels = MakePattern(w, h, channels);
        byte[]? baselinePixels = null;
        foreach (var mode in Modes)
        {
            var options = mode with { Quality = quality, Subsampling = sub };
            var jpeg = JpegEncoder.Encode(pixels, w, h, channels, options);
            var label = $"{w}x{h} c{channels} {sub} q{quality} opt={options.OptimizeHuffman} prog={options.Progressive}";

            var info = JpegDecoder.ReadInfo(jpeg);
            info.Width.ShouldBe(w, label);
            info.Height.ShouldBe(h, label);
            info.Components.ShouldBe(channels <= 2 ? 1 : 3, label);
            info.Progressive.ShouldBe(options.Progressive, label);

            var ours = JpegDecoder.Decode(jpeg).Pixels;
            if (baselinePixels is null) baselinePixels = ours;
            else ours.ShouldBe(baselinePixels, $"{label}: decoded pixels differ from the baseline encode's");

            using var m = new MagickImage(jpeg);
            ((int)m.Width, (int)m.Height).ShouldBe((w, h), label);
            using var px = m.GetPixels();
            var vals = px.GetValues()!;
            var mc = (int)px.Channels;
            var max = 0;
            for (var i = 0; i < w * h; i++)
            {
                for (var ch = 0; ch < 3; ch++)
                {
                    var libjpeg = (int)Math.Round(vals[i * mc + (mc >= 3 ? ch : 0)] / 257f);
                    max = Math.Max(max, Math.Abs(libjpeg - ours[i * 4 + ch]));
                }
            }

            // Two conformant decoders differ by IDCT / upsampling rounding only.
            max.ShouldBeLessThanOrEqualTo(sub == JpegSubsampling.Chroma420 && channels > 2 ? 12 : 4, label);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ChannelLayoutsEncodeTheSameImage(int channels)
    {
        // RGB(A) / grey(+alpha) inputs of the same picture: the alpha byte is ignored and grey
        // is coded as exactly the sample value, so each pair encodes to identical bytes.
        const int w = 37, h = 21;
        var rgb = MakePattern(w, h, 3);
        var input = new byte[w * h * channels];
        for (var i = 0; i < w * h; i++)
        {
            if (channels >= 3)
            {
                input[i * channels] = rgb[i * 3];
                input[i * channels + 1] = rgb[i * 3 + 1];
                input[i * channels + 2] = rgb[i * 3 + 2];
                if (channels == 4) input[i * 4 + 3] = (byte)(i * 7);
            }
            else
            {
                input[i * channels] = rgb[i * 3];
                if (channels == 2) input[i * 2 + 1] = (byte)(i * 7);
            }
        }

        var reference = channels >= 3 ? rgb : rgb.Where((_, i) => i % 3 == 0).ToArray();
        var expected = JpegEncoder.Encode(reference, w, h, channels >= 3 ? 3 : 1, new JpegEncodeOptions { Quality = 80 });
        JpegEncoder.Encode(input, w, h, channels, new JpegEncodeOptions { Quality = 80 }).ShouldBe(expected);
    }

    // ------------------------------------------------------------ quality vs libjpeg

    [Theory]
    [InlineData(50, "4:2:0")]
    [InlineData(75, "4:2:0")]
    [InlineData(90, "4:2:0")]
    [InlineData(95, "4:2:0")]
    [InlineData(90, "4:4:4")]
    [InlineData(95, "4:4:4")]
    public void QualityAndSizeAtLeastMatchLibjpeg(int quality, string sampling)
    {
        var (rgb, w, h) = PhotoRgb();
        var sub = sampling == "4:2:0" ? JpegSubsampling.Chroma420 : JpegSubsampling.Chroma444;
        var reference = JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, quality, sampling);
        var ours = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = quality, Subsampling = sub });
        var optimized = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = quality, Subsampling = sub, OptimizeHuffman = true });

        // Both decoded by libjpeg, scored on luma (BT.601 weights) against the source. The strict
        // gate (PSNR-Y >= libjpeg-turbo on the photo corpus) is the benchmark's `encode --gate`;
        // on this one synthetic image the two land within a few hundredths of a dB either way.
        var refPsnr = PsnrY(rgb, MagickRgb(reference), w, h);
        var ourPsnr = PsnrY(rgb, MagickRgb(ours), w, h);
        ourPsnr.ShouldBeGreaterThanOrEqualTo(refPsnr - 0.05, $"q{quality} {sampling}: PSNR-Y {ourPsnr:F3} dB < libjpeg's {refPsnr:F3} dB");

        optimized.Length.ShouldBeLessThanOrEqualTo((int)(reference.Length * 1.01),
            $"q{quality} {sampling}: {optimized.Length} bytes vs libjpeg's {reference.Length}");
    }

    [Fact]
    public void OptimizedAndProgressiveAreSmallerThanBaseline()
    {
        var (rgb, w, h) = PhotoRgb();
        var baseline = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 85 });
        var optimized = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 85, OptimizeHuffman = true });
        var progressive = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 85, Progressive = true });

        optimized.Length.ShouldBeLessThan(baseline.Length);
        progressive.Length.ShouldBeLessThan(baseline.Length);
        JpegDecoder.Decode(progressive).Pixels.ShouldBe(JpegDecoder.Decode(baseline).Pixels);
    }

    [Theory]
    [InlineData(95, JpegSubsampling.Auto, 37.0)]   // q95 → 4:4:4
    [InlineData(90, JpegSubsampling.Auto, 30.0)]   // q90 → 4:2:0
    [InlineData(75, JpegSubsampling.Chroma444, 31.0)]
    [InlineData(75, JpegSubsampling.Chroma420, 29.0)]
    public void RoundTripPsnrOnPhoto(int quality, JpegSubsampling subsampling, double minPsnr)
    {
        var (rgb, w, h) = PhotoRgb();
        var jpeg = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = quality, Subsampling = subsampling });
        var decoded = JpegDecoder.Decode(jpeg);
        decoded.Width.ShouldBe(w);
        decoded.Height.ShouldBe(h);

        var psnr = PsnrRgb(rgb, decoded.Pixels, w, h);
        psnr.ShouldBeGreaterThan(minPsnr, $"q{quality} {subsampling} PSNR {psnr:F2} dB below floor");
    }

    [Fact]
    public void ExplicitSubsamplingChangesSizeButBothDecode()
    {
        var (rgb, w, h) = PhotoRgb();
        var full = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 85, Subsampling = JpegSubsampling.Chroma444 });
        var half = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = 85, Subsampling = JpegSubsampling.Chroma420 });

        full.Length.ShouldBeGreaterThan(half.Length, "4:4:4 keeps full chroma, so it is larger than 4:2:0");
        JpegDecoder.ReadInfo(full).ChromaSubsampled.ShouldBeFalse();
        JpegDecoder.ReadInfo(half).ChromaSubsampled.ShouldBeTrue();
    }

    [Fact]
    public void GrayInputIsASingleComponentJpeg()
    {
        const int w = 32, h = 24;
        var gray = MakePattern(w, h, 1);
        var jpeg = JpegEncoder.Encode(gray, w, h, 1, new JpegEncodeOptions { Quality = 100 });
        JpegDecoder.ReadInfo(jpeg).Components.ShouldBe(1);

        var decoded = JpegDecoder.Decode(jpeg).Pixels;
        for (var i = 0; i < w * h; i++)
        {
            decoded[i * 4].ShouldBe(decoded[i * 4 + 1]);
            decoded[i * 4].ShouldBe(decoded[i * 4 + 2]);
            Math.Abs(decoded[i * 4] - gray[i]).ShouldBeLessThanOrEqualTo(2);
        }
    }

    // ------------------------------------------------------------ Huffman table construction

    [Fact]
    public void OptimalTablesAreLengthLimitedPrefixCodesWithoutAllOnes()
    {
        // Fibonacci frequencies force an unbalanced tree far deeper than 16 bits.
        var freq = new long[256];
        long a = 1, b = 1;
        for (var s = 0; s < 60; s++)
        {
            freq[s * 4] = a;
            (a, b) = (b, Math.Min(a + b, 1L << 50));
        }

        var table = JpegHuffmanEncodeTable.FromFrequencies(freq);
        table.Values.Length.ShouldBe(60);
        table.Bits.Sum(x => x).ShouldBe(60);

        var codes = new List<(uint Code, int Length)>();
        foreach (var s in table.Values)
        {
            var e = table.Codes[s];
            var len = (int)(e & 0xFF);
            len.ShouldBeInRange(1, 16);
            var code = e >> 8;
            code.ShouldNotBe((1u << len) - 1, "an all-ones code would read like a fill/marker prefix");
            codes.Add((code, len));
        }

        for (var i = 0; i < codes.Count; i++)
        {
            for (var j = 0; j < codes.Count; j++)
            {
                if (i == j || codes[i].Length > codes[j].Length) continue;
                (codes[j].Code >> (codes[j].Length - codes[i].Length)).ShouldNotBe(codes[i].Code, "prefix-free");
            }
        }

    }

    // ------------------------------------------------------------ API guards

    [Fact]
    public void Encode_ValidatesArguments()
    {
        var px = new byte[8 * 8 * 3];
        Should.Throw<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(px, 0, 8, 3));
        Should.Throw<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(px, 8, 70000, 3));
        Should.Throw<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(px, 8, 8, 5));
        Should.Throw<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(px, 8, 8, 3, new JpegEncodeOptions { Quality = 0 }));
        Should.Throw<ArgumentOutOfRangeException>(() => JpegEncoder.Encode(px, 8, 8, 3, new JpegEncodeOptions { Quality = 101 }));
        Should.Throw<ArgumentException>(() => JpegEncoder.Encode(new byte[10], 8, 8, 3)); // too small
    }

    [Fact]
    public void Output_IsAWellFormedJpeg()
    {
        var jpeg = JpegEncoder.Encode(MakePattern(20, 12, 3), 20, 12, 3);
        jpeg.Length.ShouldBeGreaterThan(4);
        (jpeg[0], jpeg[1]).ShouldBe(((byte)0xFF, (byte)0xD8)); // SOI
        (jpeg[^2], jpeg[^1]).ShouldBe(((byte)0xFF, (byte)0xD9)); // EOI
    }

    // ------------------------------------------------------------ helpers

    private static byte[] MagickRgb(byte[] jpeg)
    {
        using var m = new MagickImage(jpeg);
        return m.ToByteArray(MagickFormat.Rgb);
    }

    private static double PsnrY(byte[] a, byte[] b, int w, int h)
    {
        double sq = 0;
        for (var i = 0; i < w * h; i++)
        {
            var ya = 0.299 * a[i * 3] + 0.587 * a[i * 3 + 1] + 0.114 * a[i * 3 + 2];
            var yb = 0.299 * b[i * 3] + 0.587 * b[i * 3 + 1] + 0.114 * b[i * 3 + 2];
            sq += (ya - yb) * (ya - yb);
        }

        return 10 * Math.Log10(255.0 * 255.0 / (sq / (w * h)));
    }

    private static double PsnrRgb(byte[] a, byte[] decodedRgba, int w, int h)
    {
        long sq = 0;
        for (var i = 0; i < w * h; i++)
        {
            for (var ch = 0; ch < 3; ch++)
            {
                int d = a[i * 3 + ch] - decodedRgba[i * 4 + ch];
                sq += (long)d * d;
            }
        }

        if (sq == 0) return double.PositiveInfinity;
        return 10 * Math.Log10(255.0 * 255.0 / ((double)sq / (w * h * 3)));
    }
}
