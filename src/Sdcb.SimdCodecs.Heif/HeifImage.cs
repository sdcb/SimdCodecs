namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// A decoded HEIF image holding an RGBA8 raster.
/// </summary>
public sealed class HeifImage : IDisposable
{
    private byte[]? _pixels;

    internal HeifImage(int width, int height, byte[] pixels, int bitDepth, byte[]? iccProfile)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        IccProfile = iccProfile;
        _pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Source bit depth (8 or 10).</summary>
    public int BitDepth { get; }

    /// <summary>ICC profile extracted from the container, or <see langword="null"/>.</summary>
    public byte[]? IccProfile { get; }

    /// <summary>
    /// Decoded RGBA8 pixels, tightly packed, row-major.
    /// The array contains <c>Width × Height × 4</c> bytes.
    /// </summary>
    public ReadOnlySpan<byte> Rgba => (_pixels ?? throw new ObjectDisposedException(nameof(HeifImage))).AsSpan();

    /// <inheritdoc />
    public void Dispose() => _pixels = null;
}
