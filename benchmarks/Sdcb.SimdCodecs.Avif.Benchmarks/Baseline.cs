using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Avif.Benchmarks;

/// <summary>
/// A previous commit's <c>Sdcb.SimdCodecs.Avif</c>, built into the git-ignored cache and loaded side by side with the
/// current one, so an A/B comparison runs both in the same process, back to back — never against a number from an
/// earlier session. Build it with <c>baseline [git-ref]</c> (default <c>HEAD</c>, which is what uncommitted work is
/// measured against). The entry point is the public <c>AvifImageDecoder.TryDecodeInto</c>, bound by name through a
/// function pointer (the <see cref="PixelFormat"/> enum crosses as its integer).
/// </summary>
internal static unsafe class Baseline
{
    internal static string Root { get; } = FindRoot();
    private static string Cache => Path.Combine(Root, "benchmarks", "Sdcb.SimdCodecs.Avif.Benchmarks", ".cache");
    private static string Dir => Path.Combine(Cache, "baseline");
    private static string Marker => Path.Combine(Dir, "current.txt");

    private static readonly Lazy<IntPtr> DecodeInto = new(Load);

    /// <summary>"<c>&lt;sha&gt; &lt;subject&gt;</c>" of the built baseline, or null if none has been built.</summary>
    internal static string? Description => File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : null;

    /// <summary>The baseline's <c>AvifImageDecoder.TryDecodeInto</c> to RGBA.</summary>
    internal static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> rgba, int stride) =>
        ((delegate*<ReadOnlySpan<byte>, Span<byte>, int, PixelFormat, object?, CancellationToken, bool>)DecodeInto.Value)(
            data, rgba, stride, PixelFormat.Rgba32, null, default);

    private static readonly Lazy<Assembly> Avif = new(() =>
    {
        var description = Description ?? throw new InvalidOperationException("No baseline built. Run the 'baseline [git-ref]' command first.");
        var dir = Path.Combine(Dir, description.Split(' ')[0]);
        return new DirectoryLoadContext(dir).LoadFromAssemblyName(new AssemblyName("Sdcb.SimdCodecs.Avif"));
    });

    private static readonly Lazy<IntPtr> OpenFrames = new(() => DecoderMethod("TryOpenFrames", 2));

    /// <summary>
    /// Decodes every frame of an animated AVIF to RGBA through the baseline's public frame reader
    /// (<c>AvifImageDecoder.TryOpenFrames</c>, then <c>TryDecodeFrameInto</c> per frame), calling
    /// <paramref name="frame"/> after each with the frame index.
    /// </summary>
    internal static void DecodeAllFrames(byte[] data, byte[] rgba, int stride, Action<int>? frame = null)
    {
        object? reader = null;
        if (!((delegate*<ReadOnlyMemory<byte>, out object?, bool>)OpenFrames.Value)(data, out reader) || reader is null)
            throw new InvalidDataException("The baseline's TryOpenFrames failed.");
        using var disposable = (IDisposable)reader;
        var type = reader.GetType();
        int count = (int)type.GetProperty("FrameCount")!.GetValue(reader)!;
        var decode = (delegate*<object, int, Span<byte>, int, PixelFormat, object?, CancellationToken, bool>)type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "TryDecodeFrameInto" && m.GetParameters().Length == 6)
            .MethodHandle.GetFunctionPointer();
        for (int i = 0; i < count; i++)
        {
            if (!decode(reader, i, rgba, stride, PixelFormat.Rgba32, null, default))
                throw new InvalidDataException($"The baseline failed to decode frame {i}.");
            frame?.Invoke(i);
        }
    }

    private static IntPtr Load() => DecoderMethod("TryDecodeInto", 6);

    private static IntPtr DecoderMethod(string name, int parameters)
    {
        var method = Avif.Value.GetType("Sdcb.SimdCodecs.Avif.AvifImageDecoder")?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == name && m.GetParameters().Length == parameters)
            ?? throw new InvalidOperationException($"The baseline has no AvifImageDecoder.{name}({parameters} parameters).");
        return method.MethodHandle.GetFunctionPointer();
    }

    /// <summary>Builds <paramref name="gitRef"/>'s AVIF package into the cache via a temporary git worktree.</summary>
    internal static void Build(string gitRef)
    {
        var sha = Run("git", $"-C \"{Root}\" rev-parse --short=12 {gitRef}^{{commit}}").Trim();
        var subject = Run("git", $"-C \"{Root}\" log -1 --format=%s {sha}").Trim();
        var outDir = Path.Combine(Dir, sha);
        // Inside the git-ignored cache rather than the temp directory, which some machines keep tools out of.
        var worktree = Path.Combine(Cache, "worktree-" + sha);
        if (Directory.Exists(worktree)) Run("git", $"-C \"{Root}\" worktree remove --force \"{worktree}\"");
        Run("git", $"-C \"{Root}\" worktree add --detach \"{worktree}\" {sha}");
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
            var project = Path.Combine(worktree, "src", "Sdcb.SimdCodecs.Avif", "Sdcb.SimdCodecs.Avif.csproj");
            Run("dotnet", $"build \"{project}\" -c Release -o \"{outDir}\" -p:GeneratePackageOnBuild=false -nologo -v:q");
        }
        finally
        {
            Run("git", $"-C \"{Root}\" worktree remove --force \"{worktree}\"");
        }
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Marker, $"{sha} {subject}");
        Console.WriteLine($"[baseline] {sha} {subject} -> {outDir}");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SimdCodecs.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate SimdCodecs.slnx above the benchmark's output directory.");
    }

    private static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // MSBuild makes its own temporary folders: give it one under the cache, for machines that keep tools out of %TEMP%.
        var temp = Directory.CreateDirectory(Path.Combine(Cache, "tmp")).FullName;
        psi.Environment["TMP"] = psi.Environment["TEMP"] = temp;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {args} failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
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
