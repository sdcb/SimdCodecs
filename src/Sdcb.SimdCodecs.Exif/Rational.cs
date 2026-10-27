namespace Sdcb.SimdCodecs.Exif;

/// <summary>
/// TIFF/EXIF <c>RATIONAL</c> field — an unsigned numerator/denominator pair,
/// 8 bytes total (two 32-bit little-endian or big-endian UInt32 values per the
/// containing file's byte order). Used pervasively in EXIF for shutter
/// (1/250), aperture (28/10 = f/2.8), focal length (35/1 = 35 mm), GPS
/// coordinates (degrees/minutes/seconds), and resolution tags.
/// </summary>
public readonly record struct Rational(uint Numerator, uint Denominator)
{
    /// <summary>Convert to a floating-point ratio. Returns 0 for a 0 denominator.</summary>
    public double ToDouble() => Denominator == 0 ? 0d : (double)Numerator / Denominator;

    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>
/// TIFF/EXIF <c>SRATIONAL</c> field — signed numerator/denominator pair. Used
/// for tags like <c>ShutterSpeedValue</c> (APEX, signed) and <c>BrightnessValue</c>.
/// </summary>
public readonly record struct SRational(int Numerator, int Denominator)
{
    public double ToDouble() => Denominator == 0 ? 0d : (double)Numerator / Denominator;

    public override string ToString() => $"{Numerator}/{Denominator}";
}
