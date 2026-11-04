using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The JPEG 2000 files a user attached to issue #1 (https://github.com/sdcb/SimdCodecs/issues/1),
/// decoded and compared with <c>opj_decompress</c>: reversible images exactly, irreversible ones to
/// within one, as everywhere else.
/// <para>
/// <b>The files are not in the repository and must not be added.</b> They came from a test set
/// whose source and licence nobody could name, so under the clean-room rules in <c>AGENTS.md</c>
/// they may be run locally but neither committed nor fetched by a test. This test therefore skips
/// unless <c>SIMDCODECS_ISSUE1_DIR</c> names a directory holding them, and that skip is not an
/// oracle skip: <c>REQUIRE_ORACLES</c> does not turn it into a failure.
/// </para>
/// <para>
/// <b>After changing the JPEG 2000 decoder, run it.</b> Download both attachments of issue #1,
/// <c>https://github.com/user-attachments/files/34160416/JPX_JBig2.zip</c> and
/// <c>https://github.com/user-attachments/files/34161061/JPXDecode.zip</c>, unzip them into one
/// directory (the <c>.jb2</c> files beside them are the JBIG2 half of the same issue), set
/// <c>SIMDCODECS_ISSUE1_DIR</c> to it and run this class. Its sixteen files cover multiple tiles
/// and tile-parts, RLCP and RPCL progressions, precincts, SOP and EPH markers, segmentation symbols
/// and JP2 palettes, as real encoders write them; the committed tests that cover the same ground
/// are in <see cref="Jpeg2000TilingAndProgressionTests"/>.
/// </para>
/// </summary>
public class Issue1Jpeg2000CorpusTests
{
    /// <summary>The environment variable naming the unzipped issue #1 attachments.</summary>
    internal const string DirectoryVariable = "SIMDCODECS_ISSUE1_DIR";

    [Fact]
    public void EveryCorpusFile_DecodesAsOpenJpegDoes()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
            Assert.Skip($"{DirectoryVariable} is not set; see this class's remarks for where issue #1's files are.");

        OpenJpegOracle.RequireOrSkip();

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".j2k" or ".j2c" or ".jp2" or ".jpx")
            .Order(StringComparer.Ordinal)
            .ToList();
        files.ShouldNotBeEmpty($"{DirectoryVariable} names {directory}, which holds no JPEG 2000 files.");

        var failures = new List<string>();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            try
            {
                var bytes = File.ReadAllBytes(path);
                var decoded = Jpeg2000Decoder.Decode(bytes, TestContext.Current.CancellationToken);
                var expected = OpenJpegOracle.DecodeFile(
                    bytes, Path.GetExtension(path), decoded.Components == 1 ? ".pgm" : ".ppm");

                if (decoded.Width != expected.Width || decoded.Height != expected.Height ||
                    decoded.Components != expected.Components)
                {
                    failures.Add($"{name}: {decoded.Width}x{decoded.Height}x{decoded.Components}, OpenJPEG " +
                                 $"{expected.Width}x{expected.Height}x{expected.Components}");
                    continue;
                }

                // A reversible codestream must match exactly; one that uses the 9/7 wavelet anywhere
                // may differ by the rounding of its last step.
                var tolerance = UsesIrreversibleWavelet(bytes) ? 1 : 0;
                var worst = 0;
                for (var i = 0; i < decoded.Samples.Length; i++)
                    worst = Math.Max(worst, Math.Abs(decoded.Samples[i] - expected.Samples[i]));
                if (worst > tolerance)
                    failures.Add($"{name}: differs from OpenJPEG by up to {worst} (allowed {tolerance})");
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
            }
        }

        failures.ShouldBeEmpty();
    }

    /// <summary>Whether any COD or COC in the main header picks the 9/7 wavelet.</summary>
    private static bool UsesIrreversibleWavelet(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (Jpeg2000Decoder.IsJp2(data))
        {
            var layout = Jp2Reader.Read(data);
            data = data.Slice(layout.CodestreamStart, layout.CodestreamLength);
        }

        var header = CodestreamReader.Read(data);
        return header.ComponentCoding.Any(c => c.Transform == WaveletTransform.Irreversible97);
    }
}
