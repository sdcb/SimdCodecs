using System.Text.RegularExpressions;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using Sdcb.SimdCodecs.Webp;
using Sdcb.SimdCodecs.Webp.Benchmarks;

const string Usage = """
    Sdcb.SimdCodecs WebP decode benchmark. Run from the repository root with
    dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks -- <command>:

      --filter <pattern> [BDN args]       BenchmarkDotNet switcher (WebpDecodeBenchmarks, WebpAnimationDecodeBenchmarks)
      --profile-webp-lossy                12-second single-thread lossy decode loop (for attaching a profiler)
      baseline [git-ref]                  build git-ref's (default HEAD) decoder as the A/B baseline
      ab [--ablate] [--inputs RE]         BenchmarkDotNet A/B: current vs baseline vs libwebp; --ablate adds stage costs
      quick [--ablate] [--inputs RE|--files PATTERN] [--rounds N] [--mt]
                                          in-process interleaved A/B (seconds); for the inner loop, not for commits
      peakmem [--inputs RE]               peak working set per decode, one fresh process each (current, baseline, libwebp)
      corpus [path-to-cwebp]              encode the synthetic verification corpus into .cache/corpus
      verify                              decode the corpus and benchmark inputs with this decoder and libwebp; compare bytes
    """;

Environment.SetEnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root);
string command = args.Length > 0 ? args[0] : "help";
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
List<string> SelectInputs() => Opt("--inputs") is { } re
    ? AbBenchmarks.AllInputs.Where(i => Regex.IsMatch(i, re)).ToList()
    : AbBenchmarks.AllInputs.ToList();

switch (command)
{
    case "--profile-webp-lossy":
        WebpProfileRunner.RunLossyOpaque();
        return 0;
    case "baseline":
        BaselineDecoder.Build(args.Length > 1 ? args[1] : "HEAD");
        return 0;
    case "quick":
    {
        // --files <pattern> times files from the verification corpus (.cache/corpus) instead.
        List<string> inputs = Opt("--files") is { } pattern
            ? Directory.GetFiles(Corpus.Dir, pattern).Order(StringComparer.Ordinal).ToList()
            : SelectInputs();
        CheckCorrectness(inputs, requireBaseline: false);
        QuickAb.Run(inputs, args.Contains("--ablate"), int.TryParse(Opt("--rounds"), out int r) ? r : 15, args.Contains("--mt"));
        return 0;
    }
    case "ab":
        return Ab(SelectInputs(), args.Contains("--ablate"));
    case "peakmem":
        Console.WriteLine(PeakMemory.Run(SelectInputs()));
        return 0;
    case "peakmem-child":
        return PeakMemory.Child(args[1], args[2]);
    case "corpus":
        Corpus.Build(args.Length > 1 ? args[1] : "cwebp");
        return 0;
    case "verify":
        return Corpus.Verify();
    case "help":
        Console.WriteLine(Usage);
        return 0;
    default:
        if (command.StartsWith('-'))
        {
            BenchmarkSwitcher.FromAssembly(typeof(WebpProfileRunner).Assembly).Run(args);
            return 0;
        }

        Console.WriteLine(Usage);
        return 1;
}

// Correctness first: the current decoder must reproduce libwebp byte for byte on every input
// before anything is timed; a broken fast path cannot post a good number.
static void CheckCorrectness(IReadOnlyList<string> inputs, bool requireBaseline)
{
    foreach (string input in inputs)
    {
        byte[] source = WebpBenchmarkInputs.Read(input);
        WebpInfo info = WebpDecoder.ReadInfo(source);
        byte[] current = new byte[info.Width * info.Height * 4];
        byte[] reference = new byte[current.Length];
        WebpDecoder.DecodeTo(source, current);
        LibWebp.DecodeRgbaInto(source, reference, info.Width, info.Height);
        if (!current.AsSpan().SequenceEqual(reference))
        {
            throw new InvalidOperationException($"{input}: current decoder output differs from libwebp; refusing to benchmark.");
        }

        string vsBaseline = "no baseline";
        if (BaselineDecoder.Description is not null)
        {
            byte[] old = new byte[current.Length];
            BaselineDecoder.DecodeTo(source, old);
            vsBaseline = old.AsSpan().SequenceEqual(current) ? "baseline identical" : "BASELINE DIFFERS";
        }
        else if (requireBaseline)
        {
            throw new InvalidOperationException("No baseline built. Run 'baseline [git-ref]' first.");
        }

        Console.WriteLine($"[check] {input}: identical to libwebp, {vsBaseline}");
    }
}

static int Ab(IReadOnlyList<string> inputs, bool ablate)
{
    string baseline = BaselineDecoder.Description
        ?? throw new InvalidOperationException("No baseline built. Run 'baseline [git-ref]' first.");
    Directory.CreateDirectory(BenchPaths.Work);
    Console.Write(MachineInfo.Describe());
    CheckCorrectness(inputs, requireBaseline: true);

    string filter = "^(" + string.Join("|", inputs.Select(Regex.Escape)) + ")$";
    Environment.SetEnvironmentVariable(AbBenchmarks.InputsVariable, filter);
    string[] allMethods = AbBenchmarks.CoreMethods
        .Concat(AbBenchmarks.AllInputs.SelectMany(AbBenchmarks.AblationMethods)).Distinct().ToArray();

    var environment = new List<EnvironmentVariable>
    {
        new(BenchPaths.RootVariable, BenchPaths.Root),
        new(AbBenchmarks.InputsVariable, filter),
    };
    if (Environment.GetEnvironmentVariable("LIBWEBP_PATH") is { Length: > 0 } libwebp)
    {
        environment.Add(new EnvironmentVariable("LIBWEBP_PATH", libwebp));
    }

    Job job = Job.Default
        .WithPlatform(Platform.X64)
        .WithGcServer(false)
        .WithGcConcurrent(false)
        .WithLaunchCount(1)
        .WithWarmupCount(3)
        .WithIterationCount(9)
        .WithIterationTime(TimeInterval.FromMilliseconds(250))
        .WithEnvironmentVariables(environment.ToArray());

    ManualConfig config = ManualConfig.CreateEmpty()
        .AddJob(job)
        .AddDiagnoser(MemoryDiagnoser.Default)
        .AddColumnProvider(DefaultColumnProviders.Instance)
        .AddColumn(StatisticColumn.Median)
        .AddExporter(MarkdownExporter.GitHub)
        .AddLogger(ConsoleLogger.Default)
        .AddFilter(new SimpleFilter(b =>
        {
            string method = b.Descriptor.WorkloadMethod.Name;
            string input = b.Parameters["Input"]?.ToString() ?? "";
            return AbBenchmarks.CoreMethods.Contains(method) ||
                   (ablate && AbBenchmarks.AblationMethods(input).Contains(method));
        }))
        .WithOrderer(new InterleavedOrderer(allMethods))
        .WithArtifactsPath(Path.Combine(BenchPaths.Work, "BenchmarkDotNet.Artifacts"));

    var summary = BenchmarkRunner.Run<AbBenchmarks>(config);
    string cpu = summary.HostEnvironmentInfo.Cpu.Value.ProcessorName ?? "unknown CPU";
    string md = $"- CPU: {cpu}{Environment.NewLine}" + MachineInfo.Describe() + Environment.NewLine +
                AbBenchmarks.Render(summary, baseline);
    string file = Path.Combine(BenchPaths.Work, "ab.md");
    File.WriteAllText(file, md);
    Console.WriteLine(md);
    Console.WriteLine("[ab] " + file);
    return summary.HasCriticalValidationErrors || summary.Reports.Any(r => !r.Success) ? 1 : 0;
}
