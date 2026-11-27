using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Avif.Benchmarks;

internal static class BenchRunner
{
    internal static string MachineHeader()
        => $"{RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture} | .NET {Environment.Version} | " +
           $"CPUs={Environment.ProcessorCount} | Vector128={Vector128.IsHardwareAccelerated}, Vector256={Vector256.IsHardwareAccelerated}, Vector512={Vector512.IsHardwareAccelerated} | " +
           NativeAvif.CodecSummary();

    internal static int Verify(string[] fixtures)
    {
        NativeAvif.SelfCheck();
        Console.WriteLine(MachineHeader());
        int exit = 0;
        foreach (string fixture in fixtures)
        {
            byte[] data = AvifInputs.Load(fixture);
            using var ours = AvifDecoder.Decode(data);
            byte[] lib = NativeAvif.DecodeRgba(data, 1);

            if (ours.Rgba.Length != lib.Length)
            {
                Console.WriteLine($"FAIL {fixture}: length ours={ours.Rgba.Length} lib={lib.Length}");
                exit = 1;
                continue;
            }
            int maxDiff = 0;
            double sumSq = 0;
            for (int i = 0; i < lib.Length; i++)
            {
                int d = Math.Abs(ours.Rgba[i] - lib[i]);
                if (d > maxDiff) maxDiff = d;
                sumSq += d * (double)d;
            }
            double rmse = Math.Sqrt(sumSq / lib.Length);
            // Note: YUV planes are verified bit-exact (0 error) in AvifTests; RGBA may differ slightly by chroma upsampling / color rounding.
            Console.WriteLine($"OK   {fixture,-44} {ours.Width}x{ours.Height} RGBA maxDiff={maxDiff,2} RMSE={rmse:F3}");
        }
        return exit;
    }

    internal static int Equivalence(string[] fixtures)
    {
        ThreadPool.GetMinThreads(out int minW, out int minIo);
        ThreadPool.GetMaxThreads(out int maxW, out int maxIo);
        int exit = 0;
        foreach (string fixture in fixtures)
        {
            byte[] data = AvifInputs.Load(fixture);
            using var parallel = AvifDecoder.Decode(data);
            byte[] serialBytes;
            ThreadPool.SetMinThreads(1, minIo);
            ThreadPool.SetMaxThreads(1, maxIo);
            try
            {
                using var serial = AvifDecoder.Decode(data);
                serialBytes = serial.Rgba.ToArray();
            }
            finally
            {
                ThreadPool.SetMinThreads(minW, minIo);
                ThreadPool.SetMaxThreads(maxW, maxIo);
            }

            if (!parallel.Rgba.SequenceEqual(serialBytes))
            {
                Console.WriteLine($"FAIL {fixture}: parallel RGBA differs from serial RGBA!");
                exit = 1;
            }
            else
            {
                Console.WriteLine($"EQ   {fixture,-44} {parallel.Width}x{parallel.Height} bit-exact");
            }
        }
        return exit;
    }

    internal static string SingleCore(string[] fixtures, int reps)
    {
        NativeAvif.SelfCheck();
        var process = Process.GetCurrentProcess();
        nint prevAffinity = 0;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            prevAffinity = process.ProcessorAffinity;
            process.ProcessorAffinity = (nint)(1L << 2);
        }

        ThreadPool.GetMinThreads(out int minW, out int minIo);
        ThreadPool.GetMaxThreads(out int maxW, out int maxIo);
        ThreadPool.SetMinThreads(1, minIo);
        ThreadPool.SetMaxThreads(1, maxIo);
        ResourceLimits.Thread = 1;

        var lines = new List<string>
        {
            MachineHeader(),
            "Single-core (pinned CPU 2, 1 thread on every decoder; median ms):",
            $"{"fixture",-44} {"ours YUV",9} {"ours RGBA",10} {"dav1d YUV",10} {"dav1d RGBA",11} {"ratio",7} {"alloc MB",9}",
        };

        try
        {
            foreach (string fixture in fixtures)
            {
                byte[] data = AvifInputs.Load(fixture);
                double oursYuv = Median(reps, () => AvifDecoder.DecodePlanes(data).Y[0]);
                double oursRgba = Median(reps, () => { using var img = AvifDecoder.Decode(data); return img.Rgba[0]; });
                double libYuv = Median(reps, () => NativeAvif.DecodeYuv(data, 1));
                double libRgba = Median(reps, () => NativeAvif.DecodeRgba(data, 1)[0]);

                _ = AllocatedMb(() => { using var img = AvifDecoder.Decode(data); });
                double allocMb = AllocatedMb(() => { using var img = AvifDecoder.Decode(data); });

                lines.Add($"{fixture,-44} {oursYuv,9:F1} {oursRgba,10:F1} {libYuv,10:F1} {libRgba,11:F1} {oursRgba / libRgba,6:F2}x {allocMb,9:F1}");
            }
        }
        finally
        {
            ThreadPool.SetMinThreads(minW, minIo);
            ThreadPool.SetMaxThreads(maxW, maxIo);
            ResourceLimits.Thread = 0;
            if (prevAffinity != 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                process.ProcessorAffinity = prevAffinity;
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static string MultiCore(string[] fixtures, int reps, bool includeMagick)
    {
        NativeAvif.SelfCheck();
        ResourceLimits.Thread = 0;
        int cores = Environment.ProcessorCount;

        var lines = new List<string>
        {
            MachineHeader(),
            $"Multi-core ({cores} logical CPUs; median ms):",
            includeMagick
                ? $"{"fixture",-44} {"ours ms",8} {"dav1d ms",9} {"ratio",7} {"magick ms",10} {"alloc MB",9} {"ours MP/s",10} {"dav1d MP/s",11}"
                : $"{"fixture",-44} {"ours ms",8} {"dav1d ms",9} {"ratio",7} {"alloc MB",9} {"ours MP/s",10} {"dav1d MP/s",11}",
        };

        foreach (string fixture in fixtures)
        {
            byte[] data = AvifInputs.Load(fixture);
            var info = AvifDecoder.ReadInfo(data);
            double mp = info.Width * (double)info.Height / 1e6;

            double ours = Median(reps, () => { using var img = AvifDecoder.Decode(data); return img.Rgba[0]; });
            double dav1d = Median(reps, () => NativeAvif.DecodeRgba(data, cores)[0]);

            _ = AllocatedMb(() => { using var img = AvifDecoder.Decode(data); });
            double allocMb = AllocatedMb(() => { using var img = AvifDecoder.Decode(data); });

            if (includeMagick)
            {
                double magick = Median(reps, () =>
                {
                    using var m = new MagickImage(data);
                    using var px = m.GetPixelsUnsafe();
                    return px.ToByteArray(PixelMapping.RGBA)![0];
                });
                lines.Add($"{fixture,-44} {ours,8:F1} {dav1d,9:F1} {ours / dav1d,6:F2}x {magick,10:F1} {allocMb,9:F1} {mp / ours * 1000,10:F1} {mp / dav1d * 1000,11:F1}");
            }
            else
            {
                lines.Add($"{fixture,-44} {ours,8:F1} {dav1d,9:F1} {ours / dav1d,6:F2}x {allocMb,9:F1} {mp / ours * 1000,10:F1} {mp / dav1d * 1000,11:F1}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    internal static string Anim(string[] fixtures, int reps)
    {
        NativeAvif.SelfCheck();
        int cores = Environment.ProcessorCount;
        var lines = new List<string>
        {
            MachineHeader(),
            $"Animated AVIF full-sequence decode to RGBA (1T and {cores}T; median ms):",
            $"{"fixture",-44} {"frames",6} {"ours 1T",9} {"dav1d 1T",9} {"ratio 1T",9} {"ours MT",9} {"dav1d MT",9} {"alloc MB",9}",
        };

        foreach (string fixture in fixtures)
        {
            byte[] data = AvifInputs.Load(fixture);
            int frameCount;
            using (var r = AvifImageDecoder.TryOpenFrames(data, out var reader) ? reader : throw new InvalidOperationException())
                frameCount = r.FrameCount;

            double oursMt = Median(reps, () => DecodeAllOurs(data));
            double dav1dMt = Median(reps, () => NativeAvif.DecodeAllFramesRgba(data, cores));

            // SetMaxThreads(1) is ignored below the processor count; the decoder's own option makes it serial.
            AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism = 1;
            double ours1T, dav1d1T;
            try
            {
                ours1T = Median(reps, () => DecodeAllOurs(data));
                dav1d1T = Median(reps, () => NativeAvif.DecodeAllFramesRgba(data, 1));
            }
            finally
            {
                AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism = -1;
            }

            _ = AllocatedMb(() => DecodeAllOurs(data));
            double allocMb = AllocatedMb(() => DecodeAllOurs(data));
            lines.Add($"{fixture,-44} {frameCount,6} {ours1T,9:F1} {dav1d1T,9:F1} {ours1T / dav1d1T,8:F2}x {oursMt,9:F1} {dav1dMt,9:F1} {allocMb,9:F1}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Same-process A/B against the built baseline (<see cref="Baseline"/>): both decode each fixture to RGBA through
    /// the public <c>TryDecodeInto</c>, their outputs must match byte for byte before anything is timed, and the
    /// repetitions alternate A, B so drift hits both alike. With <paramref name="anim"/>, each rep decodes every frame
    /// of an animated fixture through the public frame reader, and every frame must match.
    /// </summary>
    internal static int Ab(string[] fixtures, int reps, bool multiCore, bool anim = false)
    {
        var process = Process.GetCurrentProcess();
        nint prevAffinity = 0;
        ThreadPool.GetMinThreads(out int minW, out int minIo);
        ThreadPool.GetMaxThreads(out int maxW, out int maxIo);
        if (!multiCore)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                prevAffinity = process.ProcessorAffinity;
                process.ProcessorAffinity = (nint)(1L << 2);
            }
            ThreadPool.SetMinThreads(1, minIo);
            ThreadPool.SetMaxThreads(1, maxIo);
        }

        Console.WriteLine(MachineHeader());
        Console.WriteLine($"A = baseline {Baseline.Description}; B = working tree");
        Console.WriteLine($"A/B ({(multiCore ? $"{Environment.ProcessorCount}T" : "1T CPU 2")}; median of {reps} alternating reps, ms):");
        Console.WriteLine($"{"fixture",-44} {"A ms",9} {"B ms",9} {"B/A",7}");
        int failures = 0;
        try
        {
            foreach (string fixture in fixtures)
            {
                byte[] data = AvifInputs.Load(fixture);
                if (!AvifImageDecoder.TryReadInfo(data, out var info))
                    throw new InvalidDataException($"{fixture}: TryReadInfo failed.");
                int stride = info.Width * 4;
                byte[] a = new byte[stride * info.Height];
                byte[] b = new byte[a.Length];
                Action decodeA, decodeB;
                bool same;
                if (anim)
                {
                    decodeA = () => Baseline.DecodeAllFrames(data, a, stride);
                    decodeB = () => DecodeAllOurs(data, b);
                    var hashes = new List<string>();
                    Baseline.DecodeAllFrames(data, a, stride, _ => hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a))));
                    var ours = new List<string>();
                    DecodeAllOurs(data, b, _ => ours.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b))));
                    same = hashes.SequenceEqual(ours);
                }
                else
                {
                    decodeA = () => Baseline.TryDecodeInto(data, a, stride);
                    decodeB = () => AvifImageDecoder.TryDecodeInto(data, b, stride, PixelFormat.Rgba32);
                    if (!Baseline.TryDecodeInto(data, a, stride) || !AvifImageDecoder.TryDecodeInto(data, b, stride, PixelFormat.Rgba32))
                        throw new InvalidDataException($"{fixture}: decode failed.");
                    same = a.AsSpan().SequenceEqual(b);
                }
                if (!same)
                {
                    Console.WriteLine($"{fixture,-44} OUTPUT DIFFERS from the baseline: not timed");
                    failures++;
                    continue;
                }

                var ta = new List<double>(reps);
                var tb = new List<double>(reps);
                for (int i = 0; i < reps; i++)
                {
                    var sw = Stopwatch.StartNew();
                    decodeA();
                    ta.Add(sw.Elapsed.TotalMilliseconds);
                    sw.Restart();
                    decodeB();
                    tb.Add(sw.Elapsed.TotalMilliseconds);
                }
                ta.Sort();
                tb.Sort();
                double ma = ta[reps / 2], mb = tb[reps / 2];
                Console.WriteLine($"{fixture,-44} {ma,9:F1} {mb,9:F1} {mb / ma,7:F3}");
            }
        }
        finally
        {
            if (!multiCore)
            {
                ThreadPool.SetMinThreads(minW, minIo);
                ThreadPool.SetMaxThreads(maxW, maxIo);
                if (prevAffinity != 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                    process.ProcessorAffinity = prevAffinity;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Single-core stage timers (needs <c>AVIF_STAGE_TIMERS=1</c>): per decode, the total and the instrumented stages
    /// inside it, plus the 'parse' ablation (every reconstruction stage switched off) for comparison. Timers cost a
    /// little themselves; read them as shares, and confirm a change with <c>ab</c>.
    /// </summary>
    internal static int Stages(string[] fixtures, int reps, bool multiCore, bool anim = false)
    {
        if (!AvifDiagnostics.Timers)
        {
            Console.Error.WriteLine("Set AVIF_STAGE_TIMERS=1 for the 'stages' command.");
            return 2;
        }
        var process = Process.GetCurrentProcess();
        nint prevAffinity = 0;
        ThreadPool.GetMinThreads(out int minW, out int minIo);
        ThreadPool.GetMaxThreads(out int maxW, out int maxIo);
        if (!multiCore)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                prevAffinity = process.ProcessorAffinity;
                process.ProcessorAffinity = (nint)(1L << 2);
            }
            ThreadPool.SetMinThreads(1, minIo);
            ThreadPool.SetMaxThreads(1, maxIo);
            AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism = 1; // truly serial: the timers then add up
        }
        Console.WriteLine(MachineHeader());
        Console.WriteLine(multiCore
            ? $"Stage timers ({Environment.ProcessorCount}T; mean ms per decode over {reps} reps). Wall time: total, tiles (parse, or decode), recon (deferred reconstruction), post. CPU time summed over threads: modeinfo / mistore / coeffs:"
            : $"Stage timers (1T CPU 2; mean ms per decode over {reps} reps; tiles = all tile groups, inside it: modeinfo / mistore / coeffs):");
        Console.WriteLine($"{"fixture",-44} {"total",8} {"tiles",8} {"modeinfo",9} {"mistore",8} {"coeffs",8} {"tile-rest",9} {"recon",8} {"post",8} {"parse",8}");
        AvifAblation parseOnly = AblationCases[^1].Flags;
        try
        {
            foreach (string fixture in fixtures)
            {
                byte[] data = AvifInputs.Load(fixture);
                Action decode = anim ? () => DecodeAllOurs(data) : () => { using var img = AvifDecoder.Decode(data); };
                decode();
                Array.Clear(AvifDiagnostics.Ticks);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < reps; i++) decode();
                double total = sw.Elapsed.TotalMilliseconds / reps;
                double Ms(AvifTimer t) => AvifDiagnostics.Ticks[(int)t] * 1000.0 / Stopwatch.Frequency / reps;
                double tiles = Ms(AvifTimer.Tiles), mode = Ms(AvifTimer.ModeInfo), mi = Ms(AvifTimer.MiStore), coeffs = Ms(AvifTimer.Coeffs), recon = Ms(AvifTimer.Recon), post = Ms(AvifTimer.PostFilters);
                AvifDiagnostics.Ablation = parseOnly;
                double parse;
                try { parse = Median(reps, () => { decode(); return 0; }); }
                finally { AvifDiagnostics.Ablation = AvifAblation.None; }
                Console.WriteLine($"{fixture,-44} {total,8:F1} {tiles,8:F1} {mode,9:F1} {mi,8:F1} {coeffs,8:F1} {tiles - mode - mi - coeffs,9:F1} {recon,8:F1} {post,8:F1} {parse,8:F1}");
            }
        }
        finally
        {
            AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism = -1;
            ThreadPool.SetMinThreads(minW, minIo);
            ThreadPool.SetMaxThreads(maxW, maxIo);
            if (prevAffinity != 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                process.ProcessorAffinity = prevAffinity;
        }
        return 0;
    }

    internal static string Quick(string[] fixtures, int reps, bool multiCore)
    {
        var process = Process.GetCurrentProcess();
        nint prevAffinity = 0;
        ThreadPool.GetMinThreads(out int minW, out int minIo);
        ThreadPool.GetMaxThreads(out int maxW, out int maxIo);
        if (!multiCore)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                prevAffinity = process.ProcessorAffinity;
                process.ProcessorAffinity = (nint)(1L << 2);
            }
            ThreadPool.SetMinThreads(1, minIo);
            ThreadPool.SetMaxThreads(1, maxIo);
        }

        var lines = new List<string>
        {
            $"Quick ({(multiCore ? $"{Environment.ProcessorCount}T" : "1T CPU 2")}; median ms):",
            $"{"fixture",-44} {"YUV ms",9} {"RGBA ms",9} {"alloc MB",9}",
        };
        try
        {
            foreach (string fixture in fixtures)
            {
                byte[] data = AvifInputs.Load(fixture);
                double yuv = Median(reps, () => AvifDecoder.DecodePlanes(data).Y[0]);
                double rgba = Median(reps, () => { using var img = AvifDecoder.Decode(data); return img.Rgba[0]; });
                _ = AllocatedMb(() => { using var img = AvifDecoder.Decode(data); });
                double allocMb = AllocatedMb(() => { using var img = AvifDecoder.Decode(data); });
                lines.Add($"{fixture,-44} {yuv,9:F1} {rgba,9:F1} {allocMb,9:F1}");
            }
        }
        finally
        {
            if (!multiCore)
            {
                ThreadPool.SetMinThreads(minW, minIo);
                ThreadPool.SetMaxThreads(maxW, maxIo);
                if (prevAffinity != 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                    process.ProcessorAffinity = prevAffinity;
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static readonly (string Name, AvifAblation Flags)[] AblationCases =
    [
        ("full", AvifAblation.None),
        ("-intra", AvifAblation.SkipIntraPred),
        ("-inter", AvifAblation.SkipInterPred),
        ("-tx", AvifAblation.SkipTransform),
        ("-lpf", AvifAblation.SkipLoopFilter),
        ("-cdef", AvifAblation.SkipCdef),
        ("-lr", AvifAblation.SkipLoopRestoration),
        ("-grain", AvifAblation.SkipFilmGrain),
        ("-color", AvifAblation.SkipColor),
        ("parse", AvifAblation.SkipIntraPred | AvifAblation.SkipInterPred | AvifAblation.SkipTransform |
                  AvifAblation.SkipLoopFilter | AvifAblation.SkipCdef | AvifAblation.SkipSuperres |
                  AvifAblation.SkipLoopRestoration | AvifAblation.SkipFilmGrain | AvifAblation.SkipColor),
    ];

    internal static string Ablate(string[] fixtures, int reps, bool anim)
    {
        var process = Process.GetCurrentProcess();
        nint prevAffinity = 0;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            prevAffinity = process.ProcessorAffinity;
            process.ProcessorAffinity = (nint)(1L << 2);
        }
        ThreadPool.GetMinThreads(out int minW, out int minIo);
        ThreadPool.GetMaxThreads(out int maxW, out int maxIo);
        ThreadPool.SetMinThreads(1, minIo);
        ThreadPool.SetMaxThreads(1, maxIo);

        var header = $"{"fixture",-40}" + string.Concat(AblationCases.Select(c => $" {c.Name,8}"));
        var lines = new List<string>
        {
            "Single-core ablation (pinned CPU 2; ms median; second row = stage share = full - case):",
            header,
        };

        try
        {
            foreach (string fixture in fixtures)
            {
                byte[] data = AvifInputs.Load(fixture);
                var times = new double[AblationCases.Length];
                for (int i = 0; i < AblationCases.Length; i++)
                {
                    AvifDiagnostics.Ablation = AblationCases[i].Flags;
                    times[i] = anim
                        ? Median(reps, () => DecodeAllOurs(data))
                        : Median(reps, () => { using var img = AvifDecoder.Decode(data); return img.Rgba[0]; });
                }
                AvifDiagnostics.Ablation = AvifAblation.None;
                lines.Add($"{fixture,-40}" + string.Concat(times.Select(t => $" {t,8:F1}")));
                lines.Add($"{"  share (full - case)",-40}" + string.Concat(times.Select((t, i) => i == 0 ? $" {"100%",8}" : $" {times[0] - t,8:F1}")));
                lines.Add($"{"  share %",-40}" + string.Concat(times.Select((t, i) => i == 0 ? $" {"",8}" : $" {(times[0] - t) / times[0] * 100,7:F1}%")));
            }
        }
        finally
        {
            AvifDiagnostics.Ablation = AvifAblation.None;
            ThreadPool.SetMinThreads(minW, minIo);
            ThreadPool.SetMaxThreads(maxW, maxIo);
            if (prevAffinity != 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
                process.ProcessorAffinity = prevAffinity;
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static int DecodeAllOurs(byte[] data, byte[]? buf = null, Action<int>? frame = null)
    {
        if (!AvifImageDecoder.TryOpenFrames(data, out var reader))
            throw new InvalidOperationException("TryOpenFrames failed.");
        using (reader)
        {
            if (!AvifImageDecoder.TryReadInfo(data, out var info))
                throw new InvalidOperationException("TryReadInfo failed.");
            int checksum = 0;
            int stride = info.Width * 4;
            buf ??= new byte[stride * info.Height];
            for (int i = 0; i < reader.FrameCount; i++)
            {
                if (!reader.TryDecodeFrameInto(i, buf, stride, PixelFormat.Rgba32))
                    throw new InvalidOperationException($"Frame {i} decode failed.");
                checksum ^= buf[0];
                frame?.Invoke(i);
            }
            return checksum;
        }
    }

    private static double AllocatedMb(Action action)
    {
        long before = GC.GetTotalAllocatedBytes(true);
        action();
        return (GC.GetTotalAllocatedBytes(true) - before) / 1048576.0;
    }

    private static double Median(int reps, Func<int> action)
    {
        action();
        var times = new List<double>(reps);
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        return times[times.Count / 2];
    }
}
