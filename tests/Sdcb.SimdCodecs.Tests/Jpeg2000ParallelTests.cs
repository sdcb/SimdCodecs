using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Tier-1 code-blocks (and the per-component wavelets) are decoded on several threads. The contract is
/// that this changes the speed and nothing else: every output sample, and every failure, is what the
/// serial decode produces. The switch is <see cref="Jpeg2000Ablation.MaxDegreeOfParallelism"/>; it is
/// process-wide, so each test restores it (the outputs being identical, a test running beside them cannot notice).
/// </summary>
public class Jpeg2000ParallelTests
{
    private static string[] AllFixtures() =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000"), "*.j2k"),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000"), "*.jp2"),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-lossy"), "*.j2k"),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "jpeg2000-raw"), "*.j2k"),
    ];

    private static T WithDegree<T>(int degree, Func<T> body)
    {
        var before = Jpeg2000Ablation.MaxDegreeOfParallelism;
        Jpeg2000Ablation.MaxDegreeOfParallelism = degree;
        try { return body(); }
        finally { Jpeg2000Ablation.MaxDegreeOfParallelism = before; }
    }

    /// <summary>What a decode ends as: its samples, or the type of the exception it raised.</summary>
    private static (ushort[]? Samples, string? Failure) Outcome(byte[] bytes, int reduce)
    {
        try
        {
            return (Jpeg2000Decoder.Decode(bytes, reduce, TestContext.Current.CancellationToken).Samples, null);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return (null, e.GetType().Name + ": " + e.Message);
        }
    }

    private static void AssertSameBothWays(byte[] bytes, int reduce, string what)
    {
        var serial = WithDegree(1, () => Outcome(bytes, reduce));
        var parallel = WithDegree(8, () => Outcome(bytes, reduce));

        parallel.Failure.ShouldBe(serial.Failure, what);
        if (serial.Samples is null) parallel.Samples.ShouldBeNull(what);
        else parallel.Samples.ShouldBe(serial.Samples, what);
    }

    /// <summary>Every committed fixture, full size and reduced: threads or not, the same samples.</summary>
    [Fact]
    public void EveryFixture_DecodesBitIdenticallyOnOneThreadAndMany()
    {
        var fixtures = AllFixtures();
        fixtures.Length.ShouldBeGreaterThan(30);
        foreach (var file in fixtures)
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var reduce in new[] { 0, 1, 2 })
                AssertSameBothWays(bytes, reduce, $"{Path.GetFileName(file)} reduce {reduce}");
        }
    }

    /// <summary>
    /// Fixtures are 64x64 at most, a handful of blocks each. These are big enough to spread over the workers:
    /// small code-blocks, tiles, layers, both wavelets, three components with the component transform.
    /// </summary>
    [Theory]
    [InlineData(300, 200, 3, "-b 16,16")]
    [InlineData(300, 200, 3, "-b 16,16 -I -q 38")]
    [InlineData(257, 129, 1, "-b 8,8 -r 1")]
    [InlineData(333, 211, 3, "-t 100,90 -b 32,32 -n 4")]
    [InlineData(333, 211, 3, "-t 100,90 -b 32,32 -I -q 40,42,44")]
    [InlineData(200, 160, 3, "-b 16,16 -r 10,5,2")]
    [InlineData(700, 450, 3, "")]
    [InlineData(700, 450, 3, "-I -q 40")]
    [InlineData(641, 333, 1, "-n 3 -b 32,16")]
    public void ManyBlockImages_DecodeBitIdenticallyOnOneThreadAndMany(int width, int height, int components, string options)
    {
        OpenJpegOracle.RequireOrSkip();
        var bytes = OpenJpegOracle.Compress(Jpeg2000TilingAndProgressionTests.Picture(width, height, components), options.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        foreach (var reduce in new[] { 0, 1, 2 })
            AssertSameBothWays(bytes, reduce, $"{options} reduce {reduce}");
    }

    /// <summary>A damaged stream fails the same way, with the same exception type, with threads: nothing is wrapped in an AggregateException.</summary>
    [Fact]
    public void DamagedStreams_FailTheSameWayOnOneThreadAndMany()
    {
        OpenJpegOracle.RequireOrSkip();
        var bytes = OpenJpegOracle.Compress(Jpeg2000TilingAndProgressionTests.Picture(300, 200, 3), "-b", "16,16");

        var failures = 0;
        for (var cut = 40; cut < bytes.Length; cut += bytes.Length / 37)
        {
            var truncated = bytes[..cut];
            AssertSameBothWays(truncated, 0, $"truncated to {cut}");
            if (WithDegree(8, () => Outcome(truncated, 0)).Failure is not null) failures++;
        }

        failures.ShouldBeGreaterThan(0, "no truncation failed, so this proves nothing about failure");

        var random = new Random(7);
        for (var i = 0; i < 40; i++)
        {
            var damaged = (byte[])bytes.Clone();
            for (var k = 0; k < 4; k++) damaged[random.Next(200, damaged.Length)] ^= (byte)random.Next(1, 256);
            AssertSameBothWays(damaged, 0, $"damage {i}");
        }
    }

    /// <summary>A cancelled token stops a multi-threaded decode with OperationCanceledException.</summary>
    [Fact]
    public void ACancelledToken_StopsAThreadedDecode()
    {
        OpenJpegOracle.RequireOrSkip();
        var bytes = OpenJpegOracle.Compress(Jpeg2000TilingAndProgressionTests.Picture(300, 200, 3), "-b", "16,16");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        WithDegree(8, () =>
        {
            Should.Throw<OperationCanceledException>(() => Jpeg2000Decoder.Decode(bytes, cancellationToken: cts.Token));
            return 0;
        });
    }
}
