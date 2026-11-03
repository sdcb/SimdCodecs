using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// Proves the serial tile path and the parallel tile path produce the identical buffer, sample for
/// sample. The <c>single</c> command only compares the first pixel of each; this compares the whole
/// image, which is what M2's parallelism work (CTU-row, wavefront) will have to preserve.
/// </summary>
internal static class SerialEquivalence
{
    public static int Run(IReadOnlyList<string> fixtures)
    {
        var failed = 0;
        foreach (var fixture in fixtures)
        {
            var data = HeifInputs.Load(fixture);
            byte[] parallel = HeifDecoder.Decode(data).Rgba.ToArray();
            byte[] serial = WithSerialTiles(() => HeifDecoder.Decode(data).Rgba.ToArray());
            byte[] parallelAgain = HeifDecoder.Decode(data).Rgba.ToArray();

            long serialDiffs = Diff(parallel, serial);
            long repeatDiffs = Diff(parallel, parallelAgain);
            bool ok = serialDiffs == 0 && repeatDiffs == 0;
            if (!ok) failed++;
            Console.WriteLine($"{fixture,-42} parallel-vs-serial {serialDiffs,4} bytes, parallel-vs-parallel {repeatDiffs,4} bytes {(ok ? "" : "FAIL")}");
        }
        return failed == 0 ? 0 : 1;
    }

    private static byte[] WithSerialTiles(Func<byte[]> decode)
    {
        ThreadPool.GetMinThreads(out int minWorker, out int minIo);
        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        ThreadPool.SetMinThreads(1, minIo);
        ThreadPool.SetMaxThreads(1, maxIo);
        try
        {
            return decode();
        }
        finally
        {
            ThreadPool.SetMinThreads(minWorker, minIo);
            ThreadPool.SetMaxThreads(maxWorker, maxIo);
        }
    }

    private static long Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return long.MaxValue;
        long diffs = 0;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) diffs++;
        return diffs;
    }
}
