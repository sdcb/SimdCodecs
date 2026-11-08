using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for standalone <c>.jb2</c> files, bridging
/// <see cref="Jbig2Decoder.DecodeFile(ReadOnlySpan{byte}, CancellationToken)"/> into the <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// This is the <em>courtesy</em> entry point, not the main one. JBIG2's reason to
/// exist in this family is PDF's <c>/JBIG2Decode</c> filter, and a PDF-embedded
/// stream has no file header to sniff, keeps its segment dictionaries in a
/// separate <c>/JBIG2Globals</c> stream, and takes its dimensions from the image
/// dictionary — none of which fits a <c>(bytes) -&gt; image</c> contract. Those
/// callers use <see cref="Jbig2Decoder.Decode(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, int, CancellationToken)"/>
/// directly. Registering here just means a real <c>.jb2</c> file on disk decodes
/// through the same facade as everything else. <see cref="TryDecode"/> gives page 1;
/// <see cref="TryOpenFrames"/> gives every page, frame <c>i</c> being page <c>i + 1</c>.
/// </para>
/// <para>
/// Fidelity mapping: bilevel expands to 1-channel <see cref="SampleFormat.UInt8"/>
/// grey, black 0 / white 255. There is no 1-bit sample format, and a consumer that
/// wants the raw bits (an <c>ImageMask</c>, say) should read
/// <see cref="Jbig2Image.Bits"/> rather than go through the facade.
/// </para>
/// </summary>
public sealed class Jbig2ImageDecoder : IMultiFrameImageDecoder
{
    /// <summary>The registration for <c>SimdCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Jbig2,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        TryOpenFrames = TryOpenFrames,
    };

    /// <inheritdoc />
    public static int SignatureLength => 8;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[..8].SequenceEqual(Jbig2Decoder.FileSignature);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (!CanDecode(data)) return false;
        if (!Jbig2Decoder.TryReadFileInfo(data, out _, out _, out var pages)) return false;
        if (!TryReadPageInfo(data, 1, out info)) return false;
        info = info with { FrameCount = Math.Max(1, pages) };
        return true;
    }

    /// <inheritdoc />
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator) =>
        ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        TryDecodePage(data, 1, options, cancellationToken, out image);

    /// <inheritdoc />
    /// <remarks>Every page is a frame: frame <c>i</c> is page <c>i + 1</c>, with its own size, decoded as
    /// <see cref="Jbig2Decoder.DecodeFile(ReadOnlySpan{byte}, int, CancellationToken)"/> decodes it. Pages are
    /// independent, so any order costs the same: the segment headers, then that page's segments and the
    /// page-0 ones it refers to.</remarks>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        reader = null;
        if (!CanDecode(data.Span)) return false;
        if (!Jbig2Decoder.TryReadFileInfo(data.Span, out _, out _, out var pages) || pages == 0) return false;
        reader = new Jbig2FrameReader(data, pages);
        return true;
    }

    /// <summary>The layout <see cref="TryDecodePage"/> gives page <paramref name="pageNumber"/> (1-based), from its
    /// page information segment alone.</summary>
    internal static bool TryReadPageInfo(ReadOnlySpan<byte> data, int pageNumber, out ImageInfo info)
    {
        info = default;
        if (!CanDecode(data)) return false;
        if (!Jbig2Decoder.TryReadPageSize(data, pageNumber, out var width, out var height)) return false;

        // Bilevel: one bit per pixel in the file, decoded as 0 / 255 grey.
        info = new ImageInfo(width, height, 1, SampleFormat.UInt8)
        {
            Format = ImageFormat.Jbig2,
            BitDepth = 1,
        };
        return true;
    }

    /// <summary>Page <paramref name="pageNumber"/> (1-based) as <see cref="TryDecode"/> decodes page 1.</summary>
    internal static bool TryDecodePage(ReadOnlySpan<byte> data, int pageNumber, DecodeOptions? options,
        CancellationToken cancellationToken, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        if (!CanDecode(data)) return false;
        if (!Jbig2Decoder.TryReadPageSize(data, pageNumber, out var w, out var h)) return false;

        options?.Validate(w, h);

        Jbig2Image page;
        try
        {
            page = Jbig2Decoder.DecodeFile(data, pageNumber, cancellationToken);
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }

        var raster = page.ToRaster(options?.Pool);
        try
        {
            image = DecodeOptions.Apply(raster, options);
            return true;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);
        return TryDecodePageInto(data, 1, destination, stride, format, options, cancellationToken);
    }

    /// <summary>
    /// Page <paramref name="pageNumber"/> (1-based), whole, as <see cref="TryDecodeInto"/> writes page 1. The caller
    /// sends options that are not <see cref="DecodeOptions.IsFullImage"/> through <see cref="ImageDecoderFallback"/>.
    /// </summary>
    internal static bool TryDecodePageInto(ReadOnlySpan<byte> data, int pageNumber, Span<byte> destination, int stride,
        PixelFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        if (!CanDecode(data)) return false;
        if (!Jbig2Decoder.TryReadPageSize(data, pageNumber, out var width, out var height)) return false;

        options?.Validate(width, height);

        try
        {
            var page = Jbig2Decoder.DecodeFile(data, pageNumber, cancellationToken);
            if (!PixelConverter.TryGetLayout(width, height, format, stride, destination.Length, out var rowStride)) return false;

            // Each row becomes the grey of ToRaster (black 0, white 255), then the requested layout.
            var bits = page.Bits;
            var rowBytes = width * format.BytesPerPixel;
            var grey = new byte[width];
            for (var y = 0; y < height; y++)
            {
                if ((y & 63) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var row = bits.Slice(y * width, width);
                for (var x = 0; x < width; x++)
                    grey[x] = row[x] != 0 ? (byte)0 : (byte)255;
                PixelConverter.ConvertRow(grey, PixelFormat.Gray8, destination.Slice(y * rowStride, rowBytes), format, width);
            }

            return true;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>JBIG2 carries no metadata blocks: a readable file gives <see cref="ImageMetadata.Empty"/>.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata) =>
        ImageDecoderFallback.TryReadNoMetadata(TryReadInfo, data, out metadata);

    /// <inheritdoc />
    /// <remarks>JBIG2 has no thumbnails: always false.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    /// <summary>
    /// What "undecodable" means for a <c>bool</c>-returning <c>Try</c> method: the
    /// payload is hostile or unsupported, and the caller wants <c>false</c> rather
    /// than an exception.
    /// <para>
    /// <see cref="OverflowException"/> and <see cref="OutOfMemoryException"/> are
    /// in the list because dimension arithmetic on attacker-chosen numbers used to
    /// reach both, and a <c>Try</c> method that throws on hostile input is a
    /// contract break the facade cannot paper over —
    /// <c>SimdCodecs.TryDecode</c> delegates here without a catch of its own.
    /// <see cref="Jbig2Limits"/> should now make both unreachable from geometry, so
    /// this is the belt to that braces: a 256 MiB bitmap can still fail to allocate
    /// on a small device, and <c>false</c> is the honest answer there too.
    /// </para>
    /// </summary>
    private static bool IsUndecodable(Exception e) =>
        e is InvalidDataException or ArgumentException or NotSupportedException
          or OverflowException or OutOfMemoryException;
}
