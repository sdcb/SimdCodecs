namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>
/// The CABAC arithmetic decoding engine and context model of H.265 §9.3.2, §9.3.4.3.
/// </summary>
/// <remarks>
/// <para>
/// The decoder reads its bins straight from an unescaped RBSP (<see cref="Nal.Unescape"/>) at a bit
/// position, exactly as clauses 9.3.4.3.2 to 9.3.4.3.5 describe: <c>read_bits(1)</c> in renormalisation,
/// the 9-bit <c>ivlOffset</c> at initialisation, and the byte-aligned bypass mode of
/// <see cref="DecodeBypass"/>. The bit position is therefore meaningful and is exposed as
/// <see cref="BitPosition"/> for the wavefront entry points of WPP, where each row's substream starts at
/// a byte offset given by the slice header's <c>entry_point_offset_minus1</c>.
/// </para>
/// <para>
/// Contexts are one byte each: <c>pStateIdx</c> in the high six bits (0..63) and <c>valMps</c> in the
/// lowest bit. <see cref="InitContexts"/> derives them per clause 9.3.2.2 with the <c>initValue</c>
/// tables of Table 9-5 to Table 9-37.
/// </para>
/// </remarks>
internal sealed class HevcCabac
{
    private const int NumContexts = HevcCabacTables.Count;

    private readonly byte[] _input;
    private readonly byte[] _ctx = new byte[NumContexts];
    private uint _range;

    // The 9-bit ivlOffset of the spec followed by _bits bits of look-ahead: _value = ivlOffset · 2^_bits + the
    // next _bits bits of the stream. Comparing ivlOffset with ivlCurrRange is comparing _value with
    // ivlCurrRange · 2^_bits, subtracting from it is subtracting that, and read_bits( 1 ) in renormalisation is
    // _bits − 1, so the engine computes exactly the spec's ivlOffset while touching memory once per few bytes.
    private ulong _value;
    private int _bits;
    private int _next; // index of the next byte of _input to load; bytes past the end read as 0

    /// <summary>Initialises the engine over the slice's unescaped RBSP starting at <paramref name="byteOffset"/>.</summary>
    internal HevcCabac(byte[] rbsp, int byteOffset)
    {
        _input = rbsp;
        InitEngine(byteOffset);
    }

    /// <summary>Current bit position of the slice data: the bits the spec's engine has read.</summary>
    internal long BitPosition => (long)_next * 8 - _bits;

    /// <summary>Restarts the arithmetic engine at <paramref name="byteOffset"/> (clause 9.3.2.6).</summary>
    internal void InitEngine(int byteOffset)
    {
        // 9.3.2.6: ivlCurrRange = 510, ivlOffset = read_bits( 9 ).
        _range = 510;
        _next = Math.Max(byteOffset, 0);
        _value = 0;
        _bits = -9;
        Refill();
    }

    /// <summary>Loads whole bytes until 47..54 look-ahead bits are buffered (ivlOffset's 9 bits on top fit in 64).</summary>
    private void Refill()
    {
        var count = (54 - _bits) >> 3;
        var next = _next;
        if (next <= _input.Length - 8)
        {
            var word = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(_input.AsSpan(next, 8));
            _value = (_value << (count * 8)) | (word >> (64 - count * 8));
        }
        else
        {
            for (var i = 0; i < count; i++)
            {
                var at = next + i;
                _value = (_value << 8) | ((uint)at < (uint)_input.Length ? _input[at] : 0u);
            }
        }
        _next = next + count;
        _bits += count * 8;
    }

    /// <summary>
    /// Initialises the context variables for the current slice (clause 9.3.2.2). <paramref name="initType"/>
    /// is 0 for I slices, which is the only slice type this decoder handles.
    /// </summary>
    internal void InitContexts(int sliceQpY, int initType)
    {
        if (initType != 0) throw new InvalidDataException("Only I slices are supported.");
        var qp = Math.Clamp(sliceQpY, 0, 51);
        for (var i = 0; i < HevcCabacTables.Count; i++)
        {
            _ctx[i] = (byte)HevcCabacTables.Initialize(HevcCabacTables.InitValue[i], qp);
        }
    }

    /// <summary>Copies the context variables out (the storage process of 9.3.2.4).</summary>
    internal void SaveContexts(byte[] destination) => Array.Copy(_ctx, destination, NumContexts);

    /// <summary>Restores contexts saved by <see cref="SaveContexts"/> (the WPP and dependent-slice synchronisation of 9.3.2.5).</summary>
    internal void LoadContexts(byte[] source) => Array.Copy(source, _ctx, NumContexts);

    /// <summary>DecodeDecision (clause 9.3.4.3.2): one regular bin against context <paramref name="ctxIdx"/>.</summary>
    internal uint DecodeBin(int ctxIdx)
    {
        if (_bits < 8) Refill(); // a decision reads at most 7 bits
        var state = _ctx[ctxIdx];
        uint lpsRange = HevcCabacTables.RangeTabLps[((state >> 1) << 2) | (int)((_range >> 6) & 3)];
        var range = _range - lpsRange;
        var scaled = (ulong)range << _bits;
        if (_value < scaled)
        {
            // MPS: ivlCurrRange stays ≥ 128, so renormalisation reads at most one bit.
            _ctx[ctxIdx] = HevcCabacTables.TransMps[state];
            if (range < 256)
            {
                range <<= 1;
                _bits--;
            }
            _range = range;
            return (uint)(state & 1);
        }

        _value -= scaled;
        // LPS: ivlCurrRange = rangeLps (≥ 6), renormalised to ≥ 256 in one step.
        var shift = System.Numerics.BitOperations.LeadingZeroCount(lpsRange) - 23;
        _range = lpsRange << shift;
        _bits -= shift;
        _ctx[ctxIdx] = HevcCabacTables.TransLps[state];
        return (uint)(state & 1) ^ 1;
    }

    /// <summary>DecodeBypass (clause 9.3.4.3.4).</summary>
    internal uint DecodeBypass()
    {
        if (_bits < 1) Refill();
        _bits--;
        var scaled = (ulong)_range << _bits;
        if (_value >= scaled)
        {
            _value -= scaled;
            return 1;
        }
        return 0;
    }

    /// <summary>DecodeTerminate (clause 9.3.4.3.5): used for end_of_slice_segment_flag and end_of_subset_one_bit.</summary>
    internal uint DecodeTerminate()
    {
        if (_bits < 8) Refill();
        _range -= 2;
        if (_value >= (ulong)_range << _bits) return 1; // decoding terminates; no renormalisation
        if (_range < 256)
        {
            _range <<= 1;
            _bits--;
        }
        return 0;
    }

    /// <summary>The binarization of §9.3.3 and its mapping onto bypass / terminate / context bins (§9.3.4.1).</summary>
    internal uint DecodeBypassBits(int count)
    {
        uint value = 0;
        for (var i = 0; i < count; i++) value = (value << 1) | DecodeBypass();
        return value;
    }

    /// <summary>Truncated unary with context bins 0..ctxEnd-1, then bypass bins up to cMax.</summary>
    internal uint DecodeTu(int cMax, int ctxOffset)
    {
        uint value = 0;
        while (value < cMax && DecodeBin(ctxOffset + (int)value) == 1) value++;
        return value;
    }

    /// <summary>Fixed-length bypass reading of a k-th order Exp-Golomb code (§9.3.3.3).</summary>
    internal uint DecodeExpGolomb(int order)
    {
        var value = 0u;
        while (DecodeBypass() == 1) value += 1u << order++;
        return value + DecodeBypassBits(order);
    }
}

/// <summary>The normative CABAC tables of H.265: Table 9-52, Table 9-53 and the initValue tables 9-5 to 9-37.</summary>
internal static class HevcCabacTables
{
    /// <summary>Table 9-52: <c>rangeTabLps[pStateIdx * 4 + qRangeIdx]</c>.</summary>
    internal static readonly byte[] RangeTabLps =
    [
        128, 176, 208, 240,
        128, 167, 197, 227,
        128, 158, 187, 216,
        123, 150, 178, 205,
        116, 142, 169, 195,
        111, 135, 160, 185,
        105, 128, 152, 175,
        100, 122, 144, 166,
        95, 116, 137, 158,
        90, 110, 130, 150,
        85, 104, 123, 142,
        81, 99, 117, 135,
        77, 94, 111, 128,
        73, 89, 105, 122,
        69, 85, 100, 116,
        66, 80, 95, 110,
        62, 76, 90, 104,
        59, 72, 86, 99,
        56, 69, 81, 94,
        53, 65, 77, 89,
        51, 62, 73, 85,
        48, 59, 69, 80,
        46, 56, 66, 76,
        43, 53, 63, 72,
        41, 50, 59, 69,
        39, 48, 56, 65,
        37, 45, 54, 62,
        35, 43, 51, 59,
        33, 41, 48, 56,
        32, 39, 46, 53,
        30, 37, 43, 50,
        29, 35, 41, 48,
        27, 33, 39, 45,
        26, 31, 37, 43,
        24, 30, 35, 41,
        23, 28, 33, 39,
        22, 27, 32, 37,
        21, 26, 30, 35,
        20, 24, 29, 33,
        19, 23, 27, 31,
        18, 22, 26, 30,
        17, 21, 25, 28,
        16, 20, 23, 27,
        15, 19, 22, 25,
        14, 18, 21, 24,
        14, 17, 20, 23,
        13, 16, 19, 22,
        12, 15, 18, 21,
        12, 14, 17, 20,
        11, 14, 16, 19,
        11, 13, 15, 18,
        10, 12, 15, 17,
        10, 12, 14, 16,
        9, 11, 13, 15,
        9, 11, 12, 14,
        8, 10, 12, 14,
        8, 9, 11, 13,
        7, 9, 11, 12,
        7, 9, 10, 12,
        7, 8, 10, 11,
        6, 8, 9, 11,
        6, 7, 9, 10,
        6, 7, 8, 9,
        2, 2, 2, 2,
    ];

    /// <summary>Table 9-53, transIdxLps.</summary>
    internal static readonly byte[] TransIdxLps =
    [
        0, 0, 1, 2, 2, 4, 4, 5, 6, 7, 8, 9, 9, 11, 11, 12,
        13, 13, 15, 15, 16, 16, 18, 18, 19, 19, 21, 21, 22, 22, 23, 24,
        24, 25, 26, 26, 27, 27, 28, 29, 29, 30, 30, 30, 31, 32, 32, 33,
        33, 33, 34, 34, 35, 35, 35, 36, 36, 36, 37, 37, 37, 38, 38, 63,
    ];

    /// <summary>
    /// The state transitions of clause 9.3.4.3.2.2 on the packed context byte <c>pStateIdx &lt;&lt; 1 | valMps</c>:
    /// after an MPS, pStateIdx follows transIdxMps (+1 up to 62); after an LPS it follows transIdxLps, and valMps
    /// flips at pStateIdx 0.
    /// </summary>
    internal static readonly byte[] TransMps = BuildTransitions(mps: true);

    /// <inheritdoc cref="TransMps"/>
    internal static readonly byte[] TransLps = BuildTransitions(mps: false);

    private static byte[] BuildTransitions(bool mps)
    {
        var table = new byte[128];
        for (var state = 0; state < 128; state++)
        {
            var stateIdx = state >> 1;
            var valMps = state & 1;
            int next;
            if (mps)
            {
                next = stateIdx >= 62 ? stateIdx : stateIdx + 1;
            }
            else
            {
                next = TransIdxLps[stateIdx];
                if (stateIdx == 0) valMps ^= 1;
            }
            table[state] = (byte)((next << 1) | valMps);
        }
        return table;
    }

    // Context layout for I slices (initType 0). Each syntax element's ctxIdx 0.. of its Table 9-x maps to
    // Offset + ctxInc; the values are the initType-0 columns of Tables 9-5 .. 9-31, selected by Table 9-4.
    internal const int SaoMerge = 0;          // 9-5
    internal const int SaoType = 1;           // 9-6
    internal const int SplitCu = 2;           // 9-7, ctxInc 0..2
    internal const int TransquantBypass = 5;  // 9-8
    internal const int PartMode = 6;          // 9-11, intra uses bin 0 only
    internal const int PrevIntraLuma = 7;     // 9-12
    internal const int IntraChroma = 8;       // 9-13
    internal const int SplitTransform = 9;    // 9-20, ctxInc 5 - log2TrafoSize (0..2)
    internal const int CbfLuma = 12;          // 9-21, ctxInc 0..1
    internal const int CbfChroma = 14;        // 9-22, ctxInc trafoDepth 0..4 (ctxIdx 0..3 and 12)
    internal const int CuQpDeltaAbs = 19;     // 9-24, ctxInc 0..1
    internal const int TransformSkip = 21;    // 9-25, luma 0, chroma 1 (ctxIdx 0 and 3)
    internal const int LastX = 23;            // 9-26, 18
    internal const int LastY = 41;            // 9-27, 18
    internal const int CodedSubBlock = 59;    // 9-28, 4
    internal const int Sig = 63;              // 9-29, 44 (0..41 and 126..127)
    internal const int Greater1 = 107;        // 9-30, 24
    internal const int Greater2 = 131;        // 9-31, 6
    internal const int Count = 137;

    /// <summary>initValue per context of the I-slice layout above.</summary>
    internal static readonly byte[] InitValue =
    [
        153,                                    // sao_merge_left/up_flag
        200,                                    // sao_type_idx
        139, 141, 157,                          // split_cu_flag
        154,                                    // cu_transquant_bypass_flag
        184,                                    // part_mode
        184,                                    // prev_intra_luma_pred_flag
        63,                                     // intra_chroma_pred_mode
        153, 138, 138,                          // split_transform_flag
        111, 141,                               // cbf_luma
        94, 138, 182, 154, 154,                 // cbf_cb / cbf_cr (ctxIdx 0..3, 12)
        154, 154,                               // cu_qp_delta_abs
        139, 139,                               // transform_skip_flag luma (0), chroma (3)
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_x_prefix
        110, 110, 124, 125, 140, 153, 125, 127, 140, 109, 111, 143, 127, 111, 79, 108, 123, 63, // last_sig_coeff_y_prefix
        91, 171, 134, 141,                      // coded_sub_block_flag
        111, 111, 125, 110, 110, 94, 124, 108, 124, 107, 125, 141, 179, 153, 125, 107, // sig_coeff_flag 0..15
        125, 141, 179, 153, 125, 107, 125, 141, 179, 153, 125, 140, 139, 182, 182, 152, // 16..31
        136, 152, 136, 153, 136, 139, 111, 136, 139, 111,                               // 32..41
        141, 111,                                                                       // 126..127
        140, 92, 137, 138, 140, 152, 138, 139, 153, 74, 149, 92, 139, 107, 122, 152,    // coeff_abs_level_greater1_flag
        140, 179, 166, 182, 140, 227, 122, 197,
        138, 153, 136, 167, 152, 152,           // coeff_abs_level_greater2_flag
    ];

    /// <summary>
    /// Clause 9.3.2.2: the packed <c>pStateIdx &lt;&lt; 1 | valMps</c> for one initValue entry at initType.
    /// </summary>
    internal static int Initialize(int initValue, int clippedSliceQpY)
    {
        var slopeIdx = initValue >> 4;
        var offsetIdx = initValue & 15;
        var m = slopeIdx * 5 - 45;
        var n = (offsetIdx << 3) - 16;
        var preCtxState = Math.Clamp(((m * clippedSliceQpY) >> 4) + n, 1, 126);
        var valMps = preCtxState <= 63 ? 0 : 1;
        var pStateIdx = valMps != 0 ? preCtxState - 64 : 63 - preCtxState;
        return (pStateIdx << 1) | valMps;
    }
}
