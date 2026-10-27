namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// The ITU-T T.800 Table A.2 marker codes this decoder recognises by name.
/// <para>
/// Every marker is two bytes beginning <c>0xFF</c>. The ones listed here are
/// either parsed or deliberately skipped; anything else in the <c>0xFF30</c> to
/// <c>0xFFFF</c> range is refused rather than guessed at, because a marker this
/// decoder does not understand may well change how the bytes after it are to be
/// read.
/// </para>
/// </summary>
internal static class Markers
{
    /// <summary>Start of codestream. Must be the first two bytes.</summary>
    public const ushort Soc = 0xFF4F;

    /// <summary>Image and tile size (A.5.1). Immediately follows SOC.</summary>
    public const ushort Siz = 0xFF51;

    /// <summary>Coding style default (A.6.1).</summary>
    public const ushort Cod = 0xFF52;

    /// <summary>Coding style component (A.6.2) — per-component override of COD.</summary>
    public const ushort Coc = 0xFF53;

    /// <summary>Quantization default (A.6.4).</summary>
    public const ushort Qcd = 0xFF5C;

    /// <summary>Quantization component (A.6.5) — per-component override of QCD.</summary>
    public const ushort Qcc = 0xFF5D;

    /// <summary>Region of interest (A.6.3). Refused: MAXSHIFT is out of scope.</summary>
    public const ushort Rgn = 0xFF5E;

    /// <summary>Progression order change (A.6.6).</summary>
    public const ushort Poc = 0xFF5F;

    /// <summary>Tile-part lengths (A.7.1) — an index, safe to skip.</summary>
    public const ushort Tlm = 0xFF55;

    /// <summary>Packet length, main header (A.7.2) — an index, safe to skip.</summary>
    public const ushort Plm = 0xFF57;

    /// <summary>Packet length, tile-part header (A.7.3) — an index, safe to skip.</summary>
    public const ushort Plt = 0xFF58;

    /// <summary>Packed packet headers, main header (A.7.4).</summary>
    public const ushort Ppm = 0xFF60;

    /// <summary>Packed packet headers, tile-part header (A.7.5).</summary>
    public const ushort Ppt = 0xFF61;

    /// <summary>Component registration (A.9.1) — subsampling offsets, safe to skip.</summary>
    public const ushort Crg = 0xFF63;

    /// <summary>Comment (A.9.2). OpenJPEG stamps its version here.</summary>
    public const ushort Com = 0xFF64;

    /// <summary>Start of tile-part (A.4.2).</summary>
    public const ushort Sot = 0xFF90;

    /// <summary>Start of packet (A.8.1) — optional per-packet marker.</summary>
    public const ushort Sop = 0xFF91;

    /// <summary>End of packet header (A.8.2) — optional per-packet marker.</summary>
    public const ushort Eph = 0xFF92;

    /// <summary>Start of data (A.4.4). Bitstream runs from here to the tile-part end.</summary>
    public const ushort Sod = 0xFF93;

    /// <summary>End of codestream (A.4.4).</summary>
    public const ushort Eoc = 0xFFD9;
}

/// <summary>
/// One component's entry in the <c>SIZ</c> marker (T.800 A.5.1): its sample
/// precision, whether samples are signed, and its subsampling factors on the
/// reference grid.
/// </summary>
/// <param name="BitDepth">Bits per sample, 1 to 38 per the spec; <c>Ssiz</c> low seven bits plus one.</param>
/// <param name="IsSigned">Whether samples are two's complement; <c>Ssiz</c> bit 7.</param>
/// <param name="HorizontalSeparation">XRsiz — reference-grid points per sample horizontally.</param>
/// <param name="VerticalSeparation">YRsiz — reference-grid points per sample vertically.</param>
internal readonly record struct ComponentInfo(
    int BitDepth,
    bool IsSigned,
    int HorizontalSeparation,
    int VerticalSeparation);

/// <summary>
/// The <c>SIZ</c> marker segment (T.800 A.5.1): the image's extent on the
/// reference grid, the tile partition over it, and the components.
/// <para>
/// Note that the image is a <em>region</em> of the reference grid, from
/// <c>(XOsiz, YOsiz)</c> to <c>(Xsiz, Ysiz)</c>, not a width and a height
/// anchored at the origin. Nearly every geometry formula in Annex B is written
/// in those absolute coordinates, and rewriting them in terms of a width loses
/// the parity information the DWT interleave depends on — so they are kept as
/// the spec states them and <see cref="Width"/> / <see cref="Height"/> are
/// derived, not stored.
/// </para>
/// </summary>
internal sealed record SizMarker(
    int X0,
    int Y0,
    int X1,
    int Y1,
    int TileX0,
    int TileY0,
    int TileWidth,
    int TileHeight,
    ComponentInfo[] Components)
{
    /// <summary>Image width in reference-grid points.</summary>
    public int Width => X1 - X0;

    /// <summary>Image height in reference-grid points.</summary>
    public int Height => Y1 - Y0;

    /// <summary>Tiles across the image (T.800 Equation B-5).</summary>
    public long TilesWide => Ceil(X1 - TileX0, TileWidth);

    /// <summary>Tiles down the image (T.800 Equation B-5).</summary>
    public long TilesHigh => Ceil(Y1 - TileY0, TileHeight);

    /// <summary>Total tiles in the image.</summary>
    public long TileCount => TilesWide * TilesHigh;

    /// <summary>
    /// Ceiling division in <see cref="long"/>, and the width is not incidental.
    /// Both operands here are 32-bit codestream fields, so the usual
    /// <c>(numerator + denominator - 1) / denominator</c> overflows for an image
    /// declaring an extent near <see cref="int.MaxValue"/> — which wraps
    /// negative and reports a nonsense tile count, so a decompression bomb gets
    /// refused for the wrong reason and with the wrong exception type. The tile
    /// count then multiplies two of these, hence the long return all the way
    /// out.
    /// </summary>
    private static long Ceil(long numerator, long denominator) =>
        (numerator + denominator - 1) / denominator;
}

/// <summary>Progression order (T.800 Table A.16).</summary>
internal enum ProgressionOrder
{
    /// <summary>Layer, resolution, component, position.</summary>
    Lrcp = 0,

    /// <summary>Resolution, layer, component, position.</summary>
    Rlcp = 1,

    /// <summary>Resolution, position, component, layer.</summary>
    Rpcl = 2,

    /// <summary>Position, component, resolution, layer.</summary>
    Pcrl = 3,

    /// <summary>Component, position, resolution, layer.</summary>
    Cprl = 4,
}

/// <summary>Wavelet filter (T.800 Table A.20).</summary>
internal enum WaveletTransform
{
    /// <summary>9/7 irreversible — lossy, irrational lifting coefficients.</summary>
    Irreversible97 = 0,

    /// <summary>5/3 reversible — lossless, integer lifting.</summary>
    Reversible53 = 1,
}

/// <summary>
/// The coding style for one tile-component: <c>COD</c>'s contents (T.800 A.6.1),
/// possibly overridden per component by <c>COC</c>.
/// </summary>
/// <param name="DecompositionLevels">Number of DWT decomposition levels; resolutions are this plus one.</param>
/// <param name="CodeBlockWidthExponent">xcb, so code-blocks are 2^xcb wide.</param>
/// <param name="CodeBlockHeightExponent">ycb, so code-blocks are 2^ycb high.</param>
/// <param name="CodeBlockStyle">SPcod code-block style flags (Table A.19) — selective bypass, reset, termall, and so on.</param>
/// <param name="Transform">Which wavelet filter the tile-component uses.</param>
/// <param name="PrecinctSizes">
/// Per-resolution precinct exponents, low nibble PPx and high nibble PPy, one
/// entry per resolution from 0 upward. Empty when <c>COD</c> did not define
/// custom precincts, which means the maximal 2^15 x 2^15 default.
/// </param>
internal sealed record CodingStyle(
    int DecompositionLevels,
    int CodeBlockWidthExponent,
    int CodeBlockHeightExponent,
    int CodeBlockStyle,
    WaveletTransform Transform,
    byte[] PrecinctSizes)
{
    /// <summary>Resolution levels in this tile-component: decomposition levels plus the LL band.</summary>
    public int ResolutionCount => DecompositionLevels + 1;

    /// <summary>
    /// PPx for resolution <paramref name="resolution"/>: the precinct width
    /// exponent on that resolution's own coordinate grid. 15 — the maximum the
    /// spec allows — when no custom precincts were signalled, which makes the
    /// whole resolution one precinct.
    /// </summary>
    public int PrecinctWidthExponent(int resolution) =>
        PrecinctSizes.Length == 0 ? 15 : PrecinctSizes[Math.Min(resolution, PrecinctSizes.Length - 1)] & 0x0F;

    /// <summary>PPy for resolution <paramref name="resolution"/>. See <see cref="PrecinctWidthExponent"/>.</summary>
    public int PrecinctHeightExponent(int resolution) =>
        PrecinctSizes.Length == 0 ? 15 : (PrecinctSizes[Math.Min(resolution, PrecinctSizes.Length - 1)] >> 4) & 0x0F;
}

/// <summary>Quantization style (T.800 Table A.28), the low five bits of <c>Sqcd</c>.</summary>
internal enum QuantizationStyle
{
    /// <summary>No quantization — reversible path; one 8-bit exponent per subband.</summary>
    None = 0,

    /// <summary>Scalar derived — one 16-bit value for the LL band, the rest derived from it.</summary>
    ScalarDerived = 1,

    /// <summary>Scalar expounded — one 16-bit value per subband.</summary>
    ScalarExpounded = 2,
}

/// <summary>
/// The quantization parameters for one tile-component: <c>QCD</c>'s contents
/// (T.800 A.6.4), possibly overridden per component by <c>QCC</c>.
/// </summary>
/// <param name="Style">Which of the three encodings the step sizes use.</param>
/// <param name="GuardBits">
/// Number of guard bits, <c>Sqcd</c> bits 5 to 7. Together with a subband's
/// exponent this fixes Mb, the number of magnitude bit-planes tier-1 will code
/// (T.800 Equation E-2), so it is load-bearing on the reversible path even
/// though nothing there is actually quantized.
/// </param>
/// <param name="Exponents">Per-subband exponent, in the codestream's subband order.</param>
/// <param name="Mantissas">Per-subband mantissa; all zero when <see cref="Style"/> is <see cref="QuantizationStyle.None"/>.</param>
internal sealed record Quantization(
    QuantizationStyle Style,
    int GuardBits,
    int[] Exponents,
    int[] Mantissas)
{
    /// <summary>
    /// The exponent and mantissa (eps_b, mu_b) of one subband.
    /// <para>
    /// Expounded and unquantized styles signal one pair per subband, in the codestream's order.
    /// The derived style signals only the LL band's, and every other band's follows from it by
    /// T.800 Equation E-5: <c>eps_b = eps_0 - N_L + n_b</c>, <c>mu_b = mu_0</c>, where N_L is the
    /// number of decomposition levels and n_b the level the band comes from — so each level
    /// nearer full resolution halves the step.
    /// </para>
    /// </summary>
    /// <param name="bandIndex">The band's position in the codestream's order: LL, then HL, LH, HH per resolution upward.</param>
    /// <param name="decompositionLevel">n_b: the decomposition level the band comes from, N_L for the LL band.</param>
    /// <param name="levels">N_L, the tile-component's decomposition levels.</param>
    /// <param name="component">For the error message.</param>
    /// <exception cref="InvalidDataException">The marker carries too few pairs for the bands it must describe.</exception>
    public (int Exponent, int Mantissa) StepFor(int bandIndex, int decompositionLevel, int levels, int component)
    {
        if (Style == QuantizationStyle.ScalarDerived)
        {
            if (Exponents.Length == 0)
                throw new InvalidDataException(
                    $"JPEG 2000: component {component}'s derived quantization carries no step size.");

            return (Exponents[0] - levels + decompositionLevel, Mantissas[0]);
        }

        if (bandIndex >= Exponents.Length)
            throw new InvalidDataException(
                $"JPEG 2000: component {component}'s quantization carries {Exponents.Length} subband " +
                $"exponents, too few for {levels} decomposition levels (needs {3 * levels + 1}).");

        return (Exponents[bandIndex], Mantissas[bandIndex]);
    }
}
