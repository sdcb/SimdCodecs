using System.Collections.Concurrent;
using System.Globalization;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// The correctness gate that runs before anything is timed: every contestant decodes
/// every file, and the three contestants must agree pairwise at PSNR &gt; 40 dB. A
/// decoder that disagrees is on a broken path, and timing a broken path produces a
/// meaningless number — so this throws instead.
/// </summary>
internal static class Validation
{
    public const double MinPsnr = 40.0;

    public sealed record Row(string Id, double OursVsTurbo, double OursVsImageSharp, double TurboVsImageSharp, double FastVsTurbo);

    public static string ResultFile => Path.Combine(BenchPaths.Work, "validation.tsv");

    public static IReadOnlyList<Row> Run(IReadOnlyList<MatrixCase> cases)
    {
        TurboJpeg.SelfCheck();
        var rows = new ConcurrentBag<Row>();
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, c =>
        {
            var jpeg = File.ReadAllBytes(c.Path);
            var ours = Contestants.DecodeRgba(Contestant.Ours, jpeg, out var w0, out var h0);
            var turbo = Contestants.DecodeRgba(Contestant.LibjpegTurbo, jpeg, out var w1, out var h1);
            var fast = Contestants.DecodeRgba(Contestant.LibjpegTurboFast, jpeg, out _, out _);
            var isharp = Contestants.DecodeRgba(Contestant.ImageSharp, jpeg, out var w2, out var h2);
            if ((w0, h0) != (w1, h1) || (w0, h0) != (w2, h2) || (w0, h0) != (c.Width, c.Height))
            {
                failures.Add($"{c.Id}: dimensions disagree (ours {w0}x{h0}, turbo {w1}x{h1}, ImageSharp {w2}x{h2}, index {c.Width}x{c.Height})");
                return;
            }

            var row = new Row(c.Id,
                Contestants.PsnrRgb(ours, turbo),
                Contestants.PsnrRgb(ours, isharp),
                Contestants.PsnrRgb(turbo, isharp),
                Contestants.PsnrRgb(fast, turbo));
            rows.Add(row);
            if (row.OursVsTurbo <= MinPsnr || row.OursVsImageSharp <= MinPsnr || row.TurboVsImageSharp <= MinPsnr)
            {
                failures.Add($"{c.Id}: PSNR ours/turbo {row.OursVsTurbo:F2}, ours/ImageSharp {row.OursVsImageSharp:F2}, " +
                    $"turbo/ImageSharp {row.TurboVsImageSharp:F2} dB (must all exceed {MinPsnr} dB)");
            }
        });

        var ordered = rows.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(BenchPaths.Work);
        using (var w = new StreamWriter(ResultFile))
        {
            w.WriteLine("id\tours_vs_turbo\tours_vs_imagesharp\tturbo_vs_imagesharp\tturbofast_vs_turbo");
            foreach (var r in ordered)
                w.WriteLine(string.Join('\t', r.Id, F(r.OursVsTurbo), F(r.OursVsImageSharp), F(r.TurboVsImageSharp), F(r.FastVsTurbo)));
        }

        Console.WriteLine($"[validate] {ordered.Count} files; min PSNR ours/turbo {ordered.Min(r => r.OursVsTurbo):F2} dB, " +
            $"ours/ImageSharp {ordered.Min(r => r.OursVsImageSharp):F2} dB, turbo/ImageSharp {ordered.Min(r => r.TurboVsImageSharp):F2} dB, " +
            $"turbo-fast/turbo {ordered.Min(r => r.FastVsTurbo):F2} dB");

        if (!failures.IsEmpty)
        {
            foreach (var f in failures.OrderBy(x => x, StringComparer.Ordinal)) Console.Error.WriteLine("[validate] FAIL " + f);
            throw new InvalidOperationException($"{failures.Count} file(s) failed the pre-timing correctness gate; refusing to benchmark.");
        }

        return ordered;
    }

    private static string F(double v) => double.IsPositiveInfinity(v) ? "inf" : v.ToString("F3", CultureInfo.InvariantCulture);
}
