using System.Buffers.Binary;
using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Pattern dictionaries and halftone regions — T.88 §6.6/§6.7, the path that
/// stores a photograph in a bilevel format.
/// <para>
/// No third-party encoder emits these: jbig2enc has no halftone mode at all. So
/// the streams are synthesised here and the conformance check is
/// <c>Jbig2HalftoneOracleTests</c> pushing them through jbig2dec — the same
/// arrangement refinement uses, and for the same reason.
/// </para>
/// </summary>
public sealed class Jbig2HalftoneTests
{
    [Theory]
    [InlineData(2)]     // one bitplane
    [InlineData(4)]     // two
    [InlineData(5)]     // three, with the top level unused — the Gray code still has to line up
    [InlineData(16)]
    public void GreyLevels_RoundTripThroughTheBitplanes(int levels)
    {
        var (stream, expected) = Build(levels, 8, 6, 4, 4);

        var image = Jbig2Decoder.Decode(stream, expected.Width, expected.Height);
        image.Bits.ToArray().ShouldBe(expected.Data);
    }

    /// <summary>
    /// The grid is a lattice, not a rectangle: (HRX, HRY) is a vector in 1/256
    /// pixel units, so a dither grid can be sheared relative to the page. A
    /// decoder that treated it as a plain row/column pitch would pass the square
    /// cases above and fail here. Both components are unsigned 16-bit per
    /// §7.4.5.1.4, so a lattice can shear one way but not the other.
    /// </summary>
    [Theory]
    [InlineData(4 * 256, 0)]
    [InlineData(4 * 256, 256)]
    [InlineData(5 * 256, 512)]
    [InlineData(4 * 256 + 128, 0)]   // a fractional pitch, which only the 1/256 units can express
    public void GridVector_PlacesCellsAlongTheLattice(int vectorX, int vectorY)
    {
        var (stream, expected) = Build(4, 6, 5, 4, 4, vectorX, vectorY);

        var image = Jbig2Decoder.Decode(stream, expected.Width, expected.Height);
        image.Bits.ToArray().ShouldBe(expected.Data);
    }

    [Fact]
    public void HalftoneRegion_WithNoPatternDictionary_Fails()
    {
        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(32, 32)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateHalftoneRegion, 1,
                HalftoneRegionSegment(32, 32, new int[4], 2, 2, 4, Patterns(2))));

        Should.Throw<InvalidDataException>(() => Jbig2Decoder.Decode(stream, 32, 32))
            .Message.ShouldContain("pattern dictionary");
    }

    [Fact]
    public void HalftoneRegion_WithSkipEnabled_IsRefusedByName()
    {
        var patterns = Patterns(4);
        var segment = HalftoneRegionSegment(32, 32, new int[4], 2, 2, 4, patterns);
        segment[17] |= 0x08;   // HENABLESKIP, in the flags byte after the region info

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(32, 32)),
            Jbig2StreamBuilder.Segment(1, SegmentType.PatternDictionary, 1, PatternDictionarySegment(patterns)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateHalftoneRegion, 1, segment, referredTo: [1]));

        Should.Throw<NotSupportedException>(() => Jbig2Decoder.Decode(stream, 32, 32))
            .Message.ShouldContain("HENABLESKIP");
    }

    [Fact]
    public void MmrCodedPatternDictionary_IsRefusedByName()
    {
        var segment = PatternDictionarySegment(Patterns(4));
        segment[0] |= 0x01;   // HDMMR

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(32, 32)),
            Jbig2StreamBuilder.Segment(1, SegmentType.PatternDictionary, 1, segment));

        Should.Throw<NotSupportedException>(() => Jbig2Decoder.Decode(stream, 32, 32))
            .Message.ShouldContain("MMR");
    }

    // ---- stream construction -----------------------------------------------------

    /// <summary>
    /// A pattern dictionary plus a halftone region over a deterministic grey
    /// ramp, and the raster it should produce — computed by stamping the patterns
    /// directly, so the test and the decoder agree only if both are right.
    /// </summary>
    /// <summary>The segments and expected raster, for callers that want to wrap them differently.</summary>
    internal static (byte[][] Segments, Jbig2Bitmap Expected) BuildSegments(
        int levels, int gridWidth = 6, int gridHeight = 5, int patternWidth = 4, int patternHeight = 4,
        int vectorX = 0, int vectorY = 0)
    {
        var (stream, expected) = Build(levels, gridWidth, gridHeight, patternWidth, patternHeight, vectorX, vectorY);
        return (Split(stream), expected);
    }

    /// <summary>Splits an embedded stream back into its segments, so it can be re-wrapped as a file.</summary>
    private static byte[][] Split(byte[] stream) =>
        [.. Jbig2FixtureReader.ReadSegments(stream).Select(s => s.Raw)];

    internal static (byte[] Stream, Jbig2Bitmap Expected) Build(
        int levels, int gridWidth, int gridHeight, int patternWidth, int patternHeight,
        int vectorX = 0, int vectorY = 0)
    {
        if (vectorX == 0) vectorX = patternWidth * 256;

        var patterns = Patterns(levels, patternWidth, patternHeight);
        var grey = new int[gridWidth * gridHeight];
        for (var i = 0; i < grey.Length; i++) grey[i] = (i * 7 + i / gridWidth) % levels;

        const int gridX = 0, gridY = 0;
        var width = gridWidth * patternWidth + 16;
        var height = gridHeight * patternHeight + 16;

        var stream = Jbig2StreamBuilder.Stream(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1,
                Jbig2StreamBuilder.PageInformation(width, height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.PatternDictionary, 1, PatternDictionarySegment(patterns)),
            Jbig2StreamBuilder.Segment(2, SegmentType.ImmediateHalftoneRegion, 1,
                HalftoneRegionSegment(width, height, grey, gridWidth, gridHeight, levels, patterns,
                    gridX, gridY, vectorX, vectorY),
                referredTo: [1]));

        var expected = new Jbig2Bitmap(width, height);
        for (var m = 0; m < gridHeight; m++)
            for (var n = 0; n < gridWidth; n++)
            {
                var x = gridX + m * vectorY + n * vectorX;
                var y = gridY + m * vectorX - n * vectorY;
                expected.Combine(patterns[grey[m * gridWidth + n]], x >> 8, y >> 8, CombinationOperator.Or);
            }

        return (stream, expected);
    }

    /// <summary>Dither patterns of increasing darkness, the way a real halftone screen is built.</summary>
    internal static Jbig2Bitmap[] Patterns(int levels, int width = 4, int height = 4)
    {
        var patterns = new Jbig2Bitmap[levels];
        var cells = width * height;
        for (var level = 0; level < levels; level++)
        {
            var pattern = new Jbig2Bitmap(width, height);

            // Fill in a fixed scatter order so each level is a superset of the one
            // below — otherwise the "ramp" would not be monotone and a placement
            // bug could hide behind a coincidence.
            var filled = level * cells / (levels - 1 == 0 ? 1 : levels - 1);
            for (var i = 0; i < filled && i < cells; i++)
            {
                var index = (i * 7) % cells;
                pattern.Data[index] = 1;
            }

            patterns[level] = pattern;
        }

        return patterns;
    }

    /// <summary>A pattern dictionary segment (T.88 §7.4.4) holding the patterns side by side.</summary>
    internal static byte[] PatternDictionarySegment(Jbig2Bitmap[] patterns, int template = 0)
    {
        var patternWidth = patterns[0].Width;
        var patternHeight = patterns[0].Height;

        // §6.7.5: the dictionary is coded as one wide bitmap, patterns abutting.
        var collective = new Jbig2Bitmap(patterns.Length * patternWidth, patternHeight);
        for (var i = 0; i < patterns.Length; i++)
            collective.Combine(patterns[i], i * patternWidth, 0, CombinationOperator.Replace);

        var at = HalftoneDecoder.CollectiveAt(patternWidth);
        var body = new List<byte>
        {
            (byte)(template << 1),
            (byte)patternWidth,
            (byte)patternHeight,
        };

        WriteUInt32(body, (uint)(patterns.Length - 1));   // GRAYMAX
        body.AddRange(Jbig2StreamBuilder.EncodeGenericRegion(collective, template, false, at));
        return [.. body];
    }

    /// <summary>A halftone region segment (T.88 §7.4.5) carrying the grey grid as Gray-coded bitplanes.</summary>
    internal static byte[] HalftoneRegionSegment(
        int width, int height, int[] grey, int gridWidth, int gridHeight, int levels,
        Jbig2Bitmap[] patterns, int gridX = 0, int gridY = 0, int vectorX = 0, int vectorY = 0,
        int template = 0)
    {
        if (vectorX == 0) vectorX = patterns[0].Width * 256;

        var body = new List<byte>();
        WriteUInt32(body, (uint)width);
        WriteUInt32(body, (uint)height);
        WriteUInt32(body, 0);
        WriteUInt32(body, 0);
        body.Add((byte)CombinationOperator.Or);           // external combination
        body.Add((byte)(template << 1));                  // HMMR=0, HENABLESKIP=0, HCOMBOP=OR, HDEFPIXEL=0
        WriteUInt32(body, (uint)gridWidth);
        WriteUInt32(body, (uint)gridHeight);
        WriteUInt32(body, (uint)gridX);
        WriteUInt32(body, (uint)gridY);
        WriteUInt16(body, (ushort)vectorX);
        WriteUInt16(body, (ushort)vectorY);
        body.AddRange(EncodeGrayscale(grey, gridWidth, gridHeight, levels, template));
        return [.. body];
    }

    /// <summary>
    /// The write side of T.88 Annex C.5: split the values into bitplanes, Gray-code
    /// them against the plane above, and code each as a generic region — all into
    /// one arithmetic stream with one shared context array, exactly as the decoder
    /// reads them back.
    /// </summary>
    private static byte[] EncodeGrayscale(int[] values, int width, int height, int levels, int template)
    {
        var planes = 0;
        while (1 << planes < levels) planes++;
        planes = Math.Max(planes, 1);

        var encoder = new Jbig2MqEncoder();
        var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];
        var at = HalftoneDecoder.GrayscaleAt(template);
        byte[]? previous = null;

        for (var bit = planes - 1; bit >= 0; bit--)
        {
            var plane = new Jbig2Bitmap(width, height);
            for (var i = 0; i < values.Length; i++)
            {
                var raw = (byte)((values[i] >> bit) & 1);
                plane.Data[i] = previous is null ? raw : (byte)(raw ^ previous[i]);
            }

            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    encoder.Encode(contexts,
                        GenericRegionDecoder.Context(plane, x, y, template, at),
                        plane.Data[y * width + x]);

            // The next plane down is coded against this one *after* decoding, so
            // the running reference is the reconstructed bit, not the coded one.
            var reconstructed = new byte[values.Length];
            for (var i = 0; i < values.Length; i++) reconstructed[i] = (byte)((values[i] >> bit) & 1);
            previous = reconstructed;
        }

        return encoder.Flush();
    }

    private static void WriteUInt16(List<byte> target, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        target.AddRange(buffer);
    }

    private static void WriteUInt32(List<byte> target, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        target.AddRange(buffer);
    }
}
