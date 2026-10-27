namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The IEC 61966-2-1 sRGB transfer functions. Gain-map math is defined on
/// <em>linear</em> light, and both renditions of a gain-map JPEG are ordinary
/// display-referred sRGB — so every pixel passes through the EOTF on the way
/// into the math and (for the SDR side of generation) the OETF on the way out.
/// </summary>
internal static class SrgbTransfer
{
    /// <summary>EOTF (decode): sRGB-encoded [0,1] to linear [0,1].</summary>
    public static double Eotf(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    /// <summary>OETF (encode): linear [0,1] to sRGB-encoded [0,1].</summary>
    public static double Oetf(double linear) =>
        linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

    /// <summary>EOTF for every 8-bit code value — the per-pixel hot path.</summary>
    public static readonly double[] EotfLut8 = BuildLut();

    private static double[] BuildLut()
    {
        var lut = new double[256];
        for (var i = 0; i < lut.Length; i++)
            lut[i] = Eotf(i / 255.0);
        return lut;
    }
}
