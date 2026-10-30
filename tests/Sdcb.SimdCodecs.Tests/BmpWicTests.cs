using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Shouldly;
using W = Sdcb.SimdCodecs.Tests.TestBmpWriter;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// BMP decoding against WIC, Windows' own decoder (<see cref="WicOracle"/>): a corpus of every header, depth,
/// mask and compression, hand-assembled and from <see cref="BmpEncoder"/>, decodes to exactly WIC's pixels.
/// <para>
/// The exceptions, each a decision recorded in the package README:
/// <list type="bullet">
/// <item>Alpha WIC ignores: a 32-bit <c>BI_RGB</c> pixel's fourth byte, a 16-bit alpha mask, and any alpha mask
///   but the 0xFF000000 of a B, G, R, A layout. This decoder reads them, as ImageMagick and ImageSharp do; RGB is
///   compared, not alpha.</item>
/// <item>What WIC does not read: OS/2 2.x headers, 2-bit pixels, RLE24, <c>BI_PNG</c>. It must refuse them, so a
///   variant it starts to read shows up here.</item>
/// <item>Channels narrower than 4 bits, which WIC widens by writing the value twice (a 3-bit 7 is 0xFC), where
///   this decoder repeats it to fill the byte (0xFF, the scaling that keeps white white). Left out of the corpus;
///   <see cref="BmpDecoderTests"/> pins them.</item>
/// </list>
/// </para>
/// </summary>
public sealed class BmpWicTests
{
    /// <summary>What a corpus entry tells WIC apart from us: whether its alpha is one WIC ignores, and whether
    /// WIC reads the variant at all.</summary>
    private sealed record Entry(byte[] Bmp, bool WicIgnoresAlpha, bool WicRefuses);

    private static Dictionary<string, Entry> Corpus()
    {
        var corpus = new Dictionary<string, Entry>();
        var random = new Random(20261030);
        foreach (var size in new[] { 12, 16, 40, 64, 108, 124 })
        foreach (var bpp in new[] { 1, 2, 4, 8 })
        foreach (var topDown in new[] { false, true })
        foreach (var width in new[] { 1, 7, 33 })
        {
            if (size == 12 && (bpp == 2 || topDown)) continue;
            const int height = 3;
            var palette = BmpDecoderTests.RandomPalette(random, 1 << bpp);
            var header = size == 12 ? W.CoreHeader(width, height, bpp) : W.InfoHeader(size, width, topDown ? -height : height, bpp, 0);
            var bmp = W.File(header, W.Palette(palette, size == 12 ? 3 : 4),
                W.Rows(width, height, bpp, (_, _) => (uint)random.Next(1 << bpp), topDown));
            corpus[$"palette h{size} {bpp}-bit top-down {topDown} w{width}"] = new(bmp, false, WicRefuses: size is 16 or 64 || bpp == 2);
        }

        foreach (var (name, size, compression, bpp, r, g, b, a) in new (string, int, uint, int, uint, uint, uint, uint)[]
        {
            ("555", 40, 0, 16, 0x7C00, 0x03E0, 0x001F, 0),
            ("565", 40, 3, 16, 0xF800, 0x07E0, 0x001F, 0),
            ("565 V4", 108, 3, 16, 0xF800, 0x07E0, 0x001F, 0),
            ("1555", 56, 3, 16, 0x7C00, 0x03E0, 0x001F, 0x8000),
            ("4444", 108, 3, 16, 0x0F00, 0x00F0, 0x000F, 0xF000),
            ("24", 40, 0, 24, 0, 0, 0, 0),
            ("24 core", 12, 0, 24, 0, 0, 0, 0),
            ("BGRX", 40, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
            ("BGRA V4", 108, 3, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
            ("RGBA V5", 124, 3, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000),
            ("1010102", 108, 3, 32, 0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000),
            ("12-bit", 108, 3, 32, 0x00000FFF, 0x00FFF000, 0xFF000000, 0),
        })
        foreach (var topDown in new[] { false, true })
        foreach (var width in new[] { 1, 9, 33 })
        {
            if (size == 12 && topDown) continue;
            const int height = 3;
            var masksInside = size >= 52;
            var header = size == 12 ? W.CoreHeader(width, height, bpp)
                : W.InfoHeader(size, width, topDown ? -height : height, bpp, compression,
                    red: masksInside ? r : 0, green: masksInside ? g : 0, blue: masksInside ? b : 0, alpha: size >= 56 ? a : 0);
            var after = compression == 3 && !masksInside ? W.Masks(r, g, b) : [];
            var bmp = W.File(header, after, W.Rows(width, height, bpp, (_, _) => (uint)random.NextInt64(1L << bpp), topDown));
            // WIC ignores a 32-bit BI_RGB pixel's fourth byte, a 16-bit alpha mask, and alpha in any layout but
            // B, G, R, A.
            var ignoresAlpha = (bpp == 32 && compression == 0)
                || (a != 0 && (bpp == 16 || (r, g, b, a) != (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000)));
            corpus[$"{name} top-down {topDown} w{width}"] = new(bmp, ignoresAlpha, false);
        }

        var pal8 = BmpDecoderTests.RandomPalette(random, 7);
        var rle8 = new W.Rle(9, 6, 8, pal8)
            .Run(3, 1).Literal(2, 3, 4).Run(4, 5).EndOfLine()
            .Literal(6, 5, 4, 3, 2).Delta(2, 1).Run(2, 6).EndOfLine()
            .Run(200, 2).EndOfLine()
            .Delta(4, 1).Literal(9, 1, 2).EndOfBitmap();
        corpus["RLE8"] = new(W.File(W.InfoHeader(40, 9, 6, 8, 1, colorsUsed: 7), W.Palette(pal8), rle8.Stream), false, false);
        var pal4 = BmpDecoderTests.RandomPalette(random, 16);
        var rle4 = new W.Rle(11, 4, 4, pal4)
            .Run(5, 0x3A).Literal(1, 2, 3, 4, 5).EndOfLine()
            .Literal(15, 14, 13).Run(7, 0x77).Run(3, 0x12).EndOfLine()
            .Delta(3, 1).Run(2, 0x4F).Literal(6, 7, 8, 9).EndOfBitmap();
        corpus["RLE4"] = new(W.File(W.InfoHeader(40, 11, 4, 4, 2), W.Palette(pal4), rle4.Stream), false, false);
        var rle24 = new W.Rle(7, 3, 24, []).Run(4, 0x123456).Literal(0xFF0000, 0x00FF00, 0x0000FF).EndOfBitmap();
        corpus["RLE24"] = new(W.File(W.InfoHeader(64, 7, 3, 24, 4), [], rle24.Stream), false, true);

        var png = Png.PngWriter.Encode(Enumerable.Range(0, 13 * 6 * 4).Select(i => (byte)(i * 7)).ToArray(), 13, 6);
        corpus["BI_PNG"] = new(W.File(W.InfoHeader(40, 13, 6, 0, 5), [], png), false, true);

        foreach (var format in Enum.GetValues<BmpEncodeFormat>())
        foreach (var header in new[] { BmpHeaderVersion.Core, BmpHeaderVersion.Info, BmpHeaderVersion.V4, BmpHeaderVersion.V5 })
        foreach (var rle in new[] { false, true })
        {
            var palette = format is BmpEncodeFormat.Indexed1 or BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8;
            if (header == BmpHeaderVersion.Core && (rle || !(palette || format == BmpEncodeFormat.Rgb24))) continue;
            if (rle && format is not (BmpEncodeFormat.Indexed4 or BmpEncodeFormat.Indexed8)) continue;
            const int width = 37, height = 5;
            var rgba = new byte[width * height * 4];
            random.NextBytes(rgba);
            var bmp = BmpEncoder.Encode(rgba, width, height, ImagePixelFormat.Rgba32,
                new BmpEncodeOptions { Format = format, Header = header, Rle = rle, Dither = true });
            var argbAsBiRgb = format == BmpEncodeFormat.Argb32 && header <= BmpHeaderVersion.Info
                || format == BmpEncodeFormat.Auto && header == BmpHeaderVersion.Info;
            corpus[$"encoded {format} {header} RLE {rle}"] = new(bmp, argbAsBiRgb, false);
        }
        return corpus;
    }

    [Fact]
    public void The_corpus_decodes_to_wics_pixels()
    {
        WicOracle.RequireOrSkip();
        var corpus = Corpus();
        var wic = WicOracle.Decode(corpus.ToDictionary(e => e.Key, e => e.Value.Bmp));
        var failures = new List<string>();
        foreach (var (name, entry) in corpus)
        {
            var result = wic[name];
            if (result.Error is not null)
            {
                if (!entry.WicRefuses) failures.Add($"{name}: WIC refused it ({Hresult(result.Error)})");
                continue;
            }
            if (entry.WicRefuses) failures.Add($"{name}: WIC decoded a variant it was expected to refuse");
            var image = BmpDecoder.Decode(entry.Bmp);
            var ours = new byte[image.Width * image.Height * 4];
            BmpDecoder.DecodeTo(entry.Bmp, ours);
            var (width, height, theirs) = result.Frames[0];
            if ((width, height) != (image.Width, image.Height))
            {
                failures.Add($"{name}: WIC is {width}x{height}, ours {image.Width}x{image.Height}");
                continue;
            }
            for (var i = 0; i < ours.Length; i += 4)
            {
                var same = entry.WicIgnoresAlpha
                    ? ours.AsSpan(i, 3).SequenceEqual(theirs.AsSpan(i, 3))
                    : ours.AsSpan(i, 4).SequenceEqual(theirs.AsSpan(i, 4));
                if (same) continue;
                failures.Add($"{name}: pixel {i / 4} is {Convert.ToHexString(ours, i, 4)}, WIC has {Convert.ToHexString(theirs, i, 4)}");
                break;
            }
        }
        failures.ShouldBeEmpty();
    }

    private static string Hresult(string error)
    {
        var at = error.IndexOf("HRESULT: ", StringComparison.Ordinal);
        return at < 0 ? error.Split('\n')[0] : error.Substring(at + 9, 10);
    }
}
