using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using B = Sdcb.SimdCodecs.Tests.TestTiffBuilder;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="DecodeOptions.Region"/> and <see cref="TiffImageDecoder.TryDecodeInto"/> decode by segment (only the
/// strips or tiles the region touches, straight into the result). The contract is exact: the pixels equal decoding
/// the whole page and cropping it, and converting it with <see cref="PixelConverter"/>, for every layout, planar
/// configuration, predictor, compression and photometric.
/// </summary>
public sealed class TiffRegionDecodeTests
{
    private const int W = 71;
    private const int H = 53;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly PixelRect[] Regions =
    [
        new(0, 0, W, H),
        new(0, 0, 1, 1),
        new(W - 1, H - 1, 1, 1),
        new(5, 3, 40, 1),
        new(17, 9, 33, 31),
        new(31, 15, 2, 38),
        new(0, 20, W, 7),
    ];

    public static TheoryData<string> Files() => [.. Corpus().Keys];

    private static Dictionary<string, byte[]> _corpus = null!;

    private static Dictionary<string, byte[]> Corpus()
    {
        if (_corpus is not null) return _corpus;
        var files = new Dictionary<string, byte[]>();
        var rgb = new byte[W * H * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)((i * 7) ^ (i / 97));

        byte[] Magick(Action<MagickImage> setup, string mapping = "RGB", byte[]? pixels = null)
        {
            using var image = new MagickImage();
            image.ReadPixels(pixels ?? rgb, new PixelReadSettings(W, H, StorageType.Char, mapping));
            setup(image);
            return image.ToByteArray(MagickFormat.Tiff);
        }

        foreach (var compression in new[] { CompressionMethod.NoCompression, CompressionMethod.LZW, CompressionMethod.Zip, CompressionMethod.RLE })
        foreach (var tiled in new[] { false, true })
        foreach (var planar in new[] { false, true })
        {
            files[$"{compression} tiled={tiled} planar={planar}"] = Magick(m =>
            {
                m.Settings.Compression = compression;
                m.Settings.Interlace = planar ? Interlace.Plane : Interlace.NoInterlace;
                m.Settings.SetDefine(MagickFormat.Tiff, "predictor", compression is CompressionMethod.LZW or CompressionMethod.Zip ? "2" : "1");
                if (tiled) m.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", "16x16");
                else m.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "6");
            });
        }
        files["16-bit zip predictor"] = Magick(m =>
        {
            m.Depth = 16;
            m.Settings.Compression = CompressionMethod.Zip;
            m.Settings.SetDefine(MagickFormat.Tiff, "predictor", "2");
            m.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "5");
        });
        files["float tiled predictor 3"] = Magick(m =>
        {
            m.Depth = 32;
            m.Settings.SetDefine("quantum:format", "floating-point");
            m.Settings.Compression = CompressionMethod.Zip;
            m.Settings.SetDefine(MagickFormat.Tiff, "predictor", "3");
            m.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", "16x32");
        });
        files["palette"] = Magick(m => { m.ColorType = ColorType.Palette; m.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "4"); },
            pixels: [.. rgb.Select(b => (byte)(b & 0xC0))]);
        files["cmyk"] = Magick(m => m.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "7"), "CMYK",
            [.. Enumerable.Range(0, W * H * 4).Select(i => (byte)(i * 13))]);
        files["jpeg ycbcr tiled"] = Magick(m =>
        {
            m.ColorSpace = ColorSpace.YCbCr;
            m.Settings.Compression = CompressionMethod.JPEG;
            m.Settings.SetDefine(MagickFormat.Tiff, "tile-geometry", "32x16");
        });
        files["jpeg rgb strips"] = Magick(m =>
        {
            m.Settings.Compression = CompressionMethod.JPEG;
            m.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "16");
        });
        files["bilevel g4"] = Magick(m =>
        {
            m.ColorType = ColorType.Bilevel;
            m.Depth = 1;
            m.Settings.Compression = CompressionMethod.Group4;
            m.Settings.SetDefine(MagickFormat.Tiff, "rows-per-strip", "9");
        });

        // Subsampled YCbCr (4:2:0) and RGBA with associated alpha, hand-built.
        var ycc = B.Basic(W, H, 3, 8, TiffCompression.Uncompressed, TiffPhotometric.YCbCr, rowsPerStrip: 10);
        var strips = new List<byte[]>();
        for (var y = 0; y < H; y += 10)
            strips.Add([.. Enumerable.Range(0, ((W + 1) / 2) * ((Math.Min(10, H - y) + 1) / 2) * 6).Select(i => (byte)((i * 31) + y))]);
        files["ycbcr 4:2:0"] = new B().AddPage(ycc, [.. strips]).Build();
        var rgba = B.Basic(W, H, 4, 8, TiffCompression.Uncompressed, TiffPhotometric.Rgb, rowsPerStrip: 8);
        rgba.Add(B.Short(TiffTag.ExtraSamples, 1));
        var premultiplied = new byte[W * H * 4];
        for (var i = 0; i < W * H; i++)
        {
            var a = (byte)(i * 5);
            premultiplied[(i * 4) + 3] = a;
            for (var c = 0; c < 3; c++) premultiplied[(i * 4) + c] = (byte)(rgb[(i * 3) + c] * a / 255);
        }
        files["rgba associated"] = new B().AddPage(rgba, TiffRobustnessTests.Strips(premultiplied, W * 4, H, 8)).Build();
        // A big-endian BigTIFF, 16-bit grey tiles.
        var gray16 = new byte[W * H * 2];
        new Random(4).NextBytes(gray16);
        using (var g = new MagickImage())
        {
            g.ReadPixels(gray16, new PixelReadSettings(W, H, StorageType.Short, "I"));
            g.ColorSpace = ColorSpace.Gray;
            g.Depth = 16;
            g.Settings.SetDefine(MagickFormat.Tiff64, "tile-geometry", "32x16");
            g.Settings.SetDefine(MagickFormat.Tiff64, "endian", "msb");
            files["bigtiff gray16 MM tiled"] = g.ToByteArray(MagickFormat.Tiff64);
        }
        return _corpus = files;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RegionDecode_EqualsCropOfTheWholePage(string name)
    {
        var tiff = Corpus()[name];
        TiffImageDecoder.TryDecode(tiff, out var whole, cancellationToken: Ct).ShouldBeTrue(name);
        using (whole)
        {
            var bpp = whole!.Pixels.Length / (W * H);
            foreach (var region in Regions)
            {
                TiffImageDecoder.TryDecode(tiff, out var part, new DecodeOptions { Region = region }, Ct).ShouldBeTrue($"{name} {region}");
                using (part)
                {
                    part!.Width.ShouldBe(region.Width);
                    part.Channels.ShouldBe(whole.Channels);
                    part.SampleFormat.ShouldBe(whole.SampleFormat);
                    var expected = new byte[region.Width * region.Height * bpp];
                    for (var y = 0; y < region.Height; y++)
                        whole.Pixels.Slice((((region.Y + y) * W) + region.X) * bpp, region.Width * bpp).CopyTo(expected.AsSpan(y * region.Width * bpp));
                    part.Pixels.ToArray().ShouldBe(expected, $"{name} {region}");
                }

                foreach (var format in new[] { PixelFormat.Rgba32, PixelFormat.Bgr24, PixelFormat.Gray8, PixelFormat.Rgba64 })
                {
                    var options = new DecodeOptions { Region = region };
                    var direct = new byte[(region.Width * format.BytesPerPixel * region.Height) + 7];
                    var reference = new byte[direct.Length];
                    var stride = (region.Width * format.BytesPerPixel) + (region.Height > 1 ? 0 : 7);
                    var ok = TiffImageDecoder.TryDecodeInto(tiff, direct, stride, format, options, Ct);
                    var refOk = Sdcb.SimdCodecs.Abstractions.CodecSupport.ImageDecoderFallback.TryDecodeInto(
                        TiffImageDecoder.TryDecode, tiff, reference, stride, format, options, Ct);
                    ok.ShouldBe(refOk, $"{name} {region} {format}");
                    direct.ShouldBe(reference, $"{name} {region} {format}");
                }
            }
        }
    }

    [Fact]
    public void RegionDecode_ReadsOnlyTheSegmentsItTouches()
    {
        // Poison every strip but the ones covering rows 20..26: a region inside them still decodes, the whole page does not.
        var tiff = Corpus()["LZW tiled=False planar=False"].ToArray();
        var layout = TiffReader.ReadLayout(tiff);
        for (var s = 0; s < layout.SegmentCount; s++)
        {
            var rect = layout.SegmentRectangle(s);
            if (rect.Y + rect.Height > 20 && rect.Y < 27) continue;
            tiff.AsSpan((int)layout.Offsets[s], (int)layout.ByteCounts[s]).Fill(0xFF);
        }
        TiffImageDecoder.TryDecode(Corpus()["LZW tiled=False planar=False"], out var clean, new DecodeOptions { Region = new PixelRect(3, 20, 60, 7) }, Ct).ShouldBeTrue();
        TiffImageDecoder.TryDecode(tiff, out var part, new DecodeOptions { Region = new PixelRect(3, 20, 60, 7) }, Ct).ShouldBeTrue();
        part!.Pixels.ToArray().ShouldBe(clean!.Pixels.ToArray());
        TiffImageDecoder.TryDecode(tiff, out var whole, cancellationToken: Ct).ShouldBeTrue();
        whole!.Pixels.ToArray().ShouldNotBe(TiffReader.ReadPage(Corpus()["LZW tiled=False planar=False"], 0, Ct).Pixels,
            "premise: the poisoned strips change the whole page");
    }

    [Fact]
    public void ARegionOutsideThePage_Throws()
    {
        var tiff = Corpus()["NoCompression tiled=False planar=False"];
        Should.Throw<ArgumentException>(() => TiffImageDecoder.TryDecode(tiff, out _, new DecodeOptions { Region = new PixelRect(60, 0, 20, 2) }, Ct));
    }
}
