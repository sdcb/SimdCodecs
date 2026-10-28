using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>How a frame's area is treated once the frame has been shown (GIF89a §23, Graphic Control Extension).</summary>
internal enum GifDisposal : byte
{
    /// <summary>0 (unspecified) and 1 (do not dispose): the frame stays. Values 4..7, undefined, are read as this.</summary>
    Keep = 1,

    /// <summary>2: the frame's area is cleared to transparent black (not to the background colour: see <see cref="GifContainer"/>).</summary>
    Background = 2,

    /// <summary>3: the frame's area is restored to what it was before the frame was drawn.</summary>
    Previous = 3,
}

/// <summary>One image of a GIF: its rectangle, colour table, control extension and where its LZW data starts.</summary>
internal readonly record struct GifFrame(
    int X, int Y, int Width, int Height,
    bool Interlaced,
    int PaletteOffset, int PaletteCount,
    int MinCodeSize, int DataOffset,
    GifDisposal Disposal, int TransparentIndex, int DelayCentiseconds);

/// <summary>
/// The block structure of a GIF file (GIF89a): logical screen, global colour table, and every image with
/// the Graphic Control Extension that precedes it. No pixel data is decoded.
/// <para>
/// Decisions the specification leaves to the decoder, made as browsers make them:
/// </para>
/// <list type="bullet">
/// <item>The canvas starts transparent black, and disposal to background clears to transparent black: the
///   background colour index is not used.</item>
/// <item>A logical screen of zero width or height is the bounding box of the images.</item>
/// <item>The file may end early: the images read so far are kept, including one whose image data is cut
///   off (it is drawn as far as its data goes). A missing trailer, or an unknown block, ends the file the
///   same way. A file with no image is refused.</item>
/// <item>A Graphic Control Extension applies to the next graphic block only; a Plain Text Extension consumes
///   it and is otherwise ignored, as are comments and unknown application extensions.</item>
/// </list>
/// </summary>
internal sealed class GifContainer
{
    /// <summary>The largest canvas accepted, in pixels (as WebP's limit): 2^26, so a frame's RGBA is at most 256 MiB.</summary>
    public const long MaximumPixels = 1L << 26;

    private GifContainer(int width, int height, GifFrame[] frames, int? loopCount)
    {
        Width = width;
        Height = height;
        Frames = frames;
        LoopCount = loopCount;
    }

    public int Width { get; }

    public int Height { get; }

    public GifFrame[] Frames { get; }

    /// <summary>The NETSCAPE2.0 / ANIMEXTS1.0 loop count as stored (0: forever), or null without that extension.</summary>
    public int? LoopCount { get; }

    /// <summary>
    /// True when every frame covers the whole canvas and none has a transparent index: then every pixel of
    /// every composited frame is opaque, and the image is presented as RGB.
    /// </summary>
    public bool IsOpaque
    {
        get
        {
            foreach (var f in Frames)
            {
                if (f.TransparentIndex >= 0 || f.X > 0 || f.Y > 0 || f.X + f.Width < Width || f.Y + f.Height < Height)
                    return false;
            }
            return true;
        }
    }

    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 6 && (data[..6].SequenceEqual("GIF89a"u8) || data[..6].SequenceEqual("GIF87a"u8));

    /// <exception cref="InvalidDataException">Not a GIF, truncated before its first image, without an image, or
/// a canvas or image above <see cref="MaximumPixels"/>.</exception>
    public static GifContainer Read(ReadOnlySpan<byte> data)
    {
        if (!HasSignature(data))
            throw new InvalidDataException("Not a GIF: the GIF87a / GIF89a signature is missing.");
        if (data.Length < 13)
            throw new InvalidDataException("The GIF ends inside its logical screen descriptor.");

        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var packed = data[10];
        var pos = 13;
        int globalOffset = -1, globalCount = 0;
        if ((packed & 0x80) != 0)
        {
            globalCount = 2 << (packed & 7);
            if (data.Length - pos < globalCount * 3)
                throw new InvalidDataException("The GIF ends inside its global colour table.");
            globalOffset = pos;
            pos += globalCount * 3;
        }

        var frames = new List<GifFrame>();
        int? loopCount = null;
        // The pending Graphic Control Extension: disposal, transparent index, delay.
        var disposal = GifDisposal.Keep;
        int transparent = -1, delay = 0;

        while (pos < data.Length)
        {
            var introducer = data[pos];
            if (introducer == 0x3B) break; // trailer

            if (introducer == 0x21)
            {
                if (data.Length - pos < 2) break;
                var label = data[pos + 1];
                var body = pos + 2;
                if (label == 0xF9 && data.Length - body >= 6 && data[body] == 4)
                {
                    var flags = data[body + 1];
                    var method = (flags >> 2) & 7;
                    disposal = method is 2 or 3 ? (GifDisposal)method : GifDisposal.Keep;
                    delay = BinaryPrimitives.ReadUInt16LittleEndian(data[(body + 2)..]);
                    transparent = (flags & 1) != 0 ? data[body + 4] : -1;
                }
                else if (label == 0xFF && data.Length - body >= 12 && data[body] == 11
                         && (data.Slice(body + 1, 11).SequenceEqual("NETSCAPE2.0"u8) || data.Slice(body + 1, 11).SequenceEqual("ANIMEXTS1.0"u8)))
                {
                    // The looping sub-block: 3, then sub-block ID 1, then the count.
                    var sub = body + 12;
                    if (data.Length - sub >= 4 && data[sub] == 3 && data[sub + 1] == 1)
                        loopCount = BinaryPrimitives.ReadUInt16LittleEndian(data[(sub + 2)..]);
                }
                else if (label == 0x01)
                {
                    // A Plain Text Extension is a graphic rendering block: it consumes the pending control.
                    (disposal, transparent, delay) = (GifDisposal.Keep, -1, 0);
                }

                if (!TrySkipSubBlocks(data, body, out pos)) break;
                continue;
            }

            if (introducer != 0x2C) break; // not a block a GIF may contain: the file ends here

            // Image descriptor: left, top, width, height, packed; then the local colour table, if any, and
            // the LZW minimum code size.
            if (data.Length - pos < 11) break;
            int x = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 1)..]);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 3)..]);
            int w = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 5)..]);
            int h = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 7)..]);
            var imagePacked = data[pos + 9];
            pos += 10;
            int paletteOffset = globalOffset, paletteCount = globalCount;
            if ((imagePacked & 0x80) != 0)
            {
                paletteCount = 2 << (imagePacked & 7);
                if (data.Length - pos < paletteCount * 3 + 1) break;
                paletteOffset = pos;
                pos += paletteCount * 3;
            }
            if (pos >= data.Length) break;
            var minCodeSize = data[pos++];
            // Its LZW output is held whole before clipping, so it is bounded like the canvas.
            if ((long)w * h > MaximumPixels)
                throw new InvalidDataException($"A {w}x{h} GIF image is above the limit of {MaximumPixels:N0} pixels.");

            frames.Add(new GifFrame(x, y, w, h, (imagePacked & 0x40) != 0, paletteOffset, paletteCount,
                minCodeSize, pos, disposal, transparent, delay));
            (disposal, transparent, delay) = (GifDisposal.Keep, -1, 0);
            if (!TrySkipSubBlocks(data, pos, out pos)) break; // cut off inside its data: kept, drawn as far as it goes
        }

        if (frames.Count == 0)
            throw new InvalidDataException("The GIF holds no image.");

        if (width == 0 || height == 0)
        {
            foreach (var f in frames)
            {
                width = Math.Max(width, f.X + f.Width);
                height = Math.Max(height, f.Y + f.Height);
            }
        }
        if (width == 0 || height == 0)
            throw new InvalidDataException("The GIF's canvas is empty: its logical screen and every image are zero-sized.");
        if ((long)width * height > MaximumPixels)
            throw new InvalidDataException($"A {width}x{height} GIF canvas is above the limit of {MaximumPixels:N0} pixels.");

        return new GifContainer(width, height, [.. frames], loopCount);
    }

    /// <summary>
    /// Steps over a chain of data sub-blocks (a length byte, that many bytes, ..., then a zero) starting at
    /// <paramref name="pos"/>. False, with <paramref name="end"/> at the end of the data, when the file ends
    /// first.
    /// </summary>
    internal static bool TrySkipSubBlocks(ReadOnlySpan<byte> data, int pos, out int end)
    {
        while (pos < data.Length)
        {
            var size = data[pos];
            pos += 1 + size;
            if (size == 0)
            {
                end = pos;
                return true;
            }
        }
        end = data.Length;
        return false;
    }
}
