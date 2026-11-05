using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using B = Sdcb.SimdCodecs.Tests.TestTiffBuilder;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// JPEG-compressed TIFF (Compression 7, TIFF Technical Note 2), written by libtiff through Magick.NET: strips and tiles,
/// RGB / YCbCr / grey / CMYK photometrics, chunky and planar, with JPEGTables and with the tables inline. The contract
/// is exact: every segment decodes to what <see cref="JpegDecoder"/> gives for the stream reassembled by hand (the
/// JPEGTables' SOI-to-EOI tables spliced in after the segment's SOI), with the colour interpretation the photometric
/// names, cropped to the image. Against libtiff's own decode (libjpeg's IDCT and upsampling, not ours) a tolerance is
/// asserted, recorded in Oracle/README.md.
/// </summary>
public sealed class TiffJpegReadTests
{
    private const int W = 70;
    private const int H = 45;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Max difference from ImageMagick/libtiff's decode on smooth content; see Oracle/README.md.</summary>
    internal const int MagickTolerance = 3;

    private static byte[] Smooth(int channels)
    {
        var px = new byte[W * H * channels];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        for (var c = 0; c < channels; c++)
            px[(((y * W) + x) * channels) + c] = (byte)(128 + (100 * Math.Sin((x * (0.11 + (c * 0.03))) + (y * 0.07 * (c + 1)))));
        return px;
    }

    public static TheoryData<string, bool, bool, int> Cases() => new()
    {
        { "rgb", false, false, 0 },
        { "rgb", false, false, 16 },
        { "rgb", true, false, 0 },
        { "rgb", false, true, 0 },
        { "rgb", true, true, 0 },
        { "ycbcr", false, false, 0 },
        { "ycbcr", false, false, 16 },
        { "ycbcr", true, false, 0 },
        { "gray", false, false, 0 },
        { "gray", true, false, 0 },
        { "cmyk", false, false, 0 },
        { "cmyk", true, false, 0 },
    };

    private static byte[] Write(string kind, bool tiled, bool planar, int rowsPerStrip)
    {
        using var image = new MagickImage();
        image.ReadPixels(Smooth(3), new PixelReadSettings(W, H, StorageType.Char, "RGB"));
        switch (kind)
        {
            case "gray": image.ColorSpace = ColorSpace.Gray; break;
            case "cmyk": image.ColorSpace = ColorSpace.CMYK; break;
            case "ycbcr": image.ColorSpace = ColorSpace.YCbCr; break;
        }
        image.Settings.Compression = CompressionMethod.JPEG;
        image.Quality = 92;
        image.Settings.Interlace = planar ? Interlace.Plane : Interlace.NoInterlace;
        if (tiled) image.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", "32x16");
        else if (rowsPerStrip > 0) image.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", rowsPerStrip.ToString());
        return image.ToByteArray(MagickFormat.Tiff);
    }

    /// <summary>T.81 B.5 abbreviated tables spliced into an abbreviated image stream: tables' body after the image's SOI.</summary>
    internal static byte[] Reassemble(ReadOnlySpan<byte> tables, ReadOnlySpan<byte> segment)
    {
        if (tables.IsEmpty) return segment.ToArray();
        tables[..2].ToArray().ShouldBe(new byte[] { 0xFF, 0xD8 }, "JPEGTables starts with SOI");
        tables[^2..].ToArray().ShouldBe(new byte[] { 0xFF, 0xD9 }, "JPEGTables ends with EOI");
        segment[..2].ToArray().ShouldBe(new byte[] { 0xFF, 0xD8 }, "a segment starts with SOI");
        return [.. segment[..2], .. tables[2..^2], .. segment[2..]];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LibTiffJpeg_EqualsJpegDecoderOnTheReassembledStreams(string kind, bool tiled, bool planar, int rowsPerStrip)
    {
        var tiff = Write(kind, tiled, planar, rowsPerStrip);
        var layout = TiffReader.ReadLayout(tiff);
        layout.Compression.ShouldBe(TiffCompression.Jpeg, "premise: libtiff wrote JPEG");
        layout.IsTiled.ShouldBe(tiled);
        layout.IsPlanar.ShouldBe(planar && kind != "gray");
        var stored = StoredPhotometric(tiff);
        stored.ShouldBe(kind switch { "gray" => 1, "cmyk" => 5, "ycbcr" => 6, _ => 2 }, "premise: the photometric of the case");
        var transform = kind switch { "ycbcr" => JpegColorTransform.YCbCr, "gray" => JpegColorTransform.Auto, _ => JpegColorTransform.None };
        var channels = layout.SamplesPerPixel;
        var tables = layout.Ifd.Color.JpegTables ?? [];

        // The expected page, segment by segment, from JpegDecoder alone.
        var expected = new byte[W * H * channels];
        var planes = layout.IsPlanar ? channels : 1;
        var perPlane = layout.Offsets.Length / planes;
        for (var plane = 0; plane < planes; plane++)
        for (var s = 0; s < perPlane; s++)
        {
            var index = (plane * perPlane) + s;
            var stream = Reassemble(tables, tiff.AsSpan((int)layout.Offsets[index], (int)layout.ByteCounts[index]));
            var segChannels = layout.IsPlanar ? 1 : channels;
            var info = JpegDecoder.ReadInfo(stream);
            var px = new byte[info.Width * info.Height * segChannels];
            JpegDecoder.DecodeTo(stream, px, info.Width * segChannels,
                segChannels switch { 1 => JpegPixelFormat.Gray8, 3 => JpegPixelFormat.Rgb24, _ => JpegPixelFormat.Rgba32 },
                new JpegDecodeOptions { ColorTransform = transform }, JpegScale.Full, Ct);
            var rect = layout.SegmentRectangle(s);
            for (var y = 0; y < rect.Height; y++)
            for (var x = 0; x < rect.Width; x++)
            for (var c = 0; c < segChannels; c++)
                expected[((((rect.Y + y) * W) + rect.X + x) * channels) + c + (layout.IsPlanar ? plane : 0)] =
                    px[(((y * info.Width) + x) * segChannels) + c];
        }

        var page = TiffReader.ReadPage(tiff, 0, Ct);
        page.Pixels.ShouldBe(expected);

        // Every segment on demand agrees with the whole page.
        for (var s = 0; s < layout.SegmentCount; s++)
        {
            var rect = layout.SegmentRectangle(s);
            var buffer = new byte[layout.SegmentBytes(s)];
            TiffReader.ReadSegment(tiff, layout, s, buffer);
            for (var y = 0; y < rect.Height; y++)
                buffer.AsSpan(y * rect.Width * channels, rect.Width * channels).ToArray()
                    .ShouldBe(expected.AsSpan((((rect.Y + y) * W) + rect.X) * channels, rect.Width * channels).ToArray());
        }

        // The facade, and a documented tolerance against libtiff's decode.
        TiffImageDecoder.TryDecode(tiff, out var decoded, cancellationToken: Ct).ShouldBeTrue();
        decoded!.Channels.ShouldBe(kind == "gray" ? 1 : 3);
        // ImageMagick cannot read back its own tiled CMYK JPEG ("Can not read scanlines from a tiled image").
        if (kind == "cmyk" && tiled) return;
        using var magick = new MagickImage(tiff);
        if (magick.ColorSpace is ColorSpace.CMYK or ColorSpace.YCbCr) magick.ColorSpace = ColorSpace.sRGB;
        using var pixels = magick.GetPixels();
        var theirs = pixels.ToByteArray(kind == "gray" ? PixelMapping.RGB : PixelMapping.RGB)!;
        var ours = decoded.ToRgba8();
        var max = 0;
        for (var i = 0; i < W * H; i++)
        for (var c = 0; c < 3; c++)
            max = Math.Max(max, Math.Abs(ours[(i * 4) + c] - theirs[(i * 3) + c]));
        max.ShouldBeLessThanOrEqualTo(MagickTolerance, "Oracle/README.md: JPEG-in-TIFF vs libtiff/libjpeg");
    }

    [Fact]
    public void AbbreviatedTables_AndAStreamWithItsOwnTables_DecodeAlike()
    {
        // A complete JPEG (with its own tables) placed as the only strip of a page that also carries JPEGTables from a
        // different stream: the strip's own tables replace the shared ones, as T.81 says later tables do.
        var rgb = Smooth(3);
        var jpeg = JpegEncoder.Encode(rgb, W, H, 3, new JpegEncodeOptions { Quality = 90 });
        var other = JpegEncoder.Encode(Smooth(3).Reverse().ToArray(), W, H, 3, new JpegEncodeOptions { Quality = 20 });
        var tags = B.Basic(W, H, 3, 8, TiffCompression.Jpeg, TiffPhotometric.YCbCr);
        tags.Add(B.Bytes(TiffTag.JpegTables, TiffFieldType.Undefined, TablesOf(other)));
        var tiff = new B().AddPage(tags, [jpeg]).Build();

        var direct = JpegDecoder.Decode(jpeg);
        var rgbDirect = new byte[W * H * 3];
        for (var i = 0; i < W * H; i++) direct.Pixels.AsSpan(i * 4, 3).CopyTo(rgbDirect.AsSpan(i * 3));
        TiffReader.ReadPage(tiff, 0, Ct).Pixels.ShouldBe(rgbDirect);
    }

    [Fact]
    public void RgbPhotometric_IsNotColourConverted_WhateverTheStreamSuggests()
    {
        // libtiff's RGB strip, its component IDs rewritten to 1, 2, 3 and any Adobe marker removed: on its own such a
        // stream is taken for YCbCr (T.871), but the page's photometric says RGB, and that is what decides.
        var source = Write("rgb", false, false, 0);
        var layout = TiffReader.ReadLayout(source);
        var tables = layout.Ifd.Color.JpegTables ?? [];
        var strip = Neutralise(source.AsSpan((int)layout.Offsets[0], (int)layout.ByteCounts[0]).ToArray());
        var tags = B.Basic(W, H, 3, 8, TiffCompression.Jpeg, TiffPhotometric.Rgb);
        tags.Add(B.Bytes(TiffTag.JpegTables, TiffFieldType.Undefined, tables));
        var tiff = new B().AddPage(tags, [strip]).Build();

        var stream = Reassemble(tables, strip);
        var asIs = new byte[W * H * 3];
        JpegDecoder.DecodeTo(stream, asIs, W * 3, JpegPixelFormat.Rgb24, new JpegDecodeOptions { ColorTransform = JpegColorTransform.None }, JpegScale.Full, Ct);
        var guessed = new byte[W * H * 3];
        JpegDecoder.DecodeTo(stream, guessed, W * 3, JpegPixelFormat.Rgb24, null, JpegScale.Full, Ct);
        guessed.ShouldNotBe(asIs, "premise: the stream alone is guessed to be YCbCr");

        TiffReader.ReadPage(tiff, 0, Ct).Pixels.ShouldBe(asIs);
        var smooth = Smooth(3);
        var max = 0;
        for (var i = 0; i < smooth.Length; i++) max = Math.Max(max, Math.Abs(smooth[i] - asIs[i]));
        max.ShouldBeLessThanOrEqualTo(12, "the RGB components are the source's, give or take quantisation");
    }

    /// <summary>Component IDs set to 1, 2, 3 in the SOF and the SOS, APP14 segments dropped.</summary>
    private static byte[] Neutralise(byte[] jpeg)
    {
        var output = new List<byte> { 0xFF, 0xD8 };
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] != 0xDA)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
            var segment = jpeg.AsSpan(at, length + 2).ToArray();
            if (segment[1] is 0xC0 or 0xC1 or 0xC2)
                for (var k = 0; k < segment[9]; k++) segment[10 + (k * 3)] = (byte)(k + 1);
            if (segment[1] != 0xEE) output.AddRange(segment);
            at += length + 2;
        }
        // The (single, interleaved) scan header names the components by ID too.
        var sos = jpeg.AsSpan(at).ToArray();
        if (sos.Length > 5 && sos[1] == 0xDA)
            for (var k = 0; k < sos[4]; k++) sos[5 + (k * 2)] = (byte)(k + 1);
        output.AddRange(sos);
        return [.. output];
    }
    [Fact]
    public void OldStyleJpeg_IsRefusedWithAClearMessage()
    {
        var tiff = new B().AddPage(B.Basic(8, 8, 1, 8, TiffCompression.OldJpeg, TiffPhotometric.MinIsBlack), [new byte[16]]).Build();
        Should.Throw<NotSupportedException>(() => TiffReader.Read(tiff)).Message.ShouldContain("Old-style JPEG");
        TiffImageDecoder.TryDecode(tiff, out _, cancellationToken: Ct).ShouldBeFalse();
    }

    [Fact]
    public void ASegmentWiderThanThePage_IsInvalid()
    {
        var jpeg = JpegEncoder.Encode(new byte[64 * 8], 64, 8, 1);
        var tiff = new B().AddPage(B.Basic(32, 8, 1, 8, TiffCompression.Jpeg, TiffPhotometric.MinIsBlack), [jpeg]).Build();
        Should.Throw<InvalidDataException>(() => TiffReader.Read(tiff));
    }

    /// <summary>The DQT / DHT segments of a JPEG as an abbreviated table-specification stream (SOI, tables, EOI).</summary>
    internal static byte[] TablesOf(byte[] jpeg)
    {
        var tables = new List<byte> { 0xFF, 0xD8 };
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] != 0xDA)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
            if (jpeg[at + 1] is 0xDB or 0xC4) tables.AddRange(jpeg.AsSpan(at, length + 2).ToArray());
            at += length + 2;
        }
        tables.Add(0xFF);
        tables.Add(0xD9);
        return [.. tables];
    }

    /// <summary>PhotometricInterpretation as stored in the first IFD of a little-endian classic TIFF (the facade reports a decoded YCbCr JPEG page as RGB).</summary>
    internal static int StoredPhotometric(byte[] tiff)
    {
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(4));
        var n = BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(ifd));
        for (var i = 0; i < n; i++)
        {
            var e = tiff.AsSpan(ifd + 2 + (i * 12));
            if (BinaryPrimitives.ReadUInt16LittleEndian(e) == 262) return BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
        }
        return -1;
    }
}
