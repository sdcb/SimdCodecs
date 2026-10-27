using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class WebpRgbaEquivalenceTests
{
    [Fact]
    public void VectorKernelMatchesScalarForEveryYuvCombination()
    {
        Span<byte> y = stackalloc byte[16];
        Span<byte> u = stackalloc byte[16];
        Span<byte> v = stackalloc byte[16];
        Span<byte> scalar = stackalloc byte[16 * 4];
        Span<byte> vector = stackalloc byte[16 * 4];

        for (int yValue = 0; yValue < 256; yValue++)
        {
            y.Fill((byte)yValue);
            for (int uValue = 0; uValue < 256; uValue++)
            {
                u.Fill((byte)uValue);
                for (int firstV = 0; firstV < 256; firstV += 16)
                {
                    for (int column = 0; column < 16; column++)
                    {
                        v[column] = (byte)(firstV + column);
                    }

                    WebpDecoder.ConvertRowScalar(y, u, v, [], scalar);
                    WebpDecoder.ConvertRowVector128(y, u, v, [], vector);
                    if (!vector.SequenceEqual(scalar))
                    {
                        Assert.Fail(
                            $"YUV conversion differs at Y={yValue}, U={uValue}, V={firstV}..{firstV + 15}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void Vector256KernelMatchesScalarForEveryYuvCombination()
    {
        if (!System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            Assert.Skip("Vector256 is not accelerated here");
        }

        // 32 pixels per row: every Y × U pair with V = 0..255 in 8 rows; Y and U vary inside the
        // row too, so lane placement mistakes in the interleaves show up.
        byte[] y = new byte[32];
        byte[] u = new byte[32];
        byte[] v = new byte[32];
        byte[] scalar = new byte[32 * 4];
        byte[] vector = new byte[32 * 4];
        for (int yValue = 0; yValue < 256; yValue++)
        {
            for (int uValue = 0; uValue < 256; uValue++)
            {
                for (int column = 0; column < 32; column++)
                {
                    y[column] = (byte)(yValue + column * 7);
                    u[column] = (byte)(uValue + column * 3);
                }

                for (int firstV = 0; firstV < 256; firstV += 32)
                {
                    for (int column = 0; column < 32; column++)
                    {
                        v[column] = (byte)(firstV + column);
                    }

                    WebpDecoder.ConvertRowScalar(y, u, v, [], scalar);
                    WebpDecoder.ConvertRowVector256(y, u, v, [], vector);
                    if (!vector.AsSpan().SequenceEqual(scalar))
                    {
                        Assert.Fail($"YUV conversion differs at Y={yValue}, U={uValue}, V={firstV}..{firstV + 31}.");
                    }
                }
            }
        }
    }

    [Fact]
    public void FancyUpsamplingMatchesScalarForOffsetsAndOddWidths()
    {
        var random = new Random(0x9649);
        for (int chromaWidth = 1; chromaWidth <= 70; chromaWidth++)
        {
            byte[] current = new byte[chromaWidth + 8];
            byte[] adjacent = new byte[chromaWidth + 8];
            const int inputOffset = 3;
            for (int column = 0; column < chromaWidth; column++)
            {
                current[inputOffset + column] = (column & 1) == 0 ? byte.MinValue : byte.MaxValue;
                adjacent[inputOffset + column] = (column & 1) == 0 ? byte.MaxValue : byte.MinValue;
            }

            AssertFancyEquivalent(current, adjacent, inputOffset, chromaWidth, chromaWidth * 2);
            AssertFancyEquivalent(current, adjacent, inputOffset, chromaWidth, chromaWidth * 2 - 1);

            for (int column = 0; column < chromaWidth; column++)
            {
                current[inputOffset + column] = (byte)random.Next(256);
                adjacent[inputOffset + column] = (byte)random.Next(256);
            }

            AssertFancyEquivalent(current, adjacent, inputOffset, chromaWidth, chromaWidth * 2);
            AssertFancyEquivalent(current, adjacent, inputOffset, chromaWidth, chromaWidth * 2 - 1);
        }
    }

    [Fact]
    public void VectorAndScalarRowsMatchForOffsetsTailsAndExtremeSamples()
    {
        var random = new Random(0x6386);
        for (int width = 1; width <= 35; width++)
        {
            foreach (bool useAlpha in new[] { false, true })
            {
                byte[] y = new byte[width + 9];
                byte[] u = new byte[width + 9];
                byte[] v = new byte[width + 9];
                byte[] alpha = new byte[width + 9];
                int inputOffset = 4;
                for (int column = 0; column < width; column++)
                {
                    bool high = (column & 1) != 0;
                    y[inputOffset + column] = high ? byte.MaxValue : byte.MinValue;
                    u[inputOffset + column] = high ? byte.MinValue : byte.MaxValue;
                    v[inputOffset + column] = high ? byte.MaxValue : byte.MinValue;
                    alpha[inputOffset + column] = high ? (byte)31 : (byte)224;
                }

                AssertEquivalent(y, u, v, useAlpha ? alpha : [], inputOffset, width);

                for (int column = 0; column < width; column++)
                {
                    y[inputOffset + column] = (byte)random.Next(256);
                    u[inputOffset + column] = (byte)random.Next(256);
                    v[inputOffset + column] = (byte)random.Next(256);
                    alpha[inputOffset + column] = (byte)random.Next(256);
                }

                AssertEquivalent(y, u, v, useAlpha ? alpha : [], inputOffset, width);
            }
        }
    }

    private static void AssertEquivalent(
        byte[] y,
        byte[] u,
        byte[] v,
        byte[] alpha,
        int inputOffset,
        int width)
    {
        const int outputOffset = 7;
        var scalar = Enumerable.Repeat((byte)0xA5, outputOffset + width * 4 + 11).ToArray();
        var vector = (byte[])scalar.Clone();
        var vector256 = (byte[])scalar.Clone();
        ReadOnlySpan<byte> alphaRow = alpha.Length == 0
            ? ReadOnlySpan<byte>.Empty
            : alpha.AsSpan(inputOffset, width);

        WebpDecoder.ConvertRowScalar(
            y.AsSpan(inputOffset, width),
            u.AsSpan(inputOffset, width),
            v.AsSpan(inputOffset, width),
            alphaRow,
            scalar.AsSpan(outputOffset, width * 4));
        WebpDecoder.ConvertRowVector128(
            y.AsSpan(inputOffset, width),
            u.AsSpan(inputOffset, width),
            v.AsSpan(inputOffset, width),
            alphaRow,
            vector.AsSpan(outputOffset, width * 4));
        WebpDecoder.ConvertRowVector256(
            y.AsSpan(inputOffset, width),
            u.AsSpan(inputOffset, width),
            v.AsSpan(inputOffset, width),
            alphaRow,
            vector256.AsSpan(outputOffset, width * 4));

        Assert.Equal(scalar, vector);
        Assert.Equal(scalar, vector256);
    }

    private static void AssertFancyEquivalent(
        byte[] current,
        byte[] adjacent,
        int inputOffset,
        int chromaWidth,
        int width)
    {
        const int outputOffset = 7;
        var scalarT = new ushort[chromaWidth + 2];
        var vectorT = (ushort[])scalarT.Clone();
        var scalar = Enumerable.Repeat((byte)0xA5, outputOffset + width + 5).ToArray();
        var vector = (byte[])scalar.Clone();
        ReadOnlySpan<byte> currentRow = current.AsSpan(inputOffset, chromaWidth);
        ReadOnlySpan<byte> adjacentRow = adjacent.AsSpan(inputOffset, chromaWidth);
        Span<ushort> scalarScratch = scalarT.AsSpan(1, chromaWidth);
        Span<ushort> vectorScratch = vectorT.AsSpan(1, chromaWidth);

        WebpDecoder.UpsampleFancyScalar(currentRow, adjacentRow, scalarScratch,
            scalar.AsSpan(outputOffset, width));
        WebpDecoder.UpsampleFancyVector128(currentRow, adjacentRow, vectorScratch,
            vector.AsSpan(outputOffset, width));

        Assert.Equal(scalar, vector);
        Assert.Equal(scalarT, vectorT);
    }
}
