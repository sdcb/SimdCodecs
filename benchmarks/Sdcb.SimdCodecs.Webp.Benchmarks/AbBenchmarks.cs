using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>
/// The optimisation loop's A/B: the current decoder vs a baseline commit's decoder
/// (<see cref="BaselineDecoder"/>) vs SIMD libwebp, all in one BenchmarkDotNet run, plus ablated
/// variants of the current decoder that skip one stage each. A stage's cost is the time that
/// disappears when it is switched off — attribution by ablation, not sampling.
/// </summary>
public class AbBenchmarks
{
    public const string InputsVariable = "SIMDCODECS_WEBP_AB_INPUTS";

    public static readonly string[] AllInputs =
    [
        WebpBenchmarkInputs.LossyOpaque,
        WebpBenchmarkInputs.LossyAlpha,
        WebpBenchmarkInputs.Lossless,
    ];

    private byte[] _source = [];
    private byte[] _rgba = [];
    private int _width;
    private int _height;

    public static IEnumerable<string> Inputs()
    {
        string? filter = Environment.GetEnvironmentVariable(InputsVariable);
        return string.IsNullOrEmpty(filter)
            ? AllInputs
            : AllInputs.Where(i => System.Text.RegularExpressions.Regex.IsMatch(i, filter));
    }

    [ParamsSource(nameof(Inputs))]
    public string Input { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _source = WebpBenchmarkInputs.Read(Input);
        WebpInfo info = WebpDecoder.ReadInfo(_source);
        _width = info.Width;
        _height = info.Height;
        _rgba = new byte[_width * _height * 4];
        BaselineDecoder.DecodeTo(_source, _rgba);
    }

    [Benchmark(Baseline = true)]
    public void Baseline() => BaselineDecoder.DecodeTo(_source, _rgba);

    [Benchmark]
    public void Current() => WebpDecoder.DecodeTo(_source, _rgba);

    [Benchmark]
    public void LibWebp() => Benchmarks.LibWebp.DecodeRgbaInto(_source, _rgba, _width, _height);

    [Benchmark]
    public void NoLoopFilter() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipLoopFilter);

    [Benchmark]
    public void NoResidual() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipResidual);

    [Benchmark]
    public void NoPrediction() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipPrediction);

    [Benchmark]
    public void NoOutput() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipOutput);

    [Benchmark]
    public void NoMacroblockCopy() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipMacroblockCopy);

    [Benchmark]
    public void NoAlpha() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipAlpha);

    [Benchmark]
    public void EntropyOnly() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.EntropyOnly | WebpAblation.SkipAlpha);

    [Benchmark]
    public void NoTransforms() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipTransforms);

    [Benchmark]
    public void NoPredictorTransform() => WebpDiagnostics.DecodeTo(_source, _rgba, WebpAblation.SkipPredictorTransform);

    public static readonly string[] CoreMethods = [nameof(Baseline), nameof(Current), nameof(LibWebp)];

    /// <summary>Ablations that mean something for <paramref name="input"/>.</summary>
    public static string[] AblationMethods(string input)
    {
        // By content rather than name, so that `quick --files` corpus inputs get the right set.
        WebpInfo info = WebpDecoder.ReadInfo(WebpBenchmarkInputs.Read(input));
        return info.Format == WebpFormat.Lossless ? [nameof(NoOutput), nameof(NoTransforms), nameof(NoPredictorTransform)]
            : info.HasAlpha ? [nameof(NoAlpha), nameof(NoLoopFilter), nameof(NoResidual), nameof(NoPrediction), nameof(NoMacroblockCopy), nameof(NoOutput), nameof(EntropyOnly)]
            : [nameof(NoLoopFilter), nameof(NoResidual), nameof(NoPrediction), nameof(NoMacroblockCopy), nameof(NoOutput), nameof(EntropyOnly)];
    }

    /// <summary>Per-input medians → markdown: speed vs baseline and libwebp, and stage costs by ablation.</summary>
    public static string Render(Summary summary, string baselineLabel)
    {
        var medians = new Dictionary<(string Input, string Method), double>();
        var allocated = new Dictionary<(string Input, string Method), long>();
        foreach (BenchmarkReport r in summary.Reports.Where(r => r.Success && r.ResultStatistics is not null))
        {
            string input = r.BenchmarkCase.Parameters["Input"]?.ToString() ?? "";
            string method = r.BenchmarkCase.Descriptor.WorkloadMethod.Name;
            medians[(input, method)] = r.ResultStatistics!.Median / 1e6;
            if (r.GcStats.GetBytesAllocatedPerOperation(r.BenchmarkCase) is { } bytes)
            {
                allocated[(input, method)] = bytes;
            }
        }

        return RenderTable(medians, baselineLabel, "BenchmarkDotNet medians", allocated);
    }

    internal static string RenderTable(
        IReadOnlyDictionary<(string Input, string Method), double> medians,
        string baselineLabel,
        string source,
        IReadOnlyDictionary<(string Input, string Method), long>? allocated = null)
    {
        string Ms(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        string X(double v) => v.ToString("F2", CultureInfo.InvariantCulture) + "×";
        double? Get(string i, string m) => medians.TryGetValue((i, m), out double v) ? v : null;
        List<string> inputs = medians.Keys.Select(k => k.Input).Distinct().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Baseline: {baselineLabel}. Times are {source} in ms.");
        sb.AppendLine();
        sb.AppendLine("| Input | libwebp | baseline | current | current ÷ baseline | current ÷ libwebp |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (string input in inputs)
        {
            if (Get(input, nameof(Current)) is not { } current)
            {
                continue;
            }

            double? lib = Get(input, nameof(LibWebp));
            double? baseline = Get(input, nameof(Baseline));
            sb.AppendLine($"| `{Path.GetFileName(input)}` | {(lib is { } l ? Ms(l) : "—")} | {(baseline is { } b ? Ms(b) : "—")} | {Ms(current)} | " +
                          $"{(baseline is { } b2 ? X(current / b2) : "—")} | {(lib is { } l2 ? X(current / l2) : "—")} |");
        }

        if (allocated is { Count: > 0 })
        {
            string B(string i, string m) => allocated.TryGetValue((i, m), out long v) ? v.ToString("N0", CultureInfo.InvariantCulture) : "—";
            sb.AppendLine();
            sb.AppendLine("Managed bytes allocated per decode (BenchmarkDotNet MemoryDiagnoser; libwebp's native allocations are " +
                          "not visible here — see `peakmem`).");
            sb.AppendLine();
            sb.AppendLine("| Input | libwebp | baseline | current |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (string input in inputs)
            {
                sb.AppendLine($"| `{Path.GetFileName(input)}` | {B(input, nameof(LibWebp))} | {B(input, nameof(Baseline))} | {B(input, nameof(Current))} |");
            }
        }

        if (medians.Keys.Any(k => k.Method == QuickAb.Multithreaded))
        {
            sb.AppendLine();
            sb.AppendLine("Opt-in multithreaded decode (not part of the single-thread comparison above):");
            sb.AppendLine();
            sb.AppendLine("| Input | libwebp | current (1 thread) | multithreaded | ÷ 1 thread | ÷ libwebp |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (string input in inputs)
            {
                if (Get(input, nameof(Current)) is not { } current || Get(input, QuickAb.Multithreaded) is not { } mt)
                {
                    continue;
                }

                double? lib = Get(input, nameof(LibWebp));
                sb.AppendLine($"| `{Path.GetFileName(input)}` | {(lib is { } l ? Ms(l) : "—")} | {Ms(current)} | {Ms(mt)} | {X(mt / current)} | " +
                              $"{(lib is { } l2 ? X(mt / l2) : "—")} |");
            }
        }

        string[] ablations = [nameof(NoAlpha), nameof(NoLoopFilter), nameof(NoResidual), nameof(NoPrediction), nameof(NoMacroblockCopy), nameof(NoOutput), nameof(NoTransforms), nameof(NoPredictorTransform)];
        if (medians.Keys.Any(k => ablations.Contains(k.Method) || k.Method == nameof(EntropyOnly)))
        {
            sb.AppendLine();
            sb.AppendLine("Stage cost = current − (current with that stage skipped); \"entropy only\" is the time left with " +
                          "alpha, prediction, residual, macroblock copies, loop filter and output all skipped (headers, mode and " +
                          "token parsing).");
            sb.AppendLine();
            sb.AppendLine("| Input | alpha | loop filter | residual | prediction | MB copy | output | VP8L transforms | (of which predictor) | entropy only |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (string input in inputs)
            {
                if (Get(input, nameof(Current)) is not { } current)
                {
                    continue;
                }

                string Cost(string m) => Get(input, m) is { } v ? Ms(current - v) : "—";
                sb.AppendLine($"| `{Path.GetFileName(input)}` | {Cost(nameof(NoAlpha))} | {Cost(nameof(NoLoopFilter))} | {Cost(nameof(NoResidual))} | " +
                              $"{Cost(nameof(NoPrediction))} | {Cost(nameof(NoMacroblockCopy))} | {Cost(nameof(NoOutput))} | {Cost(nameof(NoTransforms))} | {Cost(nameof(NoPredictorTransform))} | " +
                              $"{(Get(input, nameof(EntropyOnly)) is { } e ? Ms(e) : "—")} |");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Quick in-process A/B for the inner loop (seconds, not minutes): every contestant is timed in
/// interleaved rounds so drift lands on all alike; the median per contestant is reported. Commit
/// numbers come from the BenchmarkDotNet <c>ab</c> run.
/// </summary>
internal static class QuickAb
{
    /// <summary>Contestant name of the opt-in multithreaded decode (<c>quick --mt</c>).</summary>
    public const string Multithreaded = "Multithreaded";

    public static void Run(IReadOnlyList<string> inputs, bool ablate, int rounds, bool multithreaded = false)
    {
        Console.Write(MachineInfo.Describe());
        bool hasBaseline = BaselineDecoder.Description is not null;
        var medians = new Dictionary<(string, string), double>();
        foreach (string input in inputs)
        {
            byte[] source = WebpBenchmarkInputs.Read(input);
            WebpInfo info = WebpDecoder.ReadInfo(source);
            byte[] rgba = new byte[info.Width * info.Height * 4];
            var contestants = new List<(string Name, Action Decode)>
            {
                (nameof(AbBenchmarks.Current), () => WebpDecoder.DecodeTo(source, rgba)),
                (nameof(AbBenchmarks.LibWebp), () => LibWebp.DecodeRgbaInto(source, rgba, info.Width, info.Height)),
            };
            if (hasBaseline)
            {
                contestants.Add((nameof(AbBenchmarks.Baseline), () => BaselineDecoder.DecodeTo(source, rgba)));
            }

            if (multithreaded)
            {
                contestants.Add((Multithreaded, () => WebpDiagnostics.DecodeToMultithreaded(source, rgba)));
            }

            if (ablate)
            {
                foreach (string method in AbBenchmarks.AblationMethods(input))
                {
                    WebpAblation flags = method switch
                    {
                        nameof(AbBenchmarks.NoLoopFilter) => WebpAblation.SkipLoopFilter,
                        nameof(AbBenchmarks.NoResidual) => WebpAblation.SkipResidual,
                        nameof(AbBenchmarks.NoPrediction) => WebpAblation.SkipPrediction,
                        nameof(AbBenchmarks.NoOutput) => WebpAblation.SkipOutput,
                        nameof(AbBenchmarks.NoMacroblockCopy) => WebpAblation.SkipMacroblockCopy,
                        nameof(AbBenchmarks.NoAlpha) => WebpAblation.SkipAlpha,
                        nameof(AbBenchmarks.NoTransforms) => WebpAblation.SkipTransforms,
                        nameof(AbBenchmarks.NoPredictorTransform) => WebpAblation.SkipPredictorTransform,
                        _ => WebpAblation.EntropyOnly | WebpAblation.SkipAlpha,
                    };
                    contestants.Add((method, () => WebpDiagnostics.DecodeTo(source, rgba, flags)));
                }
            }

            var samples = contestants.ToDictionary(c => c.Name, _ => new List<double>());
            // Warm up by time as well as count: 20 decodes of a tiny file finish before tiered
            // compilation has promoted anything, and the first inputs would then time tier-0 code.
            foreach ((_, Action decode) in contestants)
            {
                long warmStart = Stopwatch.GetTimestamp();
                for (int i = 0; i < 20 || Stopwatch.GetElapsedTime(warmStart).TotalMilliseconds < 300; i++)
                {
                    decode();
                }
            }

            for (int round = 0; round < rounds; round++)
            {
                foreach ((string name, Action decode) in contestants)
                {
                    const int PerSample = 10;
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < PerSample; i++)
                    {
                        decode();
                    }

                    samples[name].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds / PerSample);
                }
            }

            foreach ((string name, List<double> list) in samples)
            {
                list.Sort();
                medians[(input, name)] = list[list.Count / 2];
            }
        }

        Console.WriteLine(AbBenchmarks.RenderTable(medians, BaselineDecoder.Description ?? "(none built)",
            $"in-process medians of {rounds} interleaved rounds × 10 decodes"));
    }
}

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
        sb.AppendLine($"- libwebp: {Environment.GetEnvironmentVariable("LIBWEBP_PATH") ?? "(default search)"}");
        return sb.ToString();
    }
}

/// <summary>Runs every contestant on one input before moving to the next.</summary>
internal sealed class InterleavedOrderer(params string[] methodOrder) : DefaultOrderer
{
    public override IEnumerable<BenchmarkCase> GetExecutionOrder(
        ImmutableArray<BenchmarkCase> benchmarksCase, IEnumerable<BenchmarkLogicalGroupRule>? order = null) =>
        benchmarksCase
            .OrderBy(b => Array.IndexOf(AbBenchmarks.AllInputs, b.Parameters.Items.FirstOrDefault()?.Value?.ToString()))
            .ThenBy(b => Array.IndexOf(methodOrder, b.Descriptor.WorkloadMethod.Name));

    public override IEnumerable<BenchmarkCase> GetSummaryOrder(ImmutableArray<BenchmarkCase> benchmarksCase, Summary summary) =>
        GetExecutionOrder(benchmarksCase);
}
