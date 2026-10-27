using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The marker layer (T.800 Annex A) against the committed fixtures: what the
/// header says must match what <c>opj_compress</c> was told to produce.
/// <para>
/// This is the first rung-1 step and the only one that can be checked in
/// isolation. Everything after it — tier-2, tier-1, the DWT — produces nothing
/// interpretable until the whole core closes, which is why the roadmap says to
/// lean hard on component tests here.
/// </para>
/// </summary>
public class Jpeg2000CodestreamTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k"));

    /// <summary>
    /// SIZ's geometry must agree with the source raster beside it. The fixtures
    /// are the cross-check: their dimensions are in the PGM header, written by a
    /// different program from the one that wrote the codestream.
    /// </summary>
    [Theory]
    [InlineData("dwt5-struct64", 64, 64)]
    [InlineData("nodwt-struct32", 32, 32)]
    [InlineData("odd37x23", 37, 23)]
    [InlineData("odd1x1", 1, 1)]
    [InlineData("odd5x64", 5, 64)]
    [InlineData("odd64x5", 64, 5)]
    public void Siz_MatchesTheSourceRaster(string name, int width, int height)
    {
        var header = CodestreamReader.Read(Fixture(name));
        var source = Pnm.Read(Path.Combine(FixtureDirectory, name + ".pgm"));

        header.Siz.Width.ShouldBe(width);
        header.Siz.Height.ShouldBe(height);
        header.Siz.Width.ShouldBe(source.Width);
        header.Siz.Height.ShouldBe(source.Height);

        // opj_compress puts the image at the grid origin and makes one tile of
        // the whole thing unless told otherwise.
        header.Siz.X0.ShouldBe(0);
        header.Siz.Y0.ShouldBe(0);
        header.Siz.TileCount.ShouldBe(1);

        header.Siz.Components.Length.ShouldBe(1);
        header.Siz.Components[0].BitDepth.ShouldBe(8);
        header.Siz.Components[0].IsSigned.ShouldBeFalse();
        header.Siz.Components[0].HorizontalSeparation.ShouldBe(1);
        header.Siz.Components[0].VerticalSeparation.ShouldBe(1);
    }

    /// <summary>
    /// COD's decomposition levels must be the <c>-n</c> the fixture was built
    /// with, minus one: <c>-n</c> counts <em>resolutions</em> and SPcod counts
    /// <em>decomposition levels</em>. Getting that off by one puts every subband
    /// at the wrong size, so it is worth pinning against fixtures whose intended
    /// level count is written down in make-fixtures.sh.
    /// </summary>
    [Theory]
    [InlineData("nodwt-struct32", 0)]
    [InlineData("nodwt-noise32", 0)]
    [InlineData("dwt1-struct32", 1)]
    [InlineData("odd5x64", 1)]
    [InlineData("odd37x23", 2)]
    [InlineData("dwt5-struct64", 5)]
    [InlineData("noise64", 5)]
    public void Cod_ReportsTheDecompositionLevelsTheFixtureWasBuiltWith(string name, int levels)
    {
        var header = CodestreamReader.Read(Fixture(name));

        header.Cod.DecompositionLevels.ShouldBe(levels);
        header.Cod.ResolutionCount.ShouldBe(levels + 1);
    }

    /// <summary>
    /// The code-block exponent is stored biased by two — SPcod's <c>4</c> means
    /// 2^6 = 64 — and the fixtures pin all three sizes the corpus uses.
    /// </summary>
    [Theory]
    [InlineData("dwt5-struct64", 6, 6)]
    [InlineData("cblk16-noise64", 4, 4)]
    [InlineData("cblk4-struct64", 2, 2)]
    public void Cod_UnbiasesTheCodeBlockExponents(string name, int widthExponent, int heightExponent)
    {
        var header = CodestreamReader.Read(Fixture(name));

        header.Cod.CodeBlockWidthExponent.ShouldBe(widthExponent);
        header.Cod.CodeBlockHeightExponent.ShouldBe(heightExponent);
    }

    /// <summary>
    /// Every fixture is inside the exact envelope, by construction and now by assertion, and is
    /// what its name says: <c>rgb*</c> has three components with RCT unless it says <c>nomct</c>,
    /// <c>layers*</c> and <c>*-layers*</c> have several quality layers, and everything else is
    /// rung 1's single component in a single layer. Tying the name to the header keeps a
    /// regeneration from quietly turning a feature's only fixture into a plain one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Jpeg2000FixtureTests.Codestreams), MemberType = typeof(Jpeg2000FixtureTests))]
    public void EveryFixture_IsWhatItsNameSays(string name)
    {
        var header = CodestreamReader.Read(Fixture(name));

        header.Progression.ShouldBe(ProgressionOrder.Lrcp);
        header.UseSopMarkers.ShouldBeFalse();
        header.UseEphMarkers.ShouldBeFalse();
        header.TileParts.Count.ShouldBe(1);
        foreach (var coding in header.ComponentCoding)
        {
            coding.Transform.ShouldBe(WaveletTransform.Reversible53);
            coding.CodeBlockStyle.ShouldBe(0);
            coding.PrecinctSizes.ShouldBeEmpty();
        }

        var rgb = name.StartsWith("rgb", StringComparison.Ordinal);
        header.Siz.Components.Length.ShouldBe(rgb ? 3 : 1);
        header.MultipleComponentTransform.ShouldBe(rgb && !name.Contains("nomct", StringComparison.Ordinal));

        var layered = name.Contains("layers", StringComparison.Ordinal);
        if (layered) header.Layers.ShouldBeGreaterThan(1);
        else header.Layers.ShouldBe(1);
    }

    /// <summary>
    /// The reversible path carries no quantization, but it does carry guard bits
    /// and per-subband exponents, and those fix Mb — how many magnitude
    /// bit-planes tier-1 codes (T.800 Equation E-2). So they are load-bearing
    /// even where nothing is quantized, and a decoder that skipped QCD because
    /// "reversible means no quantization" would start tier-1 on the wrong plane.
    /// </summary>
    [Fact]
    public void Qcd_CarriesGuardBitsAndOneExponentPerSubband()
    {
        var header = CodestreamReader.Read(Fixture("dwt5-struct64"));

        header.Qcd.Style.ShouldBe(QuantizationStyle.None);
        header.Qcd.GuardBits.ShouldBe(2);

        // Five decomposition levels: one LL band plus three per level.
        header.Qcd.Exponents.Length.ShouldBe(3 * 5 + 1);
        header.Qcd.Mantissas.ShouldAllBe(m => m == 0);
    }

    /// <summary>With no decomposition, QCD carries exactly one subband.</summary>
    [Fact]
    public void Qcd_HasOneSubbandWhenThereIsNoDecomposition()
    {
        var header = CodestreamReader.Read(Fixture("nodwt-struct32"));

        header.Qcd.Exponents.Length.ShouldBe(1);
    }

    /// <summary>The tile-part's coded data must be inside the codestream and non-empty.</summary>
    [Theory]
    [MemberData(nameof(Jpeg2000FixtureTests.Codestreams), MemberType = typeof(Jpeg2000FixtureTests))]
    public void TilePart_PointsAtRealData(string name)
    {
        var bytes = Fixture(name);
        var header = CodestreamReader.Read(bytes);

        var part = header.TileParts[0];
        part.TileIndex.ShouldBe(0);
        part.PartIndex.ShouldBe(0);
        part.Start.ShouldBeGreaterThan(0);
        part.Length.ShouldBeGreaterThan(0);
        (part.Start + part.Length).ShouldBeLessThanOrEqualTo(bytes.Length);
    }

    /// <summary>Bytes that are not a codestream are rejected, not misread.</summary>
    [Fact]
    public void NonCodestreamBytes_AreRejected()
    {
        CodestreamReader.LooksLikeCodestream([0x89, (byte)'P', (byte)'N', (byte)'G']).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => CodestreamReader.Read([0x89, (byte)'P', (byte)'N', (byte)'G']));
    }

    /// <summary>
    /// A truncated codestream is malformed input, so it must surface as
    /// <see cref="InvalidDataException"/> — never as an index-out-of-range
    /// escaping from inside the parser.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(44)]
    public void TruncatedCodestream_RaisesInvalidData(int keep)
    {
        var truncated = Fixture("dwt5-struct64")[..keep];

        Should.Throw<InvalidDataException>(() => CodestreamReader.Read(truncated));
    }

    /// <summary>
    /// SIZ's extents are 32-bit fields read into <see cref="int"/>, so a
    /// codestream declaring <c>Xsiz = 0x80000000</c> arrives as a negative
    /// width. Every later bound is a <c>&gt;</c> test that a negative number
    /// sails straight through, so the sign is checked at the parse rather than
    /// trusted — hazard 6's "every number here is attacker-chosen", in its
    /// smallest form.
    /// </summary>
    [Fact]
    public void SizWithAnExtentPastIntMax_IsRejected()
    {
        var bytes = Fixture("dwt5-struct64");

        // Xsiz sits at offset 8: SOC(2) + SIZ marker(2) + Lsiz(2) + Rsiz(2).
        bytes[8] = 0x80;
        bytes[9] = 0x00;
        bytes[10] = 0x00;
        bytes[11] = 0x00;

        Should.Throw<InvalidDataException>(() => CodestreamReader.Read(bytes));
    }
}
