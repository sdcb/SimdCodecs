using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Reports;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// The optimisation loop's A/B: the current decoder vs a baseline commit's decoder
/// (<see cref="BaselineDecoder"/>) vs libjpeg-turbo, all in one BenchmarkDotNet run, plus
/// ablated variants of the current decoder that skip one stage each. A stage's cost is the
/// time that disappears when it is switched off — attribution by ablation, not sampling.
/// </summary>
public class AbBenchmarks
{
    public const string CasesVariable = "SIMDCODECS_AB_CASES";

    /// <summary>
    /// Default inner-loop subset: two contents (smooth landscape, busy night scene) ×
    /// 4:2:0/4:4:4 × q50/q90 at 3 MP, plus restart, progressive, grayscale, 4:2:2 and a
    /// camera original. Small enough for a few minutes; the full matrix is for milestones.
    /// </summary>
    public static readonly string[] QuickCases =
    [
        "420-q50-3mp/landscape-almsee",
        "420-q90-3mp/landscape-almsee",
        "444-q50-3mp/landscape-almsee",
        "444-q90-3mp/landscape-almsee",
        "420-q50-3mp/night-alexanderplatz",
        "420-q90-3mp/night-alexanderplatz",
        "444-q50-3mp/night-alexanderplatz",
        "444-q90-3mp/night-alexanderplatz",
        "420-q75-3mp-rst/landscape-almsee",
        "420-q75-3mp-prog/landscape-almsee",
        "422-q75-3mp/landscape-almsee",
        "gray-q75-3mp/landscape-almsee",
        "camera/cam-pixel7",
    ];

    private byte[] _jpeg = [];
    private byte[] _rgba = [];
    private TurboDecoder? _turbo;

    public static IEnumerable<string> Cases()
    {
        var file = Environment.GetEnvironmentVariable(CasesVariable);
        return string.IsNullOrEmpty(file) || !File.Exists(file) ? QuickCases : File.ReadAllLines(file).Where(l => l.Length > 0);
    }

    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _jpeg = File.ReadAllBytes(Path.Combine(BenchPaths.Matrix, Case.Replace('/', Path.DirectorySeparatorChar) + ".jpg"));
        var info = JpegDecoder.ReadInfo(_jpeg);
        _rgba = new byte[info.Width * info.Height * 4];
        _turbo = new TurboDecoder(fast: false);
        BaselineDecoder.DecodeTo(_jpeg, _rgba);

        // A benchmark refuses to time wrong output: the streaming reader's rows must be the span decode's.
        var expected = new byte[_rgba.Length];
        JpegDecoder.DecodeTo(_jpeg, expected);
        Streaming();
        if (!_rgba.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"{Case}: the streaming reader's rows differ from JpegDecoder.DecodeTo.");
    }

    [GlobalCleanup]
    public void Cleanup() => _turbo?.Dispose();

    [Benchmark(Baseline = true)]
    public (int, int) Baseline() => BaselineDecoder.DecodeTo(_jpeg, _rgba);

    [Benchmark]
    public (int, int) Current() => JpegDecoder.DecodeTo(_jpeg, _rgba);

    /// <summary>
    /// The streaming reader (<see cref="JpegImageDecoder.OpenRowReader"/>) over the same bytes as a stream, every row read
    /// into the same buffer, 64 KB of input at a time: what a forward-only decode costs over the span decode
    /// (<see cref="Current"/>). Progressive files take its buffered fallback.
    /// </summary>
    [Benchmark]
    public (int, int) Streaming()
    {
        using var reader = JpegImageDecoder.OpenRowReader(new MemoryStream(_jpeg, writable: false), PixelFormat.Rgba32);
        reader.Initialize();
        while (reader.RowsRead < reader.Height)
            reader.ReadRows(_rgba.AsSpan(reader.RowsRead * reader.Width * 4), StreamBand);
        return (reader.Width, reader.Height);
    }

    /// <summary>Rows per <see cref="Streaming"/> read: a typical consumer band.</summary>
    private const int StreamBand = 16;

    [Benchmark]
    public (int, int) LibjpegTurbo() => _turbo!.DecodeRgba(_jpeg, _rgba);

    [Benchmark]
    public (int, int) LibjpegTurboPlanes() => (_turbo!.DecodePlanesInto(_jpeg, _rgba), 0);

    [Benchmark]
    public (int, int) NoIdct() => JpegDiagnostics.DecodeTo(_jpeg, _rgba, JpegAblation.SkipIdct);

    [Benchmark]
    public (int, int) NoUpsample() => JpegDiagnostics.DecodeTo(_jpeg, _rgba, JpegAblation.SkipUpsample);

    [Benchmark]
    public (int, int) NoColor() => JpegDiagnostics.DecodeTo(_jpeg, _rgba, JpegAblation.SkipColor);

    [Benchmark]
    public (int, int) EntropyOnly() =>
        JpegDiagnostics.DecodeTo(_jpeg, _rgba, JpegAblation.SkipIdct | JpegAblation.SkipUpsample | JpegAblation.SkipColor);

    [Benchmark]
    public (int, int) EntropyCore() =>
        JpegDiagnostics.DecodeTo(_jpeg, _rgba, JpegAblation.SkipIdct | JpegAblation.SkipUpsample | JpegAblation.SkipColor | JpegAblation.SkipBlockSink);

    public static readonly string[] CoreMethods = [nameof(Baseline), nameof(Current), nameof(Streaming), nameof(LibjpegTurbo)];
    public static readonly string[] AblationMethods = [nameof(NoIdct), nameof(NoUpsample), nameof(NoColor), nameof(EntropyOnly), nameof(EntropyCore), nameof(LibjpegTurboPlanes)];

    /// <summary>Per-file medians → markdown: speed vs baseline and turbo, and stage costs by ablation.</summary>
    public static string Render(Summary summary, string baselineLabel)
    {
        var medians = new Dictionary<(string Case, string Method), double>();
        foreach (var r in summary.Reports.Where(r => r.Success && r.ResultStatistics is not null))
        {
            var c = r.BenchmarkCase.Parameters["Case"]?.ToString() ?? "";
            medians[(c, r.BenchmarkCase.Descriptor.WorkloadMethod.Name)] = r.ResultStatistics!.Median / 1e6;
        }

        var cases = medians.Keys.Select(k => k.Case).Distinct().ToList();
        var ablated = medians.Keys.Any(k => k.Method == nameof(EntropyOnly));
        string Ms(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        string X(double v) => v.ToString("F2", CultureInfo.InvariantCulture) + "×";
        double? Get(string c, string m) => medians.TryGetValue((c, m), out var v) ? v : null;

        var sb = new StringBuilder();
        sb.AppendLine($"Baseline: {baselineLabel}. Times are BenchmarkDotNet medians in ms.");
        sb.AppendLine();
        sb.Append("| File | turbo | baseline | current | streaming | current ÷ baseline | current ÷ turbo | streaming ÷ current |");
        if (ablated) sb.Append(" IDCT | upsample | colour | entropy + rest | entropy | rest | turbo planes | turbo up+colour |");
        sb.AppendLine();
        sb.Append("|---|---:|---:|---:|---:|---:|---:|---:|");
        if (ablated) sb.Append("---:|---:|---:|---:|---:|---:|---:|---:|");
        sb.AppendLine();

        double logBase = 0, logTurbo = 0, logStream = 0;
        var streamed = 0;
        var n = 0;
        foreach (var c in cases)
        {
            var turbo = Get(c, nameof(LibjpegTurbo));
            var baseline = Get(c, nameof(Baseline));
            var current = Get(c, nameof(Current));
            if (turbo is null || baseline is null || current is null) continue;
            n++;
            logBase += Math.Log(current.Value / baseline.Value);
            logTurbo += Math.Log(current.Value / turbo.Value);
            var streaming = Get(c, nameof(Streaming));
            if (streaming is { } st)
            {
                streamed++;
                logStream += Math.Log(st / current.Value);
            }

            sb.Append($"| `{c}` | {Ms(turbo.Value)} | {Ms(baseline.Value)} | {Ms(current.Value)} | {(streaming is { } s1 ? Ms(s1) : "—")} | " +
                      $"{X(current.Value / baseline.Value)} | {X(current.Value / turbo.Value)} | {(streaming is { } s2 ? X(s2 / current.Value) : "—")} |");
            if (ablated)
            {
                string Cost(string m) => Get(c, m) is { } v ? Ms(current.Value - v) : "—";
                var entropyRest = Get(c, nameof(EntropyOnly));
                var entropy = Get(c, nameof(EntropyCore));
                sb.Append($" {Cost(nameof(NoIdct))} | {Cost(nameof(NoUpsample))} | {Cost(nameof(NoColor))} | {(entropyRest is { } e ? Ms(e) : "—")} |" +
                          $" {(entropy is { } ec ? Ms(ec) : "—")} | {(entropyRest is { } a && entropy is { } b ? Ms(a - b) : "—")} |");
                var planes = Get(c, nameof(LibjpegTurboPlanes));
                sb.Append($" {(planes is { } tp ? Ms(tp) : "—")} | {(planes is { } tp2 ? Ms(turbo.Value - tp2) : "—")} |");
            }

            sb.AppendLine();
        }

        if (n > 0)
            sb.AppendLine($"| **geomean** | | | | | **{X(Math.Exp(logBase / n))}** | **{X(Math.Exp(logTurbo / n))}** | " +
                          $"{(streamed > 0 ? $"**{X(Math.Exp(logStream / streamed))}**" : "")} |{(ablated ? " | | | | | | | |" : "")}");
        if (ablated)
        {
            sb.AppendLine();
            sb.AppendLine("Stage columns: current − (current with that stage skipped). \"entropy + rest\" is the time left with IDCT, " +
                          "upsampling and colour conversion all skipped (headers, entropy decoding, buffer management). \"entropy\" " +
                          "additionally drops every block as soon as it is decoded and emits no rows (headers + entropy decoding); " +
                          "\"rest\" is the difference (block hand-off, strip addressing, row loop, progressive dequantize/scatter). " +
                          "\"turbo planes\" is libjpeg-turbo decoding to component planes (tj3DecompressToYUVPlanes8: entropy + IDCT, " +
                          "no upsampling or colour conversion); \"turbo up+colour\" is its full decode minus that.");
        }

        return sb.ToString();
    }
}
