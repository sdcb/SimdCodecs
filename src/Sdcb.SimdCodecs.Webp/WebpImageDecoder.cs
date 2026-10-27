using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for WebP, bridging <see cref="WebpDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// Lossy (VP8), lossless (VP8L) and animated input are all accepted; an animation is presented as
/// its first composited frame. Images with alpha decode as 8-bit RGBA, opaque ones as 8-bit RGB, an
/// ICCP profile is passed through, and the colour encoding is reported as assumed sRGB. Malformed or
/// unsupported data makes the <c>Try</c> methods return false rather than throw.
/// </para>
/// </summary>
public sealed class WebpImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 12;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> data) =>
        data.Length >= SignatureLength &&
        data[..4].SequenceEqual("RIFF"u8) &&
        data[8..12].SequenceEqual("WEBP"u8);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (!WebpDecoder.TryReadInfo(data, out WebpInfo webpInfo))
        {
            info = default;
            return false;
        }

        info = new ImageInfo(webpInfo.Width, webpInfo.Height, webpInfo.HasAlpha ? 4 : 3, SampleFormat.UInt8);
        return true;
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
    /// Decodes into a caller-owned, tightly packed RGBA8 buffer (an animation's first frame for
    /// animated input). Returns false, without throwing, for malformed or unsupported data or a
    /// destination shorter than width × height × 4 bytes.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="destination">At least width × height × 4 bytes.</param>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (!WebpDecoder.TryReadInfo(data, out WebpInfo info))
        {
            return false;
        }

        try
        {
            int requiredLength = checked(info.Width * info.Height * 4);
            if (destination.Length < requiredLength)
            {
                return false;
            }

            WebpDecoder.DecodeTo(data, destination[..requiredLength]);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

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
