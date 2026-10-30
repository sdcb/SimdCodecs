namespace Sdcb.SimdCodecs.Qoi;

/// <summary>The colour space byte of a QOI header. It is informational: it never changes how pixels decode.</summary>
public enum QoiColorSpace : byte
{
    /// <summary>sRGB with linear alpha (0).</summary>
    Srgb = 0,

    /// <summary>Every channel linear (1).</summary>
    Linear = 1,
}

/// <summary>What a QOI header says about an image.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Channels">3 (RGB) or 4 (RGBA), as the header declares them.</param>
/// <param name="ColorSpace">The header's colour space.</param>
public readonly record struct QoiInfo(int Width, int Height, int Channels, QoiColorSpace ColorSpace);

/// <summary>A decoded QOI image: tightly packed 8-bit RGB, or RGBA when the header declares 4 channels.</summary>
public sealed class QoiImage
{
    internal QoiImage(int width, int height, int channels, QoiColorSpace colorSpace, byte[] pixels)
    {
        Width = width;
        Height = height;
        Channels = channels;
        ColorSpace = colorSpace;
        Pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>3 (RGB) or 4 (non-premultiplied RGBA).</summary>
    public int Channels { get; }

    /// <summary>The header's colour space.</summary>
    public QoiColorSpace ColorSpace { get; }

    /// <summary>The pixels, top row first, exactly <c>Width * Height * Channels</c> bytes.</summary>
    public byte[] Pixels { get; }
}
