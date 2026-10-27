using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The encoder's Vector256 / Vector128 kernels against their scalar twins, bit for bit: the
/// same float operations in the same order give the same encoded bytes on every machine.
/// Tiers this machine cannot run are skipped (CI also runs with AVX2 and all intrinsics off).
/// </summary>
public sealed class JpegEncoderEquivalenceTests
{
    private static float[] RandomFloats(int count, int seed, float lo, float hi)
    {
        var r = new Random(seed);
        var a = new float[count];
        for (var i = 0; i < a.Length; i++) a[i] = lo + (float)r.NextDouble() * (hi - lo);
        return a;
    }

    public static IEnumerable<object[]> Quantisers() => [[1], [2], [17]];

    [Theory]
    [MemberData(nameof(Quantisers))]
    public void ForwardDctTiersAreIdentical(int quant)
    {
        // A uniform quantiser keeps the outputs fine-grained (quant 1: every float rounding step shows).
        var recip = new float[64];
        ReadOnlySpan<float> aan = [1.0f, 1.387039845f, 1.306562965f, 1.175875602f, 1.0f, 0.785694958f, 0.541196100f, 0.275899379f];
        for (var u = 0; u < 8; u++)
            for (var v = 0; v < 8; v++)
                recip[u * 8 + v] = 1f / (quant * (aan[v] * 2.828427125f) * (aan[u] * 2.828427125f));

        const int stride = 24;
        var src = RandomFloats(7 * stride + 8 + 4000 * 3, quant, -128f, 127f);
        Span<short> scalar = stackalloc short[64];
        Span<short> v128 = stackalloc short[64];
        Span<short> v256 = stackalloc short[64];
        for (var b = 0; b < 4000; b++)
        {
            var block = src.AsSpan(b * 3);
            JpegEncoderKernels.ForwardDctScalar(block, stride, recip, scalar);
            if (Vector128.IsHardwareAccelerated)
            {
                JpegEncoderKernels.ForwardDctV128(block, stride, recip, v128);
                v128.ToArray().ShouldBe(scalar.ToArray(), $"block {b}: Vector128");
            }

            if (Avx2.IsSupported)
            {
                JpegEncoderKernels.ForwardDctV256(block, stride, recip, v256);
                v256.ToArray().ShouldBe(scalar.ToArray(), $"block {b}: Vector256");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ColourConversionTiersAreIdentical(int channels)
    {
        var r = new Random(channels);
        foreach (var width in new[] { 1, 7, 8, 9, 16, 31, 67 })
        {
            var src = new byte[width * channels];
            r.NextBytes(src);
            var expected = Convert(src, channels, width, 0);
            if (Vector128.IsHardwareAccelerated) Convert(src, channels, width, 1).ShouldBe(expected, $"width {width}: Vector128");
            if (Avx2.IsSupported) Convert(src, channels, width, 2).ShouldBe(expected, $"width {width}: Vector256");
        }
    }

    private static float[] Convert(byte[] src, int channels, int width, int tier)
    {
        var y = new float[width];
        var cb = new float[width];
        var cr = new float[width];
        var x = tier switch
        {
            2 => JpegEncoderKernels.ConvertRowV256(src, channels, width, y, cb, cr),
            1 => JpegEncoderKernels.ConvertRowV128(src, channels, width, y, cb, cr),
            _ => 0,
        };
        JpegEncoderKernels.ConvertRowScalar(src, channels, x, width, y, cb, cr);
        return channels <= 2 ? y : [.. y, .. cb, .. cr];
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void RowPair420TiersAreIdentical(int channels)
    {
        var r = new Random(channels);
        foreach (var width in new[] { 1, 2, 7, 8, 15, 16, 17, 33, 67 })
        {
            var src0 = new byte[width * channels];
            var src1 = new byte[width * channels];
            r.NextBytes(src0);
            r.NextBytes(src1);
            var expected = Pair(src0, src1, channels, width, 0);
            if (Sse3.IsSupported || AdvSimd.Arm64.IsSupported) Pair(src0, src1, channels, width, 1).ShouldBe(expected, $"width {width}: Vector128");
            if (Avx2.IsSupported) Pair(src0, src1, channels, width, 2).ShouldBe(expected, $"width {width}: Vector256");
        }
    }

    private static float[] Pair(byte[] src0, byte[] src1, int channels, int width, int tier)
    {
        var y0 = new float[width];
        var y1 = new float[width];
        var cb = new float[(width + 1) / 2];
        var cr = new float[(width + 1) / 2];
        var x = tier switch
        {
            2 => JpegEncoderKernels.ConvertRowPair420V256(src0, src1, channels, width, y0, y1, cb, cr),
            1 => JpegEncoderKernels.ConvertRowPair420V128(src0, src1, channels, width, y0, y1, cb, cr),
            _ => 0,
        };
        JpegEncoderKernels.ConvertRowPair420Scalar(src0, src1, channels, x, width, y0, y1, cb, cr);
        return [.. y0, .. y1, .. cb, .. cr];
    }

    [Fact]
    public void RowPair420ChromaIsTheChromaOfThe2x2Average()
    {
        // Independent of the kernels' arithmetic: average R, G, B in double, then T.871.
        var r = new Random(5);
        const int width = 37;
        var src0 = new byte[width * 3];
        var src1 = new byte[width * 3];
        r.NextBytes(src0);
        r.NextBytes(src1);
        var all = Pair(src0, src1, 3, width, Avx2.IsSupported ? 2 : 0);
        for (var j = 0; j < (width + 1) / 2; j++)
        {
            int a = 2 * j * 3, b = Math.Min(2 * j + 1, width - 1) * 3;
            double R = (src0[a] + src0[b] + src1[a] + src1[b]) / 4.0;
            double G = (src0[a + 1] + src0[b + 1] + src1[a + 1] + src1[b + 1]) / 4.0;
            double B = (src0[a + 2] + src0[b + 2] + src1[a + 2] + src1[b + 2]) / 4.0;
            all[2 * width + j].ShouldBe((float)(-0.168736 * R - 0.331264 * G + 0.5 * B), 1e-3);
            all[2 * width + (width + 1) / 2 + j].ShouldBe((float)(0.5 * R - 0.418688 * G - 0.081312 * B), 1e-3);
        }
    }
    [Fact]
    public void NonZeroMaskMatchesTheCoefficients()
    {
        var r = new Random(7);
        var zz = new short[64];
        for (var n = 0; n < 2000; n++)
        {
            ulong expected = 0;
            for (var i = 0; i < 64; i++)
            {
                zz[i] = r.Next(4) == 0 ? (short)r.Next(-1024, 1024) : (short)0;
                if (n % 7 == 0) zz[i] = (short)(i == n % 64 ? -32768 : 0);
                if (zz[i] != 0) expected |= 1ul << i;
            }

            JpegEncoderKernels.NonZeroMask(zz).ShouldBe(expected);
        }
    }
}
