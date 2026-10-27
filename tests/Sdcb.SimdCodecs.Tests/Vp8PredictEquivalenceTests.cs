using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class Vp8PredictEquivalenceTests
{
    [Fact]
    public void VerticalPredictVectorMatchesScalar()
    {
        var random = new Random(0xA11);
        foreach (int size in new[] { 8, 16 })
        {
            int stride = size + 8;
            int height = size + 8;
            for (int y = 1; y <= height - size; y += size)
            {
                byte[] plane = new byte[stride * height];
                FillPredictPlane(plane, random);
                byte[] scalar = (byte[])plane.Clone();
                byte[] vector = (byte[])plane.Clone();
                const int x = 4;

                Vp8Decoder.PredictLargeScalar(ref scalar[y * stride + x], stride, size, 1, true, true, ref scalar[(y - 1) * stride + x]);
                Vp8Decoder.PredictVerticalVector(ref vector[y * stride + x], stride, size, ref vector[(y - 1) * stride + x]);

                Assert.Equal(scalar, vector);
            }
        }
    }

    [Fact]
    public void TrueMotionPredictVectorMatchesScalar()
    {
        var random = new Random(0xB22);
        foreach (int size in new[] { 8, 16 })
        {
            int stride = size + 8;
            int height = size + 8;
            for (int y = 1; y <= height - size; y += size)
            {
                byte[] plane = new byte[stride * height];
                FillPredictPlane(plane, random);
                byte[] scalar = (byte[])plane.Clone();
                byte[] vector = (byte[])plane.Clone();
                const int x = 4;

                Vp8Decoder.PredictLargeScalar(ref scalar[y * stride + x], stride, size, 3, true, true, ref scalar[(y - 1) * stride + x]);
                Vp8Decoder.PredictTrueMotionVector(ref vector[y * stride + x], stride, size, ref vector[(y - 1) * stride + x]);

                Assert.Equal(scalar, vector);
            }
        }
    }

    [Fact]
    public void DcPredictVectorMatchesScalarForAllBorderCombinations()
    {
        var random = new Random(0xC33);
        foreach (int size in new[] { 8, 16 })
        {
            int stride = size + 8;
            int height = size + 8;
            // y/x == 1 exercises the no-above / no-left DC branches.
            foreach (int y in new[] { 1, 4 })
            {
                foreach (int x in new[] { 1, 4 })
                {
                    for (int pattern = 0; pattern < 3; pattern++)
                    {
                        byte[] plane = new byte[stride * height];
                        FillPredictPlane(plane, random, pattern);
                        byte[] scalar = (byte[])plane.Clone();
                        byte[] vector = (byte[])plane.Clone();

                        Vp8Decoder.PredictLargeScalar(ref scalar[y * stride + x], stride, size, 0, y > 1, x > 1, ref scalar[(y - 1) * stride + x]);
                        Vp8Decoder.PredictDcVector(ref vector[y * stride + x], stride, size, y > 1, x > 1, ref vector[(y - 1) * stride + x]);

                        Assert.Equal(scalar, vector);
                    }
                }
            }
        }
    }

    [Fact]
    public void SubblockPredictVectorMatchesScalarForAllModes()
    {
        if (!System.Runtime.Intrinsics.X86.Ssse3.IsSupported && !System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            Assert.Skip("Needs Ssse3 or AdvSimd.Arm64.");
        }

        var random = new Random(0xD44);
        const int stride = 16;
        for (int iteration = 0; iteration < 4000; iteration++)
        {
            byte[] plane = new byte[stride * 6];
            FillPredictPlane(plane, random, iteration % 3);
            if (iteration % 7 == 0)
            {
                // Extremes exercise the TrueMotion clamp and the Avg3 rounding carry.
                for (int i = 0; i < plane.Length; i++)
                {
                    plane[i] = (byte)(random.Next(2) * 255);
                }
            }

            for (int mode = 0; mode < 10; mode++)
            {
                byte[] scalar = (byte[])plane.Clone();
                byte[] vector = (byte[])plane.Clone();
                const int x = 2;
                const int y = 1;

                Vp8Decoder.PredictSubblockScalar(ref scalar[y * stride + x], stride, mode, ref scalar[(y - 1) * stride + x]);
                Vp8Decoder.PredictSubblockVector(ref vector[y * stride + x], stride, mode, ref vector[(y - 1) * stride + x]);

                Assert.True(scalar.AsSpan().SequenceEqual(vector), $"mode {mode}, iteration {iteration}");
            }
        }
    }

    private static void FillPredictPlane(byte[] plane, Random random, int pattern = 0)
    {
        for (int i = 0; i < plane.Length; i++)
        {
            plane[i] = pattern switch
            {
                1 => (byte)(i % 255),
                2 => (byte)(255 - i % 128),
                _ => (byte)random.Next(256)
            };
        }
    }
}
