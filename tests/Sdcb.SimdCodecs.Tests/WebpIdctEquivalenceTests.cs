using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class WebpIdctEquivalenceTests
{
    [Fact]
    public void DcOnlyFastPathMatchesGeneralTransform()
    {
        var random = new Random(0x6386);
        short[] coefficients = new short[16];
        short[] fast = new short[24];
        short[] general = new short[24];
        int[] dcValues = [-32768, -32767, -4096, -1, 0, 1, 4095, 32766, 32767];

        foreach (int dc in dcValues.Concat(Enumerable.Range(0, 256).Select(_ => random.Next(short.MinValue, short.MaxValue + 1))))
        {
            Array.Clear(coefficients);
            coefficients[0] = (short)dc;
            fast.AsSpan().Fill(short.MinValue);
            general.AsSpan().Fill(short.MinValue);

            Vp8Decoder.InverseDctShort(ref coefficients[0], fast.AsSpan(4, 16), hasAcCoefficients: false);
            Vp8Decoder.InverseDctShort(ref coefficients[0], general.AsSpan(4, 16), hasAcCoefficients: true);

            Assert.Equal(general, fast);
        }
    }

    [Fact]
    public void VectorTransformAndAddMatchesScalarIncludingWrapAndClamp()
    {
        if (!System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            Assert.Skip("Vector128 is not accelerated here");
        }

        var random = new Random(0x14_4);
        const int stride = 11;
        for (int trial = 0; trial < 20000; trial++)
        {
            short[] coefficients = new short[16];
            for (int i = 0; i < 16; i++)
            {
                // Mostly realistic magnitudes; every 8th trial uses the full 16-bit range, where the
                // vertical pass wraps and the add saturates.
                coefficients[i] = (short)(trial % 8 == 0
                    ? random.Next(short.MinValue, short.MaxValue + 1)
                    : random.Next(-600, 601) >> random.Next(4));
            }

            byte[] plane = new byte[stride * 6];
            random.NextBytes(plane);
            byte[] expected = (byte[])plane.Clone();
            short[] residual = new short[16];
            short[] scalarInput = (short[])coefficients.Clone();
            Vp8Decoder.InverseDctShort(ref scalarInput[0], residual, hasAcCoefficients: true);
            Vp8Decoder.AddResidualScalar(ref expected[stride + 2], stride, residual);
            Vp8Decoder.InverseDctAddVector(ref coefficients[0], ref plane[stride + 2], stride);
            Assert.Equal(expected, plane);
        }
    }
}
