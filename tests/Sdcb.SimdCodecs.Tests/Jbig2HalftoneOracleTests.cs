using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Halftone regions checked against jbig2dec — the layer that establishes the
/// §6.6/§6.7 procedures are right rather than merely self-consistent.
/// <para>
/// Two things here have no other check. The Gray coding of Annex C.5 is
/// invisible to a round-trip, since our encoder and decoder would apply the same
/// XOR chain either way; and the lattice arithmetic of §6.6.5.1 — where a cell's
/// position mixes both grid vector components in 1/256 pixel units — is easy to
/// get self-consistently wrong. jbig2dec computes both independently.
/// </para>
/// </summary>
public sealed class Jbig2HalftoneOracleTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Theory]
    [InlineData(2, 4 * 256, 0)]
    [InlineData(4, 4 * 256, 0)]
    [InlineData(5, 4 * 256, 0)]
    [InlineData(16, 4 * 256, 0)]
    [InlineData(4, 4 * 256, 256)]        // sheared lattice
    [InlineData(4, 5 * 256, 512)]
    [InlineData(4, 4 * 256 + 128, 0)]    // fractional pitch
    public void HalftoneRegion_AgreesWithJbig2dec(int levels, int vectorX, int vectorY)
    {
        Jbig2Oracle.RequireOrSkip();

        var (segments, expected) = BuildSegments(levels, vectorX, vectorY);
        var file = Jbig2StreamBuilder.SequentialFile([.. segments,
            Jbig2StreamBuilder.Segment(9, SegmentType.EndOfPage, 1, [])]);

        var raster = Jbig2Oracle.Decode(file);
        _out.WriteLine($"{levels} levels, vector ({vectorX},{vectorY}): {file.Length} bytes");

        raster.Width.ShouldBe(expected.Width);
        raster.Height.ShouldBe(expected.Height);

        if (!raster.Bits.AsSpan().SequenceEqual(expected.Data))
        {
            var first = -1;
            for (var i = 0; i < expected.Data.Length && first < 0; i++)
                if (raster.Bits[i] != expected.Data[i])
                    first = i;

            throw new Shouldly.ShouldAssertException(
                $"jbig2dec disagrees for {levels} levels, vector ({vectorX},{vectorY}), " +
                $"first at ({first % expected.Width},{first / expected.Width})\n" +
                "expected:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(expected.Width, expected.Height, expected.Data)) +
                "\nactual:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(expected.Width, expected.Height, raster.Bits)));
        }

        Jbig2Decoder.DecodeFile(file).Bits.ToArray().ShouldBe(expected.Data);
    }

    private static (byte[][] Segments, Jbig2Bitmap Expected) BuildSegments(int levels, int vectorX, int vectorY)
    {
        const int gridWidth = 6, gridHeight = 5, patternWidth = 4, patternHeight = 4;

        var patterns = Jbig2HalftoneTests.Patterns(levels, patternWidth, patternHeight);
        var grey = new int[gridWidth * gridHeight];
        for (var i = 0; i < grey.Length; i++) grey[i] = (i * 7 + i / gridWidth) % levels;

        var width = gridWidth * patternWidth + 16;
        var height = gridHeight * patternHeight + 16;

        byte[][] segments =
        [
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(width, height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.PatternDictionary, 1,
                Jbig2HalftoneTests.PatternDictionarySegment(patterns)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateHalftoneRegion, 1,
                Jbig2HalftoneTests.HalftoneRegionSegment(
                    width, height, grey, gridWidth, gridHeight, levels, patterns, 0, 0, vectorX, vectorY),
                referredTo: [1]),
        ];

        var expected = new Jbig2Bitmap(width, height);
        for (var m = 0; m < gridHeight; m++)
            for (var n = 0; n < gridWidth; n++)
            {
                var x = m * vectorY + n * vectorX;
                var y = m * vectorX - n * vectorY;
                expected.Combine(patterns[grey[m * gridWidth + n]], x >> 8, y >> 8, CombinationOperator.Or);
            }

        return (segments, expected);
    }
}
