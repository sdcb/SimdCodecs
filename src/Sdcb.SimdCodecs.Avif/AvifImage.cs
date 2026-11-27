namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// A decoded AVIF image holding an RGBA8 raster.
/// </summary>
public sealed class AvifImage : IDisposable
{
    private byte[]? _pixels;

    internal AvifImage(int width, int height, byte[] pixels, int bitDepth, bool hasAlpha, byte[]? iccProfile)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        HasAlpha = hasAlpha;
        IccProfile = iccProfile;
        _pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Source bit depth (8, 10 or 12).</summary>
    public int BitDepth { get; }

    /// <summary>Whether the source image had an alpha plane.</summary>
    public bool HasAlpha { get; }

    /// <summary>ICC profile extracted from the container, or <see langword="null"/>.</summary>
    public byte[]? IccProfile { get; }

    /// <summary>
    /// Decoded RGBA8 pixels, tightly packed, row-major.
    /// The array contains <c>Width × Height × 4</c> bytes.
    /// </summary>
    public ReadOnlySpan<byte> Rgba => (_pixels ?? throw new ObjectDisposedException(nameof(AvifImage))).AsSpan();

    /// <inheritdoc />
    public void Dispose() => _pixels = null;
}
