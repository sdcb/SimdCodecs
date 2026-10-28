using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// Tier-1: the EBCOT block decoder of T.800 Annex D. It turns one code-block's
/// coded bytes into signed coefficients, bit-plane by bit-plane, through three
/// coding passes driven by the MQ arithmetic decoder.
/// <para>
/// Two things here are worth reading before changing anything.
/// </para>
/// <para>
/// <b>The scan is not raster.</b> D.2: the block is walked in horizontal stripes
/// four rows tall, and within a stripe column by column, top to bottom in each
/// column. Writing a raster scan produces output that looks like structured
/// noise, which reads as a context-modelling bug and is not one.
/// </para>
/// <para>
/// <b>The contexts are indices, not values.</b> Every adaptive slot starts in the
/// same state bar three, so permuting the context <em>numbers</em> is a bijection
/// that changes nothing — the JBIG2 work established this by measurement, where
/// swapping two template bits left the reference decoder still agreeing. What
/// matters is <em>which neighbours a context reads</em>. So the tests for this
/// file pin the neighbourhood, not the number; and the numbering is nonetheless
/// written to match T.800's tables exactly, because the three exceptional initial
/// states (Table D.7) name specific contexts and a permutation would seat them on
/// the wrong ones.
/// </para>
/// </summary>
internal static class BlockDecoder
{
    // T.800 Table D.7. Nineteen adaptive contexts: nine for zero coding, five
    // for sign coding, three for magnitude refinement, plus run-length and
    // uniform.
    private const int ContextCount = 19;
    private const int RunLengthContext = 17;
    private const int UniformContext = 18;

    /// <summary>
    /// Decodes one code-block into <paramref name="band"/>'s coefficient array.
    /// </summary>
    /// <param name="band">The subband the block belongs to; its orientation picks the context table.</param>
    /// <param name="block">The block, with its inclusion state and coded byte ranges already read by tier-2.</param>
    /// <param name="tilePartData">The tile-part's coded data, which the block's segments index into.</param>
    /// <param name="state">Scratch for the decode, shared by every block of one image so blocks do not allocate their own.</param>
    public static void Decode(Subband band, CodeBlock block, ReadOnlySpan<byte> tilePartData, BlockState state)
    {
        var width = block.Bounds.Width;
        var height = block.Bounds.Height;
        if (width == 0 || height == 0 || block.PassCount == 0) return;

        // How many magnitude bit-planes actually carry data: the band's Mb, less
        // the leading all-zero planes the packet header declared.
        var planes = band.MagnitudeBits - block.ZeroBitPlanes;
        if (planes <= 0) return;

        // One segment, which is every block of a one-layer codestream and many of a
        // layered one, is read where it lies. The MQ decoder treats the end of its
        // span as the end of the codeword, so a slice is exactly the right input.
        byte[]? gathered = null;
        var coded = block.Segments.Count == 1
            ? tilePartData.Slice(block.Segments[0].Start, block.Segments[0].Length)
            : Gather(block, tilePartData, out gathered);

        try
        {
            DecodePasses(band, block, coded, state, width, height, planes);
        }
        finally
        {
            if (gathered is not null) ArrayPool<byte>.Shared.Return(gathered);
        }
    }

    private static void DecodePasses(
        Subband band, CodeBlock block, ReadOnlySpan<byte> coded, BlockState state, int width, int height, int planes)
    {
        state.Reset(width, height, band.Kind);
        var mq = new MqDecoder(coded);

        Span<byte> contexts = stackalloc byte[ContextCount];
        contexts.Clear();

        // The three states T.800 Table D.7 seeds away from zero. This is the
        // roadmap's hazard 1: the coder is shared with JBIG2 but its
        // initialisation is not, and getting it wrong decodes the first
        // code-block plausibly and then drifts.
        contexts[0] = 4 << 1;                    // zero coding, all-insignificant neighbourhood
        contexts[RunLengthContext] = 3 << 1;
        contexts[UniformContext] = 46 << 1;

        var passesLeft = block.PassCount;
        var plane = planes - 1;

        // The first pass of a code-block is always a cleanup pass: there is
        // nothing significant yet for a significance-propagation pass to
        // propagate from, and nothing to refine.
        state.CleanupPass(ref mq, contexts, plane);
        passesLeft--;
        plane--;

        while (plane >= 0 && passesLeft > 0)
        {
            state.SignificancePropagationPass(ref mq, contexts, plane);
            if (--passesLeft == 0) break;

            state.MagnitudeRefinementPass(ref mq, contexts, plane);
            if (--passesLeft == 0) break;

            state.CleanupPass(ref mq, contexts, plane);
            passesLeft--;
            plane--;
        }

        state.WriteTo(band, block);
    }

    /// <summary>
    /// Concatenates the block's coded segments.
    /// <para>
    /// A block gets one segment from each quality layer that adds passes to it.
    /// With no code-block style flags the encoder codes all of a block's passes as
    /// ONE MQ codeword, terminated once at the end, and the layers only cut that
    /// codeword at pass boundaries — so the segments are read back as the single
    /// run of bytes they were cut from. (Termination on every pass, A.19's
    /// <c>TERMALL</c>, would make each segment its own codeword and this a
    /// different function; the reader refuses that flag.)
    /// </para>
    /// </summary>
    private static ReadOnlySpan<byte> Gather(CodeBlock block, ReadOnlySpan<byte> tilePartData, out byte[] rented)
    {
        var total = 0;
        foreach (var (_, length) in block.Segments) total += length;

        rented = ArrayPool<byte>.Shared.Rent(total);
        var offset = 0;
        foreach (var (start, length) in block.Segments)
        {
            tilePartData.Slice(start, length).CopyTo(rented.AsSpan(offset));
            offset += length;
        }

        // Exactly the gathered bytes: a rented array is usually longer, and the MQ decoder must see
        // the codeword end where it ends, not at whatever the pool left after it.
        return rented.AsSpan(0, total);
    }
}

/// <summary>
/// The per-coefficient state one code-block decode carries: significance, sign,
/// accumulated magnitude, and the two flags that sequence the passes.
/// <para>
/// <b>Everything a context needs is in one flags word per coefficient.</b> Each
/// word holds the coefficient's own state and, kept up to date as the passes run,
/// which of its eight neighbours are significant and the signs of the four straight
/// ones. When a coefficient turns significant it marks itself in its neighbours'
/// words, once, so every later context is one table lookup rather than eight
/// neighbour reads. The tables are built from <see cref="ZeroCodingContext(int,int,int,BandKind)"/>
/// and <see cref="SignContext(int,int)"/>, which stay the statement of T.800's
/// tables that the tests check: the lookup is a cache of those functions, not a
/// second transcription of the spec. Reading the eight neighbours afresh for every
/// coefficient on every pass was most of tier-1's time.
/// </para>
/// <para>
/// The arrays are padded by one on each side so that a coefficient's eight
/// neighbours exist without a bounds test. A padding cell is never coded and never
/// significant itself, which is exactly what T.800 D.3 requires of positions
/// outside the code-block, rather than a convenient fiction; it only collects the
/// neighbour bits its block neighbours set, which nothing reads.
/// </para>
/// <para>
/// One instance serves a whole decode: <see cref="Reset"/> clears the part a block
/// uses, so code-blocks do not each allocate their own, which they used to, at
/// about 40 KB a 64x64 block.
/// </para>
/// </summary>
internal sealed class BlockState
{
    // Neighbour significance, named by where the neighbour is.
    private const int SignificantNorth = 1 << 0;
    private const int SignificantSouth = 1 << 1;
    private const int SignificantWest = 1 << 2;
    private const int SignificantEast = 1 << 3;
    private const int SignificantNorthWest = 1 << 4;
    private const int SignificantNorthEast = 1 << 5;
    private const int SignificantSouthWest = 1 << 6;
    private const int SignificantSouthEast = 1 << 7;
    private const int NeighbourMask = 0xFF;

    // The straight neighbours' signs, meaningful where the matching bit above is set.
    private const int NegativeNorth = 1 << 8;
    private const int NegativeSouth = 1 << 9;
    private const int NegativeWest = 1 << 10;
    private const int NegativeEast = 1 << 11;

    // The coefficient's own state.
    private const int Significant = 1 << 12;
    private const int Visited = 1 << 13;
    private const int Refined = 1 << 14;
    private const int Negative = 1 << 15;

    /// <summary>The zero-coding context for every neighbourhood, one table per band orientation.</summary>
    private static readonly byte[][] ZeroCodingTables = BuildZeroCodingTables();

    /// <summary>
    /// The sign context and XOR bit, as <c>context &lt;&lt; 1 | invert</c>, for every combination of
    /// the straight neighbours' significance (low nibble) and signs (high nibble).
    /// </summary>
    private static readonly byte[] SignTable = BuildSignTable();

    private int _width;
    private int _height;
    private int _stride;
    private byte[] _zeroCoding = ZeroCodingTables[0];

    private ushort[] _flags = [];
    private int[] _magnitude = [];

    /// <summary>
    /// The lowest bit-plane whose bit is known for each significant coefficient: where it became
    /// significant, then each plane that refined it. Below it the magnitude is unknown, because the
    /// codestream was cut there, and <see cref="WriteTo"/> reconstructs it at the middle of what
    /// is unknown rather than at the bottom.
    /// </summary>
    private byte[] _lowestPlane = [];

    /// <summary>Readies the state for a <paramref name="width"/> x <paramref name="height"/> block of one orientation.</summary>
    public void Reset(int width, int height, BandKind kind)
    {
        _width = width;
        _height = height;
        _stride = width + 2;
        _zeroCoding = ZeroCodingTables[(int)kind];

        var cells = _stride * (height + 2);
        if (_flags.Length < cells)
        {
            _flags = new ushort[cells];
            _magnitude = new int[cells];
            _lowestPlane = new byte[cells];
            return;
        }

        _flags.AsSpan(0, cells).Clear();
        _magnitude.AsSpan(0, cells).Clear();
        _lowestPlane.AsSpan(0, cells).Clear();
    }

    private int Index(int x, int y) => (y + 1) * _stride + (x + 1);

    /// <summary>
    /// D.3.1: code every insignificant coefficient that has at least one
    /// significant neighbour, and remember which ones were visited so the other
    /// two passes skip them.
    /// </summary>
    public void SignificancePropagationPass(ref MqDecoder decoder, scoped Span<byte> contexts, int plane)
    {
        // Decoded through a local copy, written back at the end, so the coder's registers can
        // live in machine registers for the whole pass rather than behind a reference.
        var mq = decoder;
        var flags = _flags;
        for (var stripe = 0; stripe < _height; stripe += 4)
        {
            var stripeEnd = Math.Min(stripe + 4, _height);
            for (var x = 0; x < _width; x++)
            {
                for (var y = stripe; y < stripeEnd; y++)
                {
                    var i = Index(x, y);
                    var f = flags[i];

                    // No significant neighbour means zero-coding context 0, and such a
                    // coefficient is not coded in this pass — it is left to the cleanup
                    // pass. This test is what divides the two passes.
                    if ((f & Significant) != 0 || (f & NeighbourMask) == 0) continue;

                    if (mq.Decode(contexts, _zeroCoding[f & NeighbourMask]) != 0)
                        MakeSignificant(ref mq, contexts, i, plane);
                    flags[i] |= Visited;
                }
            }
        }

        decoder = mq;
    }

    /// <summary>
    /// D.3.3: refine every coefficient that was already significant when this
    /// bit-plane began. One that became significant during this plane's
    /// significance-propagation pass is skipped — its bit for this plane is
    /// already the one that made it significant.
    /// </summary>
    public void MagnitudeRefinementPass(ref MqDecoder decoder, scoped Span<byte> contexts, int plane)
    {
        // Decoded through a local copy, written back at the end, so the coder's registers can
        // live in machine registers for the whole pass rather than behind a reference.
        var mq = decoder;
        var flags = _flags;
        for (var stripe = 0; stripe < _height; stripe += 4)
        {
            var stripeEnd = Math.Min(stripe + 4, _height);
            for (var x = 0; x < _width; x++)
            {
                for (var y = stripe; y < stripeEnd; y++)
                {
                    var i = Index(x, y);
                    var f = flags[i];
                    if ((f & (Significant | Visited)) != Significant) continue;

                    // Table D.2: the first refinement of a coefficient is coded
                    // against whether its neighbourhood is quiet; every later one
                    // shares a single context.
                    var context = (f & Refined) != 0 ? 16 : (f & NeighbourMask) != 0 ? 15 : 14;

                    if (mq.Decode(contexts, context) != 0) _magnitude[i] |= 1 << plane;
                    flags[i] = (ushort)(f | Refined);
                    _lowestPlane[i] = (byte)plane;
                }
            }
        }

        decoder = mq;
    }

    /// <summary>
    /// D.3.4: code everything the first two passes left, with the run-length
    /// shortcut for a whole column of four that is quiet in every direction.
    /// </summary>
    public void CleanupPass(ref MqDecoder decoder, scoped Span<byte> contexts, int plane)
    {
        // Decoded through a local copy, written back at the end, so the coder's registers can
        // live in machine registers for the whole pass rather than behind a reference.
        var mq = decoder;
        var flags = _flags;
        var stride = _stride;
        for (var stripe = 0; stripe < _height; stripe += 4)
        {
            var full = stripe + 4 <= _height;
            var stripeEnd = Math.Min(stripe + 4, _height);

            for (var x = 0; x < _width; x++)
            {
                var y = stripe;

                // The run-length mode of D.3.4 applies only to a complete column
                // of four in which nothing is significant, nothing was coded in
                // the significance pass, and no coefficient has a significant
                // neighbour. One symbol then stands for all four.
                if (full)
                {
                    var top = Index(x, stripe);
                    var column = flags[top] | flags[top + stride] | flags[top + 2 * stride] | flags[top + 3 * stride];
                    if ((column & (NeighbourMask | Significant | Visited)) == 0)
                    {
                        if (mq.Decode(contexts, 17) == 0) continue;

                        // Two bits, most significant first, in the uniform context:
                        // which of the four is the first significant one.
                        var first = (mq.Decode(contexts, 18) << 1) | mq.Decode(contexts, 18);
                        y = stripe + first;
                        MakeSignificant(ref mq, contexts, Index(x, y), plane);
                        y++;
                    }
                }

                for (; y < stripeEnd; y++)
                {
                    var i = Index(x, y);
                    var f = flags[i];
                    if ((f & (Visited | Significant)) != 0) continue;

                    if (mq.Decode(contexts, _zeroCoding[f & NeighbourMask]) != 0)
                        MakeSignificant(ref mq, contexts, i, plane);
                }
            }
        }

        // The visited flags scope to one bit-plane, and the cleanup pass is where
        // a bit-plane ends.
        for (var y = 0; y < _height; y++)
        {
            var row = flags.AsSpan(Index(0, y), _width);
            for (var x = 0; x < row.Length; x++) row[x] &= unchecked((ushort)~Visited);
        }

        decoder = mq;
    }

    /// <summary>
    /// A coefficient has just been found significant at <paramref name="plane"/>:
    /// set the bit, decode its sign, and tell its neighbours.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MakeSignificant(ref MqDecoder mq, scoped Span<byte> contexts, int i, int plane)
    {
        _magnitude[i] |= 1 << plane;
        _lowestPlane[i] = (byte)plane;

        var flags = _flags;
        var f = flags[i];

        // Hazard 4: the sign is the decoded bit XOR a bit from the SAME table
        // entry. Dropping the XOR yields correct magnitudes with wrong signs,
        // which is invisible on detail and unmistakable on a smooth gradient —
        // hence the ramp fixture.
        var entry = SignTable[(f & 0x0F) | ((f >> 4) & 0xF0)];
        var negative = (mq.Decode(contexts, entry >> 1) ^ (entry & 1)) != 0;

        flags[i] = (ushort)(f | Significant | (negative ? Negative : 0));

        // Each neighbour records this coefficient under the direction it lies in FROM that
        // neighbour: the one to the north sees it to its south, and so on.
        var s = _stride;
        flags[i - s] |= (ushort)(SignificantSouth | (negative ? NegativeSouth : 0));
        flags[i + s] |= (ushort)(SignificantNorth | (negative ? NegativeNorth : 0));
        flags[i - 1] |= (ushort)(SignificantEast | (negative ? NegativeEast : 0));
        flags[i + 1] |= (ushort)(SignificantWest | (negative ? NegativeWest : 0));
        flags[i - s - 1] |= SignificantSouthEast;
        flags[i - s + 1] |= SignificantSouthWest;
        flags[i + s - 1] |= SignificantNorthEast;
        flags[i + s + 1] |= SignificantNorthWest;
    }

    /// <summary>
    /// T.800 Table D.1: the zero-coding context, from how many of the eight
    /// neighbours are significant, split by direction.
    /// <para>
    /// The three column groups of the table are one function of (horizontal,
    /// vertical, diagonal) applied three ways: LL and LH read it as written, HL
    /// reads it with the horizontal and vertical counts <em>interchanged</em>, and
    /// HH has its own ordering that leads on the diagonals. Writing it as a
    /// decision rather than a 256-entry lookup keeps it checkable line by line
    /// against the published table; the lookup the passes use is built from it.
    /// </para>
    /// </summary>
    internal static int ZeroCodingContext(int horizontal, int vertical, int diagonal, BandKind kind)
    {
        if (kind == BandKind.Hh)
        {
            var straight = horizontal + vertical;
            if (diagonal >= 3) return 8;
            if (diagonal == 2) return straight >= 1 ? 7 : 6;
            if (diagonal == 1) return straight >= 2 ? 5 : straight == 1 ? 4 : 3;
            return straight >= 2 ? 2 : straight;
        }

        // HL is the LL/LH table read with the two straight directions swapped.
        if (kind == BandKind.Hl) (horizontal, vertical) = (vertical, horizontal);

        if (horizontal == 2) return 8;
        if (horizontal == 1) return vertical >= 1 ? 7 : diagonal >= 1 ? 6 : 5;
        if (vertical == 2) return 4;
        if (vertical == 1) return 3;
        return diagonal >= 2 ? 2 : diagonal;
    }

    /// <summary>
    /// T.800 Tables D.3 and D.4: the sign context and the XOR bit that goes with
    /// it, from the two horizontal and two vertical neighbours' signed
    /// contributions, each clamped to -1..1.
    /// </summary>
    internal static (int Context, int Invert) SignContext(int horizontal, int vertical)
    {
        // The table is antisymmetric: negating both contributions keeps the
        // context and flips the XOR bit, which is how five contexts cover nine
        // combinations.
        if (horizontal < 0) return (SignContext(-horizontal, -vertical).Context, 1);

        if (horizontal > 0)
            return vertical switch { > 0 => (13, 0), 0 => (12, 0), _ => (11, 0) };

        return vertical switch { > 0 => (10, 0), 0 => (9, 0), _ => (10, 1) };
    }

    private static byte[][] BuildZeroCodingTables()
    {
        var tables = new byte[4][];
        for (var kind = 0; kind < 4; kind++)
        {
            var table = new byte[256];
            for (var bits = 0; bits < 256; bits++)
            {
                var horizontal = Count(bits, SignificantWest | SignificantEast);
                var vertical = Count(bits, SignificantNorth | SignificantSouth);
                var diagonal = Count(bits,
                    SignificantNorthWest | SignificantNorthEast | SignificantSouthWest | SignificantSouthEast);
                table[bits] = (byte)ZeroCodingContext(horizontal, vertical, diagonal, (BandKind)kind);
            }

            tables[kind] = table;
        }

        return tables;
    }

    private static byte[] BuildSignTable()
    {
        // Low nibble: N, S, W, E significant. High nibble: the same four, negative.
        var table = new byte[256];
        for (var key = 0; key < 256; key++)
        {
            int Contribution(int significant, int negative) =>
                (key & significant) == 0 ? 0 : (key & negative) != 0 ? -1 : 1;

            var horizontal = Contribution(SignificantWest, NegativeWest >> 4) +
                             Contribution(SignificantEast, NegativeEast >> 4);
            var vertical = Contribution(SignificantNorth, NegativeNorth >> 4) +
                           Contribution(SignificantSouth, NegativeSouth >> 4);

            var (context, invert) = SignContext(Math.Clamp(horizontal, -1, 1), Math.Clamp(vertical, -1, 1));
            table[key] = (byte)((context << 1) | invert);
        }

        return table;
    }

    private static int Count(int bits, int mask) => System.Numerics.BitOperations.PopCount((uint)(bits & mask));

    /// <summary>
    /// Copies the decoded magnitudes and signs into the subband's coefficient array.
    /// <para>
    /// A coefficient whose lowest bits were never decoded, because a quality layer cut the
    /// code-block's passes short, is reconstructed by T.800 E.1.1.2 with r = 1/2: half of the
    /// unknown range is added to what was decoded, so the value lands in the middle of the interval
    /// the decoded bits leave open rather than at its floor. With every bit-plane decoded the half is
    /// below the last bit and rounds away, so a lossless codestream still decodes exactly. Without
    /// it, a lossy 5/3 codestream decodes a sample or two off OpenJPEG's, which reconstructs the
    /// same way — one of the four reversible images measured in real PDFs did, by 1 in 1,007
    /// samples.
    /// </para>
    /// </summary>
    public void WriteTo(Subband band, CodeBlock block)
    {
        var bandWidth = band.Bounds.Width;
        var offsetX = block.Bounds.X0 - band.Bounds.X0;
        var offsetY = block.Bounds.Y0 - band.Bounds.Y0;

        if (band.Irreversible)
        {
            WriteDequantized(band, bandWidth, offsetX, offsetY);
            return;
        }

        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var i = Index(x, y);
                var magnitude = _magnitude[i];
                if (magnitude == 0) continue;

                var lowest = _lowestPlane[i];
                if (lowest > 0) magnitude += 1 << (lowest - 1);

                band.Coefficients[(offsetY + y) * bandWidth + offsetX + x] =
                    (_flags[i] & Negative) != 0 ? -magnitude : magnitude;
            }
        }
    }

    /// <summary>
    /// The irreversible counterpart of <see cref="WriteTo"/>: T.800 Equation E-6 with r = 1/2,
    /// <c>(q + r * 2^(Mb - Nb)) * Delta_b</c>. Here the half is added even when every bit-plane was
    /// decoded, because a quantization index stands for an interval of width Delta_b, not for its
    /// lower edge, and the middle of it is the best guess. On the reversible path the same half
    /// falls below the last bit and rounds away.
    /// </summary>
    private void WriteDequantized(Subband band, int bandWidth, int offsetX, int offsetY)
    {
        var step = band.StepSize;

        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var i = Index(x, y);
                var magnitude = _magnitude[i];
                if (magnitude == 0) continue;

                var value = (magnitude + MathF.ScaleB(0.5f, _lowestPlane[i])) * step;
                band.Values[(offsetY + y) * bandWidth + offsetX + x] = (_flags[i] & Negative) != 0 ? -value : value;
            }
        }
    }
}
