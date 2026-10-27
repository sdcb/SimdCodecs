namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// Per-page options for <see cref="TiffWriter.AddPageAsync"/>.
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

    public static TiffPageOptions Default { get; } = new();
}
