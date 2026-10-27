using System.Numerics;
using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Rung 1 end to end: every committed fixture must decode to the source raster
/// beside it, sample for sample, with <b>no tolerance at all</b>.
/// <para>
/// The exactness is the point and is not an accident of small test images. The
/// reversible 5/3 wavelet is integer lifting and the codestreams are lossless,
/// so the right answer is the encoder's own input — there is nothing here to
/// round. That makes this the sharpest assertion available on this format, and
/// the roadmap's warning applies: when the 9/7 path arrives it needs a
/// tolerance, and that tolerance must not be allowed to leak back over these
/// cases. Keep the two suites apart.
/// </para>
/// </summary>
public class Jpeg2000DecoderTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    /// <summary>
    /// The whole corpus, exact. If this passes, tier-2, tier-1, the DWT and the
    /// level shift are all right together — which is the only way any of them
    /// can be shown to be right at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Jpeg2000FixtureTests.Codestreams), MemberType = typeof(Jpeg2000FixtureTests))]
    public void EveryFixture_DecodesExactly(string name)
    {
        var expected = Pnm.Read(Jpeg2000FixtureTests.SourceRaster(name));
        var decoded = Jpeg2000Decoder.Decode(File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k")));

        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);
        decoded.Components.ShouldBe(expected.Components);
        decoded.BitDepth.ShouldBe(BitOperations.Log2((uint)expected.MaxValue) + 1);
        decoded.Samples.ShouldBe(expected.Samples);
    }

    /// <summary>
    /// The magic-byte sniff the facade will use, kept honest in both directions.
    /// </summary>
    [Fact]
    public void IsCodestream_RecognisesSocSizAndNothingElse()
    {
        Jpeg2000Decoder.IsCodestream(File.ReadAllBytes(Path.Combine(FixtureDirectory, "flat64.j2k")))
            .ShouldBeTrue();

        Jpeg2000Decoder.IsCodestream([0xFF, 0xD8, 0xFF, 0xE0]).ShouldBeFalse();   // JPEG
        Jpeg2000Decoder.IsCodestream([0xFF, 0x4F]).ShouldBeFalse();               // SOC alone is too short
        Jpeg2000Decoder.IsCodestream([]).ShouldBeFalse();
    }

    /// <summary>
    /// A flat image is the case a decoder written only against busy pictures
    /// gets wrong: no code-block is ever included, so most packets are the
    /// single zero bit of B.10.3 and tier-1 never runs.
    /// </summary>
    [Fact]
    public void FlatImage_DecodesToTheConstantItWas()
    {
        var decoded = Jpeg2000Decoder.Decode(File.ReadAllBytes(Path.Combine(FixtureDirectory, "flat64.j2k")));

        decoded.Samples.ShouldAllBe(s => s == 128);
    }

    /// <summary>
    /// Truncating the coded data must raise <see cref="InvalidDataException"/>,
    /// never an index-out-of-range or an infinite loop.
    /// <para>
    /// This is hazard 6's real shape. The MQ decoder does not stop at the end of
    /// its data — T.800 C.3.4, like T.88 E.3.4, has it read every byte past the
    /// end as <c>0xFF</c> for ever — so "ran out of input" is not a backstop
    /// anywhere in this decoder, and the ceilings have to do the work instead.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.9)]
    [InlineData(0.5)]
    [InlineData(0.1)]
    public void TruncatedCodedData_FailsCleanly(double keepFraction)
    {
        var full = File.ReadAllBytes(Path.Combine(FixtureDirectory, "noise64.j2k"));
        var truncated = full[..(int)(full.Length * keepFraction)];

        // Either it decodes something (the MQ coder is happy to invent bits) or
        // it reports malformed input. What it must never do is throw something
        // from outside that contract.
        try
        {
            Jpeg2000Decoder.Decode(truncated);
        }
        catch (InvalidDataException)
        {
            // The expected way to fail.
        }
    }
}
