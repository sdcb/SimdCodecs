using System.Buffers.Binary;
using System.Text;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Pnm;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Netpbm against foreign bytes: every variant ImageMagick writes (PBM, PGM, PPM plain and raw, 8 and 16 bits; PAM
/// of 1 to 4 channels; PFM) decodes to exactly ImageMagick's reading, every encoder format reads back in
/// ImageMagick as the pixels meant, and hand-written files cover what ImageMagick does not write: comments and
/// odd whitespace everywhere, maxima other than 2^n − 1, samples above the maximum, unknown tuple types,
/// big-endian PFM, and a second image after the first.
/// </summary>
public sealed class PnmTests
{
    private static readonly int[] Widths = [1, 3, 7, 8, 9, 17, 33, 1100];

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static MagickImage Source(int width, int height, int channels, int seed)
    {
        var rgba = new byte[width * height * 4];
        new Random(seed).NextBytes(rgba);
        var image = new MagickImage();
        image.ReadPixels(rgba, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGBA"));
        if (channels is 1 or 2) image.ColorType = channels == 1 ? ColorType.Grayscale : ColorType.GrayscaleAlpha;
        if (channels is 1 or 3) image.Alpha(AlphaOption.Off);
        return image;
    }

    /// <summary>ImageMagick's 16-bit reading of a file, in <paramref name="channels"/> channels.</summary>
    private static ushort[] MagickSamples(byte[] data, MagickFormat format, int channels)
    {
        using var image = new MagickImage(data, format);
        using var pixels = image.GetPixels();
        var mapping = channels switch { 1 => "R", 2 => "RA", 3 => "RGB", _ => "RGBA" };
        return pixels.ToShortArray(mapping)!;
    }

    private static ushort[] Native16(PnmImage image) => image.SampleFormat == SampleFormat.UInt16
        ? [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.Pixels)]
        : [.. image.Pixels.Select(v => (ushort)(v * 257))];

    public static TheoryData<string> MagickVariants() =>
    [
        "pbm 1", "pbm 1 plain", "pgm 1", "pgm 1 16", "pgm 1 plain", "pgm 1 plain 16", "ppm 3", "ppm 3 16", "ppm 3 plain",
        "pam 1", "pam 2", "pam 3", "pam 4", "pam 4 16", "pam 2 16",
    ];

    [Theory]
    [MemberData(nameof(MagickVariants))]
    public void Files_imagemagick_writes_decode_to_its_own_reading(string variant)
    {
        var parts = variant.Split(' ');
        var format = parts[0] switch { "pbm" => MagickFormat.Pbm, "pgm" => MagickFormat.Pgm, "ppm" => MagickFormat.Ppm, _ => MagickFormat.Pam };
        var channels = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        foreach (var width in Widths)
        {
            using var image = Source(width, 5, channels, width * 13 + variant.Length);
            image.Depth = parts.Contains("16") ? 16u : 8u;
            if (parts.Contains("plain")) image.Settings.Compression = CompressionMethod.NoCompression;
            var data = image.ToByteArray(format);

            var info = PnmDecoder.ReadInfo(data);
            info.IsPlain.ShouldBe(parts.Contains("plain"), $"{variant} width {width}");
            info.Channels.ShouldBe(channels, $"{variant} width {width}");
            var decoded = PnmDecoder.Decode(data);
            Native16(decoded).ShouldBe(MagickSamples(data, format, channels), $"{variant} width {width}");
        }
    }

    [Fact]
    public void Pfm_imagemagick_writes_decodes_to_the_float_of_each_8_bit_value()
    {
        foreach (var channels in new[] { 1, 3 })
        foreach (var width in new[] { 1, 9, 33 })
        {
            // 8-bit samples (grey ones as equal R, G and B), so each converts to float exactly once.
            var source = new byte[width * 4 * channels];
            new Random(width + channels).NextBytes(source);
            var rgb = channels == 3 ? source : [.. source.SelectMany(v => new[] { v, v, v })];
            using var image = new MagickImage();
            image.ReadPixels(rgb, new PixelReadSettings((uint)width, 4, StorageType.Char, "RGB"));
            if (channels == 1) image.ColorType = ColorType.Grayscale;
            var data = image.ToByteArray(MagickFormat.Pfm);
            var decoded = PnmDecoder.Decode(data);
            decoded.SampleFormat.ShouldBe(SampleFormat.Float32);
            decoded.Info.Format.ShouldBe(channels == 1 ? PnmFormat.FloatGraymap : PnmFormat.FloatPixmap);
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(decoded.Pixels).ToArray()
                .ShouldBe([.. source.Select(v => (float)(v / 255.0))], $"{channels} channels, width {width}");
        }
    }

    public static TheoryData<PnmFormat, int> EncoderFormats() => new()
    {
        { PnmFormat.PlainBitmap, 0 }, { PnmFormat.Bitmap, 0 }, { PnmFormat.PlainGraymap, 0 }, { PnmFormat.Graymap, 0 },
        { PnmFormat.PlainPixmap, 0 }, { PnmFormat.Pixmap, 0 }, { PnmFormat.Arbitrary, 0 }, { PnmFormat.Auto, 0 },
        { PnmFormat.Graymap, 65535 }, { PnmFormat.Pixmap, 65535 }, { PnmFormat.PlainPixmap, 65535 }, { PnmFormat.Pixmap, 1 },
    };

    [Theory]
    [MemberData(nameof(EncoderFormats))]
    public void Encoded_files_read_back_in_imagemagick_as_the_pixels_meant(PnmFormat format, int maxValue)
    {
        foreach (var input in new[] { ImagePixelFormat.Rgba32, ImagePixelFormat.Bgr24, ImagePixelFormat.Gray8 })
        foreach (var width in Widths)
        {
            var rgba = new byte[width * 3 * 4];
            new Random(width).NextBytes(rgba);
            var bpp = PixelConverter.BytesPerPixel(input);
            var pixels = new byte[width * 3 * bpp];
            PixelConverter.ConvertRow(rgba, 4, SampleFormat.UInt8, pixels, input, width * 3);
            var data = PnmEncoder.Encode(pixels, width, 3, input, new PnmEncodeOptions { Format = format, MaxValue = maxValue });

            // The RGBA the input holds, then the target's channels from it, worked out independently.
            var source = new byte[width * 3 * 4];
            for (var i = 0; i < width * 3; i++)
            {
                int r = rgba[4 * i], g = rgba[4 * i + 1], b = rgba[4 * i + 2], a = rgba[4 * i + 3];
                if (input == ImagePixelFormat.Gray8) r = g = b = (77 * r + 150 * g + 29 * b + 128) >> 8;
                if (input != ImagePixelFormat.Rgba32) a = 255;
                (source[4 * i], source[4 * i + 1], source[4 * i + 2], source[4 * i + 3]) = ((byte)r, (byte)g, (byte)b, (byte)a);
            }
            var resolved = format == PnmFormat.Auto ? (bpp == 1 ? PnmFormat.Graymap : bpp == 3 ? PnmFormat.Pixmap : PnmFormat.Arbitrary) : format;
            var channels = resolved switch
            {
                PnmFormat.PlainBitmap or PnmFormat.Bitmap or PnmFormat.PlainGraymap or PnmFormat.Graymap => 1,
                PnmFormat.PlainPixmap or PnmFormat.Pixmap => 3,
                _ => bpp,
            };
            var max = resolved is PnmFormat.PlainBitmap or PnmFormat.Bitmap ? 1 : maxValue == 0 ? 255 : maxValue;
            var expected = new ushort[width * 3 * channels];
            for (var i = 0; i < width * 3; i++)
            {
                int r = source[4 * i], g = source[4 * i + 1], b = source[4 * i + 2];
                var grey = bpp == 1 ? r : (77 * r + 150 * g + 29 * b + 128) >> 8;
                int[] values = channels switch
                {
                    1 => [resolved is PnmFormat.PlainBitmap or PnmFormat.Bitmap ? (grey < 128 ? 0 : 255) : grey],
                    3 => [r, g, b],
                    _ => [r, g, b, source[4 * i + 3]],
                };
                if (channels == 1 && bpp == 1 && resolved == PnmFormat.Arbitrary) values = [r];
                for (var c = 0; c < channels; c++)
                {
                    // Scale 0..255 to 0..max and back to 16 bits, rounding each way.
                    var stored = (values[c] * max + 127) / 255;
                    expected[i * channels + c] = (ushort)((stored * 65535 + max / 2) / max);
                }
            }

            var magickFormat = resolved switch
            {
                PnmFormat.PlainBitmap or PnmFormat.Bitmap => MagickFormat.Pbm,
                PnmFormat.PlainGraymap or PnmFormat.Graymap => MagickFormat.Pgm,
                PnmFormat.PlainPixmap or PnmFormat.Pixmap => MagickFormat.Ppm,
                _ => MagickFormat.Pam,
            };
            MagickSamples(data, magickFormat, channels).ShouldBe(expected, $"{input} as {format} max {maxValue}, width {width}");

            using var stream = new MemoryStream();
            PnmEncoder.Encode(pixels, width, 3, input, stream, new PnmEncodeOptions { Format = format, MaxValue = maxValue });
            stream.ToArray().ShouldBe(data, "the stream overload writes the same bytes");
        }
    }

    [Fact]
    public void Plain_rasters_keep_lines_within_70_characters_and_start_each_row_on_a_line()
    {
        var grey = Enumerable.Range(0, 100 * 2).Select(i => (byte)(i * 37)).ToArray();
        foreach (var format in new[] { PnmFormat.PlainBitmap, PnmFormat.PlainGraymap, PnmFormat.PlainPixmap })
        {
            var text = Encoding.ASCII.GetString(PnmEncoder.Encode(grey, 100, 2, ImagePixelFormat.Gray8, new PnmEncodeOptions { Format = format }));
            var lines = text.Split('\n');
            lines.ShouldAllBe(l => l.Length <= 70, format.ToString());
            text.ShouldEndWith("\n");
        }
    }

    [Fact]
    public void Sixteen_bit_input_writes_its_samples()
    {
        ushort[] samples = [0, 1, 255, 256, 4095, 65535];
        var data = PnmEncoder.Encode(samples, 3, 2, 1);
        Encoding.ASCII.GetString(data, 0, 13).ShouldBe("P5\n3 2\n65535\n");
        MagickSamples(data, MagickFormat.Pgm, 1).ShouldBe(samples);
        PnmDecoder.Decode(data).Pixels.ShouldBe(System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(samples).ToArray());

        var plain = PnmEncoder.Encode(samples, 3, 2, 1, new PnmEncodeOptions { Format = PnmFormat.PlainGraymap, MaxValue = 4095 });
        Encoding.ASCII.GetString(plain).ShouldBe("P2\n3 2\n4095\n0 0 16\n16 256 4095\n");
        Should.Throw<ArgumentException>(() => PnmEncoder.Encode(samples, 3, 2, 1, new PnmEncodeOptions { Format = PnmFormat.Pixmap }));
    }

    [Fact]
    public void Pfm_round_trips_and_stores_rows_bottom_up_little_endian()
    {
        float[] samples = [0f, 1.5f, -2f, float.MaxValue, 1e-30f, 0.25f];
        var data = PnmEncoder.EncodePfm(samples, 2, 3, 1);
        Encoding.ASCII.GetString(data, 0, 12).ShouldBe("Pf\n2 3\n-1.0\n");
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(12)).ShouldBe(1e-30f, "the bottom row comes first");
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(PnmDecoder.Decode(data).Pixels).ToArray().ShouldBe(samples);
        using var stream = new MemoryStream();
        PnmEncoder.EncodePfm(samples, 2, 3, 1, stream);
        stream.ToArray().ShouldBe(data);
    }

    public static TheoryData<string, byte[], int, byte[]> HandWritten() => new()
    {
        // name, file, channels, expected 8-bit samples (16-bit ones are checked separately)
        { "comments and odd whitespace", Ascii("P2 # a comment\n#another\n 3\t\r2 #c\n7\n0 7 3\n# in the raster\n 8 1\v2"), 1,
            [0, 255, 109, 255, 36, 73] }, // (v·255 + 3) / 7, 8 clamped to 7
        { "plain PBM without separators", Ascii("P1\n4 2\n0101\n1 1 0 0"), 1, [255, 0, 255, 0, 0, 0, 255, 255] },
        { "raw PBM padded rows", [.. Ascii("P4\n10 2\n"), 0b1010_0000, 0b1100_0000, 0b0000_0000, 0b0100_0000], 1,
            [0, 255, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0] },
        { "maximum 1 raw PGM", [.. Ascii("P5\n3 1\n1\n"), 0, 1, 2], 1, [0, 255, 255] },
        { "PAM with an unknown tuple type over two lines", [.. Ascii("P7\n# made by hand\nWIDTH 2\nHEIGHT 1\nDEPTH 2\nMAXVAL 255\nTUPLTYPE FOO\nTUPLTYPE BAR\nENDHDR\n"), 1, 2, 3, 4], 2,
            [1, 2, 3, 4] },
        { "PAM BLACKANDWHITE: 1 is white", [.. Ascii("P7\nWIDTH 3\nHEIGHT 1\nDEPTH 1\nMAXVAL 1\nTUPLTYPE BLACKANDWHITE\nENDHDR\n"), 0, 1, 0], 1, [0, 255, 0] },
        { "a second image after the first", [.. Ascii("P5\n2 1\n255\n"), 9, 8, .. Ascii("P5\n1 1\n255\n"), 7], 1, [9, 8] },
        { "plain PPM", Ascii("P3\n2 1\n255\n1 2 3 4 5 6\n"), 3, [1, 2, 3, 4, 5, 6] },
    };

    [Theory]
    [MemberData(nameof(HandWritten))]
    public void Hand_written_files_decode_as_worked_out(string name, byte[] data, int channels, byte[] expected)
    {
        var image = PnmDecoder.Decode(data);
        image.Channels.ShouldBe(channels, name);
        image.Pixels.ShouldBe(expected, name);
        if (channels != 2)
        {
            // Every 8-bit layout through the adapter agrees with the native samples.
            var grey = new byte[image.Width * image.Height];
            PnmImageDecoder.TryDecodeInto(data, grey, 0, ImagePixelFormat.Gray8).ShouldBeTrue(name);
            if (channels == 1) grey.ShouldBe(expected, name);
        }
    }

    [Fact]
    public void A_maximum_above_255_scales_to_16_bits_and_its_high_byte()
    {
        // 300: (v · 65535 + 150) / 300, with 301 clamped to 300.
        byte[] data = [.. Ascii("P5\n4 1\n300\n"), 0, 0, 0, 150, 1, 44, 1, 45];
        var image = PnmDecoder.Decode(data);
        image.SampleFormat.ShouldBe(SampleFormat.UInt16);
        Native16(image).ShouldBe(new ushort[] { 0, 32768, 65535, 65535 });
        var grey = new byte[4];
        PnmDecoder.DecodeTo(data, grey, ImagePixelFormat.Gray8);
        grey.ShouldBe(new byte[] { 0, 128, 255, 255 });
        PnmDecoder.Decode(Ascii("P2\n2 1\n300\n150 301")).Pixels.ShouldBe(System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>([32768, 65535]).ToArray());
    }

    [Fact]
    public void Pfm_reads_either_byte_order_bottom_up_and_reports_the_scale()
    {
        // Big-endian (positive scale) RGB 1x2: bottom row first.
        var bottom = new[] { 1f, 2f, 3f };
        var top = new[] { -4f, 0.5f, 6f };
        var big = new byte[24];
        for (var i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteSingleBigEndian(big.AsSpan(4 * i), bottom[i]);
            BinaryPrimitives.WriteSingleBigEndian(big.AsSpan(12 + 4 * i), top[i]);
        }
        var data = (byte[])[.. Ascii("PF\n1 2\n2.5\n"), .. big];
        var image = PnmDecoder.Decode(data);
        image.Info.Scale.ShouldBe(2.5f);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(image.Pixels).ToArray().ShouldBe([.. top, .. bottom]);
        PfmImageDecoder.TryDecode(data, out var decoded).ShouldBeTrue();
        decoded.ColorEncoding.Transfer.ShouldBe(TransferFunction.Linear);
        PfmImageDecoder.TryDecodeInto(data, new byte[8], 0, ImagePixelFormat.Rgba32).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => PnmDecoder.DecodeTo(data, new byte[8]));
        PnmImageDecoder.TryDecode(data, out _).ShouldBeFalse("PFM has its own adapter");
    }

    public static TheoryData<string, byte[]> Malformed() => new()
    {
        { "no magic", Ascii("P8\n1 1\n255\n\0") },
        { "missing height", Ascii("P5\n1\n") },
        { "letters for width", Ascii("P5\nx 1\n255\n\0") },
        { "width with a suffix", Ascii("P5\n1x 1\n255\n\0") },
        { "maximum 0", Ascii("P5\n1 1\n0\n\0") },
        { "maximum 65536", Ascii("P5\n1 1\n65536\n\0\0") },
        { "zero width", Ascii("P5\n0 1\n255\n") },
        { "above 2^28 pixels", Ascii("P5\n65536 4097\n255\n") },
        { "width above int", Ascii("P5\n99999999999 1\n255\n") },
        { "raster cut", Ascii("P6\n2 1\n255\n12345") },
        { "16-bit raster cut", Ascii("P5\n2 1\n65535\n123") },
        { "no whitespace after the maximum", Ascii("P5\n1 1\n255") },
        { "plain raster cut", Ascii("P2\n3 1\n255\n1 2") },
        { "plain raster with text", Ascii("P2\n2 1\n255\n1 x") },
        { "plain PBM with a 2", Ascii("P1\n2 1\n0 2") },
        { "PAM without ENDHDR", Ascii("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\n") },
        { "PAM without DEPTH", Ascii("P7\nWIDTH 1\nHEIGHT 1\nMAXVAL 255\nENDHDR\n\0") },
        { "PAM depth 5", Ascii("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 5\nMAXVAL 255\nENDHDR\n\0\0\0\0\0") },
        { "PAM unknown keyword", Ascii("P7\nWIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nCOLOUR 3\nENDHDR\n\0") },
        { "PAM repeated WIDTH", Ascii("P7\nWIDTH 1\nWIDTH 1\nHEIGHT 1\nDEPTH 1\nMAXVAL 255\nENDHDR\n\0") },
        { "PFM scale 0", [.. Ascii("Pf\n1 1\n0\n"), 0, 0, 0, 0] },
        { "PFM scale text", [.. Ascii("Pf\n1 1\nabc\n"), 0, 0, 0, 0] },
        { "PFM scale infinite", [.. Ascii("Pf\n1 1\n1e99\n"), 0, 0, 0, 0] },
        { "PFM raster cut", [.. Ascii("PF\n1 1\n-1\n"), 0, 0, 0, 0] },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_files_are_refused(string name, byte[] data)
    {
        Should.Throw<InvalidDataException>(() => PnmDecoder.Decode(data), name);
        PnmImageDecoder.TryDecode(data, out _).ShouldBeFalse(name);
        PfmImageDecoder.TryDecode(data, out _).ShouldBeFalse(name);
        PnmImageDecoder.TryDecodeInto(data, new byte[64], 0, ImagePixelFormat.Rgba32).ShouldBeFalse(name);
    }

    [Fact]
    public void Encoder_arguments_are_checked()
    {
        Should.Throw<ArgumentException>(() => PnmEncoder.Encode(new byte[4], 0, 4, ImagePixelFormat.Gray8));
        Should.Throw<ArgumentException>(() => PnmEncoder.Encode(new byte[3], 2, 2, ImagePixelFormat.Gray8));
        Should.Throw<ArgumentException>(() => PnmEncoder.Encode(new byte[4], 2, 2, ImagePixelFormat.Gray8, new PnmEncodeOptions { MaxValue = 70000 }));
        Should.Throw<ArgumentException>(() => PnmEncoder.Encode(new byte[4], 2, 2, ImagePixelFormat.Gray8, new PnmEncodeOptions { Format = PnmFormat.FloatPixmap }));
        Should.Throw<ArgumentException>(() => PnmEncoder.Encode(new byte[4], 2, 2, 5));
        Should.Throw<ArgumentException>(() => PnmEncoder.Encode(new byte[4], 2, 2, ImagePixelFormat.Gray8, new PnmEncodeOptions { Format = PnmFormat.Arbitrary, TupleType = "A\nB" }));
        Should.Throw<ArgumentException>(() => PnmEncoder.EncodePfm(new float[8], 2, 2, 2));
    }
}
