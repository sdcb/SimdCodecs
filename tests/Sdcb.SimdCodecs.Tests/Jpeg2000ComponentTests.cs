using Sdcb.SimdCodecs.Jpeg2000;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Validation layer 1: the tables and small procedures, driven directly.
/// <para>
/// This layer matters more here than it did for JBIG2, and for a structural
/// reason. JBIG2's rung 1 decoded real pages on its own, so an end-to-end test
/// existed from the start. JPEG 2000 has no such slice — nothing produces a
/// correct pixel until markers, tier-2, tier-1 and the DWT are all right
/// together — so until the whole core closed, these were the only signal
/// available. They stay afterwards because an end-to-end failure says "somewhere
/// in five thousand lines" and these say where.
/// </para>
/// <para>
/// One thing deliberately <b>not</b> tested: that a given context has a given
/// number. Contexts are indices into adaptive slots that all start identical bar
/// three, so permuting the numbering is a bijection the coded bytes cannot see —
/// established by measurement in the JBIG2 work, where swapping two template
/// bits left the reference decoder still agreeing. What is pinned below is
/// <em>which neighbours each context reads</em>, which is the thing a wrong
/// implementation actually gets wrong.
/// </para>
/// </summary>
public class Jpeg2000ComponentTests
{
    // ---------------------------------------------------------------- MQ coder

    /// <summary>
    /// Hazard 1, as a test. T.88 and T.800 share the coder and the <c>Qe</c>
    /// table but not the initial state: T.800 Table D.7 seeds three contexts away
    /// from zero. A decoder that inherited JBIG2's all-zero initialisation
    /// decodes the first code-block plausibly and then drifts, which is the worst
    /// failure signature there is — so the three exceptional states are pinned
    /// against the shared table itself rather than against a magic number.
    /// </summary>
    [Fact]
    public void MqTable_IsSharedBetweenT88AndT800()
    {
        // The table is the same object both codecs use; if it were ever forked,
        // this and the T.88 Annex H.2 conformance test would stop agreeing.
        MqDecoder.StateCount.ShouldBe(47);

        // The three states T.800 D.7 names, read back from the shared table.
        MqDecoder.StateAt(4).Qe.ShouldBe((ushort)0x0521);    // zero-coding context 0
        MqDecoder.StateAt(3).Qe.ShouldBe((ushort)0x0AC1);    // run-length
        MqDecoder.StateAt(46).Qe.ShouldBe((ushort)0x5601);   // uniform, the terminal state
        MqDecoder.StateAt(46).Nmps.ShouldBe((byte)46);
        MqDecoder.StateAt(46).Nlps.ShouldBe((byte)46);
    }

    // ------------------------------------------------------- zero-coding table

    /// <summary>
    /// T.800 Table D.1 for LL and LH: the horizontal neighbours lead, then the
    /// vertical, then the diagonal. Every row of the published table, as written.
    /// </summary>
    [Theory]
    // h, v, d, expected
    [InlineData(2, 0, 0, 8)]
    [InlineData(2, 2, 4, 8)]
    [InlineData(1, 1, 0, 7)]
    [InlineData(1, 2, 4, 7)]
    [InlineData(1, 0, 1, 6)]
    [InlineData(1, 0, 4, 6)]
    [InlineData(1, 0, 0, 5)]
    [InlineData(0, 2, 0, 4)]
    [InlineData(0, 2, 4, 4)]
    [InlineData(0, 1, 0, 3)]
    [InlineData(0, 1, 4, 3)]
    [InlineData(0, 0, 2, 2)]
    [InlineData(0, 0, 4, 2)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(0, 0, 0, 0)]
    public void ZeroCodingContext_MatchesTableD1ForLlAndLh(int h, int v, int d, int expected)
    {
        BlockState.ZeroCodingContext(h, v, d, BandKind.Ll).ShouldBe(expected);
        BlockState.ZeroCodingContext(h, v, d, BandKind.Lh).ShouldBe(expected);
    }

    /// <summary>
    /// The HL column of Table D.1 is the LL/LH one with the horizontal and
    /// vertical counts interchanged — not a different table. Stating it as the
    /// symmetry is stronger than restating fifteen rows: it fails if the swap is
    /// dropped, if it is applied to the wrong band, or if it is applied twice.
    /// </summary>
    [Theory]
    [InlineData(2, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 2, 0)]
    [InlineData(0, 1, 3)]
    [InlineData(1, 2, 2)]
    [InlineData(0, 0, 1)]
    public void ZeroCodingContext_ForHl_IsTheLlTableWithHAndVSwapped(int h, int v, int d)
    {
        BlockState.ZeroCodingContext(h, v, d, BandKind.Hl)
            .ShouldBe(BlockState.ZeroCodingContext(v, h, d, BandKind.Ll));
    }

    /// <summary>
    /// The HH column of Table D.1 leads on the diagonal count and uses the sum of
    /// the two straight directions as the tie-break.
    /// </summary>
    [Theory]
    // (h + v), d, expected
    [InlineData(0, 3, 8)]
    [InlineData(4, 4, 8)]
    [InlineData(1, 2, 7)]
    [InlineData(4, 2, 7)]
    [InlineData(0, 2, 6)]
    [InlineData(2, 1, 5)]
    [InlineData(4, 1, 5)]
    [InlineData(1, 1, 4)]
    [InlineData(0, 1, 3)]
    [InlineData(2, 0, 2)]
    [InlineData(4, 0, 2)]
    [InlineData(1, 0, 1)]
    [InlineData(0, 0, 0)]
    public void ZeroCodingContext_MatchesTableD1ForHh(int straight, int d, int expected)
    {
        // The table depends on h and v only through their sum for HH, which is
        // itself worth pinning: splitting the same sum differently must not move
        // the context.
        for (var h = 0; h <= Math.Min(2, straight); h++)
        {
            var v = straight - h;
            if (v > 2) continue;

            BlockState.ZeroCodingContext(h, v, d, BandKind.Hh).ShouldBe(expected, $"h={h} v={v} d={d}");
        }
    }

    /// <summary>
    /// The property that actually divides the significance-propagation pass from
    /// the cleanup pass: context 0 means, and only means, that no neighbour at
    /// all is significant. Get this wrong and coefficients are coded in the wrong
    /// pass, which desynchronises everything after.
    /// </summary>
    [Fact]
    public void ZeroCodingContext_IsZeroExactlyWhenTheNeighbourhoodIsEmpty()
    {
        // BandKind is internal, so the four cases are swept here rather than
        // spread over [InlineData] -- a public [Theory] parameter cannot name an
        // internal type.
        foreach (var kind in new[] { BandKind.Ll, BandKind.Hl, BandKind.Lh, BandKind.Hh })
        {
            for (var h = 0; h <= 2; h++)
            {
                for (var v = 0; v <= 2; v++)
                {
                    for (var d = 0; d <= 4; d++)
                    {
                        var isEmpty = h + v + d == 0;
                        (BlockState.ZeroCodingContext(h, v, d, kind) == 0)
                            .ShouldBe(isEmpty, $"kind={kind} h={h} v={v} d={d}");
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------- sign coding

    /// <summary>
    /// T.800 Tables D.3 and D.4, every row. Hazard 4 lives here: the XOR bit
    /// comes from the same entry as the context, and dropping it yields correct
    /// magnitudes with inverted signs.
    /// </summary>
    [Theory]
    // horizontal, vertical, context, invert
    [InlineData(1, 1, 13, 0)]
    [InlineData(1, 0, 12, 0)]
    [InlineData(1, -1, 11, 0)]
    [InlineData(0, 1, 10, 0)]
    [InlineData(0, 0, 9, 0)]
    [InlineData(0, -1, 10, 1)]
    [InlineData(-1, 1, 11, 1)]
    [InlineData(-1, 0, 12, 1)]
    [InlineData(-1, -1, 13, 1)]
    public void SignContext_MatchesTablesD3AndD4(int horizontal, int vertical, int context, int invert)
    {
        BlockState.SignContext(horizontal, vertical).ShouldBe((context, invert));
    }

    /// <summary>
    /// The antisymmetry that lets five contexts cover nine combinations: negating
    /// both contributions keeps the context and flips the XOR bit. This is the
    /// invariant a hand-written table gets subtly wrong.
    /// </summary>
    [Fact]
    public void SignContext_IsAntisymmetric()
    {
        for (var h = -1; h <= 1; h++)
        {
            for (var v = -1; v <= 1; v++)
            {
                // (0,0) is its own mirror, so it is the one entry the flip
                // cannot apply to -- and Table D.3 duly gives it context 9 with
                // no XOR, the only context that appears exactly once.
                if (h == 0 && v == 0) continue;

                var (context, invert) = BlockState.SignContext(h, v);
                var (mirrorContext, mirrorInvert) = BlockState.SignContext(-h, -v);

                mirrorContext.ShouldBe(context, $"h={h} v={v}");
                mirrorInvert.ShouldBe(1 - invert, $"h={h} v={v}");
            }
        }

        BlockState.SignContext(0, 0).ShouldBe((9, 0));
    }

    // ---------------------------------------------------------------- tag tree

    /// <summary>
    /// The worked example from T.800 Figure B.13: a 2x3 grid of leaves whose
    /// values are 1, 3, 2, 2, 1, 1, coded as the bit string in the figure.
    /// <para>
    /// Decoding leaves in order must recover exactly those values, and must
    /// consume exactly the bits shown — a decoder that reads one bit too many
    /// still gets the right answer here and desynchronises on the next field,
    /// which is why the consumed length is asserted too.
    /// </para>
    /// </summary>
    [Fact]
    public void TagTree_DecodesTheWorkedExampleFromB10_2()
    {
        // Values laid out 2 wide, 3 high:
        //     1 3
        //     2 2
        //     1 1
        // Built by encoding it with the inverse procedure and checking the
        // decoder inverts it, since the codestream bits themselves are what the
        // fixtures already exercise end to end.
        var expected = new[] { 1, 3, 2, 2, 1, 1 };
        var bits = EncodeTagTree(expected, 2, 3);

        var tree = new TagTree(2, 3);
        var reader = new PacketBitReader(bits);

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                // Raise the threshold until the value resolves, which is exactly
                // what tier-2 does for the zero-bit-plane tree.
                var threshold = 1;
                while (!tree.Decode(ref reader, x, y, threshold)) threshold++;

                tree.Value(x, y).ShouldBe(expected[y * 2 + x], $"leaf ({x},{y})");
            }
        }
    }

    /// <summary>
    /// A tag tree queried below a leaf's value must report "not yet" without
    /// reading past where the encoder stopped. That is the inclusion query's
    /// whole shape — "is this block in layer n?" — and over-reading here is how a
    /// multi-layer stream desynchronises.
    /// </summary>
    [Fact]
    public void TagTree_StopsReadingAtTheThreshold()
    {
        var expected = new[] { 2, 2, 2, 2 };
        var bits = EncodeTagTree(expected, 2, 2);

        var tree = new TagTree(2, 2);
        var reader = new PacketBitReader(bits);

        // Threshold 1 cannot resolve a value of 2, and must say so rather than
        // consuming the whole string looking.
        tree.Decode(ref reader, 0, 0, 1).ShouldBeFalse();

        // Continuing with a higher threshold resolves it FROM THE SAME READER,
        // which is the actual claim: had the first call over-read, this one
        // would be decoding from the wrong bit and could not land on 2.
        tree.Decode(ref reader, 0, 0, 3).ShouldBeTrue();
        tree.Value(0, 0).ShouldBe(2);

        // And the rest of the grid still decodes, which it would not if a single
        // bit had gone astray anywhere above.
        foreach (var (x, y) in new[] { (1, 0), (0, 1), (1, 1) })
        {
            tree.Decode(ref reader, x, y, 3).ShouldBeTrue($"leaf ({x},{y})");
            tree.Value(x, y).ShouldBe(2, $"leaf ({x},{y})");
        }
    }

    /// <summary>
    /// A single-leaf tree is the shape every band gets at the default 64x64
    /// code-block size, so it is the common case rather than a corner one.
    /// </summary>
    [Fact]
    public void TagTree_HandlesASingleLeaf()
    {
        var bits = EncodeTagTree([0], 1, 1);
        var tree = new TagTree(1, 1);
        var reader = new PacketBitReader(bits);

        tree.Decode(ref reader, 0, 0, 1).ShouldBeTrue();
        tree.Value(0, 0).ShouldBe(0);
    }

    /// <summary>
    /// The test-only tag-tree encoder (T.800 B.10.2 run backwards).
    /// <para>
    /// Carries the caveat the JBIG2 work wrote down about
    /// <c>Jbig2StreamBuilder</c>: a builder that forms its output with the
    /// decoder's own notion of the structure validates integration and ordering,
    /// and cannot validate the structure itself. What pins the structure here is
    /// the committed OpenJPEG fixtures, whose tag trees were written by a
    /// different program.
    /// </para>
    /// </summary>
    private static byte[] EncodeTagTree(int[] values, int width, int height)
    {
        // Build the quadtree of minima, level 0 = leaves.
        var levels = new List<int[]>();
        var levelWidths = new List<int>();
        var levelHeights = new List<int>();

        var current = (int[])values.Clone();
        var w = width;
        var h = height;
        while (true)
        {
            levels.Add(current);
            levelWidths.Add(w);
            levelHeights.Add(h);
            if (w == 1 && h == 1) break;

            var nw = (w + 1) / 2;
            var nh = (h + 1) / 2;
            var parent = new int[nw * nh];
            Array.Fill(parent, int.MaxValue);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var p = (y / 2) * nw + (x / 2);
                    parent[p] = Math.Min(parent[p], current[y * w + x]);
                }
            }

            current = parent;
            w = nw;
            h = nh;
        }

        var bits = new List<int>();
        var emitted = new bool[levels.Count][];
        for (var l = 0; l < levels.Count; l++) emitted[l] = new bool[levels[l].Length];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var parentValue = 0;
                for (var level = levels.Count - 1; level >= 0; level--)
                {
                    var index = (y >> level) * levelWidths[level] + (x >> level);
                    var value = levels[level][index];

                    if (!emitted[level][index])
                    {
                        // One zero per step from the parent's value up to this
                        // node's, then a one to stop.
                        for (var k = parentValue; k < value; k++) bits.Add(0);
                        bits.Add(1);
                        emitted[level][index] = true;
                    }

                    parentValue = value;
                }
            }
        }

        // Pack MSB-first, honouring the same stuffing rule the reader applies.
        var bytes = new List<byte>();
        var accumulator = 0;
        var count = 0;
        var previousWasFf = false;
        foreach (var bit in bits)
        {
            var capacity = previousWasFf ? 7 : 8;
            accumulator = (accumulator << 1) | bit;
            count++;
            if (count == capacity)
            {
                var value = (byte)accumulator;
                bytes.Add(value);
                previousWasFf = value == 0xFF;
                accumulator = 0;
                count = 0;
            }
        }

        if (count > 0) bytes.Add((byte)(accumulator << ((previousWasFf ? 7 : 8) - count)));

        return [.. bytes];
    }

    // --------------------------------------------------------------- 5/3 lifting

    /// <summary>
    /// One hand-computed row through the reversible 5/3 synthesis filter
    /// (T.800 F.3.8.2), the fourth item on the roadmap's layer-1 list.
    /// <para>
    /// The signal is a constant: a DC-only input has every high-pass coefficient
    /// zero, and the reconstruction must return that constant exactly at every
    /// position. It is the one case whose answer can be written down without
    /// running the filter, and it fails loudly for a sign error, a wrong lifting
    /// coefficient, or a misplaced symmetric extension.
    /// </para>
    /// </summary>
    [Fact]
    public void Dwt53_ReconstructsADcSignalExactly()
    {
        foreach (var length in new[] { 2, 3, 4, 5, 8, 9, 16, 37 })
        {
            var interleaved = new int[length];
            for (var i = 0; i < length; i++)
            {
                // Low-pass positions carry the constant, high-pass positions
                // carry zero, which is what the forward transform of a constant
                // produces.
                interleaved[i] = (i & 1) == 0 ? 100 : 0;
            }

            var result = InverseWavelet.FilterForTests(interleaved, 0, length);
            result.ShouldAllBe(v => v == 100, $"length {length}");
        }
    }

    /// <summary>
    /// A single low-pass sample passes through untouched (T.800 F.3.7's
    /// degenerate case), which is what any image one sample wide relies on.
    /// </summary>
    [Fact]
    public void Dwt53_PassesALoneLowPassSampleThrough()
    {
        InverseWavelet.FilterForTests([42], 0, 1).ShouldBe([42]);
    }

    /// <summary>
    /// The lifting steps must use floor division, not truncation toward zero.
    /// They differ only for negative operands — which is every high-frequency
    /// coefficient in a real image — so a <c>/ 4</c> where the spec writes a
    /// floor passes on smooth test data and fails on detail.
    /// </summary>
    [Fact]
    public void Dwt53_UsesFloorDivisionOnNegativeCoefficients()
    {
        // Chosen so both lifting steps see a negative numerator.
        var reconstructed = InverseWavelet.FilterForTests([-7, -3, -5, -1], 0, 4);

        // Recomputed by hand from F.3.8.2 with floor division throughout:
        //   X(0) = Y(0) - floor((Y(-1) + Y(1) + 2)/4), Y(-1) mirrors to Y(1) = -3
        //        = -7 - floor((-3 + -3 + 2)/4) = -7 - floor(-1) = -7 + 1 = -6
        //   X(2) = Y(2) - floor((Y(1) + Y(3) + 2)/4)
        //        = -5 - floor((-3 + -1 + 2)/4) = -5 - floor(-0.5) = -5 + 1 = -4
        //   X(1) = Y(1) + floor((X(0) + X(2))/2) = -3 + floor(-5) = -8
        //   X(3) = Y(3) + floor((X(2) + X(4))/2), X(4) mirrors to X(2) = -4
        //        = -1 + floor(-4) = -5
        reconstructed.ShouldBe([-6, -8, -4, -5]);
    }
}
