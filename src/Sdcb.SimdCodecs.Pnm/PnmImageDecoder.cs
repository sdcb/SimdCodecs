using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for PBM, PGM, PPM and PAM (P1 to P7), bridging <see cref="PnmDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. An image decodes as 8-bit samples up to a maximum value of 255,
/// 16-bit above. <see cref="TryDecodeInto"/> writes the caller's buffer directly. Malformed data makes the
/// <c>Try</c> methods return false rather than throw. PFM has its own adapter, <see cref="PfmImageDecoder"/>.
/// </summary>
public sealed class PnmImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    /// <remarks><c>P1</c> to <c>P7</c> and a whitespace byte.</remarks>
    public static int SignatureLength => 3;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => PnmHeader.HasPnmSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info) => PnmAdapter.TryReadInfo(data, pfm: false, out info);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image) =>
        PnmAdapter.TryDecode(data, pfm: false, out image);

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format) =>
        PnmAdapter.TryDecodeInto(data, pfm: false, destination, stride, format);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be at
    /// least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);
}

/// <summary>
/// <see cref="IImageDecoder"/> adapter for PFM (<c>Pf</c> grey, <c>PF</c> RGB), bridging <see cref="PnmDecoder"/>
/// into the <c>Sdcb.SimdCodecs</c> facade. An image decodes as linear 32-bit float samples, as stored (the
/// header's scale is not applied). Float samples have no 8-bit form without a tone policy, so
/// <see cref="TryDecodeInto"/> returns false; <see cref="PixelConverter.StretchToRgba8(IDecodedImage, bool)"/>
/// makes a viewable image.
/// </summary>
public sealed class PfmImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    /// <remarks><c>Pf</c> or <c>PF</c> and a whitespace byte.</remarks>
    public static int SignatureLength => 3;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => PnmHeader.HasPfmSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info) => PnmAdapter.TryReadInfo(data, pfm: true, out info);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image) =>
        PnmAdapter.TryDecode(data, pfm: true, out image);

    /// <inheritdoc />
    /// <remarks>Always false: float samples have no 8-bit form without a tone policy.</remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format) => false;
}

internal static class PnmAdapter
{
    private static readonly ColorEncoding LinearFloat = new() { Transfer = TransferFunction.Linear, Float = FloatSemantics.SceneReferred };

    private static PnmInfo Read(ReadOnlySpan<byte> data, bool pfm, out int offset)
    {
        if (pfm ? !PnmHeader.HasPfmSignature(data) : !PnmHeader.HasPnmSignature(data))
            throw new InvalidDataException(pfm ? "Not a PFM image." : "Not a PBM, PGM, PPM or PAM image.");
        return PnmHeader.Read(data, out offset, tupleType: false);
    }

    public static bool TryReadInfo(ReadOnlySpan<byte> data, bool pfm, out ImageInfo info)
    {
        try
        {
            var pnm = Read(data, pfm, out _);
            info = new ImageInfo(pnm.Width, pnm.Height, pnm.Channels, pnm.SampleFormat)
            {
                Format = pfm ? ImageFormat.Pfm : ImageFormat.Pnm,
                BitDepth = pfm ? 32 : 32 - BitOperations.LeadingZeroCount((uint)pnm.MaxValue),
            };
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, bool pfm, [NotNullWhen(true)] out IDecodedImage? image)
    {
        try
        {
            var info = Read(data, pfm, out var offset);
            image = new RasterImage(info.Width, info.Height, info.Channels, info.SampleFormat, PnmPixels.Decode(data, info, offset),
                colorEncoding: pfm ? LinearFloat : null);
            return true;
        }
        catch (InvalidDataException)
        {
            image = null;
            return false;
        }
    }

    public static bool TryDecodeInto(ReadOnlySpan<byte> data, bool pfm, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        try
        {
            var info = Read(data, pfm, out var offset);
            if (info.SampleFormat == SampleFormat.Float32
                || !PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride))
                return false;
            PnmPixels.DecodeTo(data, info, offset, destination, rowStride, format);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
