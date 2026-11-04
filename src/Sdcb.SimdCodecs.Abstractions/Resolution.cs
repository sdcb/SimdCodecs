using System;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>The unit of a <see cref="Resolution"/>.</summary>
public enum ResolutionUnit : byte
{
    /// <summary>No physical unit: the values give only the pixel aspect ratio (PNG pHYs unit 0, JFIF units 0).</summary>
    AspectRatio,

    /// <summary>Pixels per inch.</summary>
    PerInch,

    /// <summary>Pixels per centimetre.</summary>
    PerCentimeter,

    /// <summary>Pixels per metre (PNG pHYs, BMP).</summary>
    PerMeter,
}

/// <summary>A pixel density as the file declares it, in its own unit.</summary>
/// <param name="X">Pixels per unit, horizontally.</param>
/// <param name="Y">Pixels per unit, vertically.</param>
/// <param name="Unit">The unit.</param>
public readonly record struct Resolution(double X, double Y, ResolutionUnit Unit)
{
    /// <summary>The horizontal density in pixels per inch; null for <see cref="ResolutionUnit.AspectRatio"/>.</summary>
    public double? DpiX => ToDpi(X);

    /// <summary>The vertical density in pixels per inch; null for <see cref="ResolutionUnit.AspectRatio"/>.</summary>
    public double? DpiY => ToDpi(Y);

    /// <summary>A density in pixels per inch.</summary>
    public static Resolution FromDpi(double x, double y) => new(x, y, ResolutionUnit.PerInch);

    private double? ToDpi(double v) => Unit switch
    {
        ResolutionUnit.PerInch => v,
        ResolutionUnit.PerCentimeter => v * 2.54,
        ResolutionUnit.PerMeter => v * 0.0254,
        _ => null,
    };
}
