using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The metadata blocks a file carries besides its pixels, as raw bytes: what a transcoder copies across so that
/// capture time, GPS position and colour profile survive. The interface layer only extracts and passes these
/// blocks on; parsing Exif is the <c>Sdcb.SimdCodecs.Exif</c> package's job.
/// <para>
/// Build one with an object initializer (<c>new ImageMetadata { Exif = exif, Icc = icc }</c>) and change one with
/// <c>with</c> (<c>metadata with { Icc = profile }</c>); a block left unset is empty. Two instances are equal when
/// they refer to the same memory for every block, not when the bytes match.
/// </para>
/// </summary>
public sealed record ImageMetadata
{
    /// <summary>No metadata.</summary>
    public static ImageMetadata Empty { get; } = new();

    /// <summary>The Exif block, starting at its TIFF header (<c>II*\0</c> or <c>MM\0*</c>), without the JPEG
    /// <c>Exif\0\0</c> prefix; empty when the file has none.</summary>
    public ReadOnlyMemory<byte> Exif { get; init; }

    /// <summary>The UTF-8 XMP packet; empty when the file has none.</summary>
    public ReadOnlyMemory<byte> Xmp { get; init; }

    /// <summary>The ICC profile; empty when the file has none.</summary>
    public ReadOnlyMemory<byte> Icc { get; init; }

    /// <summary>True when every block is empty.</summary>
    public bool IsEmpty => Exif.IsEmpty && Xmp.IsEmpty && Icc.IsEmpty;
}
