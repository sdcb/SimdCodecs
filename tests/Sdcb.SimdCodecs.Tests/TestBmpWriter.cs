using System.Buffers.Binary;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Hand-assembled bitmap streams for the variants no stock encoder writes: every header size, any mask, any
/// palette length, RLE streams with deltas and early ends, OS/2 Huffman 1D. It shares no code with the codec;
/// the layouts are written out from Microsoft's DIB documentation and IBM's OS/2 bitmap documentation, field by
/// field, so a test's expected pixels come from the test's own model rather than the decoder's.
/// </summary>
internal static class TestBmpWriter
{
    /// <summary>A whole file: <c>BITMAPFILEHEADER</c>, then the DIB parts. <paramref name="offBits"/> defaults to
    /// where the pixels start.</summary>
    public static byte[] File(byte[] header, byte[] afterHeader, byte[] pixels, uint? offBits = null)
    {
        var dib = Concat(header, afterHeader, pixels);
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), offBits ?? (uint)(14 + header.Length + afterHeader.Length));
        dib.CopyTo(file, 14);
        return file;
    }

    /// <summary><c>BITMAPCOREHEADER</c>: size 12, 16-bit width and height, planes, bit count.</summary>
    public static byte[] CoreHeader(int width, int height, int bitCount)
    {
        var h = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), (ushort)bitCount);
        return h;
    }

    /// <summary>
    /// A header of <paramref name="size"/> bytes in the <c>BITMAPINFOHEADER</c> family: 16..64 for OS/2 2.x (which
    /// stops after any field), 40, 52 and 56 (masks at 40), 108 (V4), 124 (V5). Fields past the size are dropped.
    /// </summary>
    public static byte[] InfoHeader(int size, int width, int height, int bitCount, uint compression, uint colorsUsed = 0,
        uint red = 0, uint green = 0, uint blue = 0, uint alpha = 0, uint colorSpace = 0, uint profileOffset = 0, uint profileSize = 0)
    {
        var h = new byte[Math.Max(size, 124)];
        var s = h.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)size);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(s[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], colorsUsed);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], red);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], green);
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], blue);
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], alpha);
        BinaryPrimitives.WriteUInt32LittleEndian(s[56..], colorSpace);
        BinaryPrimitives.WriteUInt32LittleEndian(s[112..], profileOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[116..], profileSize);
        return h[..size];
    }

    /// <summary>Masks as stored after a 40-byte header: red, green, blue (and alpha).</summary>
    public static byte[] Masks(params uint[] masks)
    {
        var b = new byte[4 * masks.Length];
        for (var i = 0; i < masks.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 * i), masks[i]);
        return b;
    }

    /// <summary>Palette entries (0xRRGGBB) as stored: blue, green, red, and a zero byte unless 3-byte entries.</summary>
    public static byte[] Palette(int[] rgb, int entrySize = 4)
    {
        var b = new byte[rgb.Length * entrySize];
        for (var i = 0; i < rgb.Length; i++)
        {
            b[i * entrySize] = (byte)rgb[i];
            b[i * entrySize + 1] = (byte)(rgb[i] >> 8);
            b[i * entrySize + 2] = (byte)(rgb[i] >> 16);
        }
        return b;
    }

    /// <summary>
    /// Uncompressed rows: <paramref name="value"/>(x, y) for each pixel (y counting from the top), packed
    /// <paramref name="bitCount"/> bits each, most significant first below 8 bits and little-endian above, rows in
    /// whole 32-bit words, bottom row first unless <paramref name="topDown"/>.
    /// </summary>
    public static byte[] Rows(int width, int height, int bitCount, Func<int, int, uint> value, bool topDown = false)
    {
        var stride = (width * bitCount + 31) / 32 * 4;
        var b = new byte[stride * height];
        for (var r = 0; r < height; r++)
        {
            var y = topDown ? r : height - 1 - r;
            var row = b.AsSpan(r * stride, stride);
            for (var x = 0; x < width; x++)
            {
                var v = value(x, y);
                if (bitCount < 8)
                {
                    var bit = x * bitCount;
                    row[bit / 8] |= (byte)(v << (8 - bitCount - bit % 8));
                }
                else
                {
                    for (var k = 0; k < bitCount / 8; k++) row[x * bitCount / 8 + k] = (byte)(v >> (8 * k));
                }
            }
        }
        return b;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var b = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts)
        {
            p.CopyTo(b, at);
            at += p.Length;
        }
        return b;
    }

    /// <summary>
    /// Builds an RLE4 / RLE8 / RLE24 stream op by op, painting what each op means into an expected image (RGBA,
    /// top row first) as Microsoft documents the codes: (n, v) paints n pixels, (0, 0) ends the line, (0, 1) the
    /// bitmap, (0, 2, dx, dy) moves right and up, (0, n, …) paints n literal pixels padded to 16 bits. Pixels no
    /// op paints stay opaque black; paint past the right edge is dropped.
    /// </summary>
    internal sealed class Rle(int width, int height, int bits, int[] palette)
    {
        private readonly List<byte> _stream = [];
        private int _x, _y; // y counts from the bottom, as the stream does

        public byte[] Expected { get; } = Black(width, height);

        public byte[] Stream => [.. _stream];

        private static byte[] Black(int width, int height)
        {
            var b = new byte[width * height * 4];
            for (var i = 3; i < b.Length; i += 4) b[i] = 255;
            return b;
        }

        private void Paint(int rgb)
        {
            if (_x < width && _y < height)
            {
                var i = ((height - 1 - _y) * width + _x) * 4;
                (Expected[i], Expected[i + 1], Expected[i + 2]) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
            _x++;
        }

        private int Colour(int index) => index < palette.Length ? palette[index] : 0;

        /// <summary>An encoded run: <paramref name="count"/> pixels of <paramref name="value"/> (RLE8), of the
        /// value's two nibbles alternating (RLE4), or of the colour 0xRRGGBB (RLE24).</summary>
        public Rle Run(int count, int value)
        {
            _stream.Add((byte)count);
            if (bits == 24)
            {
                _stream.AddRange([(byte)value, (byte)(value >> 8), (byte)(value >> 16)]);
                for (var i = 0; i < count; i++) Paint(value);
            }
            else
            {
                _stream.Add((byte)value);
                for (var i = 0; i < count; i++)
                    Paint(Colour(bits == 8 ? value : (i & 1) == 0 ? value >> 4 : value & 15));
            }
            return this;
        }

        /// <summary>Absolute mode: indices (RLE4 / RLE8) or 0xRRGGBB colours (RLE24), at least three.</summary>
        public Rle Literal(params int[] values)
        {
            _stream.AddRange([0, (byte)values.Length]);
            var bytes = new List<byte>();
            if (bits == 24)
            {
                foreach (var v in values) bytes.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16)]);
            }
            else if (bits == 8)
            {
                bytes.AddRange(values.Select(v => (byte)v));
            }
            else
            {
                for (var i = 0; i < values.Length; i += 2)
                    bytes.Add((byte)(values[i] << 4 | (i + 1 < values.Length ? values[i + 1] : 0)));
            }
            if (bytes.Count % 2 != 0) bytes.Add(0);
            _stream.AddRange(bytes);
            foreach (var v in values) Paint(bits == 24 ? v : Colour(v));
            return this;
        }

        public Rle EndOfLine()
        {
            _stream.AddRange([0, 0]);
            _x = 0;
            _y++;
            return this;
        }

        public Rle EndOfBitmap()
        {
            _stream.AddRange([0, 1]);
            return this;
        }

        public Rle Delta(int dx, int dy)
        {
            _stream.AddRange([0, 2, (byte)dx, (byte)dy]);
            _x += dx;
            _y += dy;
            return this;
        }
    }
}
