using System.Diagnostics;
using System.Security.Cryptography;
using ImageMagick;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Byte-exact + acceptance tests for <see cref="JpegEncoder"/>.
///
/// <para><b>Byte-exactness.</b> The encoder is a faithful port of the
/// stb_image_write JPEG writer, so for the same pixels + quality (and its
/// quality-derived subsampling) our bytes must equal the reference's exactly.
/// Two layers enforce this: a committed digest manifest
/// (<c>jpeg-encoder-golden.tsv</c>) that CI checks with no external dependency,
/// and — when the oracle binary is present — a direct byte comparison against
/// <c>jpegenc.exe</c> (built from the pinned reference via
/// <c>Oracle/jpegenc/build.sh</c>). The golden IS the oracle output, frozen; the
/// two layers agree by construction.</para>
///
/// <para><b>Determinism.</b> The reference contracts float multiply-adds into FMA
/// on some targets; the oracle is compiled with <c>-ffp-contract=off</c> so both
/// sides do plain multiply+add, matching the managed encoder bit-for-bit across
/// platforms.</para>
///
/// <para>Independent acceptance (libjpeg via Magick.NET + our own decoder) and
/// round-trip PSNR guard against "byte-exact to a broken reference".</para>
/// </summary>
public sealed class JpegEncoderOracleTests
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

    // ------------------------------------------------------------ case matrix
    //
    // One source of truth for both the byte-exact theory and the golden regen, so
    // "what we assert" and "what we froze" never drift. Quality drives subsampling
    // under the default (Auto) options, exactly like the reference.

    private static readonly (string Label, int W, int H, int C, int Q)[] Cases = BuildCases();

    private static (string, int, int, int, int)[] BuildCases()
    {
        var list = new List<(string, int, int, int, int)>();

        // Sizes (incl. partial edge MCUs in each axis, and the 1×1 degenerate)
        // × qualities spanning both subsampling regimes and the quant extremes.
        foreach (var (w, h) in new[] { (64, 48), (67, 45), (1, 1), (8, 8), (16, 16), (17, 13), (130, 7), (7, 130) })
            foreach (var q in new[] { 100, 95, 90, 75, 25, 1 })
                list.Add(($"{w}x{h}_c3_q{q}", w, h, 3, q));

        // Channel counts: 1 gray, 2 gray+alpha, 3 RGB, 4 RGBA (alpha ignored).
        foreach (var c in new[] { 1, 2, 3, 4 })
            list.Add(($"48x32_c{c}_q90", 48, 32, c, 90));

        return list.ToArray();
    }

    public static IEnumerable<object[]> CaseLabels() => Cases.Select(c => new object[] { c.Label });

    private static (string Label, int W, int H, int C, int Q) Case(string label) => Cases.First(c => c.Label == label);

    private static byte[] Encode(string label)
    {
        var (_, w, h, c, q) = Case(label);
        return JpegEncoder.Encode(MakePattern(w, h, c), w, h, c, new JpegEncodeOptions { Quality = q });
    }

    // ------------------------------------------------------------ golden digests

    [Theory]
    [MemberData(nameof(CaseLabels))]
    public void MatchesFrozenGolden(string label)
    {
        var golden = Golden.Value;
        golden.ShouldContainKey(label);
        Sha256(Encode(label)).ShouldBe(golden[label], $"{label}: encoded bytes differ from the frozen (oracle-validated) golden");
    }

    private static readonly Lazy<Dictionary<string, string>> Golden = new(LoadGolden);
    private const string GoldenFileName = "jpeg-encoder-golden.tsv";

    private static Dictionary<string, string> LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", GoldenFileName);
        var map = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split('\t');
            map[parts[0]] = parts[1];
        }

        return map;
    }

    /// <summary>Regenerates <c>jpeg-encoder-golden.tsv</c> from the current encoder.
    /// Skipped unless <c>REGEN_JPEG_ENCODER_GOLDEN=1</c>; run after an intentional
    /// change (or a reference bump) and review the diff — ideally with the oracle
    /// present so <see cref="ByteExactVsOracle"/> confirms the new bytes.</summary>
    [Fact]
    public void RegenerateGolden()
    {
        if (Environment.GetEnvironmentVariable("REGEN_JPEG_ENCODER_GOLDEN") != "1")
        {
            Assert.Skip("set REGEN_JPEG_ENCODER_GOLDEN=1 to rewrite the golden manifest");
            return;
        }

        var lines = new List<string>
        {
            "# jpeg-encoder-golden.tsv - byte-exact baseline for JpegEncoderOracleTests.",
            "# label<TAB>sha256(encoded JPEG bytes, uppercase hex).",
            "# The bytes are byte-for-byte identical to the stb_image_write reference",
            "# (Oracle/jpegenc); regenerate with REGEN_JPEG_ENCODER_GOLDEN=1.",
        };
        foreach (var (label, _, _, _, _) in Cases)
            lines.Add($"{label}\t{Sha256(Encode(label))}");

        File.WriteAllLines(Path.Combine(RepoFixturesDir(), GoldenFileName), lines);
    }

    // ------------------------------------------------------------ direct oracle byte-match

    [Theory]
    [MemberData(nameof(CaseLabels))]
    public void ByteExactVsOracle(string label)
    {
        var oracle = RequireOracle();

        var (_, w, h, c, q) = Case(label);
        var pixels = MakePattern(w, h, c);

        var rawPath = Path.GetTempFileName();
        var outPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(rawPath, pixels);
            var (exit, stderr) = Run(oracle, $"{w} {h} {c} {q} \"{rawPath}\" \"{outPath}\"");
            exit.ShouldBe(0, $"oracle failed for {label}: {stderr}");

            var reference = File.ReadAllBytes(outPath);
            var ours = JpegEncoder.Encode(pixels, w, h, c, new JpegEncodeOptions { Quality = q });
            ours.ShouldBe(reference, $"{label}: encoder output differs from the stb_image_write reference");
        }
        finally
        {
            File.Delete(rawPath);
            File.Delete(outPath);
        }
    }

    // ------------------------------------------------------------ independent acceptance

    [Theory]
    [InlineData(95)]
    [InlineData(75)]
    [InlineData(25)]
    public void LibjpegDecodesWithinToleranceOfOurDecoder(int quality)
    {
        // Our bytes must be a JPEG libjpeg (Magick) reads, and its pixels must sit
        // within a couple of code values of our own decoder — proof the bytes are a
        // conformant stream, not just self-consistent.
        const int w = 64, h = 48;
        var jpeg = JpegEncoder.Encode(MakePattern(w, h, 3), w, h, 3, new JpegEncodeOptions { Quality = quality });

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

        max.ShouldBeLessThanOrEqualTo(4, "our encode + two conformant decoders should agree closely");
        (sum / (w * h * 3)).ShouldBeLessThanOrEqualTo(1.0);
    }

    [Theory]
    [InlineData(95, JpegSubsampling.Auto, 37.0)]   // q95 → 4:4:4
    [InlineData(90, JpegSubsampling.Auto, 30.0)]   // q90 → 4:2:0
    [InlineData(75, JpegSubsampling.Chroma444, 31.0)]
    [InlineData(75, JpegSubsampling.Chroma420, 29.0)]
    public void RoundTripPsnrOnPhoto(int quality, JpegSubsampling subsampling, double minPsnr)
    {
        // Photo-like content → our encode → our decode: PSNR floors pin encode quality and
        // exercise the explicit-subsampling overrides (which have no byte oracle,
        // since the reference derives subsampling from quality).
        var (rgb, w, h) = PhotoRgb();
        var jpeg = JpegEncoder.Encode(rgb, w, h, 3, new JpegEncodeOptions { Quality = quality, Subsampling = subsampling });
        var decoded = JpegDecoder.Decode(jpeg);
        decoded.Width.ShouldBe(w);
        decoded.Height.ShouldBe(h);

        var psnr = Psnr(rgb, decoded.Pixels, w, h);
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
    public void GrayInputEncodesAndDecodesGray()
    {
        // comp==1 replicates into YCbCr (matching the reference); the decode is a
        // neutral gray, so R==G==B within the codec's rounding.
        const int w = 32, h = 24;
        var gray = MakePattern(w, h, 1);
        var jpeg = JpegEncoder.Encode(gray, w, h, 1, new JpegEncodeOptions { Quality = 92 });
        var decoded = JpegDecoder.Decode(jpeg);

        for (var i = 0; i < w * h; i++)
        {
            int r = decoded.Pixels[i * 4], g = decoded.Pixels[i * 4 + 1], b = decoded.Pixels[i * 4 + 2];
            Math.Abs(r - g).ShouldBeLessThanOrEqualTo(2);
            Math.Abs(g - b).ShouldBeLessThanOrEqualTo(2);
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

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static (byte[] Rgb, int W, int H) PhotoRgb()
    {
        const int w = 609, h = 406;
        return (SyntheticPhoto.Rgb(w, h), w, h);
    }

    private static double Psnr(byte[] a, byte[] decodedRgba, int w, int h)
    {
        // a is packed RGB (3/px); decoded is RGBA (4/px).
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
        var mse = (double)sq / (w * h * 3);
        return 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>
    /// Resolves <c>jpegenc.exe</c> or stops the test — skipping locally, failing
    /// under <c>REQUIRE_ORACLES=1</c>. CI builds the binary, so a missing one
    /// there means the build step broke rather than that a dev never compiled
    /// it. The committed golden guards the encoder either way; what *this* test
    /// adds is proof the golden still matches the live reference.
    /// </summary>
    private static string RequireOracle()
    {
        var oracle = FindOracle();
        OracleGate.RequireOrSkip(oracle is not null, "jpegenc.exe",
            "Build it with `bash tests/Sdcb.SimdCodecs.Tests/Oracle/jpegenc/build.sh` " +
            "(needs clang and curl). The committed golden still guards the encoder without it.");
        return oracle!;
    }

    private static string? FindOracle()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "Oracle", "jpegenc.exe");
            if (File.Exists(direct)) return direct;
            var bin = Path.Combine(dir.FullName, "tests", "Sdcb.SimdCodecs.Tests", "Oracle", "bin", "jpegenc.exe");
            if (File.Exists(bin)) return bin;
        }

        return null;
    }

    private static (int Exit, string Stderr) Run(string exe, string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        p.Start();
        var stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stderr);
    }

    private static string RepoFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("could not locate the Sdcb.SimdCodecs.Tests/Fixtures source directory");
    }
}
