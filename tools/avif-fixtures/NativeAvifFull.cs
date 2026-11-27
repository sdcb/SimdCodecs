using System.Runtime.InteropServices;

namespace AvifFixtures;

/// <summary>
/// Black-box P/Invoke bindings for vcpkg <c>avif.dll</c> (1.4.2).
/// Signatures, enums and struct layouts come strictly from <c>avif/avif.h</c> (AGENTS.md).
/// </summary>
internal static unsafe partial class NativeAvifFull
{
    private const string Lib = "avif";

    internal const int AVIF_TRUE = 1;
    internal const int AVIF_FALSE = 0;
    internal const int AVIF_RESULT_OK = 0;

    internal const uint AVIF_PLANES_YUV = 1;
    internal const uint AVIF_PLANES_A = 2;
    internal const uint AVIF_PLANES_ALL = 0xff;

    internal const int AVIF_CHAN_Y = 0;
    internal const int AVIF_CHAN_U = 1;
    internal const int AVIF_CHAN_V = 2;
    internal const int AVIF_CHAN_A = 3;

    internal const int AVIF_PIXEL_FORMAT_NONE = 0;
    internal const int AVIF_PIXEL_FORMAT_YUV444 = 1;
    internal const int AVIF_PIXEL_FORMAT_YUV422 = 2;
    internal const int AVIF_PIXEL_FORMAT_YUV420 = 3;
    internal const int AVIF_PIXEL_FORMAT_YUV400 = 4;

    internal const int AVIF_CHROMA_SAMPLE_POSITION_UNKNOWN = 0;
    internal const int AVIF_CHROMA_SAMPLE_POSITION_VERTICAL = 1;
    internal const int AVIF_CHROMA_SAMPLE_POSITION_COLOCATED = 2;

    internal const int AVIF_RANGE_LIMITED = 0;
    internal const int AVIF_RANGE_FULL = 1;

    internal const ushort AVIF_COLOR_PRIMARIES_BT709 = 1;
    internal const ushort AVIF_COLOR_PRIMARIES_UNSPECIFIED = 2;
    internal const ushort AVIF_COLOR_PRIMARIES_BT601 = 6;
    internal const ushort AVIF_COLOR_PRIMARIES_BT2020 = 9;
    internal const ushort AVIF_COLOR_PRIMARIES_SMPTE432 = 12;

    internal const ushort AVIF_TRANSFER_CHARACTERISTICS_BT709 = 1;
    internal const ushort AVIF_TRANSFER_CHARACTERISTICS_UNSPECIFIED = 2;
    internal const ushort AVIF_TRANSFER_CHARACTERISTICS_BT601 = 6;
    internal const ushort AVIF_TRANSFER_CHARACTERISTICS_SRGB = 13;
    internal const ushort AVIF_TRANSFER_CHARACTERISTICS_PQ = 16;
    internal const ushort AVIF_TRANSFER_CHARACTERISTICS_HLG = 18;

    internal const ushort AVIF_MATRIX_COEFFICIENTS_IDENTITY = 0;
    internal const ushort AVIF_MATRIX_COEFFICIENTS_BT709 = 1;
    internal const ushort AVIF_MATRIX_COEFFICIENTS_UNSPECIFIED = 2;
    internal const ushort AVIF_MATRIX_COEFFICIENTS_BT601 = 6;
    internal const ushort AVIF_MATRIX_COEFFICIENTS_BT2020_NCL = 9;

    internal const uint AVIF_TRANSFORM_NONE = 0;
    internal const uint AVIF_TRANSFORM_PASP = 1 << 0;
    internal const uint AVIF_TRANSFORM_CLAP = 1 << 1;
    internal const uint AVIF_TRANSFORM_IROT = 1 << 2;
    internal const uint AVIF_TRANSFORM_IMIR = 1 << 3;

    internal const int AVIF_RGB_FORMAT_RGB = 0;
    internal const int AVIF_RGB_FORMAT_RGBA = 1;

    internal const int AVIF_CODEC_CHOICE_AUTO = 0;
    internal const int AVIF_CODEC_CHOICE_AOM = 1;
    internal const int AVIF_CODEC_CHOICE_DAV1D = 2;
    internal const int AVIF_CODEC_CHOICE_SVT = 5;

    internal const uint AVIF_ADD_IMAGE_FLAG_NONE = 0;
    internal const uint AVIF_ADD_IMAGE_FLAG_FORCE_KEYFRAME = 1 << 0;
    internal const uint AVIF_ADD_IMAGE_FLAG_SINGLE = 1 << 1;

    internal const uint AVIF_IMAGE_CONTENT_COLOR_AND_ALPHA = 3;
    internal const uint AVIF_IMAGE_CONTENT_GAIN_MAP = 4;
    internal const uint AVIF_IMAGE_CONTENT_ALL = 7;

    internal const int AVIF_REPETITION_COUNT_INFINITE = -1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifRWData
    {
        public byte* Data;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifDiagnostics
    {
        public fixed byte Error[256];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifFraction
    {
        public int N;
        public int D;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifSignedFraction
    {
        public int N;
        public uint D;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifUnsignedFraction
    {
        public uint N;
        public uint D;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifPixelAspectRatioBox
    {
        public uint HSpacing;
        public uint VSpacing;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifCleanApertureBox
    {
        public uint WidthN, WidthD;
        public uint HeightN, HeightD;
        public uint HorizOffN, HorizOffD;
        public uint VertOffN, VertOffD;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifCropRect
    {
        public uint X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifContentLightLevelInformationBox
    {
        public ushort MaxCll;
        public ushort MaxPall;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifGainMap
    {
        public AvifImage* Image;
        public AvifSignedFraction GainMapMin0, GainMapMin1, GainMapMin2;
        public AvifSignedFraction GainMapMax0, GainMapMax1, GainMapMax2;
        public AvifUnsignedFraction GainMapGamma0, GainMapGamma1, GainMapGamma2;
        public AvifSignedFraction BaseOffset0, BaseOffset1, BaseOffset2;
        public AvifSignedFraction AlternateOffset0, AlternateOffset1, AlternateOffset2;
        public AvifUnsignedFraction BaseHdrHeadroom;
        public AvifUnsignedFraction AlternateHdrHeadroom;
        public int UseBaseColorSpace;
        public AvifRWData AltIcc;
        public ushort AltColorPrimaries;
        public ushort AltTransferCharacteristics;
        public ushort AltMatrixCoefficients;
        public int AltYuvRange;
        public uint AltDepth;
        public uint AltPlaneCount;
        public AvifContentLightLevelInformationBox AltClli;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifImage
    {
        public uint Width;
        public uint Height;
        public uint Depth;
        public int YuvFormat;
        public int YuvRange;
        public int YuvChromaSamplePosition;
        public byte* YuvPlane0, YuvPlane1, YuvPlane2;
        public uint YuvRowBytes0, YuvRowBytes1, YuvRowBytes2;
        public int ImageOwnsYuvPlanes;
        public byte* AlphaPlane;
        public uint AlphaRowBytes;
        public int ImageOwnsAlphaPlane;
        public int AlphaPremultiplied;
        public AvifRWData Icc;
        public ushort ColorPrimaries;
        public ushort TransferCharacteristics;
        public ushort MatrixCoefficients;
        public AvifContentLightLevelInformationBox Clli;
        public uint TransformFlags;
        public AvifPixelAspectRatioBox Pasp;
        public AvifCleanApertureBox Clap;
        public byte IrotAngle;
        public byte ImirAxis;
        public AvifRWData Exif;
        public AvifRWData Xmp;
        public nint Properties;
        public nuint NumProperties;
        public AvifGainMap* GainMap;
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifScalingMode
    {
        public AvifFraction Horizontal;
        public AvifFraction Vertical;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifIoStats
    {
        public nuint ColorObuSize;
        public nuint AlphaObuSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AvifEncoder
    {
        public int CodecChoice;
        public int MaxThreads;
        public int Speed;
        public int KeyframeInterval;
        public ulong Timescale;
        public int RepetitionCount;
        public uint ExtraLayerCount;
        public int Quality;
        public int QualityAlpha;
        public int MinQuantizer;
        public int MaxQuantizer;
        public int MinQuantizerAlpha;
        public int MaxQuantizerAlpha;
        public int TileRowsLog2;
        public int TileColsLog2;
        public int AutoTiling;
        public AvifScalingMode ScalingMode;
        public AvifIoStats IoStats;
        public AvifDiagnostics Diag;
        public nint Data;
        public nint CsOptions;
        public int HeaderFormat;
        public int QualityGainMap;
        public ulong CreationTime;
        public ulong ModificationTime;
        public int SampleTransformRecipe;
    }

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
        public AvifImage* Image;
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

    [LibraryImport(Lib)] internal static partial byte* avifVersion();
    [LibraryImport(Lib)] internal static partial void avifCodecVersions(byte* outBuffer);
    [LibraryImport(Lib)] internal static partial byte* avifResultToString(int result);
    [LibraryImport(Lib)] internal static partial void avifRWDataFree(AvifRWData* raw);

    [LibraryImport(Lib)] internal static partial AvifImage* avifImageCreate(uint width, uint height, uint depth, int yuvFormat);
    [LibraryImport(Lib)] internal static partial void avifImageDestroy(AvifImage* image);
    [LibraryImport(Lib)] internal static partial int avifImageAllocatePlanes(AvifImage* image, uint planes);
    [LibraryImport(Lib)] internal static partial int avifImageSetViewRect(AvifImage* dst, AvifImage* src, AvifCropRect* rect);
    [LibraryImport(Lib)] internal static partial int avifImageSetProfileICC(AvifImage* image, byte* icc, nuint iccSize);
    [LibraryImport(Lib)] internal static partial int avifImageSetMetadataExif(AvifImage* image, byte* exif, nuint exifSize);
    [LibraryImport(Lib)] internal static partial int avifImageSetMetadataXMP(AvifImage* image, byte* xmp, nuint xmpSize);

    [LibraryImport(Lib)] internal static partial byte* avifImagePlane(AvifImage* image, int channel);
    [LibraryImport(Lib)] internal static partial uint avifImagePlaneRowBytes(AvifImage* image, int channel);
    [LibraryImport(Lib)] internal static partial uint avifImagePlaneWidth(AvifImage* image, int channel);
    [LibraryImport(Lib)] internal static partial uint avifImagePlaneHeight(AvifImage* image, int channel);

    [LibraryImport(Lib)] internal static partial void avifRGBImageSetDefaults(AvifRgbImage* rgb, AvifImage* image);
    [LibraryImport(Lib)] internal static partial int avifImageRGBToYUV(AvifImage* image, AvifRgbImage* rgb);
    [LibraryImport(Lib)] internal static partial int avifImageYUVToRGB(AvifImage* image, AvifRgbImage* rgb);

    [LibraryImport(Lib)] internal static partial int avifCleanApertureBoxFromCropRect(AvifCleanApertureBox* clap, AvifCropRect* cropRect, uint imageW, uint imageH, AvifDiagnostics* diag);

    [LibraryImport(Lib)] internal static partial AvifGainMap* avifGainMapCreate();
    [LibraryImport(Lib)] internal static partial int avifImageComputeGainMap(AvifImage* baseImage, AvifImage* altImage, AvifGainMap* gainMap, AvifDiagnostics* diag);

    [LibraryImport(Lib)] internal static partial AvifEncoder* avifEncoderCreate();
    [LibraryImport(Lib)] internal static partial void avifEncoderDestroy(AvifEncoder* encoder);
    [LibraryImport(Lib)] internal static partial int avifEncoderWrite(AvifEncoder* encoder, AvifImage* image, AvifRWData* output);
    [LibraryImport(Lib)] internal static partial int avifEncoderAddImage(AvifEncoder* encoder, AvifImage* image, ulong durationInTimescales, uint addImageFlags);
    [LibraryImport(Lib)] internal static partial int avifEncoderAddImageGrid(AvifEncoder* encoder, uint gridCols, uint gridRows, AvifImage** cellImages, uint addImageFlags);
    [LibraryImport(Lib)] internal static partial int avifEncoderFinish(AvifEncoder* encoder, AvifRWData* output);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avifEncoderSetCodecSpecificOption(AvifEncoder* encoder, string key, string? value);

    [LibraryImport(Lib)] internal static partial AvifDecoder* avifDecoderCreate();
    [LibraryImport(Lib)] internal static partial void avifDecoderDestroy(AvifDecoder* decoder);
    [LibraryImport(Lib)] internal static partial int avifDecoderSetIOMemory(AvifDecoder* decoder, byte* data, nuint size);
    [LibraryImport(Lib)] internal static partial int avifDecoderParse(AvifDecoder* decoder);
    [LibraryImport(Lib)] internal static partial int avifDecoderNextImage(AvifDecoder* decoder);

    internal static void Check(int result, string context, AvifDiagnostics* diag = null)
    {
        if (result == AVIF_RESULT_OK) return;
        string name = Marshal.PtrToStringUTF8((nint)avifResultToString(result)) ?? result.ToString();
        string extra = diag != null ? (Marshal.PtrToStringUTF8((nint)diag->Error) ?? "") : "";
        throw new InvalidOperationException(string.IsNullOrEmpty(extra)
            ? $"{context}: libavif error {name} ({result})"
            : $"{context}: libavif error {name} ({result}) — {extra}");
    }
}
