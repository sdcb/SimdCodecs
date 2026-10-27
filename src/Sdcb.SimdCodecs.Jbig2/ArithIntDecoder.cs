using System;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// The arithmetic integer decoding procedure of ITU-T T.88 Annex A — how every
/// <em>number</em> in a symbol dictionary or text region is coded, as opposed to
/// the pixels, which go through the generic and refinement templates.
/// <para>
/// A.2 codes an integer as a sign bit, then a prefix selecting one of six
/// magnitude ranges, then that many raw bits. The ranges are cumulative — 2 bits
/// from 0, 4 bits from 4, 6 from 20, 8 from 84, 12 from 340, 32 from 4436 — so
/// small values, which is nearly all of them, cost a handful of decisions.
/// </para>
/// <para>
/// The interesting part is <c>PREV</c>: the context for each decision is built
/// from the bits decoded so far <em>within this integer</em>, so the coder adapts
/// to the shape of the numbers a particular field carries. Once PREV would exceed
/// nine bits it is folded back into the top half of the 512-slot range, which
/// caps the context array while keeping the most recent bits significant. Each
/// field (IADH, IADW, IADT, …) owns its own instance, which is why T.88 names
/// them individually rather than sharing one integer decoder.
/// </para>
/// </summary>
internal sealed class ArithIntDecoder
{
    // A.3: PREV is confined to 9 bits, so 512 adaptive slots per field.
    private readonly byte[] _contexts = new byte[512];

    /// <summary>
    /// The out-of-band value A.2 defines: not a number, but "the sequence this
    /// field terminates has ended". Decoded as a negative zero — sign set,
    /// magnitude zero — which no in-band value can produce.
    /// </summary>
    public const int OutOfBand = int.MinValue;

    /// <summary>Resets every context, as T.88 requires between segments that do not retain state.</summary>
    public void Reset() => Array.Clear(_contexts);

    /// <summary>
    /// Decodes one integer (T.88 A.2), returning <see cref="OutOfBand"/> for OOB.
    /// </summary>
    public int Decode(ref MqDecoder mq)
    {
        var prev = 1;

        var negative = Bit(ref mq, ref prev);

        int value, offset;
        if (Bit(ref mq, ref prev) == 0) { value = Bits(ref mq, ref prev, 2); offset = 0; }
        else if (Bit(ref mq, ref prev) == 0) { value = Bits(ref mq, ref prev, 4); offset = 4; }
        else if (Bit(ref mq, ref prev) == 0) { value = Bits(ref mq, ref prev, 6); offset = 20; }
        else if (Bit(ref mq, ref prev) == 0) { value = Bits(ref mq, ref prev, 8); offset = 84; }
        else if (Bit(ref mq, ref prev) == 0) { value = Bits(ref mq, ref prev, 12); offset = 340; }
        else { value = Bits(ref mq, ref prev, 32); offset = 4436; }

        var magnitude = (long)(uint)value + offset;

        // A.2 step 5: a negative zero is not -0, it is OOB. Every other negative
        // is just itself.
        if (negative == 1 && magnitude == 0) return OutOfBand;
        if (magnitude > int.MaxValue)
            throw new InvalidDataException("JBIG2: arithmetic integer overflows a 32-bit value.");

        return negative == 1 ? (int)-magnitude : (int)magnitude;
    }

    /// <summary>
    /// Decodes a symbol ID (T.88 A.3, IAID). Unlike A.2 this is a plain
    /// fixed-width binary read down a context tree <paramref name="codeLength"/>
    /// deep, so its context array is sized by the symbol count rather than by the
    /// 512-slot PREV rule.
    /// </summary>
    public static int DecodeId(ref MqDecoder mq, scoped Span<byte> contexts, int codeLength)
    {
        var prev = 1;
        for (var i = 0; i < codeLength; i++)
            prev = (prev << 1) | mq.Decode(contexts, prev);

        return prev - (1 << codeLength);
    }

    /// <summary>Context array size for <see cref="DecodeId"/> at a given symbol code length.</summary>
    public static int IdContextSize(int codeLength) => 1 << (codeLength + 1);

    private int Bit(ref MqDecoder mq, ref int prev)
    {
        var bit = mq.Decode(_contexts, prev);

        // A.3: keep PREV inside nine bits. Below 256 it simply accumulates; at or
        // above, the top bit is pinned so the window slides instead of growing.
        prev = prev < 256 ? (prev << 1) | bit : ((((prev << 1) | bit) & 511) | 256);
        return bit;
    }

    private int Bits(ref MqDecoder mq, ref int prev, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | Bit(ref mq, ref prev);
        return value;
    }
}
