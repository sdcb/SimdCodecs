namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>Locates the benchmark project directory and its git-ignored cache.</summary>
internal static class BenchPaths
{
    public const string RootVariable = "SIMDCODECS_BENCH_ROOT";

    private static readonly Lazy<string> _root = new(FindRoot);

    /// <summary><c>benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks</c>.</summary>
    public static string Root => _root.Value;

    public static string Cache => Path.Combine(Root, ".cache");
    public static string Originals => Path.Combine(Cache, "corpus", "originals");
    public static string Masters => Path.Combine(Cache, "corpus", "masters");
    public static string Matrix => Path.Combine(Cache, "corpus", "matrix");
    public static string Work => Path.Combine(Cache, "work");
    public static string Manifest => Path.Combine(Root, "corpus", "manifest.tsv");
    public static string ResultsDir => Path.GetFullPath(Path.Combine(Root, "results"));

    private static string FindRoot()
    {
        // BenchmarkDotNet children run from a generated directory; the driver passes the
        // root down through the environment.
        var configured = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrEmpty(configured)) return configured;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sdcb.SimdCodecs.Jpeg.Benchmarks.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Cannot find benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks; set " + RootVariable);
    }
}
