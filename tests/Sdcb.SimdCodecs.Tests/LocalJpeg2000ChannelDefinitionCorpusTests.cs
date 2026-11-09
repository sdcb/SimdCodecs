using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A local check against real-world JP2 / JPF files with a channel definition box, which cannot be
/// committed (their licences are unknown). Set <c>SIMDCODECS_TESTIMAGES_DIR</c> to a directory tree of
/// images; without it the test is skipped, and a skip is not a pass.
/// <para>
/// For every file with a <c>cdef</c> box that the facade presents and <c>opj_decompress</c> can write
/// as PNM / PAM at 8 bits, the facade's samples must equal the oracle's within 1 (exact on the 5/3
/// path, whose files all match exactly in practice). The per-file outcome goes to the test output.
/// </para>
/// </summary>
public class LocalJpeg2000ChannelDefinitionCorpusTests
{
    private const string DirectoryVariable = "SIMDCODECS_TESTIMAGES_DIR";

    [Fact]
    public void EveryFileWithAChannelDefinition_DecodesAsOpenJpegDoes()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
            Assert.Skip($"{DirectoryVariable} is not set.");
        if (!Directory.Exists(directory))
            Assert.Skip($"{DirectoryVariable} names '{directory}', which does not exist.");
        OpenJpegOracle.RequireOrSkip();

        var output = TestContext.Current.TestOutputHelper!;
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jp2" or ".jpf" or ".jpx")
            .Order(StringComparer.Ordinal)
            .ToList();

        int withCdef = 0, presented = 0, compared = 0, refused = 0;
        var mismatches = new List<string>();
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            if (!Jpeg2000Decoder.IsJp2(bytes) || bytes.AsSpan().IndexOf("cdef"u8) < 0) continue;

            Jpeg2000Info info;
            try { info = Jpeg2000Decoder.ReadInfo(bytes); }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                withCdef++;
                refused++;
                output.WriteLine($"unreadable   {Path.GetFileName(file)}: {e.Message}");
                continue;
            }

            if (info.Channels is null) continue;
            withCdef++;

            if (!ImageCodecs.TryDecode(bytes, out var image, cancellationToken: TestContext.Current.CancellationToken))
            {
                refused++;
                output.WriteLine($"refused      {Path.GetFileName(file)}: {info.Components} channels {string.Join(",", info.Channels.Select(c => c.Type))}, colour {info.Colour?.EnumeratedColourSpace}");
                continue;
            }

            presented++;
            using (image)
            {
                var premultiplied = info.Channels.Any(c => c.Type == Jp2ChannelType.PremultipliedOpacity);
                Pnm.Image oracle;
                try { oracle = OpenJpegOracle.DecodeFile(bytes, ".jp2", ".pnm"); }
                catch (Exception e) when (e is InvalidOperationException or InvalidDataException or FormatException)
                {
                    output.WriteLine($"presented    {Path.GetFileName(file)}: {image.Width}x{image.Height}x{image.Channels} (oracle could not write it)");
                    continue;
                }

                if (premultiplied || info.BitDepth != 8 || oracle.MaxValue != 255 || oracle.Components != image.Channels)
                {
                    output.WriteLine($"presented    {Path.GetFileName(file)}: {image.Width}x{image.Height}x{image.Channels} (not compared: depth {info.BitDepth}, premultiplied {premultiplied})");
                    continue;
                }

                compared++;
                var pixels = image.Pixels.ToArray();
                var worst = 0;
                for (var i = 0; i < pixels.Length; i++) worst = Math.Max(worst, Math.Abs(pixels[i] - oracle.Samples[i]));
                output.WriteLine($"compared     {Path.GetFileName(file)}: {image.Width}x{image.Height}x{image.Channels}, worst difference {worst}");
                if (worst > 1) mismatches.Add($"{file} (worst {worst})");
            }
        }

        output.WriteLine($"{withCdef} files with a channel definition: {presented} presented ({compared} compared with opj_decompress), {refused} refused or unreadable");
        withCdef.ShouldBeGreaterThan(0, "the directory holds no JP2 file with a channel definition box");
        mismatches.ShouldBeEmpty();
    }
}
