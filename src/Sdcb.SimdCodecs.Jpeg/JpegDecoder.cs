using System.Diagnostics.CodeAnalysis;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Pure-managed JPEG (ITU-T T.81) decoder. Baseline sequential and progressive
/// DCT, restart intervals, all chroma subsampling layouts, grayscale, YCbCr,
/// RGB-marked, and Adobe CMYK / YCCK streams. Output is 8-bit RGBA by default,
/// or BGRA / RGB at any row stride through the
/// <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, JpegPixelFormat, JpegScale)"/> overload.
///
/// <para>
/// Entropy decoding is coefficient-exact against libjpeg-turbo. Sample
/// reconstruction uses a single-precision IDCT, unrounded linear chroma
/// interpolation and T.871 colour conversion, which on real photographs are at
/// least as close to a double-precision reference decode as libjpeg-turbo's
/// default (accurate) mode. Scaled decode (<see cref="JpegScale.Half"/> and below)
/// runs a reduced inverse DCT so neither the full-resolution sample planes nor
/// the full-resolution output ever exist — the key property for decoding huge
/// scans straight to thumbnail/LOD size without large-object-heap churn. All
/// intermediate buffers are pooled; pair with <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, JpegScale)"/> and a
/// caller-pooled destination for allocation-free decoding.
/// </para>
/// </summary>
public static class JpegDecoder
{
    /// <summary>
    /// Reads the frame header only (dimensions, component count, progressive
    /// flag) — no entropy decode, no allocation beyond the result.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    public static JpegInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        var core = new JpegCore(data, 1);
        try
        {
            return core.ParseInfo();
        }
        finally
        {
            core.ReturnBuffers();
        }
    }

    /// <summary>
    /// <see cref="ReadInfo"/> for untrusted input: false, rather than an exception, when the stream is
    /// not a decodable JPEG.
    /// </summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, [NotNullWhen(true)] out JpegInfo? info)
    {
        try
        {
            info = ReadInfo(data);
            return true;
        }
        catch (InvalidDataException)
        {
            info = null;
            return false;
        }
    }

    /// <summary>
    /// The embedded ICC profile: the APP2 <c>ICC_PROFILE</c> chunks (ICC.1 Annex B.4), joined in
    /// sequence order. A profile over 64 KB spans several chunks, each with its 1-based sequence number
    /// and the chunk count.
    /// </summary>
    /// <param name="data">The JPEG file.</param>
    /// <param name="profile">The profile, or null on false.</param>
    /// <returns>False when there is none, its chunks are inconsistent (a count that differs between
    /// chunks, a missing or repeated sequence number) or the header is malformed.</returns>
    public static bool TryReadIccProfile(ReadOnlySpan<byte> data, [NotNullWhen(true)] out byte[]? profile)
    {
        profile = JoinIccChunks(data);
        return profile is not null;
    }

    private static byte[]? JoinIccChunks(ReadOnlySpan<byte> data)
    {
        List<JpegSegment> segments;
        try
        {
            segments = JpegSegmentScanner.Scan(data);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        // Identifier, then sequence number and chunk count, one byte each.
        ReadOnlySpan<byte> identifier = "ICC_PROFILE\0"u8;
        Range[]? chunks = null;
        var found = 0;
        foreach (var segment in segments)
        {
            var payload = segment.Payload(data);
            if (segment.Marker != 0xE2 || payload.Length < identifier.Length + 2 || !payload.StartsWith(identifier))
                continue;
            int sequence = payload[identifier.Length], count = payload[identifier.Length + 1];
            chunks ??= new Range[count];
            if (count != chunks.Length || sequence < 1 || sequence > count || !chunks[sequence - 1].Equals(default(Range)))
                return null;
            var start = segment.PayloadOffset + identifier.Length + 2;
            chunks[sequence - 1] = start..(segment.PayloadOffset + segment.PayloadLength);
            found++;
        }

        if (chunks is null || found != chunks.Length)
            return null;
        var length = 0;
        foreach (var chunk in chunks)
            length += chunk.End.Value - chunk.Start.Value;
        var profile = new byte[length];
        var at = 0;
        foreach (var chunk in chunks)
        {
            data[chunk].CopyTo(profile.AsSpan(at));
            at += chunk.End.Value - chunk.Start.Value;
        }
        return profile.Length == 0 ? null : profile;
    }

    /// <summary>Decodes to a freshly allocated RGBA image at the requested scale.</summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    public static JpegImage Decode(ReadOnlySpan<byte> data, JpegScale scale = JpegScale.Full)
    {
        var core = new JpegCore(data, ValidateScale(scale));
        try
        {
            core.Begin();
            var pixels = new byte[core.OutX * core.OutY * 4];
            core.DecodeInto(pixels, core.OutX * 4);
            return new JpegImage(core.OutX, core.OutY, pixels);
        }
        finally
        {
            core.ReturnBuffers();
        }
    }

    /// <summary>
    /// Decodes into a caller-supplied buffer (use <see cref="ReadInfo"/> +
    /// <see cref="JpegInfo.ScaledSize"/> to size it: width × height × 4 bytes).
    /// Returns the actual output dimensions. Rows are written tightly packed.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination, JpegScale scale = JpegScale.Full)
    {
        var core = new JpegCore(data, ValidateScale(scale));
        try
        {
            core.Begin();
            var required = (long)core.OutX * core.OutY * 4;
            if (rgbaDestination.Length < required)
            {
                throw new ArgumentException(
                    $"Destination needs {required} bytes for {core.OutX}×{core.OutY} RGBA, got {rgbaDestination.Length}.",
                    nameof(rgbaDestination));
            }

            core.DecodeInto(rgbaDestination, core.OutX * 4);
            return (core.OutX, core.OutY);
        }
        finally
        {
            core.ReturnBuffers();
        }
    }

    /// <summary>
    /// Decodes into a caller-supplied buffer in the given pixel <paramref name="format"/>,
    /// row <c>y</c> starting at byte <c>y × <paramref name="stride"/></c> (padding
    /// between rows is left untouched). Size it with <see cref="ReadInfo"/> +
    /// <see cref="JpegInfo.ScaledSize"/>: at least
    /// <c>stride × (height − 1) + width × bytesPerPixel</c> bytes.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a decodable JPEG.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The stride is shorter than a row, or the format is unknown.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> destination, int stride, JpegPixelFormat format, JpegScale scale = JpegScale.Full)
    {
        var bytesPerPixel = format switch
        {
            JpegPixelFormat.Rgba32 or JpegPixelFormat.Bgra32 => 4,
            JpegPixelFormat.Rgb24 or JpegPixelFormat.Bgr24 => 3,
            JpegPixelFormat.Gray8 => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
        };
        var core = new JpegCore(data, ValidateScale(scale));
        try
        {
            core.Begin();
            var rowBytes = (long)core.OutX * bytesPerPixel;
            if (stride < rowBytes)
                throw new ArgumentOutOfRangeException(nameof(stride), stride, $"Stride must be at least {rowBytes} bytes for a {core.OutX}-pixel {format} row.");
            var required = (long)stride * (core.OutY - 1) + rowBytes;
            if (destination.Length < required)
            {
                throw new ArgumentException(
                    $"Destination needs {required} bytes for {core.OutX}×{core.OutY} {format} at stride {stride}, got {destination.Length}.",
                    nameof(destination));
            }

            core.DecodeInto(destination, stride, format);
            return (core.OutX, core.OutY);
        }
        finally
        {
            core.ReturnBuffers();
        }
    }

    private static int ValidateScale(JpegScale scale)
    {
        var s = (int)scale;
        if (s != 1 && s != 2 && s != 4 && s != 8)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be Full, Half, Quarter, or Eighth.");
        return s;
    }
}
