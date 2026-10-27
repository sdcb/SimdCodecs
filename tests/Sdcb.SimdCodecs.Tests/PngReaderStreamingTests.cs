using System.Buffers.Binary;
using System.IO.Compression;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PngReader"/> inflates the IDAT stream a row at a time straight into the samples and
/// unfilters each row in place, reading the compressed bytes where they lie in the file. These pin what
/// that changes: every filter type undone in place, a zlib stream split across IDATs anywhere, and a
/// stream that is a row short or runs past the last row refused as before.
/// </summary>
public sealed class PngReaderStreamingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EachFilterType_IsUndoneInPlace(byte filterType)
    {
        foreach (var (colorType, bitDepth, channels) in new[] { (2, 8, 3), (6, 8, 4), (0, 16, 1), (4, 8, 2) })
        {
            const int w = 13, h = 9;
            var bpp = channels * bitDepth / 8;
            var samples = new byte[w * h * bpp];
            new Random(filterType * 100 + colorType).NextBytes(samples);

            var png = Png(w, h, colorType, bitDepth, Filtered(samples, w * bpp, h, bpp, filterType));
            var img = PngReader.Decode(png);

            img.Pixels.ShouldBe(samples, $"filter {filterType}, colour type {colorType}, {bitDepth}-bit");
        }
    }

    /// <summary>The spec lets a producer split the zlib stream across IDATs anywhere, even mid-token.</summary>
    [Fact]
    public void ZlibStreamSplitAcrossManyIdats_DecodesTheSame()
    {
        const int w = 31, h = 17;
        var samples = new byte[w * h * 4];
        new Random(7).NextBytes(samples);
        var raw = Filtered(samples, w * 4, h, 4, filterType: 4);

        foreach (var chunk in new[] { 1, 2, 7, 64, int.MaxValue })
            PngReader.Decode(Png(w, h, 6, 8, raw, chunk)).Pixels.ShouldBe(samples, $"IDATs of {chunk} bytes");
    }

    [Fact]
    public void StreamARowShort_Throws()
    {
        const int w = 5, h = 4;
        var raw = Filtered(new byte[w * h * 3], w * 3, h, 3, filterType: 0);
        var png = Png(w, h + 1, 2, 8, raw);

        Should.Throw<InvalidDataException>(() => PngReader.Decode(png));
        PngReader.TryDecode(png, out _).ShouldBeFalse();
    }

    [Fact]
    public void StreamRunningPastTheLastRow_Throws()
    {
        const int w = 5, h = 4;
        var raw = Filtered(new byte[w * h * 3], w * 3, h, 3, filterType: 0);
        var png = Png(w, h, 2, 8, [.. raw, 0]);

        Should.Throw<InvalidDataException>(() => PngReader.Decode(png));
        PngReader.TryDecode(png, out _).ShouldBeFalse();
    }

    /// <summary>Every row filtered with <paramref name="filterType"/>, each prefixed with it (PNG §9.2).</summary>
    private static byte[] Filtered(byte[] samples, int rowBytes, int h, int bpp, byte filterType)
    {
        var raw = new byte[(rowBytes + 1) * h];
        for (var y = 0; y < h; y++)
        {
            raw[y * (rowBytes + 1)] = filterType;
            for (var i = 0; i < rowBytes; i++)
            {
                int x = samples[y * rowBytes + i];
                var a = i >= bpp ? samples[y * rowBytes + i - bpp] : 0;
                var b = y > 0 ? samples[(y - 1) * rowBytes + i] : 0;
                var c = i >= bpp && y > 0 ? samples[(y - 1) * rowBytes + i - bpp] : 0;
                var predicted = filterType switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                raw[y * (rowBytes + 1) + 1 + i] = (byte)(x - predicted);
            }
        }
        return raw;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>A PNG of the filtered rows <paramref name="raw"/>, its zlib stream cut into IDATs of
    /// <paramref name="idatBytes"/> bytes.</summary>
    private static byte[] Png(int w, int h, int colorType, int bitDepth, byte[] raw, int idatBytes = int.MaxValue)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)h);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        WriteChunk(ms, "IHDR"u8, ihdr);

        byte[] compressed;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
                zs.Write(raw);
            compressed = z.ToArray();
        }
        for (var o = 0; o < compressed.Length; o += idatBytes)
            WriteChunk(ms, "IDAT"u8, compressed.AsSpan(o, Math.Min(idatBytes, compressed.Length - o)));
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        s.Write(word);
        s.Write(type);
        s.Write(data);
        var c = 0xFFFFFFFFu;
        foreach (var x in type) c = Crc[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in data) c = Crc[(c ^ x) & 0xFF] ^ (c >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(word, c ^ 0xFFFFFFFFu);
        s.Write(word);
    }

    private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();
}
