namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>Locates the HEIF fixtures and the native competitor binaries.</summary>
internal static class HeifInputs
{
    /// <summary>Overrides the fixture directory (default: the repository's test fixtures).</summary>
    public const string InputsVariable = "HEIF_INPUTS";

    /// <summary>Overrides the directory holding <c>heif.dll</c> and <c>libde265.dll</c>.</summary>
    public const string NativeVariable = "HEIF_NATIVE_DIR";

    private static readonly Lazy<string> _root = new(FindRoot);
    private static readonly Lazy<string> _inputs = new(FindInputs);
    private static readonly Lazy<string> _native = new(FindNative);

    /// <summary>The repository root (the directory holding <c>SimdCodecs.slnx</c>).</summary>
    public static string Root => _root.Value;

    /// <summary>The fixtures every case decodes, in the order the m0 record lists them.</summary>
    public static readonly string[] Fixtures =
    [
        "hevc-main-3072x4096-grid.heic",
        "hevc-main10-3072x4096-grid.heic",
        "hevc-main-8192x6144-grid.heic",
        "synth-photo-fuji-1600x900-main8.heic",
        "synth-photo-mangrove-1599x1199-main8.heic",
        "synth-screen-testsrc2-1280x720-main8.heic",
    ];

    /// <summary>The directory holding <c>heif.dll</c> and <c>libde265.dll</c>.</summary>
    public static string NativeDir => _native.Value;

    /// <summary>Reads one fixture, with a message that says how to fetch it when it is missing.</summary>
    public static byte[] Load(string name)
    {
        string path = Path.Combine(_inputs.Value, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture not found: {path}. Run `bash tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch.sh` " +
                $"or set {InputsVariable} to a directory that holds it.", path);
        }
        return File.ReadAllBytes(path);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SimdCodecs.slnx"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cannot find the repository root (SimdCodecs.slnx) above " + AppContext.BaseDirectory);
    }

    private static string FindInputs()
    {
        var configured = Environment.GetEnvironmentVariable(InputsVariable);
        if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
        return Path.Combine(Root, "tests", "Sdcb.SimdCodecs.Tests", "Fixtures", "heif");
    }

    private static string FindNative()
    {
        var configured = Environment.GetEnvironmentVariable(NativeVariable);
        if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
        string path = Path.Combine(Root, "benchmarks", "Sdcb.SimdCodecs.Heif.Benchmarks", "heif-native");
        if (Directory.Exists(path)) return path;
        // This machine's vcpkg tree, for a run without any copying.
        const string vcpkg = @"C:\_\3rd\vcpkg\installed\x64-windows\bin";
        return Directory.Exists(vcpkg) ? vcpkg : path;
    }
}
