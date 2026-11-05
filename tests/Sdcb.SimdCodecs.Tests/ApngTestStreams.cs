using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Hand-assembled APNG streams for the tests: chunks written one by one (CRCs right, everything else as the test says), pixel
/// data deflated with filter 0 on every row, Adam7 passes laid out here, and a reference compositor that is the specification
/// written out naively (a whole-canvas copy for Previous, the OVER operation in exact rational arithmetic).
/// </summary>
internal sealed class ApngStream
{
    private readonly MemoryStream _ms = new();

    public ApngStream(int width, int height, byte depth, byte colorType, bool interlaced = false)
    {
        _ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = depth;
        ihdr[9] = colorType;
        ihdr[12] = interlaced ? (byte)1 : (byte)0;
        Raw("IHDR"u8, ihdr);
    }

    public ApngStream Raw(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        PngRgba8ExpansionTests.Chunk(_ms, type, data);
        return this;
    }

    public ApngStream Plte(byte[] rgb) => Raw("PLTE"u8, rgb);

    public ApngStream Trns(byte[] data) => Raw("tRNS"u8, data);

    public ApngStream Actl(uint frames, uint plays)
    {
        Span<byte> d = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(d, frames);
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], plays);
        return Raw("acTL"u8, d);
    }

    public ApngStream Fctl(uint sequence, uint width, uint height, uint x, uint y, ushort delayNum, ushort delayDen, byte dispose, byte blend)
    {
        Span<byte> d = stackalloc byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(d, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], width);
        BinaryPrimitives.WriteUInt32BigEndian(d[8..], height);
        BinaryPrimitives.WriteUInt32BigEndian(d[12..], x);
        BinaryPrimitives.WriteUInt32BigEndian(d[16..], y);
        BinaryPrimitives.WriteUInt16BigEndian(d[20..], delayNum);
        BinaryPrimitives.WriteUInt16BigEndian(d[22..], delayDen);
        d[24] = dispose;
        d[25] = blend;
        return Raw("fcTL"u8, d);
    }

    public ApngStream Idat(byte[] zlib) => Raw("IDAT"u8, zlib);

    public ApngStream Fdat(uint sequence, byte[] zlib) => Raw("fdAT"u8, [.. SequenceBytes(sequence), .. zlib]);

    public byte[] End()
    {
        Raw("IEND"u8, []);
        return _ms.ToArray();
    }

    private static byte[] SequenceBytes(uint sequence)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, sequence);
        return b;
    }

    public static byte[] Zlib(ReadOnlySpan<byte> raw, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Filter-0 rows of <paramref name="bytesPerPixel"/>-byte pixels, deflated: <paramref name="pixel"/> gives the bytes of the pixel at (x, y).</summary>
    public static byte[] Rows(int width, int height, int bytesPerPixel, Func<int, int, byte[]> pixel, bool interlaced = false)
    {
        var raw = new List<byte>();
        if (!interlaced)
        {
            for (var y = 0; y < height; y++)
            {
                raw.Add(0);
                for (var x = 0; x < width; x++)
                    raw.AddRange(pixel(x, y));
            }
        }
        else
        {
            (int X0, int Y0, int Dx, int Dy)[] passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
            foreach (var (x0, y0, dx, dy) in passes)
            {
                if (x0 >= width || y0 >= height)
                    continue;
                for (var y = y0; y < height; y += dy)
                {
                    raw.Add(0);
                    for (var x = x0; x < width; x += dx)
                        raw.AddRange(pixel(x, y));
                }
            }
        }
        return Zlib([.. raw]);
    }

    // ---------------------------------------------------------------- the reference compositor

    /// <summary>One frame for <see cref="Composite"/>: a rectangle of RGBA samples (8-bit values, or 16-bit ones when <c>max</c> is 65535).</summary>
    public sealed record Frame(int X, int Y, int Width, int Height, byte Dispose, byte Blend, Func<int, int, int[]> Pixel);

    /// <summary>
    /// The canvas after each frame, by the specification read literally: a transparent canvas, frames drawn with SOURCE (replace)
    /// or OVER (exact rational arithmetic, rounded half up), the whole canvas copied before a frame whose dispose is Previous.
    /// </summary>
    public static List<int[]> Composite(int width, int height, int max, IReadOnlyList<Frame> frames)
    {
        var canvas = new int[width * height * 4];
        var shown = new List<int[]>();
        int[]? before = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (i > 0)
            {
                var p = frames[i - 1];
                for (var y = p.Y; y < p.Y + p.Height; y++)
                {
                    for (var x = p.X; x < p.X + p.Width; x++)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            var o = (y * width + x) * 4 + c;
                            if (p.Dispose == 1 || (p.Dispose == 2 && i - 1 == 0))
                                canvas[o] = 0;
                            else if (p.Dispose == 2)
                                canvas[o] = before![o];
                        }
                    }
                }
            }
            before = (int[])canvas.Clone();
            for (var y = 0; y < f.Height; y++)
            {
                for (var x = 0; x < f.Width; x++)
                {
                    var s = f.Pixel(x, y);
                    var o = ((f.Y + y) * width + f.X + x) * 4;
                    if (f.Blend == 0)
                    {
                        for (var c = 0; c < 4; c++)
                            canvas[o + c] = s[c];
                    }
                    else
                    {
                        var (r, g, b, a) = Over(s, canvas.AsSpan(o, 4), max);
                        canvas[o] = r;
                        canvas[o + 1] = g;
                        canvas[o + 2] = b;
                        canvas[o + 3] = a;
                    }
                }
            }
            shown.Add((int[])canvas.Clone());
        }
        return shown;
    }

    // Straight-alpha OVER of a source pixel on a backdrop: ao = as + ab(1 − as), co = (as·cs + (1 − as)·ab·cb) / ao, with every
    // sample a fraction of max, the exact value rounded to the nearest integer (halves up). A transparent source leaves the backdrop.
    private static (int, int, int, int) Over(int[] s, ReadOnlySpan<int> b, int max)
    {
        BigInteger m = max, sa = s[3], ba = b[3];
        if (sa == 0)
            return (b[0], b[1], b[2], b[3]);
        if (sa == m)
            return (s[0], s[1], s[2], s[3]);
        var x = sa * m + ba * (m - sa); // ao · m
        int Alpha() => (int)((2 * x + m) / (2 * m));
        int Colour(int cs, int cb)
        {
            var n = cs * sa * m + cb * ba * (m - sa);
            return (int)((2 * n + x) / (2 * x));
        }
        return (Colour(s[0], b[0]), Colour(s[1], b[1]), Colour(s[2], b[2]), Alpha());
    }
}
