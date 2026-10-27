using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegIdct.Idct8x8(byte[], int, int, ReadOnlySpan{short})"/> dispatches to a <c>Vector256</c>, <c>Vector128</c> or
/// scalar kernel depending on the machine, so the golden digests
/// (<c>jpeg-oracle-golden.tsv</c>) only ever exercise <em>one</em> of them. These tests
/// drive all three directly and demand byte-for-byte equality: the IDCT's arithmetic
/// is a fixed IEEE single-precision operation sequence (see <see cref="JpegIdct"/>), so
/// there is no "close enough" here. A separate test pins accuracy against a
/// double-precision IDCT evaluated straight from the T.81 A.3.3 definition.
/// </summary>
public sealed class JpegIdctEquivalenceTests
{
    private const int Stride = 8;

    private static void AllAgree(short[] block, string because)
    {
        block.Length.ShouldBe(64);

        // Pre-rounding floats first: a reordered sum changes these bits long before it
        // happens to move a sample across a rounding boundary.
        var fs = new float[64];
        var f128 = new float[64];
        var f256 = new float[64];
        JpegIdct.TransformScalar(block, fs);
        JpegIdct.TransformV128(block, f128);
        JpegIdct.TransformV256(block, f256);
        for (var i = 0; i < 64; i++)
        {
            BitConverter.SingleToInt32Bits(f128[i]).ShouldBe(BitConverter.SingleToInt32Bits(fs[i]),
                $"{because}: Vector128 transform differs at {i}: {f128[i]:R} vs scalar {fs[i]:R}");
            BitConverter.SingleToInt32Bits(f256[i]).ShouldBe(BitConverter.SingleToInt32Bits(fs[i]),
                $"{because}: Vector256 transform differs at {i}: {f256[i]:R} vs scalar {fs[i]:R}");
        }

        var scalar = new byte[64];
        var v128 = new byte[64];
        var v256 = new byte[64];

        JpegIdct.Idct8x8Scalar(scalar, 0, Stride, block);
        JpegIdct.Idct8x8V128(v128, 0, Stride, block);
        JpegIdct.Idct8x8V256(v256, 0, Stride, block);

        for (var i = 0; i < 64; i++)
        {
            v128[i].ShouldBe(scalar[i], $"{because}: Vector128 mismatch at sample {i} (row {i / 8}, col {i % 8})");
            v256[i].ShouldBe(scalar[i], $"{because}: Vector256 mismatch at sample {i} (row {i / 8}, col {i % 8})");
        }
    }

    [Fact]
    public void DcOnly()
    {
        foreach (var dc in new short[] { 0, 1, -1, 4, -4, 255, -255, 1024, -1024, short.MaxValue, short.MinValue })
        {
            var block = new short[64];
            block[0] = dc;
            AllAgree(block, $"DC-only block, dc={dc}");
        }
    }

    [Fact]
    public void SingleCoefficient_AtEveryPosition()
    {
        for (var pos = 0; pos < 64; pos++)
        {
            foreach (var v in new short[] { 1, -1, 512, -512 })
            {
                var block = new short[64];
                block[0] = 64;
                block[pos] = v;
                AllAgree(block, $"single coefficient {v} at position {pos}");
            }
        }
    }

    [Fact]
    public void ClampBoundaries_AtBothEnds()
    {
        foreach (var scale in new short[] { 4000, -4000, 16000, -16000 })
        {
            var block = new short[64];
            block[0] = scale;
            block[1] = (short)(-scale / 2);
            block[8] = (short)(scale / 3);
            block[9] = (short)(-scale / 5);
            AllAgree(block, $"clamp boundary sweep, scale={scale}");
        }
    }

    [Fact]
    public void ExtremeCoefficients()
    {
        var rnd = new Random(4242);
        for (var iter = 0; iter < 200; iter++)
        {
            var block = new short[64];
            for (var i = 0; i < 64; i++)
                block[i] = (short)(rnd.Next(2) == 0 ? short.MinValue + rnd.Next(64) : short.MaxValue - rnd.Next(64));
            AllAgree(block, $"extreme-coefficient block, iteration {iter}");
        }
    }

    [Fact]
    public void RandomBlocks_AcrossRealisticSparsity()
    {
        var rnd = new Random(1337);
        foreach (var lastNonZero in new[] { 1, 3, 6, 12, 20, 32, 45, 63 })
        {
            for (var iter = 0; iter < 200; iter++)
                AllAgree(RandomBlock(rnd, lastNonZero, 400), $"random block, lastNonZero={lastNonZero}, iteration={iter}");
        }
    }

    [Fact]
    public void WritesRespectOffsetAndStride()
    {
        var rnd = new Random(5150);
        var block = new short[64];
        for (var i = 0; i < 16; i++)
            block[i] = (short)rnd.Next(-500, 500);

        const int stride = 37;
        const int offset = 11;
        var outputs = new List<byte[]>();
        foreach (var kernel in new Action<byte[], int, int, ReadOnlySpan<short>>[]
                 { JpegIdct.Idct8x8Scalar, JpegIdct.Idct8x8V128, JpegIdct.Idct8x8V256 })
        {
            var buf = new byte[stride * 8 + offset];
            Array.Fill(buf, (byte)0xAB);
            kernel(buf, offset, stride, block);
            for (var i = 0; i < buf.Length; i++)
            {
                var inside = i >= offset && (i - offset) % stride < 8 && (i - offset) / stride < 8;
                if (!inside)
                    buf[i].ShouldBe((byte)0xAB, $"byte {i} is outside the 8x8 window and must be untouched");
            }

            outputs.Add(buf);
        }

        outputs[1].ShouldBe(outputs[0]);
        outputs[2].ShouldBe(outputs[0]);
    }

    /// <summary>
    /// Accuracy against <c>round(s + 128)</c>, clamped, where s is the T.81 A.3.3 IDCT
    /// evaluated directly in double precision. Single-precision rounding can only flip a
    /// result whose exact value lies within ~1e-4 of a .5 boundary, so on 20 000
    /// realistic blocks the kernel must be exact on all but a handful of samples and
    /// never off by more than one.
    /// </summary>
    [Fact]
    public void MatchesDoublePrecisionReference()
    {
        var rnd = new Random(1180);
        var output = new byte[64];
        long samples = 0, off = 0;
        for (var b = 0; b < 20_000; b++)
        {
            var block = RandomBlock(rnd, rnd.Next(1, 64), rnd.Next(1, 1024));
            JpegIdct.Idct8x8(output, 0, Stride, block);
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var reference = (int)Math.Clamp(Math.Round(ReferenceIdct(block, x, y) + 128, MidpointRounding.AwayFromZero), 0, 255);
                    var err = Math.Abs(output[y * 8 + x] - reference);
                    err.ShouldBeLessThanOrEqualTo(1);
                    samples++;
                    off += err;
                }
            }
        }

        ((double)off / samples).ShouldBeLessThan(1e-4, $"{off} of {samples} samples differ from the double-precision reference");
    }

    private static readonly int[] Zigzag =
    [
         0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // ------------------------------------------------------------ sparse fast paths

    /// <summary>
    /// The DC-only path replaces both passes by (DC·K4)·K4. Exhaustive over every
    /// 16-bit DC value: float bits against all 64 outputs of the full scalar
    /// transform, and bytes against the full scalar kernel.
    /// </summary>
    [Fact]
    public void DcOnlyFastPath_Exhaustive()
    {
        var block = new short[64];
        var fs = new float[64];
        var full = new byte[64];
        var fast = new byte[64];
        for (int dc = short.MinValue; dc <= short.MaxValue; dc++)
        {
            block[0] = (short)dc;
            JpegIdct.TransformScalar(block, fs);
            var v = BitConverter.SingleToInt32Bits(JpegIdct.DcValue((short)dc));
            for (var i = 0; i < 64; i++)
            {
                if (BitConverter.SingleToInt32Bits(fs[i]) != v)
                    BitConverter.SingleToInt32Bits(fs[i]).ShouldBe(v, $"dc {dc} sample {i}");
            }

            JpegIdct.Idct8x8Scalar(full, 0, Stride, block);
            JpegIdct.DcOnly(fast, 0, Stride, (short)dc);
            if (!fast.AsSpan().SequenceEqual(full))
                fast.ShouldBe(full, $"dc {dc}");
        }
    }

    /// <summary>
    /// For every last-coefficient index the dispatcher may drop the zero half of either
    /// pass; that must be bit-exact (float bits) and pick halves that really are zero.
    /// </summary>
    [Fact]
    public void SparsePaths_MatchFullTransform_ForEveryLastIndex()
    {
        var rnd = new Random(42);
        var fs = new float[64];
        var f256 = new float[64];
        var scalar = new byte[64];
        var dispatched = new byte[64];
        for (var last = 0; last < 64; last++)
        {
            var (halfRows, halfCols) = JpegIdct.SparseFlags(last);
            halfRows.ShouldBe(Zigzag.Take(last + 1).All(i => i / 8 < 4), $"last {last}");
            halfCols.ShouldBe(Zigzag.Take(last + 1).All(i => i % 8 < 4), $"last {last}");

            for (var trial = 0; trial < 300; trial++)
            {
                var block = RandomBlock(rnd, last, trial % 3 == 0 ? 1024 : 64);
                if (last > 0)
                    block[Zigzag[last]] = (short)(rnd.Next(1, 300) * (rnd.Next(2) == 0 ? 1 : -1));

                JpegIdct.TransformScalar(block, fs);
                JpegIdct.TransformV256(block, f256, halfRows, halfCols);
                for (var i = 0; i < 64; i++)
                    BitConverter.SingleToInt32Bits(f256[i]).ShouldBe(BitConverter.SingleToInt32Bits(fs[i]), $"last {last} trial {trial} sample {i}");

                JpegIdct.Idct8x8Scalar(scalar, 0, Stride, block);
                JpegIdct.Idct8x8(dispatched, 0, Stride, block, last);
                dispatched.ShouldBe(scalar, $"last {last} trial {trial}");
            }
        }
    }

    private static short[] RandomBlock(Random rnd, int lastNonZero, int acRange)
    {
        var block = new short[64];
        block[0] = (short)rnd.Next(-2048, 2048);
        for (var k = 1; k <= lastNonZero; k++)
        {
            if (rnd.Next(100) < 70)
                block[Zigzag[k]] = (short)rnd.Next(-acRange, acRange);
        }

        return block;
    }

    // Basis[x * 8 + u] = C(u) · cos((2x+1)uπ/16)
    private static readonly double[] Basis = Enumerable.Range(0, 64)
        .Select(i => (i % 8 == 0 ? 1 / Math.Sqrt(2) : 1.0) * Math.Cos((2 * (i / 8) + 1) * (i % 8) * Math.PI / 16))
        .ToArray();

    private static double ReferenceIdct(short[] block, int x, int y)
    {
        var sum = 0.0;
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
                sum += Basis[x * 8 + u] * Basis[y * 8 + v] * block[v * 8 + u];
        }

        return sum / 4;
    }
}
