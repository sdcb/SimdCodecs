using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class WebpAnimationOracleTests
{
    private const string GoldenFileName = "webp-animation-oracle-golden.tsv";

    private sealed record GoldenEntry(
        int Width,
        int Height,
        int TimestampMs,
        int DurationMs,
        string Sha256);

    private static readonly Lazy<Dictionary<(string Fixture, int Frame), GoldenEntry>> Golden =
        new(LoadGolden);

    public static IEnumerable<object[]> AnimationFixtureCases() =>
        OracleFixtureNames().Select(static name => new object[] { name });

    public static IEnumerable<object[]> AllAnimationFixtureCases() =>
        FixtureNames().Select(static name => new object[] { name });

    public static IEnumerable<object[]> AnimationFrameCases()
    {
        foreach (string fixture in OracleFixtureNames())
        {
            string path = Path.Combine(FixtureDirectory(), fixture);
            int frameCount = WebpDecoder.ReadInfo(File.ReadAllBytes(path)).FrameCount;
            for (int index = 0; index < frameCount; index++)
            {
                yield return new object[] { fixture, index };
            }
        }
    }

    [Fact]
    public void GoldenManifestCoversEveryAnimationFrame()
    {
        string[] expected = OracleFixtureNames()
            .SelectMany(fixture =>
            {
                int frameCount = WebpDecoder.ReadInfo(
                    File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixture))).FrameCount;
                return Enumerable.Range(0, frameCount).Select(index => $"{fixture}\t{index}");
            })
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        string[] actual = Golden.Value.Keys
            .Select(static key => $"{key.Fixture}\t{key.Frame}")
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AnimationFrameCases))]
    public void AnimationFrameMatchesGolden(string fixture, int frameIndex)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixture));
        var decoder = new WebpAnimationDecoder(source);
        byte[] canvas = new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4];
        WebpFrameInfo frame = default;
        for (int index = 0; index <= frameIndex; index++)
        {
            try
            {
                if (!decoder.TryDecodeNextFrame(canvas, out frame))
                {
                    throw new InvalidDataException($"{fixture} ended before frame {frameIndex}.");
                }
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"{fixture} frame {index} failed to decode.", exception);
            }
        }

        GoldenEntry expected = Golden.Value[(fixture, frameIndex)];

        Assert.Equal((expected.Width, expected.Height), (decoder.CanvasWidth, decoder.CanvasHeight));
        Assert.Equal(expected.TimestampMs, frame.TimestampMs);
        Assert.Equal(expected.DurationMs, frame.DurationMs);
        Assert.Equal(expected.Sha256, Sha256(canvas));
    }

    [Theory]
    [MemberData(nameof(AnimationFixtureCases))]
    public void AnimationFramesMatchAnimDump(string fixture)
    {
        string animDump = WebpOracle.RequireAnimDump();

        string input = Path.Combine(FixtureDirectory(), fixture);
        byte[] source = File.ReadAllBytes(input);
        var decoder = new WebpAnimationDecoder(source);
        byte[] canvas = new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4];
        var actualFrames = new List<byte[]>();
        while (true)
        {
            int frameIndex = decoder.NextFrameIndex;
            bool decoded;
            try
            {
                decoded = decoder.TryDecodeNextFrame(canvas, out _);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"{fixture} frame {frameIndex} failed to decode.", exception);
            }

            if (!decoded)
            {
                break;
            }

            actualFrames.Add(canvas.ToArray());
        }

        byte[][] oracleFrames = RunAnimDump(
            animDump,
            input,
            decoder.CanvasWidth,
            decoder.CanvasHeight);
        Assert.Equal(decoder.FrameCount, oracleFrames.Length);
        for (int index = 0; index < oracleFrames.Length; index++)
        {
            byte[] actual = actualFrames[index];
            if (!actual.AsSpan().SequenceEqual(oracleFrames[index]))
            {
                Assert.Fail(DescribeMismatch(fixture, index, oracleFrames[index], actual, decoder.CanvasWidth));
            }
        }
    }

    [Fact]
    public void RegenerateAnimationGoldenManifest()
    {
        if (Environment.GetEnvironmentVariable("REGEN_ANIMATION_WEBP_ORACLE") != "1")
        {
            Assert.Skip("set REGEN_ANIMATION_WEBP_ORACLE=1 to regenerate the animation oracle manifest");
            return;
        }

        string animDump = WebpOracle.RequireAnimDump();

        string fixtureDirectory = RepoFixtureDirectory();
        var lines = new List<string>
        {
            "# webp-animation-oracle-golden.tsv - per-frame anim_dump 1.6.0 RGBA baselines.",
            "# fixture<TAB>frame<TAB>canvas-width<TAB>canvas-height<TAB>timestamp-ms<TAB>duration-ms<TAB>sha256(RGBA).",
            "# Regenerate with REGEN_ANIMATION_WEBP_ORACLE=1 and anim_dump on PATH or ANIM_DUMP.",
        };
        foreach (string fixture in OracleFixtureNames(fixtureDirectory))
        {
            string input = Path.Combine(fixtureDirectory, fixture);
            var decoder = new WebpAnimationDecoder(File.ReadAllBytes(input));
            byte[][] oracleFrames = RunAnimDump(
                animDump,
                input,
                decoder.CanvasWidth,
                decoder.CanvasHeight);
            if (oracleFrames.Length != decoder.FrameCount)
            {
                throw new InvalidDataException(
                    $"{fixture}: anim_dump returned {oracleFrames.Length} frames, expected {decoder.FrameCount}.");
            }

            for (int index = 0; index < oracleFrames.Length; index++)
            {
                WebpFrameInfo frame = decoder.Frames[index];
                lines.Add(
                    $"{fixture}\t{index}\t{decoder.CanvasWidth}\t{decoder.CanvasHeight}\t" +
                    $"{frame.TimestampMs}\t{frame.DurationMs}\t{Sha256(oracleFrames[index])}");
            }
        }

        File.WriteAllLines(Path.Combine(fixtureDirectory, GoldenFileName), lines);
    }

    [Theory]
    [MemberData(nameof(AllAnimationFixtureCases))]
    public void SeekReproducesSequentialFramesInRandomOrder(string fixture)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixture));
        var sequentialDecoder = new WebpAnimationDecoder(source);
        byte[] sequentialCanvas = new byte[sequentialDecoder.CanvasWidth * sequentialDecoder.CanvasHeight * 4];
        var sequential = new List<(byte[] Rgba, WebpFrameInfo Info)>();
        while (sequentialDecoder.TryDecodeNextFrame(sequentialCanvas, out WebpFrameInfo info))
        {
            sequential.Add((sequentialCanvas.ToArray(), info));
        }

        var random = new Random(0x9649);
        int[] shuffled = Enumerable.Range(0, sequential.Count).ToArray();
        for (int index = shuffled.Length - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            (shuffled[index], shuffled[swapIndex]) = (shuffled[swapIndex], shuffled[index]);
        }

        var decoder = new WebpAnimationDecoder(source);
        byte[] canvas = new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4];
        foreach (int index in Enumerable.Range(0, sequential.Count).Concat(shuffled))
        {
            decoder.Seek(index);
            Assert.Equal(index, decoder.NextFrameIndex);
            Assert.True(decoder.TryDecodeNextFrame(canvas, out WebpFrameInfo info));
            Assert.Equal(sequential[index].Info, info);
            Assert.Equal(sequential[index].Rgba, canvas);
            Assert.Equal(index + 1, decoder.NextFrameIndex);
            if (index == sequential.Count - 1)
            {
                Assert.False(decoder.TryDecodeNextFrame(canvas, out _));
            }
        }

        decoder.Reset();
        Assert.Equal(0, decoder.NextFrameIndex);
    }

    [Theory]
    [MemberData(nameof(AllAnimationFixtureCases))]
    public void FrameIndexAtUsesDurationBoundaries(string fixture)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixture));
        var decoder = new WebpAnimationDecoder(source);
        Assert.Equal(0, decoder.FrameIndexAt(-1));
        Assert.Equal(0, decoder.FrameIndexAt(0));

        foreach (WebpFrameInfo frame in decoder.Frames)
        {
            Assert.Equal(frame.Index, decoder.FrameIndexAt(frame.TimestampMs));
            if (frame.DurationMs > 0)
            {
                Assert.Equal(frame.Index, decoder.FrameIndexAt(
                    (long)frame.TimestampMs + frame.DurationMs - 1));
            }

            Assert.Equal(
                Math.Min(frame.Index + 1, decoder.FrameCount - 1),
                decoder.FrameIndexAt((long)frame.TimestampMs + frame.DurationMs));
        }

        Assert.Equal(decoder.FrameCount - 1, decoder.FrameIndexAt(long.MaxValue));
    }

    [Theory]
    [MemberData(nameof(AllAnimationFixtureCases))]
    public void StillDecodeAndFacadeReturnCompositedFrameZero(string fixture)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixture));
        WebpAnimation animation = WebpDecoder.DecodeAnimation(source);
        byte[] expected = animation.Frames[0].Rgba;

        WebpImage decoded = WebpDecoder.Decode(source);
        Assert.Equal(expected, decoded.Rgba);
        byte[] destination = new byte[expected.Length];
        WebpDecoder.DecodeTo(source, destination);
        Assert.Equal(expected, destination);

        Assert.True(WebpImageDecoder.TryReadInfo(source, out ImageInfo info));
        Assert.Equal(decoded.HasAlpha ? 4 : 3, info.Channels);
        Assert.True(WebpImageDecoder.TryDecode(source, out IDecodedImage? facadeImage));
        Assert.NotNull(facadeImage);
        Assert.Equal(decoded.HasAlpha ? 4 : 3, facadeImage.Channels);
        Assert.Equal(expected, facadeImage.ToRgba8());
        byte[] facadeRgba = new byte[expected.Length];
        Assert.True(WebpImageDecoder.TryDecodeIntoRgba8(source, facadeRgba));
        Assert.Equal(expected, facadeRgba);
    }

    [Fact]
    public void StillImageIsExposedAsOneAnimationFrame()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), "tiny.webp"));
        WebpImage image = WebpDecoder.Decode(source);
        WebpAnimation animation = WebpDecoder.DecodeAnimation(source);

        WebpFrame frame = Assert.Single(animation.Frames);
        Assert.Equal(image.Rgba, frame.Rgba);
        Assert.Equal(0, frame.TimestampMs);
        Assert.Equal(0, frame.DurationMs);
    }

    [Fact]
    public void WebpmuxFixtureReportsFrameFlagsBackgroundAndLoopCount()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(
            FixtureDirectory(),
            "anim-webpmux-combinations.webp"));
        var decoder = new WebpAnimationDecoder(source);

        Assert.Equal(3, decoder.LoopCount);
        Assert.Equal(0xC8D228FFu, decoder.BackgroundColor);
        Assert.Equal(5, decoder.FrameCount);
        Assert.Equal(decoder.FrameCount, WebpDecoder.ReadInfo(source).FrameCount);
        Assert.Equal(new[] { 0, 2, 4, 6, 8 }, decoder.Frames.Select(static frame => frame.X));
        Assert.Equal(new[] { 0, 2, 2, 4, 4 }, decoder.Frames.Select(static frame => frame.Y));
        Assert.Equal(new[] { true, false, true, false, true },
            decoder.Frames.Select(static frame => frame.Blend));
        Assert.Equal(new[] { false, false, true, true, false },
            decoder.Frames.Select(static frame => frame.DisposeToBackground));
        Assert.True(decoder.Frames[0].IsIndependent);
        Assert.All(decoder.Frames.Skip(1), static frame => Assert.False(frame.IsIndependent));

        WebpAnimation animation = WebpDecoder.DecodeAnimation(source);
        Assert.Equal(decoder.LoopCount, animation.LoopCount);
        Assert.Equal(decoder.BackgroundColor, animation.BackgroundColor);
    }

    [Fact]
    public void Vp8lAlphaHintDoesNotMakeBlendedFullCanvasFrameIndependent()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(
            FixtureDirectory(),
            "anim-vp8l-alpha-hint.webp"));
        WebpContainer container = WebpContainer.Read(source);
        var decoder = new WebpAnimationDecoder(source);

        Assert.Equal(WebpFormat.Lossless, container.Frames[1].Format);
        Assert.False(container.Frames[1].HasAlpha);
        Assert.True(container.Frames[1].Blend);
        Assert.False(decoder.Frames[1].IsIndependent);

        WebpFrameData frame = container.Frames[1];
        byte[] sourceRgba = WebpDecoder.DecodeFrameRgba(
            source.AsSpan(frame.BitstreamOffset, frame.BitstreamLength),
            frame.Format,
            ReadOnlySpan<byte>.Empty,
            frame.Width,
            frame.Height);
        Assert.Equal((byte)0, sourceRgba[3]);
        Assert.Contains(
            Enumerable.Range(0, sourceRgba.Length)
                .Where(static offset => (offset & 3) == 3)
                .Select(offset => sourceRgba[offset]),
            static alpha => alpha < 255);

        WebpAnimation animation = WebpDecoder.DecodeAnimation(source);
        Assert.Equal(
            animation.Frames[0].Rgba.AsSpan(0, 4).ToArray(),
            animation.Frames[1].Rgba.AsSpan(0, 4).ToArray());
    }

    [Theory]
    [MemberData(nameof(AllAnimationFixtureCases))]
    public void DecoderValidatesDestinationAndSeekBounds(string fixture)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(FixtureDirectory(), fixture));
        var decoder = new WebpAnimationDecoder(source);

        Assert.Throws<ArgumentException>(() => decoder.TryDecodeNextFrame(
            new byte[decoder.CanvasWidth * decoder.CanvasHeight * 4 - 1],
            out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Seek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Seek(decoder.FrameCount));
    }

    private static byte[][] RunAnimDump(string animDump, string input, int width, int height)
    {
        string outputDirectory = Directory.CreateTempSubdirectory("anim_dump_").FullName;
        try
        {
            var startInfo = new ProcessStartInfo(animDump)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-pam");
            startInfo.ArgumentList.Add("-folder");
            startInfo.ArgumentList.Add(outputDirectory);
            startInfo.ArgumentList.Add("-prefix");
            startInfo.ArgumentList.Add("frame_");
            startInfo.ArgumentList.Add(input);
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Failed to start anim_dump.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"anim_dump failed: {stdout}{stderr}");
            }

            return Directory.EnumerateFiles(outputDirectory, "*.pam")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .Select(path => ReadPamPixels(File.ReadAllBytes(path), width, height))
                .ToArray();
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static byte[] ReadPamPixels(byte[] pam, int expectedWidth, int expectedHeight)
    {
        int headerEnd = pam.AsSpan().IndexOf("ENDHDR\n"u8);
        if (headerEnd < 0)
        {
            throw new InvalidDataException("anim_dump produced an invalid PAM header.");
        }

        string[] lines = Encoding.ASCII.GetString(pam, 0, headerEnd).Split('\n');
        int width = 0;
        int height = 0;
        int depth = 0;
        int maxValue = 0;
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                switch (parts[0])
                {
                    case "WIDTH": width = int.Parse(parts[1]); break;
                    case "HEIGHT": height = int.Parse(parts[1]); break;
                    case "DEPTH": depth = int.Parse(parts[1]); break;
                    case "MAXVAL": maxValue = int.Parse(parts[1]); break;
                }
            }
        }

        int pixelOffset = headerEnd + "ENDHDR\n".Length;
        int pixelLength = checked(width * height * 4);
        if (width != expectedWidth || height != expectedHeight || depth != 4 || maxValue != 255 ||
            pam.Length - pixelOffset != pixelLength)
        {
            throw new InvalidDataException("anim_dump PAM dimensions or RGBA payload are invalid.");
        }

        return pam.AsSpan(pixelOffset, pixelLength).ToArray();
    }

    private static string DescribeMismatch(string fixture, int frame, byte[] expected, byte[] actual, int width)
    {
        int commonLength = Math.Min(expected.Length, actual.Length);
        int mismatchCount = Math.Abs(expected.Length - actual.Length);
        int maximumDifference = 0;
        int first = -1;
        for (int index = 0; index < commonLength; index++)
        {
            int difference = Math.Abs(actual[index] - expected[index]);
            if (difference != 0)
            {
                first = first < 0 ? index : first;
                mismatchCount++;
                maximumDifference = Math.Max(maximumDifference, difference);
            }
        }

        int pixel = Math.Max(first, 0) / 4;
        return $"{fixture} frame {frame}: {mismatchCount} channel mismatches, " +
            $"max |diff| {maximumDifference}; first pixel ({pixel % width},{pixel / width}) " +
            $"channel {Math.Max(first, 0) % 4}, expected {(first >= 0 ? expected[first] : -1)}, " +
            $"actual {(first >= 0 ? actual[first] : -1)}.";
    }

    private static IEnumerable<string> FixtureNames() => FixtureNames(FixtureDirectory());

    private static IEnumerable<string> OracleFixtureNames() =>
        OracleFixtureNames(FixtureDirectory());

    private static IEnumerable<string> FixtureNames(string directory) =>
        Directory.EnumerateFiles(directory, "anim-*.webp")
            .Select(static path => Path.GetFileName(path)!)
            .OrderBy(static name => name, StringComparer.Ordinal);

    private static IEnumerable<string> OracleFixtureNames(string directory) =>
        FixtureNames(directory)
            // anim_dump 1.6.0 emits this RFC-blended frame as uncomposited.
            .Where(static name => name != "anim-vp8l-alpha-hint.webp");

    private static Dictionary<(string Fixture, int Frame), GoldenEntry> LoadGolden()
    {
        string path = Path.Combine(FixtureDirectory(), GoldenFileName);
        var map = new Dictionary<(string Fixture, int Frame), GoldenEntry>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length != 7 ||
                !map.TryAdd(
                    (parts[0], int.Parse(parts[1])),
                    new GoldenEntry(
                        int.Parse(parts[2]),
                        int.Parse(parts[3]),
                        int.Parse(parts[4]),
                        int.Parse(parts[5]),
                        parts[6])))
            {
                throw new InvalidDataException($"Invalid or duplicate WebP animation golden row: {line}");
            }
        }

        return map;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FixtureDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");

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
}
