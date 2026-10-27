using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Rung 0: tests <em>about the fixtures and the oracle</em>, before there is a
/// decoder to test.
/// <para>
/// The roadmap's warning is that JPEG 2000 has no early pipeline stage that
/// decodes anything on its own, so rung 1's first end-to-end pass comes late.
/// That makes it worth establishing now, while nothing else can be blamed, that
/// the fixture corpus is well-formed and that its central claim holds: a
/// reversible 5/3 codestream reproduces its source raster <b>exactly</b>, so
/// each committed <c>.pgm</c> is the expected output for the <c>.j2k</c> beside
/// it and no oracle process is needed at test time.
/// </para>
/// <para>
/// If these ever fail, the corpus is wrong and every rung-1 failure built on it
/// is uninterpretable. Fix here first.
/// </para>
/// </summary>
public class Jpeg2000FixtureTests
{
    private static string FixtureDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000");

    public static TheoryData<string> Codestreams
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(FixtureDirectory, "*.j2k").OrderBy(p => p))
                data.Add(Path.GetFileNameWithoutExtension(path));

            return data;
        }
    }

    /// <summary>
    /// The source raster a lossless fixture was made from, which is its expected output: a
    /// <c>.pgm</c> for one component, a <c>.ppm</c> for three.
    /// </summary>
    internal static string SourceRaster(string name)
    {
        var pgm = Path.Combine(FixtureDirectory, name + ".pgm");
        return File.Exists(pgm) ? pgm : Path.Combine(FixtureDirectory, name + ".ppm");
    }

    /// <summary>
    /// The corpus exists and every codestream has the source raster that is its
    /// expected output. Needs nothing installed — it is the check that a clone
    /// with no oracle can still tell the fixtures apart from an empty directory.
    /// </summary>
    [Fact]
    public void EveryCodestream_HasItsSourceRaster()
    {
        Directory.Exists(FixtureDirectory).ShouldBeTrue(
            $"{FixtureDirectory} is missing; regenerate with Oracle/jpeg2000/make-fixtures.sh");

        var codestreams = Directory.GetFiles(FixtureDirectory, "*.j2k");
        codestreams.Length.ShouldBeGreaterThan(0, "no .j2k fixtures were copied to the output directory");

        foreach (var j2k in codestreams)
        {
            var name = Path.GetFileNameWithoutExtension(j2k);
            File.Exists(SourceRaster(name)).ShouldBeTrue($"{Path.GetFileName(j2k)} has no source raster beside it");
        }
    }

    /// <summary>
    /// Every codestream starts SOC+SIZ (<c>FF 4F FF 51</c>) — the raw-J2K magic
    /// the facade will sniff on. Cheap, but it is the one structural claim rung 1
    /// will build its marker parser against, and it pins that the generator did
    /// not quietly start emitting JP2-boxed output.
    /// </summary>
    [Theory]
    [MemberData(nameof(Codestreams))]
    public void EveryCodestream_StartsWithSocSiz(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k"));

        bytes.Length.ShouldBeGreaterThan(4);
        bytes[..4].ShouldBe([0xFF, 0x4F, 0xFF, 0x51]);
    }

    /// <summary>
    /// The claim the whole no-oracle strategy rests on, checked against the
    /// reference decoder rather than assumed: OpenJPEG decoding our committed
    /// codestream reproduces our committed source raster, sample for sample,
    /// with no tolerance.
    /// <para>
    /// This is deliberately the oracle validating the <em>fixtures</em>, not the
    /// fixtures validating our decoder — there is no decoder yet. Once rung 1
    /// lands, its tests assert the same equality without shelling out, and this
    /// one stays as the guard that the corpus still means what it meant when it
    /// was generated.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Codestreams))]
    public void EveryCodestream_DecodesToItsSourceRaster_ViaOpenJpeg(string name)
    {
        OpenJpegOracle.RequireOrSkip();

        var source = SourceRaster(name);
        var expected = Pnm.Read(source);
        var actual = OpenJpegOracle.Decode(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, name + ".j2k")), Path.GetExtension(source));

        actual.Width.ShouldBe(expected.Width);
        actual.Height.ShouldBe(expected.Height);
        actual.Components.ShouldBe(expected.Components);
        actual.MaxValue.ShouldBe(expected.MaxValue);
        actual.Samples.ShouldBe(expected.Samples);
    }

    /// <summary>
    /// The oracle is really running, in the shape <c>Jbig2OracleTests</c>
    /// established: a test whose only job is to fail when the harness has
    /// quietly stopped working, so a wall of skips is never mistaken for a wall
    /// of passes.
    /// </summary>
    [Fact]
    public void Oracle_IsActuallyRunning()
    {
        OpenJpegOracle.RequireOrSkip();

        var decoded = OpenJpegOracle.Decode(
            File.ReadAllBytes(Path.Combine(FixtureDirectory, "odd1x1.j2k")));

        decoded.Width.ShouldBe(1);
        decoded.Height.ShouldBe(1);
    }
}
