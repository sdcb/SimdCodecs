using System.Buffers.Binary;
using System.IO.Compression;
using ImageMagick;
using Sdcb.SimdCodecs.Png;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PngWriteOptions.LosslessReduction"/>, the grey + alpha entry points, and the tRNS and pHYs
/// the writer can now emit. The oracle is ImageMagick (libpng) reading each file back as 16-bit RGBA:
/// a reduction is lossless exactly when that equals the source widened the way PNG widens samples
/// (an 8-bit v as v·257, grey to R = G = B, no alpha as 65535). Each case also pins the layout the
/// reduction must choose, so a reduction that silently stops applying fails too.
/// </summary>
public sealed class PngWriterReductionTests
{
    private const int W = 37, H = 29; // odd width: packed rows end part-way through a byte; 1073 pixels

    /// <summary>A source: its layout, and its pixels as 16-bit RGBA (what the file must read back as).</summary>
    public sealed record Source(int Channels, int SampleBytes, ushort[] Rgba16, bool DiscardAlpha = false, bool Icc = false);

    private static ushort[] Pixels(int seed, Func<Random, int, (int R, int G, int B, int A)> pixel, bool wide = false)
    {
        var random = new Random(seed);
        var rgba = new ushort[W * H * 4];
        for (var i = 0; i < W * H; i++)
        {
            var (r, g, b, a) = pixel(random, i);
            var k = wide ? 1 : 257; // 8-bit values widened as PNG widens them
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = ((ushort)(r * k), (ushort)(g * k), (ushort)(b * k), (ushort)(a * k));
        }
        return rgba;
    }

    private static (int, int, int, int) Grey(int v, int a = 255) => (v, v, v, a);

    public static TheoryData<string, Source, int, int, bool> Cases()
    {
        var d = new TheoryData<string, Source, int, int, bool>();
        void Add(string name, Source source, int colorType, int depth, bool trns = false) => d.Add(name, source, colorType, depth, trns);

        // Nothing to reduce: the layout as given.
        Add("rgba8 random", new(4, 1, Pixels(1, (r, _) => (r.Next(256), r.Next(256), r.Next(256), r.Next(256)))), 6, 8);
        Add("rgb8 random", new(3, 1, Pixels(2, (r, _) => (r.Next(256), r.Next(256), r.Next(256), 255))), 2, 8);
        Add("gray8 random", new(1, 1, Pixels(3, (r, _) => Grey(r.Next(256)))), 0, 8);
        Add("ga8 random", new(2, 1, Pixels(4, (r, _) => Grey(r.Next(256), r.Next(256)))), 4, 8);
        Add("rgba16 random", new(4, 2, Pixels(5, (r, _) => (r.Next(65536), r.Next(65536), r.Next(65536), r.Next(65536)), wide: true)), 6, 16);
        Add("gray16 random", new(1, 2, Pixels(6, (r, _) => Grey(r.Next(65536), 65535), wide: true)), 0, 16);
        Add("ga16 random", new(2, 2, Pixels(7, (r, _) => Grey(r.Next(65536), r.Next(65536)), wide: true)), 4, 16);

        // Alpha.
        Add("rgba8 opaque", new(4, 1, Pixels(10, (r, _) => (r.Next(256), r.Next(256), r.Next(256), 255))), 2, 8);
        Add("ga8 opaque", new(2, 1, Pixels(11, (r, _) => Grey(r.Next(256)))), 0, 8);
        Add("rgba8 key", new(4, 1, Pixels(12, (r, i) => i % 7 == 0 ? (0, 0, 0, 0) : (1 + r.Next(255), r.Next(256), r.Next(256), 255))), 2, 8, true);
        Add("rgba8 key colour also opaque", new(4, 1, Pixels(13, (r, i) => i % 7 == 0 ? (0, 0, 0, i % 2 == 0 ? 0 : 255) : (1 + r.Next(255), r.Next(256), r.Next(256), 255))), 6, 8);
        Add("rgba8 transparent pixels differ", new(4, 1, Pixels(14, (r, i) => i % 7 == 0 ? (i % 3, 0, 0, 0) : (3 + r.Next(253), r.Next(256), r.Next(256), 255))), 6, 8);
        Add("ga8 key", new(2, 1, Pixels(15, (r, i) => i % 5 == 0 ? Grey(7, 0) : Grey(8 + r.Next(248)))), 0, 8, true);
        Add("ga8 key 4-bit", new(2, 1, Pixels(16, (r, i) => i % 5 == 0 ? Grey(17 * 3, 0) : Grey(17 * (4 + r.Next(12))))), 0, 4, true);
        Add("rgba16 key", new(4, 2, Pixels(17, (r, i) => i % 7 == 0 ? (0x1234, 0x5678, 0x9ABC, 0) : (0x1235 + r.Next(60000), r.Next(65536), r.Next(65536), 65535), wide: true)), 2, 16, true);
        Add("ga16 key", new(2, 2, Pixels(18, (r, i) => i % 7 == 0 ? Grey(0x0102, 0) : Grey(0x0103 + r.Next(60000), 65535), wide: true)), 0, 16, true);
        Add("rgba8 discarded alpha", new(4, 1, Pixels(19, (r, _) => Grey(r.Next(256), 255)), DiscardAlpha: true), 0, 8);

        // Greyscale and its depths.
        Add("rgb8 grey", new(3, 1, Pixels(20, (r, _) => Grey(r.Next(256)))), 0, 8);
        Add("rgb8 grey 4-bit", new(3, 1, Pixels(21, (r, _) => Grey(17 * r.Next(16)))), 0, 4);
        Add("rgb8 grey 2-bit", new(3, 1, Pixels(22, (r, _) => Grey(85 * r.Next(4)))), 0, 2);
        Add("gray8 1-bit", new(1, 1, Pixels(23, (r, _) => Grey(255 * r.Next(2)))), 0, 1);
        Add("gray8 4-bit", new(1, 1, Pixels(24, (r, _) => Grey(17 * r.Next(16)))), 0, 4);
        Add("gray8 ten arbitrary levels", new(1, 1, Pixels(25, (r, _) => Grey(3 + 20 * r.Next(10)))), 3, 4);

        // Indexed colour, at each index depth.
        foreach (var (colours, depth) in new[] { (2, 1), (3, 2), (4, 2), (5, 4), (16, 4), (17, 8), (256, 8) })
            Add($"rgb8 {colours} colours", new(3, 1, Pixels(30 + colours, (r, i) => i < colours ? (i * 13 % 256, i, 200, 255) : (r.Next(colours) is var c ? (c * 13 % 256, c, 200, 255) : default))), 3, depth);
        Add("rgb8 257 colours", new(3, 1, Pixels(29, (_, i) => ((i % 257) % 256, (i % 257) / 256, 9, 255))), 2, 8);
        Add("rgba8 palette with alpha", new(4, 1, Pixels(40, (_, i) => (i % 40 * 6, 50, 60, i % 40 >= 31 ? (i % 40 - 31) * 20 : 255))), 3, 8, true);

        // 16-bit samples that are 8-bit values.
        Add("rgba16 narrow", new(4, 2, Pixels(50, (r, _) => (r.Next(256), r.Next(256), r.Next(256), r.Next(256)))), 6, 8);
        Add("rgb16 narrow palette", new(3, 2, Pixels(51, (_, i) => (i % 5 * 40, 1, 2, 255))), 3, 4);
        Add("rgba16 only alpha wide", new(4, 2, Pixels(54, (r, _) => (257 * r.Next(256), 257 * r.Next(256), 257 * r.Next(256), r.Next(65536)), wide: true)), 6, 16);
        Add("rgb16 grey wide", new(3, 2, Pixels(52, (r, _) => Grey(r.Next(65536), 65535), wide: true)), 0, 16);
        Add("gray16 narrow 2-bit", new(1, 2, Pixels(53, (r, _) => Grey(85 * r.Next(4)))), 0, 2);

        // An ICC profile keeps the colour model.
        Add("rgb8 grey with icc", new(3, 1, Pixels(60, (r, _) => Grey(r.Next(256))), Icc: true), 3, 8);
        Add("rgb16 grey wide with icc", new(3, 2, Pixels(61, (r, _) => Grey(r.Next(65536), 65535), wide: true), Icc: true), 2, 16);
        Add("gray8 ten levels with icc", new(1, 1, Pixels(62, (r, _) => Grey(3 + 20 * r.Next(10))), Icc: true), 0, 8);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReducesLosslesslyToTheSmallestLayout(string name, Source source, int colorType, int depth, bool trns)
    {
        _ = name;
        var options = new PngWriteOptions
        {
            LosslessReduction = true,
            DiscardAlpha = source.DiscardAlpha,
            IccProfile = source.Icc ? Sdcb.SimdCodecs.Color.Icc.IccProfiles.SRgbV4.ToArray() : null,
        };
        var png = Encode(source, options);

        (png[24], png[25]).ShouldBe(((byte)depth, (byte)colorType));
        (png.AsSpan().IndexOf("tRNS"u8) > 0).ShouldBe(trns);
        ReadBack16(png).ShouldBe(Expected(source));

        // The RGBA8 projection agrees. The layout choice is a size heuristic; on these images it never
        // costs more than the PLTE and tRNS it adds.
        PngReader.Decode(png).ToRgba8().ShouldBe(Expected(source).Select(v => (byte)(v >> 8)).ToArray());
        png.Length.ShouldBeLessThanOrEqualTo(Encode(source, options with { LosslessReduction = false }).Length + 800);
    }

    [Theory]
    [InlineData(CompressionLevel.NoCompression, 1)]
    [InlineData(CompressionLevel.Fastest, 1)]
    [InlineData(CompressionLevel.SmallestSize, 1)]
    [InlineData(CompressionLevel.Optimal, 8)]
    public void IndexedAndPackedRowsSurviveEveryLevelAndFragmentation(CompressionLevel level, int fragments)
    {
        const int width = 301, height = 1203; // enough payload for the fragments to apply
        foreach (var colours in new[] { 2, 12, 200 })
        {
            var rgb = new byte[width * height * 3];
            for (var i = 0; i < width * height; i++)
            {
                var c = (i / 7 + i % width) % colours;
                (rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2]) = ((byte)(c * 7), (byte)(255 - c), (byte)(c % 3 * 100));
            }
            var png = PngWriter.EncodeRgb8(rgb, width, height,
                new PngWriteOptions { LosslessReduction = true, CompressionLevel = level, ParallelFragments = fragments });

            png[25].ShouldBe((byte)3);
            using var image = new MagickImage(png);
            using var pixels = image.GetPixels();
            pixels.ToByteArray(PixelMapping.RGB).ShouldBe(rgb);
        }
    }

    public static TheoryData<int, int, int, int> OpacityCases()
    {
        // Layout (channels, sample bytes) × where the one non-opaque pixel is × what it is (0: none,
        // 1: translucent, 2: transparent in a colour nothing else has, 3: translucent again, for 16-bit
        // with the other byte of the alpha the one below 0xFF). Large enough (125 × 160) that the
        // scan outlives its first 4096-pixel block and checks the rest of the alpha in 64-byte blocks;
        // the positions hit a block's first pixel, its middle, and the scalar tail.
        var d = new TheoryData<int, int, int, int>();
        foreach (var (channels, bytes) in new[] { (2, 1), (4, 1), (2, 2), (4, 2) })
        foreach (var at in new[] { 4096, 9999, 125 * 160 - 1 })
        foreach (var kind in new[] { 0, 1, 2, 3 })
        {
            if ((kind == 0 && at != 4096) || (kind == 3 && bytes == 1)) continue;
            d.Add(channels, bytes, at, kind);
        }
        return d;
    }

    [Theory]
    [MemberData(nameof(OpacityCases))]
    public void OpacityIsFoundAnywhereInALargeImage(int channels, int sampleBytes, int at, int kind)
    {
        const int width = 125, height = 160;
        var random = new Random(at + kind);
        var wide = sampleBytes == 2;
        // Colour (or, for grey + alpha, 16-bit grey) noise: no palette, no greyscale, no 8-bit narrowing,
        // so only the alpha is left open after the first block. The odd pixel's colour is one the noise
        // never produces (it is kept above 1).
        var rgba = new ushort[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            int Noise() => wide ? 2 + random.Next(65534) : 257 * (2 + random.Next(254));
            var v = Noise();
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = channels == 2
                ? ((ushort)v, (ushort)v, (ushort)v, (ushort)65535)
                : ((ushort)v, (ushort)Noise(), (ushort)Noise(), (ushort)65535);
        }
        // 16-bit translucent alphas 0xFFFE and 0xFEFF: only one of the two bytes says "not opaque".
        if (kind != 0)
            (rgba[4 * at], rgba[4 * at + 1], rgba[4 * at + 2], rgba[4 * at + 3]) = kind == 2
                ? ((ushort)0, (ushort)0, (ushort)0, (ushort)0)
                : ((ushort)(257 * 9), (ushort)(257 * 9), (ushort)(257 * 9), (ushort)(!wide ? 257 * 128 : kind == 1 ? 0xFFFE : 0xFEFF));

        var samples = Enumerable.Range(0, width * height)
            .SelectMany(i => channels == 2 ? new[] { rgba[4 * i], rgba[4 * i + 3] } : rgba.AsSpan(4 * i, 4).ToArray()).ToArray();
        // With an ICC profile a greyscale source keeps out of the palette, which would otherwise hold its
        // 8-bit levels and keep the scan scalar to the end.
        var options = new PngWriteOptions { LosslessReduction = true, IccProfile = channels == 2 && !wide ? Sdcb.SimdCodecs.Color.Icc.IccProfiles.SRgbV4.ToArray() : null };
        var png = (channels, wide) switch
        {
            (2, false) => PngWriter.EncodeGrayAlpha8([.. samples.Select(v => (byte)(v / 257))], width, height, options),
            (4, false) => PngWriter.Encode([.. samples.Select(v => (byte)(v / 257))], width, height, options),
            (2, true) => PngWriter.EncodeGrayAlpha16(samples, width, height, options),
            _ => PngWriter.EncodeRgba16(samples, width, height, options),
        };

        var grey = channels == 2;
        png[25].ShouldBe((byte)(kind is 1 or 3 ? (grey ? 4 : 6) : (grey ? 0 : 2)));
        (png.AsSpan().IndexOf("tRNS"u8) > 0).ShouldBe(kind == 2);
        using var image = new MagickImage(png);
        using var pixels = image.GetPixels();
        pixels.ToShortArray(PixelMapping.RGBA)!.ShouldBe(rgba);
    }

    [Fact]
    public void AScreenshotShrinks()
    {
        // Flat UI: a handful of colours in large areas. Indexed at 4 bits beats RGBA at 32 by far.
        const int width = 640, height = 400;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = 4 * (y * width + x);
            var (r, g, b) = y < 30 ? (40, 44, 52) : x < 160 ? (33, 37, 43) : ((x / 8 + y / 12) % 9 == 0 ? (171, 178, 191) : (40, 44, 52));
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)r, (byte)g, (byte)b, 255);
        }

        var plain = PngWriter.Encode(rgba, width, height, new PngWriteOptions());
        var reduced = PngWriter.Encode(rgba, width, height, new PngWriteOptions { LosslessReduction = true });

        (reduced[24], reduced[25]).ShouldBe(((byte)2, (byte)3));
        reduced.Length.ShouldBeLessThan(plain.Length / 2);
        PngReader.Decode(reduced).ToRgba8().ShouldBe(rgba);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GreyAlphaEntryPointsWriteColourType4(int sampleBytes)
    {
        var source = sampleBytes == 1
            ? new Source(2, 1, Pixels(70, (r, _) => Grey(r.Next(256), r.Next(256))))
            : new Source(2, 2, Pixels(71, (r, _) => Grey(r.Next(65536), r.Next(65536)), wide: true));

        var png = Encode(source, new PngWriteOptions());

        (png[24], png[25]).ShouldBe(((byte)(8 * sampleBytes), (byte)4));
        ReadBack16(png).ShouldBe(Expected(source));

        // DiscardAlpha drops it to plain greyscale.
        var grey = Encode(source, new PngWriteOptions { DiscardAlpha = true });
        grey[25].ShouldBe((byte)0);
        ReadBack16(grey).ShouldBe(Expected(source with { Rgba16 = [.. source.Rgba16.Select((v, i) => i % 4 == 3 ? (ushort)65535 : v)] }));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    public void AnExplicitTransparentColourIsWrittenAsTrns(int channels, int sampleBytes)
    {
        var wide = sampleBytes == 2;
        var keyValue = wide ? 0x4321 : 0x43;
        var source = new Source(channels, sampleBytes, Pixels(80, (r, i) => i % 3 == 0
            ? (keyValue, channels == 1 ? keyValue : 7, channels == 1 ? keyValue : 9, 255)
            : channels == 1 ? Grey(r.Next(wide ? 65536 : 256), wide ? 65535 : 255) : (r.Next(wide ? 65536 : 256), r.Next(256), r.Next(256), wide ? 65535 : 255), wide));
        ushort[] key = channels == 1 ? [(ushort)keyValue] : [(ushort)keyValue, 7, 9];

        var png = Encode(source, new PngWriteOptions { TransparentColor = key });

        var expected = Expected(source);
        for (var i = 0; i < W * H; i++)
        {
            if (channels == 1
                    ? expected[4 * i] == (wide ? keyValue : keyValue * 257)
                    : expected[4 * i] == (wide ? keyValue : keyValue * 257) && expected[4 * i + 1] == (wide ? 7 : 7 * 257) && expected[4 * i + 2] == (wide ? 9 : 9 * 257))
                expected[4 * i + 3] = 0;
        }
        expected.Where((_, i) => i % 4 == 3).ShouldContain((ushort)0);
        ReadBack16(png).ShouldBe(expected);
        PngReader.Decode(png).TransparentColor.ShouldBe(key);
    }

    [Fact]
    public void TransparentColorMisuseIsAnArgumentException()
    {
        var rgba = new byte[4 * 4];
        var rgb = new byte[4 * 3];
        Should.Throw<ArgumentException>(() => PngWriter.Encode(rgba, 2, 2, new PngWriteOptions { TransparentColor = [1, 2, 3] }));
        Should.Throw<ArgumentException>(() => PngWriter.EncodeRgb8(rgb, 2, 2, new PngWriteOptions { TransparentColor = [1] }));
        Should.Throw<ArgumentException>(() => PngWriter.EncodeRgb8(rgb, 2, 2, new PngWriteOptions { TransparentColor = [1, 2, 256] }));
        Should.Throw<ArgumentException>(() => PngWriter.EncodeRgb8(rgb, 2, 2, new PngWriteOptions { TransparentColor = [1, 2, 3], LosslessReduction = true }));
        // With the alpha discarded the output is RGB, so a key is fine.
        PngWriter.Encode(rgba, 2, 2, new PngWriteOptions { TransparentColor = [1, 2, 3], DiscardAlpha = true })[25].ShouldBe((byte)2);
    }

    [Fact]
    public void PhysicalDimensionsAreWritten()
    {
        var png = PngWriter.EncodeRgb8(new byte[2 * 2 * 3], 2, 2, new PngWriteOptions { PhysicalDimensions = PngPhysicalDimensions.FromDpi(300) });

        PngReader.Decode(png).PhysicalDimensions.ShouldBe(new PngPhysicalDimensions(11811, 11811, true));
        using var image = new MagickImage(png);
        image.Density.ChangeUnits(DensityUnit.PixelsPerInch).X.ShouldBe(300, 0.01);

        var aspect = PngWriter.EncodeRgb8(new byte[2 * 2 * 3], 2, 2, new PngWriteOptions { PhysicalDimensions = new(2, 1, false) });
        PngReader.Decode(aspect).PhysicalDimensions.ShouldBe(new PngPhysicalDimensions(2, 1, false));
    }

    private static byte[] Encode(Source source, PngWriteOptions options)
    {
        var count = W * H;
        var rgba = source.Rgba16;
        var samples = new ushort[count * source.Channels];
        for (var i = 0; i < count; i++)
        {
            ushort[] px = source.Channels switch
            {
                1 => [rgba[4 * i]],
                2 => [rgba[4 * i], rgba[4 * i + 3]],
                3 => [rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2]],
                _ => [rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]],
            };
            px.CopyTo(samples, i * source.Channels);
        }

        if (source.SampleBytes == 2)
        {
            return source.Channels switch
            {
                1 => PngWriter.EncodeGray16(samples, W, H, options),
                2 => PngWriter.EncodeGrayAlpha16(samples, W, H, options),
                3 => PngWriter.EncodeRgb16(samples, W, H, options),
                _ => PngWriter.EncodeRgba16(samples, W, H, options),
            };
        }

        samples.ShouldAllBe(v => v % 257 == 0); // an 8-bit source holds 8-bit values
        var bytes = samples.Select(v => (byte)(v / 257)).ToArray();
        return source.Channels switch
        {
            1 => PngWriter.EncodeGray8(bytes, W, H, options),
            2 => PngWriter.EncodeGrayAlpha8(bytes, W, H, options),
            3 => PngWriter.EncodeRgb8(bytes, W, H, options),
            _ => PngWriter.Encode(bytes, W, H, options),
        };
    }

    // The source as a decoder must give it back: grey sources have R = G = B, sources without alpha
    // (or with it discarded) are opaque.
    private static ushort[] Expected(Source source)
    {
        var expected = (ushort[])source.Rgba16.Clone();
        for (var i = 0; i < W * H; i++)
        {
            if (source.Channels <= 2)
                expected[4 * i + 1] = expected[4 * i + 2] = expected[4 * i];
            if (source.Channels is 1 or 3 || source.DiscardAlpha)
                expected[4 * i + 3] = 65535;
        }
        return expected;
    }

    private static ushort[] ReadBack16(byte[] png)
    {
        using var image = new MagickImage(png);
        using var pixels = image.GetPixels();
        return pixels.ToShortArray(PixelMapping.RGBA)!;
    }
}
