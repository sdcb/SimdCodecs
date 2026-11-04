namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Per-page options for <c>TiffWriter.AddPageAsync</c>.
/// </summary>
public sealed record TiffPageOptions
{
    public TiffLayout Layout { get; init; } = TiffLayout.Strip;
    public TiffCompression Compression { get; init; } = TiffCompression.Deflate;
    public int SamplesPerPixel { get; init; } = 3;
    public int BitsPerSample { get; init; } = 8;
    public TiffPhotometric Photometric { get; init; } = TiffPhotometric.Rgb;
    public TiffExtraSamples? ExtraSamples { get; init; }

    /// <summary>
    /// SampleFormat (tag 339) — how readers should interpret the sample bits.
    /// Defaults to <see cref="TiffSampleFormat.Uint"/> to match the TIFF spec default.
    /// Set to <see cref="TiffSampleFormat.IeeeFloat"/> when <see cref="BitsPerSample"/>
    /// is 32 and the pixel bytes hold IEEE 754 floats — without this, downstream readers
    /// (Magick.NET, libtiff, Photoshop, PIL) will silently misinterpret the bits as
    /// unsigned integers.
    /// </summary>
    public TiffSampleFormat SampleFormat { get; init; } = TiffSampleFormat.Uint;

    /// <summary>
    /// SMinSampleValue (tag 340) and <see cref="SMaxSampleValue"/> (tag 341) — the actual
    /// value range stored in the pixel bytes. Mostly relevant for
    /// <see cref="TiffSampleFormat.IeeeFloat"/> output: without these tags, Magick.NET and
    /// libtiff-derived readers assume float TIFFs are in <c>[0, 1]</c> SMPTE/scene-linear
    /// range and rescale on read. Set both to your actual min/max (e.g. <c>0</c> and
    /// <c>65535</c>) when emitting floats in a non-unit range so readers don't multiply by
    /// their own quantum max. Emitted as Float (type 11), one value per sample. Null = omit.
    /// </summary>
    public float? SMinSampleValue { get; init; }

    /// <summary>See <see cref="SMinSampleValue"/>. Null = omit.</summary>
    public float? SMaxSampleValue { get; init; }
    public double DpiX { get; init; } = 96.0;
    public double DpiY { get; init; } = 96.0;
    public string? Artist { get; init; }
    public string? Software { get; init; }

    /// <summary>Tile dimensions (must be multiples of 16 per TIFF spec). Only used when Layout=Tiled.</summary>
    public int TileWidth { get; init; } = 256;
    /// <summary>Tile dimensions (must be multiples of 16 per TIFF spec). Only used when Layout=Tiled.</summary>
    public int TileHeight { get; init; } = 256;

    /// <summary>Rows per strip. 0 = entire image as one strip. Only used when Layout=Strip.</summary>
    public int RowsPerStrip { get; init; } = 0;

    /// <summary>
    /// Raw ICC profile bytes (tag 34675). Empty (the default) = no profile embedded.
    /// Accepts <see cref="ReadOnlyMemory{T}"/> so callers can pass bundled blobs like
    /// <c>Sdcb.SimdCodecs.Color.Icc.IccProfiles.SRgbV4</c> without an explicit copy at the
    /// call site — the writer internalises the bytes once at the API boundary.
    /// </summary>
    public ReadOnlyMemory<byte> IccProfile { get; init; }

    /// <summary>
    /// An Exif block: the TIFF-structured bytes themselves (<c>II*\0</c> or <c>MM\0*</c> onwards; a leading
    /// <c>Exif\0\0</c> is skipped), as <c>Sdcb.SimdCodecs.Exif</c>'s <c>ExifWriter.Build</c> makes and
    /// <see cref="Abstractions.ImageMetadata.Exif"/> carries. Empty (the default) = none.
    /// <para>
    /// A TIFF page's own IFD is the Exif IFD0, so the block is merged into the page rather than embedded whole: IFD0's
    /// descriptive tags (Make, Model, Orientation, DateTime, Copyright, ...) join the page's IFD, and the Exif IFD
    /// (tag 34665), its Interoperability IFD (tag 40965) and the GPS IFD (tag 34853) are written as sub-IFDs with their
    /// offsets relocated. Not carried: IFD1 (the thumbnail), tags that describe image data (the page sets its own),
    /// pointers to any other IFD, entries of unknown type and entries whose value lies outside the block. Values are
    /// copied as they are, so a MakerNote whose own offsets count from the block's header points elsewhere afterwards.
    /// Resolution, and <see cref="Artist"/> and <see cref="Software"/> when set, replace the block's.
    /// </para>
    /// A block without a TIFF header is an <see cref="InvalidDataException"/>, thrown before the page is written.
    /// </summary>
    public ReadOnlyMemory<byte> Exif { get; init; }

    /// <summary>The raw XMP packet (UTF-8), written as tag 700. Empty (the default) = none.</summary>
    public ReadOnlyMemory<byte> Xmp { get; init; }

    public static TiffPageOptions Default { get; } = new();
}
