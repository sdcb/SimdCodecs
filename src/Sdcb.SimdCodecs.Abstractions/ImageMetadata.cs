using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The metadata blocks a file carries besides its pixels, as raw bytes: what a transcoder copies across so that
/// capture time, GPS position and colour profile survive. The interface layer only extracts and passes these
/// blocks on; parsing Exif is the <c>Sdcb.SimdCodecs.Exif</c> package's job.
/// </summary>
public sealed class ImageMetadata
{
    /// <summary>Wraps raw metadata blocks; an empty one means the file has none.</summary>
    /// <param name="exif">The Exif block, starting at its TIFF header (<c>II*\0</c> or <c>MM\0*</c>) — without the
    /// JPEG <c>Exif\0\0</c> prefix.</param>
    /// <param name="xmp">The XMP packet, UTF-8.</param>
    /// <param name="icc">The ICC profile.</param>
    public ImageMetadata(ReadOnlyMemory<byte> exif = default, ReadOnlyMemory<byte> xmp = default, ReadOnlyMemory<byte> icc = default)
    {
        Exif = exif;
        Xmp = xmp;
        Icc = icc;
    }

    /// <summary>No metadata.</summary>
    public static ImageMetadata Empty { get; } = new();

    /// <summary>The Exif block from its TIFF header on; empty when the file has none.</summary>
    public ReadOnlyMemory<byte> Exif { get; }

    /// <summary>The UTF-8 XMP packet; empty when the file has none.</summary>
    public ReadOnlyMemory<byte> Xmp { get; }

    /// <summary>The ICC profile; empty when the file has none.</summary>
    public ReadOnlyMemory<byte> Icc { get; }

    /// <summary>True when every block is empty.</summary>
    public bool IsEmpty => Exif.IsEmpty && Xmp.IsEmpty && Icc.IsEmpty;

    /// <summary>The same blocks with <see cref="Exif"/> replaced.</summary>
    public ImageMetadata WithExif(ReadOnlyMemory<byte> exif) => new(exif, Xmp, Icc);

    /// <summary>The same blocks with <see cref="Xmp"/> replaced.</summary>
    public ImageMetadata WithXmp(ReadOnlyMemory<byte> xmp) => new(Exif, xmp, Icc);

    /// <summary>The same blocks with <see cref="Icc"/> replaced.</summary>
    public ImageMetadata WithIcc(ReadOnlyMemory<byte> icc) => new(Exif, Xmp, icc);
}
