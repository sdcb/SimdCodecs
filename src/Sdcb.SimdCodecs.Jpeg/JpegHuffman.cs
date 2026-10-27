namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// One JPEG Huffman table: the canonical code assignment of T.81 Annex C (the
/// maxcode/delta ladder, as in stb_image's public-domain <c>stbi__build_huffman</c>)
/// plus a <see cref="LutBits"/>-bit lookup table (<see cref="BuildLut"/>) that
/// resolves short codes — and, for AC tables, short codes together with their
/// magnitude bits — in one probe.
/// </summary>
internal sealed class JpegHuffman
{
    public const int LutBits = 11;
    public const int LutFull = 1 << 10;

    /// <summary>
    /// Bit position of the code length (4 bits). Bits 0–4 hold the bits to consume and bit 5
    /// is always 0, so a 64-bit shift by the whole entry (the count is taken mod 64) consumes
    /// exactly that many bits — the decoder's critical chain skips the masking step.
    /// </summary>
    public const int CodeLenShift = 6;

    /// <summary>Bit position of the signed value in a full entry (11 bits: magnitudes up to LutBits − 1 bits).</summary>
    public const int ValueShift = 21;

    public readonly int[] Lut = new int[1 << LutBits];
    public int Codes;
    public readonly ushort[] Code = new ushort[256];
    public readonly byte[] Values = new byte[256];
    public readonly byte[] Size = new byte[257];
    public readonly uint[] MaxCode = new uint[18];
    public readonly int[] Delta = new int[17];

    /// <summary>Back to the state of a new instance: no codes, an all-slow-path lookup table.</summary>
    public void Reset()
    {
        Array.Clear(Lut);
        Codes = 0;
        Array.Clear(Code);
        Array.Clear(Values);
        Array.Clear(Size);
        Array.Clear(MaxCode);
        Array.Clear(Delta);
    }

    /// <summary>Builds code tables from the 16 DHT length counts. Returns false on a malformed table.</summary>
    public bool Build(ReadOnlySpan<int> count)
    {
        int k = 0;
        for (var i = 0; i < 16; ++i)
        {
            for (var j = 0; j < count[i]; ++j)
            {
                Size[k++] = (byte)(i + 1);
                if (k >= 257)
                    return false; // bad size list
            }
        }

        Size[k] = 0;
        uint code = 0;
        k = 0;
        int jj;
        for (jj = 1; jj <= 16; ++jj)
        {
            Delta[jj] = (int)(k - code);
            if (Size[k] == jj)
            {
                while (Size[k] == jj)
                    Code[k++] = (ushort)code++;

                if (code - 1 >= 1u << jj)
                    return false; // bad code lengths
            }

            MaxCode[jj] = code << (16 - jj);
            code <<= 1;
        }

        MaxCode[jj] = 0xffffffff;
        Codes = k;
        return true;
    }

    /// <summary>
    /// Builds <see cref="Lut"/> once <see cref="Values"/> is filled. Every code of at
    /// most <see cref="LutBits"/> bits gets all <c>2^(LutBits − len)</c> entries it
    /// prefixes. When the symbol's magnitude bits (its low nibble, as for AC
    /// run/size symbols) also fit in the window, the entry carries the sign-extended
    /// value too and is marked <see cref="LutFull"/>, so (run, value, total length)
    /// decode in a single lookup. Entry layout: bits 0–4 bits to consume, 5 zero, 6–9 code
    /// length, 10 <see cref="LutFull"/>, 11–18 symbol, 21–31 value (signed). Zero =
    /// code longer than the window (or no code): take the canonical slow path.
    /// </summary>
    public void BuildLut()
    {
        Array.Clear(Lut);
        for (var i = 0; i < Codes; ++i)
        {
            int s = Size[i];
            if (s > LutBits)
                continue;
            int sym = Values[i];
            var size = sym & 15;
            var first = Code[i] << (LutBits - s);
            var span = 1 << (LutBits - s);
            for (var j = 0; j < span; ++j)
            {
                var entry = s | (s << CodeLenShift) | (sym << 11);
                if (size == 0)
                {
                    entry |= LutFull;
                }
                else if (s + size <= LutBits)
                {
                    var bits = (j >> (LutBits - s - size)) & ((1 << size) - 1);
                    var v = bits < 1 << (size - 1) ? bits - (1 << size) + 1 : bits;
                    entry = (s + size) | (s << CodeLenShift) | LutFull | (sym << 11) | (v << ValueShift);
                }

                Lut[first + j] = entry;
            }
        }
    }
}