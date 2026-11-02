using System.Buffers;
using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>Options for <see cref="WebpEncoder.EncodeLossless(ReadOnlySpan{byte}, int, int, ImagePixelFormat, WebpLosslessEncodeOptions?, int)"/>.</summary>
public sealed record WebpLosslessEncodeOptions
{
    /// <summary>
    /// Compression effort, 0 (fastest, largest) to 9 (slowest, smallest). The default, 6, is a
    /// balance in the spirit of <c>cwebp -z 6</c>; the levels are this encoder's own, not libwebp's.
    /// </summary>
    public int Effort { get; init; } = 6;

    /// <summary>
    /// Keep the colour of fully transparent pixels. Off by default, as in libwebp: the RGB under
    /// alpha 0 is invisible, and making it uniform compresses better. Every visible pixel and every
    /// alpha value is kept exactly either way.
    /// </summary>
    public bool Exact { get; init; }

    /// <summary>An ICC profile to embed (<c>ICCP</c> chunk), or null.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Exif metadata to embed (<c>EXIF</c> chunk, the TIFF-structured payload without the Exif header), or null.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>XMP metadata to embed (<c>XMP </c> chunk), or null.</summary>
    public byte[]? Xmp { get; init; }
}

/// <summary>
/// Pure-managed WebP encoder. Lossless output is VP8L (RFC 9649) in a simple RIFF container, built
/// from the format specification alone; any WebP decoder reads it.
/// </summary>
public static partial class WebpEncoder
{
    /// <summary>The largest width or height VP8L can describe (14 bits).</summary>
    public const int MaximumDimension = 16384;

    /// <summary>Encodes a lossless WebP file.</summary>
    /// <param name="pixels">The image, top row first.</param>
    /// <param name="width">Width in pixels, 1..16384.</param>
    /// <param name="height">Height in pixels, 1..16384.</param>
    /// <param name="format">The input layout. Formats without alpha are stored opaque.</param>
    /// <param name="options">Effort and transparency handling; null for the defaults.</param>
    /// <param name="stride">Bytes from one input row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The size or buffer does not fit.</exception>
    public static byte[] EncodeLossless(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        WebpLosslessEncodeOptions? options = null, int stride = 0) =>
        EncodeLossless(pixels, width, height, format, options, stride, Vp8LAblation.None);

    /// <summary>The benchmark's entry: <see cref="EncodeLossless(ReadOnlySpan{byte}, int, int, ImagePixelFormat, WebpLosslessEncodeOptions?, int)"/> with stages switched off.</summary>
    internal static byte[] EncodeLossless(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        WebpLosslessEncodeOptions? options, int stride, Vp8LAblation ablation)
    {
        using var writer = EncodeVp8L(pixels, width, height, format, options, stride, ablation);
        return Container(writer.Finish(), width, height, options);
    }

    /// <summary>Encodes a lossless WebP file into <paramref name="output"/>, from its current position.</summary>
    /// <inheritdoc cref="EncodeLossless(ReadOnlySpan{byte}, int, int, ImagePixelFormat, WebpLosslessEncodeOptions?, int)"/>
    public static void EncodeLossless(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format, Stream output,
        WebpLosslessEncodeOptions? options = null, int stride = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = EncodeVp8L(pixels, width, height, format, options, stride, Vp8LAblation.None);
        output.Write(Container(writer.Finish(), width, height, options));
    }

    /// <summary>The VP8L bitstream in its container: the simple format, or VP8X when there is metadata.</summary>
    private static byte[] Container(ReadOnlySpan<byte> vp8l, int width, int height, WebpLosslessEncodeOptions? options)
    {
        // The VP8L header's alpha_is_used bit: bit 28 after the signature byte, i.e. bit 4 of byte 4.
        bool hasAlpha = (vp8l[4] & 0x10) != 0;
        return WebpContainerWriter.Still(width, height, hasAlpha, "VP8L"u8, vp8l, default,
            new WebpMetadata(options?.IccProfile, options?.Exif, options?.Xmp));
    }

    internal static Vp8LBitWriter EncodeVp8L(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format,
        WebpLosslessEncodeOptions? options, int stride, Vp8LAblation ablation)
    {
        options ??= new WebpLosslessEncodeOptions();
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension)
        {
            throw new ArgumentException($"WebP size {width}x{height} must be 1..{MaximumDimension} on each side.");
        }

        if (options.Effort is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Effort, "Effort must be 0..9.");
        }

        if (!PixelConverter.TryGetLayout(width, height, format, stride, pixels.Length, out int rowStride))
        {
            throw new ArgumentException($"The pixels do not hold a {width}x{height} {format} image with stride {stride}.", nameof(pixels));
        }

        int n = width * height;
        uint[] argb = ArrayPool<uint>.Shared.Rent(n);
        try
        {
            ToArgb(pixels, width, height, format, rowStride, argb);
            var writer = new Vp8LBitWriter(n / 2 + 1024);
            try
            {
                Vp8LEncoder.Encode(argb, width, height, options.Exact, options.Effort, writer, ablation);
                return writer;
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(argb);
        }
    }

    /// <summary>
    /// Packs the input as ARGB words. A little-endian ARGB word is B, G, R, A in memory, so RGB-order
    /// input converts to <see cref="ImagePixelFormat.Bgra32"/> and BGR-order input, read as if it were
    /// RGB, to <see cref="ImagePixelFormat.Rgba32"/>.
    /// </summary>
    internal static void ToArgb(ReadOnlySpan<byte> pixels, int width, int height, ImagePixelFormat format, int rowStride, uint[] argb)
    {
        (int channels, ImagePixelFormat target) = format switch
        {
            ImagePixelFormat.Rgba32 => (4, ImagePixelFormat.Bgra32),
            ImagePixelFormat.Bgra32 => (4, ImagePixelFormat.Rgba32),
            ImagePixelFormat.Rgb24 => (3, ImagePixelFormat.Bgra32),
            ImagePixelFormat.Bgr24 => (3, ImagePixelFormat.Rgba32),
            ImagePixelFormat.Gray8 => (1, ImagePixelFormat.Bgra32),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        int bpp = PixelConverter.BytesPerPixel(format);
        Span<byte> words = System.Runtime.InteropServices.MemoryMarshal.AsBytes(argb.AsSpan(0, width * height));
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = pixels.Slice(y * rowStride, width * bpp);
            Span<byte> destination = words.Slice(y * width * 4, width * 4);
            if (format == ImagePixelFormat.Bgra32)
            {
                row.CopyTo(destination);
            }
            else
            {
                PixelConverter.ConvertRow(row, channels, SampleFormat.UInt8, destination, target, width);
            }
        }

        if (!BitConverter.IsLittleEndian)
        {
            BinaryPrimitives.ReverseEndianness(argb.AsSpan(0, width * height), argb.AsSpan(0, width * height));
        }
    }
}
