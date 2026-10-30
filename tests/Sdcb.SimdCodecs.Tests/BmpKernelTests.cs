using Sdcb.SimdCodecs.Bmp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Every BMP row kernel gives the same bytes on every tier (<see cref="BmpKernels.Scalar"/>, 128-bit, 256-bit)
/// as the test's own per-pixel model, at widths that end inside, on and past each vector step, from spans that
/// are exactly a row long (so a vector step that read or wrote past the row would throw or show).
/// </summary>
public sealed class BmpKernelTests
{
    private static readonly int[] Widths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 67, 200];

    private static IEnumerable<int> Tiers()
    {
        yield return BmpKernels.Scalar;
        yield return BmpKernels.Vector128Tier;
        yield return BmpKernels.Vector256Tier;
    }

    private static byte[] Random(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    // A destination a row long, with a sentinel span after it the kernel must not touch.
    private static void Run(int rowBytes, Action<Span<byte>> kernel, byte[] expected, string because)
    {
        var buffer = Enumerable.Repeat((byte)0xA5, rowBytes + 64).ToArray();
        kernel(buffer.AsSpan(0, rowBytes));
        buffer.AsSpan(0, rowBytes).ToArray().ShouldBe(expected, because);
        buffer.AsSpan(rowBytes).ToArray().ShouldAllBe(b => b == 0xA5, because);
    }

    [Fact]
    public void Expand24_widens_three_bytes_to_four_on_every_tier()
    {
        foreach (var width in Widths)
        foreach (var swap in new[] { false, true })
        {
            var source = Random(3 * width, width);
            var expected = new byte[4 * width];
            for (var x = 0; x < width; x++)
            {
                expected[4 * x] = source[3 * x + (swap ? 2 : 0)];
                expected[4 * x + 1] = source[3 * x + 1];
                expected[4 * x + 2] = source[3 * x + (swap ? 0 : 2)];
                expected[4 * x + 3] = 255;
            }
            foreach (var tier in Tiers())
                Run(4 * width, d => BmpKernels.Expand24(source, d, width, swap, tier), expected, $"width {width}, swap {swap}, tier {tier}");
        }
    }

    [Fact]
    public void Shuffle32_rearranges_and_reports_every_bit_seen_on_every_tier()
    {
        byte[][] controls = [[2, 1, 0, 3], [0, 1, 2, 0x80], [3, 2, 1, 0], [1, 0x80, 3, 2]];
        foreach (var width in Widths)
        foreach (var control in controls)
        foreach (var set in new[] { 0u, 0xFF000000u })
        {
            var source = Random(4 * width, width + control[0]);
            var expected = new byte[4 * width];
            uint seen = 0;
            for (var x = 0; x < width; x++)
            {
                uint pixel = set;
                for (var k = 0; k < 4; k++)
                {
                    if (control[k] < 4) pixel |= (uint)source[4 * x + control[k]] << (8 * k);
                }
                seen |= pixel;
                BitConverter.GetBytes(pixel).CopyTo(expected, 4 * x);
            }
            foreach (var tier in Tiers())
            {
                uint reported = 0;
                Run(4 * width, d => reported = BmpKernels.Shuffle32(source, d, width, control, set, tier), expected,
                    $"width {width}, control {string.Join(',', control)}, tier {tier}");
                reported.ShouldBe(seen, $"width {width}, tier {tier}");
            }
        }
    }

    [Fact]
    public void Pack32To24_drops_a_byte_on_every_tier()
    {
        foreach (var width in Widths)
        foreach (var control in new byte[][] { [2, 1, 0], [0, 1, 2], [1, 3, 0] })
        {
            var source = Random(4 * width, width * 3 + control[0]);
            var expected = new byte[3 * width];
            for (var x = 0; x < width; x++)
            {
                for (var k = 0; k < 3; k++) expected[3 * x + k] = source[4 * x + control[k]];
            }
            foreach (var tier in Tiers())
                Run(3 * width, d => BmpKernels.Pack32To24(source, d, width, control, tier), expected, $"width {width}, tier {tier}");
        }
    }

    [Fact]
    public void Swap24_exchanges_the_outer_bytes_on_every_tier()
    {
        foreach (var width in Widths)
        {
            var source = Random(3 * width, width * 7);
            var expected = new byte[3 * width];
            for (var x = 0; x < width; x++)
                (expected[3 * x], expected[3 * x + 1], expected[3 * x + 2]) = (source[3 * x + 2], source[3 * x + 1], source[3 * x]);
            foreach (var tier in Tiers())
                Run(3 * width, d => BmpKernels.Swap24(source, d, width, tier), expected, $"width {width}, tier {tier}");
        }
    }

    [Fact]
    public void Expand16_widens_565_and_555_on_every_tier()
    {
        foreach (var width in Widths)
        foreach (var green6 in new[] { false, true })
        foreach (var rgba in new[] { false, true })
        {
            var source = Random(2 * width, width * 11);
            var expected = new byte[4 * width];
            for (var x = 0; x < width; x++)
            {
                var p = (uint)(source[2 * x] | source[2 * x + 1] << 8);
                var (rm, gm) = green6 ? (0xF800u, 0x07E0u) : (0x7C00u, 0x03E0u);
                byte r = BmpDecoderTests.Field(p, rm), g = BmpDecoderTests.Field(p, gm), b = BmpDecoderTests.Field(p, 0x1F);
                (expected[4 * x], expected[4 * x + 1], expected[4 * x + 2], expected[4 * x + 3]) = (rgba ? r : b, g, rgba ? b : r, 255);
            }
            foreach (var tier in Tiers())
                Run(4 * width, d => BmpKernels.Expand16(source, d, width, green6, rgba, tier), expected,
                    $"width {width}, 565 {green6}, rgba {rgba}, tier {tier}");
        }
    }

    [Fact]
    public void Expand16_covers_every_16_bit_value()
    {
        var source = new byte[2 * 65536];
        for (var v = 0; v < 65536; v++) (source[2 * v], source[2 * v + 1]) = ((byte)v, (byte)(v >> 8));
        foreach (var green6 in new[] { false, true })
        {
            var scalar = new byte[4 * 65536];
            BmpKernels.Expand16(source, scalar, 65536, green6, rgba: true, BmpKernels.Scalar);
            foreach (var tier in Tiers())
            {
                var vector = new byte[4 * 65536];
                BmpKernels.Expand16(source, vector, 65536, green6, rgba: true, tier);
                vector.ShouldBe(scalar, $"565 {green6}, tier {tier}");
            }
        }
    }

    [Fact]
    public void The_widening_rule_is_bit_repetition()
    {
        // 5 and 6 bits: what WIC, ImageMagick and ImageSharp all give; 1, 2 and 4 bits: exact multiples.
        Sdcb.SimdCodecs.Bmp.BmpPixels.Widen(1, 1).ShouldBe((byte)255);
        Sdcb.SimdCodecs.Bmp.BmpPixels.Widen(7, 3).ShouldBe((byte)255);
        Sdcb.SimdCodecs.Bmp.BmpPixels.Widen(0b10110, 5).ShouldBe((byte)0b10110101);
        Sdcb.SimdCodecs.Bmp.BmpPixels.Widen(0b101101, 6).ShouldBe((byte)0b10110110);
        for (var bits = 1; bits < 8; bits++)
        for (var v = 0; v < 1 << bits; v++)
            Sdcb.SimdCodecs.Bmp.BmpPixels.Widen(v, bits).ShouldBe(BmpDecoderTests.Widen((uint)v, bits), $"{v} in {bits} bits");
    }
}
