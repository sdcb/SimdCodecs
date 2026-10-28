using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using Sdcb.SimdCodecs.Png.Benchmarks;

const string Usage = """
    Sdcb.SimdCodecs PNG encode / decode benchmark. Run from the repository root with
    dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Png.Benchmarks -- <command> [options]:

      matrix [decode|encode|all]   every case × every contestant, in-process interleaved timing (minutes);
                                   writes .cache/work/matrix.md
      bdn decode|encode            BenchmarkDotNet run (MemoryDiagnoser) of the selected cases; for commit numbers
      verify                       correctness of every contestant on every selected case, no timing
      formats                      decode into BGRA / RGB / BGR / grey: the native path vs decode-to-RGBA + convert, and libpng
                                   in the same layout (default cases: the common colour types; writes .cache/work/formats.md)
      peakmem decode|encode        peak working set per operation, one fresh process per run (native memory included)
      inputs [--force]             (re)generate the decode inputs into .cache/inputs (done on demand otherwise)
      baseline [git-ref]           build git-ref's (default HEAD) Sdcb.SimdCodecs.Png as the A/B baseline

    Options:
      --cases RE     only cases whose name matches RE (e.g. 'photo-rgba8', '^screen-.*-optimal$', 'adam7')
      --size WxH     image size of every case (default 1920x1080)
      --rounds N     matrix rounds per contestant (default 7)
      --ablate       matrix / bdn decode: also time Current with the CRC, unfilter or expansion stage skipped
    Environment: LIBPNG_PATH (libpng shared library), PNG_BENCH_PHOTO (photo source image).
    """;

Environment.SetEnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root);
string command = args.Length > 0 ? args[0] : "help";
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
if (Opt("--size") is { } sizeOption)
{
    Environment.SetEnvironmentVariable(Settings.SizeVariable, sizeOption);
}

if (Opt("--cases") is { } casesOption)
{
    Environment.SetEnvironmentVariable(Settings.CasesVariable, casesOption);
}

if (args.Contains("--ablate"))
{
    Environment.SetEnvironmentVariable(Settings.AblateVariable, "1");
}

if (LibPng.IsAvailable)
{
    LibPng.SelfCheck();
}

switch (command)
{
    case "baseline":
        BaselinePng.Build(args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "HEAD");
        return 0;
    case "inputs":
    {
        var size = Settings.Size;
        foreach (DecodeCase c in DecodeCase.Selected)
        {
            string path = c.PathFor(size);
            if (args.Contains("--force") || !File.Exists(path))
            {
                c.Generate(size, path);
            }

            Console.WriteLine($"[inputs] {c.Name,-22} {new FileInfo(path).Length / 1024.0,8:F0} KB  {path}");
        }

        return 0;
    }
    case "verify":
        return Verify();
    case "matrix":
    {
        string kind = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "all";
        int rounds = int.TryParse(Opt("--rounds"), out int r) ? r : 7;
        var sb = new StringBuilder();
        sb.Append(MachineInfo.Describe());
        Console.Write(sb.ToString());
        if (kind is "decode" or "all")
        {
            sb.AppendLine().Append(Matrix.RunDecode(rounds));
        }

        if (kind is "encode" or "all")
        {
            sb.AppendLine().Append(Matrix.RunEncode(rounds));
        }

        return Write("matrix.md", sb.ToString());
    }
    case "formats":
    {
        if (Opt("--cases") is null)
        {
            Environment.SetEnvironmentVariable(Settings.CasesVariable, Formats.DefaultCases);
        }

        int rounds = int.TryParse(Opt("--rounds"), out int r) ? r : 7;
        Console.Write(MachineInfo.Describe());
        return Write("formats.md", MachineInfo.Describe() + Environment.NewLine + Formats.Run(rounds));
    }
    case "peakmem" when args.Length > 1 && args[1] is "decode" or "encode":
    {
        string report = MachineInfo.Describe() + Environment.NewLine + PeakMemory.Run(args[1] == "decode");
        return Write($"peakmem-{args[1]}.md", report);
    }
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
    string file = Path.Combine(BenchPaths.Work, name);
    File.WriteAllText(file, markdown);
    Console.WriteLine();
    Console.WriteLine(markdown);
    Console.WriteLine("[report] " + file);
    return 0;
}

static int Verify()
{
    Console.Write(MachineInfo.Describe());
    foreach (DecodeCase c in DecodeCase.Selected)
    {
        DecodeSetup s = DecodeSetup.Create(c);
        Console.WriteLine($"[verify] decode {c.Name,-22} timed: {string.Join(", ", s.Contestants.Select(x => x.Name))}" +
                          (s.Notes.Count > 0 ? "  notes: " + string.Join("; ", s.Notes) : ""));
    }

    foreach (EncodeCase c in EncodeCase.Selected)
    {
        EncodeSetup s = EncodeSetup.Create(c);
        Console.WriteLine($"[verify] encode {c.Name,-26} " +
                          string.Join(", ", s.Sizes.Select(kv => $"{kv.Key} {kv.Value / 1024.0:F0} KB")) +
                          (s.Notes.Count > 0 ? "  notes: " + string.Join("; ", s.Notes) : ""));
    }

    Console.WriteLine("[verify] every timed contestant reproduces the reference");
    return 0;
}

static int Bdn(bool decode)
{
    Console.Write(MachineInfo.Describe());
    // Set up every case once in the driver: that is the correctness check, and it tells the filter
    // which contestant can run which case.
    var decodeSetups = decode ? DecodeCase.Selected.ToDictionary(c => c.Name, c => DecodeSetup.Create(c)) : [];
    var encodeSetups = decode ? [] : EncodeCase.Selected.ToDictionary(c => c.Name, c => EncodeSetup.Create(c));
    Dictionary<string, HashSet<string>> runnable = decode
        ? decodeSetups.ToDictionary(kv => kv.Key, kv => kv.Value.Contestants.Select(c => c.Name).ToHashSet())
        : encodeSetups.ToDictionary(kv => kv.Key, kv => kv.Value.Contestants.Select(c => c.Name).ToHashSet());
    string[] caseOrder = [.. runnable.Keys];
    string filter = "^(" + string.Join("|", caseOrder.Select(Regex.Escape)) + ")$";
    Environment.SetEnvironmentVariable(Settings.CasesVariable, filter);

    var environment = new List<EnvironmentVariable>
    {
        new(BenchPaths.RootVariable, BenchPaths.Root),
        new(Settings.CasesVariable, filter),
        new(Settings.SizeVariable, $"{Settings.Size.Width}x{Settings.Size.Height}"),
        new(Settings.AblateVariable, Settings.Ablate ? "1" : "0"),
    };
    foreach (string variable in new[] { "LIBPNG_PATH", "PNG_BENCH_PHOTO" })
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
        {
            environment.Add(new EnvironmentVariable(variable, value));
        }
    }

    Job job = Job.Default
        .WithGcServer(false)
        .WithGcConcurrent(false)
        .WithLaunchCount(1)
        .WithWarmupCount(3)
        .WithIterationCount(9)
        .WithIterationTime(TimeInterval.FromMilliseconds(250))
        .WithEnvironmentVariables(environment.ToArray());

    string[] methods = decode
        ? [.. PngDecodeBenchmarks.Methods, .. Settings.Ablate ? DecodeSetup.Ablations.Select(a => a.Name) : []]
        : PngEncodeBenchmarks.Methods;
    ManualConfig config = ManualConfig.CreateEmpty()
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

    Summary summary = BenchmarkRunner.Run(decode ? typeof(PngDecodeBenchmarks) : typeof(PngEncodeBenchmarks), config);

    var medians = new Dictionary<string, Dictionary<string, double>>();
    foreach (BenchmarkReport report in summary.Reports.Where(r => r.Success && r.ResultStatistics is not null))
    {
        string name = report.BenchmarkCase.Parameters["Case"]?.ToString() ?? "";
        string method = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
        (medians.TryGetValue(name, out var m) ? m : medians[name] = [])[method] = report.ResultStatistics!.Median / 1e6;
        if (report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) is { } b)
        {
            (decode ? decodeSetups[name].Allocated : encodeSetups[name].Allocated)[method] = b;
        }
    }

    string table = decode
        ? Matrix.RenderDecode([.. caseOrder.Where(medians.ContainsKey).Select(n => (decodeSetups[n], medians[n]))], "BenchmarkDotNet medians")
        : Matrix.RenderEncode([.. caseOrder.Where(medians.ContainsKey).Select(n => (encodeSetups[n], medians[n]))], "BenchmarkDotNet medians");
    string cpu = summary.HostEnvironmentInfo.Cpu.Value.ProcessorName ?? "unknown CPU";
    Write(decode ? "bdn-decode.md" : "bdn-encode.md",
        $"- BenchmarkDotNet CPU: {cpu}{Environment.NewLine}" + MachineInfo.Describe() + Environment.NewLine + table);
    return summary.HasCriticalValidationErrors || summary.Reports.Any(r => !r.Success) ? 1 : 0;
}
