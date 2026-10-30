using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Qoi;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for QOI, bridging <see cref="QoiDecoder"/> into the <c>Sdcb.SimdCodecs</c>
/// facade. An image decodes as 8-bit RGB or RGBA, as its header declares; a linear colour space is reported as
/// a linear transfer in <see cref="IDecodedImage.ColorEncoding"/>. <see cref="TryDecodeInto"/> writes the
/// caller's buffer directly. Malformed data makes the <c>Try</c> methods return false rather than throw.
/// </summary>
public sealed class QoiImageDecoder : IImageDecoder
{
    private static readonly ColorEncoding LinearSrgb = new() { Transfer = TransferFunction.Linear };

    /// <inheritdoc />
    /// <remarks>The magic <c>qoif</c>.</remarks>
    public static int SignatureLength => 4;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => QoiCodec.HasSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            var qoi = QoiCodec.ReadHeader(data);
            info = new ImageInfo(qoi.Width, qoi.Height, qoi.Channels, SampleFormat.UInt8) { Format = ImageFormat.Qoi, BitDepth = 8 };
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
            var qoi = QoiDecoder.Decode(data);
            image = new RasterImage(qoi.Width, qoi.Height, qoi.Channels, SampleFormat.UInt8, qoi.Pixels,
                colorEncoding: qoi.ColorSpace == QoiColorSpace.Linear ? LinearSrgb : null);
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
            var info = QoiCodec.ReadHeader(data);
            if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride))
                return false;
            QoiDecoder.Decode(data, info, destination, rowStride, format);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be at
    /// least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);
}
