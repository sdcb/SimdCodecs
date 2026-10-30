using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Qoi;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// QOI against foreign bytes: images ImageMagick writes decode to exactly ImageMagick's reading of them, our
/// encoder's output reads back in ImageMagick as the source pixels, and a hand-assembled stream that uses every
/// chunk (including byte wrap-around in diff and luma) decodes to the pixels worked out by hand.
/// </summary>
public sealed class QoiTests
{
    internal static readonly int[] Widths = [1, 2, 3, 7, 16, 33, 67, 600];

    /// <summary>Pixels with runs, repeats (index hits), small steps (diff / luma) and jumps, as RGBA.</summary>
    internal static byte[] Pattern(int width, int height, bool alpha, int seed)
    {
        var random = new Random(seed);
        var rgba = new byte[width * height * 4];
        byte r = 0, g = 0, b = 0, a = 255;
        var palette = Enumerable.Range(0, 5).Select(_ => (uint)random.Next()).ToArray();
        for (var i = 0; i < width * height; i++)
        {
            switch (random.Next(6))
            {
                case 0: break; // a run
                case 1: r += (byte)random.Next(-2, 2); g += (byte)random.Next(-2, 2); b += (byte)random.Next(-2, 2); break;
                case 2:
                    var dg = random.Next(-32, 32);
                    g += (byte)dg; r += (byte)(dg + random.Next(-8, 8)); b += (byte)(dg + random.Next(-8, 8));
                    break;
                case 3:
                    var c = palette[random.Next(palette.Length)];
                    (r, g, b) = ((byte)c, (byte)(c >> 8), (byte)(c >> 16));
                    if (alpha) a = (byte)(c >> 24);
                    break;
                case 4: r = (byte)random.Next(256); g = (byte)random.Next(256); b = (byte)random.Next(256); break;
                default: if (alpha) a = (byte)random.Next(256); break;
            }
            rgba[4 * i] = r; rgba[4 * i + 1] = g; rgba[4 * i + 2] = b; rgba[4 * i + 3] = a;
        }
        return rgba;
    }

    private static byte[] MagickRgba(byte[] qoi) => BmpMagickTests.MagickRgba(qoi, MagickFormat.Qoi);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Images_imagemagick_writes_decode_to_its_own_reading(bool alpha)
    {
        foreach (var width in Widths)
        {
            var source = Pattern(width, 5, alpha, width);
            using var image = new MagickImage();
            image.ReadPixels(source, new PixelReadSettings((uint)width, 5, StorageType.Char, "RGBA"));
            if (!alpha) image.Alpha(AlphaOption.Off);
            var qoi = image.ToByteArray(MagickFormat.Qoi);

            var info = QoiDecoder.ReadInfo(qoi);
            info.Channels.ShouldBe(alpha ? 4 : 3, $"width {width}");
            var rgba = new byte[width * 5 * 4];
            QoiDecoder.DecodeTo(qoi, rgba);
            rgba.ShouldBe(MagickRgba(qoi), $"width {width}");
            if (alpha) rgba.ShouldBe(source, $"width {width}");
        }
    }

    [Theory]
    [InlineData(ImagePixelFormat.Rgba32)]
    [InlineData(ImagePixelFormat.Bgra32)]
    [InlineData(ImagePixelFormat.Rgb24)]
    [InlineData(ImagePixelFormat.Bgr24)]
    [InlineData(ImagePixelFormat.Gray8)]
    public void Encoded_images_read_back_in_imagemagick_as_the_source(ImagePixelFormat format)
    {
        foreach (var width in Widths)
        {
            var rgba = Pattern(width, 6, alpha: true, seed: width * 7);
            var bpp = PixelConverter.BytesPerPixel(format);
            // A padded stride, to show rows are read where the stride says.
            var stride = width * bpp + 3;
            var input = new byte[stride * 6];
            for (var y = 0; y < 6; y++)
                PixelConverter.ConvertRow(rgba.AsSpan(y * width * 4, width * 4), 4, SampleFormat.UInt8, input.AsSpan(y * stride), format, width);

            var qoi = QoiEncoder.Encode(input, width, 6, format, stride: stride);
            var expected = new byte[width * 6 * 4];
            for (var y = 0; y < 6; y++)
            {
                // What the input holds, as RGBA: a 3-channel or grey input is opaque.
                var row = new byte[width * 4];
                PixelConverter.ConvertRow(rgba.AsSpan(y * width * 4, width * 4), 4, SampleFormat.UInt8, row, format, width);
                var back = format switch
                {
                    ImagePixelFormat.Bgra32 or ImagePixelFormat.Bgr24 => SwapToRgba(row, format == ImagePixelFormat.Bgra32 ? 4 : 3, width),
                    _ => Widen(row, bpp, width),
                };
                back.CopyTo(expected, y * width * 4);
            }
            MagickRgba(qoi).ShouldBe(expected, $"{format} width {width}");
            QoiDecoder.Decode(qoi).Channels.ShouldBe(bpp == 4 ? 4 : 3);

            using var stream = new MemoryStream();
            QoiEncoder.Encode(input, width, 6, format, stream, stride: stride);
            stream.ToArray().ShouldBe(qoi, $"{format} width {width}: the stream overload writes the same bytes");
        }
    }

    private static byte[] SwapToRgba(byte[] bgr, int channels, int width)
    {
        var rgba = new byte[width * 4];
        for (var x = 0; x < width; x++)
        {
            rgba[4 * x] = bgr[channels * x + 2];
            rgba[4 * x + 1] = bgr[channels * x + 1];
            rgba[4 * x + 2] = bgr[channels * x];
            rgba[4 * x + 3] = channels == 4 ? bgr[4 * x + 3] : (byte)255;
        }
        return rgba;
    }

    private static byte[] Widen(byte[] row, int bpp, int width)
    {
        var rgba = new byte[width * 4];
        for (var x = 0; x < width; x++)
        {
            for (var c = 0; c < 3; c++) rgba[4 * x + c] = row[bpp * x + (bpp == 1 ? 0 : c)];
            rgba[4 * x + 3] = bpp == 4 ? row[4 * x + 3] : (byte)255;
        }
        return rgba;
    }

    internal static byte[] Header(int width, int height, int channels, int colorSpace = 0) =>
    [
        (byte)'q', (byte)'o', (byte)'i', (byte)'f',
        (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
        (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        (byte)channels, (byte)colorSpace,
    ];

    private static readonly byte[] End = [0, 0, 0, 0, 0, 0, 0, 1];

    /// <summary>Every chunk once, 3x3 pixels, with the pixels each one makes worked out by hand.</summary>
    internal static (byte[] Stream, byte[] Rgba) HandStream()
    {
        byte[] chunks =
        [
            0xFF, 10, 20, 30, 40,        // RGBA                                  -> 10, 20, 30, 40
            0x4E,                        // DIFF r −2, g +1, b 0                  ->  8, 21, 30, 40
            0x80, 0xF0,                  // LUMA g −32, r = g + 7, b = g − 8      -> 239, 245, 246, 40 (wrapped)
            0xFE, 1, 2, 3,               // RGB                                   ->  1,  2,  3, 40
            0x0C,                        // INDEX 12 = (10·3 + 20·5 + 30·7 + 40·11) mod 64 -> 10, 20, 30, 40
            0xC1,                        // RUN of 2                              -> twice more
            0xFF, 255, 0, 255, 0,        // RGBA                                  -> 255, 0, 255, 0
            0x73,                        // DIFF r +1, g −2, b +1 (wrapping)      ->  0, 254, 0, 0
        ];
        byte[] rgba =
        [
            10, 20, 30, 40, 8, 21, 30, 40, 239, 245, 246, 40,
            1, 2, 3, 40, 10, 20, 30, 40, 10, 20, 30, 40,
            10, 20, 30, 40, 255, 0, 255, 0, 0, 254, 0, 0,
        ];
        return ([.. Header(3, 3, 4), .. chunks, .. End], rgba);
    }

    [Fact]
    public void A_hand_assembled_stream_with_every_chunk_decodes_as_worked_out()
    {
        var (stream, rgba) = HandStream();
        QoiDecoder.Decode(stream).Pixels.ShouldBe(rgba);
        MagickRgba(stream).ShouldBe(rgba, "ImageMagick agrees with the working");
    }

    [Fact]
    public void Index_hash_matches_its_definition()
    {
        var random = new Random(5);
        for (var i = 0; i < 100_000; i++)
        {
            var px = (uint)random.Next() ^ ((uint)random.Next() << 16);
            int r = (byte)px, g = (byte)(px >> 8), b = (byte)(px >> 16), a = (byte)(px >> 24);
            QoiCodec.Hash(px).ShouldBe((r * 3 + g * 5 + b * 7 + a * 11) % 64);
        }
    }

    [Fact]
    public void Every_layout_and_stride_is_the_converted_image()
    {
        var rgba = Pattern(37, 9, alpha: true, seed: 3);
        var qoi = QoiEncoder.Encode(rgba, 37, 9, ImagePixelFormat.Rgba32);
        QoiDecoder.Decode(qoi).Pixels.ShouldBe(rgba);
        foreach (var format in Enum.GetValues<ImagePixelFormat>())
        {
            var bpp = PixelConverter.BytesPerPixel(format);
            foreach (var stride in new[] { 0, 37 * bpp + 5 })
            {
                var rowStride = stride == 0 ? 37 * bpp : stride;
                var actual = Enumerable.Repeat((byte)0xA5, rowStride * 8 + 37 * bpp).ToArray();
                QoiDecoder.DecodeTo(qoi, actual, format, stride);
                for (var y = 0; y < 9; y++)
                {
                    var expected = new byte[37 * bpp];
                    PixelConverter.ConvertRow(rgba.AsSpan(y * 37 * 4, 37 * 4), 4, SampleFormat.UInt8, expected, format, 37);
                    actual.AsSpan(y * rowStride, 37 * bpp).ToArray().ShouldBe(expected, $"{format} stride {stride} row {y}");
                    if (y < 8) actual.AsSpan(y * rowStride + 37 * bpp, rowStride - 37 * bpp).ToArray().ShouldAllBe(b => b == 0xA5);
                }
            }
        }
    }

    [Fact]
    public void A_three_channel_image_is_opaque_whatever_its_chunks_say()
    {
        // An RGBA chunk with alpha 7, and an index slot never filled (all zero).
        byte[] stream = [.. Header(2, 1, 3), 0xFF, 1, 2, 3, 7, 0x05, .. End];
        QoiDecoder.Decode(stream).Pixels.ShouldBe(new byte[] { 1, 2, 3, 0, 0, 0 });
        var rgba = new byte[8];
        QoiDecoder.DecodeTo(stream, rgba);
        rgba.ShouldBe(new byte[] { 1, 2, 3, 255, 0, 0, 0, 255 });
    }

    [Fact]
    public void A_run_past_the_last_pixel_ends_the_image_and_the_end_marker_is_not_needed()
    {
        byte[] stream = [.. Header(2, 2, 4), 0xFE, 9, 8, 7, 0xC0 | 61];
        QoiDecoder.Decode(stream).Pixels.ShouldBe(Enumerable.Repeat(new byte[] { 9, 8, 7, 255 }, 4).SelectMany(p => p).ToArray());
    }

    [Fact]
    public void The_colour_space_is_reported_not_applied()
    {
        var rgba = Pattern(5, 3, alpha: false, seed: 1);
        var linear = QoiEncoder.Encode(rgba, 5, 3, ImagePixelFormat.Rgba32, QoiColorSpace.Linear);
        linear[13].ShouldBe((byte)1);
        QoiDecoder.ReadInfo(linear).ColorSpace.ShouldBe(QoiColorSpace.Linear);
        QoiDecoder.Decode(linear).Pixels.ShouldBe(rgba);
        QoiImageDecoder.TryDecode(linear, out var image).ShouldBeTrue();
        image.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        QoiImageDecoder.TryDecode(QoiEncoder.Encode(rgba, 5, 3, ImagePixelFormat.Rgba32), out var srgb).ShouldBeTrue();
        srgb.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
    }

    public static TheoryData<string, byte[]> Malformed() => new()
    {
        { "no magic", [.. "qoig"u8, .. Header(1, 1, 4)[4..], 0xFE, 1, 2, 3, .. End] },
        { "header cut", Header(1, 1, 4)[..13] },
        { "zero width", [.. Header(0, 1, 4), .. End] },
        { "zero height", [.. Header(1, 0, 4), .. End] },
        { "5 channels", [.. Header(1, 1, 5), 0xFE, 1, 2, 3, .. End] },
        { "colour space 2", [.. Header(1, 1, 4, 2), 0xFE, 1, 2, 3, .. End] },
        { "above 2^28 pixels", [.. Header(1 << 15, (1 << 13) + 1, 4), .. End] },
        { "no chunks", Header(2, 1, 4) },
        { "one pixel short", [.. Header(2, 1, 4), 0xFE, 1, 2, 3] },
        { "last chunk cut", [.. Header(2, 1, 4), 0xFE, 1, 2, 3, 0xFF, 1, 2] },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_streams_are_refused(string name, byte[] data)
    {
        Should.Throw<InvalidDataException>(() => QoiDecoder.Decode(data), name);
        QoiImageDecoder.TryDecode(data, out _).ShouldBeFalse(name);
        QoiImageDecoder.TryDecodeInto(data, new byte[64], 0, ImagePixelFormat.Rgba32).ShouldBeFalse(name);
    }

    [Fact]
    public void Encoder_arguments_are_checked()
    {
        Should.Throw<ArgumentException>(() => QoiEncoder.Encode(new byte[16], 0, 4, ImagePixelFormat.Gray8));
        Should.Throw<ArgumentException>(() => QoiEncoder.Encode(new byte[15], 4, 4, ImagePixelFormat.Gray8));
        Should.Throw<ArgumentException>(() => QoiEncoder.Encode(new byte[16], 4, 4, ImagePixelFormat.Gray8, (QoiColorSpace)2));
    }
}
