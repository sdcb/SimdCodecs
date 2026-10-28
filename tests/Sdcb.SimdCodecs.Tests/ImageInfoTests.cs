using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jbig2;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// What <see cref="IImageDecoder.TryReadInfo"/> reports beyond the raster's layout — bit depth, EXIF
/// orientation, frame count. Orientation comes from
/// files ImageMagick wrote (and reads back) or from hand-assembled EXIF; frame counts from multi-page
/// files ImageMagick wrote and hand-built APNG / JBIG2 streams. That every corpus entry's info matches the
/// image it decodes to is <see cref="DecodeIntoTests.InfoDescribesTheDecodedImage"/>.
/// </summary>
public sealed class ImageInfoTests
{
    public static TheoryData<MagickFormat, ImageOrientation> OrientedFiles()
    {
        var data = new TheoryData<MagickFormat, ImageOrientation>();
        foreach (var format in new[] { MagickFormat.Jpeg, MagickFormat.Png, MagickFormat.WebP, MagickFormat.Tiff })
        foreach (var orientation in Enum.GetValues<ImageOrientation>())
            data.Add(format, orientation);
        return data;
    }

    [Theory]
    [MemberData(nameof(OrientedFiles))]
    public void OrientationIsReportedAndNotApplied(MagickFormat format, ImageOrientation orientation)
    {
        var file = MagickOriented(format, orientation, width: 12, height: 5);
        // The file carries the tag: ImageMagick reads it back.
        using (var check = new MagickImage(file))
            ((int)check.Orientation).ShouldBe((int)orientation);

        SimdCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        info.Orientation.ShouldBe(orientation);
        info.SwapsDimensions.ShouldBe(orientation >= ImageOrientation.Transpose);
        // Stored dimensions, stored pixels: nothing is turned.
        (info.Width, info.Height).ShouldBe((12, 5));
        SimdCodecs.TryDecode(file, out var image).ShouldBeTrue();
        (image.Width, image.Height).ShouldBe((12, 5));
    }

    private static byte[] MagickOriented(MagickFormat format, ImageOrientation orientation, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        new Random(width).NextBytes(pixels);
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGB"));
        image.Orientation = (OrientationType)orientation;
        if (format != MagickFormat.Tiff)
        {
            // JPEG, PNG and WebP keep orientation in an EXIF profile (APP1, eXIf, EXIF).
            var exif = new ExifProfile();
            exif.SetValue(ImageMagick.ExifTag.Orientation, (ushort)orientation);
            image.SetProfile(exif);
        }
        return image.ToByteArray(format);
    }

    // A minimal EXIF payload: the TIFF header and an IFD0 holding the given 12-byte entries.
    private static byte[] Exif(bool littleEndian, params (ushort Tag, ushort Type, uint Count, uint Value)[] entries)
    {
        var b = new byte[8 + 2 + entries.Length * 12 + 4];
        void U16(int at, ushort v) { if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), v); }
        void U32(int at, uint v) { if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v); }
        b[0] = b[1] = (byte)(littleEndian ? 'I' : 'M');
        U16(2, 42);
        U32(4, 8);
        U16(8, (ushort)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            var at = 10 + i * 12;
            U16(at, entries[i].Tag);
            U16(at + 2, entries[i].Type);
            U32(at + 4, entries[i].Count);
            // A SHORT sits in the first two bytes of the value field, in the file's byte order.
            if (entries[i].Type == 3) U16(at + 8, (ushort)entries[i].Value);
            else U32(at + 8, entries[i].Value);
        }
        return b;
    }

    // A greyscale JPEG with the given APPn segments inserted after SOI.
    private static byte[] JpegWith(params (byte Marker, byte[] Payload)[] segments)
    {
        var jpeg = JpegDecoderOracleTests.EncodeJpeg(JpegDecoderOracleTests.MakeRgbPattern(9, 4), 9, 4, gray: true);
        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        foreach (var (marker, payload) in segments)
        {
            ms.WriteByte(0xFF);
            ms.WriteByte(marker);
            ms.WriteByte((byte)((payload.Length + 2) >> 8));
            ms.WriteByte((byte)(payload.Length + 2));
            ms.Write(payload);
        }
        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
    }

    private static byte[] App1Exif(byte[] tiff) => [.. "Exif\0\0"u8, .. tiff];

    private static ImageOrientation JpegOrientation(byte[] jpeg)
    {
        JpegImageDecoder.TryReadInfo(jpeg, out var info).ShouldBeTrue();
        JpegDecoder.ReadInfo(jpeg).Orientation.ShouldBe(info.Orientation);
        return info.Orientation;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HandAssembledExifIsReadInEitherByteOrder(bool littleEndian)
    {
        foreach (var orientation in Enum.GetValues<ImageOrientation>())
        {
            // The tag after others, as cameras write it (Make, Model, Orientation).
            var tiff = Exif(littleEndian, (0x010F, 2, 4, 0), (0x0110, 2, 4, 0), (0x0112, 3, 1, (uint)orientation));
            JpegOrientation(JpegWith((0xE1, App1Exif(tiff)))).ShouldBe(orientation);
        }
    }

    [Fact]
    public void MalformedOrientationReadsAsNormal()
    {
        (ushort, ushort, uint, uint) Orientation(ushort type, uint count, uint value) => (0x0112, type, count, value);
        byte[][] bad =
        [
            Exif(true, Orientation(3, 1, 0)),     // 0 is not an orientation
            Exif(true, Orientation(3, 1, 9)),     // nor is 9
            Exif(true, Orientation(4, 1, 6)),     // LONG, not SHORT
            Exif(true, Orientation(3, 2, 6)),     // two values
            Exif(true, (0x0110, 2, 4, 0)),        // no Orientation tag
            Exif(true, Orientation(3, 1, 6))[..14], // IFD cut off inside the entry
            [.. "II"u8, 42, 0, 0xFF, 0xFF, 0, 0], // IFD0 offset past the end
            [.. "XX"u8, 42, 0, 8, 0, 0, 0, 0, 0], // not a TIFF header
            [],
        ];
        foreach (var tiff in bad)
            JpegOrientation(JpegWith((0xE1, App1Exif(tiff)))).ShouldBe(ImageOrientation.Normal);

        // No EXIF at all, and an APP1 that is XMP rather than EXIF.
        JpegOrientation(JpegWith()).ShouldBe(ImageOrientation.Normal);
        JpegOrientation(JpegWith((0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Exif(true, Orientation(3, 1, 6))])))
            .ShouldBe(ImageOrientation.Normal);
    }

    [Fact]
    public void TheFirstExifSegmentCounts()
    {
        var six = App1Exif(Exif(true, (0x0112, 3, 1, 6)));
        var three = App1Exif(Exif(false, (0x0112, 3, 1, 3)));
        JpegOrientation(JpegWith((0xE1, six), (0xE1, three))).ShouldBe(ImageOrientation.Rotate90);
        JpegOrientation(JpegWith((0xE1, three), (0xE1, six))).ShouldBe(ImageOrientation.Rotate180);
    }

    [Fact]
    public void PngExifMayCarryTheJpegIdentifier()
    {
        // Some writers copy APP1's payload verbatim, "Exif\0\0" included, into eXIf.
        var tiff = App1Exif(Exif(false, (0x0112, 3, 1, 8)));
        var grey = new byte[4 * 3];
        var png = PngWriter.EncodeGray8(grey, 4, 3, new PngWriteOptions { Exif = tiff });
        SimdCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
        info.Orientation.ShouldBe(ImageOrientation.Rotate270);
    }

    [Fact]
    public void PngExifAfterTheImageDataCounts()
    {
        // The 2017 eXIf extension lets it follow IDAT; move it there.
        var png = PngWriter.EncodeGray8(new byte[4 * 3], 4, 3, new PngWriteOptions { Exif = Exif(true, (0x0112, 3, 1, 5)) });
        var chunks = PngChunks(png);
        var exif = chunks.Single(c => c.Type == "eXIf");
        var reordered = new MemoryStream();
        reordered.Write(png, 0, 8);
        foreach (var chunk in chunks.Where(c => c.Type != "eXIf"))
        {
            if (chunk.Type == "IEND") reordered.Write(png, exif.Offset, exif.Length);
            reordered.Write(png, chunk.Offset, chunk.Length);
        }
        var after = reordered.ToArray();
        PngChunks(after).Select(c => c.Type).ShouldBe(["IHDR", "IDAT", "eXIf", "IEND"]);

        SimdCodecs.TryReadInfo(after, out var info).ShouldBeTrue();
        info.Orientation.ShouldBe(ImageOrientation.Transpose);
    }

    private static List<(string Type, int Offset, int Length)> PngChunks(byte[] png)
    {
        var chunks = new List<(string, int, int)>();
        for (var pos = 8; pos < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            chunks.Add((System.Text.Encoding.ASCII.GetString(png, pos + 4, 4), pos, 12 + length));
            pos += 12 + length;
        }
        return chunks;
    }

    [Fact]
    public void ApngReportsItsAnimationFrames()
    {
        var png = PngWriter.EncodeGray8(new byte[4 * 3], 4, 3);
        // acTL (num_frames 3, num_plays 0) between IHDR and IDAT, where the APNG spec puts it.
        var acTL = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(acTL, 3);
        var apng = new MemoryStream();
        apng.Write(png, 0, 33);
        PngRgba8ExpansionTests.Chunk(apng, "acTL"u8, acTL);
        apng.Write(png, 33, png.Length - 33);
        var bytes = apng.ToArray();

        SimdCodecs.TryReadInfo(bytes, out var info).ShouldBeTrue();
        info.FrameCount.ShouldBe(3);
        SimdCodecs.TryDecode(bytes, out _).ShouldBeTrue();

        // An acTL after the image data is not an APNG's.
        var late = new MemoryStream();
        late.Write(png, 0, png.Length - 12);
        PngRgba8ExpansionTests.Chunk(late, "acTL"u8, acTL);
        late.Write(png, png.Length - 12, 12);
        SimdCodecs.TryReadInfo(late.ToArray(), out info).ShouldBeTrue();
        info.FrameCount.ShouldBe(1);
    }

    [Fact]
    public void MultiPageTiffReportsItsPages()
    {
        using var pages = new MagickImageCollection();
        for (var i = 0; i < 3; i++)
            pages.Add(new MagickImage(MagickColors.Gray, 7, (uint)(4 + i)));
        var tiff = pages.ToByteArray(MagickFormat.Tiff);

        SimdCodecs.TryReadInfo(tiff, out var info).ShouldBeTrue();
        info.FrameCount.ShouldBe(3);
        (info.Width, info.Height).ShouldBe((7, 4)); // the first page, which TryDecode returns
    }

    [Fact]
    public void ALoopingIfdChainIsCountedOnce()
    {
        using var image = new MagickImage(MagickColors.Gray, 5, 3);
        var tiff = image.ToByteArray(MagickFormat.Tiff);
        tiff[0].ShouldBe((byte)'I');
        // Point IFD0's next-IFD field back at IFD0.
        var ifd0 = BinaryPrimitives.ReadInt32LittleEndian(tiff.AsSpan(4));
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd0));
        BinaryPrimitives.WriteInt32LittleEndian(tiff.AsSpan(ifd0 + 2 + entries * 12), ifd0);

        SimdCodecs.TryReadInfo(tiff, out var info).ShouldBeTrue();
        info.FrameCount.ShouldBe(1);
    }

    [Fact]
    public void TiffBitDepthIsAsStored()
    {
        using var bilevel = new MagickImage(MagickColors.White, 9, 2);
        bilevel.ColorType = ColorType.Bilevel;
        bilevel.Depth = 1;
        SimdCodecs.TryReadInfo(bilevel.ToByteArray(MagickFormat.Tiff), out var info).ShouldBeTrue();
        (info.SampleFormat, info.BitDepth).ShouldBe((SampleFormat.UInt8, 1));

        SimdCodecs.TryReadInfo(HdrTiff.HalfGray(3, 2, new Half[6]), out info).ShouldBeTrue();
        (info.SampleFormat, info.BitDepth).ShouldBe((SampleFormat.Float32, 16));
        SimdCodecs.TryReadInfo(HdrTiff.Float32Gray(3, 2, new float[6]), out info).ShouldBeTrue();
        (info.SampleFormat, info.BitDepth).ShouldBe((SampleFormat.Float32, 32));
    }

    [Fact]
    public void Jbig2ReportsItsPages()
    {
        var page = Jbig2StreamBuilder.FromRows(["#..#", ".##."]);
        var file = Jbig2StreamBuilder.SequentialFile(
            Jbig2StreamBuilder.Segment(0, SegmentType.PageInformation, 1, Jbig2StreamBuilder.PageInformation(page.Width, page.Height)),
            Jbig2StreamBuilder.Segment(1, SegmentType.ImmediateGenericRegion, 1, Jbig2StreamBuilder.GenericRegionSegment(page)),
            Jbig2StreamBuilder.Segment(2, SegmentType.EndOfPage, 1, []),
            Jbig2StreamBuilder.Segment(3, SegmentType.PageInformation, 2, Jbig2StreamBuilder.PageInformation(page.Width, page.Height)),
            Jbig2StreamBuilder.Segment(4, SegmentType.ImmediateGenericRegion, 2, Jbig2StreamBuilder.GenericRegionSegment(page)),
            Jbig2StreamBuilder.Segment(5, SegmentType.EndOfPage, 2, []),
            Jbig2StreamBuilder.Segment(6, SegmentType.EndOfFile, 0, []));

        SimdCodecs.TryReadInfo(file, out var info).ShouldBeTrue();
        (info.FrameCount, info.BitDepth).ShouldBe((2, 1));
        SimdCodecs.TryDecode(file, out var image).ShouldBeTrue();
        (image.Width, image.Height).ShouldBe((page.Width, page.Height));
    }

    [Fact]
    public void UnsetPropertiesReadAsThePlainCase()
    {
        default(ImageInfo).Orientation.ShouldBe(ImageOrientation.Normal);
        default(ImageInfo).FrameCount.ShouldBe(1);
        new ImageInfo(1, 1, 1, SampleFormat.UInt8).BitDepth.ShouldBe(8);
        new ImageInfo(1, 1, 1, SampleFormat.UInt16).BitDepth.ShouldBe(16);
        new ImageInfo(1, 1, 1, SampleFormat.Float32).BitDepth.ShouldBe(32);

        // An explicit default is the same value as an unset one.
        var plain = new ImageInfo(3, 2, 4, SampleFormat.UInt8);
        var spelled = plain with { BitDepth = 8, Orientation = ImageOrientation.Normal, FrameCount = 1 };
        spelled.ShouldBe(plain);
        spelled.GetHashCode().ShouldBe(plain.GetHashCode());
        (plain with { BitDepth = 4 }).ShouldNotBe(plain);
        (plain with { Orientation = ImageOrientation.Rotate90 }).ShouldNotBe(plain);
        (plain with { FrameCount = 2 }).ShouldNotBe(plain);
    }

    [Fact]
    public void OutOfRangePropertiesAreRefused()
    {
        var info = new ImageInfo(1, 1, 1, SampleFormat.UInt8);
        Should.Throw<ArgumentOutOfRangeException>(() => info with { BitDepth = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => info with { BitDepth = 33 });
        Should.Throw<ArgumentOutOfRangeException>(() => info with { Orientation = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => info with { Orientation = (ImageOrientation)9 });
        Should.Throw<ArgumentOutOfRangeException>(() => info with { FrameCount = 0 });
    }
}
