using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using ImageMagick;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// M0 baseline: measures Magick.NET (libheif) decode only.
/// Sdcb.SimdCodecs.Heif is added as a competitor in M2 once it can decode these images.
/// </summary>
[Config(typeof(HeifBenchmarkConfig))]
[MemoryDiagnoser]
public class HeifDecodeBenchmarks
{
    // Inputs are copied to the output directory by the .csproj CopyToOutputDirectory rule.
    // Run fetch.sh first to download them into Oracle/heif/, then copy or symlink them into
    // heif-inputs/ here, or set HEIF_INPUTS to the directory that contains them.
    private static readonly string InputDir =
        Environment.GetEnvironmentVariable("HEIF_INPUTS")
        ?? Path.Combine(AppContext.BaseDirectory, "heif-inputs");

    private byte[] _main8_3k = null!;
    private byte[] _main10_3k = null!;
    private byte[] _main8_8k = null!;

    [GlobalSetup]
    public void Setup()
    {
        _main8_3k  = LoadInput("hevc-main-3072x4096-grid.heic");
        _main10_3k = LoadInput("hevc-main10-3072x4096-grid.heic");
        _main8_8k  = LoadInput("hevc-main-8192x6144-grid.heic");
    }

    // ── Magick.NET baseline (libheif black-box oracle) ────────────────────────

    [Benchmark(Description = "Magick.NET  hevc-main-3072x4096")]
    public int MagickNet_Main8_3k() => DecodeViaMagick(_main8_3k);

    [Benchmark(Description = "Magick.NET  hevc-main10-3072x4096")]
    public int MagickNet_Main10_3k() => DecodeViaMagick(_main10_3k);

    [Benchmark(Description = "Magick.NET  hevc-main-8192x6144")]
    public int MagickNet_Main8_8k() => DecodeViaMagick(_main8_8k);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int DecodeViaMagick(byte[] data)
    {
        using var image = new MagickImage(data);
        // Force a full pixel read so the decode is not deferred.
        return (int)(image.Width * image.Height);
    }

    private static byte[] LoadInput(string name)
    {
        string path = Path.Combine(InputDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Benchmark input not found: {path}. " +
                $"Run tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch.sh to download it, " +
                $"then copy it into benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/heif-inputs/.", path);
        return File.ReadAllBytes(path);
    }
}

internal sealed class HeifBenchmarkConfig : ManualConfig
{
    public HeifBenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(3).WithIterationCount(10));
        AddColumn(StatisticColumn.P95);
        SummaryStyle = SummaryStyle.Default.WithMaxParameterColumnWidth(36);
    }
}
