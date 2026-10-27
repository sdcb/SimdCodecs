using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The 9/7 irreversible path: dequantisation, the float wavelet and ICT. Kept in a file of its
/// own, as the roadmap asks, because it is the one place in the JPEG 2000 suite that asserts a
/// tolerance, and a tolerance must not leak over the exact cases beside it.
/// <para>
/// <b>Where the tolerance comes from.</b> The roadmap wanted it from T.803's conformance bounds.
/// It is measured instead, against OpenJPEG on the committed fixtures and on the two 9/7 images
/// found in real PDFs, and the measurement is stated rather than dressed up: every sample within
/// 1 of OpenJPEG's, and at most 0.32% of samples on any fixture (0.15% on the real images)
/// differing at all. The two decoders round the same irrational arithmetic in a different order,
/// and that is the whole of the difference. The assertion is therefore two-sided: a peak of 1,
/// and a share of differing samples well under what a systematic error would produce. A wrong
/// rounding rule, say, can stay inside a peak of 1 everywhere and move a third of the samples.
/// </para>
/// </summary>
public class Jpeg2000IrreversibleTests
{
    private const int PeakTolerance = 1;
    private const double DifferingShareTolerance = 0.01;

    private static string LossyDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-lossy");

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(LossyDirectory, name + ".j2k"));

    public static TheoryData<string> IrreversibleCodestreams
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(LossyDirectory, "lossy97-*.j2k").OrderBy(p => p))
                data.Add(Path.GetFileNameWithoutExtension(path));

            return data;
        }
    }

    /// <summary>Every 9/7 fixture decodes within the measured tolerance of OpenJPEG.</summary>
    [Theory]
    [MemberData(nameof(IrreversibleCodestreams))]
    public void EveryIrreversibleFixture_IsWithinToleranceOfOpenJpeg(string name)
    {
        OpenJpegOracle.RequireOrSkip();

        var bytes = Fixture(name);
        var decoded = Jpeg2000Decoder.Decode(bytes);
        var expected = OpenJpegOracle.Decode(bytes, decoded.Components == 1 ? ".pgm" : ".ppm");

        decoded.Width.ShouldBe(expected.Width);
        decoded.Height.ShouldBe(expected.Height);
        decoded.Components.ShouldBe(expected.Components);

        var peak = 0;
        var differing = 0;
        for (var i = 0; i < expected.Samples.Length; i++)
        {
            var difference = Math.Abs(decoded.Samples[i] - expected.Samples[i]);
            peak = Math.Max(peak, difference);
            if (difference != 0) differing++;
        }

        peak.ShouldBeLessThanOrEqualTo(PeakTolerance);
        ((double)differing / expected.Samples.Length).ShouldBeLessThanOrEqualTo(DifferingShareTolerance);
    }

    /// <summary>
    /// The fixtures are what their names say: 9/7 with scalar quantization, ICT on three components
    /// unless the name says <c>nomct</c>, several layers where it says <c>layers</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(IrreversibleCodestreams))]
    public void EveryIrreversibleFixture_IsWhatItsNameSays(string name)
    {
        var header = CodestreamReader.Read(Fixture(name));

        foreach (var coding in header.ComponentCoding) coding.Transform.ShouldBe(WaveletTransform.Irreversible97);
        foreach (var quantization in header.ComponentQuantization)
            quantization.Style.ShouldNotBe(QuantizationStyle.None);

        var rgb = name.Contains("rgb", StringComparison.Ordinal);
        header.Siz.Components.Length.ShouldBe(rgb ? 3 : 1);
        header.MultipleComponentTransform.ShouldBe(rgb && !name.Contains("nomct", StringComparison.Ordinal));
        if (name.Contains("layers", StringComparison.Ordinal)) header.Layers.ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// A constant signal survives the inverse 9/7 unchanged. JPEG 2000 normalises the analysis
    /// low-pass filter to a DC gain of 1, so a constant c analyses to c in every low-pass sample
    /// and 0 in every high-pass one; synthesis must give c back. This pins where the scaling goes:
    /// K on the low-pass samples and 1/K on the high-pass, which swapped would return c * K^2,
    /// half as much again.
    /// </summary>
    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 17)]
    [InlineData(3, 8)]
    public void Filter97_ReconstructsADcSignal(int i0, int i1)
    {
        const float c = 37.5f;
        var interleaved = new float[i1 - i0];
        for (var i = i0; i < i1; i++) interleaved[i - i0] = (i & 1) == 0 ? c : 0f;

        var reconstructed = InverseWavelet.Filter97ForTests(interleaved, i0, i1);

        foreach (var sample in reconstructed) sample.ShouldBe(c, 1e-4f);
    }

    /// <summary>
    /// G.3.2 by hand: luma alone is grey, and each chroma component moves the channels by exactly
    /// T.800's coefficients and in the right direction.
    /// </summary>
    [Fact]
    public void InverseIct_MatchesG32()
    {
        float[] y = [100f, 100f, 100f];
        float[] cb = [0f, 10f, 0f];
        float[] cr = [0f, 0f, 10f];

        ComponentTransform.InverseIct(y, cb, cr);

        // Pixel 0: no chroma, so R = G = B = Y.
        (y[0], cb[0], cr[0]).ShouldBe((100f, 100f, 100f));

        // Pixel 1: Cb only, which moves green down and blue up.
        y[1].ShouldBe(100f, 1e-4f);
        cb[1].ShouldBe(100f - 3.4413f, 1e-4f);
        cr[1].ShouldBe(100f + 17.72f, 1e-4f);

        // Pixel 2: Cr only, which moves red up and green down.
        y[2].ShouldBe(100f + 14.02f, 1e-4f);
        cb[2].ShouldBe(100f - 7.1414f, 1e-4f);
        cr[2].ShouldBe(100f, 1e-4f);
    }

    /// <summary>
    /// The derived quantization style (T.800 Equation E-5) signals one step size for the LL band
    /// and derives every other: eps_b = eps_0 - N_L + n_b, with the mantissa shared. No fixture
    /// carries the style, because opj_compress never writes it, so the rule is checked here by
    /// hand: each decomposition level nearer full resolution has an exponent one lower.
    /// </summary>
    [Fact]
    public void DerivedQuantization_FollowsE5()
    {
        var derived = new Quantization(QuantizationStyle.ScalarDerived, 2, [13], [1000]);
        const int levels = 3;

        // The LL band comes from level N_L, so it keeps eps_0.
        derived.StepFor(0, decompositionLevel: 3, levels, component: 0).ShouldBe((13, 1000));

        // HL, LH and HH of level 3 (resolution 1), then of level 1 (resolution 3).
        for (var b = 1; b <= 3; b++) derived.StepFor(b, decompositionLevel: 3, levels, 0).ShouldBe((13, 1000));
        for (var b = 7; b <= 9; b++) derived.StepFor(b, decompositionLevel: 1, levels, 0).ShouldBe((11, 1000));
    }

    /// <summary>
    /// Expounded quantization takes each band's own pair, and a marker carrying too few pairs for
    /// the bands it must describe is malformed rather than read past its end.
    /// </summary>
    [Fact]
    public void ExpoundedQuantization_TakesEachBandsOwnPair()
    {
        var expounded = new Quantization(QuantizationStyle.ScalarExpounded, 2, [10, 9, 9, 8], [1, 2, 3, 4]);

        expounded.StepFor(2, decompositionLevel: 1, levels: 1, component: 0).ShouldBe((9, 3));
        Should.Throw<InvalidDataException>(() => expounded.StepFor(4, 1, 2, 0));
    }
}
