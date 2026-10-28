using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// The chunk CRC of PNG (ISO 3309 / ITU-T V.42: polynomial 0x04C11DB7, reflected, initial and final
/// value 0xFFFFFFFF). The state below is the raw, reflected register; <see cref="Compute"/> applies the
/// initial and final inversion.
/// <para>A byte-at-a-time table walk is a serial chain of load + xor + shift per byte, and it was a sixth
/// of a photo's decode time. ARM64 has the polynomial in hardware (<c>CRC32X</c>, eight bytes per
/// instruction). Elsewhere eight bytes go through eight 256-entry tables at once ("slicing-by-8",
/// Kounavis and Berry, <i>A Systematic Approach to Building High Performance Software-based CRC
/// Generators</i>, ISCC 2005): table k holds the CRC of a byte followed by k zero bytes, so the
/// eight lookups are independent and only their xor joins the chain.</para>
/// </summary>
internal static class PngCrc32
{
    private static readonly uint[] Tables = BuildTables();

    /// <summary>The CRC of <paramref name="a"/> followed by <paramref name="b"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        Update(Update(0xFFFFFFFFu, a), b) ^ 0xFFFFFFFFu;

    /// <summary>Advances the raw register <paramref name="crc"/> over <paramref name="data"/>.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var i = 0;
        if (ArmCrc32.Arm64.IsSupported)
        {
            for (; i + 8 <= data.Length; i += 8)
                crc = ArmCrc32.Arm64.ComputeCrc32(crc, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i, 8)));
        }
        else
        {
            // Every table index below is a byte, and each table has 256 entries: the reads stay in range;
            // the 8-byte read at i is inside data while i + 8 <= data.Length.
            ref var t = ref MemoryMarshal.GetArrayDataReference(Tables);
            ref var d = ref MemoryMarshal.GetReference(data);
            for (; i + 8 <= data.Length; i += 8)
            {
                var word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref d, i));
                if (!BitConverter.IsLittleEndian)
                    word = BinaryPrimitives.ReverseEndianness(word);
                var lo = crc ^ (uint)word;
                var hi = (uint)(word >> 32);
                crc = Unsafe.Add(ref t, 7 * 256 + (int)(lo & 0xFF))
                    ^ Unsafe.Add(ref t, 6 * 256 + (int)((lo >> 8) & 0xFF))
                    ^ Unsafe.Add(ref t, 5 * 256 + (int)((lo >> 16) & 0xFF))
                    ^ Unsafe.Add(ref t, 4 * 256 + (int)(lo >> 24))
                    ^ Unsafe.Add(ref t, 3 * 256 + (int)(hi & 0xFF))
                    ^ Unsafe.Add(ref t, 2 * 256 + (int)((hi >> 8) & 0xFF))
                    ^ Unsafe.Add(ref t, 256 + (int)((hi >> 16) & 0xFF))
                    ^ Unsafe.Add(ref t, (int)(hi >> 24));
            }
        }

        for (; i < data.Length; i++)
            crc = Tables[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildTables()
    {
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }

        // Table k: the byte, then k zero bytes.
        for (var k = 1; k < 8; k++)
        {
            for (var n = 0; n < 256; n++)
            {
                var previous = t[(k - 1) * 256 + n];
                t[k * 256 + n] = (previous >> 8) ^ t[previous & 0xFF];
            }
        }
        return t;
    }
}
