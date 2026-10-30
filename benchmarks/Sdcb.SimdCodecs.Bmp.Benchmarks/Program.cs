using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using Sdcb.SimdCodecs.Bmp.Benchmarks;

const string Usage = """
    Sdcb.SimdCodecs BMP / ICO encode and decode benchmark. Run from the repository root with
    dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- <command> [options]:

      matrix [decode|encode|all]   every case × every contestant, in-process interleaved timing; writes .cache/work/matrix.md
      bdn decode|encode            BenchmarkDotNet run (MemoryDiagnoser) of the selected cases; for commit numbers
      verify                       correctness of every contestant on every selected case, no timing
      peakmem decode|encode        peak working set per operation, one fresh process per run (native memory included)
      baseline [git-ref]           build git-ref's (default HEAD) Sdcb.SimdCodecs.Bmp / .Ico as the A/B baseline

    Options:
      --cases RE     only cases whose name matches RE (e.g. 'photo-rgb24', 'rle', '^screen-ico')
      --size WxH     bitmap size (default 1920x1080; icons keep 256x256 and 48x48)
      --rounds N     matrix rounds per contestant (default 9)
    """;

Environment.SetEnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root);
var command = args.Length > 0 ? args[0] : "help";
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
if (Opt("--size") is { } sizeOption) Environment.SetEnvironmentVariable(Settings.SizeVariable, sizeOption);
if (Opt("--cases") is { } casesOption) Environment.SetEnvironmentVariable(Settings.CasesVariable, casesOption);
if (command is not ("help" or "baseline" or "peakmem-child")) Wic.SelfCheck();

switch (command)
{
    case "baseline":
        Baseline.Build(args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "HEAD");
        return 0;
    case "verify":
        return Verify();
    case "matrix":
    {
        var kind = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "all";
        var rounds = int.TryParse(Opt("--rounds"), out var r) ? r : 9;
        var sb = new StringBuilder(MachineInfo.Describe());
        Console.Write(sb.ToString());
        if (kind is "decode" or "all") sb.AppendLine().Append(Matrix.RunDecode(rounds));
        if (kind is "encode" or "all") sb.AppendLine().Append(Matrix.RunEncode(rounds));
        return Write("matrix.md", sb.ToString());
    }
    case "peakmem" when args.Length > 1 && args[1] is "decode" or "encode":
        return Write($"peakmem-{args[1]}.md", MachineInfo.Describe() + Environment.NewLine + PeakMemory.Run(args[1] == "decode"));
    case "peakmem-child":
        return PeakMemory.Child(args[1] == "decode", args[2], args[3]);
    case "bdn" when args.Length > 1 && args[1] is "decode" or "encode":
        return Bdn(args[1] == "decode");
    case "help":
        Console.WriteLine(Usage);
        return 0;
    default:
        Console.WriteLine(Usage);
        return 1;
}

static int Write(string name, string markdown)
{
    Directory.CreateDirectory(BenchPaths.Work);
    var file = Path.Combine(BenchPaths.Work, name);
    File.WriteAllText(file, markdown);
    Console.WriteLine();
    Console.WriteLine(markdown);
    Console.WriteLine("[report] " + file);
    return 0;
}

static int Verify()
{
    Console.Write(MachineInfo.Describe());
    foreach (var c in DecodeCase.Selected)
    {
        var s = DecodeSetup.Create(c);
        Console.WriteLine($"[verify] decode {c.Name,-22} {s.Data.Length / 1024.0,7:F0} KB  timed: {string.Join(", ", s.Contestants.Select(x => x.Name))}" +
                          (s.Notes.Count > 0 ? "  notes: " + string.Join("; ", s.Notes) : ""));
    }
    foreach (var c in EncodeCase.Selected)
    {
        var s = EncodeSetup.Create(c);
        Console.WriteLine($"[verify] encode {c.Name,-22} " + string.Join(", ", s.Sizes.Select(kv => $"{kv.Key} {kv.Value / 1024.0:F0} KB")) +
                          (s.Notes.Count > 0 ? "  notes: " + string.Join("; ", s.Notes) : ""));
    }
    Console.WriteLine("[verify] every timed contestant reproduces the reference");
    return 0;
}

static int Bdn(bool decode)
{
    Console.Write(MachineInfo.Describe());
    // Set up every case once in the driver: that is the correctness check, and it tells the filter which
    // contestant can run which case.
    var decodeSetups = decode ? DecodeCase.Selected.ToDictionary(c => c.Name, c => DecodeSetup.Create(c)) : [];
    var encodeSetups = decode ? [] : EncodeCase.Selected.ToDictionary(c => c.Name, c => EncodeSetup.Create(c));
    Dictionary<string, HashSet<string>> runnable = decode
        ? decodeSetups.ToDictionary(kv => kv.Key, kv => kv.Value.Contestants.Select(c => c.Name).ToHashSet())
        : encodeSetups.ToDictionary(kv => kv.Key, kv => kv.Value.Contestants.Select(c => c.Name).ToHashSet());
    string[] caseOrder = [.. runnable.Keys];
    var filter = "^(" + string.Join("|", caseOrder.Select(Regex.Escape)) + ")$";
    Environment.SetEnvironmentVariable(Settings.CasesVariable, filter);

    var job = Job.Default
        .WithGcServer(false)
        .WithGcConcurrent(false)
        .WithLaunchCount(1)
        .WithWarmupCount(3)
        .WithIterationCount(9)
        .WithIterationTime(TimeInterval.FromMilliseconds(250))
        .WithEnvironmentVariables(
            new EnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root),
            new EnvironmentVariable(Settings.CasesVariable, filter),
            new EnvironmentVariable(Settings.SizeVariable, $"{Settings.Size.Width}x{Settings.Size.Height}"));

    var methods = decode ? BmpDecodeBenchmarks.Methods : BmpEncodeBenchmarks.Methods;
    var config = ManualConfig.CreateEmpty()
        .AddJob(job)
        .AddDiagnoser(MemoryDiagnoser.Default)
        .AddColumnProvider(DefaultColumnProviders.Instance)
        .AddColumn(StatisticColumn.Median)
        .AddExporter(MarkdownExporter.GitHub)
        .AddLogger(ConsoleLogger.Default)
        .AddFilter(new SimpleFilter(b =>
            runnable.TryGetValue(b.Parameters["Case"]?.ToString() ?? "", out var names) && names.Contains(b.Descriptor.WorkloadMethod.Name)))
        .WithOrderer(new InterleavedOrderer(caseOrder, methods))
        .WithArtifactsPath(Path.Combine(BenchPaths.Work, "BenchmarkDotNet.Artifacts"));

    var summary = BenchmarkRunner.Run(decode ? typeof(BmpDecodeBenchmarks) : typeof(BmpEncodeBenchmarks), config);

    var medians = new Dictionary<string, Dictionary<string, double>>();
    foreach (var report in summary.Reports.Where(r => r.Success && r.ResultStatistics is not null))
    {
        var name = report.BenchmarkCase.Parameters["Case"]?.ToString() ?? "";
        var method = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
        (medians.TryGetValue(name, out var m) ? m : medians[name] = [])[method] = report.ResultStatistics!.Median / 1e6;
        if (report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) is { } b)
            (decode ? decodeSetups[name].Allocated : encodeSetups[name].Allocated)[method] = b;
    }

    var table = decode
        ? Matrix.RenderDecode([.. caseOrder.Where(medians.ContainsKey).Select(n => (decodeSetups[n], medians[n]))], "BenchmarkDotNet medians")
        : Matrix.RenderEncode([.. caseOrder.Where(medians.ContainsKey).Select(n => (encodeSetups[n], medians[n]))], "BenchmarkDotNet medians");
    var cpu = summary.HostEnvironmentInfo.Cpu.Value.ProcessorName ?? "unknown CPU";
    Write(decode ? "bdn-decode.md" : "bdn-encode.md", $"- BenchmarkDotNet CPU: {cpu}{Environment.NewLine}" + MachineInfo.Describe() + Environment.NewLine + table);
    return summary.HasCriticalValidationErrors || summary.Reports.Any(r => !r.Success) ? 1 : 0;
}
