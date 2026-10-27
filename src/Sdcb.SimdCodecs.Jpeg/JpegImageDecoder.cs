using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for JPEG, bridging <see cref="JpegDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. JPEG always decodes to 8-bit RGBA
/// regardless of the SOF component count, so both the fidelity and display tiers
/// yield the same 4-channel <see cref="SampleFormat.UInt8"/> raster;
/// <see cref="TryDecodeIntoRgba8"/> is a genuine zero-copy decode via
/// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, JpegScale)"/>.
/// </summary>
public sealed class JpegImageDecoder : IImageDecoder
{
    private static ReadOnlySpan<byte> Signature => [0xFF, 0xD8, 0xFF];

    /// <inheritdoc />
    public static int SignatureLength => 3;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[..3].SequenceEqual(Signature);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        try
        {
            var ji = JpegDecoder.ReadInfo(data);
            info = new ImageInfo(ji.Width, ji.Height, 4, SampleFormat.UInt8);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        try
        {
            var jpeg = JpegDecoder.Decode(data);
            image = new RasterImage(jpeg.Width, jpeg.Height, 4, SampleFormat.UInt8, jpeg.Pixels);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        try
        {
            _ = JpegDecoder.DecodeTo(data, rgbaDestination);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}
