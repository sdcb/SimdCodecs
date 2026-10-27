using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// 1-bit (and 2/4-bit) TIFF pages: the CCITT fax compressions, packed rows under every other
/// compression, FillOrder 2, and the facade's handling of MinIsWhite and of multi-page files.
/// </summary>
/// <remarks>
/// <para>The writer is libtiff through Magick.NET, as in <c>Group4Tiff</c> -- it shares no code with
/// <see cref="TiffFax"/>. The expected raster is the pattern the test drew, never a decode of the file
/// by anything in this repository. T.4 two-dimensional coding (T4Options bit 0) is not something
/// ImageMagick writes, so that case is a hand-assembled stream of codes copied from T.4's tables.</para>
/// </remarks>
public sealed class TiffBilevelReadTests
{
    /// <summary>
    /// A bilevel pattern with something for every code path: noise (short runs of both colours, so
    /// vertical modes dominate in 2D), a full white row and a full black row, runs past 64 (makeup
    /// codes), and -- because the image is wider than 2560 -- runs that need the extended makeup codes
    /// and a chain of them. Rows start black as well as white, so the zero-length first run is covered.
    /// </summary>
    private static bool[] Pattern(int width, int height)
    {
        var black = new bool[width * height];
        var random = new Random(1234);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                black[(y * width) + x] = (y % 7) switch
                {
                    0 => false,                                  // all white
                    1 => true,                                   // all black
                    2 => x < 2600 || x % 97 == 0,                // a run past 2560, then sparse dots
                    3 => (x / 70) % 2 == 0,                      // makeup-sized runs, starting black
                    _ => random.Next(5) == 0 || (x / 13 + y) % 9 == 0,
                };
            }
        }
        return black;
    }

    public static TheoryData<CompressionMethod, string?, TiffCompression> MagickCases => new()
    {
        { CompressionMethod.Group4, null, TiffCompression.CcittT6 },
        { CompressionMethod.Group4, "lsb", TiffCompression.CcittT6 },
        { CompressionMethod.Fax, null, TiffCompression.CcittT4 },
        { CompressionMethod.Fax, "lsb", TiffCompression.CcittT4 },
        { CompressionMethod.NoCompression, null, TiffCompression.Uncompressed },
        { CompressionMethod.LZW, null, TiffCompression.Lzw },
        { CompressionMethod.Zip, null, TiffCompression.Deflate },
        { CompressionMethod.RLE, null, TiffCompression.PackBits },
    };

    [Theory]
    [MemberData(nameof(MagickCases))]
    public void Bilevel_WrittenByLibTiff_DecodesToThePattern(CompressionMethod method, string? fillOrder,
        TiffCompression expectedCompression)
    {
        const int width = 2803;   // past 2560, and not a multiple of 8
        const int height = 30;
        var black = Pattern(width, height);
        var tiff = WriteBilevel(black, width, height, method, fillOrder);

        var page = TiffReader.ReadPage(tiff);
        page.Compression.ShouldBe(expectedCompression);
        page.BitsPerSample.ShouldBe(8);
        page.Width.ShouldBe(width);
        page.Height.ShouldBe(height);
        TiffReader.ReadLayout(tiff).StoredBitsPerSample.ShouldBe(1);

        // The reader keeps the file's photometric; the 1 bit is expanded to 255.
        var blackSample = page.Photometric == TiffPhotometric.MinIsWhite ? (byte)255 : (byte)0;
        AssertRaster(page.Pixels, black, width, blackSample);

        // The facade normalises to MinIsBlack grey whichever photometric was written.
        TiffImageDecoder.TryDecode(tiff, out var image).ShouldBeTrue();
        image!.Channels.ShouldBe(1);
        AssertRaster(image.Pixels.ToArray(), black, width, blackSample: 0);
    }

    [Fact]
    public void Group3_MultiStrip_EachStripStartsFromAWhiteReferenceLine()
    {
        const int width = 333;
        const int height = 50;
        var black = Pattern(width, height);
        var tiff = WriteBilevel(black, width, height, CompressionMethod.Fax, fillOrder: null, rowsPerStrip: 8);

        var layout = TiffReader.ReadLayout(tiff);
        layout.SegmentCount.ShouldBeGreaterThan(1, "fixture: ImageMagick was asked for 8-row strips");
        AssertStripsMatchPage(tiff, layout, black, width);
    }

    [Fact]
    public void Group4_MultiStrip_EachStripStartsFromAWhiteReferenceLine()
    {
        // ImageMagick writes Group 4 as a single strip, so the strips here are coded by libtiff one
        // band at a time (Group4Tiff) and assembled into a two-strip page by hand.
        const int width = 333;
        const int rowsPerStrip = 32;
        const int height = 50;
        var black = Pattern(width, height);
        var strips = new List<byte[]>();
        for (var y = 0; y < height; y += rowsPerStrip)
        {
            var rows = Math.Min(rowsPerStrip, height - y);
            var band = new byte[width * rows];
            for (var i = 0; i < band.Length; i++) band[i] = black[(y * width) + i] ? (byte)1 : (byte)0;
            strips.Add(Group4Tiff.Encode(band, width, rows).Coded);
        }

        var tiff = BuildFaxTiff(width, height, TiffCompression.CcittT6, t4Options: 0, rowsPerStrip, [.. strips]);
        var layout = TiffReader.ReadLayout(tiff);
        layout.SegmentCount.ShouldBe(2);
        AssertStripsMatchPage(tiff, layout, black, width);
    }

    private static void AssertStripsMatchPage(byte[] tiff, TiffPageLayout layout, bool[] black, int width)
    {
        var page = TiffReader.ReadPage(tiff);
        var blackSample = page.Photometric == TiffPhotometric.MinIsWhite ? (byte)255 : (byte)0;
        AssertRaster(page.Pixels, black, width, blackSample);

        // On-demand strip reads agree with the whole page.
        for (var s = 0; s < layout.SegmentCount; s++)
        {
            var buffer = new byte[layout.SegmentBytes(s)];
            var rect = TiffReader.ReadSegment(tiff, layout, s, buffer);
            buffer.ShouldBe(page.Pixels.AsSpan(rect.Y * width, rect.Height * width).ToArray());
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void LowDepthGrey_IsScaledToTheFullByteRange(int bits)
    {
        const int width = 29;
        const int height = 7;
        var levels = (1 << bits) - 1;
        var grey = new byte[width * height];
        for (var i = 0; i < grey.Length; i++) grey[i] = (byte)(i * 7 % (levels + 1) * (255 / levels));

        using var image = new MagickImage(grey, new MagickReadSettings
        {
            Format = MagickFormat.Gray, Width = (uint)width, Height = (uint)height, Depth = 8,
        });
        image.Format = MagickFormat.Tiff;
        image.ColorType = ColorType.Grayscale;
        image.Depth = (uint)bits;
        image.Settings.Compression = CompressionMethod.NoCompression;
        var tiff = image.ToByteArray();

        TiffReader.ReadLayout(tiff).StoredBitsPerSample.ShouldBe(bits);
        var page = TiffReader.ReadPage(tiff);
        page.Photometric.ShouldBe(TiffPhotometric.MinIsBlack);
        page.Pixels.ShouldBe(grey);
    }

    [Fact]
    public void Facade_DecodesPageZero_WhenALaterPageIsUnsupported()
    {
        const int width = 64;
        const int height = 16;
        var black = Pattern(width, height);
        var tiff = WriteBilevel(black, width, height, CompressionMethod.Group4, fillOrder: null, pages: 2);

        // Page 1 claims a compression this reader does not implement (ThunderScan, 32809).
        PatchCompression(tiff, pageIndex: 1, 32809);
        Should.Throw<NotSupportedException>(() => TiffReader.Read(tiff));
        Should.Throw<NotSupportedException>(() => TiffReader.ReadPage(tiff, 1));

        TiffImageDecoder.TryDecode(tiff, out var image).ShouldBeTrue();
        AssertRaster(image!.Pixels.ToArray(), black, width, blackSample: 0);
    }

    [Fact]
    public void T4TwoDimensional_HandAssembled_Decodes()
    {
        // Width 8, three rows, T4Options = 1 (2D), EOL before every row and a tag bit after it.
        //   row 0 (1D): white 2, black 3, white 3   -> ..###...
        //   row 1 (2D): V0, V0, V0                   -> ..###...   (the changes of the row above: 2, 5, 8)
        //   row 2 (2D): VR1, VL1, V0                 -> ...#....   (b1 = 2 -> 3; b1 = 5 -> 4; b1 = 8 -> 8)
        const string eol = "000000000001";
        var bits = string.Concat(
            eol, "1", "0111", "10", "1000",            // 1D: white 2 ("0111"), black 3 ("10"), white 3 ("1000")
            eol, "0", "1", "1", "1",                   // 2D: V0, V0, V0
            eol, "0", "011", "010", "1");              // 2D: VR1, VL1, V0
        var coded = Pack(bits);

        var tiff = BuildFaxTiff(8, 3, TiffCompression.CcittT4, t4Options: 1, rowsPerStrip: 3, coded);
        var page = TiffReader.ReadPage(tiff);
        page.Photometric.ShouldBe(TiffPhotometric.MinIsWhite);

        byte[] expected =
        [
            0, 0, 255, 255, 255, 0, 0, 0,
            0, 0, 255, 255, 255, 0, 0, 0,
            0, 0, 0, 255, 0, 0, 0, 0,
        ];
        page.Pixels.ShouldBe(expected);
    }

    [Fact]
    public void CcittRle_HandAssembled_RowsAreByteAligned()
    {
        // Compression 2: no EOLs, every row starts on a byte boundary.
        //   row 0: white 2, black 3, white 3 -> 4+2+4 = 10 bits, padded to 16
        //   row 1: white 0, black 8          -> 8+6   = 14 bits, padded to 16
        var coded = Pack(string.Concat("0111", "10", "1000", "000000", "00110101", "000101", "00"));
        var tiff = BuildFaxTiff(8, 2, TiffCompression.CcittRle, t4Options: 0, rowsPerStrip: 2, coded);

        TiffReader.ReadPage(tiff).Pixels.ShouldBe(
            [0, 0, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255]);
    }

    [Fact]
    public void CorruptFaxData_ThrowsInvalidData()
    {
        // A white run code that is not in T.4's tables ("00000000" + more zeros) mid-stream, with data
        // after it -- so this is corruption, not truncation.
        var coded = Pack(string.Concat("0111", "0000000000000001", "11111111"));
        var tiff = BuildFaxTiff(8, 1, TiffCompression.CcittT6, t4Options: 0, rowsPerStrip: 1, coded);
        Should.Throw<InvalidDataException>(() => TiffReader.ReadPage(tiff));
    }

    [Fact]
    public void TruncatedFaxStrip_KeepsTheRowsItCompleted()
    {
        const int width = 300;
        const int height = 40;
        var black = Pattern(width, height);
        var tiff = WriteBilevel(black, width, height, CompressionMethod.Group4, fillOrder: null);
        var layout = TiffReader.ReadLayout(tiff);

        // Cut the strip's byte count in half; the page still has its declared size.
        PatchFirstStripByteCount(tiff, layout);
        var page = TiffReader.ReadPage(tiff);
        page.Pixels.Length.ShouldBe(width * height);
    }

    private static void AssertRaster(byte[] pixels, bool[] black, int width, byte blackSample)
    {
        pixels.Length.ShouldBe(black.Length);
        for (var i = 0; i < black.Length; i++)
        {
            var expected = black[i] ? blackSample : (byte)(255 - blackSample);
            if (pixels[i] != expected)
                throw new ShouldAssertException($"pixel ({i % width}, {i / width}) is {pixels[i]}, expected {expected}");
        }
    }

    private static byte[] WriteBilevel(bool[] black, int width, int height, CompressionMethod method,
        string? fillOrder, int rowsPerStrip = 0, int pages = 1)
    {
        var grey = new byte[black.Length];
        for (var i = 0; i < grey.Length; i++) grey[i] = black[i] ? (byte)0 : (byte)255;

        using var collection = new MagickImageCollection();
        for (var p = 0; p < pages; p++)
        {
            var image = new MagickImage(grey, new MagickReadSettings
            {
                Format = MagickFormat.Gray, Width = (uint)width, Height = (uint)height, Depth = 8,
            });
            image.ColorType = ColorType.Bilevel;
            image.Depth = 1;
            image.Format = MagickFormat.Tiff;
            image.Settings.Compression = method;
            if (fillOrder is not null) image.Settings.SetDefine(MagickFormat.Tiff, "fill-order", fillOrder);
            if (rowsPerStrip > 0) image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", rowsPerStrip.ToString());
            collection.Add(image);
        }

        using var ms = new MemoryStream();
        collection.Write(ms, MagickFormat.Tiff);
        return ms.ToArray();
    }

    private static byte[] Pack(string bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i] == '1') bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    /// <summary>
    /// A little-endian, MinIsWhite bilevel TIFF around already-coded fax strips. MinIsWhite makes a
    /// decoded 1 bit black, which is the polarity both T.4's codes and <see cref="Group4Tiff"/> use.
    /// </summary>
    private static byte[] BuildFaxTiff(int width, int height, TiffCompression compression, uint t4Options,
        int rowsPerStrip, params byte[][] strips)
    {
        const int entries = 10;
        const int ifd = 8;
        var arrays = ifd + 2 + (entries * 12) + 4;
        var single = strips.Length == 1;
        var data = arrays + (single ? 0 : strips.Length * 8);
        var tiff = new byte[data + strips.Sum(s => s.Length)];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), ifd);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(ifd), entries);
        var at = ifd + 2;
        Entry(TiffTag.ImageWidth, 4, (uint)width);
        Entry(TiffTag.ImageLength, 4, (uint)height);
        Entry(TiffTag.BitsPerSample, 3, 1);
        Entry(TiffTag.Compression, 3, (uint)compression);
        Entry(TiffTag.PhotometricInterp, 3, (uint)TiffPhotometric.MinIsWhite);
        Entry(TiffTag.StripOffsets, 4, single ? (uint)data : (uint)arrays, (uint)strips.Length);
        Entry(TiffTag.SamplesPerPixel, 3, 1);
        Entry(TiffTag.RowsPerStrip, 4, (uint)rowsPerStrip);
        Entry(TiffTag.StripByteCounts, 4, single ? (uint)strips[0].Length : (uint)(arrays + (strips.Length * 4)),
            (uint)strips.Length);
        Entry(compression == TiffCompression.CcittT6 ? TiffTag.T6Options : TiffTag.T4Options, 4, t4Options);

        var cursor = data;
        for (var i = 0; i < strips.Length; i++)
        {
            if (!single)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(arrays + (i * 4)), (uint)cursor);
                BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(arrays + (strips.Length * 4) + (i * 4)),
                    (uint)strips[i].Length);
            }
            strips[i].CopyTo(tiff, cursor);
            cursor += strips[i].Length;
        }
        return tiff;

        void Entry(ushort tag, ushort type, uint value, uint count = 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), count);
            if (type == 3) BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 8), (ushort)value);
            else BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), value);
            at += 12;
        }
    }

    /// <summary>Rewrites the Compression tag of page <paramref name="pageIndex"/> of a little-endian file in place.</summary>
    private static void PatchCompression(byte[] tiff, int pageIndex, ushort compression)
    {
        tiff[0].ShouldBe((byte)'I');
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        for (var p = 0; p < pageIndex; p++)
        {
            var n = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
            ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(ifd + 2 + (n * 12)));
            ifd.ShouldNotBe(0, "fixture: the file has fewer pages than expected");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + (i * 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(entry)) != TiffTag.Compression) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(entry + 8), compression);
            return;
        }
        throw new InvalidOperationException("fixture: no Compression tag");
    }

    /// <summary>Halves the byte count of strip 0 (a single-strip little-endian page), simulating a cut file.</summary>
    private static void PatchFirstStripByteCount(byte[] tiff, TiffPageLayout layout)
    {
        tiff[0].ShouldBe((byte)'I');
        layout.SegmentCount.ShouldBe(1, "fixture: ImageMagick writes fax pages as one strip");
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
        for (var i = 0; i < count; i++)
        {
            var entry = ifd + 2 + (i * 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(entry)) != TiffTag.StripByteCounts) continue;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(entry + 2));
            if (type == 3)
            {
                var v = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(entry + 8));
                BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(entry + 8), (ushort)(v / 2));
            }
            else
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(entry + 8));
                BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(entry + 8), v / 2);
            }
            return;
        }
        throw new InvalidOperationException("fixture: no StripByteCounts tag");
    }
}
