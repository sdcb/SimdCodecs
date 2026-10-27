namespace Sdcb.SimdCodecs.Webp;

/// <summary>The encoded representation used by a WebP image.</summary>
public enum WebpFormat
{
    Lossy,
    Lossless,
    Animated,
}

/// <summary>Dimensions and properties read from a WebP container.</summary>
public readonly record struct WebpInfo(
    int Width,
    int Height,
    bool HasAlpha,
    WebpFormat Format,
    int FrameCount);

internal static class WebpLimits
{
    public const long MaximumPixels = 1L << 26;
}

/// <summary>A decoded WebP image in tightly packed, non-premultiplied RGBA8.</summary>
public sealed class WebpImage
{
    internal WebpImage(
        int width,
        int height,
        bool hasAlpha,
        byte[] rgba,
        byte[]? iccProfile,
        byte[]? exif,
        byte[]? xmp)
    {
        Width = width;
        Height = height;
        HasAlpha = hasAlpha;
        Rgba = rgba;
        IccProfile = iccProfile;
        Exif = exif;
        Xmp = xmp;
    }

    /// <summary>The decoded image width in pixels.</summary>
    public int Width { get; }

    /// <summary>The decoded image height in pixels.</summary>
    public int Height { get; }

    /// <summary>Whether at least one pixel may have non-opaque alpha.</summary>
    public bool HasAlpha { get; }

    /// <summary>Tightly packed, non-premultiplied RGBA8 pixels.</summary>
    public byte[] Rgba { get; }

    /// <summary>The embedded ICC profile bytes, or null when no ICCP chunk is present.</summary>
    public byte[]? IccProfile { get; }

    /// <summary>The embedded EXIF payload, or null when no EXIF chunk is present.</summary>
    public byte[]? Exif { get; }

    /// <summary>The embedded XMP payload, or null when no XMP chunk is present.</summary>
    public byte[]? Xmp { get; }
}
