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
/// </summary>
public sealed record ExifMetadata(
    string? Make,
    string? Model,
    /// <summary>0x9003 DateTimeOriginal preferred; falls back to 0x0132 DateTime.</summary>
    DateTime? CaptureTime,
    /// <summary>0x0132 DateTime (image-file modification timestamp).</summary>
    DateTime? FileTime,
    /// <summary>0x829A ExposureTime in seconds.</summary>
    Rational? ExposureTime,
    /// <summary>0x829D FNumber (e.g. 28/10 = f/2.8).</summary>
    Rational? FNumber,
    /// <summary>0x8827 ISOSpeedRatings (first value if multi-valued).</summary>
    int? Iso,
    /// <summary>0x920A FocalLength in mm.</summary>
    Rational? FocalLength,
    /// <summary>0xA405 FocalLengthIn35mmFilm in mm.</summary>
    int? FocalLengthIn35mm,
    /// <summary>0x0131 Software string.</summary>
    string? Software,
    /// <summary>0x013B Artist string.</summary>
    string? Artist,
    /// <summary>0x8298 Copyright string.</summary>
    string? Copyright,
    /// <summary>0x0112 Orientation (1..8 per EXIF 2.31 / TIFF 6.0).</summary>
    ushort? Orientation,
    /// <summary>0xA002 PixelXDimension (full image width).</summary>
    int? PixelWidth,
    /// <summary>0xA003 PixelYDimension (full image height).</summary>
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
