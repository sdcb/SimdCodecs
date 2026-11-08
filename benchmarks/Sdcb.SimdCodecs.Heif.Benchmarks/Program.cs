using BenchmarkDotNet.Running;
using Sdcb.SimdCodecs.Heif.Benchmarks;

const string Usage = """
    Sdcb.SimdCodecs HEIF decode benchmark. Run from the repository root:

      verify                       every contestant against us, before any timing; exit 1 on a difference
      equivalence                  serial and parallel tile decode produce the identical RGBA buffer
      quick [--reps N] [--fixtures RE] [--scaled]
                                   ours only, one CPU (HEIF_BENCH_CPU, default 2): HEVC stage and RGBA, for A/B rounds;
                                   --scaled adds ScaleDenominator 2 / 4 / 8, native vs full decode + box filter
      multi [--reps N] [--fixtures RE]
                                   multi-core decode speed and allocation, ours vs libheif vs Magick
      single [--reps N] [--fixtures RE]
                                   one logical CPU for the whole process, tile parallelism off on every side
      ablate [--reps N] [--fixtures RE]
                                   single-core time with one decode stage switched off at a time
      peakmem [--reps N] [--fixtures RE] [--contestants RE]
                                   peak working set per decode, one fresh process each (ours, libheif, magick;
                                   scaled-dN native scaled decode, boxed-dN full decode + box filter)
      [--filter ...] [BDN args]    BenchmarkDotNet (HeifDecodeBenchmarks)

    Every contestant decodes in this process on the same bytes. Native competitors:
      heif.dll     libheif 1.23.1   (HEIF container + libde265 at the full pipeline)
      libde265.dll libde265 1.1.1   (the HEVC stage alone)
    Located through HEIF_NATIVE_DIR, else benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/heif-native,
    else this machine's vcpkg tree. Fixtures through HEIF_INPUTS, else the test fixtures directory.
    """;

string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "bdn";
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
int Reps() => int.TryParse(Opt("--reps"), out int r) ? r : 5;

string[] Select(bool defaultAll)
{
    var pattern = Opt("--fixtures");
    if (pattern is null) return defaultAll ? HeifInputs.Fixtures : [];
    return HeifInputs.Fixtures.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, pattern)).ToArray();
}

switch (command)
{
    case "verify":
    {
        var results = Verify.Run();
        int exitCode = Verify.Print(results);
        return exitCode;
    }
    case "single":
        Console.WriteLine(SingleCore.Run(Select(defaultAll: true), Reps(), verify: true));
        return 0;
    case "quick":
        Console.WriteLine(Quick.Run(Select(defaultAll: true), Reps(), args.Contains("--scaled")));
        return 0;
    case "multi":
        Console.WriteLine(MultiCore.Run(Select(defaultAll: true), Reps()));
        return 0;
    case "ablate":
        Console.WriteLine(Ablation.Run(Select(defaultAll: true), Reps()));
        return 0;
    case "peakmem":
        Console.WriteLine(PeakMemory.Run(Select(defaultAll: true), Reps(), Opt("--contestants")));
        return 0;
    case "peakmem-child":
        return PeakMemory.Child(args[1], args[2]);
    case "equivalence":
        return SerialEquivalence.Run(Select(defaultAll: true));
    case "help":
        Console.WriteLine(Usage);
        return 0;
    default:
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
}
