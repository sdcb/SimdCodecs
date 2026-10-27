using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// PNG text (<see cref="PngText"/>): the writer emits <c>iTXt</c>, UTF-8 and any length, deflated when long; the
/// reader takes <c>tEXt</c>, <c>zTXt</c> and <c>iTXt</c> alike, so text another tool wrote reads the same way.
/// </summary>
public sealed class PngTextTests
{
    private static readonly byte[] TinyRgba = new byte[4 * 4 * 4];

    [Fact]
    public void ShortTextRoundTripsInOrder()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions
        {
            Text = [new PngText("Software", "tianwen-fits"), new PngText("Source", "master_SculptorGalaxy.fits")],
        });

        var text = PngReader.Decode(png).Text;
        text.Count.ShouldBe(2);
        text[0].ShouldBe(new PngText("Software", "tianwen-fits"));
        text[1].ShouldBe(new PngText("Source", "master_SculptorGalaxy.fits"));
    }

    [Fact]
    public void LongTextIsDeflatedAndRoundTrips()
    {
        // A rendition's settings as JSON can run to kilobytes; past the threshold the chunk is deflated.
        var json = "{\"stretch\":\"Luma\",\"sky\":0.09," + string.Concat(Enumerable.Repeat("\"pad\":\"0123456789\",", 200)) + "\"end\":1}";
        Encoding.UTF8.GetByteCount(json).ShouldBeGreaterThan(PngWriter.TextCompressionThreshold);

        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Text = [new PngText("TianWen:Rendition", json)] });

        PngReader.Decode(png).Text.Single().Text.ShouldBe(json);
        png.Length.ShouldBeLessThan(json.Length, "the text went in deflated");
    }

    [Fact]
    public void UnicodeTextRoundTrips()
    {
        var png = PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Text = [new PngText("Comment", "Sculptor, −25° 17′, ★ 1000")] });

        PngReader.Decode(png).Text.Single().Text.ShouldBe("Sculptor, −25° 17′, ★ 1000");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Leading")]
    [InlineData("Trailing ")]
    [InlineData("Two  spaces")]
    [InlineData("Tab\there")]
    public void AKeywordTheSpecForbidsIsRefused(string keyword)
    {
        PngText.IsValidKeyword(keyword).ShouldBeFalse();
        Should.Throw<ArgumentException>(() =>
            PngWriter.Encode(TinyRgba, 4, 4, new PngWriteOptions { Text = [new PngText(keyword, "x")] }));
    }

    [Fact]
    public void AKeywordOverSeventyNineCharactersIsRefused()
    {
        PngText.IsValidKeyword(new string('k', PngText.MaxKeywordLength)).ShouldBeTrue();
        PngText.IsValidKeyword(new string('k', PngText.MaxKeywordLength + 1)).ShouldBeFalse();
    }

    [Fact]
    public void TextAndZtxtWrittenByAnotherToolAreRead()
    {
        // Built by hand, as libpng writes them: tEXt is keyword, null, Latin-1 text; zTXt is keyword, null, method 0, deflate.
        var deflated = Deflate(Encoding.Latin1.GetBytes("a compressed comment"));
        var png = InsertBeforeIend(PngWriter.Encode(TinyRgba, 4, 4),
            ("tEXt", [.. Encoding.Latin1.GetBytes("Title"), 0, .. Encoding.Latin1.GetBytes("Café")]),
            ("zTXt", [.. Encoding.Latin1.GetBytes("Comment"), 0, 0, .. deflated]));

        var text = PngReader.Decode(png).Text;
        text.ShouldBe([new PngText("Title", "Café"), new PngText("Comment", "a compressed comment")]);
    }

    [Fact]
    public void AMalformedTextChunkIsSkippedNotAFailedDecode()
    {
        // No keyword terminator at all: the chunk is dropped and the image still decodes.
        var png = InsertBeforeIend(PngWriter.Encode(TinyRgba, 4, 4), ("tEXt", Encoding.Latin1.GetBytes("no separator here")));

        var image = PngReader.Decode(png);
        image.Text.ShouldBeEmpty();
        image.Width.ShouldBe(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ItxtWithALanguageTagAndTranslatedKeywordIsRead(bool compressed)
    {
        // Our writer leaves both empty, so build one by hand to walk the reader past a real language tag and
        // translated keyword: keyword, null, flag, method 0, "en", null, "Titel", null, then the text.
        var utf8 = Encoding.UTF8.GetBytes("Sculptor Galaxy ★");
        byte[] itxt = [.. Encoding.Latin1.GetBytes("Title"), 0, compressed ? (byte)1 : (byte)0, 0,
            .. Encoding.ASCII.GetBytes("en"), 0, .. Encoding.UTF8.GetBytes("Titel"), 0, .. compressed ? Deflate(utf8) : utf8];
        var png = InsertBeforeIend(PngWriter.Encode(TinyRgba, 4, 4), ("iTXt", itxt));

        PngReader.Decode(png).Text.ShouldBe([new PngText("Title", "Sculptor Galaxy ★")]);
    }

    [Theory]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    public void TextThatInflatesPastTheCapIsSkipped(string type)
    {
        // A few KB of deflate can claim gigabytes; past the cap the chunk is dropped, like a malformed one.
        var bomb = Deflate(new byte[PngReader.MaxInflatedTextBytes + 1]);
        byte[] data = type == "zTXt"
            ? [.. Encoding.Latin1.GetBytes("Comment"), 0, 0, .. bomb]
            : [.. Encoding.Latin1.GetBytes("Comment"), 0, 1, 0, 0, 0, .. bomb];
        var png = InsertBeforeIend(PngWriter.Encode(TinyRgba, 4, 4), (type, data));

        var image = PngReader.Decode(png);
        image.Text.ShouldBeEmpty();
        image.Width.ShouldBe(4);
    }

    [Fact]
    public void TextExactlyAtTheCapIsRead()
    {
        var png = InsertBeforeIend(PngWriter.Encode(TinyRgba, 4, 4),
            ("zTXt", [.. Encoding.Latin1.GetBytes("Comment"), 0, 0, .. Deflate(new byte[PngReader.MaxInflatedTextBytes])]));

        PngReader.Decode(png).Text.Single().Text.Length.ShouldBe(PngReader.MaxInflatedTextBytes);
    }

    [Fact]
    public void AnIccProfileThatInflatesPastTheCapIsRefused()
    {
        var png = InsertBeforeIend(PngWriter.Encode(TinyRgba, 4, 4),
            ("iCCP", [.. Encoding.Latin1.GetBytes("ICC Profile"), 0, 0, .. Deflate(new byte[PngReader.MaxInflatedIccProfileBytes + 1])]));

        Should.Throw<InvalidDataException>(() => PngReader.Decode(png));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void ItxtWithACompressionFlagOtherThanZeroOrOneIsSkipped(byte flag)
    {
        // The spec allows only 0 (uncompressed) and 1 (deflated); anything else is malformed, not uncompressed.
        var png = InsertBeforeIend(PngWriter.Encode(TinyRgba, 4, 4),
            ("iTXt", [.. Encoding.Latin1.GetBytes("Comment"), 0, flag, 0, 0, 0, .. Encoding.UTF8.GetBytes("plain")]));

        PngReader.Decode(png).Text.ShouldBeEmpty();
    }

    [Fact]
    public void APngWithNoTextHasAnEmptyList()
    {
        PngReader.Decode(PngWriter.Encode(TinyRgba, 4, 4)).Text.ShouldBeEmpty();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }
        return ms.ToArray();
    }

    // Each chunk spliced in just before IEND (the last 12 bytes of a well-formed PNG), with its own CRC.
    private static byte[] InsertBeforeIend(byte[] png, params (string Type, byte[] Data)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(png.AsSpan(0, png.Length - 12));
        Span<byte> buf = stackalloc byte[4];
        foreach (var (type, data) in chunks)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
            ms.Write(buf);
            ms.Write(typeBytes);
            ms.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(buf, Crc32([.. typeBytes, .. data]));
            ms.Write(buf);
        }
        ms.Write(png.AsSpan(png.Length - 12));
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
