using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Symbol dictionaries and text regions decoded from <b>real jbig2enc output</b>.
/// <para>
/// Symbol mode is what jbig2enc is actually for, and it exercises the half of
/// T.88 that rung 1 never touched: a dictionary segment carrying glyph bitmaps,
/// and a text region that places them by coded deltas rather than by coding
/// pixels. Every number in that layout comes through the Annex A integer
/// decoder, so these fixtures cover IADT/IAFS/IADS/IADH/IADW/IAEX and the symbol
/// ID tree in one go.
/// </para>
/// <para>
/// The expected raster is committed rather than hand-written, because symbol
/// matching is lossy by default — jbig2enc replaces near-identical glyphs with a
/// shared one, so the decoded page is deliberately <em>not</em> the source image
/// and there is no grid anyone could write by eye. Both files are third-party
/// output and both are data: the licence line this repo draws is about reading
/// source, not about running programs. See <c>Oracle/jbig2/README.md</c>.
/// </para>
/// </summary>
public sealed class Jbig2SymbolFixtureTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    internal static string FixtureDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Fixtures", "jbig2");
                if (Directory.Exists(candidate)) return candidate;
            }

            throw new DirectoryNotFoundException("Fixtures/jbig2 not found next to the test binaries.");
        }
    }

    [Theory]
    [InlineData("sym")]
    [InlineData("sym_tpgd")]
    public void JbigEncSymbolMode_DecodesToTheReferenceRaster(string name)
    {
        var file = File.ReadAllBytes(Path.Combine(FixtureDirectory, $"{name}.jb2"));
        var expected = ReadPbm(Path.Combine(FixtureDirectory, $"{name}.pbm"));

        Jbig2Decoder.TryReadFileInfo(file, out var width, out var height).ShouldBeTrue();
        width.ShouldBe(expected.Width);
        height.ShouldBe(expected.Height);

        var image = Jbig2Decoder.DecodeFile(file);
        _out.WriteLine($"{name}: {file.Length} bytes -> {width}x{height}");

        ShouldMatch(image.Bits, expected.Bits, width, height, name);
    }

    /// <summary>
    /// Pins what these fixtures actually cover, so a regeneration against a newer
    /// jbig2enc cannot quietly widen or narrow it. jbig2enc emits arithmetic
    /// coding throughout, no refinement, and the bottom-left reference corner —
    /// so anything else in §6.4/§6.5 is on the synthetic tests, not here.
    /// </summary>
    [Theory]
    [InlineData("sym")]
    [InlineData("sym_tpgd")]
    public void JbigEncSymbolFixtures_UseArithmeticCodingWithoutRefinement(string name)
    {
        var file = File.ReadAllBytes(Path.Combine(FixtureDirectory, $"{name}.jb2"));
        var segments = Jbig2FixtureReader.ReadSegments(file);

        var dictionary = segments.Single(s => s.Type == 0);
        var dictionaryFlags = (dictionary.Data[0] << 8) | dictionary.Data[1];
        (dictionaryFlags & 0x0001).ShouldBe(0, "SDHUFF");
        (dictionaryFlags & 0x0002).ShouldBe(0, "SDREFAGG");
        ((dictionaryFlags >> 10) & 3).ShouldBe(0, "SDTEMPLATE");

        var text = segments.Single(s => s.Type is 4 or 6 or 7);
        var textFlags = (text.Data[17] << 8) | text.Data[18];
        (textFlags & 0x0001).ShouldBe(0, "SBHUFF");
        (textFlags & 0x0002).ShouldBe(0, "SBREFINE");
        ((textFlags >> 4) & 3).ShouldBe(0, "SBREFCORNER should be BOTTOMLEFT");
        (textFlags & 0x0040).ShouldBe(0, "SBTRANSPOSED");

        _out.WriteLine($"{name}: SD flags 0x{dictionaryFlags:x4}, TR flags 0x{textFlags:x4}");
    }

    /// <summary>
    /// The same fixtures through the PDF-shaped entry point, with the dictionary
    /// handed over as a separate globals stream — which is how a real PDF carries
    /// them, and the reason <see cref="Jbig2Decoder.Decode"/> takes globals at
    /// all. Splitting the file this way proves the symbols really do cross the
    /// stream boundary rather than merely surviving within one.
    /// </summary>
    [Theory]
    [InlineData("sym")]
    [InlineData("sym_tpgd")]
    public void SymbolDictionary_CrossesTheGlobalsBoundary(string name)
    {
        var file = File.ReadAllBytes(Path.Combine(FixtureDirectory, $"{name}.jb2"));
        var expected = ReadPbm(Path.Combine(FixtureDirectory, $"{name}.pbm"));
        var segments = Jbig2FixtureReader.ReadSegments(file);

        var globals = new List<byte>();
        var embedded = new List<byte>();
        foreach (var segment in segments)
            (segment.Type == 0 ? globals : embedded).AddRange(segment.Raw);

        globals.Count.ShouldBeGreaterThan(0, "the fixture should contain a symbol dictionary");

        var image = Jbig2Decoder.Decode([.. embedded], [.. globals], expected.Width, expected.Height);
        ShouldMatch(image.Bits, expected.Bits, expected.Width, expected.Height, $"{name} (split)");
    }

    private static void ShouldMatch(ReadOnlySpan<byte> actual, byte[] expected, int w, int h, string what)
    {
        if (actual.SequenceEqual(expected)) return;

        var first = -1;
        for (var i = 0; i < expected.Length && first < 0; i++)
            if (actual[i] != expected[i])
                first = i;

        throw new Shouldly.ShouldAssertException(
            $"{what}: first difference at ({first % w},{first / w})\n" +
            "expected:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(w, h, expected)) +
            "\nactual:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(w, h, actual)));
    }

    /// <summary>Reads a binary PBM (P4) into one byte per pixel, 1 = black.</summary>
    private static (int Width, int Height, byte[] Bits) ReadPbm(string path)
    {
        var data = File.ReadAllBytes(path);
        var position = 0;

        string Token()
        {
            while (position < data.Length && char.IsWhiteSpace((char)data[position])) position++;
            var start = position;
            while (position < data.Length && !char.IsWhiteSpace((char)data[position])) position++;
            return System.Text.Encoding.ASCII.GetString(data, start, position - start);
        }

        Token().ShouldBe("P4");
        var width = int.Parse(Token());
        var height = int.Parse(Token());
        position++;   // exactly one whitespace byte before the raster

        var stride = (width + 7) / 8;
        var bits = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bits[y * width + x] = (byte)((data[position + y * stride + (x >> 3)] >> (7 - (x & 7))) & 1);

        return (width, height, bits);
    }
}
