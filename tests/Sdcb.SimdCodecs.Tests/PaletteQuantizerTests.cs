using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The shared palette mapper's cell lists (<c>PaletteQuantizer.Mapper.SearchCell</c>) find exactly the entry the full
/// search finds — the nearest, the lowest index among equals — for random palettes, palettes with duplicate and
/// clustered entries, and colours on every cell boundary. GIF's output depends on it staying identical.
/// </summary>
public sealed class PaletteQuantizerTests
{
    public static TheoryData<string> Palettes() => ["random 256", "random 17", "duplicates", "clustered", "greys", "corners"];

    private static int[] Palette(string kind, Random random) => kind switch
    {
        "random 256" => [.. Enumerable.Range(0, 256).Select(_ => random.Next(1 << 24))],
        "random 17" => [.. Enumerable.Range(0, 17).Select(_ => random.Next(1 << 24))],
        "duplicates" => [.. Enumerable.Range(0, 200).Select(i => (i % 23) * 0x0A0B0C)],
        "clustered" => [.. Enumerable.Range(0, 256).Select(_ => 0x808080 + random.Next(-8, 9) * 0x010000 + random.Next(-8, 9) * 0x100 + random.Next(-8, 9))],
        "greys" => [.. Enumerable.Range(0, 64).Select(i => i * 4 * 0x010101)],
        _ => [0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0x00FFFF, 0xFF00FF, 0x0F0F0F, 0xF0F0F0, 0x10FF10, 0x7F7F7F, 0x808080, 0x0F1011, 0x101010, 0xEFEFEF, 0x000001],
    };

    [Theory]
    [MemberData(nameof(Palettes))]
    public void Cell_lists_find_what_the_full_search_finds(string kind)
    {
        var random = new Random(kind.Length * 977);
        var palette = Palette(kind, random);
        var gif = new Sdcb.SimdCodecs.Gif.PaletteQuantizer.Mapper(palette);
        gif.BuildCells();
        var colours = Enumerable.Range(0, 200_000).Select(_ => random.Next(1 << 24))
            .Concat(from r in new[] { 0, 15, 16, 17, 127, 128, 239, 240, 255 }
                    from g in new[] { 0, 15, 16, 31, 32, 128, 255 }
                    from b in new[] { 0, 1, 15, 16, 255 }
                    select r << 16 | g << 8 | b);
        foreach (var rgb in colours)
            gif.SearchCell(rgb).ShouldBe(gif.Search(rgb), $"{kind}: 0x{rgb:X6}");
    }
}
