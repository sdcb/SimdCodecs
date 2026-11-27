using BenchmarkDotNet.Running;
using Sdcb.SimdCodecs.Avif.Benchmarks;

if (args.Length > 0 && args[0] == "info")
{
    NativeAvif.SelfCheck();
    Console.WriteLine(NativeAvif.CodecSummary());
    Console.WriteLine($"Fixtures dir: {AvifInputs.ResolveDirectory()}");
    return 0;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
return 0;
