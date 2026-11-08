using System.Runtime.InteropServices;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="BoxDownscaler"/>, the frozen box filter behind <see cref="DecodeOptions.ScaleDenominator"/> beyond a codec's
/// native scales, against <see cref="Reference"/>, a per-box transcription of the filter's documented definition, on every
/// forced tier (scalar, Vector128, Vector256): widths past every vector step and tail, partial edge boxes, 8-, 16-bit and
/// float samples, 1 to 4 channels, alpha all 0, all opaque and mixed; column windows; rows pushed one at a time; nothing
/// written past an output row.
/// </summary>
public sealed class BoxDownscaleTests
{
    public static readonly int[] Tiers = [BoxDownscaler.Scalar, BoxDownscaler.Vector128Tier, BoxDownscaler.Vector256Tier];

    private static int Bps(SampleFormat f) => RasterImage.BytesPerSample(f);

    /// <summary>The box filter as DecodeOptions.ScaleDenominator documents it, one output pixel at a time.</summary>
    internal static byte[] Reference(ReadOnlySpan<byte> pixels, int width, int height, int channels, SampleFormat format, int d)
    {
        var ow = (int)(((long)width + d - 1) / d);
        var oh = (int)(((long)height + d - 1) / d);
        var bps = Bps(format);
        var result = new byte[ow * oh * channels * bps];
        var alpha = channels is 2 or 4;
        double Sample(ReadOnlySpan<byte> p, int index) => format switch
        {
            SampleFormat.UInt8 => p[index],
            SampleFormat.UInt16 => MemoryMarshal.Read<ushort>(p[(index * 2)..]),
            _ => MemoryMarshal.Read<float>(p[(index * 4)..]),
        };
        for (var by = 0; by < oh; by++)
        for (var bx = 0; bx < ow; bx++)
        {
            var x0 = (long)bx * d;
            var y0 = (long)by * d;
            var x1 = Math.Min(x0 + d, width);
            var y1 = Math.Min(y0 + d, height);
            var n = (ulong)((x1 - x0) * (y1 - y0));
            var outIndex = (by * ow + bx) * channels;
            if (format == SampleFormat.Float32)
            {
                var sums = new double[channels];
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var i = (int)((y * width + x) * channels);
                    if (alpha)
                    {
                        var a = Sample(pixels, i + channels - 1);
                        for (var k = 0; k < channels - 1; k++)
                            sums[k] += Sample(pixels, i + k) * a;
                        sums[channels - 1] += a;
                    }
                    else
                    {
                        for (var k = 0; k < channels; k++)
                            sums[k] += Sample(pixels, i + k);
                    }
                }
                for (var k = 0; k < channels; k++)
                {
                    float v;
                    if (!alpha) v = (float)(sums[k] / n);
                    else if (k == channels - 1) v = (float)(sums[k] / n);
                    else v = sums[channels - 1] == 0 ? 0f : (float)(sums[k] / sums[channels - 1]);
                    MemoryMarshal.Write(result.AsSpan((outIndex + k) * 4), v);
                }
                continue;
            }

            var isums = new ulong[channels];
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (int)((y * width + x) * channels);
                if (alpha)
                {
                    var a = (ulong)Sample(pixels, i + channels - 1);
                    for (var k = 0; k < channels - 1; k++)
                        isums[k] += (ulong)Sample(pixels, i + k) * a;
                    isums[channels - 1] += a;
                }
                else
                {
                    for (var k = 0; k < channels; k++)
                        isums[k] += (ulong)Sample(pixels, i + k);
                }
            }
            for (var k = 0; k < channels; k++)
            {
                ulong v;
                var asum = isums[channels - 1];
                if (!alpha || k == channels - 1) v = (isums[k] + n / 2) / n;
                else v = asum == 0 ? 0 : (isums[k] + asum / 2) / asum;
                if (format == SampleFormat.UInt8) result[outIndex + k] = (byte)v;
                else MemoryMarshal.Write(result.AsSpan((outIndex + k) * 2), (ushort)v);
            }
        }
        return result;
    }

    /// <summary>Random samples; <paramref name="alphaKind"/> 0: alpha all 0, 1: all max, 2: random, 3: mixed (0, max or random).</summary>
    internal static byte[] Image(int width, int height, int channels, SampleFormat format, int alphaKind, int seed)
    {
        var random = new Random(seed);
        var count = width * height * channels;
        var bytes = new byte[count * Bps(format)];
        for (var i = 0; i < count; i++)
        {
            var isAlpha = channels is 2 or 4 && i % channels == channels - 1;
            var pick = !isAlpha ? 2 : alphaKind == 3 ? random.Next(3) : alphaKind;
            switch (format)
            {
                case SampleFormat.UInt8:
                    bytes[i] = pick switch { 0 => 0, 1 => 255, _ => (byte)random.Next(256) };
                    break;
                case SampleFormat.UInt16:
                    MemoryMarshal.Write(bytes.AsSpan(i * 2), pick switch { 0 => (ushort)0, 1 => ushort.MaxValue, _ => (ushort)random.Next(65536) });
                    break;
                default:
                    MemoryMarshal.Write(bytes.AsSpan(i * 4), pick switch { 0 => 0f, 1 => 1f, _ => (float)(random.NextDouble() * 1.5 - 0.25) });
                    break;
            }
        }
        return bytes;
    }

    private static readonly int[] Widths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 47, 63, 64, 65, 67];
    private static readonly int[] Factors = [2, 4, 8, 16, 1024];

    public static TheoryData<SampleFormat, int, int> Cases()
    {
        var data = new TheoryData<SampleFormat, int, int>();
        foreach (var format in new[] { SampleFormat.UInt8, SampleFormat.UInt16, SampleFormat.Float32 })
        foreach (var channels in new[] { 1, 2, 3, 4 })
        foreach (var tier in Tiers)
            data.Add(format, channels, tier);
        return data;
    }

    // Pushes the rows one at a time, each in a span longer than the row (the tail must not be read), and takes each output
    // row into a span longer than the output row, prefilled with a sentinel (the tail must not be written).
    private static byte[] Stream(byte[] pixels, int width, int height, int channels, SampleFormat format, int d, int tier)
    {
        var box = new BoxDownscaler(width, height, channels, format, d, tier);
        var rowBytes = width * channels * Bps(format);
        box.SourceRowBytes.ShouldBe(rowBytes);
        var result = new List<byte>();
        var row = new byte[rowBytes + 7];
        var output = new byte[box.OutputRowBytes + 9];
        for (var y = 0; y < height; y++)
        {
            pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(row);
            row.AsSpan(rowBytes).Fill(0xC3);
            var ready = box.PushRow(row);
            ready.ShouldBe((y + 1) % d == 0 || y == height - 1);
            if (!ready)
                continue;
            output.AsSpan().Fill(0xA5);
            box.TakeRow(output);
            output.AsSpan(box.OutputRowBytes).ToArray().ShouldAllBe(b => b == 0xA5);
            result.AddRange(output.AsSpan(0, box.OutputRowBytes).ToArray());
        }
        box.IsComplete.ShouldBeTrue();
        (box.RowsPushed, box.RowsTaken).ShouldBe((height, box.OutputHeight));
        return [.. result];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheFilterIsTheReferenceOnEveryTier(SampleFormat format, int channels, int tier)
    {
        var seed = 0;
        foreach (var d in Factors)
        foreach (var width in Widths)
        foreach (var height in new[] { 1, 3, d - 1, d + 1, 2 * d + 3 }.Where(h => h is > 0 and <= 40).Distinct())
        foreach (var alphaKind in channels is 2 or 4 ? new[] { 0, 1, 2, 3 } : [2])
        {
            var pixels = Image(width, height, channels, format, alphaKind, ++seed);
            var expected = Reference(pixels, width, height, channels, format, d);
            Stream(pixels, width, height, channels, format, d, tier)
                .ShouldBe(expected, $"{format} ch{channels} tier{tier} d{d} {width}x{height} alpha{alphaKind}");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheOneShotHelperAndAWindowAreTheStreamedRows(SampleFormat format, int channels, int tier)
    {
        const int width = 67, height = 45;
        var pixels = Image(width, height, channels, format, 3, channels * 31 + (int)format);
        foreach (var d in new[] { 2, 4, 8, 16 })
        {
            var expected = Reference(pixels, width, height, channels, format, d);
            using var source = RasterImage.Wrap(width, height, channels, format, pixels, bitDepth: format == SampleFormat.UInt16 ? 12 : 0,
                metadata: new ImageMetadata { Icc = new byte[] { 1, 2, 3 } });
            var (ow, oh) = new DecodeOptions { ScaleDenominator = d }.GetScaledSize(width, height);
            using (var whole = BoxDownscaler.Downscale(source, d, new PixelRect(0, 0, ow, oh), null, tier))
            {
                (whole.Width, whole.Height, whole.Channels, whole.SampleFormat).ShouldBe((ow, oh, channels, format));
                whole.BitDepth.ShouldBe(source.BitDepth);
                whole.Metadata.Icc.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
                whole.Pixels.ToArray().ShouldBe(expected);
            }

            // A region of the scaled image: the window GetSourceRect gives, pushed as column slices of the full rows,
            // is that crop of the whole downscale.
            var bpp = channels * Bps(format);
            foreach (var region in new PixelRect[] { new(0, 0, 1, 1), new(1, 1, ow - 1, oh - 1), new(ow - 1, oh - 1, 1, 1), new(ow / 3, 0, Math.Max(1, ow / 2), oh) })
            {
                var crop = new byte[region.Width * region.Height * bpp];
                for (var y = 0; y < region.Height; y++)
                    expected.AsSpan(((region.Y + y) * ow + region.X) * bpp, region.Width * bpp).CopyTo(crop.AsSpan(y * region.Width * bpp));

                using (var part = BoxDownscaler.Downscale(source, d, region, null, tier))
                    part.Pixels.ToArray().ShouldBe(crop, $"d{d} {region}");

                var rect = BoxDownscaler.GetSourceRect(region, width, height, d);
                (rect.X % d, rect.Y % d).ShouldBe((0, 0));
                var box = new BoxDownscaler(rect.Width, rect.Height, channels, format, d, tier);
                (box.OutputWidth, box.OutputHeight).ShouldBe((region.Width, region.Height));
                var streamed = new byte[crop.Length];
                for (var y = 0; y < rect.Height; y++)
                {
                    var row = pixels.AsSpan(((rect.Y + y) * width + rect.X) * bpp); // the rest of the image follows: not read
                    if (box.PushRow(row))
                        box.TakeRow(streamed.AsSpan(box.RowsTaken * box.OutputRowBytes));
                }
                streamed.ShouldBe(crop, $"window d{d} {region}");
            }
        }
    }

    [Theory]
    [InlineData(65536)] // the last height the 8-bit path sums in 32-bit columns: 255 · 255 · 65536 < 2^32
    [InlineData(65537)] // the first it sums in 64 bits
    public void EightBitSumsDoNotOverflowAtTheColumnSumLimit(int height)
    {
        // One column of opaque white and one of a colour, every row: the largest possible column sums.
        const int width = 2;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 8)
        {
            pixels.AsSpan(i, 4).Fill(255);
            pixels[i + 4] = 200; pixels[i + 5] = 100; pixels[i + 6] = 1; pixels[i + 7] = 255;
        }
        foreach (var tier in Tiers)
        {
            Stream(pixels, width, height, 4, SampleFormat.UInt8, 1 << 17, tier)
                .ShouldBe(new byte[] { 228, 178, 128, 255 }, $"tier {tier}"); // (255 + 200 + 1)/2, (255 + 100 + 1)/2, (255 + 1 + 1)/2
        }
    }

    /// <summary>
    /// The 8-bit RGB / RGBA emit sums in 32 bits up to 2^16-pixel boxes (shifting for power-of-two ones) and in 64 bits
    /// beyond: at and around that limit, with the largest sums (white, and a colour, at full alpha) and with noise, every
    /// size gives the reference.
    /// </summary>
    [Theory]
    [InlineData(256, 256, 256)] // 2^16 pixels, a power of two: the shift
    [InlineData(256, 255, 256)] // 256 · 255: the 32-bit division
    [InlineData(512, 128, 512)] // 2^16 again, from a wider box
    [InlineData(512, 129, 512)] // 512 · 129 > 2^16: the 64-bit path
    [InlineData(300, 256, 256)] // a full box and a partial one at the right edge
    public void EightBitBoxesAtTheThirtyTwoBitLimitAreTheReference(int width, int height, int d)
    {
        foreach (var channels in new[] { 3, 4 })
        {
            var extreme = new byte[width * height * channels];
            for (var i = 0; i < extreme.Length; i += 2 * channels)
            {
                extreme.AsSpan(i, channels).Fill(255);
                if (i + channels < extreme.Length)
                {
                    extreme[i + channels] = 200; extreme[i + channels + 1] = 100; extreme[i + channels + 2] = 1;
                    if (channels == 4)
                        extreme[i + 7] = 255;
                }
            }
            foreach (var pixels in new[] { extreme, Image(width, height, channels, SampleFormat.UInt8, 3, width + height) })
            {
                var expected = Reference(pixels, width, height, channels, SampleFormat.UInt8, d);
                foreach (var tier in Tiers)
                    Stream(pixels, width, height, channels, SampleFormat.UInt8, d, tier).ShouldBe(expected, $"ch{channels} tier{tier}");
            }
        }
    }

    [Fact]
    public void AHugeFactorAveragesTheWholeImage()
    {
        const int width = 37, height = 11;
        var pixels = Image(width, height, 4, SampleFormat.UInt16, 3, 5);
        using var source = RasterImage.Wrap(width, height, 4, SampleFormat.UInt16, pixels);
        using var one = BoxDownscaler.Downscale(source, 1 << 30);
        (one.Width, one.Height).ShouldBe((1, 1));
        one.Pixels.ToArray().ShouldBe(Reference(pixels, width, height, 4, SampleFormat.UInt16, 1 << 30));
    }

    [Fact]
    public void FactorOneKeepsOpaquePixelsAndZeroesTransparentColour()
    {
        var pixels = new byte[] { 10, 20, 30, 255, 40, 50, 60, 0, 70, 80, 90, 128 };
        foreach (var tier in Tiers)
            Stream(pixels, 3, 1, 4, SampleFormat.UInt8, 1, tier).ShouldBe(new byte[] { 10, 20, 30, 255, 0, 0, 0, 0, 70, 80, 90, 128 });
    }

    [Fact]
    public void TheCallSequenceIsChecked()
    {
        var box = new BoxDownscaler(4, 3, 1, SampleFormat.UInt8, 2);
        Should.Throw<InvalidOperationException>(() => box.TakeRow(new byte[2]));
        Should.Throw<ArgumentException>(() => box.PushRow(new byte[3]));
        box.PushRow(new byte[4]).ShouldBeFalse();
        box.PushRow(new byte[4]).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => box.PushRow(new byte[4]));
        Should.Throw<ArgumentException>(() => box.TakeRow(new byte[1]));
        box.TakeRow(new byte[2]);
        box.PushRow(new byte[4]).ShouldBeTrue(); // the last row completes the partial bottom box
        box.TakeRow(new byte[2]);
        box.IsComplete.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => box.PushRow(new byte[4]));

        Should.Throw<ArgumentOutOfRangeException>(() => new BoxDownscaler(0, 1, 1, SampleFormat.UInt8, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => new BoxDownscaler(1, 1, 5, SampleFormat.UInt8, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => new BoxDownscaler(1, 1, 1, SampleFormat.UInt8, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new BoxDownscaler(1 << 17, 1 << 16, 1, SampleFormat.UInt8, 1 << 17)); // a 2^33-pixel box
        Should.Throw<ArgumentException>(() => BoxDownscaler.GetSourceRect(new PixelRect(5, 0, 1, 1), 9, 9, 2));
    }

    /// <summary>
    /// A foreign check: ImageMagick's <c>-scale</c> to an exact fraction averages each box of pixels too. On an opaque 8-bit
    /// image whose sides the factor divides, it gives exactly our bytes, so neither side linearises or weights differently.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void MagickScaleOfAnOpaqueImageIsTheBoxFilter(int d)
    {
        const int width = 64, height = 48;
        var pixels = Image(width, height, 3, SampleFormat.UInt8, 2, d);
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGB"));
        image.Scale((uint)(width / d), (uint)(height / d));
        using var magick = image.GetPixels();
        var theirs = magick.ToByteArray(PixelMapping.RGB)!;
        theirs.ShouldBe(Reference(pixels, width, height, 3, SampleFormat.UInt8, d));
    }
}
