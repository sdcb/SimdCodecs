namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// ITU-T H.273 §8.1 colour primaries. The numeric codepoints are what gets
/// written into a PNG cICP chunk byte 0 or an ICC v4.4 <c>cicp</c> tag byte 0.
/// </summary>
/// <remarks>
/// H.273 also reserves several values (0, 3, 13-21, 23+); pass any
/// <c>(ColorPrimaries)b</c> for forward-compat when the spec adds new ones.
/// Members are named after the dominant industry name where one exists;
/// where two conventions coexist (e.g. BT.709 = sRGB primaries) the
/// numerically-first name in the spec wins.
/// </remarks>
public enum ColorPrimaries : byte
{
    /// <summary>Rec.709 / sRGB / sYCC primaries. The default for SDR
    /// display-referred content.</summary>
    BT709 = 1,

    /// <summary>Image characteristics are unknown or are intentionally
    /// not signalled. Decoders generally fall back to BT.709.</summary>
    Unspecified = 2,

    /// <summary>BT.470 System M -- NTSC 1953 primaries.</summary>
    BT470M = 4,

    /// <summary>BT.470 System B/G -- PAL / SECAM.</summary>
    BT470BG = 5,

    /// <summary>SMPTE 170M / 240M -- NTSC SMPTE-C.</summary>
    SMPTE170M = 6,

    /// <summary>SMPTE 240M -- early HDTV.</summary>
    SMPTE240M = 7,

    /// <summary>Generic film primaries (Society of Motion Picture and
    /// Television Engineers).</summary>
    Generic = 8,

    /// <summary>Rec.2020 / Rec.2100 wide-gamut primaries. Used for HDR
    /// (HDR10, HLG) and 4K UHD.</summary>
    BT2020 = 9,

    /// <summary>SMPTE ST 428-1 -- CIE 1931 XYZ; used by digital cinema
    /// distribution masters.</summary>
    SMPTE428 = 10,

    /// <summary>SMPTE RP 431-2 -- DCI-P3 (D63 white point, theatre).</summary>
    DciP3 = 11,

    /// <summary>SMPTE EG 432-1 -- Display P3 (D65 white point, consumer
    /// devices). Same primaries as DCI-P3 but a different white.</summary>
    DisplayP3 = 12,

    /// <summary>EBU Tech 3213-E -- 1975 PAL studio primaries; very rare
    /// in modern signalling.</summary>
    EBU3213 = 22,
}

/// <summary>
/// ITU-T H.273 §8.2 transfer characteristics. The numeric codepoints are
/// what gets written into a PNG cICP chunk byte 1 or an ICC v4.4
/// <c>cicp</c> tag byte 1.
/// </summary>
public enum TransferFunction : byte
{
    /// <summary>Rec.709 / SMPTE 170M / BT.601 OETF. The standard SDR HD
    /// curve.</summary>
    BT709 = 1,

    /// <summary>Unknown / intentionally unsignalled.</summary>
    Unspecified = 2,

    /// <summary>BT.470 System M -- assumed display gamma 2.2.</summary>
    BT470M = 4,

    /// <summary>BT.470 System B/G -- assumed display gamma 2.8.</summary>
    BT470BG = 5,

    /// <summary>SMPTE 170M / NTSC; functionally equivalent to BT.709.</summary>
    SMPTE170M = 6,

    /// <summary>SMPTE 240M -- early HDTV transfer.</summary>
    SMPTE240M = 7,

    /// <summary>Linear -- code value equals scene-relative intensity.
    /// Often paired with float pixel formats.</summary>
    Linear = 8,

    /// <summary>Log curve, 100:1 range.</summary>
    Log100 = 9,

    /// <summary>Log curve, 316:1 (sqrt 10) range.</summary>
    Log316 = 10,

    /// <summary>IEC 61966-2-4 xvYCC -- extended-range BT.709.</summary>
    Iec6196624 = 11,

    /// <summary>BT.1361 extended colour gamut transfer.</summary>
    BT1361 = 12,

    /// <summary>IEC 61966-2-1 sRGB / sYCC. The default SDR display-
    /// referred curve for desktop / web imagery.</summary>
    Srgb = 13,

    /// <summary>Rec.2020, 10-bit.</summary>
    BT2020_10 = 14,

    /// <summary>Rec.2020, 12-bit.</summary>
    BT2020_12 = 15,

    /// <summary>SMPTE ST 2084 -- Perceptual Quantizer (PQ); the HDR10
    /// transfer. 0 to 1 code maps to 0 to 10,000 nits absolute.</summary>
    Pq = 16,

    /// <summary>SMPTE ST 428-1 -- digital cinema reference curve.</summary>
    SMPTE428 = 17,

    /// <summary>ARIB STD-B67 -- Hybrid Log-Gamma (HLG); the broadcast HDR
    /// transfer. Reference-display dependent, has graceful SDR fallback.</summary>
    Hlg = 18,
}

/// <summary>
/// ITU-T H.273 §8.3 matrix coefficients. The numeric codepoints are what
/// gets written into a PNG cICP chunk byte 2 or an ICC v4.4 <c>cicp</c>
/// tag byte 2.
/// </summary>
/// <remarks>
/// PNG-3 §11.3.2.6 and ICC v4.4's <c>cicp</c> tag both REQUIRE
/// <see cref="Identity"/> (RGB) for image-file use; matrix transforms only
/// arise in the video-codec context for which H.273 was originally written.
/// The named non-identity values are included for forward-compat reads but
/// SHOULD NOT be written into a PNG or TIFF-ICC cicp.
/// </remarks>
public enum MatrixCoefficients : byte
{
    /// <summary>RGB / identity matrix. The ONLY legal value for PNG cICP
    /// and ICC cicp per their respective specs.</summary>
    Identity = 0,

    /// <summary>BT.709 -- HDTV YCbCr.</summary>
    BT709 = 1,

    /// <summary>Unknown / unspecified.</summary>
    Unspecified = 2,

    /// <summary>FCC NTSC.</summary>
    Fcc = 4,

    /// <summary>BT.470 System B/G -- PAL/SECAM YCbCr.</summary>
    BT470BG = 5,

    /// <summary>SMPTE 170M -- NTSC SMPTE-C YCbCr.</summary>
    SMPTE170M = 6,

    /// <summary>SMPTE 240M YCbCr.</summary>
    SMPTE240M = 7,

    /// <summary>YCgCo.</summary>
    YCgCo = 8,

    /// <summary>BT.2020 non-constant luminance YCbCr.</summary>
    BT2020NCL = 9,

    /// <summary>BT.2020 constant luminance YCbCr.</summary>
    BT2020CL = 10,

    /// <summary>SMPTE ST 2085 -- Y'D'zD'x.</summary>
    SMPTE2085 = 11,

    /// <summary>ITP / Rec.2100 -- HDR perceptual matrix.</summary>
    Ictcp = 14,
}
