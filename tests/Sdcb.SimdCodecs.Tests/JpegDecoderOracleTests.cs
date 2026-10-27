using System.Security.Cryptography;
using ImageMagick;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Byte-exact regression tests for <see cref="JpegDecoder"/>: full-scale output
/// must match a committed golden baseline on a matrix of inputs (subsampling ×
/// quality × progressive × colour model, edge dimensions, grayscale, Adobe CMYK,
/// restart intervals, every scaled-decode denominator, and damaged streams).
/// Inputs are encoded in-test with Magick.NET (libjpeg), a hand-built restart
/// bitstream, and <see cref="TestJpegWriter"/> (arbitrary sampling factors,
/// non-interleaved baseline scans, colour restart intervals, YCCK / RGB / four-
/// component frames, streams cut short at a restart marker), so the matrix
/// costs no committed image fixtures; only a tiny <c>jpeg-oracle-golden.tsv</c>
/// digest manifest is stored.
///
/// <para><b>Provenance of the golden.</b> The baseline pins the decoder's own
/// defined arithmetic — the single-precision T.81 IDCT (<c>JpegIdct</c>), 16×-scale
/// linear chroma interpolation (<c>JpegResample</c>) and T.871 colour conversion
/// (<c>JpegColor</c>) — frozen once those were shown to be at least as accurate as
/// libjpeg-turbo against a double-precision reference (see
/// <c>benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/</c>). It replaced an earlier baseline that pinned
/// stb_image's integer pipeline byte for byte. The entropy decoder is held to a
/// stricter, independent standard elsewhere: identical quantized coefficients to
/// libjpeg-turbo. Performance work must leave this file untouched; to refresh it
/// after an intentional, reviewed arithmetic change, run this project with
/// <c>REGEN_JPEG_ORACLE=1</c> (see <see cref="RegenerateGolden"/>) and review the
/// resulting diff.</para>
///
/// <para>A separate tolerance check against Magick's own (libjpeg) decode
/// (<see cref="FullScale_WithinToleranceOfLibjpeg"/>) guards against pinning a
/// systematically wrong decode: two accurate decoders of the same stream agree
/// within a couple of code values.</para>
///
/// <para>The inputs are reproducible only against the pinned Magick.NET version
/// (see <c>Directory.Packages.props</c>); a Magick bump can change the encoded
/// bytes and require a golden regen.</para>
/// </summary>
public sealed class JpegDecoderOracleTests
{
    // ------------------------------------------------------------ helpers

    private static byte[] MakeRgbPattern(int w, int h)
    {
        // Gradients + checker + sine + LCG noise: exercises DC ramps, sharp
        // edges (long AC runs), and dense AC coefficients deterministically.
        var rgb = new byte[w * h * 3];
        var seed = 0x12345678u;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                seed = seed * 1664525u + 1013904223u;
                var noise = (int)(seed >> 28);
                rgb[i + 0] = (byte)Math.Clamp(x * 255 / Math.Max(1, w - 1) + noise - 8, 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(y * 255 / Math.Max(1, h - 1) + (((x >> 3) ^ (y >> 3)) & 1) * 60, 0, 255);
                rgb[i + 2] = (byte)(128 + 100 * Math.Sin(x * 0.35));
            }
        }

        return rgb;
    }

    internal static byte[] EncodeJpeg(
        byte[] rgb, int w, int h, int quality = 90, string? sampling = null,
        bool progressive = false, bool gray = false, bool cmyk = false)
    {
        var settings = new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "RGB");
        using var img = new MagickImage();
        img.ReadPixels(rgb, settings);
        img.Quality = (uint)quality;
        if (sampling != null)
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", sampling);
        if (progressive)
            img.Settings.Interlace = Interlace.Jpeg;
        if (gray)
            img.ColorSpace = ColorSpace.Gray;
        if (cmyk)
            img.ColorSpace = ColorSpace.CMYK;
        return img.ToByteArray(MagickFormat.Jpeg);
    }

    // ------------------------------------------------------------ the case matrix
    //
    // One source of truth for every byte-exact case: (label, lazy builder). The
    // [Theory] below and the golden regenerator both iterate it, so the input
    // construction never drifts between "what we assert" and "what we froze".

    private static readonly (string Label, Func<byte[]> Build, JpegScale Scale)[] OracleCases = BuildOracleCases();

    private static (string, Func<byte[]>, JpegScale)[] BuildOracleCases()
    {
        var full = new List<(string, Func<byte[]>)>();
        var cases = full;

        // Sampling × quality × progressive, at odd dimensions (partial edge MCUs
        // in both axes).
        foreach (var sampling in new[] { "4:4:4", "4:2:2", "4:2:0" })
            foreach (var quality in new[] { 95, 75, 25 })
                foreach (var progressive in new[] { false, true })
                {
                    var s = sampling;
                    var q = quality;
                    var p = progressive;
                    const int w = 67, h = 45;
                    cases.Add((
                        $"{s} q{q}{(p ? " progressive" : "")}",
                        () => EncodeJpeg(MakeRgbPattern(w, h), w, h, q, s, p)));
                }

        // Edge dimensions.
        foreach (var (w, h) in new[] { (1, 1), (8, 8), (16, 16), (17, 13), (130, 7), (7, 130) })
        {
            var ww = w;
            var hh = h;
            cases.Add((
                $"{ww}x{hh} 4:2:0",
                () => EncodeJpeg(MakeRgbPattern(ww, hh), ww, hh, 85, "4:2:0")));
        }

        // Grayscale, baseline + progressive.
        foreach (var progressive in new[] { false, true })
        {
            var p = progressive;
            const int w = 50, h = 38;
            cases.Add((
                $"grayscale{(p ? " progressive" : "")}",
                () => EncodeJpeg(MakeRgbPattern(w, h), w, h, 80, gray: true, progressive: p)));
        }

        // Adobe CMYK.
        cases.Add(("Adobe CMYK", () => EncodeJpeg(MakeRgbPattern(40, 30), 40, 30, 90, cmyk: true)));

        // Restart intervals (hand-built, deterministic, no encoder).
        cases.Add(("restart intervals", () => BuildRestartMarkerJpeg(blocksW: 10, blocksH: 7, restartInterval: 7)));

        // Streams no stock encoder emits, from the independent TestJpegWriter.
        foreach (var (label, build) in WriterCases())
            cases.Add(($"writer {label}", build));

        // Damaged streams: pin what the decoder salvages.
        cases.Add(("4:2:0 q75 truncated", () => Truncate(EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 75, "4:2:0"), 0.5)));
        cases.Add(("4:2:0 q75 progressive truncated", () => Truncate(EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 75, "4:2:0", progressive: true), 0.5)));

        var result = full.Select(c => (c.Item1, c.Item2, JpegScale.Full)).ToList();

        // Scaled decode (reduced IDCT) at every denominator.
        var scaledSources = new (string, Func<byte[]>)[]
        {
            ("4:4:4 q75", () => EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 75, "4:4:4")),
            ("4:2:2 q75", () => EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 75, "4:2:2")),
            ("4:2:0 q75", () => EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 75, "4:2:0")),
            ("4:2:0 q75 progressive", () => EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 75, "4:2:0", progressive: true)),
            ("17x13 4:2:0", () => EncodeJpeg(MakeRgbPattern(17, 13), 17, 13, 85, "4:2:0")),
            ("grayscale", () => EncodeJpeg(MakeRgbPattern(50, 38), 50, 38, 80, gray: true)),
            ("Adobe CMYK", () => EncodeJpeg(MakeRgbPattern(40, 30), 40, 30, 90, cmyk: true)),
            ("writer 4:1:1", WriterCases().First(c => c.Label == "4:1:1").Build),
        };
        foreach (var scale in new[] { JpegScale.Half, JpegScale.Quarter, JpegScale.Eighth })
            foreach (var (label, build) in scaledSources)
                result.Add(($"{label} @1/{(int)scale}", build, scale));

        return result.ToArray();
    }

    private static byte[] Truncate(byte[] jpeg, double fraction) => jpeg[..(int)(jpeg.Length * fraction)];

    private static (string Label, Func<byte[]> Build)[] WriterCases()
    {
        const int w = 67, h = 45;
        var rgb = MakeRgbPattern(w, h);

        byte Ycc(int k, int x, int y)
        {
            var i = (y * w + x) * 3;
            double r = rgb[i], g = rgb[i + 1], b = rgb[i + 2];
            var v = k switch
            {
                0 => 0.299 * r + 0.587 * g + 0.114 * b,
                1 => 128 - 0.168736 * r - 0.331264 * g + 0.5 * b,
                2 => 128 + 0.5 * r - 0.418688 * g - 0.081312 * b,
                _ => (x * 7 + y * 3) % 256,
            };
            return (byte)Math.Clamp(Math.Round(v), 0, 255);
        }

        byte Rgb(int k, int x, int y) => k < 3 ? rgb[(y * w + x) * 3 + k] : (byte)((x * 7 + y * 3) % 256);

        TestJpegWriter.Component[] Ycc3(int yh, int yv, int cbh, int cbv, int crh, int crv) =>
            [new(1, yh, yv), new(2, cbh, cbv), new(3, crh, crv)];

        byte[] Enc(TestJpegWriter.Component[] comps, Func<int, int, int, byte> sample, TestJpegWriter.Options? o = null) =>
            TestJpegWriter.Encode(w, h, comps, sample, o);

        return
        [
            ("4:1:1", () => Enc(Ycc3(4, 1, 1, 1, 1, 1), Ycc)),
            ("4:4:0", () => Enc(Ycc3(1, 2, 1, 1, 1, 1), Ycc)),
            ("Y4x1 Cb2x1 Cr1x1", () => Enc(Ycc3(4, 1, 2, 1, 1, 1), Ycc)),
            ("Y1x4 Cb1x2 Cr1x1", () => Enc(Ycc3(1, 4, 1, 2, 1, 1), Ycc)),
            ("4:2:0 restart 5", () => Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { RestartInterval = 5 })),
            ("4:2:0 restart 1", () => Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { RestartInterval = 1 })),
            ("4:2:0 non-interleaved", () => Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { Interleaved = false })),
            ("4:2:0 non-interleaved restart 4", () => Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { Interleaved = false, RestartInterval = 4 })),
            ("YCCK 4:2:0", () => Enc([new(1, 2, 2), new(2, 1, 1), new(3, 1, 1), new(4, 2, 2)], Ycc, new() { AdobeTransform = 2, Jfif = false })),
            ("4-component no Adobe", () => Enc([new(1, 1, 1), new(2, 1, 1), new(3, 1, 1), new(4, 1, 1)], Ycc)),
            ("Adobe CMYK subsampled", () => Enc([new(1, 2, 2), new(2, 1, 1), new(3, 1, 1), new(4, 2, 1)], Rgb, new() { AdobeTransform = 0, Jfif = false })),
            ("RGB ids", () => Enc([new('R', 1, 1), new('G', 1, 1), new('B', 1, 1)], Rgb, new() { Jfif = false })),
            ("RGB Adobe subsampled", () => Enc(Ycc3(2, 2, 1, 2, 2, 1), Rgb, new() { AdobeTransform = 0, Jfif = false })),
            ("grayscale restart 3", () => Enc([new(1, 1, 1)], Ycc, new() { RestartInterval = 3 })),
            ("4:2:0 restart 5 cut at RST2", () => TestJpegWriter.CutAtRestart(Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { RestartInterval = 5 }), 2)),
            ("4:2:0 restart 2 cut at RST3", () => TestJpegWriter.CutAtRestart(Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { RestartInterval = 2 }), 3)),
            ("4:2:0 restart 3 cut at RST4", () => TestJpegWriter.CutAtRestart(Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { RestartInterval = 3 }), 4)),
            ("grayscale restart 3 cut at RST7", () => TestJpegWriter.CutAtRestart(Enc([new(1, 1, 1)], Ycc, new() { RestartInterval = 3 }), 7)),
            ("4:2:0 non-interleaved cut in Cb", () => TestJpegWriter.CutAtRestart(Enc(Ycc3(2, 2, 1, 1, 1, 1), Ycc, new() { Interleaved = false, RestartInterval = 4 }), 15)),
        ];
    }

    public static IEnumerable<object[]> CaseLabels() => OracleCases.Select(c => new object[] { c.Label });

    private static (byte[] Jpeg, JpegScale Scale) BuildCase(string label)
    {
        var c = OracleCases.First(c => c.Label == label);
        return (c.Build(), c.Scale);
    }

    // ------------------------------------------------------------ the byte-exact assertion

    [Theory]
    [MemberData(nameof(CaseLabels))]
    public void ByteExact(string label)
    {
        var (jpeg, scale) = BuildCase(label);
        var ours = JpegDecoder.Decode(jpeg, scale);

        var golden = Golden.Value;
        golden.ShouldContainKey(label);
        var (w, h, sha) = golden[label];

        ours.Width.ShouldBe(w, label);
        ours.Height.ShouldBe(h, label);
        Sha256(ours.Pixels).ShouldBe(sha, $"{label}: decoded pixels differ from the frozen golden baseline");
    }

    private static string Sha256(byte[] pixels) => Convert.ToHexString(SHA256.HashData(pixels));

    // ------------------------------------------------------------ golden manifest

    private static readonly Lazy<Dictionary<string, (int W, int H, string Sha)>> Golden = new(LoadGolden);

    private static string GoldenFileName => "jpeg-oracle-golden.tsv";

    private static Dictionary<string, (int, int, string)> LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", GoldenFileName);
        var map = new Dictionary<string, (int, int, string)>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split('\t');
            map[parts[0]] = (int.Parse(parts[1]), int.Parse(parts[2]), parts[3]);
        }

        return map;
    }

    /// <summary>
    /// Regenerates <c>jpeg-oracle-golden.tsv</c> from the current decoder output.
    /// Skipped unless <c>REGEN_JPEG_ORACLE=1</c>; run it after an intentional
    /// decoder arithmetic change and review the diff.
    /// </summary>
    [Fact]
    public void RegenerateGolden()
    {
        if (Environment.GetEnvironmentVariable("REGEN_JPEG_ORACLE") != "1")
        {
            Assert.Skip("set REGEN_JPEG_ORACLE=1 to rewrite the golden baseline");
            return;
        }

        var lines = new List<string>
        {
            "# jpeg-oracle-golden.tsv - byte-exact baseline for JpegDecoderOracleTests.",
            "# label<TAB>width<TAB>height<TAB>sha256(RGBA pixels, uppercase hex).",
            "# Regenerate with REGEN_JPEG_ORACLE=1; see JpegDecoderOracleTests for provenance.",
        };
        foreach (var (label, build, scale) in OracleCases)
        {
            var ours = JpegDecoder.Decode(build(), scale);
            lines.Add($"{label}\t{ours.Width}\t{ours.Height}\t{Sha256(ours.Pixels)}");
        }

        var outPath = Path.Combine(RepoFixturesDir(), GoldenFileName);
        File.WriteAllLines(outPath, lines);
    }

    private static string RepoFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("could not locate the Sdcb.SimdCodecs.Tests/Fixtures source directory");
    }

    // ------------------------------------------------------------ restart markers

    [Fact]
    public void RestartIntervals_StreamContainsMarkers()
    {
        // Byte-exact decode is covered by the ByteExact theory ("restart intervals");
        // this proves the hand-built stream actually contains what we claim to test.
        var jpeg = BuildRestartMarkerJpeg(blocksW: 10, blocksH: 7, restartInterval: 7);
        ContainsMarker(jpeg, 0xDD).ShouldBeTrue("stream should declare a DRI interval");
        ContainsMarker(jpeg, 0xD0).ShouldBeTrue("stream should contain RST0");
    }

    private static bool ContainsMarker(byte[] jpeg, byte marker)
    {
        for (var i = 0; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == marker)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Minimal valid baseline JPEG: one grayscale component, custom Huffman
    /// tables (12 four-bit DC categories; a single one-bit AC EOB code), flat
    /// quantization, DC-only blocks with varying levels, and RSTn markers with
    /// DC-predictor resets every <paramref name="restartInterval"/> MCUs.
    /// </summary>
    private static byte[] BuildRestartMarkerJpeg(int blocksW, int blocksH, int restartInterval)
    {
        var output = new List<byte>();

        static void Marker(List<byte> o, byte m, params byte[] payload)
        {
            o.Add(0xFF);
            o.Add(m);
            if (m is 0xD8 or 0xD9)
                return;
            var len = payload.Length + 2;
            o.Add((byte)(len >> 8));
            o.Add((byte)len);
            o.AddRange(payload);
        }

        Marker(output, 0xD8); // SOI

        // DQT: table 0, 8-bit, all ones (identity quantization).
        var dqt = new byte[1 + 64];
        dqt[0] = 0x00;
        Array.Fill(dqt, (byte)1, 1, 64);
        Marker(output, 0xDB, dqt);

        // SOF0: 8-bit, 1 component, 1×1 sampling, quant table 0.
        var width = blocksW * 8;
        var height = blocksH * 8;
        Marker(output, 0xC0,
            8,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            1,          // components
            1, 0x11, 0  // id=1, h=v=1, tq=0
        );

        // DHT DC table 0: all 12 categories at code length 4 → canonical codes 0..11.
        var dhtDc = new byte[1 + 16 + 12];
        dhtDc[0] = 0x00;
        dhtDc[1 + 3] = 12; // twelve codes of length 4
        for (var i = 0; i < 12; i++)
            dhtDc[1 + 16 + i] = (byte)i;
        Marker(output, 0xC4, dhtDc);

        // DHT AC table 0: a single 1-bit code for EOB (run=0, size=0).
        var dhtAc = new byte[1 + 16 + 1];
        dhtAc[0] = 0x10;
        dhtAc[1 + 0] = 1;  // one code of length 1
        dhtAc[1 + 16] = 0x00;
        Marker(output, 0xC4, dhtAc);

        Marker(output, 0xDD, (byte)(restartInterval >> 8), (byte)restartInterval); // DRI

        Marker(output, 0xDA, 1, 1, 0x00, 0, 63, 0); // SOS: component 1, DC/AC table 0

        // Entropy data: per block emit DC category+magnitude then the 1-bit EOB.
        var acc = 0;
        var nbits = 0;

        void PutBits(int value, int count)
        {
            if (count == 0)
                return;
            acc = (acc << count) | (value & ((1 << count) - 1));
            nbits += count;
            while (nbits >= 8)
            {
                var b = (byte)(acc >> (nbits - 8));
                output.Add(b);
                if (b == 0xFF)
                    output.Add(0x00); // byte stuffing
                nbits -= 8;
                acc &= (1 << nbits) - 1;
            }
        }

        void AlignWithOnes()
        {
            if (nbits > 0)
                PutBits((1 << (8 - nbits)) - 1, 8 - nbits);
        }

        var pred = 0;
        var rst = 0;
        var sinceRestart = 0;
        var totalBlocks = blocksW * blocksH;
        for (var b = 0; b < totalBlocks; b++)
        {
            if (sinceRestart == restartInterval)
            {
                AlignWithOnes();
                output.Add(0xFF);
                output.Add((byte)(0xD0 + (rst & 7)));
                rst++;
                pred = 0;
                sinceRestart = 0;
            }

            var dc = (b * 37) % 401 - 200; // deterministic level sweep, both signs
            var diff = dc - pred;
            pred = dc;

            var cat = 0;
            for (var a = Math.Abs(diff); a > 0; a >>= 1)
                cat++;
            PutBits(cat, 4);                                       // DC category (4-bit code = category)
            PutBits(diff >= 0 ? diff : diff + (1 << cat) - 1, cat); // magnitude bits
            PutBits(0, 1);                                          // AC EOB

            sinceRestart++;
        }

        AlignWithOnes();
        Marker(output, 0xD9); // EOI
        return output.ToArray();
    }

    // ------------------------------------------------------------ diagnostics

    /// <summary>
    /// <c>JpegDiagnostics.DecodePlanes</c> reassembles full planes from the streaming
    /// strips; for grayscale the plane is exactly the decoded output's R channel.
    /// </summary>
    [Theory]
    [InlineData("grayscale")]
    [InlineData("grayscale progressive")]
    [InlineData("writer grayscale restart 3 cut at RST7")]
    public void DecodePlanes_GrayMatchesDecode(string label)
    {
        var (jpeg, _) = BuildCase(label);
        var image = JpegDecoder.Decode(jpeg);
        var plane = JpegDiagnostics.DecodePlanes(jpeg).ShouldHaveSingleItem();
        (plane.Width, plane.Height).ShouldBe((image.Width, image.Height));
        for (var i = 0; i < plane.Samples.Length; i++)
            plane.Samples[i].ShouldBe(image.Pixels[i * 4], $"sample {i}");
    }

    // ------------------------------------------------------------ bit reader

    /// <summary>
    /// The entropy bit reader refills eight bytes at a time and falls back to byte-wise
    /// reads around 0xFF. Noise at high quality makes 0xFF00 stuffing frequent, and
    /// restart intervals 1–7 put RSTn markers at every alignment within the 8-byte
    /// window; libjpeg must agree with every such decode.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void BitReader_StuffingAndMarkersAtEveryAlignment_AgreeWithLibjpeg(int restartInterval)
    {
        const int w = 83, h = 37;
        var rng = new Random(restartInterval + 100);
        var noise = new byte[w * h * 3];
        rng.NextBytes(noise);
        TestJpegWriter.Component[] comps = [new(1, 2, 2), new(2, 1, 1), new(3, 1, 1)];
        var jpeg = TestJpegWriter.Encode(w, h, comps, (k, x, y) => noise[(y * w + x) * 3 + k],
            new() { Quality = 97, RestartInterval = restartInterval });

        var stuffed = 0;
        var markers = 0;
        for (var i = 0; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0x00)
                stuffed++;
            if (jpeg[i] == 0xFF && jpeg[i + 1] is >= 0xD0 and <= 0xD7)
                markers++;
        }

        stuffed.ShouldBeGreaterThan(50, "the stream should exercise byte stuffing");
        if (restartInterval > 0)
            markers.ShouldBe((6 * 3 - 1) / restartInterval); // 6 × 3 MCUs, a marker between intervals

        var ours = JpegDecoder.Decode(jpeg);
        using var m = new MagickImage(jpeg);
        using var px = m.GetPixels();
        var vals = px.GetValues()!;
        var mc = (int)px.Channels;
        var max = 0;
        for (var i = 0; i < w * h; i++)
            for (var ch = 0; ch < 3; ch++)
                max = Math.Max(max, Math.Abs((int)Math.Round(vals[i * mc + ch] / 257f) - ours.Pixels[i * 4 + ch]));
        max.ShouldBeLessThanOrEqualTo(4);
    }

    /// <summary>
    /// After a marker inside entropy data the scan reads zeros: bytes past the marker
    /// must never leak into the decode, whatever the read-ahead window holds.
    /// </summary>
    [Fact]
    public void BitReader_NeverReadsPastAMarker()
    {
        const int w = 67, h = 45;
        var rgb = MakeRgbPattern(w, h);
        var jpeg = TestJpegWriter.Encode(w, h, [new(1, 2, 2), new(2, 1, 1), new(3, 1, 1)],
            (k, x, y) => rgb[(y * w + x) * 3 + Math.Min(k, 2)], new() { Quality = 90 });
        var sos = 0;
        for (var i = 0; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDA)
                sos = i;
        }

        var rng = new Random(5);
        for (var trial = 0; trial < 40; trial++)
        {
            var cut = sos + 20 + rng.Next(jpeg.Length - sos - 40);
            if (jpeg[cut - 1] == 0xFF)
                continue;
            var a = jpeg.ToArray();
            a[cut] = 0xFF;
            a[cut + 1] = (byte)(0xD0 + rng.Next(8));
            var b = a.ToArray();
            for (var i = cut + 2; i < b.Length; i++)
                b[i] = (byte)rng.Next(0xFF); // anything but 0xFF: maximal fast-refill exposure
            DecodeOutcome(a).ShouldBe(DecodeOutcome(b), $"marker at {cut}");
        }
    }

    private static string DecodeOutcome(byte[] jpeg)
    {
        try
        {
            return Sha256(JpegDecoder.Decode(jpeg).Pixels);
        }
        catch (InvalidDataException e)
        {
            return "throws: " + e.Message;
        }
    }

    /// <summary>
    /// A corrupt AC run can push the zig-zag index past 63 (up to 78). The decoder
    /// clamps such a coefficient to the last position, as stb does — never outside the
    /// block. Two single-block streams that differ only in reaching position 63 by an
    /// overshooting (15, 1) symbol or by a legal (0, 1) one must decode identically.
    /// </summary>
    [Fact]
    public void Baseline_RunPastBlockEnd_ClampsToLastCoefficient()
    {
        var legal = BuildOneBlockJpeg(overshoot: false);
        var overshoot = BuildOneBlockJpeg(overshoot: true);
        legal.ShouldNotBe(overshoot);
        Sha256(JpegDecoder.Decode(overshoot).Pixels).ShouldBe(Sha256(JpegDecoder.Decode(legal).Pixels));
        // And the coefficient really landed: not a flat block.
        JpegDecoder.Decode(legal).Pixels.Distinct().Count().ShouldBeGreaterThan(2);
    }

    private static byte[] BuildOneBlockJpeg(bool overshoot)
    {
        var o = new List<byte> { 0xFF, 0xD8 };
        void Marker(byte m, params byte[] payload)
        {
            o.AddRange([0xFF, m, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            o.AddRange(payload);
        }

        var dqt = new byte[65];
        Array.Fill(dqt, (byte)8, 1, 64);
        Marker(0xDB, dqt);
        Marker(0xC0, 8, 0, 8, 0, 8, 1, 1, 0x11, 0);
        // DC: one 1-bit code "0" = category 0. AC: "0" = (0,1), "10" = (15,1).
        var dc = new byte[1 + 16 + 1];
        dc[1] = 1;
        Marker(0xC4, dc);
        var ac = new byte[1 + 16 + 2];
        ac[0] = 0x10;
        ac[1] = 1;
        ac[2] = 1;
        ac[17] = 0x01;
        ac[18] = 0xF1;
        Marker(0xC4, ac);
        Marker(0xDA, 1, 1, 0x00, 0, 63, 0);

        var bits = new List<int> { 0 };          // DC category 0
        for (var k = 1; k <= 62; k++)
            bits.AddRange([0, k % 3 == 0 ? 0 : 1]); // (0,1), value ±1
        if (overshoot)
            bits.AddRange([1, 0, 1]);              // (15,1) at k = 63 → k = 78, value +1
        else
            bits.AddRange([0, 1]);                 // (0,1) at k = 63, value +1
        while (bits.Count % 8 != 0)
            bits.Add(1);
        for (var i = 0; i < bits.Count; i += 8)
        {
            var b = 0;
            for (var j = 0; j < 8; j++)
                b = (b << 1) | bits[i + j];
            o.Add((byte)b);
            if (b == 0xFF)
                o.Add(0);
        }

        o.AddRange([0xFF, 0xD9]);
        return o.ToArray();
    }

    // ------------------------------------------------------------ independent oracle

    /// <summary>
    /// The TestJpegWriter streams are only worth pinning if they are valid JPEG:
    /// libjpeg (via Magick) must decode the YCbCr / grayscale ones to within a
    /// couple of code values of our decode.
    /// </summary>
    [Theory]
    [InlineData("4:1:1")]
    [InlineData("4:4:0")]
    [InlineData("Y4x1 Cb2x1 Cr1x1")]
    [InlineData("Y1x4 Cb1x2 Cr1x1")]
    [InlineData("4:2:0 restart 5")]
    [InlineData("4:2:0 restart 1")]
    [InlineData("4:2:0 non-interleaved")]
    [InlineData("4:2:0 non-interleaved restart 4")]
    [InlineData("grayscale restart 3")]
    [InlineData("RGB ids")]
    [InlineData("RGB Adobe subsampled")]
    public void WriterStreams_AgreeWithLibjpeg(string label)
    {
        var jpeg = WriterCases().First(c => c.Label == label).Build();
        var ours = JpegDecoder.Decode(jpeg);

        using var m = new MagickImage(jpeg);
        m.Width.ShouldBe((uint)ours.Width);
        m.Height.ShouldBe((uint)ours.Height);
        using var px = m.GetPixels();
        var vals = px.GetValues()!;
        var mc = (int)px.Channels;
        var max = 0;
        var sum = 0.0;
        var n = ours.Width * ours.Height;
        for (var i = 0; i < n; i++)
        {
            for (var ch = 0; ch < 3; ch++)
            {
                var libjpeg = (int)Math.Round(vals[i * mc + (mc >= 3 ? ch : 0)] / 257f);
                var diff = Math.Abs(libjpeg - ours.Pixels[i * 4 + ch]);
                sum += diff;
                max = Math.Max(max, diff);
            }
        }

        max.ShouldBeLessThanOrEqualTo(4, label);
        (sum / (n * 3)).ShouldBeLessThanOrEqualTo(0.5, label);
    }

    [Fact]
    public void FullScale_WithinToleranceOfLibjpeg()
    {
        const int w = 64, h = 48;
        var jpeg = EncodeJpeg(MakeRgbPattern(w, h), w, h, 90, "4:2:0");
        var ours = JpegDecoder.Decode(jpeg);

        using var m = new MagickImage(jpeg);
        using var px = m.GetPixels();
        var vals = px.GetValues()!;
        var mc = (int)px.Channels;

        var sum = 0.0;
        var max = 0;
        for (var i = 0; i < w * h; i++)
        {
            for (var ch = 0; ch < 3; ch++)
            {
                var libjpeg = (int)Math.Round(vals[i * mc + ch] / 257f);
                var diff = Math.Abs(libjpeg - ours.Pixels[i * 4 + ch]);
                sum += diff;
                max = Math.Max(max, diff);
            }
        }

        max.ShouldBeLessThanOrEqualTo(6, "our decode and libjpeg's should agree closely");
        (sum / (w * h * 3)).ShouldBeLessThanOrEqualTo(1.5);
    }

    // ------------------------------------------------------------ info + errors

    [Fact]
    public void ReadInfo_ReportsDimensionsComponentsProgressive()
    {
        var baseline = EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 90, "4:2:0");
        JpegDecoder.ReadInfo(baseline).ShouldBe(
            new JpegInfo(67, 45, 3, Progressive: false) { ChromaSubsampled = true });

        var progressive = EncodeJpeg(MakeRgbPattern(20, 10), 20, 10, 90, progressive: true);
        var progInfo = JpegDecoder.ReadInfo(progressive);
        (progInfo.Width, progInfo.Height, progInfo.Components, progInfo.Progressive)
            .ShouldBe((20, 10, 3, true));

        var fullChroma = EncodeJpeg(MakeRgbPattern(16, 16), 16, 16, 90, "4:4:4");
        JpegDecoder.ReadInfo(fullChroma).ChromaSubsampled.ShouldBeFalse();

        var subsampled422 = EncodeJpeg(MakeRgbPattern(16, 16), 16, 16, 90, "4:2:2");
        JpegDecoder.ReadInfo(subsampled422).ChromaSubsampled.ShouldBeTrue();

        var gray = EncodeJpeg(MakeRgbPattern(12, 9), 12, 9, 90, gray: true);
        var grayInfo = JpegDecoder.ReadInfo(gray);
        grayInfo.Components.ShouldBe(1);
        grayInfo.ChromaSubsampled.ShouldBeFalse();

        var cmyk = EncodeJpeg(MakeRgbPattern(12, 9), 12, 9, 90, cmyk: true);
        JpegDecoder.ReadInfo(cmyk).Components.ShouldBe(4);
    }

    [Fact]
    public void Rejects_NonJpegInput()
    {
        Should.Throw<InvalidDataException>(() => JpegDecoder.ReadInfo(Array.Empty<byte>()));
        Should.Throw<InvalidDataException>(() => JpegDecoder.ReadInfo("definitely not a JPEG"u8.ToArray()));
        Should.Throw<InvalidDataException>(() => JpegDecoder.Decode(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
    }
}
