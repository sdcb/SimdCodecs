namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// Pure-managed AVIF decoder (M0 skeleton — AV1 decoding lands in M1–M3).
/// </summary>
public static class AvifDecoder
{
    /// <summary>
    /// Reads the container metadata of an AVIF file without decoding any AV1 payload.
    /// </summary>
    public static AvifInfo ReadInfo(ReadOnlySpan<byte> data)
        => throw new NotImplementedException("AVIF container and AV1 decoding land in M1.");

    /// <summary>
    /// Decodes an AVIF image to a newly allocated RGBA8 <see cref="AvifImage"/>.
    /// </summary>
    public static AvifImage Decode(ReadOnlySpan<byte> data)
        => throw new NotImplementedException("AVIF decoding lands in M1.");

    /// <summary>
    /// Decodes an AVIF image into a caller-supplied RGBA8 buffer.
    /// </summary>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
        => throw new NotImplementedException("AVIF decoding lands in M1.");
}
