namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// Y′CbCr → RGBA8 conversion for decoded HEVC images. H.265 defines the decoded samples, not this step, so
/// it is defined here and frozen (like <c>JpegColor</c>): any change to the arithmetic is an output change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Matrix.</b> <c>matrix_coefficients</c> (H.273 / H.265 Table E.5) selects Kr and Kb: 1 → BT.709,
/// 4 → FCC, 5 / 6 → BT.601, 7 → SMPTE 240M, 9 / 10 → BT.2020 (10, constant luminance, is approximated
/// by the non-constant-luminance matrix), 0 → identity (G = Y, B = Cb, R = Cr); anything else, including
/// 2 (unspecified), → BT.601. Then R = Y + 2(1 − Kr)·Cr, B = Y + 2(1 − Kb)·Cb,
/// G = Y − 2Kb(1 − Kb)/Kg·Cb − 2Kr(1 − Kr)/Kg·Cr, with Kg = 1 − Kr − Kb.
/// </para>
/// <para>
/// <b>Range and bit depth.</b> Full range: Y and C span 0..2^b − 1, C centred at 2^(b−1), scaled by
/// 255 / (2^b − 1). Limited range: Y spans 16·2^(b−8) .. 235·2^(b−8) and C 16·2^(b−8) .. 240·2^(b−8)
/// (centred at 128·2^(b−8)), scaled by 255 / (219·2^(b−8)) and 255 / (224·2^(b−8)). That maps 10-bit
/// samples straight to 8-bit output, with no separate 10 → 8 bit step.
/// </para>
/// <para>
/// <b>Arithmetic.</b> Each output is (Σ coefficient·(sample − offset) + 2^15) &gt;&gt; 16, clamped to 0..255,
/// with every coefficient round(value · 2^16) computed once in double precision from the constants above.
/// Integer-only per pixel, so the result does not depend on the ISA.
/// </para>
/// <para>
/// <b>Chroma.</b> 4:2:0 chroma is replicated (nearest neighbour): chroma sample (x / 2, y / 2) serves
/// luma sample (x, y). It needs no samples outside a grid tile, so tiles convert independently.
/// </para>
/// </remarks>
internal static class HeifColor
{
    internal readonly record struct Coefficients(
        int YOffset, int COffset, int Y, int CrR, int CbG, int CrG, int CbB, bool Identity);

    internal static Coefficients For(int matrixCoefficients, bool fullRange, int bitDepth)
    {
        var scale = 1 << (bitDepth - 8);
        double yMul, cMul;
        int yOffset;
        if (fullRange)
        {
            yMul = cMul = 255.0 / ((1 << bitDepth) - 1);
            yOffset = 0;
        }
        else
        {
            yMul = 255.0 / (219 * scale);
            cMul = 255.0 / (224 * scale);
            yOffset = 16 * scale;
        }
        var cOffset = 1 << (bitDepth - 1);

        if (matrixCoefficients == 0)
        {
            // Identity (GBR): every channel scaled like luma.
            var f = Fixed(yMul);
            return new Coefficients(yOffset, yOffset, f, 0, 0, 0, 0, true);
        }

        var (kr, kb) = matrixCoefficients switch
        {
            1 => (0.2126, 0.0722),
            4 => (0.30, 0.11),
            7 => (0.212, 0.087),
            9 or 10 => (0.2627, 0.0593),
            _ => (0.299, 0.114),
        };
        var kg = 1 - kr - kb;
        return new Coefficients(
            yOffset,
            cOffset,
            Fixed(yMul),
            Fixed(2 * (1 - kr) * cMul),
            Fixed(2 * kb * (1 - kb) / kg * cMul),
            Fixed(2 * kr * (1 - kr) / kg * cMul),
            Fixed(2 * (1 - kb) * cMul),
            false);
    }

    private static int Fixed(double value) => (int)Math.Round(value * 65536, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Converts a 4:2:0 block of <paramref name="width"/> × <paramref name="height"/> luma samples to RGBA8.
    /// <paramref name="rgba"/> starts at the block's first pixel and advances <paramref name="rgbaStride"/> bytes per row.
    /// </summary>
    internal static void ToRgba420(ReadOnlySpan<ushort> y, int yStride, ReadOnlySpan<ushort> cb, ReadOnlySpan<ushort> cr,
        int cStride, int width, int height, in Coefficients k, Span<byte> rgba, int rgbaStride)
    {
        for (var row = 0; row < height; row++)
        {
            var yRow = y.Slice(row * yStride, width);
            var cRow = (row >> 1) * cStride;
            var outRow = rgba.Slice(row * rgbaStride, width * 4);
            for (var x = 0; x < width; x++)
            {
                var luma = yRow[x] - k.YOffset;
                var u = cb[cRow + (x >> 1)] - k.COffset;
                var v = cr[cRow + (x >> 1)] - k.COffset;
                int r, g, b;
                if (k.Identity)
                {
                    r = (k.Y * v + 32768) >> 16;
                    g = (k.Y * luma + 32768) >> 16;
                    b = (k.Y * u + 32768) >> 16;
                }
                else
                {
                    var yy = k.Y * luma + 32768;
                    r = (yy + k.CrR * v) >> 16;
                    g = (yy - k.CbG * u - k.CrG * v) >> 16;
                    b = (yy + k.CbB * u) >> 16;
                }
                outRow[x * 4] = (byte)Math.Clamp(r, 0, 255);
                outRow[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
                outRow[x * 4 + 2] = (byte)Math.Clamp(b, 0, 255);
                outRow[x * 4 + 3] = 255;
            }
        }
    }
}
