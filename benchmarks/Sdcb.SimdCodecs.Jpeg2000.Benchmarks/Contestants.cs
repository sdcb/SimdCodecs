using System;
using System.Diagnostics;
using Sdcb.SimdCodecs.Jpeg2000;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal static class Contestants
{
    public const string SimdCodecs = "SimdCodecs";
    public const string LibOpenJp2 = "libopenjp2";

    public static DecodedResult DecodeSimdCodecs(byte[] data)
    {
        var sw = Stopwatch.StartNew();
        var img = Jpeg2000Decoder.Decode(data);
        sw.Stop();

        return new DecodedResult(img.Width, img.Height, img.Components, img.Samples, sw.Elapsed.TotalMilliseconds);
    }

    public static DecodedResult DecodeLibOpenJp2(byte[] data, int components)
    {
        var result = OpenJpeg.DecodeDirect(data);
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
