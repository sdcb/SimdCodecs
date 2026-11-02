using System.Buffers;

namespace Sdcb.SimdCodecs.Webp;

internal sealed partial class Vp8Encoder
{
    // ---------------------------------------------------------------- segments

    /// <summary>
    /// Splits the macroblocks into up to four segments by how busy their luma is (the mean absolute
    /// difference of horizontally and vertically adjacent pixels), with a one-dimensional k-means,
    /// and gives busier segments a coarser quantizer: errors hide in texture and show on flat areas.
    /// </summary>
    private void SetupSegments()
    {
        int mbs = _mbW * _mbH;
        int wanted = Math.Clamp(_config.Segments, 1, 4);
        if (wanted == 1 || mbs < 8 || _config.SegmentSpread == 0)
        {
            _segmentCount = 1;
            Array.Clear(_segment, 0, mbs);
            SetQuant(0, _config.QuantIndex);
            return;
        }

        var activity = new float[mbs];
        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            for (int mbX = 0; mbX < _mbW; mbX++)
            {
                long sum = 0;
                for (int y = 0; y < 16; y++)
                {
                    int row = (mbY * 16 + y) * _srcStride + mbX * 16;
                    for (int x = 0; x < 16; x++)
                    {
                        int p = _srcY[row + x];
                        if (x < 15) sum += Math.Abs(p - _srcY[row + x + 1]);
                        if (y < 15) sum += Math.Abs(p - _srcY[row + _srcStride + x]);
                    }
                }

                // Log scale: perception of error is closer to ratios of activity than to differences.
                activity[mb(mbX, mbY)] = MathF.Log2(1 + sum / 480f);
            }
        }

        // k-means on the activities, centres started at the quantiles.
        float[] sorted = (float[])activity.Clone();
        Array.Sort(sorted);
        Span<float> centre = stackalloc float[4];
        for (int k = 0; k < wanted; k++)
        {
            centre[k] = sorted[(int)((k + 0.5) * mbs / wanted)];
        }

        Span<double> total = stackalloc double[4];
        Span<int> count = stackalloc int[4];
        for (int iteration = 0; iteration < 8; iteration++)
        {
            total.Clear();
            count.Clear();
            for (int i = 0; i < mbs; i++)
            {
                int k = Nearest(centre[..wanted], activity[i]);
                _segment[i] = (byte)k;
                total[k] += activity[i];
                count[k]++;
            }

            for (int k = 0; k < wanted; k++)
            {
                if (count[k] > 0)
                {
                    centre[k] = (float)(total[k] / count[k]);
                }
            }
        }

        // Centres are sorted (they start sorted and k-means in 1-D keeps the order). Quantizer offsets
        // in proportion to the centre's distance from the mean activity, weighted by the population.
        double mean = 0;
        for (int i = 0; i < mbs; i++)
        {
            mean += activity[i];
        }

        mean /= mbs;
        double spread = Math.Max(1e-6, centre[wanted - 1] - centre[0]);
        _segmentCount = wanted;
        for (int k = 0; k < wanted; k++)
        {
            double scale = (_config.QuantIndex + 8) / 48.0; // SegmentSpread is in steps at index 40
            int offset = (int)Math.Round(_config.SegmentSpread * scale * (centre[k] - mean) / spread);
            SetQuant(k, _config.QuantIndex + offset);
        }

        static int Nearest(ReadOnlySpan<float> centres, float value)
        {
            int best = 0;
            for (int k = 1; k < centres.Length; k++)
            {
                if (Math.Abs(value - centres[k]) < Math.Abs(value - centres[best]))
                {
                    best = k;
                }
            }

            return best;
        }

        int mb(int x, int y) => y * _mbW + x;
    }

    // ---------------------------------------------------------------- probabilities

    /// <summary>
    /// Counts the coefficient tree's branches over the whole frame and chooses each node's probability:
    /// the frequency of 0, coded as an update only when that saves more than the update costs
    /// (the update flag at its fixed probability and 8 bits).
    /// </summary>
    private void ChooseProbabilities()
    {
        var counts = new uint[Vp8EncoderCosts.ProbabilityCount * 2];
        WalkTokens(null, counts, skipEnabled: SkipEnabled);
        Span<byte> defaults = stackalloc byte[Vp8EncoderCosts.ProbabilityCount];
        Vp8Tables.CopyDefaultCoefficientProbabilities(defaults);
        int index = 0;
        for (int t = 0; t < 4; t++)
        {
            for (int b = 0; b < 8; b++)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int n = 0; n < 11; n++, index++)
                    {
                        uint zeros = counts[index * 2], ones = counts[index * 2 + 1];
                        byte old = defaults[index];
                        _probabilities[index] = old;
                        if (zeros + ones == 0)
                        {
                            continue;
                        }

                        int candidate = (int)Math.Clamp((zeros * 256 + (zeros + ones) / 2) / (zeros + ones), 1, 255);
                        int updateProbability = Vp8Tables.CoefficientUpdateProbabilities[t][b][c][n];
                        long keep = zeros * (long)Vp8EncoderCosts.Zero[old] + ones * (long)Vp8EncoderCosts.One[old] +
                                    Vp8EncoderCosts.Zero[updateProbability];
                        long change = zeros * (long)Vp8EncoderCosts.Zero[candidate] + ones * (long)Vp8EncoderCosts.One[candidate] +
                                      Vp8EncoderCosts.One[updateProbability] + 8 * 256;
                        if (change < keep)
                        {
                            _probabilities[index] = (byte)candidate;
                        }
                    }
                }
            }
        }
    }

    private bool SkipEnabled
    {
        get
        {
            int mbs = _mbW * _mbH;
            return _skip.AsSpan(0, mbs).IndexOf((byte)1) >= 0;
        }
    }

    private int SkipProbability()
    {
        int mbs = _mbW * _mbH, skipped = 0;
        for (int i = 0; i < mbs; i++)
        {
            skipped += _skip[i];
        }

        return Math.Clamp(((mbs - skipped) * 256 + mbs / 2) / mbs, 1, 255);
    }

    // ---------------------------------------------------------------- loop filter

    /// <summary>
    /// The frame's loop filter level: a starting point from the quantizer, then (from effort 3) the
    /// level among a few around it whose decoded luma is closest to the source.
    /// </summary>
    private int ChooseFilterLevel()
    {
        int q = _quant[0].Index;
        int guess = Math.Clamp((q * 2 + 3) / 5, 0, 63);
        if (!_config.FilterSearch)
        {
            return guess;
        }

        int best = guess;
        long bestError = long.MaxValue;
        int step = Math.Max(2, guess / 4);
        for (int level = Math.Max(0, guess - 2 * step); level <= Math.Min(63, guess + 2 * step); level += step)
        {
            byte[] frame = WriteFrame(level);
            using Vp8Yuv decoded = Vp8Decoder.Decode(frame, _width, _height);
            long error = 0;
            for (int y = 0; y < _height; y++)
            {
                error += Vp8EncoderKernels.Sse(ref _srcY[y * _srcStride], 0, ref decoded.Y[y * _width], 0, _width, 1);
            }

            if (error < bestError)
            {
                bestError = error;
                best = level;
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- bitstream

    /// <summary>The VP8 key frame (RFC 6386 Section 9): frame tag, start code, size, then the two partitions.</summary>
    private byte[] WriteFrame(int filterLevel)
    {
        bool skipEnabled = SkipEnabled;
        int mbs = _mbW * _mbH;
        using var header = new Vp8BoolEncoder(mbs * 4 + 1024);
        using var tokens = new Vp8BoolEncoder(mbs * 64 + 1024);

        header.PutBit(0); // colour space
        header.PutBit(0); // clamping required
        bool segmented = _segmentCount > 1;
        header.PutBit(segmented ? 1 : 0);
        if (segmented)
        {
            ChooseSegmentProbabilities();
            header.PutBit(1); // update map
            header.PutBit(1); // update data
            header.PutBit(1); // absolute values
            for (int k = 0; k < 4; k++)
            {
                header.PutBit(1);
                header.PutSignedLiteral(k < _segmentCount ? _quant[k].Index : 0, 7);
            }

            for (int k = 0; k < 4; k++)
            {
                header.PutBit(1);
                header.PutSignedLiteral(filterLevel, 6);
            }

            for (int k = 0; k < 3; k++)
            {
                header.PutBit(1);
                header.PutLiteral(_segmentProbabilities[k], 8);
            }
        }

        header.PutBit(0); // normal loop filter
        header.PutLiteral(filterLevel, 6);
        header.PutLiteral(_config.Sharpness, 3);
        header.PutBit(0); // no mode / reference filter deltas
        header.PutLiteral(0, 2); // one token partition
        header.PutLiteral(_quant[0].Index, 7);
        for (int i = 0; i < 5; i++)
        {
            header.PutBit(0); // no quantizer deltas
        }

        header.PutBit(0); // refresh entropy probabilities (unused in a key frame)
        int index = 0;
        Span<byte> defaults = stackalloc byte[Vp8EncoderCosts.ProbabilityCount];
        Vp8Tables.CopyDefaultCoefficientProbabilities(defaults);
        for (int t = 0; t < 4; t++)
        {
            for (int b = 0; b < 8; b++)
            {
                for (int c = 0; c < 3; c++)
                {
                    for (int n = 0; n < 11; n++, index++)
                    {
                        bool update = _probabilities[index] != defaults[index];
                        header.Put(update, Vp8Tables.CoefficientUpdateProbabilities[t][b][c][n]);
                        if (update)
                        {
                            header.PutLiteral(_probabilities[index], 8);
                        }
                    }
                }
            }
        }

        int skipProbability = skipEnabled ? SkipProbability() : 0;
        header.PutBit(skipEnabled ? 1 : 0);
        if (skipEnabled)
        {
            header.PutLiteral(skipProbability, 8);
        }

        // Per-macroblock modes.
        var aboveB = new byte[_mbW * 4];
        Span<byte> leftB = stackalloc byte[4];
        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            leftB.Clear();
            for (int mbX = 0; mbX < _mbW; mbX++)
            {
                int mb = mbY * _mbW + mbX;
                if (segmented)
                {
                    header.PutTree(Vp8Tables.SegmentIdTree, _segmentProbabilities, _segment[mb]);
                }

                if (skipEnabled)
                {
                    header.Put(_skip[mb], skipProbability);
                }

                int yMode = _yMode[mb];
                header.PutTree(Vp8Tables.KeyFrameYModeTree, Vp8Tables.KeyFrameYModeProbabilities, yMode);
                Span<byte> above = aboveB.AsSpan(mbX * 4, 4);
                if (yMode == 4)
                {
                    ReadOnlySpan<byte> modes = _bModes.AsSpan(mb * 16, 16);
                    for (int i = 0; i < 16; i++)
                    {
                        int a = i < 4 ? above[i] : modes[i - 4];
                        int l = (i & 3) == 0 ? leftB[i >> 2] : modes[i - 1];
                        header.PutTree(Vp8Tables.BlockModeTree, Vp8Tables.KeyFrameBlockModeProbabilities[a][l], modes[i]);
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        above[i] = modes[12 + i];
                        leftB[i] = modes[i * 4 + 3];
                    }
                }
                else
                {
                    byte derived = yMode switch { 0 => 0, 1 => 2, 2 => 3, _ => 1 };
                    above.Fill(derived);
                    leftB.Fill(derived);
                }

                header.PutTree(Vp8Tables.UvModeTree, Vp8Tables.KeyFrameUvModeProbabilities, _uvMode[mb]);
            }
        }

        WalkTokens(tokens, null, skipEnabled);
        ReadOnlySpan<byte> first = header.Finish();
        ReadOnlySpan<byte> second = tokens.Finish();
        if (first.Length >= 1 << 19)
        {
            throw new ArgumentException("The image is too large for one VP8 frame header partition (512 KiB of modes).");
        }

        var frame = new byte[10 + first.Length + second.Length];
        uint tag = 0u | 0u << 1 | 1u << 4 | (uint)first.Length << 5; // key frame, version 0, shown
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        frame[3] = 0x9d;
        frame[4] = 0x01;
        frame[5] = 0x2a;
        frame[6] = (byte)_width;
        frame[7] = (byte)(_width >> 8);
        frame[8] = (byte)_height;
        frame[9] = (byte)(_height >> 8);
        first.CopyTo(frame.AsSpan(10));
        second.CopyTo(frame.AsSpan(10 + first.Length));
        return frame;
    }

    private void ChooseSegmentProbabilities()
    {
        // Tree: node 0 splits {0,1} | {2,3}; node 1 splits 0 | 1; node 2 splits 2 | 3.
        Span<int> n = stackalloc int[4];
        int mbs = _mbW * _mbH;
        for (int i = 0; i < mbs; i++)
        {
            n[_segment[i]]++;
        }

        _segmentProbabilities[0] = Probability(n[0] + n[1], n[2] + n[3]);
        _segmentProbabilities[1] = Probability(n[0], n[1]);
        _segmentProbabilities[2] = Probability(n[2], n[3]);

        static byte Probability(int zeros, int ones) =>
            zeros + ones == 0 ? (byte)255 : (byte)Math.Clamp((zeros * 256 + (zeros + ones) / 2) / (zeros + ones), 1, 255);
    }

    /// <summary>
    /// Every macroblock's tokens in decoder order (Y2, Y, U, V) with the non-zero contexts the
    /// decoder keeps: written to <paramref name="writer"/>, or counted into <paramref name="counts"/>
    /// (zeros and ones per node probability).
    /// </summary>
    private void WalkTokens(Vp8BoolEncoder? writer, uint[]? counts, bool skipEnabled)
    {
        var aboveNz = new uint[_mbW];
        for (int mbY = 0; mbY < _mbH; mbY++)
        {
            uint leftNz = 0;
            for (int mbX = 0; mbX < _mbW; mbX++)
            {
                int mb = mbY * _mbW + mbX;
                bool i16 = _yMode[mb] != 4;
                if (skipEnabled && _skip[mb] != 0)
                {
                    uint keep = i16 ? 0u : NzY2;
                    aboveNz[mbX] &= keep;
                    leftNz &= keep;
                    continue;
                }

                ReadOnlySpan<short> levels = _levels.AsSpan(mb * 25 * 16, 25 * 16);
                ref uint above = ref aboveNz[mbX];
                if (i16)
                {
                    int ctx = (int)(((above >> 24) & 1) + ((leftNz >> 24) & 1));
                    bool nz = PutBlock(writer, counts, levels.Slice(24 * 16, 16), 0, 1, ctx);
                    above = (above & ~NzY2) | (nz ? NzY2 : 0);
                    leftNz = (leftNz & ~NzY2) | (nz ? NzY2 : 0);
                }

                for (int b = 0; b < 16; b++)
                {
                    int bx = b & 3, by = b >> 2;
                    int ctx = (int)(((above >> bx) & 1) + ((leftNz >> by) & 1));
                    bool nz = PutBlock(writer, counts, levels.Slice(b * 16, 16), i16 ? 1 : 0, i16 ? 0 : 3, ctx);
                    above = (above & ~(1u << bx)) | (nz ? 1u << bx : 0);
                    leftNz = (leftNz & ~(1u << by)) | (nz ? 1u << by : 0);
                }

                for (int b = 16; b < 24; b++)
                {
                    int plane = (b - 16) >> 2, bx = b & 1, by = (b >> 1) & 1;
                    int abit = 4 + plane * 2 + bx, lbit = 4 + plane * 2 + by;
                    int ctx = (int)(((above >> abit) & 1) + ((leftNz >> lbit) & 1));
                    bool nz = PutBlock(writer, counts, levels.Slice(b * 16, 16), 0, 2, ctx);
                    above = (above & ~(1u << abit)) | (nz ? 1u << abit : 0);
                    leftNz = (leftNz & ~(1u << lbit)) | (nz ? 1u << lbit : 0);
                }
            }
        }
    }

    /// <summary>One block's tokens (RFC 6386 Section 13.2); returns whether it has a non-zero level.</summary>
    private bool PutBlock(Vp8BoolEncoder? w, uint[]? counts, ReadOnlySpan<short> levels, int start, int type, int ctx)
    {
        int last = LastNonZero(levels, start);
        ReadOnlySpan<byte> bands = Vp8Tables.CoefficientBands;
        int row = ((type * 8 + bands[start]) * 3 + ctx) * 11;
        if (last < 0)
        {
            Node(w, counts, row, 0, 0);
            return false;
        }

        bool afterZero = false;
        for (int i = start; i <= last; i++)
        {
            row = ((type * 8 + bands[i]) * 3 + ctx) * 11;
            if (!afterZero)
            {
                Node(w, counts, row, 0, 1);
            }

            int level = levels[i];
            int magnitude = Math.Abs(level);
            if (magnitude == 0)
            {
                Node(w, counts, row, 1, 0);
                ctx = 0;
                afterZero = true;
                continue;
            }

            Node(w, counts, row, 1, 1);
            int cls = Vp8EncoderCosts.ClassOf[magnitude];
            if (cls == 1)
            {
                Node(w, counts, row, 2, 0);
            }
            else
            {
                Node(w, counts, row, 2, 1);
                if (cls <= 4)
                {
                    Node(w, counts, row, 3, 0);
                    if (cls == 2)
                    {
                        Node(w, counts, row, 4, 0);
                    }
                    else
                    {
                        Node(w, counts, row, 4, 1);
                        Node(w, counts, row, 5, cls == 3 ? 0 : 1);
                    }
                }
                else
                {
                    Node(w, counts, row, 3, 1);
                    if (cls <= 6)
                    {
                        Node(w, counts, row, 6, 0);
                        Node(w, counts, row, 7, cls == 5 ? 0 : 1);
                    }
                    else
                    {
                        Node(w, counts, row, 6, 1);
                        if (cls <= 8)
                        {
                            Node(w, counts, row, 8, 0);
                            Node(w, counts, row, 9, cls == 7 ? 0 : 1);
                        }
                        else
                        {
                            Node(w, counts, row, 8, 1);
                            Node(w, counts, row, 10, cls == 9 ? 0 : 1);
                        }
                    }

                    if (w is not null)
                    {
                        byte[] p = Vp8EncoderCosts.CategoryProbabilities[cls - 5];
                        int extra = magnitude - Vp8EncoderCosts.ClassBase[cls];
                        int bits = p.Length - 1;
                        for (int k = 0; k < bits; k++)
                        {
                            w.Put((extra >> (bits - 1 - k)) & 1, p[k]);
                        }
                    }
                }
            }

            w?.PutBit(level < 0 ? 1 : 0);
            ctx = magnitude == 1 ? 1 : 2;
            afterZero = false;
        }

        if (last < 15)
        {
            row = ((type * 8 + bands[last + 1]) * 3 + ctx) * 11;
            Node(w, counts, row, 0, 0);
        }

        return true;
    }

    private void Node(Vp8BoolEncoder? w, uint[]? counts, int row, int node, int bit)
    {
        if (w is not null)
        {
            w.Put(bit, _probabilities[row + node]);
        }
        else
        {
            counts![(row + node) * 2 + bit]++;
        }
    }
}
