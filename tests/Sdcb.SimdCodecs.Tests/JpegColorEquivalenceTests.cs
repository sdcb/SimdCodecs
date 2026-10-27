using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Cross-ISA equivalence for the colour and upsampling row kernels: every
/// <see cref="Vector256"/> / <see cref="Vector128"/> kernel must reproduce the scalar
/// definition bit for bit. Bytes alone are a weak check (a reordered sum rarely moves
/// a rounded byte), so the YCbCr cores are also compared on their pre-rounding float
/// bits. Rows have odd lengths so every vector body is followed by a scalar tail, and
/// guard bytes catch writes past the row.
/// </summary>
public sealed class JpegColorEquivalenceTests
{
    private static bool HasV256 => Vector256.IsHardwareAccelerated && Avx2.IsSupported;
    private static bool HasV128 => Vector128.IsHardwareAccelerated;

    private static readonly int[] Lengths = [0, 1, 3, 7, 8, 9, 15, 16, 17, 31, 33, 64, 65, 127, 250];

    private static byte[] Bytes(Random rng, int n, int mode)
    {
        var a = new byte[n + 16];
        for (var i = 0; i < a.Length; ++i)
            a[i] = mode switch { 0 => (byte)rng.Next(256), 1 => rng.Next(2) == 0 ? (byte)0 : (byte)255, _ => (byte)(rng.Next(2) == 0 ? rng.Next(4) : 252 + rng.Next(4)) };
        return a;
    }

    private static ushort[] Chroma16(Random rng, int n, int mode)
    {
        var a = new ushort[n + 16];
        for (var i = 0; i < a.Length; ++i)
            a[i] = mode switch { 0 => (ushort)rng.Next(4081), 1 => rng.Next(2) == 0 ? (ushort)0 : (ushort)4080, _ => (ushort)(rng.Next(2) == 0 ? rng.Next(32) : 4049 + rng.Next(32)) };
        return a;
    }

    private delegate int VectorKernel(Span<byte> dst);

    /// <summary>Runs a tier's vector body + the scalar tail and compares with the scalar kernel over the whole row.</summary>
    private static void AssertTier(string name, int n, VectorKernel vector, Action<Span<byte>, int> scalarFrom)
    {
        var expected = new byte[n * 4 + 32];
        var actual = new byte[n * 4 + 32];
        Array.Fill(expected, (byte)0xA5);
        Array.Fill(actual, (byte)0xA5);
        scalarFrom(expected, 0);
        var done = vector(actual);
        (done % 8).ShouldBe(0, name);
        done.ShouldBeLessThanOrEqualTo(n, name);
        scalarFrom(actual, done);
        actual.ShouldBe(expected, $"{name} n={n}");
    }

    public static TheoryData<int> Tiers()
    {
        var d = new TheoryData<int>();
        if (HasV256)
            d.Add(256);
        if (HasV128)
            d.Add(128);
        if (d.Count == 0)
            d.Add(1);
        return d;
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void YccFloatBits_MatchScalar(int tier)
    {
        var rng = new Random(1);
        for (var mode = 0; mode < 3; ++mode)
        {
            foreach (var n in new[] { 1, 7, 8, 9, 64, 1001 })
            {
                var y = Bytes(rng, n, mode).AsSpan(0, n);
                var cb = Chroma16(rng, n, mode).AsSpan(0, n);
                var cr = Chroma16(rng, n, mode).AsSpan(0, n);
                var expected = new float[n * 3];
                var actual = new float[n * 3];
                JpegColor.YccFloats(1, y, cb, cr, expected);
                JpegColor.YccFloats(tier, y, cb, cr, actual);
                for (var i = 0; i < expected.Length; ++i)
                    BitConverter.SingleToInt32Bits(actual[i]).ShouldBe(BitConverter.SingleToInt32Bits(expected[i]), $"tier {tier} mode {mode} n {n} index {i}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void RowKernels_MatchScalar(int tier)
    {
        var rng = new Random(tier);
        foreach (var bgr in new[] { false, true })
        {
            for (var mode = 0; mode < 3; ++mode)
            {
                foreach (var n in Lengths)
                {
                    var y = Bytes(rng, n, mode);
                    var b1 = Bytes(rng, n, mode);
                    var b2 = Bytes(rng, n, mode);
                    var k = Bytes(rng, n, mode);
                    var c16a = Chroma16(rng, n, mode);
                    var c16b = Chroma16(rng, n, mode);

                    AssertTier($"ycc16 {bgr}", n,
                        d => tier == 256 ? JpegColor.YccRow16V256(d, y, c16a, c16b, n, bgr) : tier == 128 ? JpegColor.YccRow16V128(d, y, c16a, c16b, n, bgr) : 0,
                        (d, from) => JpegColor.YccRow16Scalar(d, y, c16a, c16b, from, n, bgr));
                    AssertTier($"ycc8 {bgr}", n,
                        d => tier == 256 ? JpegColor.YccRow8V256(d, y, b1, b2, n, bgr) : tier == 128 ? JpegColor.YccRow8V128(d, y, b1, b2, n, bgr) : 0,
                        (d, from) => JpegColor.YccRow8Scalar(d, y, b1, b2, from, n, bgr));
                    AssertTier($"ycck16 {bgr}", n,
                        d => tier == 256 ? JpegColor.YcckRow16V256(d, y, c16a, c16b, k, n, bgr) : tier == 128 ? JpegColor.YcckRow16V128(d, y, c16a, c16b, k, n, bgr) : 0,
                        (d, from) => JpegColor.YcckRow16Scalar(d, y, c16a, c16b, k, from, n, bgr));
                    AssertTier($"ycck8 {bgr}", n,
                        d => tier == 256 ? JpegColor.YcckRow8V256(d, y, b1, b2, k, n, bgr) : tier == 128 ? JpegColor.YcckRow8V128(d, y, b1, b2, k, n, bgr) : 0,
                        (d, from) => JpegColor.YcckRow8Scalar(d, y, b1, b2, k, from, n, bgr));
                    AssertTier($"cmyk {bgr}", n,
                        d => tier == 256 ? JpegColor.CmykRowV256(d, y, b1, b2, k, n, bgr) : tier == 128 ? JpegColor.CmykRowV128(d, y, b1, b2, k, n, bgr) : 0,
                        (d, from) => JpegColor.CmykRowScalar(d, y, b1, b2, k, from, n, bgr));
                    AssertTier($"rgb {bgr}", n,
                        d => tier == 256 ? JpegColor.RgbRowV256(d, y, b1, b2, n, bgr) : tier == 128 ? JpegColor.RgbRowV128(d, y, b1, b2, n, bgr) : 0,
                        (d, from) => JpegColor.RgbRowScalar(d, y, b1, b2, from, n, bgr));
                    AssertTier("gray", n,
                        d => tier == 256 ? JpegColor.GrayRowV256(d, y, n) : tier == 128 ? JpegColor.GrayRowV128(d, y, n) : 0,
                        (d, from) => JpegColor.GrayRowScalar(d, y, from, n));
                }
            }
        }
    }

    [Fact]
    public void ScalarKernels_FollowTheDefinition()
    {
        // Independent restatement of the T.871 / Blinn definitions in double-free
        // float arithmetic, so the scalar reference itself is pinned.
        const float kr = (float)(1.402 / 16), kgb = (float)(-(0.114 * 1.772 / 0.587) / 16), kgr = (float)(-(0.299 * 1.402 / 0.587) / 16), kb = (float)(1.772 / 16);
        static byte B(float v) => (byte)(int)MathF.Min(MathF.Max(v + 0.5f, 0f), 255f);
        static int Blinn(int x, int k) { var t = x * k + 128; return (t + (t >> 8)) >> 8; }

        var rng = new Random(7);
        const int n = 300;
        var y = Bytes(rng, n, 0);
        var c16a = Chroma16(rng, n, 0);
        var c16b = Chroma16(rng, n, 0);
        var k = Bytes(rng, n, 0);
        var ycc = new byte[n * 4];
        var ycck = new byte[n * 4];
        JpegColor.YccRow16Scalar(ycc, y, c16a, c16b, 0, n, false);
        JpegColor.YcckRow16Scalar(ycck, y, c16a, c16b, k, 0, n, true);
        for (var i = 0; i < n; ++i)
        {
            float yf = y[i], cb = c16a[i] - 2048, cr = c16b[i] - 2048;
            var r = B(yf + cr * kr);
            var g = B(yf + cb * kgb + cr * kgr);
            var b = B(yf + cb * kb);
            ycc.AsSpan(i * 4, 4).ToArray().ShouldBe(new[] { r, g, b, (byte)255 });
            ycck.AsSpan(i * 4, 4).ToArray().ShouldBe(new[] { (byte)Blinn(255 - b, k[i]), (byte)Blinn(255 - g, k[i]), (byte)Blinn(255 - r, k[i]), (byte)255 });
        }
    }

    [Fact]
    public void PackRgb24_DropsFourthByte()
    {
        var rng = new Random(3);
        foreach (var n in Lengths)
        {
            var src = Bytes(rng, n * 4, 0);
            var dst = new byte[n * 3 + 8];
            Array.Fill(dst, (byte)0xA5);
            JpegColor.PackRgb24(dst.AsSpan(0, n * 3), src, n);
            for (var i = 0; i < n; ++i)
                dst.AsSpan(i * 3, 3).ToArray().ShouldBe(src.AsSpan(i * 4, 3).ToArray(), $"n={n} px {i}");
            dst.AsSpan(n * 3).ToArray().ShouldAllBe(b => b == 0xA5, $"n={n} guard");
        }
    }

    public static TheoryData<int, int> ResampleCases()
    {
        var d = new TheoryData<int, int>();
        foreach (var kind in new[] { JpegResample.OneToOne, JpegResample.V2, JpegResample.H2, JpegResample.Hv2, JpegResample.Generic })
            foreach (var tier in new[] { 256, 128 })
                d.Add(kind, tier);
        return d;
    }

    [Theory]
    [MemberData(nameof(ResampleCases))]
    public void Resample_MatchesScalar(int kind, int tier)
    {
        if ((tier == 256 && !HasV256) || (tier == 128 && !HasV128))
            Assert.Skip($"tier {tier} not accelerated here");

        var rng = new Random(kind * 1000 + tier);
        for (var w = 1; w <= 90; ++w)
        {
            for (var mode = 0; mode < 3; ++mode)
            {
                var plane = Bytes(rng, 3 * w + 40, mode);
                var near = rng.Next(w + 1);
                var far = near + w + rng.Next(5);
                var hs = kind == JpegResample.Generic ? 3 : 2;
                var count = JpegResample.OutputCount(kind, w, hs);

                var expected16 = new ushort[count + 8];
                var actual16 = new ushort[count + 8];
                var expected8 = new byte[count + 8];
                var actual8 = new byte[count + 8];
                Array.Fill(actual16, (ushort)0xA5A5);
                Array.Fill(expected16, (ushort)0xA5A5);
                try
                {
                    JpegResample.ForceTier = 1;
                    JpegResample.Row16(kind, expected16, plane, near, far, w, hs);
                    JpegResample.Row(kind, expected8, new ushort[count + 8], plane, near, far, w, hs);
                    JpegResample.ForceTier = tier;
                    JpegResample.Row16(kind, actual16, plane, near, far, w, hs);
                    JpegResample.Row(kind, actual8, new ushort[count + 8], plane, near, far, w, hs);
                }
                finally
                {
                    JpegResample.ForceTier = 0;
                }

                actual16.ShouldBe(expected16, $"kind {kind} w {w}");
                actual8.ShouldBe(expected8, $"kind {kind} w {w}");
            }
        }
    }

    [Fact]
    public void Resample_ScalarFollowsTheDefinition()
    {
        // ¾·near + ¼·far at 16× scale, horizontally at the centred positions, edges replicated.
        var rng = new Random(11);
        for (var w = 1; w <= 40; ++w)
        {
            var plane = Bytes(rng, 2 * w, 0);
            int Up(int i) => 3 * plane[i] + plane[w + i]; // vertical 4× scale
            var dst = new ushort[2 * w];
            try
            {
                JpegResample.ForceTier = 1;
                JpegResample.Row16(JpegResample.Hv2, dst, plane, 0, w, w, 2);
            }
            finally
            {
                JpegResample.ForceTier = 0;
            }

            for (var x = 0; x < 2 * w; ++x)
            {
                var i = x / 2;
                var j = x % 2 == 0 ? Math.Max(i - 1, 0) : Math.Min(i + 1, w - 1);
                dst[x].ShouldBe((ushort)(3 * Up(i) + Up(j)), $"w {w} x {x}");
            }
        }
    }
}
