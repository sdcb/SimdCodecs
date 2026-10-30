using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Ico;

/// <summary>
/// Pure-managed decoder for Windows icons (<c>.ico</c>) and cursors (<c>.cur</c>), from Microsoft's documentation
/// of the icon file: a directory of images, each a PNG stream (decoded by <c>Sdcb.SimdCodecs.Png</c>) or a packed DIB
/// whose height counts its colour bitmap and a 1-bit AND mask after it (decoded by <c>Sdcb.SimdCodecs.Bmp</c>: 1, 4,
/// 8, 16, 24 or 32-bit, the AND mask becoming alpha unless 32-bit pixels carry their own). Every image decodes to
/// 8-bit RGBA.
/// <para>
/// The methods without an index decode <see cref="IcoInfo.Best"/>, the largest and deepest image. Every image comes
/// through the index overloads, or as frames through <see cref="IcoImageDecoder.TryOpenFrames"/> /
/// <see cref="CurImageDecoder.TryOpenFrames"/>.
/// </para>
/// </summary>
public static class IcoDecoder
{
    /// <summary>Reads the directory and every image's header.</summary>
    /// <exception cref="InvalidDataException">The data is not an icon or cursor, or an image header is unreadable.</exception>
    public static IcoInfo ReadInfo(ReadOnlySpan<byte> data) => IcoContainer.Read(data).ToInfo();

    /// <summary><see cref="ReadInfo"/> that returns false instead of throwing.</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IcoInfo? info)
    {
        try
        {
            info = ReadInfo(data);
            return true;
        }
        catch (InvalidDataException)
        {
            info = null;
            return false;
        }
    }

    /// <summary>Decodes the largest, deepest image (<see cref="IcoInfo.Best"/>).</summary>
    /// <exception cref="InvalidDataException">The data is not a decodable icon or cursor.</exception>
    public static IcoImage Decode(ReadOnlySpan<byte> data)
    {
        var container = IcoContainer.Read(data);
        return container.Decode(data, container.Best);
    }

    /// <summary>Decodes image <paramref name="index"/>, in directory order.</summary>
    /// <exception cref="InvalidDataException">The data is not a decodable icon or cursor.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is not an image's.</exception>
    public static IcoImage Decode(ReadOnlySpan<byte> data, int index)
    {
        var container = IcoContainer.Read(data);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, container.Entries.Length);
        return container.Decode(data, index);
    }

    /// <summary>
    /// Decodes image <paramref name="index"/> into <paramref name="destination"/> as <paramref name="format"/>, top
    /// row first, rows <paramref name="stride"/> bytes apart (0: tightly packed). Size it from <see cref="ReadInfo"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a decodable icon or cursor.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index is not an image's.</exception>
    /// <exception cref="ArgumentException">The destination is too small, or the stride shorter than a row.</exception>
    public static void DecodeTo(ReadOnlySpan<byte> data, int index, Span<byte> destination,
        ImagePixelFormat format = ImagePixelFormat.Rgba32, int stride = 0)
    {
        var container = IcoContainer.Read(data);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, container.Entries.Length);
        var entry = container.Entries[index];
        if (!PixelConverter.TryGetLayout(entry.Width, entry.Height, format, stride, destination.Length, out var rowStride))
            throw new ArgumentException($"The destination does not hold a {entry.Width}x{entry.Height} {format} image with stride {stride}.", nameof(destination));
        container.DecodeInto(data, index, destination, rowStride, format);
    }
}
