namespace Sdcb.SimdCodecs.Avif.Benchmarks;

internal static class AvifInputs
{
    internal static readonly string[] Fixtures =
    [
        "perf-photo-mangrove-8192x6144-grid-8b.avif",
        "perf-photo-almsee-4096x3072-grid-10b.avif",
        "perf-photo-teatro-4096x2632-tiles-svt.avif",
        "perf-photo-fuji-2560x1440-lossless-444.avif",
        "perf-screen-ui-3840x2160-444-8b.avif",
        "perf-photo-night-3840x2160-grain-10b.avif",
        "perf-photo-carter-3840x2560-p2-12b-422.avif",
        "perf-photo-fuji-2560x1440-alpha-10b.avif",
    ];

    internal static string ResolveDirectory()
    {
        string? env = Environment.GetEnvironmentVariable("AVIF_INPUTS");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "avif");
            if (Directory.Exists(candidate))
                return candidate;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent is null) break;
            dir = parent;
        }

        throw new DirectoryNotFoundException("Could not locate tests/Sdcb.SimdCodecs.Tests/Fixtures/avif. Run tests/Sdcb.SimdCodecs.Tests/Oracle/avif/fetch.sh or set AVIF_INPUTS.");
    }

    internal static byte[] Load(string fileName)
    {
        string path = Path.Combine(ResolveDirectory(), fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Missing AVIF benchmark fixture '{fileName}'. Run tests/Sdcb.SimdCodecs.Tests/Oracle/avif/fetch.sh.", path);
        return File.ReadAllBytes(path);
    }
}
