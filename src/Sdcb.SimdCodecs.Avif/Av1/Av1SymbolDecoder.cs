using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 multi-symbol arithmetic entropy decoder (§8.2) using a 64-bit preloaded bit buffer.
/// </summary>
internal ref struct Av1SymbolDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bytePos;
    private ulong _dif;          // Bits [63..48] = 16-bit SymbolValue (< SymbolRange <= 65535); bits [47..48-_cnt] = preloaded inverted bits; below = 0.
    private int _cnt;            // Number of valid preloaded bits below bit 48 (invariant: >= 16 before every decode).
    private int _symbolRange;
    private int _symbolMaxBits;
    private readonly bool _disableCdfUpdate;

    internal Av1SymbolDecoder(ReadOnlySpan<byte> tileBytes, bool disableCdfUpdate)
    {
        _data = tileBytes;
        _disableCdfUpdate = disableCdfUpdate;

        int sz = tileBytes.Length;
        _symbolRange = 1 << 15;
        _symbolMaxBits = 8 * sz - 15;

        // Preload first 6 bytes (48 bits) inverted into bits [62..15] (bit 63 = 0 since initial SymbolValue is 15-bit < 32768).
        ulong dif = 0;
        int take = Math.Min(sz, 6);
        for (int i = 0; i < take; i++)
            dif |= (ulong)(byte)(~tileBytes[i]) << (56 - i * 8);
        for (int i = take; i < 6; i++)
            dif |= 0xFFUL << (56 - i * 8);
        _dif = dif >> 1;
        _bytePos = take;
        _cnt = 33; // 48 - 15 valid preloaded bits in [47..15]; bits [14..0] are 0.
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Refill()
    {
        int rem = _data.Length - _bytePos;
        if (rem >= 4)
        {
            uint w = ~BinaryPrimitives.ReadUInt32BigEndian(_data.Slice(_bytePos));
            _dif |= (ulong)w << (16 - _cnt);
            _cnt += 32;
            _bytePos += 4;
        }
        else
        {
            while (_cnt < 16)
            {
                byte b = _bytePos < _data.Length ? (byte)(~_data[_bytePos++]) : (byte)0xFF;
                _dif |= (ulong)b << (40 - _cnt);
                _cnt += 8;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Renormalize()
    {
        int bits = BitOperations.LeadingZeroCount((uint)_symbolRange) - 16;
        _symbolRange <<= bits;
        _dif <<= bits;
        _cnt -= bits;
        _symbolMaxBits -= bits;
        if (_cnt < 16)
            Refill();
    }

    /// <summary>Decodes one binary symbol from a 3-element CDF [p0, 32768, count] (§8.2.6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadBoolCdf(scoped Span<ushort> cdf)
    {
        // Indices 0 and 2 only. Every caller passes a 3-element slice (AsSpan(offset, 3) of a CDF table, or ReadSymbol
        // dispatching on Length == 3), so they are in range.
        ref ushort r0 = ref MemoryMarshal.GetReference(cdf);
        int c0 = r0;
        int cur = (((_symbolRange >> 8) * ((32768 - c0) >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift)) + Av1Constants.EcMinProb;
        int val = (int)(_dif >> 48);
        int sym;
        if (val < cur)
        {
            sym = 1;
            _symbolRange = cur;
        }
        else
        {
            sym = 0;
            _symbolRange -= cur;
            _dif -= (ulong)cur << 48;
        }
        Renormalize();

        if (!_disableCdfUpdate)
        {
            int count = Unsafe.Add(ref r0, 2);
            int rate = 4 + (count >> 4);
            r0 = sym == 0
                ? (ushort)(c0 + ((32768 - c0) >> rate))
                : (ushort)(c0 - (c0 >> rate));
            if (count < 32)
                Unsafe.Add(ref r0, 2) = (ushort)(count + 1);
        }
        return sym;
    }

    /// <summary>Decodes one 4-way symbol from a 5-element CDF [p0, p1, p2, 32768, count] (§8.2.6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadSymbol4(scoped Span<ushort> cdf)
    {
        // Indices 0 .. 4 only. Every caller passes a 5-element slice (AsSpan(offset, 5), or ReadSymbol dispatching on
        // Length == 5), so they are in range.
        ref ushort r0 = ref MemoryMarshal.GetReference(cdf);
        int rangeShift = _symbolRange >> 8;
        int val = (int)(_dif >> 48);

        int c0 = r0;
        int cur0 = ((rangeShift * ((32768 - c0) >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift)) + 3 * Av1Constants.EcMinProb;
        int sym;
        int prev, cur;
        if (val >= cur0)
        {
            sym = 0;
            prev = _symbolRange;
            cur = cur0;
        }
        else
        {
            int c1 = Unsafe.Add(ref r0, 1);
            int cur1 = ((rangeShift * ((32768 - c1) >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift)) + 2 * Av1Constants.EcMinProb;
            if (val >= cur1)
            {
                sym = 1;
                prev = cur0;
                cur = cur1;
            }
            else
            {
                int c2 = Unsafe.Add(ref r0, 2);
                int cur2 = ((rangeShift * ((32768 - c2) >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift)) + Av1Constants.EcMinProb;
                if (val >= cur2)
                {
                    sym = 2;
                    prev = cur1;
                    cur = cur2;
                }
                else
                {
                    sym = 3;
                    prev = cur2;
                    cur = 0;
                }
            }
        }

        _symbolRange = prev - cur;
        _dif -= (ulong)cur << 48;
        Renormalize();

        if (!_disableCdfUpdate)
        {
            int count = Unsafe.Add(ref r0, 4);
            int rate = 5 + (count >> 4); // 3 + (count>>4) + min(log2(4), 2) = 5 + (count>>4)
            int a0 = r0;
            int a1 = Unsafe.Add(ref r0, 1);
            int a2 = Unsafe.Add(ref r0, 2);
            r0 = sym == 0 ? (ushort)(a0 + ((32768 - a0) >> rate)) : (ushort)(a0 - (a0 >> rate));
            Unsafe.Add(ref r0, 1) = sym <= 1 ? (ushort)(a1 + ((32768 - a1) >> rate)) : (ushort)(a1 - (a1 >> rate));
            Unsafe.Add(ref r0, 2) = sym <= 2 ? (ushort)(a2 + ((32768 - a2) >> rate)) : (ushort)(a2 - (a2 >> rate));
            if (count < 32)
                Unsafe.Add(ref r0, 4) = (ushort)(count + 1);
        }

        return sym;
    }

    /// <summary>Decodes one symbol using CDF of length N + 1 and updates the CDF (§8.2.6).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadSymbol(scoped Span<ushort> cdf)
    {
        int n = cdf.Length - 1;
        if (n == 2) return ReadBoolCdf(cdf);
        if (n == 4) return ReadSymbol4(cdf);
        return ReadSymbolGeneral(cdf, n);
    }

    private int ReadSymbolGeneral(scoped Span<ushort> cdf, int n)
    {
        // Every Unsafe.Add below reads index <= n = cdf.Length - 1: the search stops at symbol n - 1 even when the
        // table's cdf[n - 1] is not the 32768 terminator (which ends it there on its own), and the update loops stay
        // below n - 1. n >= 1, since ReadSymbol sends lengths 3 and 5 elsewhere and every CDF has a count slot.
        ref ushort r0 = ref MemoryMarshal.GetReference(cdf);
        int cur = _symbolRange;
        int prev;
        int symbol = -1;
        int rangeShift = _symbolRange >> 8;
        int val = (int)(_dif >> 48);

        do
        {
            symbol++;
            prev = cur;
            int f = 32768 - Unsafe.Add(ref r0, symbol);
            cur = (rangeShift * (f >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift);
            cur += Av1Constants.EcMinProb * (n - symbol - 1);
        } while (val < cur && symbol < n - 1);

        _symbolRange = prev - cur;
        _dif -= (ulong)cur << 48;
        Renormalize();

        if (!_disableCdfUpdate)
        {
            int count = Unsafe.Add(ref r0, n);
            int rate = 3 + (count >> 4) + Math.Min(BitOperations.Log2((uint)n), 2);
            int end = Math.Min(symbol, n - 1);
            for (int i = 0; i < end; i++)
            {
                int c = Unsafe.Add(ref r0, i);
                Unsafe.Add(ref r0, i) = (ushort)(c - (c >> rate));
            }
            for (int i = symbol; i < n - 1; i++)
            {
                int c = Unsafe.Add(ref r0, i);
                Unsafe.Add(ref r0, i) = (ushort)(c + ((32768 - c) >> rate));
            }
            if (count < 32)
                Unsafe.Add(ref r0, n) = (ushort)(count + 1);
        }

        return symbol;
    }

    /// <summary>Decodes one symbol using a temporary CDF without updating it (e.g., split_or_horz/vert).</summary>
    internal int ReadSymbolNoUpdate(scoped ReadOnlySpan<ushort> cdf)
    {
        // The search reads cdf[0 .. n - 1] only: it stops at symbol n - 1 at the latest.
        int n = cdf.Length - 1;
        if (n < 1)
            throw new ArgumentException("A CDF needs at least one symbol and the count slot.", nameof(cdf));
        ref ushort r0 = ref MemoryMarshal.GetReference(cdf);
        int cur = _symbolRange;
        int prev;
        int symbol = -1;
        int rangeShift = _symbolRange >> 8;
        int val = (int)(_dif >> 48);

        do
        {
            symbol++;
            prev = cur;
            int f = 32768 - Unsafe.Add(ref r0, symbol);
            cur = (rangeShift * (f >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift);
            cur += Av1Constants.EcMinProb * (n - symbol - 1);
        } while (val < cur && symbol < n - 1);

        _symbolRange = prev - cur;
        _dif -= (ulong)cur << 48;
        Renormalize();
        return symbol;
    }

    /// <summary>Equi-probable boolean (§8.2.3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadBool()
    {
        int cur = ((_symbolRange >> 8) << 7) + 4;
        int val = (int)(_dif >> 48);
        if (val < cur)
        {
            _symbolRange = cur;
            Renormalize();
            return 1;
        }
        _symbolRange -= cur;
        _dif -= (ulong)cur << 48;
        Renormalize();
        return 0;
    }

    /// <summary>Equi-probable n-bit literal (§8.2.5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReadLiteral(int n)
    {
        int x = 0;
        for (int i = 0; i < n; i++)
            x = (x << 1) | ReadBool();
        return x;
    }

    internal void Exit()
    {
        if (_symbolMaxBits < -14)
            throw new InvalidDataException("AV1 symbol decoder overran tile payload.");
    }
}
