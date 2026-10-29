using System;
using System.Linq;
using System.Text;
using ImageMagick;
using Sdcb.SimdCodecs.Exif;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using ExifTag = Sdcb.SimdCodecs.Exif.ExifTag;
using MagickExifTag = ImageMagick.ExifTag;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="ExifWriter"/> and <see cref="JpegEncodeOptions.Exif"/>: every field round-trips through
/// <see cref="ExifReader.FromJpeg"/>, the APP1 size limit is exact and checked before any output, a null option
/// changes no byte, and Magick.NET (libjpeg + its own EXIF parser) reads the written fields back.
/// </summary>
public sealed class JpegExifWriteTests
{
    private const int W = 16, H = 16;

    private static byte[] Pixels()
    {
        var p = new byte[W * H * 3];
        for (var i = 0; i < p.Length; i++) p[i] = (byte)(i * 7);
        return p;
    }

    private static byte[] Encode(ReadOnlyMemory<byte>? exif) =>
        JpegEncoder.Encode(Pixels(), W, H, 3, new JpegEncodeOptions { Exif = exif });

    private static byte[] AllFieldsBlob() => ExifWriter.Build(new ExifWriteData(
        Software: "Sdcb.SimdCodecs.Jpeg test",
        ImageDescription: "A short description",
        Artist: "Someone",
        DateTime: new DateTime(2026, 10, 25, 14, 50, 23),
        UserComment: "plain ascii comment"));

    [Fact]
    public void EveryField_RoundTripsThroughExifReader()
    {
        var jpeg = Encode(AllFieldsBlob());

        var exif = ExifReader.FromJpeg(jpeg);
        exif.ShouldNotBeNull();
        exif.Software.ShouldBe("Sdcb.SimdCodecs.Jpeg test");
        exif.Artist.ShouldBe("Someone");
        exif.FileTime.ShouldBe(new DateTime(2026, 10, 25, 14, 50, 23));
        AsciiOf(exif, ExifTag.ImageDescription).ShouldBe("A short description");
        exif.RawTags[ExifTag.UserComment].Type.ShouldBe(TiffFieldType.Undefined);
        exif.RawTags[ExifTag.UserComment].Bytes.ShouldBe(Encoding.ASCII.GetBytes("ASCII\0\0\0plain ascii comment"));
    }

    [Fact]
    public void UserComment_NonAscii_IsUnicodeCharacterCodeWithLittleEndianUtf16()
    {
        const string text = "Zażółć gęślą jaźń — 星雲 🌌";
        var jpeg = Encode(ExifWriter.Build(new ExifWriteData(UserComment: text)));

        var exif = ExifReader.FromJpeg(jpeg);
        exif.ShouldNotBeNull();
        var bytes = exif.RawTags[ExifTag.UserComment].Bytes;
        bytes.AsSpan(0, 8).SequenceEqual("UNICODE\0"u8).ShouldBeTrue();
        // The TIFF is "II", so the UTF-16 is little-endian.
        Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).ShouldBe(text);
        exif.RawTags[ExifTag.UserComment].Count.ShouldBe(bytes.Length);
    }

    [Fact]
    public void PartialData_WritesOnlyWhatIsSet()
    {
        var onlyArtist = ExifReader.FromJpeg(Encode(ExifWriter.Build(new ExifWriteData(Artist: "Me"))));
        onlyArtist.ShouldNotBeNull();
        onlyArtist.Artist.ShouldBe("Me");
        onlyArtist.Software.ShouldBeNull();
        onlyArtist.FileTime.ShouldBeNull();
        onlyArtist.RawTags.Keys.ShouldBe([ExifTag.Artist]);

        // A UserComment alone needs the Exif sub-IFD pointer and nothing else.
        var onlyComment = ExifReader.FromJpeg(Encode(ExifWriter.Build(new ExifWriteData(UserComment: "x"))));
        onlyComment.ShouldNotBeNull();
        onlyComment.RawTags.Keys.OrderBy(k => k).ShouldBe([ExifTag.ExifIfdPointer, ExifTag.UserComment]);
    }

    [Fact]
    public void EmptyData_IsAValidBlobWithAnEmptyIfd0()
    {
        var blob = ExifWriter.Build(new ExifWriteData());
        blob.Length.ShouldBe(8 + 2 + 4);
        blob.AsSpan(0, 4).SequenceEqual("II*\0"u8).ShouldBeTrue();

        var exif = ExifReader.FromJpeg(Encode(blob));
        exif.ShouldNotBeNull();
        exif.RawTags.ShouldBeEmpty();
    }

    [Fact]
    public void EmptyStrings_AreWrittenNotOmitted()
    {
        var exif = ExifReader.FromJpeg(Encode(ExifWriter.Build(new ExifWriteData(Artist: "", UserComment: ""))));
        exif.ShouldNotBeNull();
        exif.Artist.ShouldBe("");
        exif.RawTags[ExifTag.UserComment].Bytes.ShouldBe("ASCII\0\0\0"u8.ToArray());
    }

    [Fact]
    public void Layout_IsWordAlignedSortedAndNeverChains()
    {
        var blob = AllFieldsBlob();
        var count = BitConverter.ToUInt16(blob, 8);
        count.ShouldBe((ushort)5);
        var tags = Enumerable.Range(0, count).Select(i => BitConverter.ToUInt16(blob, 10 + i * 12)).ToArray();
        tags.ShouldBe(tags.OrderBy(t => t).ToArray());
        for (var i = 0; i < count; i++)
        {
            var size = BitConverter.ToUInt32(blob, 10 + i * 12 + 4);
            var value = BitConverter.ToUInt32(blob, 10 + i * 12 + 8);
            if (tags[i] != ExifTag.ExifIfdPointer && size > 4) (value % 2).ShouldBe(0u);
        }
        BitConverter.ToUInt32(blob, 10 + count * 12).ShouldBe(0u);
    }

    [Theory]
    [InlineData("Zażółć")]
    [InlineData("é")]
    [InlineData("nul\0inside")]
    public void Ifd0Text_ThatIsNotAscii_IsRefused(string text)
    {
        Should.Throw<ArgumentException>(() => ExifWriter.Build(new ExifWriteData(Software: text)));
        Should.Throw<ArgumentException>(() => ExifWriter.Build(new ExifWriteData(ImageDescription: text)));
        Should.Throw<ArgumentException>(() => ExifWriter.Build(new ExifWriteData(Artist: text)));
    }

    [Fact]
    public void UserComment_WithAnUnpairedSurrogate_IsRefused() =>
        Should.Throw<ArgumentException>(() => ExifWriter.Build(new ExifWriteData(UserComment: "bad \ud800 surrogate")));

    // ------------------------------------------------------------------ the APP1 size limit

    /// <summary>A TIFF-headed blob of exactly <paramref name="length"/> bytes (the rest is zero padding).</summary>
    private static byte[] BlobOfLength(int length)
    {
        var blob = new byte[length];
        "II*\0"u8.CopyTo(blob);
        return blob;
    }

    [Fact]
    public void BlobExactlyAtTheLimit_IsAcceptedAndFillsTheSegment()
    {
        JpegEncodeOptions.MaxExifBytes.ShouldBe(65527);
        var jpeg = Encode(BlobOfLength(JpegEncodeOptions.MaxExifBytes));

        jpeg[2].ShouldBe((byte)0xFF);
        jpeg[3].ShouldBe((byte)0xE0); // APP0 first
        var app1 = 2 + 2 + 0x10;
        jpeg[app1].ShouldBe((byte)0xFF);
        jpeg[app1 + 1].ShouldBe((byte)0xE1);
        ((jpeg[app1 + 2] << 8) | jpeg[app1 + 3]).ShouldBe(65535);
        // Segment payload (after the length field) is Exif\0\0 + blob = 65533 bytes.
        (65535 - 2).ShouldBe(65533);
        jpeg.AsSpan(app1 + 4, 6).SequenceEqual("Exif\0\0"u8).ShouldBeTrue();
        // The next marker is the DQT, so nothing overran.
        jpeg[app1 + 2 + 65535].ShouldBe((byte)0xFF);
        jpeg[app1 + 2 + 65535 + 1].ShouldBe((byte)0xDB);
    }

    [Fact]
    public void BlobOneOverTheLimit_IsRefusedBeforeAnyOutput()
    {
        var options = new JpegEncodeOptions { Exif = BlobOfLength(JpegEncodeOptions.MaxExifBytes + 1) };
        var ex = Should.Throw<ArgumentException>(() => JpegEncoder.Encode(Pixels(), W, H, 3, options));
        ex.ParamName.ShouldBe("options");
    }

    [Fact]
    public void LimitIsCheckedBeforeThePixelBufferIsLookedAt()
    {
        // A too-small pixel buffer is also an ArgumentException; the EXIF one must win, i.e. be raised first.
        var options = new JpegEncodeOptions { Exif = BlobOfLength(JpegEncodeOptions.MaxExifBytes + 1) };
        Should.Throw<ArgumentException>(() => JpegEncoder.Encode(new byte[1], W, H, 3, options))
            .ParamName.ShouldBe("options");
    }

    [Fact]
    public void BlobThatIsNotTiff_IsRefused()
    {
        Should.Throw<ArgumentException>(() => Encode(new byte[16]));
        // The signature is the encoder's to add; a blob that already carries it is a mistake worth catching.
        var signed = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 }.Concat(AllFieldsBlob()).ToArray();
        Should.Throw<ArgumentException>(() => Encode(signed));
    }

    // ------------------------------------------------------------------ null changes nothing

    [Fact]
    public void NullExif_LeavesTheBytesIdentical()
    {
        var plain = JpegEncoder.Encode(Pixels(), W, H, 3);
        Encode(null).ShouldBe(plain);
        JpegEncoder.Encode(Pixels(), W, H, 3, new JpegEncodeOptions { Exif = null }).ShouldBe(plain);

        // C# turns a null byte[] into an empty (not a null) ReadOnlyMemory<byte>?: that is unset too.
        byte[]? none = null;
        JpegEncoder.Encode(Pixels(), W, H, 3, new JpegEncodeOptions { Exif = none }).ShouldBe(plain);
        Encode(Array.Empty<byte>()).ShouldBe(plain);
    }

    [Fact]
    public void WithExif_OnlyTheApp1SegmentIsAdded()
    {
        var plain = JpegEncoder.Encode(Pixels(), W, H, 3);
        var blob = AllFieldsBlob();
        var withExif = Encode(blob);

        var inserted = 2 + 2 + 6 + blob.Length; // marker + length + signature + blob
        withExif.Length.ShouldBe(plain.Length + inserted);
        var at = 20; // SOI (2) + APP0 (18)
        withExif.AsSpan(0, at).SequenceEqual(plain.AsSpan(0, at)).ShouldBeTrue();
        withExif.AsSpan(at + inserted).SequenceEqual(plain.AsSpan(at)).ShouldBeTrue();
    }

    [Fact]
    public void ImageStillDecodes_WithTheSegmentPresent()
    {
        var jpeg = Encode(AllFieldsBlob());
        var decoded = JpegDecoder.Decode(jpeg);
        decoded.Width.ShouldBe(W);
        decoded.Height.ShouldBe(H);
    }

    // ------------------------------------------------------------------ an external reader

    /// <summary>
    /// Magick.NET (libjpeg for the container, its own EXIF parser for the profile) 14.13.1, run against the blob above:
    /// ImageDescription, Software, Artist and DateTime read back as the strings written; UserComment arrives as
    /// the raw UNDEFINED bytes, ASCII\0\0\0 prefix included. The non-ASCII case reads back as UNICODE\0 plus
    /// the UTF-16LE bytes, byte for byte.
    /// </summary>
    [Fact]
    public void MagickExifProfile_ReadsTheWrittenFields()
    {
        var jpeg = Encode(AllFieldsBlob());
        using (var image = new MagickImage(jpeg))
        {
            var profile = image.GetExifProfile();
            profile.ShouldNotBeNull();
            profile.GetValue(MagickExifTag.ImageDescription)!.Value.ShouldBe("A short description");
            profile.GetValue(MagickExifTag.Software)!.Value.ShouldBe("Sdcb.SimdCodecs.Jpeg test");
            profile.GetValue(MagickExifTag.Artist)!.Value.ShouldBe("Someone");
            profile.GetValue(MagickExifTag.DateTime)!.Value.ShouldBe("2026:10:25 14:50:23");
            profile.GetValue(MagickExifTag.UserComment)!.Value.ShouldBe(Encoding.ASCII.GetBytes("ASCII\0\0\0plain ascii comment"));
        }

        const string text = "Zażółć 星雲";
        using var unicode = new MagickImage(Encode(ExifWriter.Build(new ExifWriteData(UserComment: text))));
        var uc = unicode.GetExifProfile()!.GetValue(MagickExifTag.UserComment)!.Value;
        uc.AsSpan(0, 8).SequenceEqual("UNICODE\0"u8).ShouldBeTrue();
        Encoding.Unicode.GetString(uc, 8, uc.Length - 8).ShouldBe(text);
    }

    private static string? AsciiOf(ExifMetadata exif, ushort tag)
    {
        if (!exif.RawTags.TryGetValue(tag, out var v)) return null;
        return Encoding.ASCII.GetString(v.Bytes).TrimEnd('\0');
    }
}
