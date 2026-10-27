using System.Diagnostics;
using System.Security.Cryptography;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class WebpOracleTests
{
    private const string GoldenFileName = "webp-oracle-golden.tsv";

    private sealed record GoldenEntry(
        int Width,
        int Height,
        string YuvSha256,
        string NoFancyRgbaSha256,
        string FancyRgbaSha256);

    private static readonly Lazy<Dictionary<string, GoldenEntry>> Golden = new(LoadGolden);

    public static IEnumerable<object[]> RgbaFixtureCases() =>
        FixtureNames().SelectMany(
            static fixtureName => new[]
            {
                new object[] { fixtureName, true },
                new object[] { fixtureName, false },
            });

    public static IEnumerable<object[]> YuvFixtureCases()
    {
        foreach (string fixtureName in FixtureNames())
        {
            string input = Path.Combine(FixtureDirectory(), fixtureName);
            WebpInfo info = WebpDecoder.ReadInfo(File.ReadAllBytes(input));
            if (info.Format == WebpFormat.Lossy && !info.HasAlpha)
            {
                yield return new object[] { fixtureName };
            }
        }
    }

    [Fact]
    public void GoldenManifestCoversEveryFixture()
    {
        string[] fixtures = FixtureNames().ToArray();
        string[] manifest = Golden.Value.Keys.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(fixtures, manifest);
    }

    [Theory]
    [MemberData(nameof(RgbaFixtureCases))]
    public void RgbaMatchesGolden(string fixtureName, bool noFancy)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixtureName));
        WebpInfo info = WebpDecoder.ReadInfo(source);
        GoldenEntry expectedGolden = Golden.Value[fixtureName];
        Assert.Equal((expectedGolden.Width, expectedGolden.Height), (info.Width, info.Height));

        string actualSha256 = Sha256(DecodeRgba(source, info, noFancy));
        string expectedSha256 = noFancy
            ? expectedGolden.NoFancyRgbaSha256
            : expectedGolden.FancyRgbaSha256;
        Assert.True(
            actualSha256 == expectedSha256,
            $"{fixtureName} {(noFancy ? "nofancy" : "fancy")}: " +
            $"golden SHA-256 expected {expectedSha256}, actual {actualSha256}.");
    }

    [Theory]
    [MemberData(nameof(RgbaFixtureCases))]
    public void RgbaMatchesLiveDwebp(string fixtureName, bool noFancy)
    {
        string dwebp = WebpOracle.RequireDwebp();
        string input = Path.Combine(FixtureDirectory(), fixtureName);
        byte[] source = File.ReadAllBytes(input);
        WebpInfo info = WebpDecoder.ReadInfo(source);
        byte[] actual = DecodeRgba(source, info, noFancy);
        byte[] oracle = RunDwebp(dwebp, input, yuv: false, noFancy);
        if (!oracle.AsSpan().SequenceEqual(actual))
        {
            Assert.Fail(DescribeRgbaMismatch(fixtureName, noFancy, oracle, actual, info.Width));
        }
    }

    [Theory]
    [MemberData(nameof(YuvFixtureCases))]
    public void YuvMatchesGolden(string fixtureName)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixtureName));
        string actualSha256 = Sha256(FlattenYuv(WebpDecoder.DecodeYuv(source)));
        GoldenEntry expectedGolden = Golden.Value[fixtureName];
        Assert.True(
            actualSha256 == expectedGolden.YuvSha256,
            $"{fixtureName}: YUV golden SHA-256 expected {expectedGolden.YuvSha256}, actual {actualSha256}.");
    }

    [Theory]
    [MemberData(nameof(YuvFixtureCases))]
    public void YuvMatchesLiveDwebp(string fixtureName)
    {
        string dwebp = WebpOracle.RequireDwebp();
        string input = Path.Combine(FixtureDirectory(), fixtureName);
        Vp8Yuv yuv = WebpDecoder.DecodeYuv(File.ReadAllBytes(input));
        byte[] actual = FlattenYuv(yuv);
        byte[] oracle = RunDwebp(dwebp, input, yuv: true, noFancy: false);
        if (!oracle.AsSpan().SequenceEqual(actual))
        {
            Assert.Fail(DescribeYuvMismatch(fixtureName, yuv, oracle, actual));
        }
    }

    [Fact]
    public void RegenerateGolden()
    {
        if (Environment.GetEnvironmentVariable("REGEN_WEBP_ORACLE") != "1")
        {
            Assert.Skip("set REGEN_WEBP_ORACLE=1 to regenerate dwebp oracle digests");
            return;
        }

        string? dwebp = WebpOracle.Dwebp;
        Assert.True(dwebp is not null, "dwebp is required to regenerate the WebP oracle manifest.");
        string fixtureDirectory = RepoFixtureDirectory();
        var lines = new List<string>
        {
            "# webp-oracle-golden.tsv - dwebp byte-exact baselines for WebpOracleTests.",
            "# fixture<TAB>width<TAB>height<TAB>sha256(YUV planes)<TAB>sha256(nofancy RGBA)<TAB>sha256(fancy RGBA).",
            "# Regenerate with REGEN_WEBP_ORACLE=1; hashes cover decoded bytes, not PAM headers.",
        };

        foreach (string fixtureName in FixtureNames(fixtureDirectory))
        {
            string input = Path.Combine(fixtureDirectory, fixtureName);
            WebpInfo info = WebpDecoder.ReadInfo(File.ReadAllBytes(input));
            byte[] yuv = RunDwebp(dwebp!, input, yuv: true, noFancy: false);
            byte[] noFancy = RunDwebp(dwebp!, input, yuv: false, noFancy: true);
            byte[] fancy = RunDwebp(dwebp!, input, yuv: false, noFancy: false);
            lines.Add(
                $"{fixtureName}\t{info.Width}\t{info.Height}\t" +
                $"{Sha256(yuv)}\t{Sha256(noFancy)}\t{Sha256(fancy)}");
        }

        File.WriteAllLines(Path.Combine(fixtureDirectory, GoldenFileName), lines);
    }

    private static byte[] DecodeRgba(byte[] source, WebpInfo info, bool noFancy)
    {
        byte[] rgba = new byte[checked(info.Width * info.Height * 4)];
        if (noFancy)
        {
            WebpDecoder.DecodeToNoFancy(source, rgba);
        }
        else
        {
            WebpDecoder.DecodeTo(source, rgba);
        }

        return rgba;
    }

    private static byte[] FlattenYuv(Vp8Yuv yuv)
    {
        int yLength = yuv.Width * yuv.Height;
        int chromaLength = ((yuv.Width + 1) / 2) * ((yuv.Height + 1) / 2);
        byte[] actual = new byte[checked(yLength + 2 * chromaLength)];
        yuv.Y.CopyTo(actual, 0);
        yuv.U.CopyTo(actual, yLength);
        yuv.V.CopyTo(actual, yLength + chromaLength);
        return actual;
    }

    private static string DescribeRgbaMismatch(
        string fixtureName,
        bool noFancy,
        byte[] expected,
        byte[] actual,
        int width)
    {
        int mismatchCount = 0;
        int maximumDifference = 0;
        int first = -1;
        for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
        {
            int difference = Math.Abs(actual[i] - expected[i]);
            if (difference != 0)
            {
                first = first < 0 ? i : first;
                mismatchCount++;
                maximumDifference = Math.Max(maximumDifference, difference);
            }
        }

        int pixel = Math.Max(first, 0) / 4;
        return
            $"{fixtureName} {(noFancy ? "nofancy" : "fancy")}: " +
            $"{mismatchCount} channel mismatches, max |diff| {maximumDifference}; " +
            $"first pixel ({pixel % width},{pixel / width}) channel {Math.Max(first, 0) % 4}, " +
            $"expected {(first >= 0 ? expected[first] : -1)}, " +
            $"actual {(first >= 0 ? actual[first] : -1)}, " +
            $"lengths expected/actual={expected.Length}/{actual.Length}.";
    }

    private static string DescribeYuvMismatch(string fixtureName, Vp8Yuv yuv, byte[] expected, byte[] actual)
    {
        int commonLength = Math.Min(expected.Length, actual.Length);
        int difference = 0;
        while (difference < commonLength && expected[difference] == actual[difference])
        {
            difference++;
        }

        int yLength = yuv.Width * yuv.Height;
        int chromaWidth = (yuv.Width + 1) / 2;
        int chromaHeight = (yuv.Height + 1) / 2;
        int chromaLength = chromaWidth * chromaHeight;
        int mismatchCount = Math.Abs(expected.Length - actual.Length);
        int negativeOneCount = 0;
        int positiveOneCount = 0;
        int maxAbsoluteDifference = 0;
        int[] planeMismatchCounts = new int[3];
        int[] planeMaxAbsoluteDifferences = new int[3];
        for (int i = 0; i < commonLength; i++)
        {
            int delta = actual[i] - expected[i];
            if (delta != 0)
            {
                mismatchCount++;
                int planeIndex = i < yLength ? 0 : i < yLength + chromaLength ? 1 : 2;
                planeMismatchCounts[planeIndex]++;
                planeMaxAbsoluteDifferences[planeIndex] =
                    Math.Max(planeMaxAbsoluteDifferences[planeIndex], Math.Abs(delta));
                if (delta == -1)
                {
                    negativeOneCount++;
                }
                else if (delta == 1)
                {
                    positiveOneCount++;
                }
                maxAbsoluteDifference = Math.Max(maxAbsoluteDifference, Math.Abs(delta));
            }
        }

        int planeOffset;
        int blockSize;
        string plane;
        if (difference < yLength)
        {
            plane = "Y";
            planeOffset = difference;
            blockSize = 16;
        }
        else if (difference < yLength + chromaLength)
        {
            plane = "U";
            planeOffset = difference - yLength;
            blockSize = 8;
        }
        else
        {
            plane = "V";
            planeOffset = difference - yLength - chromaLength;
            blockSize = 8;
        }

        int planeWidth = plane == "Y" ? yuv.Width : chromaWidth;
        int x = planeOffset % planeWidth;
        int row = planeOffset / planeWidth;
        return
            $"{fixtureName}: first YUV mismatch at byte {difference}, {plane}({x},{row}), " +
            $"MB=({x / blockSize},{row / blockSize}), mismatches {mismatchCount}, " +
            $"max |diff| {maxAbsoluteDifference}, planes Y/U/V=" +
            $"{planeMismatchCounts[0]}/{planeMismatchCounts[1]}/{planeMismatchCounts[2]} " +
            $"(max {planeMaxAbsoluteDifferences[0]}/{planeMaxAbsoluteDifferences[1]}/" +
            $"{planeMaxAbsoluteDifferences[2]}), diff -1/+1={negativeOneCount}/{positiveOneCount}, " +
            $"expected length {expected.Length}, actual length {actual.Length}, " +
            $"expected {(difference < expected.Length ? expected[difference] : -1)}, " +
            $"actual {(difference < actual.Length ? actual[difference] : -1)}.";
    }

    private static byte[] RunDwebp(string dwebp, string input, bool yuv, bool noFancy)
    {
        string output = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}{(yuv ? ".yuv" : ".pam")}");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(dwebp)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-quiet");
            if (yuv)
            {
                process.StartInfo.ArgumentList.Add(input);
                process.StartInfo.ArgumentList.Add("-yuv");
            }
            else
            {
                if (noFancy)
                {
                    process.StartInfo.ArgumentList.Add("-nofancy");
                }
                process.StartInfo.ArgumentList.Add("-pam");
                process.StartInfo.ArgumentList.Add(input);
            }

            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(output);
            process.Start();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            byte[] bytes = File.ReadAllBytes(output);
            return yuv ? bytes : ReadPamPixels(bytes);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static IEnumerable<string> FixtureNames() => FixtureNames(FixtureDirectory());

    private static IEnumerable<string> FixtureNames(string directory) =>
        Directory.EnumerateFiles(directory, "*.webp")
            .Select(static path => Path.GetFileName(path)!)
            .Where(static name => !name.StartsWith("anim-", StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal);

    private static string FixtureDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");

    private static Dictionary<string, GoldenEntry> LoadGolden()
    {
        string path = Path.Combine(FixtureDirectory(), GoldenFileName);
        var map = new Dictionary<string, GoldenEntry>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length != 6 ||
                !map.TryAdd(
                    parts[0],
                    new GoldenEntry(
                        int.Parse(parts[1]),
                        int.Parse(parts[2]),
                        parts[3],
                        parts[4],
                        parts[5])))
            {
                throw new InvalidDataException($"Invalid or duplicate WebP golden row: {line}");
            }
        }

        return map;
    }

    private static string RepoFixtureDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "tests",
                "Sdcb.SimdCodecs.Tests",
                "Fixtures",
                "webp");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the WebP fixture source directory");
    }

    private static byte[] ReadPamPixels(byte[] pam)
    {
        int lineStart = 0;
        while (lineStart < pam.Length)
        {
            int lineEnd = Array.IndexOf(pam, (byte)'\n', lineStart);
            if (lineEnd < 0)
            {
                break;
            }

            string line = System.Text.Encoding.ASCII
                .GetString(pam, lineStart, lineEnd - lineStart)
                .TrimEnd('\r');
            lineStart = lineEnd + 1;
            if (line == "ENDHDR")
            {
                return pam.AsSpan(lineStart).ToArray();
            }
        }

        throw new InvalidDataException("The dwebp output does not contain a PAM header.");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
