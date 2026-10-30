using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>Options for <see cref="GifEncoder"/>.</summary>
public sealed record GifEncodeOptions
{
    /// <summary>
    /// Floyd–Steinberg error diffusion when the image has more colours than a GIF palette holds, trading
    /// banding for fine noise (and larger files). Ignored when every colour fits: the palette is then exact.
    /// </summary>
    public bool Dither { get; init; }

    /// <summary>
    /// For an animation, how many times it plays: 0 (the default) is forever. Written as the NETSCAPE2.0
    /// count of repetitions, <c>n − 1</c>; 1 writes no loop extension, which plays once. Ignored for a still.
    /// </summary>
    public int LoopCount { get; init; }
}

/// <summary>One frame of an animation to encode: the whole canvas, and how long it is shown.</summary>
/// <param name="Pixels">The canvas, <c>width × height × channels</c> bytes, as given to
/// <see cref="GifEncoder.EncodeAnimation"/>.</param>
/// <param name="Duration">How long the frame is shown; stored in hundredths of a second (rounded, at most 655.35 s).</param>
public readonly record struct GifAnimationFrame(ReadOnlyMemory<byte> Pixels, TimeSpan Duration);

/// <summary>
/// Pure-managed GIF89a encoder, for stills and animations. GIF stores at most 256 colours and 1-bit
/// transparency, so:
/// <list type="bullet">
/// <item>Alpha below 128 is transparent, the rest opaque.</item>
/// <item>One global palette serves every frame (colours stay put across an animation). When the opaque
///   colours of all frames fit (256, or 255 beside a transparent index), it is exact and the encode is
///   lossless; otherwise it is built by Wu's variance-minimising quantizer, with optional Floyd–Steinberg
///   dithering (<see cref="GifEncodeOptions.Dither"/>).</item>
/// <item>An opaque animation writes each frame as the rectangle that changed, its unchanged pixels as the
///   transparent index when the palette has room for one. An animation with transparency writes every frame
///   whole, each cleared before the next, so transparent pixels show through.</item>
/// </list>
/// Input is 1 (grey), 3 (RGB) or 4 (RGBA) channels of 8 bits, tightly packed.
/// </summary>
public static class GifEncoder
{
    /// <summary>Encodes a still image.</summary>
    /// <exception cref="ArgumentException">The size, channel count or pixel buffer does not fit.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int channels, GifEncodeOptions? options = null)
    {
        Validate(pixels.Length, width, height, channels, nameof(pixels));
        return Write([ToArgb(pixels, width * height, channels)], [TimeSpan.Zero], width, height, animated: false,
            options ?? new GifEncodeOptions());
    }

    /// <summary>Encodes an animation: <paramref name="frames"/> in order, each the whole canvas.</summary>
    /// <exception cref="ArgumentException">No frames, or a size, channel count or frame buffer that does not fit.</exception>
    public static byte[] EncodeAnimation(IReadOnlyList<GifAnimationFrame> frames, int width, int height, int channels,
        GifEncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0) throw new ArgumentException("An animation needs at least one frame.", nameof(frames));
        var argb = new int[frames.Count][];
        var durations = new TimeSpan[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            Validate(frames[i].Pixels.Length, width, height, channels, nameof(frames));
            if (frames[i].Duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(frames), frames[i].Duration, $"Frame {i} has a negative duration.");
            argb[i] = ToArgb(frames[i].Pixels.Span, width * height, channels);
            durations[i] = frames[i].Duration;
        }
        return Write(argb, durations, width, height, animated: true, options ?? new GifEncodeOptions());
    }

    internal static bool IsTransparent(int argb) => PaletteQuantizer.IsTransparent(argb);

    private static void Validate(int length, int width, int height, int channels, string name)
    {
        if (width is < 1 or > 65535 || height is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(width), $"A GIF is 1..65535 pixels on each side, not {width}x{height}.");
        if ((long)width * height > GifContainer.MaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width), $"{width}x{height} is above the limit of {GifContainer.MaximumPixels:N0} pixels.");
        if (channels is not (1 or 3 or 4))
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1 (grey), 3 (RGB) or 4 (RGBA).");
        if (length < (long)width * height * channels)
            throw new ArgumentException($"{width}x{height}x{channels} needs {(long)width * height * channels} bytes, got {length}.", name);
    }

    // 0xAARRGGBB per pixel.
    private static int[] ToArgb(ReadOnlySpan<byte> pixels, int count, int channels)
    {
        var argb = new int[count];
        for (var i = 0; i < count; i++)
        {
            argb[i] = channels switch
            {
                1 => unchecked((int)0xFF000000) | (pixels[i] * 0x010101),
                3 => unchecked((int)0xFF000000) | (pixels[3 * i] << 16) | (pixels[3 * i + 1] << 8) | pixels[3 * i + 2],
                _ => (pixels[4 * i + 3] << 24) | (pixels[4 * i] << 16) | (pixels[4 * i + 1] << 8) | pixels[4 * i + 2],
            };
        }
        return argb;
    }

    private static byte[] Write(int[][] frames, TimeSpan[] durations, int width, int height, bool animated, GifEncodeOptions options)
    {
        var anyTransparent = frames.Any(f => f.Any(IsTransparent));
        // A transparent index is wanted for transparent pixels, and in an opaque animation for the pixels a
        // frame leaves unchanged (worth having, not required).
        var wantsTransparentIndex = anyTransparent || (animated && frames.Length > 1);

        var (palette, exact) = ChoosePalette(frames, anyTransparent ? 255 : 256, wantsTransparentIndex ? 255 : 256);
        var transparentIndex = palette.Length < 256 && wantsTransparentIndex ? palette.Length : -1;
        var tableBits = Math.Max(1, BitsFor(palette.Length + (transparentIndex >= 0 ? 1 : 0)));
        var minCodeSize = Math.Max(2, tableBits);

        var indices = new byte[frames.Length][];
        var exactIndex = exact ? palette.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => (byte)p.i) : null;
        var mapper = exact ? null : new PaletteQuantizer.Mapper(palette);
        for (var f = 0; f < frames.Length; f++)
        {
            var map = new byte[width * height];
            if (exactIndex is not null)
            {
                for (var i = 0; i < map.Length; i++)
                    map[i] = IsTransparent(frames[f][i]) ? (byte)transparentIndex : exactIndex[frames[f][i] & 0xFFFFFF];
            }
            else
            {
                mapper!.Map(frames[f], width, height, map, transparentIndex, options.Dither);
            }
            indices[f] = map;
        }

        var output = new MemoryStream();
        output.Write("GIF89a"u8);
        U16(output, width);
        U16(output, height);
        output.WriteByte((byte)(0x80 | ((tableBits - 1) << 4) | (tableBits - 1))); // global table, its depth as the colour resolution
        output.WriteByte(0); // background colour index: not used by viewers for disposal
        output.WriteByte(0); // no aspect ratio
        var table = new byte[3 << tableBits];
        for (var i = 0; i < palette.Length; i++)
            (table[3 * i], table[3 * i + 1], table[3 * i + 2]) = ((byte)(palette[i] >> 16), (byte)(palette[i] >> 8), (byte)palette[i]);
        output.Write(table);

        if (animated && frames.Length > 1 && options.LoopCount != 1)
        {
            // NETSCAPE2.0 counts repetitions after the first play; 0 is forever.
            var repeats = options.LoopCount <= 0 ? 0 : Math.Min(options.LoopCount - 1, 65535);
            output.Write([0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1, (byte)repeats, (byte)(repeats >> 8), 0]);
        }

        for (var f = 0; f < frames.Length; f++)
        {
            var (x, y, w, h, pixels, usesTransparent) = FrameRegion(indices, f, width, height, anyTransparent, transparentIndex);
            var delay = (int)Math.Min(65535, Math.Round(durations[f].TotalMilliseconds / 10, MidpointRounding.AwayFromZero));
            if (animated || usesTransparent)
            {
                // An animation with transparency clears each frame (disposal 2) so the next one's transparent
                // pixels show the empty canvas; an opaque one keeps it (1), drawing only what changed.
                var disposal = anyTransparent ? 2 : 1;
                output.Write([0x21, 0xF9, 4, (byte)((disposal << 2) | (usesTransparent ? 1 : 0))]);
                U16(output, animated ? delay : 0);
                output.Write([(byte)Math.Max(0, transparentIndex), 0]);
            }
            output.WriteByte(0x2C);
            U16(output, x);
            U16(output, y);
            U16(output, w);
            U16(output, h);
            output.WriteByte(0); // no local table, not interlaced
            output.WriteByte((byte)minCodeSize);
            GifLzwEncoder.Encode(pixels, minCodeSize, output);
        }
        output.WriteByte(0x3B);
        return output.ToArray();
    }

    // The exact palette when the opaque colours fit `exactLimit`, else a quantized one of `quantizedLimit`.
    private static (int[] Palette, bool Exact) ChoosePalette(int[][] frames, int exactLimit, int quantizedLimit)
    {
        var colours = new HashSet<int>();
        foreach (var frame in frames)
        {
            foreach (var p in frame)
            {
                if (!IsTransparent(p) && colours.Add(p & 0xFFFFFF) && colours.Count > exactLimit) goto quantize;
            }
        }
        // At least one entry, so a wholly transparent image still has a table.
        return (colours.Count == 0 ? [0] : [.. colours.Order()], true);

    quantize:
        var quantizer = new PaletteQuantizer();
        foreach (var frame in frames)
        {
            foreach (var p in frame)
                if (!IsTransparent(p)) quantizer.Add(p & 0xFFFFFF);
        }
        return (quantizer.BuildPalette(quantizedLimit), false);
    }

    // What frame f writes: the whole canvas for a still, the first frame or an animation with transparency;
    // otherwise the bounding box of the pixels that differ from the frame before, its unchanged pixels as
    // the transparent index when there is one.
    private static (int X, int Y, int W, int H, byte[] Pixels, bool UsesTransparent) FrameRegion(
        byte[][] indices, int f, int width, int height, bool anyTransparent, int transparentIndex)
    {
        var current = indices[f];
        if (f == 0 || anyTransparent)
            return (0, 0, width, height, current, anyTransparent && transparentIndex >= 0);

        var previous = indices[f - 1];
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (current[y * width + x] == previous[y * width + x]) continue;
                x0 = Math.Min(x0, x);
                x1 = Math.Max(x1, x);
                y0 = Math.Min(y0, y);
                y1 = Math.Max(y1, y);
            }
        }
        if (x1 < 0)
        {
            // Nothing changed: a GIF frame still needs an image, so one unchanged pixel.
            return (0, 0, 1, 1, [transparentIndex >= 0 ? (byte)transparentIndex : current[0]], transparentIndex >= 0);
        }

        int w = x1 - x0 + 1, h = y1 - y0 + 1;
        var region = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y0 + y) * width + x0 + x;
                region[y * w + x] = transparentIndex >= 0 && current[i] == previous[i] ? (byte)transparentIndex : current[i];
            }
        }
        return (x0, y0, w, h, region, transparentIndex >= 0);
    }

    // Bits for a colour table of at least `entries` (a GIF table has 2^1 .. 2^8 entries).
    private static int BitsFor(int entries)
    {
        var bits = 1;
        while ((1 << bits) < entries) bits++;
        return bits;
    }

    private static void U16(Stream s, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
        s.Write(b);
    }
}
