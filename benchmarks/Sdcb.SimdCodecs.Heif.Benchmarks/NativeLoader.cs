using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// Loads <c>heif.dll</c> and <c>libde265.dll</c> from a known directory instead of PATH, so every run —
/// including BenchmarkDotNet's child processes — uses the same pinned build.
/// <para>
/// Installed from a module initializer rather than <c>Program.Main</c>: BenchmarkDotNet generates its
/// own entry point for timing runs, which never executes this project's top-level statements, so a
/// main-only install leaves the native decoder unfindable in exactly the processes that need it.
/// </para>
/// </summary>
internal static class NativeLoader
{
    private const string Heif = "heif";
    private const string De265 = "libde265";

    /// <summary>Directory holding the two native libraries; see <see cref="HeifInputs.NativeVariable"/>.</summary>
    public static string Directory => HeifInputs.NativeDir;

    [ModuleInitializer]
    internal static void Install()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name is not (Heif or De265)) return 0;
        string file = Path.Combine(Directory, name + ".dll");
        if (!File.Exists(file))
        {
            throw new FileNotFoundException(
                $"Native competitor not found: {file}. Set {HeifInputs.NativeVariable} to the directory that holds " +
                "heif.dll and libde265.dll (on this machine: C:\\_\\3rd\\vcpkg\\installed\\x64-windows\\bin).", file);
        }
        return NativeLibrary.Load(file);
    }
}
