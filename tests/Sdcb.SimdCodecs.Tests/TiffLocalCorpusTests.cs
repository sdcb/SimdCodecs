using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A local corpus of real-world and fuzzer-found TIFFs, run only when <c>SIMDCODECS_TESTIMAGES_DIR</c> names a
/// directory (searched recursively for <c>*.tif</c>, <c>*.tiff</c> and, under any <c>fuzz</c> directory, every file).
/// <para>
/// <b>The files are not in the repository and must not be:</b> their source and licence are unknown, and AGENTS.md
/// admits no third-party data of uncertain provenance. Without the variable the test skips (REQUIRE_ORACLES does not
/// change that: this is an optional corpus, not an oracle).
/// </para>
/// <para>
/// The contract checked is the untrusted-input one: every file either decodes or fails with
/// <see cref="InvalidDataException"/> / <see cref="NotSupportedException"/> from <see cref="TiffReader"/>, and with
/// <c>false</c> from the facade adapter — never another exception. A one-line outcome per file is written to the test
/// output and to <c>tiff-corpus-report.txt</c> in the temp directory.
/// </para>
/// </summary>
public sealed class TiffLocalCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryFile_DecodesOrFailsCleanly()
    {
        var directory = Environment.GetEnvironmentVariable("SIMDCODECS_TESTIMAGES_DIR");
        if (string.IsNullOrEmpty(directory))
            Assert.Skip("SIMDCODECS_TESTIMAGES_DIR is not set; see TiffLocalCorpusTests.");
        if (!Directory.Exists(directory))
            Assert.Fail($"SIMDCODECS_TESTIMAGES_DIR is '{directory}', which is not a directory.");

        var ct = TestContext.Current.CancellationToken;
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(f)!.Split(Path.DirectorySeparatorChar).Contains("fuzz", StringComparer.OrdinalIgnoreCase)
                    && Path.GetFileName(f).Contains("TIFF", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();
        files.ShouldNotBeEmpty();

        var lines = new List<string>();
        var failures = new List<string>();
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetRelativePath(directory, path);
            var data = File.ReadAllBytes(path);

            string reader;
            try
            {
                var document = TiffReader.Read(data);
                reader = $"read {document.Pages.Count} page(s)";
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                reader = $"{e.GetType().Name}: {e.Message}";
            }
            catch (Exception e)
            {
                reader = $"ESCAPED {e.GetType().Name}: {e.Message}";
                failures.Add($"{name}: TiffReader.Read let {e.GetType().Name} escape: {e.Message}");
            }

            string facade;
            try
            {
                facade = TiffImageDecoder.TryDecode(data, out var image, cancellationToken: ct)
                    ? $"facade {image!.Width}x{image.Height}x{image.Channels} {image.SampleFormat}{AgainstMagick(data, image)}"
                    : "facade false";
                image?.Dispose();
            }
            catch (Exception e)
            {
                facade = $"facade ESCAPED {e.GetType().Name}: {e.Message}";
                failures.Add($"{name}: TiffImageDecoder.TryDecode let {e.GetType().Name} escape: {e.Message}");
            }

            var line = $"{name}\t{reader}\t{facade}";
            lines.Add(line);
            output.WriteLine(line);
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "tiff-corpus-report.txt"), lines);
        failures.ShouldBeEmpty();
    }

    /// <summary>Informational: the max difference from libtiff's decode (via ImageMagick) for 8-bit colour output.</summary>
    private static string AgainstMagick(byte[] data, IDecodedImage image)
    {
        if (image.SampleFormat != SampleFormat.UInt8 || image.Channels < 3) return "";
        try
        {
            using var magick = new ImageMagick.MagickImage(data);
            if (magick.ColorSpace is ImageMagick.ColorSpace.CMYK or ImageMagick.ColorSpace.YCbCr)
                magick.ColorSpace = ImageMagick.ColorSpace.sRGB;
            using var pixels = magick.GetPixels();
            var theirs = pixels.ToByteArray(image.Channels == 4 ? ImageMagick.PixelMapping.RGBA : ImageMagick.PixelMapping.RGB)!;
            var ours = image.Pixels;
            if (theirs.Length != ours.Length) return " (magick: size differs)";
            var max = 0;
            long sum = 0;
            for (var i = 0; i < ours.Length; i++)
            {
                var d = Math.Abs(ours[i] - theirs[i]);
                max = Math.Max(max, d);
                sum += d;
            }
            return $" (vs magick: max {max}, mean {(double)sum / ours.Length:F3})";
        }
        catch (Exception e)
        {
            return $" (magick: {e.GetType().Name})";
        }
    }
}
