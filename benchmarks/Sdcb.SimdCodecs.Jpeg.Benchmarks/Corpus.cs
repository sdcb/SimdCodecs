using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>One line of <c>corpus/manifest.tsv</c>.</summary>
internal sealed record ManifestEntry(
    string Name, string Category, string License, int Width, int Height, string Sha256, string Url, string Page)
{
    public bool IsCameraOriginal => Category == "camera";

    public static IReadOnlyList<ManifestEntry> Load()
    {
        var list = new List<ManifestEntry>();
        foreach (var line in File.ReadLines(BenchPaths.Manifest))
        {
            if (line.Length == 0 || line[0] == '#' || line.StartsWith("name\t", StringComparison.Ordinal)) continue;
            var f = line.Split('\t');
            list.Add(new ManifestEntry(f[0], f[1], f[2], int.Parse(f[3], CultureInfo.InvariantCulture),
                int.Parse(f[4], CultureInfo.InvariantCulture), f[5], f[6], f[7]));
        }

        return list;
    }
}

/// <summary>One encoded test file in the matrix.</summary>
internal sealed record MatrixCase(
    string Id, string Group, string Image, string Tier, string Sampling, int Quality, string Variant,
    int Width, int Height, long Bytes)
{
    public string Path => System.IO.Path.Combine(BenchPaths.Matrix, Id.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".jpg");
    public double Megapixels => Width * (double)Height / 1e6;

    private const string IndexName = "index.tsv";

    public static IReadOnlyList<MatrixCase> LoadIndex()
    {
        var file = System.IO.Path.Combine(BenchPaths.Matrix, IndexName);
        if (!File.Exists(file))
            throw new FileNotFoundException("Test matrix not prepared. Run the 'prepare' command first.", file);
        var list = new List<MatrixCase>();
        foreach (var line in File.ReadLines(file).Skip(1))
        {
            var f = line.Split('\t');
            list.Add(new MatrixCase(f[0], f[1], f[2], f[3], f[4], int.Parse(f[5], CultureInfo.InvariantCulture), f[6],
                int.Parse(f[7], CultureInfo.InvariantCulture), int.Parse(f[8], CultureInfo.InvariantCulture),
                long.Parse(f[9], CultureInfo.InvariantCulture)));
        }

        return list;
    }

    public static void WriteIndex(IEnumerable<MatrixCase> cases)
    {
        var file = System.IO.Path.Combine(BenchPaths.Matrix, IndexName);
        using var w = new StreamWriter(file);
        w.WriteLine("id\tgroup\timage\ttier\tsampling\tquality\tvariant\twidth\theight\tbytes");
        foreach (var c in cases)
            w.WriteLine(string.Join('\t', c.Id, c.Group, c.Image, c.Tier, c.Sampling,
                c.Quality.ToString(CultureInfo.InvariantCulture), c.Variant, c.Width.ToString(CultureInfo.InvariantCulture),
                c.Height.ToString(CultureInfo.InvariantCulture), c.Bytes.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>
/// Builds the test corpus: original → Lanczos-downscaled lossless PNG master (at most
/// half size, which washes out the original's own JPEG block artefacts) → centred
/// crops at three sizes → libjpeg-turbo-encoded matrix. Camera originals are used
/// byte-for-byte as shot.
/// </summary>
internal static class CorpusBuilder
{
    /// <summary>Master target: big enough for a 12 MP crop, never more than half the original's size.</summary>
    private const double MasterPixels = 14e6;

    public sealed record Tier(string Name, int LongSide, int ShortSide);

    public static readonly Tier[] Tiers =
    [
        new("12mp", 4000, 3000),
        new("3mp", 2000, 1500),
        new("0.5mp", 816, 612),
    ];

    public sealed record Variant(string Group, string Tier, string Sampling, int Quality, string Kind, EncodeSettings Settings);

    public static IEnumerable<Variant> Variants()
    {
        int[] qualities = [50, 75, 90, 95, 100];
        foreach (var tier in Tiers)
            foreach (var (samp, code) in new[] { ("444", TurboJpeg.Samp444), ("420", TurboJpeg.Samp420) })
                foreach (var q in qualities)
                    yield return new Variant($"{samp}-q{q}-{tier.Name}", tier.Name, samp, q, "baseline", new EncodeSettings(code, q));

        // Secondary axes, at 3 MP: restart interval every MCU row, optimized Huffman
        // tables, progressive, and the optional 4:2:2 / grayscale samplings.
        foreach (var (samp, code) in new[] { ("444", TurboJpeg.Samp444), ("420", TurboJpeg.Samp420) })
            foreach (var q in new[] { 75, 95 })
            {
                yield return new Variant($"{samp}-q{q}-3mp-rst", "3mp", samp, q, "restart-row", new EncodeSettings(code, q, RestartEveryRow: true));
                yield return new Variant($"{samp}-q{q}-3mp-opt", "3mp", samp, q, "optimized", new EncodeSettings(code, q, Optimize: true));
            }

        foreach (var q in new[] { 75, 95 })
        {
            yield return new Variant($"420-q{q}-3mp-prog", "3mp", "420", q, "progressive", new EncodeSettings(TurboJpeg.Samp420, q, Progressive: true));
            yield return new Variant($"422-q{q}-3mp", "3mp", "422", q, "baseline", new EncodeSettings(TurboJpeg.Samp422, q));
            yield return new Variant($"gray-q{q}-3mp", "3mp", "gray", q, "baseline", new EncodeSettings(TurboJpeg.SampGray, q));
        }
    }

    public static void Prepare()
    {
        TurboJpeg.SelfCheck();
        Directory.CreateDirectory(BenchPaths.Masters);
        Directory.CreateDirectory(BenchPaths.Matrix);
        var cases = new List<MatrixCase>();
        var variants = Variants().ToArray();

        foreach (var entry in ManifestEntry.Load())
        {
            var original = Path.Combine(BenchPaths.Originals, entry.Name + ".jpg");
            if (!File.Exists(original))
                throw new FileNotFoundException("Corpus original missing — run scripts/fetch-corpus.sh", original);

            if (entry.IsCameraOriginal)
            {
                var dst = Path.Combine(BenchPaths.Matrix, "camera", entry.Name + ".jpg");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(original, dst, overwrite: true);
                var info = Jpeg.JpegDecoder.ReadInfo(File.ReadAllBytes(dst));
                cases.Add(new MatrixCase($"camera/{entry.Name}", "camera", entry.Name, "camera", Sampling(dst), 0, "camera-original",
                    info.Width, info.Height, new FileInfo(dst).Length));
                Console.WriteLine($"[prepare] {entry.Name}: camera original {info.Width}x{info.Height}");
                continue;
            }

            using var master = LoadOrBuildMaster(entry, original);
            foreach (var tier in Tiers)
            {
                var (cw, ch) = master.Width >= master.Height ? (tier.LongSide, tier.ShortSide) : (tier.ShortSide, tier.LongSide);
                cw = Math.Min(cw, master.Width);
                ch = Math.Min(ch, master.Height);
                var x0 = (master.Width - cw) / 2;
                var y0 = (master.Height - ch) / 2;
                var rgb = new byte[cw * ch * 3];
                master.ProcessPixelRows(acc =>
                {
                    for (var y = 0; y < ch; y++)
                    {
                        var row = acc.GetRowSpan(y0 + y).Slice(x0, cw);
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(row).CopyTo(rgb.AsSpan(y * cw * 3, cw * 3));
                    }
                });

                foreach (var v in variants.Where(v => v.Tier == tier.Name))
                {
                    var id = $"{v.Group}/{entry.Name}";
                    var file = Path.Combine(BenchPaths.Matrix, v.Group, entry.Name + ".jpg");
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var jpeg = TurboEncoder.Encode(rgb, cw, ch, v.Settings);
                    File.WriteAllBytes(file, jpeg);
                    cases.Add(new MatrixCase(id, v.Group, entry.Name, v.Tier, v.Sampling, v.Quality, v.Kind, cw, ch, jpeg.Length));
                }

                Console.WriteLine($"[prepare] {entry.Name}: {tier.Name} {cw}x{ch}");
            }
        }

        MatrixCase.WriteIndex(cases);
        Console.WriteLine($"[prepare] {cases.Count} files -> {BenchPaths.Matrix}");
    }

    private static Image<Rgb24> LoadOrBuildMaster(ManifestEntry entry, string original)
    {
        var masterPath = Path.Combine(BenchPaths.Masters, entry.Name + ".png");
        if (File.Exists(masterPath))
            return Image.Load<Rgb24>(masterPath);

        using var src = Image.Load<Rgb24>(original);
        var scale = Math.Min(0.5, Math.Max(
            Math.Sqrt(MasterPixels / ((double)src.Width * src.Height)),
            3000.0 / Math.Min(src.Width, src.Height)));
        var w = (int)Math.Round(src.Width * scale);
        var h = (int)Math.Round(src.Height * scale);
        var master = src.Clone(ctx => ctx.Resize(w, h, KnownResamplers.Lanczos3));
        master.SaveAsPng(masterPath);
        Console.WriteLine($"[prepare] {entry.Name}: master {src.Width}x{src.Height} -> {w}x{h} (x{scale:F3})");
        return master;
    }

    /// <summary>Chroma sampling label read from the SOF of a camera file.</summary>
    private static string Sampling(string file)
    {
        var planes = Jpeg.JpegDiagnostics.DecodePlanes(File.ReadAllBytes(file));
        if (planes.Length == 1) return "gray";
        var (h, v) = (planes[0].H, planes[0].V);
        return (h, v) switch
        {
            (1, 1) => "444",
            (2, 1) => "422",
            (2, 2) => "420",
            (1, 2) => "440",
            _ => $"{h}x{v}",
        };
    }
}
