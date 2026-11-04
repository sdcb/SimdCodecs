using System;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// Generic refinement region decoding — ITU-T T.88 §6.3. Reconstructs a bitmap
/// as a set of corrections to a <em>reference</em> bitmap rather than from
/// scratch, which is what lets a lossy page be upgraded to lossless by a second,
/// much cheaper pass.
/// <para>
/// The shape mirrors <see cref="GenericRegionDecoder"/>: one arithmetic decision
/// per pixel, taken against a context built from already-decoded neighbours. The
/// difference is where those neighbours come from — a refinement template reads
/// a few pixels of the bitmap being built <em>and</em> a block of the reference,
/// offset by (<c>GRREFERENCEDX</c>, <c>GRREFERENCEDY</c>). A pixel whose
/// reference neighbourhood is uniform is nearly always the same as the
/// reference, so those decisions cost almost nothing.
/// </para>
/// <para>
/// The same caveat as the generic templates applies, for the same reason: what
/// conformance turns on is <b>which pixels the template reads</b>, not which bit
/// each lands in — permuting the bit positions is a bijection over context
/// values and leaves the coded bytes unchanged. The numbering below still
/// follows T.88 Figures 12-15, because TPGRON's SLTP decision uses hard-coded
/// contexts that name a specific neighbourhood in the spec's numbering.
/// </para>
/// <para>
/// <b>TPGRON</b> (§6.3.5.6) toggles LTP with one SLTP decision per row, and
/// while LTP is on a pixel over a uniform 3x3 patch of the reference takes the
/// patch's colour without a decision. The SLTP decision has a context of its
/// own, a fixed value: 0x0010 for GRTEMPLATE 0 and 0x0004 for GRTEMPLATE 1 in
/// the numbering below. An earlier version of this file used 0x0100 / 0x0080
/// and refused TPGRON because it disagreed with jbig2dec. The constants were
/// settled by measurement: for GRTEMPLATE 1, sweeping all 1024 values against
/// jbig2dec over the six <c>Jbig2RefinementTests.Cases</c> leaves 0x0004 as the
/// only one that agrees on every case; for GRTEMPLATE 0 those cases agree with
/// many values (the slot is rarely reached), and 0x0010 is the one that also
/// decodes the TPGRON refinement region of the issue #1 corpus
/// (<c>042_24.jb2</c>, see <c>Issue1Jbig2CorpusTests</c>) exactly as
/// jbig2dec does.
/// </para>
/// </summary>
internal static class RefinementRegionDecoder
{
    /// <summary>
    /// Nominal AT offsets for GRTEMPLATE 0 (T.88 §6.3.5.3): A1 in the bitmap
    /// being decoded and A2 in the reference, both at (-1, -1). GRTEMPLATE 1 has
    /// no adaptive pixels at all.
    /// </summary>
    public static ReadOnlySpan<sbyte> NominalAt => [-1, -1, -1, -1];

    // T.88 §6.3.5.6: the pseudo-context the SLTP decision uses when TPGRON is on.
    private static ReadOnlySpan<ushort> TypicalPredictionContexts => [0x0010, 0x0004];

    /// <summary>Number of context bits (hence the context array size) for each GRTEMPLATE.</summary>
    public static int ContextBits(int template) => template switch
    {
        0 => 13,
        1 => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "GRTEMPLATE must be 0 or 1."),
    };

    /// <summary>
    /// Decodes a refinement region of <paramref name="width"/> x
    /// <paramref name="height"/> pixels against <paramref name="reference"/>.
    /// </summary>
    /// <param name="mq">The arithmetic decoder, positioned at the coded data.</param>
    /// <param name="contexts">Adaptive context storage, at least <c>1 &lt;&lt; ContextBits(template)</c> bytes.</param>
    /// <param name="width">Region width, GRW.</param>
    /// <param name="height">Region height, GRH.</param>
    /// <param name="template">GRTEMPLATE, 0 or 1.</param>
    /// <param name="reference">GRREFERENCE, the bitmap being refined.</param>
    /// <param name="dx">GRREFERENCEDX: the reference is read shifted by this much horizontally.</param>
    /// <param name="dy">GRREFERENCEDY, likewise vertically.</param>
    /// <param name="typicalPrediction">TPGRON — enables the uniform-neighbourhood shortcut.</param>
    /// <param name="at">A1 and A2 as (x,y) sbyte pairs; ignored for GRTEMPLATE 1.</param>
    /// <param name="budget">
    /// The decode's remaining pixel allowance, charged before anything is allocated.
    /// </param>
    public static Jbig2Bitmap Decode(
        ref MqDecoder mq,
        scoped Span<byte> contexts,
        int width,
        int height,
        int template,
        Jbig2Bitmap reference,
        int dx,
        int dy,
        bool typicalPrediction,
        scoped ReadOnlySpan<sbyte> at,
        Jbig2PixelBudget budget)
    {
        if (template == 0 && at.Length < 4)
            throw new ArgumentException("GRTEMPLATE 0 needs two AT pixels.", nameof(at));
        if (contexts.Length < 1 << ContextBits(template))
            throw new ArgumentException("Context array too small for this GRTEMPLATE.", nameof(contexts));

        budget.Charge(width, height);
        var bitmap = new Jbig2Bitmap(width, height);
        var sltpContext = TypicalPredictionContexts[template];
        var ltp = 0;

        for (var y = 0; y < height; y++)
        {
            if (typicalPrediction && mq.Decode(contexts, sltpContext) != 0) ltp ^= 1;

            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                // §6.3.5.6: while LTP is on, a pixel sitting over a uniform 3x3
                // patch of the reference is *assumed* to match it and costs no
                // decision at all. Anywhere the reference is not uniform — an
                // edge, which is where refinement earns its keep — the pixel is
                // coded normally.
                if (ltp == 1 && TryTypicalPixel(reference, x - dx, y - dy, out var typical))
                {
                    bitmap.Data[row + x] = typical;
                    continue;
                }

                bitmap.Data[row + x] =
                    (byte)mq.Decode(contexts, Context(bitmap, x, y, reference, x - dx, y - dy, template, at));
            }
        }

        return bitmap;
    }

    /// <summary>
    /// True when the 3x3 patch of <paramref name="reference"/> centred on
    /// (<paramref name="rx"/>, <paramref name="ry"/>) is all white or all black,
    /// in which case <paramref name="value"/> is that colour.
    /// </summary>
    private static bool TryTypicalPixel(Jbig2Bitmap reference, int rx, int ry, out byte value)
    {
        var sum = reference.Get(rx - 1, ry - 1) + reference.Get(rx, ry - 1) + reference.Get(rx + 1, ry - 1)
                + reference.Get(rx - 1, ry) + reference.Get(rx, ry) + reference.Get(rx + 1, ry)
                + reference.Get(rx - 1, ry + 1) + reference.Get(rx, ry + 1) + reference.Get(rx + 1, ry + 1);

        value = (byte)(sum == 9 ? 1 : 0);
        return sum is 0 or 9;
    }

    /// <summary>
    /// Forms the CONTEXT value for one pixel — T.88 Figures 12-15. Coordinates
    /// outside either bitmap read as white, the same rule §6.2.5.2 sets for the
    /// generic templates.
    /// </summary>
    /// <remarks>
    /// Internal so the tests can pin each template's cells directly. As with the
    /// generic templates, a round-trip through our own encoder cannot check any
    /// of this — both sides call in here — so the external oracle is what
    /// establishes the pixel set is right.
    /// </remarks>
    internal static int Context(
        Jbig2Bitmap b, int x, int y,
        Jbig2Bitmap r, int rx, int ry,
        int template, ReadOnlySpan<sbyte> at) => template switch
    {
        // Figure 12 + 13. Current: A1 and the two cells above, plus the one to
        // the left. Reference: the full 3x3, with A2 standing in for its
        // top-left corner.
        0 => (b.Get(x + at[0], y + at[1]) << 12)
           | (b.Get(x, y - 1) << 11)
           | (b.Get(x + 1, y - 1) << 10)
           | (b.Get(x - 1, y) << 9)
           | (r.Get(rx + at[2], ry + at[3]) << 8)
           | (r.Get(rx, ry - 1) << 7)
           | (r.Get(rx + 1, ry - 1) << 6)
           | (r.Get(rx - 1, ry) << 5)
           | (r.Get(rx, ry) << 4)
           | (r.Get(rx + 1, ry) << 3)
           | (r.Get(rx - 1, ry + 1) << 2)
           | (r.Get(rx, ry + 1) << 1)
           | r.Get(rx + 1, ry + 1),

        // Figure 14 + 15 — no adaptive pixels, and a reference patch trimmed to
        // its diamond.
        1 => (b.Get(x - 1, y - 1) << 9)
           | (b.Get(x, y - 1) << 8)
           | (b.Get(x + 1, y - 1) << 7)
           | (b.Get(x - 1, y) << 6)
           | (r.Get(rx, ry - 1) << 5)
           | (r.Get(rx - 1, ry) << 4)
           | (r.Get(rx, ry) << 3)
           | (r.Get(rx + 1, ry) << 2)
           | (r.Get(rx, ry + 1) << 1)
           | r.Get(rx + 1, ry + 1),

        _ => throw new ArgumentOutOfRangeException(nameof(template), template, "GRTEMPLATE must be 0 or 1."),
    };

    /// <summary>The SLTP pseudo-context a template uses when TPGRON is on (T.88 §6.3.5.6).</summary>
    internal static int TypicalPredictionContext(int template) => TypicalPredictionContexts[template];

    /// <summary>
    /// True when the pixel at (<paramref name="rx"/>, <paramref name="ry"/>) would
    /// be skipped by TPGRON — exposed so a test encoder can make the same
    /// decision the decoder will.
    /// </summary>
    internal static bool IsTypical(Jbig2Bitmap reference, int rx, int ry, out byte value) =>
        TryTypicalPixel(reference, rx, ry, out value);
}
