using System.Buffers;

namespace Sdcb.SimdCodecs.Webp;

internal static partial class Vp8LEncoder
{
    // ---------------------------------------------------------------- predictor transform (Section 4.1)

    /// <summary>
    /// Chooses a predictor per block, writes the transform and returns the residual image (pooled).
    /// Each block takes the mode whose residuals are cheapest under the residual statistics of the
    /// blocks chosen so far, so the choice favours what the entropy coder will see.
    /// </summary>
    private static uint[] ApplyPredictor(uint[] argb, int width, int height, Vp8LEncoderConfig config, Vp8LBitWriter writer)
    {
        int bits = config.PredictorBits;
        int bw = (width + (1 << bits) - 1) >> bits, bh = (height + (1 << bits) - 1) >> bits;
        uint[] residual = ArrayPool<uint>.Shared.Rent(width * height);
        uint[] modes = ArrayPool<uint>.Shared.Rent(bw * bh);
        var cost = new ResidualCost();
        Span<float> modeCost = stackalloc float[14];
        try
        {
            for (int by = 0; by < bh; by++)
            {
                cost.Refresh();
                int y0 = by << bits, y1 = Math.Min(height, y0 + (1 << bits));
                for (int bx = 0; bx < bw; bx++)
                {
                    int x0 = bx << bits, x1 = Math.Min(width, x0 + (1 << bits));
                    int best = 0;
                    if (by > 0 || bx > 0)
                    {
                        modeCost.Clear();
                        for (int y = Math.Max(y0, 1); y < y1; y++)
                        {
                            int row = y * width;
                            for (int x = Math.Max(x0, 1); x < x1; x++)
                            {
                                int i = row + x;
                                uint c = argb[i], l = argb[i - 1], t = argb[i - width], tl = argb[i - width - 1];
                                uint tr = x == width - 1 ? argb[row] : argb[i - width + 1];
                                for (int m = 0; m < 14; m++)
                                {
                                    modeCost[m] += cost.Of(SubPixels(c, Predict(m, l, t, tl, tr)));
                                }
                            }
                        }

                        // Ties go to the lower mode; the left block's mode wins a near tie, which keeps
                        // the mode image compressible.
                        int left = bx > 0 ? (int)((modes[by * bw + bx - 1] >> 8) & 0xFF) : -1;
                        float bestCost = float.MaxValue;
                        for (int m = 0; m < 14; m++)
                        {
                            float c = modeCost[m] - (m == left ? 4f : 0f);
                            if (c < bestCost)
                            {
                                bestCost = c;
                                best = m;
                            }
                        }
                    }

                    modes[by * bw + bx] = 0xFF000000u | (uint)best << 8;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            int i = row + x;
                            uint p = PredictAt(argb, i, x, y, width, best);
                            uint r = SubPixels(argb[i], p);
                            residual[i] = r;
                            cost.Add(r);
                        }
                    }
                }
            }

            writer.WriteBits(1, 1);
            writer.WriteBits(0, 2);
            writer.WriteBits((uint)(bits - 2), 3);
            EncodeImageStream(modes.AsSpan(0, bw * bh), bw, bh, isMain: false, config, writer);
            return residual;
        }
        catch
        {
            ArrayPool<uint>.Shared.Return(residual);
            throw;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(modes);
        }
    }

    /// <summary>The prediction for pixel <paramref name="i"/> with the edge rules of Section 4.1.</summary>
    private static uint PredictAt(uint[] argb, int i, int x, int y, int width, int mode)
    {
        if (y == 0)
        {
            return x == 0 ? 0xFF000000u : argb[i - 1];
        }

        if (x == 0)
        {
            return argb[i - width];
        }

        uint tr = x == width - 1 ? argb[i - x] : argb[i - width + 1];
        return Predict(mode, argb[i - 1], argb[i - width], argb[i - width - 1], tr);
    }

    // ---------------------------------------------------------------- color transform (Section 4.2)

    /// <summary>
    /// Chooses green-to-red, green-to-blue and red-to-blue multipliers per block, writes the transform
    /// and applies it in place. Each block starts from the least-squares estimate and keeps the
    /// candidate (estimate, its neighbours, zero, the left and upper blocks' values) whose red and
    /// blue residuals are cheapest.
    /// </summary>
    private static void ApplyCrossColor(uint[] pixels, int width, int height, Vp8LEncoderConfig config, Vp8LBitWriter writer)
    {
        int bits = config.CrossColorBits;
        int bw = (width + (1 << bits) - 1) >> bits, bh = (height + (1 << bits) - 1) >> bits;
        uint[] elements = ArrayPool<uint>.Shared.Rent(bw * bh);
        var red = new ChannelCost();
        var blue = new ChannelCost();
        Span<int> candidates = stackalloc int[8];
        Span<(int G2b, int R2b)> pairs = stackalloc (int, int)[9];
        try
        {
            for (int by = 0; by < bh; by++)
            {
                red.Refresh();
                blue.Refresh();
                int y0 = by << bits, y1 = Math.Min(height, y0 + (1 << bits));
                for (int bx = 0; bx < bw; bx++)
                {
                    int x0 = bx << bits, x1 = Math.Min(width, x0 + (1 << bits));
                    uint leftElement = bx > 0 ? elements[by * bw + bx - 1] : 0xFF000000u;
                    uint upElement = by > 0 ? elements[(by - 1) * bw + bx] : 0xFF000000u;

                    // Least squares: red ~ g2r * g / 32; blue ~ (g2b * g + r2b * r) / 32.
                    double gg = 0, gr = 0, rr = 0, gb = 0, rb = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            uint c = pixels[y * width + x];
                            int g = (sbyte)(c >> 8), r = (sbyte)(c >> 16), b = (sbyte)c;
                            gg += g * g; gr += g * r; rr += r * r; gb += g * b; rb += r * b;
                        }
                    }

                    int g2rEstimate = gg > 0 ? ClampMultiplier(32 * gr / gg) : 0;
                    int count = 0;
                    AddCandidate(candidates, ref count, g2rEstimate);
                    AddCandidate(candidates, ref count, g2rEstimate - 1);
                    AddCandidate(candidates, ref count, g2rEstimate + 1);
                    AddCandidate(candidates, ref count, 0);
                    AddCandidate(candidates, ref count, (sbyte)leftElement);
                    AddCandidate(candidates, ref count, (sbyte)upElement);
                    int g2r = 0;
                    float best = float.MaxValue;
                    for (int k = 0; k < count; k++)
                    {
                        float c = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            for (int x = x0; x < x1; x++)
                            {
                                uint p = pixels[y * width + x];
                                c += red.Of((int)(p >> 16) - Delta(candidates[k], (int)(p >> 8)));
                            }
                        }

                        if (c < best)
                        {
                            best = c;
                            g2r = candidates[k];
                        }
                    }

                    // Blue: a 2x2 least-squares solve for (g2b, r2b), then a small search around it.
                    double det = gg * rr - gr * gr;
                    int g2bEstimate = 0, r2bEstimate = 0;
                    if (Math.Abs(det) > 1e-9)
                    {
                        g2bEstimate = ClampMultiplier(32 * (gb * rr - rb * gr) / det);
                        r2bEstimate = ClampMultiplier(32 * (rb * gg - gb * gr) / det);
                    }
                    else if (gg > 0)
                    {
                        g2bEstimate = ClampMultiplier(32 * gb / gg);
                    }

                    int pairCount = 0;
                    pairs[pairCount++] = (g2bEstimate, r2bEstimate);
                    pairs[pairCount++] = (g2bEstimate - 1, r2bEstimate);
                    pairs[pairCount++] = (g2bEstimate + 1, r2bEstimate);
                    pairs[pairCount++] = (g2bEstimate, r2bEstimate - 1);
                    pairs[pairCount++] = (g2bEstimate, r2bEstimate + 1);
                    pairs[pairCount++] = (gg > 0 ? ClampMultiplier(32 * gb / gg) : 0, 0);
                    pairs[pairCount++] = (0, 0);
                    pairs[pairCount++] = ((sbyte)(leftElement >> 8), (sbyte)(leftElement >> 16));
                    pairs[pairCount++] = ((sbyte)(upElement >> 8), (sbyte)(upElement >> 16));
                    int g2b = 0, r2b = 0;
                    best = float.MaxValue;
                    for (int k = 0; k < pairCount; k++)
                    {
                        (int cg, int cr) = pairs[k];
                        cg = Math.Clamp(cg, -128, 127);
                        cr = Math.Clamp(cr, -128, 127);
                        float c = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            for (int x = x0; x < x1; x++)
                            {
                                uint p = pixels[y * width + x];
                                c += blue.Of((int)p - Delta(cg, (int)(p >> 8)) - Delta(cr, (int)(p >> 16)));
                            }
                        }

                        if (c < best)
                        {
                            best = c;
                            g2b = cg;
                            r2b = cr;
                        }
                    }

                    uint element = 0xFF000000u | (uint)(byte)r2b << 16 | (uint)(byte)g2b << 8 | (byte)g2r;
                    elements[by * bw + bx] = element;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int i = y * width + x;
                            uint p = pixels[i];
                            int g = (int)(p >> 8), r = (int)(p >> 16);
                            uint newRed = (uint)(r - Delta(g2r, g)) & 0xFF;
                            uint newBlue = (uint)((int)p - Delta(g2b, g) - Delta(r2b, r)) & 0xFF;
                            pixels[i] = (p & 0xFF00FF00u) | newRed << 16 | newBlue;
                            red.Add((int)newRed);
                            blue.Add((int)newBlue);
                        }
                    }
                }
            }

            writer.WriteBits(1, 1);
            writer.WriteBits(1, 2);
            writer.WriteBits((uint)(bits - 2), 3);
            EncodeImageStream(elements.AsSpan(0, bw * bh), bw, bh, isMain: false, config, writer);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(elements);
        }
    }

    /// <summary>ColorTransformDelta of Section 4.2: both operands are signed bytes.</summary>
    private static int Delta(int multiplier, int color) => (sbyte)multiplier * (sbyte)color >> 5;

    private static int ClampMultiplier(double value) => (int)Math.Clamp(Math.Round(value), -128, 127);

    private static void AddCandidate(Span<int> candidates, ref int count, int value)
    {
        value = Math.Clamp(value, -128, 127);
        if (!candidates[..count].Contains(value))
        {
            candidates[count++] = value;
        }
    }

    /// <summary>
    /// Bits a residual pixel costs under the residual histograms accumulated so far (one per
    /// channel), refreshed per row of blocks. A prior peaked at zero stands in for the empty start.
    /// </summary>
    private sealed class ResidualCost
    {
        private readonly ChannelCost[] _channels = [new(), new(), new(), new()];

        public void Refresh()
        {
            foreach (ChannelCost c in _channels)
            {
                c.Refresh();
            }
        }

        public float Of(uint residual) =>
            _channels[0].Of((int)residual) + _channels[1].Of((int)(residual >> 8)) +
            _channels[2].Of((int)(residual >> 16)) + _channels[3].Of((int)(residual >> 24));

        public void Add(uint residual)
        {
            _channels[0].Add((int)residual);
            _channels[1].Add((int)(residual >> 8));
            _channels[2].Add((int)(residual >> 16));
            _channels[3].Add((int)(residual >> 24));
        }
    }

    private sealed class ChannelCost
    {
        private readonly uint[] _counts = new uint[256];
        private readonly float[] _bits = new float[256];
        private uint _total;

        public ChannelCost()
        {
            for (int v = 0; v < 256; v++)
            {
                // Prior: a two-sided geometric distribution around 0 (as signed bytes), weight ~256 samples.
                int magnitude = Math.Min(v, 256 - v);
                _counts[v] = (uint)Math.Max(1, (int)(64 * Math.Pow(0.75, magnitude)));
                _total += _counts[v];
            }

            Refresh();
        }

        public void Refresh()
        {
            float log2Total = MathF.Log2(_total);
            for (int v = 0; v < 256; v++)
            {
                _bits[v] = log2Total - MathF.Log2(_counts[v]);
            }
        }

        public float Of(int value) => _bits[value & 0xFF];

        public void Add(int value)
        {
            _counts[value & 0xFF]++;
            _total++;
        }
    }
}
