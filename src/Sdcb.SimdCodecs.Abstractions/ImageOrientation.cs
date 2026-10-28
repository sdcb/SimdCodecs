namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// How an image's stored rows and columns map onto the picture as it should be shown: the EXIF / TIFF
/// Orientation tag (0x0112, TIFF 6.0 p.36), whose values these are. A camera writes it instead of
/// rotating the pixels. Decoders report it (<see cref="ImageInfo.Orientation"/>) and never apply it —
/// decoded pixels are always in stored order — so a viewer applies the transform each member names.
/// </summary>
public enum ImageOrientation : byte
{
    /// <summary>1: row 0 is the top, column 0 the left. Shown as stored.</summary>
    Normal = 1,

    /// <summary>2: row 0 is the top, column 0 the right. Mirror left–right to display.</summary>
    FlipHorizontal = 2,

    /// <summary>3: row 0 is the bottom, column 0 the right. Rotate 180° to display.</summary>
    Rotate180 = 3,

    /// <summary>4: row 0 is the bottom, column 0 the left. Mirror top–bottom to display.</summary>
    FlipVertical = 4,

    /// <summary>5: row 0 is the left, column 0 the top. Mirror across the main diagonal to display;
    /// the displayed width is the stored height.</summary>
    Transpose = 5,

    /// <summary>6: row 0 is the right, column 0 the top. Rotate 90° clockwise to display; the
    /// displayed width is the stored height. The usual value for a phone held upright.</summary>
    Rotate90 = 6,

    /// <summary>7: row 0 is the right, column 0 the bottom. Mirror across the anti-diagonal to
    /// display; the displayed width is the stored height.</summary>
    Transverse = 7,

    /// <summary>8: row 0 is the left, column 0 the bottom. Rotate 90° counter-clockwise to display;
    /// the displayed width is the stored height.</summary>
    Rotate270 = 8,
}
