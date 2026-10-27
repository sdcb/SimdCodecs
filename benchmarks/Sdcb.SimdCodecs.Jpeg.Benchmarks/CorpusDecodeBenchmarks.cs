using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Sdcb.SimdCodecs.Jpeg;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// Whole-image, single-threaded JPEG decode of the corpus matrix: Sdcb.SimdCodecs vs
/// libjpeg-turbo (accurate defaults, and fastdct+fastupsample for reference) vs
/// ImageSharp. Ours and libjpeg-turbo decode into a reused RGBA buffer; ImageSharp
/// has no decode-into-caller-buffer API, so it allocates its image per call (its
/// managed allocation is reported, and its pooled/unmanaged memory shows up in the
/// separate peak-working-set measurement).
/// <para>The <see cref="Case"/> list comes from the driver (see <c>Program</c>), which
/// runs the correctness gate (<see cref="Validation"/>) on exactly these files first.</para>
/// </summary>
public class CorpusDecodeBenchmarks
{
    public const string CasesVariable = "SIMDCODECS_BENCH_CASES";

    private byte[] _jpeg = [];
    private byte[] _rgba = [];
    private TurboDecoder? _turbo;
    private TurboDecoder? _turboFast;

    public static IEnumerable<string> Cases()
    {
        var file = Environment.GetEnvironmentVariable(CasesVariable);
        return string.IsNullOrEmpty(file) || !File.Exists(file)
            ? MatrixCase.LoadIndex().Select(c => c.Id)
            : File.ReadAllLines(file).Where(l => l.Length > 0);
    }

    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var path = Path.Combine(BenchPaths.Matrix, Case.Replace('/', Path.DirectorySeparatorChar) + ".jpg");
        _jpeg = File.ReadAllBytes(path);
        var info = JpegDecoder.ReadInfo(_jpeg);
        _rgba = new byte[info.Width * info.Height * 4];
        _turbo = new TurboDecoder(fast: false);
        _turboFast = new TurboDecoder(fast: true);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _turbo?.Dispose();
        _turboFast?.Dispose();
    }

    [Benchmark(Baseline = true)]
    public (int, int) LibjpegTurbo() => _turbo!.DecodeRgba(_jpeg, _rgba);

    [Benchmark]
    public (int, int) LibjpegTurboFast() => _turboFast!.DecodeRgba(_jpeg, _rgba);

    [Benchmark]
    public (int, int) Ours() => JpegDecoder.DecodeTo(_jpeg, _rgba);

    [Benchmark]
    public int ImageSharp()
    {
        using var image = Image.Load<Rgba32>(Contestants.ImageSharpOptions, _jpeg);
        return image.Width;
    }
}

/// <summary>
/// Runs every contestant on one file before moving to the next file, so slow drift in
/// machine state (thermals, background load) lands on all contestants alike instead of
/// on whichever one happened to be scheduled last.
/// </summary>
internal sealed class InterleavedOrderer(params string[] methodOrder) : DefaultOrderer
{
    private readonly string[] _methodOrder = methodOrder.Length > 0 ? methodOrder : ["LibjpegTurbo", "Ours", "ImageSharp", "LibjpegTurboFast"];

    public override IEnumerable<BenchmarkCase> GetExecutionOrder(
        ImmutableArray<BenchmarkCase> benchmarksCase, IEnumerable<BenchmarkLogicalGroupRule>? order = null) =>
        benchmarksCase
            .OrderBy(b => b.Parameters.Items.FirstOrDefault()?.Value?.ToString(), StringComparer.Ordinal)
            .ThenBy(b => Array.IndexOf(_methodOrder, b.Descriptor.WorkloadMethod.Name));

    public override IEnumerable<BenchmarkCase> GetSummaryOrder(ImmutableArray<BenchmarkCase> benchmarksCase, Summary summary) =>
        GetExecutionOrder(benchmarksCase);
}
