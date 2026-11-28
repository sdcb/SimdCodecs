using Sdcb.SimdCodecs.Avif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The YCbCr → RGBA row kernels (RGBA8, RGBA16, and deep samples to RGBA8): the vector form gives exactly what the scalar twin gives, and the scalar twin
/// what the per-pixel formula gives, for every bit depth, range, matrix and subsampling, over random samples that
/// include the extremes, and over widths that leave a scalar tail.
/// </summary>
public sealed class AvifColorTests
{
    public static TheoryData<int, bool, int> Cases()
    {
        var data = new TheoryData<int, bool, int>();
        foreach (int depth in new[] { 8, 10, 12 })
        {
            foreach (bool full in new[] { false, true })
            {
                foreach (int matrix in new[] { 1, 2, 4, 6, 7, 9 })
                    data.Add(depth, full, matrix);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RowKernels_VectorEqualsScalarEqualsPerPixel(int depth, bool fullRange, int matrix)
    {
        var rng = new Random(depth * 1000 + matrix * 10 + (fullRange ? 1 : 0));
        int maxSample = (1 << depth) - 1;
        var k8 = AvifColor.For(matrix, fullRange, depth);
        var k16 = AvifColor.For16(matrix, fullRange, depth);
        foreach (int width in new[] { 1, 7, 8, 15, 16, 17, 33, 64, 101 })
        {
            foreach (bool mono in new[] { false, true })
            {
                foreach (int subX in new[] { 0, 1 })
                {
                    int cw = (width + subX) >> subX;
                    var y = Samples(width);
                    var cb = Samples(cw);
                    var cr = Samples(cw);
                    string what = $"{depth}-bit full={fullRange} mc={matrix} w={width} mono={mono} subX={subX}";

                    var expected8 = new byte[width * 4];
                    var expected16 = new ushort[width * 4];
                    for (int x = 0; x < width; x++)
                    {
                        ushort cbs = mono ? (ushort)0 : cb[x >> subX], crs = mono ? (ushort)0 : cr[x >> subX];
                        AvifColor.PixelToRgba(y[x], cbs, crs, mono, in k8, 255, false,
                            out expected8[x * 4], out expected8[x * 4 + 1], out expected8[x * 4 + 2], out expected8[x * 4 + 3]);
                        AvifColor.PixelToRgba16(y[x], cbs, crs, mono, in k16, 65535, false,
                            out expected16[x * 4], out expected16[x * 4 + 1], out expected16[x * 4 + 2], out expected16[x * 4 + 3]);
                    }

                    var scalar8 = new byte[width * 4];
                    AvifColor.RowToRgba8Scalar(y, cb, cr, mono, subX, in k8, scalar8, 0);
                    scalar8.ShouldBe(expected8, what);
                    var vector8 = new byte[width * 4];
                    int done8 = AvifColor.RowToRgba8Vector(y, cb, cr, mono, subX, in k8, vector8);
                    AvifColor.RowToRgba8Scalar(y, cb, cr, mono, subX, in k8, vector8, done8);
                    vector8.ShouldBe(expected8, what);

                    var scalar16 = new ushort[width * 4];
                    AvifColor.RowToRgba16Scalar(y, cb, cr, mono, subX, in k16, scalar16, 0);
                    scalar16.ShouldBe(expected16, what);
                    var vector16 = new ushort[width * 4];
                    int done16 = AvifColor.RowToRgba16Vector(y, cb, cr, mono, subX, in k16, vector16);
                    AvifColor.RowToRgba16Scalar(y, cb, cr, mono, subX, in k16, vector16, done16);
                    vector16.ShouldBe(expected16, what);

                    // Deep samples straight to RGBA8: the high bytes of the RGBA16 result.
                    var expectedWide8 = new byte[width * 4];
                    for (int i = 0; i < expectedWide8.Length; i++)
                        expectedWide8[i] = (byte)(expected16[i] >> 8);
                    var scalarWide8 = new byte[width * 4];
                    AvifColor.RowToRgba8WideScalar(y, cb, cr, mono, subX, in k16, scalarWide8, 0);
                    scalarWide8.ShouldBe(expectedWide8, what);
                    var vectorWide8 = new byte[width * 4];
                    int doneWide8 = AvifColor.RowToRgba8WideVector(y, cb, cr, mono, subX, in k16, vectorWide8);
                    AvifColor.RowToRgba8WideScalar(y, cb, cr, mono, subX, in k16, vectorWide8, doneWide8);
                    vectorWide8.ShouldBe(expectedWide8, what);

                    if (width >= 16)
                    {
                        done8.ShouldBeGreaterThan(0, what); // the vector loop ran
                        done16.ShouldBeGreaterThan(0, what);
                        doneWide8.ShouldBeGreaterThan(0, what);
                    }
                }
            }
        }

        ushort[] Samples(int n)
        {
            var s = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                s[i] = (rng.Next(4)) switch
                {
                    0 => 0,
                    1 => (ushort)maxSample,
                    _ => (ushort)rng.Next(maxSample + 1),
                };
            }
            return s;
        }
    }
}
