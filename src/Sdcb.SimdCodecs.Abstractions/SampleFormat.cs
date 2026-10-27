namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The in-memory sample type of a decoded raster's pixels. Determines the byte
/// width of each channel sample in <see cref="IDecodedImage.Pixels"/>.
/// </summary>
public enum SampleFormat
{
    /// <summary>8-bit unsigned integer per channel (1 byte/sample).</summary>
    UInt8,

    /// <summary>16-bit unsigned integer per channel, host byte order (2 bytes/sample).</summary>
    UInt16,

    /// <summary>32-bit IEEE float per channel, host byte order (4 bytes/sample).</summary>
    Float32,
}
