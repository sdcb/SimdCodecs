using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class Vp8LoopFilterEquivalenceTests
{
    // Patterns 4 and 5 are steps of ~42 levels: 3 * (q0 - p0) then reaches 124+, the range where
    // RFC 6386 15.2's c(a + 4) >> 3 differs from an unclamped (a + 4) >> 3.
    private const int PatternCount = 6;

    [Fact]
    public void HorizontalVectorFilterMatchesScalarForAllVariantsAndThresholds()
    {
        SkipUnlessVectorKernels();
        const int stride = 40;
        const int x = 4;
        const int y = 8;
        const int length = 16;
        var random = new Random(0x6386);
        int[] levels = [0, 1, 14, 15, 39, 40, 63];
        int[] sharpnessValues = [0, 1, 4, 7];

        foreach (bool simple in new[] { false, true })
        {
            foreach (bool macroblockEdge in new[] { false, true })
            {
                foreach (int level in levels)
                {
                    foreach (int sharpness in sharpnessValues)
                    {
                        int interior = level;
                        if (sharpness != 0)
                        {
                            interior >>= sharpness > 4 ? 2 : 1;
                            interior = Math.Min(interior, 9 - sharpness);
                        }

                        interior = Math.Max(interior, 1);
                        int edge = 2 * (level + 2) + interior;
                        int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;

                        for (int pattern = 0; pattern < PatternCount; pattern++)
                        {
                            byte[] plane = new byte[stride * 24];
                            FillPlane(plane, stride, y, pattern, random);
                            byte[] scalar = (byte[])plane.Clone();
                            byte[] vector = (byte[])plane.Clone();

                            Vp8Decoder.FilterHorizontalScalar(
                                scalar, stride, x, y, length, simple, macroblockEdge,
                                interior, edge, hev);
                            Vector(vector, null, stride, x, y, length, horizontal: true, simple, macroblockEdge,
                                interior, edge, hev);

                            Assert.Equal(scalar, vector);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void ChromaHorizontalVectorFilterMatchesScalarForBothPlanes()
    {
        SkipUnlessVectorKernels();
        const int stride = 40;
        const int x = 4;
        const int y = 8;
        var random = new Random(0xC4);
        foreach (bool macroblockEdge in new[] { false, true })
        {
            foreach (int level in new[] { 1, 14, 15, 39, 40, 63 })
            {
                foreach (int sharpness in new[] { 0, 3, 7 })
                {
                    int interior = level;
                    if (sharpness != 0)
                    {
                        interior >>= sharpness > 4 ? 2 : 1;
                        interior = Math.Min(interior, 9 - sharpness);
                    }

                    interior = Math.Max(interior, 1);
                    int edge = 2 * (level + 2) + interior;
                    int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                    for (int pattern = 0; pattern < PatternCount; pattern++)
                    {
                        byte[] u = new byte[stride * 24];
                        byte[] v = new byte[stride * 24];
                        FillPlane(u, stride, y, pattern, random);
                        FillPlane(v, stride, y, (pattern + 3) % PatternCount, random);
                        byte[] uScalar = (byte[])u.Clone();
                        byte[] vScalar = (byte[])v.Clone();
                        Vp8Decoder.FilterHorizontalScalar(uScalar, stride, x, y, 8, false, macroblockEdge, interior, edge, hev);
                        Vp8Decoder.FilterHorizontalScalar(vScalar, stride, x, y, 8, false, macroblockEdge, interior, edge, hev);
                        Vector(u, v, stride, x, y, 8, horizontal: true, false, macroblockEdge, interior, edge, hev);
                        Assert.Equal(uScalar, u);
                        Assert.Equal(vScalar, v);
                    }
                }
            }
        }
    }

    [Fact]
    public void VerticalVectorFilterMatchesScalarForAllVariantsAndThresholds()
    {
        SkipUnlessVectorKernels();
        const int stride = 40;
        const int x = 8;
        const int y = 4;
        var random = new Random(0x6386);
        int[] levels = [0, 1, 14, 15, 39, 40, 63];
        int[] sharpnessValues = [0, 1, 4, 7];

        foreach (bool simple in new[] { false, true })
        {
            foreach (bool macroblockEdge in new[] { false, true })
            {
                foreach (int level in levels)
                {
                    foreach (int sharpness in sharpnessValues)
                    {
                        foreach (int length in new[] { 8, 16 })
                        {
                            int interior = level;
                            if (sharpness != 0)
                            {
                                interior >>= sharpness > 4 ? 2 : 1;
                                interior = Math.Min(interior, 9 - sharpness);
                            }

                            interior = Math.Max(interior, 1);
                            int edge = 2 * (level + 2) + interior;
                            int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;

                            for (int pattern = 0; pattern < PatternCount; pattern++)
                            {
                                byte[] plane = new byte[stride * 32];
                                FillVerticalPlane(plane, stride, x, pattern, random);
                                byte[] other = new byte[plane.Length];
                                FillVerticalPlane(other, stride, x, pattern, random);
                                byte[] scalar = (byte[])plane.Clone();
                                byte[] vector = (byte[])plane.Clone();
                                byte[] otherScalar = (byte[])other.Clone();
                                byte[] otherVector = (byte[])other.Clone();

                                Vp8Decoder.FilterVerticalScalar(
                                    scalar, stride, x, y, length, simple, macroblockEdge,
                                    interior, edge, hev);
                                Vp8Decoder.FilterVerticalScalar(
                                    otherScalar, stride, x, y, length, simple, macroblockEdge,
                                    interior, edge, hev);
                                Vector(vector, length == 8 ? otherVector : null, stride, x, y, length, horizontal: false,
                                    simple, macroblockEdge, interior, edge, hev);

                                Assert.Equal(scalar, vector);
                                if (length == 8)
                                {
                                    Assert.Equal(otherScalar, otherVector);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // Scalar-versus-SIMD equivalence cannot catch a bug both kernels share, so this compares every
    // kernel with a direct transcription of RFC 6386 15.2-15.3's arithmetic, on edges strong enough
    // that the c(a + 4) / c(a + 3) clamps actually bite (asserted, so the test cannot go vacuous).
    [Fact]
    public void AllKernelsMatchRfc6386ArithmeticOnStrongEdges()
    {
        const int stride = 40;
        var random = new Random(0x15_2);
        foreach (bool simple in new[] { false, true })
        {
            foreach (bool macroblockEdge in new[] { false, true })
            {
                int clampsHit = 0;
                foreach (int level in new[] { 40, 63 })
                {
                    int interior = level;
                    int edge = 2 * (level + 2) + interior;
                    int hev = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                    for (int trial = 0; trial < 64; trial++)
                    {
                        bool outerTaps = (trial & 1) != 0;

                        byte[] horizontal = new byte[stride * 24];
                        for (int row = 0; row < 24; row++)
                        {
                            for (int column = 0; column < stride; column++)
                            {
                                horizontal[row * stride + column] = StrongStep(row - 8, outerTaps, random);
                            }
                        }

                        byte[] expectedH = (byte[])horizontal.Clone();
                        for (int column = 4; column < 20; column++)
                        {
                            clampsHit += ReferenceFilter(expectedH, 8 * stride + column, stride,
                                simple, macroblockEdge, interior, edge, hev);
                        }

                        byte[] scalarH = (byte[])horizontal.Clone();
                        byte[] vectorH = (byte[])horizontal.Clone();
                        Vp8Decoder.FilterHorizontalScalar(scalarH, stride, 4, 8, 16, simple,
                            macroblockEdge, interior, edge, hev);
                        if (Vp8LoopFilter.IsSupported)
                        {
                            Vector(vectorH, null, stride, 4, 8, 16, horizontal: true, simple,
                                macroblockEdge, interior, edge, hev);
                        }
                        else
                        {
                            vectorH = expectedH;
                        }
                        Assert.Equal(expectedH, scalarH);
                        Assert.Equal(expectedH, vectorH);

                        byte[] vertical = new byte[stride * 32];
                        for (int row = 0; row < 32; row++)
                        {
                            for (int column = 0; column < stride; column++)
                            {
                                vertical[row * stride + column] = StrongStep(column - 8, outerTaps, random);
                            }
                        }

                        byte[] expectedV = (byte[])vertical.Clone();
                        for (int row = 4; row < 20; row++)
                        {
                            clampsHit += ReferenceFilter(expectedV, row * stride + 8, 1,
                                simple, macroblockEdge, interior, edge, hev);
                        }

                        byte[] scalarV = (byte[])vertical.Clone();
                        byte[] vectorV = (byte[])vertical.Clone();
                        Vp8Decoder.FilterVerticalScalar(scalarV, stride, 8, 4, 16, simple,
                            macroblockEdge, interior, edge, hev);
                        if (Vp8LoopFilter.IsSupported)
                        {
                            Vector(vectorV, null, stride, 8, 4, 16, horizontal: false, simple,
                                macroblockEdge, interior, edge, hev);
                        }
                        else
                        {
                            vectorV = expectedV;
                        }
                        Assert.Equal(expectedV, scalarV);
                        Assert.Equal(expectedV, vectorV);
                    }
                }

                Assert.True(clampsHit > 0,
                    $"simple={simple} mbEdge={macroblockEdge}: no edge reached the c(a + 4) clamp");
            }
        }
    }

    private static void SkipUnlessVectorKernels()
    {
        if (!Vp8LoopFilter.IsSupported)
        {
            Assert.Skip("the 16-lane loop filter kernels are not accelerated here");
        }
    }

    // The 16-lane kernels: a luma edge of 16 pixels, or (length 8) the U edge in plane and the V
    // edge at the same place in other, filtered together.
    private static void Vector(byte[] plane, byte[]? other, int stride, int x, int y, int length, bool horizontal,
        bool simple, bool macroblockEdge, int interior, int edge, int hev)
    {
        Vp8LoopFilter.Kind kind = simple ? Vp8LoopFilter.Kind.Simple
            : macroblockEdge ? Vp8LoopFilter.Kind.Macroblock : Vp8LoopFilter.Kind.Inner;
        ref byte q0 = ref plane[y * stride + x];
        if (length == 16)
        {
            if (horizontal)
            {
                Vp8LoopFilter.LumaHorizontal(ref q0, stride, kind, edge, interior, hev);
            }
            else
            {
                Vp8LoopFilter.LumaVertical(ref q0, stride, kind, edge, interior, hev);
            }

            return;
        }

        ref byte vQ0 = ref other![y * stride + x];
        if (horizontal)
        {
            Vp8LoopFilter.ChromaHorizontal(ref q0, ref vQ0, stride, kind, edge, interior, hev);
        }
        else
        {
            Vp8LoopFilter.ChromaVertical(ref q0, ref vQ0, stride, kind, edge, interior, hev);
        }
    }

    // RFC 6386 15.2 (common_adjust, simple_segment), 15.3 (subblock_filter, MBfilter), in the pixel
    // domain: c() clamps to int8, and s2u(c(v - 128)) is a clamp to [0, 255]. Returns 1 when the
    // c(a + 4) clamp changed the result, so callers can prove the interesting range was exercised.
    private static int ReferenceFilter(byte[] plane, int q0Index, int step,
        bool simple, bool macroblockEdge, int interior, int edge, int hev)
    {
        static int C(int v) => Math.Clamp(v, -128, 127);
        static byte Pixel(int v) => (byte)Math.Clamp(v, 0, 255);
        int At(int k) => plane[q0Index + k * step];
        int p3 = At(-4), p2 = At(-3), p1 = At(-2), p0 = At(-1);
        int q0 = At(0), q1 = At(1), q2 = At(2), q3 = At(3);

        if (Math.Abs(p0 - q0) * 2 + (Math.Abs(p1 - q1) >> 1) > edge)
        {
            return 0;
        }

        int clampHit = 0;
        void CommonAdjust(bool useOuterTaps, out int f)
        {
            int a = C((useOuterTaps ? C(p1 - q1) : 0) + 3 * (q0 - p0));
            if (a + 4 > 127)
            {
                clampHit = 1;
            }

            int e = C(a + 3) >> 3;
            f = C(a + 4) >> 3;
            plane[q0Index] = Pixel(q0 - f);
            plane[q0Index - step] = Pixel(p0 + e);
        }

        if (simple)
        {
            CommonAdjust(true, out _);
            return clampHit;
        }

        if (Math.Abs(p3 - p2) > interior || Math.Abs(p2 - p1) > interior ||
            Math.Abs(p1 - p0) > interior || Math.Abs(q1 - q0) > interior ||
            Math.Abs(q2 - q1) > interior || Math.Abs(q3 - q2) > interior)
        {
            return 0;
        }

        bool highVariance = Math.Abs(p1 - p0) > hev || Math.Abs(q1 - q0) > hev;
        if (macroblockEdge)
        {
            if (highVariance)
            {
                CommonAdjust(true, out _);
                return clampHit;
            }

            int w = C(C(p1 - q1) + 3 * (q0 - p0));
            int a0 = C((27 * w + 63) >> 7);
            int a1 = C((18 * w + 63) >> 7);
            int a2 = C((9 * w + 63) >> 7);
            plane[q0Index] = Pixel(q0 - a0);
            plane[q0Index - step] = Pixel(p0 + a0);
            plane[q0Index + step] = Pixel(q1 - a1);
            plane[q0Index - 2 * step] = Pixel(p1 + a1);
            plane[q0Index + 2 * step] = Pixel(q2 - a2);
            plane[q0Index - 3 * step] = Pixel(p2 + a2);
            return 0;
        }

        CommonAdjust(highVariance, out int adjusted);
        if (!highVariance)
        {
            int a = (adjusted + 1) >> 1;
            plane[q0Index + step] = Pixel(q1 - a);
            plane[q0Index - 2 * step] = Pixel(p1 + a);
        }

        return clampHit;
    }

    // offset < 0 is the p side (-1 = p0), offset >= 0 the q side (0 = q0). The step q0 - p0 sits
    // around 42 so 3 * (q0 - p0) lands near 124-135; with outerTaps, p1/q1 lean across the edge so
    // c(p1 - q1) adds to it instead of cancelling.
    private static byte StrongStep(int offset, bool outerTaps, Random random)
    {
        int value = offset < 0 ? 100 + random.Next(3) : 139 + random.Next(7);
        if (outerTaps && offset == -2)
        {
            value += 24 + random.Next(16);
        }
        else if (outerTaps && offset == 1)
        {
            value -= 24 + random.Next(16);
        }

        return (byte)value;
    }

    private static void FillVerticalPlane(byte[] plane, int stride, int edgeColumn, int pattern, Random random)
    {
        for (int row = 0; row < plane.Length / stride; row++)
        {
            for (int column = 0; column < stride; column++)
            {
                plane[row * stride + column] = pattern switch
                {
                    0 => (byte)random.Next(256),
                    1 => column < edgeColumn ? (byte)(72 + random.Next(9)) : (byte)(80 + random.Next(9)),
                    2 => column < edgeColumn ? (byte)0 : (byte)255,
                    3 => (byte)Math.Clamp(16 + column * 4 + random.Next(5), 0, 255),
                    _ => StrongStep(column - edgeColumn, pattern == 5, random)
                };
            }
        }
    }

    private static void FillPlane(byte[] plane, int stride, int edgeRow, int pattern, Random random)
    {
        for (int row = 0; row < plane.Length / stride; row++)
        {
            for (int column = 0; column < stride; column++)
            {
                plane[row * stride + column] = pattern switch
                {
                    0 => (byte)random.Next(256),
                    1 => row < edgeRow ? (byte)(72 + random.Next(9)) : (byte)(80 + random.Next(9)),
                    2 => row < edgeRow ? (byte)0 : (byte)255,
                    3 => (byte)Math.Clamp(16 + row * 8 + random.Next(5), 0, 255),
                    _ => StrongStep(row - edgeRow, pattern == 5, random)
                };
            }
        }
    }
}
