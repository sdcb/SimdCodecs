// Compiled into each package that encodes a palette (GIF, BMP's 1/4/8-bit modes, PNG's lossy palette mode), under
// that package's namespace so the copies are distinct types: shipped packages never share internals.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
/// colours nearest it, until the error stops falling. With many colours, the split and most iterations run on the
/// colours merged into a few tens of thousands of groups, and only the last iterations on every colour.
/// </para>
/// <para>
/// Pixels map to the nearest entry; with dithering, Floyd–Steinberg error diffusion (R. W. Floyd and L. Steinberg,
/// 1976) carries each pixel's error to its unvisited neighbours.
/// </para>
/// <para>
/// Everything is sequential IEEE arithmetic with no transcendental functions on the path from pixels to palette
/// (the cube root is Halley's iteration, the sRGB curve a table), so the output is the same on every machine. The
/// vector paths (colour conversion four at a time, nearest-entry search a block of entries at a time) do the same
/// operations in the same order as their scalar twins, or find the same entry by definition (the nearest, the
/// lowest index among equals), so they change the speed only.
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

    /// <summary>Whether the palette last built is the colours added, each exactly (every pixel is its own entry).</summary>
    internal bool Exact { get; private set; }

    /// <summary>The mean squared distance, in the comparison space, of the colours added to the palette last built.</summary>
    internal double MeanSquaredError { get; private set; }

    /// <summary>Counts one colour into the histogram.</summary>
    public void Add(int argb) => _histogram.Add(_alpha ? argb : argb | unchecked((int)0xFF000000));

    /// <summary>
    /// Builds a palette of at most <paramref name="maxColors"/> entries from the colours added. With a
    /// <paramref name="targetError"/> (a mean squared distance in the comparison space) it stops adding entries once
    /// the error is down to it, so an image that needs few colours gets few; <see cref="MeanSquaredError"/> says what
    /// was reached.
    /// </summary>
    public int[] BuildPalette(int maxColors, double targetError = 0)
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
        space.Features(keys, features);

        int[] palette;
        if (n <= maxColors && !_histogram.Coarsened && targetError == 0)
        {
            // Every colour fits and every one is wanted: the palette is the colours themselves.
            palette = [.. keys];
            MeanSquaredError = 0;
            Exact = true;
        }
        else
        {
            // The box split stops at the target; k-means then lowers the error further, so the result usually meets it
            // with entries to spare. When rounding or coarsening leaves it short, split further and try again.
            ReadOnlySpan<double> stops = [1.0, 0.8, 0.6, 0];
            palette = [];
            Exact = false;
            // The merged colours depend only on the colours, so every try shares them.
            (float[] Features, long[] Weights, int[] Group)? merged = n > CoarseColours ? Coarse(keys, features, weights) : null;
            foreach (var stop in stops)
            {
                (palette, MeanSquaredError) = Build(space, keys, features, weights, merged, maxColors, targetError * stop);
                if (MeanSquaredError <= targetError || palette.Length >= maxColors) break;
            }
        }

        if (!_alpha)
        {
            for (var k = 0; k < palette.Length; k++) palette[k] &= 0xFFFFFF;
        }
        return palette;
    }

    private (int[] Palette, double Error) Build(Space space, int[] keys, float[] features, long[] weights,
        (float[] Features, long[] Weights, int[] Group)? merged, int maxColors, double stopError)
    {
        var n = keys.Length;
        // With many colours, the box split and most iterations run on the colours merged into at most CoarseColours
        // groups (each at its weighted mean), the last iterations on every colour, each starting from its group's
        // entry: nearly the error of doing it all on every colour, for a fraction of the time.
        var fine = Math.Min(Iterations, FineIterations);
        int[]? assigned = null;
        float[] centres;
        if (merged is { } m)
        {
            var (coarse, coarseWeights, group) = m;
            centres = Split(coarse, coarseWeights, maxColors, stopError);
            var coarseAssigned = Refine(coarse, coarseWeights, centres, Iterations - fine, null);
            assigned = new int[n];
            for (var i = 0; i < n; i++) assigned[i] = coarseAssigned[group[i]];
        }
        else
        {
            centres = Split(features, weights, maxColors, stopError);
            fine = Iterations;
        }
        assigned = Refine(features, weights, centres, fine, assigned);

        // Distinct entries, first occurrence kept: two centres can round to the same 8-bit colour.
        var k = centres.Length / 4;
        var toPalette = new int[k];
        var seen = new Dictionary<int, int>();
        var list = new List<int>(k);
        for (var j = 0; j < k; j++)
        {
            var colour = space.Colour(centres.AsSpan(j * 4, 4), _alpha);
            if (!seen.TryGetValue(colour, out var at))
            {
                at = list.Count;
                seen.Add(colour, at);
                list.Add(colour);
            }
            toPalette[j] = at;
        }
        int[] palette = [.. list];

        // The error of the palette as written (entries rounded to 8 bits); each colour's entry is the hint.
        var written = new float[palette.Length * 4];
        for (var j = 0; j < palette.Length; j++)
            space.Features(_alpha ? palette[j] : palette[j] | unchecked((int)0xFF000000), written.AsSpan(j * 4, 4));
        var search = new NearestSearch(written);
        double error = 0, total = 0;
        for (var i = 0; i < n; i++)
        {
            search.Nearest(features.AsSpan(i * 4, 4), out var d, toPalette[assigned[i]]);
            error += d * weights[i];
            total += weights[i];
        }
        return (palette, error / total);
    }

    /// <summary>
    /// The variance-minimising box split: the box of largest squared error is cut until there are
    /// <paramref name="maxColors"/> boxes, the mean squared error is at most <paramref name="stopError"/>, or no box can
    /// be cut. Returns the boxes' weighted means.
    /// </summary>
    private static float[] Split(float[] features, long[] weights, int maxColors, double stopError)
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
        double totalWeight = boxes[0].Weight, totalError = boxes[0].Error;
        // At least two entries when there are two colours: one colour is no palette worth writing.
        while (boxes.Count < maxColors && (boxes.Count < 2 || totalError > stopError * totalWeight))
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
                // Every colour equal along its widest coordinate: nothing to gain. Its error still counts in the total.
                boxes[pick] = box with { Error = 0 };
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
            totalError += boxes[pick].Error + boxes[^1].Error - box.Error;
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
    /// group is the weighted mean of its colours' coordinates, with their total weight. Groups are numbered in the
    /// order their first colour comes; <c>Group</c> gives each colour's.
    /// </summary>
    private static (float[] Features, long[] Weights, int[] Group) Coarse(int[] keys, float[] features, long[] weights)
    {
        var n = keys.Length;
        var group = new int[n];
        const int Capacity = CoarseColours * 2; // a load factor of at most one half
        var tableKey = new int[Capacity];
        var tableGroup = new int[Capacity];
        for (var shift = 1; ; shift++)
        {
            var mask = (0xFF << shift) & 0xFF;
            var keep = mask << 24 | mask << 16 | mask << 8 | mask;
            Array.Fill(tableGroup, -1);
            var groups = 0;
            var full = false;
            for (var i = 0; i < n && !full; i++)
            {
                var g = keys[i] & keep;
                var slot = (int)((uint)(g * -1640531535) >> 16) & (Capacity - 1);
                while (tableGroup[slot] >= 0 && tableKey[slot] != g) slot = (slot + 1) & (Capacity - 1);
                if (tableGroup[slot] < 0)
                {
                    // Past CoarseColours groups, try coarser bins; at seven bits dropped there are at most 16.
                    if (groups == CoarseColours && shift < 7)
                    {
                        full = true;
                        break;
                    }
                    tableKey[slot] = g;
                    tableGroup[slot] = groups++;
                }
                group[i] = tableGroup[slot];
            }
            if (full) continue;

            var sums = new double[groups * 4];
            var mergedWeights = new long[groups];
            for (var i = 0; i < n; i++)
            {
                int g = group[i], f = i * 4;
                long w = weights[i];
                mergedWeights[g] += w;
                for (var c = 0; c < 4; c++) sums[g * 4 + c] += features[f + c] * (double)w;
            }
            var merged = new float[groups * 4];
            for (var g = 0; g < groups; g++)
            {
                for (var c = 0; c < 4; c++) merged[g * 4 + c] = (float)(sums[g * 4 + c] / mergedWeights[g]);
            }
            return (merged, mergedWeights, group);
        }
    }

    /// <summary>
    /// Lloyd's iterations: each centre to the weighted mean of the colours nearest it, while the error falls.
    /// <paramref name="assigned"/>, each colour's entry from an earlier stage (or null), seeds the first search.
    /// Returns each colour's entry in the last assignment.
    /// </summary>
    private static int[] Refine(float[] features, long[] weights, float[] centres, int iterations, int[]? assigned)
    {
        var k = centres.Length / 4;
        var n = weights.Length;
        var sums = new double[k * 4];
        var mass = new double[k];
        var previous = double.PositiveInfinity;
        var seeded = assigned != null;
        assigned ??= new int[n];
        var separation = new float[k];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(sums);
            Array.Clear(mass);
            var search = new NearestSearch(centres);
            // Each centre's squared distance to its nearest other centre. A colour closer to its centre than half
            // that distance has no nearer centre (the triangle inequality, as in G. Hamerly, "Making k-means even
            // faster", SDM 2010), so it skips the search.
            Array.Fill(separation, float.PositiveInfinity);
            for (var j = 0; j < k; j++)
            {
                float x0 = centres[j * 4], x1 = centres[j * 4 + 1], x2 = centres[j * 4 + 2], x3 = centres[j * 4 + 3];
                var nearest = separation[j];
                for (var m = j + 1; m < k; m++)
                {
                    float d0 = x0 - centres[m * 4], d1 = x1 - centres[m * 4 + 1], d2 = x2 - centres[m * 4 + 2], d3 = x3 - centres[m * 4 + 3];
                    var d = d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
                    if (d < nearest) nearest = d;
                    if (d < separation[m]) separation[m] = d;
                }
                separation[j] = nearest;
            }
            var known = iteration > 0 || seeded;
            double error = 0;
            for (var i = 0; i < n; i++)
            {
                var f = features.AsSpan(i * 4, 4);
                int j;
                float d;
                if (known && 4 * (d = Distance(f, centres.AsSpan(assigned[i] * 4, 4))) < separation[assigned[i]])
                {
                    j = assigned[i];
                }
                else
                {
                    j = search.Nearest(f, out d, known ? assigned[i] : -1);
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
        return assigned;
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
    internal readonly struct Space(bool perceptual)
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

        /// <summary>
        /// The coordinates of many colours, four floats each: four colours at a time on the vector path, with the
        /// same operations in the same order as the scalar <see cref="Features(int, Span{float})"/>, so the same bits.
        /// </summary>
        public void Features(ReadOnlySpan<int> argb, Span<float> f, bool vector = true)
        {
            var i = 0;
            if (vector && perceptual && Vector128.IsHardwareAccelerated) i = FeaturesVector(argb, f);
            for (; i < argb.Length; i++) Features(argb[i], f.Slice(i * 4, 4));
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static int FeaturesVector(ReadOnlySpan<int> argb, Span<float> f)
        {
            var linear = Linear;
            var i = 0;
            for (; i + 4 <= argb.Length; i += 4)
            {
                int p0 = argb[i], p1 = argb[i + 1], p2 = argb[i + 2], p3 = argb[i + 3];
                var a = Vector128.Create((p0 >> 24) & 255, (p1 >> 24) & 255, (p2 >> 24) & 255, (p3 >> 24) & 255);
                var alpha = Vector128.ConvertToSingle(a) / Vector128.Create(255f);
                var lr = Vector128.Create(linear[(p0 >> 16) & 255], linear[(p1 >> 16) & 255], linear[(p2 >> 16) & 255], linear[(p3 >> 16) & 255]);
                var lg = Vector128.Create(linear[(p0 >> 8) & 255], linear[(p1 >> 8) & 255], linear[(p2 >> 8) & 255], linear[(p3 >> 8) & 255]);
                var lb = Vector128.Create(linear[p0 & 255], linear[p1 & 255], linear[p2 & 255], linear[p3 & 255]);
                var l = Cbrt(Vector128.Create(0.4122214708f) * lr + Vector128.Create(0.5363325363f) * lg + Vector128.Create(0.0514459929f) * lb);
                var m = Cbrt(Vector128.Create(0.2119034982f) * lr + Vector128.Create(0.6806995451f) * lg + Vector128.Create(0.1073969566f) * lb);
                var s = Cbrt(Vector128.Create(0.0883024619f) * lr + Vector128.Create(0.2817188376f) * lg + Vector128.Create(0.6299787005f) * lb);
                var f0 = (Vector128.Create(0.2104542553f) * l + Vector128.Create(0.7936177850f) * m - Vector128.Create(0.0040720468f) * s) * alpha;
                var f1 = (Vector128.Create(1.9779984951f) * l - Vector128.Create(2.4285922050f) * m + Vector128.Create(0.4505937099f) * s) * alpha;
                var f2 = (Vector128.Create(0.0259040371f) * l + Vector128.Create(0.7827717662f) * m - Vector128.Create(0.8086757660f) * s) * alpha;
                var o = f.Slice(i * 4, 16);
                for (var j = 0; j < 4; j++)
                {
                    o[j * 4] = f0.GetElement(j);
                    o[j * 4 + 1] = f1.GetElement(j);
                    o[j * 4 + 2] = f2.GetElement(j);
                    o[j * 4 + 3] = alpha.GetElement(j);
                }
            }
            return i;
        }

        private static Vector128<float> Cbrt(Vector128<float> x)
        {
            var guess = Vector128.ConvertToInt32(Vector128.ConvertToSingle(x.AsInt32()) * Vector128.Create(1f / 3)) + Vector128.Create(0x2A514067);
            var y = guess.AsSingle();
            for (var i = 0; i < 2; i++)
            {
                var y3 = y * y * y;
                y = y * (y3 + Vector128.Create(2f) * x) / (Vector128.Create(2f) * y3 + x);
            }
            return Vector128.ConditionalSelect(Vector128.GreaterThan(x, Vector128<float>.Zero), y, Vector128<float>.Zero);
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

        // Cube root of x ≥ 0 by Halley's method (cubic convergence) from an exponent-thirding first guess (the bits over
        // three, in float so the vector twin can do the same): two steps reach float precision.
        private static float Cbrt(float x)
        {
            if (!(x > 0)) return 0;
            var y = BitConverter.Int32BitsToSingle((int)(BitConverter.SingleToInt32Bits(x) * (1f / 3)) + 0x2A514067);
            for (var i = 0; i < 2; i++)
            {
                var y3 = y * y * y;
                y = y * (y3 + 2 * x) / (2 * y3 + x);
            }
            return y;
        }
    }

    /// <summary>
    /// Nearest-centre search: centres sorted by the first coordinate (lightness), searched outward from the query's
    /// place in that order; one side stops once its first-coordinate gap alone is no nearer than the best found,
    /// since the full distance is never smaller. The answer is the nearest, the lowest index among equals, so every
    /// way of searching (vector blocks, one centre at a time, every centre) gives the same one.
    /// <para>
    /// The centres are stored by coordinate (four arrays) in blocks of <see cref="Lanes"/>; the vector search measures
    /// a block at a time. Padding at the end of the last block is far from every colour, so it is never nearest.
    /// </para>
    /// </summary>
    private sealed class NearestSearch
    {
        private const int Lanes = 4;
        private const float Far = 1e15f; // padding: its squared distance (~1e30) exceeds any real one and stays finite

        private readonly float[] _c0, _c1, _c2, _c3; // coordinates in lightness order, padded to whole blocks
        private readonly int[] _index;               // sorted place → original index (padding: int.MaxValue)
        private readonly int[] _position;            // original index → sorted place
        private readonly int _count, _blocks;
        private readonly int[] _start = new int[Slots + 1]; // per lightness slot, the first place not below the slot (see LowerBound)

        public NearestSearch(float[] centres)
        {
            var k = centres.Length / 4;
            _count = k;
            _blocks = (k + Lanes - 1) / Lanes;
            var padded = _blocks * Lanes;
            // Stable: equal lightness stays in index order.
            var order = new int[k];
            for (var i = 0; i < k; i++) order[i] = i;
            Array.Sort(order, (x, y) => centres[x * 4] != centres[y * 4] ? centres[x * 4].CompareTo(centres[y * 4]) : x.CompareTo(y));
            _c0 = new float[padded];
            _c1 = new float[padded];
            _c2 = new float[padded];
            _c3 = new float[padded];
            _index = new int[padded];
            _position = new int[k];
            for (var i = 0; i < padded; i++)
            {
                if (i < k)
                {
                    var o = order[i] * 4;
                    (_c0[i], _c1[i], _c2[i], _c3[i]) = (centres[o], centres[o + 1], centres[o + 2], centres[o + 3]);
                    _index[i] = order[i];
                    _position[order[i]] = i;
                }
                else
                {
                    _c0[i] = Far;
                    _index[i] = int.MaxValue;
                }
            }
            // _start[s]: the first place whose slot is at least s, so every place before it is below any q0 in slot s.
            for (int slot = 0, place = 0; slot <= Slots; slot++)
            {
                while (place < k && Slot(_c0[place]) < slot) place++;
                _start[slot] = place;
            }
        }

        public int Count => _count;

        /// <summary>Whether <see cref="Nearest"/> takes the vector path on this machine.</summary>
        internal static bool Vectorised => Vector128.IsHardwareAccelerated;

        /// <summary>The nearest centre; <paramref name="hint"/>, a centre likely near (or −1), only tightens the bound early.</summary>
        public int Nearest(ReadOnlySpan<float> q, out float distance, int hint = -1) =>
            Vectorised ? NearestVector(q, out distance, hint) : NearestScalar(q, out distance, hint);

        // The first sorted place whose lightness is at least q0: a table over [0, 1] gives the first place at least
        // its slot's start, then a short walk up.
        private int LowerBound(float q0)
        {
            var at = _start[Slot(q0)];
            while (at < _count && _c0[at] < q0) at++;
            return at;
        }

        private const int Slots = 1024;

        private static int Slot(float v) => v > 0 ? (int)Math.Min(v * Slots, Slots) : 0;

        private float Distance(int i, float q0, float q1, float q2, float q3)
        {
            float d0 = _c0[i] - q0, d1 = _c1[i] - q1, d2 = _c2[i] - q2, d3 = _c3[i] - q3;
            return d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
        }

        /// <summary>One centre at a time: the scalar twin of <see cref="NearestVector"/>.</summary>
        internal int NearestScalar(ReadOnlySpan<float> q, out float distance, int hint = -1)
        {
            float q0 = q[0], q1 = q[1], q2 = q[2], q3 = q[3];
            var lo = LowerBound(q0);
            int up = lo, down = lo - 1, best = int.MaxValue;
            var bestDistance = float.PositiveInfinity;
            if (hint >= 0) Consider(_position[hint]);
            while (up < _count || down >= 0)
            {
                if (up < _count)
                {
                    var g = _c0[up] - q0;
                    if (g * g > bestDistance) up = _count;
                    else Consider(up++);
                }
                if (down >= 0)
                {
                    var g = q0 - _c0[down];
                    if (g * g > bestDistance) down = -1;
                    else Consider(down--);
                }
            }
            distance = bestDistance;
            return best;

            void Consider(int i)
            {
                var d = Distance(i, q0, q1, q2, q3);
                if (d < bestDistance || (d == bestDistance && _index[i] < best))
                {
                    bestDistance = d;
                    best = _index[i];
                }
            }
        }

        /// <summary>
        /// A block of <see cref="Lanes"/> centres at a time, outward from the block holding the query's place. A block
        /// above is skipped (with all beyond it) once its first centre's gap is too far, a block below once its last
        /// centre's is.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal int NearestVector(ReadOnlySpan<float> q, out float distance, int hint = -1)
        {
            float q0 = q[0], q1 = q[1], q2 = q[2], q3 = q[3];
            var best = int.MaxValue;
            var bestDistance = float.PositiveInfinity;
            if (hint >= 0)
            {
                bestDistance = Distance(_position[hint], q0, q1, q2, q3);
                best = hint;
            }
            var v0 = Vector128.Create(q0);
            var v1 = Vector128.Create(q1);
            var v2 = Vector128.Create(q2);
            var v3 = Vector128.Create(q3);
            // Every array holds _blocks · Lanes floats and each block below is in [0, _blocks): the loads stay inside.
            ref var c0 = ref MemoryMarshal.GetArrayDataReference(_c0);
            ref var c1 = ref MemoryMarshal.GetArrayDataReference(_c1);
            ref var c2 = ref MemoryMarshal.GetArrayDataReference(_c2);
            ref var c3 = ref MemoryMarshal.GetArrayDataReference(_c3);
            var start = Math.Min(LowerBound(q0), _count - 1) / Lanes;
            int up = start, down = start - 1;
            while (up < _blocks || down >= 0)
            {
                if (up < _blocks)
                {
                    // The first block holds the query's place and may reach below it: never skipped.
                    var g = _c0[up * Lanes] - q0;
                    if (up > start && g * g > bestDistance) up = _blocks;
                    else Block(ref c0, ref c1, ref c2, ref c3, up++);
                }
                if (down >= 0)
                {
                    var g = q0 - _c0[down * Lanes + Lanes - 1];
                    if (g * g > bestDistance) down = -1;
                    else Block(ref c0, ref c1, ref c2, ref c3, down--);
                }
            }
            distance = bestDistance;
            return best;

            void Block(ref float c0, ref float c1, ref float c2, ref float c3, int block)
            {
                var o = (nuint)(block * Lanes);
                var d0 = Vector128.LoadUnsafe(ref c0, o) - v0;
                var d1 = Vector128.LoadUnsafe(ref c1, o) - v1;
                var d2 = Vector128.LoadUnsafe(ref c2, o) - v2;
                var d3 = Vector128.LoadUnsafe(ref c3, o) - v3;
                var d = d0 * d0 + d1 * d1 + d2 * d2 + d3 * d3;
                var mask = Vector128.LessThanOrEqual(d, Vector128.Create(bestDistance)).ExtractMostSignificantBits();
                while (mask != 0)
                {
                    var lane = System.Numerics.BitOperations.TrailingZeroCount(mask);
                    mask &= mask - 1;
                    var dl = d.GetElement(lane);
                    var index = _index[(int)o + lane];
                    if (dl < bestDistance || (dl == bestDistance && index < best))
                    {
                        bestDistance = dl;
                        best = index;
                    }
                }
            }
        }

        /// <summary>The same answer by testing every centre: the reference the pruned searches are checked against.</summary>
        public int NearestFull(ReadOnlySpan<float> q, out float distance)
        {
            var best = int.MaxValue;
            var bestDistance = float.PositiveInfinity;
            for (var i = 0; i < _count; i++)
            {
                var d = Distance(i, q[0], q[1], q[2], q[3]);
                if (d < bestDistance || (d == bestDistance && _index[i] < best))
                {
                    bestDistance = d;
                    best = _index[i];
                }
            }
            distance = bestDistance;
            return best;
        }
    }

    /// <summary>
    /// Distinct-colour counts in an open-addressed table. Fully transparent colours count as one (0). When the
    /// colours exceed <see cref="MaxDistinct"/>, every key drops its lowest remaining bit per channel (alpha keeps
    /// 0 and 255 exact and loses at most two bits) and equal keys merge; a coarsened key stands for the centre of its bin.
    /// </summary>
    private sealed class Histogram(bool alpha, int maxDistinct)
    {
        // Each slot's key and count side by side, so a lookup touches one cache line. The table starts small and
        // doubles while more than half full, up to twice maxDistinct slots (a load factor of at most one half).
        private struct Entry
        {
            public int Key;
            public uint Count; // 0: empty; saturates (past four billion pixels of one colour the weight stops growing)
        }

        private const int MinBits = 12;
        private readonly int _maxBits = Math.Max(MinBits, 1 + System.Numerics.BitOperations.Log2(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(maxDistinct, 2))));
        private Entry[] _table = new Entry[1 << MinBits];
        private int _bits = MinBits;
        private int _distinct, _shift, _last = 1, _lastSlot = -1;

        public bool Coarsened => _shift > 0;

        public void Add(int argb)
        {
            if (argb == _last && _lastSlot >= 0)
            {
                ref var last = ref _table[_lastSlot];
                if (last.Count != uint.MaxValue) last.Count++;
                return;
            }
            _last = argb;
            var key = Key(argb);
            var slot = Slot(_table, _bits, key);
            ref var entry = ref _table[slot];
            if (entry.Count == 0)
            {
                entry.Key = key;
                if (++_distinct > maxDistinct)
                {
                    entry.Count = 1;
                    Coarsen();
                    _lastSlot = -1;
                    return;
                }
                if (_distinct * 2 > _table.Length && _bits < _maxBits)
                {
                    entry.Count = 1;
                    Rebuild(_bits + 1);
                    _lastSlot = -1;
                    return;
                }
            }
            if (entry.Count != uint.MaxValue) entry.Count++;
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

        private static int Slot(Entry[] table, int bits, int key)
        {
            var mask = table.Length - 1;
            var slot = (int)((uint)(key * -1640531535) >> (32 - bits));
            while (table[slot].Count != 0 && table[slot].Key != key) slot = (slot + 1) & mask;
            return slot;
        }

        // Re-inserts every entry (keys passed through Key, so coarsened if _shift moved) into a table of 2^bits slots.
        private void Rebuild(int bits)
        {
            var old = _table;
            _table = new Entry[1 << bits];
            _bits = bits;
            _distinct = 0;
            foreach (var e in old)
            {
                if (e.Count == 0) continue;
                var key = Key(e.Key);
                ref var entry = ref _table[Slot(_table, bits, key)];
                if (entry.Count == 0)
                {
                    entry.Key = key;
                    _distinct++;
                }
                entry.Count = (uint)Math.Min(uint.MaxValue, (ulong)entry.Count + e.Count);
            }
        }

        private void Coarsen()
        {
            while (_distinct > maxDistinct / 2)
            {
                _shift++;
                Rebuild(_bits);
            }
        }

        // Least-significant-digit radix sort on the unsigned keys, two 16-bit digits, each pass stable.
        private static (int[] Keys, long[] Weights) RadixSort(int[] keys, long[] weights)
        {
            var n = keys.Length;
            var k2 = new int[n];
            var w2 = new long[n];
            var count = new int[1 << 16];
            for (var shift = 0; shift < 32; shift += 16)
            {
                Array.Clear(count);
                for (var i = 0; i < n; i++) count[(int)((uint)keys[i] >> shift) & 0xFFFF]++;
                for (int d = 0, at = 0; d < count.Length; d++) (count[d], at) = (at, at + count[d]);
                for (var i = 0; i < n; i++)
                {
                    var to = count[(int)((uint)keys[i] >> shift) & 0xFFFF]++;
                    k2[to] = keys[i];
                    w2[to] = weights[i];
                }
                (keys, k2) = (k2, keys);
                (weights, w2) = (w2, weights);
            }
            return (keys, weights);
        }

        /// <summary>The colours (bin centres once coarsened) and their counts, in key order.</summary>
        public (int[] Keys, long[] Weights) Entries()
        {
            var keys = new int[_distinct];
            var weights = new long[_distinct];
            var n = 0;
            foreach (var e in _table)
            {
                if (e.Count == 0) continue;
                keys[n] = e.Key;
                weights[n++] = e.Count;
            }
            // Key order (unsigned), not slot order, so the result depends only on the colours counted.
            (keys, weights) = RadixSort(keys, weights);
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
        // A direct-mapped cache: most images reuse few colours, so most lookups skip the search. It grows with the
        // frames mapped, up to 2^MaxCacheBits entries: a photo has hundreds of thousands of colours. An empty slot
        // holds Empty, which no colour is once normalised (transparent with a colour).
        private const int MinCacheBits = 12, MaxCacheBits = 18, Empty = 1;
        private int[] _cacheKey = NewCache(MinCacheBits);
        private byte[] _cacheIndex = new byte[1 << MinCacheBits];
        private int _cacheShift = 32 - MinCacheBits;
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
        public byte Nearest(int argb) => Nearest(argb, -1);

        // hint: an entry likely near (the previous pixel's), or -1; it only speeds the search.
        internal byte Nearest(int argb, int hint)
        {
            argb = Normalise(argb);
            var slot = (int)((uint)(argb * -1640531535) >> _cacheShift);
            if (_cacheKey[slot] == argb) return _cacheIndex[slot];
            Span<float> f = stackalloc float[4];
            _space.Features(argb, f);
            var best = (byte)_search.Nearest(f, out _, hint);
            _cacheKey[slot] = argb;
            _cacheIndex[slot] = best;
            return best;
        }

        // The colour as the palette sees it: opaque for an opaque palette, 0 for every fully transparent one.
        private int Normalise(int argb) => !_alpha ? argb | unchecked((int)0xFF000000) : (uint)argb < 0x01000000u ? 0 : argb;

        private static int[] NewCache(int bits)
        {
            var keys = new int[1 << bits];
            Array.Fill(keys, Empty);
            return keys;
        }

        // Grows the cache toward one entry per pixel of a frame of this size.
        private void FitCache(int pixels)
        {
            var bits = Math.Clamp(32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(pixels - 1, 1)), MinCacheBits, MaxCacheBits);
            if (32 - _cacheShift >= bits) return;
            _cacheKey = NewCache(bits);
            _cacheIndex = new byte[1 << bits];
            _cacheShift = 32 - bits;
        }

        /// <summary>The nearest entry by the scalar search, whatever the machine: <see cref="Nearest(int)"/>'s twin, for the tests.</summary>
        internal byte NearestScalar(int argb)
        {
            if (!_alpha) argb |= unchecked((int)0xFF000000);
            else if ((uint)argb < 0x01000000u) argb = 0;
            Span<float> f = stackalloc float[4];
            _space.Features(argb, f);
            return (byte)_search.NearestScalar(f, out _);
        }

        /// <summary>The nearest entry by testing every one: the reference for <see cref="Nearest(int)"/>.</summary>
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
            FitCache(argb.Length);
            if (!dither)
            {
                MapPlain(argb, indices, transparentIndex);
                return;
            }

            // Errors in 256ths (Floyd–Steinberg's sixteenths times the strength's sixteenths), for this row and the
            // next, with a pixel of margin at each end; four channels.
            var current = new int[(width + 2) * 4];
            var below = new int[(width + 2) * 4];
            var previous = -1;
            // Each pixel's diffusion strength, a row at a time, from the luma of this row and its neighbours.
            var lumaAbove = new int[width];
            var luma = new int[width];
            var lumaBelow = new int[width];
            var strengths = new int[width];
            LumaRow(argb.Slice(0, width), luma);
            for (var y = 0; y < height; y++)
            {
                if (y + 1 < height) LumaRow(argb.Slice((y + 1) * width, width), lumaBelow);
                RowStrength(y > 0 ? lumaAbove : null, luma, y + 1 < height ? lumaBelow : null, strengths);
                (lumaAbove, luma, lumaBelow) = (luma, lumaBelow, lumaAbove);

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
                    var index = Nearest(a << 24 | r << 16 | g << 8 | b, previous);
                    previous = index;
                    indices[i] = index;
                    var strength = strengths[x];
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

        // Without dithering: the colours the cache misses are gathered into batches, so their coordinates are computed
        // a batch at a time (the vector path), then searched, each from the previous answer.
        private void MapPlain(ReadOnlySpan<int> argb, Span<byte> indices, int transparentIndex)
        {
            const int Batch = 64;
            Span<int> at = stackalloc int[Batch];
            Span<int> colours = stackalloc int[Batch];
            Span<float> f = stackalloc float[Batch * 4];
            int pending = 0, previous = -1;
            for (var i = 0; i < argb.Length; i++)
            {
                var p = argb[i];
                if (!_alpha && IsTransparent(p))
                {
                    indices[i] = (byte)transparentIndex;
                    continue;
                }
                p = Normalise(p);
                var slot = (int)((uint)(p * -1640531535) >> _cacheShift);
                if (_cacheKey[slot] == p)
                {
                    indices[i] = _cacheIndex[slot];
                    continue;
                }
                at[pending] = i;
                colours[pending++] = p;
                if (pending == Batch) Flush(at, colours, f, indices, ref pending, ref previous);
            }
            Flush(at, colours, f, indices, ref pending, ref previous);
        }

        private void Flush(Span<int> at, Span<int> colours, Span<float> f, Span<byte> indices, ref int pending, ref int previous)
        {
            _space.Features(colours[..pending], f);
            for (var j = 0; j < pending; j++)
            {
                var best = (byte)_search.Nearest(f.Slice(j * 4, 4), out _, previous);
                previous = best;
                var p = colours[j];
                var slot = (int)((uint)(p * -1640531535) >> _cacheShift);
                _cacheKey[slot] = p;
                _cacheIndex[slot] = best;
                indices[at[j]] = best;
            }
            pending = 0;
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

        private static void LumaRow(ReadOnlySpan<int> argb, int[] luma)
        {
            for (var x = 0; x < argb.Length; x++) luma[x] = Luma(argb[x]);
        }

        // Each pixel's diffusion strength in sixteenths, from its largest luma step to a 4-neighbour (rows above and
        // below null at the image's edges).
        private void RowStrength(int[]? above, int[] luma, int[]? below, int[] strengths)
        {
            var width = luma.Length;
            if (EdgeLimit == 0)
            {
                Array.Fill(strengths, Diffusion);
                return;
            }
            for (var x = 0; x < width; x++)
            {
                var l = luma[x];
                var contrast = 0;
                if (x > 0) contrast = Math.Max(contrast, Math.Abs(l - luma[x - 1]));
                if (x + 1 < width) contrast = Math.Max(contrast, Math.Abs(l - luma[x + 1]));
                if (above != null) contrast = Math.Max(contrast, Math.Abs(l - above[x]));
                if (below != null) contrast = Math.Max(contrast, Math.Abs(l - below[x]));
                strengths[x] = contrast >= EdgeLimit ? 0 : Diffusion * (EdgeLimit - contrast) / EdgeLimit;
            }
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

        // v / 256 rounded, halves away from zero, without a branch: for v < 0, -((-v + 128) >> 8) is (v + 127) >> 8.
        private static int Round256(int v) => (v + 128 + (v >> 31)) >> 8;

        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
