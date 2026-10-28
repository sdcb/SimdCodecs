using System.Buffers;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// The three CCITT fax compressions TIFF 6.0 defines for bilevel pages, decoded into packed rows
/// (MSB first, each row padded to a whole byte -- the same layout an uncompressed 1-bit strip has).
/// Written from ITU-T T.4 / T.6 and TIFF 6.0 sections 10 and 11; the code tables are the ones the
/// JBIG2 MMR decoder reads (<see cref="MmrCodes"/>).
/// <list type="bullet">
/// <item><see cref="TiffCompression.CcittRle"/> (2): every row one-dimensional Modified Huffman,
///       no EOLs, each row starting on a byte boundary.</item>
/// <item><see cref="TiffCompression.CcittT4"/> (3): T.4. Rows end in EOLs (optionally zero-filled so
///       the EOL ends a byte); with T4Options bit 0 each EOL carries a tag bit choosing a 1D or 2D
///       row.</item>
/// <item><see cref="TiffCompression.CcittT6"/> (4): T.6, every row 2D against the row above.</item>
/// </list>
/// A white run decodes to 0 bits and a black run to 1 bits whatever the page's photometric says: the
/// codes name runs, and PhotometricInterpretation then says what a 0 sample means (TIFF 6.0 section
/// 10). Each strip or tile is coded independently, so its first reference line is all white.
/// </summary>
internal static class TiffFax
{
    /// <summary>T4Options bit 0: rows may be 2D-coded.</summary>
    public const uint T4TwoDimensional = 1;

    /// <summary>
    /// Decodes up to <paramref name="rows"/> rows of a <paramref name="width"/>-pixel segment into
    /// <paramref name="destination"/> and returns the bytes written -- whole rows only. A segment whose
    /// data runs out early returns the rows it completed, the same leniency the other codecs give a
    /// truncated strip; data that is present but not a valid code throws.
    /// </summary>
    /// <remarks>FillOrder = 2 is undone by the caller before the bytes get here, as for every compression.</remarks>
    public static int Decode(ReadOnlySpan<byte> data, Span<byte> destination, int width, int rows,
        TiffCompression compression, uint options)
    {
        // The changing-element buffers are sized off the width, which comes from the file.
        if (width is <= 0 or > (1 << 28))
            throw new InvalidDataException($"CCITT row width {width} is out of range");
        var rowBytes = (width + 7) / 8;
        rows = Math.Min(rows, destination.Length / rowBytes);
        if (rows <= 0) return 0;

        // Changing elements of the current and reference rows (a row of n pixels has at most n + 1 of
        // them, counting the right margin a closing vertical code can record), plus spare slots that
        // b1/b2 read past the end.
        var current = ArrayPool<int>.Shared.Rent(width + 3);
        var reference = ArrayPool<int>.Shared.Rent(width + 3);
        try
        {
            var reader = new BitReader(data);
            var referenceCount = 0;   // the imaginary all-white row above the first has no changes
            var done = 0;
            for (; done < rows; done++)
            {
                int count;
                try
                {
                    count = compression switch
                    {
                        TiffCompression.CcittRle => reader.DecodeRow1D(current, width, alignAfter: true),
                        TiffCompression.CcittT4 => reader.DecodeT4Row(reference, referenceCount, current, width,
                            (options & T4TwoDimensional) != 0),
                        TiffCompression.CcittT6 => reader.DecodeRow2D(reference, referenceCount, current, width),
                        _ => throw new NotSupportedException($"Compression {compression} is not a CCITT fax compression"),
                    };
                }
                catch (InvalidDataException) when (reader.Overran)
                {
                    // Ran off the end of the segment mid-row: a truncated file. Keep what was complete.
                    break;
                }

                PaintRow(destination.Slice(done * rowBytes, rowBytes), current, count, width);
                (reference, current) = (current, reference);
                referenceCount = count;
            }

            return done * rowBytes;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(current);
            ArrayPool<int>.Shared.Return(reference);
        }
    }

    /// <summary>
    /// Sets the black spans of one packed row. Changing elements alternate white-to-black and
    /// black-to-white, so the black spans start at every even index; an odd count means the last black
    /// run reaches the end of the row. The pad bits past <paramref name="width"/> stay 0.
    /// </summary>
    private static void PaintRow(Span<byte> row, int[] transitions, int count, int width)
    {
        row.Clear();
        for (var i = 0; i < count; i += 2)
        {
            var start = transitions[i];
            if (start >= width) break;
            var end = i + 1 < count ? Math.Min(transitions[i + 1], width) : width;
            for (var x = start; x < end; x++) row[x >> 3] |= (byte)(0x80 >> (x & 7));
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _bitPosition;

        /// <summary>True once a peek has reached past the last byte: the zeros it saw there were not data.</summary>
        public bool Overran { get; private set; }

        private readonly bool PastEnd => _bitPosition >= (long)_data.Length * 8;

        /// <summary>
        /// One T.4 row: skips the EOL (and any fill before it) that may precede it, reads the 1D/2D tag
        /// bit when 2D coding is enabled, and decodes the row in the mode that bit selects.
        /// </summary>
        public int DecodeT4Row(int[] reference, int referenceCount, int[] coding, int width, bool twoDimensional)
        {
            SkipEols();
            if (!twoDimensional) return DecodeRow1D(coding, width, alignAfter: false);

            var oneDimensional = Peek(1) == 1;
            _bitPosition++;
            return oneDimensional
                ? DecodeRow1D(coding, width, alignAfter: false)
                : DecodeRow2D(reference, referenceCount, coding, width);
        }

        /// <summary>
        /// Consumes EOLs (eleven or more zeros, then a 1 -- T.4 lets fill zeros pad the front of an EOL)
        /// at the current position, including the six of an RTC. No valid run or mode code starts with
        /// more than seven zeros, so the run of zeros alone tells an EOL from a row's first code.
        /// </summary>
        private void SkipEols()
        {
            while (true)
            {
                var zeros = 0;
                while (!PastEnd && Peek(1) == 0)
                {
                    _bitPosition++;
                    zeros++;
                }

                if (zeros >= 11 && !PastEnd)
                {
                    _bitPosition++;   // the EOL's closing 1
                    continue;
                }

                // Not an EOL: give the zeros back, they belong to the row's first code.
                _bitPosition -= zeros;
                return;
            }
        }

        /// <summary>
        /// A one-dimensional row: alternating white and black runs, starting white, until the row is
        /// full (T.4 section 4.1). Each run lands a changing element.
        /// </summary>
        public int DecodeRow1D(int[] coding, int width, bool alignAfter)
        {
            var a0 = 0;
            var colour = 0;
            var count = 0;
            while (a0 < width)
            {
                var run = ReadRun(colour);
                a0 = Math.Min(a0 + run, width);
                Record(coding, ref count, width, a0);
                colour ^= 1;
            }

            if (alignAfter) _bitPosition = (_bitPosition + 7) & ~7L;
            return count;
        }

        /// <summary>
        /// A two-dimensional row coded against <paramref name="reference"/> (T.4 section 4.2, T.6
        /// section 2.2): pass, horizontal and vertical modes placing the row's changing elements relative
        /// to b1 and b2 on the row above.
        /// </summary>
        public int DecodeRow2D(int[] reference, int referenceCount, int[] coding, int width)
        {
            // a0 starts just before the row, so a row starting black is a first run of length zero.
            var a0 = -1;
            var colour = 0;
            var count = 0;

            // a0 only moves right, so the scan for b1 resumes where it stopped instead of restarting.
            var scan = 0;

            while (a0 < width)
            {
                // b1: the first changing element right of a0 that changes to the opposite of the current
                // colour; b2 the next one. Changes alternate starting white-to-black at index 0, so
                // "changes to the opposite of colour" is "index parity equals colour".
                while (scan < referenceCount && reference[scan] <= a0) scan++;
                var index = scan;
                if (((index ^ colour) & 1) != 0) index++;

                var b1 = index < referenceCount ? reference[index] : width;
                var b2 = index + 1 < referenceCount ? reference[index + 1] : width;

                int next;
                var mode = MmrCodes.LookupMode((int)Peek(MmrCodes.ModeBits));
                if (mode.Mode != MmrCodes.Mode.None) _bitPosition += mode.Length;
                switch (mode.Mode)
                {
                    case MmrCodes.Mode.Pass:
                        next = b2;
                        break;

                    case MmrCodes.Mode.Horizontal:
                    {
                        var start = a0 < 0 ? 0 : a0;
                        var a1 = Math.Min(start + ReadRun(colour), width);
                        var a2 = Math.Min(a1 + ReadRun(colour ^ 1), width);
                        Record(coding, ref count, width, a1);
                        Record(coding, ref count, width, a2);
                        next = a2;
                        break;
                    }

                    case MmrCodes.Mode.Vertical:
                        next = b1 + mode.Delta;
                        if (next < 0 || next > width)
                            throw new InvalidDataException($"CCITT vertical mode puts a changing element at {next}, outside the {width}-pixel row");
                        Record(coding, ref count, width, next);
                        colour ^= 1;
                        break;

                    case MmrCodes.Mode.Extension:
                        throw new NotSupportedException("CCITT 2D extension codes (uncompressed mode) are not supported");

                    default:
                        throw new InvalidDataException("CCITT data ended or lost sync before the row was complete");
                }

                // Every mode must advance a0; standing still on malformed data would loop forever.
                if (next <= a0)
                    throw new InvalidDataException($"CCITT {mode.Mode} mode made no progress at position {a0}");
                a0 = next;
            }

            return count;
        }

        private static void Record(int[] coding, ref int count, int width, int position)
        {
            if (count > width)
                throw new InvalidDataException($"CCITT row claims more changing elements than a {width}-pixel row can have");
            coding[count++] = position;
        }

        /// <summary>One complete run: makeup codes (which chain for runs past 2560) closed by a terminating code.</summary>
        private int ReadRun(int colour)
        {
            var bits = colour == 0 ? MmrCodes.WhiteBits : MmrCodes.BlackBits;
            var total = 0;
            while (true)
            {
                var run = MmrCodes.LookupRun(colour, (int)Peek(bits));
                if (run.Length == 0)
                    throw new InvalidDataException($"CCITT {(colour == 0 ? "white" : "black")} run code is not in the T.4 tables");
                _bitPosition += run.Length;
                total += run.Value;
                if (total > (1 << 24))
                    throw new InvalidDataException("CCITT run is longer than any row");
                if (run.Value < MmrCodes.MakeupThreshold) return total;
            }
        }

        /// <summary>The next <paramref name="count"/> (at most 25) bits, MSB first; past the end they read as zero.</summary>
        private uint Peek(int count)
        {
            if (_bitPosition + count > (long)_data.Length * 8) Overran = true;
            var index = _bitPosition >> 3;
            var shift = (int)(_bitPosition & 7);
            uint window = 0;
            for (var i = 0; i < 4; i++)
            {
                var at = index + i;
                window = (window << 8) | (at < _data.Length ? _data[(int)at] : 0u);
            }
            return (window << shift) >> (32 - count);
        }
    }
}
