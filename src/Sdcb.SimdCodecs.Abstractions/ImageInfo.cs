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
/// properties describe the file: <see cref="Format"/>, <see cref="BitDepth"/>, <see cref="Orientation"/>,
/// <see cref="FrameCount"/>, <see cref="ColorEncoding"/>, <see cref="Resolution"/> and <see cref="HasThumbnail"/>.
/// Left unset they read as the plain case (<see cref="ImageFormat.Unknown"/>, the format's full depth,
/// <see cref="ImageOrientation.Normal"/>, one frame, sRGB, no declared density, no thumbnail).
/// </para>
/// </summary>
/// <param name="Width">Pixel width, as stored (before <see cref="Orientation"/> is applied).</param>
/// <param name="Height">Pixel height, as stored.</param>
/// <param name="Channels">Samples per pixel of the decoded raster: 1 (gray), 2 (gray+alpha), 3 (RGB),
/// 4 (RGBA). A channel is alpha only when the file can carry one: a colour JPEG is 3.</param>
/// <param name="SampleFormat">The per-channel sample type the full decode will produce.</param>
public readonly record struct ImageInfo(int Width, int Height, int Channels, SampleFormat SampleFormat)
{
    // Each stored so that the zero of default(ImageInfo) or an unset property is the plain case.
    private readonly byte _bitDepth;     // 0: SampleFormat's full width
    private readonly byte _format;       // ImageFormat
    private readonly byte _orientation;  // ImageOrientation - 1
    private readonly int _extraFrames;   // FrameCount - 1
    private readonly ColorEncoding? _color; // null: ColorEncoding.AssumedSrgb
    private readonly Resolution? _resolution;
    private readonly bool _hasThumbnail;

    /// <summary>
    /// The file format, set by the decoder that read the header. Defaults to <see cref="ImageFormat.Unknown"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to a value that is not a defined member.</exception>
    public ImageFormat Format
    {
        get => (ImageFormat)_format;
        init
        {
            if (value is < ImageFormat.Unknown or > ImageFormat.Pfm)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined ImageFormat.");
            _format = (byte)value;
        }
    }

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

    /// <summary>
    /// How the decoded samples will encode colour, known before decoding: what
    /// <see cref="IDecodedImage.ColorEncoding"/> of the decoded image will report (PNG cICP / gAMA, HEIF nclx, the primaries of an ICC profile, …), so a
    /// caller can tell an HDR (PQ / HLG) file apart up front. Defaults to <see cref="ColorEncoding.AssumedSrgb"/>.
    /// </summary>
    public ColorEncoding ColorEncoding
    {
        get => _color ?? ColorEncoding.AssumedSrgb;
        init => _color = value;
    }

    /// <summary>The pixel density the file declares (PNG pHYs, JFIF / Exif resolution, TIFF XResolution, BMP
    /// pixels per metre); null when it declares none.</summary>
    public Resolution? Resolution
    {
        get => _resolution;
        init => _resolution = value;
    }

    /// <summary>True when the file embeds a reduced-size preview that <see cref="IImageDecoder.TryDecodeThumbnail"/>
    /// returns (an Exif IFD1 JPEG, a HEIF <c>thmb</c> item, a TIFF reduced-resolution subfile).</summary>
    public bool HasThumbnail
    {
        get => _hasThumbnail;
        init => _hasThumbnail = value;
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
        && Orientation == other.Orientation && FrameCount == other.FrameCount && Format == other.Format
        && ColorEncoding == other.ColorEncoding && Resolution == other.Resolution && HasThumbnail == other.HasThumbnail;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine(Width, Height, Channels, SampleFormat, BitDepth, Orientation, FrameCount, Format),
            ColorEncoding, Resolution, HasThumbnail);
}
