using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="ImageInfo.Format"/> from every codec, and <see cref="SimdCodecs.DetectFormat"/> on the
/// decodable formats and on the signatures it only recognises.
/// </summary>
public sealed class ImageFormatTests
{
    private static byte[] Fixture(string directory, string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", directory, name));

    private static readonly byte[] Rgb = Enumerable.Range(0, 8 * 6 * 3).Select(i => (byte)(i * 5)).ToArray();

    public static TheoryData<string, ImageFormat> Decodable => new()
    {
        { "png", ImageFormat.Png },
        { "jpeg", ImageFormat.Jpeg },
        { "ultra-hdr", ImageFormat.Jpeg },
        { "tiff", ImageFormat.Tiff },
        { "jbig2", ImageFormat.Jbig2 },
        { "j2k", ImageFormat.Jpeg2000 },
        { "jp2", ImageFormat.Jpeg2000 },
        { "webp", ImageFormat.Webp },
        { "webp-anim", ImageFormat.Webp },
        { "gif", ImageFormat.Gif },
    };

    private static byte[] Sample(string kind) => kind switch
    {
        "png" => PngWriter.EncodeRgb8(Rgb, 8, 6),
        "jpeg" => JpegEncoder.Encode(Rgb, 8, 6, 3),
        "ultra-hdr" => GainMapFacadeTests.BuildUltraHdr(out _),
        "tiff" => Tiff().GetAwaiter().GetResult(),
        "jbig2" => Fixture("jbig2", "sym.jb2"),
        "j2k" => Fixture("jpeg2000", "rgb-struct64.j2k"),
        "jp2" => Fixture("jpeg2000", "jp2-rgb-struct64.jp2"),
        "webp" => Fixture("webp", "lossless-palette-16.webp"),
        "webp-anim" => Fixture("webp", "anim-img2webp-mixed.webp"),
        "gif" => GifEncoder.Encode(Rgb, 8, 6, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task<byte[]> Tiff()
    {
        using var stream = new MemoryStream();
        await using (var writer = TiffWriter.Create(stream))
        {
            await writer.AddPageAsync(Rgb, 8, 6);
            await writer.FlushAsync();
        }

        return stream.ToArray();
    }

    [Theory]
    [MemberData(nameof(Decodable))]
    public void Every_codec_reports_its_format(string kind, ImageFormat format)
    {
        var data = Sample(kind);

        SimdCodecs.DetectFormat(data).ShouldBe(format);
        SimdCodecs.TryReadInfo(data, out var info).ShouldBeTrue();
        info.Format.ShouldBe(format);
        SimdCodecs.TryOpenFrames(data, out var reader).ShouldBeTrue();
        using (reader)
        {
            for (var i = 0; i < reader.FrameCount; i++)
            {
                reader.TryGetFrameInfo(i, out var frame).ShouldBeTrue();
                frame.Info.Format.ShouldBe(format, $"frame {i}");
            }
        }
    }

    public static TheoryData<byte[], ImageFormat> Recognised => new()
    {
        { [(byte)'B', (byte)'M', 0x36, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0], ImageFormat.Bmp },
        { [0, 0, 1, 0, 1, 0], ImageFormat.Ico },
        { Ftyp("heic", "mif1", "heic"), ImageFormat.Heif },
        { Ftyp("mif1", "mif1", "miaf"), ImageFormat.Heif },
        { Ftyp("avif", "mif1", "miaf"), ImageFormat.Avif },
        // An AVIF brand among the compatible ones wins over a HEIF-generic major brand.
        { Ftyp("mif1", "mif1", "avif"), ImageFormat.Avif },
        { [0xFF, 0x0A, 0xFA, 0x00], ImageFormat.JpegXl },
        { [0, 0, 0, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A], ImageFormat.JpegXl },
        { [(byte)'I', (byte)'I', 0xBC, 0x01, 0x08, 0, 0, 0], ImageFormat.JpegXr },
        { [0x76, 0x2F, 0x31, 0x01, 2, 0, 0, 0], ImageFormat.OpenExr },
        { "8BPS\0\u0001"u8.ToArray(), ImageFormat.Psd },
        { "qoif\0\0\0\u0001"u8.ToArray(), ImageFormat.Qoi },
        // An ISO-BMFF file that is neither (an MP4).
        { Ftyp("isom", "isom", "mp41"), ImageFormat.Unknown },
        { "not an image"u8.ToArray(), ImageFormat.Unknown },
        { [], ImageFormat.Unknown },
    };

    // An ISO-BMFF 'ftyp' box: size, type, major brand, minor version, compatible brands.
    private static byte[] Ftyp(string major, params string[] compatible)
    {
        var brands = new[] { major, "\0\0\0\0" }.Concat(compatible).SelectMany(b => b.Select(c => (byte)c));
        var body = "ftyp"u8.ToArray().Concat(brands).ToArray();
        var size = body.Length + 4;
        return new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size }.Concat(body).ToArray();
    }

    [Theory]
    [MemberData(nameof(Recognised))]
    public void Recognises_formats_it_does_not_decode(byte[] header, ImageFormat format)
    {
        SimdCodecs.DetectFormat(header).ShouldBe(format);
        SimdCodecs.CanDecode(header).ShouldBeFalse();
    }

    [Fact]
    public void IsDecodable_names_exactly_the_registered_formats()
    {
        ImageFormat[] decodable =
            [ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Tiff, ImageFormat.Jbig2, ImageFormat.Jpeg2000, ImageFormat.Webp, ImageFormat.Gif];
        foreach (var format in Enum.GetValues<ImageFormat>())
            SimdCodecs.IsDecodable(format).ShouldBe(decodable.Contains(format), format.ToString());
    }

    [Fact]
    public void Format_takes_part_in_equality()
    {
        var info = new ImageInfo(4, 4, 3, SampleFormat.UInt8);
        info.Format.ShouldBe(ImageFormat.Unknown);
        (info with { Format = ImageFormat.Png }).ShouldNotBe(info);
        (info with { Format = ImageFormat.Png }).ShouldBe(info with { Format = ImageFormat.Png });
        Should.Throw<ArgumentOutOfRangeException>(() => info with { Format = (ImageFormat)200 });
    }
}
