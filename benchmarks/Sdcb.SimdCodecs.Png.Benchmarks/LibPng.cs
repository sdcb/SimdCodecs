using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Sdcb.SimdCodecs.Png.Benchmarks;

/// <summary>
/// libpng as a black box, through its documented "simplified API" (<c>png_image_*</c>, libpng-manual.txt
/// section V). Optional: when no libpng shared library is found every libpng column reads "—".
/// The interop is written from the manual's description of <c>png_image</c> and the functions, and
/// checked against the binary's behaviour (<see cref="SelfCheck"/>) — never from libpng's headers or sources.
/// </summary>
internal static unsafe class LibPng
{
    private const string ImportName = "sdcb-png-benchmark-libpng";
    private const uint ImageVersion = 1;            // PNG_IMAGE_VERSION

    // PNG_FORMAT_FLAG_*: ALPHA 1, COLOR 2, LINEAR (16-bit linear samples) 4, BGR 0x10.
    public const uint FormatGray = 0, FormatRgb = 2, FormatRgba = 3;
    public const uint FormatLinear = 4;
    public const uint FlagBgr = 0x10;

    private static readonly Lazy<IntPtr> Handle = new(Load);
    private static string? _loadError;

    static LibPng()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibPng).Assembly, Resolve);
    }

    public static bool IsAvailable => Handle.Value != IntPtr.Zero;

    /// <summary>"libpng 1.6.x (path)" or why it is unavailable.</summary>
    public static string Describe()
    {
        if (!IsAvailable)
        {
            return "not found (" + _loadError + ")";
        }

        uint v = png_access_version_number();
        return $"libpng {v / 10000}.{v / 100 % 100}.{v % 100} ({Environment.GetEnvironmentVariable("LIBPNG_PATH") ?? "default search"})";
    }

    /// <summary>The manual's <c>png_image</c>: opaque pointer, six 32-bit fields, a 64-byte message.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImage
    {
        public IntPtr Opaque;
        public uint Version;
        public uint Width;
        public uint Height;
        public uint Format;
        public uint Flags;
        public uint ColormapEntries;
        public uint WarningOrError;
        public fixed byte Message[64];
    }

    /// <summary>Decode any PNG to 8-bit RGBA (sRGB) into <paramref name="rgba"/>.</summary>
    public static void DecodeRgba(ReadOnlySpan<byte> png, Span<byte> rgba) => Decode(png, rgba, FormatRgba);

    /// <summary>Decode any PNG to 8-bit sRGB samples in the simplified-API <paramref name="format"/>, tightly packed.</summary>
    public static void Decode(ReadOnlySpan<byte> png, Span<byte> pixels, uint format)
    {
        var image = new NativeImage { Version = ImageVersion };
        int channels = (format & FormatRgb) != 0 ? 3 : 1;
        channels += (int)(format & 1);
        fixed (byte* source = png)
        fixed (byte* destination = pixels)
        {
            try
            {
                if (png_image_begin_read_from_memory(&image, source, (nuint)png.Length) == 0)
                {
                    throw Error(ref image, "begin_read");
                }

                if ((long)image.Width * image.Height * channels > pixels.Length)
                {
                    throw new InvalidOperationException("libpng: destination too small");
                }

                image.Format = format;
                if (png_image_finish_read(&image, null, destination, 0, null) == 0)
                {
                    throw Error(ref image, "finish_read");
                }
            }
            finally
            {
                png_image_free(&image);
            }
        }
    }

    /// <summary>(width, height) from the header, through libpng.</summary>
    public static (int Width, int Height) ReadSize(ReadOnlySpan<byte> png)
    {
        var image = new NativeImage { Version = ImageVersion };
        fixed (byte* source = png)
        {
            try
            {
                if (png_image_begin_read_from_memory(&image, source, (nuint)png.Length) == 0)
                {
                    throw Error(ref image, "begin_read");
                }

                return ((int)image.Width, (int)image.Height);
            }
            finally
            {
                png_image_free(&image);
            }
        }
    }

    /// <summary>
    /// Encode packed samples (8-bit, or host-order 16-bit with <see cref="FormatLinear"/>) into
    /// <paramref name="scratch"/>, growing it when libpng reports it needs more; returns the length.
    /// </summary>
    public static int Encode(ReadOnlySpan<byte> samples, int width, int height, uint format, ref byte[] scratch)
    {
        while (true)
        {
            var image = new NativeImage { Version = ImageVersion, Width = (uint)width, Height = (uint)height, Format = format };
            nuint size = (nuint)scratch.Length;
            int ok;
            fixed (byte* source = samples)
            fixed (byte* destination = scratch)
            {
                ok = png_image_write_to_memory(&image, destination, &size, 0, source, 0, null);
            }

            png_image_free(&image);
            if (ok != 0)
            {
                return (int)size;
            }

            if (size > (nuint)scratch.Length)
            {
                scratch = new byte[(int)size];
                continue;
            }

            throw Error(ref image, "write_to_memory");
        }
    }

    /// <summary>
    /// The interop's ground truth: the struct layout and the calls have to reproduce known facts —
    /// a size read back from the header and a write/read round trip of a tiny RGBA image.
    /// </summary>
    public static void SelfCheck()
    {
        byte[] rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)(i * 37 + 11);
        }

        for (int i = 3; i < rgba.Length; i += 4)
        {
            rgba[i] = 255; // opaque, so no premultiplication question arises
        }

        byte[] scratch = new byte[16];
        int length = Encode(rgba, 3, 2, FormatRgba, ref scratch);
        byte[] png = scratch.AsSpan(0, length).ToArray();
        if (ReadSize(png) != (3, 2))
        {
            throw new InvalidOperationException("libpng self-check: header size mismatch (png_image layout?)");
        }

        PngImage written = PngReader.Decode(png);
        if (written is not { Width: 3, Height: 2, ColorType: 6, BitDepth: 8 } || !written.Pixels.AsSpan().SequenceEqual(rgba))
        {
            throw new InvalidOperationException("libpng self-check: written pixels differ");
        }

        byte[] back = new byte[rgba.Length];
        DecodeRgba(png, back);
        if (!back.AsSpan().SequenceEqual(rgba))
        {
            throw new InvalidOperationException("libpng self-check: decoded pixels differ");
        }

        // The BGR flag: the same pixels with R and B exchanged.
        byte[] bgr = new byte[3 * 2 * 3];
        Decode(png, bgr, FormatRgb | FlagBgr);
        for (int p = 0; p < 6; p++)
        {
            if (bgr[3 * p] != rgba[4 * p + 2] || bgr[3 * p + 1] != rgba[4 * p + 1] || bgr[3 * p + 2] != rgba[4 * p])
            {
                throw new InvalidOperationException("libpng self-check: the BGR format flag is not what the manual says");
            }
        }
    }

    private static Exception Error(ref NativeImage image, string call)
    {
        string message;
        fixed (byte* m = image.Message)
        {
            message = Encoding.ASCII.GetString(m, new ReadOnlySpan<byte>(m, 64).IndexOf((byte)0) is var n and >= 0 ? n : 64);
        }

        return new InvalidDataException($"libpng png_image_{call} failed: {message}");
    }

    // LIBPNG_PATH wins; otherwise each platform's usual shared-library names. On Windows a libpng16.dll
    // (vcpkg's, for example) needs its zlib1.dll in the same directory.
    private static IntPtr Load()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("LIBPNG_PATH") is { Length: > 0 } configured)
            {
                return NativeLibrary.Load(configured);
            }

            string[] candidates = OperatingSystem.IsWindows() ? ["libpng16.dll", "libpng.dll"]
                : OperatingSystem.IsMacOS() ? ["/opt/homebrew/lib/libpng16.dylib", "/usr/local/lib/libpng16.dylib", "libpng16.dylib"]
                : ["libpng16.so.16", "libpng16.so"];
            foreach (string candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                {
                    return handle;
                }
            }

            _loadError = $"tried {string.Join(", ", candidates)}; set LIBPNG_PATH";
            return IntPtr.Zero;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            _loadError = e.Message;
            return IntPtr.Zero;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName == ImportName ? Handle.Value : IntPtr.Zero;

    [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern uint png_access_version_number();

    [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int png_image_begin_read_from_memory(NativeImage* image, void* memory, nuint size);

    [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int png_image_finish_read(NativeImage* image, void* background, void* buffer, int rowStride, void* colormap);

    [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void png_image_free(NativeImage* image);

    [DllImport(ImportName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int png_image_write_to_memory(NativeImage* image, void* memory, nuint* memoryBytes, int convertTo8Bit,
        void* buffer, int rowStride, void* colormap);
}
