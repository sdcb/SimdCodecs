using System.Diagnostics;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The encoder's RIFF container (RFC 9649 Section 2): the simple format when there is nothing but the
/// image, VP8X with the ICCP / EXIF / XMP chunks in their order when there is metadata. Read back by
/// our decoder, by ImageMagick (libwebp's demuxer inside) and by libwebp's <c>webpinfo</c>, which checks
/// every chunk, flag and size.
/// </summary>
public sealed class WebpEncoderContainerTests
{
    // A big-endian TIFF header and one IFD entry: Orientation (0x0112), SHORT, 1 value = 6.
    private static readonly byte[] Exif =
    [
        (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
        0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0,
        0, 0, 0, 0,
        0x55, // odd length: the chunk needs a pad byte
    ];

    private static readonly byte[] Xmp = System.Text.Encoding.UTF8.GetBytes(
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>t</dc:title></rdf:Description>" +
        "</rdf:RDF></x:xmpmeta>");

    private static byte[] Source(bool alpha) => WebpLosslessEncoderTests.Content(alpha ? "gradient-alpha" : "pattern", 45, 21, 3);

    [Fact]
    public void Without_metadata_the_file_is_the_simple_format()
    {
        byte[] webp = WebpEncoder.EncodeLossless(Source(alpha: true), 45, 21, ImagePixelFormat.Rgba32);
        webp.AsSpan(12, 4).SequenceEqual("VP8L"u8).ShouldBeTrue();
        BitConverter.ToUInt32(webp, 4).ShouldBe((uint)(webp.Length - 8));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, false)]
    public void Metadata_goes_into_a_vp8x_file_in_chunk_order(bool icc, bool exif, bool xmp, bool alpha)
    {
        byte[] iccBytes = IccProfiles.SRgbV4.ToArray();
        byte[] source = Source(alpha);
        var options = new WebpLosslessEncodeOptions
        {
            Exact = true,
            IccProfile = icc ? iccBytes : null,
            Exif = exif ? Exif : null,
            Xmp = xmp ? Xmp : null,
        };
        byte[] webp = WebpEncoder.EncodeLossless(source, 45, 21, ImagePixelFormat.Rgba32, options);

        // Chunk walk: order, sizes, padding, and the VP8X flags and canvas.
        List<(string FourCc, int Offset, int Length)> chunks = Chunks(webp);
        string[] expected = new[] { "VP8X", icc ? "ICCP" : null, "VP8L", exif ? "EXIF" : null, xmp ? "XMP " : null }
            .Where(c => c is not null).ToArray()!;
        chunks.Select(c => c.FourCc).ShouldBe(expected);
        byte flags = webp[chunks[0].Offset];
        (flags & 0x20).ShouldBe(icc ? 0x20 : 0, "ICC flag");
        (flags & 0x10).ShouldBe(alpha ? 0x10 : 0, "alpha flag");
        (flags & 0x08).ShouldBe(exif ? 0x08 : 0, "Exif flag");
        (flags & 0x04).ShouldBe(xmp ? 0x04 : 0, "XMP flag");
        (flags & 0xC3).ShouldBe(0, "reserved and animation bits");
        (webp[chunks[0].Offset + 4] | webp[chunks[0].Offset + 5] << 8 | webp[chunks[0].Offset + 6] << 16).ShouldBe(44);
        (webp[chunks[0].Offset + 7] | webp[chunks[0].Offset + 8] << 8 | webp[chunks[0].Offset + 9] << 16).ShouldBe(20);

        // Our decoder: pixels and every metadata block, byte for byte.
        WebpImage decoded = WebpDecoder.Decode(webp);
        decoded.Rgba.ShouldBe(source);
        decoded.IccProfile.ShouldBe(icc ? iccBytes : null);
        decoded.Exif.ShouldBe(exif ? Exif : null);
        decoded.Xmp.ShouldBe(xmp ? Xmp : null);

        // ImageMagick (libwebp's demux): pixels, the ICC profile and the Exif orientation.
        using var image = new MagickImage(webp, MagickFormat.WebP);
        if (icc)
        {
            image.GetColorProfile().ShouldNotBeNull().ToByteArray().ShouldBe(iccBytes);
        }

        if (exif)
        {
            image.GetExifProfile().ShouldNotBeNull().GetValue(ExifTag.Orientation).ShouldNotBeNull().Value.ShouldBe((ushort)6);
        }

        if (xmp)
        {
            image.GetXmpProfile().ShouldNotBeNull().ToByteArray().ShouldBe(Xmp);
        }
    }

    [Fact]
    public void Webpinfo_accepts_every_container_layout()
    {
        string webpinfo = WebpOracle.RequireWebpinfo();
        string directory = Path.Combine(Path.GetTempPath(), "simdcodecs-webp-container");
        Directory.CreateDirectory(directory);
        byte[] iccBytes = IccProfiles.SRgbV4.ToArray();
        var layouts = new (string Name, WebpLosslessEncodeOptions Options, bool Alpha)[]
        {
            ("simple", new(), false),
            ("simple-alpha", new(), true),
            ("all-metadata", new() { IccProfile = iccBytes, Exif = Exif, Xmp = Xmp }, true),
            ("icc", new() { IccProfile = iccBytes }, false),
            ("xmp", new() { Xmp = Xmp }, false),
        };
        foreach ((string name, WebpLosslessEncodeOptions options, bool alpha) in layouts)
        {
            string file = Path.Combine(directory, $"{name}-{Guid.NewGuid():N}.webp");
            File.WriteAllBytes(file, WebpEncoder.EncodeLossless(Source(alpha), 45, 21, ImagePixelFormat.Rgba32, options));
            try
            {
                (int exit, string output) = Run(webpinfo, "-diag", file);
                exit.ShouldBe(0, $"{name}: {output}");
                output.ShouldContain("No error detected", customMessage: $"{name}: {output}");
            }
            finally
            {
                File.Delete(file);
            }
        }
    }

    internal static List<(string FourCc, int Offset, int Length)> Chunks(byte[] webp)
    {
        var list = new List<(string, int, int)>();
        System.Text.Encoding.ASCII.GetString(webp, 0, 4).ShouldBe("RIFF");
        BitConverter.ToUInt32(webp, 4).ShouldBe((uint)(webp.Length - 8), "RIFF size");
        System.Text.Encoding.ASCII.GetString(webp, 8, 4).ShouldBe("WEBP");
        int pos = 12;
        while (pos < webp.Length)
        {
            string fourCc = System.Text.Encoding.ASCII.GetString(webp, pos, 4);
            int length = BitConverter.ToInt32(webp, pos + 4);
            list.Add((fourCc, pos + 8, length));
            if ((length & 1) != 0)
            {
                webp[pos + 8 + length].ShouldBe((byte)0, $"{fourCc} pad byte");
            }

            pos += 8 + length + (length & 1);
        }

        pos.ShouldBe(webp.Length);
        return list;
    }

    internal static (int Exit, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }
}
