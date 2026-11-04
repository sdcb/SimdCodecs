using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Tiff;
using Sdcb.SimdCodecs.Webp;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The JPEG, WebP and TIFF <see cref="IImageEncoder"/> adapters: for every <see cref="PixelFormat"/> a session's bytes equal
/// the existing encoder's on the converted pixels, plus the multi-frame and metadata paths.
/// </summary>
public sealed class EncoderAdapterJpegWebpTiffTests
{
    private const int W = 37, H = 11;

    private static byte[] RandomImage(PixelFormat format, int seed)
    {
        var data = new byte[W * H * format.BytesPerPixel];
        new Random(seed).NextBytes(data);
        return data;
    }

    // Pushes the image through a session as two row chunks at a padded stride.
    private static IImageFrameEncoder Push(IImageEncoderSession session, byte[] pixels, PixelFormat format,
        ImageMetadata? metadata = null, TimeSpan? duration = null)
    {
        var rowBytes = W * format.BytesPerPixel;
        var stride = rowBytes + 5;
        var padded = new byte[stride * H];
        for (var y = 0; y < H; y++)
            pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(padded.AsSpan(y * stride));
        var frame = session.AddFrame(W, H, format, null, metadata, duration);
        const int first = 4;
        frame.WriteRows(padded, first, stride, TestContext.Current.CancellationToken);
        frame.WriteRows(padded.AsSpan(first * stride), H - first, stride, TestContext.Current.CancellationToken);
        return frame;
    }

    private static byte[] Session(Func<IBufferWriter<byte>, IImageEncoderSession> create, byte[] pixels, PixelFormat format)
    {
        var output = new ArrayBufferWriter<byte>();
        using var session = create(output);
        Push(session, pixels, format);
        session.Commit(TestContext.Current.CancellationToken);
        return output.WrittenSpan.ToArray();
    }

    // ---------------------------------------------------------------- JPEG

    [Fact]
    public void Jpeg_matches_the_encoder_on_converted_pixels_for_every_format()
    {
        var options = new JpegEncodeOptions { Quality = 80, Subsampling = JpegSubsampling.Chroma420 };
        foreach (var format in PixelFormat.All)
        {
            var pixels = RandomImage(format, 1);
            var native = JpegImageEncoder.GetClosestInputFormat(format);
            var converted = PixelConverter.ToPacked(pixels, 0, format, native, W, H);
            var expected = JpegEncoder.Encode(converted, W, H, native.Channels, options);
            var actual = Session(o => JpegImageEncoder.Create(o, options), pixels, format);
            actual.ShouldBe(expected, format.ToString());
        }
    }

    [Fact]
    public void Jpeg_closest_formats_are_8_bit_grey_or_colour()
    {
        foreach (var format in PixelFormat.All)
        {
            var native = JpegImageEncoder.GetClosestInputFormat(format);
            native.Sample.ShouldBe(SampleFormat.UInt8);
            native.IsPremultiplied.ShouldBeFalse();
            native.ShouldBeOneOf(PixelFormat.Gray8, PixelFormat.Rgb24, PixelFormat.Rgba32);
        }
        JpegImageEncoder.Registration.Format.ShouldBe(ImageFormat.Jpeg);
    }

    [Fact]
    public void Jpeg_bgra_and_rgba_input_give_identical_bytes()
    {
        var rgba = RandomImage(PixelFormat.Rgba32, 2);
        var bgra = PixelConverter.ToPacked(rgba, 0, PixelFormat.Rgba32, PixelFormat.Bgra32, W, H);
        var a = Session(o => JpegImageEncoder.Create(o), rgba, PixelFormat.Rgba32);
        var b = Session(o => JpegImageEncoder.Create(o), bgra, PixelFormat.Bgra32);
        b.ShouldBe(a);
    }

    [Fact]
    public void Jpeg_metadata_comes_back_and_an_unwritable_block_is_dropped()
    {
        byte[] exif = [(byte)'I', (byte)'I', (byte)'*', 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var icc = new byte[300];
        new Random(5).NextBytes(icc);
        var pixels = RandomImage(PixelFormat.Rgb24, 3);

        var output = new ArrayBufferWriter<byte>();
        using (var session = JpegImageEncoder.Create(output))
        {
            Push(session, pixels, PixelFormat.Rgb24, new ImageMetadata(exif: exif, icc: icc));
            session.Commit(TestContext.Current.CancellationToken);
        }
        SimdCodecs.TryReadMetadata(output.WrittenSpan, out var metadata).ShouldBeTrue();
        metadata.ShouldNotBeNull();
        metadata.Exif.ToArray().ShouldBe(exif);
        metadata.Icc.ToArray().ShouldBe(icc);

        // Not a TIFF structure: dropped, and the file equals one without metadata.
        var plain = Session(o => JpegImageEncoder.Create(o), pixels, PixelFormat.Rgb24);
        var dropped = new ArrayBufferWriter<byte>();
        using (var session = JpegImageEncoder.Create(dropped))
        {
            Push(session, pixels, PixelFormat.Rgb24, new ImageMetadata(exif: new byte[20], xmp: new byte[10]));
            session.Commit(TestContext.Current.CancellationToken);
        }
        dropped.WrittenSpan.ToArray().ShouldBe(plain);
    }

    // ---------------------------------------------------------------- WebP

    [Fact]
    public void Webp_lossless_matches_the_encoder_on_converted_pixels_for_every_format()
    {
        var options = new WebpLosslessEncodeOptions { Effort = 3 };
        foreach (var format in PixelFormat.All)
        {
            var pixels = RandomImage(format, 4);
            var native = WebpImageEncoder.GetClosestInputFormat(format);
            var converted = PixelConverter.ToPacked(pixels, 0, format, native, W, H);
            var expected = WebpEncoder.EncodeLossless(converted, W, H, native, options);
            Session(o => WebpImageEncoder.Create(o, options), pixels, format).ShouldBe(expected, format.ToString());
        }
        // No options at all is lossless defaults.
        var rgb = RandomImage(PixelFormat.Rgb24, 4);
        Session(o => WebpImageEncoder.Create(o), rgb, PixelFormat.Rgb24)
            .ShouldBe(WebpEncoder.EncodeLossless(rgb, W, H, PixelFormat.Rgb24));
    }

    [Fact]
    public void Webp_lossy_matches_the_encoder_on_converted_pixels_for_every_format()
    {
        var options = new WebpLossyEncodeOptions { Quality = 60, Effort = 2 };
        foreach (var format in PixelFormat.All)
        {
            var pixels = RandomImage(format, 6);
            var native = WebpImageEncoder.GetClosestInputFormat(format);
            var converted = PixelConverter.ToPacked(pixels, 0, format, native, W, H);
            var expected = WebpEncoder.EncodeLossy(converted, W, H, native, options);
            Session(o => WebpImageEncoder.Create(o, options), pixels, format).ShouldBe(expected, format.ToString());
        }
    }

    [Fact]
    public void Webp_closest_formats_are_8_bit_straight()
    {
        foreach (var format in PixelFormat.All)
        {
            var native = WebpImageEncoder.GetClosestInputFormat(format);
            native.Sample.ShouldBe(SampleFormat.UInt8);
            native.IsPremultiplied.ShouldBeFalse();
            native.Layout.ShouldBe(format.Layout);
        }
    }

    [Fact]
    public void Webp_animation_matches_EncodeAnimation()
    {
        var options = new WebpAnimationEncodeOptions { Mode = WebpAnimationMode.Lossless, LosslessEffort = 2, LoopCount = 3 };
        var format = PixelFormat.Bgra32;
        var images = new[] { RandomImage(format, 10), RandomImage(format, 11), RandomImage(format, 12) };
        var durations = new TimeSpan?[] { TimeSpan.FromMilliseconds(40), null, TimeSpan.FromMilliseconds(250) };

        var output = new ArrayBufferWriter<byte>();
        using (var session = WebpImageEncoder.Create(output, options))
        {
            session.MaxFrames.ShouldBeGreaterThan(3);
            for (var i = 0; i < 3; i++)
                Push(session, images[i], format, duration: durations[i]);
            session.Commit(TestContext.Current.CancellationToken);
        }

        var native = WebpImageEncoder.GetClosestInputFormat(format);
        var canvas = images.Select((p, i) => new WebpAnimationCanvasFrame(
                PixelConverter.ToPacked(p, 0, format, native, W, H), durations[i] ?? TimeSpan.FromMilliseconds(100)))
            .ToList();
        output.WrittenSpan.ToArray().ShouldBe(WebpEncoder.EncodeAnimation(canvas, W, H, native, options));
    }

    [Fact]
    public void Webp_metadata_comes_back()
    {
        byte[] exif = [(byte)'I', (byte)'I', (byte)'*', 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var icc = new byte[300];
        new Random(7).NextBytes(icc);
        foreach (EncodeOptions? options in new EncodeOptions?[] { null, new WebpLossyEncodeOptions() })
        {
            var output = new ArrayBufferWriter<byte>();
            using (var session = WebpImageEncoder.Create(output, options))
            {
                Push(session, RandomImage(PixelFormat.Rgb24, 8), PixelFormat.Rgb24, new ImageMetadata(exif: exif, icc: icc));
                session.Commit(TestContext.Current.CancellationToken);
            }
            SimdCodecs.TryReadMetadata(output.WrittenSpan, out var metadata).ShouldBeTrue();
            metadata.ShouldNotBeNull();
            metadata.Exif.ToArray().ShouldBe(exif);
            metadata.Icc.ToArray().ShouldBe(icc);
        }
    }

    // ---------------------------------------------------------------- TIFF

    [Fact]
    public void Tiff_every_format_decodes_back_to_the_converted_pixels()
    {
        var options = new TiffEncodeOptions { Page = new TiffPageOptions { Compression = TiffCompression.Uncompressed } };
        foreach (var format in PixelFormat.All)
        {
            var pixels = RandomImage(format, 20);
            var native = TiffImageEncoder.GetClosestInputFormat(format);
            native.IsPremultiplied.ShouldBeFalse();
            native.Sample.ShouldBe(format.Sample);
            var converted = PixelConverter.ToPacked(pixels, 0, format, native, W, H);
            var actual = Session(o => TiffImageEncoder.Create(o, options), pixels, format);

            TiffImageDecoder.TryOpenFrames(actual, out var reader).ShouldBeTrue(format.ToString());
            using (reader)
            {
                reader!.FrameCount.ShouldBe(1);
                reader.TryDecodeFrame(0, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue(format.ToString());
                using (image)
                {
                    image!.Width.ShouldBe(W);
                    image.Height.ShouldBe(H);
                    image.Channels.ShouldBe(native.Channels, format.ToString());
                    image.SampleFormat.ShouldBe(native.Sample);
                    image.Pixels.ToArray().ShouldBe(converted, format.ToString());
                }
            }
        }
    }

    [Fact]
    public void Tiff_two_pages_of_different_formats_decode_back()
    {
        PixelFormat[] formats = [PixelFormat.Bgra32, PixelFormat.Gray16];
        var images = formats.Select((f, i) => RandomImage(f, 30 + i)).ToArray();

        var output = new ArrayBufferWriter<byte>();
        using (var session = TiffImageEncoder.Create(output))
        {
            session.MaxFrames.ShouldBe(int.MaxValue);
            for (var i = 0; i < 2; i++)
                Push(session, images[i], formats[i]);
            session.Commit(TestContext.Current.CancellationToken);
        }

        TiffImageDecoder.TryOpenFrames(output.WrittenMemory, out var reader).ShouldBeTrue();
        using (reader)
        {
            reader!.FrameCount.ShouldBe(2);
            for (var i = 0; i < 2; i++)
            {
                var native = TiffImageEncoder.GetClosestInputFormat(formats[i]);
                reader.TryDecodeFrame(i, out var image, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
                using (image)
                    image!.Pixels.ToArray().ShouldBe(PixelConverter.ToPacked(images[i], 0, formats[i], native, W, H));
            }
        }
    }

    [Fact]
    public void Tiff_icc_profile_comes_back()
    {
        var icc = new byte[300];
        new Random(9).NextBytes(icc);
        var output = new ArrayBufferWriter<byte>();
        using (var session = TiffImageEncoder.Create(output))
        {
            Push(session, RandomImage(PixelFormat.Rgb24, 9), PixelFormat.Rgb24, new ImageMetadata(icc: icc));
            session.Commit(TestContext.Current.CancellationToken);
        }
        TiffImageDecoder.TryReadMetadata(output.WrittenSpan, out var metadata).ShouldBeTrue();
        metadata!.Icc.ToArray().ShouldBe(icc);
    }
}
