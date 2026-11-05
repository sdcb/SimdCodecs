using System;
using System.Threading.Tasks;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// The one place the decoder's data-parallel loops (wavelet rows and column strips, the component
/// transform, the level shift) are split across threads, so each of them respects
/// <see cref="Jpeg2000Ablation.MaxDegreeOfParallelism"/> and stays serial for work too small to share.
/// <para>
/// Every loop given to it is elementwise or owns disjoint output (no reduction, no shared scratch), so
/// the result does not depend on how the range is cut: the parallel decode is the serial decode.
/// </para>
/// </summary>
internal static class Jpeg2000Parallel
{
    /// <summary>Work below this many elements is not worth waking a thread for.</summary>
    private const int MinimumParallelElements = 1 << 16;

    /// <summary>
    /// Runs <paramref name="body"/> over <c>[0, count)</c> cut into chunks of <paramref name="grain"/>
    /// items: <c>body(start, end)</c> for each chunk. Serial (one chunk, this thread) when the switch says
    /// one thread, or when <paramref name="count"/> items of <paramref name="elementsPerItem"/> elements
    /// each are too little to share.
    /// </summary>
    public static void ForRanges(int count, int grain, long elementsPerItem, Action<int, int> body)
    {
        if (count <= 0) return;

        var degree = Jpeg2000Ablation.EffectiveDegree;
        if (degree == 1 || count <= grain || count * elementsPerItem < MinimumParallelElements)
        {
            body(0, count);
            return;
        }

        var chunks = (count + grain - 1) / grain;
        Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = degree },
            chunk => body(chunk * grain, Math.Min(count, (chunk + 1) * grain)));
    }
}
