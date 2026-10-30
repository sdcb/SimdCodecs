using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// Pure-managed Netpbm decoder, written from the Netpbm format pages: PBM, PGM and PPM, plain (P1 to P3) and raw
/// (P4 to P6), PAM (P7) with 1 to 4 channels, and PFM (<c>Pf</c> / <c>PF</c>). Only the first image of a file is
/// read. The decisions the format pages leave open are listed in the package README.
/// </summary>
public static class PnmDecoder
{
    /// <summary>Reads the size, format, channels and maximum value from the header.</summary>
    /// <exception cref="InvalidDataException">The data is not a Netpbm image, or its header is invalid.</exception>
    public static PnmInfo ReadInfo(ReadOnlySpan<byte> data) => PnmHeader.Read(data, out _);

    /// <summary><see cref="ReadInfo"/> that returns false instead of throwing.</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out PnmInfo info)
    {
        try
        {
            info = ReadInfo(data);
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    /// <summary>
    /// Decodes to the native samples: 8-bit for a maximum up to 255 (a bitmap's black is 0, white 255), 16-bit
    /// above it, 32-bit float for PFM, each scaled to its full range (floats as stored).
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a decodable Netpbm image.</exception>
    public static PnmImage Decode(ReadOnlySpan<byte> data)
    {
        var info = PnmHeader.Read(data, out var offset);
        return new PnmImage(info, PnmPixels.Decode(data, info, offset));
    }

    /// <summary>
    /// Decodes into <paramref name="destination"/> as <paramref name="format"/>, top row first, rows
    /// <paramref name="stride"/> bytes apart (0: tightly packed). Size it with <see cref="ReadInfo"/>. Bytes
    /// between rows are left untouched; nothing is allocated. 16-bit samples give their high byte.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a decodable Netpbm image.</exception>
    /// <exception cref="NotSupportedException">The image is PFM, whose float samples have no 8-bit form without a
    /// tone policy: use <see cref="Decode"/>.</exception>
    /// <exception cref="ArgumentException">The destination is too small, or the stride shorter than a row.</exception>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> destination, ImagePixelFormat format = ImagePixelFormat.Rgba32, int stride = 0)
    {
        var info = PnmHeader.Read(data, out var offset, tupleType: false);
        if (info.SampleFormat == SampleFormat.Float32)
            throw new NotSupportedException("PFM float samples have no 8-bit form without a tone policy; use Decode.");
        if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride))
            throw new ArgumentException($"The destination does not hold a {info.Width}x{info.Height} {format} image with stride {stride}.", nameof(destination));
        PnmPixels.DecodeTo(data, info, offset, destination, rowStride, format);
    }
}
