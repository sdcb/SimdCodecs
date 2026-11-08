using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Extended XMP in JPEG (XMP Specification Part 3, §1.1.3.1): <c>ImageMetadata.Xmp</c> is the standard packet with the
/// content of the extended serialization's <c>rdf:RDF</c> inserted before its <c>&lt;/rdf:RDF&gt;</c>. A file ExifTool
/// wrote is the foreign case; hand-assembled segments on a Magick.NET JPEG cover chunk order, prefixes, namespace
/// re-declaration and every way the chunks can be wrong, each of which must give the standard packet unchanged.
/// Expected packets are written out from their pieces, never computed by the reader.
/// </summary>
public sealed class JpegExtendedXmpTests
{
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Note = "http://ns.adobe.com/xmp/note/";
    private const string DcUri = "http://purl.org/dc/elements/1.1/";

    private static readonly Lazy<byte[]> BaseJpeg = new(() =>
    {
        var rgb = new byte[16 * 8 * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 13);
        return JpegDecoderOracleTests.EncodeJpeg(rgb, 16, 8);
    });

    /// <summary>~100 KB of text with multi-byte UTF-8 throughout, so chunk boundaries split characters.</summary>
    internal static string LongText(int bytes = 100_000)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (Encoding.UTF8.GetByteCount(sb.ToString()) < bytes - 64)
        {
            for (var k = 0; k < 500; k++, i++)
                sb.Append(i % 3 == 0 ? "星雲" : i % 3 == 1 ? " é " : "x");
        }

        return sb.ToString();
    }

    internal static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data));

    private static byte[] Segment(byte marker, params byte[][] parts)
    {
        var length = 2 + parts.Sum(p => p.Length);
        length.ShouldBeLessThanOrEqualTo(ushort.MaxValue, "one marker segment");
        var s = new byte[2 + length];
        s[0] = 0xFF;
        s[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)length);
        var o = 4;
        foreach (var p in parts) { p.CopyTo(s, o); o += p.Length; }
        return s;
    }

    internal static byte[] StandardSegment(string packet) =>
        Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0"u8.ToArray(), Encoding.UTF8.GetBytes(packet));

    internal static byte[] ExtensionSegment(string guid, uint fullLength, uint offset, ReadOnlySpan<byte> chunk)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, fullLength);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), offset);
        return Segment(0xE1, "http://ns.adobe.com/xmp/extension/\0"u8.ToArray(), Encoding.ASCII.GetBytes(guid), header, chunk.ToArray());
    }

    /// <summary>The extension segments for <paramref name="extended"/>, cut at <paramref name="cuts"/>.</summary>
    internal static List<byte[]> Chunks(string guid, byte[] extended, params int[] cuts)
    {
        var bounds = new[] { 0 }.Concat(cuts).Append(extended.Length).ToArray();
        var list = new List<byte[]>();
        for (var i = 0; i + 1 < bounds.Length; i++)
            list.Add(ExtensionSegment(guid, (uint)extended.Length, (uint)bounds[i], extended.AsSpan(bounds[i], bounds[i + 1] - bounds[i])));
        return list;
    }

    /// <summary>The Magick.NET base JPEG with <paramref name="segments"/> inserted right after SOI.</summary>
    internal static byte[] Jpeg(IEnumerable<byte[]> segments, byte[]? baseJpeg = null)
    {
        var b = baseJpeg ?? BaseJpeg.Value;
        var ms = new MemoryStream();
        ms.Write(b, 0, 2);
        foreach (var s in segments) ms.Write(s);
        ms.Write(b, 2, b.Length - 2);
        return ms.ToArray();
    }

    /// <summary><paramref name="std"/> with <paramref name="content"/> written in just before its one <c>&lt;/rdf:RDF&gt;</c>.</summary>
    private static string Spliced(string std, string content) => std.Replace("</rdf:RDF>", content + "</rdf:RDF>");

    private static string Xmp(byte[] jpeg) => Encoding.UTF8.GetString(JpegMetadata.ReadXmp(jpeg).Span);

    // ---- the hand-assembled packets ---------------------------------------------------------------------------

    private const string StdHead = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n <rdf:RDF xmlns:rdf=\"" + Rdf + "\">\n";
    private const string StdTail = " </rdf:RDF>\n</x:xmpmeta>\n" + "                    \n<?xpacket end=\"w\"?>";

    internal static string StandardPacket(string guid, string notePrefix = "xmpNote") =>
        StdHead + $"  <rdf:Description rdf:about=\"\" xmlns:{notePrefix}=\"{Note}\" {notePrefix}:HasExtendedXMP=\"{guid}\" xmlns:dc=\"{DcUri}\">\n   <dc:format>image/jpeg</dc:format>\n  </rdf:Description>\n" + StdTail;

    private static string ExtendedContent(string text) =>
        $"\n  <rdf:Description rdf:about=\"\" xmlns:dc=\"{DcUri}\">\n   <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{text}</rdf:li></rdf:Alt></dc:description>\n  </rdf:Description>\n ";

    internal static string ExtendedPacket(string text) =>
        $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"{Rdf}\">" + ExtendedContent(text) + "</rdf:RDF></x:xmpmeta>";

    /// <summary>A standard packet and the JPEG carrying it plus <paramref name="extended"/> cut at <paramref name="cuts"/>.</summary>
    private static (string Standard, byte[] Jpeg) Build(byte[] extended, Func<string, string>? standard = null, params int[] cuts)
    {
        var guid = Md5Hex(extended);
        var std = (standard ?? (g => StandardPacket(g)))(guid);
        return (std, Jpeg(new[] { StandardSegment(std) }.Concat(Chunks(guid, extended, cuts))));
    }

    // ---- ExifTool -------------------------------------------------------------------------------------------

    [Fact]
    public void JoinsTheExtendedXmpExifToolWrites()
    {
        var exe = ExifToolOracle.Require();
        var text = LongText();
        var dir = Directory.CreateTempSubdirectory("sdcb-extxmp-");
        try
        {
            var jpegPath = Path.Combine(dir.FullName, "a.jpg");
            var textPath = Path.Combine(dir.FullName, "description.txt");
            File.WriteAllBytes(jpegPath, BaseJpeg.Value);
            File.WriteAllBytes(textPath, Encoding.UTF8.GetBytes(text));
            ExifToolOracle.Run(exe, "-q", "-overwrite_original", "-XMP-dc:Description<=" + textPath, "-XMP-dc:Title=hello", jpegPath);
            var jpeg = File.ReadAllBytes(jpegPath);

            // ExifTool's own segments, read with a walk of the test's own: the standard packet and the chunks by offset.
            string? standard = null;
            var chunks = new SortedDictionary<uint, byte[]>();
            for (var p = 2; jpeg[p + 1] != 0xDA;)
            {
                var length = jpeg[p + 2] << 8 | jpeg[p + 3];
                var payload = jpeg.AsSpan(p + 4, length - 2);
                if (jpeg[p + 1] == 0xE1 && payload.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
                    standard = Encoding.UTF8.GetString(payload[29..]);
                if (jpeg[p + 1] == 0xE1 && payload.StartsWith("http://ns.adobe.com/xmp/extension/\0"u8))
                    chunks.Add(BinaryPrimitives.ReadUInt32BigEndian(payload[(35 + 36)..]), payload[(35 + 40)..].ToArray());
                p += 2 + length;
            }

            standard.ShouldNotBeNull();
            chunks.Count.ShouldBeGreaterThan(1, "ExifTool should have split the packet over several APP1 segments");
            standard.ShouldContain("HasExtendedXMP");
            var extended = Encoding.UTF8.GetString(chunks.Values.SelectMany(c => c).ToArray());

            // Expected: the standard packet with the content of the extended rdf:RDF spliced in before </rdf:RDF>.
            var open = extended.IndexOf("<rdf:RDF", StringComparison.Ordinal);
            var contentStart = extended.IndexOf('>', open) + 1;
            var contentEnd = extended.IndexOf("</rdf:RDF>", StringComparison.Ordinal);
            var insertAt = standard.IndexOf("</rdf:RDF>", StringComparison.Ordinal);
            var expected = standard[..insertAt] + extended[contentStart..contentEnd] + standard[insertAt..];

            Xmp(jpeg).ShouldBe(expected);
            JpegImageDecoder.TryReadMetadata(jpeg, out var metadata).ShouldBeTrue();
            Encoding.UTF8.GetString(metadata.Xmp.Span).ShouldBe(expected);

            // And it means what ExifTool was told: both properties in one parseable packet.
            var doc = XDocument.Parse(Xmp(jpeg));
            XNamespace dc = DcUri, rdf = Rdf;
            doc.Descendants(dc + "description").Single().Descendants(rdf + "li").Single().Value.ShouldBe(text);
            doc.Descendants(dc + "title").Single().Descendants(rdf + "li").Single().Value.ShouldBe("hello");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- joins ------------------------------------------------------------------------------------------------

    [Fact]
    public void ChunksInOrderAreJoined()
    {
        var text = LongText();
        var extended = Encoding.UTF8.GetBytes(ExtendedPacket(text));
        var (std, jpeg) = Build(extended, null, 40_000, 80_001);
        Xmp(jpeg).ShouldBe(Spliced(std, ExtendedContent(text)));
    }

    [Fact]
    public void ChunksOutOfOrderAreJoined()
    {
        var text = LongText();
        var extended = Encoding.UTF8.GetBytes(ExtendedPacket(text));
        var guid = Md5Hex(extended);
        var std = StandardPacket(guid);
        var chunks = Chunks(guid, extended, 40_000, 90_001);
        var jpeg = Jpeg([chunks[2], chunks[0], StandardSegment(std), chunks[1]]);
        Xmp(jpeg).ShouldBe(Spliced(std, ExtendedContent(text)));
    }

    [Fact]
    public void TheNotePrefixIsResolvedByNamespace()
    {
        var extended = Encoding.UTF8.GetBytes(ExtendedPacket("short"));
        var (std, jpeg) = Build(extended, g => StandardPacket(g, notePrefix: "n"), 10);
        Xmp(jpeg).ShouldBe(Spliced(std, ExtendedContent("short")));
    }

    [Fact]
    public void TheElementFormWithAnotherPrefixIsRead()
    {
        var extended = Encoding.UTF8.GetBytes(ExtendedPacket("element form"));
        string Standard(string g) => StdHead + $"  <rdf:Description rdf:about=\"\" xmlns:note=\"{Note}\">\n   <note:HasExtendedXMP>\n    {g}\n   </note:HasExtendedXMP>\n  </rdf:Description>\n" + StdTail;
        var (std, jpeg) = Build(extended, Standard);
        Xmp(jpeg).ShouldBe(Spliced(std, ExtendedContent("element form")));
    }

    [Fact]
    public void ANamespaceBoundOnTheExtendedAncestorsIsDeclaredOnTheInsertedElements()
    {
        // dc and ex are declared on x:xmpmeta / rdf:RDF of the extended serialization only; the first child declares
        // ex itself and gets dc alone, the second gets both, in document order and in their original bytes.
        var extended = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x='adobe:ns:meta/' xmlns:ex='urn:ex'><rdf:RDF xmlns:rdf='" + Rdf + "' xmlns:dc = \"" + DcUri + "\">" +
            "<rdf:Description xmlns:ex='urn:ex' rdf:about=''><ex:a>1</ex:a><dc:subject>s</dc:subject></rdf:Description>" +
            "<rdf:Description rdf:about=''><ex:b>2</ex:b></rdf:Description>" +
            "</rdf:RDF></x:xmpmeta>");
        string Standard(string g) => StdHead + $"  <rdf:Description rdf:about=\"\" xmlns:xmpNote=\"{Note}\" xmpNote:HasExtendedXMP=\"{g}\"/>\n" + StdTail;
        var (std, jpeg) = Build(extended, Standard);

        var inserted =
            "<rdf:Description xmlns:dc = \"" + DcUri + "\" xmlns:ex='urn:ex' rdf:about=''><ex:a>1</ex:a><dc:subject>s</dc:subject></rdf:Description>" +
            "<rdf:Description xmlns:ex='urn:ex' xmlns:dc = \"" + DcUri + "\" rdf:about=''><ex:b>2</ex:b></rdf:Description>";
        var joined = Xmp(jpeg);
        joined.ShouldBe(Spliced(std, inserted));

        var doc = XDocument.Parse(joined);
        doc.Descendants(XName.Get("a", "urn:ex")).Single().Value.ShouldBe("1");
        doc.Descendants(XName.Get("b", "urn:ex")).Single().Value.ShouldBe("2");
        doc.Descendants(XName.Get("subject", DcUri)).Single().Value.ShouldBe("s");
    }

    [Fact]
    public void APrefixTheStandardPacketBindsDifferentlyIsRebound()
    {
        var extended = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf + "\" xmlns:p=\"urn:b\">" +
            "<rdf:Description rdf:about=\"\"><p:v>from b</p:v></rdf:Description></rdf:RDF></x:xmpmeta>");
        string Standard(string g) =>
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns=\"urn:default\">\n <rdf:RDF xmlns:rdf=\"" + Rdf + "\" xmlns:p=\"urn:a\">\n" +
            $"  <rdf:Description rdf:about=\"\" xmlns:xmpNote=\"{Note}\" xmpNote:HasExtendedXMP=\"{g}\"><p:v>from a</p:v></rdf:Description>\n" +
            " </rdf:RDF>\n</x:xmpmeta>";
        var (std, jpeg) = Build(extended, Standard);

        // p is re-bound to urn:b, and the standard packet's default namespace is undeclared for the inserted content.
        var expected = Spliced(std, "<rdf:Description xmlns:p=\"urn:b\" xmlns=\"\" rdf:about=\"\"><p:v>from b</p:v></rdf:Description>");
        var joined = Xmp(jpeg);
        joined.ShouldBe(expected);
        var doc = XDocument.Parse(joined);
        doc.Descendants(XName.Get("v", "urn:a")).Single().Value.ShouldBe("from a");
        doc.Descendants(XName.Get("v", "urn:b")).Single().Value.ShouldBe("from b");
    }

    [Fact]
    public void ReDeclarationsThatWouldMultiplyThePacketGiveTheStandardPacket()
    {
        // One 3000-byte ancestor declaration, repeated onto 3000 children, would grow ~60 KB into ~9 MB.
        var extended = Encoding.UTF8.GetBytes(
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:big=\"urn:" + new string('a', 3000) + "\"><rdf:RDF xmlns:rdf=\"" + Rdf + "\">" +
            string.Concat(Enumerable.Repeat("<rdf:Description/>", 3000)) + "</rdf:RDF></x:xmpmeta>");
        var (std, jpeg) = Build(extended, null, 30_000);
        Xmp(jpeg).ShouldBe(std);
    }

    [Fact]
    public void WithoutExtensionSegmentsThePacketIsUnchanged()
    {
        var std = StandardPacket(new string('A', 32));
        Xmp(Jpeg([StandardSegment(std)])).ShouldBe(std);
    }

    // ---- everything that must leave the standard packet alone -------------------------------------------------

    public static TheoryData<string> Broken =>
    [
        "other GUID", "MD5 mismatch", "missing chunk", "overlapping chunk", "chunk past the end", "full length over cap",
        "full length zero", "full lengths disagree", "truncated chunk header", "empty chunk", "no HasExtendedXMP",
        "two different HasExtendedXMP", "GUID not hex", "DOCTYPE in extended", "unbalanced extended", "no rdf:RDF in extended",
        "two rdf:RDF in extended", "self-closing standard rdf:RDF", "unknown entity in GUID",
    ];

    [Theory]
    [MemberData(nameof(Broken))]
    public void ABrokenExtensionGivesTheStandardPacket(string how)
    {
        var text = LongText(20_000);
        var packet = ExtendedPacket(text);
        packet = how switch
        {
            "DOCTYPE in extended" => "<!DOCTYPE x>" + packet,
            "unbalanced extended" => packet.Replace("</rdf:Alt>", ""),
            "no rdf:RDF in extended" => packet.Replace("rdf:RDF", "rdf:Bag"),
            "two rdf:RDF in extended" => packet + "<rdf:RDF xmlns:rdf=\"" + Rdf + "\"/>",
            _ => packet,
        };
        var extended = Encoding.UTF8.GetBytes(packet);
        var guid = Md5Hex(extended);
        var std = how switch
        {
            "no HasExtendedXMP" => StdHead + StdTail,
            "two different HasExtendedXMP" => StandardPacket(guid).Replace("<dc:format>", $"<xmpNote:HasExtendedXMP>{new string('B', 32)}</xmpNote:HasExtendedXMP><dc:format>"),
            "GUID not hex" => StandardPacket(guid[..31] + "G"),
            "self-closing standard rdf:RDF" => "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf + $"\" xmlns:xmpNote=\"{Note}\" xmpNote:HasExtendedXMP=\"{guid}\"/></x:xmpmeta>",
            "unknown entity in GUID" => StandardPacket("&nbsp;" + guid[6..]),
            _ => StandardPacket(guid),
        };
        var chunkGuid = how is "other GUID" or "GUID not hex" ? Md5Hex([1, 2, 3]) : guid;
        if (how == "GUID not hex") chunkGuid = guid[..31] + "G";

        var n = (uint)extended.Length;
        var segments = new List<byte[]> { StandardSegment(std) };
        switch (how)
        {
            case "MD5 mismatch":
                var tampered = (byte[])extended.Clone();
                tampered[tampered.Length / 2] ^= 1; // inside the text: still well-formed XML, only the digest tells
                segments.AddRange(Chunks(guid, tampered, 7_000));
                break;
            case "missing chunk":
                segments.AddRange(Chunks(guid, extended, 7_000, 14_000).Where((_, i) => i != 1));
                break;
            case "overlapping chunk":
                segments.Add(ExtensionSegment(guid, n, 0, extended.AsSpan(0, 7_001)));
                segments.Add(ExtensionSegment(guid, n, 7_000, extended.AsSpan(7_000)));
                break;
            case "chunk past the end":
                segments.Add(ExtensionSegment(guid, n - 1, 0, extended));
                break;
            case "full length over cap":
                segments.Add(ExtensionSegment(guid, 16u << 20 | 1, 0, extended));
                break;
            case "full length zero":
                segments.Add(ExtensionSegment(guid, 0, 0, extended));
                break;
            case "full lengths disagree":
                segments.Add(ExtensionSegment(guid, n, 0, extended.AsSpan(0, 7_000)));
                segments.Add(ExtensionSegment(guid, n + 1, 7_000, extended.AsSpan(7_000)));
                break;
            case "truncated chunk header":
                segments.AddRange(Chunks(guid, extended));
                segments.Add(Segment(0xE1, "http://ns.adobe.com/xmp/extension/\0"u8.ToArray(), Encoding.ASCII.GetBytes(guid), [0, 0, 0]));
                break;
            case "empty chunk":
                segments.AddRange(Chunks(guid, extended));
                segments.Add(ExtensionSegment(guid, n, n, []));
                break;
            default:
                segments.AddRange(Chunks(chunkGuid, extended, 9_999));
                break;
        }

        var jpeg = Jpeg(segments);
        Should.NotThrow(() => JpegMetadata.ReadXmp(jpeg));
        Xmp(jpeg).ShouldBe(std, how);
    }

    [Fact]
    public void AStreamThatIsNotAJpegHasNoXmp() =>
        JpegMetadata.ReadXmp([0xFF, 0xD8, 0xFF]).IsEmpty.ShouldBeTrue();

    // ---- MD5 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Md5MatchesTheRfcTestSuite()
    {
        // RFC 1321 Appendix A.5.
        (string Input, string Digest)[] suite =
        [
            ("", "d41d8cd98f00b204e9800998ecf8427e"),
            ("a", "0cc175b9c0f1b6a831c399e269772661"),
            ("abc", "900150983cd24fb0d6963f7d28e17f72"),
            ("message digest", "f96b697d7cb7938d525a2f31aaf161d0"),
            ("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b"),
            ("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "d174ab98d277d9f5a5611c2c9f419d9f"),
            ("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "57edf4a22be3c955ac49da2e2107b67a"),
        ];
        var digest = new byte[16];
        foreach (var (input, expected) in suite)
        {
            JpegMd5.HashData(Encoding.ASCII.GetBytes(input), digest);
            Convert.ToHexStringLower(digest).ShouldBe(expected, input);
        }
    }

    [Fact]
    public void Md5MatchesTheSystemImplementationAtEveryPaddingLength()
    {
        var rng = new Random(1321);
        var digest = new byte[16];
        foreach (var length in Enumerable.Range(0, 300).Append(65_458).Append(1 << 20))
        {
            var data = new byte[length];
            rng.NextBytes(data);
            JpegMd5.HashData(data, digest);
            digest.ShouldBe(MD5.HashData(data), $"length {length}");
        }
    }
}
