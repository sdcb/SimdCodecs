using System;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The default concrete <see cref="IDecodedImage"/>: owns an interleaved,
/// row-major, tightly-packed pixel array and (optionally) an ICC profile. Codec
/// packages construct one from their native decode output; consumers read
/// <see cref="Pixels"/> for the fidelity tier or call <see cref="ToRgba8"/> /
/// <see cref="ExpandToRgba8"/> for display.
/// </summary>
public sealed class RasterImage : IDecodedImage
{
    private readonly byte[] _pixels;
    private readonly byte[] _icc;

    /// <param name="width">Pixel width (&gt; 0).</param>
    /// <param name="height">Pixel height (&gt; 0).</param>
    /// <param name="channels">Samples per pixel, 1..4.</param>
    /// <param name="sampleFormat">The per-channel sample type of <paramref name="pixels"/>.</param>
    /// <param name="pixels">Interleaved, row-major, tightly-packed samples in host byte order. Ownership transfers to this instance - do not mutate after handing it over.</param>
    /// <param name="iccProfile">Optional embedded ICC profile bytes.</param>
    /// <param name="colorEncoding">How the samples encode colour; null means <see cref="ColorEncoding.AssumedSrgb"/>.</param>
    public RasterImage(int width, int height, int channels, SampleFormat sampleFormat, byte[] pixels, byte[]? iccProfile = null, ColorEncoding? colorEncoding = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if (channels is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be 1..4.");
        ArgumentNullException.ThrowIfNull(pixels);

        var expected = checked(width * height * channels * BytesPerSample(sampleFormat));
        if (pixels.Length < expected)
            throw new ArgumentException($"Pixel buffer too small: {pixels.Length} < {expected} (width*height*channels*bytesPerSample).", nameof(pixels));

        Width = width;
        Height = height;
        Channels = channels;
        SampleFormat = sampleFormat;
        _pixels = pixels;
        _icc = iccProfile ?? [];
        ColorEncoding = colorEncoding ?? ColorEncoding.AssumedSrgb;
    }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public SampleFormat SampleFormat { get; }

    /// <inheritdoc />
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <inheritdoc />
    public ReadOnlySpan<byte> IccProfile => _icc;

    /// <inheritdoc />
    public ColorEncoding ColorEncoding { get; }

    /// <summary>Bytes per single channel sample for a given <see cref="SampleFormat"/>.</summary>
    public static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.UInt8 => 1,
        SampleFormat.UInt16 => 2,
        SampleFormat.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <inheritdoc />
    public byte[] ToRgba8()
    {
        var dst = new byte[checked(Width * Height * 4)];
        ExpandToRgba8(dst);
        return dst;
    }

    /// <summary>
    /// Expands into a caller-provided 8-bit RGBA destination (row-major, 4
    /// bytes/pixel, R,G,B,A byte order). <paramref name="destination"/> must be at
    /// least <c>Width * Height * 4</c> bytes. This is the allocation-free form used
    /// by a zero-copy display adapter (decode straight into a UI raster's buffer).
    /// </summary>
    public void ExpandToRgba8(Span<byte> destination)
    {
        var pixelCount = Width * Height;
        if (destination.Length < checked(pixelCount * 4))
            throw new ArgumentException("Destination too small for Width*Height*4 RGBA bytes.", nameof(destination));

        switch (SampleFormat)
        {
            case SampleFormat.UInt8:
                ExpandU8(_pixels, Channels, pixelCount, destination);
                break;
            case SampleFormat.UInt16:
                ExpandU16(_pixels, Channels, pixelCount, destination);
                break;
            default:
                // Float rasters have no canonical 8-bit mapping without a tone/stretch
                // policy (black/white point, gamma) - that is a consumer decision, not
                // a codec one. Read Pixels and apply your own conversion.
                throw new NotSupportedException(
                    $"{SampleFormat} rasters cannot be expanded to 8-bit RGBA without an explicit tone/stretch policy; read Pixels instead.");
        }
    }

    /// <inheritdoc />
    public float[] ToFloats()
    {
        var dst = new float[checked(Width * Height * 4)];
        ExpandToFloats(dst);
        return dst;
    }

    /// <summary>
    /// Widens into a caller-provided RGBA float32 destination (row-major, 4
    /// floats/pixel, R,G,B,A order) — the allocation-free form of
    /// <see cref="ToFloats"/>. <paramref name="destination"/> must be at least
    /// <c>Width * Height * 4</c> floats. Integer samples divide by their max code
    /// (exact at the endpoints); Float32 samples copy verbatim.
    /// </summary>
    public void ExpandToFloats(Span<float> destination)
    {
        var pixelCount = Width * Height;
        if (destination.Length < checked(pixelCount * 4))
            throw new ArgumentException("Destination too small for Width*Height*4 RGBA floats.", nameof(destination));

        switch (SampleFormat)
        {
            case SampleFormat.UInt8:
                ExpandU8F(_pixels, Channels, pixelCount, destination);
                break;
            case SampleFormat.UInt16:
                ExpandU16F(_pixels, Channels, pixelCount, destination);
                break;
            default:
                ExpandF32(_pixels, Channels, pixelCount, destination);
                break;
        }
    }

    private static void ExpandU8(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<byte> dst)
    {
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1: // gray -> RGB, opaque
                    dst[d] = dst[d + 1] = dst[d + 2] = src[s];
                    dst[d + 3] = 255;
                    break;
                case 2: // gray + alpha
                    dst[d] = dst[d + 1] = dst[d + 2] = src[s];
                    dst[d + 3] = src[s + 1];
                    break;
                case 3: // RGB, opaque
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = 255;
                    break;
                default: // 4: RGBA, straight copy
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = src[s + 3];
                    break;
            }
        }
    }

    private static void ExpandU16(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<byte> dst)
    {
        // Host byte order per the IDecodedImage contract, so a straight reinterpret is valid.
        var s16 = MemoryMarshal.Cast<byte, ushort>(src);
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1:
                {
                    var v = (byte)(s16[s] >> 8);
                    dst[d] = dst[d + 1] = dst[d + 2] = v;
                    dst[d + 3] = 255;
                    break;
                }
                case 2:
                {
                    var v = (byte)(s16[s] >> 8);
                    dst[d] = dst[d + 1] = dst[d + 2] = v;
                    dst[d + 3] = (byte)(s16[s + 1] >> 8);
                    break;
                }
                case 3:
                    dst[d] = (byte)(s16[s] >> 8);
                    dst[d + 1] = (byte)(s16[s + 1] >> 8);
                    dst[d + 2] = (byte)(s16[s + 2] >> 8);
                    dst[d + 3] = 255;
                    break;
                default: // 4
                    dst[d] = (byte)(s16[s] >> 8);
                    dst[d + 1] = (byte)(s16[s + 1] >> 8);
                    dst[d + 2] = (byte)(s16[s + 2] >> 8);
                    dst[d + 3] = (byte)(s16[s + 3] >> 8);
                    break;
            }
        }
    }

    // Division (not multiply-by-reciprocal) so the endpoints are exact:
    // 255/255f == 1f and 65535/65535f == 1f in IEEE single.
    private static void ExpandU8F(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<float> dst)
    {
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1: // gray -> RGB, opaque
                    dst[d] = dst[d + 1] = dst[d + 2] = src[s] / 255f;
                    dst[d + 3] = 1f;
                    break;
                case 2: // gray + alpha
                    dst[d] = dst[d + 1] = dst[d + 2] = src[s] / 255f;
                    dst[d + 3] = src[s + 1] / 255f;
                    break;
                case 3: // RGB, opaque
                    dst[d] = src[s] / 255f;
                    dst[d + 1] = src[s + 1] / 255f;
                    dst[d + 2] = src[s + 2] / 255f;
                    dst[d + 3] = 1f;
                    break;
                default: // 4: RGBA
                    dst[d] = src[s] / 255f;
                    dst[d + 1] = src[s + 1] / 255f;
                    dst[d + 2] = src[s + 2] / 255f;
                    dst[d + 3] = src[s + 3] / 255f;
                    break;
            }
        }
    }

    private static void ExpandU16F(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<float> dst)
    {
        // Host byte order per the IDecodedImage contract, so a straight reinterpret is valid.
        var s16 = MemoryMarshal.Cast<byte, ushort>(src);
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1:
                    dst[d] = dst[d + 1] = dst[d + 2] = s16[s] / 65535f;
                    dst[d + 3] = 1f;
                    break;
                case 2:
                    dst[d] = dst[d + 1] = dst[d + 2] = s16[s] / 65535f;
                    dst[d + 3] = s16[s + 1] / 65535f;
                    break;
                case 3:
                    dst[d] = s16[s] / 65535f;
                    dst[d + 1] = s16[s + 1] / 65535f;
                    dst[d + 2] = s16[s + 2] / 65535f;
                    dst[d + 3] = 1f;
                    break;
                default: // 4
                    dst[d] = s16[s] / 65535f;
                    dst[d + 1] = s16[s + 1] / 65535f;
                    dst[d + 2] = s16[s + 2] / 65535f;
                    dst[d + 3] = s16[s + 3] / 65535f;
                    break;
            }
        }
    }

    private static void ExpandF32(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<float> dst)
    {
        // Verbatim: scene-/display-referred float values pass through unclamped
        // (HDR highlights > 1 and wide-gamut negatives survive); only the channel
        // layout is widened to RGBA.
        var sf = MemoryMarshal.Cast<byte, float>(src);
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1:
                    dst[d] = dst[d + 1] = dst[d + 2] = sf[s];
                    dst[d + 3] = 1f;
                    break;
                case 2:
                    dst[d] = dst[d + 1] = dst[d + 2] = sf[s];
                    dst[d + 3] = sf[s + 1];
                    break;
                case 3:
                    dst[d] = sf[s];
                    dst[d + 1] = sf[s + 1];
                    dst[d + 2] = sf[s + 2];
                    dst[d + 3] = 1f;
                    break;
                default: // 4
                    dst[d] = sf[s];
                    dst[d + 1] = sf[s + 1];
                    dst[d + 2] = sf[s + 2];
                    dst[d + 3] = sf[s + 3];
                    break;
            }
        }
    }
}
