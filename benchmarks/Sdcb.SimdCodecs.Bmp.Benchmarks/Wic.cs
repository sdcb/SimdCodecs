using System.Diagnostics;
#if WINDOWS
using System.Windows.Media;
using System.Windows.Media.Imaging;
#endif

namespace Sdcb.SimdCodecs.Bmp.Benchmarks;

/// <summary>
/// WIC, Windows' own BMP and ICO codec, as the native competitor. It is reached through WPF's documented imaging API
/// (<c>System.Windows.Media.Imaging</c>, a thin wrapper over WIC), so no COM interface is declared by hand: decode is
/// <c>BitmapDecoder.Create</c> on the bytes (no caching, so the decode happens in <c>CopyPixels</c>), converted to
/// BGRA by <c>FormatConvertedBitmap</c> straight into the caller's buffer; encode is <c>BmpBitmapEncoder</c> over a
/// <c>BitmapSource</c> of the B, G, R(, A) pixels. The wrapper's own cost is a few allocations per call, small next
/// to a full-size image, not next to a 48×48 icon. Checked against known pixels at start-up (<see cref="SelfCheck"/>).
/// </summary>
internal static class Wic
{
#if WINDOWS
    public static bool IsAvailable => true;
#else
    public static bool IsAvailable => false;
#endif

    public static string Describe()
    {
        if (!IsAvailable) return "(not available: Windows only)";
        var dll = Path.Combine(Environment.SystemDirectory, "WindowsCodecs.dll");
        return File.Exists(dll) ? $"WindowsCodecs.dll {FileVersionInfo.GetVersionInfo(dll).FileVersion}, through WPF" : "through WPF";
    }

    /// <summary>Decodes image <paramref name="frame"/> (0 for a bitmap) into <paramref name="bgra"/>, tightly packed.</summary>
    public static void Decode(byte[] data, int frame, byte[] bgra)
    {
#if WINDOWS
        using var stream = new MemoryStream(data, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);
        var source = new FormatConvertedBitmap(decoder.Frames[frame], PixelFormats.Bgra32, null, 0);
        source.CopyPixels(bgra, source.PixelWidth * 4, 0);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>Encodes B, G, R (<paramref name="alpha"/> false) or B, G, R, A pixels as a BMP into
    /// <paramref name="output"/> (reused, as a caller would); returns its length.</summary>
    public static int Encode(byte[] pixels, int width, int height, bool alpha, MemoryStream output)
    {
#if WINDOWS
        var format = alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * (alpha ? 4 : 3));
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        output.SetLength(0);
        encoder.Save(output);
        return (int)output.Length;
#else
        throw new PlatformNotSupportedException();
#endif
    }

    /// <summary>A 2×2 24-bit bitmap of known pixels must decode to them; WIC's own BMP must decode to its input.</summary>
    public static void SelfCheck()
    {
        if (!IsAvailable) return;
        // Bottom row first: blue, green; then red, white. Rows padded to 8 bytes.
        byte[] bmp =
        [
            (byte)'B', (byte)'M', 70, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
            40, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            255, 0, 0, 0, 255, 0, 0, 0,
            0, 0, 255, 255, 255, 255, 0, 0,
        ];
        var bgra = new byte[16];
        Decode(bmp, 0, bgra);
        byte[] expected = [0, 0, 255, 255, 255, 255, 255, 255, 255, 0, 0, 255, 0, 255, 0, 255];
        if (!bgra.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"WIC self-check failed: decoded {Convert.ToHexString(bgra)}");

        // WIC writes B, G, R, A as a 32-bit BI_RGB bitmap and, reading one, ignores the fourth byte: colour comes
        // back, alpha as 255.
        var output = new MemoryStream();
        Encode([1, 2, 3, 200, 4, 5, 6, 100], 2, 1, alpha: true, output);
        var back = new byte[8];
        Decode(output.ToArray(), 0, back);
        if (!back.AsSpan().SequenceEqual((byte[])[1, 2, 3, 255, 4, 5, 6, 255]))
            throw new InvalidOperationException($"WIC self-check failed: its own BMP decoded to {Convert.ToHexString(back)}");
    }
}
