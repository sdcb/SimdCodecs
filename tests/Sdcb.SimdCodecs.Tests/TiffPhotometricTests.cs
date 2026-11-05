using System.Buffers.Binary;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using B = Sdcb.SimdCodecs.Tests.TestTiffBuilder;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The facade's photometric conversions (<c>TiffColor</c>): palette, CMYK, non-JPEG YCbCr (every subsampling, odd
/// sizes), ExtraSamples and associated alpha. Each is checked two ways: exactly against the arithmetic this repository
/// defines, recomputed here from the stored samples; and against libtiff's decode through Magick.NET, exactly where the
/// spec leaves no choice (palette, unassociated alpha) and within the tolerance recorded in Oracle/README.md where it
/// does (YCbCr: ImageMagick converts with its own arithmetic).
/// </summary>
public sealed class TiffPhotometricTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Decode(byte[] tiff, out IDecodedImage image)
    {
        TiffImageDecoder.TryDecode(tiff, out var decoded, cancellationToken: Ct).ShouldBeTrue();
        image = decoded!;
        return decoded!.Pixels.ToArray();
    }

    /// <summary>libtiff's decode, as 8-bit RGB (or RGBA when <paramref name="alpha"/>).</summary>
    private static byte[] MagickRgb(byte[] tiff, bool alpha = false)
    {
        using var image = new MagickImage(tiff);
        // ImageMagick keeps CMYK and YCbCr pages in those colour spaces; its own transform takes them to sRGB.
        if (image.ColorSpace is ColorSpace.CMYK or ColorSpace.YCbCr) image.ColorSpace = ColorSpace.sRGB;
        using var pixels = image.GetPixels();
        return pixels.ToByteArray(alpha ? PixelMapping.RGBA : PixelMapping.RGB)!;
    }

    private static int MaxDifference(byte[] a, byte[] b)
    {
        a.Length.ShouldBe(b.Length);
        var max = 0;
        for (var i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // ---------------------------------------------------------------- palette

    [Theory]
    [InlineData(CompressionMethod.NoCompression)]
    [InlineData(CompressionMethod.LZW)]
    [InlineData(CompressionMethod.Zip)]
    public void LibTiffPalette_DecodesToLibTiffsColours(CompressionMethod compression)
    {
        const int w = 41, h = 19;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            rgb[i * 3] = (byte)(i % 7 * 37);
            rgb[(i * 3) + 1] = (byte)(i % 5 * 61);
            rgb[(i * 3) + 2] = (byte)(i % 3 * 120);
        }
        using var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings(w, h, StorageType.Char, "RGB"));
        image.ColorType = ColorType.Palette;
        image.Settings.Compression = compression;
        var tiff = image.ToByteArray(MagickFormat.Tiff);
        TiffReader.ReadLayout(tiff).Photometric.ShouldBe(TiffPhotometric.Palette, "premise: libtiff wrote a palette page");

        var ours = Decode(tiff, out var decoded);
        decoded.Channels.ShouldBe(3);
        decoded.SampleFormat.ShouldBe(SampleFormat.UInt8);
        ours.ShouldBe(MagickRgb(tiff));
        ours.ShouldBe(rgb, "35 colours fit the palette, so the source survives exactly");
    }

    /// <summary>A hand-built palette of 2-bit indices and arbitrary 16-bit entries: v &gt;&gt; 8 per entry, by definition.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void SubByteAndByteIndices_MapThroughTheColourMap(int bits)
    {
        const int w = 13, h = 5;
        var entries = 1 << bits;
        var map = new int[3 * entries];
        var random = new Random(bits);
        for (var i = 0; i < map.Length; i++) map[i] = random.Next(65536);
        var indices = new int[w * h];
        for (var i = 0; i < indices.Length; i++) indices[i] = random.Next(entries);

        var rowBytes = ((w * bits) + 7) / 8;
        var packed = new byte[rowBytes * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var bit = x * bits;
            packed[(y * rowBytes) + (bit >> 3)] |= (byte)(indices[(y * w) + x] << (8 - bits - (bit & 7)));
        }
        var tags = B.Basic(w, h, 1, bits, TiffCompression.Uncompressed, TiffPhotometric.Palette);
        tags.Add(B.Short(TiffTag.ColorMap, map));
        var tiff = new B().AddPage(tags, [packed]).Build();

        var expected = new byte[w * h * 3];
        for (var i = 0; i < indices.Length; i++)
        for (var c = 0; c < 3; c++)
            expected[(i * 3) + c] = (byte)(map[(c * entries) + indices[i]] >> 8);
        Decode(tiff, out _).ShouldBe(expected);
        // The reader hands palette indices over unscaled.
        TiffReader.ReadPage(tiff, 0, Ct).Pixels.ShouldBe([.. indices.Select(i => (byte)i)]);
        // libtiff agrees wherever an entry is an exact 8-bit colour; for arbitrary 16-bit entries it rounds, so
        // only the definition above is asserted for them.
    }

    [Fact]
    public void SixteenBitIndices_GiveSixteenBitColour()
    {
        const int w = 7, h = 3;
        var map = new int[3 * 65536];
        for (var i = 0; i < 65536; i++) (map[i], map[65536 + i], map[131072 + i]) = (i, 65535 - i, (i * 7) & 0xFFFF);
        var indices = new ushort[w * h];
        for (var i = 0; i < indices.Length; i++) indices[i] = (ushort)(i * 3001);
        var stored = new byte[indices.Length * 2];
        for (var i = 0; i < indices.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(i * 2), indices[i]);
        var tags = B.Basic(w, h, 1, 16, TiffCompression.Uncompressed, TiffPhotometric.Palette);
        tags.Add(B.Short(TiffTag.ColorMap, map));
        var tiff = new B().AddPage(tags, [stored]).Build();

        var ours = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(Decode(tiff, out var image)).ToArray();
        image.SampleFormat.ShouldBe(SampleFormat.UInt16);
        for (var i = 0; i < indices.Length; i++)
        {
            ours[i * 3].ShouldBe((ushort)map[indices[i]]);
            ours[(i * 3) + 1].ShouldBe((ushort)map[65536 + indices[i]]);
            ours[(i * 3) + 2].ShouldBe((ushort)map[131072 + indices[i]]);
        }
    }

    [Fact]
    public void PaletteWithAlpha_GivesRgba_AssociatedAlphaOrAShortMapIsRefused()
    {
        var map = new int[3 * 256];
        for (var i = 0; i < 256; i++) (map[i], map[256 + i], map[512 + i]) = (i * 257, (255 - i) * 257, 0x1234);
        var tags = B.Basic(2, 2, 2, 8, TiffCompression.Uncompressed, TiffPhotometric.Palette);
        tags.Add(B.Short(TiffTag.ColorMap, map));
        tags.Add(B.Short(TiffTag.ExtraSamples, 2));
        var tiff = new B().AddPage(tags, [[0, 255, 1, 128, 200, 0, 255, 7]]).Build();
        Decode(tiff, out var image).ShouldBe([0, 255, 0x12, 255, 1, 254, 0x12, 128, 200, 55, 0x12, 0, 255, 0, 0x12, 7]);
        image.Channels.ShouldBe(4);
        Decode(tiff, out _).ShouldBe(MagickRgb(tiff, alpha: true));

        tags[^1] = B.Short(TiffTag.ExtraSamples, 1);
        TiffImageDecoder.TryDecode(new B().AddPage(tags, [new byte[8]]).Build(), out _, cancellationToken: Ct).ShouldBeFalse();

        var shortMap = B.Basic(2, 2, 1, 8, TiffCompression.Uncompressed, TiffPhotometric.Palette);
        shortMap.Add(B.Short(TiffTag.ColorMap, new int[3 * 16]));
        TiffImageDecoder.TryDecode(new B().AddPage(shortMap, [new byte[4]]).Build(), out _, cancellationToken: Ct).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- CMYK

    [Theory]
    [InlineData(8, CompressionMethod.LZW, 2)]
    [InlineData(8, CompressionMethod.NoCompression, 1)]
    [InlineData(16, CompressionMethod.Zip, 2)]
    public void LibTiffCmyk_ConvertsByTheDefinedFormula(int depth, CompressionMethod compression, int predictor)
    {
        const int w = 37, h = 17;
        var cmyk = Noise(w * h * 4, depth);
        using var image = new MagickImage();
        image.ReadPixels(cmyk, new PixelReadSettings(w, h, StorageType.Char, "CMYK"));
        image.Depth = (uint)depth;
        image.Settings.Compression = compression;
        image.Settings.SetDefine(MagickFormat.Tiff, "predictor", predictor.ToString());
        var tiff = image.ToByteArray(MagickFormat.Tiff);
        TiffReader.ReadLayout(tiff).Photometric.ShouldBe(TiffPhotometric.Cmyk, "premise: libtiff wrote a separated page");

        var raw = TiffReader.ReadPage(tiff, 0, Ct).Pixels;
        var ours = Decode(tiff, out var decoded);
        decoded.Channels.ShouldBe(3);
        if (depth == 8)
        {
            raw.ShouldBe(cmyk);
            var expected = new byte[w * h * 3];
            for (var i = 0; i < w * h; i++)
            for (var c = 0; c < 3; c++)
                expected[(i * 3) + c] = (byte)Math.Round((255 - cmyk[(i * 4) + c]) * (255 - cmyk[(i * 4) + 3]) / 255.0,
                    MidpointRounding.AwayFromZero);
            ours.ShouldBe(expected);
            ours.ShouldBe(MagickRgb(tiff), "ImageMagick's CMYK to sRGB gives the same rounded values");
        }
        else
        {
            var inks = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(raw);
            var rgb = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(ours);
            for (var i = 0; i < w * h; i++)
            for (var c = 0; c < 3; c++)
                rgb[(i * 3) + c].ShouldBe((ushort)Math.Round((65535.0 - inks[(i * 4) + c]) * (65535 - inks[(i * 4) + 3]) / 65535.0,
                    MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public void Mul8_IsRoundedDivisionEverywhere()
    {
        for (var x = 0; x < 256; x++)
        for (var y = 0; y < 256; y++)
            TiffColor.Mul8(x, y).ShouldBe((byte)(((x * y) + 127) / 255));
    }

    // ---------------------------------------------------------------- YCbCr (not JPEG)

    /// <summary>
    /// RGB to YCbCr by the TIFF 6.0 section 21 forward equations, CCIR 601-1 coefficients, ReferenceBlackWhite
    /// 0/255/128/255/128/255, rounded; then packed into data units. Odd sizes leave partial units at the edges, which
    /// the writer pads by repeating the last row / column.
    /// </summary>
    private static byte[] YccUnits(byte[] rgb, int w, int h, int sh, int sv, out byte[] y, out byte[] cb, out byte[] cr)
    {
        y = new byte[w * h];
        var cbFull = new double[w * h];
        var crFull = new double[w * h];
        for (var i = 0; i < w * h; i++)
        {
            double r = rgb[i * 3], g = rgb[(i * 3) + 1], b = rgb[(i * 3) + 2];
            var yy = (0.299 * r) + (0.587 * g) + (0.114 * b);
            y[i] = (byte)Math.Clamp(Math.Round(yy), 0, 255);
            cbFull[i] = ((b - yy) / (2 - (2 * 0.114))) + 128;
            crFull[i] = ((r - yy) / (2 - (2 * 0.299))) + 128;
        }
        var ua = (w + sh - 1) / sh;
        var ud = (h + sv - 1) / sv;
        cb = new byte[ua * ud];
        cr = new byte[ua * ud];
        var units = new List<byte>();
        for (var uy = 0; uy < ud; uy++)
        for (var ux = 0; ux < ua; ux++)
        {
            double sumB = 0, sumR = 0;
            for (var j = 0; j < sv; j++)
            for (var i = 0; i < sh; i++)
            {
                var px = Math.Min((ux * sh) + i, w - 1);
                var py = Math.Min((uy * sv) + j, h - 1);
                units.Add(y[(py * w) + px]);
                sumB += cbFull[(py * w) + px];
                sumR += crFull[(py * w) + px];
            }
            cb[(uy * ua) + ux] = (byte)Math.Clamp(Math.Round(sumB / (sh * sv)), 0, 255);
            cr[(uy * ua) + ux] = (byte)Math.Clamp(Math.Round(sumR / (sh * sv)), 0, 255);
            units.Add(cb[(uy * ua) + ux]);
            units.Add(cr[(uy * ua) + ux]);
        }
        return [.. units];
    }

    /// <summary>The definition in <c>TiffColor</c>, written out again independently.</summary>
    private static byte[] YccToRgb(byte[] y, byte[] cb, byte[] cr, int w, int h, int sh, int sv, double[] rbw, double[] luma)
    {
        var ua = (w + sh - 1) / sh;
        var rgb = new byte[w * h * 3];
        static byte C(double v) => (byte)Math.Clamp(Math.Floor(v + 0.5), 0, 255);
        for (var py = 0; py < h; py++)
        for (var px = 0; px < w; px++)
        {
            var unit = ((py / sv) * ua) + (px / sh);
            var yv = (y[(py * w) + px] - rbw[0]) * 255 / (rbw[1] - rbw[0]);
            var cbv = (cb[unit] - rbw[2]) * 127 / (rbw[3] - rbw[2]);
            var crv = (cr[unit] - rbw[4]) * 127 / (rbw[5] - rbw[4]);
            var r = (crv * (2 - (2 * luma[0]))) + yv;
            var b = (cbv * (2 - (2 * luma[2]))) + yv;
            var g = (yv - (luma[2] * b) - (luma[0] * r)) / luma[1];
            var at = ((py * w) + px) * 3;
            (rgb[at], rgb[at + 1], rgb[at + 2]) = (C(r), C(g), C(b));
        }
        return rgb;
    }

    public static TheoryData<int, int, int, int, bool> YccCases() => new()
    {
        { 1, 1, 16, 9, false },
        { 2, 1, 17, 9, false },   // 4:2:2, odd width
        { 2, 2, 17, 11, false },  // 4:2:0, odd both ways; also the default when the tag is absent
        { 2, 2, 17, 11, true },
        { 4, 1, 18, 7, false },   // 4:1:1
        { 4, 2, 19, 10, true },
        { 4, 4, 13, 13, false },
    };

    [Theory]
    [MemberData(nameof(YccCases))]
    public void UncompressedYCbCr_EverySubsampling(int sh, int sv, int w, int h, bool tiled)
    {
        var rgb = Noise(w * h * 3, (sh * 10) + sv);
        var units = YccUnits(rgb, w, h, sh, sv, out var y, out var cb, out var cr);
        var tags = B.Basic(w, h, 3, 8, TiffCompression.Uncompressed, TiffPhotometric.YCbCr);
        if (sh != 2 || sv != 2) tags.Add(B.Short(TiffTag.YCbCrSubSampling, sh, sv));
        tags.Add(B.Rational(TiffTag.ReferenceBlackWhite, (0, 1), (255, 1), (128, 1), (255, 1), (128, 1), (255, 1)));
        byte[] tiff;
        if (tiled)
        {
            // One tile per 16x16 (TIFF 6.0 tiles are multiples of 16, and so of every subsampling factor).
            tags.Add(B.Long(TiffTag.TileWidth, 16));
            tags.Add(B.Long(TiffTag.TileLength, 16));
            var tiles = new List<byte[]>();
            for (var ty = 0; ty < h; ty += 16)
            for (var tx = 0; tx < w; tx += 16)
            {
                var tile = new byte[16 * 16 * 3];
                for (var yy = 0; yy < 16; yy++)
                for (var xx = 0; xx < 16; xx++)
                {
                    var sx = Math.Min(tx + xx, w - 1);
                    var sy = Math.Min(ty + yy, h - 1);
                    rgb.AsSpan(((sy * w) + sx) * 3, 3).CopyTo(tile.AsSpan(((yy * 16) + xx) * 3));
                }
                tiles.Add(YccUnits(tile, 16, 16, sh, sv, out _, out _, out _));
            }
            tiff = new B().AddPage(tags, [.. tiles], tiled: true).Build();
            // Expected: each tile's own codes, cropped.
            var expectedTiled = new byte[w * h * 3];
            var t = 0;
            for (var ty = 0; ty < h; ty += 16)
            for (var tx = 0; tx < w; tx += 16, t++)
            {
                var tile = new byte[16 * 16 * 3];
                for (var yy = 0; yy < 16; yy++)
                for (var xx = 0; xx < 16; xx++)
                    rgb.AsSpan(((Math.Min(ty + yy, h - 1) * w) + Math.Min(tx + xx, w - 1)) * 3, 3).CopyTo(tile.AsSpan(((yy * 16) + xx) * 3));
                YccUnits(tile, 16, 16, sh, sv, out var ty2, out var tcb, out var tcr);
                var tileRgb = YccToRgb(ty2, tcb, tcr, 16, 16, sh, sv, [0, 255, 128, 255, 128, 255], [0.299, 0.587, 0.114]);
                for (var yy = 0; yy < 16 && ty + yy < h; yy++)
                for (var xx = 0; xx < 16 && tx + xx < w; xx++)
                    tileRgb.AsSpan(((yy * 16) + xx) * 3, 3).CopyTo(expectedTiled.AsSpan((((ty + yy) * w) + tx + xx) * 3));
            }
            Decode(tiff, out _).ShouldBe(expectedTiled);
        }
        else
        {
            tiff = new B().AddPage(tags, [units]).Build();
            Decode(tiff, out _).ShouldBe(YccToRgb(y, cb, cr, w, h, sh, sv, [0, 255, 128, 255, 128, 255], [0.299, 0.587, 0.114]));
        }

        var ours = Decode(tiff, out var image);
        image.Channels.ShouldBe(3);
        if (sh == 1 && sv == 1)
        {
            // ImageMagick hands an uncompressed YCbCr page over as YCbCr samples and converts it to sRGB itself. For
            // 1x1 data its samples are the stored ones, equal to the reader's, and its conversion is within the
            // tolerance recorded in Oracle/README.md of the definition here. Subsampled uncompressed data it reads
            // as if the data units were chunky pixels (observed with Magick.NET 14), so it is no oracle there; the
            // exact check above against the spec's arithmetic is what holds those cases.
            using (var raw = new MagickImage(tiff))
            {
                raw.ColorSpace.ShouldBe(ColorSpace.YCbCr);
                using var px = raw.GetPixels();
                TiffReader.ReadPage(tiff, 0, Ct).Pixels.ShouldBe(px.ToByteArray(PixelMapping.RGB)!);
            }
            MaxDifference(ours, MagickRgb(tiff)).ShouldBeLessThanOrEqualTo(YccTolerance);
            MaxDifference(ours, rgb).ShouldBeLessThanOrEqualTo(2, "the source survives a 1x1 round trip within rounding");
        }
    }

    /// <summary>Measured max difference against ImageMagick's YCbCr to sRGB on noise; see Oracle/README.md.</summary>
    internal const int YccTolerance = 1;

    [Fact]
    public void YCbCrHonoursReferenceBlackWhiteAndCoefficients()
    {
        const int w = 6, h = 4;
        var y = new byte[w * h];
        var cb = new byte[w * h];
        var cr = new byte[w * h];
        var random = new Random(9);
        random.NextBytes(y);
        random.NextBytes(cb);
        random.NextBytes(cr);
        var stored = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++) (stored[i * 3], stored[(i * 3) + 1], stored[(i * 3) + 2]) = (y[i], cb[i], cr[i]);
        var tags = B.Basic(w, h, 3, 8, TiffCompression.Uncompressed, TiffPhotometric.YCbCr);
        tags.Add(B.Short(TiffTag.YCbCrSubSampling, 1, 1));
        tags.Add(B.Rational(TiffTag.YCbCrCoefficients, (2125, 10000), (7154, 10000), (721, 10000)));
        tags.Add(B.Rational(TiffTag.ReferenceBlackWhite, (16, 1), (235, 1), (128, 1), (240, 1), (128, 1), (240, 1)));
        var tiff = new B().AddPage(tags, [stored]).Build();
        Decode(tiff, out _).ShouldBe(YccToRgb(y, cb, cr, w, h, 1, 1, [16, 235, 128, 240, 128, 240], [0.2125, 0.7154, 0.0721]));
    }

    [Fact]
    public void SubsampledYCbCrInSeparatePlanes_IsRefusedCleanly()
    {
        var tags = B.Basic(4, 4, 3, 8, TiffCompression.Uncompressed, TiffPhotometric.YCbCr);
        tags.Add(B.Short(284, 2));
        var tiff = new B().AddPage(tags, [new byte[16], new byte[4], new byte[4]]).Build();
        Should.Throw<NotSupportedException>(() => TiffReader.Read(tiff));
        TiffImageDecoder.TryDecode(tiff, out _, cancellationToken: Ct).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- ExtraSamples

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void AssociatedAlpha_IsUnpremultiplied(int bits)
    {
        const int w = 9, h = 4;
        var max = bits == 8 ? 255 : 65535;
        var random = new Random(bits);
        var samples = new int[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var a = i == 0 ? 0 : random.Next(max + 1);
            samples[(i * 4) + 3] = a;
            for (var c = 0; c < 3; c++) samples[(i * 4) + c] = (int)((long)random.Next(max + 1) * a / max);
        }
        var tiff = Rgba(samples, w, h, bits, extra: 1);
        var expected = new int[samples.Length];
        for (var i = 0; i < w * h; i++)
        {
            var a = samples[(i * 4) + 3];
            expected[(i * 4) + 3] = a;
            for (var c = 0; c < 3; c++)
                expected[(i * 4) + c] = a == 0 ? 0 : (int)Math.Min(max, (((long)samples[(i * 4) + c] * max) + (a / 2)) / a);
        }
        Samples(Decode(tiff, out var image), bits).ShouldBe(expected);
        image.Channels.ShouldBe(4);
    }

    [Fact]
    public void UnassociatedAlpha_MatchesLibTiff_AndUnspecifiedExtrasAreDropped()
    {
        const int w = 9, h = 4;
        var samples = Noise(w * h * 4, 4).Select(b => (int)b).ToArray();
        var unassociated = Rgba(samples, w, h, 8, extra: 2);
        Decode(unassociated, out var rgba).ShouldBe([.. samples.Select(s => (byte)s)]);
        rgba.Channels.ShouldBe(4);
        Decode(unassociated, out _).ShouldBe(MagickRgb(unassociated, alpha: true));

        var unspecified = Rgba(samples, w, h, 8, extra: 0);
        var rgb = Decode(unspecified, out var image);
        image.Channels.ShouldBe(3);
        rgb.ShouldBe([.. samples.Where((_, i) => i % 4 != 3).Select(s => (byte)s)]);
    }

    [Fact]
    public void CieLab_IsStillRefused()
    {
        var tiff = new B().AddPage(B.Basic(2, 2, 3, 8, TiffCompression.Uncompressed, TiffPhotometric.CieLab), [new byte[12]]).Build();
        TiffImageDecoder.TryDecode(tiff, out _, cancellationToken: Ct).ShouldBeFalse();
        TiffImageDecoder.TryReadInfo(tiff, out _).ShouldBeFalse();
    }

    private static byte[] Rgba(int[] samples, int w, int h, int bits, int extra)
    {
        var stored = new byte[samples.Length * bits / 8];
        for (var i = 0; i < samples.Length; i++)
        {
            if (bits == 8) stored[i] = (byte)samples[i];
            else BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(i * 2), (ushort)samples[i]);
        }
        var tags = B.Basic(w, h, 4, bits, TiffCompression.Uncompressed, TiffPhotometric.Rgb);
        tags.Add(B.Short(TiffTag.ExtraSamples, extra));
        return new B().AddPage(tags, [stored]).Build();
    }

    private static int[] Samples(byte[] pixels, int bits) => bits == 8
        ? [.. pixels.Select(b => (int)b)]
        : [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(pixels).ToArray().Select(v => (int)v)];
}
