using System.Globalization;
using System.Text;
using Sdcb.SimdCodecs.Png.Benchmarks;
using static Sdcb.SimdCodecs.Bmp.Benchmarks.Timing;

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

/// <summary>The decode and encode matrices: every selected case, every contestant, timed and tabulated.</summary>
internal static class Matrix
{
    public static string RunDecode(int rounds)
    {
        var rows = new List<(DecodeSetup Setup, Dictionary<string, double> Ms)>();
        foreach (var c in DecodeCase.Selected)
        {
            var setup = DecodeSetup.Create(c);
            var ms = Time(setup.Contestants, rounds, setup.Allocated);
            rows.Add((setup, ms));
            Console.WriteLine($"[decode] {c.Name,-22} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {F(kv.Value)}")) +
                              (setup.Notes.Count > 0 ? "  (" + string.Join("; ", setup.Notes) + ")" : ""));
        }
        return RenderDecode(rows, $"in-process medians of {rounds} interleaved rounds");
    }

    public static string RunEncode(int rounds)
    {
        var rows = new List<(EncodeSetup Setup, Dictionary<string, double> Ms)>();
        foreach (var c in EncodeCase.Selected)
        {
            var setup = EncodeSetup.Create(c);
            var ms = Time(setup.Contestants, rounds, setup.Allocated);
            rows.Add((setup, ms));
            Console.WriteLine($"[encode] {c.Name,-22} " + string.Join("  ", ms.Select(kv => $"{kv.Key} {F(kv.Value)}")) +
                              (setup.Notes.Count > 0 ? "  (" + string.Join("; ", setup.Notes) + ")" : ""));
        }
        return RenderEncode(rows, $"in-process medians of {rounds} interleaved rounds");
    }

    public static string RenderDecode(IReadOnlyList<(DecodeSetup Setup, Dictionary<string, double> Ms)> rows, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Decode ({source}, ms)");
        sb.AppendLine();
        sb.AppendLine("`current` = `BmpImageDecoder` / `IcoImageDecoder.TryDecodeInto` to RGBA in a caller buffer; `current BGR(A)` = " +
                      "the same to BGRA, the layout WIC produces; ImageSharp = `Image.Load<Rgba32>` + `CopyPixelDataTo`, one thread " +
                      "(it reads no icons); WIC = WPF's `BitmapDecoder` + `FormatConvertedBitmap` to BGRA. Ratios below 1 mean we are " +
                      "faster: current ÷ ImageSharp (both RGBA), current BGR(A) ÷ WIC (both BGRA). Every timed output was checked " +
                      "against the others first.");
        sb.AppendLine();
        sb.AppendLine("| Case | KB | MP | current | current BGR(A) | baseline | ImageSharp | WIC | current MP/s | ÷ ImageSharp | ÷ WIC | ÷ baseline |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var notes = new List<string>();
        foreach (var (s, ms) in rows)
        {
            var mp = s.Width * (double)s.Height / 1e6;
            sb.AppendLine($"| `{s.Case.Name}` | {s.Data.Length / 1024.0:F0} | {mp:F2} | {Ms(ms, Names.Current)} | {Ms(ms, Names.CurrentBgra)} | " +
                          $"{Ms(ms, Names.Baseline)} | {Ms(ms, Names.ImageSharp)} | {Ms(ms, Names.Wic)} | " +
                          $"{(Get(ms, Names.Current) is { } c ? (mp / (c / 1000)).ToString("F0", CultureInfo.InvariantCulture) : "—")} | " +
                          $"{Ratio(ms, Names.Current, Names.ImageSharp)} | {Ratio(ms, Names.CurrentBgra, Names.Wic)} | {Ratio(ms, Names.Current, Names.Baseline)} |");
            notes.AddRange(s.Notes.Select(n => $"`{s.Case.Name}`: {n}"));
        }

        sb.AppendLine();
        sb.AppendLine("Geometric means over the cases both decode:");
        sb.AppendLine();
        foreach (var (a, b) in new[] { (Names.Current, Names.ImageSharp), (Names.CurrentBgra, Names.Wic), (Names.Current, Names.Baseline) })
        {
            foreach (var content in SourceImage.Contents)
            {
                var subset = rows.Where(r => r.Setup.Case.Content == content).Select(r => r.Ms).ToList();
                if (GeoMean(subset, a, b) is { } g)
                    sb.AppendLine($"- {Names.Label(a)} ÷ {Names.Label(b)}, {content}: {g.ToString("F2", CultureInfo.InvariantCulture)}× " +
                                  $"({subset.Count(m => m.ContainsKey(a) && m.ContainsKey(b))} cases)");
            }
        }

        AppendAllocations(sb, rows.Select(r => (r.Setup.Case.Name, r.Setup.Allocated)).ToList(),
            [Names.Current, Names.CurrentBgra, Names.Baseline, Names.ImageSharp, Names.Wic]);
        AppendNotes(sb, notes);
        return sb.ToString();
    }

    public static string RenderEncode(IReadOnlyList<(EncodeSetup Setup, Dictionary<string, double> Ms)> rows, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Encode ({source}, ms; sizes in KB)");
        sb.AppendLine();
        sb.AppendLine("`current` = `BmpEncoder.Encode` from RGBA to a new array (`IcoEncoder.Encode` for the icon set); `current → stream` " +
                      "= the `Stream` overload into a reused stream, as ImageSharp and WIC write; `current BGR(A)` = that from the B, G, R(, A) " +
                      "input WIC gets; ImageSharp = `BmpEncoder` with the same bits per pixel into a reused stream, one " +
                      "thread, from an `Image<Rgba32>` built beforehand; WIC = WPF's `BmpBitmapEncoder` over a `BitmapSource` of B, G, " +
                      "R(, A), into a reused stream (24- and 32-bit only). Every output decodes, and the lossless layouts give the source back.");
        sb.AppendLine();
        sb.AppendLine("| Case | current | KB | current → stream | current BGR(A) | baseline | ImageSharp | KB | WIC | KB | stream ÷ ImageSharp | BGR(A) ÷ WIC | ÷ baseline |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var notes = new List<string>();
        foreach (var (s, ms) in rows)
        {
            sb.AppendLine($"| `{s.Case.Name}` | {Ms(ms, Names.Current)} | {Kb(s, Names.Current)} | {Ms(ms, Names.CurrentStream)} | {Ms(ms, Names.CurrentBgra)} | " +
                          $"{Ms(ms, Names.Baseline)} | {Ms(ms, Names.ImageSharp)} | {Kb(s, Names.ImageSharp)} | {Ms(ms, Names.Wic)} | {Kb(s, Names.Wic)} | " +
                          $"{Ratio(ms, Names.CurrentStream, Names.ImageSharp)} | {Ratio(ms, Names.CurrentBgra, Names.Wic)} | {Ratio(ms, Names.Current, Names.Baseline)} |");
            notes.AddRange(s.Notes.Select(n => $"`{s.Case.Name}`: {n}"));
        }
        AppendAllocations(sb, rows.Select(r => (r.Setup.Case.Name, r.Setup.Allocated)).ToList(),
            [Names.Current, Names.CurrentStream, Names.CurrentBgra, Names.Baseline, Names.ImageSharp, Names.Wic]);
        AppendNotes(sb, notes);
        return sb.ToString();
    }

    private static string Kb(EncodeSetup s, string name) =>
        s.Sizes.TryGetValue(name, out var bytes) ? (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) : "—";
}
