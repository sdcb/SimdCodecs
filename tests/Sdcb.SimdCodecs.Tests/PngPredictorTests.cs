using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PngPredictor"/>'s row unfilter against the PNG specification's formulas (§9.2-9.4),
/// written out here independently, byte by byte: every filter type, every byte stride a PNG or a
/// TIFF/PDF predictor uses, and row lengths that end the per-pixel vector loop on every tail. The
/// vector paths and the scalar twin both have to land here; CI runs this on each instruction tier.
/// </summary>
public sealed class PngPredictorTests
{
    public static TheoryData<int, int, int> Cases()
    {
        var data = new TheoryData<int, int, int>();
        foreach (var filter in new[] { 0, 1, 2, 3, 4 })
        {
            foreach (var bpp in new[] { 1, 2, 3, 4, 6, 8 })
            {
                foreach (var pixels in new[] { 1, 2, 3, 5, 8, 13, 40 })
                    data.Add(filter, bpp, pixels);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void UnfilterRowFollowsTheSpecification(int filter, int bpp, int pixels)
    {
        var random = new Random(filter * 10_000 + bpp * 100 + pixels);
        var length = bpp * pixels;
        var above = new byte[length];
        var filtered = new byte[length];
        random.NextBytes(above);
        random.NextBytes(filtered);

        var row = (byte[])filtered.Clone();
        PngPredictor.UnfilterRow((byte)filter, row, above, bpp);

        row.ShouldBe(Reference(filter, filtered, above, bpp));
    }

    [Fact]
    public void UnfilterRunsEveryRowAgainstTheOneAbove()
    {
        // Public multi-row entry: filter byte per row, each row predicted from the previous output.
        const int bpp = 3, columns = 3 * 11;
        var random = new Random(7);
        var input = new byte[5 * (columns + 1)];
        random.NextBytes(input);
        for (var y = 0; y < 5; y++)
            input[y * (columns + 1)] = (byte)(y % 5);

        var output = PngPredictor.Unfilter(input, columns, bpp);

        var above = new byte[columns];
        for (var y = 0; y < 5; y++)
        {
            var expected = Reference(y % 5, input.AsSpan(y * (columns + 1) + 1, columns).ToArray(), above, bpp);
            output.AsSpan(y * columns, columns).ToArray().ShouldBe(expected);
            above = expected;
        }
    }

    private static byte[] Reference(int filter, byte[] filtered, byte[] above, int bpp)
    {
        var output = new byte[filtered.Length];
        for (var i = 0; i < filtered.Length; i++)
        {
            int a = i >= bpp ? output[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
            var predictor = filter switch
            {
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            output[i] = (byte)(filtered[i] + predictor);
        }
        return output;
    }

    // PNG specification 9.4: the nearest of a, b, c to a + b - c, ties in the order a, b, c.
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
