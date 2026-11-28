using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Sdcb.SimdCodecs.Avif.Av1;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The AV1 vector kernels against their scalar twins on random inputs, beyond what the decode goldens happen to
/// exercise: every transform size and kind at every bit depth (the 12-bit row pass multiplies with split inputs),
/// every deblocking filter length in both directions, dequantization with and without quantizer matrices, Paeth and
/// chroma-from-luma.
/// </summary>
public sealed class AvifKernelTests
{
    public static TheoryData<int> Depths() => new() { 8, 10, 12 };

    [Theory]
    [MemberData(nameof(Depths))]
    public void RowPass_VectorEqualsScalar(int bitDepth)
    {
        if (!Vector128.IsHardwareAccelerated)
            return; // only the scalar pass runs on this tier
        var rng = new Random(bitDepth);
        int rowClampRange = bitDepth + 8;
        int colClampRange = Math.Max(bitDepth + 6, 16);
        int colMin = -(1 << (colClampRange - 1)), colMax = (1 << (colClampRange - 1)) - 1;
        int dqMax = (1 << (7 + bitDepth)) - 1;
        for (int log2W = 2; log2W <= 6; log2W++)
        {
            for (int log2H = Math.Max(2, log2W - 2); log2H <= Math.Min(6, log2W + 2); log2H++)
            {
                int w = 1 << log2W, h = 1 << log2H, tw = Math.Min(32, w), th = Math.Min(32, h);
                bool rectScale = Math.Abs(log2W - log2H) == 1;
                var kinds = new List<int> { Av1Transform.KindDct };
                if (log2W <= 4) kinds.Add(Av1Transform.KindAdst);
                if (log2W <= 5) kinds.Add(Av1Transform.KindIdtx);
                if (log2W == 2 && log2H == 2) kinds.Add(Av1Transform.KindWht);
                foreach (int kind in kinds)
                {
                    foreach (int rowShift in kind == Av1Transform.KindWht ? new[] { 0 } : new[] { 0, 1, 2 })
                    {
                        for (int trial = 0; trial < 6; trial++)
                        {
                            int activeRows = trial == 0 ? 1 : trial == 1 ? th : rng.Next(1, th + 1);
                            var dq = new int[tw * th];
                            // Full-range coefficients on most trials; small ones on the rest, where nothing clamps.
                            int range = trial < 4 ? dqMax : 1 << (bitDepth - 2);
                            for (int i = 0; i < activeRows * tw; i++)
                                dq[i] = rng.Next(-range - 1, range + 1);
                            string what = $"{bitDepth}-bit {w}x{h} kind={kind} shift={rowShift} rows={activeRows} trial={trial}";

                            var expected = new int[w * h];
                            Av1Transform.RowPassScalar(dq, expected, tw, w, activeRows, h, kind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
                            var v128 = new int[w * h];
                            Av1TransformSimd.RowPassV128(dq, v128, tw, w, activeRows, h, kind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
                            v128.ShouldBe(expected, what + " V128");
                            if (Avx2.IsSupported)
                            {
                                var v256 = new int[w * h];
                                Av1TransformSimd.RowPassV256(dq, v256, tw, w, activeRows, h, kind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
                                v256.ShouldBe(expected, what + " V256");
                            }
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Depths))]
    public void Dequant_VectorEqualsScalar(int bitDepth)
    {
        var rng = new Random(100 + bitDepth);
        int dqMin = -(1 << (7 + bitDepth)), dqMax = (1 << (7 + bitDepth)) - 1;
        var qm = new byte[32 * 32];
        foreach ((int tw, int th) in new[] { (4, 4), (8, 4), (4, 16), (8, 8), (16, 16), (32, 8), (32, 32) })
        {
            foreach (int denom in new[] { 1, 2, 4 })
            {
                foreach (bool useQm in new[] { false, true })
                {
                    for (int trial = 0; trial < 8; trial++)
                    {
                        var quant = new int[tw * th];
                        int nonzero = rng.Next(1, tw * th + 1);
                        for (int i = 0; i < nonzero; i++)
                        {
                            // Mostly small levels, some huge ones (golomb-coded) so the 24-bit mask and the clamp act.
                            int mag = rng.Next(8) == 0 ? rng.Next(1 << 20) : rng.Next(1, 64);
                            quant[rng.Next(tw * th)] = rng.Next(2) == 0 ? mag : -mag;
                        }
                        rng.NextBytes(qm);
                        int dcQ = rng.Next(4, 21388), acQ = rng.Next(4, 29248);
                        string what = $"{bitDepth}-bit {tw}x{th} denom={denom} qm={useQm} trial={trial}";

                        var expected = new int[tw * th];
                        int expectedRows = Av1Transform.DequantScalar(quant, expected, tw, th, dcQ, acQ, qm, useQm, denom, dqMin, dqMax);
                        var actual = new int[tw * th];
                        int rows = Av1Transform.DequantVector(quant, actual, tw, th, dcQ, acQ, qm, useQm, denom >> 1, dqMin, dqMax);
                        rows.ShouldBe(expectedRows, what);
                        actual.ShouldBe(expected, what);
                    }
                }
            }
        }
    }

    public static TheoryData<int, int, int> FilterCases()
    {
        var data = new TheoryData<int, int, int>();
        foreach (int depth in new[] { 8, 10, 12 })
        {
            data.Add(depth, 0, 4); data.Add(depth, 0, 8); data.Add(depth, 0, 16);
            data.Add(depth, 1, 4); data.Add(depth, 1, 8);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void DeblockEdge_VectorEqualsScalar(int bitDepth, int plane, int filterSize)
    {
        var rng = new Random(bitDepth * 100 + plane * 10 + filterSize);
        const int stride = 48, size = stride * 48;
        int maxSample = (1 << bitDepth) - 1;
        for (int trial = 0; trial < 400; trial++)
        {
            // Smooth ramps with small noise reach the flat / flat2 filters; rough content the narrow one and the masks.
            var buf = new ushort[size];
            int baseV = rng.Next(maxSample + 1);
            int noise = trial % 4 == 0 ? maxSample : 1 << Math.Max(0, bitDepth - 8 + rng.Next(3));
            int slope = rng.Next(-3, 4);
            for (int i = 0; i < size; i++)
                buf[i] = (ushort)Math.Clamp(baseV + slope * (i % stride) + rng.Next(-noise, noise + 1), 0, maxSample);
            int lvl = rng.Next(1, 64);
            int sharp = rng.Next(8);
            int shift = sharp > 4 ? 2 : sharp > 0 ? 1 : 0;
            int limit = sharp > 0 ? Math.Clamp(lvl >> shift, 1, 9 - sharp) : Math.Max(1, lvl >> shift);
            int blimit = 2 * (lvl + 2) + limit, thresh = lvl >> 4;
            foreach ((int dx, int dy) in new[] { (1, 0), (0, 1) })
            {
                int x = 16 + rng.Next(8), y = 16 + rng.Next(8);
                var expected = (ushort[])buf.Clone();
                Av1LoopFilter.FilterEdgeScalar(expected, stride, bitDepth, plane, x, y, limit, blimit, thresh, dx, dy, filterSize);
                var actual = (ushort[])buf.Clone();
                Av1LoopFilter.FilterEdgeVector(actual, stride, bitDepth, plane, x, y, limit, blimit, thresh, dx, dy, filterSize);
                actual.ShouldBe(expected, $"{bitDepth}-bit plane={plane} size={filterSize} d=({dx},{dy}) trial={trial}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Depths))]
    public void Paeth_VectorEqualsScalar(int bitDepth)
    {
        var rng = new Random(200 + bitDepth);
        int maxSample = (1 << bitDepth) - 1;
        const int stride = 80;
        var above = new int[260];
        var left = new int[260];
        foreach (int w in new[] { 4, 8, 16, 32, 64 })
        {
            foreach (int h in new[] { 4, 8, 16, 32, 64 })
            {
                for (int i = 0; i < 260; i++)
                {
                    above[i] = rng.Next(maxSample + 1);
                    left[i] = rng.Next(maxSample + 1);
                }
                var expected = new ushort[stride * 70];
                Av1IntraPred.PredictPaethScalar(expected, stride, 3, 2, w, h, above, left, 2);
                var actual = new ushort[stride * 70];
                Av1IntraPred.PredictPaethVector(actual, stride, 3, 2, w, h, above, left, 2);
                actual.ShouldBe(expected, $"{bitDepth}-bit {w}x{h}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Depths))]
    public void ChromaFromLuma_VectorEqualsScalar(int bitDepth)
    {
        var rng = new Random(300 + bitDepth);
        int maxSample = (1 << bitDepth) - 1;
        const int yStride = 160, cStride = 80;
        foreach ((int subX, int subY) in new[] { (1, 1), (1, 0), (0, 0) })
        {
            for (int txSz = 0; txSz < Av1Constants.TxWidthLog2.Length; txSz++)
            {
                int w = Av1Constants.TxWidth[txSz], h = Av1Constants.TxHeight[txSz];
                if (w > 32 || h > 32)
                    continue; // CfL blocks are at most 32x32
                for (int trial = 0; trial < 4; trial++)
                {
                    var yBuf = new ushort[yStride * 80];
                    for (int i = 0; i < yBuf.Length; i++)
                        yBuf[i] = (ushort)rng.Next(maxSample + 1);
                    var cBuf = new ushort[cStride * 40];
                    for (int i = 0; i < cBuf.Length; i++)
                        cBuf[i] = (ushort)rng.Next(maxSample + 1);
                    int startX = rng.Next(4) * 4, startY = rng.Next(2) * 4;
                    // Half the trials clamp the luma reads inside the block.
                    int maxLumaW = trial % 2 == 0 ? (startX + w) << subX : ((startX + rng.Next(1, w + 1)) << subX);
                    int maxLumaH = trial % 2 == 0 ? (startY + h) << subY : ((startY + rng.Next(1, h + 1)) << subY);
                    int alpha = rng.Next(-16 * 8, 16 * 8 + 1);
                    string what = $"{bitDepth}-bit sub=({subX},{subY}) tx={txSz} trial={trial}";

                    var expected = (ushort[])cBuf.Clone();
                    Av1IntraPred.PredictChromaFromLumaScalar(yBuf, yStride, expected, cStride, bitDepth, subX, subY, startX, startY, txSz, alpha, maxLumaW, maxLumaH);
                    var actual = (ushort[])cBuf.Clone();
                    Av1IntraPred.PredictChromaFromLumaVector(yBuf, yStride, actual, cStride, bitDepth, subX, subY, startX, startY, txSz, alpha, maxLumaW, maxLumaH);
                    actual.ShouldBe(expected, what);
                }
            }
        }
    }
    [Theory]
    [MemberData(nameof(Depths))]
    public void LoopRestorationRows_VectorEqualsScalar(int bitDepth)
    {
        if (!Vector128.IsHardwareAccelerated)
            return;
        var rng = new Random(400 + bitDepth);
        int maxSample = (1 << bitDepth) - 1;
        int round0 = bitDepth == 12 ? 5 : 3, round1 = bitDepth == 12 ? 9 : 11;
        int offset = 1 << (bitDepth + 7 - round0 - 1);
        int minInter = -offset, maxInter = (1 << (bitDepth + 1 + 7 - round0)) - 1 - offset;
        foreach (int w in new[] { 1, 3, 4, 7, 32, 61, 64 })
        {
            for (int trial = 0; trial < 20; trial++)
            {
                // Wiener taps: c0 in [-5, 10], c1 in [-23, 8], c2 in [-17, 46], centre 128 - 2 * (c0 + c1 + c2).
                int c0 = rng.Next(-5, 11), c1 = rng.Next(-23, 9), c2 = rng.Next(-17, 47);
                int[] f = [c0, c1, c2, 128 - 2 * (c0 + c1 + c2), c2, c1, c0];
                var row = new ushort[w + 6];
                for (int i = 0; i < row.Length; i++) row[i] = (ushort)rng.Next(maxSample + 1);
                var hS = new int[w];
                Av1LoopRestoration.WienerRowHScalar(row, hS, f, round0, minInter, maxInter, 0);
                var hV = new int[w];
                int done = Av1LoopRestoration.WienerRowHVector(row, hV, f, round0, minInter, maxInter);
                Av1LoopRestoration.WienerRowHScalar(row, hV, f, round0, minInter, maxInter, done);
                hV.ShouldBe(hS, $"H {bitDepth}-bit w={w} trial={trial}");

                var inter = new int[w * 7];
                for (int i = 0; i < inter.Length; i++) inter[i] = rng.Next(minInter, maxInter + 1);
                var vS = new ushort[w];
                Av1LoopRestoration.WienerRowVScalar(inter, 0, w, f, round1, maxSample, vS, 0);
                var vV = new ushort[w];
                done = Av1LoopRestoration.WienerRowVVector(inter, 0, w, f, round1, maxSample, vV);
                Av1LoopRestoration.WienerRowVScalar(inter, 0, w, f, round1, maxSample, vV, done);
                vV.ShouldBe(vS, $"V {bitDepth}-bit w={w} trial={trial}");

                // SGR rows: A in [1, 256], B as the box filter produces, three modes.
                var a3 = new int[3][];
                var b3 = new int[3][];
                for (int k = 0; k < 3; k++)
                {
                    a3[k] = new int[w + 2];
                    b3[k] = new int[w + 2];
                    for (int i = 0; i < w + 2; i++) { a3[k][i] = rng.Next(1, 257); b3[k][i] = rng.Next(0, 1 << (bitDepth + 9)); }
                }
                var cdef = new ushort[w];
                for (int i = 0; i < w; i++) cdef[i] = (ushort)rng.Next(maxSample + 1);
                for (int mode = 0; mode < 3; mode++)
                {
                    int totalShift = mode == 1 ? 8 : 9;
                    var oS = new int[w];
                    Av1LoopRestoration.SgrFilterRowScalar(a3[0], a3[1], a3[2], b3[0], b3[1], b3[2], cdef, oS, mode, totalShift, 0);
                    var oV = new int[w];
                    done = Av1LoopRestoration.SgrFilterRowVector(a3[0], a3[1], a3[2], b3[0], b3[1], b3[2], cdef, oV, mode, totalShift);
                    Av1LoopRestoration.SgrFilterRowScalar(a3[0], a3[1], a3[2], b3[0], b3[1], b3[2], cdef, oV, mode, totalShift, done);
                    oV.ShouldBe(oS, $"SGR {bitDepth}-bit w={w} mode={mode} trial={trial}");
                }

                int w0 = rng.Next(-96, 32), w1 = rng.Next(-32, 96), w2 = 128 - w0 - w1;
                var f0 = new int[w];
                var f1 = new int[w];
                for (int i = 0; i < w; i++) { f0[i] = rng.Next(-(1 << (bitDepth + 5)), 1 << (bitDepth + 5)); f1[i] = rng.Next(-(1 << (bitDepth + 5)), 1 << (bitDepth + 5)); }
                foreach ((int[]? g0, int[]? g1) in new[] { (f0, f1), (f0, (int[]?)null), ((int[]?)null, f1) })
                {
                    var cS = new ushort[w];
                    Av1LoopRestoration.SgrCombineScalar(cdef, g0, g1, w0, w1, w2, maxSample, cS, 0);
                    var cV = new ushort[w];
                    done = Av1LoopRestoration.SgrCombineVector(cdef, g0, g1, w0, w1, w2, maxSample, cV);
                    Av1LoopRestoration.SgrCombineScalar(cdef, g0, g1, w0, w1, w2, maxSample, cV, done);
                    cV.ShouldBe(cS, $"combine {bitDepth}-bit w={w} trial={trial}");
                }
            }
        }
    }
}
