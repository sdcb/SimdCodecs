namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>Chroma subsampling for the encoder.</summary>
public enum JpegSubsampling : byte
{
    /// <summary>Pick from quality the way stb_image_write does: 4:2:0 when
    /// <see cref="JpegEncodeOptions.Quality"/> ≤ 90, else 4:4:4. This is the only
    /// mode whose bytes are validated against the reference encoder.</summary>
    Auto = 0,

    /// <summary>Full chroma resolution (no subsampling), one 8×8 block per component per MCU.</summary>
    Chroma444,

    /// <summary>2×2 chroma subsampling (4:2:0), four Y blocks + one each of Cb/Cr per 16×16 MCU.</summary>
    Chroma420,
}

/// <summary>
/// Options for <see cref="JpegEncoder.Encode"/>. Baseline sequential JPEG with
/// fixed Annex K Huffman tables (matching the stb_image_write reference the
/// encoder is ported from); progressive, restart intervals, and optimized
/// Huffman tables are deliberately out of scope for this baseline.
/// </summary>
public sealed record JpegEncodeOptions
{
    /// <summary>Quality 1..100 (libjpeg-style). Higher is larger and closer to the
    /// source. The default, 90, is the reference encoder's default and the highest
    /// quality that still uses 4:2:0 under <see cref="JpegSubsampling.Auto"/>.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>Chroma subsampling. <see cref="JpegSubsampling.Auto"/> derives it
    /// from <see cref="Quality"/> exactly as the reference encoder does.</summary>
    public JpegSubsampling Subsampling { get; init; } = JpegSubsampling.Auto;

    /// <summary>
    /// When set, written as an APP1 <c>Exif\0\0</c> segment right after the APP0 JFIF header. The bytes are the
    /// TIFF-structured EXIF blob itself (<c>II*\0</c> or <c>MM\0*</c> onwards, <b>without</b> the <c>Exif\0\0</c>
    /// signature, which the encoder adds); <c>Sdcb.SimdCodecs.Exif</c>'s <c>ExifWriter.Build</c> makes one. Taking bytes
    /// keeps this package free of a dependency on the Exif package. <c>null</c> (the default) writes no segment and
    /// leaves the output byte-identical to an encode without the option.
    /// <para>
    /// The segment length field is 16 bits, so the blob is at most <see cref="MaxExifBytes"/> bytes (65527: 65535
    /// less the length field and the six signature bytes). A blob that is longer, empty, or does not start with a TIFF
    /// header is an <see cref="System.ArgumentException"/> from <see cref="JpegEncoder.Encode"/>, before any output.
    /// </para>
    /// </summary>
    public ReadOnlyMemory<byte>? Exif { get; init; }

    /// <summary>Largest <see cref="Exif"/> blob that fits one APP1 segment: 65535 - 2 (length field) - 6 (<c>Exif\0\0</c>).</summary>
    public const int MaxExifBytes = 65535 - 2 - 6;

    /// <summary>
    /// When set, written as an APP1 <c>http://ns.adobe.com/xap/1.0/\0</c> XMP segment after the EXIF APP1 (if
    /// <see cref="Exif"/> is set) and before the quantisation tables. <see cref="JpegXmp.BuildPacket"/> builds the
    /// packet; one that is invalid or over <see cref="MaxXmpPacketBytes"/> is an <see cref="System.ArgumentException"/>
    /// from <see cref="JpegEncoder.Encode"/>, before any output. <c>null</c> (the default) writes no segment and leaves
    /// the output byte-identical to an encode without the option.
    /// </summary>
    public JpegXmp? Xmp { get; init; }

    /// <summary>Largest XMP packet that fits one APP1 segment: 65535 - 2 (length field) - 29 (<c>http://ns.adobe.com/xap/1.0/\0</c>).</summary>
    public const int MaxXmpPacketBytes = 65535 - 2 - 29;
}
