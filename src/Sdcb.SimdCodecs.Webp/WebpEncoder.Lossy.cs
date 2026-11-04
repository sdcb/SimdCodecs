using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>Options for <see cref="WebpEncoder.EncodeLossy(ReadOnlySpan{byte}, int, int, PixelFormat, WebpLossyEncodeOptions?, int)"/>.</summary>
public sealed record WebpLossyEncodeOptions : Sdcb.SimdCodecs.Abstractions.EncodeOptions
{
    /// <summary>
    /// Quality, 0 (smallest) to 100 (best). The scale is this encoder's own, calibrated so that a
    /// quality gives about the file size libwebp gives at the same number.
    /// </summary>
    public int Quality { get; init; } = 75;

    /// <summary>Compression effort, 0 (fastest) to 6 (slowest, smallest at a given quality). Default 4.</summary>
    public int Effort { get; init; } = 4;

    /// <summary>
    /// Quality of the alpha plane of an image with transparency, 0 to 100. At 100 (the default) every
    /// alpha value is kept exactly; below it the values are reduced to fewer levels (0 and 255 always
    /// kept) before the plane is compressed losslessly. Opaque images have no alpha plane.
    /// </summary>
    public int AlphaQuality { get; init; } = 100;

    /// <summary>
    /// Keep the colour of fully transparent pixels as closely as the colour of visible ones. Off by
    /// default, as in libwebp: the RGB under alpha 0 is invisible, and the encoder replaces it with
    /// colours that cost few bits. Visible pixels and the alpha plane are coded the same way either way.
    /// </summary>
    public bool Exact { get; init; }

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

    /// <summary>
    /// Encodes a lossy WebP file: the colour as VP8 (RFC 6386) and, when the image has transparency,
    /// the alpha plane in an <c>ALPH</c> chunk (losslessly at the default
    /// <see cref="WebpLossyEncodeOptions.AlphaQuality"/>), which needs the extended (VP8X) format.
    /// </summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels, 1..16383.</param>
    /// <param name="height">Height in pixels, 1..16383.</param>
    /// <param name="format">The input layout: any <see cref="PixelFormat"/>. WebP stores 8 bits, so 16-bit input is narrowed and premultiplied input unpremultiplied; formats without alpha are stored opaque.</param>
    /// <param name="options">Quality, effort and metadata; null for the defaults.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size or buffer does not fit.</exception>
    public static byte[] EncodeLossy(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format,
        WebpLossyEncodeOptions? options = null, int stride = 0)
    {
        options ??= new WebpLossyEncodeOptions();
        (byte[] vp8, byte[]? alph) = EncodeVp8(pixels, width, height, format, options, stride, null);
        return WebpContainerWriter.Still(width, height, alph is not null, "VP8 "u8, vp8, alph,
            new WebpMetadata(options.IccProfile, options.Exif, options.Xmp));
    }

    /// <summary>Encodes a lossy WebP file into <paramref name="output"/>, from its current position.</summary>
    /// <inheritdoc cref="EncodeLossy(ReadOnlySpan{byte}, int, int, PixelFormat, WebpLossyEncodeOptions?, int)"/>
    public static void EncodeLossy(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, Stream output,
        WebpLossyEncodeOptions? options = null, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(EncodeLossy(pixels, width, height, format, options, stride));
    }

    /// <summary>The benchmark's entry: the VP8 bitstream under an explicit encoder configuration.</summary>
    internal static byte[] EncodeLossy(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format,
        Vp8EncoderConfig config)
    {
        (byte[] vp8, byte[]? alph) = EncodeVp8(pixels, width, height, format, new WebpLossyEncodeOptions { Effort = config.Effort }, 0, config);
        return WebpContainerWriter.Still(width, height, alph is not null, "VP8 "u8, vp8, alph, default);
    }

    /// <summary>The VP8 frame, and the ALPH payload when the image has transparency.</summary>
    internal static (byte[] Vp8, byte[]? Alph) EncodeVp8(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format,
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

        if (options.AlphaQuality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.AlphaQuality, "AlphaQuality must be 0..100.");
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
            byte[]? alph = null;
            if (WebpAlphaEncoder.HasAlpha(argb.AsSpan(0, n)))
            {
                alph = WebpAlphaEncoder.Encode(argb.AsSpan(0, n), width, height, config, options.AlphaQuality);
                if (!options.Exact)
                {
                    WebpAlphaEncoder.FlattenInvisible(argb.AsSpan(0, n), width, height);
                }
            }

            using var encoder = new Vp8Encoder(width, height, config);
            encoder.SetArgb(argb.AsSpan(0, n));
            return (encoder.Encode(), alph);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(argb);
        }
    }
}
