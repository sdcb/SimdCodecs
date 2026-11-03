using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// P/Invoke surface for libheif (<c>heif.dll</c>, the vcpkg build), used as a black-box competitor.
/// <para>
/// <b>Clean-room.</b> libheif and libde265 are black boxes (AGENTS.md). Every signature, struct
/// layout and enumerator below is taken from the library's <b>public headers</b>
/// (<c>include/libheif/*.h</c>), which AGENTS.md allows for test and oracle tooling; no
/// implementation source is read, and no default behaviour is inferred from header comments — only
/// from the published API contract and from what the binary does when probed.
/// </para>
/// <para>
/// Decoding happens <b>in this process</b>, on the same <c>byte[]</c> the managed decoder gets, under
/// the same CPU affinity — the whole point of this file. Spawning a CLI (<c>ffmpeg</c>) would fold
/// process start-up, DLL load and raw-output I/O into the number.
/// </para>
/// </summary>
internal static unsafe partial class NativeHeif
{
    private const string Lib = "heif";

    /// <summary>The libheif release the numbers are recorded against (checked at start-up).</summary>
    public const string PinnedVersion = "1.23.1";

    // heif_colorspace
    public const int ColorspaceYCbCr = 0, ColorspaceRGB = 1;

    // heif_chroma
    public const int Chroma420 = 1, ChromaInterleavedRGBA = 11;

    // heif_channel
    public const int ChannelY = 0, ChannelCb = 1, ChannelCr = 2, ChannelInterleaved = 10;

    /// <summary><c>heif_error</c>: code, subcode, message pointer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HeifError
    {
        public int Code;
        public int Subcode;
        public nint Message;
    }

    [LibraryImport(Lib)] public static partial byte* heif_get_version();

    [LibraryImport(Lib)] public static partial nint heif_context_alloc();
    [LibraryImport(Lib)] public static partial void heif_context_free(nint ctx);
    [LibraryImport(Lib)] public static partial HeifError heif_context_read_from_memory_without_copy(
        nint ctx, void* mem, nuint size, nint options);
    [LibraryImport(Lib)] public static partial HeifError heif_context_get_primary_image_handle(nint ctx, out nint handle);
    [LibraryImport(Lib)] public static partial void heif_image_handle_release(nint handle);
    [LibraryImport(Lib)] public static partial void heif_context_set_max_decoding_threads(nint ctx, int maxThreads);
    [LibraryImport(Lib)] public static partial int heif_image_handle_get_luma_bits_per_pixel(nint handle);

    [LibraryImport(Lib)] public static partial nint heif_decoding_options_alloc();
    [LibraryImport(Lib)] public static partial void heif_decoding_options_free(nint options);
    [LibraryImport(Lib)] public static partial HeifError heif_decode_image(
        nint handle, out nint image, int colorspace, int chroma, nint options);
    [LibraryImport(Lib)] public static partial void heif_image_release(nint image);

    [LibraryImport(Lib)] public static partial int heif_image_get_width(nint image, int channel);
    [LibraryImport(Lib)] public static partial int heif_image_get_height(nint image, int channel);
    [LibraryImport(Lib)] public static partial int heif_image_get_bits_per_pixel(nint image, int channel);
    [LibraryImport(Lib)] public static partial int heif_image_get_bits_per_pixel_range(nint image, int channel);
    [LibraryImport(Lib)] public static partial byte* heif_image_get_plane_readonly(nint image, int channel, out int stride);

    /// <summary>The version of the loaded <c>heif.dll</c>.</summary>
    public static string Version => Marshal.PtrToStringUTF8((nint)heif_get_version()) ?? "unknown";

    /// <summary>Fails the run when the loaded library is not the pinned release.</summary>
    public static void SelfCheck()
    {
        string version = Version;
        if (version != PinnedVersion)
        {
            throw new InvalidOperationException(
                $"libheif {version} is loaded but the baseline is pinned to {PinnedVersion}. " +
                $"Point HEIF_NATIVE_DIR at the pinned build, or re-record the baseline.");
        }
        if (heif_context_alloc() is var ctx && ctx == 0)
        {
            throw new InvalidOperationException("libheif could not allocate a context.");
        }
        else
        {
            heif_context_free(ctx);
        }
    }

    /// <summary>Throws with the native message when <paramref name="error"/> is not <c>heif_error_Ok</c>.</summary>
    public static void Throw(HeifError error, string what)
    {
        if (error.Code == 0) return;
        string message = Marshal.PtrToStringUTF8(error.Message) ?? "";
        throw new InvalidOperationException($"libheif {what} failed ({error.Code}/{error.Subcode}): {message}");
    }
}
