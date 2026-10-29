namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// An image file format, as recognised by its leading signature (<c>SimdCodecs.DetectFormat</c>) and
/// reported by a decoder in <see cref="ImageInfo.Format"/>. Some members are formats the Sdcb.SimdCodecs
/// family decodes; the rest are recognised only, so a caller can say what a file is ("HEIF is not
/// supported") instead of "unknown". Ask <c>SimdCodecs.IsDecodable</c> which is which rather than
/// reading it from the member order: a format that gains a decoder keeps its place and value.
/// </summary>
public enum ImageFormat
{
    /// <summary>No known signature.</summary>
    Unknown = 0,

    /// <summary>PNG, including APNG.</summary>
    Png,

    /// <summary>JPEG (JFIF / EXIF), including gain-map ("Ultra HDR") JPEGs.</summary>
    Jpeg,

    /// <summary>TIFF.</summary>
    Tiff,

    /// <summary>JBIG2, as a standalone <c>.jb2</c> file.</summary>
    Jbig2,

    /// <summary>JPEG 2000: a JP2 file or a raw J2K codestream.</summary>
    Jpeg2000,

    /// <summary>WebP, lossy, lossless or animated.</summary>
    Webp,

    /// <summary>GIF.</summary>
    Gif,

    /// <summary>Windows bitmap (BMP). Recognised, not decoded.</summary>
    Bmp,

    /// <summary>Windows icon (ICO). Recognised, not decoded.</summary>
    Ico,

    /// <summary>HEIF (<c>.heic</c> / <c>.heif</c>), usually HEVC-coded. Recognised, not decoded.</summary>
    Heif,

    /// <summary>AVIF. Recognised, not decoded.</summary>
    Avif,

    /// <summary>JPEG XL, as a container or a bare codestream. Recognised, not decoded.</summary>
    JpegXl,

    /// <summary>JPEG XR (HD Photo). Recognised, not decoded.</summary>
    JpegXr,

    /// <summary>OpenEXR. Recognised, not decoded.</summary>
    OpenExr,

    /// <summary>Photoshop document (PSD / PSB). Recognised, not decoded.</summary>
    Psd,

    /// <summary>QOI ("Quite OK Image"). Recognised, not decoded.</summary>
    Qoi,
}
