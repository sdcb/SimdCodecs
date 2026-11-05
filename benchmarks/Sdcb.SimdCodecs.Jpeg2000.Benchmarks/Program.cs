using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        PrintEnvironment();

        if (!OpenJpeg.IsAvailable)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[WARN] OpenJPEG binaries (opj_decompress / opj_compress) were not found.");
            Console.WriteLine("       Please run `bash tests/Sdcb.SimdCodecs.Tests/Oracle/jpeg2000/fetch.sh` first.");
            Console.ResetColor();
        }

        // `--threads N` sets the thread count of the threaded rows (default: every logical processor).
        var threadsIndex = Array.IndexOf(args, "--threads");
        var threads = threadsIndex >= 0 && threadsIndex + 1 < args.Length ? int.Parse(args[threadsIndex + 1]) : Environment.ProcessorCount;
        var filter = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
        var cases = Cases.All.Where(c => filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (cases.Count == 0)
        {
            Console.WriteLine($"No cases matched filter '{filter}'. Available cases:");
            foreach (var c in Cases.All) Console.WriteLine($"  - {c.Name} ({c.Description})");
            return 1;
        }

        Console.WriteLine($"Running direct in-process benchmark on {cases.Count} test cases: Sdcb.SimdCodecs vs libopenjp2\n");

        var results = new List<BenchmarkRow>();

        foreach (var c in cases)
        {
            Console.WriteLine($"[Case] {c.Name} ({c.Description})");
            byte[] data;
            try
            {
                data = c.LoadData();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  Failed to load case data: {ex.Message}");
                Console.ResetColor();
                continue;
            }

            var sizeKb = data.Length / 1024.0;
            var bpp = (data.Length * 8.0) / (c.Width * c.Height);

            // Correctness before timing: the threaded decode must be the serial decode, sample for sample.
            var simdWarmup = Contestants.DecodeSimdCodecs(data, threads: 1);
            var threaded = Contestants.DecodeSimdCodecs(data, threads);
            if (!threaded.Samples.AsSpan().SequenceEqual(simdWarmup.Samples))
                throw new InvalidOperationException($"{c.Name}: the threaded decode differs from the serial one");

            // 1. SimdCodecs, serial (the in-run baseline) and threaded
            const int rounds = 5;
            var simdMedianMs = Median(rounds, () => Contestants.DecodeSimdCodecs(data, threads: 1).ElapsedMs);
            var simdParMs = Median(rounds, () => Contestants.DecodeSimdCodecs(data, threads).ElapsedMs);

            // 2. libopenjp2 in-process Decode, one thread and `threads`
            DecodedResult? opjDecoded = null;
            var opjMedianMs = 0.0;
            var opjParMs = 0.0;
            if (OpenJpeg.IsAvailable)
            {
                try
                {
                    opjDecoded = Contestants.DecodeLibOpenJp2(data, c.Components);
                    opjMedianMs = Median(rounds, () => Contestants.DecodeLibOpenJp2(data, c.Components).ElapsedMs);
                    opjParMs = Median(rounds, () => Contestants.DecodeLibOpenJp2(data, c.Components, threads).ElapsedMs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  libopenjp2 decode error: {ex.Message}");
                }
            }

            // 3. Accuracy Comparison (SimdCodecs vs libopenjp2)
            AccuracyMetrics? acc = null;
            if (opjDecoded is not null && simdWarmup.Samples.Length == opjDecoded.Samples.Length)
            {
                acc = Accuracy.Compare(simdWarmup.Samples, opjDecoded.Samples, 255);
            }

            // 4. Memory Measurement
            var mem = PeakMemory.MeasureSimdCodecsMemory(data);

            var row = new BenchmarkRow(
                Case: c,
                FileBytes: data.Length,
                Bpp: bpp,
                SimdMs: simdMedianMs,
                SimdParMs: simdParMs,
                OpjMs: opjMedianMs,
                OpjParMs: opjParMs,
                RatioVsOpj: opjMedianMs > 0 ? simdMedianMs / opjMedianMs : 0.0,
                Accuracy: acc,
                AllocatedBytes: mem.ManagedAllocated,
                PeakWsBytes: mem.PeakWs);

            results.Add(row);

            var psnrText = acc is not null
                ? (double.IsPositiveInfinity(acc.Psnr) ? "inf (Exact 100%)" : $"{acc.Psnr:F2} dB (Peak err {acc.PeakError})")
                : "N/A";

            Console.WriteLine($"  Size: {sizeKb:F1} KB ({bpp:F2} bpp)");
            Console.WriteLine($"  SimdCodecs 1 thread:  {simdMedianMs,7:F2} ms  ({GetThroughput(c, simdMedianMs):F1} MP/s)");
            Console.WriteLine($"  SimdCodecs {threads} threads: {simdParMs,7:F2} ms  ({GetThroughput(c, simdParMs):F1} MP/s) -> {simdMedianMs / simdParMs:F2}x faster than serial");
            Console.WriteLine($"  libopenjp2 {threads} threads: {opjParMs,7:F2} ms  ({GetThroughput(c, opjParMs):F1} MP/s) -> SimdCodecs {threads} threads is {simdParMs / opjParMs:F2}x of it");
            Console.WriteLine($"  libopenjp2 1 thread:  {opjMedianMs,7:F2} ms  ({GetThroughput(c, opjMedianMs):F1} MP/s) -> SimdCodecs is {simdMedianMs / opjMedianMs,4:F2}x of libopenjp2");
            Console.WriteLine($"  PSNR vs libopenjp2: {psnrText}");
            Console.WriteLine($"  Memory: {mem.ManagedAllocated / 1024.0,6:F1} KB managed alloc per decode\n");
        }

        PrintMarkdownTable(results, threads);
        return 0;
    }

    private static double Median(int rounds, Func<double> run)
    {
        var times = new List<double>();
        for (var r = 0; r < rounds; r++) times.Add(run());
        times.Sort();
        return times[rounds / 2];
    }

    private static double GetThroughput(BenchCase c, double ms)
    {
        if (ms <= 0) return 0;
        var megapixels = (double)c.Width * c.Height / 1_000_000.0;
        return megapixels / (ms / 1000.0);
    }

    private static void PrintEnvironment()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine(" Sdcb.SimdCodecs vs libopenjp2 (OpenJPEG) In-Process Benchmark Harness");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"OS:             {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        Console.WriteLine($".NET Runtime:   {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"CPU Cores:      {Environment.ProcessorCount} logical processors");
        Console.WriteLine($"AVX2 Support:   {Vector256.IsHardwareAccelerated && System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        Console.WriteLine($"AVX-512 Support:{System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
        Console.WriteLine($"OpenJPEG Tool:  {(OpenJpeg.IsAvailable ? OpenJpeg.DecompressExe : "Not found")}");
        Console.WriteLine("================================================================================\n");
    }

    private static void PrintMarkdownTable(List<BenchmarkRow> rows, int threads)
    {
        Console.WriteLine($"\n### Benchmark Results Summary: SimdCodecs vs libopenjp2 (1 thread, and {threads} threads)\n");
        Console.WriteLine($"| Case | Dimensions | Filter | File Size | Simd 1T | Simd {threads}T | Simd speed-up | opj 1T | opj {threads}T | opj speed-up | Simd 1T / opj 1T | Simd {threads}T / opj {threads}T | PSNR vs libopenjp2 | Managed Alloc (1T) |");
        Console.WriteLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var r in rows)
        {
            var filter = r.Case.Lossless ? "5/3 (Lossless)" : "9/7 (Lossy)";
            var psnr = r.Accuracy is not null
                ? (double.IsPositiveInfinity(r.Accuracy.Psnr) ? "Exact (inf)" : $"{r.Accuracy.Psnr:F2} dB (peak={r.Accuracy.PeakError})")
                : "N/A";
            var allocStr = r.AllocatedBytes >= 1048576
                ? $"{r.AllocatedBytes / 1048576.0:F2} MB"
                : $"{r.AllocatedBytes / 1024.0:F1} KB";

            Console.WriteLine($"| `{r.Case.Name}` | {r.Case.Width}x{r.Case.Height} ({r.Case.Components}c) | {filter} | {r.FileBytes / 1024.0:F1} KB | {r.SimdMs:F2} ms | **{r.SimdParMs:F2} ms** | {r.SimdMs / r.SimdParMs:F2}x | {r.OpjMs:F2} ms | {r.OpjParMs:F2} ms | {(r.OpjParMs > 0 ? r.OpjMs / r.OpjParMs : 0):F2}x | {r.RatioVsOpj:F2}x | **{(r.OpjParMs > 0 ? r.SimdParMs / r.OpjParMs : 0):F2}x** | {psnr} | {allocStr} |");
        }
        Console.WriteLine();
    }

    private sealed record BenchmarkRow(
        BenchCase Case,
        long FileBytes,
        double Bpp,
        double SimdMs,
        double SimdParMs,
        double OpjMs,
        double OpjParMs,
        double RatioVsOpj,
        AccuracyMetrics? Accuracy,
        long AllocatedBytes,
        long PeakWsBytes);
}
