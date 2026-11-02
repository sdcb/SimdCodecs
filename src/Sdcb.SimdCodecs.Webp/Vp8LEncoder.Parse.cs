using System.Buffers;
using System.Numerics;

namespace Sdcb.SimdCodecs.Webp;

internal static partial class Vp8LEncoder
{
    // ---------------------------------------------------------------- cost-based parsing

    /// <summary>
    /// Bits per symbol under one histogram: −log2 of the symbol's share, with unseen symbols priced as
    /// if they had been seen half a time, so a parse may still choose them. Length and distance costs
    /// include their extra bits.
    /// </summary>
    private sealed class CostModel
    {
        public readonly float[] Green, Red, Blue, Alpha;
        public readonly float[] LengthCost = new float[MaxLength + 1];
        private readonly float[] _distancePrefix = new float[NumDistanceCodes];
        private readonly int _cacheBase;

        public CostModel(Histogram h)
        {
            Green = Prices(h.Green);
            Red = Prices(h.Red);
            Blue = Prices(h.Blue);
            Alpha = Prices(h.Alpha);
            float[] distance = Prices(h.Distance);
            distance.CopyTo(_distancePrefix, 0);
            _cacheBase = 256 + NumLengthCodes;
            for (int length = 1; length <= MaxLength; length++)
            {
                int prefix = PrefixEncode(length, out int extraBits, out _);
                LengthCost[length] = Green[256 + prefix] + extraBits;
            }
        }

        public float Literal(uint argb) => Green[(argb >> 8) & 0xFF] + Red[(argb >> 16) & 0xFF] + Blue[argb & 0xFF] + Alpha[argb >> 24];

        public float CacheHit(int key) => Green[_cacheBase + key];

        public float Distance(int distanceCode)
        {
            int prefix = PrefixEncode(distanceCode, out int extraBits, out _);
            return _distancePrefix[prefix] + extraBits;
        }

        private static float[] Prices(uint[] counts)
        {
            double total = 0;
            foreach (uint c in counts)
            {
                total += c;
            }

            var prices = new float[counts.Length];
            if (total == 0)
            {
                // Nothing was seen: every symbol is equally unlikely; keep copies and literals comparable.
                Array.Fill(prices, (float)Math.Log2(counts.Length));
                return prices;
            }

            // A prefix code spends no bits on the only symbol of its alphabet, and at least one bit on
            // each symbol when there are two or more: −log2 p below 1 would make a parse prefer
            // near-free literals the real code cannot deliver.
            int used = 0;
            foreach (uint c in counts)
            {
                used += c != 0 ? 1 : 0;
            }

            double unseen = Math.Max(1, Math.Log2((total + 0.5) / 0.5));
            for (int i = 0; i < counts.Length; i++)
            {
                prices[i] = counts[i] == 0 ? (float)unseen
                    : used == 1 ? 0f
                    : (float)Math.Max(1, Math.Log2(total / counts[i]));
            }

            return prices;
        }
    }

    /// <summary>
    /// Replaces <paramref name="refs"/> with the parse of <paramref name="pixels"/> that costs least under
    /// the symbol prices of <paramref name="models"/>, one per tile group (a shortest path over pixel positions).
    /// <para>The color cache holds, at every position, the last pixel of each slot in pixel order,
    /// whatever was a literal and whatever was copied (Section 5.2.3 inserts every decoded pixel), so its
    /// state does not depend on the parse and a literal's price is exact: the cache symbol when the slot
    /// holds the colour, the four channel symbols otherwise.</para>
    /// <para>Copies come from distance 1, the pixel above, and a hash chain of <c>ChainDepth</c>
    /// candidates; along the chain only candidates longer than every nearer one are kept, so each
    /// length is offered with its nearest distance. A long match (128 pixels or more) is followed
    /// without searching inside it.</para>
    /// </summary>
    private static void OptimalParse(ReadOnlySpan<uint> pixels, int width, int cacheBits, CostModel[] models,
        int histogramBits, int tileWidth, int[]? tileGroups, Vp8LEncoderConfig config, BackwardRefs refs)
    {
        int n = pixels.Length;
        float[] cost = ArrayPool<float>.Shared.Rent(n + 1);
        int[] fromLength = ArrayPool<int>.Shared.Rent(n + 1);
        int[] fromCode = ArrayPool<int>.Shared.Rent(n + 1);
        int hashBits = Math.Clamp(BitOperations.Log2((uint)Math.Max(n, 2)) + 1, 8, 18);
        int[] head = ArrayPool<int>.Shared.Rent(1 << hashBits);
        int[] chain = ArrayPool<int>.Shared.Rent(Math.Max(n, 1));
        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        Span<int> candLength = stackalloc int[64];
        Span<int> candDistance = stackalloc int[64];
        try
        {
            cost.AsSpan(0, n + 1).Fill(float.MaxValue);
            cost[0] = 0;
            head.AsSpan(0, 1 << hashBits).Fill(-1);
            var matcher = new Matcher(pixels, width, head, chain, hashBits, config.ParseChainDepth);

            // While inside a long match, the distance and the end it continues to.
            int followDistance = 0, followEnd = 0;
            for (int i = 0, x = 0, y = 0; i < n; i++)
            {
                float here = cost[i];
                uint argb = pixels[i];

                // Every symbol starting here is coded with the prefix codes of this pixel's tile group.
                CostModel model = models[tileGroups is null ? 0 : tileGroups[(y >> histogramBits) * tileWidth + (x >> histogramBits)]];
                if (++x == width)
                {
                    x = 0;
                    y++;
                }

                // Literal (or cache hit).
                float literal;
                if (cache is not null)
                {
                    int key = CacheKey(argb, cacheBits);
                    literal = cache[key] == argb ? model.CacheHit(key) : model.Literal(argb);
                    cache[key] = argb;
                }
                else
                {
                    literal = model.Literal(argb);
                }

                if (here + literal < cost[i + 1])
                {
                    cost[i + 1] = here + literal;
                    fromLength[i + 1] = 0;
                }

                int max = Math.Min(MaxLength, n - i);
                if (i < followEnd)
                {
                    // Continue the long match: offer its remainder, and its short prefixes.
                    int code = DistanceToCode(followDistance, width);
                    float d = model.Distance(code);
                    Relax(cost, fromLength, fromCode, model, i, followEnd - i, d, code, 0);
                }
                else
                {
                    int count = matcher.Candidates(i, max, candLength, candDistance);
                    int previous = 0;
                    for (int c = 0; c < count; c++)
                    {
                        int length = candLength[c];
                        int code = DistanceToCode(candDistance[c], width);
                        Relax(cost, fromLength, fromCode, model, i, length, model.Distance(code), code, previous);
                        previous = length;
                    }

                    if (count > 0 && candLength[count - 1] >= 128)
                    {
                        followDistance = candDistance[count - 1];
                        followEnd = i + candLength[count - 1];
                    }
                }

                matcher.Insert(i);
            }

            // Trace back, then write the parse front to back.
            int steps = 0;
            for (int pos = n; pos > 0; steps++)
            {
                int length = fromLength[pos];
                pos -= length == 0 ? 1 : length;
            }

            refs.Count = steps;
            for (int pos = n, t = steps - 1; pos > 0; t--)
            {
                int length = fromLength[pos];
                if (length == 0)
                {
                    refs.Length[t] = 0;
                    refs.Value[t] = pixels[pos - 1];
                    pos--;
                }
                else
                {
                    refs.Length[t] = length;
                    refs.Value[t] = (uint)fromCode[pos];
                    pos -= length;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(cost);
            ArrayPool<int>.Shared.Return(fromLength);
            ArrayPool<int>.Shared.Return(fromCode);
            ArrayPool<int>.Shared.Return(head);
            ArrayPool<int>.Shared.Return(chain);
        }
    }

    /// <summary>
    /// Offers copies of <paramref name="distanceCost"/> from position <paramref name="i"/>: every length
    /// in (<paramref name="shorter"/>, <paramref name="shorter"/> + 16], the full
    /// <paramref name="length"/>, and the lengths where the length prefix code steps up.
    /// </summary>
    private static void Relax(float[] cost, int[] fromLength, int[] fromCode, CostModel model, int i, int length,
        float distanceCost, int code, int shorter)
    {
        float start = cost[i] + distanceCost;
        int dense = Math.Min(length, shorter + 16);
        for (int l = Math.Max(1, shorter + 1); l <= dense; l++)
        {
            Offer(cost, fromLength, fromCode, i + l, start + model.LengthCost[l], l, code);
        }

        if (length > dense)
        {
            for (int step = 32; step < length; step <<= 1)
            {
                if (step > dense)
                {
                    Offer(cost, fromLength, fromCode, i + step + 1, start + model.LengthCost[step + 1], step + 1, code);
                }
            }

            Offer(cost, fromLength, fromCode, i + length, start + model.LengthCost[length], length, code);
        }
    }

    private static void Offer(float[] cost, int[] fromLength, int[] fromCode, int end, float value, int length, int code)
    {
        if (value < cost[end])
        {
            cost[end] = value;
            fromLength[end] = length;
            fromCode[end] = code;
        }
    }
}
