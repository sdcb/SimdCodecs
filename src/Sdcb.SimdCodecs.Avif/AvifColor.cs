namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// YCbCr / monochrome + optional alpha → RGBA8 conversion for decoded AVIF images.
/// Uses exact fixed-point integer arithmetic (matching the repo's frozen conversion model).
/// </summary>
internal static class AvifColor
{
    internal readonly record struct Coefficients(
        int YOffset, int COffset, int Y, int CrR, int CbG, int CrG, int CbB, bool Identity);

    internal static Coefficients For(int matrixCoefficients, bool fullRange, int bitDepth)
    {
        int scale = 1 << (bitDepth - 8);
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
        int cOffset = 1 << (bitDepth - 1);

        if (matrixCoefficients == 0)
            return new Coefficients(yOffset, yOffset, Fixed(yMul), 0, 0, 0, 0, true);

        var (kr, kb) = matrixCoefficients switch
        {
            1 => (0.2126, 0.0722),
            4 => (0.30, 0.11),
            7 => (0.212, 0.087),
            9 or 10 => (0.2627, 0.0593),
            _ => (0.299, 0.114),
        };
        double kg = 1.0 - kr - kb;
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

    private static int Fixed(double value) => (int)Math.Round(value * 65536.0, MidpointRounding.AwayFromZero);

    internal static byte SampleAlphaTo8(ushort sample, int alphaBitDepth, bool alphaFullRange)
    {
        if (alphaFullRange)
        {
            if (alphaBitDepth == 8) return (byte)Math.Min(sample, (ushort)255);
            int max = (1 << alphaBitDepth) - 1;
            return (byte)Math.Clamp((sample * 255 + (max >> 1)) / max, 0, 255);
        }
        int scale = 1 << (alphaBitDepth - 8);
        int a = (int)Math.Round((sample - 16 * scale) * 255.0 / (219 * scale));
        return (byte)Math.Clamp(a, 0, 255);
    }

    internal static void PixelToRgba(
        ushort ySample, ushort cbSample, ushort crSample, bool monochrome,
        in Coefficients k, byte alpha, bool premultiplied,
        out byte rOut, out byte gOut, out byte bOut, out byte aOut)
    {
        int r, g, b;
        int luma = ySample - k.YOffset;
        if (monochrome)
        {
            r = g = b = Math.Clamp((k.Y * luma + 32768) >> 16, 0, 255);
        }
        else if (k.Identity)
        {
            int u = cbSample - k.COffset;
            int v = crSample - k.COffset;
            r = Math.Clamp((k.Y * v + 32768) >> 16, 0, 255);
            g = Math.Clamp((k.Y * luma + 32768) >> 16, 0, 255);
            b = Math.Clamp((k.Y * u + 32768) >> 16, 0, 255);
        }
        else
        {
            int u = cbSample - k.COffset;
            int v = crSample - k.COffset;
            int yy = k.Y * luma + 32768;
            r = Math.Clamp((yy + k.CrR * v) >> 16, 0, 255);
            g = Math.Clamp((yy - k.CbG * u - k.CrG * v) >> 16, 0, 255);
            b = Math.Clamp((yy + k.CbB * u) >> 16, 0, 255);
        }

        if (premultiplied && alpha > 0 && alpha < 255)
        {
            r = Math.Min(255, (r * 255 + (alpha >> 1)) / alpha);
            g = Math.Min(255, (g * 255 + (alpha >> 1)) / alpha);
            b = Math.Min(255, (b * 255 + (alpha >> 1)) / alpha);
        }

        rOut = (byte)r;
        gOut = (byte)g;
        bOut = (byte)b;
        aOut = alpha;
    }
}
