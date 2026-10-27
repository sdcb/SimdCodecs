using System;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// Pattern dictionaries and halftone regions — ITU-T T.88 §6.6 and §6.7.
/// <para>
/// A halftone region is how JBIG2 stores a <em>photograph</em> in a bilevel
/// format. The page is treated as a coarse grid of cells; each cell holds a small
/// integer, its grey level; and each grey level has a fixed dither pattern in a
/// pattern dictionary. Decoding is therefore two unrelated steps — recover the
/// grid of numbers, then stamp the matching pattern for each — and neither looks
/// anything like the text path.
/// </para>
/// <para>
/// The grid of numbers is itself coded as bilevel images: one generic region per
/// bit of the grey value, most significant first, Gray-coded so that adjacent
/// levels differ in one plane. That is the trick that makes the planes
/// compressible at all — a smooth gradient would otherwise flip every bit of the
/// value at each step.
/// </para>
/// </summary>
internal static class HalftoneDecoder
{
    /// <summary>
    /// AT pixels for a pattern dictionary's collective bitmap (T.88 §6.7.5). A1
    /// reaches back exactly one pattern width, so each pattern is predicted from
    /// the one before it rather than from its own left edge.
    /// </summary>
    public static sbyte[] CollectiveAt(int patternWidth) =>
        [(sbyte)-patternWidth, 0, -3, -1, 2, -2, -2, -2];

    /// <summary>AT pixels for a grey-code bitplane (T.88 §C.5).</summary>
    public static sbyte[] GrayscaleAt(int template) =>
        [(sbyte)(template <= 1 ? 3 : 2), -1, -3, -1, 2, -2, -2, -2];

    /// <summary>
    /// Decodes a pattern dictionary (T.88 §6.7.5): a single wide generic region
    /// holding every pattern side by side, then sliced up.
    /// </summary>
    /// <param name="mq">The arithmetic decoder, positioned at the collective bitmap's data.</param>
    /// <param name="patternWidth">HDPW.</param>
    /// <param name="patternHeight">HDPH.</param>
    /// <param name="maxIndex">GRAYMAX — the dictionary holds one more pattern than this.</param>
    /// <param name="template">HDTEMPLATE.</param>
    /// <param name="budget">The decode's remaining pixel allowance.</param>
    public static Jbig2Bitmap[] DecodePatternDictionary(
        ref MqDecoder mq, int patternWidth, int patternHeight, int maxIndex, int template,
        Jbig2PixelBudget budget)
    {
        var count = maxIndex + 1;
        var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];

        var collective = GenericRegionDecoder.Decode(
            ref mq, contexts, count * patternWidth, patternHeight, template,
            typicalPrediction: false, CollectiveAt(patternWidth), budget);

        var patterns = new Jbig2Bitmap[count];
        for (var i = 0; i < count; i++)
            patterns[i] = collective.Crop(i * patternWidth, 0, patternWidth, patternHeight);

        return patterns;
    }

    /// <summary>Everything §6.6 needs beyond the coded data and the pattern dictionary.</summary>
    /// <param name="Width">HBW, the region width.</param>
    /// <param name="Height">HBH, the region height.</param>
    /// <param name="GridWidth">HGW, cells across.</param>
    /// <param name="GridHeight">HGH, cells down.</param>
    /// <param name="GridX">HGX, the grid origin in 1/256 pixel units.</param>
    /// <param name="GridY">HGY, likewise.</param>
    /// <param name="VectorX">HRX, the grid's step vector in 1/256 pixel units.</param>
    /// <param name="VectorY">HRY, likewise.</param>
    /// <param name="Template">HTEMPLATE, the generic template each bitplane uses.</param>
    /// <param name="DefaultPixel">HDEFPIXEL.</param>
    /// <param name="Combination">HCOMBOP.</param>
    internal readonly record struct HalftoneParameters(
        int Width,
        int Height,
        int GridWidth,
        int GridHeight,
        int GridX,
        int GridY,
        int VectorX,
        int VectorY,
        int Template,
        byte DefaultPixel,
        CombinationOperator Combination);

    /// <summary>Decodes a halftone region (T.88 §6.6.5).</summary>
    /// <param name="mq">The arithmetic decoder, positioned at the grey-scale bitplanes.</param>
    /// <param name="p">The region parameters, read from the segment header.</param>
    /// <param name="patterns">The dither patterns from the pattern dictionary this region names.</param>
    /// <param name="budget">The decode's remaining pixel allowance.</param>
    public static Jbig2Bitmap DecodeHalftoneRegion(
        ref MqDecoder mq, HalftoneParameters p, Jbig2Bitmap[] patterns, Jbig2PixelBudget budget)
    {
        if (patterns.Length == 0)
            throw new InvalidDataException("JBIG2: halftone region refers to an empty pattern dictionary.");

        budget.Charge(p.Width, p.Height);
        var region = new Jbig2Bitmap(p.Width, p.Height, p.DefaultPixel);
        var grey = DecodeGrayscale(ref mq, p.GridWidth, p.GridHeight, patterns.Length, p.Template, budget);

        for (var m = 0; m < p.GridHeight; m++)
        {
            for (var n = 0; n < p.GridWidth; n++)
            {
                // §6.6.5.1. The grid is a lattice, not a rectangle: (HRX, HRY) is
                // a vector, so a dither grid can be rotated or sheared relative to
                // the page. Coordinates are in 1/256 pixel units throughout, hence
                // the shift rather than a division.
                var x = p.GridX + m * p.VectorY + n * p.VectorX;
                var y = p.GridY + m * p.VectorX - n * p.VectorY;

                var level = grey[m * p.GridWidth + n];
                if ((uint)level >= (uint)patterns.Length)
                    throw new InvalidDataException(
                        $"JBIG2: halftone grey level {level} exceeds the {patterns.Length}-pattern dictionary.");

                region.Combine(patterns[level], x >> 8, y >> 8, p.Combination);
            }
        }

        return region;
    }

    /// <summary>
    /// The grey-scale image decoding procedure of T.88 Annex C.5: one generic
    /// region per bitplane, most significant first, each XOR-ed with the plane
    /// above it to undo the Gray coding.
    /// </summary>
    private static int[] DecodeGrayscale(
        ref MqDecoder mq, int width, int height, int levels, int template, Jbig2PixelBudget budget)
    {
        var planes = 0;
        while (1 << planes < levels) planes++;
        planes = Math.Max(planes, 1);

        var contexts = new byte[1 << GenericRegionDecoder.ContextBits(template)];
        var at = GrayscaleAt(template);

        // The caller has already bounded the grid by Jbig2Limits.MaxHalftoneGridCells,
        // which is what keeps this product inside int and these two arrays (five
        // bytes per cell between them) proportionate.
        var values = new int[checked(width * height)];
        var previous = new byte[width * height];

        for (var bit = planes - 1; bit >= 0; bit--)
        {
            var plane = GenericRegionDecoder.Decode(
                ref mq, contexts, width, height, template, typicalPrediction: false, at, budget);

            // C.5: every plane but the first is coded relative to the one above,
            // so the running XOR is what turns Gray code back into binary.
            if (bit < planes - 1)
                for (var i = 0; i < plane.Data.Length; i++)
                    plane.Data[i] ^= previous[i];

            for (var i = 0; i < values.Length; i++) values[i] |= plane.Data[i] << bit;

            previous = plane.Data;
        }

        return values;
    }
}
