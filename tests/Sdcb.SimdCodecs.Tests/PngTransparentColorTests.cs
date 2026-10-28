using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// tRNS on greyscale and RGB images (PNG §11.3.1.1): one colour, compared at the file's full sample
/// precision before any rescaling, whose pixels are fully transparent. ImageMagick (libpng) reading
/// the same file is the oracle for every RGBA projection; the files are ImageMagick's own with a tRNS
/// chunk spliced in, keyed to a colour the image really has, so some pixels turn transparent.
/// </summary>
public sealed class PngTransparentColorTests
{
    private const int Height = 5;

    public static TheoryData<int, int, int, bool, bool> Cases()
    {
        var data = new TheoryData<int, int, int, bool, bool>();
        (int ColorType, int Depth)[] formats = [(0, 1), (0, 2), (0, 4), (0, 8), (0, 16), (2, 8), (2, 16)];
        foreach (var (colorType, depth) in formats)
        foreach (var width in new[] { 1, 3, 4, 7, 8, 9, 17, 33 })
        foreach (var interlace in new[] { false, true })
        {
            data.Add(colorType, depth, width, interlace, false);
            if (depth < 16)
                data.Add(colorType, depth, width, interlace, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryProjectionMatchesLibpng(int colorType, int depth, int width, bool interlace, bool dirtyHighBits)
    {
        var plain = PngRgba8ExpansionTests.Encode(colorType, depth, false, width, Height, interlace);
        var key = FirstPixel(plain, colorType, depth);
        var clean = WithChunk(plain, "tRNS"u8, Samples(key));
        // Bits above the depth are not part of the key: "decoders must mask the other bits to 0 before
        // the value is used" (PNG 3rd edition, §11.3.1.1). libpng 1.6 predates that sentence and drops
        // such a chunk, so its reading of the CLEAN file is the expectation for the dirty one.
        var png = dirtyHighBits
            ? WithChunk(plain, "tRNS"u8, Samples([.. key.Select(v => (ushort)(v | (0xFFFF << depth)))]))
            : clean;

        using var reference = new MagickImage(clean);
        using var referencePixels = reference.GetPixels();
        var reference16 = referencePixels.ToShortArray(PixelMapping.RGBA)!;
        var expected = reference16.Select(v => (byte)(v >> 8)).ToArray();
        // The splice did what it should: the oracle sees transparency.
        expected.Where((_, i) => i % 4 == 3).ShouldContain((byte)0);

        var decoded = PngReader.Decode(png);
        decoded.TransparentColor.ShouldBe(key);
        decoded.ToRgba8().ShouldBe(expected);

        var into = new byte[width * Height * 4];
        PngImageDecoder.TryDecodeIntoRgba8(png, into).ShouldBeTrue();
        into.ShouldBe(expected);
        PngImageDecoder.TryDecodeRgba8(png, out var rgba, out _, out _).ShouldBeTrue();
        rgba.ShouldBe(expected);

        // The fidelity tier gains an alpha channel, at the file's own depth.
        var channels = colorType == 0 ? 2 : 4;
        PngImageDecoder.TryReadInfo(png, out var info).ShouldBeTrue();
        info.Channels.ShouldBe(channels);
        PngImageDecoder.TryDecode(png, out var image).ShouldBeTrue();
        image.Channels.ShouldBe(channels);
        image.SampleFormat.ShouldBe(info.SampleFormat);
        var fidelity = depth == 16
            ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.Pixels).ToArray()
            : image.Pixels.ToArray().Select(v => (ushort)(v * 257)).ToArray();
        fidelity.ShouldBe(Enumerable.Range(0, width * Height)
            .SelectMany(p => channels == 2
                ? new[] { reference16[4 * p], reference16[4 * p + 3] }
                : new[] { reference16[4 * p], reference16[4 * p + 1], reference16[4 * p + 2], reference16[4 * p + 3] })
            .ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A16BitKeyMatchesAllSixteenBits(int colorType)
    {
        // Two pixels with the same high bytes and different low ones: only the exact one is transparent.
        var channels = colorType == 0 ? 1 : 3;
        ushort[] first = [.. Enumerable.Repeat((ushort)0x1234, channels)];
        ushort[] second = [.. Enumerable.Repeat((ushort)0x1299, channels)];
        var png = HandBuilt(colorType, 16, 2, Samples([.. first, .. second]), Samples(first));

        using var reference = new MagickImage(png);
        using var referencePixels = reference.GetPixels();
        var expected = referencePixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8)).ToArray();
        expected.ShouldBe(new byte[] { 0x12, 0x12, 0x12, 0, 0x12, 0x12, 0x12, 255 });

        PngReader.Decode(png).ToRgba8().ShouldBe(expected);
        var into = new byte[8];
        PngImageDecoder.TryDecodeIntoRgba8(png, into).ShouldBeTrue();
        into.ShouldBe(expected);
    }

    [Fact]
    public void AKeyOfAColourNotInTheImageLeavesItOpaque()
    {
        var png = HandBuilt(2, 8, 3, [1, 2, 3, 4, 5, 6, 7, 8, 9], [0, 9, 0, 9, 0, 9]);

        var into = new byte[12];
        PngImageDecoder.TryDecodeIntoRgba8(png, into).ShouldBeTrue();
        into.ShouldBe(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255 });
    }

    [Theory]
    [InlineData(6, 8, 6)]   // tRNS on RGBA: forbidden, so ignored
    [InlineData(4, 8, 2)]   // ... and on grey + alpha
    [InlineData(2, 8, 2)]   // RGB with a greyscale-sized tRNS: malformed, ignored
    [InlineData(0, 8, 6)]   // greyscale with an RGB-sized one
    public void AMisplacedOrMalformedTrnsIsIgnoredAsLibpngIgnoresIt(int colorType, int depth, int trnsLength)
    {
        var plain = PngRgba8ExpansionTests.Encode(colorType, depth, false, 9);
        var png = WithChunk(plain, "tRNS"u8, new byte[trnsLength]);

        using var reference = new MagickImage(png);
        using var referencePixels = reference.GetPixels();
        var expected = referencePixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8)).ToArray();

        var decoded = PngReader.Decode(png);
        decoded.TransparentColor.ShouldBeNull();
        decoded.PaletteAlpha.ShouldBeNull();
        decoded.ToRgba8().ShouldBe(expected);
        PngImageDecoder.TryDecodeRgba8(png, out var rgba, out _, out _).ShouldBeTrue();
        rgba.ShouldBe(expected);
        PngImageDecoder.TryReadInfo(png, out var info).ShouldBeTrue();
        info.Channels.ShouldBe(PngReader.SamplesPerPixel(colorType));
    }

    [Fact]
    public void PhysicalDimensionsAreRead()
    {
        var plain = PngRgba8ExpansionTests.Encode(2, 8, false, 3);
        var phys = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(phys, 11811);           // 300 dpi
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), 5906);  // 150 dpi
        phys[8] = 1;

        var decoded = PngReader.Decode(WithChunk(StripChunk(plain, "pHYs"u8), "pHYs"u8, phys));

        decoded.PhysicalDimensions.ShouldBe(new PngPhysicalDimensions(11811, 5906, true));
        decoded.PhysicalDimensions!.DpiX!.Value.ShouldBe(300, 0.01);
        decoded.PhysicalDimensions.DpiY!.Value.ShouldBe(150, 0.02);
    }

    // The first pixel's samples at the file's depth (a sub-byte grey level unscaled).
    private static ushort[] FirstPixel(byte[] png, int colorType, int depth)
    {
        var image = PngReader.Decode(png);
        var channels = colorType == 0 ? 1 : 3;
        return Enumerable.Range(0, channels).Select(c => depth switch
        {
            16 => BinaryPrimitives.ReadUInt16BigEndian(image.Pixels.AsSpan(2 * c)),
            8 => image.Pixels[c],
            _ => (ushort)(image.Pixels[c] * ((1 << depth) - 1) / 255),
        }).ToArray();
    }

    private static byte[] Samples(ushort[] values)
    {
        var bytes = new byte[2 * values.Length];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 * i), values[i]);
        return bytes;
    }

    // The file with one more chunk right after IHDR.
    internal static byte[] WithChunk(byte[] png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        ms.Write(png.AsSpan(0, 33));
        PngRgba8ExpansionTests.Chunk(ms, type, data);
        ms.Write(png.AsSpan(33));
        return ms.ToArray();
    }

    private static byte[] StripChunk(byte[] png, ReadOnlySpan<byte> type)
    {
        using var ms = new MemoryStream();
        ms.Write(png.AsSpan(0, 8));
        for (var pos = 8; pos < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            if (!png.AsSpan(pos + 4, 4).SequenceEqual(type))
                ms.Write(png.AsSpan(pos, length + 12));
            pos += length + 12;
        }
        return ms.ToArray();
    }

    // One row of `width` pixels, filter None, with a tRNS chunk.
    private static byte[] HandBuilt(int colorType, int depth, int width, byte[] row, byte[] trns)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        PngRgba8ExpansionTests.Chunk(ms, "IHDR"u8, ihdr);
        PngRgba8ExpansionTests.Chunk(ms, "tRNS"u8, trns);
        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.WriteByte(0);
            z.Write(row);
        }
        PngRgba8ExpansionTests.Chunk(ms, "IDAT"u8, deflated.ToArray());
        PngRgba8ExpansionTests.Chunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }
}
