using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for WebP, bridging <see cref="WebpDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// Lossy (VP8), lossless (VP8L) and animated input are all accepted; an animation is presented as
/// its first composited frame, and <see cref="TryOpenFrames"/> reads all of them. Images with alpha decode as 8-bit RGBA, opaque ones as 8-bit RGB, an
/// ICCP profile is passed through, and the colour encoding is reported as assumed sRGB. Malformed or
/// unsupported data makes the <c>Try</c> methods return false rather than throw.
/// </para>
/// </summary>
public sealed class WebpImageDecoder : IMultiFrameImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 12;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= SignatureLength &&
        data[..4].SequenceEqual("RIFF"u8) &&
        data[8..12].SequenceEqual("WEBP"u8);

    /// <inheritdoc />
    /// <remarks>A still image is one frame. An animation's frames are its composited canvas, in the
    /// container's channel count, streamed through one <see cref="WebpAnimationDecoder"/>: decoding them in
    /// order composites each once, and any other order replays from the nearest independent frame.</remarks>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        try
        {
            reader = WebpFrameReader.Open(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            reader = null;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            WebpContainer container = WebpContainer.Read(data);
            WebpInfo webpInfo = container.Info;
            info = new ImageInfo(webpInfo.Width, webpInfo.Height, webpInfo.HasAlpha ? 4 : 3, SampleFormat.UInt8)
            {
                Orientation = container.HasExif ? ExifOrientation.Read(container.Exif) : ImageOrientation.Normal,
                FrameCount = Math.Max(1, webpInfo.FrameCount),
            };
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            info = default;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        try
        {
            WebpImage decoded = WebpDecoder.Decode(data);
            int channels = decoded.HasAlpha ? 4 : 3;
            byte[] pixels = channels == 4
                ? decoded.Rgba
                : ToRgb24(decoded.Rgba, decoded.Width, decoded.Height);
            image = new RasterImage(
                decoded.Width,
                decoded.Height,
                channels,
                SampleFormat.UInt8,
                pixels,
                decoded.IccProfile,
                ColorEncoding.AssumedSrgb);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Decodes into a caller-owned buffer in <paramref name="format"/> (an animation's first frame for
    /// animated input), rows <paramref name="stride"/> bytes apart (0: tightly packed). RGBA is the
    /// decoder's own output and BGRA is swapped in place; the 3- and 1-byte layouts convert from a pooled
    /// RGBA frame. Returns false, without throwing, for malformed or unsupported data or a destination
    /// that does not fit (see <see cref="PixelConverter.TryGetLayout"/>).
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="destination">The pixels.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="format">The pixel layout to write.</param>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        if (!WebpDecoder.TryReadInfo(data, out WebpInfo info))
        {
            return false;
        }

        if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out int rowStride))
        {
            return false;
        }

        byte[]? rented = null;
        try
        {
            int rgbaRow = info.Width * 4;
            if (format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32)
            {
                WebpDecoder.DecodeTo(data, destination, rowStride);
                if (format == ImagePixelFormat.Bgra32)
                {
                    for (int y = 0; y < info.Height; y++)
                    {
                        Span<byte> row = destination.Slice(y * rowStride, rgbaRow);
                        PixelConverter.ConvertRow(row, 4, SampleFormat.UInt8, row, format, info.Width);
                    }
                }

                return true;
            }

            rented = ArrayPool<byte>.Shared.Rent(checked(rgbaRow * info.Height));
            WebpDecoder.DecodeTo(data, rented, rgbaRow);
            int rowBytes = info.Width * PixelConverter.BytesPerPixel(format);
            for (int y = 0; y < info.Height; y++)
            {
                PixelConverter.ConvertRow(rented.AsSpan(y * rgbaRow, rgbaRow), 4, SampleFormat.UInt8,
                    destination.Slice(y * rowStride, rowBytes), format, info.Width);
            }

            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA (an animation's first frame for animated
    /// input). Returns false, without throwing, for malformed or unsupported data or a destination
    /// shorter than width × height × 4 bytes.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="destination">At least width × height × 4 bytes.</param>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> destination) =>
        TryDecodeInto(data, destination, 0, ImagePixelFormat.Rgba32);

    private static byte[] ToRgb24(byte[] rgba, int width, int height)
    {
        byte[] rgb = new byte[checked(width * height * 3)];
        for (int source = 0, target = 0; source < rgba.Length; source += 4)
        {
            rgb[target++] = rgba[source];
            rgb[target++] = rgba[source + 1];
            rgb[target++] = rgba[source + 2];
        }

        return rgb;
    }
}
