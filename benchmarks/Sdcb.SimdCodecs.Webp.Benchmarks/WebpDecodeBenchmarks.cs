using System.Reflection;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Webp.Benchmarks;

[MemoryDiagnoser]
public class WebpDecodeBenchmarks
{
    [Params(
        WebpBenchmarkInputs.LossyOpaque,
        WebpBenchmarkInputs.LossyAlpha,
        WebpBenchmarkInputs.Lossless)]
    public string Fixture { get; set; } = string.Empty;

    private byte[] _source = [];
    private byte[] _rgba = [];
    private int _width;
    private int _height;

    [GlobalSetup]
    public void Setup()
    {
        _source = WebpBenchmarkInputs.Read(Fixture);
        WebpInfo info = WebpDecoder.ReadInfo(_source);
        if (info.Format == WebpFormat.Animated)
        {
            throw new InvalidOperationException($"{Fixture} must be a still WebP.");
        }

        _width = info.Width;
        _height = info.Height;
        _rgba = new byte[checked(_width * _height * 4)];

        (int width, int height) = LibWebp.GetInfo(_source);
        if (width != _width || height != _height)
        {
            throw new InvalidOperationException(
                $"libwebp reports {width}×{height} for {Fixture}; Sdcb.SimdCodecs.Webp reports {_width}×{_height}.");
        }
    }

    [Benchmark(Baseline = true, Description = "libwebp DecodeRGBAInto")]
    public void LibWebpDecodeTo() =>
        LibWebp.DecodeRgbaInto(_source, _rgba, _width, _height);

    [Benchmark(Description = "Sdcb.SimdCodecs.Webp DecodeTo")]
    public void SdcbDecodeTo() =>
        WebpDecoder.DecodeTo(_source, _rgba);
}

[MemoryDiagnoser]
public class WebpAnimationDecodeBenchmarks
{
    private byte[] _source = [];

    [GlobalSetup]
    public void Setup()
    {
        _source = WebpBenchmarkInputs.Read(WebpBenchmarkInputs.Animation);
        WebpInfo info = WebpDecoder.ReadInfo(_source);
        if (info.Format != WebpFormat.Animated)
        {
            throw new InvalidOperationException("The animation benchmark input is not animated.");
        }
    }

    [Benchmark]
    public WebpAnimation DecodeAnimation() => WebpDecoder.DecodeAnimation(_source);
}

internal static class WebpBenchmarkInputs
{
    public const string LossyOpaque = "lossy-opaque-1080p.webp";
    public const string LossyAlpha = "lossy-alpha-1080p.webp";
    public const string Lossless = "lossless-1080p.webp";
    public const string Animation = "animated-10x640.webp";

    public static byte[] Read(string fileName) => File.ReadAllBytes(FindPath(fileName));

    public static string PathOf(string fileName) => FindPath(fileName);

    private static string FindPath(string fileName)
    {
        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
        {
            return fileName;
        }

        string? configuredDirectory = Environment.GetEnvironmentVariable("WEBP_BENCHMARK_INPUT_DIR");
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.Combine(configuredDirectory, fileName);
        }

        string projectRelativeDirectory = Path.Combine(
            "benchmarks", "Sdcb.SimdCodecs.Webp.Benchmarks", "webp-inputs");
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                string[] candidates =
                [
                    Path.Combine(directory.FullName, "webp-inputs", fileName),
                    Path.Combine(directory.FullName, projectRelativeDirectory, fileName)
                ];
                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        throw new FileNotFoundException(
            $"Benchmark input '{fileName}' was not found. Run " +
            "benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/generate-webp-inputs.sh first.");
    }
}

internal static class LibWebp
{
    private const string ImportName = "sdcb-webp-benchmark";
    private static readonly IntPtr LibraryHandle = LoadLibrary();

    static LibWebp()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibWebp).Assembly, ResolveLibrary);
    }

    public static unsafe (int Width, int Height) GetInfo(ReadOnlySpan<byte> encoded)
    {
        fixed (byte* source = encoded)
        {
            if (WebPGetInfoNative(source, (nuint)encoded.Length, out int width, out int height) == 0)
            {
                throw new InvalidDataException("libwebp could not read the benchmark input.");
            }

            return (width, height);
        }
    }

    public static unsafe void DecodeRgbaInto(
        ReadOnlySpan<byte> encoded,
        Span<byte> rgba,
        int width,
        int height)
    {
        fixed (byte* source = encoded)
        fixed (byte* destination = rgba)
        {
            if (WebPDecodeRgbaIntoNative(
                source,
                (nuint)encoded.Length,
                destination,
                (nuint)rgba.Length,
                checked(width * 4)) == null)
            {
                throw new InvalidDataException("libwebp failed to decode the benchmark input.");
            }
        }
    }

    // LIBWEBP_PATH wins; otherwise each platform's usual shared-library names are tried. The official
    // Windows release archive ships only a static library, so on Windows LIBWEBP_PATH normally has to
    // point at a webp DLL (vcpkg's `libwebp.dll`, for example).
    private static IntPtr LoadLibrary()
    {
        if (Environment.GetEnvironmentVariable("LIBWEBP_PATH") is { Length: > 0 } configured)
        {
            return NativeLibrary.Load(configured);
        }

        // What optimization/build-libwebp.sh produces.
        foreach (string name in new[] { "libwebp.dll", "libwebp.so", "libwebp.dylib" })
        {
            string built = Path.Combine(BenchPaths.Cache, name);
            if (File.Exists(built))
            {
                return NativeLibrary.Load(built);
            }
        }

        string[] candidates = OperatingSystem.IsWindows() ? ["libwebp.dll", "webp.dll"]
            : OperatingSystem.IsMacOS() ? ["/opt/homebrew/lib/libwebp.dylib", "/usr/local/lib/libwebp.dylib", "libwebp.dylib"]
            : ["libwebp.so.7", "libwebp.so"];
        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException(
            $"libwebp was not found (tried {string.Join(", ", candidates)}). " +
            "Set LIBWEBP_PATH to the full path of a libwebp shared library to run the WebP A/B benchmarks.");
    }

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath) =>
        libraryName == ImportName ? LibraryHandle : IntPtr.Zero;

    [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, EntryPoint = "WebPGetInfo")]
    private static extern unsafe int WebPGetInfoNative(
        byte* data,
        nuint dataSize,
        out int width,
        out int height);

    [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true, EntryPoint = "WebPDecodeRGBAInto")]
    private static extern unsafe byte* WebPDecodeRgbaIntoNative(
        byte* data,
        nuint dataSize,
        byte* outputBuffer,
        nuint outputBufferSize,
        int outputStride);
}

internal static class WebpProfileRunner
{
    public static void RunLossyOpaque()
    {
        byte[] source = WebpBenchmarkInputs.Read(WebpBenchmarkInputs.LossyOpaque);
        WebpInfo info = WebpDecoder.ReadInfo(source);
        byte[] rgba = new byte[checked(info.Width * info.Height * 4)];

        for (int i = 0; i < 3; i++)
        {
            WebpDecoder.DecodeTo(source, rgba);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int iterations = 0;
        do
        {
            WebpDecoder.DecodeTo(source, rgba);
            iterations++;
        }
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(12));

        stopwatch.Stop();
        Console.WriteLine(
            $"Profiled {iterations} single-thread lossy 1080p decodes in {stopwatch.Elapsed.TotalSeconds:F1}s; " +
            $"output={rgba[0]:X2}{rgba[1]:X2}{rgba[2]:X2}{rgba[3]:X2}.");
    }
}
