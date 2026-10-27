using System;
using System.IO;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// TIFF's LZW variant (TIFF 6.0 section 13), the other compression a real-world TIFF is likely to use.
///
/// <para><b>It is not GIF's LZW and not raw DEFLATE.</b> Codes are packed <b>MSB-first</b> (GIF packs
/// LSB-first), and the code width grows on the <i>early change</i> schedule: it steps up one code
/// BEFORE the table is actually full -- to 10 bits when the next free code is 511, not 512, and
/// likewise at 1023 and 2047. Both quirks decode plausibly for the first few hundred codes and then
/// diverge, so an implementation that gets either wrong produces a picture that starts correct and
/// turns to noise partway down. libtiff and Photoshop both write early-change, which is what makes it
/// the compatible reading rather than merely one option.</para>
///
/// <para>The table is stored as a prefix chain -- <c>(prefix code, one byte)</c> per entry -- rather
/// than as materialised strings, so adding an entry is O(1) and needs no allocation. Expansion walks
/// the chain, which yields the bytes in reverse, hence the scratch buffer and the backwards fill.</para>
///
/// <para>A predictor, if the file declares one, is applied by the reader AFTER this returns. Nothing
/// here knows about predictors, and LZW plus horizontal differencing is Photoshop's own default pair,
/// so the two must compose rather than one implying the other.</para>
/// </summary>
internal static class TiffLzw
{
    private const int ClearCode = 256;
    private const int EoiCode = 257;
    private const int FirstFreeCode = 258;
    private const int MaxCode = 4096;

    /// <summary>
    /// Decode one LZW-compressed strip into <paramref name="dst"/>, returning the number of bytes
    /// written. Stops at end-of-information, at the end of the input, or when the destination is full
    /// -- a strip that decodes to more than its share is truncated rather than throwing, matching how
    /// the uncompressed path clamps.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        // prefix[c] is the code for everything but the last byte of entry c; suffix[c] is that byte.
        var prefix = new short[MaxCode];
        var suffix = new byte[MaxCode];
        // Longest possible entry, so the reversal scratch never needs bounds checks beyond this.
        var scratch = new byte[MaxCode];

        var nextCode = FirstFreeCode;
        var codeWidth = 9;
        var bitPos = 0L;
        var totalBits = (long)src.Length * 8;
        var written = 0;
        var oldCode = -1;

        while (true)
        {
            var code = ReadCode(src, ref bitPos, totalBits, codeWidth);
            if (code < 0 || code == EoiCode)
            {
                break;
            }

            if (code == ClearCode)
            {
                nextCode = FirstFreeCode;
                codeWidth = 9;
                oldCode = -1;
                continue;
            }

            int emitCode;
            if (oldCode < 0)
            {
                // First code after a clear must be a literal; anything else is a corrupt stream.
                if (code >= FirstFreeCode)
                {
                    break;
                }
                emitCode = code;
            }
            else if (code < nextCode)
            {
                emitCode = code;
            }
            else if (code == nextCode)
            {
                // The KwKwK case: the encoder used an entry it defined with THIS code, which the decoder
                // has not built yet. The string is the previous one plus its own first byte.
                if (nextCode < MaxCode)
                {
                    prefix[nextCode] = (short)oldCode;
                    suffix[nextCode] = FirstByte(prefix, suffix, oldCode);
                    nextCode++;
                }
                emitCode = code;
                written += Emit(prefix, suffix, scratch, emitCode, dst, written);
                oldCode = code;
                codeWidth = WidthFor(nextCode, codeWidth);
                if (written >= dst.Length)
                {
                    break;
                }
                continue;
            }
            else
            {
                // A code beyond the next free one cannot be produced by a conforming encoder.
                break;
            }

            written += Emit(prefix, suffix, scratch, emitCode, dst, written);

            if (oldCode >= 0 && nextCode < MaxCode)
            {
                prefix[nextCode] = (short)oldCode;
                suffix[nextCode] = FirstByte(prefix, suffix, emitCode);
                nextCode++;
            }

            oldCode = code;
            codeWidth = WidthFor(nextCode, codeWidth);

            if (written >= dst.Length)
            {
                break;
            }
        }

        return written;
    }

    /// <summary>
    /// The early-change schedule: step up when the next FREE code reaches 511 / 1023 / 2047, one short
    /// of each power of two. Off by that one and a stream decodes correctly until the first boundary.
    /// </summary>
    private static int WidthFor(int nextCode, int current) => nextCode switch
    {
        >= 2047 => 12,
        >= 1023 => 11,
        >= 511 => 10,
        _ => current,
    };

    /// <summary>MSB-first bit reader. Returns -1 when the code would run past the input.</summary>
    private static int ReadCode(ReadOnlySpan<byte> src, ref long bitPos, long totalBits, int width)
    {
        if (bitPos + width > totalBits)
        {
            return -1;
        }

        var value = 0;
        for (var i = 0; i < width; i++)
        {
            var b = src[(int)(bitPos >> 3)];
            var bit = (b >> (7 - (int)(bitPos & 7))) & 1;
            value = (value << 1) | bit;
            bitPos++;
        }
        return value;
    }

    private static byte FirstByte(short[] prefix, byte[] suffix, int code)
    {
        while (code >= FirstFreeCode)
        {
            code = prefix[code];
        }
        return code < ClearCode ? (byte)code : suffix[code];
    }

    /// <summary>
    /// Expand one code into <paramref name="dst"/> at <paramref name="at"/>, returning how many bytes
    /// were written (which may be fewer than the entry's length when the destination fills).
    /// </summary>
    private static int Emit(short[] prefix, byte[] suffix, byte[] scratch, int code, Span<byte> dst, int at)
    {
        // The chain yields bytes last-to-first, so collect then reverse.
        var len = 0;
        var c = code;
        while (c >= FirstFreeCode && len < scratch.Length)
        {
            scratch[len++] = suffix[c];
            c = prefix[c];
        }
        if (len < scratch.Length)
        {
            scratch[len++] = (byte)c;
        }

        var room = dst.Length - at;
        var n = Math.Min(len, room);
        for (var i = 0; i < n; i++)
        {
            dst[at + i] = scratch[len - 1 - i];
        }
        return n;
    }
}
