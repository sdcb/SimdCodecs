using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// How a region bitmap is merged into the page it is placed on — the external
/// combination operator of T.88 §7.4.1.5 (also the page default combination
/// operator of §7.4.8.5).
/// </summary>
internal enum CombinationOperator
{
    /// <summary>Set a page pixel black if either it or the region pixel is black.</summary>
    Or = 0,

    /// <summary>Keep a page pixel black only where the region pixel is black too.</summary>
    And = 1,

    /// <summary>Black where exactly one of the two is black.</summary>
    Xor = 2,

    /// <summary>Black where the two agree.</summary>
    Xnor = 3,

    /// <summary>Overwrite the page pixels with the region's, ignoring what was there.</summary>
    Replace = 4,
}

/// <summary>
/// A JBIG2 bilevel bitmap, stored one byte per pixel holding 0 or 1 where
/// <b>1 means black</b> — T.88's own polarity, kept all the way to the public
/// API rather than silently inverted somewhere in the middle.
/// <para>
/// One byte per pixel rather than packed bits: every template pixel fetch in
/// <see cref="GenericRegionDecoder"/> is a random access into the neighbourhood
/// above and to the left of the current pixel, and unpacking a bit per fetch
/// costs more than the memory saved. The packed projection is produced once, at
/// the boundary, by <see cref="Jbig2Image"/>.
/// </para>
/// </summary>
internal sealed class Jbig2Bitmap
{
    /// <param name="width">Pixel width (&gt; 0).</param>
    /// <param name="height">Pixel height (&gt; 0).</param>
    /// <param name="fill">Initial value for every pixel: 0 (white) or 1 (black).</param>
    public Jbig2Bitmap(int width, int height, byte fill = 0)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Bitmap dimensions must be positive.");

        // The backstop for every sizing path at once. The segment layer bounds
        // region and page geometry before it gets here, but symbol dictionaries
        // do not go through region info at all — a symbol's width and height
        // accumulate from coded deltas (§6.5.8) with nothing but "> 0" on them, so
        // a one-symbol dictionary could otherwise name a 46000 x 46000 glyph.
        // Malformed data rather than a bad argument: the numbers came off the wire.
        if ((long)width * height > Jbig2Limits.MaxBitmapPixels)
            throw new InvalidDataException(
                $"JBIG2: a {width}x{height} bitmap is {(long)width * height:N0} pixels, past the " +
                $"{Jbig2Limits.MaxBitmapPixels:N0} this decoder will allocate for one bitmap.");

        Width = width;
        Height = height;
        Data = new byte[checked(width * height)];
        if (fill != 0) Data.AsSpan().Fill(1);
    }

    /// <summary>Pixel width.</summary>
    public int Width { get; }

    /// <summary>Pixel height.</summary>
    public int Height { get; }

    /// <summary>Row-major pixels, one byte each, 0 (white) or 1 (black).</summary>
    public byte[] Data { get; }

    /// <summary>
    /// A 1x1 white placeholder. Symbol dictionaries size their ID alphabet by the
    /// symbol count they declare, so slots past the ones decoded so far need
    /// something addressable — naming one is a malformed stream, and this makes
    /// that a clean rejection rather than a null dereference.
    /// </summary>
    public static Jbig2Bitmap Empty { get; } = new(1, 1);

    /// <summary>
    /// Reads a pixel, returning 0 for any coordinate outside the bitmap. T.88
    /// §6.2.5.2 requires exactly this: template pixels that fall off the top or
    /// the sides of the region are treated as white, which is what makes the
    /// first two rows and the left edge decodable at all.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Get(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? Data[y * Width + x] : 0;

    /// <summary>
    /// Copies out a rectangle as a new bitmap, reading anything outside this one
    /// as white. Used where a decoder needs a snapshot of the page it is about to
    /// write over — a refinement region reads its reference while producing its
    /// output, so the two cannot share storage.
    /// </summary>
    public Jbig2Bitmap Crop(int x0, int y0, int width, int height)
    {
        var crop = new Jbig2Bitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                crop.Data[y * width + x] = (byte)Get(x0 + x, y0 + y);

        return crop;
    }

    /// <summary>
    /// Merges <paramref name="source"/> into this bitmap with its top-left corner
    /// at (<paramref name="x0"/>, <paramref name="y0"/>), clipping anything that
    /// falls outside. A region whose placement runs past the page edge is
    /// clipped rather than rejected — PDF images routinely declare a page larger
    /// or smaller than the region the encoder emitted.
    /// </summary>
    public void Combine(Jbig2Bitmap source, int x0, int y0, CombinationOperator op)
    {
        for (var sy = 0; sy < source.Height; sy++)
        {
            var dy = y0 + sy;
            if ((uint)dy >= (uint)Height) continue;

            var srcRow = sy * source.Width;
            var dstRow = dy * Width;

            // Clip the horizontal span once instead of testing every pixel.
            var sxStart = Math.Max(0, -x0);
            var sxEnd = Math.Min(source.Width, Width - x0);
            if (sxEnd <= sxStart) continue;

            switch (op)
            {
                case CombinationOperator.Or:
                    for (var sx = sxStart; sx < sxEnd; sx++)
                        Data[dstRow + x0 + sx] |= source.Data[srcRow + sx];
                    break;
                case CombinationOperator.And:
                    for (var sx = sxStart; sx < sxEnd; sx++)
                        Data[dstRow + x0 + sx] &= source.Data[srcRow + sx];
                    break;
                case CombinationOperator.Xor:
                    for (var sx = sxStart; sx < sxEnd; sx++)
                        Data[dstRow + x0 + sx] ^= source.Data[srcRow + sx];
                    break;
                case CombinationOperator.Xnor:
                    for (var sx = sxStart; sx < sxEnd; sx++)
                    {
                        ref var d = ref Data[dstRow + x0 + sx];
                        d = (byte)(1 - (d ^ source.Data[srcRow + sx]));
                    }
                    break;
                default: // Replace
                    source.Data.AsSpan(srcRow + sxStart, sxEnd - sxStart)
                               .CopyTo(Data.AsSpan(dstRow + x0 + sxStart));
                    break;
            }
        }
    }
}
