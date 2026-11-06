using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <b>libjpeg-turbo</b>'s TurboJPEG 3 decompressor as an external oracle for the JPEG encoder: the official release
/// binary that <c>benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-libjpeg-turbo.sh</c> fetches (pinned version
/// and SHA-256), found by walking up from the test binaries to that project's git-ignored <c>.cache</c>, or in the
/// directory named by <c>SIMDCODECS_TURBOJPEG_DIR</c>.
/// <para>
/// <b>Clean-room.</b> A black box through its documented API: the functions and enumerator values are those of the
/// published TurboJPEG documentation (the same surface as the benchmark project's <c>TurboJpeg.cs</c>), and
/// <see cref="Resolve"/> checks them against the library's behaviour before any test relies on them.
/// </para>
/// </summary>
internal static unsafe class TurboJpegOracle
{
    public const string DirectoryVariable = "SIMDCODECS_TURBOJPEG_DIR";

    private const string HowToGet =
        "Fetch it with `bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-libjpeg-turbo.sh` (Windows needs 7-Zip), or set " + DirectoryVariable + ".";

    // TJINIT_DECOMPRESS; TJPARAM_JPEGWIDTH / JPEGHEIGHT; TJPF_RGB / TJPF_GRAY.
    private const int InitDecompress = 1, ParamJpegWidth = 5, ParamJpegHeight = 6, PfRgb = 0, PfGray = 6;

    private sealed class Api
    {
        public delegate* unmanaged<int, nint> Init;
        public delegate* unmanaged<nint, void> Destroy;
        public delegate* unmanaged<nint, byte*> GetErrorStr;
        public delegate* unmanaged<nint, int, int> Get;
        public delegate* unmanaged<nint, byte*, nuint, int> DecompressHeader;
        public delegate* unmanaged<nint, byte*, nuint, byte*, int, int, int> Decompress8;
    }

    private static readonly Lazy<(Api? Api, string Reason)> Resolved = new(Resolve);

    public static bool IsAvailable => Resolved.Value.Api is not null;

    /// <summary>Skips when libjpeg-turbo is missing, or fails when <c>REQUIRE_ORACLES=1</c> says it should be there.</summary>
    public static void RequireOrSkip() => OracleGate.RequireOrSkip(IsAvailable, "libjpeg-turbo", Resolved.Value.Reason);

    /// <summary>
    /// Decodes <paramref name="jpeg"/> with <c>tj3Decompress8</c> (default, accurate settings) to tightly packed RGB
    /// (<paramref name="gray"/> false) or grey samples.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Decode(byte[] jpeg, bool gray) =>
        Decode(Resolved.Value.Api ?? throw new InvalidOperationException("libjpeg-turbo oracle is not available."), jpeg, gray);

    private static (byte[] Pixels, int Width, int Height) Decode(Api api, byte[] jpeg, bool gray)
    {
        var h = api.Init(InitDecompress);
        if (h == 0) throw new InvalidOperationException("tj3Init failed.");
        try
        {
            fixed (byte* p = jpeg)
            {
                Check(api, h, api.DecompressHeader(h, p, (nuint)jpeg.Length), "tj3DecompressHeader");
                var w = api.Get(h, ParamJpegWidth);
                var ht = api.Get(h, ParamJpegHeight);
                var bpp = gray ? 1 : 3;
                var pixels = new byte[w * ht * bpp];
                fixed (byte* d = pixels)
                    Check(api, h, api.Decompress8(h, p, (nuint)jpeg.Length, d, w * bpp, gray ? PfGray : PfRgb), "tj3Decompress8");
                return (pixels, w, ht);
            }
        }
        finally
        {
            api.Destroy(h);
        }
    }

    private static void Check(Api api, nint h, int rc, string what)
    {
        if (rc == 0) return;
        var s = api.GetErrorStr(h);
        throw new InvalidOperationException($"{what} failed: {(s == null ? "(no message)" : Marshal.PtrToStringUTF8((nint)s))}");
    }

    private static (Api?, string) Resolve()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            return (null, "The pinned libjpeg-turbo binaries are win-x64 and linux-x64 only.");

        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var file = OperatingSystem.IsWindows() ? "turbojpeg.dll" : "libturbojpeg.so";
        string? path = null;
        if (Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } dir)
        {
            path = Path.Combine(dir, file);
        }
        else
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && path is null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "benchmarks", "Sdcb.SimdCodecs.Jpeg.Benchmarks", ".cache", "libjpeg-turbo", rid, file);
                if (File.Exists(candidate)) path = candidate;
            }
        }

        if (path is null || !File.Exists(path)) return (null, $"{file} not found. {HowToGet}");

        var lib = NativeLibrary.Load(path);
        var api = new Api
        {
            Init = (delegate* unmanaged<int, nint>)NativeLibrary.GetExport(lib, "tj3Init"),
            Destroy = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(lib, "tj3Destroy"),
            GetErrorStr = (delegate* unmanaged<nint, byte*>)NativeLibrary.GetExport(lib, "tj3GetErrorStr"),
            Get = (delegate* unmanaged<nint, int, int>)NativeLibrary.GetExport(lib, "tj3Get"),
            DecompressHeader = (delegate* unmanaged<nint, byte*, nuint, int>)NativeLibrary.GetExport(lib, "tj3DecompressHeader"),
            Decompress8 = (delegate* unmanaged<nint, byte*, nuint, byte*, int, int, int>)NativeLibrary.GetExport(lib, "tj3Decompress8"),
        };

        // Self-check of the enumerators: a 37×21 grey image reports its width and height the right way round, and
        // decodes as TJPF_GRAY to the R (= G = B) channel of its TJPF_RGB decode.
        const int w = 37, h = 21;
        var src = new byte[w * h];
        for (var i = 0; i < src.Length; i++) src[i] = (byte)(i * 7);
        var jpeg = Jpeg.JpegEncoder.Encode(src, w, h, 1);
        var (grey, gw, gh) = Decode(api, jpeg, gray: true);
        var (rgb, _, _) = Decode(api, jpeg, gray: false);
        if ((gw, gh) != (w, h)) throw new InvalidOperationException($"libjpeg-turbo self-check: reported {gw}x{gh}, expected {w}x{h}.");
        for (var i = 0; i < w * h; i++)
        {
            if (rgb[3 * i] != grey[i] || rgb[3 * i + 1] != grey[i] || rgb[3 * i + 2] != grey[i])
                throw new InvalidOperationException("libjpeg-turbo self-check: TJPF_GRAY / TJPF_RGB disagree.");
        }

        return (api, "");
    }
}
