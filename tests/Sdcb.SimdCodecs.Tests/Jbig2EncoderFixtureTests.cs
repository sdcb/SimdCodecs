using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Decodes JBIG2 streams produced by <b>jbig2enc</b> — a third-party encoder this
/// repo neither wrote nor ports. This is the layer nothing else in the JBIG2
/// suite provides: the H.2 vector proves the arithmetic, the one-hot tests pin
/// the templates against the spec figures, and the synthetic round-trips prove
/// self-consistency, but all three are bytes we produced ourselves. These
/// fixtures are not.
/// <para>
/// The <c>.jb2</c> files are <b>committed</b> rather than generated at test time,
/// deliberately. They are under 130 bytes each, they are the whole point (bytes
/// from an encoder we do not control), and committing them means this runs in CI
/// unconditionally with no tooling installed — no skip, and therefore no
/// silently-skipped oracle. Regenerate with
/// <c>tests/Sdcb.SimdCodecs.Tests/Oracle/jbig2/make-fixtures.sh</c>.
/// </para>
/// <para>
/// Licence note: jbig2enc is Apache-2.0, and the clean-room rules keep its
/// <em>source</em> out as a port source. Its <em>output</em> is data — a rendering
/// of a pattern authored here — and carries no such constraint. Same distinction
/// the roadmap draws for every reference implementation in this space.
/// </para>
/// <para>
/// What these do and do not cover: jbig2enc emits GBTEMPLATE 0 with nominal AT
/// pixels (verified in the fixture bytes), so templates 1-3 and moved AT pixels
/// are not exercised here. Those are covered from the other direction by
/// <see cref="Jbig2OracleTests"/>, which pushes our own encoder's output through
/// jbig2dec across every template.
/// </para>
/// </summary>
public sealed class Jbig2EncoderFixtureTests
{
    // The pattern the committed fixtures encode: a bordered box over a diagonal
    // brick tiling, 32x16. Chosen to exercise long runs, short runs, and edges
    // rather than to look like anything.
    private static readonly string[] Expected =
    [
        "################################",
        "###...###...###...###...###...##",
        "#..###...###...###...###...###.#",
        "#..###...###...###...###...###.#",
        "###...###...###...###...###...##",
        "###...###...###...###...###...##",
        "#..###...###...###...###...###.#",
        "#..###...###...###...###...###.#",
        "###...###...###...###...###...##",
        "###...###...###...###...###...##",
        "#..###...###...###...###...###.#",
        "#..###...###...###...###...###.#",
        "###...###...###...###...###...##",
        "###...###...###...###...###...##",
        "#..###...###...###...###...###.#",
        "################################",
    ];

    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jbig2", name));

    [Theory]
    [InlineData("s.jb2")]         // jbig2 t.pbm            - standalone file, generic region
    [InlineData("s_tpgd.jb2")]    // jbig2 -d t.pbm         - standalone, TPGD typical prediction
    public void DecodeFile_JbigEncStandaloneOutput_MatchesTheSourcePattern(string name)
    {
        var page = Jbig2Decoder.DecodeFile(Fixture(name));

        page.Width.ShouldBe(32);
        page.Height.ShouldBe(16);
        Jbig2StreamBuilder.ToRows(page.Width, page.Height, page.Bits).ShouldBe(Expected);
    }

    [Theory]
    [InlineData("e.jb2")]         // jbig2 -p t.pbm         - PDF-ready embedded stream
    [InlineData("e_tpgd.jb2")]    // jbig2 -p -d t.pbm      - embedded, TPGD
    public void Decode_JbigEncEmbeddedOutput_MatchesTheSourcePattern(string name)
    {
        // The PDF-shaped path: jbig2enc's -p output is exactly what a PDF
        // /JBIG2Decode stream carries, and the dimensions arrive out of band the
        // way an image dictionary supplies them.
        var page = Jbig2Decoder.Decode(Fixture(name), 32, 16);

        Jbig2StreamBuilder.ToRows(page.Width, page.Height, page.Bits).ShouldBe(Expected);
    }

    [Fact]
    public void JbigEncFixtures_UseTemplate0WithNominalAtPixels()
    {
        // Pins what the fixtures actually exercise, so the coverage claim in this
        // file's summary cannot quietly go stale if the fixtures are regenerated
        // with a different encoder version.
        //
        // Embedded layout: segment header (11) + page info (19) + segment header
        // (11) + region info (17) = 58, then the generic region flags byte.
        var bytes = Fixture("e.jb2");
        var flags = bytes[58];

        (flags & 0x01).ShouldBe(0, "MMR");
        ((flags >> 1) & 0x03).ShouldBe(0, "GBTEMPLATE");
        (flags & 0x08).ShouldBe(0, "TPGDON");

        // Four AT pairs at their T.88 nominal offsets: A1 (3,-1), A2 (-3,-1),
        // A3 (2,-2), A4 (-2,-2).
        var at = bytes.AsSpan(59, 8).ToArray().Select(b => (sbyte)b).ToArray();
        at.ShouldBe(new sbyte[] { 3, -1, -3, -1, 2, -2, -2, -2 });
    }

    [Fact]
    public void JbigEncTpgdFixture_ActuallySetsTypicalPrediction()
    {
        // -d is jbig2enc's "duplicate line removal", i.e. TPGDON. If a future
        // fixture regeneration silently dropped it, the TPGD path would stop
        // being covered by third-party bytes without any test failing.
        Fixture("e_tpgd.jb2")[58].ShouldBe((byte)0x08);
    }

    [Fact]
    public void Facade_DecodesAJbigEncStandaloneFile()
    {
        SimdCodecs.TryDecode(Fixture("s.jb2"), out var image).ShouldBeTrue();

        image!.Width.ShouldBe(32);
        image.Height.ShouldBe(16);

        // Grey projection: black 0, white 255.
        var rows = new string[16];
        for (var y = 0; y < 16; y++)
        {
            var row = new char[32];
            for (var x = 0; x < 32; x++) row[x] = image.Pixels[y * 32 + x] == 0 ? '#' : '.';
            rows[y] = new string(row);
        }

        rows.ShouldBe(Expected);
    }
}
