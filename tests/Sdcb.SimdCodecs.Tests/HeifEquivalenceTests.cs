using System.Runtime.Intrinsics;
using Sdcb.SimdCodecs.Heif.Hevc;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Cross-ISA equivalence for the HEVC / HEIF kernels: every Vector256 / Vector128 kernel must reproduce its
/// scalar twin bit for bit, and the scalar twin must reproduce the direct definition in H.265. The inputs are
/// random (no fixture needed), with the edge cases the kernels special-case: sparse blocks, full blocks,
/// extreme coefficients and samples at the ends of the range. CI runs this class with the Vector256 and the
/// Vector128 tier disabled too, so on each machine every available tier is checked.
/// </summary>
public sealed class HeifEquivalenceTests
{
    private static bool HasV256 => Vector256.IsHardwareAccelerated;
    private static bool HasV128 => Vector128.IsHardwareAccelerated;

    /// <summary>The inverse transform of (8-314 .. 8-316) and (8-299) + (8-325), straight from the formulas.</summary>
    private static void ReferenceInverseAdd(int[] d, int log2Size, bool dst4, int bitDepth, ushort[] dst, int stride, int offset)
    {
        var n = 1 << log2Size;
        sbyte[] dstMatrix = [29, 55, 74, 84, 74, 74, 0, -74, 84, -29, -74, 55, 55, -84, 74, -29];
        int M(int j, int k) => dst4 ? dstMatrix[j * 4 + k] : HevcTransform.Dct[j * (32 >> log2Size) * 32 + k];
        var g = new int[n * n];
        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++)
            {
                var sum = 0;
                for (var j = 0; j < n; j++) sum += M(j, y) * d[j * n + x];
                g[y * n + x] = Math.Clamp((sum + 64) >> 7, short.MinValue, short.MaxValue);
            }
        }
        var bdShift = 20 - bitDepth;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                var sum = 0;
                for (var j = 0; j < n; j++) sum += M(j, x) * g[y * n + j];
                var at = offset + y * stride + x;
                dst[at] = (ushort)Math.Clamp(dst[at] + ((sum + (1 << (bdShift - 1))) >> bdShift), 0, (1 << bitDepth) - 1);
            }
        }
    }

    private delegate void InverseKernel(int[] coeffs, int[] scratch, int log2Size, bool dst4, int maxX, int maxY, int bitDepth, ushort[] dst, int offset, int stride);

    private static void RunInverse(InverseKernel kernel, bool v256Only)
    {
        var rng = new Random(20261104);
        for (var iteration = 0; iteration < 3000; iteration++)
        {
            var log2Size = v256Only ? rng.Next(3, 6) : rng.Next(2, 6);
            var n = 1 << log2Size;
            var dst4 = log2Size == 2 && rng.Next(2) == 0;
            var bitDepth = rng.Next(2) == 0 ? 8 : 10;
            var maxX = rng.Next(4) == 0 ? 0 : rng.Next(n);
            var maxY = rng.Next(4) == 0 ? 0 : rng.Next(n);
            var mode = rng.Next(4);
            var coeffs = new int[n * n];
            for (var y = 0; y <= maxY; y++)
            {
                for (var x = 0; x <= maxX; x++)
                {
                    if (mode == 0 && rng.Next(3) != 0) continue; // sparse
                    coeffs[y * n + x] = mode switch
                    {
                        1 => rng.Next(2) == 0 ? short.MinValue : short.MaxValue,
                        2 => rng.Next(-64, 65),
                        _ => rng.Next(short.MinValue, short.MaxValue + 1),
                    };
                }
            }
            coeffs[maxY * n + maxX] = rng.Next(1, 300) * (rng.Next(2) == 0 ? -1 : 1); // the bounding box is tight

            var stride = n + rng.Next(0, 9);
            var offset = rng.Next(0, 5);
            var max = (1 << bitDepth) - 1;
            var expected = new ushort[offset + n * stride + 8];
            for (var i = 0; i < expected.Length; i++) expected[i] = (ushort)(rng.Next(3) == 0 ? (rng.Next(2) == 0 ? 0 : max) : rng.Next(max + 1));
            var actual = (ushort[])expected.Clone();

            ReferenceInverseAdd(coeffs, log2Size, dst4, bitDepth, expected, stride, offset);
            var input = (int[])coeffs.Clone();
            var scratch = new int[n * n];
            Array.Fill(scratch, unchecked((int)0xA5A5A5A5)); // the kernels must not depend on the scratch contents
            kernel(input, scratch, log2Size, dst4, maxX, maxY, bitDepth, actual, offset, stride);
            actual.ShouldBe(expected, $"iteration {iteration}: N={n} dst4={dst4} bitDepth={bitDepth} maxX={maxX} maxY={maxY} mode={mode}");
            input.ShouldBe(coeffs, "the coefficients are read only");
        }
    }

    [Fact]
    public void InverseTransform_scalar_matches_the_definition() =>
        RunInverse((c, s, l, d4, mx, my, b, dst, o, st) => HevcTransform.InverseAddScalar(c, s, l, d4, mx, my, b, dst.AsSpan(o), st), v256Only: false);

    [Fact]
    public void InverseTransform_Vector128_matches_the_definition()
    {
        if (!HasV128) return;
        RunInverse((c, s, l, d4, mx, my, b, dst, o, st) => HevcTransform.InverseAddVector128(c, s, l, d4, mx, my, b, dst.AsSpan(o), st), v256Only: false);
    }

    [Fact]
    public void InverseTransform_Vector256_matches_the_definition()
    {
        if (!HasV256) return;
        RunInverse((c, s, l, d4, mx, my, b, dst, o, st) => HevcTransform.InverseAddVector256(c, s, l, mx, my, b, dst.AsSpan(o), st), v256Only: true);
    }

    [Fact]
    public void InverseTransform_dispatch_matches_the_definition() =>
        RunInverse((c, s, l, d4, mx, my, b, dst, o, st) => HevcTransform.InverseAdd(c, s, l, d4, mx, my, b, dst.AsSpan(o), st), v256Only: false);

    /// <summary>SAO edge offset of (8-411 .. 8-413), straight from the formulas, sample by sample.</summary>
    private static ushort ReferenceEdge(int v, int a, int b, int[] offsets, int max)
    {
        var edgeIdx = 2 + Math.Sign(v - a) + Math.Sign(v - b);
        if (edgeIdx is 0 or 1 or 2) edgeIdx = edgeIdx == 2 ? 0 : edgeIdx + 1;
        return edgeIdx == 0 ? (ushort)v : (ushort)Math.Clamp(v + offsets[edgeIdx - 1], 0, max);
    }

    private static ushort ReferenceBand(int v, int band, int bitDepth, int[] offsets)
    {
        var bandTable = new int[32];
        for (var k = 0; k < 4; k++) bandTable[(k + band) & 31] = k + 1;
        var bandIdx = bandTable[v >> (bitDepth - 5)];
        return bandIdx == 0 ? (ushort)v : (ushort)Math.Clamp(v + offsets[bandIdx - 1], 0, (1 << bitDepth) - 1);
    }

    private delegate int EdgeKernel(ushort[] cur, ushort[] a, ushort[] b, ushort[] dst, int count, int[] offsets, int max);
    private delegate int BandKernel(ushort[] cur, ushort[] dst, int count, int band, int bitDepth, int[] offsets);

    private static ushort[] Samples(Random rng, int count, int bitDepth, int mode)
    {
        var max = (1 << bitDepth) - 1;
        var result = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = mode switch
            {
                0 => (ushort)rng.Next(max + 1),
                1 => (ushort)(rng.Next(2) == 0 ? rng.Next(3) : max - rng.Next(3)), // the clamps
                _ => (ushort)((max >> 1) + rng.Next(3)), // flat, so all five edge categories occur
            };
        }
        return result;
    }

    private static int[] SaoOffsets(Random rng, int bitDepth)
    {
        var cMax = (1 << (Math.Min(bitDepth, 10) - 5)) - 1;
        return [rng.Next(-cMax, cMax + 1), rng.Next(-cMax, cMax + 1), rng.Next(-cMax, cMax + 1), rng.Next(-cMax, cMax + 1)];
    }

    private static void RunEdge(EdgeKernel kernel, int granule)
    {
        var rng = new Random(411);
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var count = rng.Next(0, 80);
            var bitDepth = rng.Next(2) == 0 ? 8 : 10;
            var mode = rng.Next(3);
            var cur = Samples(rng, count, bitDepth, mode);
            var a = Samples(rng, count, bitDepth, mode);
            var b = Samples(rng, count, bitDepth, mode);
            var offsets = SaoOffsets(rng, bitDepth);
            var max = (1 << bitDepth) - 1;
            var expected = new ushort[count + 4];
            var actual = new ushort[count + 4];
            Array.Fill(actual, (ushort)0xBEEF);
            Array.Fill(expected, (ushort)0xBEEF);
            for (var i = 0; i < count; i++) expected[i] = ReferenceEdge(cur[i], a[i], b[i], offsets, max);
            var done = kernel(cur, a, b, actual, count, offsets, max);
            (done % granule).ShouldBe(0);
            HevcSao.EdgeRowScalar(cur, a, b, actual, done, count, offsets, max);
            actual.ShouldBe(expected, $"iteration {iteration}: count={count} done={done}");
        }
    }

    private static void RunBand(BandKernel kernel, int granule)
    {
        var rng = new Random(413);
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            var count = rng.Next(0, 80);
            var bitDepth = rng.Next(2) == 0 ? 8 : 10;
            var cur = Samples(rng, count, bitDepth, rng.Next(2));
            var offsets = SaoOffsets(rng, bitDepth);
            var band = rng.Next(32);
            var expected = new ushort[count + 4];
            var actual = new ushort[count + 4];
            Array.Fill(actual, (ushort)0xBEEF);
            Array.Fill(expected, (ushort)0xBEEF);
            for (var i = 0; i < count; i++) expected[i] = ReferenceBand(cur[i], band, bitDepth, offsets);
            var done = kernel(cur, actual, count, band, bitDepth, offsets);
            (done % granule).ShouldBe(0);
            HevcSao.BandRowScalar(cur, actual, done, count, band, bitDepth, offsets);
            actual.ShouldBe(expected, $"iteration {iteration}: count={count} band={band}");
        }
    }

    [Fact]
    public void SaoEdge_scalar_matches_the_definition() => RunEdge((c, a, b, d, n, o, m) => 0, 1);

    [Fact]
    public void SaoEdge_Vector128_matches_the_definition()
    {
        if (!HasV128) return;
        RunEdge((c, a, b, d, n, o, m) => n < 8 ? 0 : HevcSao.EdgeRowVector128(c, a, b, d, n, o, m), 8);
    }

    [Fact]
    public void SaoEdge_Vector256_matches_the_definition()
    {
        if (!HasV256) return;
        RunEdge((c, a, b, d, n, o, m) => n < 16 ? 0 : HevcSao.EdgeRowVector256(c, a, b, d, n, o, m), 16);
    }

    [Fact]
    public void SaoEdge_dispatch_matches_the_definition() =>
        RunEdge((c, a, b, d, n, o, m) => { HevcSao.EdgeRow(c, a, b, d, n, o, m); return n; }, 1);

    [Fact]
    public void SaoBand_scalar_matches_the_definition() => RunBand((c, d, n, band, bd, o) => 0, 1);

    [Fact]
    public void SaoBand_Vector128_matches_the_definition()
    {
        if (!HasV128) return;
        RunBand((c, d, n, band, bd, o) => n < 8 ? 0 : HevcSao.BandRowVector128(c, d, n, band, bd, o), 8);
    }

    [Fact]
    public void SaoBand_Vector256_matches_the_definition()
    {
        if (!HasV256) return;
        RunBand((c, d, n, band, bd, o) => n < 16 ? 0 : HevcSao.BandRowVector256(c, d, n, band, bd, o), 16);
    }

    [Fact]
    public void SaoBand_dispatch_matches_the_definition() =>
        RunBand((c, d, n, band, bd, o) => { HevcSao.BandRow(c, d, n, band, bd, o); return n; }, 1);
}
