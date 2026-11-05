using System;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// Switches for measuring and testing the decoder, not part of the public API (the tests and the
/// benchmark project see it through <c>InternalsVisibleTo</c>).
/// <para>
/// <see cref="MaxDegreeOfParallelism"/> bounds the threads one decode may use. The default is every
/// logical processor; 1 forces the serial path, which is how the benchmark builds its serial
/// baseline in the same run as the parallel number, and how the tests check that the parallel
/// output is bit-identical to the serial one.
/// </para>
/// </summary>
internal static class Jpeg2000Ablation
{
    private static volatile int _maxDegreeOfParallelism = -1;

    /// <summary>Threads one decode may use: -1 (the default) for every logical processor, otherwise at least 1.</summary>
    public static int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        set
        {
            if (value == 0 || value < -1) throw new ArgumentOutOfRangeException(nameof(value));
            _maxDegreeOfParallelism = value;
        }
    }

    /// <summary>The thread count a decode uses now: at least 1.</summary>
    public static int EffectiveDegree =>
        _maxDegreeOfParallelism is var degree and > 0 ? degree : Math.Max(1, Environment.ProcessorCount);
}
