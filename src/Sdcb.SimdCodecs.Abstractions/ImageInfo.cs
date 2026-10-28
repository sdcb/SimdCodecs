using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// Dimensions + layout read from an image's header alone, without decoding
/// pixels. Lets a caller size a destination buffer before calling
/// <see cref="IImageDecoder.TryDecodeInto"/> - the info-then-decode split
/// every real decoder already supports (PNG's IHDR / JPEG's SOF precede the
/// pixel data).
/// <para>
/// <see cref="Width"/>, <see cref="Height"/>, <see cref="Channels"/> and <see cref="SampleFormat"/>
/// describe exactly the raster <see cref="IImageDecoder.TryDecode"/> returns. The init-only
/// properties describe the file: <see cref="BitDepth"/>, <see cref="Orientation"/> and
/// <see cref="FrameCount"/>. Left unset they read as the plain case (the format's full depth,
/// <see cref="ImageOrientation.Normal"/>, one frame), so an implementation that predates them keeps
/// its meaning.
/// </para>
/// </summary>
/// <param name="Width">Pixel width, as stored (before <see cref="Orientation"/> is applied).</param>
/// <param name="Height">Pixel height, as stored.</param>
/// <param name="Channels">Samples per pixel: 1 (gray), 2 (gray+alpha), 3 (RGB), 4 (RGBA).</param>
/// <param name="SampleFormat">The per-channel sample type the full decode will produce.</param>
public readonly record struct ImageInfo(int Width, int Height, int Channels, SampleFormat SampleFormat)
{
    // Each stored so that the zero of default(ImageInfo) or an unset property is the plain case.
    private readonly byte _bitDepth;     // 0: SampleFormat's full width
    private readonly byte _orientation;  // ImageOrientation - 1
    private readonly int _extraFrames;   // FrameCount - 1

    /// <summary>
    /// Bits of precision per sample in the file: 1 for a bilevel image, 2 or 4 for low-depth grey,
    /// 8, 12 for a 12-bit JPEG 2000 component, 16, or 16 / 32 for half / single floats. Decoded
    /// samples are scaled to fill <see cref="SampleFormat"/> (a 12-bit 4095 decodes as 65535), so
    /// this says how many of their bits are real. A palette image reports its palette entries' depth.
    /// Defaults to the full width of <see cref="SampleFormat"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set outside 1..32.</exception>
    public int BitDepth
    {
        get => _bitDepth != 0 ? _bitDepth : FullDepth(SampleFormat);
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 32);
            _bitDepth = (byte)value;
        }
    }

    /// <summary>
    /// How the stored pixels are to be turned for display (EXIF / TIFF Orientation). Decoders never
    /// apply it; <see cref="Width"/> and <see cref="Height"/> are the stored dimensions. A file
    /// without the tag, or with a value outside 1..8, reads as <see cref="ImageOrientation.Normal"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a value that is not a defined member.</exception>
    public ImageOrientation Orientation
    {
        get => (ImageOrientation)(_orientation + 1);
        init
        {
            if (value is < ImageOrientation.Normal or > ImageOrientation.Rotate270)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Orientation must be 1..8.");
            _orientation = (byte)(value - 1);
        }
    }

    /// <summary>
    /// Images the file holds: an animation's frames (APNG, animated WebP), a multi-page document's
    /// pages (TIFF, JBIG2), otherwise 1. <see cref="IImageDecoder.TryDecode"/> returns one of them —
    /// the first page, or the still image an animated format shows where animation is not supported
    /// (which for an APNG may be an image outside the animation).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set below 1.</exception>
    public int FrameCount
    {
        get => _extraFrames + 1;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _extraFrames = value - 1;
        }
    }

    /// <summary>True when the displayed image is the stored one turned a quarter: <see cref="Orientation"/> 5..8,
    /// where the displayed width is <see cref="Height"/>.</summary>
    public bool SwapsDimensions => Orientation >= ImageOrientation.Transpose;

    private static int FullDepth(SampleFormat format) => format switch
    {
        SampleFormat.UInt8 => 8,
        SampleFormat.UInt16 => 16,
        _ => 32,
    };

    /// <summary>Compares every property, so an explicit default (<c>BitDepth = 8</c> on
    /// <see cref="SampleFormat.UInt8"/>) equals an unset one.</summary>
    public bool Equals(ImageInfo other) =>
        Width == other.Width && Height == other.Height && Channels == other.Channels
        && SampleFormat == other.SampleFormat && BitDepth == other.BitDepth
        && Orientation == other.Orientation && FrameCount == other.FrameCount;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(Width, Height, Channels, SampleFormat, BitDepth, Orientation, FrameCount);
}
