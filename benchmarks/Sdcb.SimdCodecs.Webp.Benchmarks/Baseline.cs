using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

/// <summary>Locates the benchmark project directory and its git-ignored cache.</summary>
internal static class BenchPaths
{
    public const string RootVariable = "SIMDCODECS_BENCH_ROOT";

    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    /// <summary><c>benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks</c>.</summary>
    public static string Root => LazyRoot.Value;

    public static string Cache => Path.Combine(Root, ".cache");
    public static string Work => Path.Combine(Cache, "work");

    private static string FindRoot()
    {
        // BenchmarkDotNet children run from a generated directory; the driver passes the root
        // down through the environment.
        string? configured = Environment.GetEnvironmentVariable(RootVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            return configured;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sdcb.SimdCodecs.Webp.Benchmarks.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Cannot find benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks; set " + RootVariable);
    }
}

/// <summary>
/// A previous commit's <c>Sdcb.SimdCodecs.Webp</c>, built into the git-ignored cache and loaded
/// side by side with the current one, so an A/B comparison runs both decoders in the same
/// process, back to back — never against a number from an earlier session.
/// <para>Build it with <c>baseline [git-ref]</c> (default <c>HEAD</c>, i.e. the last commit,
/// which is what uncommitted work is measured against).</para>
/// </summary>
internal static unsafe class BaselineDecoder
{
    private static string Dir => Path.Combine(BenchPaths.Cache, "baseline");
    private static string Marker => Path.Combine(Dir, "current.txt");

    private static readonly Lazy<(IntPtr Fn, string Label)> Loaded = new(Load);

    /// <summary>"<c>&lt;sha&gt; &lt;subject&gt;</c>" of the built baseline, or null if none has been built.</summary>
    public static string? Description => File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : null;

    public static string Label => Loaded.Value.Label;

    /// <summary>The baseline's <c>WebpDecoder.DecodeTo(data, rgba, stride: 0)</c>.</summary>
    public static void DecodeTo(ReadOnlySpan<byte> webp, Span<byte> rgba)
    {
        var fn = (delegate*<ReadOnlySpan<byte>, Span<byte>, int, void>)Loaded.Value.Fn;
        fn(webp, rgba, 0);
    }

    private static (IntPtr, string) Load()
    {
        string description = Description
            ?? throw new InvalidOperationException("No baseline built. Run the 'baseline [git-ref]' command first.");
        string dir = Path.Combine(Dir, description.Split(' ')[0]);
        var alc = new DirectoryLoadContext(dir);
        Assembly asm = alc.LoadFromAssemblyName(new AssemblyName("Sdcb.SimdCodecs.Webp"));
        Type type = asm.GetType("Sdcb.SimdCodecs.Webp.WebpDecoder", throwOnError: true)!;
        MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "DecodeTo" && m.GetParameters().Length == 3);
        return (method.MethodHandle.GetFunctionPointer(), "baseline " + description);
    }

    /// <summary>Builds <paramref name="gitRef"/>'s WebP package into the cache via a temporary git worktree.</summary>
    public static void Build(string gitRef)
    {
        string repo = Path.GetFullPath(Path.Combine(BenchPaths.Root, "..", ".."));
        string sha = Run("git", $"-C \"{repo}\" rev-parse --short=12 {gitRef}^{{commit}}").Trim();
        string subject = Run("git", $"-C \"{repo}\" log -1 --format=%s {sha}").Trim();
        string outDir = Path.Combine(Dir, sha);
        string worktree = Path.Combine(Path.GetTempPath(), "simdcodecs-webp-baseline-" + sha);

        if (Directory.Exists(worktree))
        {
            Run("git", $"-C \"{repo}\" worktree remove --force \"{worktree}\"");
        }

        Run("git", $"-C \"{repo}\" worktree add --detach \"{worktree}\" {sha}");
        try
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }

            string project = Path.Combine(worktree, "src", "Sdcb.SimdCodecs.Webp", "Sdcb.SimdCodecs.Webp.csproj");
            Run("dotnet", $"build \"{project}\" -c Release -o \"{outDir}\" -p:GeneratePackageOnBuild=false -nologo -v:q");
        }
        finally
        {
            Run("git", $"-C \"{repo}\" worktree remove --force \"{worktree}\"");
        }

        Directory.CreateDirectory(Dir);
        File.WriteAllText(Marker, $"{sha} {subject}");
        Console.WriteLine($"[baseline] {sha} {subject} -> {outDir}");
    }

    private static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process p = Process.Start(psi)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {args} failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
        }

        return stdout.Result;
    }

    /// <summary>Resolves assemblies from one directory first, so the baseline gets its own Abstractions too.</summary>
    private sealed class DirectoryLoadContext(string dir) : AssemblyLoadContext("baseline-" + Path.GetFileName(dir))
    {
        protected override Assembly? Load(AssemblyName name)
        {
            string path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
