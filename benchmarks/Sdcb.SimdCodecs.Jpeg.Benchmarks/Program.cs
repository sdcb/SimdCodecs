using System.Globalization;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

internal static class Program
{
    private const string Usage = """
        Sdcb.SimdCodecs JPEG decode benchmark. Run from benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks:

          dotnet run -c Release -- prepare                 build masters + libjpeg-turbo test matrix (run the fetch scripts first)
          dotnet run -c Release -- validate [--groups RE] [--images RE]
                                                           correctness gate only (pairwise RGB PSNR > 40 dB)
          dotnet run -c Release -- bench [--groups RE] [--images RE]
                                                           correctness gate, then BenchmarkDotNet over the matrix
          dotnet run -c Release -- peakmem                 peak working set, one fresh process per decode
          dotnet run -c Release -- accuracy [--groups RE] [--images RE] [--gate]
                                                           coefficient / plane / RGB accuracy tables (3 MP tier + camera);
                                                           --gate checks the output-change quality gate against the
                                                           baseline build and libjpeg-turbo (exit code 1 on failure)
          dotnet run -c Release -- report [bdn.json]       write results/<date>-<cpu>.md
          dotnet run -c Release -- synthetic [BDN args]    the synthetic 2048x1536 micro benchmark
          dotnet run -c Release -- baseline [git-ref]      build git-ref's (default HEAD) decoder as the A/B baseline
          dotnet run -c Release -- ab [--ablate] [--groups RE] [--images RE]
                                                           same-process A/B: current vs baseline vs libjpeg-turbo on the quick
                                                           subset (or the selected files); --ablate adds per-stage costs
          dotnet run -c Release -- formats [--groups RE] [--images RE] [--rounds N]
                                                           decode into BGRA / RGB / BGR / grey: the native path vs decode-to-RGBA +
                                                           convert, and libjpeg-turbo in the same layout (quick subset by default)
          dotnet run -c Release -- encode [--mode baseline|optimize|progressive|all] [--reps N] [--max-dim N]
                                          [--baseline] [--only NAME] [--gate]
                                                           encoder comparison on the masters (downscaled to --max-dim, default
                                                           2048): ours [+ baseline build] vs libjpeg-turbo vs ImageSharp; size,
                                                           PSNR-Y/RGB, SSIM-Y, MP/s; --gate: PSNR-Y >= turbo, size <= turbo +1%
        """;

    private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root);
        var command = args.Length > 0 ? args[0] : "help";
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

        switch (command)
        {
            case "prepare":
                CorpusBuilder.Prepare();
                return 0;
            case "validate":
                Validation.Run(Select(Opt("--groups"), Opt("--images")));
                return 0;
            case "bench":
                return Bench(Select(Opt("--groups"), Opt("--images")));
            case "peakmem":
                PeakMemory.Run(PeakMemory.DefaultCases);
                return 0;
            case "peakmem-child":
                return PeakMemory.Child(args[1], args[2]);
            case "accuracy":
                return Accuracy.Run(Select(Opt("--groups"), Opt("--images")), args.Contains("--gate"));
            case "report":
            {
                var json = args.Length > 1 && File.Exists(args[1]) ? args[1] : LatestBdnJson();
                Console.WriteLine("[report] " + Report.Write(json, Opt("--date")));
                return 0;
            }
            case "synthetic":
                BenchmarkRunner.Run<JpegDecodeBenchmarks>(args: args.Skip(1).ToArray());
                return 0;
            case "baseline":
                BaselineDecoder.Build(args.Length > 1 ? args[1] : "HEAD");
                return 0;
            case "ab":
            {
                var selected = Opt("--groups") is null && Opt("--images") is null
                    ? AbBenchmarks.QuickCases.ToList()
                    : Select(Opt("--groups"), Opt("--images")).Select(c => c.Id).ToList();
                return Ab(selected, args.Contains("--ablate"));
            }
            case "formats":
            {
                var selected = Opt("--groups") is null && Opt("--images") is null
                    ? AbBenchmarks.QuickCases.ToList()
                    : Select(Opt("--groups"), Opt("--images")).Select(c => c.Id).ToList();
                return Formats.Run(selected, int.Parse(Opt("--rounds") ?? "7", CultureInfo.InvariantCulture));
            }
            case "encode":
                return EncodeBenchmark.Run(
                    Opt("--mode") ?? "baseline",
                    int.Parse(Opt("--reps") ?? "5", CultureInfo.InvariantCulture),
                    int.Parse(Opt("--max-dim") ?? "2048", CultureInfo.InvariantCulture),
                    args.Contains("--gate"),
                    args.Contains("--baseline"),
                    Opt("--only"));
            default:
                Console.WriteLine(Usage);
                return command == "help" ? 0 : 1;
        }
    }

    private static List<MatrixCase> Select(string? groups, string? images) =>
        MatrixCase.LoadIndex()
            .Where(c => (groups is null || Regex.IsMatch(c.Group, groups)) && (images is null || Regex.IsMatch(c.Image, images)))
            .ToList();

    private static int Bench(IReadOnlyList<MatrixCase> cases)
    {
        Directory.CreateDirectory(BenchPaths.Work);
        File.WriteAllText(MachineInfo.File, MachineInfo.Describe());
        Console.Write(MachineInfo.Describe());

        // Correctness first: refuse to time a decoder that is on a broken path.
        Validation.Run(cases);

        var list = Path.Combine(BenchPaths.Work, "bench-cases.txt");
        File.WriteAllLines(list, cases.Select(c => c.Id));
        Environment.SetEnvironmentVariable(CorpusDecodeBenchmarks.CasesVariable, list);

        var job = Job.Default
            .WithPlatform(Platform.X64)
            .WithGcServer(false)
            .WithGcConcurrent(false)
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(9)
            .WithIterationTime(TimeInterval.FromMilliseconds(200))
            .WithEnvironmentVariables(
                new EnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root),
                new EnvironmentVariable(CorpusDecodeBenchmarks.CasesVariable, list));

        var config = ManualConfig.CreateEmpty()
            .AddJob(job)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddColumn(StatisticColumn.Median)
            .AddExporter(JsonExporter.Full)
            .AddExporter(MarkdownExporter.GitHub)
            .AddLogger(ConsoleLogger.Default)
            .WithOrderer(new InterleavedOrderer())
            .WithArtifactsPath(Path.Combine(BenchPaths.Work, "BenchmarkDotNet.Artifacts"));

        var summary = BenchmarkRunner.Run<CorpusDecodeBenchmarks>(config);
        return summary.HasCriticalValidationErrors || summary.Reports.Any(r => !r.Success) ? 1 : 0;
    }

    private static int Ab(IReadOnlyList<string> ids, bool ablate)
    {
        var baseline = BaselineDecoder.Description
                       ?? throw new InvalidOperationException("No baseline built. Run 'baseline [git-ref]' first.");
        Directory.CreateDirectory(BenchPaths.Work);
        Console.Write(MachineInfo.Describe());

        // Correctness first: the current decoder must stay within the gate against
        // libjpeg-turbo; the baseline comparison is printed so output changes are visible.
        TurboJpeg.SelfCheck();
        foreach (var id in ids)
        {
            var jpeg = File.ReadAllBytes(Path.Combine(BenchPaths.Matrix, id.Replace('/', Path.DirectorySeparatorChar) + ".jpg"));
            var current = Contestants.DecodeRgba(Contestant.Ours, jpeg, out var w, out var h);
            var turbo = Contestants.DecodeRgba(Contestant.LibjpegTurbo, jpeg, out _, out _);
            var old = new byte[w * h * 4];
            BaselineDecoder.DecodeTo(jpeg, old);
            var vsTurbo = Contestants.PsnrRgb(current, turbo);
            var vsBase = Contestants.PsnrRgb(current, old);
            Console.WriteLine($"[ab] {id}: PSNR current/turbo {vsTurbo:F2} dB, current/baseline {(double.IsPositiveInfinity(vsBase) ? "identical" : vsBase.ToString("F2", CultureInfo.InvariantCulture) + " dB")}");
            if (vsTurbo <= Validation.MinPsnr)
                throw new InvalidOperationException($"{id}: current decoder fails the correctness gate; refusing to benchmark.");
        }

        var list = Path.Combine(BenchPaths.Work, "ab-cases.txt");
        File.WriteAllLines(list, ids);
        Environment.SetEnvironmentVariable(AbBenchmarks.CasesVariable, list);

        var methods = ablate ? AbBenchmarks.CoreMethods.Concat(AbBenchmarks.AblationMethods).ToArray() : AbBenchmarks.CoreMethods;
        var job = Job.Default
            .WithPlatform(Platform.X64)
            .WithGcServer(false)
            .WithGcConcurrent(false)
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(9)
            .WithIterationTime(TimeInterval.FromMilliseconds(200))
            .WithEnvironmentVariables(
                new EnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root),
                new EnvironmentVariable(AbBenchmarks.CasesVariable, list));

        var config = ManualConfig.CreateEmpty()
            .AddJob(job)
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddColumn(StatisticColumn.Median)
            .AddExporter(MarkdownExporter.GitHub)
            .AddLogger(ConsoleLogger.Default)
            .AddFilter(new NameFilter(name => methods.Contains(name)))
            .WithOrderer(new InterleavedOrderer(methods))
            .WithArtifactsPath(Path.Combine(BenchPaths.Work, "BenchmarkDotNet.Artifacts"));

        var summary = BenchmarkRunner.Run<AbBenchmarks>(config);
        var cpu = summary.HostEnvironmentInfo.Cpu.Value.ProcessorName ?? "unknown CPU";
        var md = $"- CPU: {cpu}{Environment.NewLine}" + MachineInfo.Describe() + Environment.NewLine + AbBenchmarks.Render(summary, baseline);
        var file = Path.Combine(BenchPaths.Work, "ab.md");
        File.WriteAllText(file, md);
        Console.WriteLine(md);
        Console.WriteLine("[ab] " + file);
        return summary.HasCriticalValidationErrors || summary.Reports.Any(r => !r.Success) ? 1 : 0;
    }

    private static string LatestBdnJson()
    {
        var dir = Path.Combine(BenchPaths.Work, "BenchmarkDotNet.Artifacts", "results");
        return Directory.GetFiles(dir, "*CorpusDecodeBenchmarks-report-full.json").OrderByDescending(File.GetLastWriteTimeUtc).First();
    }
}

