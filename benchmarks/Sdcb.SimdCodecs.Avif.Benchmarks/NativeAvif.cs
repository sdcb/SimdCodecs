using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Avif.Benchmarks;

/// <summary>
/// Black-box P/Invoke wrapper for <c>libavif</c> (<c>avif.dll</c> + <c>dav1d.dll</c>).
/// Signatures and struct layouts come strictly from the public <c>avif/avif.h</c> header (AGENTS.md).
/// </summary>
internal static unsafe partial class NativeAvif
{
    private const string Lib = "avif";

    internal const int AvifResultOk = 0;
    internal const int AvifCodecChoiceDav1d = 2;
    internal const uint AvifCodecFlagCanDecode = 1;
    internal const int AvifRgbFormatRgba = 1;
    internal const int AvifChromaUpsamplingAutomatic = 0;
    internal const int AvifDecoderSourceAuto = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifImageTiming
    {
        public ulong Timescale;
        public double Pts;
        public ulong PtsInTimescales;
        public double Duration;
        public ulong DurationInTimescales;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifIoStats
    {
        public nuint ColorObuSize;
        public nuint AlphaObuSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifDiagnostics
    {
        public fixed byte Error[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifImageHeader
    {
        public uint Width;
        public uint Height;
        public uint Depth;
        public int YuvFormat;
        public int YuvRange;
        public int YuvChromaSamplePosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifDecoder
    {
        public int CodecChoice;
        public int MaxThreads;
        public int RequestedSource;
        public int AllowProgressive;
        public int AllowIncremental;
        public int IgnoreExif;
        public int IgnoreXmp;
        public uint ImageSizeLimit;
        public uint ImageDimensionLimit;
        public uint ImageCountLimit;
        public uint StrictFlags;

        public AvifImageHeader* Image;
        public int ImageIndex;
        public int ImageCount;
        public int ProgressiveState;
        public AvifImageTiming ImageTiming;
        public ulong Timescale;
        public double Duration;
        public ulong DurationInTimescales;
        public int RepetitionCount;
        public int AlphaPresent;
        public AvifIoStats IoStats;
        public AvifDiagnostics Diag;

        public nint Io;
        public nint Data;
        public int ImageSequenceTrackPresent;
        public uint ImageContentToDecode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifRgbImage
    {
        public uint Width;
        public uint Height;
        public uint Depth;
        public int Format;
        public int ChromaUpsampling;
        public int ChromaDownsampling;
        public int AvoidLibYuv;
        public int IgnoreAlpha;
        public int AlphaPremultiplied;
        public int IsFloat;
        public int MaxThreads;
        public byte* Pixels;
        public uint RowBytes;
    }

    [LibraryImport(Lib)]
    internal static partial byte* avifVersion();

    [LibraryImport(Lib)]
    internal static partial void avifCodecVersions(byte* outBuffer);

    [LibraryImport(Lib)]
    internal static partial byte* avifCodecName(int choice, uint requiredFlags);

    [LibraryImport(Lib)]
    internal static partial byte* avifResultToString(int result);

    [LibraryImport(Lib)]
    internal static partial AvifDecoder* avifDecoderCreate();

    [LibraryImport(Lib)]
    internal static partial void avifDecoderDestroy(AvifDecoder* decoder);

    [LibraryImport(Lib)]
    internal static partial int avifDecoderSetIOMemory(AvifDecoder* decoder, byte* data, nuint size);

    [LibraryImport(Lib)]
    internal static partial int avifDecoderParse(AvifDecoder* decoder);

    [LibraryImport(Lib)]
    internal static partial int avifDecoderNextImage(AvifDecoder* decoder);

    [LibraryImport(Lib)]
    internal static partial void avifRGBImageSetDefaults(AvifRgbImage* rgb, AvifImageHeader* image);

    [LibraryImport(Lib)]
    internal static partial int avifImageYUVToRGB(AvifImageHeader* image, AvifRgbImage* rgb);

    internal static void SelfCheck()
    {
        string version = Marshal.PtrToStringUTF8((nint)avifVersion()) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("avifVersion() returned empty string.");

        byte* dav1d = avifCodecName(AvifCodecChoiceDav1d, AvifCodecFlagCanDecode);
        if (dav1d == null)
            throw new InvalidOperationException($"libavif {version} was built without dav1d decode support.");
    }

    internal static string CodecSummary()
    {
        byte* buf = stackalloc byte[256];
        avifCodecVersions(buf);
        string version = Marshal.PtrToStringUTF8((nint)avifVersion()) ?? "?";
        string codecs = Marshal.PtrToStringUTF8((nint)buf) ?? "?";
        return $"libavif {version} ({codecs})";
    }

    internal static byte[] DecodeRgba(byte[] encoded, int maxThreads)
    {
        AvifDecoder* dec = avifDecoderCreate();
        if (dec == null) throw new OutOfMemoryException("avifDecoderCreate failed.");
        try
        {
            dec->CodecChoice = AvifCodecChoiceDav1d;
            dec->MaxThreads = maxThreads;
            dec->StrictFlags = 0;

            fixed (byte* src = encoded)
            {
                Check(avifDecoderSetIOMemory(dec, src, (nuint)encoded.Length), dec);
                Check(avifDecoderParse(dec), dec);
                Check(avifDecoderNextImage(dec), dec);

                AvifImageHeader* img = dec->Image;
                AvifRgbImage rgb = default;
                avifRGBImageSetDefaults(&rgb, img);
                rgb.Depth = 8;
                rgb.Format = AvifRgbFormatRgba;
                rgb.ChromaUpsampling = AvifChromaUpsamplingAutomatic;
                rgb.MaxThreads = maxThreads;

                int rowBytes = checked((int)img->Width * 4);
                var output = new byte[checked(rowBytes * (int)img->Height)];
                fixed (byte* dst = output)
                {
                    rgb.Pixels = dst;
                    rgb.RowBytes = (uint)rowBytes;
                    Check(avifImageYUVToRGB(img, &rgb), dec);
                }
                return output;
            }
        }
        finally
        {
            avifDecoderDestroy(dec);
        }
    }

    private static void Check(int result, AvifDecoder* dec)
    {
        if (result == AvifResultOk) return;
        string name = Marshal.PtrToStringUTF8((nint)avifResultToString(result)) ?? result.ToString();
        string diag = Marshal.PtrToStringUTF8((nint)dec->Diag.Error) ?? string.Empty;
        throw new InvalidOperationException(string.IsNullOrEmpty(diag) ? $"libavif error: {name}" : $"libavif error: {name} ({diag})");
    }
}
