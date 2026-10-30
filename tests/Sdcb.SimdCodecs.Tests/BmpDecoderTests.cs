using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Shouldly;
using W = Sdcb.SimdCodecs.Tests.TestBmpWriter;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// BMP decoding of hand-assembled streams (<see cref="TestBmpWriter"/>): every header, depth, mask and
/// compression, with expected pixels from the test's own model of the documented layouts. Each case is also
/// decoded into every <see cref="ImagePixelFormat"/> with a padded stride (<see cref="CheckEveryFormat"/>), and
/// through the facade.
/// </summary>
public sealed class BmpDecoderTests
{
    internal static readonly int[] Widths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 33, 67];

    // ---- Expected-pixel model --------------------------------------------------------------------------------

    /// <summary>A channel of <paramref name="bits"/> bits to 8: its bit pattern repeated from the top (fewer than
    /// 8 bits), or its top 8 bits.</summary>
    internal static byte Widen(uint value, int bits)
    {
        if (bits >= 8) return (byte)(value >> (bits - 8));
        uint repeated = 0;
        var filled = 0;
        while (filled < 8)
        {
            repeated = repeated << bits | value;
            filled += bits;
        }
        return (byte)(repeated >> (filled - 8));
    }

    internal static byte Field(uint pixel, uint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var bits = System.Numerics.BitOperations.PopCount(mask);
        return Widen((pixel & mask) >> shift, bits);
    }

    internal static byte[] Image(int width, int height, Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b, a) = pixel(x, y);
            var i = (y * width + x) * 4;
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)r, (byte)g, (byte)b, (byte)a);
        }
        return rgba;
    }

    internal static int[] RandomPalette(Random random, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => random.Next(1 << 24))];

    // ---- Checks ----------------------------------------------------------------------------------------------

    /// <summary>Decodes to RGBA and compares, then checks every other output layout against it.</summary>
    internal static void ShouldDecodeTo(byte[] bmp, int width, int height, byte[] expectedRgba, bool hasAlpha, string because)
    {
        var info = BmpDecoder.ReadInfo(bmp);
        (info.Width, info.Height, info.HasAlpha).ShouldBe((width, height, hasAlpha), because);
        var rgba = new byte[width * height * 4];
        BmpDecoder.DecodeTo(bmp, rgba);
        ShouldMatch(rgba, expectedRgba, because);
        CheckEveryFormat(bmp, because);
    }

    internal static void ShouldMatch(byte[] actual, byte[] expected, string because)
    {
        actual.Length.ShouldBe(expected.Length, because);
        for (var i = 0; i < actual.Length; i += 4)
        {
            if (!actual.AsSpan(i, 4).SequenceEqual(expected.AsSpan(i, 4)))
                throw new ShouldAssertException($"{because}: pixel {i / 4} is {Convert.ToHexString(actual, i, 4)}, expected {Convert.ToHexString(expected, i, 4)}");
        }
    }

    /// <summary>
    /// The adapter contract: <c>TryDecodeInto</c> in every layout, with a padded stride, is exactly
    /// <see cref="PixelConverter.ConvertRow"/> of <c>TryDecode</c>'s raster, and leaves the padding alone; the
    /// facade agrees.
    /// </summary>
    internal static void CheckEveryFormat(byte[] bmp, string because)
    {
        BmpImageDecoder.TryDecode(bmp, out var image).ShouldBeTrue(because);
        BmpImageDecoder.TryReadInfo(bmp, out var info).ShouldBeTrue(because);
        (info.Width, info.Height, info.Channels, info.Format).ShouldBe((image!.Width, image.Height, image.Channels, ImageFormat.Bmp), because);
        int width = image.Width, height = image.Height;
        foreach (var format in Enum.GetValues<ImagePixelFormat>())
        {
            var bpp = PixelConverter.BytesPerPixel(format);
            var stride = width * bpp + 3;
            var expected = Enumerable.Repeat((byte)0xCD, stride * height).ToArray();
            for (var y = 0; y < height; y++)
            {
                PixelConverter.ConvertRow(image.Pixels.Slice(y * width * image.Channels, width * image.Channels), image.Channels,
                    SampleFormat.UInt8, expected.AsSpan(y * stride, width * bpp), format, width);
            }
            var actual = Enumerable.Repeat((byte)0xCD, stride * height).ToArray();
            BmpImageDecoder.TryDecodeInto(bmp, actual, stride, format).ShouldBeTrue($"{because}, {format}");
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                var at = actual.AsSpan().CommonPrefixLength(expected);
                throw new ShouldAssertException($"{because}, {format}: byte {at} (row {at / stride}) is {actual[at]:X2}, expected {expected[at]:X2}");
            }
        }
        SimdCodecs.TryDecode(bmp, out var viaFacade).ShouldBeTrue(because);
        viaFacade!.ToRgba8().ShouldBe(image.ToRgba8(), because);
    }

    // ---- Palettes --------------------------------------------------------------------------------------------

    public static TheoryData<int, int, bool> Headers()
    {
        var data = new TheoryData<int, int, bool>();
        foreach (var size in new[] { 12, 16, 24, 40, 52, 56, 64, 108, 124 })
        foreach (var bpp in new[] { 1, 2, 4, 8 })
        foreach (var topDown in new[] { false, true })
        {
            if (size == 12 && (bpp == 2 || topDown)) continue; // a core header has neither
            data.Add(size, bpp, topDown);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Headers))]
    public void Palette_images_decode_for_every_header_depth_and_width(int headerSize, int bpp, bool topDown)
    {
        var random = new Random(headerSize * 100 + bpp * 2 + (topDown ? 1 : 0));
        foreach (var width in Widths)
        {
            const int height = 5;
            var palette = RandomPalette(random, 1 << bpp);
            var indices = new int[width * height];
            for (var i = 0; i < indices.Length; i++) indices[i] = random.Next(1 << bpp);
            var header = headerSize == 12
                ? W.CoreHeader(width, height, bpp)
                : W.InfoHeader(headerSize, width, topDown ? -height : height, bpp, 0);
            var bmp = W.File(header, W.Palette(palette, headerSize == 12 ? 3 : 4),
                W.Rows(width, height, bpp, (x, y) => (uint)indices[y * width + x], topDown));
            var expected = Image(width, height, (x, y) =>
            {
                var c = palette[indices[y * width + x]];
                return (c >> 16 & 255, c >> 8 & 255, c & 255, 255);
            });
            ShouldDecodeTo(bmp, width, height, expected, hasAlpha: false, $"header {headerSize}, {bpp}-bit, width {width}, top-down {topDown}");
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 3)]
    [InlineData(8, 5)]
    [InlineData(8, 200)]
    public void A_short_palette_reads_only_its_entries_and_indices_past_it_are_opaque_black(int bpp, int colorsUsed)
    {
        const int width = 33, height = 3;
        var random = new Random(bpp * 1000 + colorsUsed);
        var palette = RandomPalette(random, colorsUsed);
        var indices = Enumerable.Range(0, width * height).Select(_ => random.Next(1 << bpp)).ToArray();
        var bmp = W.File(W.InfoHeader(40, width, height, bpp, 0, colorsUsed: (uint)colorsUsed), W.Palette(palette),
            W.Rows(width, height, bpp, (x, y) => (uint)indices[y * width + x]));
        var expected = Image(width, height, (x, y) =>
        {
            var i = indices[y * width + x];
            var c = i < colorsUsed ? palette[i] : 0;
            return (c >> 16 & 255, c >> 8 & 255, c & 255, 255);
        });
        ShouldDecodeTo(bmp, width, height, expected, false, $"{bpp}-bit, {colorsUsed} colours");
        BmpDecoder.ReadInfo(bmp).PaletteSize.ShouldBe(colorsUsed);
    }

    [Fact]
    public void A_palette_cut_short_by_the_pixel_offset_ends_there()
    {
        // biClrUsed says 0 (all 256) but the pixels start after 4 entries: the rest are not palette.
        const int width = 9, height = 2;
        var palette = new[] { 0x102030, 0x405060, 0x708090, 0xA0B0C0 };
        var indices = new[] { 0, 1, 2, 3, 4, 200, 255, 1, 2 };
        var bmp = W.File(W.InfoHeader(40, width, height, 8, 0), W.Palette(palette),
            W.Rows(width, height, 8, (x, _) => (uint)indices[x]));
        var expected = Image(width, height, (x, _) =>
        {
            var c = indices[x] < 4 ? palette[indices[x]] : 0;
            return (c >> 16 & 255, c >> 8 & 255, c & 255, 255);
        });
        ShouldDecodeTo(bmp, width, height, expected, false, "4-entry palette");
        BmpDecoder.ReadInfo(bmp).PaletteSize.ShouldBe(4);
    }

    // ---- Direct colour ---------------------------------------------------------------------------------------

    public static TheoryData<string, int, uint, int, uint, uint, uint, uint> Masks() => new()
    {
        // name, header size, compression, bpp, red, green, blue, alpha (masks after a 40-byte header, inside larger ones)
        { "555 BI_RGB", 40, 0, 16, 0, 0, 0, 0 },
        { "565", 40, 3, 16, 0xF800, 0x07E0, 0x001F, 0 },
        { "565 V2", 52, 3, 16, 0xF800, 0x07E0, 0x001F, 0 },
        { "1555", 56, 3, 16, 0x7C00, 0x03E0, 0x001F, 0x8000 },
        { "4444", 108, 3, 16, 0x0F00, 0x00F0, 0x000F, 0xF000 },
        { "4444 alphabitfields", 40, 6, 16, 0x0F00, 0x00F0, 0x000F, 0xF000 },
        { "332 in 16", 40, 3, 16, 0x00E0, 0x001C, 0x0003, 0 },
        { "blue only", 40, 3, 16, 0, 0, 0x001F, 0 },
        { "BGRX BI_RGB 32", 40, 0, 32, 0, 0, 0, 0 },
        { "BGRX bitfields", 40, 3, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0 },
        { "BGRA V4", 108, 3, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 },
        { "RGBA V5", 124, 3, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000 },
        { "ABGR V3", 56, 3, 32, 0xFF000000, 0x00FF0000, 0x0000FF00, 0x000000FF },
        { "XRGB alphabitfields no alpha", 40, 6, 32, 0x0000FF00, 0x00FF0000, 0xFF000000, 0 },
        { "1010102 V4", 108, 3, 32, 0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000 },
        { "12-bit fields", 108, 3, 32, 0x00000FFF, 0x00FFF000, 0xFF000000, 0 },
        { "OS/2 64 header", 64, 0, 32, 0, 0, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Masks))]
    public void Direct_colour_pixels_widen_each_mask(string name, int headerSize, uint compression, int bpp, uint red, uint green, uint blue, uint alpha)
    {
        var random = new Random(name.GetHashCode(StringComparison.Ordinal));
        if (compression == 0)
        {
            (red, green, blue, alpha) = bpp == 16 ? (0x7C00u, 0x03E0u, 0x001Fu, 0u) : (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u);
        }
        foreach (var width in Widths)
        foreach (var topDown in new[] { false, true })
        {
            const int height = 4;
            var pixels = Enumerable.Range(0, width * height).Select(_ => (uint)random.NextInt64(1L << bpp)).ToArray();
            var masksInside = headerSize >= 52 && headerSize != 64;
            var header = W.InfoHeader(headerSize, width, topDown ? -height : height, bpp, compression,
                red: masksInside ? red : 0, green: masksInside ? green : 0, blue: masksInside ? blue : 0, alpha: headerSize >= 56 ? alpha : 0);
            var after = compression == 0 || masksInside ? [] : compression == 6 ? W.Masks(red, green, blue, alpha) : W.Masks(red, green, blue);
            var bmp = W.File(header, after, W.Rows(width, height, bpp, (x, y) => pixels[y * width + x], topDown));
            var hasAlpha = alpha != 0;
            var expected = Image(width, height, (x, y) =>
            {
                var p = pixels[y * width + x];
                return (Field(p, red), Field(p, green), Field(p, blue), hasAlpha ? Field(p, alpha) : 255);
            });
            ShouldDecodeTo(bmp, width, height, expected, hasAlpha, $"{name}, width {width}, top-down {topDown}");
        }
    }

    [Fact]
    public void Twenty_four_bit_rows_are_blue_green_red()
    {
        var random = new Random(24);
        foreach (var width in Widths)
        foreach (var headerSize in new[] { 12, 40, 108 })
        {
            const int height = 3;
            var pixels = Enumerable.Range(0, width * height).Select(_ => (uint)random.Next(1 << 24)).ToArray();
            var header = headerSize == 12 ? W.CoreHeader(width, height, 24) : W.InfoHeader(headerSize, width, height, 24, 0);
            var bmp = W.File(header, [], W.Rows(width, height, 24, (x, y) => pixels[y * width + x]));
            var expected = Image(width, height, (x, y) =>
            {
                var p = pixels[y * width + x];
                return ((int)(p >> 16 & 255), (int)(p >> 8 & 255), (int)(p & 255), 255);
            });
            ShouldDecodeTo(bmp, width, height, expected, false, $"24-bit, header {headerSize}, width {width}");
        }
    }

    [Fact]
    public void A_32_bit_BI_RGB_fourth_byte_is_alpha_unless_it_is_zero_everywhere()
    {
        const int width = 17, height = 3;
        var random = new Random(32);
        var pixels = Enumerable.Range(0, width * height).Select(_ => (uint)random.Next(1 << 24)).ToArray();
        var zero = W.File(W.InfoHeader(40, width, height, 32, 0), [], W.Rows(width, height, 32, (x, y) => pixels[y * width + x]));
        ShouldDecodeTo(zero, width, height, Image(width, height, (x, y) =>
        {
            var p = pixels[y * width + x];
            return ((int)(p >> 16 & 255), (int)(p >> 8 & 255), (int)(p & 255), 255);
        }), hasAlpha: true, "alpha 0 everywhere: opaque");

        var oneAlpha = W.File(W.InfoHeader(40, width, height, 32, 0), [], W.Rows(width, height, 32,
            (x, y) => pixels[y * width + x] | (x == 5 && y == 1 ? 0x80000000u : 0)));
        ShouldDecodeTo(oneAlpha, width, height, Image(width, height, (x, y) =>
        {
            var p = pixels[y * width + x];
            return ((int)(p >> 16 & 255), (int)(p >> 8 & 255), (int)(p & 255), x == 5 && y == 1 ? 128 : 0);
        }), hasAlpha: true, "one pixel with alpha: every byte is alpha");
    }

    // ---- Run-length coding -----------------------------------------------------------------------------------

    [Fact]
    public void Rle8_paints_runs_literals_deltas_and_leaves_skipped_pixels_opaque_black()
    {
        var palette = RandomPalette(new Random(8), 7);
        var rle = new W.Rle(9, 6, 8, palette)
            .Run(3, 1).Literal(2, 3, 4).Run(4, 5).EndOfLine()          // a full row, run clipped at the edge
            .Literal(6, 5, 4, 3, 2).Delta(2, 1).Run(2, 6).EndOfLine()  // odd literal (padded), a delta up a row
            .Run(200, 2).EndOfLine()                                   // a run past the edge
            .Delta(4, 1).Literal(9, 1, 2).EndOfBitmap();               // an index past the palette; rows above stay black
        var bmp = W.File(W.InfoHeader(40, 9, 6, 8, 1, colorsUsed: 7), W.Palette(palette), rle.Stream);
        ShouldDecodeTo(bmp, 9, 6, rle.Expected, false, "RLE8");
    }

    [Fact]
    public void Rle4_alternates_the_nibbles_of_a_run_and_packs_literals()
    {
        var palette = RandomPalette(new Random(4), 16);
        var rle = new W.Rle(11, 4, 4, palette)
            .Run(5, 0x3A).Literal(1, 2, 3, 4, 5).EndOfLine()
            .Literal(15, 14, 13).Run(7, 0x77).Run(3, 0x12).EndOfLine()
            .Delta(3, 1).Run(2, 0x4F).Literal(6, 7, 8, 9);              // no end of bitmap: the data just ends
        var bmp = W.File(W.InfoHeader(40, 11, 4, 4, 2), W.Palette(palette), rle.Stream);
        ShouldDecodeTo(bmp, 11, 4, rle.Expected, false, "RLE4");
    }

    [Fact]
    public void Os2_rle24_paints_blue_green_red_triples()
    {
        var rle = new W.Rle(7, 3, 24, [])
            .Run(4, 0x123456).Literal(0xFF0000, 0x00FF00, 0x0000FF).EndOfLine() // a 9-byte literal, padded
            .Literal(0xABCDEF, 0x010203, 0x0A0B0C, 0x102030).Run(2, 0x7F7F7F).EndOfLine()
            .Delta(1, 0).Run(3, 0x00FFFF).EndOfBitmap();
        foreach (var size in new[] { 40, 64 })
        {
            var bmp = W.File(W.InfoHeader(size, 7, 3, 24, 4), [], rle.Stream);
            ShouldDecodeTo(bmp, 7, 3, rle.Expected, false, $"RLE24, header {size}");
            BmpDecoder.ReadInfo(bmp).Compression.ShouldBe(BmpCompression.Rle24);
        }
    }

    // ---- OS/2 Huffman 1D -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, false)]
    [InlineData(13, false)]
    [InlineData(64, true)]
    [InlineData(1500, true)]
    [InlineData(3000, false)]
    public void Huffman_1d_rows_are_t4_runs_starting_white(int width, bool eols)
    {
        const int height = 7;
        var random = new Random(width);
        var bits = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            // Long runs and single pixels both, so makeup codes and chains past 2560 appear.
            var v = random.Next(2);
            for (var x = 0; x < width; x++)
            {
                if (random.Next(y % 2 == 0 ? 400 : 3) == 0) v ^= 1;
                bits[y * width + x] = v;
            }
        }
        var stream = HuffmanStream(bits, width, height, eols);
        var palette = new[] { 0xFFFFF0, 0x101010 };
        var bmp = W.File(W.InfoHeader(64, width, height, 1, 3), W.Palette(palette), stream);
        BmpDecoder.ReadInfo(bmp).Compression.ShouldBe(BmpCompression.Huffman1D);
        var expected = Image(width, height, (x, y) =>
        {
            var c = palette[bits[y * width + x]];
            return (c >> 16 & 255, c >> 8 & 255, c & 255, 255);
        });
        ShouldDecodeTo(bmp, width, height, expected, false, $"Huffman 1D, width {width}");
    }

    /// <summary>T.4 Modified Huffman: each row's runs, white first, bottom row first; an EOL before each row if asked.</summary>
    internal static byte[] HuffmanStream(int[] bits, int width, int height, bool eols)
    {
        var output = new List<bool>();
        void Put(string code) => output.AddRange(code.Select(c => c == '1'));
        void Run(int colour, int length)
        {
            var table = Sdcb.SimdCodecs.Tiff.MmrCodes.TableFor(colour);
            var extended = Sdcb.SimdCodecs.Tiff.MmrCodes.ExtendedMakeupTable;
            while (length >= 2560)
            {
                Put(extended.Single(e => e.Run == 2560).Bits);
                length -= 2560;
            }
            if (length >= 64)
            {
                var makeup = length / 64 * 64;
                Put((makeup >= 1792 ? extended : table).Single(e => e.Run == makeup).Bits);
                length -= makeup;
            }
            Put(table.Single(e => e.Run == length).Bits);
        }

        for (var r = 0; r < height; r++)
        {
            if (eols) Put("000000000001");
            var y = height - 1 - r;
            var colour = 0;
            var x = 0;
            while (x < width)
            {
                var end = x;
                while (end < width && bits[y * width + end] == colour) end++;
                Run(colour, end - x);
                x = end;
                colour ^= 1;
            }
        }
        var bytes = new byte[(output.Count + 7) / 8];
        for (var i = 0; i < output.Count; i++)
        {
            if (output[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return bytes;
    }

    // ---- Containers and metadata -----------------------------------------------------------------------------

    [Fact]
    public void Bi_png_decodes_the_embedded_stream()
    {
        var random = new Random(5);
        var rgba = new byte[13 * 6 * 4];
        random.NextBytes(rgba);
        var png = Png.PngWriter.Encode(rgba, 13, 6);
        var bmp = W.File(W.InfoHeader(40, 13, 6, 0, 5), [], png);
        BmpDecoder.ReadInfo(bmp).Compression.ShouldBe(BmpCompression.Png);
        ShouldDecodeTo(bmp, 13, 6, rgba, hasAlpha: true, "BI_PNG");

        var wrongSize = W.File(W.InfoHeader(40, 12, 6, 0, 5), [], png);
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(wrongSize));
    }

    [Fact]
    public void A_v5_embedded_profile_is_returned()
    {
        var profile = new byte[300];
        new Random(124).NextBytes(profile);
        const int width = 5, height = 2;
        var pixels = W.Rows(width, height, 24, (x, y) => (uint)(x * 40 << 8 | y * 90));
        // bV5ProfileData counts from the start of the header; the profile follows the pixels.
        var header = W.InfoHeader(124, width, height, 24, 0, colorSpace: 0x4D424544, profileOffset: (uint)(124 + pixels.Length), profileSize: 300);
        var bmp = W.File(header, [], W.Concat(pixels, profile));
        BmpDecoder.Decode(bmp).IccProfile.ShouldBe(profile);
        BmpImageDecoder.TryDecode(bmp, out var image).ShouldBeTrue();
        image!.IccProfile.ToArray().ShouldBe(profile);

        // A profile that points outside the data is ignored, not fatal.
        var broken = W.File(W.InfoHeader(124, width, height, 24, 0, colorSpace: 0x4D424544, profileOffset: 5000, profileSize: 300), [], pixels);
        BmpDecoder.Decode(broken).IccProfile.ShouldBeNull();
    }

    [Fact]
    public void An_os2_bitmap_array_decodes_its_first_bitmap()
    {
        const int width = 6, height = 3;
        var palette = new[] { 0x112233, 0x445566 };
        var pixels = W.Rows(width, height, 1, (x, y) => (uint)((x + y) & 1));
        var header = W.InfoHeader(64, width, height, 1, 0);
        // BITMAPARRAYFILEHEADER2 (type 'BA', size, offNext 0, display size), then the bitmap's file header.
        var array = new byte[14];
        array[0] = (byte)'B';
        array[1] = (byte)'A';
        BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan(2), 40);
        var file = W.File(header, W.Palette(palette), pixels);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)(14 + 14 + header.Length + 8)); // offBits from the start of the array
        var bmp = W.Concat(array, file);
        var expected = Image(width, height, (x, y) =>
        {
            var c = palette[(x + y) & 1];
            return (c >> 16 & 255, c >> 8 & 255, c & 255, 255);
        });
        ShouldDecodeTo(bmp, width, height, expected, false, "OS/2 bitmap array");
    }

    [Fact]
    public void Pixels_follow_the_palette_when_the_file_offset_points_into_the_headers()
    {
        const int width = 3, height = 2;
        var palette = new[] { 0xFF0000, 0x00FF00 };
        var bmp = W.File(W.InfoHeader(40, width, height, 8, 0, colorsUsed: 2), W.Palette(palette),
            W.Rows(width, height, 8, (x, _) => (uint)(x & 1)), offBits: 0);
        ShouldDecodeTo(bmp, width, height, Image(width, height, (x, _) => x % 2 == 0 ? (255, 0, 0, 255) : (0, 255, 0, 255)), false, "offBits 0");
    }

    [Fact]
    public void The_last_row_may_leave_out_its_padding_but_not_its_pixels()
    {
        const int width = 5, height = 3;
        var rows = W.Rows(width, height, 24, (x, y) => (uint)(x * 50 + y));
        var bmp = W.File(W.InfoHeader(40, width, height, 24, 0), [], rows[..^1]); // 15 bytes of pixels, 1 of padding
        BmpDecoder.Decode(bmp).Width.ShouldBe(width);
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(bmp[..^2]));
    }

    [Fact]
    public void Packed_dibs_decode_like_the_file()
    {
        var random = new Random(7);
        var palette = RandomPalette(random, 16);
        var bmp = W.File(W.InfoHeader(40, 21, 4, 4, 0, colorsUsed: 16), W.Palette(palette),
            W.Rows(21, 4, 4, (_, _) => (uint)random.Next(16)));
        var dib = bmp[14..];
        BmpDecoder.ReadDibInfo(dib).ShouldBe(BmpDecoder.ReadInfo(bmp));
        BmpDecoder.DecodeDib(dib).Pixels.ShouldBe(BmpDecoder.Decode(bmp).Pixels);
        var into = new byte[21 * 4 * 3];
        BmpDecoder.DecodeDibTo(dib, into, ImagePixelFormat.Bgr24);
        var expected = new byte[into.Length];
        BmpDecoder.DecodeTo(bmp, expected, ImagePixelFormat.Bgr24);
        into.ShouldBe(expected);
    }

    // ---- Icon images -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(32)]
    public void An_icon_image_takes_alpha_from_its_and_mask_unless_32_bit_pixels_carry_it(int bpp)
    {
        foreach (var width in new[] { 16, 17, 32, 33 })
        {
            const int height = 9;
            var random = new Random(bpp * 100 + width);
            var palette = bpp <= 8 ? RandomPalette(random, 1 << bpp) : [];
            var values = Enumerable.Range(0, width * height).Select(_ => (uint)random.NextInt64(bpp == 32 ? 1L << 24 : 1L << Math.Min(bpp, 24))).ToArray();
            var mask = Enumerable.Range(0, width * height).Select(_ => random.Next(3) == 0).ToArray();
            var andMask = W.Rows(width, height, 1, (x, y) => mask[y * width + x] ? 1u : 0u);
            int Colour(int x, int y)
            {
                var v = values[y * width + x];
                return bpp <= 8 ? palette[v] : (int)(v & 0xFFFFFF);
            }
            var dib = W.Concat(W.InfoHeader(40, width, 2 * height, bpp, 0), W.Palette(palette),
                W.Rows(width, height, bpp, (x, y) => values[y * width + x]), andMask);
            var expected = Image(width, height, (x, y) =>
            {
                var c = Colour(x, y);
                return (c >> 16 & 255, c >> 8 & 255, c & 255, mask[y * width + x] ? 0 : 255);
            });
            var image = BmpDecoder.DecodeDib(dib, iconImage: true);
            (image.Width, image.Height, image.Channels).ShouldBe((width, height, 4));
            ShouldMatch(image.Pixels, expected, $"{bpp}-bit icon, width {width}");

            // Without its AND mask an icon image is opaque.
            var unmasked = BmpDecoder.DecodeDib(dib[..^andMask.Length], iconImage: true);
            ShouldMatch(unmasked.Pixels, Image(width, height, (x, y) =>
            {
                var c = Colour(x, y);
                return (c >> 16 & 255, c >> 8 & 255, c & 255, 255);
            }), $"{bpp}-bit icon without a mask, width {width}");
        }
    }

    [Fact]
    public void A_32_bit_icon_image_with_alpha_ignores_its_and_mask()
    {
        const int width = 16, height = 16;
        var dib = W.Concat(W.InfoHeader(40, width, 2 * height, 32, 0),
            W.Rows(width, height, 32, (x, y) => (uint)((x * 16) << 24 | 0x00405060)),
            W.Rows(width, height, 1, (_, _) => 1)); // a mask that would hide everything
        var image = BmpDecoder.DecodeDib(dib, iconImage: true);
        ShouldMatch(image.Pixels, Image(width, height, (x, _) => (0x40, 0x50, 0x60, x * 16)), "32-bit icon with alpha");
    }

    // ---- Refusals --------------------------------------------------------------------------------------------

    public static TheoryData<string, byte[]> Corrupt()
    {
        var pixels = new byte[64];
        return new()
        {
            { "BI_JPEG", W.File(W.InfoHeader(40, 4, 4, 0, 4), [], pixels) },
            { "64-bit", W.File(W.InfoHeader(40, 2, 2, 64, 0), [], pixels) },
            { "top-down RLE8", W.File(W.InfoHeader(40, 4, -4, 8, 1), W.Palette([0]), [0, 1]) },
            { "split mask", W.File(W.InfoHeader(40, 2, 2, 16, 3), W.Masks(0xF00F, 0x0F0, 0x0F00), pixels) },
            { "mask past 16 bits", W.File(W.InfoHeader(40, 2, 2, 16, 3), W.Masks(0x1F0000, 0x0F0, 0x00F), pixels) },
            { "header size 41", W.File(W.InfoHeader(40, 2, 2, 24, 0), [], pixels).Select((b, i) => i == 14 ? (byte)41 : b).ToArray() },
            { "zero width", W.File(W.InfoHeader(40, 0, 2, 24, 0), [], pixels) },
            { "huge", W.File(W.InfoHeader(40, 1 << 16, 1 << 14, 24, 0), [], pixels) },
            { "truncated pixels", W.File(W.InfoHeader(40, 8, 8, 24, 0), [], pixels) },
            { "RLE8 on 4 bits", W.File(W.InfoHeader(40, 4, 4, 4, 1), W.Palette(new int[16]), [0, 1]) },
            { "core 16-bit", W.File(W.CoreHeader(2, 2, 16), [], pixels) },
            { "truncated header", W.File(W.InfoHeader(40, 2, 2, 24, 0), [], [])[..30] },
        };
    }

    [Theory]
    [MemberData(nameof(Corrupt))]
    public void Unsupported_and_corrupt_bitmaps_are_refused(string name, byte[] bmp)
    {
        Should.Throw<InvalidDataException>(() => BmpDecoder.Decode(bmp), name);
        BmpImageDecoder.TryDecode(bmp, out _).ShouldBeFalse(name);
        BmpImageDecoder.TryDecodeInto(bmp, new byte[1 << 20], 0, ImagePixelFormat.Rgba32).ShouldBeFalse(name);
    }
}
