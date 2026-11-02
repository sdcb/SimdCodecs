using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>Effort-dependent choices of the VP8L encoder.</summary>
internal readonly record struct Vp8LEncoderConfig(
    int PredictorBits,
    int CrossColorBits,
    int ChainDepth,
    bool Lazy,
    int MaxCacheBits,
    int HistogramBits,
    int MaxGroups,
    Vp8LAblation Ablation = Vp8LAblation.None)
{
    public static Vp8LEncoderConfig ForEffort(int effort) => effort switch
    {
        <= 0 => new(5, 6, 1, false, 0, 0, 1),
        1 => new(5, 6, 4, false, 6, 0, 1),
        2 => new(4, 5, 8, true, 10, 6, 16),
        3 => new(4, 5, 16, true, 10, 5, 32),
        4 => new(4, 5, 32, true, 10, 5, 64),
        5 => new(4, 5, 48, true, 10, 4, 96),
        6 => new(4, 5, 64, true, 10, 4, 128),
        7 => new(4, 4, 128, true, 10, 4, 160),
        8 => new(3, 4, 256, true, 10, 3, 200),
        _ => new(3, 4, 512, true, 10, 3, 256),
    };
}

/// <summary>Encoder stages the benchmark can switch off, to attribute size and time (never set by the public API).</summary>
[Flags]
internal enum Vp8LAblation
{
    None = 0,
    NoPalette = 1 << 0,
    NoSubtractGreen = 1 << 1,
    NoPredictor = 1 << 2,
    NoCrossColor = 1 << 3,
    NoCache = 1 << 4,
    NoBackwardRefs = 1 << 5,
    NoClustering = 1 << 6,
}

/// <summary>
/// RFC 9649 VP8L encoder: the lossless bitstream after the 5-byte header. Built from the format
/// description only: the transforms (Section 4), LZ77 backward references with 2-D distance codes and
/// the color cache (Section 5), and spatially varying prefix codes (Section 6). The choices an encoder
/// is free to make — which transforms, which predictor per block, which references, how codes are
/// grouped — are made here by entropy estimates.
/// </summary>
internal static partial class Vp8LEncoder
{
    private const int NumLengthCodes = 24;
    private const int NumDistanceCodes = 40;
    private const int MaxLength = 4096;
    private const int MaxDistance = (3 << 18) + (1 << 18) - 120; // the largest distance code value is 2^20

    /// <summary>Writes the VP8L header (signature, size, alpha hint, version) and the image.</summary>
    public static void Encode(uint[] argb, int width, int height, bool exact, int effort, Vp8LBitWriter writer,
        Vp8LAblation ablation = Vp8LAblation.None)
    {
        int n = width * height;
        bool hasAlpha = false;
        for (int i = 0; i < n; i++)
        {
            if (argb[i] < 0xFF000000u)
            {
                hasAlpha = true;
                break;
            }
        }

        if (!exact && hasAlpha)
        {
            // The RGB under alpha 0 is invisible; making it constant lets runs and the palette absorb it.
            for (int i = 0; i < n; i++)
            {
                if (argb[i] < 0x01000000u)
                {
                    argb[i] = 0;
                }
            }
        }

        writer.WriteBits(0x2F, 8);
        writer.WriteBits((uint)(width - 1), 14);
        writer.WriteBits((uint)(height - 1), 14);
        writer.WriteBits(hasAlpha ? 1u : 0u, 1);
        writer.WriteBits(0, 3);

        Vp8LEncoderConfig config = Vp8LEncoderConfig.ForEffort(effort) with { Ablation = ablation };
        if ((ablation & Vp8LAblation.NoCache) != 0)
        {
            config = config with { MaxCacheBits = 0 };
        }

        if ((ablation & Vp8LAblation.NoClustering) != 0)
        {
            config = config with { MaxGroups = 1, HistogramBits = 0 };
        }

        Span<uint> palette = stackalloc uint[256];
        int paletteSize = (ablation & Vp8LAblation.NoPalette) != 0 ? 0 : CollectPalette(argb.AsSpan(0, n), palette);
        uint[]? owned = null;
        try
        {
            uint[] pixels = argb;
            int codedWidth = width;
            if (paletteSize > 0)
            {
                pixels = owned = ApplyPalette(argb, width, height, palette[..paletteSize], writer, config, out codedWidth);
            }
            else
            {
                if ((ablation & Vp8LAblation.NoSubtractGreen) == 0 && SubtractGreenHelps(argb.AsSpan(0, n)))
                {
                    writer.WriteBits(1, 1);
                    writer.WriteBits(2, 2);
                    SubtractGreen(argb.AsSpan(0, n));
                }

                if ((ablation & Vp8LAblation.NoPredictor) == 0)
                {
                    owned = ApplyPredictor(argb, width, height, config, writer);
                    pixels = owned;
                }

                if ((ablation & Vp8LAblation.NoCrossColor) == 0)
                {
                    ApplyCrossColor(pixels, width, height, config, writer);
                }
            }

            writer.WriteBits(0, 1); // no more transforms
            EncodeImageStream(pixels.AsSpan(0, codedWidth * height), codedWidth, height, isMain: true, config, writer);
        }
        finally
        {
            if (owned is not null)
            {
                ArrayPool<uint>.Shared.Return(owned);
            }
        }
    }

    // ---------------------------------------------------------------- palette (color indexing)

    /// <summary>The distinct colours, sorted, when there are at most 256; otherwise 0.</summary>
    private static int CollectPalette(ReadOnlySpan<uint> argb, Span<uint> palette)
    {
        // Open-addressed set of up to 256 colours in 1024 slots; the run check skips most lookups.
        Span<uint> slots = stackalloc uint[1024];
        Span<bool> filled = stackalloc bool[1024];
        filled.Clear();
        int count = 0;
        uint last = ~argb[0];
        foreach (uint c in argb)
        {
            if (c == last)
            {
                continue;
            }

            last = c;
            int h = (int)((c * 0x9E3779B1u) >> 22);
            while (filled[h] && slots[h] != c)
            {
                h = (h + 1) & 1023;
            }

            if (!filled[h])
            {
                if (count == 256)
                {
                    return 0;
                }

                filled[h] = true;
                slots[h] = c;
                palette[count++] = c;
            }
        }

        palette[..count].Sort();
        return count;
    }

    private static uint[] ApplyPalette(uint[] argb, int width, int height, ReadOnlySpan<uint> palette,
        Vp8LBitWriter writer, Vp8LEncoderConfig config, out int codedWidth)
    {
        int size = palette.Length;
        int bits = size <= 2 ? 3 : size <= 4 ? 2 : size <= 16 ? 1 : 0;
        writer.WriteBits(1, 1);
        writer.WriteBits(3, 2);
        writer.WriteBits((uint)(size - 1), 8);

        // The table is delta-coded: each entry minus the previous, per channel.
        Span<uint> deltas = stackalloc uint[size];
        uint previous = 0;
        for (int i = 0; i < size; i++)
        {
            deltas[i] = SubPixels(palette[i], previous);
            previous = palette[i];
        }

        EncodeImageStream(deltas, size, 1, isMain: false, config, writer);

        // Index lookup: binary search in the sorted palette, memoised for runs.
        codedWidth = (width + (1 << bits) - 1) >> bits;
        uint[] packed = ArrayPool<uint>.Shared.Rent(codedWidth * height);
        int perPixel = 8 >> bits;
        uint lastColor = palette[0];
        int lastIndex = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width, outRow = y * codedWidth;
            uint acc = 0;
            for (int x = 0; x < width; x++)
            {
                uint c = argb[row + x];
                if (c != lastColor)
                {
                    lastColor = c;
                    lastIndex = palette.BinarySearch(c);
                }

                int slot = x & ((1 << bits) - 1);
                acc |= (uint)lastIndex << (slot * perPixel);
                if (slot == (1 << bits) - 1 || x == width - 1)
                {
                    packed[outRow + (x >> bits)] = 0xFF000000u | (acc << 8);
                    acc = 0;
                }
            }
        }

        return packed;
    }

    // ---------------------------------------------------------------- subtract green

    private static bool SubtractGreenHelps(ReadOnlySpan<uint> argb)
    {
        Span<uint> red = stackalloc uint[256], blue = stackalloc uint[256], red2 = stackalloc uint[256], blue2 = stackalloc uint[256];
        red.Clear(); blue.Clear(); red2.Clear(); blue2.Clear();
        int step = Math.Max(1, argb.Length / 65536);
        for (int i = 0; i < argb.Length; i += step)
        {
            uint c = argb[i];
            uint g = (c >> 8) & 0xFF;
            red[(int)((c >> 16) & 0xFF)]++;
            blue[(int)(c & 0xFF)]++;
            red2[(int)(((c >> 16) - g) & 0xFF)]++;
            blue2[(int)((c - g) & 0xFF)]++;
        }

        return Entropy.Bits(red2) + Entropy.Bits(blue2) < Entropy.Bits(red) + Entropy.Bits(blue);
    }

    private static void SubtractGreen(Span<uint> argb)
    {
        for (int i = 0; i < argb.Length; i++)
        {
            uint c = argb[i];
            uint g = (c >> 8) & 0xFF;
            uint r = ((c >> 16) - g) & 0xFF;
            uint b = (c - g) & 0xFF;
            argb[i] = (c & 0xFF00FF00u) | (r << 16) | b;
        }
    }

    // ---------------------------------------------------------------- pixel arithmetic (Section 4.1)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint SubPixels(uint a, uint b) =>
        (((a | 0x00FF00FFu) - (b & 0xFF00FF00u)) & 0xFF00FF00u) |
        (((a | 0xFF00FF00u) - (b & 0x00FF00FFu)) & 0x00FF00FFu);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint AddPixels(uint a, uint b) =>
        (((a & 0xFF00FF00u) + (b & 0xFF00FF00u)) & 0xFF00FF00u) |
        (((a & 0x00FF00FFu) + (b & 0x00FF00FFu)) & 0x00FF00FFu);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Average(uint a, uint b) => (a & b) + (((a ^ b) & 0xFEFEFEFEu) >> 1);

    private static uint Select(uint left, uint top, uint topLeft)
    {
        // Manhattan distances of the gradient estimate to top and to left (Section 4.1, mode 11).
        int pl = 0, pt = 0;
        for (int s = 0; s < 32; s += 8)
        {
            int l = (int)(left >> s) & 0xFF, t = (int)(top >> s) & 0xFF, tl = (int)(topLeft >> s) & 0xFF;
            pl += Math.Abs(t - tl);
            pt += Math.Abs(l - tl);
        }

        return pl < pt ? left : top;
    }

    private static uint ClampAddSubtractFull(uint a, uint b, uint c)
    {
        uint r = 0;
        for (int s = 0; s < 32; s += 8)
        {
            int v = (int)(a >> s & 0xFF) + (int)(b >> s & 0xFF) - (int)(c >> s & 0xFF);
            r |= (uint)Math.Clamp(v, 0, 255) << s;
        }

        return r;
    }

    private static uint ClampAddSubtractHalf(uint a, uint b)
    {
        uint r = 0;
        for (int s = 0; s < 32; s += 8)
        {
            int av = (int)(a >> s & 0xFF);
            int v = av + (av - (int)(b >> s & 0xFF)) / 2;
            r |= (uint)Math.Clamp(v, 0, 255) << s;
        }

        return r;
    }

    /// <summary>The prediction of mode 0..13 from the neighbours (interior pixels: x &gt; 0 and y &gt; 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Predict(int mode, uint left, uint top, uint topLeft, uint topRight) => mode switch
    {
        0 => 0xFF000000u,
        1 => left,
        2 => top,
        3 => topRight,
        4 => topLeft,
        5 => Average(Average(left, topRight), top),
        6 => Average(left, topLeft),
        7 => Average(left, top),
        8 => Average(topLeft, top),
        9 => Average(top, topRight),
        10 => Average(Average(left, topLeft), Average(top, topRight)),
        11 => Select(left, top, topLeft),
        12 => ClampAddSubtractFull(left, top, topLeft),
        _ => ClampAddSubtractHalf(Average(left, top), topLeft),
    };
}
