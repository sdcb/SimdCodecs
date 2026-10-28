using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using ImageMagick;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The RGBA8 projections — <see cref="PngImageDecoder.TryDecodeIntoRgba8"/> (rows expanded as they are
/// inflated), <see cref="PngImageDecoder.TryDecodeRgba8"/> and <see cref="PngImage.ToRgba8"/> — against
/// ImageMagick (libpng) reading the same file, for every colour type and bit depth at widths that end
/// each vector loop on every possible tail. The expected bytes are libpng's 16-bit samples' high bytes,
/// the mapping the reader documents (an 8-bit sample v reads as v·257, so its high byte is v).
/// </summary>
public sealed class PngRgba8ExpansionTests
{
    private const int Height = 5;

    private static readonly int[] Widths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 67];

    public static TheoryData<int, int, bool, int> Cases()
    {
        var data = new TheoryData<int, int, bool, int>();
        (int ColorType, int Depth, bool PaletteAlpha)[] formats =
        [
            (0, 1, false), (0, 2, false), (0, 4, false), (0, 8, false), (0, 16, false),
            (2, 8, false), (2, 16, false),
            (3, 1, false), (3, 2, false), (3, 4, false), (3, 8, false), (3, 8, true),
            (4, 8, false), (4, 16, false),
            (6, 8, false), (6, 16, false),
        ];
        foreach (var (colorType, depth, paletteAlpha) in formats)
        {
            foreach (var width in Widths)
                data.Add(colorType, depth, paletteAlpha, width);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryProjectionMatchesLibpng(int colorType, int depth, bool paletteAlpha, int width)
    {
        var png = Encode(colorType, depth, paletteAlpha, width);

        using var reference = new MagickImage(png);
        using var referencePixels = reference.GetPixels();
        var expected = referencePixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8)).ToArray();

        var into = new byte[width * Height * 4];
        PngImageDecoder.TryDecodeIntoRgba8(png, into).ShouldBeTrue();
        into.ShouldBe(expected);

        PngImageDecoder.TryDecodeRgba8(png, out var rgba, out var w, out var h).ShouldBeTrue();
        (w, h).ShouldBe((width, Height));
        rgba.ShouldBe(expected);

        PngReader.Decode(png).ToRgba8().ShouldBe(expected);
    }

    [Fact]
    public void TryDecodeIntoRgba8_LeavesBytesPastTheImageAlone()
    {
        var png = Encode(2, 8, false, 9);
        var destination = Enumerable.Repeat((byte)0xA5, 9 * Height * 4 + 7).ToArray();

        PngImageDecoder.TryDecodeIntoRgba8(png, destination).ShouldBeTrue();

        destination.AsSpan(9 * Height * 4).ToArray().ShouldAllBe(b => b == 0xA5);
    }

    [Theory]
    [InlineData(8, 2, new byte[] { 0, 1, 5 })]     // 8-bit: index 5 of a 2-entry palette
    [InlineData(8, 200, new byte[] { 0, 255, 1 })] // 8-bit: 255 of 200 entries
    [InlineData(2, 2, new byte[] { 0b0001_1000 })] // 2-bit, width 3: samples 0, 1, 2 — the 2 is out of range
    [InlineData(4, 3, new byte[] { 0x0F })]        // 4-bit, width 2: sample 15 of 3 entries
    [InlineData(1, 1, new byte[] { 0b0100_0000 })] // 1-bit, width 3: the second sample is index 1 of 1 entry
    public void AnIndexPastThePaletteIsRefused(int depth, int entries, byte[] row)
    {
        var width = depth == 8 ? row.Length : Math.Min(3, 8 / depth);
        var png = HandBuiltIndexed(depth, width, entries, row);

        PngImageDecoder.TryDecodeIntoRgba8(png, new byte[width * 4]).ShouldBeFalse();
        PngImageDecoder.TryDecodeRgba8(png, out _, out _, out _).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => PngReader.Decode(png).ToRgba8());
    }

    [Fact]
    public void PaddingBitsAreNotSamples()
    {
        // 2-bit, width 1, one palette entry: the row byte's last six bits are padding and would be
        // index 3 if they were read.
        var png = HandBuiltIndexed(2, 1, 1, [0b0011_1111]);
        var rgba = new byte[4];

        PngImageDecoder.TryDecodeIntoRgba8(png, rgba).ShouldBeTrue();

        rgba.ShouldBe(new byte[] { 10, 20, 30, 255 });
        PngReader.Decode(png).ToRgba8().ShouldBe(rgba);
    }

    /// <summary>
    /// A PNG of the given colour type and depth, written by ImageMagick (libpng) from deterministic
    /// content: grey levels the depth can hold, at most as many colours as an indexed depth holds,
    /// alpha that varies, and for 16 bits samples whose low bytes differ from their high bytes.
    /// </summary>
    private static byte[] Encode(int colorType, int depth, bool paletteAlpha, int width)
    {
        var random = new Random(colorType * 1000 + depth * 100 + width);
        var hasAlpha = colorType is 4 or 6 || paletteAlpha;
        // An indexed image keeps a palette entry spare, as ImageMagick writes one more than it is given.
        var colours = colorType == 3 ? Math.Min(200, (1 << depth) - 1) : 0;
        var palette = Enumerable.Range(0, Math.Max(colours, 1))
            .Select(_ => (R: (ushort)(random.Next(256) * 257), G: (ushort)(random.Next(256) * 257), B: (ushort)(random.Next(256) * 257),
                A: paletteAlpha ? (ushort)(random.Next(256) * 257) : ushort.MaxValue))
            .ToArray();

        var samples = new ushort[width * Height * 4];
        for (var i = 0; i < width * Height; i++)
        {
            ushort r, g, b, a = ushort.MaxValue;
            if (colorType == 3)
            {
                (r, g, b, a) = palette[random.Next(palette.Length)];
            }
            else
            {
                var levels = depth < 8 ? 1 << depth : 0;
                ushort Sample() => depth switch
                {
                    16 => (ushort)random.Next(65536),
                    8 => (ushort)(random.Next(256) * 257),
                    _ => (ushort)(random.Next(levels) * 65535 / (levels - 1)),
                };
                r = Sample();
                if (colorType is 0 or 4)
                {
                    g = b = r;
                }
                else
                {
                    g = Sample();
                    b = Sample();
                }
                if (hasAlpha)
                    a = depth == 16 ? (ushort)random.Next(65536) : (ushort)(random.Next(256) * 257);
            }
            samples[4 * i] = r;
            samples[4 * i + 1] = g;
            samples[4 * i + 2] = b;
            samples[4 * i + 3] = a;
        }

        using var image = new MagickImage();
        image.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(),
            new PixelReadSettings((uint)width, Height, StorageType.Short, "RGBA"));
        if (colorType == 3)
            image.ColorType = hasAlpha ? ColorType.PaletteAlpha : ColorType.Palette;
        image.Settings.SetDefine(MagickFormat.Png, "bit-depth", depth.ToString(CultureInfo.InvariantCulture));
        // With alpha, forcing colour type 3 makes ImageMagick drop the alpha; left to itself it writes
        // PLTE + tRNS (asserted below).
        if (!paletteAlpha)
            image.Settings.SetDefine(MagickFormat.Png, "color-type", colorType.ToString(CultureInfo.InvariantCulture));
        var png = image.ToByteArray(MagickFormat.Png);

        // IHDR's depth, colour type and interlace, so each case is what it says it is.
        (png[24], png[25], png[28]).ShouldBe(((byte)depth, (byte)colorType, (byte)0));
        if (paletteAlpha)
            png.AsSpan().IndexOf("tRNS"u8).ShouldBeGreaterThan(0);
        return png;
    }

    /// <summary>
    /// An indexed PNG with one row: palette entry k is (10k + 10, 10k + 20, 10k + 30), filter None.
    /// </summary>
    private static byte[] HandBuiltIndexed(int depth, int width, int entries, byte[] row)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = (byte)depth;
        ihdr[9] = 3;
        Chunk(ms, "IHDR"u8, ihdr);

        var plte = new byte[entries * 3];
        for (var k = 0; k < entries; k++)
        {
            plte[3 * k] = (byte)(10 * k + 10);
            plte[3 * k + 1] = (byte)(10 * k + 20);
            plte[3 * k + 2] = (byte)(10 * k + 30);
        }
        Chunk(ms, "PLTE"u8, plte);

        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.WriteByte(0);
            z.Write(row);
        }
        Chunk(ms, "IDAT"u8, deflated.ToArray());
        Chunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> be = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(be, data.Length);
        s.Write(be);
        s.Write(type);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(be, Crc32([.. type, .. data]));
        s.Write(be);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFFu;
    }
}
