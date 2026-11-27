namespace Sdcb.SimdCodecs.Avif.Av1;

/// <summary>
/// AV1 Constrained Directional Enhancement Filter (CDEF) (§7.15).
/// </summary>
internal static class Av1Cdef
{
    private static readonly int[] DivTable = [0, 840, 420, 280, 210, 168, 140, 120, 105];

    private static readonly int[,] PriTaps = { { 4, 2 }, { 3, 3 } };
    private static readonly int[,] SecTaps = { { 2, 1 }, { 2, 1 } };

    private static readonly int[,,] CdefUvDir =
    {
        { { 0, 1, 2, 3, 4, 5, 6, 7 }, { 1, 2, 2, 2, 3, 4, 6, 0 } },
        { { 7, 0, 2, 4, 5, 6, 6, 6 }, { 0, 1, 2, 3, 4, 5, 6, 7 } },
    };

    /// <summary>
    /// Runs CDEF reading from <paramref name="srcPlanes"/> and writing into <paramref name="dstPlanes"/>.
    /// </summary>
    internal static void Apply(Av1SequenceHeader seq, Av1FrameHeader fh, Av1Picture pic, Av1FrameState st, ushort[][] srcPlanes, ushort[][] dstPlanes)
    {
        int numPlanes = seq.NumPlanes;
        int bitDepth = seq.BitDepth;
        int coeffShift = bitDepth - 8;
        int miRows = fh.MiRows;
        int miCols = fh.MiCols;

        // First copy entire source planes to destination so unfiltered / skipped / padded pixels match.
        for (int p = 0; p < numPlanes; p++)
            srcPlanes[p].AsSpan().CopyTo(dstPlanes[p]);

        if (fh.CodedLossless || fh.AllowIntrabc || !seq.EnableCdef)
            return;

        for (int r = 0; r < miRows; r += 2)
        {
            int cr = r >> 4;
            for (int c = 0; c < miCols; c += 2)
            {
                int cc = c >> 4;
                int idx = st.CdefIdx[cr * st.CdefCols + cc];
                if (idx < 0) continue;

                bool skip = st.Skips[r * miCols + c] != 0
                         && st.Skips[(r + 1) * miCols + c] != 0
                         && st.Skips[r * miCols + (c + 1)] != 0
                         && st.Skips[(r + 1) * miCols + (c + 1)] != 0;
                if (skip) continue;

                FindDirection(srcPlanes[0], pic.PlaneStrides[0], bitDepth, r, c, out int yDir, out int variance);

                // Luma
                int priStr = fh.CdefYPriStrength[idx] << coeffShift;
                int secStr = fh.CdefYSecStrength[idx] << coeffShift;
                int dir = priStr == 0 ? 0 : yDir;
                int varStr = (variance >> 6) != 0 ? Math.Min(Av1Constants.FloorLog2(variance >> 6), 12) : 0;
                priStr = variance != 0 ? ((priStr * (4 + varStr) + 8) >> 4) : 0;
                int damping = fh.CdefDamping + coeffShift;

                if (priStr != 0 || secStr != 0)
                {
                    FilterBlock(
                        srcPlanes[0], dstPlanes[0], pic.PlaneStrides[0],
                        miRows, miCols, 0, 0, coeffShift,
                        r, c, priStr, secStr, damping, dir);
                }

                if (numPlanes == 1) continue;

                // Chroma U & V
                int uvPri = fh.CdefUvPriStrength[idx] << coeffShift;
                int uvSec = fh.CdefUvSecStrength[idx] << coeffShift;
                if (uvPri != 0 || uvSec != 0)
                {
                    int uvDir = uvPri == 0 ? 0 : CdefUvDir[seq.SubsamplingX, seq.SubsamplingY, yDir];
                    int uvDamping = fh.CdefDamping + coeffShift - 1;
                    FilterBlock(
                        srcPlanes[1], dstPlanes[1], pic.PlaneStrides[1],
                        miRows, miCols, seq.SubsamplingX, seq.SubsamplingY, coeffShift,
                        r, c, uvPri, uvSec, uvDamping, uvDir);
                    FilterBlock(
                        srcPlanes[2], dstPlanes[2], pic.PlaneStrides[2],
                        miRows, miCols, seq.SubsamplingX, seq.SubsamplingY, coeffShift,
                        r, c, uvPri, uvSec, uvDamping, uvDir);
                }
            }
        }
    }

    private static void FindDirection(ushort[] yBuf, int stride, int bitDepth, int r, int c, out int yDir, out int variance)
    {
        Span<int> partial = stackalloc int[8 * 15];
        partial.Clear();
        Span<int> cost = stackalloc int[8];
        cost.Clear();

        int x0 = c << 2;
        int y0 = r << 2;
        int shift = bitDepth - 8;

        for (int i = 0; i < 8; i++)
        {
            int rowOff = (y0 + i) * stride + x0;
            for (int j = 0; j < 8; j++)
            {
                int x = (yBuf[rowOff + j] >> shift) - 128;
                partial[0 * 15 + (i + j)] += x;
                partial[1 * 15 + (i + (j >> 1))] += x;
                partial[2 * 15 + i] += x;
                partial[3 * 15 + (3 + i - (j >> 1))] += x;
                partial[4 * 15 + (7 + i - j)] += x;
                partial[5 * 15 + (3 - (i >> 1) + j)] += x;
                partial[6 * 15 + j] += x;
                partial[7 * 15 + ((i >> 1) + j)] += x;
            }
        }

        for (int i = 0; i < 8; i++)
        {
            int p2 = partial[2 * 15 + i];
            int p6 = partial[6 * 15 + i];
            cost[2] += p2 * p2;
            cost[6] += p6 * p6;
        }
        cost[2] *= DivTable[8];
        cost[6] *= DivTable[8];

        for (int i = 0; i < 7; i++)
        {
            int a0 = partial[0 * 15 + i], b0 = partial[0 * 15 + (14 - i)];
            cost[0] += (a0 * a0 + b0 * b0) * DivTable[i + 1];
            int a4 = partial[4 * 15 + i], b4 = partial[4 * 15 + (14 - i)];
            cost[4] += (a4 * a4 + b4 * b4) * DivTable[i + 1];
        }
        cost[0] += partial[0 * 15 + 7] * partial[0 * 15 + 7] * DivTable[8];
        cost[4] += partial[4 * 15 + 7] * partial[4 * 15 + 7] * DivTable[8];

        for (int i = 1; i < 8; i += 2)
        {
            int baseOff = i * 15;
            for (int j = 0; j < 5; j++)
            {
                int v = partial[baseOff + 3 + j];
                cost[i] += v * v;
            }
            cost[i] *= DivTable[8];
            for (int j = 0; j < 3; j++)
            {
                int a = partial[baseOff + j];
                int b = partial[baseOff + 10 - j];
                cost[i] += (a * a + b * b) * DivTable[2 * j + 2];
            }
        }

        int bestCost = 0;
        yDir = 0;
        for (int i = 0; i < 8; i++)
        {
            if (cost[i] > bestCost)
            {
                bestCost = cost[i];
                yDir = i;
            }
        }
        variance = (bestCost - cost[(yDir + 4) & 7]) >> 10;
    }

    private static void FilterBlock(
        ushort[] src, ushort[] dst, int stride,
        int miRows, int miCols, int subX, int subY, int coeffShift,
        int r, int c, int priStr, int secStr, int damping, int dir)
    {
        int x0 = (c * 4) >> subX;
        int y0 = (r * 4) >> subY;
        int w = 8 >> subX;
        int h = 8 >> subY;
        int maxX = (miCols * 4) >> subX;
        int maxY = (miRows * 4) >> subY;

        int tapSet = (priStr >> coeffShift) & 1;
        int priDampAdj = priStr != 0 ? Math.Max(0, damping - Av1Constants.FloorLog2(priStr)) : 0;
        int secDampAdj = secStr != 0 ? Math.Max(0, damping - Av1Constants.FloorLog2(secStr)) : 0;

        int dirSec1 = (dir + 6) & 7;
        int dirSec2 = (dir + 2) & 7;

        for (int i = 0; i < h; i++)
        {
            int py = y0 + i;
            for (int j = 0; j < w; j++)
            {
                int px = x0 + j;
                int x = src[py * stride + px];
                int sum = 0;
                int max = x;
                int min = x;

                for (int k = 0; k < 2; k++)
                {
                    int pTap = PriTaps[tapSet, k];
                    int sTap = SecTaps[tapSet, k];
                    int pOff = (dir * 2 + k) * 2;
                    int s1Off = (dirSec1 * 2 + k) * 2;
                    int s2Off = (dirSec2 * 2 + k) * 2;

                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        int ny = py + sign * Av1Tables.CdefDirections[pOff];
                        int nx = px + sign * Av1Tables.CdefDirections[pOff + 1];
                        if ((uint)ny < (uint)maxY && (uint)nx < (uint)maxX)
                        {
                            int p = src[ny * stride + nx];
                            sum += pTap * Constrain(p - x, priStr, priDampAdj);
                            if (p > max) max = p;
                            if (p < min) min = p;
                        }

                        ny = py + sign * Av1Tables.CdefDirections[s1Off];
                        nx = px + sign * Av1Tables.CdefDirections[s1Off + 1];
                        if ((uint)ny < (uint)maxY && (uint)nx < (uint)maxX)
                        {
                            int s = src[ny * stride + nx];
                            sum += sTap * Constrain(s - x, secStr, secDampAdj);
                            if (s > max) max = s;
                            if (s < min) min = s;
                        }

                        ny = py + sign * Av1Tables.CdefDirections[s2Off];
                        nx = px + sign * Av1Tables.CdefDirections[s2Off + 1];
                        if ((uint)ny < (uint)maxY && (uint)nx < (uint)maxX)
                        {
                            int s = src[ny * stride + nx];
                            sum += sTap * Constrain(s - x, secStr, secDampAdj);
                            if (s > max) max = s;
                            if (s < min) min = s;
                        }
                    }
                }

                int delta = (8 + sum - (sum < 0 ? 1 : 0)) >> 4;
                dst[py * stride + px] = (ushort)Av1Constants.Clip3(min, max, x + delta);
            }
        }
    }

    private static int Constrain(int diff, int threshold, int dampingAdj)
    {
        if (threshold == 0) return 0;
        int absDiff = Math.Abs(diff);
        int mag = Av1Constants.Clip3(0, absDiff, threshold - (absDiff >> dampingAdj));
        return diff < 0 ? -mag : mag;
    }
}
