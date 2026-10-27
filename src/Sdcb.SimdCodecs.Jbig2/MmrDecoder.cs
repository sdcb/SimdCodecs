using System;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// MMR decoding — ITU-T T.6 two-dimensional coding, which T.88 §6.2.6 selects
/// for a generic region whose <c>MMR</c> flag is 1. Written clean-room from
/// T.4/T.6, like the rest of this package.
/// <para>
/// This is the same coding Group 4 fax uses, and the same coding behind PDF's
/// <c>/CCITTFaxDecode</c> with <c>K &lt; 0</c>. T.88 takes it verbatim with two
/// conventions worth stating: the line above the region's first row is an
/// imaginary all-white line, and decoding stops after the region's declared
/// height rather than at an end-of-block marker. A trailing EOFB is therefore
/// simply never read.
/// </para>
/// <para>
/// <b>How it works.</b> Every row is coded as a set of edits relative to the row
/// above, expressed in terms of <em>changing elements</em> — the positions where
/// a line changes colour. <c>a0</c> is the position already decoded up to,
/// <c>b1</c> the next colour change on the reference line that would flip to the
/// opposite of the current colour, and <c>b2</c> the one after it. Each mode code
/// says how the current line's next change relates to those: vertical mode places
/// it within three pixels of <c>b1</c>, pass mode says the reference run extends
/// beyond <c>b2</c>, and horizontal mode gives up on the reference line and codes
/// two explicit run lengths with the Modified Huffman tables in
/// <see cref="MmrCodes"/>.
/// </para>
/// <para>
/// A <c>ref struct</c> for the same reason <see cref="MqDecoder"/> is one: the
/// coded bytes stay a <see cref="ReadOnlySpan{T}"/> and are never copied.
/// </para>
/// </summary>
internal ref struct MmrDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    private MmrDecoder(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    /// <summary>
    /// Decodes a <paramref name="width"/> x <paramref name="height"/> bilevel
    /// bitmap from MMR-coded <paramref name="data"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The coded data is malformed or ends early.</exception>
    /// <exception cref="NotSupportedException">The data uses a 2D extension code, which includes T.6's uncompressed mode.</exception>
    public static Jbig2Bitmap Decode(ReadOnlySpan<byte> data, int width, int height, Jbig2PixelBudget budget)
    {
        budget.Charge(width, height);
        var bitmap = new Jbig2Bitmap(width, height);
        var decoder = new MmrDecoder(data);

        // Changing-element positions for the current and reference lines. A line
        // has at most `width` of them; the two spare slots are what b1/b2 read
        // when the lookup runs off the end, so the loop needs no bounds test
        // there. The two buffers swap each row rather than being copied.
        var current = new int[width + 2];
        var reference = new int[width + 2];

        // T.6 §2.2.1: the line above the first one is imaginary and all white,
        // which is to say it has no changing elements at all.
        var referenceCount = 0;

        for (var y = 0; y < height; y++)
        {
            var count = decoder.DecodeRow(reference, referenceCount, current, width);
            PaintRow(bitmap.Data.AsSpan(y * width, width), current, count);

            (reference, current) = (current, reference);
            referenceCount = count;
        }

        return bitmap;
    }

    /// <summary>
    /// Decodes one line into <paramref name="coding"/> as changing-element
    /// positions, returning how many there are.
    /// </summary>
    private int DecodeRow(int[] reference, int referenceCount, int[] coding, int width)
    {
        // a0 starts "just before" the line: T.6 puts the imaginary first changing
        // element at -1 so that a first run of length zero — a line starting
        // black — is expressible.
        var a0 = -1;
        var colour = 0;
        var count = 0;

        // a0 only ever moves right, so the scan for b1 can resume where it left
        // off instead of restarting; without this a long line costs O(n²).
        var scan = 0;

        while (a0 < width)
        {
            // b1 is the first changing element on the reference line strictly
            // right of a0 that changes *to* the opposite of the current colour,
            // and b2 the next one after it. Changing elements alternate, starting
            // with white-to-black at index 0, so "changes to the opposite of
            // colour" is exactly "index parity equals colour".
            while (scan < referenceCount && reference[scan] <= a0) scan++;

            var index = scan;
            if (((index ^ colour) & 1) != 0) index++;

            var b1 = index < referenceCount ? reference[index] : width;
            var b2 = index + 1 < referenceCount ? reference[index + 1] : width;

            var next = a0;
            var mode = ReadMode();
            switch (mode.Mode)
            {
                case MmrCodes.Mode.Pass:
                    // The reference run covers everything up to b2, so the colour
                    // continues and no changing element is produced.
                    next = b2;
                    break;

                case MmrCodes.Mode.Horizontal:
                    // Two explicit runs, the first in the current colour. The
                    // first run of a line measures from 0, not from a0 = -1.
                    var start = a0 < 0 ? 0 : a0;
                    var first = ReadRun(colour);
                    var second = ReadRun(colour ^ 1);

                    var a1 = Math.Min(start + first, width);
                    var a2 = Math.Min(a1 + second, width);
                    Record(coding, ref count, width, a1);
                    Record(coding, ref count, width, a2);
                    next = a2;
                    break;

                case MmrCodes.Mode.Vertical:
                    var v = b1 + mode.Delta;
                    if (v < 0 || v > width)
                        throw new InvalidDataException($"JBIG2: MMR vertical mode puts a changing element at {v}, outside the {width}-pixel line.");

                    Record(coding, ref count, width, v);
                    next = v;
                    colour ^= 1;
                    break;

                case MmrCodes.Mode.Extension:
                    throw new NotSupportedException(
                        "JBIG2: MMR 2D extension codes (including T.6 uncompressed mode) are not implemented.");

                default:
                    // The all-zero prefix: an EOL, an EOFB, or fill bits. None of
                    // those may appear before the region's rows are all decoded.
                    throw new InvalidDataException("JBIG2: MMR data ended or desynchronized before the region was complete.");
            }

            // Every mode advances a0. Standing still would loop forever, and it
            // can only happen on malformed data, so say so rather than hang.
            if (next <= a0)
                throw new InvalidDataException($"JBIG2: MMR {mode.Mode} mode made no progress at position {a0}.");

            a0 = next;
        }

        return count;
    }

    /// <summary>
    /// Appends a changing element, rejecting a line that claims more of them than
    /// it could have. The ceiling is <c>width + 1</c>, not <c>width</c>: an
    /// alternating line changes colour at every one of its pixels, and the closing
    /// vertical code then records the right margin as one more.
    /// </summary>
    private static void Record(int[] coding, ref int count, int width, int position)
    {
        if (count > width)
            throw new InvalidDataException($"JBIG2: MMR line claims more changing elements than a {width}-pixel line can have.");

        coding[count++] = position;
    }

    /// <summary>
    /// Paints a line from its changing elements. They alternate white-to-black
    /// then black-to-white, so the black spans are the pairs starting at every
    /// even index; an odd count means the last black run reaches the line's end.
    /// </summary>
    private static void PaintRow(Span<byte> row, int[] transitions, int count)
    {
        row.Clear();

        for (var i = 0; i < count; i += 2)
        {
            var start = transitions[i];
            if (start >= row.Length) break;

            var end = i + 1 < count ? Math.Min(transitions[i + 1], row.Length) : row.Length;
            if (end > start) row[start..end].Fill(1);
        }
    }

    /// <summary>Reads one two-dimensional mode code (T.4 Table 4) and consumes its bits.</summary>
    private MmrCodes.ModeCode ReadMode()
    {
        var code = MmrCodes.LookupMode((int)Peek(MmrCodes.ModeBits));
        if (code.Mode != MmrCodes.Mode.None) _bitPosition += code.Length;
        return code;
    }

    /// <summary>
    /// Reads a complete run length of <paramref name="colour"/>: zero or more
    /// makeup codes followed by one terminating code (T.4 §4.1.2). Makeup codes
    /// chain, which is how runs beyond 2560 are expressed.
    /// </summary>
    private int ReadRun(int colour)
    {
        var bits = colour == 0 ? MmrCodes.WhiteBits : MmrCodes.BlackBits;
        var total = 0;

        while (true)
        {
            var run = MmrCodes.LookupRun(colour, (int)Peek(bits));
            if (run.Length == 0)
                throw new InvalidDataException($"JBIG2: MMR {(colour == 0 ? "white" : "black")} run code is not in the T.4 tables.");

            _bitPosition += run.Length;
            total += run.Value;

            // Only a makeup code continues the run; a terminating code (0-63)
            // ends it, and every chain must finish with one.
            if (run.Value < MmrCodes.MakeupThreshold) return total;
        }
    }

    /// <summary>
    /// Returns the next <paramref name="count"/> bits, most significant first,
    /// without consuming them. Past the end of the data the bits read as zero,
    /// which lands on the all-zero mode prefix and fails the row as truncated
    /// rather than inventing pixels.
    /// </summary>
    private readonly uint Peek(int count)
    {
        var index = _bitPosition >> 3;
        var shift = _bitPosition & 7;

        uint window = 0;
        for (var i = 0; i < 4; i++)
            window = (window << 8) | (index + i < _data.Length ? _data[index + i] : 0u);

        // count is at most 13 and shift at most 7, so the wanted bits are always
        // inside the 32-bit window.
        return (window << shift) >> (32 - count);
    }
}
