namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// Pure-managed HEIF / HEIC decoder (ISO/IEC 23008-12).
/// Decodes HEVC-coded still images and tiled grids from an ISOBMFF container.
/// </summary>
/// <remarks>
/// <para>
/// HEVC (H.265) is patent-encumbered. This package conveys no patent licence.
/// </para>
/// <para>
/// Only intra (I-frame) decoding is implemented; inter-coded sequences are not supported.
/// Supported profiles: Main (8-bit 4:2:0) and Main 10 (10-bit 4:2:0).
/// </para>
/// </remarks>
public static class HeifDecoder
{
    /// <summary>
    /// Reads the HEIF container metadata without decoding any HEVC payload.
    /// </summary>
    /// <param name="data">HEIF file bytes.</param>
    /// <returns>Container-level information: primary image dimensions, bit depth, colour info.</returns>
    /// <exception cref="InvalidDataException">The data is not a decodable HEIF container.</exception>
    public static HeifInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        throw new NotImplementedException("HEIF container parsing not yet implemented (M1).");
    }

    /// <summary>
    /// Decodes the primary image from a HEIF container into a new <see cref="HeifImage"/>.
    /// </summary>
    /// <param name="data">HEIF file bytes.</param>
    /// <returns>Decoded image with RGBA pixels.</returns>
    /// <exception cref="InvalidDataException">The container is malformed or uses an unsupported codec.</exception>
    public static HeifImage Decode(ReadOnlySpan<byte> data)
    {
        throw new NotImplementedException("HEIF decode not yet implemented (M1).");
    }

    /// <summary>
    /// Decodes the primary image into a caller-supplied RGBA buffer.
    /// </summary>
    /// <param name="data">HEIF file bytes.</param>
    /// <param name="rgbaDestination">
    /// Destination buffer, tightly packed row-major RGBA8. Must be at least
    /// <c>width × height × 4</c> bytes; call <see cref="ReadInfo"/> to obtain dimensions first.
    /// </param>
    /// <returns>The decoded (width, height) in pixels.</returns>
    /// <exception cref="InvalidDataException">The container is malformed or uses an unsupported codec.</exception>
    /// <exception cref="ArgumentException"><paramref name="rgbaDestination"/> is too small.</exception>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        throw new NotImplementedException("HEIF decode not yet implemented (M1).");
    }
}
