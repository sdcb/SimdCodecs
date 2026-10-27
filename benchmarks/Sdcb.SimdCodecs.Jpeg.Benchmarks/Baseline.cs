using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// A previous commit's <c>Sdcb.SimdCodecs.Jpeg</c>, built into the git-ignored cache and
/// loaded side by side with the current one, so an A/B comparison runs both decoders in
/// the same process, back to back — never against a number from an earlier session.
/// <para>Build it with <c>baseline [git-ref]</c> (default <c>HEAD</c>, i.e. the last
/// commit, which is what uncommitted work is measured against).</para>
/// </summary>
internal static unsafe class BaselineDecoder
{
    private static string Dir => Path.Combine(BenchPaths.Cache, "baseline");
    private static string Marker => Path.Combine(Dir, "current.txt");

    private static readonly Lazy<(IntPtr Fn, string Label)> Loaded = new(Load);

    /// <summary>"<c>&lt;sha&gt; &lt;subject&gt;</c>" of the built baseline, or null if none has been built.</summary>
    public static string? Description => File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : null;

    public static string Label => Loaded.Value.Label;

    /// <summary>The baseline's <c>JpegDecoder.DecodeTo(data, rgba, JpegScale.Full)</c>.</summary>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> jpeg, Span<byte> rgba)
    {
        // JpegScale is an int-backed enum and Full = 1; the baseline's enum type lives in
        // the other load context, so the call goes through a raw function pointer.
        var fn = (delegate*<ReadOnlySpan<byte>, Span<byte>, int, ValueTuple<int, int>>)Loaded.Value.Fn;
        return fn(jpeg, rgba, 1);
    }

    /// <summary>The baseline build's <c>Sdcb.SimdCodecs.Jpeg</c> assembly, in its own load context.</summary>
    public static Assembly Assembly => LoadedAssembly.Value;

    private static readonly Lazy<Assembly> LoadedAssembly = new(() =>
    {
        var description = Description ?? throw new InvalidOperationException("No baseline built. Run the 'baseline [git-ref]' command first.");
        var dir = Path.Combine(Dir, description.Split(' ')[0]);
        return new DirectoryLoadContext(dir).LoadFromAssemblyName(new AssemblyName("Sdcb.SimdCodecs.Jpeg"));
    });

    private static (IntPtr, string) Load()
    {
        var asm = Assembly;
        var description = Description!;
        var type = asm.GetType("Sdcb.SimdCodecs.Jpeg.JpegDecoder", throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "DecodeTo" && m.GetParameters().Length == 3);
        return (method.MethodHandle.GetFunctionPointer(), "baseline " + description);
    }

    /// <summary>Builds <paramref name="gitRef"/>'s JPEG package into the cache via a temporary git worktree.</summary>
    public static void Build(string gitRef)
    {
        var repo = Path.GetFullPath(Path.Combine(BenchPaths.Root, "..", ".."));
        var sha = Run("git", $"-C \"{repo}\" rev-parse --short=12 {gitRef}^{{commit}}").Trim();
        var subject = Run("git", $"-C \"{repo}\" log -1 --format=%s {sha}").Trim();
        var outDir = Path.Combine(Dir, sha);
        var worktree = Path.Combine(Path.GetTempPath(), "simdcodecs-baseline-" + sha);

        if (Directory.Exists(worktree))
            Run("git", $"-C \"{repo}\" worktree remove --force \"{worktree}\"");
        Run("git", $"-C \"{repo}\" worktree add --detach \"{worktree}\" {sha}");
        try
        {
            if (Directory.Exists(outDir))
                Directory.Delete(outDir, recursive: true);
            var project = Path.Combine(worktree, "src", "Sdcb.SimdCodecs.Jpeg", "Sdcb.SimdCodecs.Jpeg.csproj");
            Run("dotnet", $"build \"{project}\" -c Release -o \"{outDir}\" -p:GeneratePackageOnBuild=false -nologo -v:q");
        }
        finally
        {
            Run("git", $"-C \"{repo}\" worktree remove --force \"{worktree}\"");
        }

        File.WriteAllText(Marker, $"{sha} {subject}");
        Console.WriteLine($"[baseline] {sha} {subject} -> {outDir}");
    }

    private static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{file} {args} failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
        return stdout.Result;
    }

    /// <summary>Resolves assemblies from one directory first, so the baseline gets its own Abstractions too.</summary>
    private sealed class DirectoryLoadContext(string dir) : AssemblyLoadContext("baseline-" + Path.GetFileName(dir))
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
