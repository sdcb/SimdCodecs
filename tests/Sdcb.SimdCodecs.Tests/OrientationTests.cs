using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// EXIF orientation applied: <see cref="PixelTransform"/> against a per-pixel reference written from the TIFF 6.0 /
/// EXIF definitions of the Orientation tag, and <see cref="DecodeOptions.ApplyOrientation"/> through every decode path
/// against ImageMagick's <c>AutoOrient</c> of files ImageMagick wrote (PNG eXIf, TIFF Orientation; JPEG against our own
/// stored decode turned by the reference, since JPEG decoders differ).
/// </summary>
public sealed class OrientationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the reference ------------------------------------------------------------------------------------------

    /// <summary>
    /// Where stored pixel (x, y) of a w × h image is shown, from the tag's definition (TIFF 6.0 p.36): the value names
    /// the side of the picture stored row 0 is on, and the side stored column 0 is on.
    /// </summary>
    private static (int U, int V) Displayed(int o, int x, int y, int w, int h) => o switch
    {
        1 => (x, y),                 // row 0 top, column 0 left
        2 => (w - 1 - x, y),         // row 0 top, column 0 right
        3 => (w - 1 - x, h - 1 - y), // row 0 bottom, column 0 right
        4 => (x, h - 1 - y),         // row 0 bottom, column 0 left
        5 => (y, x),                 // row 0 left, column 0 top
        6 => (h - 1 - y, x),         // row 0 right, column 0 top
        7 => (h - 1 - y, w - 1 - x), // row 0 right, column 0 bottom
        8 => (y, w - 1 - x),         // row 0 left, column 0 bottom
        _ => throw new ArgumentOutOfRangeException(nameof(o)),
    };

    /// <summary>The displayed image, tightly packed, one pixel at a time.</summary>
    internal static byte[] Reference(ReadOnlySpan<byte> source, int w, int h, int stride, int bpp, int o, out int dw, out int dh)
    {
        (dw, dh) = o >= 5 ? (h, w) : (w, h);
        stride = stride == 0 ? w * bpp : stride;
        var result = new byte[dw * dh * bpp];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (u, v) = Displayed(o, x, y, w, h);
                source.Slice(y * stride + x * bpp, bpp).CopyTo(result.AsSpan((v * dw + u) * bpp, bpp));
            }
        }
        return result;
    }

    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // ---- PixelTransform ------------------------------------------------------------------------------------------

    public static TheoryData<int, int> FormatsAndOrientations()
    {
        var data = new TheoryData<int, int>();
        for (var f = 0; f < Formats.Length; f++)
            for (var o = 1; o <= 8; o++)
                data.Add(f, o);
        return data;
    }

    private static readonly PixelFormat[] Formats =
        [PixelFormat.Gray8, PixelFormat.GrayAlpha16, PixelFormat.Rgb24, PixelFormat.Rgba32, PixelFormat.Rgb48, PixelFormat.Rgba64];

    private static readonly (int W, int H)[] Sizes = [(1, 1), (1, 7), (7, 1), (13, 5), (16, 32), (37, 41), (33, 17)];

    [Theory]
    [MemberData(nameof(FormatsAndOrientations))]
    public void SpanOrientMatchesTheReference(int formatIndex, int o)
    {
        var format = Formats[formatIndex];
        var bpp = format.BytesPerPixel;
        foreach (var (w, h) in Sizes)
        {
            foreach (var (inPad, outPad) in new[] { (0, 0), (5, 3), (bpp, 0), (0, 7) })
            {
                var inStride = inPad == 0 ? 0 : w * bpp + inPad;
                var source = Noise((inStride == 0 ? w * bpp : inStride) * h, w * 31 + h + o);
                var expected = Reference(source, w, h, inStride, bpp, o, out var dw, out var dh);
                var outStride = outPad == 0 ? 0 : dw * bpp + outPad;
                var step = outStride == 0 ? dw * bpp : outStride;
                var destination = new byte[step * dh + 9];
                destination.AsSpan().Fill(0xCD);

                PixelTransform.Orient(source, w, h, inStride, format, (ImageOrientation)o, destination, outStride);

                var because = $"{format} {w}x{h} o={o} strides {inStride}/{outStride}";
                for (var v = 0; v < dh; v++)
                {
                    destination.AsSpan(v * step, dw * bpp).ToArray().ShouldBe(expected.AsSpan(v * dw * bpp, dw * bpp).ToArray(), because);
                    destination.AsSpan(v * step + dw * bpp, step - dw * bpp).ToArray().ShouldAllBe(b => b == 0xCD, because);
                }
                destination.AsSpan(step * dh).ToArray().ShouldAllBe(b => b == 0xCD, because);
            }
        }
    }

    [Theory]
    [InlineData(1, SampleFormat.Float32)]
    [InlineData(3, SampleFormat.Float32)]
    [InlineData(4, SampleFormat.Float32)]
    [InlineData(3, SampleFormat.UInt16)]
    [InlineData(2, SampleFormat.UInt8)]
    public void ImageOrientMatchesTheReferenceForEveryPixelSize(int channels, SampleFormat sample)
    {
        var bpp = channels * RasterImage.BytesPerSample(sample);
        foreach (var (w, h) in Sizes)
        {
            for (var o = 1; o <= 8; o++)
            {
                var pixels = Noise(w * h * bpp, o * 7 + w);
                var encoding = new ColorEncoding { Primaries = ColorPrimaries.DisplayP3 };
                using var image = RasterImage.Wrap(w, h, channels, sample, pixels, encoding,
                    bitDepth: sample == SampleFormat.UInt16 ? 12 : 0);
                using var turned = PixelTransform.Orient(image, (ImageOrientation)o);

                var expected = Reference(pixels, w, h, 0, bpp, o, out var dw, out var dh);
                (turned.Width, turned.Height).ShouldBe((dw, dh));
                turned.Pixels.ToArray().ShouldBe(expected, $"{channels}x{sample} {w}x{h} o={o}");
                turned.Channels.ShouldBe(channels);
                turned.SampleFormat.ShouldBe(sample);
                turned.BitDepth.ShouldBe(image.BitDepth);
                turned.ColorEncoding.ShouldBe(encoding);
                ReferenceEquals(turned, image).ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void PixelBufferOrientKeepsFormatEncodingAndResetsExif()
    {
        var exif = MetadataTests.ExifBlock(littleEndian: false, orientation: 8, resolution: (300, 300, 2));
        using var buffer = PixelBuffer.Create(13, 5, PixelFormat.Bgr24, metadata: new ImageMetadata { Exif = exif });
        Noise(13 * 5 * 3, 4).CopyTo(buffer.WritablePixels);
        using var turned = PixelTransform.Orient(buffer, ImageOrientation.Rotate270);
        (turned.Width, turned.Height, turned.Format).ShouldBe((5, 13, PixelFormat.Bgr24));
        turned.Pixels.ToArray().ShouldBe(Reference(buffer.Pixels, 13, 5, 0, 3, 8, out _, out _));
        ShouldBeExifWithNormalOrientation(turned.Metadata.Exif.ToArray(), exif);
        using var copy = PixelTransform.Orient(buffer, ImageOrientation.Normal);
        copy.Pixels.ToArray().ShouldBe(buffer.Pixels.ToArray());
        ReferenceEquals(copy, buffer).ShouldBeFalse();
    }

    [Fact]
    public void OrientRefusesBadArguments()
    {
        var source = new byte[13 * 5 * 3];
        var destination = new byte[13 * 5 * 3];
        Should.Throw<ArgumentOutOfRangeException>(() => PixelTransform.Orient(source, 13, 5, 0, PixelFormat.Rgb24, 0, destination, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => PixelTransform.Orient(source, 13, 5, 0, PixelFormat.Rgb24, (ImageOrientation)9, destination, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => PixelTransform.Orient(source, 0, 5, 0, PixelFormat.Rgb24, ImageOrientation.Normal, destination, 0));
        Should.Throw<ArgumentException>(() => PixelTransform.Orient(source, 13, 5, 0, PixelFormat.Rgb24, ImageOrientation.Rotate90, destination.AsSpan(1), 0));
        Should.Throw<ArgumentException>(() => PixelTransform.Orient(source.AsSpan(1), 13, 5, 0, PixelFormat.Rgb24, ImageOrientation.Rotate90, destination, 0));
        // A destination stride shorter than a displayed row (13 pixels of 3 bytes); then source and destination overlapping.
        Should.Throw<ArgumentException>(() => PixelTransform.Orient(source, 13, 5, 0, PixelFormat.Rgb24, ImageOrientation.FlipVertical, new byte[1000], 20));
        Should.Throw<ArgumentException>(() => PixelTransform.Orient(source, 13, 5, 0, PixelFormat.Rgb24, ImageOrientation.Rotate90, source, 0));
    }

    // ---- metadata ------------------------------------------------------------------------------------------------

    /// <summary>The block is <paramref name="original"/> with the Orientation value 1, and no other byte changed.</summary>
    private static void ShouldBeExifWithNormalOrientation(byte[] actual, byte[] original)
    {
        actual.Length.ShouldBe(original.Length);
        var differing = Enumerable.Range(0, original.Length).Where(i => actual[i] != original[i]).ToArray();
        differing.Length.ShouldBeInRange(1, 2);
        var le = original[0] == 'I';
        // IFD0's entries; find the Orientation one and read its value.
        var ifd = (int)(le ? BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(4)));
        var count = le ? BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(ifd)) : BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(ifd));
        for (var i = 0; i < count; i++)
        {
            var at = ifd + 2 + i * 12;
            var tag = le ? BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(actual.AsSpan(at));
            if (tag != 0x0112) continue;
            (le ? BinaryPrimitives.ReadUInt16LittleEndian(actual.AsSpan(at + 8)) : BinaryPrimitives.ReadUInt16BigEndian(actual.AsSpan(at + 8))).ShouldBe((ushort)1);
            differing.ShouldAllBe(d => d == at + 8 || d == at + 9);
            return;
        }
        throw new ShouldAssertException("The Exif block has no Orientation entry.");
    }

    private const int PW = 13, PH = 7;

    /// <summary>Our PNG of noise carrying an eXIf with Orientation <paramref name="o"/> and a resolution.</summary>
    private static (byte[] File, byte[] Rgba, byte[] Exif) OwnPng(int o, bool littleEndian = true)
    {
        var rgba = Noise(PW * PH * 4, o);
        var exif = MetadataTests.ExifBlock(littleEndian, orientation: o, resolution: (72, 72, 2));
        return (PngWriter.Encode(rgba, PW, PH, new PngWriteOptions { Exif = exif }), rgba, exif);
    }

    private static DecodeOptions Oriented => new() { ApplyOrientation = true };

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void DecodeTurnsThePixelsAndResetsTheExifOrientation(int o)
    {
        foreach (var le in new[] { true, false })
        {
            var (png, rgba, exif) = OwnPng(o, le);
            var expected = Reference(rgba, PW, PH, 0, 4, o, out var dw, out var dh);

            using var image = ImageCodecs.Decode(png, Oriented, Ct);
            (image.Width, image.Height).ShouldBe((dw, dh));
            image.Pixels.ToArray().ShouldBe(expected);
            if (o == 1)
                image.Metadata.Exif.ToArray().ShouldBe(exif);
            else
                ShouldBeExifWithNormalOrientation(image.Metadata.Exif.ToArray(), exif);

            // Without the option: stored pixels, the file's Exif.
            using var stored = ImageCodecs.Decode(png, cancellationToken: Ct);
            (stored.Width, stored.Height).ShouldBe((PW, PH));
            stored.Pixels.ToArray().ShouldBe(rgba);
            stored.Metadata.Exif.ToArray().ShouldBe(exif);

            // TryReadInfo describes the file: the stored size.
            ImageCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
            (info.Width, info.Height, info.Orientation).ShouldBe((PW, PH, (ImageOrientation)o));
            Oriented.GetOutputSize(info).ShouldBe((dw, dh));
            DecodeOptions.Default.GetOutputSize(info).ShouldBe((PW, PH));
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void EveryDecodePathTurnsThePixels(int o)
    {
        var (png, rgba, exif) = OwnPng(o);
        var expected = Reference(rgba, PW, PH, 0, 4, o, out var dw, out var dh);
        ImageCodecs.TryReadInfo(png, out var info).ShouldBeTrue();

        ImageCodecs.TryDecode(png, out var tried, Oriented, Ct).ShouldBeTrue();
        using (tried)
            tried.Pixels.ToArray().ShouldBe(expected);

        // TryDecodeInto, sized by GetOutputSize(info), at a padded stride, in BGR.
        var (w, h) = Oriented.GetOutputSize(info);
        (w, h).ShouldBe((dw, dh));
        var stride = w * 3 + 5;
        var into = new byte[stride * h];
        ImageCodecs.TryDecodeInto(png, into, stride, PixelFormat.Bgr24, Oriented, Ct).ShouldBeTrue();
        var bgr = PixelConverter.ToPacked(expected, 0, PixelFormat.Rgba32, PixelFormat.Bgr24, dw, dh);
        for (var v = 0; v < h; v++)
            into.AsSpan(v * stride, w * 3).ToArray().ShouldBe(bgr.AsSpan(v * w * 3, w * 3).ToArray());
        // A destination of the stored size does not hold a turned image.
        if (o >= 5)
            ImageCodecs.TryDecodeInto(png, new byte[PW * 3 * (PH - 1) + PH * 3 - 1], PH * 3, PixelFormat.Bgr24, Oriented, Ct).ShouldBeFalse();

        // Decode(data, format): one call to a PixelBuffer, with the Exif reset.
        using var buffer = ImageCodecs.Decode(png, PixelFormat.Bgr24, Oriented, Ct);
        (buffer.Width, buffer.Height).ShouldBe((dw, dh));
        buffer.Pixels.ToArray().ShouldBe(bgr);
        if (o != 1)
            ShouldBeExifWithNormalOrientation(buffer.Metadata.Exif.ToArray(), exif);

        // The session API: ImageFrame.Decode and CopyPixels.
        using var session = ImageDecoder.Open(png);
        var frame = session.GetFrame(0);
        using (var decoded = frame.Decode(Oriented, Ct))
            decoded.Pixels.ToArray().ShouldBe(expected);
        var copied = new byte[dw * dh * 4];
        frame.CopyPixels(null, PixelFormat.Rgba32, copied, 0, Oriented, Ct);
        copied.ShouldBe(expected);
        Should.Throw<ArgumentException>(() => frame.CopyPixels(null, PixelFormat.Rgba32, new byte[dw * dh * 4 - 1], 0, Oriented, Ct));

        // The codec's own entry points, without the facade.
        using (var direct = PngImageDecoder.Decode(png, Oriented, Ct))
            direct.Pixels.ToArray().ShouldBe(expected);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void RegionIsInDisplayedCoordinates(int o)
    {
        var (png, rgba, _) = OwnPng(o);
        var full = Reference(rgba, PW, PH, 0, 4, o, out var dw, out var dh);
        foreach (var region in new PixelRect[] { new(0, 0, 1, 1), new(1, 2, 3, 4), new(dw - 2, dh - 3, 2, 3), new(0, 0, dw, dh), new(2, 0, 1, dh) })
        {
            var expected = Crop(full, dw, 4, region);
            var options = Oriented with { Region = region };
            using (var image = ImageCodecs.Decode(png, options, Ct))
            {
                (image.Width, image.Height).ShouldBe((region.Width, region.Height));
                image.Pixels.ToArray().ShouldBe(expected, $"o={o} {region}");
            }
            var into = new byte[region.Width * region.Height * 4];
            ImageCodecs.TryDecodeInto(png, into, 0, PixelFormat.Rgba32, options, Ct).ShouldBeTrue();
            into.ShouldBe(expected, $"o={o} {region} into");
        }

        // Inside the stored image, outside the displayed one (they differ for a quarter turn).
        if (o >= 5)
        {
            var stored = Oriented with { Region = new PixelRect(0, 0, PW, PH) };
            Should.Throw<ArgumentException>(() => ImageCodecs.Decode(png, stored, Ct));
            Should.Throw<ArgumentException>(() => ImageCodecs.TryDecodeInto(png, new byte[PW * PH * 4], 0, PixelFormat.Rgba32, stored, Ct));
            Should.Throw<ArgumentException>(() => ImageCodecs.Decode(png, PixelFormat.Rgba32, stored, Ct));
        }
    }

    [Theory]
    [InlineData(2)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ScaleComesFirstThenTheTurn(int o)
    {
        var (png, _, _) = OwnPng(o);
        // The stored image box-filtered, then turned: its partial boxes stay at the stored right and bottom edges.
        using var scaledStored = ImageCodecs.Decode(png, new DecodeOptions { ScaleDenominator = 2 }, Ct);
        var expected = Reference(scaledStored.Pixels, scaledStored.Width, scaledStored.Height, 0, 4, o, out var dw, out var dh);
        using var image = ImageCodecs.Decode(png, Oriented with { ScaleDenominator = 2 }, Ct);
        (image.Width, image.Height).ShouldBe((dw, dh));
        image.Pixels.ToArray().ShouldBe(expected);

        ImageCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
        (Oriented with { ScaleDenominator = 2 }).GetOutputSize(info).ShouldBe((dw, dh));

        var region = new PixelRect(1, 1, dw - 1, dh - 2);
        using var cropped = ImageCodecs.Decode(png, Oriented with { ScaleDenominator = 2, Region = region }, Ct);
        cropped.Pixels.ToArray().ShouldBe(Crop(expected, dw, 4, region));
        var into = new byte[region.Width * region.Height * 3];
        ImageCodecs.TryDecodeInto(png, into, 0, PixelFormat.Rgb24, Oriented with { ScaleDenominator = 2, Region = region }, Ct).ShouldBeTrue();
        into.ShouldBe(PixelConverter.ToPacked(Crop(expected, dw, 4, region), 0, PixelFormat.Rgba32, PixelFormat.Rgb24, region.Width, region.Height));
    }

    private static byte[] Crop(byte[] pixels, int width, int bpp, PixelRect r)
    {
        var result = new byte[r.Width * r.Height * bpp];
        for (var y = 0; y < r.Height; y++)
            pixels.AsSpan(((r.Y + y) * width + r.X) * bpp, r.Width * bpp).CopyTo(result.AsSpan(y * r.Width * bpp));
        return result;
    }

    // ---- ImageMagick files, against AutoOrient -------------------------------------------------------------------

    /// <summary>An RGB image of noise, written by ImageMagick as <paramref name="format"/> with Exif Orientation
    /// <paramref name="o"/>.</summary>
    private static byte[] MagickFile(int o, MagickFormat format, int width = 19, int height = 11)
    {
        using var image = new MagickImage();
        image.ReadPixels(Noise(width * height * 3, o + 100), new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGB"));
        image.Depth = 8;
        var profile = new ExifProfile();
        profile.SetValue(ImageMagick.ExifTag.Orientation, (ushort)o);
        image.SetProfile(profile);
        image.Orientation = (OrientationType)o;
        return image.ToByteArray(format);
    }

    /// <summary>ImageMagick's AutoOrient of <paramref name="file"/>: its size and 8-bit RGB pixels.</summary>
    private static (int Width, int Height, byte[] Rgb) MagickAutoOriented(byte[] file)
    {
        using var image = new MagickImage(file);
        image.AutoOrient();
        using var pixels = image.GetPixels();
        return ((int)image.Width, (int)image.Height, [.. pixels.ToShortArray(PixelMapping.RGB)!.Select(v => (byte)(v >> 8))]);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void MagickPngMatchesAutoOrientExactly(int o)
    {
        var png = MagickFile(o, MagickFormat.Png);
        ImageCodecs.TryReadInfo(png, out var info).ShouldBeTrue();
        info.Orientation.ShouldBe((ImageOrientation)o, "ImageMagick wrote the orientation into eXIf");
        var (mw, mh, magick) = MagickAutoOriented(png);

        using var image = ImageCodecs.Decode(png, PixelFormat.Rgb24, Oriented, Ct);
        (image.Width, image.Height).ShouldBe((mw, mh));
        image.Pixels.ToArray().ShouldBe(magick);
        using var decoded = ImageCodecs.Decode(png, Oriented, Ct);
        decoded.ToRgba8().ShouldBe(PixelConverter.ToPacked(magick, 0, PixelFormat.Rgb24, PixelFormat.Rgba32, mw, mh));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void MagickTiffMatchesAutoOrientExactly(int o)
    {
        var tiff = MagickFile(o, MagickFormat.Tiff);
        ImageCodecs.TryReadInfo(tiff, out var info).ShouldBeTrue();
        info.Orientation.ShouldBe((ImageOrientation)o, "ImageMagick wrote the TIFF Orientation tag");
        var (mw, mh, magick) = MagickAutoOriented(tiff);

        using var image = ImageCodecs.Decode(tiff, PixelFormat.Rgb24, Oriented, Ct);
        (image.Width, image.Height).ShouldBe((mw, mh));
        image.Pixels.ToArray().ShouldBe(magick);

        // The page reader turns each page by its own tag.
        using var session = ImageDecoder.Open(tiff);
        using var page = session.GetFrame(0).Decode(Oriented, Ct);
        page.ToRgba8().ShouldBe(PixelConverter.ToPacked(magick, 0, PixelFormat.Rgb24, PixelFormat.Rgba32, mw, mh));
        var rgb = new byte[mw * mh * 3];
        session.GetFrame(0).CopyPixels(null, PixelFormat.Rgb24, rgb, 0, Oriented, Ct);
        rgb.ShouldBe(magick);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void MagickJpegTurnsOurStoredDecode(int o)
    {
        var jpeg = MagickFile(o, MagickFormat.Jpeg, 37, 21);
        ImageCodecs.TryReadInfo(jpeg, out var info).ShouldBeTrue();
        info.Orientation.ShouldBe((ImageOrientation)o, "ImageMagick wrote the orientation into APP1 Exif");
        var (mw, mh, _) = MagickAutoOriented(jpeg);

        using var stored = ImageCodecs.Decode(jpeg, cancellationToken: Ct);
        var expected = Reference(stored.Pixels, stored.Width, stored.Height, 0, 3, o, out var dw, out var dh);
        (dw, dh).ShouldBe((mw, mh));

        using var image = ImageCodecs.Decode(jpeg, Oriented, Ct);
        (image.Width, image.Height).ShouldBe((dw, dh));
        image.Pixels.ToArray().ShouldBe(expected);

        // The native JPEG writer path (BGR into a padded destination).
        var stride = dw * 3 + 2;
        var into = new byte[stride * dh];
        ImageCodecs.TryDecodeInto(jpeg, into, stride, PixelFormat.Rgb24, Oriented, Ct).ShouldBeTrue();
        for (var v = 0; v < dh; v++)
            into.AsSpan(v * stride, dw * 3).ToArray().ShouldBe(expected.AsSpan(v * dw * 3, dw * 3).ToArray());

        // A native DCT scale, then the turn.
        using var halfStored = ImageCodecs.Decode(jpeg, new DecodeOptions { ScaleDenominator = 2 }, Ct);
        using var half = ImageCodecs.Decode(jpeg, Oriented with { ScaleDenominator = 2 }, Ct);
        half.Pixels.ToArray().ShouldBe(Reference(halfStored.Pixels, halfStored.Width, halfStored.Height, 0, 3, o, out _, out _));

        if (o != 1)
        {
            ImageCodecs.TryReadMetadata(jpeg, out var metadata).ShouldBeTrue();
            ShouldBeExifWithNormalOrientation(image.Metadata.Exif.ToArray(), metadata.Exif.ToArray());
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void MagickLosslessWebpMatchesAutoOrientExactly(int o)
    {
        byte[] webp;
        using (var source = new MagickImage())
        {
            source.ReadPixels(Noise(19 * 11 * 3, o + 200), new PixelReadSettings(19, 11, StorageType.Char, "RGB"));
            var profile = new ExifProfile();
            profile.SetValue(ImageMagick.ExifTag.Orientation, (ushort)o);
            source.SetProfile(profile);
            source.Orientation = (OrientationType)o;
            source.Settings.SetDefine(MagickFormat.WebP, "lossless", true);
            webp = source.ToByteArray(MagickFormat.WebP);
        }
        ImageCodecs.TryReadInfo(webp, out var info).ShouldBeTrue();
        info.Orientation.ShouldBe((ImageOrientation)o, "ImageMagick wrote the orientation into the EXIF chunk");
        var (mw, mh, magick) = MagickAutoOriented(webp);

        using var image = ImageCodecs.Decode(webp, PixelFormat.Rgb24, Oriented, Ct);
        (image.Width, image.Height).ShouldBe((mw, mh));
        image.Pixels.ToArray().ShouldBe(magick);
        using var session = ImageDecoder.Open(webp);
        using var frame = session.GetFrame(0).Decode(Oriented, Ct);
        frame.ToRgba8().ShouldBe(PixelConverter.ToPacked(magick, 0, PixelFormat.Rgb24, PixelFormat.Rgba32, mw, mh));
    }

    [Theory]
    [InlineData(3)] [InlineData(6)] [InlineData(7)]
    public void AnimationFramesAreTurned(int o)
    {
        var exif = MetadataTests.ExifBlock(orientation: o);
        var encoder = new PngAnimationEncoder(PW, PH, new PngWriteOptions { Exif = exif });
        var frames = new[] { Noise(PW * PH * 4, 1), Noise(PW * PH * 4, 2) };
        foreach (var f in frames)
        {
            for (var i = 3; i < f.Length; i += 4) f[i] = 255; // opaque, so the canvas is each frame as given
            encoder.AddFrame(f, PixelFormat.Rgba32, TimeSpan.FromMilliseconds(100));
        }
        var apng = encoder.Encode();

        using var session = ImageDecoder.Open(apng);
        session.FrameCount.ShouldBe(2);
        for (var i = 0; i < 2; i++)
        {
            var frame = session.GetFrame(i);
            var rgb = PixelConverter.ToPacked(frames[i], 0, PixelFormat.Rgba32, PixelFormat.Rgb24, PW, PH);
            var expected = Reference(rgb, PW, PH, 0, 3, o, out var dw, out var dh);
            using (var image = frame.Decode(Oriented, Ct))
            {
                (image.Width, image.Height).ShouldBe((dw, dh));
                PixelConverter.ToPacked(image.Pixels, 0, PixelFormat.ForChannels(image.Channels, SampleFormat.UInt8), PixelFormat.Rgb24, dw, dh)
                    .ShouldBe(expected);
            }
            var into = new byte[dw * dh * 3];
            frame.CopyPixels(new PixelRect(0, 0, dw, dh), PixelFormat.Rgb24, into, 0, Oriented, Ct);
            into.ShouldBe(expected);
        }
    }

    [Fact]
    public void WithoutAnOrientationTheOptionChangesNothing()
    {
        // A BMP carries no orientation: the option is a no-op.
        var rgba = Noise(PW * PH * 4, 9);
        var bmp = Bmp.BmpEncoder.Encode(rgba, PW, PH, PixelFormat.Rgba32);
        using var a = ImageCodecs.Decode(bmp, Oriented, Ct);
        using var b = ImageCodecs.Decode(bmp, cancellationToken: Ct);
        a.Pixels.ToArray().ShouldBe(b.Pixels.ToArray());
        (a.Width, a.Height).ShouldBe((b.Width, b.Height));
    }
}
