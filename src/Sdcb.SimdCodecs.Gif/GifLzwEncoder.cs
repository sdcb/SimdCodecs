namespace Sdcb.SimdCodecs.Gif;

/// <summary>
/// The GIF89a Appendix F LZW encoder, the exact counterpart of <see cref="GifLzw.Decode"/>: codes least
/// significant bit first, a clear code first, the table cleared when it reaches 4096 codes, and the
/// end-of-information code last, all packed into data sub-blocks of at most 255 bytes.
/// <para>
/// The encoder's table runs one entry ahead of the decoder's (it defines an entry as it emits a code; the
/// decoder only when it reads the next), so a code goes out at the width the decoder will read it with:
/// one bit wider once the decoder's next free code, the encoder's minus one, reaches <c>2^width</c>.
/// </para>
/// </summary>
internal static class GifLzwEncoder
{
    private const int MaxCodes = 4096;
    private const int HashSize = 1 << 13; // over twice the table, so probes stay short

    /// <summary>Writes <paramref name="indices"/> (each below <c>2^minCodeSize</c>) as sub-blocks, terminator included.</summary>
    public static void Encode(ReadOnlySpan<byte> indices, int minCodeSize, Stream output)
    {
        var keys = new int[HashSize];   // (prefix << 8 | pixel) + 1, or 0 when empty
        var codes = new short[HashSize];
        var bits = new SubBlockWriter(output);

        int clear = 1 << minCodeSize, end = clear + 1;
        var size = minCodeSize + 1;
        var next = clear + 2;
        bits.Write(clear, size);

        if (indices.IsEmpty)
        {
            bits.Write(end, size);
            bits.Finish();
            return;
        }

        int prefix = indices[0];
        for (var i = 1; i < indices.Length; i++)
        {
            int pixel = indices[i];
            var key = (prefix << 8) | pixel;
            var slot = Hash(key);
            while (keys[slot] != 0 && keys[slot] != key + 1) slot = (slot + 1) & (HashSize - 1);
            if (keys[slot] != 0)
            {
                prefix = codes[slot];
                continue;
            }

            bits.Write(prefix, size);
            keys[slot] = key + 1;
            codes[slot] = (short)next;
            Advance(ref next, ref size);
            if (next == MaxCodes)
            {
                bits.Write(clear, size);
                Array.Clear(keys);
                (next, size) = (clear + 2, minCodeSize + 1);
            }
            prefix = pixel;
        }

        bits.Write(prefix, size);
        // The decoder defines an entry on reading that last code too, which can widen the end code.
        if (next < MaxCodes) Advance(ref next, ref size);
        bits.Write(end, size);
        bits.Finish();
    }

    private static void Advance(ref int next, ref int size)
    {
        next++;
        if (next - 1 == 1 << size && size < 12) size++;
    }

    private static int Hash(int key) => (int)((uint)(key * -1640531535) >> (32 - 13)); // Fibonacci hashing

    /// <summary>Packs codes least significant bit first into 255-byte data sub-blocks.</summary>
    private sealed class SubBlockWriter(Stream output)
    {
        private readonly byte[] _block = new byte[255];
        private int _length;
        private ulong _acc;
        private int _count;

        public void Write(int code, int size)
        {
            _acc |= (ulong)(uint)code << _count;
            _count += size;
            while (_count >= 8)
            {
                Put((byte)_acc);
                _acc >>= 8;
                _count -= 8;
            }
        }

        public void Finish()
        {
            if (_count > 0) Put((byte)_acc);
            if (_length > 0) Flush();
            output.WriteByte(0); // block terminator
        }

        private void Put(byte b)
        {
            _block[_length++] = b;
            if (_length == 255) Flush();
        }

        private void Flush()
        {
            output.WriteByte((byte)_length);
            output.Write(_block, 0, _length);
            _length = 0;
        }
    }
}
