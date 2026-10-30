using System.Text;
using Sdcb.SimdCodecs.QoiPnm.Benchmarks;

const string Usage = """
    Sdcb.SimdCodecs QOI / Netpbm encode and decode benchmark. Run from the repository root with
    dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.QoiPnm.Benchmarks -- <command> [options]:

      matrix [decode|encode|all]   every case × every contestant, in-process interleaved timing; writes .cache/work/matrix.md
      verify                       correctness of every contestant on every selected case, no timing
      baseline [git-ref]           build git-ref's (default HEAD) Sdcb.SimdCodecs.Qoi / .Pnm as the A/B baseline

    Options:
      --cases RE     only cases whose name matches RE (e.g. 'qoi', 'photo-ppm16', '^screen-p')
      --size WxH     image size (default 1920x1080)
      --rounds N     matrix rounds per contestant (default 9)
    """;

Environment.SetEnvironmentVariable(BenchPaths.RootVariable, BenchPaths.Root);
var command = args.Length > 0 ? args[0] : "help";
string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
if (Opt("--size") is { } sizeOption) Environment.SetEnvironmentVariable(Settings.SizeVariable, sizeOption);
if (Opt("--cases") is { } casesOption) Environment.SetEnvironmentVariable(Settings.CasesVariable, casesOption);

switch (command)
{
    case "baseline":
        Baseline.Build(args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "HEAD");
        return 0;
    case "verify":
    {
        Console.Write(MachineInfo.Describe());
        foreach (var c in Case.Selected)
        {
            var d = Setup.Decode(c);
            var e = Setup.Encode(c);
            Console.WriteLine($"[verify] {c.Name,-20} decode: {string.Join(", ", d.Contestants.Select(x => x.Name))}  " +
                              $"encode: {string.Join(", ", e.Sizes.Select(kv => $"{kv.Key} {kv.Value / 1024.0:F0} KB"))}" +
                              (d.Notes.Count + e.Notes.Count > 0 ? "  notes: " + string.Join("; ", d.Notes.Concat(e.Notes)) : ""));
        }
        Console.WriteLine("[verify] every timed contestant reproduces the reference");
        return 0;
    }
    case "matrix":
    {
        var kind = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : "all";
        var rounds = int.TryParse(Opt("--rounds"), out var r) ? r : 9;
        var sb = new StringBuilder(MachineInfo.Describe());
        Console.Write(sb.ToString());
        if (kind is "decode" or "all") sb.AppendLine().Append(Matrix.Run(decode: true, rounds));
        if (kind is "encode" or "all") sb.AppendLine().Append(Matrix.Run(decode: false, rounds));
        Directory.CreateDirectory(BenchPaths.Work);
        var file = Path.Combine(BenchPaths.Work, "matrix.md");
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine();
        Console.WriteLine(sb.ToString());
        Console.WriteLine("[report] " + file);
        return 0;
    }
    case "help":
        Console.WriteLine(Usage);
        return 0;
    default:
        Console.WriteLine(Usage);
        return 1;
}
