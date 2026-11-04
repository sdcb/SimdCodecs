using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using ImageMagick;
using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// HEIF / HEIC decode, all contestants <b>in this process</b> on the same <c>byte[]</c>.
/// <para>
/// Three tiers, each a like-for-like pair:
/// </para>
/// <list type="bullet">
/// <item><b>HEVC stage</b> — our per-tile <see cref="Tiles.Decode"/> against libde265's per-tile
/// decode, both from the same NAL units, no container and no colour.</item>
/// <item><b>Composed planes</b> — <see cref="HeifDecoder.DecodeYuv(ReadOnlySpan{byte})"/> against libheif's YCbCr output
/// with transformations ignored.</item>
/// <item><b>Full pipeline</b> — <see cref="HeifDecoder.Decode"/> to RGBA8 against libheif's
/// interleaved-RGBA output and against Magick.NET (libheif through ImageMagick's Q16 HDRI pipeline).</item>
/// </list>
/// <para>
/// The native libraries are black boxes reached by P/Invoke (<see cref="NativeHeif"/>,
/// <see cref="NativeDe265"/>); running them as child processes would fold process start-up and I/O
/// into the number, which is exactly the mistake this project's m0 record made with <c>ffmpeg</c>.
/// Every case decodes the same bytes the managed decoder gets.
/// </para>
/// </summary>
[Config(typeof(HeifBenchmarkConfig))]
[MemoryDiagnoser]
public class HeifDecodeBenchmarks
{
    private byte[] _data = null!;
    private Tile[] _tiles = null!;

    /// <summary>One fixture per case; <c>[Params]</c> keeps every contestant on the same input.</summary>
    [ParamsSource(nameof(Fixtures))]
    public string Fixture { get; set; } = null!;

    public static IEnumerable<string> Fixtures => HeifInputs.Fixtures;

    [GlobalSetup]
    public void Setup()
    {
        _data = HeifInputs.Load(Fixture);
        _tiles = Tiles.Extract(_data);
        // Correctness is verified by the standalone `verify` command before benchmarking;
        // repeating it in every child process would cost more time than the measurements themselves.
    }

    // ── Tier A: HEVC stage ───────────────────────────────────────────────────

    [Benchmark(Description = "HEVC stage, ours")]
    public int HevcStage_Ours()
    {
        var sum = 0;
        foreach (var tile in _tiles) sum += Tiles.Decode(tile).Planes[0][0];
        return sum;
    }

    [Benchmark(Description = "HEVC stage, libde265")]
    public int HevcStage_Libde265()
    {
        var sum = 0;
        foreach (var tile in _tiles) sum += NativeContestants.DecodeTile(tile).Y[0];
        return sum;
    }

    // ── Tier C: composed planes ──────────────────────────────────────────────

    [Benchmark(Description = "planes, ours (DecodeYuv)")]
    public int Planes_Ours()
    {
        var (y, _, _, _, _, _) = HeifDecoder.DecodeYuv(_data);
        return y[0];
    }

    [Benchmark(Description = "planes, libheif (YCbCr 420)")]
    public int Planes_Libheif()
    {
        return NativeContestants.DecodeYuv(_data, int.MaxValue).Y[0];
    }

    // ── Tier B: full pipeline to RGBA ────────────────────────────────────────

    [Benchmark(Description = "RGBA, ours (Decode)")]
    public int Full_Ours()
    {
        using var image = HeifDecoder.Decode(_data);
        return image.Rgba[0];
    }

    [Benchmark(Description = "RGBA, libheif")]
    public int Full_Libheif()
    {
        return NativeContestants.DecodeRgba(_data, int.MaxValue)[0];
    }

    [Benchmark(Description = "RGBA, Magick.NET (libheif+Q16)")]
    public int Full_Magick()
    {
        using var image = new MagickImage(_data);
        using var pixels = image.GetPixels();
        var rgba = pixels.ToByteArray(PixelMapping.RGBA)!;
        return rgba[0];
    }
}

/// <summary>Shared BenchmarkDotNet settings: enough iterations for a stable median, P95 reported.</summary>
internal sealed class HeifBenchmarkConfig : ManualConfig
{
    public HeifBenchmarkConfig()
    {
        AddJob(Job.Default.WithWarmupCount(3).WithIterationCount(10));
        AddColumn(StatisticColumn.P95);
        SummaryStyle = SummaryStyle.Default.WithMaxParameterColumnWidth(36);
    }
}
