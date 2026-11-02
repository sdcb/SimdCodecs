using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Webp;

internal static partial class Vp8LEncoder
{
    // Declared before PlaneCodes, which is built from it (static fields initialise in order).
    private static readonly (int X, int Y)[] DistanceMap =
    {
        (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0), (1, 2), (-1, 2),
        (2, 1), (-2, 1), (2, 2), (-2, 2), (0, 3), (3, 0), (1, 3), (-1, 3),
        (3, 1), (-3, 1), (2, 3), (-2, 3), (3, 2), (-3, 2), (0, 4), (4, 0),
        (1, 4), (-1, 4), (4, 1), (-4, 1), (3, 3), (-3, 3), (2, 4), (-2, 4),
        (4, 2), (-4, 2), (0, 5), (3, 4), (-3, 4), (4, 3), (-4, 3), (5, 0),
        (1, 5), (-1, 5), (5, 1), (-5, 1), (2, 5), (-2, 5), (5, 2), (-5, 2),
        (4, 4), (-4, 4), (3, 5), (-3, 5), (5, 3), (-5, 3), (0, 6), (6, 0),
        (1, 6), (-1, 6), (6, 1), (-6, 1), (2, 6), (-2, 6), (6, 2), (-6, 2),
        (4, 5), (-4, 5), (5, 4), (-5, 4), (3, 6), (-3, 6), (6, 3), (-6, 3),
        (0, 7), (7, 0), (1, 7), (-1, 7), (5, 5), (-5, 5), (7, 1), (-7, 1),
        (4, 6), (-4, 6), (6, 4), (-6, 4), (2, 7), (-2, 7), (7, 2), (-7, 2),
        (3, 7), (-3, 7), (7, 3), (-7, 3), (5, 6), (-5, 6), (6, 5), (-6, 5),
        (8, 0), (4, 7), (-4, 7), (7, 4), (-7, 4), (8, 1), (8, 2), (6, 6),
        (-6, 6), (8, 3), (5, 7), (-5, 7), (7, 5), (-7, 5), (8, 4), (6, 7),
        (-6, 7), (7, 6), (-7, 6), (8, 5), (7, 7), (-7, 7), (8, 6), (8, 7)
    };

    /// <summary>
    /// The pixel-distance → plane-code lookup: the inverse of RFC 9649 Section 5.2.2's 120-entry
    /// (xi, yi) table, indexed by yi * 16 + xi + 7.
    /// </summary>
    private static readonly byte[] PlaneCodes = BuildPlaneCodes();

    private static byte[] BuildPlaneCodes()
    {
        var table = new byte[8 * 16];
        for (int i = 0; i < DistanceMap.Length; i++)
        {
            (int x, int y) = DistanceMap[i];
            table[y * 16 + x + 7] = (byte)(i + 1);
        }

        return table;
    }

    /// <summary>The distance code value (Section 5.2.2) for a backward distance of <paramref name="distance"/> pixels.</summary>
    internal static int DistanceToCode(int distance, int width)
    {
        int yOffset = distance / width;
        int xOffset = distance - yOffset * width;
        if (xOffset <= 8 && yOffset < 8)
        {
            int code = PlaneCodes[yOffset * 16 + xOffset + 7];
            if (code != 0)
            {
                return code;
            }
        }

        if (xOffset > width - 8 && yOffset < 7)
        {
            int code = PlaneCodes[(yOffset + 1) * 16 + xOffset - width + 7];
            if (code != 0)
            {
                return code;
            }
        }

        return distance + 120;
    }

    /// <summary>Prefix code of a length or distance code value (Section 5.2.2), with its extra bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PrefixEncode(int value, out int extraBits, out int extra)
    {
        int d = value - 1;
        if (d < 4)
        {
            extraBits = 0;
            extra = 0;
            return d;
        }

        int high = BitOperations.Log2((uint)d);
        int second = (d >> (high - 1)) & 1;
        extraBits = high - 1;
        extra = d & ((1 << extraBits) - 1);
        return 2 * high + second;
    }

    /// <summary>The color cache slot of a colour (Section 5.2.3).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CacheKey(uint argb, int bits) => (int)((0x1E35A7BDu * argb) >> (32 - bits));

    // ---------------------------------------------------------------- the entropy-coded image

    /// <summary>
    /// Writes one entropy-coded image (Section 5): the main ARGB image (<paramref name="isMain"/>,
    /// which may use a meta prefix image) or a subimage (transform data, palette, entropy image).
    /// </summary>
    private static void EncodeImageStream(ReadOnlySpan<uint> pixels, int width, int height, bool isMain,
        Vp8LEncoderConfig config, Vp8LBitWriter writer)
    {
        int n = width * height;
        var refs = new BackwardRefs(n);
        try
        {
            bool parse = (config.Ablation & Vp8LAblation.NoBackwardRefs) == 0 && config.ParsePasses > 0 && n > 1;

            // A cost-based parse follows: its first prices only need the copies' rough statistics, which
            // runs and copies from above give at a fraction of the hash-chain search.
            refs.Find(pixels, width, parse ? config with { ChainDepth = 1 } : config);
            int cacheBits = ChooseCacheBits(pixels, width, refs, 0, config.MaxCacheBits);
            bool clustered = isMain && config.MaxGroups > 1 && config.HistogramBits > 0;
            int histogramBits = clustered ? config.HistogramBits : 0;
            int tileWidth = clustered ? (width + (1 << histogramBits) - 1) >> histogramBits : 1;
            int[]? tileGroups = null;
            int groupCount = 1;
            if (clustered)
            {
                tileGroups = ClusterTiles(pixels, width, height, refs, cacheBits, histogramBits, config.MaxGroups, out groupCount);
            }

            if (parse)
            {
                // Re-parse under the prices of the previous parse, each pixel priced by the prefix codes
                // of its own tile group; the cache and the groups are chosen again after.
                for (int pass = 0; pass < config.ParsePasses; pass++)
                {
                    var groupHistograms = new Histogram[groupCount];
                    for (int g = 0; g < groupCount; g++)
                    {
                        groupHistograms[g] = new Histogram(cacheBits);
                    }

                    Accumulate(pixels, width, refs, cacheBits, histogramBits, tileWidth, tileGroups, groupHistograms);
                    if (pass == 0)
                    {
                        // The greedy parse undercounts literals (its copies took their pixels), which would
                        // price literals too high; price them as if every pixel were one, and copies by the
                        // greedy parse's length and distance statistics.
                        SeedLiterals(pixels, width, cacheBits, histogramBits, tileWidth, tileGroups, groupHistograms);
                    }

                    var models = new CostModel[groupCount];
                    for (int g = 0; g < groupCount; g++)
                    {
                        models[g] = new CostModel(groupHistograms[g]);
                    }

                    OptimalParse(pixels, width, cacheBits, models, histogramBits, tileWidth, tileGroups, config, refs);
                }

                cacheBits = ChooseCacheBits(pixels, width, refs, Math.Max(0, cacheBits - 1), Math.Min(config.MaxCacheBits, cacheBits + 1));
                if (clustered)
                {
                    tileGroups = ClusterTiles(pixels, width, height, refs, cacheBits, histogramBits, config.MaxGroups, out groupCount);
                }
            }

            if (groupCount == 1)
            {
                tileGroups = null;
                histogramBits = 0;
                tileWidth = 1;
            }

            var histograms = new Histogram[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                histograms[g] = new Histogram(cacheBits);
            }

            Accumulate(pixels, width, refs, cacheBits, histogramBits, tileWidth, tileGroups, histograms);

            if (cacheBits > 0)
            {
                writer.WriteBits(1, 1);
                writer.WriteBits((uint)cacheBits, 4);
            }
            else
            {
                writer.WriteBits(0, 1);
            }

            if (isMain)
            {
                if (tileGroups is null)
                {
                    writer.WriteBits(0, 1);
                }
                else
                {
                    int tileHeight = (height + (1 << histogramBits) - 1) >> histogramBits;
                    var entropyImage = new uint[tileWidth * tileHeight];
                    for (int t = 0; t < entropyImage.Length; t++)
                    {
                        int g = tileGroups[t];
                        entropyImage[t] = 0xFF000000u | (uint)(g >> 8) << 16 | (uint)(g & 0xFF) << 8;
                    }

                    writer.WriteBits(1, 1);
                    writer.WriteBits((uint)(histogramBits - 2), 3);
                    EncodeImageStream(entropyImage, tileWidth, tileHeight, isMain: false, config, writer);
                }
            }

            var codes = new GroupCodes[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                codes[g] = new GroupCodes(histograms[g], writer);
            }

            WriteSymbols(pixels, width, refs, cacheBits, histogramBits, tileWidth, tileGroups, codes, writer);
        }
        finally
        {
            refs.Dispose();
        }
    }

    /// <summary>
    /// Runs the symbol stream of <paramref name="refs"/> through the color cache and counts it into
    /// the histogram of each pixel's group.
    /// </summary>
    private static void Accumulate(ReadOnlySpan<uint> pixels, int width, BackwardRefs refs, int cacheBits,
        int histogramBits, int tileWidth, int[]? tileGroups, Histogram[] histograms)
    {
        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        int x = 0, y = 0, pos = 0;
        for (int t = 0; t < refs.Count; t++)
        {
            Histogram h = histograms[tileGroups is null ? 0 : tileGroups[(y >> histogramBits) * tileWidth + (x >> histogramBits)]];
            int length = refs.Length[t];
            if (length == 0)
            {
                uint argb = refs.Value[t];
                if (cache is not null)
                {
                    int key = CacheKey(argb, cacheBits);
                    if (cache[key] == argb)
                    {
                        h.Green[256 + NumLengthCodes + key]++;
                        goto Next1;
                    }

                    cache[key] = argb;
                }

                h.Green[(argb >> 8) & 0xFF]++;
                h.Red[(argb >> 16) & 0xFF]++;
                h.Blue[argb & 0xFF]++;
                h.Alpha[argb >> 24]++;
            Next1:
                pos++;
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            else
            {
                h.Green[256 + PrefixEncode(length, out _, out _)]++;
                h.Distance[PrefixEncode((int)refs.Value[t], out _, out _)]++;
                if (cache is not null)
                {
                    for (int k = 0; k < length; k++)
                    {
                        uint c = pixels[pos + k];
                        cache[CacheKey(c, cacheBits)] = c;
                    }
                }

                pos += length;
                x += length;
                if (x >= width)
                {
                    y += x / width;
                    x %= width;
                }
            }
        }
    }

    private static void WriteSymbols(ReadOnlySpan<uint> pixels, int width, BackwardRefs refs, int cacheBits,
        int histogramBits, int tileWidth, int[]? tileGroups, GroupCodes[] codes, Vp8LBitWriter writer)
    {
        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        int x = 0, y = 0, pos = 0;
        for (int t = 0; t < refs.Count; t++)
        {
            GroupCodes c = codes[tileGroups is null ? 0 : tileGroups[(y >> histogramBits) * tileWidth + (x >> histogramBits)]];
            int length = refs.Length[t];
            if (length == 0)
            {
                uint argb = refs.Value[t];
                if (cache is not null)
                {
                    int key = CacheKey(argb, cacheBits);
                    if (cache[key] == argb)
                    {
                        c.WriteGreen(writer, 256 + NumLengthCodes + key);
                        goto Next2;
                    }

                    cache[key] = argb;
                }

                c.WriteGreen(writer, (int)(argb >> 8) & 0xFF);
                writer.WriteBits(c.Codes[1][(argb >> 16) & 0xFF], c.Lengths[1][(argb >> 16) & 0xFF]);
                writer.WriteBits(c.Codes[2][argb & 0xFF], c.Lengths[2][argb & 0xFF]);
                writer.WriteBits(c.Codes[3][argb >> 24], c.Lengths[3][argb >> 24]);
            Next2:
                pos++;
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
            else
            {
                int lengthPrefix = PrefixEncode(length, out int lengthBits, out int lengthExtra);
                c.WriteGreen(writer, 256 + lengthPrefix);
                writer.WriteBits((uint)lengthExtra, lengthBits);
                int distancePrefix = PrefixEncode((int)refs.Value[t], out int distanceBits, out int distanceExtra);
                writer.WriteBits(c.Codes[4][distancePrefix], c.Lengths[4][distancePrefix]);
                writer.WriteBits((uint)distanceExtra, distanceBits);
                if (cache is not null)
                {
                    for (int k = 0; k < length; k++)
                    {
                        uint p = pixels[pos + k];
                        cache[CacheKey(p, cacheBits)] = p;
                    }
                }

                pos += length;
                x += length;
                if (x >= width)
                {
                    y += x / width;
                    x %= width;
                }
            }
        }
    }

    /// <summary>Replaces the literal and cache counts of every group with those of an all-literal parse.</summary>
    private static void SeedLiterals(ReadOnlySpan<uint> pixels, int width, int cacheBits, int histogramBits, int tileWidth,
        int[]? tileGroups, Histogram[] histograms)
    {
        foreach (Histogram g in histograms)
        {
            g.Green.AsSpan(0, 256).Clear();
            g.Green.AsSpan(256 + NumLengthCodes).Clear();
            g.Red.AsSpan().Clear();
            g.Blue.AsSpan().Clear();
            g.Alpha.AsSpan().Clear();
        }

        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint argb = pixels[i];
            Histogram h = histograms[tileGroups is null ? 0
                : tileGroups[((i / width) >> histogramBits) * tileWidth + ((i % width) >> histogramBits)]];
            if (cache is not null)
            {
                int key = CacheKey(argb, cacheBits);
                if (cache[key] == argb)
                {
                    h.Green[256 + NumLengthCodes + key]++;
                    continue;
                }

                cache[key] = argb;
            }

            h.Green[(argb >> 8) & 0xFF]++;
            h.Red[(argb >> 16) & 0xFF]++;
            h.Blue[argb & 0xFF]++;
            h.Alpha[argb >> 24]++;
        }
    }

    /// <summary>The cache size in [<paramref name="minBits"/>, <paramref name="maxBits"/>] (0 = none) whose single-histogram entropy is lowest.</summary>
    private static int ChooseCacheBits(ReadOnlySpan<uint> pixels, int width, BackwardRefs refs, int minBits, int maxBits)
    {
        int best = minBits;
        double bestBits = double.MaxValue;
        var one = new Histogram[1];
        for (int bits = minBits; bits <= maxBits; bits++)
        {
            one[0] = new Histogram(bits);
            Accumulate(pixels, width, refs, bits, 0, 1, null, one);
            double cost = one[0].Cost();
            if (cost < bestBits)
            {
                bestBits = cost;
                best = bits;
            }
        }

        return best;
    }

    /// <summary>Symbol counts of one prefix-code group: green (+ lengths + cache), red, blue, alpha, distance.</summary>
    private sealed class Histogram
    {
        public readonly uint[] Green, Red = new uint[256], Blue = new uint[256], Alpha = new uint[256], Distance = new uint[NumDistanceCodes];

        public Histogram(int cacheBits)
        {
            Green = new uint[256 + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0)];
        }

        public bool IsEmpty => Green.AsSpan().IndexOfAnyExcept(0u) < 0;

        /// <summary>Estimated bits: the Shannon entropy of each alphabet plus a rough cost of its code.</summary>
        public double Cost() =>
            Entropy.CodeBits(Green) + Entropy.CodeBits(Red) + Entropy.CodeBits(Blue) + Entropy.CodeBits(Alpha) + Entropy.CodeBits(Distance);

        public void Add(Histogram other)
        {
            AddTo(Green, other.Green);
            AddTo(Red, other.Red);
            AddTo(Blue, other.Blue);
            AddTo(Alpha, other.Alpha);
            AddTo(Distance, other.Distance);
        }

        private static void AddTo(Span<uint> a, ReadOnlySpan<uint> b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                a[i] += b[i];
            }
        }

        /// <summary>Estimated bits of the union of two histograms, without building it.</summary>
        public static double MergedCost(Histogram a, Histogram b) =>
            Entropy.MergedCodeBits(a.Green, b.Green) + Entropy.MergedCodeBits(a.Red, b.Red) +
            Entropy.MergedCodeBits(a.Blue, b.Blue) + Entropy.MergedCodeBits(a.Alpha, b.Alpha) +
            Entropy.MergedCodeBits(a.Distance, b.Distance);
    }

    /// <summary>The five prefix codes of one group, written to the stream when built.</summary>
    private sealed class GroupCodes
    {
        public readonly byte[][] Lengths = new byte[5][];
        public readonly ushort[][] Codes = new ushort[5][];

        public GroupCodes(Histogram h, Vp8LBitWriter writer)
        {
            uint[][] counts = [h.Green, h.Red, h.Blue, h.Alpha, h.Distance];
            for (int i = 0; i < 5; i++)
            {
                Lengths[i] = new byte[counts[i].Length];
                Codes[i] = new ushort[counts[i].Length];
                Vp8LHuffman.WriteCode(writer, counts[i], Lengths[i], Codes[i]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteGreen(Vp8LBitWriter writer, int symbol) => writer.WriteBits(Codes[0][symbol], Lengths[0][symbol]);
    }

    // ---------------------------------------------------------------- spatially varying codes (Section 6.2.2)

    /// <summary>
    /// Groups the tiles of 2^bits × 2^bits pixels whose symbol statistics are alike, so each group
    /// gets its own five prefix codes. Returns the group of every tile (renumbered in order of first
    /// use) and the group count.
    /// </summary>
    private static int[] ClusterTiles(ReadOnlySpan<uint> pixels, int width, int height, BackwardRefs refs, int cacheBits,
        int bits, int maxGroups, out int groupCount)
    {
        int tileWidth = (width + (1 << bits) - 1) >> bits, tileHeight = (height + (1 << bits) - 1) >> bits;
        int tiles = tileWidth * tileHeight;
        var tileHistograms = new Histogram[tiles];
        var identity = new int[tiles];
        for (int t = 0; t < tiles; t++)
        {
            tileHistograms[t] = new Histogram(cacheBits);
            identity[t] = t;
        }

        Accumulate(pixels, width, refs, cacheBits, bits, tileWidth, identity, tileHistograms);

        // 1. Coarse: bucket tiles by the entropy of their green, red and blue, merging within a bucket,
        //    so the exact stage below starts from at most a few hundred clusters.
        var clusters = new List<Histogram>();
        var members = new List<List<int>>();
        var bucketOf = new Dictionary<int, int>();
        var signatures = new (double G, double R, double B)[tiles];
        double maxG = 1e-9, maxR = 1e-9, maxB = 1e-9;
        for (int t = 0; t < tiles; t++)
        {
            Histogram h = tileHistograms[t];
            double total = Math.Max(1, h.Green.AsSpan().ToArray().Sum(c => (double)c));
            signatures[t] = (Entropy.Bits(h.Green) / total, Entropy.Bits(h.Red) / total, Entropy.Bits(h.Blue) / total);
            maxG = Math.Max(maxG, signatures[t].G);
            maxR = Math.Max(maxR, signatures[t].R);
            maxB = Math.Max(maxB, signatures[t].B);
        }

        const int Bins = 6;
        for (int t = 0; t < tiles; t++)
        {
            if (tileHistograms[t].IsEmpty)
            {
                continue;
            }

            (double g, double r, double b) = signatures[t];
            int key = (int)Math.Min(Bins - 1, g / maxG * Bins) * Bins * Bins +
                      (int)Math.Min(Bins - 1, r / maxR * Bins) * Bins + (int)Math.Min(Bins - 1, b / maxB * Bins);
            if (!bucketOf.TryGetValue(key, out int c))
            {
                c = clusters.Count;
                bucketOf[key] = c;
                clusters.Add(new Histogram(cacheBits));
                members.Add([]);
            }

            clusters[c].Add(tileHistograms[t]);
            members[c].Add(t);
        }

        // 2. Exact: repeatedly merge the pair whose union costs least relative to the two apart,
        //    while that saves bits or while there are more clusters than allowed.
        //    The pair deltas are kept in a matrix; a merge recomputes only the merged cluster's row.
        int k = clusters.Count;
        var alive = new bool[k];
        Array.Fill(alive, true);
        var cost = clusters.Select(c => c.Cost()).ToArray();
        var delta = new double[k * k];
        for (int i = 0; i < k; i++)
        {
            for (int j = i + 1; j < k; j++)
            {
                delta[i * k + j] = Histogram.MergedCost(clusters[i], clusters[j]) - cost[i] - cost[j];
            }
        }

        int liveCount = k;
        while (liveCount > 1)
        {
            double bestDelta = double.MaxValue;
            int bi = -1, bj = -1;
            for (int i = 0; i < k; i++)
            {
                if (!alive[i]) continue;
                for (int j = i + 1; j < k; j++)
                {
                    if (alive[j] && delta[i * k + j] < bestDelta)
                    {
                        bestDelta = delta[i * k + j];
                        bi = i;
                        bj = j;
                    }
                }
            }

            if (bestDelta >= 0 && liveCount <= maxGroups)
            {
                break;
            }

            clusters[bi].Add(clusters[bj]);
            members[bi].AddRange(members[bj]);
            cost[bi] = clusters[bi].Cost();
            alive[bj] = false;
            liveCount--;
            for (int j = 0; j < k; j++)
            {
                if (alive[j] && j != bi)
                {
                    double d = Histogram.MergedCost(clusters[bi], clusters[j]) - cost[bi] - cost[j];
                    delta[Math.Min(bi, j) * k + Math.Max(bi, j)] = d;
                }
            }
        }

        // Renumber in order of first use (row-major tiles), which keeps the entropy image cheap.
        var groups = new int[tiles];
        var clusterOfTile = new int[tiles];
        Array.Fill(clusterOfTile, -1);
        for (int c = 0; c < k; c++)
        {
            if (!alive[c]) continue;
            foreach (int t in members[c])
            {
                clusterOfTile[t] = c;
            }
        }

        var number = new Dictionary<int, int>();
        int previous = 0;
        for (int t = 0; t < tiles; t++)
        {
            int c = clusterOfTile[t];
            if (c < 0)
            {
                groups[t] = previous; // an empty tile (covered by a copy): any group will do
                continue;
            }

            if (!number.TryGetValue(c, out int g))
            {
                g = number.Count;
                number[c] = g;
            }

            groups[t] = previous = g;
        }

        groupCount = Math.Max(1, number.Count);
        return groups;
    }

    // ---------------------------------------------------------------- LZ77 backward references (Section 5.2.2)

    /// <summary>The image as literals and copies: <c>Length[t] == 0</c> is a literal with the colour in <c>Value[t]</c>;
    /// otherwise a copy of that many pixels with the distance code value in <c>Value[t]</c>.</summary>
    private sealed class BackwardRefs(int capacity) : IDisposable
    {
        public int[] Length = ArrayPool<int>.Shared.Rent(Math.Max(1, capacity));
        public uint[] Value = ArrayPool<uint>.Shared.Rent(Math.Max(1, capacity));
        public int Count;

        public void Find(ReadOnlySpan<uint> pixels, int width, Vp8LEncoderConfig config)
        {
            int n = pixels.Length;
            Count = 0;
            if ((config.Ablation & Vp8LAblation.NoBackwardRefs) != 0)
            {
                for (int i = 0; i < n; i++)
                {
                    AddLiteral(pixels[i]);
                }

                return;
            }

            if (n < 2 || config.ChainDepth <= 1)
            {
                FindRuns(pixels, width);
                return;
            }

            int hashBits = Math.Clamp(BitOperations.Log2((uint)n) + 1, 8, 18);
            int[] head = ArrayPool<int>.Shared.Rent(1 << hashBits);
            int[] chain = ArrayPool<int>.Shared.Rent(n);
            try
            {
                head.AsSpan(0, 1 << hashBits).Fill(-1);
                var matcher = new Matcher(pixels, width, head, chain, hashBits, config.ChainDepth);
                int i = 0;
                while (i < n)
                {
                    (int length, int distance) = matcher.Best(i);
                    if (length > 0 && config.Lazy && length < 64 && i + 1 < n)
                    {
                        matcher.Insert(i);
                        (int nextLength, _) = matcher.Best(i + 1);
                        if (nextLength > length + 1)
                        {
                            AddLiteral(pixels[i]);
                            i++;
                            continue;
                        }

                        AddCopy(length, DistanceToCode(distance, width));
                        for (int k = 1; k < length; k++)
                        {
                            matcher.Insert(i + k);
                        }

                        i += length;
                        continue;
                    }

                    if (length > 0)
                    {
                        AddCopy(length, DistanceToCode(distance, width));
                        for (int k = 0; k < length; k++)
                        {
                            matcher.Insert(i + k);
                        }

                        i += length;
                    }
                    else
                    {
                        AddLiteral(pixels[i]);
                        matcher.Insert(i);
                        i++;
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(head);
                ArrayPool<int>.Shared.Return(chain);
            }
        }

        /// <summary>Only runs (distance 1) and copies from the row above: the fastest search.</summary>
        private void FindRuns(ReadOnlySpan<uint> pixels, int width)
        {
            int n = pixels.Length;
            int i = 0;
            while (i < n)
            {
                int max = Math.Min(MaxLength, n - i);
                int run = i >= 1 ? pixels.Slice(i, max).CommonPrefixLength(pixels.Slice(i - 1, max)) : 0;
                int up = i >= width ? pixels.Slice(i, max).CommonPrefixLength(pixels.Slice(i - width, max)) : 0;
                if (Math.Max(run, up) >= 3)
                {
                    bool useUp = up >= run;
                    int length = useUp ? up : run;
                    AddCopy(length, DistanceToCode(useUp ? width : 1, width));
                    i += length;
                }
                else
                {
                    AddLiteral(pixels[i++]);
                }
            }
        }

        private void AddLiteral(uint argb)
        {
            Length[Count] = 0;
            Value[Count++] = argb;
        }

        private void AddCopy(int length, int distanceCode)
        {
            Length[Count] = length;
            Value[Count++] = (uint)distanceCode;
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(Length);
            ArrayPool<uint>.Shared.Return(Value);
            Length = [];
            Value = [];
        }
    }

    /// <summary>Hash-chain match finder over pixel pairs; the run (distance 1) and the pixel above are always tried.</summary>
    private readonly ref struct Matcher(ReadOnlySpan<uint> pixels, int width, int[] head, int[] chain, int hashBits, int depth)
    {
        private readonly ReadOnlySpan<uint> _pixels = pixels;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Hash(int i) => (int)(((_pixels[i] * 0x9E3779B1u) ^ (_pixels[i + 1] * 0x85EBCA6Bu)) >> (32 - hashBits));

        public void Insert(int i)
        {
            if (i + 1 >= _pixels.Length)
            {
                return;
            }

            int h = Hash(i);
            chain[i] = head[h];
            head[h] = i;
        }

        /// <summary>
        /// The matches at <paramref name="i"/> for a cost-based parse, in increasing length: distance 1
        /// and the pixel above first (their distance codes are the cheapest), then the hash chain nearest
        /// first, each kept only when longer than every one before it. Returns the count.
        /// </summary>
        public int Candidates(int i, int max, Span<int> lengths, Span<int> distances)
        {
            int count = 0, best = 0;
            ReadOnlySpan<uint> here = _pixels.Slice(i, max);
            if (i >= 1)
            {
                int run = here.CommonPrefixLength(_pixels.Slice(i - 1, max));
                if (run > 0)
                {
                    lengths[count] = best = run;
                    distances[count++] = 1;
                }
            }

            if (i >= width && best < max)
            {
                int up = here.CommonPrefixLength(_pixels.Slice(i - width, max));
                if (up > best)
                {
                    lengths[count] = best = up;
                    distances[count++] = width;
                }
            }

            if (max < 2 || i + 1 >= _pixels.Length)
            {
                return count;
            }

            int candidate = head[Hash(i)];
            for (int d = 0; d < depth && candidate >= 0 && best < max && count < lengths.Length; d++)
            {
                int distance = i - candidate;
                if (distance > MaxDistance)
                {
                    break;
                }

                if (_pixels[candidate + best] == here[best])
                {
                    int length = here.CommonPrefixLength(_pixels.Slice(candidate, max));
                    if (length > best)
                    {
                        lengths[count] = best = length;
                        distances[count++] = distance;
                    }
                }

                candidate = chain[candidate];
            }

            return count;
        }

        /// <summary>The longest match at <paramref name="i"/> (0 if none is worth a copy) and its distance.</summary>
        public (int Length, int Distance) Best(int i)
        {
            int n = _pixels.Length;
            int max = Math.Min(MaxLength, n - i);
            if (max < 2)
            {
                return (0, 0);
            }

            ReadOnlySpan<uint> here = _pixels.Slice(i, max);
            int bestLength = 0, bestDistance = 0;
            if (i >= 1)
            {
                bestLength = here.CommonPrefixLength(_pixels.Slice(i - 1, max));
                bestDistance = 1;
            }

            if (i >= width && bestLength < max)
            {
                int up = here.CommonPrefixLength(_pixels.Slice(i - width, max));
                if (up > bestLength)
                {
                    bestLength = up;
                    bestDistance = width;
                }
            }

            int candidate = head[Hash(i)];
            for (int d = 0; d < depth && candidate >= 0 && bestLength < max; d++)
            {
                int distance = i - candidate;
                if (distance > MaxDistance)
                {
                    break;
                }

                if (_pixels[candidate + bestLength] == here[bestLength])
                {
                    int length = here.CommonPrefixLength(_pixels.Slice(candidate, max));
                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestDistance = distance;
                    }
                }

                candidate = chain[candidate];
            }

            // A short copy with a long distance code costs more than its literals.
            int minimum = bestDistance == 1 || bestDistance == width ? 2 : 3;
            return bestLength >= minimum ? (bestLength, bestDistance) : (0, 0);
        }
    }
}

/// <summary>Entropy estimates in bits.</summary>
internal static class Entropy
{
    private static readonly float[] NLog2N = BuildTable();

    private static float[] BuildTable()
    {
        var t = new float[4096];
        for (int i = 1; i < t.Length; i++)
        {
            t[i] = (float)(i * Math.Log2(i));
        }

        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double F(ulong n) => n < 4096 ? NLog2N[n] : n * Math.Log2(n);

    /// <summary>Shannon bits to code these counts with an ideal code: N log N − Σ n log n.</summary>
    public static double Bits(ReadOnlySpan<uint> counts)
    {
        ulong total = 0;
        double sum = 0;
        foreach (uint c in counts)
        {
            if (c != 0)
            {
                total += c;
                sum += F(c);
            }
        }

        return F(total) - sum;
    }

    /// <summary><see cref="Bits"/> plus a rough cost of describing the code itself.</summary>
    public static double CodeBits(ReadOnlySpan<uint> counts)
    {
        ulong total = 0;
        double sum = 0;
        int used = 0;
        foreach (uint c in counts)
        {
            if (c != 0)
            {
                total += c;
                sum += F(c);
                used++;
            }
        }

        return F(total) - sum + HeaderBits(used);
    }

    public static double MergedCodeBits(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        ulong total = 0;
        double sum = 0;
        int used = 0;
        for (int i = 0; i < a.Length; i++)
        {
            ulong c = (ulong)a[i] + b[i];
            if (c != 0)
            {
                total += c;
                sum += F(c);
                used++;
            }
        }

        return F(total) - sum + HeaderBits(used);
    }

    private static double HeaderBits(int used) => used <= 2 ? 12 : 30 + 3.5 * used;
}
