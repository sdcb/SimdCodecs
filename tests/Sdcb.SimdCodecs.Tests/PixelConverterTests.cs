using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="PixelConverter"/> against <see cref="ReferenceConvert"/>, a per-pixel transcription of the mapping its
/// documentation states, for every source <see cref="PixelFormat"/> × every destination <see cref="PixelFormat"/> (16 ×
/// 16, 8- and 16-bit, straight and premultiplied, grey + alpha). Widths run past every vector step and tail, and past the
/// 256-pixel chunk of the staged path; spans longer than the row check that nothing outside it is read into the output
/// or written. CI runs this on every tier (AVX2, Vector128, scalar, arm64).
/// </summary>
public sealed class PixelConverterTests
{
    /// <summary>The five 8-bit layouts of 1.x — the formats whose output is frozen by the 1.x goldens.</summary>
    public static readonly PixelFormat[] Formats =
        [PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Bgr24, PixelFormat.Gray8];

    public static TheoryData<int, SampleFormat, int> Layouts()
    {
        var data = new TheoryData<int, SampleFormat, int>();
        foreach (var channels in new[] { 1, 2, 3, 4 })
        foreach (var sample in new[] { SampleFormat.UInt8, SampleFormat.UInt16 })
        for (var f = 0; f < PixelFormat.All.Length; f++)
            data.Add(channels, sample, f);
        return data;
    }

    public static TheoryData<int, int> Pairs()
    {
        var data = new TheoryData<int, int>();
        for (var s = 0; s < PixelFormat.All.Length; s++)
        for (var d = 0; d < PixelFormat.All.Length; d++)
            data.Add(s, d);
        return data;
    }

    // ---- the reference ------------------------------------------------------------------------------------------

    // A pixel at full precision of its format: samples 0..max (max 255 or 65535), straight or premultiplied.
    private readonly record struct Pixel(int R, int G, int B, int A);

    private static int Max(PixelFormat f) => f.Sample == SampleFormat.UInt16 ? 65535 : 255;

    private static int Sample(ReadOnlySpan<byte> source, PixelFormat f, int index) =>
        f.Sample == SampleFormat.UInt16 ? BitConverter.ToUInt16(source.Slice(index * 2, 2)) : source[index];

    private static void Store(Span<byte> target, PixelFormat f, int index, int value)
    {
        if (f.Sample == SampleFormat.UInt16)
            BitConverter.TryWriteBytes(target.Slice(index * 2, 2), (ushort)value);
        else
            target[index] = (byte)value;
    }

    private static Pixel Read(ReadOnlySpan<byte> source, PixelFormat f, int p)
    {
        var c = f.Channels;
        var max = Max(f);
        int s0 = Sample(source, f, p * c);
        int s1 = c > 1 ? Sample(source, f, p * c + 1) : 0;
        int s2 = c > 2 ? Sample(source, f, p * c + 2) : 0;
        int s3 = c > 3 ? Sample(source, f, p * c + 3) : 0;
        return f.Layout switch
        {
            ChannelLayout.Rgba => new(s0, s1, s2, s3),
            ChannelLayout.Bgra => new(s2, s1, s0, s3),
            ChannelLayout.Rgb => new(s0, s1, s2, max),
            ChannelLayout.Bgr => new(s2, s1, s0, max),
            ChannelLayout.Gray => new(s0, s0, s0, max),
            _ => new(s0, s0, s0, s1),
        };
    }

    // The documented order: unpremultiply at the source depth, change depth, change layout, premultiply at the
    // destination depth. Grey sources keep their sample; colour reaches grey as BT.601 luma at the working depth.
    internal static byte[] ReferenceConvert(ReadOnlySpan<byte> source, PixelFormat from, PixelFormat to, int pixels)
    {
        var result = new byte[pixels * to.BytesPerPixel];
        int inMax = Max(from), outMax = Max(to);
        for (var p = 0; p < pixels; p++)
        {
            var px = Read(source, from, p);
            if (from.IsPremultiplied && !to.IsPremultiplied)
                px = new(Unpremultiply(px.R, px.A, inMax), Unpremultiply(px.G, px.A, inMax), Unpremultiply(px.B, px.A, inMax), px.A);
            if (inMax != outMax)
            {
                static int Depth(int v, bool widen) => widen ? v * 257 : v >> 8;
                var widen = outMax > inMax;
                px = new(Depth(px.R, widen), Depth(px.G, widen), Depth(px.B, widen), Depth(px.A, widen));
            }
            if (to.IsPremultiplied && !from.IsPremultiplied)
                px = new(Premultiply(px.R, px.A, outMax), Premultiply(px.G, px.A, outMax), Premultiply(px.B, px.A, outMax), px.A);

            var grey = from.IsGray ? px.R : (77 * px.R + 150 * px.G + 29 * px.B + 128) >> 8;
            var o = p * to.Channels;
            switch (to.Layout)
            {
                case ChannelLayout.Rgba: Store(result, to, o, px.R); Store(result, to, o + 1, px.G); Store(result, to, o + 2, px.B); Store(result, to, o + 3, px.A); break;
                case ChannelLayout.Bgra: Store(result, to, o, px.B); Store(result, to, o + 1, px.G); Store(result, to, o + 2, px.R); Store(result, to, o + 3, px.A); break;
                case ChannelLayout.Rgb: Store(result, to, o, px.R); Store(result, to, o + 1, px.G); Store(result, to, o + 2, px.B); break;
                case ChannelLayout.Bgr: Store(result, to, o, px.B); Store(result, to, o + 1, px.G); Store(result, to, o + 2, px.R); break;
                case ChannelLayout.Gray: Store(result, to, o, grey); break;
                default: Store(result, to, o, grey); Store(result, to, o + 1, px.A); break;
            }
        }
        return result;
    }

    // round(c × a / max); max is odd, so there are no halves.
    private static int Premultiply(int c, int a, int max) => (int)Math.Round((double)c * a / max, MidpointRounding.AwayFromZero);

    // 0 when a = 0, else min(max, round(c × max / a)) with halves up.
    private static int Unpremultiply(int c, int a, int max) =>
        a == 0 ? 0 : (int)Math.Min(max, Math.Floor((double)c * max / a + 0.5));

    /// <summary>The 1.x reference, in its 1.x terms (channels + sample format → an 8-bit layout), for the tests that
    /// compare decoders against "<c>ToRgba8</c> converted".</summary>
    internal static byte[] Reference(ReadOnlySpan<byte> samples, int channels, SampleFormat sampleFormat, PixelFormat format, int pixels) =>
        ReferenceConvert(samples, PixelFormat.ForChannels(channels, sampleFormat), format, pixels);

    // ---- the matrix ---------------------------------------------------------------------------------------------

    private static readonly int[] Widths = [.. Enumerable.Range(0, 70), 100, 255, 256, 257, 300, 513, 1000];

    [Theory]
    [MemberData(nameof(Pairs))]
    public void ConvertRowMatchesTheReferenceForEveryPair(int from, int to)
    {
        var source = PixelFormat.All[from];
        var target = PixelFormat.All[to];
        var random = new Random(from * 31 + to);
        foreach (var width in Widths)
        {
            // Trailing source bytes the row must not read into its output; trailing destination bytes it must not write.
            var input = new byte[width * source.BytesPerPixel + 37];
            random.NextBytes(input);
            var destination = new byte[width * target.BytesPerPixel + 41];
            Array.Fill(destination, (byte)0xA5);

            PixelConverter.ConvertRow(input, source, destination, target, width);

            var expected = ReferenceConvert(input, source, target, width);
            destination.AsSpan(0, width * target.BytesPerPixel).ToArray().ShouldBe(expected, $"{source} -> {target}, width {width}");
            destination.AsSpan(width * target.BytesPerPixel).ToArray().ShouldAllBe(v => v == 0xA5, $"{source} -> {target}, width {width}: wrote past the row");
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void InPlaceConversionMatchesTheReference(int from, int to)
    {
        var source = PixelFormat.All[from];
        var target = PixelFormat.All[to];
        if (source.BytesPerPixel != target.BytesPerPixel)
            return; // in place needs equal pixel sizes
        var random = new Random(from * 37 + to + 5);
        foreach (var width in Widths)
        {
            var row = new byte[width * source.BytesPerPixel];
            random.NextBytes(row);
            var expected = ReferenceConvert(row, source, target, width);
            PixelConverter.ConvertRow(row, source, row, target, width);
            row.ShouldBe(expected, $"{source} -> {target} in place, width {width}");
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ConvertRowFromRasterChannelsMatchesTheReference(int channels, SampleFormat sampleFormat, int format)
    {
        var target = PixelFormat.All[format];
        var sampleBytes = sampleFormat == SampleFormat.UInt16 ? 2 : 1;
        var random = new Random(channels * 100 + (int)sampleFormat * 10 + format);
        foreach (var width in new[] { 0, 1, 15, 16, 17, 257, 1000 })
        {
            var source = new byte[width * channels * sampleBytes + 13];
            random.NextBytes(source);
            var destination = new byte[width * target.BytesPerPixel];
            PixelConverter.ConvertRow(source, channels, sampleFormat, destination, target, width);
            destination.ShouldBe(Reference(source, channels, sampleFormat, target, width), $"width {width}");
        }
    }

    // ---- the frozen numbers -------------------------------------------------------------------------------------

    [Fact]
    public void EightToSixteenBitsIsTimes257()
    {
        var bytes = Enumerable.Range(0, 256).Select(v => (byte)v).ToArray();
        var wide = new byte[256 * 2];
        PixelConverter.ConvertRow(bytes, PixelFormat.Gray8, wide, PixelFormat.Gray16, 256);
        for (var v = 0; v < 256; v++)
            BitConverter.ToUInt16(wide, v * 2).ShouldBe((ushort)(v * 257));
    }

    [Fact]
    public void SixteenToEightBitsKeepsTheHighByte()
    {
        var wide = new byte[65536 * 2];
        for (var v = 0; v < 65536; v++)
            BitConverter.TryWriteBytes(wide.AsSpan(v * 2), (ushort)v);
        var narrow = new byte[65536];
        PixelConverter.ConvertRow(wide, PixelFormat.Gray16, narrow, PixelFormat.Gray8, 65536);
        for (var v = 0; v < 65536; v++)
            narrow[v].ShouldBe((byte)(v >> 8));
    }

    [Fact]
    public void PremultiplyingEveryEightBitPairIsRounded()
    {
        // Every (c, a): c in R, G and B, a in A.
        var rgba = new byte[256 * 256 * 4];
        for (var a = 0; a < 256; a++)
        for (var c = 0; c < 256; c++)
        {
            var i = (a * 256 + c) * 4;
            rgba[i] = rgba[i + 1] = rgba[i + 2] = (byte)c;
            rgba[i + 3] = (byte)a;
        }
        var pre = new byte[rgba.Length];
        PixelConverter.ConvertRow(rgba, PixelFormat.Rgba32, pre, PixelFormat.Rgba32Premultiplied, 256 * 256);
        for (var a = 0; a < 256; a++)
        for (var c = 0; c < 256; c++)
        {
            var i = (a * 256 + c) * 4;
            pre[i].ShouldBe((byte)Premultiply(c, a, 255), $"c {c} a {a}");
            pre[i + 3].ShouldBe((byte)a);
        }
    }

    [Fact]
    public void UnpremultiplyingInvertsPremultiplying()
    {
        // For every alpha, every premultiplied value a straight colour can produce comes back to a colour that
        // premultiplies to it again: the round trip is stable at the precision alpha leaves.
        for (var a = 1; a < 256; a++)
        {
            var row = new byte[256 * 4];
            for (var c = 0; c < 256; c++)
            {
                row[c * 4] = (byte)Premultiply(c, a, 255);
                row[c * 4 + 3] = (byte)a;
            }
            var straight = new byte[row.Length];
            PixelConverter.ConvertRow(row, PixelFormat.Rgba32Premultiplied, straight, PixelFormat.Rgba32, 256);
            for (var c = 0; c < 256; c++)
                Premultiply(straight[c * 4], a, 255).ShouldBe(row[c * 4], $"a {a} c {c}");
        }
    }

    [Fact]
    public void TransparentPremultipliedPixelsUnpremultiplyToZero()
    {
        var row = new byte[] { 9, 8, 7, 0 }; // not a valid premultiplied pixel, but a = 0 means colour 0
        var straight = new byte[4];
        PixelConverter.ConvertRow(row, PixelFormat.Rgba32Premultiplied, straight, PixelFormat.Rgba32, 1);
        straight.ShouldBe(new byte[] { 0, 0, 0, 0 });
    }

    [Fact]
    public void GreyPixelsKeepTheirValueInGray8()
    {
        // The luma weights sum to 256, so R = G = B = v comes back as v for every v.
        var rgb = new byte[256 * 3];
        for (var v = 0; v < 256; v++)
            rgb[3 * v] = rgb[3 * v + 1] = rgb[3 * v + 2] = (byte)v;
        var grey = new byte[256];
        PixelConverter.ConvertRow(rgb, 3, SampleFormat.UInt8, grey, PixelFormat.Gray8, 256);
        grey.ShouldBe(Enumerable.Range(0, 256).Select(v => (byte)v).ToArray());
    }

    // ---- layout, strides, refusals ------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 3, 0, 0, 120, true, 40)]
    [InlineData(10, 3, 0, 0, 119, false, 0)]
    [InlineData(10, 3, 3, 32, 32 * 2 + 30, true, 32)]
    [InlineData(10, 3, 3, 32, 32 * 2 + 29, false, 0)]
    [InlineData(10, 3, 4, 9, 100, false, 0)]     // stride shorter than a row
    [InlineData(10, 3, 4, -1, 100, false, 0)]
    [InlineData(10, 1, 4, 1000, 10, true, 1000)] // one row: the stride never applies
    [InlineData(0, 3, 4, 0, 100, false, 0)]
    [InlineData(10, 3, 6, 0, 240, true, 80)]     // Rgba64: 8 bytes a pixel
    [InlineData(10, 3, 6, 0, 239, false, 0)]
    public void TryGetLayoutStatesTheRequirement(int width, int height, int format, int stride, int length, bool fits, int rowStride)
    {
        PixelConverter.TryGetLayout(width, height, PixelFormat.All[format], stride, length, out var actual).ShouldBe(fits);
        actual.ShouldBe(rowStride);
    }

    [Fact]
    public void TryConvertLeavesTheRowPaddingAlone()
    {
        const int width = 23, height = 5, channels = 3;
        var pixels = new byte[width * height * channels];
        new Random(3).NextBytes(pixels);
        using var image = RasterImage.Wrap(width, height, channels, SampleFormat.UInt8, pixels);
        const int stride = width * 4 + 9;
        var destination = new byte[stride * height];
        Array.Fill(destination, (byte)0x5A);

        PixelConverter.TryConvert(image, destination, stride, PixelFormat.Bgra32).ShouldBeTrue();

        var expected = Reference(pixels, channels, SampleFormat.UInt8, PixelFormat.Bgra32, width * height);
        for (var y = 0; y < height; y++)
        {
            destination.AsSpan(y * stride, width * 4).ToArray().ShouldBe(expected.AsSpan(y * width * 4, width * 4).ToArray());
            destination.AsSpan(y * stride + width * 4, 9).ToArray().ShouldAllBe(v => v == 0x5A);
        }
    }

    [Fact]
    public void ConvertHonoursBothStrides()
    {
        const int width = 19, height = 4;
        var source = PixelFormat.Bgra64;
        var target = PixelFormat.Rgb24;
        int inStride = width * source.BytesPerPixel + 6, outStride = width * target.BytesPerPixel + 5;
        var input = new byte[inStride * height];
        new Random(9).NextBytes(input);
        var output = new byte[outStride * height];
        Array.Fill(output, (byte)0x3C);
        PixelConverter.Convert(input, inStride, source, output, outStride, target, width, height);
        for (var y = 0; y < height; y++)
        {
            var expected = ReferenceConvert(input.AsSpan(y * inStride, width * source.BytesPerPixel), source, target, width);
            output.AsSpan(y * outStride, width * 3).ToArray().ShouldBe(expected);
            output.AsSpan(y * outStride + width * 3, 5).ToArray().ShouldAllBe(v => v == 0x3C);
        }
    }

    [Fact]
    public void FloatSamplesHaveNoIntegerProjection()
    {
        using var image = RasterImage.Wrap(2, 2, 1, SampleFormat.Float32, new byte[16]);
        PixelConverter.TryConvert(image, new byte[16], 0, PixelFormat.Gray8).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() =>
            PixelConverter.ConvertRow(new byte[16], 1, SampleFormat.Float32, new byte[4], PixelFormat.Gray8, 4));
    }

    [Fact]
    public void ShortSpansAreRefused()
    {
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[11], 3, SampleFormat.UInt8, new byte[16], PixelFormat.Rgba32, 4));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[12], 3, SampleFormat.UInt8, new byte[15], PixelFormat.Rgba32, 4));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[23], 3, SampleFormat.UInt16, new byte[16], PixelFormat.Rgba32, 4));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[16], 5, SampleFormat.UInt8, new byte[16], PixelFormat.Rgba32, 1));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(new byte[31], PixelFormat.Rgba64, new byte[16], PixelFormat.Rgba32, 4));
    }

    [Fact]
    public void OverlappingSpansThatAreNotInPlaceAreRefused()
    {
        var buffer = new byte[64];
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(buffer.AsSpan(0, 32), PixelFormat.Rgba32, buffer.AsSpan(4, 32), PixelFormat.Bgra32, 8));
        Should.Throw<ArgumentException>(() =>
            PixelConverter.ConvertRow(buffer.AsSpan(0, 24), PixelFormat.Rgb24, buffer.AsSpan(0, 32), PixelFormat.Rgba32, 8));
    }

    [Fact]
    public void ToRgba8AndToRgba16AreTheRgbaConversions()
    {
        var random = new Random(11);
        foreach (var channels in new[] { 1, 2, 3, 4 })
        foreach (var sample in new[] { SampleFormat.UInt8, SampleFormat.UInt16 })
        {
            const int width = 37, height = 11;
            var pixels = new byte[width * height * channels * (sample == SampleFormat.UInt16 ? 2 : 1)];
            random.NextBytes(pixels);
            using var image = RasterImage.Wrap(width, height, channels, sample, pixels);
            image.ToRgba8().ShouldBe(Reference(pixels, channels, sample, PixelFormat.Rgba32, width * height));
            var wide = image.ToRgba16();
            var expected = Reference(pixels, channels, sample, PixelFormat.Rgba64, width * height);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(wide.AsSpan()).ToArray().ShouldBe(expected);
        }
    }
}
