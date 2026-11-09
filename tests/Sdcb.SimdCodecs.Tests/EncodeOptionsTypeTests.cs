using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Ico;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using Sdcb.SimdCodecs.Tiff;
using Sdcb.SimdCodecs.Webp;
using Shouldly;
using Xunit;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// An encoder takes its own options type or none; any other options object throws instead of silently encoding with
/// defaults (WebP's default once made a lossless file out of <see cref="JpegEncodeOptions"/>). Also the raw-pixel
/// <c>ImageCodecs.Encode</c> overloads that write to a stream or a buffer writer.
/// </summary>
public sealed class EncodeOptionsTypeTests
{
    private const int W = 16, H = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ForeignOptions : EncodeOptions;

    private static readonly ImageFormat[] AllFormats =
    [
        ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Tiff, ImageFormat.Webp, ImageFormat.Gif,
        ImageFormat.Bmp, ImageFormat.Ico, ImageFormat.Qoi, ImageFormat.Pnm,
    ];

    public static TheoryData<ImageFormat> Formats => new(AllFormats);

    private static byte[] Rgb()
    {
        var data = new byte[W * H * 3];
        new Random(5).NextBytes(data);
        return data;
    }

    // The options types each encoder takes (ICO and QOI take none).
    private static EncodeOptions[] OptionsOf(ImageFormat format) => format switch
    {
        ImageFormat.Png => [new PngWriteOptions()],
        ImageFormat.Jpeg => [new JpegEncodeOptions()],
        ImageFormat.Tiff => [new TiffEncodeOptions()],
        ImageFormat.Webp => [new WebpLossyEncodeOptions(), new WebpLosslessEncodeOptions(), new WebpAnimationEncodeOptions()],
        ImageFormat.Gif => [new GifEncodeOptions()],
        ImageFormat.Bmp => [new BmpEncodeOptions()],
        ImageFormat.Pnm => [new PnmEncodeOptions()],
        _ => [],
    };

    private static IImageEncoderSession Create(ImageFormat format, IBufferWriter<byte> output, EncodeOptions? options) => format switch
    {
        ImageFormat.Png => PngImageEncoder.Create(output, options),
        ImageFormat.Jpeg => JpegImageEncoder.Create(output, options),
        ImageFormat.Tiff => TiffImageEncoder.Create(output, options),
        ImageFormat.Webp => WebpImageEncoder.Create(output, options),
        ImageFormat.Gif => GifImageEncoder.Create(output, options),
        ImageFormat.Bmp => BmpImageEncoder.Create(output, options),
        ImageFormat.Ico => IcoImageEncoder.Create(output, options),
        ImageFormat.Qoi => QoiImageEncoder.Create(output, options),
        ImageFormat.Pnm => PnmImageEncoder.Create(output, options),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_foreign_options_type_throws_ArgumentException_naming_the_type(ImageFormat format)
    {
        var ex = Should.Throw<ArgumentException>(() => Create(format, new ArrayBufferWriter<byte>(), new ForeignOptions()));
        ex.ParamName.ShouldBe("options");
        ex.Message.ShouldContain(nameof(ForeignOptions));
        // The same through the facade.
        Should.Throw<ArgumentException>(() => ImageCodecs.CreateEncoder(format, new ArrayBufferWriter<byte>(), new ForeignOptions()))
            .ParamName.ShouldBe("options");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Another_formats_options_throw_and_the_message_lists_the_accepted_types(ImageFormat format)
    {
        foreach (var other in AllFormats.Where(f => f != format))
        {
            foreach (var options in OptionsOf(other))
            {
                if (OptionsOf(format).Any(o => o.GetType() == options.GetType()))
                    continue;
                var ex = Should.Throw<ArgumentException>(() => Create(format, new ArrayBufferWriter<byte>(), options),
                    $"{format} given {options.GetType().Name}");
                ex.ParamName.ShouldBe("options");
                ex.Message.ShouldContain(options.GetType().Name);
                foreach (var accepted in OptionsOf(format))
                    ex.Message.ShouldContain(accepted.GetType().Name);
            }
        }
    }

    [Fact]
    public void Webp_message_names_the_received_and_accepted_types()
    {
        var ex = Should.Throw<ArgumentException>(() => WebpImageEncoder.Create(new ArrayBufferWriter<byte>(), new JpegEncodeOptions()));
        ex.Message.ShouldStartWith(
            "WebP encoding takes WebpLossyEncodeOptions, WebpLosslessEncodeOptions or WebpAnimationEncodeOptions, not JpegEncodeOptions.");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Null_and_the_formats_own_options_encode(ImageFormat format)
    {
        var rgb = Rgb();
        ImageCodecs.DetectFormat(ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, format, cancellationToken: Ct)).ShouldBe(format);
        foreach (var options in OptionsOf(format))
            ImageCodecs.DetectFormat(ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, format, options: options, cancellationToken: Ct)).ShouldBe(format);
    }

    // The FourCC of a RIFF file's first chunk.
    private static string FourCc(byte[] riff) => System.Text.Encoding.ASCII.GetString(riff, 12, 4);

    [Fact]
    public void Webp_with_no_options_is_lossy_and_lossless_needs_its_options()
    {
        var rgb = Rgb();
        var none = ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Webp, cancellationToken: Ct);
        FourCc(none).ShouldBe("VP8 ");
        none.ShouldBe(WebpEncoder.EncodeLossy(rgb, W, H, PixelFormat.Rgb24, new WebpLossyEncodeOptions()));
        FourCc(ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Webp, options: new WebpLossyEncodeOptions(), cancellationToken: Ct)).ShouldBe("VP8 ");
        FourCc(ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Webp, options: new WebpLosslessEncodeOptions(), cancellationToken: Ct)).ShouldBe("VP8L");
    }

    [Fact]
    public void Webp_with_no_options_and_several_frames_is_a_lossy_animation()
    {
        var output = new ArrayBufferWriter<byte>();
        using (var session = WebpImageEncoder.Create(output))
        {
            for (var i = 0; i < 2; i++)
            {
                var frame = Rgb();
                frame[0] = (byte)i;
                session.AddFrame(W, H, PixelFormat.Rgb24, duration: TimeSpan.FromMilliseconds(50)).WriteRows(frame, H, cancellationToken: Ct);
            }
            session.Commit(Ct);
        }
        var file = output.WrittenSpan.ToArray();
        FourCc(file).ShouldBe("VP8X");
        var text = System.Text.Encoding.Latin1.GetString(file);
        text.ShouldContain("ANMF");
        text.ShouldContain("VP8 ");
        text.ShouldNotContain("VP8L");
    }

    [Fact]
    public void Stream_and_buffer_writer_overloads_write_the_bytes_the_array_overload_returns()
    {
        var rgb = Rgb();
        foreach (var format in AllFormats)
        {
            var expected = ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, format, cancellationToken: Ct);

            var writer = new ArrayBufferWriter<byte>();
            ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, format, writer, cancellationToken: Ct);
            writer.WrittenSpan.ToArray().ShouldBe(expected, format.ToString());

            using var stream = new MemoryStream();
            ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, format, stream, cancellationToken: Ct);
            stream.ToArray().ShouldBe(expected, format.ToString());
        }
    }

    [Fact]
    public void Overloads_resolve_for_every_call_shape()
    {
        var rgb = Rgb();
        var writer = new ArrayBufferWriter<byte>();
        using var stream = new MemoryStream();
        var jpeg = new JpegEncodeOptions { Quality = 70 };
        var meta = new ImageMetadata();
        var ct = TestContext.Current.CancellationToken;

        var a = ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, cancellationToken: Ct);                          // byte[]
        var b = ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, 0, jpeg, cancellationToken: Ct);                 // byte[], positional stride
        var c = ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, stride: W * 3, options: jpeg, metadata: meta, cancellationToken: ct);
        var d = ImageCodecs.Encode(rgb.AsSpan(), W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, options: jpeg, cancellationToken: Ct);
        ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, writer, cancellationToken: Ct);                          // IBufferWriter<byte>
        ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, writer, W * 3, jpeg, meta, ct);
        ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, stream, cancellationToken: Ct);                          // Stream
        ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, stream, stride: 0, options: jpeg, cancellationToken: ct);
        ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, destination: stream, cancellationToken: Ct);
        ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Jpeg, destination: writer, options: jpeg, cancellationToken: Ct);

        a.Length.ShouldBeGreaterThan(0);
        c.ShouldBe(b);
        d.ShouldBe(b);
        // The writer got shapes 5, 6 and 10; the stream shapes 7, 8 and 9.
        writer.WrittenCount.ShouldBe(a.Length + b.Length + b.Length);
        stream.Length.ShouldBe(a.Length + b.Length + a.Length);
    }

    [Fact]
    public void Overloads_refuse_a_null_destination_and_a_foreign_options_type()
    {
        var rgb = Rgb();
        Should.Throw<ArgumentNullException>(() => ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Png, (Stream)null!, cancellationToken: Ct));
        Should.Throw<ArgumentNullException>(() => ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Png, (IBufferWriter<byte>)null!, cancellationToken: Ct));
        Should.Throw<ArgumentException>(() => ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Webp, options: new JpegEncodeOptions(), cancellationToken: Ct))
            .ParamName.ShouldBe("options");
        Should.Throw<ArgumentException>(() => ImageCodecs.Encode(rgb, W, H, PixelFormat.Rgb24, ImageFormat.Webp, new MemoryStream(), options: new JpegEncodeOptions(), cancellationToken: Ct));
    }
}
