namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Scalar gain-map parameters — the Adobe hdrgm 1.0 vocabulary, which is also
/// what Android Ultra HDR v1 carries and what ISO/TS 21496-1 standardized.
/// <para>
/// Every ratio field here is a <b>linear</b> value (matching libultrahdr's public
/// metadata struct); the XMP serialization stores <see cref="GainMapMin"/>,
/// <see cref="GainMapMax"/>, <see cref="HdrCapacityMin"/> and
/// <see cref="HdrCapacityMax"/> as log2 — <see cref="GainMapXmp"/> converts on
/// the way in and out. <see cref="Gamma"/> and the offsets are linear in both.
/// </para>
/// <para>
/// Reconstruction (SDR base form, per pixel): <c>recovery = code / 255</c>,
/// <c>logRecovery = lerp(log2(GainMapMin), log2(GainMapMax), recovery^(1/Gamma))</c>,
/// <c>HDR = (SDR_linear + OffsetSdr) · 2^(logRecovery · W) − OffsetHdr</c>, where
/// <c>W</c> ramps 0→1 as the display's linear headroom goes
/// <see cref="HdrCapacityMin"/>→<see cref="HdrCapacityMax"/>.
/// </para>
/// </summary>
public sealed record GainMapMetadata
{
    /// <summary>Linear gain encoded by code 0 (hdrgm default: 1.0 — no attenuation).</summary>
    public double GainMapMin { get; init; } = 1.0;

    /// <summary>Linear gain encoded by code 255 — the content's max boost. Required by the format.</summary>
    public required double GainMapMax { get; init; }

    /// <summary>Encoding gamma applied to the normalized recovery value (default 1.0).</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>Offset added to the SDR linear signal before the ratio (default 1/64, avoids log-of-zero).</summary>
    public double OffsetSdr { get; init; } = 1.0 / 64;

    /// <summary>Offset subtracted from the boosted signal (default 1/64; equal offsets make W=0 reproduce the base exactly).</summary>
    public double OffsetHdr { get; init; } = 1.0 / 64;

    /// <summary>Linear display headroom at/below which the base is shown unmodified (default 1.0 = SDR).</summary>
    public double HdrCapacityMin { get; init; } = 1.0;

    /// <summary>Linear display headroom at/above which the full gain map is applied. Required by the format.</summary>
    public required double HdrCapacityMax { get; init; }

    /// <summary>True when the <em>base</em> rendition is the HDR one (Adobe allows it; Ultra HDR
    /// and this package's write path always use the SDR-base form, i.e. false).</summary>
    public bool BaseRenditionIsHdr { get; init; }

    /// <summary>Throws when the parameters are unusable for encode/reconstruction.</summary>
    public void Validate()
    {
        if (!(GainMapMin > 0) || !(GainMapMax > 0) || GainMapMax < GainMapMin)
            throw new InvalidOperationException($"Gain map bounds must satisfy 0 < GainMapMin <= GainMapMax (got {GainMapMin}..{GainMapMax}).");
        if (!(Gamma > 0))
            throw new InvalidOperationException($"Gamma must be positive (got {Gamma}).");
        if (OffsetSdr < 0 || OffsetHdr < 0)
            throw new InvalidOperationException($"Offsets must be non-negative (got SDR {OffsetSdr}, HDR {OffsetHdr}).");
        if (!(HdrCapacityMin >= 1) || !(HdrCapacityMax > HdrCapacityMin))
            throw new InvalidOperationException($"Capacity must satisfy 1 <= HdrCapacityMin < HdrCapacityMax (got {HdrCapacityMin}..{HdrCapacityMax}).");
    }
}
