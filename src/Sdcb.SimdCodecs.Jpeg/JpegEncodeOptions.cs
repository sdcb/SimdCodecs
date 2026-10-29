namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>Chroma subsampling for the encoder.</summary>
public enum JpegSubsampling : byte
{
    /// <summary>Pick from quality: 4:2:0 when <see cref="JpegEncodeOptions.Quality"/> ≤ 90,
    /// else 4:4:4.</summary>
    Auto = 0,

    /// <summary>Full chroma resolution (no subsampling), one 8×8 block per component per MCU.</summary>
    Chroma444,

    /// <summary>2×2 chroma subsampling (4:2:0), four Y blocks + one each of Cb/Cr per 16×16 MCU.</summary>
    Chroma420,
}

/// <summary>
/// Options for <see cref="JpegEncoder.Encode"/>. The default is baseline sequential JPEG with
/// the Annex K Huffman tables, the fastest mode; <see cref="OptimizeHuffman"/> and
/// <see cref="Progressive"/> trade encode time for smaller files with identical pixels.
/// Restart intervals are not written.
/// </summary>
public sealed record JpegEncodeOptions
{
    /// <summary>Quality 1..100 (libjpeg-style scaling of the Annex K quantisation tables).
    /// Higher is larger and closer to the source. The default, 90, is the highest quality
    /// that still uses 4:2:0 under <see cref="JpegSubsampling.Auto"/>.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>Chroma subsampling. <see cref="JpegSubsampling.Auto"/> derives it from
    /// <see cref="Quality"/>. Ignored for grey input, which is written as one component.</summary>
    public JpegSubsampling Subsampling { get; init; } = JpegSubsampling.Auto;

    /// <summary>
    /// Builds Huffman tables optimised for this image (T.81 Annex K.2) instead of the Annex K
    /// example tables: typically 2–8% smaller at the same quality, the decoded pixels are
    /// unchanged, and encoding takes a second entropy pass over stored coefficients.
    /// </summary>
    public bool OptimizeHuffman { get; init; }

    /// <summary>
    /// Writes a progressive JPEG (T.81 Annex G: spectral selection and successive
    /// approximation), which renders coarse-to-fine while loading and is typically 5–10% smaller
    /// than baseline. Its tables are always optimised (this implies <see cref="OptimizeHuffman"/>);
    /// the decoded pixels are identical to a baseline encode with the same settings.
    /// </summary>
    public bool Progressive { get; init; }

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

    /// <summary>
    /// When set, the ICC profile to embed, written as APP2 <c>ICC_PROFILE</c> segments (ICC.1 Annex B.4) after
    /// the EXIF and XMP segments: one per <see cref="MaxIccChunkBytes"/> bytes, each numbered, so a profile of
    /// any size up to <see cref="MaxIccProfileBytes"/> fits. Pass the decoded image's
    /// <see cref="Abstractions.IDecodedImage.IccProfile"/> to keep a Display P3 or Adobe RGB photo's colour
    /// space through a re-encode. The bytes are written as given, not validated as a profile; an empty or
    /// oversized one is an <see cref="System.ArgumentException"/> from <see cref="JpegEncoder.Encode"/>.
    /// <c>null</c> (the default) writes no segment and leaves the output byte-identical to an encode
    /// without the option.
    /// </summary>
    public ReadOnlyMemory<byte>? IccProfile { get; init; }

    /// <summary>Profile bytes one APP2 segment carries: 65535 - 2 (length field) - 12 (<c>ICC_PROFILE\0</c>) - 2
    /// (sequence number and count).</summary>
    public const int MaxIccChunkBytes = 65535 - 2 - 12 - 2;

    /// <summary>Largest <see cref="IccProfile"/>: 255 segments, the most a one-byte count can number.</summary>
    public const int MaxIccProfileBytes = 255 * MaxIccChunkBytes;
}
