using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Jpeg2000;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal static class PeakMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(nint process, out ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    public static long CurrentWorkingSet()
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetProcessMemoryInfo(GetCurrentProcess(), out var counters, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
                return (long)counters.WorkingSetSize;
        }
        return Environment.WorkingSet;
    }

    public static long MeasureAllocatedBytes(byte[] data)
    {
        // Warmup
        _ = Jpeg2000Decoder.Decode(data);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var img = Jpeg2000Decoder.Decode(data);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(img);
        return allocated;
    }

    public sealed record MemoryResult(long BaselineWs, long PeakWs, long WsRise, long ManagedAllocated);

    public static MemoryResult MeasureSimdCodecsMemory(byte[] data)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baselineWs = CurrentWorkingSet();
        var allocated = MeasureAllocatedBytes(data);
        var peakWs = CurrentWorkingSet();

        return new MemoryResult(baselineWs, peakWs, Math.Max(0, peakWs - baselineWs), allocated);
    }
}
