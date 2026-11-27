using System.Numerics;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 multi-symbol arithmetic entropy decoder (§8.2).
/// </summary>
internal ref struct Av1SymbolDecoder
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;
    private int _symbolValue;
    private int _symbolRange;
    private int _symbolMaxBits;
    private readonly bool _disableCdfUpdate;

    internal Av1SymbolDecoder(ReadOnlySpan<byte> tileBytes, bool disableCdfUpdate)
    {
        _data = tileBytes;
        _bitPos = 0;
        _disableCdfUpdate = disableCdfUpdate;

        int sz = tileBytes.Length;
        int numBits = Math.Min(sz * 8, 15);
        int buf = ReadRawBits(numBits);
        int paddedBuf = buf << (15 - numBits);
        _symbolValue = ((1 << 15) - 1) ^ paddedBuf;
        _symbolRange = 1 << 15;
        _symbolMaxBits = 8 * sz - 15;
    }

    private int ReadRawBits(int n)
    {
        if (n <= 0) return 0;
        int val = 0;
        int rem = n;
        while (rem > 0)
        {
            int byteIdx = _bitPos >> 3;
            int bitOff = _bitPos & 7;
            int avail = 8 - bitOff;
            int take = Math.Min(rem, avail);
            int shift = avail - take;
            int mask = (1 << take) - 1;
            int b = byteIdx < _data.Length ? _data[byteIdx] : 0;
            val = (val << take) | ((b >> shift) & mask);
            _bitPos += take;
            rem -= take;
        }
        return val;
    }

    /// <summary>Decodes one symbol using CDF of length N + 1 and updates the CDF (§8.2.6).</summary>
    internal int ReadSymbol(scoped Span<ushort> cdf)
    {
        int n = cdf.Length - 1;
        int cur = _symbolRange;
        int prev;
        int symbol = -1;
        int rangeShift = _symbolRange >> 8;

        do
        {
            symbol++;
            prev = cur;
            int f = 32768 - cdf[symbol];
            cur = (rangeShift * (f >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift);
            cur += Av1Constants.EcMinProb * (n - symbol - 1);
        } while (_symbolValue < cur);

        _symbolRange = prev - cur;
        _symbolValue -= cur;
        Renormalize();

        if (!_disableCdfUpdate)
        {
            int count = cdf[n];
            int rate = 3 + (count > 15 ? 1 : 0) + (count > 31 ? 1 : 0) + Math.Min(BitOperations.Log2((uint)n), 2);
            int tmp = 0;
            for (int i = 0; i < n - 1; i++)
            {
                if (i == symbol) tmp = 32768;
                int c = cdf[i];
                if (tmp < c)
                    cdf[i] = (ushort)(c - ((c - tmp) >> rate));
                else
                    cdf[i] = (ushort)(c + ((tmp - c) >> rate));
            }
            if (count < 32)
                cdf[n] = (ushort)(count + 1);
        }

        return symbol;
    }

    /// <summary>Decodes one symbol using a temporary CDF without updating it (e.g., split_or_horz/vert).</summary>
    internal int ReadSymbolNoUpdate(scoped ReadOnlySpan<ushort> cdf)
    {
        int n = cdf.Length - 1;
        int cur = _symbolRange;
        int prev;
        int symbol = -1;
        int rangeShift = _symbolRange >> 8;

        do
        {
            symbol++;
            prev = cur;
            int f = 32768 - cdf[symbol];
            cur = (rangeShift * (f >> Av1Constants.EcProbShift)) >> (7 - Av1Constants.EcProbShift);
            cur += Av1Constants.EcMinProb * (n - symbol - 1);
        } while (_symbolValue < cur);

        _symbolRange = prev - cur;
        _symbolValue -= cur;
        Renormalize();
        return symbol;
    }

    /// <summary>Equi-probable boolean (§8.2.3).</summary>
    internal int ReadBool()
    {
        // cdf = { 16384, 32768, 0 }, n = 2:
        // symbol=0: f=16384, cur = ((range>>8)*(16384>>6))>>1 + 4*1 = ((range>>8)*256)>>1 + 4 = (range & ~0xFF)>>1 + 4
        int cur = ((_symbolRange >> 8) << 7) + 4;
        int symbol;
        if (_symbolValue < cur)
        {
            symbol = 1;
            _symbolRange = cur;
        }
        else
        {
            symbol = 0;
            _symbolRange -= cur;
            _symbolValue -= cur;
        }
        Renormalize();
        return symbol;
    }

    /// <summary>Equi-probable n-bit literal (§8.2.5).</summary>
    internal int ReadLiteral(int n)
    {
        int x = 0;
        for (int i = 0; i < n; i++)
            x = (x << 1) | ReadBool();
        return x;
    }

    private void Renormalize()
    {
        int bits = 15 - BitOperations.Log2((uint)_symbolRange);
        if (bits == 0) return;
        _symbolRange <<= bits;
        int numBits = Math.Min(bits, Math.Max(0, _symbolMaxBits));
        int newData = ReadRawBits(numBits);
        int paddedData = newData << (bits - numBits);
        _symbolValue = paddedData ^ (((_symbolValue + 1) << bits) - 1);
        _symbolMaxBits -= bits;
    }

    internal void Exit()
    {
        if (_symbolMaxBits < -14)
            throw new InvalidDataException("AV1 symbol decoder overran tile payload.");
    }
}
