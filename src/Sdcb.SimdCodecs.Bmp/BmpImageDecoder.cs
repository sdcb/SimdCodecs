using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for BMP, bridging <see cref="BmpDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade. A bitmap decodes as 8-bit RGB, or RGBA when it can carry alpha
/// (<see cref="BmpInfo.HasAlpha"/>), with a V5 header's embedded ICC profile. <see cref="TryDecodeInto"/>
/// writes the caller's buffer directly. Malformed data makes the <c>Try</c> methods return false rather
/// than throw.
/// </summary>
public sealed class BmpImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    /// <remarks>The 14-byte file header and the bitmap header's size field.</remarks>
    public static int SignatureLength => 18;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => BmpHeader.HasSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            var header = BmpHeader.ReadFile(data);
            var bmp = BmpDecoder.Info(data, header);
            info = new ImageInfo(bmp.Width, bmp.Height, bmp.Channels, SampleFormat.UInt8)
            {
                Format = ImageFormat.Bmp,
                BitDepth = BmpDecoder.BitDepth(header),
            };
            return true;
        }
        catch (InvalidDataException)
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
            var bmp = BmpDecoder.Decode(data);
            image = new RasterImage(bmp.Width, bmp.Height, bmp.Channels, SampleFormat.UInt8, bmp.Pixels, bmp.IccProfile);
            return true;
        }
        catch (InvalidDataException)
        {
            image = null;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        try
        {
            var header = BmpHeader.ReadFile(data);
            if (!PixelConverter.TryGetLayout(header.Width, header.Height, format, stride, destination.Length, out var rowStride))
                return false;
            BmpPixels.Decode(data, header, destination, rowStride, format);
            return true;
        }
        catch (InvalidDataException)
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
}
