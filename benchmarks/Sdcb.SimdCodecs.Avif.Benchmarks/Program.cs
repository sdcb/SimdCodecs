using System.Text.RegularExpressions;
using BenchmarkDotNet.Running;
using Sdcb.SimdCodecs.Avif.Benchmarks;

const string Usage = """
    Sdcb.SimdCodecs AVIF decode benchmark. Run from the repository root:

      info                         machine CPU / SIMD / libavif codec summary
      verify [--fixtures RE]       verify ours vs libavif[dav1d] before timing
      equivalence [--fixtures RE]  verify serial vs parallel decode produce identical RGBA
      single [--reps N] [--fixtures RE]
                                   single-core (1T) ours vs libavif[dav1d]
      multi [--reps N] [--fixtures RE] [--magick]
                                   multi-core ours vs libavif[dav1d] (+ optional Magick.NET)
      anim [--reps N] [--fixtures RE]
                                   animated AVIF sequence decode (1T & MT), ours vs libavif[dav1d]
      quick [--reps N] [--fixtures RE] [--multi]
                                   fast A/B timing of ours only
      ablate [--reps N] [--fixtures RE] [--anim]
                                   single-core stage-by-stage ablation
      stages [--reps N] [--fixtures RE]
                                   single-core stage timers inside tile decoding (AVIF_STAGE_TIMERS=1)
      baseline [git-ref]           build git-ref's (default HEAD) Avif package into .cache/baseline for 'ab'
      ab [--reps N] [--fixtures RE] [--multi]
                                   same-process A/B: the baseline vs the working tree (1T unless --multi)
      [--filter ...] [BDN args]    BenchmarkDotNet (AvifDecodeBenchmarks)
    """;

string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "bdn";
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
int Reps() => int.TryParse(Opt("--reps"), out int r) ? r : 3;

string[] Select(string[] defaults)
{
    string? pattern = Opt("--fixtures");
    if (pattern is null) return defaults;
    return defaults.Where(f => Regex.IsMatch(f, pattern)).ToArray();
}

switch (command)
{
    case "info":
        NativeAvif.SelfCheck();
        Console.WriteLine(BenchRunner.MachineHeader());
        Console.WriteLine($"Fixtures dir: {AvifInputs.ResolveDirectory()}");
        return 0;
    case "verify":
        return BenchRunner.Verify(Select(AvifInputs.StillFixtures));
    case "equivalence":
        return BenchRunner.Equivalence(Select(AvifInputs.StillFixtures));
    case "single":
        Console.WriteLine(BenchRunner.SingleCore(Select(AvifInputs.StillFixtures), Reps()));
        return 0;
    case "multi":
        Console.WriteLine(BenchRunner.MultiCore(Select(AvifInputs.StillFixtures), Reps(), args.Contains("--magick")));
        return 0;
    case "anim":
        Console.WriteLine(BenchRunner.Anim(Select(AvifInputs.AnimFixtures), Reps()));
        return 0;
    case "quick":
        Console.WriteLine(BenchRunner.Quick(Select(AvifInputs.StillFixtures), Reps(), args.Contains("--multi")));
        return 0;
    case "ablate":
    {
        bool anim = args.Contains("--anim");
        Console.WriteLine(BenchRunner.Ablate(Select(anim ? AvifInputs.AnimFixtures : AvifInputs.StillFixtures), Reps(), anim));
        return 0;
    }
    case "stages":
        return BenchRunner.Stages(Select(AvifInputs.StillFixtures), Reps());
    case "baseline":
        Baseline.Build(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "HEAD");
        return 0;
    case "ab":
        return BenchRunner.Ab(Select(AvifInputs.StillFixtures), int.TryParse(Opt("--reps"), out int abReps) ? abReps : 7, args.Contains("--multi"));
    case "help":
        Console.WriteLine(Usage);
        return 0;
    default:
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
}
