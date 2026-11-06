// Compiled into each package that encodes a palette (GIF, BMP's 1/4/8-bit modes, PNG's lossy palette mode), under
// that package's namespace so the copies are distinct types: shipped packages never share internals.
#if SIMDCODECS_GIF
namespace Sdcb.SimdCodecs.Gif;
#elif SIMDCODECS_BMP
namespace Sdcb.SimdCodecs.Bmp;
#elif SIMDCODECS_PNG
namespace Sdcb.SimdCodecs.Png;
#else
#error PaletteQuantizer.cs is shared source for Sdcb.SimdCodecs.Gif, Sdcb.SimdCodecs.Bmp and Sdcb.SimdCodecs.Png only.
#endif

/// <summary>
/// Reduces colours to a palette, for the palette encoders.
/// <para>
/// Colours are compared in a perceptual space: OKLab (B. Ottosson, "A perceptual color space for image processing",
/// 2020) of the sRGB colour (IEC 61966-2-1), premultiplied by alpha, with alpha as a fourth coordinate, so
/// (L·α, a·α, b·α, α). A fully transparent pixel is the origin whatever its colour, and a faint one barely counts.
/// </para>
/// <para>
/// The palette is built in three steps. A histogram counts each distinct colour; past <see cref="MaxDistinct"/>
/// colours it merges them into coarser bins (dropping low bits), so its memory does not grow with the image. The
/// first palette comes from a variance-minimising box split over the histogram's colours (the box of largest
/// squared error is cut, along its widest coordinate, where the two halves' squared errors sum least: the
/// criterion of X. Wu, "Efficient Statistical Computations for Optimal Color Quantization", Graphics Gems II,
/// 1991, applied to the colours themselves rather than to a coarse grid, in the median-cut framework of
/// P. Heckbert, "Color Image Quantization for Frame Buffer Display", SIGGRAPH 1982). Then Lloyd's k-means
/// iterations (S. P. Lloyd, "Least squares quantization in PCM", 1982) move each entry to the weighted mean of the
/// colours nearest it, until the error stops falling.
/// </para>
/// <para>
/// Pixels map to the nearest entry; with dithering, Floyd–Steinberg error diffusion (R. W. Floyd and L. Steinberg,
/// 1976) carries each pixel's error to its unvisited neighbours.
/// </para>
/// <para>
/// Everything is sequential IEEE arithmetic with no transcendental functions on the path from pixels to palette
/// (the cube root is Newton's iteration, the sRGB curve a table), so the output is the same on every machine.
/// </para>
/// </summary>
internal sealed class PaletteQuantizer
{
    /// <summary>The most distinct colours the histogram keeps before merging them into coarser bins.</summary>
    internal const int MaxDistinct = 1 << 18;

    /// <summary>Whether a 0xAARRGGBB pixel counts as transparent in an opaque palette: alpha below 128.</summary>
    internal static bool IsTransparent(int argb) => (uint)argb < 0x80000000u;

    private readonly bool _alpha;
    private readonly Histogram _histogram;

    /// <summary>
    /// A quantizer for opaque colours (<paramref name="alpha"/> false: <see cref="Add"/> ignores the alpha bits and
    /// <see cref="BuildPalette"/> returns 0xRRGGBB), or for RGBA colours (true: both are 0xAARRGGBB).
    /// </summary>
    public PaletteQuantizer(bool alpha = false, int maxDistinct = MaxDistinct)
    {
        _alpha = alpha;
        _histogram = new Histogram(alpha, maxDistinct);
    }

    /// <summary>Lloyd iterations after the box split; 0 keeps the box means. Stops early once the error stops falling.</summary>
    internal int Iterations { get; init; } = 16;

    /// <summary>Whether colours are compared in OKLab (true) or in premultiplied sRGB (false, for ablation).</summary>
    internal bool Perceptual { get; init; } = true;

    /// <summary>The mean squared distance, in the comparison space, of the colours added to the palette last built.</summary>
    internal double MeanSquaredError { get; private set; }

    /// <summary>Counts one colour into the histogram.</summary>
    public void Add(int argb) => _histogram.Add(_alpha ? argb : argb | unchecked((int)0xFF000000));

    /// <summary>Builds a palette of at most <paramref name="maxColors"/> entries from the colours added.</summary>
    public int[] BuildPalette(int maxColors)
    {
        var (keys, weights) = _histogram.Entries();
        var n = keys.Length;
        if (n == 0)
        {
            MeanSquaredError = 0;
            return [];
        }

        var space = new Space(Perceptual);
        var features = new float[n * 4];
        for (var i = 0; i < n; i++)
            space.Features(keys[i], features.AsSpan(i * 4, 4));

        int[] palette;
        float[] centres;
        if (n <= maxColors && !_histogram.Coarsened)
        {
            // Every colour fits: the palette is the colours themselves.
            palette = [.. keys];
            centres = features;
        }
        else
        {
            centres = Split(features, weights, maxColors);
            // Most iterations on the colours merged into at most CoarseColours groups (each at its weighted mean),
            // the last ones on every colour: nearly the error of iterating on all of them, for a fraction of the time.
            var fine = Math.Min(Iterations, FineIterations);
            if (Iterations > fine && n > CoarseColours)
            {
                var (coarse, coarseWeights) = Coarse(keys, features, weights);
                Refine(coarse, coarseWeights, centres, Iterations - fine);
            }
            else
            {
                fine = Iterations;
            }
            Refine(features, weights, centres, fine);
            palette = new int[centres.Length / 4];
            for (var k = 0; k < palette.Length; k++)
                palette[k] = space.Colour(centres.AsSpan(k * 4, 4), _alpha);
            palette = Dedupe(palette);
        }

        // The error of the palette as written (entries rounded to 8 bits).
        var written = new float[palette.Length * 4];
        for (var k = 0; k < palette.Length; k++)
            space.Features(_alpha ? palette[k] : palette[k] | unchecked((int)0xFF000000), written.AsSpan(k * 4, 4));
        var search = new NearestSearch(written);
        double error = 0, total = 0;
        for (var i = 0; i < n; i++)
        {
            search.Nearest(features.AsSpan(i * 4, 4), out var d);
            error += d * weights[i];
            total += weights[i];
        }
        MeanSquaredError = error / total;

        if (!_alpha)
        {
            for (var k = 0; k < palette.Length; k++) palette[k] &= 0xFFFFFF;
        }
        return palette;
    }

    // Distinct entries, first occurrence kept: two centres can round to the same 8-bit colour.
    private static int[] Dedupe(int[] palette)
    {
        var seen = new HashSet<int>();
        var list = new List<int>(palette.Length);
        foreach (var p in palette)
        {
            if (seen.Add(p)) list.Add(p);
        }
        return [.. list];
    }

    /// <summary>
    /// The variance-minimising box split: the box of largest squared error is cut until there are
    /// <paramref name="maxColors"/> boxes or no box can be cut. Returns the boxes' weighted means.
    /// </summary>
    private static float[] Split(float[] features, long[] weights, int maxColors)
    {
        var n = weights.Length;
        var order = new int[n];
        for (var i = 0; i < n; i++) order[i] = i;
        var boxes = new List<Box> { Measure(features, weights, order, 0, n) };
        var scratch = new int[n];
        Span<double> sum = stackalloc double[4];
        const int Buckets = 256;
        var bucketWeight = new double[Buckets];
        var bucketSum = new double[Buckets * 4];
        var bucketCount = new int[Buckets];
        while (boxes.Count < maxColors)
        {
            var pick = -1;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Error > 0 && boxes[i].Count > 1 && (pick < 0 || boxes[i].Error > boxes[pick].Error)) pick = i;
            }
            if (pick < 0) break;

            var box = boxes[pick];
            var axis = box.WidestAxis;
            var items = order.AsSpan(box.Start, box.Count);

            // The colours fall into equal-width buckets along the widest coordinate; the cut goes between buckets.
            // Colours equal along that coordinate share a bucket, so they stay together.
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            foreach (var item in items)
            {
                var v = features[item * 4 + axis];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (!(hi > lo))
            {
                boxes[pick] = box with { Error = 0 }; // every colour equal along its widest coordinate: nothing to gain
                continue;
            }
            var scale = Buckets / (hi - lo);
            Array.Clear(bucketWeight);
            Array.Clear(bucketSum);
            Array.Clear(bucketCount);
            foreach (var item in items)
            {
                var f = item * 4;
                var bucket = Bucket(features[f + axis], lo, scale);
                double wi = weights[item];
                bucketWeight[bucket] += wi;
                bucketCount[bucket]++;
                for (var c = 0; c < 4; c++) bucketSum[bucket * 4 + c] += features[f + c] * wi;
            }

            // The cut leaving the least squared error: the total is fixed, so the one keeping the most of
            // Σ|sum|² / weight in the two halves.
            sum.Clear();
            double w = 0, best = double.NegativeInfinity;
            int cut = -1, lowerCount = 0, cutCount = 0;
            for (var bucket = 0; bucket < Buckets - 1; bucket++)
            {
                if (bucketCount[bucket] == 0) continue;
                w += bucketWeight[bucket];
                lowerCount += bucketCount[bucket];
                for (var c = 0; c < 4; c++) sum[c] += bucketSum[bucket * 4 + c];
                if (lowerCount == items.Length) break;
                double wb = box.Weight - w, lower = 0, upper = 0;
                for (var c = 0; c < 4; c++)
                {
                    lower += sum[c] * sum[c];
                    var rest = box.Sum(c) - sum[c];
                    upper += rest * rest;
                }
                var score = lower / w + upper / wb;
                if (score > best)
                {
                    best = score;
                    cut = bucket;
                    cutCount = lowerCount;
                }
            }

            // Stable partition: buckets up to the cut first, the rest after, each in its old order.
            int a = 0, b = cutCount;
            var tmp = scratch.AsSpan(0, items.Length);
            foreach (var item in items)
            {
                if (Bucket(features[item * 4 + axis], lo, scale) <= cut) tmp[a++] = item;
                else tmp[b++] = item;
            }
            tmp.CopyTo(items);
            boxes[pick] = Measure(features, weights, order, box.Start, cutCount);
            boxes.Add(Measure(features, weights, order, box.Start + cutCount, box.Count - cutCount));
        }

        var centres = new float[boxes.Count * 4];
        for (var k = 0; k < boxes.Count; k++)
        {
            for (var c = 0; c < 4; c++) centres[k * 4 + c] = (float)(boxes[k].Sum(c) / boxes[k].Weight);
        }
        return centres;
    }

    private static int Bucket(float v, float lo, float scale) => Math.Min((int)((v - lo) * scale), 255);

    private static Box Measure(float[] features, long[] weights, int[] order, int start, int count)
    {
        double w = 0, s0 = 0, s1 = 0, s2 = 0, s3 = 0, q0 = 0, q1 = 0, q2 = 0, q3 = 0;
        for (var i = start; i < start + count; i++)
        {
            var f = order[i] * 4;
            double wi = weights[order[i]];
            float x0 = features[f], x1 = features[f + 1], x2 = features[f + 2], x3 = features[f + 3];
            w += wi;
            s0 += x0 * wi; s1 += x1 * wi; s2 += x2 * wi; s3 += x3 * wi;
            q0 += x0 * x0 * wi; q1 += x1 * x1 * wi; q2 += x2 * x2 * wi; q3 += x3 * x3 * wi;
        }
        double v0 = q0 - s0 * s0 / w, v1 = q1 - s1 * s1 / w, v2 = q2 - s2 * s2 / w, v3 = q3 - s3 * s3 / w;
        var axis = 0;
        var widest = v0;
        if (v1 > widest) (axis, widest) = (1, v1);
        if (v2 > widest) (axis, widest) = (2, v2);
        if (v3 > widest) axis = 3;
        return new Box(start, count, w, s0, s1, s2, s3, Math.Max(0, v0 + v1 + v2 + v3), axis);
    }

    /// <summary>A run of <c>order</c>: its weight, per-coordinate weighted sums, squared error and widest coordinate.</summary>
    private readonly record struct Box(int Start, int Count, double Weight, double S0, double S1, double S2, double S3, double Error, int WidestAxis)
    {
        public double Sum(int c) => c switch { 0 => S0, 1 => S1, 2 => S2, _ => S3 };
    }

    /// <summary>Lloyd's iterations: each centre to the weighted mean of the colours nearest it, while the error falls.</summary>
    private const int CoarseColours = 1 << 15, FineIterations = 2;

    /// <summary>
    /// The colours merged into at most <see cref="CoarseColours"/> groups by dropping low bits of every channel; each
    /// group is the weighted mean of its colours' coordinates, with their total weight.
    /// </summary>
    private static (float[] Features, long[] Weights) Coarse(int[] keys, float[] features, long[] weights)
    {
        var n = keys.Length;
        var group = new int[n];
        var order = new int[n];
        for (var shift = 1; ; shift++)
        {
            var mask = (0xFF << shift) & 0xFF;
            var keep = mask << 24 | mask << 16 | mask << 8 | mask;
            for (var i = 0; i < n; i++)
            {
                group[i] = keys[i] & keep;
                order[i] = i;
            }
            Array.Sort(group, order);
            var groups = 1;
            for (var i = 1; i < n; i++)
            {
                if (group[i] != group[i - 1]) groups++;
            }
            if (groups > CoarseColours && shift < 7) continue;

            var merged = new float[groups * 4];
            var mergedWeights = new long[groups];
            Span<double> sum = stackalloc double[4];
            for (int i = 0, g = 0; i < n; g++)
            {
                sum.Clear();
                long w = 0;
                var j = i;
                for (; j < n && group[j] == group[i]; j++)
                {
                    var f = order[j] * 4;
                    long wj = weights[order[j]];
                    w += wj;
                    for (var c = 0; c < 4; c++) sum[c] += features[f + c] * (double)wj;
                }
                for (var c = 0; c < 4; c++) merged[g * 4 + c] = (float)(sum[c] / w);
                mergedWeights[g] = w;
                i = j;
            }
            return (merged, mergedWeights);
        }
    }

    private static void Refine(float[] features, long[] weights, float[] centres, int iterations)
    {
        var k = centres.Length / 4;
        var n = weights.Length;
        var sums = new double[k * 4];
        var mass = new double[k];
        var previous = double.PositiveInfinity;
        var assigned = new int[n];
        var separation = new float[k];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(sums);
            Array.Clear(mass);
            var search = new NearestSearch(centres);
            // Each centre's squared distance to its nearest other centre. A colour closer to its centre than half
            // that distance has no nearer centre (the triangle inequality, as in G. Hamerly, "Making k-means even
            // faster", SDM 2010), so it skips the search.
            for (var j = 0; j < k; j++)
            {
                var nearest = float.PositiveInfinity;
                for (var m = 0; m < k; m++)
                {
                    if (m != j) nearest = Math.Min(nearest, Distance(centres.AsSpan(j * 4, 4), centres.AsSpan(m * 4, 4)));
                }
                separation[j] = nearest;
            }
            double error = 0;
            for (var i = 0; i < n; i++)
            {
                var f = features.AsSpan(i * 4, 4);
                int j;
                float d;
                if (iteration > 0 && 4 * (d = Distance(f, centres.AsSpan(assigned[i] * 4, 4))) < separation[assigned[i]])
                {
                    j = assigned[i];
                }
                else
                {
                    j = search.Nearest(f, out d, iteration == 0 ? -1 : assigned[i]);
                }
                assigned[i] = j;
                double w = weights[i];
                error += d * w;
                mass[j] += w;
                for (var c = 0; c < 4; c++) sums[j * 4 + c] += f[c] * w;
            }

            // An entry no colour is nearest keeps its place.
            for (var j = 0; j < k; j++)
            {
                if (mass[j] == 0) continue;
                for (var c = 0; c < 4; c++) centres[j * 4 + c] = (float)(sums[j * 4 + c] / mass[j]);
            }

            if (error >= previous * (1 - 1e-4)) break;
            previous = error;
        }
    }

    private static float Distance(ReadOnlySpan<float> x, ReadOnlySpan<float> y)
    {
        float d0 = x[0] - y[0], d1 = x[1] - y[1], d2 = x[2] - y[2], d3 = x[3] - y[3];
        return d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
    }

    /// <summary>
    /// The comparison space: a pixel to its four coordinates and back. OKLab per Ottosson's published matrices; the
    /// sRGB curve both ways through a 256-entry table of linear values.
    /// </summary>
    private readonly struct Space(bool perceptual)
    {
        // sRGB code → linear light (IEC 61966-2-1). Math.Pow in double rounded to float: the same on every machine
        // unless a value sat within 2⁻⁵³ of a float rounding boundary, which none of these does.
        private static readonly float[] Linear = BuildLinear();

        private static float[] BuildLinear()
        {
            var t = new float[256];
            for (var v = 0; v < 256; v++)
            {
                var c = v / 255.0;
                t[v] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
            }
            return t;
        }

        public void Features(int argb, Span<float> f)
        {
            var a = ((argb >> 24) & 255) / 255f;
            int r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
            if (!perceptual)
            {
                f[0] = r / 255f * a;
                f[1] = g / 255f * a;
                f[2] = b / 255f * a;
                f[3] = a;
                return;
            }
            float lr = Linear[r], lg = Linear[g], lb = Linear[b];
            var l = Cbrt(0.4122214708f * lr + 0.5363325363f * lg + 0.0514459929f * lb);
            var m = Cbrt(0.2119034982f * lr + 0.6806995451f * lg + 0.1073969566f * lb);
            var s = Cbrt(0.0883024619f * lr + 0.2817188376f * lg + 0.6299787005f * lb);
            f[0] = (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s) * a;
            f[1] = (1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s) * a;
            f[2] = (0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s) * a;
            f[3] = a;
        }

        /// <summary>The 8-bit colour nearest a point: alpha rounded, colour un-premultiplied, each channel to the nearest code.</summary>
        public int Colour(ReadOnlySpan<float> f, bool alpha)
        {
            var a8 = alpha ? Math.Clamp((int)(f[3] * 255 + 0.5f), 0, 255) : 255;
            if (a8 == 0) return 0;
            var a = alpha ? f[3] : 1f;
            if (a <= 0) a = 1 / 255f;
            float r, g, b;
            if (!perceptual)
            {
                r = f[0] / a; g = f[1] / a; b = f[2] / a;
                return a8 << 24 | Code(r) << 16 | Code(g) << 8 | Code(b);
            }
            float lab0 = f[0] / a, lab1 = f[1] / a, lab2 = f[2] / a;
            var l = lab0 + 0.3963377774f * lab1 + 0.2158037573f * lab2;
            var m = lab0 - 0.1055613458f * lab1 - 0.0638541728f * lab2;
            var s = lab0 - 0.0894841775f * lab1 - 1.2914855480f * lab2;
            l = l * l * l; m = m * m * m; s = s * s * s;
            r = 4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
            g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
            b = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;
            return a8 << 24 | LinearCode(r) << 16 | LinearCode(g) << 8 | LinearCode(b);
        }

        private static int Code(float v) => Math.Clamp((int)(v * 255 + 0.5f), 0, 255);

        // The sRGB code whose linear value is nearest: a binary search of the table.
        private static int LinearCode(float v)
        {
            if (!(v > 0)) return 0;
            if (v >= 1) return 255;
            int lo = 0, hi = 255;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) >> 1;
                if (Linear[mid] <= v) lo = mid; else hi = mid;
            }
            return v - Linear[lo] <= Linear[hi] - v ? lo : hi;
        }

        // Cube root of x ≥ 0 by Newton's method from an exponent-thirding first guess: four steps reach float precision.
        private static float Cbrt(float x)
        {
            if (!(x > 0)) return 0;
            var y = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) / 3 + 0x2A514067);
            for (var i = 0; i < 4; i++) y -= (y * y * y - x) / (3 * y * y);
            return y;
        }
    }

    /// <summary>
    /// Nearest-centre search: centres sorted by the first coordinate (lightness), searched outward from the query's
    /// place in that order; one side stops once its first-coordinate gap alone is no nearer than the best found,
    /// since the full distance is never smaller. The answer is the nearest, the lowest index among equals.
    /// </summary>
    private sealed class NearestSearch
    {
        private readonly float[] _sorted; // centres in lightness order, four coordinates each
        private readonly int[] _index;    // their original indices
        private readonly float[] _first;  // their first coordinates, for the binary search
        private readonly int[] _position; // original index → place in the sorted order

        public NearestSearch(float[] centres)
        {
            var k = centres.Length / 4;
            _index = new int[k];
            _first = new float[k];
            for (var i = 0; i < k; i++)
            {
                _index[i] = i;
                _first[i] = centres[i * 4];
            }
            // Stable: equal lightness stays in index order.
            Array.Sort(_first, _index);
            var stable = _index.Select((original, pos) => (original, pos)).OrderBy(p => _first[p.pos]).ThenBy(p => p.original).Select(p => p.original).ToArray();
            _index = stable;
            _sorted = new float[k * 4];
            _position = new int[k];
            for (var i = 0; i < k; i++)
            {
                _position[_index[i]] = i;
                _first[i] = centres[_index[i] * 4];
                centres.AsSpan(_index[i] * 4, 4).CopyTo(_sorted.AsSpan(i * 4, 4));
            }
        }

        public int Count => _index.Length;

        /// <summary>The nearest centre; <paramref name="hint"/>, a centre likely near (or −1), only tightens the bound early.</summary>
        public int Nearest(ReadOnlySpan<float> q, out float distance, int hint = -1)
        {
            var k = _index.Length;
            float q0 = q[0], q1 = q[1], q2 = q[2], q3 = q[3];
            int lo = 0, hi = k;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (_first[mid] < q0) lo = mid + 1; else hi = mid;
            }
            int up = lo, down = lo - 1, best = -1;
            var bestDistance = float.PositiveInfinity;
            if (hint >= 0) Consider(_position[hint]);
            while (up < k || down >= 0)
            {
                if (up < k)
                {
                    var g = _first[up] - q0;
                    if (g * g > bestDistance) up = k;
                    else
                    {
                        Consider(up);
                        up++;
                    }
                }
                if (down >= 0)
                {
                    var g = q0 - _first[down];
                    if (g * g > bestDistance) down = -1;
                    else
                    {
                        Consider(down);
                        down--;
                    }
                }
            }
            distance = bestDistance;
            return _index[best];

            void Consider(int i)
            {
                var o = i * 4;
                float d0 = _sorted[o] - q0, d1 = _sorted[o + 1] - q1, d2 = _sorted[o + 2] - q2, d3 = _sorted[o + 3] - q3;
                var d = d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
                if (d < bestDistance || (d == bestDistance && _index[i] < _index[best]))
                {
                    bestDistance = d;
                    best = i;
                }
            }
        }

        /// <summary>The same answer by testing every centre: the reference the pruned search is checked against.</summary>
        public int NearestFull(ReadOnlySpan<float> q, out float distance)
        {
            var best = -1;
            var bestDistance = float.PositiveInfinity;
            for (var i = 0; i < _index.Length; i++)
            {
                var o = i * 4;
                float d0 = _sorted[o] - q[0], d1 = _sorted[o + 1] - q[1], d2 = _sorted[o + 2] - q[2], d3 = _sorted[o + 3] - q[3];
                var d = d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
                if (d < bestDistance || (d == bestDistance && _index[i] < _index[best]))
                {
                    bestDistance = d;
                    best = i;
                }
            }
            distance = bestDistance;
            return _index[best];
        }
    }

    /// <summary>
    /// Distinct-colour counts in an open-addressed table. Fully transparent colours count as one (0). When the
    /// colours exceed <see cref="MaxDistinct"/>, every key drops its lowest remaining bit per channel (alpha keeps
    /// 0 and 255 exact and loses at most two bits) and equal keys merge; a coarsened key stands for the centre of its bin.
    /// </summary>
    private sealed class Histogram(bool alpha, int maxDistinct)
    {
        private readonly int _capacity = maxDistinct * 2; // a load factor of at most one half
        private readonly int[] _keys = new int[maxDistinct * 2];
        private readonly long[] _counts = new long[maxDistinct * 2];
        private int _distinct, _shift, _last = 1, _lastSlot = -1;

        public bool Coarsened => _shift > 0;

        public void Add(int argb)
        {
            if (argb == _last && _lastSlot >= 0)
            {
                _counts[_lastSlot]++;
                return;
            }
            _last = argb;
            var key = Key(argb);
            var slot = Slot(key);
            if (_counts[slot] == 0)
            {
                _keys[slot] = key;
                if (++_distinct > maxDistinct)
                {
                    _counts[slot] = 1;
                    Coarsen();
                    _lastSlot = -1;
                    return;
                }
            }
            _counts[slot]++;
            _lastSlot = slot;
        }

        private int Key(int argb)
        {
            if (alpha && (uint)argb < 0x01000000u) return 0;
            if (_shift == 0) return argb;
            var mask = (0xFF << _shift) & 0xFF;
            var colour = argb & (mask << 16 | mask << 8 | mask);
            var a = (argb >> 24) & 255;
            if (a != 255 && a != 0)
            {
                var alphaShift = Math.Min(_shift, 2);
                a &= (0xFF << alphaShift) & 0xFF;
                if (a == 0) a = 1 << (alphaShift - 1);
            }
            return a << 24 | colour;
        }

        private int Slot(int key)
        {
            var slot = (int)((uint)(key * -1640531535) >> 13) & (_capacity - 1);
            while (_counts[slot] != 0 && _keys[slot] != key) slot = (slot + 1) & (_capacity - 1);
            return slot;
        }

        private void Coarsen()
        {
            while (_distinct > maxDistinct / 2)
            {
                _shift++;
                var keys = (int[])_keys.Clone();
                var counts = (long[])_counts.Clone();
                Array.Clear(_counts);
                _distinct = 0;
                for (var i = 0; i < _capacity; i++)
                {
                    if (counts[i] == 0) continue;
                    var key = Key(keys[i]);
                    var slot = Slot(key);
                    if (_counts[slot] == 0)
                    {
                        _keys[slot] = key;
                        _distinct++;
                    }
                    _counts[slot] += counts[i];
                }
            }
        }

        /// <summary>The colours (bin centres once coarsened) and their counts, in key order.</summary>
        public (int[] Keys, long[] Weights) Entries()
        {
            var keys = new int[_distinct];
            var weights = new long[_distinct];
            var n = 0;
            for (var i = 0; i < _capacity; i++)
            {
                if (_counts[i] == 0) continue;
                keys[n] = _keys[i];
                weights[n++] = _counts[i];
            }
            // Key order, not slot order, so the result does not depend on the table's layout.
            Array.Sort(keys, weights, Comparer<int>.Create((x, y) => ((uint)x).CompareTo((uint)y)));
            if (_shift > 0)
            {
                var half = 1 << (_shift - 1);
                for (var i = 0; i < n; i++)
                {
                    var k = keys[i];
                    if (k == 0 && alpha) continue;
                    int a = (k >> 24) & 255, r = ((k >> 16) & 255) + half, g = ((k >> 8) & 255) + half, b = (k & 255) + half;
                    if (a != 255 && a != 0) a = Math.Min(254, a + (1 << (Math.Min(_shift, 2) - 1)));
                    keys[i] = a << 24 | Math.Min(r, 255) << 16 | Math.Min(g, 255) << 8 | Math.Min(b, 255);
                }
            }
            return (keys, weights);
        }
    }

    /// <summary>
    /// Maps pixels to their nearest palette entries, caching each colour's answer. Pixels are 0xAARRGGBB. With an
    /// opaque palette (0xRRGGBB entries) transparent pixels (<see cref="IsTransparent"/>) take the transparent index
    /// given to <see cref="Map"/>; with an RGBA palette every pixel maps to an entry.
    /// </summary>
    internal sealed class Mapper
    {
        // A direct-mapped cache: most images reuse few colours, so most lookups skip the search.
        private readonly int[] _cacheKey = new int[1 << 16];
        private readonly byte[] _cacheIndex = new byte[1 << 16];
        private readonly int[] _palette;
        private readonly bool _alpha;
        private readonly Space _space;
        private readonly NearestSearch _search;

        public Mapper(int[] palette, bool alpha = false, bool perceptual = true)
        {
            _palette = alpha ? palette : [.. palette.Select(p => p | unchecked((int)0xFF000000))];
            _alpha = alpha;
            _space = new Space(perceptual);
            var features = new float[palette.Length * 4];
            for (var k = 0; k < palette.Length; k++)
                _space.Features(_palette[k], features.AsSpan(k * 4, 4));
            _search = new NearestSearch(features);
        }

        /// <summary>The nearest entry to a 0xAARRGGBB colour (alpha ignored for an opaque palette).</summary>
        public byte Nearest(int argb)
        {
            if (!_alpha) argb |= unchecked((int)0xFF000000);
            else if ((uint)argb < 0x01000000u) argb = 0;
            var slot = (int)((uint)(argb * -1640531535) >> 16);
            if (_cacheKey[slot] == argb + 1 && argb != -1) return _cacheIndex[slot];
            Span<float> f = stackalloc float[4];
            _space.Features(argb, f);
            var best = (byte)_search.Nearest(f, out _);
            _cacheKey[slot] = argb + 1;
            _cacheIndex[slot] = best;
            return best;
        }

        /// <summary>The nearest entry by testing every one: the reference for <see cref="Nearest"/>.</summary>
        internal byte NearestFull(int argb)
        {
            if (!_alpha) argb |= unchecked((int)0xFF000000);
            else if ((uint)argb < 0x01000000u) argb = 0;
            Span<float> f = stackalloc float[4];
            _space.Features(argb, f);
            return (byte)_search.NearestFull(f, out _);
        }

        /// <summary>Maps one frame, optionally with Floyd–Steinberg dithering; transparent pixels are skipped by the diffusion.</summary>
        public void Map(ReadOnlySpan<int> argb, int width, int height, Span<byte> indices, int transparentIndex, bool dither)
        {
            if (!dither)
            {
                for (var i = 0; i < argb.Length; i++)
                    indices[i] = !_alpha && IsTransparent(argb[i]) ? (byte)transparentIndex : Nearest(argb[i]);
                return;
            }

            // Errors in 256ths (Floyd–Steinberg's sixteenths times the strength's sixteenths), for this row and the
            // next, with a pixel of margin at each end; four channels.
            var current = new int[(width + 2) * 4];
            var below = new int[(width + 2) * 4];
            for (var y = 0; y < height; y++)
            {
                // Serpentine: odd rows run right to left, with the kernel mirrored (R. Ulichney, Digital Halftoning, 1987).
                var step = Serpentine && (y & 1) == 1 ? -1 : 1;
                for (int n = 0, x = step > 0 ? 0 : width - 1; n < width; n++, x += step)
                {
                    var i = y * width + x;
                    var p = argb[i];
                    if (!_alpha && IsTransparent(p))
                    {
                        indices[i] = (byte)transparentIndex;
                        continue;
                    }
                    var e = (x + 1) * 4;
                    int a = _alpha ? Clamp(((p >> 24) & 255) + Round256(current[e + 3])) : 255;
                    // A fully transparent pixel has no colour to correct, and passes no error on.
                    if (a == 0 && ((p >> 24) & 255) == 0)
                    {
                        indices[i] = Nearest(0);
                        continue;
                    }
                    int r = Clamp(((p >> 16) & 255) + Round256(current[e])),
                        g = Clamp(((p >> 8) & 255) + Round256(current[e + 1])),
                        b = Clamp((p & 255) + Round256(current[e + 2]));
                    var index = Nearest(a << 24 | r << 16 | g << 8 | b);
                    indices[i] = index;
                    var strength = Strength(argb, width, height, x, y);
                    if (strength == 0) continue;
                    var q = _palette[index];
                    var d = step * 4;
                    Spread(current, below, e, d, (r - ((q >> 16) & 255)) * strength, 0);
                    Spread(current, below, e, d, (g - ((q >> 8) & 255)) * strength, 1);
                    Spread(current, below, e, d, (b - (q & 255)) * strength, 2);
                    if (_alpha) Spread(current, below, e, d, (a - ((q >> 24) & 255)) * strength, 3);
                }
                (current, below) = (below, current);
                Array.Clear(below);
            }
        }

        /// <summary>Run odd rows right to left (for ablation; on by default).</summary>
        internal bool Serpentine { get; init; } = true;

        /// <summary>The share of each pixel's error passed on, in sixteenths.</summary>
        internal int Diffusion { get; init; } = 12;

        /// <summary>
        /// Source contrast (largest luma step to a 4-neighbour, 0–255) at which no error is passed on; diffusion
        /// falls linearly to it from full on flat areas, where dithering hides banding, to none on edges and texture,
        /// where the quantization error is masked and the dither noise would only cost. 0 turns this off.
        /// </summary>
        internal int EdgeLimit { get; init; } = 32;

        // The pixel's diffusion strength in sixteenths.
        private int Strength(ReadOnlySpan<int> argb, int width, int height, int x, int y)
        {
            if (EdgeLimit == 0) return Diffusion;
            var i = y * width + x;
            var l = Luma(argb[i]);
            var contrast = 0;
            if (x > 0) contrast = Math.Max(contrast, Math.Abs(l - Luma(argb[i - 1])));
            if (x + 1 < width) contrast = Math.Max(contrast, Math.Abs(l - Luma(argb[i + 1])));
            if (y > 0) contrast = Math.Max(contrast, Math.Abs(l - Luma(argb[i - width])));
            if (y + 1 < height) contrast = Math.Max(contrast, Math.Abs(l - Luma(argb[i + width])));
            return contrast >= EdgeLimit ? 0 : Diffusion * (EdgeLimit - contrast) / EdgeLimit;
        }

        private static int Luma(int argb) => (((argb >> 16) & 255) * 77 + ((argb >> 8) & 255) * 150 + (argb & 255) * 29) >> 8;

        // 7/16 ahead, 3/16 below-behind, 5/16 below, 1/16 below-ahead; d is ±4 (one pixel in the scan direction).
        private static void Spread(int[] current, int[] below, int e, int d, int error, int channel)
        {
            current[e + d + channel] += error * 7;
            below[e - d + channel] += error * 3;
            below[e + channel] += error * 5;
            below[e + d + channel] += error;
        }

        private static int Round256(int v) => v >= 0 ? (v + 128) >> 8 : -((-v + 128) >> 8);

        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
