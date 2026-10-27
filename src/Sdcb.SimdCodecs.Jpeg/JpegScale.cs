namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Decode scale denominator. At <see cref="Half"/> and below the decoder runs a
/// reduced inverse DCT (4×4 / 2×2 / 1×1 instead of 8×8), so the full-resolution
/// raster never materialises — component planes and the output buffer shrink by
/// the square of the factor. Output dimensions are <c>ceil(dim / factor)</c>,
/// matching the libjpeg scaled-decode convention.
/// </summary>
public enum JpegScale
{
    /// <summary>Full resolution (8×8 IDCT; see the decoder's accuracy notes).</summary>
    Full = 1,

    /// <summary>1/2 linear resolution (1/4 the pixels) via 4×4 reduced IDCT.</summary>
    Half = 2,

    /// <summary>1/4 linear resolution (1/16 the pixels) via 2×2 reduced IDCT.</summary>
    Quarter = 4,

    /// <summary>1/8 linear resolution (1/64 the pixels) — each block collapses to its DC term.</summary>
    Eighth = 8,
}
