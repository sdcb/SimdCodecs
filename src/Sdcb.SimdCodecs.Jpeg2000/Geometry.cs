using System;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// Which of the four subband orientations a band is (T.800 Figure B.5). The
/// values are the spec's own <c>b</c> indices, and the orientation is not
/// cosmetic: it selects the zero-coding context table in tier-1 (Table D.1) and
/// the log2 gain in Annex E.
/// </summary>
internal enum BandKind
{
    /// <summary>Low-pass both ways — the only band at resolution 0.</summary>
    Ll = 0,

    /// <summary>Horizontally high-pass, vertically low-pass.</summary>
    Hl = 1,

    /// <summary>Horizontally low-pass, vertically high-pass.</summary>
    Lh = 2,

    /// <summary>High-pass both ways.</summary>
    Hh = 3,
}

/// <summary>
/// A rectangle on one of T.800's coordinate grids, half-open: it contains
/// <c>X0 &lt;= x &lt; X1</c>.
/// <para>
/// Absolute coordinates, never a width and a height at the origin. Annex B
/// writes every partition — tiles, resolutions, subbands, precincts,
/// code-blocks — as a rectangle on a grid whose origin the image does not
/// necessarily sit on, and the <em>parity</em> of the coordinates is load-bearing:
/// the DWT interleave of F.3.3 places a sample by whether its index is even or
/// odd, so a rectangle rebased to zero decodes a shifted image.
/// </para>
/// </summary>
internal readonly record struct Rect(int X0, int Y0, int X1, int Y1)
{
    /// <summary>Samples across; zero when the rectangle is empty.</summary>
    public int Width => Math.Max(0, X1 - X0);

    /// <summary>Samples down; zero when the rectangle is empty.</summary>
    public int Height => Math.Max(0, Y1 - Y0);

    /// <summary>True when the rectangle holds no samples at all.</summary>
    public bool IsEmpty => X1 <= X0 || Y1 <= Y0;

    /// <summary>Total samples.</summary>
    public long Area => (long)Width * Height;
}

/// <summary>
/// One code-block: the unit tier-1 entropy-decodes (T.800 B.7).
/// <para>
/// Mutable, and deliberately so — <see cref="Included"/>, <see cref="LBlock"/>
/// and <see cref="PassCount"/> persist <em>across</em> the packets of successive
/// quality layers, which is the state that makes tier-2 stateful. Rung 1 has one
/// layer and so cannot exercise it, which is exactly why hazard 5 warns that a
/// single-layer fixture proves nothing about this.
/// </para>
/// </summary>
internal sealed class CodeBlock(Rect bounds)
{
    /// <summary>The block's extent in its subband's coordinates, already clipped to the subband.</summary>
    public Rect Bounds { get; } = bounds;

    /// <summary>Whether any layer has included this block yet.</summary>
    public bool Included { get; set; }

    /// <summary>Missing most-significant bit-planes, from the precinct's imsb tag tree.</summary>
    public int ZeroBitPlanes { get; set; }

    /// <summary>Coding passes signalled so far, across every layer.</summary>
    public int PassCount { get; set; }

    /// <summary>
    /// The length-signalling state of B.10.7, which starts at 3 and only ever
    /// grows. It is per code-block and persists across layers.
    /// </summary>
    public int LBlock { get; set; } = 3;

    /// <summary>Where this block's coded bytes are, as offsets into the tile-part data.</summary>
    public List<(int Start, int Length)> Segments { get; } = [];
}

/// <summary>
/// One subband of one resolution level: a rectangle of coefficients, the
/// quantization exponent that fixes how many bit-planes tier-1 will code, and
/// the code-block grid over it.
/// </summary>
internal sealed class Subband
{
    /// <summary>Which orientation this band is.</summary>
    public required BandKind Kind { get; init; }

    /// <summary>The band's extent in its own coordinate system (T.800 Equation B-15).</summary>
    public required Rect Bounds { get; init; }

    /// <summary>The band's exponent from QCD, which with the guard bits fixes Mb.</summary>
    public required int Exponent { get; init; }

    /// <summary>
    /// Mb, the number of magnitude bit-planes tier-1 codes for this band
    /// (T.800 Equation E-2: <c>Mb = G + eps_b - 1</c>).
    /// </summary>
    public required int MagnitudeBits { get; init; }

    /// <summary>Code-blocks across the band.</summary>
    public required int BlocksWide { get; init; }

    /// <summary>Code-blocks down the band.</summary>
    public required int BlocksHigh { get; init; }

    /// <summary>The code-block grid, row-major — the order tier-2 walks it in.</summary>
    public required CodeBlock[] Blocks { get; init; }

    /// <summary>
    /// Whether this band belongs to a 9/7 tile-component, whose coefficients are
    /// quantization indices to be scaled by <see cref="StepSize"/> rather than
    /// the integers the 5/3 wavelet takes as they are.
    /// </summary>
    public required bool Irreversible { get; init; }

    /// <summary>
    /// Delta_b, the dequantisation step (T.800 Equation E-3), for an
    /// irreversible band; 1 for a reversible one, where nothing is quantized.
    /// </summary>
    public required float StepSize { get; init; }

    /// <summary>
    /// Decoded coefficients, row-major over <see cref="Bounds"/>, for a
    /// reversible band; empty for an irreversible one. Signed: tier-1 produces a
    /// magnitude and a sign, and the reversible path needs no scaling on top.
    /// </summary>
    public required int[] Coefficients { get; init; }

    /// <summary>
    /// Dequantized coefficients, row-major over <see cref="Bounds"/>, for an
    /// irreversible band; empty for a reversible one.
    /// </summary>
    public required float[] Values { get; init; }

    /// <summary>
    /// The precinct's inclusion tag tree: for each code-block, the quality layer
    /// that first carries it.
    /// </summary>
    public TagTree? InclusionTree { get; private set; }

    /// <summary>
    /// The precinct's imsb tag tree: for each code-block, how many leading
    /// magnitude bit-planes are entirely zero.
    /// </summary>
    public TagTree? ZeroBitPlaneTree { get; private set; }

    /// <summary>
    /// Creates the pair once, on first use.
    /// <para>
    /// One pair per <em>band</em> is correct only because rung 1 has maximal
    /// precincts, which makes each resolution a single precinct. The trees
    /// genuinely belong to a precinct, and rung 3 must move them there when
    /// custom precinct sizes arrive. What must not change either way is the
    /// lifetime: they persist across the packets of successive quality layers,
    /// and rebuilding them per packet decodes layer 0 correctly and everything
    /// after it wrongly.
    /// </para>
    /// </summary>
    public void EnsureTagTrees()
    {
        InclusionTree ??= new TagTree(BlocksWide, BlocksHigh);
        ZeroBitPlaneTree ??= new TagTree(BlocksWide, BlocksHigh);
    }
}

/// <summary>
/// One resolution level of a tile-component: the image at 1/2^n scale, made of
/// the subbands that have to be inverse-transformed to reach it.
/// </summary>
internal sealed class Resolution
{
    /// <summary>Level index, 0 for the smallest (the bare LL band).</summary>
    public required int Index { get; init; }

    /// <summary>The resolution's extent (T.800 Equation B-14).</summary>
    public required Rect Bounds { get; init; }

    /// <summary>One band (LL) at level 0, three (HL, LH, HH) above it.</summary>
    public required Subband[] Bands { get; init; }
}

/// <summary>
/// One component of one tile, resolved from the header into the full geometry
/// tier-2 and the DWT both need (T.800 Annex B).
/// <para>
/// Everything here is computed from declared numbers before a coded byte is
/// read, which is why <see cref="Build"/> is where the resource ceilings are
/// charged. Hazard 6's point exactly: a <c>SIZ</c> and a <c>COD</c> alone can
/// ask for an arbitrarily large object graph, and no amount of running out of
/// input will stop it.
/// </para>
/// </summary>
internal sealed class TileComponent
{
    /// <summary>The tile-component's extent (T.800 Equation B-12).</summary>
    public required Rect Bounds { get; init; }

    /// <summary>Resolutions from 0 (smallest) to <c>DecompositionLevels</c>.</summary>
    public required Resolution[] Resolutions { get; init; }

    /// <summary>Whether this tile-component uses the 9/7 wavelet, and so reconstructs in floats.</summary>
    public required bool Irreversible { get; init; }

    /// <summary>
    /// How many resolution levels, from 0, a decode reconstructs: all of them for the full image,
    /// fewer for a reduced one. The levels above are read by tier-2, because their packet headers
    /// sit between the ones that matter and must be parsed to be skipped, but are never
    /// entropy-decoded and hold no coefficients.
    /// </summary>
    public required int DecodedResolutions { get; init; }

    /// <summary>
    /// Builds the geometry for one component of a single-tile codestream, from that component's own
    /// coding style and quantization (COD and QCD with its COC and QCC applied).
    /// </summary>
    /// <param name="decodedResolutions">
    /// How many resolution levels from 0 will be reconstructed; only their subbands get coefficient
    /// storage, and only theirs are charged to the budget as samples.
    /// </param>
    public static TileComponent Build(
        CodestreamHeader header, int componentIndex, Jpeg2000SampleBudget budget, int decodedResolutions)
    {
        var siz = header.Siz;
        var cod = header.ComponentCoding[componentIndex];
        var quantization = header.ComponentQuantization[componentIndex];
        var component = siz.Components[componentIndex];

        // T.800 Equation B-12. With one tile the tile IS the image region, and
        // with no subsampling the separation divides out to 1 -- but the formula
        // is written in full because the coordinates, not the sizes, are what
        // later equations consume.
        var bounds = new Rect(
            CeilDiv(siz.X0, component.HorizontalSeparation),
            CeilDiv(siz.Y0, component.VerticalSeparation),
            CeilDiv(siz.X1, component.HorizontalSeparation),
            CeilDiv(siz.Y1, component.VerticalSeparation));

        if (bounds.Area > Jpeg2000Limits.MaxTileComponentSamples)
            throw new InvalidDataException(
                $"JPEG 2000: a tile-component of {bounds.Width}x{bounds.Height} is {bounds.Area:N0} samples, " +
                $"over the {Jpeg2000Limits.MaxTileComponentSamples:N0} ceiling.");

        var levels = cod.DecompositionLevels;
        var resolutions = new Resolution[cod.ResolutionCount];

        for (var r = 0; r < resolutions.Length; r++)
        {
            // T.800 Equation B-14: the resolution grid is the tile-component
            // scaled down by the number of decompositions still to be undone.
            var scale = levels - r;
            var resolutionBounds = new Rect(
                CeilDivPow2(bounds.X0, scale),
                CeilDivPow2(bounds.Y0, scale),
                CeilDivPow2(bounds.X1, scale),
                CeilDivPow2(bounds.Y1, scale));

            // T.800 Table B.1: at resolution 0 the single band is the LL band of
            // the deepest decomposition; above it, each resolution contributes
            // the HL/LH/HH triple of one decomposition level.
            var kinds = r == 0 ? new[] { BandKind.Ll } : [BandKind.Hl, BandKind.Lh, BandKind.Hh];
            var decompositionLevel = r == 0 ? levels : levels - r + 1;

            var bands = new Subband[kinds.Length];
            for (var b = 0; b < kinds.Length; b++)
            {
                var kind = kinds[b];
                var bandBounds = BandBounds(bounds, decompositionLevel, kind);

                // Subband order in QCD is the codestream's: LL, then HL/LH/HH per
                // resolution upward. Deriving the index rather than carrying a
                // running counter keeps it correct if bands are ever built out
                // of order.
                var bandIndex = r == 0 ? 0 : 3 * (r - 1) + b + 1;
                var (exponent, mantissa) = quantization.StepFor(bandIndex, decompositionLevel, levels, componentIndex);

                // T.800 Equation E-3 and Table E.1: Delta_b = 2^(R_b - eps_b) * (1 + mu_b / 2^11),
                // where R_b, the band's nominal dynamic range, is the component's precision
                // plus the log2 gain of its orientation (0 for LL, 1 for HL and LH, 2 for HH).
                var irreversible = cod.Transform == WaveletTransform.Irreversible97;
                var gain = kind switch { BandKind.Ll => 0, BandKind.Hh => 2, _ => 1 };
                var stepSize = irreversible
                    ? (float)(Math.ScaleB(1.0, component.BitDepth + gain - exponent) * (1.0 + mantissa / 2048.0))
                    : 1f;

                var decoded = r < decodedResolutions;
                if (decoded) budget.Charge(bandBounds.Width, bandBounds.Height);
                var band = BuildBand(
                    kind, bandBounds, exponent, quantization.GuardBits, cod, r, irreversible, stepSize, decoded);
                budget.ChargeCodeBlocks(band.Blocks.Length);

                bands[b] = band;
            }

            resolutions[r] = new Resolution { Index = r, Bounds = resolutionBounds, Bands = bands };
        }

        return new TileComponent
        {
            Bounds = bounds,
            Resolutions = resolutions,
            Irreversible = cod.Transform == WaveletTransform.Irreversible97,
            DecodedResolutions = decodedResolutions,
        };
    }

    private static Subband BuildBand(
        BandKind kind, Rect bounds, int exponent, int guardBits, CodingStyle cod, int resolution,
        bool irreversible, float stepSize, bool decoded)
    {
        // T.800 Equation B-16: the code-block size is capped by the precinct's,
        // and above resolution 0 a precinct maps onto a subband at half size, so
        // the cap loses one from the exponent there.
        var precinctWidth = cod.PrecinctWidthExponent(resolution);
        var precinctHeight = cod.PrecinctHeightExponent(resolution);
        var blockWidthExponent = Math.Min(
            cod.CodeBlockWidthExponent, resolution == 0 ? precinctWidth : precinctWidth - 1);
        var blockHeightExponent = Math.Min(
            cod.CodeBlockHeightExponent, resolution == 0 ? precinctHeight : precinctHeight - 1);

        var blockWidth = 1 << blockWidthExponent;
        var blockHeight = 1 << blockHeightExponent;

        // The code-block partition is anchored at the origin of the subband's
        // coordinate system, NOT at the subband's own corner, so the first block
        // of a band whose X0 is not a multiple of the block width is a partial
        // one. Getting this wrong shifts every block by a few columns and looks
        // like an entropy-coding bug.
        var blocksWide = bounds.IsEmpty
            ? 0
            : CeilDiv(bounds.X1, blockWidth) - FloorDiv(bounds.X0, blockWidth);
        var blocksHigh = bounds.IsEmpty
            ? 0
            : CeilDiv(bounds.Y1, blockHeight) - FloorDiv(bounds.Y0, blockHeight);

        var blocks = new CodeBlock[blocksWide * blocksHigh];
        var firstColumn = bounds.IsEmpty ? 0 : FloorDiv(bounds.X0, blockWidth);
        var firstRow = bounds.IsEmpty ? 0 : FloorDiv(bounds.Y0, blockHeight);

        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                var blockRect = new Rect(
                    Math.Max(bounds.X0, (firstColumn + bx) * blockWidth),
                    Math.Max(bounds.Y0, (firstRow + by) * blockHeight),
                    Math.Min(bounds.X1, (firstColumn + bx + 1) * blockWidth),
                    Math.Min(bounds.Y1, (firstRow + by + 1) * blockHeight));

                blocks[by * blocksWide + bx] = new CodeBlock(blockRect);
            }
        }

        return new Subband
        {
            Kind = kind,
            Bounds = bounds,
            Exponent = exponent,

            // T.800 Equation E-2. On the reversible path nothing is quantized,
            // but Mb still decides which bit-plane tier-1 starts on, so QCD is
            // load-bearing here even with quantization style "none".
            MagnitudeBits = guardBits + exponent - 1,
            BlocksWide = blocksWide,
            BlocksHigh = blocksHigh,
            Blocks = blocks,
            Irreversible = irreversible,
            StepSize = stepSize,
            Coefficients = decoded && !irreversible ? new int[bounds.Area] : [],
            Values = decoded && irreversible ? new float[bounds.Area] : [],
        };
    }

    /// <summary>
    /// T.800 Equation B-15: a subband's extent, from the tile-component's, the
    /// decomposition level, and the band's orientation offsets.
    /// </summary>
    private static Rect BandBounds(Rect tileComponent, int decompositionLevel, BandKind kind)
    {
        if (decompositionLevel == 0) return tileComponent;

        // (xob, yob) from T.800 Table B.1: which half of the split each band
        // came from.
        var xob = kind is BandKind.Hl or BandKind.Hh ? 1 : 0;
        var yob = kind is BandKind.Lh or BandKind.Hh ? 1 : 0;

        var denominator = 1 << decompositionLevel;
        var half = 1 << (decompositionLevel - 1);

        // The numerator goes NEGATIVE for a high-pass band whose tile-component
        // starts at 0, so this needs a ceiling that rounds toward positive
        // infinity for negative values too -- `(a + b - 1) / b` does not.
        return new Rect(
            CeilDiv(tileComponent.X0 - half * xob, denominator),
            CeilDiv(tileComponent.Y0 - half * yob, denominator),
            CeilDiv(tileComponent.X1 - half * xob, denominator),
            CeilDiv(tileComponent.Y1 - half * yob, denominator));
    }

    /// <summary>Ceiling division that is correct for negative numerators.</summary>
    internal static int CeilDiv(int value, int divisor) =>
        value >= 0 ? (value + divisor - 1) / divisor : -((-value) / divisor);

    /// <summary>Floor division that is correct for negative numerators.</summary>
    internal static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);

    /// <summary>Ceiling division by a power of two, for the resolution grid.</summary>
    internal static int CeilDivPow2(int value, int exponent) =>
        exponent <= 0 ? value : CeilDiv(value, 1 << exponent);
}
