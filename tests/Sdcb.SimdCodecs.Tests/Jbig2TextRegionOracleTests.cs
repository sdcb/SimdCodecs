using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Text region placement checked against jbig2dec.
/// <para>
/// This is the layer that establishes the §6.4.5 placement rules are right.
/// <c>Jbig2TextRegionTests</c> builds the same streams and decodes them with our
/// own decoder, which proves the encoder and decoder agree and nothing more —
/// both compute the reference corner the same way, so both would be wrong
/// together. jbig2dec computes it independently.
/// </para>
/// <para>
/// The committed jbig2enc fixtures cannot reach here: jbig2enc emits only
/// bottom-left, untransposed, single-strip regions, so seven of the eight
/// corner/transposed combinations have no third-party bytes anywhere.
/// </para>
/// </summary>
public sealed class Jbig2TextRegionOracleTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Theory]
    [MemberData(nameof(Jbig2TextRegionTests.Corners), MemberType = typeof(Jbig2TextRegionTests))]
    public void EveryReferenceCorner_AgreesWithJbig2dec(int corner, bool transposed)
    {
        Jbig2Oracle.RequireOrSkip();

        var (segments, expected) = Jbig2TextRegionTests.BuildSegments((ReferenceCorner)corner, transposed);
        var file = Jbig2StreamBuilder.SequentialFile([.. segments,
            Jbig2StreamBuilder.Segment(9, SegmentType.EndOfPage, 1, [])]);

        var raster = Jbig2Oracle.Decode(file);
        _out.WriteLine($"corner {(ReferenceCorner)corner} transposed {transposed}: {file.Length} bytes");

        ShouldMatch(raster.Bits, expected, $"corner {(ReferenceCorner)corner}, transposed {transposed}");
        ShouldMatch(Jbig2Decoder.DecodeFile(file).Bits, expected, "our own decode");
    }

    /// <summary>
    /// Multi-row strips, which change the coded shape rather than just the
    /// geometry: SBSTRIPS &gt; 1 introduces the IAIT field that a single-strip
    /// region never codes at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MultiRowStrips_AgreeWithJbig2dec(int logStrips)
    {
        Jbig2Oracle.RequireOrSkip();

        var (segments, expected) = Jbig2TextRegionTests.BuildSegments(
            ReferenceCorner.TopLeft, transposed: false, logStrips: logStrips);
        var file = Jbig2StreamBuilder.SequentialFile([.. segments,
            Jbig2StreamBuilder.Segment(9, SegmentType.EndOfPage, 1, [])]);

        ShouldMatch(Jbig2Oracle.Decode(file).Bits, expected, $"SBSTRIPS {1 << logStrips}");
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(0)]
    [InlineData(4)]
    public void DsOffset_AgreesWithJbig2dec(int dsOffset)
    {
        Jbig2Oracle.RequireOrSkip();

        var (segments, expected) = Jbig2TextRegionTests.BuildSegments(
            ReferenceCorner.TopLeft, transposed: false, dsOffset: dsOffset);
        var file = Jbig2StreamBuilder.SequentialFile([.. segments,
            Jbig2StreamBuilder.Segment(9, SegmentType.EndOfPage, 1, [])]);

        ShouldMatch(Jbig2Oracle.Decode(file).Bits, expected, $"SBDSOFFSET {dsOffset}");
    }

    private static void ShouldMatch(ReadOnlySpan<byte> actual, Jbig2Bitmap expected, string what)
    {
        if (actual.SequenceEqual(expected.Data)) return;

        var first = -1;
        for (var i = 0; i < expected.Data.Length && first < 0; i++)
            if (actual[i] != expected.Data[i])
                first = i;

        throw new Shouldly.ShouldAssertException(
            $"{what}: first difference at ({first % expected.Width},{first / expected.Width})\n" +
            "expected:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(expected.Width, expected.Height, expected.Data)) +
            "\nactual:\n" + string.Join('\n', Jbig2StreamBuilder.ToRows(expected.Width, expected.Height, actual)));
    }
}
