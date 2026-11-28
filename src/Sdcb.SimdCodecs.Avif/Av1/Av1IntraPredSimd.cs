using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>Vector twins of the intra predictors that dominate screen content and 4:2:x chroma.</summary>
internal static partial class Av1IntraPred
{
    /// <summary><c>dst[i] = src[i]</c>, widened.</summary>
    private static void WidenEdge(ReadOnlySpan<ushort> src, Span<int> dst)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; i + 8 <= src.Length; i += 8)
            {
                var v = Vector128.Create(src.Slice(i, 8));
                Vector128.WidenLower(v).AsInt32().CopyTo(dst.Slice(i, 4));
                Vector128.WidenUpper(v).AsInt32().CopyTo(dst.Slice(i + 4, 4));
            }
        }
        for (; i < src.Length; i++)
            dst[i] = src[i];
    }

    /// <summary>
    /// <see cref="PredictPaethScalar"/> four samples at a time. With base = a + l - tl the three distances are
    /// |a - tl|, |l - tl| and |a + l - 2·tl|; the selection is the scalar one, as lane masks.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void PredictPaethVector(ushort[] dst, int stride, int x, int y, int w, int h, Span<int> above, Span<int> left, int off)
    {
        int tlS = above[off - 1];
        var tl = Vector128.Create(tlS);
        for (int i = 0; i < h; i++)
        {
            int lS = left[off + i];
            var l = Vector128.Create(lS);
            var pTop = Vector128.Create(Math.Abs(lS - tlS));
            var lMinus2Tl = Vector128.Create(lS - 2 * tlS);
            Span<ushort> row = dst.AsSpan((y + i) * stride + x, w);
            for (int j = 0; j < w; j += 4)
            {
                var a = Vector128.Create(above.Slice(off + j, 4));
                var pLeft = Vector128.Abs(a - tl);
                var pTopLeft = Vector128.Abs(a + lMinus2Tl);
                var takeLeft = Vector128.LessThanOrEqual(pLeft, pTop) & Vector128.LessThanOrEqual(pLeft, pTopLeft);
                var takeTop = Vector128.LessThanOrEqual(pTop, pTopLeft);
                var v = Vector128.ConditionalSelect(takeLeft, l, Vector128.ConditionalSelect(takeTop, a, tl));
                Store4(v, row.Slice(j, 4)); // samples are below 2^12
            }
        }
    }

    /// <summary>Stores the four low 16-bit lanes of <paramref name="v"/> (values below 2^16) into <paramref name="dst4"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store4(Vector128<int> v, Span<ushort> dst4) =>
        MemoryMarshal.Write(MemoryMarshal.AsBytes(dst4.Slice(0, 4)), Vector128.Narrow(v.AsUInt32(), v.AsUInt32()).AsUInt64().ToScalar());

    /// <summary>Four samples widened to int lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load4(ReadOnlySpan<ushort> src4) =>
        Vector128.WidenLower(Vector128.CreateScalarUnsafe(MemoryMarshal.Read<ulong>(MemoryMarshal.AsBytes(src4.Slice(0, 4)))).AsUInt16()).AsInt32();

    /// <summary>
    /// <see cref="PredictChromaFromLumaScalar"/> with vector rows: the subsampled luma of a row whose luma reads all
    /// lie before the clamp (maxLumaW) is summed in pairs four at a time, and the final blend is done four at a time;
    /// clamped rows take the scalar formula sample by sample. Same integers, same order of rounding.
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit] // every stackalloc element read is written first (audited)
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void PredictChromaFromLumaVector(
        ushort[] yBuf, int yStride,
        ushort[] cBuf, int cStride,
        int bitDepth, int subX, int subY,
        int startX, int startY, int txSz, int alpha,
        int maxLumaW, int maxLumaH)
    {
        int w = Av1Constants.TxWidth[txSz];
        int h = Av1Constants.TxHeight[txSz];
        Span<int> l = stackalloc int[w * h];

        int shift = 3 - subX - subY;
        int maxLY = maxLumaH - (1 << subY);
        int maxLX = maxLumaW - (1 << subX);
        // Columns j < unclamped read luma (startX + j) << subX directly.
        int unclamped = Math.Clamp((maxLX >> subX) - startX + 1, 0, w) & ~3;
        var sum = Vector128<int>.Zero;
        int lumaSum = 0;
        var lowHalf = Vector128.Create(0xFFFFu);

        for (int i = 0; i < h; i++)
        {
            int lumaY = Math.Min((startY + i) << subY, maxLY);
            Span<int> lRow = l.Slice(i * w, w);
            int j = 0;
            for (; j < unclamped; j += 4)
            {
                Vector128<uint> t = Vector128<uint>.Zero;
                for (int dy = 0; dy <= subY; dy++)
                {
                    int rowOff = (lumaY + dy) * yStride + ((startX + j) << subX);
                    if (subX != 0)
                    {
                        // Eight luma samples, adjacent pairs summed: lanes hold sample 2k | sample 2k + 1 << 16.
                        var pairs = Vector128.Create(yBuf.AsSpan(rowOff, 8)).AsUInt32();
                        t += (pairs & lowHalf) + Vector128.ShiftRightLogical(pairs, 16);
                    }
                    else
                    {
                        t += Load4(yBuf.AsSpan(rowOff, 4)).AsUInt32();
                    }
                }
                var v = Vector128.ShiftLeft(t.AsInt32(), shift);
                v.CopyTo(lRow.Slice(j, 4));
                sum += v;
            }
            for (; j < w; j++)
            {
                int lumaX = Math.Min((startX + j) << subX, maxLX);
                int t = 0;
                for (int dy = 0; dy <= subY; dy++)
                    for (int dx = 0; dx <= subX; dx++)
                        t += yBuf[(lumaY + dy) * yStride + (lumaX + dx)];
                int v = t << shift;
                lRow[j] = v;
                lumaSum += v;
            }
        }

        int lumaAvg = Av1Constants.Round2(lumaSum + Vector128.Sum(sum), Av1Constants.TxWidthLog2[txSz] + Av1Constants.TxHeightLog2[txSz]);

        var vAlpha = Vector128.Create(alpha);
        var vAvg = Vector128.Create(lumaAvg);
        var round = Vector128.Create(32);
        var vMax = Vector128.Create((1 << bitDepth) - 1);
        for (int i = 0; i < h; i++)
        {
            Span<ushort> cRow = cBuf.AsSpan((startY + i) * cStride + startX, w);
            ReadOnlySpan<int> lRow = l.Slice(i * w, w);
            for (int j = 0; j < w; j += 4)
            {
                var dc = Load4(cRow.Slice(j, 4));
                // Round2Signed(x, 6): the magnitude rounded, the sign put back.
                var prod = vAlpha * (Vector128.Create(lRow.Slice(j, 4)) - vAvg);
                var mag = Vector128.ShiftRightArithmetic(Vector128.Abs(prod) + round, 6);
                var scaled = Vector128.ConditionalSelect(Vector128.LessThan(prod, Vector128<int>.Zero), -mag, mag);
                var v = Vector128.Clamp(dc + scaled, Vector128<int>.Zero, vMax);
                Store4(v, cRow.Slice(j, 4));
            }
        }
    }
}
