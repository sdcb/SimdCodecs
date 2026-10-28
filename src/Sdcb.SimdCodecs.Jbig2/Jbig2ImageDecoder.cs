using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for standalone <c>.jb2</c> files, bridging
/// <see cref="Jbig2Decoder.DecodeFile"/> into the <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// This is the <em>courtesy</em> entry point, not the main one. JBIG2's reason to
/// exist in this family is PDF's <c>/JBIG2Decode</c> filter, and a PDF-embedded
/// stream has no file header to sniff, keeps its segment dictionaries in a
/// separate <c>/JBIG2Globals</c> stream, and takes its dimensions from the image
/// dictionary — none of which fits a <c>(bytes) -&gt; image</c> contract. Those
/// callers use <see cref="Jbig2Decoder.Decode(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, int)"/>
/// directly. Registering here just means a real <c>.jb2</c> file on disk decodes
/// through the same facade as everything else.
/// </para>
/// <para>
/// Fidelity mapping: bilevel expands to 1-channel <see cref="SampleFormat.UInt8"/>
/// grey, black 0 / white 255. There is no 1-bit sample format, and a consumer that
/// wants the raw bits (an <c>ImageMask</c>, say) should read
/// <see cref="Jbig2Image.Bits"/> rather than go through the facade.
/// </para>
/// </summary>
public sealed class Jbig2ImageDecoder : IImageDecoder
{
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
        if (!Jbig2Decoder.TryReadFileInfo(data, out var width, out var height, out var pages)) return false;

        // Bilevel: one bit per pixel in the file, decoded as 0 / 255 grey.
        info = new ImageInfo(width, height, 1, SampleFormat.UInt8) { BitDepth = 1, FrameCount = Math.Max(1, pages) };
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        if (!CanDecode(data)) return false;

        try
        {
            image = Jbig2Decoder.DecodeFile(data).ToRaster();
            return true;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        if (!CanDecode(data)) return false;

        try
        {
            var page = Jbig2Decoder.DecodeFile(data);
            int width = page.Width, height = page.Height;
            if (!PixelConverter.TryGetLayout(width, height, format, stride, destination.Length, out var rowStride)) return false;

            // Each row becomes the grey of ToRaster (black 0, white 255), then the requested layout.
            var bits = page.Bits;
            var rowBytes = width * PixelConverter.BytesPerPixel(format);
            var grey = new byte[width];
            for (var y = 0; y < height; y++)
            {
                var row = bits.Slice(y * width, width);
                for (var x = 0; x < width; x++)
                    grey[x] = row[x] != 0 ? (byte)0 : (byte)255;
                PixelConverter.ConvertRow(grey, 1, SampleFormat.UInt8, destination.Slice(y * rowStride, rowBytes), format, width);
            }

            return true;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);

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
