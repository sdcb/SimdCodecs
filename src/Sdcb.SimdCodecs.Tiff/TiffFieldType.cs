namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// TIFF 6.0 field type codes (used in IFD entries to describe the
/// payload type). Public because EXIF reuses the exact same taxonomy
/// — EXIF metadata is just a TIFF IFD chain accessed through an
/// alternate root.
/// </summary>
public enum TiffFieldType : ushort
{
    Byte      = 1,
    Ascii     = 2,
    Short     = 3,
    Long      = 4,
    Rational  = 5,
    SByte     = 6,
    Undefined = 7,
    SShort    = 8,
    SLong     = 9,
    SRational = 10,
    Float     = 11,
    Double    = 12,

    /// <summary>A 32-bit offset to a sub-IFD (TIFF Technical Note 1).</summary>
    Ifd       = 13,

    /// <summary>BigTIFF: unsigned 64-bit integer.</summary>
    Long8     = 16,

    /// <summary>BigTIFF: signed 64-bit integer.</summary>
    SLong8    = 17,

    /// <summary>BigTIFF: a 64-bit offset to a sub-IFD.</summary>
    Ifd8      = 18,
}
