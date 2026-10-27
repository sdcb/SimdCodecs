using System;
using System.IO;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// Resource ceilings for decoding a codestream nobody vouches for.
/// <para>
/// This is <c>Jbig2Limits</c>'s shape, ported deliberately at rung 1 rather than
/// after shipping. JBIG2 needed a 3.8 release to add it, and the roadmap's
/// hazard 6 says not to repeat that: the numbers that size the work here —
/// image extent, tile extent, component count, bit depth, decomposition levels,
/// code-block size — are every one of them read from a codestream that in the
/// primary use case arrives inside a PDF.
/// </para>
/// <para>
/// The JBIG2 lesson that transfers exactly: <b>running out of input is not a
/// backstop.</b> There the MQ decoder reads past the end as <c>0xFF</c> for
/// ever. Here it is the same coder with the same property, and tier-2 adds its
/// own version — a <c>SIZ</c> declaring a huge image and five decomposition
/// levels implies a code-block grid that costs memory to enumerate before a
/// single coded byte is touched. So the ceilings are charged against
/// <em>declared</em> geometry, up front, not against how much data actually
/// arrives.
/// </para>
/// <para>
/// The JBIG2 lesson that does <b>not</b> transfer: there the budget could be
/// anchored to the caller's page size, because PDF supplies the image
/// dictionary's width and height out of band. A raw J2K codestream carries its
/// own dimensions, so rung 1's anchor is the codestream's own <c>SIZ</c> and the
/// budget is a multiple of it. That bounds amplification (a stream cannot
/// declare a small image and then decode a huge one) without bounding absolute
/// size, which is the same residual JBIG2 documents for the standalone
/// <c>.jb2</c> path. Rung 4 gets the tighter anchor when the PDF entry point
/// arrives with caller-supplied dimensions.
/// </para>
/// </summary>
internal static class Jpeg2000Limits
{
    /// <summary>
    /// Most samples any single tile-component may hold: 2^28.
    /// <para>
    /// The same 2^28 as <c>Jbig2Limits.MaxBitmapPixels</c> and for the same
    /// reason — it admits a 1200 dpi A4 scan (≈139 Mpixels) with headroom while
    /// staying an order of magnitude clear of <see cref="int"/> overflow on
    /// <c>width * height</c>. It is a stricter ceiling in bytes than JBIG2's,
    /// because a coefficient here is a 4-byte <see cref="int"/> rather than a
    /// 1-byte pixel, so 2^28 samples is a gigabyte and this limit is what stops
    /// one tile-component allocating it.
    /// </para>
    /// </summary>
    public const int MaxTileComponentSamples = 1 << 28;

    /// <summary>
    /// Most code-blocks one decode may enumerate: 2^22.
    /// <para>
    /// A separate limit because code-block count is not proportional to sample
    /// count in the direction that matters. <c>COD</c> chooses the code-block
    /// size independently of the image size, and the smallest legal one is 4x4
    /// (T.800 says no dimension below 4 and no more than 4096 coefficients), so
    /// a modest image with <c>-b 4,4</c> already has sixteen times the blocks of
    /// the 16x16 case and each one carries its own state, tag-tree node and
    /// length bookkeeping. Bounding samples alone would let a legal-looking
    /// header buy a very large object graph.
    /// </para>
    /// </summary>
    public const int MaxCodeBlocks = 1 << 22;

    /// <summary>
    /// Most decomposition levels <c>COD</c> may declare. T.800 Table A.13 caps
    /// <c>SPcod</c>'s value at 32; each level adds a resolution to iterate and a
    /// subband triple to allocate, and no real image uses more than about 8.
    /// Rejecting above the spec's own ceiling is free.
    /// </summary>
    public const int MaxDecompositionLevels = 32;

    /// <summary>
    /// Most components <c>SIZ</c> may declare. The field is 16 bits, so the
    /// codestream may say 65535; PDF's colour spaces need at most four plus an
    /// alpha, and a decode allocates a full sample plane per component.
    /// </summary>
    public const int MaxComponents = 256;

    /// <summary>
    /// Floor for a decode's total sample budget, so a small image still has room
    /// for its subbands and the reconstruction buffer.
    /// </summary>
    public const long MinSampleBudget = 1L << 24;

    /// <summary>
    /// Total samples one decode may produce across every tile, component and
    /// resolution level.
    /// <para>
    /// Four times the declared image is deliberately loose. A legitimate decode
    /// touches each sample about twice — once as coefficients, once as the
    /// reconstructed plane — and the multiplier leaves room for the DWT's
    /// working buffers on top. What it stops is amplification: a codestream
    /// declaring a 64x64 image and then asking, through tile and subband
    /// geometry, for hundreds of megasamples of work whose result is discarded.
    /// </para>
    /// </summary>
    public static long BudgetFor(int width, int height, int components) =>
        Math.Max(MinSampleBudget, (long)width * height * Math.Max(1, components) * 4);
}

/// <summary>
/// A decode's running sample allowance. Charged by everything that allocates a
/// sample plane — tile-components, subbands, the reconstruction buffer —
/// <em>before</em> the allocation, so an over-budget codestream is rejected
/// rather than served.
/// <para>
/// Shared across tiles and components on purpose, exactly as
/// <c>Jbig2PixelBudget</c> is shared across segments: it is what stops a stream
/// splitting the same total work into many individually plausible pieces.
/// </para>
/// </summary>
internal sealed class Jpeg2000SampleBudget(long total)
{
    // An explicit field rather than reading the primary-constructor parameter
    // from Charge: referencing `total` in a method body would make the compiler
    // capture it as a second hidden field alongside _remaining.
    private readonly long _total = total;
    private long _remaining = total;
    private long _codeBlocks;

    /// <summary>
    /// An allowance nothing can exhaust, for unit tests that drive one stage
    /// directly rather than through <see cref="Jpeg2000Decoder"/>. Required
    /// rather than defaulted at every call site, so a new one cannot silently
    /// opt out of metering.
    /// </summary>
    public static Jpeg2000SampleBudget Unmetered() => new(long.MaxValue);

    /// <summary>Books <paramref name="width"/> x <paramref name="height"/> samples of work.</summary>
    /// <exception cref="InvalidDataException">The codestream has asked for more than its budget.</exception>
    public void Charge(int width, int height)
    {
        var samples = (long)width * height;
        if (samples > _remaining)
            throw new InvalidDataException(
                $"JPEG 2000: the codestream asks to decode {samples:N0} more samples than its remaining " +
                $"budget of {_remaining:N0} allows (total {_total:N0}, scaled to the declared image size). " +
                "A codestream needing this much work relative to the image it claims to be is a " +
                "decompression bomb, not a picture.");

        _remaining -= samples;
    }

    /// <summary>
    /// Books <paramref name="count"/> more code-blocks against
    /// <see cref="Jpeg2000Limits.MaxCodeBlocks"/>. Held here beside the samples so the ceiling is one
    /// total across every component, as its documentation says, rather than one per component that a
    /// many-component header would multiply.
    /// </summary>
    /// <exception cref="InvalidDataException">The declared geometry has more code-blocks than the ceiling.</exception>
    public void ChargeCodeBlocks(int count)
    {
        _codeBlocks += count;
        if (_codeBlocks > Jpeg2000Limits.MaxCodeBlocks)
            throw new InvalidDataException(
                $"JPEG 2000: the declared geometry has more than {Jpeg2000Limits.MaxCodeBlocks:N0} " +
                "code-blocks.");
    }
}
