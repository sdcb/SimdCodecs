using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// PackBits (compression 32773) and PlanarConfig=Separate on the whole-page read, against files
/// written by libtiff through Magick.NET and against Magick's own read of the same bytes.
/// </summary>
/// <remarks>
/// <para>Planar-separate is the case where a reader goes wrong quietly: the offset arrays hold one run
/// of segments per sample, so a reader that treats them as chunky decodes the first third of the
/// segments as if they were the whole picture. The fixtures cover strips whose RowsPerStrip does not
/// divide the height and tiles with edge tiles on both axes, so the plane stride in the offset array and
/// the per-plane segment count are both exercised.</para>
///
/// <para>Q16 Magick stores 16 bits per channel, so 8-bit samples come back as v*257 and 32-bit integer
/// samples are only exact when they are multiples of 65537; the 32-bit fixtures are built that way.
/// Everything is compared as a fraction of the sample's full scale, which is what Magick exposes.</para>
/// </remarks>
public sealed class TiffPackBitsPlanarReadTests
{
    private const int Width = 70;
    private const int Height = 50;

    public enum Kind { U8, U16, U32, F32 }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var tiled in new[] { false, true })
        foreach (var planar in new[] { false, true })
        foreach (var spp in new[] { 1, 3 })
        foreach (var kind in Enum.GetValues<Kind>())
        foreach (var (compression, predictor) in new[]
        {
            (CompressionMethod.RLE, 1),
            (CompressionMethod.NoCompression, 1),
            (CompressionMethod.Zip, 1),
            (CompressionMethod.Zip, 2),
        })
        {
            yield return [tiled, planar, spp, kind, compression, predictor];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LibTiffFile_ReadsLikeMagick(bool tiled, bool planar, int spp, Kind kind,
        CompressionMethod compression, int predictor)
    {
        var source = Source(spp, kind);
        var tiff = WriteWithLibTiff(source, spp, kind, tiled, planar, compression, predictor);

        // Premises: libtiff really wrote what the case names. A define it ignored would leave a file
        // the old reader handles, and the case would pass having proved nothing.
        var tags = FirstIfdTags(tiff);
        tags[259].ShouldBe(compression switch
        {
            CompressionMethod.RLE => 32773u,
            CompressionMethod.Zip => 8u,
            _ => 1u,
        });
        tags.GetValueOrDefault((ushort)317, 1u).ShouldBe((uint)predictor);
        tags.GetValueOrDefault((ushort)277, 1u).ShouldBe((uint)spp);
        tags.GetValueOrDefault((ushort)284, 1u).ShouldBe(planar && spp > 1 ? 2u : 1u,
            "PlanarConfig is the point of the case");
        if (tiled)
        {
            tags[322].ShouldBe(32u);
            tags[323].ShouldBe(16u);
            tags.ShouldNotContainKey((ushort)273);
        }
        else
        {
            tags.ShouldContainKey((ushort)273);
            (Height % (int)tags[278]).ShouldNotBe(0, "RowsPerStrip should not divide the height");
        }

        var bytesPerSample = kind switch { Kind.U8 => 1, Kind.U16 => 2, _ => 4 };
        var viaRead = TiffReader.Read(tiff).Pages[0];
        viaRead.SamplesPerPixel.ShouldBe(spp);
        viaRead.BitsPerSample.ShouldBe(bytesPerSample * 8);
        viaRead.Pixels.Length.ShouldBe(Width * Height * spp * bytesPerSample);

        var sink = new CollectingSink();
        var viaInto = TiffReader.ReadInto(tiff, ref sink);
        viaInto.Pages[0].Pixels.ShouldBeEmpty();
        sink.Bytes.ToArray().ShouldBe(viaRead.Pixels, "ReadInto and Read are one path and must agree");

        // Exact against what libtiff was given (integers), then against Magick's read of the bytes.
        if (kind != Kind.F32)
            viaRead.Pixels.ShouldBe(source.Bytes);

        using var magick = new MagickImage(tiff);
        magick.Width.ShouldBe((uint)Width);
        magick.Height.ShouldBe((uint)Height);
        var theirs = magick.GetPixels().ToArray()!;
        theirs.Length.ShouldBe(Width * Height * spp);
        for (var i = 0; i < theirs.Length; i++)
        {
            var ours = Fraction(viaRead.Pixels, i, kind);
            Math.Abs(ours - (theirs[i] / 65535.0)).ShouldBeLessThan(kind == Kind.F32 ? 1e-5 : 1e-6,
                $"sample {i} (x={(i / spp) % Width}, y={(i / spp) / Width}, c={i % spp})");
        }
    }

    /// <summary>
    /// TIFF 6.0 section 9's own worked example, plus the codes the example does not use: literal runs,
    /// replicate runs, and 128, which must be skipped without consuming a byte.
    /// </summary>
    [Fact]
    public void PackBits_HandBuiltVector_DecodesLiteralReplicateAndNoOp()
    {
        byte[] encoded =
        [
            0xFE, 0xAA,                                   // -2: repeat AA three times
            0x02, 0x80, 0x00, 0x2A,                       //  2: literal 80 00 2A
            0x80,                                         // -128: no-op, consumes nothing else
            0xFD, 0x22,                                   // -3: repeat 22 four times
            0x00, 0x7F,                                   //  0: literal of one byte
            0xFF, 0x11,                                   // -1: repeat 11 twice
        ];
        byte[] expected = [0xAA, 0xAA, 0xAA, 0x80, 0x00, 0x2A, 0x22, 0x22, 0x22, 0x22, 0x7F, 0x11, 0x11];

        var output = new byte[expected.Length];
        TiffPackBitsForTest(encoded, output).ShouldBe(expected.Length);
        output.ShouldBe(expected);
    }

    /// <summary>A run that would pass the end of the output, or a truncated input, stops where the buffer ends.</summary>
    [Fact]
    public void PackBits_RunsPastTheOutputOrInput_StopInsteadOfOverrunning()
    {
        var guard = new byte[8];
        guard.AsSpan().Fill(0xEE);

        // Replicate of 100 bytes into room for 5.
        TiffPackBitsForTest([0x9D, 0x33], guard.AsSpan(0, 5)).ShouldBe(5);
        guard.AsSpan(0, 5).ToArray().ShouldBe([0x33, 0x33, 0x33, 0x33, 0x33]);
        guard.AsSpan(5).ToArray().ShouldBe([0xEE, 0xEE, 0xEE], "nothing is written past the span");

        // Literal of 8 with only 3 bytes present, and a replicate header with no byte after it.
        var shortOut = new byte[16];
        TiffPackBitsForTest([0x07, 1, 2, 3], shortOut).ShouldBe(3);
        TiffPackBitsForTest([0xFE], shortOut).ShouldBe(0);
    }

    // ---- helpers -----------------------------------------------------------

    // The decoder is internal; the test assembly sees it through InternalsVisibleTo.
    private static int TiffPackBitsForTest(ReadOnlySpan<byte> src, Span<byte> dst) => TiffPackBits.Decode(src, dst);

    private struct CollectingSink : ITiffStripSink
    {
        public CollectingSink() { }

        public List<byte> Bytes { get; } = [];

        public bool BeginPage(int pageIndex, TiffPage description) => true;

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples) =>
            Bytes.AddRange(samples.ToArray());
    }

    private sealed record SourceRaster(byte[] Bytes, float[] Floats, ushort[] Shorts, uint[] Ints);

    private static double Fraction(byte[] pixels, int index, Kind kind) => kind switch
    {
        Kind.U8 => pixels[index] / 255.0,
        Kind.U16 => BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(index * 2, 2)) / 65535.0,
        Kind.U32 => BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(index * 4, 4)) / 4294967295.0,
        _ => BinaryPrimitives.ReadSingleLittleEndian(pixels.AsSpan(index * 4, 4)),
    };

    /// <summary>
    /// A raster whose sample depends on x, y and channel, so a transposed tile, a plane read as another
    /// plane or a mis-strided row each put a value that belongs to no other position.
    /// </summary>
    private static SourceRaster Source(int spp, Kind kind)
    {
        var count = Width * Height * spp;
        var bytes = new byte[count * (kind == Kind.U8 ? 1 : kind == Kind.U16 ? 2 : 4)];
        var floats = new float[count];
        var shorts = new ushort[count];
        var ints = new uint[count];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        for (var c = 0; c < spp; c++)
        {
            var i = (((y * Width) + x) * spp) + c;
            var v = (ushort)(((y * 313) + (x * 7) + (c * 24593)) & 0xFFFF);
            shorts[i] = v;
            switch (kind)
            {
                case Kind.U8:
                    bytes[i] = (byte)(v >> 8);
                    break;
                case Kind.U16:
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), v);
                    break;
                case Kind.U32:
                    ints[i] = v * 65537u;
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), ints[i]);
                    break;
                default:
                    floats[i] = v / 65535f;
                    break;
            }
        }
        return new SourceRaster(bytes, floats, shorts, ints);
    }

    private static byte[] WriteWithLibTiff(SourceRaster source, int spp, Kind kind, bool tiled, bool planar,
        CompressionMethod compression, int predictor)
    {
        var mapping = spp == 1 ? "I" : "RGB";
        var width = (uint)Width;
        var height = (uint)Height;
        using var image = new MagickImage();
        switch (kind)
        {
            case Kind.U8:
                image.ReadPixels(source.Bytes, new PixelReadSettings(width, height, StorageType.Char, mapping));
                break;
            case Kind.U16:
                image.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(source.Shorts).ToArray(), new PixelReadSettings(width, height, StorageType.Short, mapping));
                break;
            case Kind.U32:
                image.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(source.Ints).ToArray(), new PixelReadSettings(width, height, StorageType.Int32, mapping));
                break;
            default:
                image.ReadPixels(System.Runtime.InteropServices.MemoryMarshal.AsBytes<float>(source.Floats).ToArray(), new PixelReadSettings(width, height, StorageType.Float, mapping));
                break;
        }

        if (spp == 1) image.ColorSpace = ColorSpace.Gray;
        image.Depth = kind switch { Kind.U8 => 8u, Kind.U16 => 16u, _ => 32u };
        image.Settings.Interlace = planar ? Interlace.Plane : Interlace.NoInterlace;
        image.Settings.Compression = compression;
        image.Settings.SetDefine(MagickFormat.Tiff, "predictor", predictor.ToString());
        if (tiled)
            image.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", "32x16");
        else
            image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "7");
        if (kind == Kind.F32)
            image.Settings.SetDefine("quantum:format", "floating-point");

        return image.ToByteArray(MagickFormat.Tiff);
    }

    /// <summary>
    /// Tag id -> first value of the first IFD, hand-rolled so a premise check does not lean on the code
    /// under test.
    /// </summary>
    private static Dictionary<ushort, uint> FirstIfdTags(byte[] tiff)
    {
        BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(0, 2)).ShouldBe((ushort)0x4949, "little-endian TIFF expected");

        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4, 4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd, 2));

        var tags = new Dictionary<ushort, uint>(count);
        for (var i = 0; i < count; i++)
        {
            var at = ifd + 2 + (i * 12);
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at, 2));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at + 2, 2));
            var n = BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at + 4, 4));
            var valueField = tiff.AsSpan(at + 8, 4);

            if (type != 3 && type != 4) continue;

            var inline = type switch
            {
                3 => n * 2u <= 4u,
                4 => n * 4u <= 4u,
                _ => false,
            };
            var span = inline
                ? valueField
                : tiff.AsSpan((int)BinaryPrimitives.ReadUInt32LittleEndian(valueField), 4);

            tags[tag] = type switch
            {
                3 => BinaryPrimitives.ReadUInt16LittleEndian(span[..2]),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(span[..4]),
                _ => 0u,
            };
        }
        return tags;
    }
}
