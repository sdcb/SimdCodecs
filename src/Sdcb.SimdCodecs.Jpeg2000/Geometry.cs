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
/// quality layers, which is the state that makes tier-2 stateful. A one-layer
/// codestream cannot exercise it, which is exactly why hazard 5 warns that a
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

    /// <summary>T.800 Table A.19 code-block style flags of the tile-component this band belongs to.</summary>
    public required int CodeBlockStyle { get; init; }

    /// <summary>xcb', the code-block width exponent after the precinct cap of Equation B-16.</summary>
    public required int BlockWidthExponent { get; init; }

    /// <summary>ycb', the code-block height exponent after the precinct cap.</summary>
    public required int BlockHeightExponent { get; init; }

    /// <summary>The absolute column of the first code-block, on the grid anchored at the band origin.</summary>
    public required int FirstBlockColumn { get; init; }

    /// <summary>The absolute row of the first code-block.</summary>
    public required int FirstBlockRow { get; init; }
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

    /// <summary>
    /// The resolution's precincts in raster order (T.800 B.6), which is the order their index k
    /// counts them in. Empty when the resolution itself is empty: such a resolution has no packets.
    /// </summary>
    public required Precinct[] Precincts { get; init; }
}

/// <summary>
/// One precinct of one resolution (T.800 B.6): the unit a packet carries. It holds, for each band
/// of its resolution, the rectangle of code-blocks that fall inside it and that band's two tag
/// trees, which belong to the precinct and persist across the packets of successive layers.
/// </summary>
internal sealed class Precinct
{
    /// <summary>One entry per band of the resolution, in the band order the packet codes them.</summary>
    public required PrecinctBand[] Bands { get; init; }

    /// <summary>
    /// Where the position-driven progressions (RPCL, PCRL, CPRL; T.800 B.12.1.3 to B.12.1.5) visit
    /// this precinct on the reference grid: the precinct's top-left corner scaled up to the reference
    /// grid, clipped to the tile's corner. Those orders walk the reference grid and take a precinct
    /// at the first point that falls on it, which is exactly this point.
    /// </summary>
    public required long VisitX { get; init; }

    /// <summary>The vertical counterpart of <see cref="VisitX"/>.</summary>
    public required long VisitY { get; init; }
}

/// <summary>The code-blocks one precinct takes from one band, and that band's tag trees for the precinct.</summary>
internal sealed class PrecinctBand
{
    /// <summary>The band.</summary>
    public required Subband Band { get; init; }

    /// <summary>The first code-block column, as an index into <see cref="Subband.Blocks"/>' grid.</summary>
    public required int BlockX0 { get; init; }

    /// <summary>The first code-block row, likewise.</summary>
    public required int BlockY0 { get; init; }

    /// <summary>Code-blocks across this precinct in this band; zero when none fall inside it.</summary>
    public required int BlocksWide { get; init; }

    /// <summary>Code-blocks down.</summary>
    public required int BlocksHigh { get; init; }

    /// <summary>
    /// The inclusion tag tree over this precinct's code-blocks: for each, the layer that first carries it.
    /// </summary>
    public TagTree? InclusionTree { get; private set; }

    /// <summary>The imsb tag tree: for each code-block, how many leading magnitude bit-planes are zero.</summary>
    public TagTree? ZeroBitPlaneTree { get; private set; }

    /// <summary>
    /// Creates the pair once, on first use. They persist across the packets of successive quality
    /// layers: rebuilding them per packet decodes layer 0 correctly and every later layer wrongly.
    /// </summary>
    public void EnsureTagTrees()
    {
        InclusionTree ??= new TagTree(BlocksWide, BlocksHigh);
        ZeroBitPlaneTree ??= new TagTree(BlocksWide, BlocksHigh);
    }
}

/// <summary>
/// One component of one tile, resolved from the header into the full geometry
/// tier-2 and the DWT both need (T.800 Annex B).
/// <para>
/// Everything here is computed from declared numbers before a coded byte is
/// read, which is why <see cref="Build(CodestreamHeader, int, Rect, Jpeg2000SampleBudget, int)"/> is where the resource ceilings are
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
    /// <see cref="Build(CodestreamHeader, int, Rect, Jpeg2000SampleBudget, int)"/> for the tile that
    /// covers the whole image region: the only tile of a single-tile codestream.
    /// </summary>
    public static TileComponent Build(
        CodestreamHeader header, int componentIndex, Jpeg2000SampleBudget budget, int decodedResolutions)
    {
        var siz = header.Siz;
        return Build(header, componentIndex, new Rect(siz.X0, siz.Y0, siz.X1, siz.Y1), budget, decodedResolutions);
    }

    /// <summary>
    /// Builds the geometry for one component of one tile, from that component's own coding style
    /// and quantization (COD and QCD with its COC and QCC applied).
    /// </summary>
    /// <param name="tile">The tile's extent on the reference grid (T.800 Equation B-7).</param>
    /// <param name="decodedResolutions">
    /// How many resolution levels from 0 will be reconstructed; only their subbands get coefficient
    /// storage, and only theirs are charged to the budget as samples.
    /// </param>
    public static TileComponent Build(
        CodestreamHeader header, int componentIndex, Rect tile, Jpeg2000SampleBudget budget, int decodedResolutions)
    {
        var siz = header.Siz;
        var cod = header.ComponentCoding[componentIndex];
        var quantization = header.ComponentQuantization[componentIndex];
        var component = siz.Components[componentIndex];

        // T.800 Equation B-12: the tile divided by the component's subsampling, rounding up. The
        // formula is written in full because the coordinates, not the sizes, are what later
        // equations consume.
        var bounds = new Rect(
            CeilDiv(tile.X0, component.HorizontalSeparation),
            CeilDiv(tile.Y0, component.VerticalSeparation),
            CeilDiv(tile.X1, component.HorizontalSeparation),
            CeilDiv(tile.Y1, component.VerticalSeparation));

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

            resolutions[r] = new Resolution
            {
                Index = r,
                Bounds = resolutionBounds,
                Bands = bands,
                Precincts = BuildPrecincts(resolutionBounds, bands, cod, r, levels, tile, component, budget),
            };
        }

        return new TileComponent
        {
            Bounds = bounds,
            Resolutions = resolutions,
            Irreversible = cod.Transform == WaveletTransform.Irreversible97,
            DecodedResolutions = decodedResolutions,
        };
    }

    /// <summary>
    /// T.800 B.6: partitions a resolution into precincts of 2^PPx x 2^PPy on the resolution grid,
    /// anchored at its origin, and finds, for each precinct, the code-blocks of every band that fall
    /// inside it. A band above resolution 0 sees the precinct at half size, 2^(PPx-1), because band
    /// coordinates are resolution coordinates halved; and since Equation B-16 caps the code-block size
    /// at that, code-blocks nest inside precincts exactly.
    /// </summary>
    private static Precinct[] BuildPrecincts(
        Rect resolution, Subband[] bands, CodingStyle cod, int r, int levels, Rect tile, ComponentInfo component,
        Jpeg2000SampleBudget budget)
    {
        if (resolution.IsEmpty) return [];

        var ppx = cod.PrecinctWidthExponent(r);
        var ppy = cod.PrecinctHeightExponent(r);
        var firstX = FloorDiv(resolution.X0, 1 << ppx);
        var firstY = FloorDiv(resolution.Y0, 1 << ppy);
        var wide = CeilDiv(resolution.X1, 1 << ppx) - firstX;
        var high = CeilDiv(resolution.Y1, 1 << ppy) - firstY;

        // A precinct is a handful of objects, so the count is booked against the code-block
        // ceiling: a header with tiny precincts on a large image must not buy an object graph its
        // data could never fill.
        var count = (long)wide * high;
        if (count > Jpeg2000Limits.MaxCodeBlocks)
            throw new InvalidDataException(
                $"JPEG 2000: a resolution declares {count:N0} precincts, more than the " +
                $"{Jpeg2000Limits.MaxCodeBlocks:N0} ceiling.");
        budget.ChargeCodeBlocks((int)count);

        var bandPpx = r == 0 ? ppx : ppx - 1;
        var bandPpy = r == 0 ? ppy : ppy - 1;
        var scale = levels - r;

        var precincts = new Precinct[count];
        for (var py = 0; py < high; py++)
        {
            for (var px = 0; px < wide; px++)
            {
                long column = firstX + px;
                long row = firstY + py;

                var precinctBands = new PrecinctBand[bands.Length];
                for (var b = 0; b < bands.Length; b++)
                {
                    var band = bands[b];

                    // The precinct's code-blocks on the band's absolute block grid, clipped to the
                    // blocks the band has.
                    var x0 = Math.Max(band.FirstBlockColumn, (column << bandPpx) >> band.BlockWidthExponent);
                    var x1 = Math.Min(band.FirstBlockColumn + band.BlocksWide,
                        ((column + 1) << bandPpx) >> band.BlockWidthExponent);
                    var y0 = Math.Max(band.FirstBlockRow, (row << bandPpy) >> band.BlockHeightExponent);
                    var y1 = Math.Min(band.FirstBlockRow + band.BlocksHigh,
                        ((row + 1) << bandPpy) >> band.BlockHeightExponent);

                    var empty = x1 <= x0 || y1 <= y0;
                    precinctBands[b] = new PrecinctBand
                    {
                        Band = band,
                        BlockX0 = empty ? 0 : (int)(x0 - band.FirstBlockColumn),
                        BlockY0 = empty ? 0 : (int)(y0 - band.FirstBlockRow),
                        BlocksWide = empty ? 0 : (int)(x1 - x0),
                        BlocksHigh = empty ? 0 : (int)(y1 - y0),
                    };
                }

                // Where B.12.1.3's walk over the reference grid first lands in this precinct: its
                // corner on the resolution grid times 2^(NL - r) and the subsampling, or the tile's
                // corner for the precinct that starts before the tile does.
                precincts[py * wide + px] = new Precinct
                {
                    Bands = precinctBands,
                    VisitX = Math.Max(tile.X0, ((column << ppx) << scale) * component.HorizontalSeparation),
                    VisitY = Math.Max(tile.Y0, ((row << ppy) << scale) * component.VerticalSeparation),
                };
            }
        }

        return precincts;
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
            CodeBlockStyle = cod.CodeBlockStyle,
            BlockWidthExponent = blockWidthExponent,
            BlockHeightExponent = blockHeightExponent,
            FirstBlockColumn = firstColumn,
            FirstBlockRow = firstRow,
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
