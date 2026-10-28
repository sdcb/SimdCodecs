using System;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// Generic region decoding procedure — ITU-T T.88 §6.2, arithmetic variant
/// (<c>MMR = 0</c>). Reconstructs a bilevel bitmap one pixel at a time, each
/// decision taken against a context formed from already-decoded neighbours.
/// <para>
/// The context templates are T.88 Figures 4-7 (GBTEMPLATE 0-3). Bit numbering
/// follows those figures: the template cells are numbered in raster order over
/// the <em>nominal</em> layout, MSB first, and each adaptive pixel keeps the bit
/// position of the nominal cell it replaces however far its AT offset moves it.
/// </para>
/// <para>
/// What conformance actually turns on is <b>which pixels a template reads</b> —
/// the set, AT offsets included — not which bit each one lands in. A context
/// value is only an index into adaptive-state slots that all start identical, so
/// permuting the bit positions is a bijection over context values: it preserves
/// which pixels share a slot, and the coded bytes come out unchanged. Verified
/// against jbig2dec, which keeps agreeing with us when two template bits are
/// swapped and disagrees immediately when one template pixel's <em>coordinate</em>
/// changes.
/// </para>
/// <para>
/// The numbering is still written to match the figures exactly, for one real
/// reason beyond tidiness: TPGDON's SLTP decision uses a <em>hard-coded</em>
/// context (0x9B25 and friends below). Those constants name a specific
/// neighbourhood in the spec's numbering, so permuting the real contexts changes
/// which neighbourhood collides with the SLTP slot — a genuine conformance
/// difference, even though it only surfaces on images where that particular
/// neighbourhood occurs.
/// </para>
/// <para>
/// This is the straightforward per-pixel formulation. It re-reads every template
/// neighbour for every pixel rather than sliding the context along the row; a
/// nominal-AT fast path is worthwhile future work, but correctness against the
/// figures comes first and the slow form is the one that can be read against
/// them.
/// </para>
/// </summary>
internal static class GenericRegionDecoder
{
    /// <summary>Nominal AT pixel offsets for GBTEMPLATE 0 (T.88 §6.2.5.3): A1..A4 as (x,y) pairs.</summary>
    public static ReadOnlySpan<sbyte> NominalAtTemplate0 => [3, -1, -3, -1, 2, -2, -2, -2];

    /// <summary>Nominal A1 offset for GBTEMPLATE 1.</summary>
    public static ReadOnlySpan<sbyte> NominalAtTemplate1 => [3, -1];

    /// <summary>Nominal A1 offset for GBTEMPLATE 2.</summary>
    public static ReadOnlySpan<sbyte> NominalAtTemplate2 => [2, -1];

    /// <summary>Nominal A1 offset for GBTEMPLATE 3.</summary>
    public static ReadOnlySpan<sbyte> NominalAtTemplate3 => [2, -1];

    // The pseudo-pixel context each template uses for its SLTP decision when
    // TPGDON is on (T.88 §6.2.5.7). These are fixed values, not neighbourhoods:
    // the SLTP bit shares the region's context array but occupies a slot the
    // real templates rarely reach.
    private static ReadOnlySpan<ushort> TypicalPredictionContexts => [0x9B25, 0x0795, 0x00E5, 0x0195];

    /// <summary>Number of context bits (hence the context array size) for each GBTEMPLATE.</summary>
    public static int ContextBits(int template) => template switch
    {
        0 => 16,
        1 => 13,
        2 or 3 => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "GBTEMPLATE must be 0..3."),
    };

    /// <summary>The nominal AT offsets for a template, used when a caller has none of its own.</summary>
    public static ReadOnlySpan<sbyte> NominalAt(int template) => template switch
    {
        0 => NominalAtTemplate0,
        1 => NominalAtTemplate1,
        2 => NominalAtTemplate2,
        3 => NominalAtTemplate3,
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "GBTEMPLATE must be 0..3."),
    };

    /// <summary>
    /// Decodes a generic region of <paramref name="width"/> x <paramref name="height"/>
    /// pixels out of <paramref name="mq"/>.
    /// </summary>
    /// <param name="mq">The region's arithmetic decoder, positioned at the start of its coded data.</param>
    /// <param name="contexts">
    /// Adaptive context storage, at least <c>1 &lt;&lt; ContextBits(template)</c> bytes. Supplied by
    /// the caller because T.88 lets a decoder retain contexts across segments.
    /// </param>
    /// <param name="width">Region width in pixels.</param>
    /// <param name="height">Region height in pixels.</param>
    /// <param name="template">GBTEMPLATE, 0..3.</param>
    /// <param name="typicalPrediction">TPGDON — enables the per-row "same as the row above" shortcut.</param>
    /// <param name="at">
    /// AT pixel offsets as (x,y) sbyte pairs: four pairs for GBTEMPLATE 0, one for 1..3.
    /// </param>
    /// <param name="budget">
    /// The decode's remaining pixel allowance, charged before anything is allocated.
    /// </param>
    public static Jbig2Bitmap Decode(
        ref MqDecoder mq,
        scoped Span<byte> contexts,
        int width,
        int height,
        int template,
        bool typicalPrediction,
        scoped ReadOnlySpan<sbyte> at,
        Jbig2PixelBudget budget)
    {
        var expectedAt = template == 0 ? 8 : 2;
        if (at.Length < expectedAt)
            throw new ArgumentException($"GBTEMPLATE {template} needs {expectedAt / 2} AT pixel(s).", nameof(at));
        if (contexts.Length < 1 << ContextBits(template))
            throw new ArgumentException("Context array too small for this GBTEMPLATE.", nameof(contexts));

        budget.Charge(width, height);
        var bitmap = new Jbig2Bitmap(width, height);
        var sltpContext = TypicalPredictionContexts[template];
        var ltp = 0;

        for (var y = 0; y < height; y++)
        {
            if (typicalPrediction)
            {
                // SLTP toggles LTP rather than setting it, so a run of identical
                // rows costs one bit at each end of the run and nothing between.
                if (mq.Decode(contexts, sltpContext) != 0) ltp ^= 1;

                if (ltp == 1)
                {
                    // "Typical" row: an exact copy of the one above, with no
                    // pixel decisions coded at all. Row 0 has nothing above it,
                    // so it stays white.
                    if (y > 0)
                        Array.Copy(bitmap.Data, (y - 1) * width, bitmap.Data, y * width, width);
                    continue;
                }
            }

            var row = y * width;
            for (var x = 0; x < width; x++)
                bitmap.Data[row + x] = (byte)mq.Decode(contexts, Context(bitmap, x, y, template, at));
        }

        return bitmap;
    }

    /// <summary>
    /// Forms the CONTEXT value for the pixel at (<paramref name="x"/>,
    /// <paramref name="y"/>) — T.88 Figures 4-7. Pixels above row 0 or outside
    /// the horizontal bounds read as white.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the tests can pin each template's bit
    /// assignment directly against the spec figures. An encode/decode round-trip
    /// cannot check any of this — both sides call straight into here, so they
    /// share whatever mistake is made. The external jbig2dec oracle is what
    /// catches a wrong pixel <em>set</em>; the one-hot tests are what pin the
    /// literal spec numbering that TPGDON's hard-coded SLTP contexts depend on.
    /// </remarks>
    internal static int Context(Jbig2Bitmap b, int x, int y, int template, ReadOnlySpan<sbyte> at) => template switch
    {
        // Figure 4. Row -2: A4, (-1,0,+1), A3.  Row -1: A2, (-2..+2), A1.  Row 0: (-4..-1).
        0 => (b.Get(x + at[6], y + at[7]) << 15)
           | (b.Get(x - 1, y - 2) << 14)
           | (b.Get(x, y - 2) << 13)
           | (b.Get(x + 1, y - 2) << 12)
           | (b.Get(x + at[4], y + at[5]) << 11)
           | (b.Get(x + at[2], y + at[3]) << 10)
           | (b.Get(x - 2, y - 1) << 9)
           | (b.Get(x - 1, y - 1) << 8)
           | (b.Get(x, y - 1) << 7)
           | (b.Get(x + 1, y - 1) << 6)
           | (b.Get(x + 2, y - 1) << 5)
           | (b.Get(x + at[0], y + at[1]) << 4)
           | (b.Get(x - 4, y) << 3)
           | (b.Get(x - 3, y) << 2)
           | (b.Get(x - 2, y) << 1)
           | b.Get(x - 1, y),

        // Figure 5. Row -2: (-1..+2).  Row -1: (-2..+2), A1.  Row 0: (-3..-1).
        1 => (b.Get(x - 1, y - 2) << 12)
           | (b.Get(x, y - 2) << 11)
           | (b.Get(x + 1, y - 2) << 10)
           | (b.Get(x + 2, y - 2) << 9)
           | (b.Get(x - 2, y - 1) << 8)
           | (b.Get(x - 1, y - 1) << 7)
           | (b.Get(x, y - 1) << 6)
           | (b.Get(x + 1, y - 1) << 5)
           | (b.Get(x + 2, y - 1) << 4)
           | (b.Get(x + at[0], y + at[1]) << 3)
           | (b.Get(x - 3, y) << 2)
           | (b.Get(x - 2, y) << 1)
           | b.Get(x - 1, y),

        // Figure 6. Row -2: (-1..+1).  Row -1: (-2..+1), A1.  Row 0: (-2..-1).
        2 => (b.Get(x - 1, y - 2) << 9)
           | (b.Get(x, y - 2) << 8)
           | (b.Get(x + 1, y - 2) << 7)
           | (b.Get(x - 2, y - 1) << 6)
           | (b.Get(x - 1, y - 1) << 5)
           | (b.Get(x, y - 1) << 4)
           | (b.Get(x + 1, y - 1) << 3)
           | (b.Get(x + at[0], y + at[1]) << 2)
           | (b.Get(x - 2, y) << 1)
           | b.Get(x - 1, y),

        // Figure 7 — a single row of history. Row -1: (-3..+1), A1.  Row 0: (-4..-1).
        3 => (b.Get(x - 3, y - 1) << 9)
           | (b.Get(x - 2, y - 1) << 8)
           | (b.Get(x - 1, y - 1) << 7)
           | (b.Get(x, y - 1) << 6)
           | (b.Get(x + 1, y - 1) << 5)
           | (b.Get(x + at[0], y + at[1]) << 4)
           | (b.Get(x - 4, y) << 3)
           | (b.Get(x - 3, y) << 2)
           | (b.Get(x - 2, y) << 1)
           | b.Get(x - 1, y),

        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "GBTEMPLATE must be 0..3."),
    };

    /// <summary>The SLTP pseudo-context a template uses when TPGDON is on (T.88 §6.2.5.7).</summary>
    internal static int TypicalPredictionContext(int template) => TypicalPredictionContexts[template];
}
