using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sdcb.SimdCodecs.Jpeg2000.Benchmarks;

internal static unsafe class OpenJpeg
{
    private static readonly Lazy<string?> _decompressExe = new(() => FindExe("opj_decompress"));
    private static readonly Lazy<string?> _compressExe = new(() => FindExe("opj_compress"));
    private static readonly Lazy<IntPtr> _libHandle = new(LoadOpenJp2);

    public static bool IsAvailable => _decompressExe.Value is not null && _compressExe.Value is not null;
    public static string DecompressExe => _decompressExe.Value ?? throw new FileNotFoundException("opj_decompress not found");
    public static string CompressExe => _compressExe.Value ?? throw new FileNotFoundException("opj_compress not found");

    private static string? FindExe(string name)
    {
        var bin = BenchPaths.OracleBin;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var file = Path.Combine(bin, name + ext);
        if (File.Exists(file)) return file;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathEnv.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(p, name + ext);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static IntPtr LoadOpenJp2()
    {
        var dllName = OperatingSystem.IsWindows() ? "openjp2.dll" : "libopenjp2.so";
        var dllPath = Path.Combine(BenchPaths.OracleBin, dllName);
        if (File.Exists(dllPath))
        {
            return NativeLibrary.Load(dllPath);
        }
        return NativeLibrary.Load(dllName);
    }

    // P/Invoke definitions
    [StructLayout(LayoutKind.Sequential)]
    private struct OpjImageComp
    {
        public uint Dx, Dy, W, H, X0, Y0, Prec, Bpp, Sgnd, ResnoDecoded, Factor;
        public int* Data;
        public ushort Alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpjImage
    {
        public uint X0, Y0, X1, Y1, NumComps;
        public int ColorSpace;
        public OpjImageComp* Comps;
        public byte* IccProfileBuf;
        public uint IccProfileLen;
    }

    private delegate nuint StreamReadFn(void* p_buffer, nuint p_nb_bytes, void* p_user_data);
    private delegate long StreamSkipFn(long p_nb_bytes, void* p_user_data);
    private delegate int StreamSeekFn(long p_nb_bytes, void* p_user_data);

    private struct MemoryStreamContext
    {
        public byte* Buffer;
        public long Length;
        public long Position;
    }

    private static nuint OnStreamRead(void* p_buffer, nuint p_nb_bytes, void* p_user_data)
    {
        var ctx = (MemoryStreamContext*)p_user_data;
        if (ctx->Position >= ctx->Length) return unchecked((nuint)(-1));
        var remaining = (nuint)(ctx->Length - ctx->Position);
        var count = Math.Min(p_nb_bytes, remaining);
        Buffer.MemoryCopy(ctx->Buffer + ctx->Position, p_buffer, count, count);
        ctx->Position += (long)count;
        return count;
    }

    private static long OnStreamSkip(long p_nb_bytes, void* p_user_data)
    {
        var ctx = (MemoryStreamContext*)p_user_data;
        if (ctx->Position + p_nb_bytes < 0) return -1;
        ctx->Position += p_nb_bytes;
        return p_nb_bytes;
    }

    private static int OnStreamSeek(long p_nb_bytes, void* p_user_data)
    {
        var ctx = (MemoryStreamContext*)p_user_data;
        if (p_nb_bytes < 0 || p_nb_bytes > ctx->Length) return 0;
        ctx->Position = p_nb_bytes;
        return 1;
    }

    public sealed record NativeDecodeResult(int Width, int Height, int Components, ushort[] Samples, double ElapsedMs);

    public static NativeDecodeResult DecodeDirect(byte[] codestream)
    {
        var hLib = _libHandle.Value;

        var opj_create_decompress = (delegate* unmanaged[Cdecl]<int, void*>)NativeLibrary.GetExport(hLib, "opj_create_decompress");
        var opj_destroy_codec = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(hLib, "opj_destroy_codec");
        var opj_set_default_decoder_parameters = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(hLib, "opj_set_default_decoder_parameters");
        var opj_setup_decoder = (delegate* unmanaged[Cdecl]<void*, void*, int>)NativeLibrary.GetExport(hLib, "opj_setup_decoder");
        var opj_stream_create = (delegate* unmanaged[Cdecl]<nuint, int, void*>)NativeLibrary.GetExport(hLib, "opj_stream_create");
        var opj_stream_destroy = (delegate* unmanaged[Cdecl]<void*, void>)NativeLibrary.GetExport(hLib, "opj_stream_destroy");
        var opj_stream_set_read_function = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint>, void>)NativeLibrary.GetExport(hLib, "opj_stream_set_read_function");
        var opj_stream_set_skip_function = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<long, void*, long>, void>)NativeLibrary.GetExport(hLib, "opj_stream_set_skip_function");
        var opj_stream_set_seek_function = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<long, void*, int>, void>)NativeLibrary.GetExport(hLib, "opj_stream_set_seek_function");
        var opj_stream_set_user_data = (delegate* unmanaged[Cdecl]<void*, void*, void*, void>)NativeLibrary.GetExport(hLib, "opj_stream_set_user_data");
        var opj_stream_set_user_data_length = (delegate* unmanaged[Cdecl]<void*, ulong, void>)NativeLibrary.GetExport(hLib, "opj_stream_set_user_data_length");
        var opj_read_header = (delegate* unmanaged[Cdecl]<void*, void*, OpjImage**, int>)NativeLibrary.GetExport(hLib, "opj_read_header");
        var opj_decode = (delegate* unmanaged[Cdecl]<void*, void*, OpjImage*, int>)NativeLibrary.GetExport(hLib, "opj_decode");
        var opj_end_decompress = (delegate* unmanaged[Cdecl]<void*, void*, int>)NativeLibrary.GetExport(hLib, "opj_end_decompress");
        var opj_image_destroy = (delegate* unmanaged[Cdecl]<OpjImage*, void>)NativeLibrary.GetExport(hLib, "opj_image_destroy");

        // Detect format: J2K (0) or JP2 (2)
        var isJp2 = codestream.Length >= 12 &&
                    codestream[4] == 'j' && codestream[5] == 'P' && codestream[6] == ' ' && codestream[7] == ' ';
        var format = isJp2 ? 2 : 0; // OPJ_CODEC_JP2 = 2, OPJ_CODEC_J2K = 0

        var codec = opj_create_decompress(format);
        if (codec == null) throw new InvalidOperationException("opj_create_decompress failed");

        // Allocate opj_dparameters_t (approx 8.5 KB)
        var paramBuf = stackalloc byte[16384];
        opj_set_default_decoder_parameters(paramBuf);
        opj_setup_decoder(codec, paramBuf);

        fixed (byte* pData = codestream)
        {
            var ctx = new MemoryStreamContext { Buffer = pData, Length = codestream.Length, Position = 0 };
            var stream = opj_stream_create(4096, 1);
            if (stream == null)
            {
                opj_destroy_codec(codec);
                throw new InvalidOperationException("opj_stream_create failed");
            }

            // Unmanaged function pointers for callbacks
            var pRead = (delegate* unmanaged[Cdecl]<void*, nuint, void*, nuint>)&NativeRead;
            var pSkip = (delegate* unmanaged[Cdecl]<long, void*, long>)&NativeSkip;
            var pSeek = (delegate* unmanaged[Cdecl]<long, void*, int>)&NativeSeek;

            opj_stream_set_read_function(stream, pRead);
            opj_stream_set_skip_function(stream, pSkip);
            opj_stream_set_seek_function(stream, pSeek);
            opj_stream_set_user_data(stream, &ctx, null);
            opj_stream_set_user_data_length(stream, (ulong)codestream.Length);

            OpjImage* image = null;

            // Measure precise decode time
            var sw = Stopwatch.StartNew();

            if (opj_read_header(stream, codec, &image) == 0 || image == null)
            {
                opj_stream_destroy(stream);
                opj_destroy_codec(codec);
                throw new InvalidOperationException("opj_read_header failed");
            }

            if (opj_decode(codec, stream, image) == 0)
            {
                opj_image_destroy(image);
                opj_stream_destroy(stream);
                opj_destroy_codec(codec);
                throw new InvalidOperationException("opj_decode failed");
            }

            opj_end_decompress(codec, stream);
            sw.Stop();

            var width = (int)(image->X1 - image->X0);
            var height = (int)(image->Y1 - image->Y0);
            var comps = (int)image->NumComps;
            var samples = new ushort[width * height * comps];

            // Interleave samples for multi-component or copy single
            if (comps == 1)
            {
                var compData = image->Comps[0].Data;
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = (ushort)compData[i];
            }
            else
            {
                for (var c = 0; c < comps; c++)
                {
                    var compData = image->Comps[c].Data;
                    var pixels = width * height;
                    for (var i = 0; i < pixels; i++)
                    {
                        samples[i * comps + c] = (ushort)compData[i];
                    }
                }
            }

            opj_image_destroy(image);
            opj_stream_destroy(stream);
            opj_destroy_codec(codec);

            return new NativeDecodeResult(width, height, comps, samples, sw.Elapsed.TotalMilliseconds);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint NativeRead(void* p_buffer, nuint p_nb_bytes, void* p_user_data)
    {
        var ctx = (MemoryStreamContext*)p_user_data;
        if (ctx->Position >= ctx->Length) return unchecked((nuint)(-1));
        var remaining = (nuint)(ctx->Length - ctx->Position);
        var count = Math.Min(p_nb_bytes, remaining);
        Buffer.MemoryCopy(ctx->Buffer + ctx->Position, p_buffer, count, count);
        ctx->Position += (long)count;
        return count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static long NativeSkip(long p_nb_bytes, void* p_user_data)
    {
        var ctx = (MemoryStreamContext*)p_user_data;
        if (ctx->Position + p_nb_bytes < 0) return -1;
        ctx->Position += p_nb_bytes;
        return p_nb_bytes;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NativeSeek(long p_nb_bytes, void* p_user_data)
    {
        var ctx = (MemoryStreamContext*)p_user_data;
        if (p_nb_bytes < 0 || p_nb_bytes > ctx->Length) return 0;
        ctx->Position = p_nb_bytes;
        return 1;
    }

    public static byte[] Compress(string inImagePath, string[] extraArgs)
    {
        var tempDir = Path.Combine(BenchPaths.Work, "temp");
        Directory.CreateDirectory(tempDir);

        var stem = Path.Combine(tempDir, Guid.NewGuid().ToString("n"));
        var outJ2k = stem + ".j2k";

        try
        {
            var psi = new ProcessStartInfo(CompressExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inImagePath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outJ2k);
            foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0 || !File.Exists(outJ2k))
            {
                throw new InvalidOperationException($"opj_compress failed ({proc.ExitCode}): {stdout}\n{stderr}");
            }

            return File.ReadAllBytes(outJ2k);
        }
        finally
        {
            try { if (File.Exists(outJ2k)) File.Delete(outJ2k); } catch { }
        }
    }
}
