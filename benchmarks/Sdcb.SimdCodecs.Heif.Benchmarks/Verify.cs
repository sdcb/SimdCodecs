using Sdcb.SimdCodecs.Heif;
namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// The correctness gate: nothing is timed until every contestant agrees with us. Runs once per
/// process, before any benchmark case.
/// <para>
/// What is a contract and what is not matters here:
/// </para>
/// <list type="bullet">
/// <item><b>HEVC stage</b> — H.265 defines the samples, so our per-tile planes must equal
/// libde265's, bit for bit. This is the byte-exact contract of AGENTS.md, checked against a second
/// black-box decoder as well as the pinned ffmpeg oracle in the test suite.</item>
/// <item><b>Grid composition</b> — our canvas must equal the same canvas built by pasting
/// libde265's tiles at the grid positions. That validates tile order and placement independently of
/// the decoder.</item>
/// <item><b>RGBA</b> — the colour conversion is defined in-repo and each library rounds its own way,
/// so this is <i>informational only</i> (reported, never fatal). The test suite owns the real RGBA
/// check against Magick at ±2. libheif's own YCbCr output is not the decoder's raw planes either —
/// it passes them through its colour handling (the phone images carry a Display P3 ICC profile),
/// so it gets a separate informational row instead of being treated as ground truth.</item>
/// </list>
/// </summary>
internal static class Verify
{
    public sealed record Result(string Fixture, string Check, bool Contract, long DifferingSamples, int MaxDelta)
    {
        public bool Passed => !Contract || DifferingSamples == 0;
    }

    public static IReadOnlyList<Result> Run() => Run(HeifInputs.Fixtures);

    public static IReadOnlyList<Result> Run(IReadOnlyList<string> fixtures)
    {
        NativeHeif.SelfCheck();
        NativeDe265.SelfCheck();
        var results = new List<Result>();
        foreach (var fixture in fixtures)
        {
            var data = HeifInputs.Load(fixture);
            var tiles = Tiles.Extract(data);
            var container = HeifContainer.Parse(data);
            var ours = HeifDecoder.DecodeYuv(data);

            // Contract: the HEVC stage, tile by tile, against libde265.
            long tileDiffs = 0;
            int tileMax = 0;
            foreach (var tile in tiles)
            {
                var oursTile = Tiles.Decode(tile);
                var theirs = NativeContestants.DecodeTile(tile);
                ComparePlanes(oursTile.Planes[0], oursTile.PlaneWidth(0), oursTile.CropLeft, oursTile.CropTop,
                    theirs.Y, theirs.Width, 0, 0,
                    Math.Min(oursTile.OutputWidth, theirs.Width), Math.Min(oursTile.OutputHeight, theirs.Height),
                    ref tileDiffs, ref tileMax);
            }
            results.Add(new(fixture, "HEVC stage vs libde265 (per tile)", true, tileDiffs, tileMax));

            // Contract: grid composition — our canvas against the same canvas built from libde265 tiles.
            var composed = Compose(tiles, container, ours.Width, ours.Height);
            long composeDiffs = 0;
            int composeMax = 0;
            ComparePlanes(ours.Y, ours.Width, 0, 0, composed, ours.Width, 0, 0, ours.Width, ours.Height, ref composeDiffs, ref composeMax);
            results.Add(new(fixture, "grid composition vs libde265 tiles", true, composeDiffs, composeMax));

            // Informational: libheif's YCbCr output (its own colour handling) against ours.
            var heifYuv = NativeContestants.DecodeYuv(data, int.MaxValue);
            long yuvDiffs = 0;
            int yuvMax = 0;
            ComparePlanes(ours.Y, ours.Width, 0, 0, heifYuv.Y, heifYuv.Width, 0, 0,
                Math.Min(ours.Width, heifYuv.Width), Math.Min(ours.Height, heifYuv.Height), ref yuvDiffs, ref yuvMax);
            results.Add(new(fixture, "planes vs libheif YCbCr (info)", false, yuvDiffs, yuvMax));

            // Informational: RGBA against libheif's interleaved output.
            using var image = HeifDecoder.Decode(data);
            var ourRgba = image.Rgba.ToArray();
            var heifRgba = NativeContestants.DecodeRgba(data, int.MaxValue);
            long rgbaDiffs = 0;
            int rgbaMax = 0;
            int n = Math.Min(ourRgba.Length, heifRgba.Length);
            for (var i = 0; i + 3 < n; i += 4)
            {
                for (var c = 0; c < 4; c++)
                {
                    int delta = Math.Abs(ourRgba[i + c] - heifRgba[i + c]);
                    if (delta > 2) rgbaDiffs++;
                    if (delta > rgbaMax) rgbaMax = delta;
                }
            }
            results.Add(new(fixture, "RGBA vs libheif, beyond ±2 (info)", false, rgbaDiffs, rgbaMax));
        }
        return results;
    }

    /// <summary>Pastes libde265's tiles at the grid positions, the way <c>DecodeYuv</c> pastes ours.</summary>
    private static ushort[] Compose(Tile[] tiles, HeifContainer container, int width, int height)
    {
        var first = Tiles.Decode(tiles[0]);
        int tileW = first.OutputWidth, tileH = first.OutputHeight;
        var canvas = new ushort[(long)width * height];
        for (var i = 0; i < tiles.Length; i++)
        {
            var p = NativeContestants.DecodeTile(tiles[i]);
            int x0 = i % container.GridColumns * tileW, y0 = i / container.GridColumns * tileH;
            if (x0 >= width || y0 >= height) continue;
            int copyW = Math.Min(tileW, width - x0), copyH = Math.Min(tileH, height - y0);
            for (var y = 0; y < copyH; y++)
            {
                for (var x = 0; x < copyW; x++) canvas[(long)(y0 + y) * width + x0 + x] = p.Y[y * p.Width + x];
            }
        }
        return canvas;
    }

    private static void ComparePlanes(ushort[] ours, int oursStride, int oursX, int oursY,
        ushort[] theirs, int theirsStride, int theirsX, int theirsY, int w, int h, ref long diffs, ref int max)
    {
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int delta = Math.Abs(ours[(long)(oursY + y) * oursStride + oursX + x] - theirs[(long)(theirsY + y) * theirsStride + theirsX + x]);
                if (delta != 0) diffs++;
                if (delta > max) max = delta;
            }
        }
    }

    /// <summary>Prints the table; returns the process exit code (0 when every contract passed).</summary>
    public static int Print(IReadOnlyList<Result> results)
    {
        Console.WriteLine($"libheif {NativeHeif.Version}, libde265 {NativeDe265.Version}");
        Console.WriteLine($"{"fixture",-42} {"check",-40} {"kind",-8} {"diff",12} {"max",5}");
        var failed = 0;
        foreach (var r in results)
        {
            Console.WriteLine($"{r.Fixture,-42} {r.Check,-40} {(r.Contract ? "contract" : "info"),-8} {r.DifferingSamples,12} {r.MaxDelta,5} {(r.Passed ? "" : "FAIL")}");
            if (!r.Passed) failed++;
        }
        Console.WriteLine(failed == 0 ? "all contracts passed" : $"{failed} contract(s) FAILED");
        return failed == 0 ? 0 : 1;
    }
}
