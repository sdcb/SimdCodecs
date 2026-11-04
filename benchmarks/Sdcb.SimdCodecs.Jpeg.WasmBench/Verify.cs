namespace Sdcb.SimdCodecs.Jpeg.WasmBench;

/// <summary>
/// The kernels this runtime dispatches to against their scalar twins, on this runtime: the
/// desktop test suite cannot reach the PackedSimd branches, so this is the browser-wasm tier
/// of the equivalence tests. Any mismatch fails the run before anything is timed.
/// </summary>
internal static class Verify
{
    private static readonly int[] Zigzag =
    [
         0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    public static bool Run()
    {
        long checks = 0, bad = 0;
        var rng = new Random(20261104);

        // IDCT: every last-coefficient index, realistic and full-range magnitudes, plus DC sweeps
        // through both clamps; the dispatched sparse and full kernels against the scalar one.
        var block = new short[64];
        var scalar = new byte[64];
        var sparse = new byte[64];
        var full = new byte[64];
        for (var last = 0; last < 64; last++)
        {
            for (var trial = 0; trial < 400; trial++)
            {
                Array.Clear(block);
                var range = trial % 4 == 0 ? 32767 : trial % 4 == 1 ? 1024 : 200;
                block[0] = (short)rng.Next(-range, range + 1);
                for (var k = 1; k <= last; k++)
                {
                    if (rng.Next(100) < 70)
                        block[Zigzag[k]] = (short)rng.Next(-range, range + 1);
                }

                if (last > 0)
                    block[Zigzag[last]] = (short)(rng.Next(1, 300) * (rng.Next(2) == 0 ? 1 : -1));

                JpegIdct.Idct8x8Scalar(scalar, 0, 8, block);
                JpegIdct.Idct8x8(sparse, 0, 8, block, last);
                JpegIdct.Idct8x8(full, 0, 8, block);
                checks += 2;
                if (!sparse.AsSpan().SequenceEqual(scalar))
                    bad++;
                if (!full.AsSpan().SequenceEqual(scalar))
                    bad++;
            }
        }

        for (var dc = -4200; dc <= 4200; dc++)
        {
            Array.Clear(block);
            block[0] = (short)dc;
            block[1] = (short)(dc / 3);
            block[8] = (short)(-dc / 5);
            JpegIdct.Idct8x8Scalar(scalar, 0, 8, block);
            JpegIdct.Idct8x8(sparse, 0, 8, block, 2);
            checks++;
            if (!sparse.AsSpan().SequenceEqual(scalar))
                bad++;
        }

        // Colour: random rows with the extremes mixed in (chroma 0 / 4080 at 16x, luma 0 / 255),
        // odd lengths so the scalar tails run too, both channel orders.
        for (var trial = 0; trial < 300; trial++)
        {
            var n = rng.Next(1, 200);
            var y = new byte[n];
            var cb8 = new byte[n];
            var cr8 = new byte[n];
            var cb16 = new ushort[n];
            var cr16 = new ushort[n];
            for (var i = 0; i < n; i++)
            {
                var e = rng.Next(8);
                y[i] = e == 0 ? (byte)0 : e == 1 ? (byte)255 : (byte)rng.Next(256);
                cb8[i] = e == 2 ? (byte)0 : e == 3 ? (byte)255 : (byte)rng.Next(256);
                cr8[i] = e == 4 ? (byte)0 : e == 5 ? (byte)255 : (byte)rng.Next(256);
                cb16[i] = e == 2 ? (ushort)0 : e == 3 ? (ushort)4080 : (ushort)rng.Next(4081);
                cr16[i] = e == 4 ? (ushort)0 : e == 5 ? (ushort)4080 : (ushort)rng.Next(4081);
            }

            var a = new byte[n * 4];
            var b = new byte[n * 4];
            foreach (var bgr in new[] { false, true })
            {
                JpegColor.YccRow(a, y, cb16, cr16, n, bgr);
                JpegColor.YccRow16Scalar(b, y, cb16, cr16, 0, n, bgr);
                checks++;
                if (!a.AsSpan().SequenceEqual(b))
                    bad++;
                JpegColor.YccRow(a, y, cb8, cr8, n, bgr);
                JpegColor.YccRow8Scalar(b, y, cb8, cr8, 0, n, bgr);
                checks++;
                if (!a.AsSpan().SequenceEqual(b))
                    bad++;
            }
        }

        // Upsampling: every kernel kind, the dispatched tier against ForceTier = 1 (scalar).
        for (var trial = 0; trial < 200; trial++)
        {
            var w = rng.Next(1, 120);
            var plane = new byte[w * 2];
            rng.NextBytes(plane);
            foreach (var kind in new[] { JpegResample.OneToOne, JpegResample.V2, JpegResample.H2, JpegResample.Hv2, JpegResample.Generic })
            {
                var hs = kind == JpegResample.Generic ? 3 : 2;
                var n = JpegResample.OutputCount(kind, w, hs);
                var v16 = new ushort[n];
                var s16 = new ushort[n];
                var v8 = new byte[n];
                var s8 = new byte[n];
                var scratch = new ushort[n];
                JpegResample.ForceTier = 1;
                JpegResample.Row16(kind, s16, plane, 0, w, w, hs);
                JpegResample.Row(kind, s8, scratch, plane, 0, w, w, hs);
                JpegResample.ForceTier = 0;
                JpegResample.Row16(kind, v16, plane, 0, w, w, hs);
                JpegResample.Row(kind, v8, scratch, plane, 0, w, w, hs);
                checks += 2;
                if (!v16.AsSpan().SequenceEqual(s16))
                    bad++;
                if (!v8.AsSpan().SequenceEqual(s8))
                    bad++;
            }
        }

        Console.WriteLine($"# kernel equivalence on this runtime: {checks} checks, {bad} mismatches");
        return bad == 0;
    }
}
