using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The size of a whole-image buffer, computed in 64 bits: one that does not fit a single .NET array
/// (<see cref="Array.MaxLength"/> elements) is refused with a message saying what to do instead, never an
/// <see cref="OverflowException"/>.
/// </summary>
internal static class RasterSize
{
    /// <summary>
    /// <paramref name="width"/> × <paramref name="height"/> × <paramref name="elementsPerPixel"/>, the element count of
    /// one array holding the image.
    /// </summary>
    /// <param name="width">Pixels per row (≥ 0).</param>
    /// <param name="height">Rows (≥ 0).</param>
    /// <param name="elementsPerPixel">Array elements per pixel.</param>
    /// <param name="bytesPerElement">Bytes per array element, for the message.</param>
    /// <exception cref="ArgumentOutOfRangeException">The count exceeds <see cref="Array.MaxLength"/>.</exception>
    public static int Elements(int width, int height, int elementsPerPixel, int bytesPerElement = 1)
    {
        // width × height < 2^62; dividing first keeps the comparison from wrapping past long.MaxValue.
        var pixels = (long)width * height;
        if (elementsPerPixel > 0 && pixels > Array.MaxLength / elementsPerPixel)
        {
            var elements = (Int128)pixels * elementsPerPixel;
            throw new ArgumentOutOfRangeException(nameof(width),
                $"The requested image raster ({width}x{height}, {(long)elementsPerPixel * bytesPerElement} bytes per pixel, " +
                $"{elements * bytesPerElement} bytes) exceeds the maximum single-buffer size of {Array.MaxLength} elements (2 GB). " +
                "Decode at a reduced scale (DecodeOptions.ScaleDenominator, a power of two; one the codec scales to natively, " +
                "such as JPEG up to 8 in the DCT domain, never holds the full size) or decode a region of the image (DecodeOptions.Region).");
        }

        return (int)(pixels * elementsPerPixel);
    }
}
