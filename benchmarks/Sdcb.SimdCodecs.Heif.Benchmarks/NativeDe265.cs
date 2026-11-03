using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>
/// P/Invoke surface for libde265 (<c>libde265.dll</c>, the vcpkg build) — the HEVC decoder libheif
/// loads — used as a black-box competitor for the <b>HEVC stage alone</b>.
/// <para>
/// <b>Clean-room.</b> Signatures and enumerator values come from <c>include/libde265/de265.h</c>, a
/// public header (AGENTS.md allows public headers for test tooling). No implementation source is read.
/// </para>
/// <para>
/// One context decodes one tile; the header's contract is that a context is single-owner, so parallel
/// use means one context per thread, which is exactly what <see cref="NativeContestants.DecodeTile"/> does per call.
/// </para>
/// </summary>
internal static unsafe partial class NativeDe265
{
    private const string Lib = "libde265";

    /// <summary>The libde265 release the numbers are recorded against (checked at start-up).</summary>
    public const string PinnedVersion = "1.1.1";

    public const int ErrorOk = 0;
    public const int ErrorImageBufferFull = 9;
    public const int ErrorWaitingForInputData = 13;

    // de265_get_image_plane channel indices.
    public const int ChannelY = 0, ChannelCb = 1, ChannelCr = 2;

    [LibraryImport(Lib)] public static partial byte* de265_get_version();
    [LibraryImport(Lib)] public static partial byte* de265_get_error_text(int error);

    [LibraryImport(Lib)] public static partial nint de265_new_decoder();
    [LibraryImport(Lib)] public static partial void de265_free_decoder(nint ctx);
    [LibraryImport(Lib)] public static partial int de265_start_worker_threads(nint ctx, int numberOfThreads);
    [LibraryImport(Lib)] public static partial int de265_push_NAL(nint ctx, void* data, int length, long pts, void* userData);
    [LibraryImport(Lib)] public static partial int de265_flush_data(nint ctx);
    [LibraryImport(Lib)] public static partial int de265_decode(nint ctx, out int more);
    [LibraryImport(Lib)] public static partial nint de265_get_next_picture(nint ctx);

    [LibraryImport(Lib)] public static partial int de265_get_image_width(nint image, int channel);
    [LibraryImport(Lib)] public static partial int de265_get_image_height(nint image, int channel);
    [LibraryImport(Lib)] public static partial int de265_get_bits_per_pixel(nint image, int channel);
    [LibraryImport(Lib)] public static partial byte* de265_get_image_plane(nint image, int channel, out int stride);

    /// <summary>The version of the loaded <c>libde265.dll</c>.</summary>
    public static string Version => Marshal.PtrToStringUTF8((nint)de265_get_version()) ?? "unknown";

    /// <summary>Fails the run when the loaded library is not the pinned release.</summary>
    public static void SelfCheck()
    {
        string version = Version;
        if (version != PinnedVersion)
        {
            throw new InvalidOperationException(
                $"libde265 {version} is loaded but the baseline is pinned to {PinnedVersion}. " +
                $"Point HEIF_NATIVE_DIR at the pinned build, or re-record the baseline.");
        }
    }

    /// <summary>Returns false when <paramref name="error"/> is <c>DE265_OK</c> or a warning (≥ 1000).</summary>
    public static bool IsError(int error) => error != ErrorOk && error < 1000;

    public static string Describe(int error) => Marshal.PtrToStringUTF8((nint)de265_get_error_text(error)) ?? $"error {error}";
}
