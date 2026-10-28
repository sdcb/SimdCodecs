using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>Locates the benchmark project directory and its git-ignored cache.</summary>
internal static class BenchPaths
{
    public const string RootVariable = "SIMDCODECS_BENCH_ROOT";

    private static readonly Lazy<string> LazyRoot = new(FindRoot);

    /// <summary><c>benchmarks/Sdcb.SimdCodecs.Png.Benchmarks</c>.</summary>
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
            if (File.Exists(Path.Combine(dir.FullName, "Sdcb.SimdCodecs.Png.Benchmarks.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Cannot find benchmarks/Sdcb.SimdCodecs.Png.Benchmarks; set " + RootVariable);
    }
}

/// <summary>
/// A previous commit's <c>Sdcb.SimdCodecs.Png</c>, built into the git-ignored cache and loaded side by
/// side with the current one, so an A/B comparison runs both in the same process, back to back —
/// never against a number from an earlier session. Build it with <c>baseline [git-ref]</c> (default
/// <c>HEAD</c>, which is what uncommitted work is measured against).
/// <para>Entry points are bound by name through function pointers; one the baseline does not have
/// (an older commit) is simply absent from the comparison.</para>
/// </summary>
internal static unsafe class BaselinePng
{
    private static string Dir => Path.Combine(BenchPaths.Cache, "baseline");
    private static string Marker => Path.Combine(Dir, "current.txt");

    private static readonly Lazy<Bound> Loaded = new(Load);

    /// <summary>"<c>&lt;sha&gt; &lt;subject&gt;</c>" of the built baseline, or null if none has been built.</summary>
    public static string? Description => File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : null;

    public static bool IsBuilt => Description is not null;

    private sealed class Bound
    {
        public IntPtr TryDecodeIntoRgba8;
        public IntPtr Decode;
        public readonly Dictionary<EncodeFormat, IntPtr> Encoders = [];
        public Type? OptionsType;
    }

    /// <summary>The baseline's <c>PngImageDecoder.TryDecodeIntoRgba8</c>.</summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> png, Span<byte> rgba) =>
        ((delegate*<ReadOnlySpan<byte>, Span<byte>, bool>)Loaded.Value.TryDecodeIntoRgba8)(png, rgba);

    /// <summary>The baseline's <c>PngReader.Decode</c> (its own <c>PngImage</c> type, hence object).</summary>
    public static object Decode(ReadOnlySpan<byte> png) =>
        ((delegate*<ReadOnlySpan<byte>, object>)Loaded.Value.Decode)(png);

    public static bool CanEncode(EncodeFormat format) => Loaded.Value.Encoders.ContainsKey(format);

    /// <summary>A baseline <c>PngWriteOptions</c> with only the compression level set.</summary>
    public static object CreateOptions(CompressionLevel level)
    {
        Type type = Loaded.Value.OptionsType ?? throw new InvalidOperationException("baseline has no PngWriteOptions");
        object options = Activator.CreateInstance(type)!;
        type.GetProperty("CompressionLevel")!.SetValue(options, level);
        return options;
    }

    /// <summary>The baseline's <c>PngWriter.Encode*(samples, width, height, options)</c> for <paramref name="format"/>.</summary>
    public static byte[] Encode(EncodeFormat format, ReadOnlySpan<byte> samples, int width, int height, object options)
    {
        IntPtr fn = Loaded.Value.Encoders[format];
        return format is EncodeFormat.Gray16 or EncodeFormat.Rgb16 or EncodeFormat.Rgba16
            ? ((delegate*<ReadOnlySpan<ushort>, int, int, object, byte[]>)fn)(
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(samples), width, height, options)
            : ((delegate*<ReadOnlySpan<byte>, int, int, object, byte[]>)fn)(samples, width, height, options);
    }

    private static Bound Load()
    {
        string description = Description
            ?? throw new InvalidOperationException("No baseline built. Run the 'baseline [git-ref]' command first.");
        string dir = Path.Combine(Dir, description.Split(' ')[0]);
        var alc = new DirectoryLoadContext(dir);
        Assembly asm = alc.LoadFromAssemblyName(new AssemblyName("Sdcb.SimdCodecs.Png"));
        var bound = new Bound();

        Type decoder = asm.GetType("Sdcb.SimdCodecs.Png.PngImageDecoder", throwOnError: true)!;
        bound.TryDecodeIntoRgba8 = decoder.GetMethod("TryDecodeIntoRgba8", BindingFlags.Public | BindingFlags.Static)!
            .MethodHandle.GetFunctionPointer();
        Type reader = asm.GetType("Sdcb.SimdCodecs.Png.PngReader", throwOnError: true)!;
        bound.Decode = reader.GetMethod("Decode", BindingFlags.Public | BindingFlags.Static)!.MethodHandle.GetFunctionPointer();

        bound.OptionsType = asm.GetType("Sdcb.SimdCodecs.Png.PngWriteOptions");
        Type writer = asm.GetType("Sdcb.SimdCodecs.Png.PngWriter", throwOnError: true)!;
        foreach ((EncodeFormat format, string name) in new[]
                 {
                     (EncodeFormat.Rgba8, "Encode"), (EncodeFormat.Rgb8, "EncodeRgb8"), (EncodeFormat.Gray8, "EncodeGray8"),
                     (EncodeFormat.Gray16, "EncodeGray16"), (EncodeFormat.Rgb16, "EncodeRgb16"), (EncodeFormat.Rgba16, "EncodeRgba16"),
                 })
        {
            MethodInfo? method = writer.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
                m.Name == name && m.GetParameters() is { Length: 4 } p && p[3].ParameterType == bound.OptionsType);
            if (method is not null)
            {
                bound.Encoders[format] = method.MethodHandle.GetFunctionPointer();
            }
        }

        return bound;
    }

    /// <summary>Builds <paramref name="gitRef"/>'s PNG package into the cache via a temporary git worktree.</summary>
    public static void Build(string gitRef)
    {
        string repo = Path.GetFullPath(Path.Combine(BenchPaths.Root, "..", ".."));
        string sha = Run("git", $"-C \"{repo}\" rev-parse --short=12 {gitRef}^{{commit}}").Trim();
        string subject = Run("git", $"-C \"{repo}\" log -1 --format=%s {sha}").Trim();
        string outDir = Path.Combine(Dir, sha);
        string worktree = Path.Combine(Path.GetTempPath(), "simdcodecs-png-baseline-" + sha);

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

            string project = Path.Combine(worktree, "src", "Sdcb.SimdCodecs.Png", "Sdcb.SimdCodecs.Png.csproj");
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
