using Sdcb.SimdCodecs.Ico;
using Shouldly;
using W = Sdcb.SimdCodecs.Tests.TestBmpWriter;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Icons and cursors against WIC (<see cref="WicOracle"/>): every image of a corpus of hand-built DIB icons (each
/// depth, with AND masks, a 32-bit image with alpha and one without), PNG images and every encoder layout decodes
/// to exactly WIC's pixels, frame for frame. Where a pixel is transparent only alpha is compared.
/// </summary>
public sealed class IcoWicTests
{
    private static Dictionary<string, byte[]> Corpus()
    {
        var corpus = new Dictionary<string, byte[]>();
        var random = new Random(0x1c0);
        foreach (var bpp in new[] { 1, 4, 8, 16, 24, 32 })
        foreach (var size in new[] { 16, 17, 32, 48 })
        foreach (var alpha in bpp == 32 ? new[] { false, true } : [false])
        {
            var palette = bpp <= 8 ? BmpDecoderTests.RandomPalette(random, 1 << bpp) : [];
            var values = Enumerable.Range(0, size * size).Select(_ => (uint)random.NextInt64(1L << Math.Min(bpp, 24))).ToArray();
            var dib = W.Concat(W.InfoHeader(40, size, 2 * size, bpp, 0), W.Palette(palette),
                W.Rows(size, size, bpp, (x, y) => values[y * size + x] | (alpha ? (uint)(x * 255 / size) << 24 : 0)),
                W.Rows(size, size, 1, (x, y) => (x * 7 + y * 3) % 5 == 0 ? 1u : 0u));
            corpus[$"DIB {bpp}-bit {size} alpha {alpha}"] = IcoTests.Directory(1, (dib, size, size, 1, bpp));
        }
        foreach (var encoding in Enum.GetValues<IcoImageEncoding>())
        {
            var rgba = new byte[40 * 40 * 4];
            random.NextBytes(rgba);
            var big = new byte[256 * 256 * 4];
            random.NextBytes(big);
            corpus[$"encoded {encoding}"] = IcoEncoder.Encode([new IcoFrame(rgba, 40, 40, Encoding: encoding), new IcoFrame(big, 256, 256)]);
        }
        var cursor = new byte[32 * 32 * 4];
        random.NextBytes(cursor);
        corpus["cursor"] = IcoEncoder.EncodeCursor([new IcoFrame(cursor, 32, 32, HotspotX: 9, HotspotY: 4)]);
        return corpus;
    }

    [Fact]
    public void Every_image_decodes_to_wics_pixels()
    {
        WicOracle.RequireOrSkip();
        var corpus = Corpus();
        var wic = WicOracle.Decode(corpus);
        var failures = new List<string>();
        foreach (var (name, data) in corpus)
        {
            var result = wic[name];
            if (result.Error is not null)
            {
                failures.Add($"{name}: WIC refused it: {result.Error.Split('\n')[0]}");
                continue;
            }
            var info = IcoDecoder.ReadInfo(data);
            if (result.Frames.Count != info.Entries.Count)
            {
                failures.Add($"{name}: WIC has {result.Frames.Count} frames, ours {info.Entries.Count}");
                continue;
            }
            for (var i = 0; i < info.Entries.Count; i++)
            {
                var ours = IcoDecoder.Decode(data, i).Rgba;
                var (width, height, theirs) = result.Frames[i];
                if ((width, height) != (info.Entries[i].Width, info.Entries[i].Height))
                {
                    failures.Add($"{name}, image {i}: WIC is {width}x{height}");
                    continue;
                }
                for (var p = 0; p < ours.Length; p += 4)
                {
                    if (ours[p + 3] == 0 && theirs[p + 3] == 0) continue;
                    if (ours.AsSpan(p, 4).SequenceEqual(theirs.AsSpan(p, 4))) continue;
                    failures.Add($"{name}, image {i}: pixel {p / 4} is {Convert.ToHexString(ours, p, 4)}, WIC has {Convert.ToHexString(theirs, p, 4)}");
                    break;
                }
            }
        }
        failures.ShouldBeEmpty();
    }
}
