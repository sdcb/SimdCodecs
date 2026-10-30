using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

/// <summary>
/// A previous commit's <c>Sdcb.SimdCodecs.Bmp</c> and <c>Sdcb.SimdCodecs.Ico</c>, built into the git-ignored cache
/// and loaded side by side with the current ones, so an A/B comparison runs both in the same process, back to back —
/// never against a number from an earlier session. Build it with <c>baseline [git-ref]</c> (default <c>HEAD</c>,
/// which is what uncommitted work is measured against).
/// <para>Entry points are bound by name through function pointers (an enum crosses as its integer, an options object
/// as a reference); one the baseline does not have is simply absent from the comparison.</para>
/// </summary>
internal static unsafe class Baseline
{
    private static string Dir => Path.Combine(BenchPaths.Cache, "baseline");
    private static string Marker => Path.Combine(Dir, "current.txt");

    private static readonly Lazy<Bound> Loaded = new(Load);

    /// <summary>"<c>&lt;sha&gt; &lt;subject&gt;</c>" of the built baseline, or null if none has been built.</summary>
    public static string? Description => File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : null;

    public static bool IsBuilt => Description is not null;

    private sealed class Bound
    {
        public IntPtr BmpDecodeInto, IcoDecodeInto, BmpEncode;
        public Type? OptionsType;
    }

    public static bool CanDecode(bool icon) => (icon ? Loaded.Value.IcoDecodeInto : Loaded.Value.BmpDecodeInto) != IntPtr.Zero;

    public static bool CanEncode => Loaded.Value.BmpEncode != IntPtr.Zero && Loaded.Value.OptionsType is not null;

    /// <summary>The baseline's <c>BmpImageDecoder</c> / <c>IcoImageDecoder.TryDecodeInto</c>, to RGBA.</summary>
    public static bool TryDecodeInto(bool icon, ReadOnlySpan<byte> data, Span<byte> rgba) =>
        ((delegate*<ReadOnlySpan<byte>, Span<byte>, int, ImagePixelFormat, bool>)(icon ? Loaded.Value.IcoDecodeInto : Loaded.Value.BmpDecodeInto))(
            data, rgba, 0, ImagePixelFormat.Rgba32);

    /// <summary>The baseline's <c>BmpEncoder.Encode(pixels, width, height, Rgba32, options)</c>, the options copied
    /// across by property name.</summary>
    public static Func<byte[], int, int, byte[]> CreateEncoder(BmpEncodeOptions options)
    {
        var bound = Loaded.Value;
        var copy = Activator.CreateInstance(bound.OptionsType!)!;
        foreach (var property in typeof(BmpEncodeOptions).GetProperties())
        {
            var target = bound.OptionsType!.GetProperty(property.Name);
            if (target is null) continue;
            var value = property.GetValue(options);
            target.SetValue(copy, value is Enum e ? Enum.Parse(target.PropertyType, e.ToString()) : value);
        }
        var fn = bound.BmpEncode;
        return (pixels, width, height) =>
            ((delegate*<ReadOnlySpan<byte>, int, int, ImagePixelFormat, object, int, byte[]>)fn)(pixels, width, height, ImagePixelFormat.Rgba32, copy, 0);
    }

    private static Bound Load()
    {
        var description = Description ?? throw new InvalidOperationException("No baseline built. Run the 'baseline [git-ref]' command first.");
        var dir = Path.Combine(Dir, description.Split(' ')[0]);
        var alc = new DirectoryLoadContext(dir);
        var bound = new Bound();
        var bmp = alc.LoadFromAssemblyName(new AssemblyName("Sdcb.SimdCodecs.Bmp"));
        bound.BmpDecodeInto = Pointer(bmp.GetType("Sdcb.SimdCodecs.Bmp.BmpImageDecoder"), "TryDecodeInto", 4);
        bound.OptionsType = bmp.GetType("Sdcb.SimdCodecs.Bmp.BmpEncodeOptions");
        var encoder = bmp.GetType("Sdcb.SimdCodecs.Bmp.BmpEncoder");
        var encode = encoder?.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m =>
            m.Name == "Encode" && m.GetParameters() is { Length: 6 } p && p[3].ParameterType.Name == nameof(ImagePixelFormat));
        bound.BmpEncode = encode?.MethodHandle.GetFunctionPointer() ?? IntPtr.Zero;
        if (File.Exists(Path.Combine(dir, "Sdcb.SimdCodecs.Ico.dll")))
        {
            var ico = alc.LoadFromAssemblyName(new AssemblyName("Sdcb.SimdCodecs.Ico"));
            bound.IcoDecodeInto = Pointer(ico.GetType("Sdcb.SimdCodecs.Ico.IcoImageDecoder"), "TryDecodeInto", 4);
        }
        return bound;
    }

    private static IntPtr Pointer(Type? type, string name, int parameters) =>
        type?.GetMethods(BindingFlags.Public | BindingFlags.Static).SingleOrDefault(m => m.Name == name && m.GetParameters().Length == parameters)
            ?.MethodHandle.GetFunctionPointer() ?? IntPtr.Zero;

    /// <summary>Builds <paramref name="gitRef"/>'s BMP and ICO packages into the cache via a temporary git worktree.</summary>
    public static void Build(string gitRef)
    {
        var repo = Path.GetFullPath(Path.Combine(BenchPaths.Root, "..", ".."));
        var sha = Run("git", $"-C \"{repo}\" rev-parse --short=12 {gitRef}^{{commit}}").Trim();
        var subject = Run("git", $"-C \"{repo}\" log -1 --format=%s {sha}").Trim();
        var outDir = Path.Combine(Dir, sha);
        // Inside the git-ignored cache rather than the temp directory, which some machines keep tools out of.
        var worktree = Path.Combine(BenchPaths.Cache, "worktree-" + sha);
        if (Directory.Exists(worktree)) Run("git", $"-C \"{repo}\" worktree remove --force \"{worktree}\"");
        Run("git", $"-C \"{repo}\" worktree add --detach \"{worktree}\" {sha}");
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
            // The ICO package brings BMP, PNG and Abstractions with it; a commit before it has BMP alone.
            var ico = Path.Combine(worktree, "src", "Sdcb.SimdCodecs.Ico", "Sdcb.SimdCodecs.Ico.csproj");
            var project = File.Exists(ico) ? ico : Path.Combine(worktree, "src", "Sdcb.SimdCodecs.Bmp", "Sdcb.SimdCodecs.Bmp.csproj");
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
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // MSBuild makes its own temporary folders: give it one under the cache, for machines that keep tools out of %TEMP%.
        var temp = Directory.CreateDirectory(Path.Combine(BenchPaths.Cache, "tmp")).FullName;
        psi.Environment["TMP"] = psi.Environment["TEMP"] = temp;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{file} {args} failed ({p.ExitCode}):\n{stdout.Result}\n{stderr.Result}");
        return stdout.Result;
    }

    /// <summary>Resolves assemblies from one directory first, so the baseline gets its own Png and Abstractions too.</summary>
    private sealed class DirectoryLoadContext(string dir) : AssemblyLoadContext("baseline-" + Path.GetFileName(dir))
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
