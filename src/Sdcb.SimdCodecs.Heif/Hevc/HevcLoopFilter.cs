namespace Sdcb.SimdCodecs.Heif.Hevc;

/// <summary>The in-loop filters of H.265 §8.7: deblocking (§8.7.2) and sample adaptive offset (§8.7.3).</summary>
internal sealed partial class HevcFrameDecoder
{
    /// <summary>β′ of Table 8-12, Q = 0..51.</summary>
    private static ReadOnlySpan<byte> BetaTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24,
        26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56, 58, 60, 62, 64,
    ];

    /// <summary>tC′ of Table 8-12, Q = 0..53.</summary>
    private static ReadOnlySpan<byte> TcTable =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 3,
        3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 9, 10, 11, 13, 14, 16, 18, 20, 22, 24,
    ];

    private HevcSliceHeader SliceAt(int x, int y)
    {
        var ctbLog2 = _sps.CtbLog2SizeY;
        return _ctbSlice[(y >> ctbLog2) * _sps.PicWidthInCtbs + (x >> ctbLog2)]!;
    }

    // ───────────────────────────── deblocking (8.7.2) ─────────────────────────────

    private void Deblock()
    {
        // All vertical edges of the picture first, then all horizontal edges on the result (8.7.2.1).
        // Edges are 8 samples apart and a filter reads at most 4 and writes at most 3 samples per side, so the
        // edges of one direction are independent and their order does not matter.
        DeblockDirection(vertical: true);
        DeblockDirection(vertical: false);
    }

    private void DeblockDirection(bool vertical)
    {
        var edges = vertical ? _edgeVer : _edgeHor;
        var width = _sps.Width;
        var height = _sps.Height;
        var luma = _pic.Planes[0];

        for (var y = 0; y < height; y += 4)
        {
            for (var x = 0; x < width; x += 4)
            {
                if ((vertical ? x : y) % 8 != 0) continue;
                var b = Block(x, y);
                if (edges[b] == 0) continue;
                var bS = edges[b];
                var xP = vertical ? x - 1 : x;
                var yP = vertical ? y : y - 1;
                var qpQ = _qpY[b];
                var qpP = _qpY[Block(xP, yP)];
                var slice = SliceAt(x, y);
                var noP = _noFilter[Block(xP, yP)] != 0;
                var noQ = _noFilter[b] != 0;

                var qPL = (qpQ + qpP + 1) >> 1;
                var bitScale = 1 << (_sps.BitDepthY - 8);
                var beta = BetaTable[Math.Clamp(qPL + (slice.BetaOffsetDiv2 << 1), 0, 51)] * bitScale;
                var tc = TcTable[Math.Clamp(qPL + 2 * (bS - 1) + (slice.TcOffsetDiv2 << 1), 0, 53)] * bitScale;
                // Sample k across the edge at position i: offset = base + i * across + k * along.
                var stride = width;
                var across = vertical ? 1 : stride;
                var along = vertical ? stride : 1;
                FilterLumaEdge(luma, y * stride + x, across, along, beta, tc, noP, noQ, (1 << _sps.BitDepthY) - 1);

                // Chroma (4:2:0): edges on the 8x8 chroma grid, i.e. luma positions that are multiples of 16, one
                // 4-sample chroma segment per 8 luma samples along the edge; bS must be 2.
                if (bS == 2 && (vertical ? x : y) % 16 == 0 && (vertical ? y : x) % 8 == 0)
                {
                    var chromaStride = _pic.ChromaWidth;
                    var cBase = (y >> 1) * chromaStride + (x >> 1);
                    var cAcross = vertical ? 1 : chromaStride;
                    var cAlong = vertical ? chromaStride : 1;
                    var chromaScale = 1 << (_sps.BitDepthC - 8);
                    var maxC = (1 << _sps.BitDepthC) - 1;
                    for (var c = 1; c <= 2; c++)
                    {
                        var offset = c == 1 ? _pps.CbQpOffset : _pps.CrQpOffset;
                        var qPi = ((qpQ + qpP + 1) >> 1) + offset;
                        var qpC = ChromaQpTable(qPi);
                        var tcC = TcTable[Math.Clamp(qpC + 2 * (bS - 1) + (slice.TcOffsetDiv2 << 1), 0, 53)] * chromaScale;
                        FilterChromaEdge(_pic.Planes[c], cBase, cAcross, cAlong, tcC, noP, noQ, maxC);
                    }
                }
            }
        }
    }

    /// <summary>8.7.2.5.3 (decisions) and 8.7.2.5.4 / 8.7.2.5.7 (filtering) for one 4-sample luma edge segment.</summary>
    private static void FilterLumaEdge(ushort[] s, int origin, int across, int along, int beta, int tc, bool noP, bool noQ, int max)
    {
        int P(int i, int k) => s[origin - (i + 1) * across + k * along];
        int Q(int i, int k) => s[origin + i * across + k * along];

        var dp0 = Math.Abs(P(2, 0) - 2 * P(1, 0) + P(0, 0));
        var dp3 = Math.Abs(P(2, 3) - 2 * P(1, 3) + P(0, 3));
        var dq0 = Math.Abs(Q(2, 0) - 2 * Q(1, 0) + Q(0, 0));
        var dq3 = Math.Abs(Q(2, 3) - 2 * Q(1, 3) + Q(0, 3));
        var dpq0 = dp0 + dq0;
        var dpq3 = dp3 + dq3;
        var dp = dp0 + dp3;
        var dq = dq0 + dq3;
        var d = dpq0 + dpq3;
        if (d >= beta) return;

        var dSam0 = StrongDecision(2 * dpq0, P(0, 0), P(3, 0), Q(0, 0), Q(3, 0), beta, tc);
        var dSam3 = StrongDecision(2 * dpq3, P(0, 3), P(3, 3), Q(0, 3), Q(3, 3), beta, tc);
        var dE = dSam0 && dSam3 ? 2 : 1;
        var dEp = dp < ((beta + (beta >> 1)) >> 3);
        var dEq = dq < ((beta + (beta >> 1)) >> 3);

        for (var k = 0; k < 4; k++)
        {
            int p0 = P(0, k), p1 = P(1, k), p2 = P(2, k), p3 = P(3, k);
            int q0 = Q(0, k), q1 = Q(1, k), q2 = Q(2, k), q3 = Q(3, k);
            var baseP = origin + k * along - across;
            var baseQ = origin + k * along;
            if (dE == 2)
            {
                // (8-389 .. 8-394).
                if (!noP)
                {
                    s[baseP] = (ushort)Math.Clamp((p2 + 2 * p1 + 2 * p0 + 2 * q0 + q1 + 4) >> 3, p0 - 2 * tc, p0 + 2 * tc);
                    s[baseP - across] = (ushort)Math.Clamp((p2 + p1 + p0 + q0 + 2) >> 2, p1 - 2 * tc, p1 + 2 * tc);
                    s[baseP - 2 * across] = (ushort)Math.Clamp((2 * p3 + 3 * p2 + p1 + p0 + q0 + 4) >> 3, p2 - 2 * tc, p2 + 2 * tc);
                }
                if (!noQ)
                {
                    s[baseQ] = (ushort)Math.Clamp((p1 + 2 * p0 + 2 * q0 + 2 * q1 + q2 + 4) >> 3, q0 - 2 * tc, q0 + 2 * tc);
                    s[baseQ + across] = (ushort)Math.Clamp((p0 + q0 + q1 + q2 + 2) >> 2, q1 - 2 * tc, q1 + 2 * tc);
                    s[baseQ + 2 * across] = (ushort)Math.Clamp((p0 + q0 + q1 + 3 * q2 + 2 * q3 + 4) >> 3, q2 - 2 * tc, q2 + 2 * tc);
                }
            }
            else
            {
                // (8-395 .. 8-402).
                var delta = (9 * (q0 - p0) - 3 * (q1 - p1) + 8) >> 4;
                if (Math.Abs(delta) >= tc * 10) continue;
                delta = Math.Clamp(delta, -tc, tc);
                if (!noP)
                {
                    s[baseP] = (ushort)Math.Clamp(p0 + delta, 0, max);
                    if (dEp)
                    {
                        var deltaP = Math.Clamp((((p2 + p0 + 1) >> 1) - p1 + delta) >> 1, -(tc >> 1), tc >> 1);
                        s[baseP - across] = (ushort)Math.Clamp(p1 + deltaP, 0, max);
                    }
                }
                if (!noQ)
                {
                    s[baseQ] = (ushort)Math.Clamp(q0 - delta, 0, max);
                    if (dEq)
                    {
                        var deltaQ = Math.Clamp((((q2 + q0 + 1) >> 1) - q1 - delta) >> 1, -(tc >> 1), tc >> 1);
                        s[baseQ + across] = (ushort)Math.Clamp(q1 + deltaQ, 0, max);
                    }
                }
            }
        }
    }

    /// <summary>8.7.2.5.6.</summary>
    private static bool StrongDecision(int dpq, int p0, int p3, int q0, int q3, int beta, int tc) =>
        dpq < (beta >> 2) && Math.Abs(p3 - p0) + Math.Abs(q0 - q3) < (beta >> 3) && Math.Abs(p0 - q0) < ((5 * tc + 1) >> 1);

    /// <summary>8.7.2.5.5 / 8.7.2.5.8 for one 4-sample chroma edge segment.</summary>
    private static void FilterChromaEdge(ushort[] s, int origin, int across, int along, int tc, bool noP, bool noQ, int max)
    {
        for (var k = 0; k < 4; k++)
        {
            var q = origin + k * along;
            var p = q - across;
            int p0 = s[p], p1 = s[p - across], q0 = s[q], q1 = s[q + across];
            var delta = Math.Clamp((((q0 - p0) << 2) + p1 - q1 + 4) >> 3, -tc, tc);
            if (!noP) s[p] = (ushort)Math.Clamp(p0 + delta, 0, max);
            if (!noQ) s[q] = (ushort)Math.Clamp(q0 - delta, 0, max);
        }
    }

    // ───────────────────────────── sample adaptive offset (8.7.3) ─────────────────────────────

    private void ApplySao()
    {
        if (!_sps.SaoEnabled) return;
        var anySao = false;
        foreach (var slice in _ctbSlice)
        {
            if (slice is { } h && (h.SaoLuma || h.SaoChroma)) anySao = true;
        }
        if (!anySao) return;

        // Transquant-bypass CUs are not filtered (8.7.3.1): with any in the picture, every CTB takes the
        // per-sample path that checks for them.
        var anyNoFilter = _noFilter.AsSpan(0, _blocks).IndexOf((byte)1) >= 0;
        Span<int> offsets = stackalloc int[4];
        for (var c = 0; c < 3; c++)
        {
            // SAO reads the deblocked picture and writes a new one (8.7.3.1). Edge offset reads at most one sample
            // beyond its CTB, so the deblocked samples it needs while the plane is rewritten in place, CTB row by CTB
            // row, are a strip: the row above the CTB row (saved before the previous strip rewrote it), the CTB row
            // itself and the row below. strip row r holds plane row y0 − 1 + r.
            var plane = _pic.Planes[c];
            var width = _pic.PlaneWidth(c);
            var height = _pic.PlaneHeight(c);
            var ctbSize = c == 0 ? _sps.CtbSizeY : _sps.CtbSizeY >> 1;
            var bitDepth = c == 0 ? _sps.BitDepthY : _sps.BitDepthC;
            var strip = System.Buffers.ArrayPool<ushort>.Shared.Rent((ctbSize + 2) * width);
            var above = System.Buffers.ArrayPool<ushort>.Shared.Rent(width);
            try
            {
                for (var ry = 0; ry < _sps.PicHeightInCtbs; ry++)
                {
                    var y0 = ry * ctbSize;
                    var yEnd = Math.Min(y0 + ctbSize + 1, height); // through the row below the strip
                    if (ry > 0) above.AsSpan(0, width).CopyTo(strip);
                    plane.AsSpan(y0 * width, (yEnd - y0) * width).CopyTo(strip.AsSpan(width));
                    if (y0 + ctbSize - 1 < height) strip.AsSpan(ctbSize * width, width).CopyTo(above); // deblocked last row
                    var stripOffset = -(y0 - 1) * width; // strip[stripOffset + y * width + x] = deblocked (x, y)

                    for (var rx = 0; rx < _sps.PicWidthInCtbs; rx++)
                    {
                        var ctbAddr = ry * _sps.PicWidthInCtbs + rx;
                        var slice = _ctbSlice[ctbAddr]!;
                        if (c == 0 ? !slice.SaoLuma : !slice.SaoChroma) continue;
                        var p = _sao[ctbAddr];
                        var type = p.Type(c);
                        if (type == 0) continue;
                        var x0 = rx * ctbSize;
                        var x1 = Math.Min(x0 + ctbSize, width);
                        var y1 = Math.Min(y0 + ctbSize, height);
                        for (var i = 0; i < 4; i++) offsets[i] = p.Offset(c, i);
                        if (type == 1)
                        {
                            // Band offset reads only the sample it writes: in place.
                            if (anyNoFilter)
                            {
                                SaoBand(c, plane, width, x0, y0, x1, y1, p, bitDepth);
                                continue;
                            }
                            for (var y = y0; y < y1; y++)
                            {
                                var row = plane.AsSpan(y * width + x0, x1 - x0);
                                HevcSao.BandRow(row, row, x1 - x0, p.Band(c), bitDepth, offsets);
                            }
                        }
                        else if (anyNoFilter || !SaoNeighboursUsable(rx, ry, ctbAddr, slice))
                        {
                            SaoEdge(c, strip, stripOffset, width, height, x0, y0, x1, y1, p, bitDepth, ctbAddr);
                        }
                        else
                        {
                            SaoEdgeFast(strip, stripOffset, plane, width, height, x0, y0, x1, y1, p.Class(c), bitDepth, offsets);
                        }
                    }
                }
            }
            finally
            {
                System.Buffers.ArrayPool<ushort>.Shared.Return(strip);
                System.Buffers.ArrayPool<ushort>.Shared.Return(above);
            }
        }
    }

    /// <summary>
    /// True when every CTB around <paramref name="ctbAddr"/> that exists may be read by its edge offset (the slice
    /// conditions of 8.7.3.2 depend only on the neighbouring sample's CTB).
    /// </summary>
    private bool SaoNeighboursUsable(int rx, int ry, int ctbAddr, HevcSliceHeader slice)
    {
        var ctbLog2 = _sps.CtbLog2SizeY;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                int nx = rx + dx, ny = ry + dy;
                if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= _sps.PicWidthInCtbs || ny >= _sps.PicHeightInCtbs) continue;
                if (!SaoNeighbourUsable(nx << ctbLog2, ny << ctbLog2, ctbAddr, slice, ctbLog2)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Edge offset of one CTB whose neighbours are all usable: only the picture boundary excludes samples (8.7.3.2),
    /// so each row is one run for <see cref="HevcSao.EdgeRow"/>.
    /// </summary>
    private static void SaoEdgeFast(ushort[] source, int sourceOffset, ushort[] dst, int width, int height, int x0, int y0, int x1, int y1,
        int eoClass, int bitDepth, ReadOnlySpan<int> offsets)
    {
        // Table 8-13; hPos[1] = −hPos[0] and vPos[1] = −vPos[0].
        var (hPos, vPos) = eoClass switch { 0 => (-1, 0), 1 => (0, -1), 2 => (-1, -1), _ => (1, -1) };
        int xs = x0, xe = x1, ys = y0, ye = y1;
        if (hPos != 0)
        {
            if (xs == 0) xs = 1;
            if (xe == width) xe = width - 1;
        }
        if (vPos != 0)
        {
            if (ys == 0) ys = 1;
            if (ye == height) ye = height - 1;
        }
        var count = xe - xs;
        if (count <= 0) return;
        var max = (1 << bitDepth) - 1;
        for (var y = ys; y < ye; y++)
        {
            var at = y * width + xs;
            var src = sourceOffset + at;
            var neighbour = vPos * width + hPos;
            HevcSao.EdgeRow(source.AsSpan(src, count), source.AsSpan(src + neighbour, count), source.AsSpan(src - neighbour, count),
                dst.AsSpan(at, count), count, offsets, max);
        }
    }

    private void SaoBand(int c, ushort[] source, int stride, int x0, int y0, int x1, int y1, SaoParams p, int bitDepth)
    {
        Span<int> bandTable = stackalloc int[32];
        Span<int> offsetVal = [0, p.Offset(c, 0), p.Offset(c, 1), p.Offset(c, 2), p.Offset(c, 3)];
        for (var k = 0; k < 4; k++) bandTable[(k + p.Band(c)) & 31] = k + 1;
        var shift = bitDepth - 5;
        var max = (1 << bitDepth) - 1;
        var dst = _pic.Planes[c];
        var lumaShift = c == 0 ? 0 : 1;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (_noFilter[Block(x << lumaShift, y << lumaShift)] != 0) continue;
                var v = source[y * stride + x];
                var bandIdx = bandTable[v >> shift];
                if (bandIdx != 0) dst[y * stride + x] = (ushort)Math.Clamp(v + offsetVal[bandIdx], 0, max);
            }
        }
    }

    private void SaoEdge(int c, ushort[] source, int sourceOffset, int width, int height, int x0, int y0, int x1, int y1, SaoParams p, int bitDepth, int ctbAddr)
    {
        // Table 8-13.
        var eoClass = p.Class(c);
        int hPos0, vPos0, hPos1, vPos1;
        switch (eoClass)
        {
            case 0: hPos0 = -1; vPos0 = 0; hPos1 = 1; vPos1 = 0; break;
            case 1: hPos0 = 0; vPos0 = -1; hPos1 = 0; vPos1 = 1; break;
            case 2: hPos0 = -1; vPos0 = -1; hPos1 = 1; vPos1 = 1; break;
            default: hPos0 = 1; vPos0 = -1; hPos1 = -1; vPos1 = 1; break;
        }
        Span<int> offsetVal = [0, p.Offset(c, 0), p.Offset(c, 1), p.Offset(c, 2), p.Offset(c, 3)];
        var max = (1 << bitDepth) - 1;
        var dst = _pic.Planes[c];
        var lumaShift = c == 0 ? 0 : 1;
        var currentSlice = _ctbSlice[ctbAddr]!;
        var ctbLog2 = _sps.CtbLog2SizeY;

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                if (_noFilter[Block(x << lumaShift, y << lumaShift)] != 0) continue;
                int xa = x + hPos0, ya = y + vPos0, xb = x + hPos1, yb = y + vPos1;
                if (xa < 0 || ya < 0 || xa >= width || ya >= height || xb < 0 || yb < 0 || xb >= width || yb >= height) continue;
                if (!SaoNeighbourUsable(xa << lumaShift, ya << lumaShift, ctbAddr, currentSlice, ctbLog2) ||
                    !SaoNeighbourUsable(xb << lumaShift, yb << lumaShift, ctbAddr, currentSlice, ctbLog2))
                {
                    continue;
                }
                var v = source[sourceOffset + y * width + x];
                var edgeIdx = 2 + Math.Sign(v - source[sourceOffset + ya * width + xa]) + Math.Sign(v - source[sourceOffset + yb * width + xb]);
                if (edgeIdx is 0 or 1 or 2) edgeIdx = edgeIdx == 2 ? 0 : edgeIdx + 1;
                if (edgeIdx != 0) dst[y * width + x] = (ushort)Math.Clamp(v + offsetVal[edgeIdx], 0, max);
            }
        }
    }

    /// <summary>The slice-boundary conditions of 8.7.3.2 for a neighbouring sample at luma (xN, yN).</summary>
    private bool SaoNeighbourUsable(int xN, int yN, int ctbAddr, HevcSliceHeader currentSlice, int ctbLog2)
    {
        var neighbourCtb = (yN >> ctbLog2) * _sps.PicWidthInCtbs + (xN >> ctbLog2);
        if (neighbourCtb == ctbAddr) return true;
        var neighbourSlice = _ctbSlice[neighbourCtb]!;
        if (_ctbSliceAddr[neighbourCtb] == _ctbSliceAddr[ctbAddr]) return true;
        // Different slices: the later slice's flag decides (slices are in CTB raster order without tiles).
        return neighbourCtb < ctbAddr ? currentSlice.LoopFilterAcrossSlices : neighbourSlice.LoopFilterAcrossSlices;
    }
}
