using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// SSSE3 twin of the scalar block warp (§7.11.3.5) for an 8×8 block whose horizontal taps all lie inside the
/// reference row. Each output is an 8-tap dot product with its own filter: pmaddwd multiplies the filter row by
/// eight contiguous samples and sums adjacent pairs, phaddd finishes four outputs at once. The horizontal outputs
/// are kept transposed (one column per output position) as int16 so that the vertical taps of an output are
/// contiguous too. Same integer sums as the scalar code, so the same results.
/// </summary>
internal sealed partial class Av1InterPredict
{
    private readonly short[] _warpT = new short[8 * 16]; // column c, inter row r at c * 16 + r

    /// <summary>Largest sum of absolute taps over the warped filters: bounds every horizontal output.</summary>
    private static readonly int WarpMaxAbsTapSum = ComputeWarpMaxAbsTapSum();

    private static int ComputeWarpMaxAbsTapSum()
    {
        int max = 0;
        for (int f = 0; f + 8 <= WarpedFilters.Length; f += 8)
        {
            int sum = 0;
            for (int t = 0; t < 8; t++)
                sum += Math.Abs(WarpedFilters[f + t]);
            max = Math.Max(max, sum);
        }
        return max;
    }

    /// <summary>
    /// Whether every horizontal warp output fits int16: |sum| is at most the largest tap sum times the largest sample,
    /// and rounding by InterRound0 adds at most one.
    /// </summary>
    private static bool WarpFitsInt16(int bitDepth, int interRound0) =>
        (((long)WarpMaxAbsTapSum * ((1 << bitDepth) - 1)) >> interRound0) + 1 <= short.MaxValue;

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void BlockWarpSsse3(ushort[] refBuf, int refStride, int lastY, int ix4, int sx4, int iy4, int sy4,
        int alpha, int beta, int gamma, int delta, int i8, int j8, int w, int h, int interRound0, int interRound1, int[] pred)
    {
        ReadOnlySpan<short> refS = MemoryMarshal.Cast<ushort, short>(refBuf.AsSpan()); // samples < 2^12: same values
        ReadOnlySpan<short> filters = WarpedFilters;
        Span<short> warpT = _warpT;
        var round0 = Vector128.Create(1 << (interRound0 - 1));
        var round1 = Vector128.Create(1 << (interRound1 - 1));

        // Horizontal: 15 rows, 8 outputs each. Output k (i2 = k - 4) reads samples ix4 + k - 7 .. ix4 + k.
        for (int i1 = -7; i1 < 8; i1++)
        {
            int srcRow = Av1Constants.Clip3(0, lastY, iy4 + i1) * refStride + ix4 - 7;
            int sxRow = sx4 + beta * i1;
            var lo = HSum4(filters, refS, srcRow, sxRow, alpha, 0);
            var hi = HSum4(filters, refS, srcRow, sxRow, alpha, 4);
            lo = Vector128.ShiftRightArithmetic(lo + round0, interRound0);
            hi = Vector128.ShiftRightArithmetic(hi + round0, interRound0);
            int r = i1 + 7;
            warpT[r] = (short)lo.GetElement(0);
            warpT[16 + r] = (short)lo.GetElement(1);
            warpT[32 + r] = (short)lo.GetElement(2);
            warpT[48 + r] = (short)lo.GetElement(3);
            warpT[64 + r] = (short)hi.GetElement(0);
            warpT[80 + r] = (short)hi.GetElement(1);
            warpT[96 + r] = (short)hi.GetElement(2);
            warpT[112 + r] = (short)hi.GetElement(3);
        }

        // Vertical: output (i1, i2) reads inter rows i1 + 4 .. i1 + 11 of column i2 + 4.
        int rLimit = Math.Min(4, h - i8 * 8 - 4);
        int cLimit = Math.Min(4, w - j8 * 8 - 4);
        Span<int> row = stackalloc int[8];
        for (int i1 = -4; i1 < rLimit; i1++)
        {
            int syRow = sy4 + delta * i1;
            var lo = VSum4(filters, warpT, i1 + 4, syRow, gamma, 0);
            var hi = VSum4(filters, warpT, i1 + 4, syRow, gamma, 4);
            Vector128.ShiftRightArithmetic(lo + round1, interRound1).CopyTo(row);
            Vector128.ShiftRightArithmetic(hi + round1, interRound1).CopyTo(row[4..]);
            int dstRow = (i8 * 8 + i1 + 4) * w + j8 * 8 + 4;
            for (int i2 = -4; i2 < cLimit; i2++)
                pred[dstRow + i2] = row[i2 + 4];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WarpFilterOffset(int s) =>
        Av1Constants.Clip3(0, 192, Av1Constants.Round2(s, Av1Constants.WarpedDiffPrecBits) + Av1Constants.WarpedPixelPrecShifts) << 3;

    /// <summary>Outputs k0 .. k0 + 3 of one horizontal row.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> HSum4(ReadOnlySpan<short> filters, ReadOnlySpan<short> refS, int srcRow, int sxRow, int alpha, int k0)
    {
        var m0 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(sxRow + alpha * (k0 - 4)), 8)), Vector128.Create(refS.Slice(srcRow + k0, 8)));
        var m1 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(sxRow + alpha * (k0 - 3)), 8)), Vector128.Create(refS.Slice(srcRow + k0 + 1, 8)));
        var m2 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(sxRow + alpha * (k0 - 2)), 8)), Vector128.Create(refS.Slice(srcRow + k0 + 2, 8)));
        var m3 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(sxRow + alpha * (k0 - 1)), 8)), Vector128.Create(refS.Slice(srcRow + k0 + 3, 8)));
        return Ssse3.HorizontalAdd(Ssse3.HorizontalAdd(m0, m1), Ssse3.HorizontalAdd(m2, m3));
    }

    /// <summary>Outputs i2 = k0 - 4 .. k0 - 1 of one vertical row, inter rows from <paramref name="r0"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> VSum4(ReadOnlySpan<short> filters, ReadOnlySpan<short> warpT, int r0, int syRow, int gamma, int k0)
    {
        var m0 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(syRow + gamma * (k0 - 4)), 8)), Vector128.Create(warpT.Slice(k0 * 16 + r0, 8)));
        var m1 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(syRow + gamma * (k0 - 3)), 8)), Vector128.Create(warpT.Slice((k0 + 1) * 16 + r0, 8)));
        var m2 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(syRow + gamma * (k0 - 2)), 8)), Vector128.Create(warpT.Slice((k0 + 2) * 16 + r0, 8)));
        var m3 = Sse2.MultiplyAddAdjacent(Vector128.Create(filters.Slice(WarpFilterOffset(syRow + gamma * (k0 - 1)), 8)), Vector128.Create(warpT.Slice((k0 + 3) * 16 + r0, 8)));
        return Ssse3.HorizontalAdd(Ssse3.HorizontalAdd(m0, m1), Ssse3.HorizontalAdd(m2, m3));
    }
}
