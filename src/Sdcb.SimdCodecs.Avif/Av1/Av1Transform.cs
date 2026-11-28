using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 dequantization (§7.12) and 1D/2D inverse transforms (§7.13):
/// DCT (4/8/16/32/64), ADST (4/8/16), FLIPADST (4/8/16), IDTX (4/8/16/32), and WHT (4x4 lossless).
/// </summary>
internal static class Av1Transform
{
    private static readonly int[] Cos128Lookup =
    [
        4096, 4095, 4091, 4085, 4076, 4065, 4052, 4036,
        4017, 3996, 3973, 3948, 3920, 3889, 3857, 3822,
        3784, 3745, 3703, 3659, 3612, 3564, 3513, 3461,
        3406, 3349, 3290, 3229, 3166, 3102, 3035, 2967,
        2896, 2824, 2751, 2675, 2598, 2520, 2440, 2359,
        2276, 2191, 2106, 2019, 1931, 1842, 1751, 1660,
        1567, 1474, 1380, 1285, 1189, 1092, 995, 897,
        799, 700, 601, 501, 401, 301, 201, 101, 0,
    ];

    private static readonly int[] TransformRowShift =
    [
        0, 1, 2, 2, 2, 0, 0, 1, 1,
        1, 1, 1, 1, 1, 1, 2, 2, 2, 2,
    ];

    private const int Sinpi1_9 = 1321;
    private const int Sinpi2_9 = 2482;
    private const int Sinpi3_9 = 3344;
    private const int Sinpi4_9 = 3803;

    internal static int GetDcQuant(Av1FrameHeader fh, int bitDepth, int plane, int segmentId, int currentQIndex)
    {
        int qidx = fh.GetQIndex(ignoreDeltaQ: false, segmentId, currentQIndex);
        int delta = plane == 0 ? fh.DeltaQYDc : plane == 1 ? fh.DeltaQUDc : fh.DeltaQVDc;
        int idx = Av1Constants.Clip3(0, 255, qidx + delta);
        return Av1Tables.DcQlookup[((bitDepth - 8) >> 1) * 256 + idx];
    }

    internal static int GetAcQuant(Av1FrameHeader fh, int bitDepth, int plane, int segmentId, int currentQIndex)
    {
        int qidx = fh.GetQIndex(ignoreDeltaQ: false, segmentId, currentQIndex);
        int delta = plane == 0 ? 0 : plane == 1 ? fh.DeltaQUAc : fh.DeltaQVAc;
        int idx = Av1Constants.Clip3(0, 255, qidx + delta);
        return Av1Tables.AcQlookup[((bitDepth - 8) >> 1) * 256 + idx];
    }

    /// <summary>
    /// Dequantizes <paramref name="quant"/> (tw*th coefficients), runs the 2D inverse transform,
    /// and adds the residual in-place onto <paramref name="planeBuf"/> at (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    internal static void Reconstruct(
        Av1FrameHeader fh,
        int bitDepth,
        int plane,
        int segmentId,
        int currentQIndex,
        int txSz,
        int txType,
        ReadOnlySpan<int> quant,
        int eob,
        ushort[] planeBuf,
        int stride,
        int x,
        int y,
        int[] dequantBuf,
        int[] residualBuf)
    {
        if (eob == 0) return;

        int log2W = Av1Constants.TxWidthLog2[txSz];
        int log2H = Av1Constants.TxHeightLog2[txSz];
        int w = 1 << log2W;
        int h = 1 << log2H;
        int tw = Math.Min(32, w);
        int th = Math.Min(32, h);

        int dqDenom = txSz switch
        {
            Av1Constants.Tx32x32 or Av1Constants.Tx16x32 or Av1Constants.Tx32x16 or Av1Constants.Tx16x64 or Av1Constants.Tx64x16 => 2,
            Av1Constants.Tx64x64 or Av1Constants.Tx32x64 or Av1Constants.Tx64x32 => 4,
            _ => 1,
        };

        bool lossless = fh.LosslessArray[segmentId];
        int dcQ = GetDcQuant(fh, bitDepth, plane, segmentId, currentQIndex);
        int acQ = GetAcQuant(fh, bitDepth, plane, segmentId, currentQIndex);
        int qmLevel = fh.UsingQMatrix ? fh.SegQmLevel[plane, segmentId] : 15;
        bool useQm = fh.UsingQMatrix && txType < Av1Constants.Idtx && qmLevel < 15;
        ReadOnlySpan<byte> qm = useQm ? Av1QuantMatrix.GetMatrix(qmLevel, plane > 0 ? 1 : 0, txSz) : default;

        int dqMin = -(1 << (7 + bitDepth));
        int dqMax = (1 << (7 + bitDepth)) - 1;
        int nCoeff = tw * th;

        // 2D inverse transform (§7.13.3)
        int rowShift = lossless ? 0 : TransformRowShift[txSz];
        int colShift = lossless ? 0 : 4;
        int rowClampRange = bitDepth + 8;
        int colClampRange = Math.Max(bitDepth + 6, 16);
        int colMin = -(1 << (colClampRange - 1));
        int colMax = (1 << (colClampRange - 1)) - 1;
        bool rectScale = Math.Abs(log2W - log2H) == 1;

        int activeRows = 0;
        if (eob == 1)
        {
            int c0 = quant[0];
            if (c0 == 0) return;
            int q0 = useQm ? Av1Constants.Round2(dcQ * qm[0], 5) : dcQ;
            long dq = (long)c0 * q0;
            int dq0 = (dq < 0 ? -1 : 1) * (int)((Math.Abs(dq) & 0xFFFFFFL) / dqDenom);
            dq0 = Av1Constants.Clip3(dqMin, dqMax, dq0);

            if (!lossless && txType == Av1Constants.DctDct)
            {
                int rowMin = -(1 << (rowClampRange - 1));
                int rowMax = (1 << (rowClampRange - 1)) - 1;
                int v = rectScale ? Av1Constants.Round2(dq0 * 2896, 12) : dq0;
                int v1 = Av1Constants.Clip3(rowMin, rowMax, Av1Constants.Round2(v * 2896, 12));
                int v2 = Av1Constants.Clip3(colMin, colMax, Av1Constants.Round2(v1, rowShift));
                int v3 = Av1Constants.Clip3(colMin, colMax, Av1Constants.Round2(v2 * 2896, 12));
                int res = Av1Constants.Round2(v3, colShift);
                if (res == 0) return;
                AddConstantResidual(planeBuf, stride, x, y, w, h, res, (1 << bitDepth) - 1);
                return;
            }

            dequantBuf[0] = dq0;
            dequantBuf.AsSpan(1, nCoeff - 1).Clear();
            activeRows = 1;
        }
        else
        {
            activeRows = Vector128.IsHardwareAccelerated
                ? DequantVector(quant, dequantBuf, tw, th, dcQ, acQ, qm, useQm, dqDenom >> 1, dqMin, dqMax)
                : DequantScalar(quant, dequantBuf, tw, th, dcQ, acQ, qm, useQm, dqDenom, dqMin, dqMax);
            if (activeRows == 0) return;
        }

        RunTransform(fh, bitDepth, txSz, txType, lossless, log2W, log2H, w, h, tw, rowShift, colShift, rowClampRange,
            colClampRange, colMin, colMax, rectScale, activeRows, planeBuf, stride, x, y, dequantBuf, residualBuf);
    }

    /// <summary>Dequantizes rows 0 .. th - 1 (§7.12.3) into <paramref name="dequantBuf"/>; returns 1 + the last row with a nonzero coefficient.</summary>
    internal static int DequantScalar(ReadOnlySpan<int> quant, int[] dequantBuf, int tw, int th, int dcQ, int acQ,
        ReadOnlySpan<byte> qm, bool useQm, int dqDenom, int dqMin, int dqMax)
    {
        int activeRows = 0;
            for (int r = 0; r < th; r++)
            {
                int rowOff = r * tw;
                bool any = false;
                for (int cIdx = 0; cIdx < tw; cIdx++)
                {
                    int idx = rowOff + cIdx;
                    int c = quant[idx];
                    if (c == 0)
                    {
                        dequantBuf[idx] = 0;
                        continue;
                    }
                    any = true;
                    int q = idx == 0 ? dcQ : acQ;
                    int q2 = useQm ? Av1Constants.Round2(q * qm[idx], 5) : q;
                    long dq = (long)c * q2;
                    int sign = dq < 0 ? -1 : 1;
                    int dq2 = sign * (int)((Math.Abs(dq) & 0xFFFFFFL) / dqDenom);
                    dequantBuf[idx] = Av1Constants.Clip3(dqMin, dqMax, dq2);
                }
                if (any) activeRows = r + 1;
            }
        return activeRows;
    }

    /// <summary>
    /// Vector twin of <see cref="DequantScalar"/>, four coefficients per step. Only the low 24 bits of |c| * q survive
    /// the spec's mask, and the low 24 bits of a product depend only on the low bits of its factors, so the wrapping
    /// 32-bit product gives exactly the scalar 64-bit result. <paramref name="dqShift"/> is log2 of the denominator.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static int DequantVector(ReadOnlySpan<int> quant, int[] dequantBuf, int tw, int th, int dcQ, int acQ,
        ReadOnlySpan<byte> qm, bool useQm, int dqShift, int dqMin, int dqMax)
    {
        var vAc = Vector128.Create(acQ);
        var vDc = vAc.WithElement(0, dcQ);
        var vMin = Vector128.Create(dqMin);
        var vMax = Vector128.Create(dqMax);
        var mask24 = Vector128.Create(0xFFFFFF);
        var round = Vector128.Create(16);
        int activeRows = 0;
        Span<int> dst = dequantBuf.AsSpan(0, tw * th);
        for (int r = 0; r < th; r++)
        {
            int rowOff = r * tw;
            var any = Vector128<int>.Zero;
            for (int j = 0; j < tw; j += 4)
            {
                int idx = rowOff + j;
                var c = Vector128.Create(quant.Slice(idx, 4));
                if (c == Vector128<int>.Zero)
                {
                    Vector128<int>.Zero.CopyTo(dst.Slice(idx, 4));
                    continue;
                }
                any |= c;
                var q = idx == 0 ? vDc : vAc;
                if (useQm)
                {
                    uint raw = MemoryMarshal.Read<uint>(qm.Slice(idx, 4));
                    var m = Vector128.WidenLower(Vector128.WidenLower(Vector128.CreateScalarUnsafe(raw).AsByte())).AsInt32();
                    q = Vector128.ShiftRightArithmetic(q * m + round, 5);
                }
                var p = Vector128.ShiftRightLogical((Vector128.Abs(c) * q) & mask24, dqShift);
                var v = Vector128.ConditionalSelect(Vector128.LessThan(c, Vector128<int>.Zero), -p, p);
                Vector128.Clamp(v, vMin, vMax).CopyTo(dst.Slice(idx, 4));
            }
            if (any != Vector128<int>.Zero)
                activeRows = r + 1;
        }
        return activeRows;
    }

    private static void RunTransform(Av1FrameHeader fh, int bitDepth, int txSz, int txType, bool lossless, int log2W,
        int log2H, int w, int h, int tw, int rowShift, int colShift, int rowClampRange, int colClampRange, int colMin,
        int colMax, bool rectScale, int activeRows, ushort[] planeBuf, int stride, int x, int y, int[] dequantBuf,
        int[] residualBuf)
    {
        int rowKind = GetRowTransformKind(txType, lossless);
        int colKind = GetColTransformKind(txType, lossless);

        // Row pass: the vector butterflies multiply in int32 directly up to rowClampRange 18 (8 and 10 bits) and with
        // split inputs at 20 (12 bits), see ITxRange.
        if (rowClampRange <= 20 && Avx2.IsSupported && activeRows >= 8)
        {
            Av1TransformSimd.RowPassV256(
                dequantBuf, residualBuf, tw, w, activeRows, h,
                rowKind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
        }
        else if (rowClampRange <= 20 && activeRows >= 2 && Vector128.IsHardwareAccelerated)
        {
            Av1TransformSimd.RowPassV128(
                dequantBuf, residualBuf, tw, w, activeRows, h,
                rowKind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
        }
        else
        {
            RowPassScalar(dequantBuf, residualBuf, tw, w, activeRows, h, rowKind, log2W, rectScale, rowShift, rowClampRange, colMin, colMax);
        }

        // Column pass + add residual to planeBuf: colClampRange <= 18 for ALL bit depths (8, 10, 12)!
        bool flipUd = txType is Av1Constants.FlipAdstDct or Av1Constants.FlipAdstAdst or Av1Constants.VFlipAdst or Av1Constants.FlipAdstFlipAdst;
        bool flipLr = txType is Av1Constants.DctFlipAdst or Av1Constants.AdstFlipAdst or Av1Constants.HFlipAdst or Av1Constants.FlipAdstFlipAdst;
        int maxSample = (1 << bitDepth) - 1;

        if (Avx2.IsSupported && w >= 8)
        {
            Av1TransformSimd.ColPassAndAddV256(
                residualBuf, planeBuf, stride, x, y, w, h,
                colKind, log2H, colShift, colClampRange, flipUd, flipLr, maxSample);
            return;
        }
        if (Vector128.IsHardwareAccelerated && w >= 4)
        {
            Av1TransformSimd.ColPassAndAddV128(
                residualBuf, planeBuf, stride, x, y, w, h,
                colKind, log2H, colShift, colClampRange, flipUd, flipLr, maxSample);
            return;
        }

        Span<int> ct = stackalloc int[64];
        Span<int> cCopy = stackalloc int[64];
        for (int j = 0; j < w; j++)
        {
            for (int i = 0; i < h; i++)
                ct[i] = residualBuf[i * w + j];

            Run1D(ct, cCopy, colKind, log2H, colClampRange, whtShift: 0);

            for (int i = 0; i < h; i++)
                residualBuf[i * w + j] = Av1Constants.Round2(ct[i], colShift);
        }

        for (int i = 0; i < h; i++)
        {
            int yy = flipUd ? (h - i - 1) : i;
            int dstRow = (y + yy) * stride + x;
            int resRow = i * w;
            if (!flipLr)
            {
                for (int j = 0; j < w; j++)
                {
                    int v = planeBuf[dstRow + j] + residualBuf[resRow + j];
                    planeBuf[dstRow + j] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
            else
            {
                for (int j = 0; j < w; j++)
                {
                    int xx = w - j - 1;
                    int v = planeBuf[dstRow + xx] + residualBuf[resRow + j];
                    planeBuf[dstRow + xx] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
                }
            }
        }
    }

    /// <summary>The scalar row pass: the twin of <see cref="Av1TransformSimd.RowPassV128"/> and <see cref="Av1TransformSimd.RowPassV256"/>.</summary>
    internal static void RowPassScalar(
        int[] dequantBuf, int[] residualBuf,
        int tw, int w, int activeRows, int h,
        int rowKind, int log2W, bool rectScale,
        int rowShift, int rowClampRange, int colMin, int colMax)
    {
        Span<int> t = stackalloc int[64];
        Span<int> copyT = stackalloc int[64];
        for (int i = 0; i < activeRows; i++)
        {
            int rowOff = i * w;
            int srcOff = i * tw;
            for (int j = 0; j < tw; j++)
                t[j] = dequantBuf[srcOff + j];
            for (int j = tw; j < w; j++)
                t[j] = 0;

            if (rectScale)
            {
                for (int j = 0; j < tw; j++)
                    t[j] = Av1Constants.Round2(t[j] * 2896, 12);
            }

            Run1D(t, copyT, rowKind, log2W, rowClampRange, whtShift: 2);

            for (int j = 0; j < w; j++)
                residualBuf[rowOff + j] = Av1Constants.Clip3(colMin, colMax, Av1Constants.Round2(t[j], rowShift));
        }
        if (activeRows < h)
            residualBuf.AsSpan(activeRows * w, (h - activeRows) * w).Clear();
    }

    private static void AddConstantResidual(ushort[] planeBuf, int stride, int x, int y, int w, int h, int res, int maxSample)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Av1TransformSimd.AddConstantResidualV256(planeBuf, stride, x, y, w, h, res, maxSample);
            return;
        }
        if (Vector128.IsHardwareAccelerated)
        {
            Av1TransformSimd.AddConstantResidualV128(planeBuf, stride, x, y, w, h, res, maxSample);
            return;
        }
        for (int i = 0; i < h; i++)
        {
            int dstRow = (y + i) * stride + x;
            for (int j = 0; j < w; j++)
            {
                int v = planeBuf[dstRow + j] + res;
                planeBuf[dstRow + j] = (ushort)(v < 0 ? 0 : v > maxSample ? maxSample : v);
            }
        }
    }

    internal const int KindDct = 0;
    internal const int KindAdst = 1;
    internal const int KindIdtx = 2;
    internal const int KindWht = 3;

    private static int GetRowTransformKind(int txType, bool lossless)
    {
        if (lossless) return KindWht;
        return txType switch
        {
            Av1Constants.DctDct or Av1Constants.AdstDct or Av1Constants.FlipAdstDct or Av1Constants.HDct => KindDct,
            Av1Constants.DctAdst or Av1Constants.AdstAdst or Av1Constants.DctFlipAdst or Av1Constants.FlipAdstFlipAdst
                or Av1Constants.AdstFlipAdst or Av1Constants.FlipAdstAdst or Av1Constants.HAdst or Av1Constants.HFlipAdst => KindAdst,
            _ => KindIdtx,
        };
    }

    private static int GetColTransformKind(int txType, bool lossless)
    {
        if (lossless) return KindWht;
        return txType switch
        {
            Av1Constants.DctDct or Av1Constants.DctAdst or Av1Constants.DctFlipAdst or Av1Constants.VDct => KindDct,
            Av1Constants.AdstDct or Av1Constants.AdstAdst or Av1Constants.FlipAdstDct or Av1Constants.FlipAdstFlipAdst
                or Av1Constants.AdstFlipAdst or Av1Constants.FlipAdstAdst or Av1Constants.VAdst or Av1Constants.VFlipAdst => KindAdst,
            _ => KindIdtx,
        };
    }

    private static void Run1D(Span<int> t, Span<int> copyT, int kind, int n, int r, int whtShift)
    {
        switch (kind)
        {
            case KindWht:
                InverseWht4(t, whtShift);
                break;
            case KindDct:
                InverseDct(t, copyT, n, r);
                break;
            case KindAdst:
                InverseAdst(t, copyT, n, r);
                break;
            default:
                InverseIdtx(t, n);
                break;
        }
    }

    private static int Brev(int numBits, int x)
    {
        int t = 0;
        for (int i = 0; i < numBits; i++)
            t |= ((x >> i) & 1) << (numBits - 1 - i);
        return t;
    }

    private static int Cos128(int angle)
    {
        int a = angle & 255;
        if (a <= 64) return Cos128Lookup[a];
        if (a <= 128) return -Cos128Lookup[128 - a];
        if (a <= 192) return -Cos128Lookup[a - 128];
        return Cos128Lookup[256 - a];
    }

    private static int Sin128(int angle) => Cos128(angle - 64);

    private static void B(Span<int> t, int a, int b, int angle, int flip, int r)
    {
        long ta = t[a];
        long tb = t[b];
        int c = Cos128(angle);
        int s = Sin128(angle);
        int x = (int)Av1Constants.Round2(ta * c - tb * s, 12);
        int y = (int)Av1Constants.Round2(ta * s + tb * c, 12);
        int min = -(1 << (r - 1));
        int max = (1 << (r - 1)) - 1;
        x = Av1Constants.Clip3(min, max, x);
        y = Av1Constants.Clip3(min, max, y);
        if (flip != 0)
        {
            t[a] = y;
            t[b] = x;
        }
        else
        {
            t[a] = x;
            t[b] = y;
        }
    }

    private static void H(Span<int> t, int a, int b, int flip, int r)
    {
        if (flip != 0) (a, b) = (b, a);
        int x = t[a];
        int y = t[b];
        int min = -(1 << (r - 1));
        int max = (1 << (r - 1)) - 1;
        t[a] = Av1Constants.Clip3(min, max, x + y);
        t[b] = Av1Constants.Clip3(min, max, x - y);
    }

    /// <summary>Inverse DCT of length 2^n (2 &lt;= n &lt;= 6), §7.13.2.3.</summary>
    private static void InverseDct(Span<int> t, Span<int> copyT, int n, int r)
    {
        int len = 1 << n;
        t[..len].CopyTo(copyT);
        for (int i = 0; i < len; i++)
            t[i] = copyT[Brev(n, i)];

        if (n == 6)
            for (int i = 0; i < 16; i++) B(t, 32 + i, 63 - i, 63 - 4 * Brev(4, i), 0, r);
        if (n >= 5)
            for (int i = 0; i < 8; i++) B(t, 16 + i, 31 - i, 6 + (Brev(3, 7 - i) << 3), 0, r);
        if (n == 6)
            for (int i = 0; i < 16; i++) H(t, 32 + i * 2, 33 + i * 2, i & 1, r);
        if (n >= 4)
            for (int i = 0; i < 4; i++) B(t, 8 + i, 15 - i, 12 + (Brev(2, 3 - i) << 4), 0, r);
        if (n >= 5)
            for (int i = 0; i < 8; i++) H(t, 16 + 2 * i, 17 + 2 * i, i & 1, r);
        if (n == 6)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    B(t, 62 - i * 4 - j, 33 + i * 4 + j, 60 - 16 * Brev(2, i) + 64 * j, 1, r);
        if (n >= 3)
            for (int i = 0; i < 2; i++) B(t, 4 + i, 7 - i, 56 - 32 * i, 0, r);
        if (n >= 4)
            for (int i = 0; i < 4; i++) H(t, 8 + 2 * i, 9 + 2 * i, i & 1, r);
        if (n >= 5)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    B(t, 30 - 4 * i - j, 17 + 4 * i + j, 24 + (j << 6) + ((1 - i) << 5), 1, r);
        if (n == 6)
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 2; j++)
                    H(t, 32 + i * 4 + j, 35 + i * 4 - j, i & 1, r);

        for (int i = 0; i < 2; i++) B(t, 2 * i, 2 * i + 1, 32 + 16 * i, 1 - i, r);

        if (n >= 3)
            for (int i = 0; i < 2; i++) H(t, 4 + 2 * i, 5 + 2 * i, i, r);
        if (n >= 4)
            for (int i = 0; i < 2; i++) B(t, 14 - i, 9 + i, 48 + 64 * i, 1, r);
        if (n >= 5)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 2; j++)
                    H(t, 16 + 4 * i + j, 19 + 4 * i - j, i & 1, r);
        if (n == 6)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                    B(t, 61 - i * 8 - j, 34 + i * 8 + j, 56 - i * 32 + (j >> 1) * 64, 1, r);

        for (int i = 0; i < 2; i++) H(t, i, 3 - i, 0, r);

        if (n >= 3)
            B(t, 6, 5, 32, 1, r);
        if (n >= 4)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    H(t, 8 + 4 * i + j, 11 + 4 * i - j, i, r);
        if (n >= 5)
            for (int i = 0; i < 4; i++) B(t, 29 - i, 18 + i, 48 + (i >> 1) * 64, 1, r);
        if (n == 6)
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    H(t, 32 + 8 * i + j, 39 + 8 * i - j, i & 1, r);

        if (n >= 3)
            for (int i = 0; i < 4; i++) H(t, i, 7 - i, 0, r);
        if (n >= 4)
            for (int i = 0; i < 2; i++) B(t, 13 - i, 10 + i, 32, 1, r);
        if (n >= 5)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 4; j++)
                    H(t, 16 + i * 8 + j, 23 + i * 8 - j, i, r);
        if (n == 6)
            for (int i = 0; i < 8; i++) B(t, 59 - i, 36 + i, i < 4 ? 48 : 112, 1, r);

        if (n >= 4)
            for (int i = 0; i < 8; i++) H(t, i, 15 - i, 0, r);
        if (n >= 5)
            for (int i = 0; i < 4; i++) B(t, 27 - i, 20 + i, 32, 1, r);
        if (n == 6)
        {
            for (int i = 0; i < 8; i++)
            {
                H(t, 32 + i, 47 - i, 0, r);
                H(t, 48 + i, 63 - i, 1, r);
            }
        }

        if (n >= 5)
            for (int i = 0; i < 16; i++) H(t, i, 31 - i, 0, r);
        if (n == 6)
        {
            for (int i = 0; i < 8; i++) B(t, 55 - i, 40 + i, 32, 1, r);
            for (int i = 0; i < 32; i++) H(t, i, 63 - i, 0, r);
        }
    }

    private static void AdstInputPerm(Span<int> t, Span<int> copyT, int n)
    {
        int n0 = 1 << n;
        t[..n0].CopyTo(copyT);
        for (int i = 0; i < n0; i++)
        {
            int idx = (i & 1) != 0 ? (i - 1) : (n0 - i - 1);
            t[i] = copyT[idx];
        }
    }

    private static void AdstOutputPerm(Span<int> t, Span<int> copyT, int n)
    {
        int n0 = 1 << n;
        t[..n0].CopyTo(copyT);
        for (int i = 0; i < n0; i++)
        {
            int a = (i >> 3) & 1;
            int b = ((i >> 2) & 1) ^ ((i >> 3) & 1);
            int c = ((i >> 1) & 1) ^ ((i >> 2) & 1);
            int d = (i & 1) ^ ((i >> 1) & 1);
            int idx = ((d << 3) | (c << 2) | (b << 1) | a) >> (4 - n);
            t[i] = (i & 1) != 0 ? -copyT[idx] : copyT[idx];
        }
    }

    private static void InverseAdst(Span<int> t, Span<int> copyT, int n, int r)
    {
        if (n == 2)
        {
            long s0 = (long)Sinpi1_9 * t[0];
            long s1 = (long)Sinpi2_9 * t[0];
            long s2 = (long)Sinpi3_9 * t[1];
            long s3 = (long)Sinpi4_9 * t[2];
            long s4 = (long)Sinpi1_9 * t[2];
            long s5 = (long)Sinpi2_9 * t[3];
            long s6 = (long)Sinpi4_9 * t[3];
            long a7 = (long)t[0] - t[2];
            long b7 = a7 + t[3];
            s0 += s3;
            s1 -= s4;
            s3 = s2;
            s2 = (long)Sinpi3_9 * b7;
            s0 += s5;
            s1 -= s6;
            long x0 = s0 + s3;
            long x1 = s1 + s3;
            long x2 = s2;
            long x3 = s0 + s1 - s3;
            t[0] = (int)Av1Constants.Round2(x0, 12);
            t[1] = (int)Av1Constants.Round2(x1, 12);
            t[2] = (int)Av1Constants.Round2(x2, 12);
            t[3] = (int)Av1Constants.Round2(x3, 12);
            return;
        }

        if (n == 3)
        {
            AdstInputPerm(t, copyT, 3);
            for (int i = 0; i < 4; i++) B(t, 2 * i, 2 * i + 1, 60 - 16 * i, 1, r);
            for (int i = 0; i < 4; i++) H(t, i, 4 + i, 0, r);
            for (int i = 0; i < 2; i++) B(t, 4 + 3 * i, 5 + i, 48 - 32 * i, 1, r);
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                    H(t, 4 * j + i, 2 + 4 * j + i, 0, r);
            for (int i = 0; i < 2; i++) B(t, 2 + 4 * i, 3 + 4 * i, 32, 1, r);
            AdstOutputPerm(t, copyT, 3);
            return;
        }

        // n == 4
        AdstInputPerm(t, copyT, 4);
        for (int i = 0; i < 8; i++) B(t, 2 * i, 2 * i + 1, 62 - 8 * i, 1, r);
        for (int i = 0; i < 8; i++) H(t, i, 8 + i, 0, r);
        for (int i = 0; i < 2; i++)
        {
            B(t, 8 + 2 * i, 9 + 2 * i, 56 - 32 * i, 1, r);
            B(t, 13 + 2 * i, 12 + 2 * i, 8 + 32 * i, 1, r);
        }
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 4; i++)
                H(t, 8 * j + i, 4 + 8 * j + i, 0, r);
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
                B(t, 4 + 8 * j + 3 * i, 5 + 8 * j + i, 48 - 32 * i, 1, r);
        for (int j = 0; j < 4; j++)
            for (int i = 0; i < 2; i++)
                H(t, 4 * j + i, 2 + 4 * j + i, 0, r);
        for (int i = 0; i < 4; i++) B(t, 2 + 4 * i, 3 + 4 * i, 32, 1, r);
        AdstOutputPerm(t, copyT, 4);
    }

    private static void InverseWht4(Span<int> t, int shift)
    {
        int a = t[0] >> shift;
        int c = t[1] >> shift;
        int d = t[2] >> shift;
        int b = t[3] >> shift;
        a += c;
        d -= b;
        int e = (a - d) >> 1;
        b = e - b;
        c = e - c;
        a -= b;
        d += c;
        t[0] = a;
        t[1] = b;
        t[2] = c;
        t[3] = d;
    }

    private static void InverseIdtx(Span<int> t, int n)
    {
        switch (n)
        {
            case 2:
                for (int i = 0; i < 4; i++) t[i] = (int)Av1Constants.Round2((long)t[i] * 5793, 12); // 33 bits at 12-bit depth
                break;
            case 3:
                for (int i = 0; i < 8; i++) t[i] *= 2;
                break;
            case 4:
                for (int i = 0; i < 16; i++) t[i] = (int)Av1Constants.Round2((long)t[i] * 11586, 12);
                break;
            default: // 5
                for (int i = 0; i < 32; i++) t[i] *= 4;
                break;
        }
    }
}
