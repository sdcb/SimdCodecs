using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Rung 5: decoding at reduced resolution, <see cref="Jpeg2000Decoder.Decode(ReadOnlySpan{byte}, int)"/>.
/// <para>
/// What it must do is <c>opj_decompress -r</c>, so that is the oracle: exact on the reversible path,
/// within the 9/7 tolerance on the irreversible one. What it must NOT do is the other half of the
/// point, and needs a test of its own because no output can show it: the levels left out must never
/// be entropy-decoded, or a reduced decode is a full decode with a smaller answer. That is checked by
/// corrupting exactly those levels' coded bytes and decoding again.
/// </para>
/// </summary>
public class Jpeg2000ReducedResolutionTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    private static string LossyDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-lossy");

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k"));

    /// <summary>Lossless fixtures reduced by 1 to 3 decode to exactly what OpenJPEG decodes.</summary>
    [Theory]
    [InlineData("dwt5-struct64", 1)]
    [InlineData("dwt5-struct64", 3)]
    [InlineData("cblk4-struct64", 2)]
    [InlineData("odd37x23", 1)]
    [InlineData("rgb-struct64", 2)]
    [InlineData("rgb-odd37x23", 1)]
    [InlineData("rgb-layers2-struct64", 1)]
    [InlineData("rgb-layers2-struct64", 3)]
    public void AReducedLosslessDecode_IsExactlyOpenJpegs(string name, int reduce)
    {
        OpenJpegOracle.RequireOrSkip();

        var bytes = Fixture(name);
        var decoded = Jpeg2000Decoder.Decode(bytes, reduce);
        var expected = OpenJpegOracle.Decode(bytes, decoded.Components == 1 ? ".pgm" : ".ppm", "-r", reduce.ToString());

        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);
        decoded.Samples.ShouldBe(expected.Samples);
    }

    /// <summary>
    /// A reduced 9/7 decode is within the tolerance the full one is held to: every sample within 1,
    /// at most 1% differing. Measured: at most 0.4% differing on the fixtures, the real images and a
    /// 2048x1536 photo-like image, at reductions 1 to 3.
    /// </summary>
    [Theory]
    [InlineData("lossy97-rgb-struct64", 1)]
    [InlineData("lossy97-rgb-struct64", 2)]
    [InlineData("lossy97-rgb-layers3", 1)]
    [InlineData("lossy97-odd37x23", 1)]
    public void AReducedLossyDecode_IsWithinToleranceOfOpenJpegs(string name, int reduce)
    {
        OpenJpegOracle.RequireOrSkip();

        var bytes = File.ReadAllBytes(Path.Combine(LossyDirectory, name + ".j2k"));
        var decoded = Jpeg2000Decoder.Decode(bytes, reduce);
        var expected = OpenJpegOracle.Decode(bytes, decoded.Components == 1 ? ".pgm" : ".ppm", "-r", reduce.ToString());

        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);

        var differing = 0;
        for (var i = 0; i < expected.Samples.Length; i++)
        {
            var difference = Math.Abs(decoded.Samples[i] - expected.Samples[i]);
            difference.ShouldBeLessThanOrEqualTo(1);
            if (difference != 0) differing++;
        }

        ((double)differing / expected.Samples.Length).ShouldBeLessThanOrEqualTo(0.01);
    }

    /// <summary>Each level left out halves each side, rounding up, as T.800 Equation B-14 has it.</summary>
    [Theory]
    [InlineData("dwt5-struct64", 0, 64, 64)]
    [InlineData("dwt5-struct64", 1, 32, 32)]
    [InlineData("dwt5-struct64", 5, 2, 2)]
    [InlineData("odd37x23", 1, 19, 12)]
    [InlineData("odd37x23", 2, 10, 6)]
    public void EachLevelLeftOut_HalvesEachSide(string name, int reduce, int width, int height)
    {
        var decoded = Jpeg2000Decoder.Decode(Fixture(name), reduce);

        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
    }

    /// <summary>
    /// Asking for more levels than a codestream has gives the smallest image it has, rather than an
    /// error: a thumbnail asking for a small image should get the smallest there is.
    /// </summary>
    [Fact]
    public void AReductionPastTheLevelsThereAre_IsClamped()
    {
        // No decomposition at all: there is only the full image.
        var flat = Jpeg2000Decoder.Decode(Fixture("nodwt-struct32"), 4);
        flat.Width.ShouldBe(32);
        flat.Samples.ShouldBe(Jpeg2000Decoder.Decode(Fixture("nodwt-struct32")).Samples);

        // Two levels: a reduction of 9 is a reduction of 2.
        Jpeg2000Decoder.Decode(Fixture("odd37x23"), 9).Samples
            .ShouldBe(Jpeg2000Decoder.Decode(Fixture("odd37x23"), 2).Samples);
    }

    [Fact]
    public void ANegativeReduction_IsAnArgumentError()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Jpeg2000Decoder.Decode(Fixture("flat64"), -1));
    }

    /// <summary>
    /// The levels left out are never entropy-decoded. Every byte of their code-block bodies is
    /// overwritten, which a full decode turns into a different picture; a decode reduced past them
    /// must not notice. The packet HEADERS are left alone, because tier-2 has to read those to find
    /// where the kept levels' data is.
    /// </summary>
    [Theory]
    [InlineData("dwt5-struct64")]
    [InlineData("rgb-layers2-struct64")]
    public void TheLevelsLeftOut_AreNeverDecoded(string name)
    {
        var bytes = Fixture(name);
        var corrupted = (byte[])bytes.Clone();

        // Where the finest level's code-block bodies are, from tier-2 itself.
        var header = CodestreamReader.Read(bytes);
        var components = Enumerable.Range(0, header.Siz.Components.Length)
            .Select(c => TileComponent.Build(header, c, Jpeg2000SampleBudget.Unmetered(),
                header.ComponentCoding[c].ResolutionCount))
            .ToArray();
        var part = header.TileParts[0];
        Tier2.ReadPackets(header, components, bytes.AsSpan(part.Start, part.Length));

        var overwritten = 0;
        foreach (var component in components)
        {
            foreach (var band in component.Resolutions[^1].Bands)
            {
                foreach (var block in band.Blocks)
                {
                    foreach (var (start, length) in block.Segments)
                    {
                        // 0x5A everywhere: never 0xFF, so no false marker is introduced into the body.
                        corrupted.AsSpan(part.Start + start, length).Fill(0x5A);
                        overwritten += length;
                    }
                }
            }
        }

        overwritten.ShouldBeGreaterThan(0, "the finest level carried no coded data, so this proves nothing");
        Jpeg2000Decoder.Decode(corrupted).Samples.ShouldNotBe(Jpeg2000Decoder.Decode(bytes).Samples);

        Jpeg2000Decoder.Decode(corrupted, 1).Samples.ShouldBe(Jpeg2000Decoder.Decode(bytes, 1).Samples);
    }

    /// <summary>
    /// <see cref="Jpeg2000Decoder.ReadInfo"/> reads what a decode will produce without running one:
    /// the size, components and depth, the levels a reduction can leave out, and the JP2 colour, and
    /// <see cref="Jpeg2000Info.SizeAt"/> is the size a reduced decode really comes out at, clamping
    /// included.
    /// </summary>
    [Theory]
    [InlineData("odd37x23.j2k", 37, 23, 1, 2)]
    [InlineData("rgb-struct64.j2k", 64, 64, 3, 5)]
    [InlineData("nodwt-struct32.j2k", 32, 32, 1, 0)]
    [InlineData("jp2-rgb-struct64.jp2", 64, 64, 3, 5)]
    public void ReadInfo_ReadsWhatADecodeWillProduce(string file, int width, int height, int components, int levels)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, file));

        var info = Jpeg2000Decoder.ReadInfo(bytes);

        info.Width.ShouldBe(width);
        info.Height.ShouldBe(height);
        info.Components.ShouldBe(components);
        info.BitDepth.ShouldBe(8);
        info.DecompositionLevels.ShouldBe(levels);
        (info.Colour is not null).ShouldBe(file.EndsWith(".jp2", StringComparison.Ordinal));

        for (var reduce = 0; reduce <= levels + 1; reduce++)
        {
            var decoded = Jpeg2000Decoder.Decode(bytes, reduce);
            info.SizeAt(reduce).ShouldBe((decoded.Width, decoded.Height), $"reduce {reduce}");
        }
    }

    /// <summary>
    /// <see cref="Jpeg2000Decoder.ReductionFor"/> takes the largest reduction whose long edge still
    /// reaches the size asked for, halving with rounding up as the decode does.
    /// </summary>
    [Theory]
    [InlineData(2048, 1536, 256, 3)]   // 2048 -> 1024 -> 512 -> 256
    [InlineData(2048, 1536, 257, 2)]   // 256 would be short of 257
    [InlineData(1536, 2048, 256, 3)]   // the long edge, whichever way round
    [InlineData(395, 219, 100, 1)]     // 395 -> 198 -> 99, which is short
    [InlineData(64, 64, 64, 0)]        // already the size asked for
    [InlineData(64, 64, 1000, 0)]      // smaller than asked: no reduction, never a negative one
    [InlineData(1, 1, 1, 0)]
    public void ReductionFor_IsTheLargestThatStillReachesTheSize(int width, int height, int minLongEdge, int expected)
    {
        Jpeg2000Decoder.ReductionFor(width, height, minLongEdge).ShouldBe(expected);
    }
}
