namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal static class BenchPaths
{
    public const string RootVariable = "SIMDCODECS_JPEG2000_BENCH_ROOT";

    private static readonly Lazy<string> _root = new(FindRoot);
    private static readonly Lazy<string> _repoRoot = new(FindRepoRoot);

    public static string Root => _root.Value;
    public static string RepoRoot => _repoRoot.Value;

    public static string Cache => Path.Combine(Root, ".cache");
    public static string Inputs => Path.Combine(Cache, "inputs");
    public static string Work => Path.Combine(Cache, "work");

    public static string FixturesLossless => Path.Combine(RepoRoot, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "jpeg2000");
    public static string FixturesLossy => Path.Combine(RepoRoot, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "jpeg2000-lossy");

    public static string OracleBin
    {
        get
        {
            var opjHome = Environment.GetEnvironmentVariable("OPJ_HOME");
            if (!string.IsNullOrEmpty(opjHome) && Directory.Exists(Path.Combine(opjHome, "bin")))
            {
                return Path.Combine(opjHome, "bin");
            }
            return Path.Combine(RepoRoot, "tests", "Sdcb.SimdCodecs.Tests", "Oracle", "jpeg2000", "dist", "bin");
        }
    }

    private static string FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrEmpty(configured)) return configured;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sdcb.SimdCodecs.Jpeg2000.Benchmarks.csproj")))
                return dir.FullName;
        }

        return Path.Combine(RepoRoot, "benchmarks", "Sdcb.SimdCodecs.Jpeg2000.Benchmarks");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SimdCodecs.slnx")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("Cannot find repository root (SimdCodecs.slnx)");
    }
}
