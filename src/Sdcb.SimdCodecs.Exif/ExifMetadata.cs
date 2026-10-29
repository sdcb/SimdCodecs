using System;
using System.Collections.Generic;
using Sdcb.SimdCodecs.Tiff;

namespace Sdcb.SimdCodecs.Exif;

/// <summary>
/// Strongly-typed projection of the well-known EXIF tags plus the raw
/// tag→value map for the long tail. Returned by <see cref="ExifReader"/>
/// regardless of which container (JPEG / TIFF / PNG) the EXIF blob came
/// from — the underlying format is the same TIFF-IFD structure in all
/// three cases.
///
/// All strongly-typed properties are nullable because:
/// <list type="bullet">
/// <item>The TIFF spec says readers must skip unknown tags — symmetrically,
///   absent tags here just mean the camera/encoder didn't write them.</item>
/// <item>Some tags only appear in certain image origins (e.g. GPS rarely
///   makes it onto astronomical TIFFs).</item>
/// </list>
/// Use <see cref="RawTags"/> for vendor-private tags that aren't in the
/// well-known set, or to read MakerNote (0x927C) as a bag of bytes.
/// <param name="Make">Camera make, when present.</param>
/// <param name="Model">Camera model, when present.</param>
/// <param name="CaptureTime">0x9003 DateTimeOriginal, falling back to 0x0132 DateTime.</param>
/// <param name="FileTime">0x0132 DateTime (image-file modification timestamp).</param>
/// <param name="ExposureTime">0x829A ExposureTime in seconds.</param>
/// <param name="FNumber">0x829D FNumber (for example, 28/10 = f/2.8).</param>
/// <param name="Iso">0x8827 ISOSpeedRatings (the first value if multi-valued).</param>
/// <param name="FocalLength">0x920A FocalLength in millimetres.</param>
/// <param name="FocalLengthIn35mm">0xA405 FocalLengthIn35mmFilm in millimetres.</param>
/// <param name="Software">0x0131 Software string.</param>
/// <param name="Artist">0x013B Artist string.</param>
/// <param name="Copyright">0x8298 Copyright string.</param>
/// <param name="Orientation">0x0112 Orientation (1..8 per EXIF 2.31 / TIFF 6.0).</param>
/// <param name="PixelWidth">0xA002 PixelXDimension (full image width).</param>
/// <param name="PixelHeight">0xA003 PixelYDimension (full image height).</param>
/// <param name="Gps">GPS coordinates reconstructed from the GPS IFD, when present.</param>
/// <param name="RawTags">The raw tag-to-value map, including vendor-private tags.</param>
/// </summary>
public sealed record ExifMetadata(
    string? Make,
    string? Model,
    DateTime? CaptureTime,
    DateTime? FileTime,
    Rational? ExposureTime,
    Rational? FNumber,
    int? Iso,
    Rational? FocalLength,
    int? FocalLengthIn35mm,
    string? Software,
    string? Artist,
    string? Copyright,
    ushort? Orientation,
    int? PixelWidth,
    int? PixelHeight,
    GpsCoordinates? Gps,
    IReadOnlyDictionary<ushort, ExifTagValue> RawTags);

/// <summary>
/// Signed lat/lon (decimal degrees) and optional altitude/timestamp,
/// reconstructed from the GPS-IFD tags 1..7 / 0x1D. Positive latitude is
/// north, positive longitude is east, positive altitude is above sea
/// level. <see cref="Timestamp"/> is the combination of GPSDateStamp
/// (UTC date) + GPSTimeStamp (UTC h/m/s rationals).
/// </summary>
public sealed record GpsCoordinates(
    double Latitude,
    double Longitude,
    double? Altitude,
    DateTime? Timestamp);

/// <summary>
/// Raw representation of a single IFD entry — preserved verbatim from the
/// file so callers can decode vendor / private tags themselves. The bytes
/// are in the file's byte order (read <see cref="ExifMetadata.RawTags"/>
/// in concert with knowledge of which container they came from).
/// </summary>
public sealed record ExifTagValue(ushort Tag, TiffFieldType Type, int Count, byte[] Bytes);
