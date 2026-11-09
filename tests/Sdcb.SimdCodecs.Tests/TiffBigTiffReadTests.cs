using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Exif;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using B = Sdcb.SimdCodecs.Tests.TestTiffBuilder;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// BigTIFF (version 43: 8-byte offsets, 8-byte entry counts, 20-byte entries, LONG8 / IFD8 types) is read like classic
/// TIFF. The oracle is libtiff's own BigTIFF writer (Magick.NET's <c>TIFF64</c>), checked byte for byte against the
/// lossless source raster, across compressions, strips and tiles, planar layouts and pages; and hand-assembled files
/// for the parts libtiff does not choose for us (big-endian, LONG8 strip arrays, an IFD8 Exif pointer).
/// </summary>
public sealed class TiffBigTiffReadTests
{
    private const int W = 45;
    private const int H = 31;

    public static TheoryData<CompressionMethod, bool, bool, int> Cases()
    {
        var data = new TheoryData<CompressionMethod, bool, bool, int>();
        foreach (var compression in new[] { CompressionMethod.NoCompression, CompressionMethod.LZW, CompressionMethod.Zip, CompressionMethod.RLE })
        foreach (var tiled in new[] { false, true })
        foreach (var planar in new[] { false, true })
            data.Add(compression, tiled, planar, compression == CompressionMethod.NoCompression ? 1 : 2);
        return data;
    }

    private static byte[] Source(int seed)
    {
        var rgb = new byte[W * H * 3];
        new Random(seed).NextBytes(rgb);
        return rgb;
    }

    private static MagickImage Image(byte[] rgb, CompressionMethod compression, bool tiled, bool planar, int predictor)
    {
        var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings(W, H, StorageType.Char, "RGB"));
        image.Depth = 8;
        image.Settings.Compression = compression;
        image.Settings.Interlace = planar ? Interlace.Plane : Interlace.NoInterlace;
        image.Settings.SetDefine(MagickFormat.Tiff64, "predictor", predictor.ToString());
        if (tiled) image.Settings.SetDefine(MagickFormat.Tiff64, "tile-geometry", "16x16");
        else image.Settings.SetDefine(MagickFormat.Tiff64, "rows-per-strip", "7");
        return image;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LibTiffBigTiff_DecodesToTheSourcePixels(CompressionMethod compression, bool tiled, bool planar, int predictor)
    {
        var rgb = Source(1);
        using var image = Image(rgb, compression, tiled, planar, predictor);
        var big = image.ToByteArray(MagickFormat.Tiff64);

        // Premises: libtiff wrote a BigTIFF with the layout the case names.
        BinaryPrimitives.ReadUInt16LittleEndian(big.AsSpan(2)).ShouldBe((ushort)43);
        var layout = TiffReader.ReadLayout(big);
        layout.IsBigTiff.ShouldBeTrue();
        layout.IsTiled.ShouldBe(tiled);
        layout.IsPlanar.ShouldBe(planar);

        var page = TiffReader.Read(big).Pages.ShouldHaveSingleItem();
        page.IsBigTiff.ShouldBeTrue();
        page.Pixels.ShouldBe(rgb);

        // Every segment on demand, and the facade, agree with the whole-page read.
        for (var s = 0; s < layout.SegmentCount; s++)
        {
            var rect = layout.SegmentRectangle(s);
            var buffer = new byte[layout.SegmentBytes(s)];
            TiffReader.ReadSegment(big, layout, s, buffer);
            for (var y = 0; y < rect.Height; y++)
                buffer.AsSpan(y * rect.Width * 3, rect.Width * 3).ToArray()
                    .ShouldBe(rgb.AsSpan((((rect.Y + y) * W) + rect.X) * 3, rect.Width * 3).ToArray());
        }
        TiffImageDecoder.TryDecode(big, out var decoded, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        decoded!.Pixels.ToArray().ShouldBe(rgb);
    }

    [Fact]
    public void MultiPageBigTiff_ReadsEveryPage()
    {
        using var collection = new MagickImageCollection();
        var sources = new[] { Source(2), Source(3), Source(4) };
        collection.Add(Image(sources[0], CompressionMethod.LZW, tiled: false, planar: false, 2));
        collection.Add(Image(sources[1], CompressionMethod.Zip, tiled: true, planar: false, 2));
        collection.Add(Image(sources[2], CompressionMethod.NoCompression, tiled: false, planar: true, 1));
        using var ms = new MemoryStream();
        collection.Write(ms, MagickFormat.Tiff64);
        var big = ms.ToArray();

        var pages = TiffReader.Read(big).Pages;
        pages.Count.ShouldBe(3);
        for (var p = 0; p < 3; p++)
        {
            pages[p].Pixels.ShouldBe(sources[p]);
            TiffReader.ReadPage(big, p, TestContext.Current.CancellationToken).Pixels.ShouldBe(sources[p]);
        }
        TiffImageDecoder.TryReadInfo(big, out var info).ShouldBeTrue();
        info.FrameCount.ShouldBe(3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HandBuiltBigTiff_Long8StripArrays_EitherByteOrder(bool littleEndian)
    {
        var gray16 = new ushort[W * H];
        for (var i = 0; i < gray16.Length; i++) gray16[i] = (ushort)(i * 977);
        var stored = new byte[gray16.Length * 2];
        for (var i = 0; i < gray16.Length; i++)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(i * 2), gray16[i]);
            else BinaryPrimitives.WriteUInt16BigEndian(stored.AsSpan(i * 2), gray16[i]);
        }
        var tags = B.Basic(W, H, 1, 16, TiffCompression.Uncompressed, TiffPhotometric.MinIsBlack, rowsPerStrip: 4);
        var big = new B(littleEndian, bigTiff: true)
            .AddPage(tags, TiffRobustnessTests.Strips(stored, W * 2, H, 4))
            .Build();

        var page = TiffReader.Read(big).Pages.ShouldHaveSingleItem();
        page.IsBigTiff.ShouldBeTrue();
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(page.Pixels).ToArray().ShouldBe(gray16);
    }

    [Fact]
    public void BigTiffExifSubIfd_IsReassembledIntoTheMetadataBlock()
    {
        // An Exif IFD reached by an IFD8 pointer, holding one inline SHORT and one out-of-line ASCII value.
        var rgb = Source(5);
        var tags = B.Basic(W, H, 3, 8, TiffCompression.Uncompressed, TiffPhotometric.Rgb);
        var builder = new B(littleEndian: true, bigTiff: true);
        var plain = builder.AddPage(tags, [rgb]).Build();

        // Append an Exif IFD to the end of the file and point tag 34665 at it, by rebuilding with the offset known.
        var exifAt = (plain.Length + 1) & ~1L;
        tags.Add(new B.Entry(TiffTag.ExifIfd, TiffFieldType.Ifd8, [(ulong)(exifAt + 20 + 8)]));
        var withPointer = new B(littleEndian: true, bigTiff: true).AddPage(tags, [rgb]).Build();
        // The pointer entry grew the directory by 20 bytes; place the Exif IFD where the pointer says.
        var exifIfdAt = (int)(exifAt + 20 + 8);
        withPointer.Length.ShouldBeLessThanOrEqualTo(exifIfdAt, "fixture: the Exif IFD must follow the page");
        var file = new byte[exifIfdAt + 8 + (2 * 20) + 8 + 16];
        withPointer.CopyTo(file, 0);
        var at = exifIfdAt;
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at), 2); at += 8;
        // ISOSpeedRatings (34855) SHORT = 400, inline.
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), 34855);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 2), (ushort)TiffFieldType.Short);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at + 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 12), 400);
        at += 20;
        // LensModel (42036) ASCII, 16 bytes out of line (more than the 8-byte slot).
        var lens = "BigTIFF lens 01\0"u8.ToArray();
        var lensAt = exifIfdAt + 8 + (2 * 20) + 8;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), 42036);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 2), (ushort)TiffFieldType.Ascii);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at + 4), (ulong)lens.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(at + 12), (ulong)lensAt);
        lens.CopyTo(file, lensAt);

        TiffReader.ReadPage(file, 0, TestContext.Current.CancellationToken).Pixels.ShouldBe(rgb);
        TiffImageDecoder.TryReadMetadata(file, out var metadata).ShouldBeTrue();
        var exif = ExifReader.FromTiff(metadata!.Exif.Span).ShouldNotBeNull();
        exif.Iso.ShouldBe(400);
        exif.RawTags.ShouldContainKey((ushort)42036);
    }

    [Fact]
    public void TheFacadeSniffsBigTiff()
    {
        using var image = Image(Source(6), CompressionMethod.Zip, tiled: false, planar: false, 2);
        var big = image.ToByteArray(MagickFormat.Tiff64);
        TiffImageDecoder.CanDecode(big).ShouldBeTrue();
        TiffImageDecoder.CanDecode(new B(littleEndian: false, bigTiff: true)
            .AddPage(B.Basic(1, 1, 1, 8, TiffCompression.Uncompressed, TiffPhotometric.MinIsBlack), [[7]]).Build()).ShouldBeTrue();
        ImageCodecs.DetectFormat(big).ShouldBe(ImageFormat.Tiff);
        ImageCodecs.TryDecode(big, out var decoded, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        decoded!.Width.ShouldBe(W);
    }

    [Theory]
    [InlineData(4, 8)]    // offset size 4 is not BigTIFF
    [InlineData(8, 1)]    // the reserved field must be 0
    public void AMalformedBigTiffHeader_IsRefused(int offsetSize, int reserved)
    {
        var big = new B(bigTiff: true).AddPage(B.Basic(1, 1, 1, 8, TiffCompression.Uncompressed, TiffPhotometric.MinIsBlack), [[7]]).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(big.AsSpan(4), (ushort)offsetSize);
        BinaryPrimitives.WriteUInt16LittleEndian(big.AsSpan(6), (ushort)(reserved == 1 ? 1 : 0));
        if (offsetSize == 8 && reserved != 1) return;
        Should.Throw<InvalidDataException>(() => TiffReader.Read(big));
        TiffImageDecoder.TryDecode(big, out _, cancellationToken: TestContext.Current.CancellationToken).ShouldBeFalse();
    }
}
