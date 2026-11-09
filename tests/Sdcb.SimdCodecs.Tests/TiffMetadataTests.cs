using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Exif;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using Xunit;
using MagickExifTag = ImageMagick.ExifTag;
using Rational = ImageMagick.Rational;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Exif, XMP and ICC through the TIFF writer and reader: a frame's <see cref="ImageMetadata"/> lands in the page
/// (Exif merged into the page IFD, its Exif / GPS / Interoperability IFDs relocated), libtiff (Magick.NET) reads
/// it back, and <see cref="TiffImageDecoder.TryReadMetadata"/> reassembles the same blocks.
/// </summary>
public sealed class TiffMetadataTests
{
    private const int W = 40, H = 30;

    private static readonly byte[] Xmp = Encoding.UTF8.GetBytes(
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">"
        + "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description rdf:about=\"\" "
        + "xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmp:CreatorTool=\"TiffMetadataTests 星雲\"/></rdf:RDF></x:xmpmeta>"
        + "<?xpacket end=\"w\"?>");

    /// <summary>A JPEG written by libjpeg (Magick.NET) with an Exif profile in IFD0, the Exif IFD and the GPS IFD, plus XMP and ICC.</summary>
    private static byte[] MagickJpeg()
    {
        using var image = new MagickImage(MagickColors.Coral, W, H) { Format = MagickFormat.Jpeg };
        var exif = new ExifProfile();
        exif.SetValue(MagickExifTag.Make, "TestCam");
        exif.SetValue(MagickExifTag.Model, "Model 7");
        exif.SetValue(MagickExifTag.Copyright, "CC0");
        exif.SetValue(MagickExifTag.DateTimeOriginal, "2026:11:04 10:20:30");
        exif.SetValue(MagickExifTag.FNumber, new Rational(28, 10));
        exif.SetValue(MagickExifTag.ExposureTime, new Rational(1, 250));
        exif.SetValue(MagickExifTag.ISOSpeedRatings, [(ushort)400]);
        exif.SetValue(MagickExifTag.GPSLatitudeRef, "N");
        exif.SetValue(MagickExifTag.GPSLatitude, [new Rational(31, 1), new Rational(14, 1), new Rational(3000, 100)]);
        exif.SetValue(MagickExifTag.GPSLongitudeRef, "E");
        exif.SetValue(MagickExifTag.GPSLongitude, [new Rational(121, 1), new Rational(28, 1), new Rational(1500, 100)]);
        image.SetProfile(exif);
        image.SetProfile(new XmpProfile(Xmp));
        image.SetProfile(ColorProfiles.SRGB);
        return image.ToByteArray();
    }

    private static byte[] Encode(IDecodedImage image, EncodeOptions? options = null)
    {
        using var stream = new MemoryStream();
        ImageCodecs.Encode(image, ImageFormat.Tiff, stream, options);
        return stream.ToArray();
    }

    private static byte[] Pixels(int seed = 1)
    {
        var pixels = new byte[W * H * 3];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    private static byte[] WritePage(TiffPageOptions options, int seed = 1)
    {
        using var stream = new MemoryStream();
        var writer = TiffWriter.Create(stream);
        try
        {
            writer.AddPageAsync(Pixels(seed), W, H, options, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return stream.ToArray();
    }

    // ---------------------------------------------------------------- an external reader

    /// <summary>
    /// Foreign bytes in and out: the Exif, XMP and ICC of a JPEG libjpeg wrote, through our decoder and TIFF encoder,
    /// read by libtiff (Magick.NET 14.13.1). libtiff reports IFD0's Make / Model / Copyright as <c>tiff:*</c>
    /// attributes and the Exif and GPS IFDs as <c>exif:*</c>; each must equal what it reads from the source JPEG.
    /// </summary>
    [Fact]
    public void Libtiff_reads_the_exif_xmp_and_icc_of_the_source()
    {
        var jpeg = MagickJpeg();
        ImageCodecs.TryDecode(jpeg, out var decoded, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        byte[] tiff;
        using (decoded)
        {
            decoded.Metadata.Exif.IsEmpty.ShouldBeFalse();
            tiff = Encode(decoded);
        }

        using var source = new MagickImage(jpeg);
        using var written = new MagickImage(tiff);
        written.Format.ShouldBe(MagickFormat.Tiff);

        var sourceExif = source.GetExifProfile().ShouldNotBeNull();
        written.GetAttribute("tiff:make").ShouldBe(sourceExif.GetValue(MagickExifTag.Make)!.Value);
        written.GetAttribute("tiff:model").ShouldBe(sourceExif.GetValue(MagickExifTag.Model)!.Value);
        written.GetAttribute("tiff:copyright").ShouldBe(sourceExif.GetValue(MagickExifTag.Copyright)!.Value);

        // (ImageMagick's name for the JPEG profile's attribute, libtiff's for the TIFF's): tag 0x8827 is
        // PhotographicSensitivity in Exif 2.3 and ISOSpeedRatings in libtiff's table.
        (string Jpeg, string Tiff)[] fromSubIfds =
        [
            ("exif:DateTimeOriginal", "exif:DateTimeOriginal"), ("exif:FNumber", "exif:FNumber"),
            ("exif:ExposureTime", "exif:ExposureTime"), ("exif:PhotographicSensitivity", "exif:ISOSpeedRatings"),
            ("exif:GPSLatitudeRef", "exif:GPSLatitudeRef"), ("exif:GPSLatitude", "exif:GPSLatitude"),
            ("exif:GPSLongitudeRef", "exif:GPSLongitudeRef"), ("exif:GPSLongitude", "exif:GPSLongitude"),
        ];
        foreach (var (jpegName, tiffName) in fromSubIfds)
        {
            var expected = source.GetAttribute(jpegName).ShouldNotBeNull(jpegName);
            var actual = written.GetAttribute(tiffName)
                .ShouldNotBeNull($"{tiffName}; libtiff gave {string.Join(", ", written.AttributeNames.Where(a => a.StartsWith("exif:")))}");
            // ImageMagick prints a JPEG profile's rationals as fractions (14/5) and libtiff's as decimals (2.800000).
            if (Numbers(expected) is { } e)
                Numbers(actual).ShouldNotBeNull(tiffName).ShouldBe(e, 1e-6, tiffName);
            else
                actual.ShouldBe(expected, tiffName);
        }

        written.GetXmpProfile().ShouldNotBeNull().ToByteArray().ShouldBe(source.GetXmpProfile()!.ToByteArray());
        written.GetColorProfile().ShouldNotBeNull().ToByteArray().ShouldBe(source.GetColorProfile()!.ToByteArray());
    }

    /// <summary>A comma-separated list of decimals or fractions; null when it is not one.</summary>
    private static double[]? Numbers(string list)
    {
        var parts = list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        for (var i = 0; i < parts.Length; i++)
        {
            var slash = parts[i].Split('/');
            if (slash.Length == 2 && double.TryParse(slash[0], invariant, out var n) && double.TryParse(slash[1], invariant, out var d) && d != 0)
                values[i] = n / d;
            else if (slash.Length != 1 || !double.TryParse(parts[i], invariant, out values[i]))
                return null;
        }
        return parts.Length == 0 ? null : values;
    }

    // ---------------------------------------------------------------- the facade, end to end

    /// <summary>
    /// What a transcode keeps: decode a JPEG, encode it as TIFF through the facade, and read the metadata back. The
    /// Exif block comes back in a new layout (relocated, host byte order, no IFD1), so it is compared tag by tag: every
    /// entry of the source's IFD0 (except those describing image data), Exif IFD and GPS IFD is there, with equal bytes.
    /// </summary>
    [Fact]
    public void Facade_transcode_keeps_exif_xmp_and_icc()
    {
        var jpeg = MagickJpeg();
        ImageCodecs.TryDecode(jpeg, out var decoded, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        ImageMetadata original;
        byte[] tiff;
        using (decoded)
        {
            original = decoded.Metadata;
            tiff = Encode(decoded);
        }

        ImageCodecs.TryReadMetadata(tiff, out var back).ShouldBeTrue();
        back.Exif.IsEmpty.ShouldBeFalse();
        back.Xmp.ToArray().ShouldBe(original.Xmp.ToArray());
        back.Icc.ToArray().ShouldBe(original.Icc.ToArray());
        AssertSameTags(original.Exif.Span, back.Exif.Span);

        var expected = ExifReader.FromTiff(original.Exif.Span).ShouldNotBeNull();
        var actual = ExifReader.FromTiff(back.Exif.Span).ShouldNotBeNull();
        actual.Make.ShouldBe(expected.Make);
        actual.Model.ShouldBe(expected.Model);
        actual.Copyright.ShouldBe(expected.Copyright);
        actual.CaptureTime.ShouldBe(expected.CaptureTime);
        actual.FNumber.ShouldBe(expected.FNumber);
        actual.ExposureTime.ShouldBe(expected.ExposureTime);
        actual.Iso.ShouldBe(expected.Iso);
        actual.Gps.ShouldBe(expected.Gps);
        actual.Gps.ShouldNotBeNull();

        // The decoded image carries the same blocks.
        ImageCodecs.TryDecode(tiff, out var again, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (again)
        {
            again.Metadata.Exif.ToArray().ShouldBe(back.Exif.ToArray());
            again.Metadata.Xmp.ToArray().ShouldBe(back.Xmp.ToArray());
            again.Metadata.Icc.ToArray().ShouldBe(back.Icc.ToArray());
        }

        // TIFF → TIFF is stable: the reassembled block survives another pass unchanged.
        var page = new TiffPageOptions { Exif = back.Exif, Xmp = back.Xmp, IccProfile = back.Icc };
        TiffImageDecoder.TryReadMetadata(WritePage(page), out var twice).ShouldBeTrue();
        twice.Exif.ToArray().ShouldBe(back.Exif.ToArray());
    }

    /// <summary>Every copyable entry of <paramref name="expected"/>'s IFD0, Exif, Interop and GPS IFDs is in <paramref name="actual"/> with the same value.</summary>
    private static void AssertSameTags(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var e = Directories(expected);
        var a = Directories(actual);
        foreach (var (name, source, target) in new[] { ("IFD0", e.Ifd0, a.Ifd0), ("Exif", e.Exif, a.Exif), ("Interop", e.Interop, a.Interop), ("GPS", e.Gps, a.Gps) })
        {
            var byTag = target.ToDictionary(t => t.Tag);
            var count = 0;
            foreach (var entry in source)
            {
                // The page writes its own resolution, replacing the block's (and adds it when the block had none).
                if (name == "IFD0" && IsResolution(entry.Tag))
                    continue;
                byTag.TryGetValue(entry.Tag, out var copied).ShouldBeTrue($"{name} tag {entry.Tag}");
                copied.Type.ShouldBe(entry.Type, $"{name} tag {entry.Tag}");
                copied.Count.ShouldBe(entry.Count, $"{name} tag {entry.Tag}");
                copied.Value.ShouldBe(entry.Value, $"{name} tag {entry.Tag}");
                count++;
            }
            target.Count(t => name != "IFD0" || !IsResolution(t.Tag)).ShouldBe(count, name);
        }
        a.HasExifIfd.ShouldBe(e.HasExifIfd);
        a.HasGpsIfd.ShouldBe(e.HasGpsIfd);
    }

    private static bool IsResolution(ushort tag) => tag is 282 or 283 or 296;

    private static TiffExifDirectories Directories(ReadOnlySpan<byte> block) => TiffExif.ParseBlock(block).ShouldNotBeNull();

    // ---------------------------------------------------------------- byte order, skipped entries

    /// <summary>A big-endian block: every value is swapped by its type (SHORT, LONG, RATIONAL, DOUBLE), BYTE / ASCII / UNDEFINED stay.</summary>
    [Fact]
    public void Big_endian_block_is_converted_value_by_value()
    {
        var block = new BlockBuilder(littleEndian: false);
        block.Ifd0.Add(block.Ascii(0x010F, "BigEndianCam"));
        block.Ifd0.Add(block.Short(0x0112, 6));
        block.Exif.Add(block.Rational(0x829D, 28, 10));
        block.Exif.Add(block.Short(0x8827, 800));
        block.Exif.Add(block.Undefined(0x9000, "0232"u8.ToArray()));
        block.Exif.Add(block.Double(0xFFF0, 1.5));
        block.Interop.Add(block.Ascii(0x0001, "R98"));
        block.Gps.Add(block.Ascii(0x0001, "S"));
        block.Gps.Add(block.Rationals(0x0002, (33, 1), (51, 1), (5400, 100)));
        var bytes = block.Build();

        TiffImageDecoder.TryReadMetadata(WritePage(new TiffPageOptions { Exif = bytes }), out var back).ShouldBeTrue();
        AssertSameTags(bytes, back.Exif.Span);
        var exif = ExifReader.FromTiff(back.Exif.Span).ShouldNotBeNull();
        exif.Make.ShouldBe("BigEndianCam");
        exif.Orientation.ShouldBe((ushort)6);
        exif.FNumber.ShouldBe(new Sdcb.SimdCodecs.Exif.Rational(28, 10));
        exif.Iso.ShouldBe(800);
        exif.Gps.ShouldNotBeNull().Latitude.ShouldBe(-(33 + (51 / 60.0) + (54 / 3600.0)), 1e-9);
        // The page's own orientation is the block's: the pixels are stored as the source stored them.
        TiffReader.ReadLayout(WritePage(new TiffPageOptions { Exif = bytes })).Orientation.ShouldBe(6);
    }

    /// <summary>
    /// Entries that describe image data, IFD1 and pointers this writer does not follow are not copied: the page keeps its
    /// own geometry and decodes to its own pixels, and the block read back has none of them.
    /// </summary>
    [Fact]
    public void Image_data_tags_and_the_thumbnail_ifd_are_not_copied()
    {
        var block = new BlockBuilder(littleEndian: true);
        block.Ifd0.Add(block.Long(TiffTag.ImageWidth, 9999));
        block.Ifd0.Add(block.Long(TiffTag.ImageLength, 9999));
        block.Ifd0.Add(block.Short(TiffTag.Compression, 6));
        block.Ifd0.Add(block.Long(TiffTag.StripOffsets, 4));
        block.Ifd0.Add(block.Short(TiffTag.BitsPerSample, 1));
        block.Ifd0.Add(block.Long(330, 8));            // SubIFDs: a pointer this writer does not follow
        block.Ifd0.Add(block.Short(531, 2));           // YCbCrPositioning: describes the JPEG's samples, not these
        block.Ifd0.Add(block.Undefined(TiffTag.XmpPacket, "<not the page's xmp/>"u8.ToArray()));
        block.Ifd0.Add(block.Ascii(0x010F, "Kept"));
        block.Ifd0.Add(new BlockBuilder.Entry(0x0110, 99, 1, new byte[4])); // an unknown type
        block.Exif.Add(block.Ascii(0x9003, "2026:01:02 03:04:05"));
        block.Thumbnail = true;
        var bytes = block.Build();

        var pixels = Pixels(3);
        var tiff = WritePage(new TiffPageOptions { Exif = bytes }, seed: 3);
        var page = TiffReader.Read(tiff).Pages.ShouldHaveSingleItem();
        page.Width.ShouldBe(W);
        page.Height.ShouldBe(H);
        page.Pixels.ShouldBe(pixels);

        TiffImageDecoder.TryReadMetadata(tiff, out var back).ShouldBeTrue();
        back.Xmp.IsEmpty.ShouldBeTrue();
        var dirs = Directories(back.Exif.Span);
        dirs.Ifd0.Select(e => e.Tag).ShouldNotContain(TiffTag.ImageWidth);
        dirs.Ifd0.ShouldContain(e => e.Tag == 0x010F);
        dirs.Ifd0.Select(e => e.Tag).ShouldNotContain((ushort)330);
        dirs.Ifd0.Select(e => e.Tag).ShouldNotContain((ushort)531);
        dirs.Ifd0.Select(e => e.Tag).ShouldNotContain((ushort)0x0110);
        dirs.Exif.ShouldHaveSingleItem().Tag.ShouldBe((ushort)0x9003);
        // IFD1 is gone: the reassembled block's IFD0 has no next directory.
        var ifd0 = BinaryPrimitives.ReadUInt32LittleEndian(back.Exif.Span[4..]);
        var n = BinaryPrimitives.ReadUInt16LittleEndian(back.Exif.Span[(int)ifd0..]);
        BinaryPrimitives.ReadUInt32LittleEndian(back.Exif.Span[((int)ifd0 + 2 + (12 * n))..]).ShouldBe(0u);
    }

    /// <summary>A page whose options set Artist / Software keeps them over the block's; resolution is always the page's.</summary>
    [Fact]
    public void Page_options_win_over_the_blocks_ifd0()
    {
        var block = new BlockBuilder(littleEndian: true);
        block.Ifd0.Add(block.Ascii(TiffTag.Software, "from exif"));
        block.Ifd0.Add(block.Ascii(TiffTag.Artist, "exif artist"));
        block.Ifd0.Add(block.Rational(282, 72, 1));
        block.Exif.Add(block.Ascii(0x9003, "2026:01:02 03:04:05"));
        var bytes = block.Build();

        var tiff = WritePage(new TiffPageOptions { Exif = bytes, Software = "page software", DpiX = 300, DpiY = 300 });
        var exif = ExifReader.FromTiff(TiffReaderMetadata(tiff).Exif.Span).ShouldNotBeNull();
        exif.Software.ShouldBe("page software");
        exif.Artist.ShouldBe("exif artist");
        TiffImageDecoder.TryReadInfo(tiff, out var info).ShouldBeTrue();
        info.Resolution!.Value.X.ShouldBe(300);
    }

    private static ImageMetadata TiffReaderMetadata(byte[] tiff)
    {
        TiffImageDecoder.TryReadMetadata(tiff, out var m).ShouldBeTrue();
        return m;
    }

    [Fact]
    public void Plain_tiff_without_exif_or_gps_ifd_has_no_exif_block()
    {
        var tiff = WritePage(new TiffPageOptions { Artist = "someone", Software = "tests" });
        TiffReaderMetadata(tiff).Exif.IsEmpty.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- untrusted blocks

    [Fact]
    public void A_block_that_is_not_a_tiff_structure_is_refused_by_the_writer_and_dropped_by_the_adapter()
    {
        Should.Throw<InvalidDataException>(() => WritePage(new TiffPageOptions { Exif = "not exif at all"u8.ToArray() }));

        var plain = AdapterEncode(null);
        var withJunk = AdapterEncode(new ImageMetadata { Exif = "MM\0+broken"u8.ToArray() });
        withJunk.ShouldBe(plain);
    }

    private static byte[] AdapterEncode(ImageMetadata? metadata)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var session = TiffImageEncoder.Create(output))
        {
            var frame = session.AddFrame(W, H, PixelFormat.Rgb24, null, metadata, null);
            frame.WriteRows(Pixels(), H, W * 3, TestContext.Current.CancellationToken);
            session.Commit(TestContext.Current.CancellationToken);
        }
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Overlapping entries cannot amplify a block: a thousand entries that all point at the same 16 KB are copied only
    /// while the bytes copied stay within the block's own length.
    /// </summary>
    [Fact]
    public void Overlapping_entries_are_bounded_by_the_blocks_length()
    {
        const int payload = 16 * 1024, entries = 1000;
        var ifd0Size = 2 + (12 * 1) + 4;
        var exifAt = 8 + ifd0Size;
        var exifSize = 2 + (12 * entries) + 4;
        var dataAt = exifAt + exifSize;
        var block = new byte[dataAt + payload];
        "II*\0"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 1);
        WriteEntry(block, 10, TiffTag.ExifIfd, 4, 1, (uint)exifAt);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(exifAt), entries);
        for (var i = 0; i < entries; i++)
            WriteEntry(block, exifAt + 2 + (12 * i), (ushort)(0xC000 + i), 7, payload, (uint)dataAt);

        var tiff = WritePage(new TiffPageOptions { Exif = block });
        tiff.Length.ShouldBeLessThan((W * H * 3) + (2 * block.Length));
        TiffReaderMetadata(tiff).Exif.Length.ShouldBeLessThanOrEqualTo(2 * block.Length);
    }

    /// <summary>An IFD0 of 65535 distinct tags: the page IFD keeps a count that fits its 16 bits, and the page reads back.</summary>
    [Fact]
    public void A_directory_with_every_tag_is_capped()
    {
        const int entries = ushort.MaxValue;
        var block = new byte[8 + 2 + (12 * entries) + 4];
        "II* "u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), entries);
        for (var i = 0; i < entries; i++)
            WriteEntry(block, 10 + (12 * i), (ushort)(i + 1), 3, 1, 7);

        var tiff = WritePage(new TiffPageOptions { Exif = block });
        var page = TiffReader.Read(tiff).Pages.ShouldHaveSingleItem();
        page.Width.ShouldBe(W);
        page.Pixels.Length.ShouldBe(W * H * 3);
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd)).ShouldBeLessThanOrEqualTo((ushort)(TiffExif.MaxEntries + 64));
    }

    /// <summary>Pointers into a loop, at themselves, past the end, and counts that overflow a 32-bit size: no hang, no throw.</summary>
    [Theory]
    [InlineData(8u)]          // the Exif IFD pointer leads back to IFD0
    [InlineData(26u)]         // ... to the Exif IFD itself (where the GPS pointer also leads)
    [InlineData(0xFFFFFFF0u)] // past the end
    [InlineData(3u)]          // inside the header
    public void Hostile_pointers_are_skipped(uint pointer)
    {
        var block = new byte[64];
        "II*\0"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 1);
        WriteEntry(block, 10, TiffTag.ExifIfd, 4, 1, pointer);
        // At 26: an IFD whose one entry claims 0xFFFFFFFF DOUBLEs, and a GPS pointer.
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(26), 2);
        WriteEntry(block, 28, 0x829A, 12, uint.MaxValue, 0);
        WriteEntry(block, 40, TiffTag.GpsInfoIfd, 4, 1, 26);

        var tiff = WritePage(new TiffPageOptions { Exif = block });
        TiffImageDecoder.TryDecode(tiff, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Dispose();
        TiffReaderMetadata(tiff);
    }

    /// <summary>Random damage to a real block: the writer succeeds or throws <see cref="InvalidDataException"/>, and the file reads back.</summary>
    [Fact]
    public void Damaged_blocks_write_or_throw_invalid_data()
    {
        var jpeg = MagickJpeg();
        ImageCodecs.TryReadMetadata(jpeg, out var m).ShouldBeTrue();
        var original = m.Exif.ToArray();
        var random = new Random(20261104);
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("TIFF_EXIF_FUZZ_ITERATIONS"), out var n) ? n : 2000;
        for (var i = 0; i < iterations; i++)
        {
            var block = (byte[])original.Clone();
            var hits = random.Next(1, 8);
            for (var h = 0; h < hits; h++)
                block[random.Next(block.Length)] = (byte)random.Next(256);
            if (random.Next(4) == 0)
                Array.Resize(ref block, random.Next(block.Length));
            byte[] tiff;
            try
            {
                tiff = WritePage(new TiffPageOptions { Exif = block });
            }
            catch (InvalidDataException)
            {
                continue;
            }
            TiffImageDecoder.TryReadMetadata(tiff, out var back).ShouldBeTrue();
            back.Exif.Length.ShouldBeLessThanOrEqualTo(2 * Math.Max(block.Length, 64) + 64);
            TiffReader.Read(tiff).Pages.ShouldHaveSingleItem().Pixels.Length.ShouldBe(W * H * 3);
        }
    }

    private static void WriteEntry(byte[] block, int at, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(at + 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at + 8), value);
    }

    /// <summary>Hand-assembles an Exif block (TIFF 6.0 layout) in either byte order: IFD0, Exif, Interop, GPS and optionally an IFD1.</summary>
    private sealed class BlockBuilder(bool littleEndian)
    {
        public sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

        public List<Entry> Ifd0 { get; } = [];
        public List<Entry> Exif { get; } = [];
        public List<Entry> Interop { get; } = [];
        public List<Entry> Gps { get; } = [];
        public bool Thumbnail { get; set; }

        private void P16(Span<byte> d, ushort v) { if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(d, v); else BinaryPrimitives.WriteUInt16BigEndian(d, v); }
        private void P32(Span<byte> d, uint v) { if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(d, v); else BinaryPrimitives.WriteUInt32BigEndian(d, v); }

        public Entry Ascii(ushort tag, string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); return new(tag, 2, (uint)b.Length, b); }
        public Entry Undefined(ushort tag, byte[] b) => new(tag, 7, (uint)b.Length, b);
        public Entry Short(ushort tag, ushort v) { var b = new byte[2]; P16(b, v); return new(tag, 3, 1, b); }
        public Entry Long(ushort tag, uint v) { var b = new byte[4]; P32(b, v); return new(tag, 4, 1, b); }
        public Entry Rational(ushort tag, uint n, uint d) => Rationals(tag, (n, d));
        public Entry Rationals(ushort tag, params (uint N, uint D)[] values)
        {
            var b = new byte[8 * values.Length];
            for (var i = 0; i < values.Length; i++) { P32(b.AsSpan(8 * i), values[i].N); P32(b.AsSpan((8 * i) + 4), values[i].D); }
            return new(tag, 5, (uint)values.Length, b);
        }
        public Entry Double(ushort tag, double v)
        {
            var b = new byte[8];
            if (littleEndian) BinaryPrimitives.WriteDoubleLittleEndian(b, v); else BinaryPrimitives.WriteDoubleBigEndian(b, v);
            return new(tag, 12, 1, b);
        }

        public byte[] Build()
        {
            var ifd0 = new List<Entry>(Ifd0);
            var exif = new List<Entry>(Exif);
            if (Exif.Count > 0) ifd0.Add(Long(TiffTag.ExifIfd, 0));
            if (Gps.Count > 0) ifd0.Add(Long(TiffTag.GpsInfoIfd, 0));
            if (Interop.Count > 0) exif.Add(Long(40965, 0));
            var ifd1 = new List<Entry>();
            var thumb = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            if (Thumbnail) { ifd1.Add(Long(0x0201, 0)); ifd1.Add(Long(0x0202, (uint)thumb.Length)); }

            static int Size(List<Entry> e) => 2 + (12 * e.Count) + 4 + e.Where(x => x.Value.Length > 4).Sum(x => (x.Value.Length + 1) & ~1);
            var at = 8;
            var ifd0At = at; at += Size(ifd0);
            var exifAt = at; at += exif.Count > 0 ? Size(exif) : 0;
            var interopAt = at; at += Interop.Count > 0 ? Size(Interop) : 0;
            var gpsAt = at; at += Gps.Count > 0 ? Size(Gps) : 0;
            var ifd1At = at; at += Thumbnail ? Size(ifd1) : 0;
            var thumbAt = at; at += Thumbnail ? thumb.Length : 0;

            void Point(List<Entry> list, ushort tag, int offset)
            {
                var i = list.FindIndex(e => e.Tag == tag);
                if (i >= 0) list[i] = Long(tag, (uint)offset);
            }
            Point(ifd0, TiffTag.ExifIfd, exifAt);
            Point(ifd0, TiffTag.GpsInfoIfd, gpsAt);
            Point(exif, 40965, interopAt);
            Point(ifd1, 0x0201, thumbAt);

            var block = new byte[at];
            block[0] = block[1] = (byte)(littleEndian ? 'I' : 'M');
            P16(block.AsSpan(2), 42);
            P32(block.AsSpan(4), (uint)ifd0At);
            void Write(List<Entry> list, int offset, int next)
            {
                list.Sort((a, b) => a.Tag.CompareTo(b.Tag));
                P16(block.AsSpan(offset), (ushort)list.Count);
                var data = offset + 2 + (12 * list.Count) + 4;
                for (var i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    var entry = block.AsSpan(offset + 2 + (12 * i));
                    P16(entry, e.Tag); P16(entry[2..], e.Type); P32(entry[4..], e.Count);
                    if (e.Value.Length <= 4) e.Value.CopyTo(entry[8..]);
                    else { P32(entry[8..], (uint)data); e.Value.CopyTo(block.AsSpan(data)); data += (e.Value.Length + 1) & ~1; }
                }
                P32(block.AsSpan(offset + 2 + (12 * list.Count)), (uint)next);
            }
            Write(ifd0, ifd0At, Thumbnail ? ifd1At : 0);
            if (exif.Count > 0) Write(exif, exifAt, 0);
            if (Interop.Count > 0) Write(Interop, interopAt, 0);
            if (Gps.Count > 0) Write(Gps, gpsAt, 0);
            if (Thumbnail) { Write(ifd1, ifd1At, 0); thumb.CopyTo(block, thumbAt); }
            return block;
        }
    }
}
