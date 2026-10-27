using System;
using System.IO;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// Resource ceilings for decoding a stream nobody vouches for.
/// <para>
/// JBIG2's primary consumer here is PDF's <c>/JBIG2Decode</c> filter, which means
/// every number this decoder reads is attacker-chosen. Two of them size work
/// directly — a region's width and height — and the MQ decoder deliberately keeps
/// producing decisions past the end of its data (T.88 E.3.4 reads every byte after
/// the end as <c>0xFF</c>), so running out of input is <em>not</em> a backstop. A
/// region declaring 46340x46340 therefore costs 2 GiB and billions of decisions no
/// matter how few bytes back it: measured at 82 input bytes for 2 GiB, an
/// amplification of 26 million to one.
/// </para>
/// <para>
/// Compression ratio is not a usable bound either. A blank 1200 dpi page with
/// TPGDON costs about one decision per row, so "pixels must be proportional to
/// coded bytes" would reject perfectly ordinary faxes. What is left is an absolute
/// ceiling per bitmap plus a total budget tied to the page the caller actually
/// asked for, which is the one number in the transaction that is not chosen by the
/// stream.
/// </para>
/// </summary>
internal static class Jbig2Limits
{
    /// <summary>
    /// Most pixels any single bitmap may have: 2^28, or 256 MiB at this decoder's
    /// one byte per pixel. A 1200 dpi A4 page is 9921 x 14031 ≈ 139 Mpixels, so
    /// this admits the largest plausible scan with room over it.
    /// <para>
    /// It also keeps <c>width * height</c> an order of magnitude clear of
    /// <see cref="int"/> overflow, which is what the previous ceiling of
    /// <c>1L &lt;&lt; 31</c> did not: an area of exactly 2^31 passed a
    /// <c>&gt;</c> test and then overflowed the <c>checked</c> multiply in
    /// <see cref="Jbig2Bitmap"/>, turning a malformed stream into an
    /// <see cref="OverflowException"/> instead of a rejection.
    /// </para>
    /// </summary>
    public const int MaxBitmapPixels = 1 << 28;

    /// <summary>
    /// Most cells a halftone grid may have: 2^24. Lower than
    /// <see cref="MaxBitmapPixels"/> on purpose — the grey-scale procedure of
    /// Annex C.5 holds an <see cref="int"/> accumulator plus a byte plane per
    /// cell, so a cell costs five bytes rather than one, and it decodes one
    /// bitplane per bit of grey depth.
    /// </summary>
    public const int MaxHalftoneGridCells = 1 << 24;

    /// <summary>
    /// Floor for a decode's total pixel budget, so that a small page still gets
    /// enough room for a symbol dictionary and its text region.
    /// </summary>
    public const long MinPixelBudget = 1L << 26;

    /// <summary>
    /// Total pixels a single decode may produce, across every region, symbol,
    /// halftone plane and refinement pass.
    /// <para>
    /// Four times the page is deliberately loose: a legitimate stream decodes the
    /// page roughly once, and the multiplier leaves room for a refinement pass over
    /// it plus the dictionaries feeding a text region. What it stops is the shape
    /// with no legitimate reading — a 32x16 page whose stream asks for 536 million
    /// pixels of region, every one of them clipped away by composition.
    /// </para>
    /// </summary>
    public static long BudgetFor(int pageWidth, int pageHeight) =>
        Math.Max(MinPixelBudget, (long)pageWidth * pageHeight * 4);
}

/// <summary>
/// A decode's running pixel allowance. Charged by each decoder that produces
/// pixels — generic, refinement, MMR, text and halftone — <em>before</em> it
/// allocates, so an over-budget stream is rejected rather than served.
/// </summary>
internal sealed class Jbig2PixelBudget(long total)
{
    // An explicit field rather than reading the primary-constructor parameter from
    // Charge: referencing `total` in a method body makes the compiler capture the
    // parameter as a second hidden field alongside _remaining.
    private readonly long _total = total;
    private long _remaining = total;

    /// <summary>
    /// An allowance nothing can exhaust, for unit tests that drive a single
    /// decoder directly rather than through <see cref="Jbig2Decoder"/>.
    /// </summary>
    public static Jbig2PixelBudget Unmetered() => new(long.MaxValue);

    /// <summary>Books <paramref name="width"/> x <paramref name="height"/> pixels of work.</summary>
    /// <exception cref="InvalidDataException">The stream has asked for more than its budget.</exception>
    public void Charge(int width, int height)
    {
        var pixels = (long)width * height;
        if (pixels > _remaining)
            throw new InvalidDataException(
                $"JBIG2: the stream asks to decode {pixels:N0} more pixels than its remaining budget of " +
                $"{_remaining:N0} allows (total {_total:N0}, scaled to the page). A stream needing this much " +
                "work relative to its own page size is a decompression bomb, not an image.");

        _remaining -= pixels;
    }
}
