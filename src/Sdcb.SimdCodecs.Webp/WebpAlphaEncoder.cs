using System.Buffers;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// The <c>ALPH</c> chunk of a lossy WebP (RFC 9649 Section 2.7.1.3): one header byte (pre-processing,
/// filtering method, compression method) and the alpha plane, either raw or as a VP8L image stream of
/// implicit size whose green channel holds the (filtered) alpha values.
/// <para>A filter replaces each value by its difference (mod 256) from a prediction: the left value
/// (horizontal), the value above (vertical) or clip(left + above − above-left) (gradient); the first row
/// predicts from the left, the first column from above, and the first value from 0. The encoder tries
/// no filter, and at the highest effort all four, and keeps the smallest result.</para>
/// </summary>
internal static class WebpAlphaEncoder
{
    private const int FilterNone = 0, FilterHorizontal = 1, FilterVertical = 2, FilterGradient = 3;

    /// <summary>Whether any of the ARGB words is not fully opaque.</summary>
    public static bool HasAlpha(ReadOnlySpan<uint> argb)
    {
        foreach (uint p in argb)
        {
            if (p < 0xFF000000u)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ALPH payload of the alpha of <paramref name="argb"/>. <paramref name="quality"/> 100 keeps every
    /// value; below it the values are reduced to fewer levels first (0 and 255 always kept), which the
    /// header records as level reduction, unless the exact plane is no larger. The configuration sets
    /// how hard the lossless stream works and which filters are tried.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<uint> argb, int width, int height, Vp8EncoderConfig config, int quality)
    {
        int n = width * height;
        byte[] alpha = ArrayPool<byte>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                alpha[i] = (byte)(argb[i] >> 24);
            }

            byte[] exact = EncodePlane(alpha.AsSpan(0, n), width, height, config, preprocessing: 0);
            if (quality >= 100)
            {
                return exact;
            }

            // Fewer levels usually compress better, but a near-lossless reduction of a smooth ramp can
            // turn its regular steps into irregular ones: keep the exact plane when it is not larger.
            ReduceLevels(alpha.AsSpan(0, n), quality);
            byte[] reduced = EncodePlane(alpha.AsSpan(0, n), width, height, config, preprocessing: 1);
            return reduced.Length < exact.Length ? reduced : exact;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(alpha);
        }
    }

    /// <summary>The ALPH payload of one plane: the smallest of the filters tried, or raw.</summary>
    private static byte[] EncodePlane(ReadOnlySpan<byte> alpha, int width, int height, Vp8EncoderConfig config, int preprocessing)
    {
        int n = width * height;
        byte[] filtered = ArrayPool<byte>.Shared.Rent(n);
        uint[] words = ArrayPool<uint>.Shared.Rent(n);
        uint[] scratch = ArrayPool<uint>.Shared.Rent(n); // the lossless encoder may change its input
        try
        {
            byte[]? best = null; // raw (n bytes) unless a compressed stream is smaller

            // No filter usually wins: the lossless stream's own predictors (14 modes, chosen per block)
            // fit the plane better than any one of these over the whole of it. The others are tried at
            // the highest effort only.
            int first = config.AlphaFilter >= 0 ? config.AlphaFilter & 3 : FilterNone;
            int last = config.AlphaFilter >= 0 || !config.AlphaTryFilters ? first : FilterGradient;
            for (int filter = first; filter <= last; filter++)
            {
                Filter(alpha, filtered.AsSpan(0, n), width, height, filter);
                for (int i = 0; i < n; i++)
                {
                    words[i] = 0xFF000000u | ((uint)filtered[i] << 8);
                }

                // The plane's values always form a palette, so the lossless stream can be indexed (no
                // predictor: copies and the colour cache carry it) or direct (predicted); each is encoded
                // at its own effort and the smaller kept. With 16 values or fewer, indexing packs several
                // pixels per code and the direct stream is not tried.
                bool tryIndexed = config.AlphaPath != 2;
                bool tryDirect = config.AlphaPath != 1 && (config.AlphaPath == 2 || DistinctAtLeast(filtered.AsSpan(0, n), 17));
                if (tryIndexed)
                {
                    Keep(Stream(words, scratch, width, height, config.AlphaPaletteEffort, Vp8LAblation.PaletteOnly), filter);
                }

                if (tryDirect)
                {
                    Keep(Stream(words, scratch, width, height, config.AlphaEffort, Vp8LAblation.NoPalette), filter);
                }
            }

            if (best is null)
            {
                best = new byte[1 + n];
                best[0] = (byte)(preprocessing << 4);
                alpha.CopyTo(best.AsSpan(1));
            }

            return best;

            void Keep(byte[] stream, int filter)
            {
                if (stream.Length < (best?.Length - 1 ?? n))
                {
                    best = new byte[1 + stream.Length];
                    best[0] = (byte)((preprocessing << 4) | (filter << 2) | 1);
                    stream.CopyTo(best.AsSpan(1));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(filtered);
            ArrayPool<uint>.Shared.Return(words);
            ArrayPool<uint>.Shared.Return(scratch);
        }
    }

    /// <summary>One lossless image stream of the plane in <paramref name="words"/> (left as it is).</summary>
    private static byte[] Stream(uint[] words, uint[] scratch, int width, int height, int effort, Vp8LAblation path)
    {
        int n = width * height;
        words.AsSpan(0, n).CopyTo(scratch); // the lossless encoder may change its input
        using var writer = new Vp8LBitWriter(n / 8 + 1024);

        // Only green varies: subtract green and cross colour cannot pay, and searches cost green alone.
        Vp8LEncoder.EncodeImageStream(scratch, width, height, effort, writer,
            Vp8LAblation.NoSubtractGreen | Vp8LAblation.NoCrossColor | Vp8LAblation.GreenOnly | path);
        return writer.Finish().ToArray();
    }

    private static bool DistinctAtLeast(ReadOnlySpan<byte> values, int count)
    {
        Span<bool> seen = stackalloc bool[256];
        int distinct = 0;
        foreach (byte v in values)
        {
            if (!seen[v])
            {
                seen[v] = true;
                if (++distinct >= count)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The lossless effort for the alpha plane at a lossy effort.</summary>
    internal static int LosslessEffort(int effort) => effort switch { <= 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 5, _ => 6 };

    /// <summary>
    /// Quantizes the values to evenly spaced levels, 0 and 255 among them: 256 levels at quality 100,
    /// down to 4 at quality 0.
    /// </summary>
    internal static void ReduceLevels(Span<byte> alpha, int quality)
    {
        int levels = 4 + (Math.Clamp(quality, 0, 100) * 252 + 50) / 100;
        if (levels >= 256)
        {
            return;
        }

        Span<byte> map = stackalloc byte[256];
        for (int v = 0; v < 256; v++)
        {
            int level = (v * (levels - 1) + 127) / 255;
            map[v] = (byte)((level * 255 + (levels - 1) / 2) / (levels - 1));
        }

        for (int i = 0; i < alpha.Length; i++)
        {
            alpha[i] = map[alpha[i]];
        }
    }

    /// <summary>The filter's residuals: value − prediction mod 256, the predictions the decoder makes.</summary>
    internal static void Filter(ReadOnlySpan<byte> alpha, Span<byte> residual, int width, int height, int filter)
    {
        if (filter == FilterNone)
        {
            alpha.CopyTo(residual);
            return;
        }

        // First row: from the left (the first value from 0), for every filter.
        residual[0] = alpha[0];
        for (int x = 1; x < width; x++)
        {
            residual[x] = (byte)(alpha[x] - alpha[x - 1]);
        }

        for (int y = 1; y < height; y++)
        {
            int row = y * width, up = row - width;
            if (filter == FilterVertical)
            {
                for (int x = 0; x < width; x++)
                {
                    residual[row + x] = (byte)(alpha[row + x] - alpha[up + x]);
                }

                continue;
            }

            // Horizontal and gradient: the first column from above.
            residual[row] = (byte)(alpha[row] - alpha[up]);
            if (filter == FilterHorizontal)
            {
                for (int x = 1; x < width; x++)
                {
                    residual[row + x] = (byte)(alpha[row + x] - alpha[row + x - 1]);
                }
            }
            else
            {
                for (int x = 1; x < width; x++)
                {
                    int prediction = Math.Clamp(alpha[row + x - 1] + alpha[up + x] - alpha[up + x - 1], 0, 255);
                    residual[row + x] = (byte)(alpha[row + x] - prediction);
                }
            }
        }
    }

    /// <summary>
    /// Gives the RGB of fully transparent pixels values that cost the lossy encoder little: within each
    /// 16×16 macroblock, the mean colour of its visible pixels; a macroblock with none takes the mean of
    /// the one to its left (or above, at the start of a row). Visible pixels and every alpha value are
    /// left as they are.
    /// </summary>
    public static void FlattenInvisible(Span<uint> argb, int width, int height)
    {
        int mbW = (width + 15) >> 4, mbH = (height + 15) >> 4;
        uint[] fill = ArrayPool<uint>.Shared.Rent(mbW * mbH);
        try
        {
            for (int mbY = 0; mbY < mbH; mbY++)
            {
                for (int mbX = 0; mbX < mbW; mbX++)
                {
                    int x0 = mbX * 16, y0 = mbY * 16, x1 = Math.Min(width, x0 + 16), y1 = Math.Min(height, y0 + 16);
                    long r = 0, g = 0, b = 0;
                    int visible = 0, invisible = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            uint p = argb[y * width + x];
                            if (p < 0x01000000u)
                            {
                                invisible++;
                                continue;
                            }

                            r += (p >> 16) & 0xFF;
                            g += (p >> 8) & 0xFF;
                            b += p & 0xFF;
                            visible++;
                        }
                    }

                    uint colour;
                    if (visible > 0)
                    {
                        int half = visible / 2;
                        colour = (uint)((r + half) / visible) << 16 | (uint)((g + half) / visible) << 8 | (uint)((b + half) / visible);
                    }
                    else
                    {
                        colour = mbX > 0 ? fill[mbY * mbW + mbX - 1] : mbY > 0 ? fill[(mbY - 1) * mbW] : 0x808080u;
                    }

                    fill[mbY * mbW + mbX] = colour;
                    if (invisible == 0)
                    {
                        continue;
                    }

                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int i = y * width + x;
                            if (argb[i] < 0x01000000u)
                            {
                                argb[i] = colour; // alpha stays 0
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(fill);
        }
    }
}
