using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>Options for <see cref="WebpEncoder.EncodeLossy(ReadOnlySpan{byte}, int, int, ImagePixelFormat, WebpLossyEncodeOptions?, int)"/>.</summary>
public sealed record WebpLossyEncodeOptions
{
    /// <summary>
    /// Quality, 0 (smallest) to 100 (best). The scale is this encoder's own, calibrated so that a
    /// quality gives about the file size libwebp gives at the same number.
    /// </summary>
    public int Quality { get; init; } = 75;

    /// <summary>Compression effort, 0 (fastest) to 6 (slowest, smallest at a given quality). Default 4.</summary>
    public int Effort { get; init; } = 4;

    /// <summary>An ICC profile to embed (<c>ICCP</c> chunk), or null.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Exif metadata to embed (<c>EXIF</c> chunk), or null.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>XMP metadata to embed (<c>XMP </c> chunk), or null.</summary>
    public byte[]? Xmp { get; init; }
}

public static partial class WebpEncoder
{
    /// <summary>The largest width or height a VP8 key frame can describe (14 bits).</summary>
    public const int MaximumLossyDimension = 16383;

    /// <summary>Encodes a lossy WebP file (VP8, RFC 6386).</summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels, 1..16383.</param>
    /// <param name="height">Height in pixels, 1..16383.</param>
    /// <param name="format">The input layout.</param>
    /// <param name="options">Quality, effort and metadata; null for the defaults.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size or buffer does not fit.</exception>
    /// <exception cref="NotSupportedException">The image has transparency (lossy alpha is not written yet).</exception>
    public static byte[] EncodeLossy(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        WebpLossyEncodeOptions? options = null, int stride = 0)
    {
        options ??= new WebpLossyEncodeOptions();
        byte[] vp8 = EncodeVp8(pixels, width, height, format, options, stride, null);
        return WebpContainerWriter.Still(width, height, hasAlpha: false, "VP8 "u8, vp8, default,
            new WebpMetadata(options.IccProfile, options.Exif, options.Xmp));
    }

    /// <summary>Encodes a lossy WebP file into <paramref name="output"/>, from its current position.</summary>
    /// <inheritdoc cref="EncodeLossy(ReadOnlySpan{byte}, int, int, ImagePixelFormat, WebpLossyEncodeOptions?, int)"/>
    public static void EncodeLossy(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format, Stream output,
        WebpLossyEncodeOptions? options = null, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(EncodeLossy(pixels, width, height, format, options, stride));
    }

    /// <summary>The benchmark's entry: the VP8 bitstream under an explicit encoder configuration.</summary>
    internal static byte[] EncodeLossy(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        Vp8EncoderConfig config)
    {
        byte[] vp8 = EncodeVp8(pixels, width, height, format, new WebpLossyEncodeOptions(), 0, config);
        return WebpContainerWriter.Still(width, height, hasAlpha: false, "VP8 "u8, vp8, default, default);
    }

    private static byte[] EncodeVp8(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        WebpLossyEncodeOptions options, int stride, Vp8EncoderConfig? config)
    {
        if (width is < 1 or > MaximumLossyDimension || height is < 1 or > MaximumLossyDimension)
        {
            throw new ArgumentException($"Lossy WebP size {width}x{height} must be 1..{MaximumLossyDimension} on each side.");
        }

        if (options.Quality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Quality, "Quality must be 0..100.");
        }

        if (options.Effort is < 0 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Effort, "Effort must be 0..6.");
        }

        if (!PixelConverter.TryGetLayout(width, height, format, stride, pixels.Length, out int rowStride))
        {
            throw new ArgumentException($"The pixels do not hold a {width}x{height} {format} image with stride {stride}.", nameof(pixels));
        }

        config ??= Vp8EncoderConfig.For(options.Quality, options.Effort);
        int n = width * height;
        uint[] argb = ArrayPool<uint>.Shared.Rent(n);
        try
        {
            ToArgb(pixels, width, height, format, rowStride, argb);
            for (int i = 0; i < n; i++)
            {
                if (argb[i] < 0xFF000000u)
                {
                    throw new NotSupportedException("Lossy WebP with transparency is not written yet; use EncodeLossless.");
                }
            }

            using var encoder = new Vp8Encoder(width, height, config);
            encoder.SetArgb(argb.AsSpan(0, n));
            return encoder.Encode();
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(argb);
        }
    }
}
