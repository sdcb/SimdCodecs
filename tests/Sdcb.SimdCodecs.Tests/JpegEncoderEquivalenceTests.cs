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

    [Fact]
    public void ChromaDownsamplingTiersAreIdentical()
    {
        foreach (var count in new[] { 1, 3, 4, 8, 13, 40 })
        {
            var r0 = RandomFloats(2 * count, count, -128f, 128f);
            var r1 = RandomFloats(2 * count, count + 100, -128f, 128f);
            var expected = new float[count];
            JpegEncoderKernels.Downsample2x2Scalar(r0, r1, expected, 0, count);

            if (Sse3.IsSupported || AdvSimd.Arm64.IsSupported)
            {
                var v128 = new float[count];
                var x = JpegEncoderKernels.Downsample2x2V128(r0, r1, v128, count);
                JpegEncoderKernels.Downsample2x2Scalar(r0, r1, v128, x, count);
                v128.ShouldBe(expected, $"count {count}: Vector128");
            }

            if (Avx2.IsSupported)
            {
                var v256 = new float[count];
                var x = JpegEncoderKernels.Downsample2x2V256(r0, r1, v256, count);
                JpegEncoderKernels.Downsample2x2Scalar(r0, r1, v256, x, count);
                v256.ShouldBe(expected, $"count {count}: Vector256");
            }
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
