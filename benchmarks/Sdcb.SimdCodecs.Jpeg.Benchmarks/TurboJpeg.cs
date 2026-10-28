using System.Reflection;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Jpeg.Benchmarks;

/// <summary>
/// P/Invoke surface for the TurboJPEG 3 API of the official libjpeg-turbo release
/// binaries (fetched by <c>scripts/fetch-libjpeg-turbo.sh</c>).
/// <para>
/// <b>Clean-room.</b> libjpeg-turbo is a black box here. Every signature, struct
/// layout and enumerator below was taken from the published TurboJPEG API
/// documentation (enumerators in documented order), never from <c>turbojpeg.h</c>
/// or any other source file — and <see cref="SelfCheck"/> verifies the enumerator
/// values against the library's own behaviour at start-up.
/// </para>
/// </summary>
internal static unsafe partial class TurboJpeg
{
    private const string Lib = "turbojpeg";

    // TJINIT
    public const int InitCompress = 0, InitDecompress = 1, InitTransform = 2;

    // TJSAMP
    public const int Samp444 = 0, Samp422 = 1, Samp420 = 2, SampGray = 3;

    // TJPF
    public const int PfRgb = 0, PfBgr = 1, PfGray = 6, PfRgba = 7, PfBgra = 8;

    // TJPARAM
    public const int ParamQuality = 3, ParamSubsamp = 4, ParamJpegWidth = 5, ParamJpegHeight = 6,
        ParamFastUpsample = 9, ParamFastDct = 10, ParamOptimize = 11, ParamProgressive = 12,
        ParamRestartRows = 19;

    // TJXOP
    public const int XopNone = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct TjRegion
    {
        public int X, Y, W, H;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TjTransform
    {
        public TjRegion R;
        public int Op;
        public int Options;
        public void* Data;
        public delegate* unmanaged<short*, TjRegion, TjRegion, int, int, TjTransform*, int> CustomFilter;
    }

    [LibraryImport(Lib)] public static partial nint tj3Init(int initType);
    [LibraryImport(Lib)] public static partial void tj3Destroy(nint handle);
    [LibraryImport(Lib)] public static partial byte* tj3GetErrorStr(nint handle);
    [LibraryImport(Lib)] public static partial int tj3Set(nint handle, int param, int value);
    [LibraryImport(Lib)] public static partial int tj3Get(nint handle, int param);
    [LibraryImport(Lib)] public static partial void tj3Free(void* buffer);

    [LibraryImport(Lib)]
    public static partial int tj3Compress8(nint handle, byte* srcBuf, int width, int pitch, int height, int pixelFormat,
        byte** jpegBuf, nuint* jpegSize);

    [LibraryImport(Lib)] public static partial int tj3DecompressHeader(nint handle, byte* jpegBuf, nuint jpegSize);

    [LibraryImport(Lib)]
    public static partial int tj3Decompress8(nint handle, byte* jpegBuf, nuint jpegSize, byte* dstBuf, int pitch, int pixelFormat);

    [LibraryImport(Lib)]
    public static partial int tj3DecompressToYUVPlanes8(nint handle, byte* jpegBuf, nuint jpegSize, byte** dstPlanes, int* strides);

    [LibraryImport(Lib)] public static partial int tj3YUVPlaneWidth(int componentId, int width, int subsamp);
    [LibraryImport(Lib)] public static partial int tj3YUVPlaneHeight(int componentId, int height, int subsamp);

    [LibraryImport(Lib)]
    public static partial int tj3Transform(nint handle, byte* jpegBuf, nuint jpegSize, int n, byte** dstBufs, nuint* dstSizes,
        TjTransform* transforms);

    /// <summary>Where the fetched binaries live for this OS.</summary>
    public static string NativeDirectory =>
        Path.Combine(BenchPaths.Cache, "libjpeg-turbo", OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");

    private static int _resolverInstalled;

    /// <summary>Points the <c>turbojpeg</c> import at the fetched release binary (idempotent).</summary>
    public static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0) return;
        NativeLibrary.SetDllImportResolver(typeof(TurboJpeg).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return 0;
        var file = Path.Combine(NativeDirectory, OperatingSystem.IsWindows() ? "turbojpeg.dll" : "libturbojpeg.so");
        if (!File.Exists(file))
        {
            throw new FileNotFoundException(
                $"libjpeg-turbo not found at {file}. Run: bash benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/scripts/fetch-libjpeg-turbo.sh");
        }

        return NativeLibrary.Load(file);
    }

    public static string LastError(nint handle)
    {
        var p = tj3GetErrorStr(handle);
        return p == null ? "(no message)" : Marshal.PtrToStringUTF8((nint)p) ?? "(no message)";
    }

    public static nint Init(int initType)
    {
        EnsureLoaded();
        var h = tj3Init(initType);
        if (h == 0) throw new InvalidOperationException("tj3Init failed: " + LastError(0));
        return h;
    }

    public static void Check(nint handle, int rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"{what} failed: {LastError(handle)}");
    }

    /// <summary>
    /// Verifies the documented enumerator values against the library: encode a small
    /// 4:2:0 image, read its header back, and check the reported geometry and
    /// subsampling. A wrong value here would silently mis-configure every benchmark.
    /// </summary>
    public static void SelfCheck()
    {
        const int w = 37, h = 21;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 7);
        var jpeg = TurboEncoder.Encode(rgb, w, h, new EncodeSettings(Samp420, 90));
        var d = Init(InitDecompress);
        try
        {
            fixed (byte* p = jpeg)
                Check(d, tj3DecompressHeader(d, p, (nuint)jpeg.Length), "tj3DecompressHeader");
            if (tj3Get(d, ParamJpegWidth) != w || tj3Get(d, ParamJpegHeight) != h || tj3Get(d, ParamSubsamp) != Samp420)
                throw new InvalidOperationException("TurboJPEG enumerator self-check failed (width/height/subsamp).");
        }
        finally
        {
            tj3Destroy(d);
        }

        var info = Jpeg.JpegDecoder.ReadInfo(jpeg);
        if (info.Width != w || info.Height != h || !info.ChromaSubsampled)
            throw new InvalidOperationException("TurboJPEG self-check: our decoder disagrees about the test image.");

        // The pixel-format enumerators: BGR and BGRA are RGB and RGBA with R and B exchanged, RGBA's
        // alpha is 255, and GRAY is one byte a pixel.
        using var decoder = new TurboDecoder(fast: false);
        byte[] Decode(int pf, int bpp)
        {
            var pixels = new byte[w * h * bpp];
            decoder.Decode(jpeg, pixels, pf, bpp);
            return pixels;
        }
        byte[] rgb3 = Decode(PfRgb, 3), bgr3 = Decode(PfBgr, 3), rgba = Decode(PfRgba, 4), bgra = Decode(PfBgra, 4);
        _ = Decode(PfGray, 1);
        for (var i = 0; i < w * h; i++)
        {
            if (bgr3[3 * i] != rgb3[3 * i + 2] || bgr3[3 * i + 2] != rgb3[3 * i] || bgr3[3 * i + 1] != rgb3[3 * i + 1]
                || rgba[4 * i] != rgb3[3 * i] || rgba[4 * i + 3] != 255
                || bgra[4 * i] != rgb3[3 * i + 2] || bgra[4 * i + 2] != rgb3[3 * i] || bgra[4 * i + 3] != 255)
                throw new InvalidOperationException("TurboJPEG pixel-format enumerator self-check failed.");
        }
    }
}

/// <summary>libjpeg-turbo encode settings for the test matrix.</summary>
internal readonly record struct EncodeSettings(
    int Subsamp, int Quality, bool RestartEveryRow = false, bool Optimize = false, bool Progressive = false);

/// <summary>Encodes packed RGB with <c>tj3Compress8</c>.</summary>
internal static unsafe class TurboEncoder
{
    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height, EncodeSettings s)
    {
        var h = TurboJpeg.Init(TurboJpeg.InitCompress);
        try
        {
            TurboJpeg.Check(h, TurboJpeg.tj3Set(h, TurboJpeg.ParamQuality, s.Quality), "QUALITY");
            TurboJpeg.Check(h, TurboJpeg.tj3Set(h, TurboJpeg.ParamSubsamp, s.Subsamp), "SUBSAMP");
            TurboJpeg.Check(h, TurboJpeg.tj3Set(h, TurboJpeg.ParamRestartRows, s.RestartEveryRow ? 1 : 0), "RESTARTROWS");
            TurboJpeg.Check(h, TurboJpeg.tj3Set(h, TurboJpeg.ParamOptimize, s.Optimize ? 1 : 0), "OPTIMIZE");
            TurboJpeg.Check(h, TurboJpeg.tj3Set(h, TurboJpeg.ParamProgressive, s.Progressive ? 1 : 0), "PROGRESSIVE");

            // Grayscale output from RGB input: feed a luma-only buffer so the encoder
            // does not have to pick a conversion.
            byte* buf = null;
            nuint size = 0;
            if (s.Subsamp == TurboJpeg.SampGray)
            {
                var gray = new byte[width * height];
                for (var i = 0; i < gray.Length; i++)
                    gray[i] = (byte)((rgb[i * 3] * 77 + rgb[i * 3 + 1] * 150 + rgb[i * 3 + 2] * 29 + 128) >> 8);
                fixed (byte* src = gray)
                    TurboJpeg.Check(h, TurboJpeg.tj3Compress8(h, src, width, width, height, TurboJpeg.PfGray, &buf, &size), "tj3Compress8");
            }
            else
            {
                fixed (byte* src = rgb)
                    TurboJpeg.Check(h, TurboJpeg.tj3Compress8(h, src, width, width * 3, height, TurboJpeg.PfRgb, &buf, &size), "tj3Compress8");
            }

            var result = new ReadOnlySpan<byte>(buf, checked((int)size)).ToArray();
            TurboJpeg.tj3Free(buf);
            return result;
        }
        finally
        {
            TurboJpeg.tj3Destroy(h);
        }
    }
}

/// <summary>A reusable TurboJPEG decompressor (one handle, as a real caller would hold).</summary>
internal sealed unsafe class TurboDecoder : IDisposable
{
    private nint _handle;

    public TurboDecoder(bool fast)
    {
        _handle = TurboJpeg.Init(TurboJpeg.InitDecompress);
        // Defaults are the accurate path (islow IDCT, fancy upsampling); set explicitly anyway.
        TurboJpeg.Check(_handle, TurboJpeg.tj3Set(_handle, TurboJpeg.ParamFastDct, fast ? 1 : 0), "FASTDCT");
        TurboJpeg.Check(_handle, TurboJpeg.tj3Set(_handle, TurboJpeg.ParamFastUpsample, fast ? 1 : 0), "FASTUPSAMPLE");
    }

    public (int Width, int Height) ReadSize(ReadOnlySpan<byte> jpeg)
    {
        fixed (byte* p = jpeg)
            TurboJpeg.Check(_handle, TurboJpeg.tj3DecompressHeader(_handle, p, (nuint)jpeg.Length), "tj3DecompressHeader");
        return (TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegWidth), TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegHeight));
    }

    /// <summary>Header + decode into <paramref name="rgba"/> (width*4 pitch), as one call site would.</summary>
    public (int Width, int Height) DecodeRgba(ReadOnlySpan<byte> jpeg, Span<byte> rgba)
    {
        fixed (byte* p = jpeg)
        fixed (byte* d = rgba)
        {
            TurboJpeg.Check(_handle, TurboJpeg.tj3DecompressHeader(_handle, p, (nuint)jpeg.Length), "tj3DecompressHeader");
            var w = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegWidth);
            var h = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegHeight);
            if ((long)w * h * 4 > rgba.Length) throw new ArgumentException("destination too small");
            TurboJpeg.Check(_handle, TurboJpeg.tj3Decompress8(_handle, p, (nuint)jpeg.Length, d, w * 4, TurboJpeg.PfRgba), "tj3Decompress8");
            return (w, h);
        }
    }

    /// <summary>Header + decode into <paramref name="pixels"/> in TJPF <paramref name="pixelFormat"/>, tightly packed.</summary>
    public void Decode(ReadOnlySpan<byte> jpeg, Span<byte> pixels, int pixelFormat, int bytesPerPixel)
    {
        fixed (byte* p = jpeg)
        fixed (byte* d = pixels)
        {
            TurboJpeg.Check(_handle, TurboJpeg.tj3DecompressHeader(_handle, p, (nuint)jpeg.Length), "tj3DecompressHeader");
            var w = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegWidth);
            var h = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegHeight);
            if ((long)w * h * bytesPerPixel > pixels.Length) throw new ArgumentException("destination too small");
            TurboJpeg.Check(_handle, TurboJpeg.tj3Decompress8(_handle, p, (nuint)jpeg.Length, d, w * bytesPerPixel, pixelFormat), "tj3Decompress8");
        }
    }

    /// <summary>Component planes after IDCT, before upsampling/colour conversion.</summary>
    public (byte[] Plane, int Width, int Height)[] DecodeYuvPlanes(ReadOnlySpan<byte> jpeg)
    {
        fixed (byte* p = jpeg)
        {
            TurboJpeg.Check(_handle, TurboJpeg.tj3DecompressHeader(_handle, p, (nuint)jpeg.Length), "tj3DecompressHeader");
            var w = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegWidth);
            var h = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegHeight);
            var subsamp = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamSubsamp);
            var n = subsamp == TurboJpeg.SampGray ? 1 : 3;
            var result = new (byte[] Plane, int Width, int Height)[n];
            var handles = new GCHandle[n];
            var ptrs = stackalloc byte*[3];
            var strides = stackalloc int[3];
            try
            {
                for (var c = 0; c < n; c++)
                {
                    var pw = TurboJpeg.tj3YUVPlaneWidth(c, w, subsamp);
                    var ph = TurboJpeg.tj3YUVPlaneHeight(c, h, subsamp);
                    result[c] = (new byte[pw * ph], pw, ph);
                    handles[c] = GCHandle.Alloc(result[c].Plane, GCHandleType.Pinned);
                    ptrs[c] = (byte*)handles[c].AddrOfPinnedObject();
                    strides[c] = pw;
                }

                TurboJpeg.Check(_handle, TurboJpeg.tj3DecompressToYUVPlanes8(_handle, p, (nuint)jpeg.Length, ptrs, strides),
                    "tj3DecompressToYUVPlanes8");
            }
            finally
            {
                foreach (var gh in handles)
                    if (gh.IsAllocated) gh.Free();
            }

            return result;
        }
    }

    /// <summary>
    /// Header + decode to component planes (entropy decoding + IDCT only: no upsampling, no
    /// colour conversion) into <paramref name="planes"/>, laid out back to back. Times
    /// libjpeg-turbo's own stage split in the A/B, as a black box.
    /// </summary>
    public int DecodePlanesInto(ReadOnlySpan<byte> jpeg, Span<byte> planes)
    {
        fixed (byte* p = jpeg)
        fixed (byte* d = planes)
        {
            TurboJpeg.Check(_handle, TurboJpeg.tj3DecompressHeader(_handle, p, (nuint)jpeg.Length), "tj3DecompressHeader");
            var w = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegWidth);
            var h = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamJpegHeight);
            var subsamp = TurboJpeg.tj3Get(_handle, TurboJpeg.ParamSubsamp);
            var n = subsamp == TurboJpeg.SampGray ? 1 : 3;
            var ptrs = stackalloc byte*[3];
            var strides = stackalloc int[3];
            var offset = 0L;
            for (var c = 0; c < n; c++)
            {
                var pw = TurboJpeg.tj3YUVPlaneWidth(c, w, subsamp);
                ptrs[c] = d + offset;
                strides[c] = pw;
                offset += (long)pw * TurboJpeg.tj3YUVPlaneHeight(c, h, subsamp);
            }

            if (offset > planes.Length) throw new ArgumentException("destination too small");
            TurboJpeg.Check(_handle, TurboJpeg.tj3DecompressToYUVPlanes8(_handle, p, (nuint)jpeg.Length, ptrs, strides),
                "tj3DecompressToYUVPlanes8");
            return n;
        }
    }

    public void Dispose()
    {
        if (_handle != 0) TurboJpeg.tj3Destroy(_handle);
        _handle = 0;
    }
}

/// <summary>
/// Quantized DCT coefficients as libjpeg-turbo's entropy decoder produced them,
/// captured through the documented <c>tj3Transform</c> custom-filter callback on an
/// identity transform (TJXOP_NONE).
/// </summary>
internal static unsafe class TurboCoefficients
{
    /// <summary>Per component: block grid (natural-order 64-coefficient blocks), keyed by block (x, y).</summary>
    public sealed class Plane
    {
        public int BlocksW, BlocksH;
        public short[] Coeffs = [];
    }

    [ThreadStatic] private static Dictionary<int, Plane>? _capture;
    [ThreadStatic] private static Func<int, int, int, short[]?>? _rewrite;

    /// <summary>
    /// Re-encodes <paramref name="jpeg"/> losslessly (identity transform), replacing each
    /// block's quantized coefficients with <paramref name="provider"/>(component, bx, by)
    /// when it returns non-null. Used to synthesize streams with exact, known coefficients.
    /// </summary>
    public static byte[] Rewrite(ReadOnlySpan<byte> jpeg, Func<int, int, int, short[]?> provider)
    {
        var h = TurboJpeg.Init(TurboJpeg.InitTransform);
        _rewrite = provider;
        try
        {
            var xf = new TurboJpeg.TjTransform { Op = TurboJpeg.XopNone, CustomFilter = &RewriteFilter };
            byte* dst = null;
            nuint dstSize = 0;
            fixed (byte* p = jpeg)
                TurboJpeg.Check(h, TurboJpeg.tj3Transform(h, p, (nuint)jpeg.Length, 1, &dst, &dstSize, &xf), "tj3Transform");
            var result = new ReadOnlySpan<byte>(dst, checked((int)dstSize)).ToArray();
            TurboJpeg.tj3Free(dst);
            return result;
        }
        finally
        {
            _rewrite = null;
            TurboJpeg.tj3Destroy(h);
        }
    }

    [UnmanagedCallersOnly]
    private static int RewriteFilter(short* coeffs, TurboJpeg.TjRegion arrayRegion, TurboJpeg.TjRegion planeRegion,
        int componentId, int transformId, TurboJpeg.TjTransform* transform)
    {
        try
        {
            var bw = (arrayRegion.W + 7) / 8;
            var bh = (arrayRegion.H + 7) / 8;
            for (var by = 0; by < bh; by++)
            {
                for (var bx = 0; bx < bw; bx++)
                {
                    var block = _rewrite!(componentId, arrayRegion.X / 8 + bx, arrayRegion.Y / 8 + by);
                    if (block is not null)
                        block.CopyTo(new Span<short>(coeffs + ((long)by * bw + bx) * 64, 64));
                }
            }

            return 0;
        }
        catch
        {
            return -1;
        }
    }

    public static Dictionary<int, Plane> Read(ReadOnlySpan<byte> jpeg)
    {
        var h = TurboJpeg.Init(TurboJpeg.InitTransform);
        _capture = new Dictionary<int, Plane>();
        try
        {
            var xf = new TurboJpeg.TjTransform { Op = TurboJpeg.XopNone, CustomFilter = &Filter };
            byte* dst = null;
            nuint dstSize = 0;
            fixed (byte* p = jpeg)
                TurboJpeg.Check(h, TurboJpeg.tj3Transform(h, p, (nuint)jpeg.Length, 1, &dst, &dstSize, &xf), "tj3Transform");
            if (dst != null) TurboJpeg.tj3Free(dst);
            return _capture;
        }
        finally
        {
            _capture = null;
            TurboJpeg.tj3Destroy(h);
        }
    }

    [UnmanagedCallersOnly]
    private static int Filter(short* coeffs, TurboJpeg.TjRegion arrayRegion, TurboJpeg.TjRegion planeRegion,
        int componentId, int transformId, TurboJpeg.TjTransform* transform)
    {
        try
        {
            var cap = _capture!;
            if (!cap.TryGetValue(componentId, out var plane))
            {
                plane = new Plane { BlocksW = (planeRegion.W + 7) / 8, BlocksH = (planeRegion.H + 7) / 8 };
                plane.Coeffs = new short[plane.BlocksW * plane.BlocksH * 64];
                cap[componentId] = plane;
            }

            // The array covers arrayRegion (in samples) of the component plane, as
            // consecutive 8x8 blocks, block rows top to bottom.
            var bw = (arrayRegion.W + 7) / 8;
            var bh = (arrayRegion.H + 7) / 8;
            var bx0 = arrayRegion.X / 8;
            var by0 = arrayRegion.Y / 8;
            var copyW = Math.Min(bw, plane.BlocksW - bx0);
            for (var by = 0; by < bh && by0 + by < plane.BlocksH; by++)
            {
                var src = new ReadOnlySpan<short>(coeffs + (long)by * bw * 64, copyW * 64);
                src.CopyTo(plane.Coeffs.AsSpan(((by0 + by) * plane.BlocksW + bx0) * 64, copyW * 64));
            }

            return 0;
        }
        catch
        {
            return -1;
        }
    }
}
