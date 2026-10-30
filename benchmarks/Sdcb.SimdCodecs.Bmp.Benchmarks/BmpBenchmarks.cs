using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

/// <summary>
/// BenchmarkDotNet over the decode cases selected by <see cref="Settings.CasesVariable"/>. Each method is one
/// contestant of <see cref="DecodeSetup"/>; the driver (<c>bdn decode</c>) filters out the combinations a contestant
/// cannot run (ImageSharp on icons, WIC off Windows, …).
/// </summary>
public class BmpDecodeBenchmarks
{
    private Dictionary<string, Action> _run = [];

    public static IEnumerable<string> Cases() => DecodeCase.Selected.Select(c => c.Name);

    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = "";

    [GlobalSetup]
    public void Setup() => _run = DecodeSetup.Create(DecodeCase.Find(Case)).Contestants.ToDictionary(c => c.Name, c => c.Run);

    [Benchmark]
    public void Current() => _run[Names.Current]();

    [Benchmark]
    public void CurrentBgra() => _run[Names.CurrentBgra]();

    [Benchmark]
    public void Baseline() => _run[Names.Baseline]();

    [Benchmark]
    public void ImageSharp() => _run[Names.ImageSharp]();

    [Benchmark]
    public void Wic() => _run[Names.Wic]();

    public static readonly string[] Methods = [Names.Current, Names.CurrentBgra, Names.Baseline, Names.ImageSharp, Names.Wic];
}

/// <summary>BenchmarkDotNet over the encode cases; see <see cref="BmpDecodeBenchmarks"/>.</summary>
public class BmpEncodeBenchmarks
{
    private Dictionary<string, Action> _run = [];

    public static IEnumerable<string> Cases() => EncodeCase.Selected.Select(c => c.Name);

    [ParamsSource(nameof(Cases))]
    public string Case { get; set; } = "";

    [GlobalSetup]
    public void Setup() => _run = EncodeSetup.Create(EncodeCase.Find(Case)).Contestants.ToDictionary(c => c.Name, c => c.Run);

    [Benchmark]
    public void Current() => _run[Names.Current]();

    [Benchmark]
    public void CurrentBgra() => _run[Names.CurrentBgra]();

    [Benchmark]
    public void Baseline() => _run[Names.Baseline]();

    [Benchmark]
    public void ImageSharp() => _run[Names.ImageSharp]();

    [Benchmark]
    public void Wic() => _run[Names.Wic]();

    public static readonly string[] Methods = BmpDecodeBenchmarks.Methods;
}

/// <summary>Runs every contestant of a case back to back before the next case, in the matrix's order.</summary>
internal sealed class InterleavedOrderer(string[] caseOrder, string[] methodOrder) : DefaultOrderer
{
    public override IEnumerable<BenchmarkCase> GetExecutionOrder(
        ImmutableArray<BenchmarkCase> benchmarksCase, IEnumerable<BenchmarkLogicalGroupRule>? order = null) =>
        benchmarksCase
            .OrderBy(b => Array.IndexOf(caseOrder, b.Parameters["Case"]?.ToString()))
            .ThenBy(b => Array.IndexOf(methodOrder, b.Descriptor.WorkloadMethod.Name));

    public override IEnumerable<BenchmarkCase> GetSummaryOrder(ImmutableArray<BenchmarkCase> benchmarksCase, Summary summary) =>
        GetExecutionOrder(benchmarksCase);
}
