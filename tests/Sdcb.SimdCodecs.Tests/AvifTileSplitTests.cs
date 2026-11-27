using Sdcb.SimdCodecs.Avif;

namespace Sdcb.SimdCodecs.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AvifTileSplitCollection
{
    public const string Name = "AVIF tile split (switches decoder-wide diagnostics)";
}

/// <summary>
/// The parse / reconstruction split (tiles parsed into records, reconstructed in a superblock wavefront that overlaps
/// the parse) must give exactly the goldens. The decoder only splits large frames on its own, so these tests force it
/// for every fixture, still and animated, on all threads and on one (where every parse precedes every
/// reconstruction). The switches are decoder-wide, hence a collection that runs alone.
/// </summary>
[Collection(AvifTileSplitCollection.Name)]
public sealed class AvifTileSplitTests
{
    private readonly AvifTests _tests = new();

    public static IEnumerable<object[]> Decodable() => AvifTests.Decodable();
    public static IEnumerable<object[]> Animated() => AvifTests.Animated();

    [Theory]
    [MemberData(nameof(Decodable))]
    public void Still_ParsedThenReconstructed_MatchesGolden(string fixture) =>
        WithSplit(threads: -1, () => _tests.PrimaryImage_PlanesDigest_MatchesGolden(fixture));

    [Theory]
    [MemberData(nameof(Animated))]
    public void Animated_ParsedThenReconstructed_MatchesGolden(string fixture) =>
        WithSplit(threads: -1, () => _tests.AnimatedSequence_AllFramesPlanesDigest_MatchesGolden(fixture));

    [Theory]
    [MemberData(nameof(Decodable))]
    public void Still_ParsedThenReconstructedOnOneThread_MatchesGolden(string fixture) =>
        WithSplit(threads: 1, () => _tests.PrimaryImage_PlanesDigest_MatchesGolden(fixture));

    [Theory]
    [MemberData(nameof(Animated))]
    public void Animated_ParsedThenReconstructedOnOneThread_MatchesGolden(string fixture) =>
        WithSplit(threads: 1, () => _tests.AnimatedSequence_AllFramesPlanesDigest_MatchesGolden(fixture));

    private static void WithSplit(int threads, Action test)
    {
        int split = AvifDiagnostics.TileSplit;
        int dop = AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism;
        AvifDiagnostics.TileSplit = 1;
        AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism = threads;
        try
        {
            test();
        }
        finally
        {
            AvifDiagnostics.TileSplit = split;
            AvifDiagnostics.ParallelOptions.MaxDegreeOfParallelism = dop;
        }
    }
}
