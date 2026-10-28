using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Adam7 interlacing (PNG specification §8.2). The sharpest check needs no golden: the same pixels
/// written interlaced and non-interlaced by ImageMagick (libpng) must decode to the same bytes, and to
/// what libpng itself reads, for every colour type and depth at sizes that leave passes empty (any
/// side under 5) and end rows part-way through a byte. A test-only interlacing writer, whose pass
/// table is copied from the specification rather than shared with the decoder, adds what libpng
/// never writes: every filter type inside every pass, and IDATs cut into seven-byte pieces.
/// </summary>
public sealed class PngAdam7Tests
{
    private static readonly (int Width, int Height)[] Sizes =
        [(1, 1), (1, 5), (5, 1), (2, 2), (3, 3), (4, 4), (5, 5), (7, 9), (8, 8), (9, 9), (13, 17), (33, 17)];

    public static TheoryData<int, int, bool, int, int> Cases()
    {
        var data = new TheoryData<int, int, bool, int, int>();
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
            foreach (var (width, height) in Sizes)
                data.Add(colorType, depth, paletteAlpha, width, height);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void InterlacedDecodesToTheSamePixelsAsNonInterlaced(int colorType, int depth, bool paletteAlpha, int width, int height)
    {
        var interlaced = PngRgba8ExpansionTests.Encode(colorType, depth, paletteAlpha, width, height, interlace: true);
        var plain = PngRgba8ExpansionTests.Encode(colorType, depth, paletteAlpha, width, height, interlace: false);

        using var reference = new MagickImage(interlaced);
        using var referencePixels = reference.GetPixels();
        var expected = referencePixels.ToShortArray(PixelMapping.RGBA)!.Select(v => (byte)(v >> 8)).ToArray();

        var rgba = new byte[width * height * 4];
        PngImageDecoder.TryDecodeIntoRgba8(interlaced, rgba).ShouldBeTrue();
        rgba.ShouldBe(expected);
        PngImageDecoder.TryDecodeRgba8(interlaced, out var allocated, out _, out _).ShouldBeTrue();
        allocated.ShouldBe(expected);

        var fromInterlaced = PngReader.Decode(interlaced);
        var fromPlain = PngReader.Decode(plain);
        fromInterlaced.ToRgba8().ShouldBe(expected);
        fromPlain.ToRgba8().ShouldBe(expected);
        (fromInterlaced.Width, fromInterlaced.Height, fromInterlaced.BitDepth, fromInterlaced.SourceBitDepth, fromInterlaced.ColorType)
            .ShouldBe((width, height, fromPlain.BitDepth, depth, colorType));
        if (fromInterlaced.Palette.AsSpan().SequenceEqual(fromPlain.Palette))
            fromInterlaced.Pixels.ShouldBe(fromPlain.Pixels);
    }

    public static TheoryData<int, int, int, int> HandBuiltCases()
    {
        var data = new TheoryData<int, int, int, int>();
        foreach (var (colorType, depth) in new[] { (2, 8), (0, 2), (6, 16), (4, 8) })
        {
            foreach (var (width, height) in new[] { (1, 1), (3, 2), (9, 9), (31, 23) })
                data.Add(colorType, depth, width, height);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(HandBuiltCases))]
    public void EveryFilterInEveryPassAndTinyIdats(int colorType, int depth, int width, int height)
    {
        var (png, samples) = HandBuiltAdam7(colorType, depth, width, height, idatPiece: 7);

        // The writer is checked against libpng before anything is checked against it.
        using (var libpng = new MagickImage(png))
        {
            libpng.Width.ShouldBe((uint)width);
            using var pixels = libpng.GetPixels();
            var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
            var map = colorType switch { 0 => "R", 2 => "RGB", 4 => "RA", _ => "RGBA" };
            var read = pixels.ToShortArray(0, 0, (uint)width, (uint)height, map)!;
            for (var i = 0; i < width * height * channels; i++)
                ((int)read[i]).ShouldBe(depth == 16 ? samples[i] : samples[i] * 65535 / ((1 << depth) - 1), $"sample {i}");
        }

        var image = PngReader.Decode(png);
        for (var i = 0; i < samples.Length; i++)
        {
            var actual = depth == 16
                ? BinaryPrimitives.ReadUInt16BigEndian(image.Pixels.AsSpan(2 * i))
                : depth == 8 ? image.Pixels[i] : image.Pixels[i] * ((1 << depth) - 1) / 255;
            actual.ShouldBe(samples[i], $"sample {i}");
        }

        var rgba = new byte[width * height * 4];
        PngImageDecoder.TryDecodeIntoRgba8(png, rgba).ShouldBeTrue();
        rgba.ShouldBe(image.ToRgba8());
    }

    [Fact]
    public void AStreamThatEndsInsideAPassIsRefused()
    {
        var (png, _) = HandBuiltAdam7(2, 8, 31, 23, idatPiece: int.MaxValue, cut: 0.6);

        PngImageDecoder.TryDecodeIntoRgba8(png, new byte[31 * 23 * 4]).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => PngReader.Decode(png));
    }

    [Fact]
    public void BytesPastTheLastPassAreRefused()
    {
        var (png, _) = HandBuiltAdam7(2, 8, 9, 9, idatPiece: int.MaxValue, extra: 4);

        PngImageDecoder.TryDecodeIntoRgba8(png, new byte[9 * 9 * 4]).ShouldBeFalse();
        Should.Throw<InvalidDataException>(() => PngReader.Decode(png));
    }

    [Fact]
    public void AHugeInterlacedHeaderWithNoDataFailsCleanly()
    {
        // 2^30 × 2^30 claimed, a few bytes of data: refused, without trying to allocate the image.
        var (png, _) = HandBuiltAdam7(2, 8, 2, 2, idatPiece: int.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 1 << 30);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 1 << 30);
        FixCrc(png, 8); // IHDR: length at 8, type at 12

        PngImageDecoder.TryDecodeRgba8(png, out _, out _, out _).ShouldBeFalse();
        PngReader.TryDecode(png, out _).ShouldBeFalse();
    }

    // ---- a test-only Adam7 writer, from the specification (§8.2, §9) -----------------------------

    // Pass k starts at (column, row) and steps by (column increment, row increment).
    private static readonly int[] StartColumn = [0, 4, 0, 2, 0, 1, 0];
    private static readonly int[] StartRow = [0, 0, 4, 0, 2, 0, 1];
    private static readonly int[] ColumnStep = [8, 8, 4, 4, 2, 2, 1];
    private static readonly int[] RowStep = [8, 8, 8, 4, 4, 2, 2];

    /// <summary>
    /// An interlaced PNG of random samples (returned row-major, one int per sample). The rows of each
    /// pass use filters 0-4 in turn, starting at a different filter per pass; the zlib stream is split
    /// into IDATs of <paramref name="idatPiece"/> bytes. <paramref name="cut"/> keeps only that fraction
    /// of the zlib stream; <paramref name="extra"/> appends that many bytes after the last pass.
    /// </summary>
    private static (byte[] Png, int[] Samples) HandBuiltAdam7(int colorType, int depth, int width, int height,
        int idatPiece, double cut = 1, int extra = 0)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        var random = new Random(colorType * 31 + depth * 7 + width * 3 + height);
        var samples = new int[width * height * channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = random.Next(1 << depth);

        var bitsPerPixel = channels * depth;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        using var raw = new MemoryStream();
        for (var pass = 0; pass < 7; pass++)
        {
            var passWidth = width > StartColumn[pass] ? (width - StartColumn[pass] + ColumnStep[pass] - 1) / ColumnStep[pass] : 0;
            var passHeight = height > StartRow[pass] ? (height - StartRow[pass] + RowStep[pass] - 1) / RowStep[pass] : 0;
            if (passWidth == 0 || passHeight == 0)
                continue; // an empty pass has no rows, not even filter bytes

            var rowBytes = (passWidth * bitsPerPixel + 7) / 8;
            var above = new byte[rowBytes];
            for (var r = 0; r < passHeight; r++)
            {
                var y = StartRow[pass] + r * RowStep[pass];
                var row = new byte[rowBytes];
                for (var c = 0; c < passWidth; c++)
                {
                    var x = StartColumn[pass] + c * ColumnStep[pass];
                    for (var k = 0; k < channels; k++)
                        PutSample(row, c * channels + k, depth, samples[(y * width + x) * channels + k]);
                }

                var filter = (pass + r) % 5;
                raw.WriteByte((byte)filter);
                raw.Write(Filter(filter, row, above, bpp));
                above = row;
            }
        }
        raw.Write(new byte[extra]);

        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw.ToArray());
        var zlib = deflated.ToArray().AsSpan(0, (int)(deflated.Length * cut)).ToArray();

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        ihdr[12] = 1; // Adam7
        PngRgba8ExpansionTests.Chunk(png, "IHDR"u8, ihdr);
        for (var at = 0; at < zlib.Length; at += idatPiece)
            PngRgba8ExpansionTests.Chunk(png, "IDAT"u8, zlib.AsSpan(at, Math.Min(idatPiece, zlib.Length - at)));
        PngRgba8ExpansionTests.Chunk(png, "IEND"u8, []);
        return (png.ToArray(), samples);
    }

    private static void PutSample(byte[] row, int index, int depth, int value)
    {
        if (depth == 16)
        {
            BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(2 * index), (ushort)value);
        }
        else if (depth == 8)
        {
            row[index] = (byte)value;
        }
        else
        {
            var bit = index * depth;
            row[bit / 8] |= (byte)(value << (8 - depth - bit % 8));
        }
    }

    // §9.2: the filtered byte is the sample minus the predictor, from the unfiltered neighbours.
    private static byte[] Filter(int filter, byte[] row, byte[] above, int bpp)
    {
        var output = new byte[row.Length];
        for (var i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = above[i], c = i >= bpp ? above[i - bpp] : 0;
            var predictor = filter switch
            {
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => 0,
            };
            output[i] = (byte)(row[i] - predictor);
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // Recomputes the CRC of the chunk whose length field is at offset.
    private static void FixCrc(byte[] png, int offset)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
        var c = 0xFFFFFFFFu;
        foreach (var b in png.AsSpan(offset + 4, 4 + length))
        {
            c ^= b;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(offset + 8 + length), c ^ 0xFFFFFFFFu);
    }
}
