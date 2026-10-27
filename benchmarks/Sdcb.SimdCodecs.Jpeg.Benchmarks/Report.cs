using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>Machine facts recorded alongside every result.</summary>
internal static class MachineInfo
{
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"- .NET: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier}), process {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"- Logical processors: {Environment.ProcessorCount}");
        sb.AppendLine($"- Vector128.IsHardwareAccelerated: {Vector128.IsHardwareAccelerated}, " +
                      $"Vector256.IsHardwareAccelerated: {Vector256.IsHardwareAccelerated}, " +
                      $"Vector512.IsHardwareAccelerated: {Vector512.IsHardwareAccelerated}");
        sb.AppendLine($"- AVX2: {Avx2.IsSupported}, AVX-512F: {Avx512F.IsSupported}, BMI2: {Bmi2.IsSupported}");
        sb.AppendLine($"- GC: workstation, non-concurrent; tiered compilation default");
        return sb.ToString();
    }

    public static string File => Path.Combine(BenchPaths.Work, "machine.md");
}

/// <summary>Turns the BenchmarkDotNet JSON + side measurements into <c>benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/&lt;date&gt;-&lt;cpu&gt;.md</c>.</summary>
internal static class Report
{
    private sealed record Timing(string Case, string Method, double MedianNs, long AllocatedBytes);

    public static string Write(string bdnJson, string? date = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(bdnJson));
        var root = doc.RootElement;
        var host = root.GetProperty("HostEnvironmentInfo");
        var cpu = host.GetProperty("ProcessorName").GetString() ?? "unknown-cpu";

        var timings = new List<Timing>();
        foreach (var b in root.GetProperty("Benchmarks").EnumerateArray())
        {
            var stats = b.GetProperty("Statistics");
            if (stats.ValueKind != JsonValueKind.Object) continue;
            var param = b.GetProperty("Parameters").GetString() ?? "";
            var full = b.GetProperty("FullName").GetString() ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(full, "Case: \"([^\"]+)\"");
            var caseId = m.Success ? m.Groups[1].Value : param;
            var alloc = b.TryGetProperty("Memory", out var mem) && mem.TryGetProperty("BytesAllocatedPerOperation", out var a) ? a.GetInt64() : -1;
            timings.Add(new Timing(caseId, b.GetProperty("Method").GetString()!, stats.GetProperty("Median").GetDouble(), alloc));
        }

        var index = MatrixCase.LoadIndex().ToDictionary(c => c.Id);
        var byCase = timings.GroupBy(t => t.Case).Where(g => index.ContainsKey(g.Key))
            .ToDictionary(g => g.Key, g => g.ToDictionary(t => t.Method));

        var sb = new StringBuilder();
        date ??= DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sb.AppendLine($"# JPEG decode baseline — {date} — {cpu}");
        sb.AppendLine();
        sb.AppendLine("Single-threaded whole-image decode to RGBA. **Measurement only — no optimisation was done for this baseline.**");
        sb.AppendLine();
        sb.AppendLine("Contestants: **Sdcb.SimdCodecs** `JpegDecoder.DecodeTo` into a reused buffer; **libjpeg-turbo 3.2.0** official binary via TurboJPEG 3 " +
                      "(`tj3Decompress8`, `TJPF_RGBA`, reused buffer; defaults = islow IDCT + fancy upsampling); **libjpeg-turbo (fast)** = " +
                      "`TJPARAM_FASTDCT` + `TJPARAM_FASTUPSAMPLE`, for reference; **ImageSharp 3.1.12** (newest release that builds without a license key; 3.2.0 and 4.x require one) `Image.Load<Rgba32>` with " +
                      "`MaxDegreeOfParallelism = 1` (no decode-into-buffer API, so it allocates its image each call).");
        sb.AppendLine();

        sb.AppendLine("## Machine");
        sb.AppendLine();
        sb.AppendLine($"- CPU: {cpu}");
        if (System.IO.File.Exists(MachineInfo.File)) sb.Append(System.IO.File.ReadAllText(MachineInfo.File));
        sb.AppendLine($"- BenchmarkDotNet {root.GetProperty("HostEnvironmentInfo").GetProperty("BenchmarkDotNetVersion").GetString()}, " +
                      "Release, x64, out-of-process, 1 launch × 3 warm-up × 9 measured iterations (≥ 200 ms each); all four contestants run " +
                      "back-to-back per file (interleaved order).");
        sb.AppendLine();

        string R(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        string Mps(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
        static double Geo(IEnumerable<double> xs) { var l = xs.Where(x => x > 0 && !double.IsNaN(x)).ToList(); return l.Count == 0 ? double.NaN : Math.Exp(l.Average(Math.Log)); }

        double MpsOf(string id, string method) => index[id].Megapixels / (byCase[id][method].MedianNs / 1e9);
        double Ratio(string id, string method) => byCase[id][method].MedianNs / byCase[id]["LibjpegTurbo"].MedianNs;

        var groups = byCase.Keys.GroupBy(id => index[id].Group).OrderBy(g => Accuracy.GroupSort(g.Key)).ToList();

        sb.AppendLine("## Performance by group (sampling × quality × size)");
        sb.AppendLine();
        sb.AppendLine("Time ratio = contestant median ÷ libjpeg-turbo median (lower is faster; 1.00 = parity), geometric mean over the images in the group. " +
                      "MP/s = geometric mean throughput.");
        sb.AppendLine();
        sb.AppendLine("| Group | Files | turbo MP/s | ours MP/s | ImageSharp MP/s | turbo-fast MP/s | **ours ÷ turbo** | ImageSharp ÷ turbo | turbo-fast ÷ turbo | ours ÷ ImageSharp |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        var groupRows = new List<(string Group, double Ours, double IS)>();
        foreach (var g in groups)
        {
            var ids = g.ToList();
            var ours = Geo(ids.Select(i => Ratio(i, "Ours")));
            var isharp = Geo(ids.Select(i => Ratio(i, "ImageSharp")));
            var fast = Geo(ids.Select(i => Ratio(i, "LibjpegTurboFast")));
            groupRows.Add((g.Key, ours, isharp));
            sb.AppendLine($"| {g.Key} | {ids.Count} | {Mps(Geo(ids.Select(i => MpsOf(i, "LibjpegTurbo"))))} | {Mps(Geo(ids.Select(i => MpsOf(i, "Ours"))))} | " +
                          $"{Mps(Geo(ids.Select(i => MpsOf(i, "ImageSharp"))))} | {Mps(Geo(ids.Select(i => MpsOf(i, "LibjpegTurboFast"))))} | " +
                          $"**{R(ours)}×** | {R(isharp)}× | {R(fast)}× | {R(ours / isharp)}× |");
        }

        sb.AppendLine();
        var all = byCase.Keys.ToList();
        var matrixOnly = all.Where(i => index[i].Group != "camera").ToList();
        sb.AppendLine($"Overall geometric mean (all {all.Count} files): ours ÷ turbo **{R(Geo(all.Select(i => Ratio(i, "Ours"))))}×**, " +
                      $"ImageSharp ÷ turbo **{R(Geo(all.Select(i => Ratio(i, "ImageSharp"))))}×**, " +
                      $"turbo-fast ÷ turbo {R(Geo(all.Select(i => Ratio(i, "LibjpegTurboFast"))))}×.");
        sb.AppendLine();

        // Marginal views over the main matrix.
        sb.AppendLine("### Marginals over the main matrix (baseline, standard Huffman tables, no restarts)");
        sb.AppendLine();
        var main = matrixOnly.Where(i => index[i].Variant == "baseline" && index[i].Sampling is "444" or "420").ToList();
        foreach (var (title, key) in new (string, Func<MatrixCase, string>)[]
                 {
                     ("Sampling", c => c.Sampling),
                     ("Quality", c => "q" + c.Quality.ToString("D3", CultureInfo.InvariantCulture)),
                     ("Size", c => c.Tier),
                     ("Image", c => c.Image),
                 })
        {
            sb.AppendLine($"| {title} | Files | ours ÷ turbo | ImageSharp ÷ turbo | ours ÷ ImageSharp |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var g in main.GroupBy(i => key(index[i])).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var o = Geo(g.Select(i => Ratio(i, "Ours")));
                var s = Geo(g.Select(i => Ratio(i, "ImageSharp")));
                sb.AppendLine($"| {g.Key} | {g.Count()} | {R(o)}× | {R(s)}× | {R(o / s)}× |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("### Largest gaps (ours ÷ turbo, worst groups first)");
        sb.AppendLine();
        foreach (var (g, o, s) in groupRows.OrderByDescending(r => r.Ours).Take(6))
            sb.AppendLine($"- `{g}`: ours {R(o)}× turbo's time (ImageSharp {R(s)}×)");
        sb.AppendLine();

        sb.AppendLine("## Managed allocation per decode (BenchmarkDotNet MemoryDiagnoser)");
        sb.AppendLine();
        sb.AppendLine("Mean bytes allocated on the GC heap per operation. libjpeg-turbo's memory is native and invisible here; ImageSharp's " +
                      "pooled buffers are partly unmanaged — see peak working set below for the full picture.");
        sb.AppendLine();
        sb.AppendLine("| Size | turbo | ours | ImageSharp | output RGBA |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var g in all.GroupBy(i => index[i].Tier).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string A(string m) => FormatBytes(g.Average(i => (double)byCase[i][m].AllocatedBytes));
            sb.AppendLine($"| {g.Key} | {A("LibjpegTurbo")} | {A("Ours")} | {A("ImageSharp")} | {FormatBytes(g.Average(i => index[i].Width * (double)index[i].Height * 4))} |");
        }

        sb.AppendLine();

        if (System.IO.File.Exists(PeakMemory.ResultFile))
        {
            sb.AppendLine("## Peak working set (separate process per decode, median of 3)");
            sb.AppendLine();
            sb.AppendLine("Rise = peak working set − working set just before the decode (input loaded, decoder initialised, and — for ours and " +
                          "libjpeg-turbo — the RGBA destination already allocated and touched). ImageSharp's rise includes its own output image.");
            sb.AppendLine();
            sb.AppendLine("| File | Output RGBA | turbo rise | ours rise | ImageSharp rise | turbo peak | ours peak | ImageSharp peak |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
            var rows = System.IO.File.ReadAllLines(PeakMemory.ResultFile).Skip(1).Select(l => l.Split('\t')).GroupBy(f => f[0]);
            foreach (var g in rows)
            {
                var d = g.ToDictionary(f => f[1]);
                string Rise(string c) => d.TryGetValue(c, out var f) ? FormatBytes(long.Parse(f[4], CultureInfo.InvariantCulture)) : "–";
                string Peak(string c) => d.TryGetValue(c, out var f) ? FormatBytes(long.Parse(f[3], CultureInfo.InvariantCulture)) : "–";
                var output = FormatBytes(long.Parse(g.First()[5], CultureInfo.InvariantCulture));
                sb.AppendLine($"| `{g.Key}` | {output} | {Rise("LibjpegTurbo")} | {Rise("Ours")} | {Rise("ImageSharp")} | {Peak("LibjpegTurbo")} | {Peak("Ours")} | {Peak("ImageSharp")} |");
            }

            sb.AppendLine();
        }

        if (System.IO.File.Exists(Validation.ResultFile))
        {
            var v = System.IO.File.ReadAllLines(Validation.ResultFile).Skip(1).Select(l => l.Split('\t')).ToList();
            double Min(int col) => v.Select(f => f[col] == "inf" ? double.PositiveInfinity : double.Parse(f[col], CultureInfo.InvariantCulture)).Min();
            sb.AppendLine("## Correctness gate (before timing)");
            sb.AppendLine();
            sb.AppendLine($"All {v.Count} files decoded by every contestant; pairwise RGB PSNR had to exceed {Validation.MinPsnr} dB. " +
                          $"Minimum observed: ours/turbo {R(Min(1))} dB, ours/ImageSharp {R(Min(2))} dB, turbo/ImageSharp {R(Min(3))} dB " +
                          $"(turbo-fast/turbo {R(Min(4))} dB, informational).");
            sb.AppendLine();
        }

        if (System.IO.File.Exists(Accuracy.ResultFile))
        {
            sb.AppendLine("## Accuracy");
            sb.AppendLine();
            sb.Append(System.IO.File.ReadAllText(Accuracy.ResultFile));
        }

        sb.AppendLine("## Corpus");
        sb.AppendLine();
        sb.AppendLine("Wikimedia Commons, CC0 / Public Domain only (`corpus/manifest.tsv`, URL + SHA-256). Non-camera images: Lanczos-3 " +
                      "downscale to ≤ ½ size (~14 MP master, lossless PNG), centred crops at 4000×3000, 2000×1500 and 816×612 " +
                      "(clipped to the master), encoded with libjpeg-turbo 3.2.0 `tj3Compress8`. Camera originals are benchmarked as shot.");
        sb.AppendLine();
        sb.AppendLine("<details><summary>Per-file medians</summary>");
        sb.AppendLine();
        sb.AppendLine("| File | MP | turbo ms | ours ms | ImageSharp ms | turbo-fast ms | ours ÷ turbo | ImageSharp ÷ turbo |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var id in all.OrderBy(i => Accuracy.GroupSort(index[i].Group)).ThenBy(i => i, StringComparer.Ordinal))
        {
            string Ms(string m) => (byCase[id][m].MedianNs / 1e6).ToString("F2", CultureInfo.InvariantCulture);
            sb.AppendLine($"| `{id}` | {index[id].Megapixels:F2} | {Ms("LibjpegTurbo")} | {Ms("Ours")} | {Ms("ImageSharp")} | {Ms("LibjpegTurboFast")} | {R(Ratio(id, "Ours"))} | {R(Ratio(id, "ImageSharp"))} |");
        }

        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        sb.AppendLine("## Reproduce");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-libjpeg-turbo.sh");
        sb.AppendLine("bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-corpus.sh");
        sb.AppendLine("cd benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks");
        sb.AppendLine("dotnet run -c Release -- prepare      # masters + libjpeg-turbo matrix");
        sb.AppendLine("dotnet run -c Release -- accuracy     # coefficient / plane / RGB accuracy tables");
        sb.AppendLine("dotnet run -c Release -- bench        # correctness gate, then BenchmarkDotNet");
        sb.AppendLine("dotnet run -c Release -- peakmem      # peak working set, one process per decode");
        sb.AppendLine("dotnet run -c Release -- report       # writes results/<date>-<cpu>.md");
        sb.AppendLine("```");

        var slug = new string(cpu.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        var outFile = Path.Combine(BenchPaths.ResultsDir, $"{date}-{slug.Trim('-')}.md");
        Directory.CreateDirectory(BenchPaths.ResultsDir);
        System.IO.File.WriteAllText(outFile, sb.ToString());
        return outFile;
    }

    private static string FormatBytes(double b) =>
        b < 0 ? "n/a" :
        b >= 1 << 20 ? (b / 1048576).ToString("F1", CultureInfo.InvariantCulture) + " MiB" :
        b >= 1 << 10 ? (b / 1024).ToString("F1", CultureInfo.InvariantCulture) + " KiB" :
        b.ToString("F0", CultureInfo.InvariantCulture) + " B";
}
