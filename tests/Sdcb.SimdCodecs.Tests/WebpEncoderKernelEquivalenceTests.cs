using Sdcb.SimdCodecs.Webp;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The VP8 encoder's vector kernels against their scalar twins, so every tier makes the same choices
/// and writes the same file; plus the all-modes 4×4 predictor against ten single-mode predictions.
/// </summary>
public sealed class WebpEncoderKernelEquivalenceTests
{
    private static void RequireVector() =>
        Assert.SkipUnless(Vp8EncoderKernels.VectorPath, "the vector kernels do not run on this machine");

    private static byte[] Block(Random random, int range, int center)
    {
        var b = new byte[4 * 7];
        for (int i = 0; i < b.Length; i++)
        {
            b[i] = (byte)Math.Clamp(center + random.Next(-range, range + 1), 0, 255);
        }

        return b;
    }

    [Fact]
    public void Residual_dct_vector_matches_scalar()
    {
        RequireVector();
        var random = new Random(3);
        var a = new int[16];
        var b = new int[16];
        for (int trial = 0; trial < 20000; trial++)
        {
            int range = trial % 4 switch { 0 => 255, 1 => 128, 2 => 20, _ => 2 };
            byte[] src = Block(random, range, 128), pred = Block(random, range, 128);
            Vp8EncoderKernels.ResidualDctScalar(ref src[0], 7, ref pred[0], 7, a);
            Vp8EncoderKernels.ResidualDctVector(ref src[0], 7, ref pred[0], 7, b);
            b.ShouldBe(a, $"trial {trial}");
        }
    }

    [Fact]
    public void Satd_vector_matches_scalar()
    {
        RequireVector();
        var random = new Random(4);
        for (int trial = 0; trial < 20000; trial++)
        {
            byte[] src = Block(random, 255, 128), pred = Block(random, trial % 2 == 0 ? 255 : 5, 128);
            Vp8EncoderKernels.Satd4Vector(ref src[0], 7, ref pred[0], 7)
                .ShouldBe(Vp8EncoderKernels.Satd4Scalar(ref src[0], 7, ref pred[0], 7), $"trial {trial}");
        }
    }

    [Fact]
    public void Quantize_vector_matches_scalar()
    {
        RequireVector();
        var random = new Random(5);
        var coefficients = new int[16];
        for (int trial = 0; trial < 20000; trial++)
        {
            int range = trial % 3 switch { 0 => 16000, 1 => 600, _ => 40 };
            for (int i = 0; i < 16; i++) coefficients[i] = random.Next(-range, range + 1);
            int start = trial % 2;
            int dcStep = random.Next(4, 315), acStep = random.Next(4, 285);
            int rDc = random.Next(0, 200), rAc = random.Next(0, 200);
            var la = new short[16];
            var lb = new short[16];
            var da = new short[16];
            var db = new short[16];
            da[0] = db[0] = 1234;
            la[0] = lb[0] = 77;
            int ra = Vp8EncoderKernels.QuantizeScalar(coefficients, la, da, start, dcStep, 65536 / dcStep, rDc, acStep, 65536 / acStep, rAc);
            int rb = Vp8EncoderKernels.QuantizeVector(coefficients, lb, db, start, dcStep, 65536 / dcStep, rDc, acStep, 65536 / acStep, rAc);
            rb.ShouldBe(ra, $"trial {trial} last");
            lb.ShouldBe(la, $"trial {trial} levels");
            db.ShouldBe(da, $"trial {trial} dequantised");
        }
    }

    [Fact]
    public void All_modes_prediction_matches_ten_single_predictions()
    {
        var random = new Random(6);
        const int stride = 16;
        for (int trial = 0; trial < 2000; trial++)
        {
            var frame = new byte[stride * 6];
            random.NextBytes(frame);
            var above = new byte[16];
            random.NextBytes(above);
            ref byte destination = ref frame[stride + 4];
            var all = new byte[160];
            Vp8Decoder.PredictSubblockAll(ref destination, stride, ref above[1], all);
            for (int mode = 0; mode < 10; mode++)
            {
                var copy = (byte[])frame.Clone();
                Vp8Decoder.PredictSubblock(ref copy[stride + 4], stride, mode, ref above[1]);
                for (int r = 0; r < 4; r++)
                {
                    copy.AsSpan(stride + 4 + r * stride, 4).ToArray().ShouldBe(all.AsSpan(mode * 16 + r * 4, 4).ToArray(),
                        $"trial {trial} mode {mode} row {r}");
                }
            }
        }
    }
}
