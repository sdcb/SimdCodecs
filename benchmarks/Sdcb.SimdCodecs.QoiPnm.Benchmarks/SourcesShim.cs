namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>
/// What the shared <c>Sources.cs</c> (linked from the PNG benchmark) needs from its project: the benchmark
/// directory, from which it finds the JPEG benchmark's corpus masters next door.
/// </summary>
internal static class BenchPaths
{
    public static string Root => QoiPnm.Benchmarks.BenchPaths.Root;
}
