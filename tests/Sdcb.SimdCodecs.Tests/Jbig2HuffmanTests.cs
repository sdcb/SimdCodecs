using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The Huffman layer of T.88 Annex B: the standard tables, canonical code
/// assignment, custom table segments, and their resource bounds. Whether the
/// tables are the spec's is settled against jbig2dec in
/// <c>Jbig2ExtendedOracleTests</c> and on the issue #1 corpus.
/// </summary>
public sealed class Jbig2HuffmanTests
{
    /// <summary>
    /// Every standard table is a prefix code whose Kraft sum is exactly 1, except B.7,
    /// whose lines leave 1/16 of the code space unused. A misremembered line length
    /// would almost always break this.
    /// </summary>
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public void StandardTable_IsAPrefixCode(int number)
    {
        var sum = HuffmanTable.StandardLines(number)
            .Where(l => l.PrefixLength > 0)
            .Sum(l => Math.ScaleB(1.0, -l.PrefixLength));

        sum.ShouldBe(number == 7 ? 15.0 / 16 : 1.0);
        _ = HuffmanTable.Get(number);
    }

    /// <summary>B.3's codes as the table prints them, decoded bit by bit.</summary>
    [Theory]
    [InlineData("0", 0)]
    [InlineData("10", 1)]
    [InlineData("110", 2)]
    [InlineData("1110101", 8)]                       // 1110 + 3 bits: 3 + 5
    [InlineData("11110000011", 14)]                  // 11110 + 6 bits: 11 + 3
    [InlineData("111110", HuffmanTable.OutOfBand)]
    [InlineData("1111110" + "00000000000000000000000000000101", 80)]   // upper: 75 + 5
    [InlineData("11111110" + "11111111", -1)]        // -256 + 255
    [InlineData("11111111" + "00000000000000000000000000000011", -260)]   // lower: -257 - 3
    public void TableB3_DecodesItsPrintedCodes(string bits, int value)
    {
        var data = Pack(bits);
        var reader = new Jbig2BitReader(data);
        HuffmanTable.Get(3).Decode(ref reader).ShouldBe(value);
    }

    /// <summary>
    /// A custom table segment (§7.4.13): HTPS = 2, HTRS = 3, values 1 to 16 in two
    /// lines, then the lower and upper range lines.
    /// </summary>
    [Fact]
    public void TableSegment_BuildsItsLines()
    {
        // flags: HTOOB = 0, HTPS - 1 = 1, HTRS - 1 = 2; HTLOW = 1; HTHIGH = 17;
        // lines (PREFLEN, RANGELEN) = (1, 3), (2, 3); lower PREFLEN 3; upper PREFLEN 3.
        byte[] segment = [0x22, 0, 0, 0, 1, 0, 0, 0, 17, .. Pack("01011" + "10011" + "11" + "11")];
        var table = HuffmanTable.ReadSegment(segment);

        Decode(table, "0" + "000").ShouldBe(1);
        Decode(table, "0" + "111").ShouldBe(8);
        Decode(table, "10" + "101").ShouldBe(14);
        Decode(table, "110" + new string('0', 31) + "1").ShouldBe(-1);   // lower: HTLOW - 1 - 1
        Decode(table, "111" + new string('0', 30) + "10").ShouldBe(19);  // upper: HTHIGH + 2
    }

    [Fact]
    public void TableSegment_WithLowNotBelowHigh_IsInvalidData()
    {
        byte[] segment = [0x22, 0, 0, 0, 9, 0, 0, 0, 9, 0xFF, 0xFF];
        Should.Throw<InvalidDataException>(() => HuffmanTable.ReadSegment(segment));
    }

    /// <summary>
    /// HTLOW to HTHIGH spanning the whole int range with RANGELEN 0 would be four
    /// billion lines; each one costs bits of the segment, so the reader runs out of
    /// data long before it runs out of memory.
    /// </summary>
    [Fact]
    public void TableSegment_DeclaringBillionsOfLines_FailsOnItsData()
    {
        byte[] segment = [0x00, 0x80, 0, 0, 0, 0x7F, 0xFF, 0xFF, 0xFF, .. new byte[64]];
        Should.Throw<InvalidDataException>(() => HuffmanTable.ReadSegment(segment));
    }

    [Fact]
    public void Lengths_OverSubscribingTheCodeSpace_AreInvalidData()
    {
        Should.Throw<InvalidDataException>(() => HuffmanTable.FromCodeLengths([1, 1, 1]));
    }

    [Fact]
    public void Reader_PastTheEnd_IsInvalidData()
    {
        Should.Throw<InvalidDataException>(() =>
        {
            var reader = new Jbig2BitReader([0xFF]);
            reader.ReadBits(9);
        });
    }

    private static int Decode(HuffmanTable table, string bits)
    {
        var reader = new Jbig2BitReader(Pack(bits));
        return table.Decode(ref reader);
    }

    private static byte[] Pack(string bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i] == '1') bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }
}
