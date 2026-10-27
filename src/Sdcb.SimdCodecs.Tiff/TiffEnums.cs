namespace Sdcb.SimdCodecs.Tiff;

public enum TiffCompression : ushort
{
    Uncompressed = 1,
    Lzw          = 5,
    Jpeg         = 7,    // New-style JPEG (TIFF Technical Note #2)
    Deflate      = 8,    // Adobe Deflate
    ZlibPkzip    = 32946, // PKZIP / zlib (identical bytes to Deflate=8)
    PackBits     = 32773, // Macintosh RLE (TIFF 6.0 section 9)
}

public enum TiffPhotometric : ushort
{
    MinIsWhite       = 0,
    MinIsBlack       = 1,
    Rgb              = 2,
    Palette          = 3,
    TransparencyMask = 4,
    Cmyk             = 5,
    YCbCr            = 6,
    CieLab           = 8,
}

public enum TiffExtraSamples : ushort
{
    Unspecified      = 0,
    AssociatedAlpha  = 1,  // pre-multiplied
    UnassociatedAlpha = 2, // straight alpha
}

/// <summary>
/// TIFF SampleFormat tag (339) values per TIFF 6.0 + TIFF Technical Note #3.
/// Tells readers how to interpret the raw sample bits — without this tag, the
/// spec default is <see cref="Uint"/> (1), so 32-bit IEEE float pixels written
/// without an explicit SampleFormat will be silently misread as unsigned ints.
/// </summary>
public enum TiffSampleFormat : ushort
{
    Uint      = 1, // unsigned integer (spec default)
    Int       = 2, // two's-complement signed integer
    IeeeFloat = 3, // IEEE 754 floating point
    Undefined = 4, // void / opaque data
}

/// <summary>
/// TIFF Predictor tag (317) values per TIFF 6.0 section 14. A predictor is a reversible
/// transform applied BEFORE compression so the compressor sees smaller, more repetitive
/// numbers. A reader that inflates the bytes but never inverts the predictor gets the
/// horizontal DERIVATIVE of the image rather than the image, which decodes without error and
/// looks like an embossed relief map -- or, on high-frequency content, like pure noise.
/// The spec default when the tag is absent is <see cref="None"/>.
/// </summary>
public enum TiffPredictor : ushort
{
    /// <summary>No transform; samples are stored as-is.</summary>
    None = 1,

    /// <summary>
    /// Each sample is stored as the difference from the sample one pixel to its left in the
    /// same row and the same channel. This is what essentially every writer that emits ZIP
    /// compression turns on by default: Photoshop, PixInsight, GraXpert, libtiff -c zip.
    /// </summary>
    HorizontalDifferencing = 2,

    /// <summary>
    /// Floating-point predictor (TIFF Technical Note #3): bytes are split into byte planes per
    /// row and then differenced byte-wise. A different transform from
    /// <see cref="HorizontalDifferencing"/>, and not interchangeable with it.
    /// </summary>
    FloatingPoint = 3,
}

public enum TiffLayout
{
    Strip,
    Tiled,
}

internal enum TiffPlanarConfig : ushort
{
    Contig   = 1,
    Separate = 2,
}

internal enum TiffResolutionUnit : ushort
{
    None       = 1,
    Inch       = 2,
    Centimeter = 3,
}
