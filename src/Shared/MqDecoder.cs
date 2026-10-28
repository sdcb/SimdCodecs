using System;

// Compiled into each package that uses it (see the remarks below), under that package's namespace so
// the copies are distinct types.
#if SIMDCODECS_JBIG2
namespace Sdcb.SimdCodecs.Jbig2;
#elif SIMDCODECS_JPEG2000
namespace Sdcb.SimdCodecs.Jpeg2000;
#else
#error MqDecoder.cs is shared source for Sdcb.SimdCodecs.Jbig2 and Sdcb.SimdCodecs.Jpeg2000 only.
#endif

/// <summary>
/// One row of the MQ-coder probability estimation table (ITU-T T.88 Table E.1):
/// the LPS sub-interval size <see cref="Qe"/>, the next index after an MPS
/// (<see cref="Nmps"/>) or an LPS (<see cref="Nlps"/>) renormalization, and
/// whether an LPS renormalization also swaps the sense of the MPS
/// (<see cref="Switch"/>).
/// </summary>
internal readonly record struct MqState(ushort Qe, byte Nmps, byte Nlps, byte Switch);

/// <summary>
/// The MQ arithmetic decoder that ITU-T T.88 Annex E and ITU-T T.800 Annex C
/// both specify — the same coder, byte for byte, with the same <c>Qe</c> table
/// (T.88 Table E.1 = T.800 Table C.2). It is the entropy back-end under every
/// arithmetically-coded JBIG2 region and every JPEG 2000 code-block.
/// <para>
/// One source file (<c>src/Shared</c>) is compiled into both packages, so there is
/// a single copy to review and it cannot drift. It is not shared as a compiled
/// type: an <c>internal</c> type one package lends another through
/// <c>InternalsVisibleTo</c> is an API without a version contract, which breaks
/// at run time (<c>MissingMethodException</c>) when the two packages are
/// upgraded separately. Deliberately <c>internal</c> rather than public API:
/// JPEG 2000's arithmetic-bypass, RESTART and predictable-termination modes may
/// yet want to reshape the surface, and that freedom is worth more than
/// exporting a spec primitive.
/// </para>
/// <para>
/// <b>The coder is shared; its initialisation is not.</b> T.88 and T.800 seed
/// their context state differently — T.800 starts context 0 at index 4,
/// RUNLENGTH at 3 and UNIFORM at 46, where T.88 starts every context at 0. That
/// is the caller's table, not this class's, which is why the contexts live in a
/// caller-owned span (below). Getting it wrong decodes the first code-block
/// plausibly and then drifts.
/// </para>
/// <para>
/// A <c>ref struct</c> so the coded bytes stay a <see cref="ReadOnlySpan{T}"/>
/// all the way down — a scanned page's region data can be tens of megabytes and
/// there is no reason to copy it. Callers hold one on the stack and pass it by
/// <c>ref</c> into the region decoders.
/// </para>
/// <para>
/// Adaptive contexts live outside the decoder, in a caller-owned
/// <c>Span&lt;byte&gt;</c> indexed by CX value, one byte per context packed as
/// <c>(I &lt;&lt; 1) | MPS</c>. T.88 resets or retains those per region, which is
/// the caller's decision, not the coder's.
/// </para>
/// </summary>
internal ref struct MqDecoder
{
    // T.88 Table E.1, which is T.800 Table C.2. Index 46 is the
    // non-renormalizing terminal state.
    private static readonly MqState[] Table =
    [
        new(0x5601,  1,  1, 1), new(0x3401,  2,  6, 0), new(0x1801,  3,  9, 0), new(0x0AC1,  4, 12, 0),
        new(0x0521,  5, 29, 0), new(0x0221, 38, 33, 0), new(0x5601,  7,  6, 1), new(0x5401,  8, 14, 0),
        new(0x4801,  9, 14, 0), new(0x3801, 10, 14, 0), new(0x3001, 11, 17, 0), new(0x2401, 12, 18, 0),
        new(0x1C01, 13, 20, 0), new(0x1601, 29, 21, 0), new(0x5601, 15, 14, 1), new(0x5401, 16, 14, 0),
        new(0x5101, 17, 15, 0), new(0x4801, 18, 16, 0), new(0x3801, 19, 17, 0), new(0x3401, 20, 18, 0),
        new(0x3001, 21, 19, 0), new(0x2801, 22, 19, 0), new(0x2401, 23, 20, 0), new(0x2201, 24, 21, 0),
        new(0x1C01, 25, 22, 0), new(0x1801, 26, 23, 0), new(0x1601, 27, 24, 0), new(0x1401, 28, 25, 0),
        new(0x1201, 29, 26, 0), new(0x1101, 30, 27, 0), new(0x0AC1, 31, 28, 0), new(0x09C1, 32, 29, 0),
        new(0x08A1, 33, 30, 0), new(0x0521, 34, 31, 0), new(0x0441, 35, 32, 0), new(0x02A1, 36, 33, 0),
        new(0x0221, 37, 34, 0), new(0x0141, 38, 35, 0), new(0x0111, 39, 36, 0), new(0x0085, 40, 37, 0),
        new(0x0049, 41, 38, 0), new(0x0025, 42, 39, 0), new(0x0015, 43, 40, 0), new(0x0009, 44, 41, 0),
        new(0x0005, 45, 42, 0), new(0x0001, 45, 43, 0), new(0x5601, 46, 46, 0),
    ];

    private readonly ReadOnlySpan<byte> _data;
    private int _bp;
    private uint _c;
    private uint _a;
    private int _ct;

    /// <summary>
    /// INITDEC (T.88 E.3.5, software conventions): primes the code register from
    /// the first two bytes of <paramref name="data"/> and sets the interval to
    /// its full width.
    /// </summary>
    public MqDecoder(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bp = 0;
        _c = (uint)ByteAt(0) << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>The T.88 Table E.1 / T.800 Table C.2 row for a state index; exposed for table-conformance tests.</summary>
    internal static MqState StateAt(int index) => Table[index];

    /// <summary>Number of rows in T.88 Table E.1 / T.800 Table C.2.</summary>
    internal static int StateCount => Table.Length;

    /// <summary>
    /// DECODE (T.88 E.3.2): decodes one binary decision against the adaptive
    /// context stored at <paramref name="index"/> in <paramref name="contexts"/>,
    /// updating that context's state in place. Returns 0 or 1.
    /// </summary>
    public int Decode(scoped Span<byte> contexts, int index)
    {
        var packed = contexts[index];
        var state = Table[packed >> 1];
        var mps = (uint)(packed & 1);
        uint qe = state.Qe;
        uint d;

        _a -= qe;

        if ((_c >> 16) < qe)
        {
            // LPS_EXCHANGE (T.88 E.3.4). The conditional exchange: when the
            // remaining MPS sub-interval is the smaller of the two, the roles of
            // MPS and LPS swap for this decision.
            if (_a < qe)
            {
                d = mps;
                contexts[index] = (byte)(((uint)state.Nmps << 1) | mps);
            }
            else
            {
                d = 1 - mps;
                if (state.Switch != 0) mps = 1 - mps;
                contexts[index] = (byte)(((uint)state.Nlps << 1) | mps);
            }

            _a = qe;
            Renormalize();
        }
        else
        {
            _c -= qe << 16;

            // The common case: interval still normalized, no state change at all.
            if ((_a & 0x8000) != 0) return (int)mps;

            // MPS_EXCHANGE (T.88 E.3.3).
            if (_a < qe)
            {
                d = 1 - mps;
                if (state.Switch != 0) mps = 1 - mps;
                contexts[index] = (byte)(((uint)state.Nlps << 1) | mps);
            }
            else
            {
                d = mps;
                contexts[index] = (byte)(((uint)state.Nmps << 1) | mps);
            }

            Renormalize();
        }

        return (int)d;
    }

    /// <summary>RENORMD (T.88 E.3.3): shift until the interval is normalized, refilling as needed.</summary>
    private void Renormalize()
    {
        do
        {
            if (_ct == 0) ByteIn();
            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);
    }

    /// <summary>
    /// BYTEIN (T.88 E.3.4): feeds the next compressed byte into the code
    /// register, honouring the <c>0xFF</c> stuffing convention. Past the end of
    /// the data every byte reads as <c>0xFF</c>, which is what drives the
    /// marker-detected branch and lets a truncated segment keep producing
    /// decisions instead of faulting — T.88's intended behaviour, not a
    /// workaround.
    /// </summary>
    private void ByteIn()
    {
        if (ByteAt(_bp) == 0xFF)
        {
            if (ByteAt(_bp + 1) > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _bp++;
                _c += (uint)ByteAt(_bp) << 9;
                _ct = 7;
            }
        }
        else
        {
            _bp++;
            _c += (uint)ByteAt(_bp) << 8;
            _ct = 8;
        }
    }

    private readonly byte ByteAt(int i) => (uint)i < (uint)_data.Length ? _data[i] : (byte)0xFF;
}
