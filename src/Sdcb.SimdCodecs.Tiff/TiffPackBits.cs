namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// PackBits (TIFF 6.0 section 9, compression 32773): a byte-oriented run-length code. Each run starts
/// with a signed header n: 0..127 copies the next n+1 bytes literally, -1..-127 repeats the next one
/// byte 1-n times, and -128 is a no-op.
/// </summary>
internal static class TiffPackBits
{
    /// <summary>
    /// Decode one segment into <paramref name="dst"/>, returning the bytes written. Input that ends
    /// early, or a run that would pass the end of the output, stops there: a truncated literal yields
    /// what is present and a long replicate is cut to the room left, so nothing is written past the
    /// buffer and the caller sees a short count instead of an exception.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var i = 0;
        var o = 0;
        while (i < src.Length && o < dst.Length)
        {
            var n = (sbyte)src[i++];
            if (n >= 0)
            {
                var count = Math.Min(Math.Min(n + 1, src.Length - i), dst.Length - o);
                src.Slice(i, count).CopyTo(dst.Slice(o));
                i += count;
                o += count;
                if (count < n + 1) break;
            }
            else if (n != -128)
            {
                if (i >= src.Length) break;
                var count = Math.Min(1 - n, dst.Length - o);
                dst.Slice(o, count).Fill(src[i++]);
                o += count;
            }
        }
        return o;
    }
}
