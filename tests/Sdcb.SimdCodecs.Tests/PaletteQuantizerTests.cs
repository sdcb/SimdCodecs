using Shouldly;
using Quantizer = Sdcb.SimdCodecs.Gif.PaletteQuantizer;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The shared palette quantizer (<c>src/Shared/PaletteQuantizer.cs</c>): the pruned nearest-entry search finds exactly
/// what testing every entry finds (the nearest, the lowest index among equals), and the palette it builds keeps what
/// it must keep.
/// </summary>
public sealed class PaletteQuantizerTests
{
    public static TheoryData<string, bool> Palettes() => new()
    {
        { "random 256", false }, { "random 17", false }, { "duplicates", false }, { "clustered", false }, { "greys", false }, { "corners", false },
        { "random 256", true }, { "random 17", true }, { "duplicates", true }, { "clustered", true }, { "translucent", true },
    };

    private static int[] Palette(string kind, Random random) => kind switch
    {
        "random 256" => [.. Enumerable.Range(0, 256).Select(_ => random.Next(1 << 24) | 0xFF << 24)],
        "random 17" => [.. Enumerable.Range(0, 17).Select(_ => random.Next(1 << 24) | 0xFF << 24)],
        "duplicates" => [.. Enumerable.Range(0, 200).Select(i => (i % 23) * 0x0A0B0C | 0xFF << 24)],
        "clustered" => [.. Enumerable.Range(0, 256).Select(_ => 0x808080 + random.Next(-8, 9) * 0x010000 + random.Next(-8, 9) * 0x100 + random.Next(-8, 9) | 0xFF << 24)],
        "greys" => [.. Enumerable.Range(0, 64).Select(i => i * 4 * 0x010101 | 0xFF << 24)],
        "translucent" => [0, .. Enumerable.Range(0, 255).Select(_ => random.Next())],
        _ => [.. new[] { 0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0x00FFFF, 0xFF00FF, 0x0F0F0F, 0xF0F0F0, 0x10FF10, 0x7F7F7F, 0x808080, 0x0F1011, 0x101010, 0xEFEFEF, 0x000001 }.Select(c => c | 0xFF << 24)],
    };

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Pruned_search_finds_what_the_full_search_finds(string kind, bool alpha)
    {
        var random = new Random(kind.Length * 977 + (alpha ? 1 : 0));
        var palette = Palette(kind, random);
        if (!alpha) palette = [.. palette.Select(p => p & 0xFFFFFF)];
        var mapper = new Quantizer.Mapper(palette, alpha);
        var hinted = new Quantizer.Mapper(palette, alpha);
        var colours = Enumerable.Range(0, 100_000).Select(_ => alpha ? random.Next() * 2 + random.Next(2) : random.Next(1 << 24))
            .Concat(palette)
            .Concat(from r in new[] { 0, 15, 16, 127, 128, 255 }
                    from g in new[] { 0, 31, 32, 255 }
                    from b in new[] { 0, 1, 255 }
                    from a in new[] { 0, 1, 128, 254, 255 }
                    select a << 24 | r << 16 | g << 8 | b);
        foreach (var argb in colours)
        {
            var full = mapper.NearestFull(argb);
            mapper.Nearest(argb).ShouldBe(full, $"{kind}: 0x{argb:X8}");
            // A hint (any entry) only tightens the bound early; it never changes the answer.
            var hint = random.Next(palette.Length);
            hinted.Nearest(argb, hint).ShouldBe(full, $"{kind} (hint {hint}): 0x{argb:X8}");
            mapper.NearestScalar(argb).ShouldBe(full, $"{kind} (scalar): 0x{argb:X8}");
        }
    }

    [Fact]
    public void Batched_coordinates_equal_one_at_a_time()
    {
        // The vector path converts four colours at a time; it must give the scalar path's bits exactly.
        var random = new Random(3);
        int[] colours = [.. Enumerable.Range(0, 100_003).Select(_ => random.Next() * 2 + random.Next(2)),
            0, -1, 0x01000000, unchecked((int)0xFF000000), 0x00FFFFFF, unchecked((int)0xFF010101), unchecked((int)0x80FEFEFE)];
        foreach (var perceptual in new[] { true, false })
        {
            var space = new Quantizer.Space(perceptual);
            var batched = new float[colours.Length * 4];
            space.Features(colours, batched);
            var single = new float[4];
            for (var i = 0; i < colours.Length; i++)
            {
                space.Features(colours[i], single);
                for (var c = 0; c < 4; c++)
                    BitConverter.SingleToInt32Bits(batched[i * 4 + c]).ShouldBe(BitConverter.SingleToInt32Bits(single[c]), $"0x{colours[i]:X8}[{c}]");
            }
        }
    }

    [Fact]
    public void Few_colours_are_kept_exactly()
    {
        var quantizer = new Quantizer(alpha: true);
        int[] colours = [0, 0x10203040, 0x7FFF0000, unchecked((int)0xFF000001), unchecked((int)0xFFFFFFFF)];
        foreach (var c in colours)
        {
            for (var i = 0; i < 10; i++) quantizer.Add(c);
        }
        // Every fully transparent colour is one.
        quantizer.Add(0x00123456);
        quantizer.BuildPalette(256).Order().ShouldBe(colours.Order());
        quantizer.MeanSquaredError.ShouldBe(0);
    }

    [Fact]
    public void An_opaque_quantizer_ignores_alpha_and_returns_rgb()
    {
        var quantizer = new Quantizer();
        quantizer.Add(0x00112233);
        quantizer.Add(unchecked((int)0x80112233));
        quantizer.Add(0x00445566);
        quantizer.BuildPalette(256).Order().ShouldBe([0x112233, 0x445566]);
    }

    [Fact]
    public void Many_colours_stay_within_the_limit_and_the_histogram_bound()
    {
        // 2^20 distinct colours: the histogram must coarsen, the palette must still fit and be near the source.
        var random = new Random(5);
        var quantizer = new Quantizer(alpha: true);
        var pixels = Enumerable.Range(0, 1 << 20).Select(_ => random.Next() | 0x40 << 24).ToArray();
        foreach (var p in pixels) quantizer.Add(p);
        var palette = quantizer.BuildPalette(256);
        palette.Length.ShouldBeLessThanOrEqualTo(256);
        palette.Distinct().Count().ShouldBe(palette.Length);
        quantizer.MeanSquaredError.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_dark_image_keeps_its_shades()
    {
        // 224 dark shades, each a few levels apart: the old 5-bit grid merged them into a few dozen entries.
        var quantizer = new Quantizer();
        var shades = Enumerable.Range(0, 224).Select(i => (i % 16) << 16 | (i / 16) << 8 | (i % 7)).ToArray();
        foreach (var s in shades) quantizer.Add(s);
        quantizer.BuildPalette(256).Order().ShouldBe(shades.Distinct().Order());
    }

    [Fact]
    public void Output_is_deterministic()
    {
        int[] Build()
        {
            var random = new Random(11);
            var q = new Quantizer(alpha: true);
            for (var i = 0; i < 300_000; i++) q.Add(random.Next());
            return q.BuildPalette(64);
        }
        Build().ShouldBe(Build());
    }
}
