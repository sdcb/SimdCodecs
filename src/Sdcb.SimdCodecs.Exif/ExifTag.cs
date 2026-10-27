namespace Sdcb.SimdCodecs.Exif;

/// <summary>
/// EXIF 2.31 tag numbers we map into <see cref="ExifMetadata"/>'s
/// strongly-typed properties. Internal — callers reach unknown tags via
/// <c>ExifMetadata.RawTags</c> instead of hardcoding numbers in their own
/// code. (We keep the constants here so the reader doesn't sprinkle magic
/// numbers through the switch.)
/// </summary>
internal static class ExifTag
{
    // TIFF/EXIF IFD0 + ExifIFD scalars
    public const ushort ImageDescription   = 0x010E;
    public const ushort Orientation        = 0x0112;
    public const ushort Software           = 0x0131;
    public const ushort DateTime           = 0x0132; // file modification time
    public const ushort Artist             = 0x013B;
    public const ushort Copyright          = 0x8298;
    public const ushort Make               = 0x010F;
    public const ushort Model              = 0x0110;

    public const ushort ExposureTime       = 0x829A;
    public const ushort FNumber            = 0x829D;
    public const ushort IsoSpeedRatings    = 0x8827;
    public const ushort DateTimeOriginal   = 0x9003; // capture time
    public const ushort ExifIfdPointer     = 0x8769; // IFD0 LONG: offset of the Exif sub-IFD
    public const ushort UserComment        = 0x9286; // UNDEFINED: first 8 bytes name the character code
    public const ushort FocalLength        = 0x920A;
    public const ushort FocalLengthIn35mm  = 0xA405;
    public const ushort PixelXDimension    = 0xA002;
    public const ushort PixelYDimension    = 0xA003;

    // GPS sub-IFD (tags re-use small numbers because they're in their own IFD)
    public const ushort GpsLatitudeRef     = 0x0001; // ASCII "N" or "S"
    public const ushort GpsLatitude        = 0x0002; // RATIONAL × 3 = D/M/S
    public const ushort GpsLongitudeRef    = 0x0003; // ASCII "E" or "W"
    public const ushort GpsLongitude       = 0x0004; // RATIONAL × 3
    public const ushort GpsAltitudeRef     = 0x0005; // BYTE: 0 = above, 1 = below
    public const ushort GpsAltitude        = 0x0006; // RATIONAL
    public const ushort GpsTimeStamp       = 0x0007; // RATIONAL × 3 = h/m/s UTC
    public const ushort GpsDateStamp       = 0x001D; // ASCII "YYYY:MM:DD"
}
