using System.Buffers.Binary;
using System.Numerics;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// MD5 (RFC 1321), written from the RFC's prose (§3.1–3.5): only Extended XMP needs it, to check a chunked packet
/// against the digest its GUID names. <c>System.Security.Cryptography.MD5</c> is not used because it throws
/// <see cref="PlatformNotSupportedException"/> on browser-wasm, and metadata must read the same on every platform.
/// Not for security: MD5 here is a checksum.
/// </summary>
internal static class JpegMd5
{
    // T[i] = floor(2^32 · |sin(i + 1)|) (RFC 1321 §3.4), evaluated once and written out so that no platform's
    // libm decides the constants.
    private static ReadOnlySpan<uint> T =>
    [
        0xD76AA478, 0xE8C7B756, 0x242070DB, 0xC1BDCEEE, 0xF57C0FAF, 0x4787C62A, 0xA8304613, 0xFD469501,
        0x698098D8, 0x8B44F7AF, 0xFFFF5BB1, 0x895CD7BE, 0x6B901122, 0xFD987193, 0xA679438E, 0x49B40821,
        0xF61E2562, 0xC040B340, 0x265E5A51, 0xE9B6C7AA, 0xD62F105D, 0x02441453, 0xD8A1E681, 0xE7D3FBC8,
        0x21E1CDE6, 0xC33707D6, 0xF4D50D87, 0x455A14ED, 0xA9E3E905, 0xFCEFA3F8, 0x676F02D9, 0x8D2A4C8A,
        0xFFFA3942, 0x8771F681, 0x6D9D6122, 0xFDE5380C, 0xA4BEEA44, 0x4BDECFA9, 0xF6BB4B60, 0xBEBFBC70,
        0x289B7EC6, 0xEAA127FA, 0xD4EF3085, 0x04881D05, 0xD9D4D039, 0xE6DB99E5, 0x1FA27CF8, 0xC4AC5665,
        0xF4292244, 0x432AFF97, 0xAB9423A7, 0xFC93A039, 0x655B59C3, 0x8F0CCC92, 0xFFEFF47D, 0x85845DD1,
        0x6FA87E4F, 0xFE2CE6E0, 0xA3014314, 0x4E0811A1, 0xF7537E82, 0xBD3AF235, 0x2AD7D2BB, 0xEB86D391,
    ];

    // The per-step left-rotation amounts of rounds 1–4 (§3.4), four per round, repeating.
    private static ReadOnlySpan<byte> Shift => [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21];

    /// <summary>Writes the 16-byte digest of <paramref name="data"/> to <paramref name="digest"/>.</summary>
    public static void HashData(ReadOnlySpan<byte> data, Span<byte> digest)
    {
        // §3.3: the buffer A, B, C, D, low-order bytes first.
        uint a = 0x67452301, b = 0xEFCDAB89, c = 0x98BADCFE, d = 0x10325476;

        var whole = data.Length & ~63;
        for (var i = 0; i < whole; i += 64)
            Block(data.Slice(i, 64), ref a, ref b, ref c, ref d);

        // §3.1–3.2: a 1 bit, zeros up to 56 mod 64 bytes, then the bit length as 64 bits, low-order word first.
        Span<byte> tail = stackalloc byte[128];
        tail.Clear();
        var rest = data.Length - whole;
        data[whole..].CopyTo(tail);
        tail[rest] = 0x80;
        var tailLength = rest < 56 ? 64 : 128;
        BinaryPrimitives.WriteUInt64LittleEndian(tail.Slice(tailLength - 8), (ulong)data.Length * 8);
        for (var i = 0; i < tailLength; i += 64)
            Block(tail.Slice(i, 64), ref a, ref b, ref c, ref d);

        // §3.5: the message digest is A, B, C, D, low-order byte first.
        BinaryPrimitives.WriteUInt32LittleEndian(digest, a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[4..], b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[8..], c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[12..], d);
    }

    /// <summary>§3.4 on one 16-word block: 64 steps <c>a = b + ((a + f(b, c, d) + X[k] + T[i]) &lt;&lt;&lt; s)</c>, the
    /// roles of a, b, c, d rotating one place each step.</summary>
    private static void Block(ReadOnlySpan<byte> block, ref uint a0, ref uint b0, ref uint c0, ref uint d0)
    {
        Span<uint> x = stackalloc uint[16];
        for (var j = 0; j < 16; j++)
            x[j] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(j * 4, 4));

        uint a = a0, b = b0, c = c0, d = d0;
        var t = T;
        var shift = Shift;
        for (var i = 0; i < 64; i++)
        {
            uint f;
            int k;
            switch (i >> 4)
            {
                case 0: f = (b & c) | (~b & d); k = i; break;            // F, X[i]
                case 1: f = (b & d) | (c & ~d); k = (1 + 5 * i) & 15; break; // G, X[(1 + 5i) mod 16]
                case 2: f = b ^ c ^ d; k = (5 + 3 * i) & 15; break;      // H, X[(5 + 3i) mod 16]
                default: f = c ^ (b | ~d); k = 7 * i & 15; break;        // I, X[7i mod 16]
            }

            var rotated = BitOperations.RotateLeft(a + f + x[k] + t[i], shift[(i >> 4) * 4 + (i & 3)]);
            a = d;
            d = c;
            c = b;
            b += rotated;
        }

        a0 += a;
        b0 += b;
        c0 += c;
        d0 += d;
    }
}
