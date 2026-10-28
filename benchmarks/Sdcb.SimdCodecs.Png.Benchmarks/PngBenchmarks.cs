using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>
/// BenchmarkDotNet over the decode cases selected by <see cref="Settings.CasesVariable"/>. Each method
/// is one contestant of <see cref="DecodeSetup"/>; the driver (<c>bdn decode</c>) filters out the
/// combinations a contestant cannot run (Adam7 for our decoder, libpng when it is absent, …).
/// </summary>
public class PngDecodeBenchmarks
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
    public void CurrentNative() => _run[Names.CurrentNative]();

    [Benchmark]
    public void Baseline() => _run[Names.Baseline]();

    [Benchmark]
    public void ImageSharp() => _run[Names.ImageSharp]();

    [Benchmark]
    public void LibPng() => _run[Names.LibPng]();

    [Benchmark]
    public void NoCrc() => _run[Names.NoCrc]();

    [Benchmark]
    public void NoUnfilter() => _run[Names.NoUnfilter]();

    [Benchmark]
    public void NoExpand() => _run[Names.NoExpand]();

    [Benchmark]
    public void InflateOnly() => _run[Names.InflateOnly]();

    public static readonly string[] Methods = [Names.Current, Names.CurrentNative, Names.Baseline, Names.ImageSharp, Names.LibPng];
}

/// <summary>BenchmarkDotNet over the encode cases; see <see cref="PngDecodeBenchmarks"/>.</summary>
public class PngEncodeBenchmarks
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
    public void CurrentParallel() => _run[Names.CurrentParallel]();

    [Benchmark]
    public void Baseline() => _run[Names.Baseline]();

    [Benchmark]
    public void ImageSharp() => _run[Names.ImageSharp]();

    [Benchmark]
    public void LibPng() => _run[Names.LibPng]();

    public static readonly string[] Methods = [Names.Current, Names.CurrentParallel, Names.Baseline, Names.ImageSharp, Names.LibPng];
}

/// <summary>Runs every contestant on one case before moving to the next.</summary>
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
