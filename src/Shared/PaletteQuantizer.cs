// Compiled into each package that encodes a palette (GIF, and BMP's 1/4/8-bit modes), under that
// package's namespace so the copies are distinct types: shipped packages never share internals.
#if SIMDCODECS_GIF
namespace Sdcb.SimdCodecs.Gif;
#elif SIMDCODECS_BMP
namespace Sdcb.SimdCodecs.Bmp;
#else
#error PaletteQuantizer.cs is shared source for Sdcb.SimdCodecs.Gif and Sdcb.SimdCodecs.Bmp only.
#endif

/// <summary>
/// Reduces colours to a palette, for the palette encoders. The palette comes from the variance-minimising
/// box split of X. Wu, "Efficient Statistical Computations for Optimal Color Quantization" (Graphics Gems
/// II, 1991): a 32×32×32 histogram of the colours with cumulative moments (count, per-channel sums, sum of
/// squares) makes the sums over any box eight lookups, so the colour cube can be cut, box by box, at
/// the plane that most reduces the total squared error, always splitting the box of largest variance. Each
/// palette entry is the mean of the colours in its box.
/// <para>
/// Pixels then map to the nearest entry (squared RGB distance); with dithering, Floyd–Steinberg error
/// diffusion (R. W. Floyd and L. Steinberg, 1976) carries each pixel's error to its unvisited neighbours.
/// Colours are 0xRRGGBB ints throughout.
/// </para>
/// </summary>
internal sealed class PaletteQuantizer
{
    private const int Side = 33; // 32 bins per channel, plus the zero plane the cumulative sums start from

    private readonly long[] _weight = new long[Side * Side * Side];
    private readonly long[] _red = new long[Side * Side * Side];
    private readonly long[] _green = new long[Side * Side * Side];
    private readonly long[] _blue = new long[Side * Side * Side];
    private readonly double[] _squares = new double[Side * Side * Side];

    /// <summary>Whether a 0xAARRGGBB pixel counts as transparent: alpha below 128.</summary>
    internal static bool IsTransparent(int argb) => (uint)argb < 0x80000000u;

    private static int At(int r, int g, int b) => (r * Side + g) * Side + b;

    /// <summary>Counts one colour into the histogram.</summary>
    public void Add(int rgb)
    {
        int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
        var i = At((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1);
        _weight[i]++;
        _red[i] += r;
        _green[i] += g;
        _blue[i] += b;
        _squares[i] += r * r + g * g + b * b;
    }

    /// <summary>Builds a palette of at most <paramref name="maxColors"/> entries from the colours added.</summary>
    public int[] BuildPalette(int maxColors)
    {
        Accumulate();
        var boxes = new List<Box> { new(0, 32, 0, 32, 0, 32) };
        var variance = new List<double> { Variance(boxes[0]) };
        while (boxes.Count < maxColors)
        {
            // The box whose split promises the most: the one of largest variance that can still be cut.
            var pick = -1;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (variance[i] > 0 && (pick < 0 || variance[i] > variance[pick])) pick = i;
            }
            if (pick < 0) break;
            if (!TryCut(boxes[pick], out var lower, out var upper))
            {
                variance[pick] = 0; // one bin, or every colour in one plane of it: nothing to gain
                continue;
            }
            boxes[pick] = lower;
            variance[pick] = Variance(lower);
            boxes.Add(upper);
            variance.Add(Variance(upper));
        }

        var palette = new List<int>(boxes.Count);
        foreach (var box in boxes)
        {
            var w = Volume(box, _weight);
            if (w == 0) continue;
            int r = (int)((Volume(box, _red) + w / 2) / w), g = (int)((Volume(box, _green) + w / 2) / w), b = (int)((Volume(box, _blue) + w / 2) / w);
            palette.Add((r << 16) | (g << 8) | b);
        }
        return [.. palette];
    }

    // Turns each moment into its cumulative sum from the origin, one axis at a time.
    private void Accumulate()
    {
        foreach (var m in new[] { _weight, _red, _green, _blue })
            Accumulate(m);
        for (var r = 1; r < Side; r++)
        for (var g = 1; g < Side; g++)
        for (var b = 1; b < Side; b++)
            _squares[At(r, g, b)] += _squares[At(r, g, b - 1)];
        for (var r = 1; r < Side; r++)
        for (var b = 1; b < Side; b++)
        for (var g = 1; g < Side; g++)
            _squares[At(r, g, b)] += _squares[At(r, g - 1, b)];
        for (var g = 1; g < Side; g++)
        for (var b = 1; b < Side; b++)
        for (var r = 1; r < Side; r++)
            _squares[At(r, g, b)] += _squares[At(r - 1, g, b)];
    }

    private static void Accumulate(long[] m)
    {
        for (var r = 1; r < Side; r++)
        for (var g = 1; g < Side; g++)
        for (var b = 1; b < Side; b++)
            m[At(r, g, b)] += m[At(r, g, b - 1)];
        for (var r = 1; r < Side; r++)
        for (var b = 1; b < Side; b++)
        for (var g = 1; g < Side; g++)
            m[At(r, g, b)] += m[At(r, g - 1, b)];
        for (var g = 1; g < Side; g++)
        for (var b = 1; b < Side; b++)
        for (var r = 1; r < Side; r++)
            m[At(r, g, b)] += m[At(r - 1, g, b)];
    }

    /// <summary>A box of bins: lower bounds exclusive, upper inclusive, in cumulative-sum coordinates.</summary>
    private readonly record struct Box(int R0, int R1, int G0, int G1, int B0, int B1);

    // The sum of a moment over a box, by inclusion–exclusion of the cumulative sums at its eight corners.
    private static long Volume(Box x, long[] m) =>
        m[At(x.R1, x.G1, x.B1)] - m[At(x.R1, x.G1, x.B0)] - m[At(x.R1, x.G0, x.B1)] + m[At(x.R1, x.G0, x.B0)]
        - m[At(x.R0, x.G1, x.B1)] + m[At(x.R0, x.G1, x.B0)] + m[At(x.R0, x.G0, x.B1)] - m[At(x.R0, x.G0, x.B0)];

    private static double Volume(Box x, double[] m) =>
        m[At(x.R1, x.G1, x.B1)] - m[At(x.R1, x.G1, x.B0)] - m[At(x.R1, x.G0, x.B1)] + m[At(x.R1, x.G0, x.B0)]
        - m[At(x.R0, x.G1, x.B1)] + m[At(x.R0, x.G1, x.B0)] + m[At(x.R0, x.G0, x.B1)] - m[At(x.R0, x.G0, x.B0)];

    // The box's squared error about its mean: sum of squares minus |sum|^2 / count.
    private double Variance(Box x)
    {
        var w = Volume(x, _weight);
        if (w == 0) return 0;
        double r = Volume(x, _red), g = Volume(x, _green), b = Volume(x, _blue);
        return Volume(x, _squares) - (r * r + g * g + b * b) / w;
    }

    // The cut, over every plane of every axis, that leaves the most of |sum|^2 / count in the two halves:
    // the total is fixed, so that is the cut that removes the most squared error.
    private bool TryCut(Box box, out Box lower, out Box upper)
    {
        lower = upper = default;
        var best = double.NegativeInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var (lo, hi) = axis switch { 0 => (box.R0, box.R1), 1 => (box.G0, box.G1), _ => (box.B0, box.B1) };
            for (var cut = lo + 1; cut < hi; cut++)
            {
                var (a, b) = Split(box, axis, cut);
                long wa = Volume(a, _weight), wb = Volume(b, _weight);
                if (wa == 0 || wb == 0) continue;
                double ra = Volume(a, _red), ga = Volume(a, _green), ba = Volume(a, _blue);
                double rb = Volume(b, _red), gb = Volume(b, _green), bb = Volume(b, _blue);
                var score = (ra * ra + ga * ga + ba * ba) / wa + (rb * rb + gb * gb + bb * bb) / wb;
                if (score > best)
                {
                    best = score;
                    (lower, upper) = (a, b);
                }
            }
        }
        return best > double.NegativeInfinity;
    }

    private static (Box Lower, Box Upper) Split(Box x, int axis, int cut) => axis switch
    {
        0 => (x with { R1 = cut }, x with { R0 = cut }),
        1 => (x with { G1 = cut }, x with { G0 = cut }),
        _ => (x with { B1 = cut }, x with { B0 = cut }),
    };

    /// <summary>
    /// Maps opaque pixels to their nearest palette entries, caching each colour's answer. Pixels are
    /// 0xAARRGGBB; transparent ones (<see cref="IsTransparent"/>) take the transparent index
    /// given to <see cref="Map"/>.
    /// </summary>
    internal sealed class Mapper(int[] palette)
    {
        // A direct-mapped cache: most images reuse few colours, so most lookups skip the search.
        private readonly int[] _cacheKey = new int[1 << 16];
        private readonly byte[] _cacheIndex = new byte[1 << 16];

        public byte Nearest(int rgb)
        {
            var slot = (int)((uint)(rgb * -1640531535) >> 16);
            if (_cacheKey[slot] == rgb + 1) return _cacheIndex[slot];
            int r = (rgb >> 16) & 255, g = (rgb >> 8) & 255, b = rgb & 255;
            int best = 0, bestDistance = int.MaxValue;
            for (var i = 0; i < palette.Length; i++)
            {
                var p = palette[i];
                int dr = ((p >> 16) & 255) - r, dg = ((p >> 8) & 255) - g, db = (p & 255) - b;
                var d = dr * dr + dg * dg + db * db;
                if (d < bestDistance)
                {
                    (best, bestDistance) = (i, d);
                    if (d == 0) break;
                }
            }
            _cacheKey[slot] = rgb + 1;
            _cacheIndex[slot] = (byte)best;
            return (byte)best;
        }

        /// <summary>Maps one frame, optionally with Floyd–Steinberg dithering; transparent pixels are skipped by the diffusion.</summary>
        public void Map(ReadOnlySpan<int> argb, int width, int height, Span<byte> indices, int transparentIndex, bool dither)
        {
            if (!dither)
            {
                for (var i = 0; i < argb.Length; i++)
                    indices[i] = IsTransparent(argb[i]) ? (byte)transparentIndex : Nearest(argb[i] & 0xFFFFFF);
                return;
            }

            // Errors in sixteenths, for this row and the next, with a pixel of margin at each end.
            var current = new int[(width + 2) * 3];
            var below = new int[(width + 2) * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    if (IsTransparent(argb[i]))
                    {
                        indices[i] = (byte)transparentIndex;
                        continue;
                    }
                    var e = (x + 1) * 3;
                    int r = Clamp(((argb[i] >> 16) & 255) + Round16(current[e])),
                        g = Clamp(((argb[i] >> 8) & 255) + Round16(current[e + 1])),
                        b = Clamp((argb[i] & 255) + Round16(current[e + 2]));
                    var index = Nearest((r << 16) | (g << 8) | b);
                    indices[i] = index;
                    var p = palette[index];
                    Spread(current, below, e, r - ((p >> 16) & 255), 0);
                    Spread(current, below, e, g - ((p >> 8) & 255), 1);
                    Spread(current, below, e, b - (p & 255), 2);
                }
                (current, below) = (below, current);
                Array.Clear(below);
            }
        }

        // 7/16 to the right, 3/16 below-left, 5/16 below, 1/16 below-right.
        private static void Spread(int[] current, int[] below, int e, int error, int channel)
        {
            current[e + 3 + channel] += error * 7;
            below[e - 3 + channel] += error * 3;
            below[e + channel] += error * 5;
            below[e + 3 + channel] += error;
        }

        private static int Round16(int sixteenths) => sixteenths >= 0 ? (sixteenths + 8) >> 4 : -((-sixteenths + 8) >> 4);

        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
