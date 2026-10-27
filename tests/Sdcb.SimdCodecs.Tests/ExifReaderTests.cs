using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Sdcb.SimdCodecs.Exif;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Unit tests for <see cref="ExifReader"/>. Each test builds a minimal
/// container (JPEG APP1 / TIFF / PNG eXIf) with a hand-crafted EXIF blob,
/// then verifies the strongly-typed projection lands the expected values
/// and the raw-tag map preserves everything verbatim.
/// </summary>
public sealed class ExifReaderTests
{
    [Fact]
    public void FromTiff_MakeModelAndDateTime_PopulatedFromIfd0()
    {
        var tiff = BuildTiff(
            fileIsLE: true,
            ifd0Entries: new[]
            {
                AsciiTag(ExifTag.Make, "Canon"),
                AsciiTag(ExifTag.Model, "Canon EOS 6D"),
                AsciiTag(ExifTag.DateTime, "2026:04:08 21:18:28"),
                ShortTag(ExifTag.Orientation, 1),
            });

        var exif = ExifReader.FromTiff(tiff);
        exif.ShouldNotBeNull();
        exif!.Make.ShouldBe("Canon");
        exif.Model.ShouldBe("Canon EOS 6D");
        exif.Orientation.ShouldBe((ushort?)1);
        exif.FileTime.ShouldBe(new DateTime(2026, 4, 8, 21, 18, 28));
        // Without an explicit DateTimeOriginal, CaptureTime should fall back to DateTime.
        exif.CaptureTime.ShouldBe(new DateTime(2026, 4, 8, 21, 18, 28));
        exif.RawTags.ShouldContainKey(ExifTag.Make);
        exif.RawTags.ShouldContainKey(ExifTag.Model);
    }

    [Fact]
    public void FromTiff_BigEndian_DecodesIdenticallyToLittleEndian()
    {
        // Same content, both byte orders — the reader should yield identical metadata.
        var tiffLE = BuildTiff(true, new[]
        {
            AsciiTag(ExifTag.Make, "Nikon"),
            ShortTag(ExifTag.Orientation, 6),
        });
        var tiffBE = BuildTiff(false, new[]
        {
            AsciiTag(ExifTag.Make, "Nikon"),
            ShortTag(ExifTag.Orientation, 6),
        });
        var le = ExifReader.FromTiff(tiffLE);
        var be = ExifReader.FromTiff(tiffBE);
        le.ShouldNotBeNull();
        be.ShouldNotBeNull();
        le!.Make.ShouldBe(be!.Make);
        le.Orientation.ShouldBe(be.Orientation);
    }

    [Fact]
    public void FromTiff_RationalAndExposureTags_DecodedCorrectly()
    {
        var tiff = BuildTiff(
            fileIsLE: true,
            ifd0Entries: new[]
            {
                // Sub-IFD pointer to where the EXIF data really lives.
                IfdPointerTag(TiffTag.ExifIfd, /*subIfdMarker*/ 0xFEED),
            },
            exifIfdEntries: new[]
            {
                RationalTag(ExifTag.ExposureTime, 1, 250),
                RationalTag(ExifTag.FNumber, 28, 10),
                ShortTag(ExifTag.IsoSpeedRatings, 800),
                RationalTag(ExifTag.FocalLength, 35, 1),
                AsciiTag(ExifTag.DateTimeOriginal, "2026:05:12 03:14:15"),
            });

        var exif = ExifReader.FromTiff(tiff);
        exif.ShouldNotBeNull();
        exif!.ExposureTime!.Value.Numerator.ShouldBe(1u);
        exif.ExposureTime.Value.Denominator.ShouldBe(250u);
        exif.FNumber!.Value.ToDouble().ShouldBe(2.8);
        exif.Iso.ShouldBe(800);
        exif.FocalLength!.Value.ToDouble().ShouldBe(35);
        exif.CaptureTime.ShouldBe(new DateTime(2026, 5, 12, 3, 14, 15));
    }

    [Fact]
    public void FromJpeg_App1ExifSegment_Decoded()
    {
        var exifTiff = BuildTiff(true, new[]
        {
            AsciiTag(ExifTag.Make, "Sony"),
            AsciiTag(ExifTag.Model, "α7 III"),
        });
        var jpeg = WrapInJpegApp1(exifTiff);
        var exif = ExifReader.FromJpeg(jpeg);
        exif.ShouldNotBeNull();
        exif!.Make.ShouldBe("Sony");
        exif.Model.ShouldBe("α7 III");
    }

    [Fact]
    public void FromBytes_AutoDetectsJpegAndTiff()
    {
        var exifTiff = BuildTiff(true, new[] { AsciiTag(ExifTag.Make, "Fujifilm") });
        ExifReader.FromBytes(exifTiff).ShouldNotBeNull()!.Make.ShouldBe("Fujifilm");
        ExifReader.FromBytes(WrapInJpegApp1(exifTiff)).ShouldNotBeNull()!.Make.ShouldBe("Fujifilm");
    }

    [Fact]
    public void FromJpeg_NoApp1Exif_ReturnsNull()
    {
        // JPEG signature + EOI, no APP1 marker → no EXIF.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        ExifReader.FromJpeg(jpeg).ShouldBeNull();
    }

    [Fact]
    public void FromTiff_GpsSubIfd_BuildsSignedLatLonAltitude()
    {
        // GPS sub-IFD with one tag of each kind we care about:
        // 47°N 8°E altitude 412m above sea level.
        var gpsEntries = new[]
        {
            AsciiTag(ExifTag.GpsLatitudeRef,  "N"),
            DmsTag  (ExifTag.GpsLatitude,     47,  0, 0), // 47° 0' 0"
            AsciiTag(ExifTag.GpsLongitudeRef, "E"),
            DmsTag  (ExifTag.GpsLongitude,     8,  0, 0),
            ByteTag (ExifTag.GpsAltitudeRef,  0),
            RationalTag(ExifTag.GpsAltitude, 412, 1),
        };
        var tiff = BuildTiffWithGps(true, gpsEntries);

        var exif = ExifReader.FromTiff(tiff);
        exif.ShouldNotBeNull();
        exif!.Gps.ShouldNotBeNull();
        exif.Gps!.Latitude.ShouldBe(47);
        exif.Gps.Longitude.ShouldBe(8);
        exif.Gps.Altitude.ShouldBe(412);
    }

    [Fact]
    public void FromTiff_GpsSouthWestBelowSea_NegateAxes()
    {
        var gpsEntries = new[]
        {
            AsciiTag(ExifTag.GpsLatitudeRef,  "S"),
            DmsTag  (ExifTag.GpsLatitude,     30, 15, 0),
            AsciiTag(ExifTag.GpsLongitudeRef, "W"),
            DmsTag  (ExifTag.GpsLongitude,    70, 30, 0),
            ByteTag (ExifTag.GpsAltitudeRef,  1),
            RationalTag(ExifTag.GpsAltitude, 100, 1),
        };
        var tiff = BuildTiffWithGps(true, gpsEntries);

        var exif = ExifReader.FromTiff(tiff);
        exif!.Gps.ShouldNotBeNull();
        exif.Gps!.Latitude.ShouldBe(-(30 + 15.0 / 60));
        exif.Gps.Longitude.ShouldBe(-(70 + 30.0 / 60));
        exif.Gps.Altitude.ShouldBe(-100);
    }

    [Fact]
    public void FromPng_eXIfChunk_Decoded()
    {
        var exifTiff = BuildTiff(true, new[] { AsciiTag(ExifTag.Make, "Pixel 8") });
        var png = WrapInPngWithExifChunk(exifTiff);
        ExifReader.FromPng(png).ShouldNotBeNull()!.Make.ShouldBe("Pixel 8");
    }

    [Fact]
    public void FromBytes_NotAnImage_ReturnsNull()
    {
        ExifReader.FromBytes(new byte[] { 0x00, 0x01, 0x02, 0x03 }).ShouldBeNull();
    }

    [Fact]
    public void FromTiff_ThirdPartyWrittenExifBlock_ReadsCameraMetadata()
    {
        // An EXIF block serialised by ImageMagick (via Magick.NET) rather than by
        // anything in this repo: IFD0 + ExifIFD + GPS IFD with value-overflow
        // regions, which is the same TIFF-IFD structure a camera raw's metadata
        // prefix carries. Independent writer, so a shared misreading of the IFD
        // layout cannot round-trip silently.
        var profile = new ImageMagick.ExifProfile();
        profile.SetValue(ImageMagick.ExifTag.Make, "Canon");
        profile.SetValue(ImageMagick.ExifTag.Model, "Canon EOS 6D");
        profile.SetValue(ImageMagick.ExifTag.Orientation, (ushort)1);
        profile.SetValue(ImageMagick.ExifTag.DateTime, "2026:04:08 21:18:28");
        profile.SetValue(ImageMagick.ExifTag.DateTimeOriginal, "2026:04:08 21:18:27");
        profile.SetValue(ImageMagick.ExifTag.ExposureTime, new ImageMagick.Rational(1, 125));
        profile.SetValue(ImageMagick.ExifTag.FNumber, new ImageMagick.Rational(28, 10));
        profile.SetValue(ImageMagick.ExifTag.GPSLatitudeRef, "N");
        profile.SetValue(ImageMagick.ExifTag.GPSLatitude,
            [new ImageMagick.Rational(31, 1), new ImageMagick.Rational(14, 1), new ImageMagick.Rational(0, 1)]);
        profile.SetValue(ImageMagick.ExifTag.GPSLongitudeRef, "E");
        profile.SetValue(ImageMagick.ExifTag.GPSLongitude,
            [new ImageMagick.Rational(121, 1), new ImageMagick.Rational(30, 1), new ImageMagick.Rational(0, 1)]);

        var bytes = profile.ToByteArray()!;
        // Magick prefixes the APP1 "Exif\0\0" identifier; the TIFF structure follows it.
        if (bytes.AsSpan().StartsWith("Exif\0\0"u8))
            bytes = bytes[6..];

        var exif = ExifReader.FromTiff(bytes);

        exif.ShouldNotBeNull();
        exif!.Make.ShouldBe("Canon");
        exif.Model.ShouldBe("Canon EOS 6D");
        exif.Orientation.ShouldBe((ushort?)1);
        exif.FileTime.ShouldBe(new DateTime(2026, 4, 8, 21, 18, 28));
        exif.CaptureTime.ShouldBe(new DateTime(2026, 4, 8, 21, 18, 27));
        exif.FNumber.ShouldNotBeNull();
        exif.Gps.ShouldNotBeNull();
    }

    [Fact]
    public async Task FromTiff_ExifIfdOffsetExposedOnTiffPage()
    {
        // Integration with TiffReader: TiffPage now surfaces ExifIfdOffset +
        // GpsInfoIfdOffset + FileIsLittleEndian, so a EXIF-aware caller can
        // grab them directly and call ExifReader.FromIfd without re-walking
        // the TIFF header. Use a real 1-byte uint8 grayscale image so the
        // pixel-decode path succeeds.
        using var ms = new MemoryStream();
        await using (var writer = TiffWriter.Create(ms))
        {
            await writer.AddPageAsync(new byte[] { 0xAB }, 1, 1, new TiffPageOptions
            {
                SamplesPerPixel = 1, BitsPerSample = 8,
                Photometric = TiffPhotometric.MinIsBlack,
                SampleFormat = TiffSampleFormat.Uint,
                Compression = TiffCompression.Uncompressed,
            }, TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }
        // Manually inject an ExifIfd pointer (tag 34665, LONG=4) into the IFD.
        // TiffWriter sorts tags ascending — we splice a synthetic entry into the
        // IFD using a small hand-edit. Easier: rebuild the TIFF with the helper.
        var tiff = BuildTiff(true, new[]
        {
            ShortTag(0x0100, 1),      // ImageWidth
            ShortTag(0x0101, 1),      // ImageLength
            ShortTag(0x0102, 8),      // BitsPerSample
            ShortTag(0x0103, 1),      // Compression = Uncompressed
            ShortTag(0x0106, 1),      // PhotometricInterpretation = MinIsBlack
            // Strip data is appended after the IFD overflow region. The helper
            // will overwrite the IfdPointerTag below with the real offset, but
            // here we just need a stub that satisfies the strip-bounds check.
            LongTag (0x0111, 0),      // StripOffsets — patched below
            LongTag (0x0117, 1),      // StripByteCounts (1 byte)
            IfdPointerTag(TiffTag.ExifIfd, 0xABCD),
        });
        // Compute where the strip should go: end of TIFF blob currently. We'll
        // append one pixel byte and patch StripOffsets.
        var stripOffset = (uint)tiff.Length;
        var withStrip = new byte[tiff.Length + 1];
        Array.Copy(tiff, withStrip, tiff.Length);
        withStrip[tiff.Length] = 0xAB;
        // Find the 0x0111 (StripOffsets) entry in IFD0 and write the real offset.
        PatchStripOffsetTag(withStrip, stripOffset);

        var doc = TiffReader.Read(withStrip);
        doc.Pages.Count.ShouldBe(1);
        var page = doc.Pages[0];
        page.ExifIfdOffset.ShouldBe(0xABCD);
        page.FileIsLittleEndian.ShouldBeTrue();
        page.Pixels.ShouldBe(new byte[] { 0xAB });
    }

    private static void PatchStripOffsetTag(byte[] tiff, uint stripOffset)
    {
        // Walk IFD0 (starts at offset 8) and patch tag 0x0111's value field.
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(8, 2));
        for (var i = 0; i < entryCount; i++)
        {
            var entryStart = 8 + 2 + i * 12;
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(entryStart, 2));
            if (tag == 0x0111)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(entryStart + 8, 4), stripOffset);
                return;
            }
        }
    }

    // -----------------------------------------------------------------
    // Helper builders — emit valid TIFF / JPEG / PNG containers around a
    // small set of hand-crafted IFD entries.
    // -----------------------------------------------------------------

    private record struct EntryDraft(ushort Tag, TiffFieldType Type, byte[] Bytes);

    private static EntryDraft AsciiTag(ushort tag, string value)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        return new EntryDraft(tag, TiffFieldType.Ascii, raw);
    }

    private static EntryDraft ShortTag(ushort tag, ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        return new EntryDraft(tag, TiffFieldType.Short, b);
    }

    private static EntryDraft LongTag(ushort tag, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return new EntryDraft(tag, TiffFieldType.Long, b);
    }

    private static EntryDraft ByteTag(ushort tag, byte value)
        => new(tag, TiffFieldType.Byte, new[] { value });

    private static EntryDraft RationalTag(ushort tag, uint num, uint den)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), num);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), den);
        return new EntryDraft(tag, TiffFieldType.Rational, b);
    }

    private static EntryDraft DmsTag(ushort tag, uint d, uint m, uint s)
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4),  d);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4),  1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8, 4),  m);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16, 4), s);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20, 4), 1);
        return new EntryDraft(tag, TiffFieldType.Rational, b);
    }

    private static EntryDraft IfdPointerTag(ushort tag, uint subIfdOffset)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, subIfdOffset);
        return new EntryDraft(tag, TiffFieldType.Long, b);
    }

    /// <summary>
    /// Build a complete TIFF with a single IFD ("IFD0") containing the given
    /// entries plus an optional second IFD ("EXIF sub-IFD") referenced by
    /// the IFD0 ExifIfd tag pointer. Byte order configurable.
    /// </summary>
    private static byte[] BuildTiff(bool fileIsLE, EntryDraft[] ifd0Entries, EntryDraft[]? exifIfdEntries = null)
    {
        using var ms = new MemoryStream();
        WriteTiffHeader(ms, fileIsLE, firstIfdOffset: 8);
        // Sort entries by tag — TIFF requires ascending order.
        Array.Sort(ifd0Entries, (a, b) => a.Tag.CompareTo(b.Tag));
        // If a sub-IFD pointer is in IFD0, patch its offset to point past IFD0.
        var ifd0Bytes = SerializeIfd(ifd0Entries, fileIsLE, baseOffset: 8, nextIfdOffset: 0,
            out var overflowSection);
        ms.Write(ifd0Bytes);
        ms.Write(overflowSection);

        if (exifIfdEntries is not null)
        {
            // Patch the IFD0 ExifIfd pointer to where we're about to write the sub-IFD.
            var subIfdOffset = (int)ms.Length;
            PatchIfdPointer(ms, ifd0Entries, fileIsLE, TiffTag.ExifIfd, subIfdOffset);
            Array.Sort(exifIfdEntries, (a, b) => a.Tag.CompareTo(b.Tag));
            var subIfdBytes = SerializeIfd(exifIfdEntries, fileIsLE, baseOffset: subIfdOffset, nextIfdOffset: 0,
                out var subOverflow);
            ms.Write(subIfdBytes);
            ms.Write(subOverflow);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Build a TIFF where IFD0 has only the GpsInfoIfd pointer + a minimum
    /// sentinel tag, with the GPS sub-IFD containing the given entries.
    /// </summary>
    private static byte[] BuildTiffWithGps(bool fileIsLE, EntryDraft[] gpsEntries)
    {
        using var ms = new MemoryStream();
        WriteTiffHeader(ms, fileIsLE, firstIfdOffset: 8);
        var ifd0Entries = new[]
        {
            AsciiTag(ExifTag.Make, "DUMMY"),
            IfdPointerTag(TiffTag.GpsInfoIfd, 0xCAFE), // patched below
        };
        Array.Sort(ifd0Entries, (a, b) => a.Tag.CompareTo(b.Tag));
        var ifd0Bytes = SerializeIfd(ifd0Entries, fileIsLE, baseOffset: 8, nextIfdOffset: 0, out var overflow);
        ms.Write(ifd0Bytes);
        ms.Write(overflow);

        var gpsOffset = (int)ms.Length;
        PatchIfdPointer(ms, ifd0Entries, fileIsLE, TiffTag.GpsInfoIfd, gpsOffset);
        Array.Sort(gpsEntries, (a, b) => a.Tag.CompareTo(b.Tag));
        var gpsBytes = SerializeIfd(gpsEntries, fileIsLE, baseOffset: gpsOffset, nextIfdOffset: 0, out var gpsOverflow);
        ms.Write(gpsBytes);
        ms.Write(gpsOverflow);
        return ms.ToArray();
    }

    private static void WriteTiffHeader(MemoryStream ms, bool fileIsLE, uint firstIfdOffset)
    {
        var orderByte = (byte)(fileIsLE ? 'I' : 'M');
        ms.WriteByte(orderByte); ms.WriteByte(orderByte);
        Span<byte> two = stackalloc byte[2];
        WriteU16(two, 42, fileIsLE); ms.Write(two);
        Span<byte> four = stackalloc byte[4];
        WriteU32(four, firstIfdOffset, fileIsLE); ms.Write(four);
    }

    /// <summary>
    /// Serialise an IFD: 2-byte entry count + N×12-byte entries + 4-byte
    /// next-IFD-offset + overflow section (out parameter, written separately
    /// by the caller). For each entry whose value is > 4 bytes, we emit the
    /// value into the overflow region and point the entry's value-field at
    /// it.
    /// </summary>
    private static byte[] SerializeIfd(EntryDraft[] entries, bool fileIsLE, int baseOffset, uint nextIfdOffset,
        out byte[] overflowSection)
    {
        using var ifd = new MemoryStream();
        using var overflow = new MemoryStream();
        Span<byte> two = stackalloc byte[2];
        Span<byte> four = stackalloc byte[4];

        WriteU16(two, (ushort)entries.Length, fileIsLE); ifd.Write(two);

        // Overflow base address = end of the IFD directory section.
        var overflowBaseAddr = baseOffset + 2 + entries.Length * 12 + 4;

        foreach (var e in entries)
        {
            WriteU16(two, e.Tag, fileIsLE); ifd.Write(two);
            WriteU16(two, (ushort)e.Type, fileIsLE); ifd.Write(two);
            var elementSize = ElementSize(e.Type);
            var count = e.Bytes.Length / elementSize;
            WriteU32(four, (uint)count, fileIsLE); ifd.Write(four);
            // Test helpers pre-encode value bytes in LE (the host's natural
            // order). For a BE file we have to reverse each numeric element
            // so the on-disk bytes match the declared byte order.
            var valueBytes = fileIsLE ? e.Bytes : ByteSwapValues(e.Bytes, e.Type);
            if (valueBytes.Length <= 4)
            {
                var padded = new byte[4];
                valueBytes.CopyTo(padded, 0);
                ifd.Write(padded);
            }
            else
            {
                WriteU32(four, (uint)(overflowBaseAddr + overflow.Length), fileIsLE); ifd.Write(four);
                overflow.Write(valueBytes);
            }
        }

        WriteU32(four, nextIfdOffset, fileIsLE); ifd.Write(four);
        overflowSection = overflow.ToArray();
        return ifd.ToArray();
    }

    private static int ElementSize(TiffFieldType t) => t switch
    {
        TiffFieldType.Byte or TiffFieldType.Ascii or TiffFieldType.SByte or TiffFieldType.Undefined => 1,
        TiffFieldType.Short or TiffFieldType.SShort => 2,
        TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float => 4,
        TiffFieldType.Rational or TiffFieldType.SRational or TiffFieldType.Double => 8,
        _ => 1,
    };

    /// <summary>
    /// Byte-reverse each numeric element of a TIFF value buffer (used by the
    /// BE-file builder path). Rational types are two adjacent 32-bit uints —
    /// each is swapped independently. ASCII / BYTE / UNDEFINED need no swap.
    /// </summary>
    private static byte[] ByteSwapValues(byte[] src, TiffFieldType type)
    {
        var elementSize = ElementSize(type);
        if (type == TiffFieldType.Ascii || type == TiffFieldType.Byte
            || type == TiffFieldType.SByte || type == TiffFieldType.Undefined)
            return src;
        if (type == TiffFieldType.Rational || type == TiffFieldType.SRational)
        {
            // Two 32-bit components per element; swap each 4-byte half.
            var dst = new byte[src.Length];
            for (var i = 0; i < src.Length; i += 8)
            {
                for (var j = 0; j < 4; j++) dst[i + j] = src[i + 3 - j];
                for (var j = 0; j < 4; j++) dst[i + 4 + j] = src[i + 4 + 3 - j];
            }
            return dst;
        }
        var swapped = new byte[src.Length];
        for (var i = 0; i < src.Length; i += elementSize)
        {
            for (var j = 0; j < elementSize; j++)
                swapped[i + j] = src[i + elementSize - 1 - j];
        }
        return swapped;
    }

    /// <summary>
    /// Locate the IFD entry for <paramref name="tag"/> inside the just-written
    /// IFD0 (offset 8 in the stream) and patch its value field to point at
    /// <paramref name="actualOffset"/>. Used after we've appended a sub-IFD.
    /// </summary>
    private static void PatchIfdPointer(MemoryStream ms, EntryDraft[] entries, bool fileIsLE, ushort tag, int actualOffset)
    {
        const int ifd0Start = 8;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].Tag != tag) continue;
            var entryStart = ifd0Start + 2 + i * 12;
            var buf = ms.GetBuffer();
            Span<byte> four = stackalloc byte[4];
            WriteU32(four, (uint)actualOffset, fileIsLE);
            four.CopyTo(buf.AsSpan(entryStart + 8, 4));
            return;
        }
    }

    private static void WriteU16(Span<byte> dest, ushort value, bool fileIsLE)
    {
        if (fileIsLE) BinaryPrimitives.WriteUInt16LittleEndian(dest, value);
        else          BinaryPrimitives.WriteUInt16BigEndian(dest, value);
    }

    private static void WriteU32(Span<byte> dest, uint value, bool fileIsLE)
    {
        if (fileIsLE) BinaryPrimitives.WriteUInt32LittleEndian(dest, value);
        else          BinaryPrimitives.WriteUInt32BigEndian(dest, value);
    }

    private static byte[] WrapInJpegApp1(byte[] exifTiff)
    {
        // FF D8 SOI + FF E1 APP1 marker + segLen(2) + "Exif\0\0" + tiff + FF D9 EOI.
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF); ms.WriteByte(0xD8);                  // SOI
        ms.WriteByte(0xFF); ms.WriteByte(0xE1);                  // APP1
        var segLen = 2 + 6 + exifTiff.Length;                    // length includes itself
        ms.WriteByte((byte)(segLen >> 8)); ms.WriteByte((byte)(segLen & 0xFF));
        ms.Write(new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 });
        ms.Write(exifTiff);
        ms.WriteByte(0xFF); ms.WriteByte(0xD9);                  // EOI
        return ms.ToArray();
    }

    private static byte[] WrapInPngWithExifChunk(byte[] exifTiff)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }); // PNG signature
        WriteChunk(ms, "IHDR"u8, new byte[13]);                  // 13-byte zero IHDR
        WriteChunk(ms, "eXIf"u8, exifTiff);
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream ms, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(four, (uint)data.Length); ms.Write(four);
        ms.Write(type);
        ms.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(four, 0u); ms.Write(four); // CRC ignored by reader
    }
}
