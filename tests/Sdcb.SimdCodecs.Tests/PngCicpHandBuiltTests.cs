using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Cross-checks <see cref="PngReader"/>'s PNG-3 HDR signalling (cICP / mDCV / cLLI)
/// against PNGs assembled byte-by-byte in this file. Deliberately independent of
/// <see cref="PngWriter"/>: the chunks, CRCs and zlib stream are built here, so a
/// writer bug cannot hide a matching reader bug. Covers the code-point tuples of the
/// common SDR / PQ / HLG / BT.2020 signalling variants (full and narrow range), plus
/// both the final <c>mDCV</c> and the pre-final <c>mDCv</c> chunk spellings.
/// </summary>
public sealed class PngCicpHandBuiltTests
{
    public static TheoryData<string, byte, byte, byte, bool> Cases = new()
    {
        // label, primaries, transfer, matrix, fullRange
        { "SDR BT.709 FR", 1, 1, 0, true },
        { "PQ BT.2020 FR", 9, 16, 0, true },
        { "HLG BT.2020 FR", 9, 18, 0, true },
        { "SDR BT.709 NR", 1, 1, 0, false },
        { "HLG BT.2020 NR", 9, 18, 0, false },
        { "BT.2020 12-bit SDR FR", 9, 15, 0, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReadsCicp(string label, byte primaries, byte transfer, byte matrix, bool fullRange)
    {
        var png = BuildPng(16, Cicp(primaries, transfer, matrix, fullRange));

        var doc = PngReader.Decode(png);
        doc.Cicp.ShouldNotBeNull(label);
        ((byte)doc.Cicp.ColorPrimaries).ShouldBe(primaries);
        ((byte)doc.Cicp.TransferFunction).ShouldBe(transfer);
        ((byte)doc.Cicp.MatrixCoefficients).ShouldBe(matrix);
        doc.Cicp.VideoFullRangeFlag.ShouldBe(fullRange);
        doc.Mdcv.ShouldBeNull();
        doc.Clli.ShouldBeNull();
    }

    [Theory]
    [InlineData("mDCV", 1000u)]
    [InlineData("mDCv", 1000u)] // pre-final spelling used by early PNG-3 conformance files
    [InlineData("mDCV", 4000u)]
    public void ReadsMdcvAndClli(string mdcvName, uint maxCllNits)
    {
        // HDR10: BT.2020 primaries (x,y in 0.00002 units), D65 white, mastering
        // luminance 1000 nits / 0.0005 nits (0.0001 cd/m² units).
        var mdcv = new byte[24];
        ushort[] chroma = [35400, 14600, 8500, 39850, 6550, 2300, 15635, 16450];
        for (var i = 0; i < chroma.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.AsSpan(i * 2), chroma[i]);
        BinaryPrimitives.WriteUInt32BigEndian(mdcv.AsSpan(16), 1000u * 10000u);
        BinaryPrimitives.WriteUInt32BigEndian(mdcv.AsSpan(20), 5u);

        var clli = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(clli.AsSpan(0), maxCllNits * 10000u);
        BinaryPrimitives.WriteUInt32BigEndian(clli.AsSpan(4), 250u * 10000u);

        var png = BuildPng(16, Cicp(9, 16, 0, true), (mdcvName, mdcv), ("cLLI", clli));

        var doc = PngReader.Decode(png);
        doc.Cicp.ShouldNotBeNull();
        doc.Mdcv.ShouldNotBeNull("mDCV chunk should be present and parsed");
        doc.Clli.ShouldNotBeNull("cLLI chunk should be present and parsed");
        doc.Clli.MaxCllUnits.ShouldBe(maxCllNits * 10000u);
        doc.Clli.MaxFallUnits.ShouldBe(250u * 10000u);
    }

    private static (string, byte[]) Cicp(byte primaries, byte transfer, byte matrix, bool fullRange) =>
        ("cICP", [primaries, transfer, matrix, (byte)(fullRange ? 1 : 0)]);

    /// <summary>A 4×2 RGB PNG at <paramref name="bitDepth"/> with the given ancillary
    /// chunks placed between IHDR and IDAT.</summary>
    private static byte[] BuildPng(int bitDepth, params (string Type, byte[] Data)[] ancillary)
    {
        const int w = 4, h = 2;
        var bytesPerPixel = 3 * bitDepth / 8;
        var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0); // filter: None
            for (var x = 0; x < w * bytesPerPixel; x++)
                raw.WriteByte((byte)(x * 37 + y * 101));
        }

        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(zs);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = 2; // colour type: truecolour

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        foreach (var (type, data) in ancillary)
            WriteChunk(png, type, data);
        WriteChunk(png, "IDAT", z.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> be = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(be, (uint)data.Length);
        s.Write(be);
        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData, 4);
        s.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(be, Crc32(typeAndData));
        s.Write(be);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return ~crc;
    }
}
