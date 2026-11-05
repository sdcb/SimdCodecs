using System;
using System.Diagnostics;
using Sdcb.SimdCodecs.Jpeg2000;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal static class Contestants
{
    public const string SimdCodecs = "SimdCodecs";
    public const string LibOpenJp2 = "libopenjp2";

    /// <param name="threads">Threads the decode may use; 1 forces the serial path (the in-run serial baseline), 0 or less is every logical processor.</param>
    public static DecodedResult DecodeSimdCodecs(byte[] data, int threads = 0)
    {
        Jpeg2000Ablation.MaxDegreeOfParallelism = threads <= 0 ? -1 : threads;
        var sw = Stopwatch.StartNew();
        var img = Jpeg2000Decoder.Decode(data);
        sw.Stop();

        return new DecodedResult(img.Width, img.Height, img.Components, img.Samples, sw.Elapsed.TotalMilliseconds);
    }

    public static DecodedResult DecodeLibOpenJp2(byte[] data, int components, int threads = 1)
    {
        var result = OpenJpeg.DecodeDirect(data, threads);
        return new DecodedResult(
            result.Width,
            result.Height,
            result.Components,
            result.Samples,
            result.ElapsedMs);
    }
}

internal sealed record DecodedResult(
    int Width,
    int Height,
    int Components,
    ushort[] Samples,
    double ElapsedMs);
