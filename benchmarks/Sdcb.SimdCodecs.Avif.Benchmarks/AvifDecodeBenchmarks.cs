using BenchmarkDotNet.Attributes;
using ImageMagick;

namespace Sdcb.SimdCodecs.Avif.Benchmarks;

/// <summary>
/// M0 baseline AVIF decode benchmarks: <c>libavif[dav1d]</c> (in-process via P/Invoke) and <c>Magick.NET</c>.
/// <c>Sdcb.SimdCodecs.Avif</c> is added as a contestant in M1/M2.
/// </summary>
[MemoryDiagnoser]
public class AvifDecodeBenchmarks
{
    private byte[] _data = null!;

    [ParamsSource(nameof(Fixtures))]
    public string Fixture { get; set; } = null!;

    public static IEnumerable<string> Fixtures => AvifInputs.Fixtures;

    [GlobalSetup]
    public void Setup()
    {
        NativeAvif.SelfCheck();
        _data = AvifInputs.Load(Fixture);
    }

    [Benchmark(Description = "RGBA, Sdcb.SimdCodecs.Avif")]
    public byte SdcbAvif()
    {
        using var img = AvifDecoder.Decode(_data);
        return img.Rgba[0];
    }

    [Benchmark(Description = "RGBA, libavif[dav1d] (1T)")]
    public int LibAvif_SingleThread()
        => NativeAvif.DecodeRgba(_data, maxThreads: 1)[0];

    [Benchmark(Description = "RGBA, libavif[dav1d] (all cores)")]
    public int LibAvif_MultiThread()
        => NativeAvif.DecodeRgba(_data, maxThreads: Environment.ProcessorCount)[0];

    [Benchmark(Description = "RGBA, Magick.NET")]
    public byte MagickNet()
    {
        using var img = new MagickImage(_data);
        using var pixels = img.GetPixelsUnsafe();
        return pixels.ToByteArray(PixelMapping.RGBA)![0];
    }
}
