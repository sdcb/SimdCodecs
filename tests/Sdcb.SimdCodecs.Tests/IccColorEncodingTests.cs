using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Color.Icc;
using Sdcb.SimdCodecs.Heif;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Jpeg2000;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tiff;
using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// An image whose only colour signalling is an ICC profile reports the profile's primaries when H.273 names
/// them (BT.709 / Display P3 / BT.2020) in both <see cref="ImageInfo.ColorEncoding"/> and
/// <see cref="IDecodedImage.ColorEncoding"/>, with transfer and range as assumed. A format's own signalling
/// (PNG cICP / gAMA 1.0, HEIF nclx) wins over the profile. Containers come from Magick.NET where it writes
/// the profile, else from the hand-built bytes noted on each test.
/// </summary>
public sealed class IccColorEncodingTests
{
    private static readonly ColorEncoding P3Encoding = ColorEncoding.AssumedSrgb with { Primaries = ColorPrimaries.DisplayP3 };

    private static byte[] DisplayP3 => IccPrimariesTests.DisplayP3;

    private static byte[] Magick(MagickFormat format, byte[] profile, Action<MagickImage>? configure = null)
    {
        using var image = new MagickImage(MagickColors.SteelBlue, 8, 8);
        image.SetProfile(new ColorProfile(profile));
        configure?.Invoke(image);
        return image.ToByteArray(format);
    }

    private static readonly byte[] Rgba = new byte[8 * 8 * 4];

    // ───────────────────────────── per format: ICC only ─────────────────────────────

    [Fact]
    public void Jpeg_reports_the_profiles_primaries()
    {
        var file = Magick(MagickFormat.Jpeg, DisplayP3);
        JpegImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        JpegImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(P3Encoding);
        image.Metadata.Icc.Span.ToArray().ShouldBe(DisplayP3);
    }

    [Fact]
    public void Jpeg_with_an_srgb_or_unrecognised_profile_is_assumed_srgb()
    {
        foreach (var profile in new[] { IccProfiles.SRgbV4.ToArray(), ((ColorProfile)ColorProfiles.AdobeRGB1998).ToByteArray() })
        {
            var file = Magick(MagickFormat.Jpeg, profile);
            JpegImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
            info.ColorEncoding.ShouldBeSameAs(ColorEncoding.AssumedSrgb);
            JpegImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
            image.ColorEncoding.ShouldBeSameAs(ColorEncoding.AssumedSrgb);
        }
    }

    [Fact]
    public void Jpeg_with_a_bt2020_profile_reports_bt2020()
    {
        var profile = IccProfiles.WithCicp(IccProfiles.SRgbV4.Span, ColorPrimaries.BT2020, TransferFunction.Srgb);
        var file = Magick(MagickFormat.Jpeg, profile);
        JpegImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb with { Primaries = ColorPrimaries.BT2020 });
    }

    [Fact]
    public void Png_reports_the_profiles_primaries()
    {
        var file = Magick(MagickFormat.Png, DisplayP3);
        PngImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        PngImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(P3Encoding);

        using var frames = PngFrameReader.Open(file);
        frames.TryGetFrameInfo(0, out var frame).ShouldBeTrue();
        frame.Info.ColorEncoding.ShouldBe(P3Encoding);
        frames.TryDecodeFrame(0, out var first, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        first.ColorEncoding.ShouldBe(P3Encoding);
    }

    [Fact]
    public void Webp_reports_the_profiles_primaries()
    {
        var file = Magick(MagickFormat.WebP, DisplayP3, static i => i.Settings.SetDefine(MagickFormat.WebP, "lossless", true));
        WebpImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        WebpImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Metadata.Icc.Span.ToArray().ShouldBe(DisplayP3);
        image.ColorEncoding.ShouldBe(P3Encoding);
    }

    /// <summary>The animated fixture libwebp's webpmux wrote, its ICCP payload replaced.</summary>
    [Fact]
    public void Animated_webp_frames_report_the_profiles_primaries()
    {
        var source = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", "anim-webpmux-metadata.webp"));
        var file = WithIccp(source, DisplayP3);

        WebpImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        info.FrameCount.ShouldBeGreaterThan(1);
        using var frames = WebpFrameReader.Open(file);
        frames.TryGetFrameInfo(0, out var frame).ShouldBeTrue();
        frame.Info.ColorEncoding.ShouldBe(P3Encoding);
        frames.TryDecodeFrame(1, out var second, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        second.ColorEncoding.ShouldBe(P3Encoding);
        second.Metadata.Icc.Span.ToArray().ShouldBe(DisplayP3);
    }

    [Fact]
    public void Tiff_reports_the_profiles_primaries()
    {
        var file = Magick(MagickFormat.Tiff, DisplayP3);
        TiffImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        TiffImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Metadata.Icc.Span.ToArray().ShouldBe(DisplayP3);
        image.ColorEncoding.ShouldBe(P3Encoding);
    }

    /// <summary>Magick.NET writes no V5 BMP profile, so the bytes come from the BMP encoder.</summary>
    [Fact]
    public void Bmp_v5_reports_the_profiles_primaries()
    {
        var file = BmpEncoder.Encode(Rgba, 8, 8, PixelFormat.Rgba32, new BmpEncodeOptions { IccProfile = DisplayP3 });
        BmpImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        BmpImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.Metadata.Icc.Span.ToArray().ShouldBe(DisplayP3);
        image.ColorEncoding.ShouldBe(P3Encoding);

        var plain = BmpEncoder.Encode(Rgba, 8, 8, PixelFormat.Rgba32);
        BmpImageDecoder.TryReadInfo(plain, out var plainInfo).ShouldBeTrue();
        plainInfo.ColorEncoding.ShouldBeSameAs(ColorEncoding.AssumedSrgb);
    }

    /// <summary>The OpenJPEG-encoded fixture with its colour box replaced by a restricted ICC profile.</summary>
    [Fact]
    public void Jpeg2000_jp2_reports_the_profiles_primaries()
    {
        var jp2 = Jpeg2000FacadeTests.WithColour(Jpeg2000FacadeTests.File("jp2-rgb-struct64.jp2"), [2, 0, 0, .. DisplayP3]);
        Jpeg2000ImageDecoder.TryReadInfo(jp2, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        Jpeg2000ImageDecoder.TryDecode(jp2, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(P3Encoding);

        // An enumerated sRGB colour space is the assumption itself.
        var srgb = Jpeg2000FacadeTests.File("jp2-rgb-struct64.jp2");
        Jpeg2000ImageDecoder.TryReadInfo(srgb, out var plain).ShouldBeTrue();
        plain.ColorEncoding.ShouldBeSameAs(ColorEncoding.AssumedSrgb);
    }

    // ───────────────────────────── the format's own signalling wins ─────────────────────────────

    [Fact]
    public void Png_cicp_wins_over_the_profile()
    {
        var file = PngWriter.Encode(Rgba, 8, 8, new PngWriteOptions { Cicp = CicpChunk.Hdr10Pq, IccProfile = DisplayP3 });
        var expected = new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Pq, FullRange = true };
        PngImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(expected);
        PngImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(expected);
        image.Metadata.Icc.Span.ToArray().ShouldBe(DisplayP3);

        using var frames = PngFrameReader.Open(file);
        frames.TryGetFrameInfo(0, out var frame).ShouldBeTrue();
        frame.Info.ColorEncoding.ShouldBe(expected);
    }

    [Fact]
    public void Png_linear_gamma_wins_over_the_profile()
    {
        var file = PngWriter.Encode(Rgba, 8, 8, new PngWriteOptions { Gamma = 1.0, IccProfile = DisplayP3 });
        var expected = ColorEncoding.AssumedSrgb with { Transfer = TransferFunction.Linear };
        PngImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(expected);
        PngImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(expected);
    }

    // ───────────────────────────── HEIF ─────────────────────────────

    /// <summary>
    /// A phone photo: Display P3 ICC profile, no nclx, and a VUI saying BT.601. The profile's primaries win over
    /// the VUI's; the pixels are not touched (the VUI still picks the YCbCr matrix, pinned by the output golden).
    /// </summary>
    [Fact]
    public void Heif_icc_only_reports_the_profiles_primaries_over_the_vui()
    {
        var file = HeifTests.ReadFixture("hevc-main-3072x4096-grid.heic");
        HeifImageDecoder.TryReadInfo(file, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(P3Encoding);
        HeifImageDecoder.TryDecode(file, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(P3Encoding);
        IccProfiles.TryGetPrimaries(image.Metadata.Icc.Span, out var primaries).ShouldBeTrue();
        primaries.ShouldBe(ColorPrimaries.DisplayP3);
    }

    /// <summary>
    /// The phone photo's <c>prof</c> colour box is rewritten in place as an nclx box (BT.709 primaries, sRGB
    /// transfer) followed by a shortened <c>prof</c> box of the same profile (its last bytes cut: the colorants
    /// are far from the end). Box sizes add up to the original, so no offset in the file moves. The nclx box
    /// wins; the profile is still passed through.
    /// </summary>
    [Fact]
    public void Heif_nclx_wins_over_the_profile()
    {
        var file = HeifTests.ReadFixture("hevc-main-3072x4096-grid.heic");
        var colr = file.AsSpan().IndexOf("colrprof"u8) - 4;
        colr.ShouldBeGreaterThan(0);
        var oldSize = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(colr));
        const int nclxSize = 19;
        var profileBody = file.AsSpan(colr + 12, oldSize - 12).ToArray();

        var patched = (byte[])file.Clone();
        var box = patched.AsSpan(colr, oldSize);
        BinaryPrimitives.WriteInt32BigEndian(box, nclxSize);
        "colrnclx"u8.CopyTo(box[4..]);
        BinaryPrimitives.WriteUInt16BigEndian(box[12..], 1);   // BT.709 primaries
        BinaryPrimitives.WriteUInt16BigEndian(box[14..], 13);  // sRGB transfer
        BinaryPrimitives.WriteUInt16BigEndian(box[16..], 1);   // BT.709 matrix
        box[18] = 0x80;                                        // full range
        var rest = box[nclxSize..];
        BinaryPrimitives.WriteInt32BigEndian(rest, rest.Length);
        "colrprof"u8.CopyTo(rest[4..]);
        profileBody.AsSpan(0, rest.Length - 12).CopyTo(rest[12..]);

        var expected = new ColorEncoding { Primaries = ColorPrimaries.BT709, Transfer = TransferFunction.Srgb, FullRange = true };
        HeifImageDecoder.TryReadInfo(patched, out var info).ShouldBeTrue();
        info.ColorEncoding.ShouldBe(expected);
        HeifImageDecoder.TryDecode(patched, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        image.ColorEncoding.ShouldBe(expected);
        IccProfiles.TryGetPrimaries(image.Metadata.Icc.Span, out var primaries).ShouldBeTrue("the profile is still there");
        primaries.ShouldBe(ColorPrimaries.DisplayP3);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>A WebP with its <c>ICCP</c> chunk's payload replaced, the RIFF size and chunk padding kept right.</summary>
    private static byte[] WithIccp(byte[] webp, byte[] profile)
    {
        var at = webp.AsSpan().IndexOf("ICCP"u8);
        at.ShouldBeGreaterThan(0);
        var oldSize = BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(at + 4));
        var oldEnd = at + 8 + oldSize + (oldSize & 1);
        var padded = profile.Length + (profile.Length & 1);
        var chunk = new byte[8 + padded];
        "ICCP"u8.CopyTo(chunk);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), profile.Length);
        profile.CopyTo(chunk, 8);

        byte[] result = [.. webp[..at], .. chunk, .. webp[oldEnd..]];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length - 8);
        return result;
    }
}
