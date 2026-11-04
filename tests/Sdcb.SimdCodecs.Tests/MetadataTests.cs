using System.Buffers.Binary;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Exif;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Metadata through the facade: Exif, XMP and ICC bytes written by the encoders come back from
/// <see cref="SimdCodecs.TryReadMetadata"/> and from a decoded image's <c>Metadata</c> exactly; <see cref="ImageInfo"/> reports
/// resolution (PNG pHYs, JFIF density, Exif resolution tags, BMP pixels per metre), colour encoding (cICP), orientation and
/// the Exif thumbnail; and a malformed Exif block never makes a reader throw.
/// </summary>
public sealed class MetadataTests
{
    private const int W = 24, H = 16;

    private static byte[] Rgb(int seed = 1)
    {
        var pixels = new byte[W * H * 3];
        var random = new Random(seed);
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(i * 5 + random.Next(20));
        return pixels;
    }

    private static byte[] Rgba(int seed = 1)
    {
        var pixels = new byte[W * H * 4];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    // ---- hand-built Exif (TIFF) blobs --------------------------------------------------------------------------

    internal static byte[] ExifBlock(bool littleEndian = true, int? orientation = null, (uint X, uint Y, int Unit)? resolution = null, byte[]? thumbnail = null)
    {
        void U16(Span<byte> d, int v) { if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)v); }
        void U32(Span<byte> d, uint v) { if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(d, v); else BinaryPrimitives.WriteUInt32BigEndian(d, v); }

        var entries = new List<(int Tag, int Type, uint Count, uint Value, bool Short)>();
        var hasRes = resolution is not null;
        var n0 = (orientation is null ? 0 : 1) + (hasRes ? 3 : 0);
        var ifd0Size = 2 + 12 * n0 + 4;
        var ifd1At = 8 + ifd0Size;
        var ifd1Size = thumbnail is null ? 0 : 2 + 12 * 2 + 4;
        var dataAt = ifd1At + ifd1Size;
        var rationalAt = dataAt;
        var thumbAt = dataAt + (hasRes ? 16 : 0);

        if (orientation is { } o)
            entries.Add((0x0112, 3, 1, (uint)o, true));
        if (resolution is { } r)
        {
            entries.Add((0x011A, 5, 1, (uint)rationalAt, false));
            entries.Add((0x011B, 5, 1, (uint)rationalAt + 8, false));
            entries.Add((0x0128, 3, 1, (uint)r.Unit, true));
        }

        var blob = new byte[thumbAt + (thumbnail?.Length ?? 0)];
        blob[0] = blob[1] = littleEndian ? (byte)'I' : (byte)'M';
        U16(blob.AsSpan(2), 42);
        U32(blob.AsSpan(4), 8);
        U16(blob.AsSpan(8), n0);
        for (var i = 0; i < n0; i++)
        {
            var e = blob.AsSpan(10 + 12 * i, 12);
            U16(e, entries[i].Tag);
            U16(e[2..], entries[i].Type);
            U32(e[4..], entries[i].Count);
            if (entries[i].Short) U16(e[8..], (int)entries[i].Value); else U32(e[8..], entries[i].Value);
        }
        U32(blob.AsSpan(8 + 2 + 12 * n0), thumbnail is null ? 0u : (uint)ifd1At);
        if (resolution is { } res)
        {
            U32(blob.AsSpan(rationalAt), res.X);
            U32(blob.AsSpan(rationalAt + 4), 1);
            U32(blob.AsSpan(rationalAt + 8), res.Y);
            U32(blob.AsSpan(rationalAt + 12), 1);
        }
        if (thumbnail is not null)
        {
            U16(blob.AsSpan(ifd1At), 2);
            var e0 = blob.AsSpan(ifd1At + 2, 12);
            U16(e0, 0x0201); U16(e0[2..], 4); U32(e0[4..], 1); U32(e0[8..], (uint)thumbAt);
            var e1 = blob.AsSpan(ifd1At + 14, 12);
            U16(e1, 0x0202); U16(e1[2..], 4); U32(e1[4..], 1); U32(e1[8..], (uint)thumbnail.Length);
            U32(blob.AsSpan(ifd1At + 26), 0);
            thumbnail.CopyTo(blob, thumbAt);
        }
        return blob;
    }

    private static byte[] JpegWith(byte[]? exif = null, JpegXmp? xmp = null, byte[]? icc = null, int seed = 1) =>
        JpegEncoder.Encode(Rgb(seed), W, H, 3, new JpegEncodeOptions { Exif = exif, Xmp = xmp, IccProfile = icc });

    private static JpegXmp Xmp() => new() { CreatorTool = "Sdcb.SimdCodecs tests", Description = "A description with ünïcödé — 星雲" };

    private static byte[] FakeProfile(int length, int seed)
    {
        var profile = new byte[length];
        new Random(seed).NextBytes(profile);
        return profile;
    }

    // ---- JPEG --------------------------------------------------------------------------------------------------

    [Fact]
    public void JpegExifXmpAndIccRoundTrip()
    {
        var exif = ExifWriter.Build(new ExifWriteData(Software: "tests", ImageDescription: "round trip", UserComment: "hello"));
        var xmp = Xmp();
        var icc = IccProfiles.SRgbV4.ToArray();
        var data = JpegWith(exif, xmp, icc);

        SimdCodecs.TryReadMetadata(data, out var metadata).ShouldBeTrue();
        metadata.Exif.ToArray().ShouldBe(exif);
        metadata.Exif.Span[..4].ToArray().ShouldBe("II*\0"u8.ToArray());
        metadata.Xmp.ToArray().ShouldBe(xmp.BuildPacket());
        metadata.Icc.ToArray().ShouldBe(icc);
        metadata.IsEmpty.ShouldBeFalse();

        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            image.Metadata.Exif.ToArray().ShouldBe(exif);
            image.Metadata.Xmp.ToArray().ShouldBe(xmp.BuildPacket());
            image.Metadata.Icc.ToArray().ShouldBe(icc);
            image.Metadata.Icc.Span.ToArray().ShouldBe(icc);
        }

        // The Exif package reads the same bytes the metadata carries.
        ExifReader.FromTiff(metadata.Exif.Span)!.Software.ShouldBe("tests");
    }

    [Fact]
    public void JpegBigEndianExifIsKeptByteForByte()
    {
        var exif = ExifBlock(littleEndian: false, orientation: 3);
        SimdCodecs.TryReadMetadata(JpegWith(exif), out var metadata).ShouldBeTrue();
        metadata.Exif.ToArray().ShouldBe(exif);
        metadata.Xmp.IsEmpty.ShouldBeTrue();
        metadata.Icc.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AMultiSegmentJpegIccProfileComesBackWhole()
    {
        var icc = FakeProfile(2 * JpegEncodeOptions.MaxIccChunkBytes + 17, 5);
        var data = JpegWith(icc: icc);
        SimdCodecs.TryReadMetadata(data, out var metadata).ShouldBeTrue();
        metadata.Icc.ToArray().ShouldBe(icc);
        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
            image.Metadata.Icc.ToArray().ShouldBe(icc);
    }

    [Fact]
    public void AJpegWithoutMetadataHasNone()
    {
        var data = JpegWith();
        SimdCodecs.TryReadMetadata(data, out var metadata).ShouldBeTrue();
        metadata.IsEmpty.ShouldBeTrue();
        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            image.Metadata.IsEmpty.ShouldBeTrue();
            image.Metadata.Icc.Span.IsEmpty.ShouldBeTrue();
        }
    }

    // ---- PNG ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PngExifXmpAndIccRoundTrip()
    {
        var exif = ExifWriter.Build(new ExifWriteData(Software: "png tests", DateTime: new DateTime(2026, 11, 4, 10, 20, 30)));
        var packet = new JpegXmp { CreatorTool = "png", Description = new string('x', 100) + " é — 星" }.BuildPacket();
        var icc = IccProfiles.SRgbV4.ToArray();
        var data = PngWriter.Encode(Rgba(), W, H, new PngWriteOptions
        {
            Exif = exif,
            IccProfile = icc,
            Text = [new PngText("XML:com.adobe.xmp", Encoding.UTF8.GetString(packet))],
        });

        SimdCodecs.TryReadMetadata(data, out var metadata).ShouldBeTrue();
        metadata.Exif.ToArray().ShouldBe(exif);
        metadata.Xmp.ToArray().ShouldBe(packet);
        metadata.Icc.ToArray().ShouldBe(icc);

        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            image.Metadata.Exif.ToArray().ShouldBe(exif);
            image.Metadata.Xmp.ToArray().ShouldBe(packet);
            image.Metadata.Icc.ToArray().ShouldBe(icc);
            image.Metadata.Icc.Span.ToArray().ShouldBe(icc);
        }
    }

    [Fact]
    public void ALargePngXmpPacketSurvivesTheCompressedTextChunk()
    {
        var packet = new JpegXmp { Description = string.Concat(Enumerable.Range(0, 3000).Select(i => (char)('a' + i * 7 % 26))) }.BuildPacket();
        var data = PngWriter.Encode(Rgba(), W, H, new PngWriteOptions { Text = [new PngText("XML:com.adobe.xmp", Encoding.UTF8.GetString(packet))] });
        SimdCodecs.TryReadMetadata(data, out var metadata).ShouldBeTrue();
        metadata.Xmp.ToArray().ShouldBe(packet);
    }

    [Fact]
    public void APngWithoutMetadataHasNone()
    {
        SimdCodecs.TryReadMetadata(PngWriter.Encode(Rgba(), W, H), out var metadata).ShouldBeTrue();
        metadata.IsEmpty.ShouldBeTrue();
    }

    // ---- WebP --------------------------------------------------------------------------------------------------

    [Fact]
    public void WebpLosslessExifXmpAndIccRoundTrip()
    {
        var exif = ExifWriter.Build(new ExifWriteData(Software: "webp tests", Artist: "someone"));
        var packet = Xmp().BuildPacket();
        var icc = IccProfiles.SRgbV4.ToArray();
        var data = WebpEncoder.EncodeLossless(Rgba(), W, H, PixelFormat.Rgba32, new WebpLosslessEncodeOptions { Exif = exif, Xmp = packet, IccProfile = icc });

        SimdCodecs.TryReadMetadata(data, out var metadata).ShouldBeTrue();
        metadata.Exif.ToArray().ShouldBe(exif);
        metadata.Xmp.ToArray().ShouldBe(packet);
        metadata.Icc.ToArray().ShouldBe(icc);

        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        {
            image.Metadata.Exif.ToArray().ShouldBe(exif);
            image.Metadata.Xmp.ToArray().ShouldBe(packet);
            image.Metadata.Icc.ToArray().ShouldBe(icc);
            image.Metadata.Icc.Span.ToArray().ShouldBe(icc);
        }
    }

    [Fact]
    public void WebpWithOnlyOneKindOfMetadataHasOnlyThat()
    {
        var packet = Xmp().BuildPacket();
        SimdCodecs.TryReadMetadata(WebpEncoder.EncodeLossless(Rgba(), W, H, PixelFormat.Rgba32, new WebpLosslessEncodeOptions { Xmp = packet }), out var metadata).ShouldBeTrue();
        metadata.Xmp.ToArray().ShouldBe(packet);
        metadata.Exif.IsEmpty.ShouldBeTrue();
        metadata.Icc.IsEmpty.ShouldBeTrue();

        SimdCodecs.TryReadMetadata(WebpEncoder.EncodeLossless(Rgba(), W, H, PixelFormat.Rgba32), out var none).ShouldBeTrue();
        none.IsEmpty.ShouldBeTrue();
    }

    // ---- resolution --------------------------------------------------------------------------------------------

    [Fact]
    public void PngPhysicalDimensionsAreResolutionPerMeter()
    {
        var data = PngWriter.Encode(Rgba(), W, H, new PngWriteOptions { PhysicalDimensions = new PngPhysicalDimensions(11811, 5906, true) });
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.Resolution.ShouldBe(new Resolution(11811, 5906, ResolutionUnit.PerMeter));
        info.Resolution!.Value.DpiX.ShouldBe(11811 * 0.0254);
        info.Resolution!.Value.DpiY.ShouldBe(5906 * 0.0254);

        var fromDpi = PngWriter.Encode(Rgba(), W, H, new PngWriteOptions { PhysicalDimensions = PngPhysicalDimensions.FromDpi(300) });
        SimdCodecs.TryReadInfo(fromDpi, out var dpiInfo).ShouldBeTrue();
        dpiInfo.Resolution.ShouldBe(new Resolution(11811, 11811, ResolutionUnit.PerMeter));
    }

    [Fact]
    public void PngPhysicalDimensionsWithoutAUnitAreAnAspectRatio()
    {
        var data = PngWriter.Encode(Rgba(), W, H, new PngWriteOptions { PhysicalDimensions = new PngPhysicalDimensions(2, 1, false) });
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.Resolution.ShouldBe(new Resolution(2, 1, ResolutionUnit.AspectRatio));
        info.Resolution!.Value.DpiX.ShouldBeNull();
        info.Resolution!.Value.DpiY.ShouldBeNull();
    }

    [Fact]
    public void APngWithoutPhysChunkHasNoResolution()
    {
        SimdCodecs.TryReadInfo(PngWriter.Encode(Rgba(), W, H), out var info).ShouldBeTrue();
        info.Resolution.ShouldBeNull();
    }

    /// <summary>Puts a JFIF APP0 segment (version 1.01, the given density) straight after SOI, ahead of any the encoder wrote.</summary>
    private static byte[] WithJfifDensity(byte[] jpeg, byte units, ushort x, ushort y)
    {
        byte[] app0 = [0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, units, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 0, 0];
        return [.. jpeg[..2], .. app0, .. jpeg[2..]];
    }

    [Fact]
    public void JfifDensityIsTheResolution()
    {
        var jpeg = JpegWith();
        SimdCodecs.TryReadInfo(WithJfifDensity(jpeg, 1, 300, 150), out var inch).ShouldBeTrue();
        inch.Resolution.ShouldBe(new Resolution(300, 150, ResolutionUnit.PerInch));
        inch.Resolution!.Value.DpiX.ShouldBe(300);
        inch.Resolution!.Value.DpiY.ShouldBe(150);

        SimdCodecs.TryReadInfo(WithJfifDensity(jpeg, 2, 118, 118), out var cm).ShouldBeTrue();
        cm.Resolution.ShouldBe(new Resolution(118, 118, ResolutionUnit.PerCentimeter));
        cm.Resolution!.Value.DpiX.ShouldBe(118 * 2.54);

        SimdCodecs.TryReadInfo(WithJfifDensity(jpeg, 0, 4, 3), out var ratio).ShouldBeTrue();
        ratio.Resolution.ShouldBe(new Resolution(4, 3, ResolutionUnit.AspectRatio));
        ratio.Resolution!.Value.DpiX.ShouldBeNull();

        // Pixel aspect 1:1 with no unit says nothing; so does a zero density.
        SimdCodecs.TryReadInfo(WithJfifDensity(jpeg, 0, 1, 1), out var square).ShouldBeTrue();
        square.Resolution.ShouldBeNull();
        SimdCodecs.TryReadInfo(WithJfifDensity(jpeg, 1, 0, 300), out var zero).ShouldBeTrue();
        zero.Resolution.ShouldBeNull();

        // The image still decodes the same.
        SimdCodecs.TryDecode(WithJfifDensity(jpeg, 1, 300, 150), out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        SimdCodecs.TryDecode(jpeg, out var plain, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
        using (plain)
            image.Pixels.ToArray().ShouldBe(plain.Pixels.ToArray());
    }

    [Fact]
    public void ExifResolutionTagsAreTheResolutionWhenJfifSaysNothing()
    {
        // The encoder's own JFIF header carries no real density, so the Exif tags decide.
        SimdCodecs.TryReadInfo(JpegWith(ExifBlock(resolution: (72, 96, 2))), out var inch).ShouldBeTrue();
        inch.Resolution.ShouldBe(new Resolution(72, 96, ResolutionUnit.PerInch));

        SimdCodecs.TryReadInfo(JpegWith(ExifBlock(littleEndian: false, resolution: (118, 118, 3))), out var cm).ShouldBeTrue();
        cm.Resolution.ShouldBe(new Resolution(118, 118, ResolutionUnit.PerCentimeter));

        SimdCodecs.TryReadInfo(JpegWith(ExifBlock(orientation: 1)), out var none).ShouldBeTrue();
        none.Resolution.ShouldBeNull();
    }

    [Fact]
    public void JfifTakesPrecedenceOverExif()
    {
        var jpeg = WithJfifDensity(JpegWith(ExifBlock(resolution: (72, 72, 2))), 1, 300, 300);
        SimdCodecs.TryReadInfo(jpeg, out var info).ShouldBeTrue();
        info.Resolution.ShouldBe(new Resolution(300, 300, ResolutionUnit.PerInch));
    }

    [Fact]
    public void BmpPixelsPerMeterAreTheResolution()
    {
        var data = Bmp.BmpEncoder.Encode(Rgba(), W, H, PixelFormat.Rgba32, new Bmp.BmpEncodeOptions { PixelsPerMeterX = 3780, PixelsPerMeterY = 7560 });
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.Resolution.ShouldBe(new Resolution(3780, 7560, ResolutionUnit.PerMeter));

        SimdCodecs.TryReadInfo(Bmp.BmpEncoder.Encode(Rgba(), W, H, PixelFormat.Rgba32), out var defaults).ShouldBeTrue();
        defaults.Resolution.ShouldBe(new Resolution(2835, 2835, ResolutionUnit.PerMeter));
        defaults.Resolution!.Value.DpiX.ShouldBe(2835 * 0.0254);

        var unset = Bmp.BmpEncoder.Encode(Rgba(), W, H, PixelFormat.Rgba32, new Bmp.BmpEncodeOptions { PixelsPerMeterX = 0, PixelsPerMeterY = 0 });
        SimdCodecs.TryReadInfo(unset, out var none).ShouldBeTrue();
        none.Resolution.ShouldBeNull();
    }

    // ---- orientation and colour encoding -----------------------------------------------------------------------

    [Theory]
    [InlineData(1, ImageOrientation.Normal)]
    [InlineData(3, ImageOrientation.Rotate180)]
    [InlineData(6, ImageOrientation.Rotate90)]
    [InlineData(8, ImageOrientation.Rotate270)]
    [InlineData(5, ImageOrientation.Transpose)]
    public void ExifOrientationIsReportedForJpegAndPng(int exifValue, ImageOrientation expected)
    {
        foreach (var littleEndian in new[] { true, false })
        {
            var exif = ExifBlock(littleEndian, orientation: exifValue);
            SimdCodecs.TryReadInfo(JpegWith(exif), out var jpeg).ShouldBeTrue();
            jpeg.Orientation.ShouldBe(expected);
            SimdCodecs.TryReadInfo(PngWriter.Encode(Rgba(), W, H, new PngWriteOptions { Exif = exif }), out var png).ShouldBeTrue();
            png.Orientation.ShouldBe(expected);
            jpeg.SwapsDimensions.ShouldBe(expected >= ImageOrientation.Transpose);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(65535)]
    public void AnOutOfRangeExifOrientationIsNormal(int exifValue)
    {
        var exif = ExifBlock(orientation: exifValue);
        SimdCodecs.TryReadInfo(JpegWith(exif), out var jpeg).ShouldBeTrue();
        jpeg.Orientation.ShouldBe(ImageOrientation.Normal);
        SimdCodecs.TryReadInfo(PngWriter.Encode(Rgba(), W, H, new PngWriteOptions { Exif = exif }), out var png).ShouldBeTrue();
        png.Orientation.ShouldBe(ImageOrientation.Normal);
    }

    [Fact]
    public void InfoColorEncodingIsTheDecodedImagesForPngWithCicp()
    {
        (CicpChunk Chunk, ColorPrimaries Primaries, TransferFunction Transfer)[] cases =
        [
            (CicpChunk.SrgbPq, ColorPrimaries.BT709, TransferFunction.Pq),
            (CicpChunk.Hdr10Pq, ColorPrimaries.BT2020, TransferFunction.Pq),
            (CicpChunk.Bt2020Hlg, ColorPrimaries.BT2020, TransferFunction.Hlg),
            (CicpChunk.Srgb, ColorPrimaries.BT709, TransferFunction.Srgb),
        ];
        foreach (var (chunk, primaries, transfer) in cases)
        {
            var samples = new ushort[W * H * 4];
            new Random(3).NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan()));
            var data = PngWriter.EncodeRgba16(samples, W, H, new PngWriteOptions { Cicp = chunk });

            SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
            SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            using (image)
            {
                info.ColorEncoding.ShouldBe(image.ColorEncoding, $"{transfer}");
                info.ColorEncoding.Primaries.ShouldBe(primaries);
                info.ColorEncoding.Transfer.ShouldBe(transfer);
                info.ColorEncoding.FullRange.ShouldBeTrue();
            }
        }
    }

    [Fact]
    public void AnUntaggedPngIsAssumedSrgbInBoth()
    {
        var data = PngWriter.Encode(Rgba(), W, H);
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
        SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (image)
            image.ColorEncoding.ShouldBe(info.ColorEncoding);
    }

    // ---- thumbnails --------------------------------------------------------------------------------------------

    [Fact]
    public void AnExifThumbnailIsReportedAndDecoded()
    {
        var thumbPixels = new byte[8 * 6 * 3];
        for (var i = 0; i < thumbPixels.Length; i++) thumbPixels[i] = (byte)(i * 11);
        var thumbJpeg = JpegEncoder.Encode(thumbPixels, 8, 6, 3, new JpegEncodeOptions { Quality = 70 });
        foreach (var littleEndian in new[] { true, false })
        {
            var data = JpegWith(ExifBlock(littleEndian, orientation: 1, thumbnail: thumbJpeg));

            SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
            info.HasThumbnail.ShouldBeTrue();
            (info.Width, info.Height).ShouldBe((W, H));

            SimdCodecs.TryDecodeThumbnail(data, out var thumbnail).ShouldBeTrue();
            SimdCodecs.TryDecode(thumbJpeg, out var expected, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            using (thumbnail)
            using (expected)
            {
                (thumbnail.Width, thumbnail.Height).ShouldBe((8, 6));
                (thumbnail.Channels, thumbnail.SampleFormat).ShouldBe((expected.Channels, expected.SampleFormat));
                thumbnail.Pixels.ToArray().ShouldBe(expected.Pixels.ToArray());
            }

            // The thumbnail does not disturb the main image.
            SimdCodecs.TryDecode(data, out var main, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            using (main)
                (main.Width, main.Height).ShouldBe((W, H));
        }
    }

    [Fact]
    public void WithoutAnExifThumbnailThereIsNone()
    {
        foreach (var data in new[] { JpegWith(), JpegWith(ExifBlock(orientation: 6)), JpegWith(ExifBlock(resolution: (72, 72, 2))) })
        {
            SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
            info.HasThumbnail.ShouldBeFalse();
            SimdCodecs.TryDecodeThumbnail(data, out var thumbnail).ShouldBeFalse();
            thumbnail.ShouldBeNull();
        }

        var png = PngWriter.Encode(Rgba(), W, H);
        SimdCodecs.TryReadInfo(png, out var pngInfo).ShouldBeTrue();
        pngInfo.HasThumbnail.ShouldBeFalse();
        SimdCodecs.TryDecodeThumbnail(png, out _).ShouldBeFalse();
        SimdCodecs.TryDecodeThumbnail(new byte[40], out _).ShouldBeFalse();
    }

    [Fact]
    public void AThumbnailThatIsNotAJpegIsNotOne()
    {
        var notJpeg = new byte[64];
        notJpeg.AsSpan().Fill(0x42);
        var data = JpegWith(ExifBlock(thumbnail: notJpeg));
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.HasThumbnail.ShouldBeFalse();
        SimdCodecs.TryDecodeThumbnail(data, out _).ShouldBeFalse();

        // A JPEG-looking thumbnail that does not decode: reported, but decoding it fails cleanly.
        byte[] stub = [0xFF, 0xD8, 0xFF, 0xD9];
        var broken = JpegWith(ExifBlock(thumbnail: stub));
        Should.NotThrow(() => SimdCodecs.TryDecodeThumbnail(broken, out _));
    }

    // ---- malformed Exif never throws ---------------------------------------------------------------------------

    private static void ReadEverything(byte[] jpeg, byte[] png, byte[] webp, string what)
    {
        foreach (var (name, data) in new[] { ("jpeg", jpeg), ("png", png), ("webp", webp) })
        {
            Should.NotThrow(() =>
            {
                SimdCodecs.TryReadInfo(data, out _);
                SimdCodecs.TryReadMetadata(data, out _);
                if (SimdCodecs.TryDecodeThumbnail(data, out var thumbnail))
                    thumbnail.Dispose();
                if (SimdCodecs.TryDecode(data, out var image, cancellationToken: TestContext.Current.CancellationToken))
                    image.Dispose();
                SimdCodecs.TryOpenFrames(data, out var frames);
                frames?.Dispose();
            }, $"{name}: {what}");
        }
    }

    private static void ReadMalformed(byte[] exif, string what)
    {
        var jpeg = JpegWith(exif);
        var png = PngWriter.Encode(Rgba(), W, H, new PngWriteOptions { Exif = exif });
        var webp = WebpEncoder.EncodeLossless(Rgba(), W, H, PixelFormat.Rgba32, new WebpLosslessEncodeOptions { Exif = exif });
        ReadEverything(jpeg, png, webp, what);

        // The block itself is delivered as it was written.
        SimdCodecs.TryReadMetadata(jpeg, out var metadata).ShouldBeTrue(what);
        metadata.Exif.ToArray().ShouldBe(exif, what);
    }

    private static byte[] Header(bool le, uint ifd0)
    {
        var header = new byte[8];
        header[0] = header[1] = le ? (byte)'I' : (byte)'M';
        if (le) { header[2] = 42; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ifd0); }
        else { header[3] = 42; BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), ifd0); }
        return header;
    }

    [Fact]
    public void AnIfdOffsetOutsideTheBlockIsHarmless()
    {
        foreach (var le in new[] { true, false })
        {
            ReadMalformed(Header(le, 0xFFFFFFF0), "ifd0 far outside");
            ReadMalformed(Header(le, 0xFFFFFFFF), "ifd0 at uint.MaxValue");
            ReadMalformed(Header(le, 8), "ifd0 at the end of the block");
            ReadMalformed(Header(le, 0), "ifd0 zero");
            ReadMalformed(Header(le, 6), "ifd0 inside the header");
            ReadMalformed([.. Header(le, 9), 0], "ifd0 with one byte of count");
        }
    }

    [Fact]
    public void ATruncatedIfdIsHarmless()
    {
        // A count of entries that does not fit the rest of the block.
        foreach (var le in new[] { true, false })
        foreach (var count in new ushort[] { 1, 3, 100, 4096, 4097, 0xFFFF })
        {
            var body = new byte[2 + 12 + 5];
            if (le) BinaryPrimitives.WriteUInt16LittleEndian(body, count); else BinaryPrimitives.WriteUInt16BigEndian(body, count);
            // One plausible entry (XResolution, RATIONAL, 1 value at an offset), then the block ends mid-entry.
            body[2] = le ? (byte)0x1A : (byte)0x01; body[3] = le ? (byte)0x01 : (byte)0x1A;
            body[4] = le ? (byte)5 : (byte)0; body[5] = le ? (byte)0 : (byte)5;
            ReadMalformed([.. Header(le, 8), .. body], $"truncated ifd, count {count}");
        }
    }

    [Fact]
    public void OutOfRangeValueOffsetsAreHarmless()
    {
        var valid = ExifBlock(resolution: (72, 72, 2), thumbnail: JpegEncoder.Encode(Rgb(), W, H, 3));
        // The XResolution entry is the first of IFD0 here (no orientation): its offset field is at 8 + 2 + 8.
        foreach (var offset in new uint[] { 0, 1, 7, (uint)valid.Length - 7, (uint)valid.Length, (uint)valid.Length + 1, 0xFFFFFFF8, 0xFFFFFFFF })
        {
            var blob = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8 + 2 + 8), offset);
            ReadMalformed(blob, $"XResolution offset {offset}");
        }
    }

    [Fact]
    public void ThumbnailOffsetsAndLengthsOutsideTheBlockAreNotAThumbnail()
    {
        var thumb = JpegEncoder.Encode(Rgb(), W, H, 3);
        var valid = ExifBlock(thumbnail: thumb);
        // IFD0 is empty (2 + 4 bytes); IFD1 starts at 14: entries at 16 (offset, value at +8) and 28 (length).
        var offsetField = 16 + 8;
        var lengthField = 28 + 8;
        (uint Offset, uint Length)[] cases =
        [
            (0xFFFFFFFF, 0xFFFFFFFF), (0xFFFFFFFF, 10), ((uint)valid.Length, 4), ((uint)valid.Length - 2, 4), (44, (uint)thumb.Length + 1),
            (44, 0xFFFFFFFF), (44, 0), (44, 3), (0, 100), (0, 0xFFFFFFFF), (0x80000000, 0x80000000),
        ];
        foreach (var (offset, length) in cases)
        {
            var blob = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(offsetField), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(lengthField), length);
            var jpeg = JpegWith(blob);
            SimdCodecs.TryReadInfo(jpeg, out var info).ShouldBeTrue($"{offset}/{length}");
            info.HasThumbnail.ShouldBeFalse($"{offset}/{length}");
            SimdCodecs.TryDecodeThumbnail(jpeg, out _).ShouldBeFalse($"{offset}/{length}");
            ReadMalformed(blob, $"thumbnail {offset}/{length}");
        }
    }

    [Fact]
    public void AnIfdChainThatLoopsOrOverrunsIsHarmless()
    {
        var thumb = JpegEncoder.Encode(Rgb(), W, H, 3);
        var valid = ExifBlock(thumbnail: thumb);

        var self = (byte[])valid.Clone();                       // IFD1's next pointer is IFD1 itself
        BinaryPrimitives.WriteUInt32LittleEndian(self.AsSpan(14 + 26), 14);
        ReadMalformed(self, "ifd1 next is ifd1");

        var back = (byte[])valid.Clone();                       // ... or IFD0
        BinaryPrimitives.WriteUInt32LittleEndian(back.AsSpan(14 + 26), 8);
        ReadMalformed(back, "ifd1 next is ifd0");

        var ifd0Loop = (byte[])valid.Clone();                   // IFD0's next is IFD0
        BinaryPrimitives.WriteUInt32LittleEndian(ifd0Loop.AsSpan(10), 8);
        ReadMalformed(ifd0Loop, "ifd0 next is ifd0");
        SimdCodecs.TryReadInfo(JpegWith(ifd0Loop), out var loopInfo).ShouldBeTrue();
        loopInfo.HasThumbnail.ShouldBeFalse();

        var huge = (byte[])valid.Clone();                       // IFD1 claims 65535 entries
        BinaryPrimitives.WriteUInt16LittleEndian(huge.AsSpan(14), 0xFFFF);
        ReadMalformed(huge, "ifd1 count 65535");

        var far = (byte[])valid.Clone();                        // IFD0's next is far outside
        BinaryPrimitives.WriteUInt32LittleEndian(far.AsSpan(10), 0xFFFFFFFF);
        ReadMalformed(far, "ifd1 offset outside");
    }

    [Fact]
    public void EveryTruncationOfAnExifBlockIsHarmless()
    {
        var thumb = JpegEncoder.Encode(Rgb(), 8, 8, 3);
        var valid = ExifBlock(orientation: 6, resolution: (300, 300, 2), thumbnail: thumb);
        for (var length = 8; length < valid.Length; length += length < 120 ? 1 : 17)
            ReadMalformed(valid[..length], $"truncated to {length}");
    }

    [Fact]
    public void RandomlyDamagedExifBlocksAreHarmless()
    {
        var thumb = JpegEncoder.Encode(Rgb(), 8, 8, 3);
        foreach (var le in new[] { true, false })
        {
            var valid = ExifBlock(le, orientation: 6, resolution: (300, 300, 2), thumbnail: thumb);
            var random = new Random(le ? 77 : 78);
            for (var i = 0; i < 150; i++)
            {
                var blob = (byte[])valid.Clone();
                // Damage the structure (the first 120 bytes hold both IFDs and the rationals), keeping the TIFF magic.
                for (var k = random.Next(1, 5); k > 0; k--)
                    blob[4 + random.Next(Math.Min(116, blob.Length - 4))] = (byte)random.Next(256);
                ReadMalformed(blob, $"mutation {i} ({(le ? "II" : "MM")})");
            }
        }
    }

    [Fact]
    public void NonMetadataInputsAreRefusedNotThrown()
    {
        SimdCodecs.TryReadMetadata(new byte[50], out var metadata).ShouldBeFalse();
        metadata.ShouldBeNull();
        SimdCodecs.TryReadMetadata(JpegWith()[..3], out _).ShouldBeFalse();
        SimdCodecs.TryReadMetadata(PngWriter.Encode(Rgba(), W, H)[..20], out _).ShouldBeFalse();
    }
}
