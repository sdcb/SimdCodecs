using System.Buffers.Binary;
using System.IO.Compression;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Round-trip tests for the new <see cref="PngWriter.EncodeGray8"/>,
/// <see cref="PngWriter.EncodeGray16"/>, and <see cref="PngWriter.EncodeRgba16"/>
/// paths. A hand-rolled in-test PNG reader handles every encoder branch
/// (No / Sub / Up / Avg / Paeth filters, all four pixel formats) so the
/// tests don't pull in a third-party decoder dependency. The reader is
/// intentionally minimal — single IDAT, no interlacing — which matches
/// exactly what <see cref="PngWriter"/> emits.
/// </summary>
public sealed class PngWriterBitDepthTests
{
    [Fact]
    public void Gray8_RoundTrips()
    {
        const int width = 8;
        const int height = 6;
        var pixels = new byte[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)((i * 17 + 3) & 0xFF);

        var png = PngWriter.EncodeGray8(pixels, width, height);
        var decoded = DecodePng(png);

        decoded.Width.ShouldBe(width);
        decoded.Height.ShouldBe(height);
        decoded.BitDepth.ShouldBe((byte)8);
        decoded.ColorType.ShouldBe((byte)0);
        decoded.Bytes.ShouldBe(pixels);
    }

    [Fact]
    public void Gray16_RoundTrips_WithBigEndianOnDisk()
    {
        const int width = 4;
        const int height = 4;
        var pixels = new ushort[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)(i * 4097 + 13); // spread across the 16-bit range

        var png = PngWriter.EncodeGray16(pixels, width, height);
        var decoded = DecodePng(png);

        decoded.BitDepth.ShouldBe((byte)16);
        decoded.ColorType.ShouldBe((byte)0);
        decoded.Bytes.Length.ShouldBe(pixels.Length * 2);

        // Pull samples back as big-endian ushorts and compare.
        for (var i = 0; i < pixels.Length; i++)
        {
            var got = BinaryPrimitives.ReadUInt16BigEndian(decoded.Bytes.AsSpan(i * 2, 2));
            got.ShouldBe(pixels[i]);
        }
    }

    [Fact]
    public void Rgba16_RoundTrips_WithBigEndianOnDisk()
    {
        const int width = 3;
        const int height = 3;
        var pixels = new ushort[width * height * 4];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)((i * 257 + 11) & 0xFFFF);

        var png = PngWriter.EncodeRgba16(pixels, width, height);
        var decoded = DecodePng(png);

        decoded.BitDepth.ShouldBe((byte)16);
        decoded.ColorType.ShouldBe((byte)6);
        for (var i = 0; i < pixels.Length; i++)
        {
            var got = BinaryPrimitives.ReadUInt16BigEndian(decoded.Bytes.AsSpan(i * 2, 2));
            got.ShouldBe(pixels[i]);
        }
    }

    [Fact]
    public void Gray16_WithIccProfile_EmbedsBothChunks()
    {
        var pixels = new ushort[16];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(i * 1000);

        var profile = new byte[100];
        for (var i = 0; i < profile.Length; i++) profile[i] = (byte)(i * 3 + 7);

        var png = PngWriter.EncodeGray16(pixels, 4, 4, profile);
        var decoded = DecodePng(png);

        decoded.BitDepth.ShouldBe((byte)16);
        decoded.HasIccp.ShouldBeTrue();
    }

    private record DecodedPng(int Width, int Height, byte BitDepth, byte ColorType, byte[] Bytes, bool HasIccp);

    private static DecodedPng DecodePng(byte[] png)
    {
        // Verify signature.
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        png.AsSpan(0, 8).SequenceEqual(signature).ShouldBeTrue("PNG signature mismatch");

        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0;
        var hasIccp = false;
        using var idatBytes = new MemoryStream();

        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            var typeSpan = png.AsSpan(pos + 4, 4);
            var dataStart = pos + 8;

            if (typeSpan.SequenceEqual("IHDR"u8))
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(dataStart, 4));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(dataStart + 4, 4));
                bitDepth = png[dataStart + 8];
                colorType = png[dataStart + 9];
            }
            else if (typeSpan.SequenceEqual("IDAT"u8))
            {
                idatBytes.Write(png, dataStart, length);
            }
            else if (typeSpan.SequenceEqual("iCCP"u8))
            {
                hasIccp = true;
            }
            else if (typeSpan.SequenceEqual("IEND"u8))
            {
                break;
            }

            pos += 12 + length; // length(4) + type(4) + data + crc(4)
        }

        // Decompress IDAT (zlib).
        idatBytes.Position = 0;
        using var z = new ZLibStream(idatBytes, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var filtered = raw.ToArray();

        // Compute bytes-per-pixel for filter bpp lookup.
        int samplesPerPixel = colorType switch
        {
            0 => 1, // gray
            2 => 3, // rgb
            3 => 1, // indexed
            4 => 2, // gray+alpha
            6 => 4, // rgba
            _ => throw new NotSupportedException($"colorType {colorType}"),
        };
        int bytesPerPixel = Math.Max(1, samplesPerPixel * bitDepth / 8);
        int stride = width * samplesPerPixel * bitDepth / 8;
        var unfiltered = new byte[stride * height];
        var prevRow = new byte[stride];
        int rowSrc = 0;
        for (int y = 0; y < height; y++)
        {
            byte filterType = filtered[rowSrc++];
            var rowDst = unfiltered.AsSpan(y * stride, stride);
            UnfilterRow(filtered.AsSpan(rowSrc, stride), prevRow, rowDst, filterType, bytesPerPixel);
            rowSrc += stride;
            rowDst.CopyTo(prevRow);
        }

        return new DecodedPng(width, height, bitDepth, colorType, unfiltered, hasIccp);
    }

    private static void UnfilterRow(ReadOnlySpan<byte> filtered, ReadOnlySpan<byte> prev,
        Span<byte> dst, byte filterType, int bpp)
    {
        switch (filterType)
        {
            case 0:
                filtered.CopyTo(dst);
                break;
            case 1: // Sub
                for (int i = 0; i < bpp; i++) dst[i] = filtered[i];
                for (int i = bpp; i < filtered.Length; i++)
                    dst[i] = (byte)(filtered[i] + dst[i - bpp]);
                break;
            case 2: // Up
                for (int i = 0; i < filtered.Length; i++)
                    dst[i] = (byte)(filtered[i] + prev[i]);
                break;
            case 3: // Average
                for (int i = 0; i < filtered.Length; i++)
                {
                    int left = i >= bpp ? dst[i - bpp] : 0;
                    int above = prev[i];
                    dst[i] = (byte)(filtered[i] + (left + above) / 2);
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < filtered.Length; i++)
                {
                    int left = i >= bpp ? dst[i - bpp] : 0;
                    int above = prev[i];
                    int upperLeft = i >= bpp ? prev[i - bpp] : 0;
                    dst[i] = (byte)(filtered[i] + PaethPredictor(left, above, upperLeft));
                }
                break;
            default:
                throw new NotSupportedException($"filter {filterType}");
        }
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }
}
