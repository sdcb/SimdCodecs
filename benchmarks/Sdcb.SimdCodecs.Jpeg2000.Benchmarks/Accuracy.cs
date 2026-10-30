using System;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal sealed record AccuracyMetrics(
    double ExactPct,
    int PeakError,
    double Mse,
    double Psnr);

internal static class Accuracy
{
    public static AccuracyMetrics Compare(ushort[] actual, ushort[] reference, int maxVal = 255)
    {
        if (actual.Length != reference.Length)
        {
            throw new ArgumentException($"Length mismatch: actual={actual.Length}, reference={reference.Length}");
        }

        long exact = 0;
        var peak = 0;
        double sumSq = 0;

        for (var i = 0; i < actual.Length; i++)
        {
            var diff = Math.Abs(actual[i] - reference[i]);
            if (diff == 0) exact++;
            if (diff > peak) peak = diff;
            sumSq += (double)diff * diff;
        }

        var count = actual.Length;
        var exactPct = count == 0 ? 100.0 : 100.0 * exact / count;
        var mse = count == 0 ? 0.0 : sumSq / count;
        var psnr = mse == 0.0 ? double.PositiveInfinity : 10.0 * Math.Log10((double)maxVal * maxVal / mse);

        return new AccuracyMetrics(exactPct, peak, mse, psnr);
    }
}
