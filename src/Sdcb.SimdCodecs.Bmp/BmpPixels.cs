using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// Decodes a bitmap's pixels (<see cref="BmpHeader"/> has checked its layout) straight into the caller's
/// buffer, in any <see cref="ImagePixelFormat"/>: rows are written top row first whichever way they are stored,
/// and nothing is allocated beyond a pooled row for the layouts that go through <see cref="PixelConverter"/>.
/// <para>
/// What the specifications leave open is settled as Windows' own decoder (WIC) settles it, and recorded in the
/// package README: a channel narrower than 8 bits widens by repeating its bits and a wider one keeps its top 8;
/// an index past the palette is opaque black; so is every pixel an RLE stream skips (by a delta, an early end
/// of line or bitmap, or running out of data). One choice differs from WIC, following browsers, ImageMagick
/// and ImageSharp: the fourth byte of a 32-bit <c>BI_RGB</c> pixel is alpha unless it is 0 in every pixel.
/// </para>
/// </summary>
internal static class BmpPixels
{
    /// <summary>Decodes into <paramref name="destination"/>, rows <paramref name="rowStride"/> bytes apart (checked by the caller).</summary>
    /// <exception cref="InvalidDataException">The pixel data is corrupt.</exception>
    public static void Decode(ReadOnlySpan<byte> data, BmpHeader h, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        switch (h.Compression)
        {
            case BmpCompression.Png:
                DecodePng(data, h, destination, rowStride, format);
                return;
            case BmpCompression.Rle8:
            case BmpCompression.Rle4:
            case BmpCompression.Rle24:
                DecodeRle(data, h, destination, rowStride, format);
                return;
            case BmpCompression.Huffman1D:
                DecodeHuffman(data, h, destination, rowStride, format);
                return;
        }

        var seenAlpha = DecodeUncompressed(data, h, destination, rowStride, format) >> 24;
        var hasAlphaByte = format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32;
        if (!hasAlphaByte) return;
        var alphaIsReal = h.AlphaMask != 0 && !(h.AlphaUnlessAllZero && seenAlpha == 0);
        if (h.IconMask && !alphaIsReal)
            ApplyAndMask(data, h, destination, rowStride);
        else if (h.AlphaUnlessAllZero && seenAlpha == 0)
            SetOpaque(destination, h.Width, h.Height, rowStride);
    }

    /// <summary>The PNG stream of a <c>BI_PNG</c> bitmap, which must be the size the header says.</summary>
    public static ImageInfo ReadPngInfo(ReadOnlySpan<byte> data, BmpHeader h)
    {
        if (!PngImageDecoder.TryReadInfo(data[h.PixelOffset..], out var info))
            throw new InvalidDataException("The BI_PNG pixel data is not a PNG stream.");
        if (info.Width != h.Width || info.Height != h.Height)
            throw new InvalidDataException($"The BI_PNG stream is {info.Width}x{info.Height}, the header says {h.Width}x{h.Height}.");
        return info;
    }

    private static void DecodePng(ReadOnlySpan<byte> data, BmpHeader h, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        ReadPngInfo(data, h);
        if (!PngImageDecoder.TryDecodeInto(data[h.PixelOffset..], destination, rowStride, format))
            throw new InvalidDataException("The BI_PNG stream does not decode.");
    }

    // ---- Uncompressed ------------------------------------------------------------------------------------------

    private enum RowKind { Indexed, Copy24, Expand24, Swap24, Shuffle32, Pack32, Expand16, Fields }

    /// <returns>Every decoded pixel ORed together in the output's 32-bit layout, for 4-byte outputs (so its top
    /// byte, alpha in both RGBA and BGRA, says whether any pixel had alpha); 0 otherwise.</returns>
    private static uint DecodeUncompressed(ReadOnlySpan<byte> data, BmpHeader h, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        int width = h.Width, height = h.Height, stride = h.Stride, bpp = h.BitsPerPixel;
        var outBytes = PixelConverter.BytesPerPixel(format);
        var tier = BmpKernels.BestTier;

        // The row kernel, chosen once. A layout without one decodes to RGBA in a pooled row, then converts.
        var target = format;
        Span<byte> control = stackalloc byte[4];
        var kind = Choose(h, format, control, out var set);
        if (kind is null)
        {
            target = ImagePixelFormat.Rgba32;
            kind = Choose(h, target, control, out set) ?? RowKind.Fields;
        }
        Span<uint> lut = stackalloc uint[bpp <= 8 ? 256 : 0];
        if (bpp <= 8) BuildPalette(data, h, target, lut);
        var fields = kind == RowKind.Fields ? new Fields(h) : null;
        byte[]? scratch = target == format ? null : ArrayPool<byte>.Shared.Rent(width * 4);
        uint seen = 0;
        try
        {
            for (var r = 0; r < height; r++)
            {
                var offset = h.PixelOffset + r * stride;
                var source = data.Slice(offset, Math.Min(stride, data.Length - offset));
                var y = h.TopDown ? r : height - 1 - r;
                var row = destination.Slice(y * rowStride, width * outBytes);
                var output = scratch is null ? row : scratch.AsSpan(0, width * 4);
                switch (kind)
                {
                    case RowKind.Indexed:
                        ExpandIndexed(source, output, width, bpp, lut, PixelConverter.BytesPerPixel(target));
                        break;
                    case RowKind.Copy24:
                        source[..(3 * width)].CopyTo(output);
                        break;
                    case RowKind.Expand24:
                        BmpKernels.Expand24(source, output, width, swap: target == ImagePixelFormat.Rgba32, tier);
                        break;
                    case RowKind.Swap24:
                        BmpKernels.Swap24(source, output, width, tier);
                        break;
                    case RowKind.Shuffle32:
                        seen |= BmpKernels.Shuffle32(source, output, width, control, set, tier);
                        break;
                    case RowKind.Pack32:
                        BmpKernels.Pack32To24(source, output, width, control, tier);
                        break;
                    case RowKind.Expand16:
                        BmpKernels.Expand16(source, output, width, green6: h.GreenMask == 0x07E0,
                            rgba: target == ImagePixelFormat.Rgba32, BmpKernels.BestArithmeticTier);
                        break;
                    default:
                        seen |= fields!.Decode(source, output, width, bpp);
                        break;
                }
                if (scratch is not null)
                    PixelConverter.ConvertRow(output, 4, SampleFormat.UInt8, row, format, width);
            }
        }
        finally
        {
            if (scratch is not null) ArrayPool<byte>.Shared.Return(scratch);
        }
        return outBytes == 4 ? seen : 0;
    }

    // The kernel for this bitmap and output, or null when the output needs the RGBA detour.
    private static RowKind? Choose(BmpHeader h, ImagePixelFormat format, Span<byte> control, out uint set)
    {
        set = 0;
        var bpp = h.BitsPerPixel;
        if (bpp <= 8) return RowKind.Indexed;
        if (format == ImagePixelFormat.Gray8) return null;
        var four = format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32;
        var rgbOrder = format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Rgb24;
        switch (bpp)
        {
            case 24:
                return format switch
                {
                    ImagePixelFormat.Bgr24 => RowKind.Copy24,
                    ImagePixelFormat.Rgb24 => RowKind.Swap24,
                    _ => RowKind.Expand24,
                };
            case 32:
            {
                // Byte-aligned masks are a byte permutation: each output byte names its source byte.
                int r = ByteOf(h.RedMask), g = ByteOf(h.GreenMask), b = ByteOf(h.BlueMask), a = ByteOf(h.AlphaMask);
                if (r < 0 || g < 0 || b < 0 || a < 0) return format == ImagePixelFormat.Rgba32 ? RowKind.Fields : null;
                byte first = (byte)(rgbOrder ? r : b), third = (byte)(rgbOrder ? b : r);
                if (four)
                {
                    (control[0], control[1], control[2], control[3]) = (first, (byte)g, third, (byte)(a == 4 ? 0x80 : a));
                    set = a == 4 ? 0xFF000000u : 0;
                    return RowKind.Shuffle32;
                }
                // Absent colour channels are rare enough to take the detour.
                if (r == 4 || g == 4 || b == 4) return null;
                (control[0], control[1], control[2]) = (first, (byte)g, third);
                return RowKind.Pack32;
            }
            default: // 16
            {
                var plain = h.AlphaMask == 0 && h.BlueMask == 0x001F
                    && (h.RedMask, h.GreenMask) is (0xF800, 0x07E0) or (0x7C00, 0x03E0);
                if (plain && four) return RowKind.Expand16;
                return format == ImagePixelFormat.Rgba32 ? RowKind.Fields : null;
            }
        }
    }

    // The byte a mask covers exactly (0..3), 4 for an absent channel, -1 for any other mask.
    private static int ByteOf(uint mask) => mask switch
    {
        0 => 4,
        0x000000FF => 0,
        0x0000FF00 => 1,
        0x00FF0000 => 2,
        0xFF000000 => 3,
        _ => -1,
    };

    /// <summary>Arbitrary channel masks, a pixel at a time, to RGBA or BGRA.</summary>
    private sealed class Fields
    {
        private readonly Channel _r, _g, _b, _a;

        public Fields(BmpHeader h)
        {
            _r = new Channel(h.RedMask);
            _g = new Channel(h.GreenMask);
            _b = new Channel(h.BlueMask);
            _a = new Channel(h.AlphaMask);
        }

        // Output is RGBA (BGRA output never comes here: it has a kernel or detours through RGBA).
        public uint Decode(ReadOnlySpan<byte> source, Span<byte> output, int width, int bpp)
        {
            uint seen = 0;
            for (var x = 0; x < width; x++)
            {
                var p = bpp == 16
                    ? (uint)(source[2 * x] | source[2 * x + 1] << 8)
                    : (uint)(source[4 * x] | source[4 * x + 1] << 8 | source[4 * x + 2] << 16 | source[4 * x + 3] << 24);
                var a = _a.Present ? _a.Get(p) : 255u;
                var pixel = _r.Get(p) | _g.Get(p) << 8 | _b.Get(p) << 16 | a << 24;
                seen |= pixel;
                output[4 * x] = (byte)pixel;
                output[4 * x + 1] = (byte)(pixel >> 8);
                output[4 * x + 2] = (byte)(pixel >> 16);
                output[4 * x + 3] = (byte)(pixel >> 24);
            }
            return seen;
        }
    }

    /// <summary>One channel mask: its value widened to 8 bits by repeating its bits, or narrowed to its top 8.</summary>
    private readonly struct Channel
    {
        private readonly int _shift, _bits;
        private readonly byte[]? _widen;

        public Channel(uint mask)
        {
            if (mask == 0) return;
            _shift = BitOperations.TrailingZeroCount(mask);
            _bits = BitOperations.PopCount(mask);
            if (_bits < 8)
            {
                _widen = new byte[1 << _bits];
                for (var v = 0; v < _widen.Length; v++) _widen[v] = Widen(v, _bits);
            }
        }

        public bool Present => _bits != 0;

        public uint Get(uint pixel)
        {
            if (_bits == 0) return 0;
            var v = (pixel >> _shift) & (uint)((1L << _bits) - 1);
            return _widen is not null ? _widen[v] : v >> (_bits - 8);
        }
    }

    /// <summary>A <paramref name="bits"/>-bit value (1..7) to 8 bits by repeating its bits from the top down.</summary>
    internal static byte Widen(int value, int bits)
    {
        var result = 0;
        for (var shift = 8 - bits; shift > -bits; shift -= bits)
            result |= shift >= 0 ? value << shift : value >> -shift;
        return (byte)result;
    }

    // ---- Palettes ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The palette as 32-bit values in the output's byte order (R, G, B, 255 / B, G, R, 255 / BT.601 luma), all
    /// 256 entries: those past the bitmap's palette are opaque black.
    /// </summary>
    internal static void BuildPalette(ReadOnlySpan<byte> data, BmpHeader h, ImagePixelFormat format, Span<uint> lut)
    {
        lut[..256].Fill(Pack(0, 0, 0, format));
        for (var i = 0; i < h.PaletteCount; i++)
        {
            var e = data.Slice(h.PaletteOffset + i * h.PaletteEntrySize, 3);
            lut[i] = Pack(e[2], e[1], e[0], format);
        }
    }

    /// <summary>A colour in <paramref name="format"/>'s byte order, as a little-endian 32-bit value.</summary>
    internal static uint Pack(int r, int g, int b, ImagePixelFormat format) => format switch
    {
        ImagePixelFormat.Rgba32 => (uint)(r | g << 8 | b << 16) | 0xFF000000u,
        ImagePixelFormat.Bgra32 => (uint)(b | g << 8 | r << 16) | 0xFF000000u,
        ImagePixelFormat.Rgb24 => (uint)(r | g << 8 | b << 16),
        ImagePixelFormat.Bgr24 => (uint)(b | g << 8 | r << 16),
        _ => (uint)((77 * r + 150 * g + 29 * b + 128) >> 8),
    };

    /// <summary>Palette indices (1, 2, 4 or 8 bits, most significant first) to <paramref name="outBytes"/>-byte pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ExpandIndexed(ReadOnlySpan<byte> source, Span<byte> destination, int width, int bpp, ReadOnlySpan<uint> lut, int outBytes)
    {
        if (source.Length < ((long)width * bpp + 7) / 8 || destination.Length < (long)width * outBytes || lut.Length < 256)
            throw new ArgumentException("The spans are shorter than the row.");
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        ref var table = ref MemoryMarshal.GetReference(lut);
        // Every index is a byte, or a part of one, so it is below 256 = lut.Length; source and destination
        // indices stay below the row lengths checked above.
        var perByte = 8 / bpp;
        var mask = (1 << bpp) - 1;
        switch (outBytes)
        {
            case 4:
                if (bpp == 8)
                {
                    for (var x = 0; x < width; x++)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 4 * x), Unsafe.Add(ref table, Unsafe.Add(ref s, x)));
                    break;
                }
                for (var x = 0; x < width; x++)
                {
                    var index = (Unsafe.Add(ref s, x / perByte) >> (8 - bpp - bpp * (x % perByte))) & mask;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 4 * x), Unsafe.Add(ref table, index));
                }
                break;
            case 3:
                // Four-byte stores, each overwriting the next pixel's first byte, except the last pixel's.
                for (var x = 0; x < width; x++)
                {
                    var index = bpp == 8 ? Unsafe.Add(ref s, x) : (Unsafe.Add(ref s, x / perByte) >> (8 - bpp - bpp * (x % perByte))) & mask;
                    var v = Unsafe.Add(ref table, index);
                    if (x + 1 < width)
                    {
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 3 * x), v);
                    }
                    else
                    {
                        Unsafe.Add(ref d, 3 * x) = (byte)v;
                        Unsafe.Add(ref d, 3 * x + 1) = (byte)(v >> 8);
                        Unsafe.Add(ref d, 3 * x + 2) = (byte)(v >> 16);
                    }
                }
                break;
            default:
                for (var x = 0; x < width; x++)
                {
                    var index = bpp == 8 ? Unsafe.Add(ref s, x) : (Unsafe.Add(ref s, x / perByte) >> (8 - bpp - bpp * (x % perByte))) & mask;
                    Unsafe.Add(ref d, x) = (byte)Unsafe.Add(ref table, index);
                }
                break;
        }
    }

    // ---- Alpha -------------------------------------------------------------------------------------------------

    private static void SetOpaque(Span<byte> destination, int width, int height, int rowStride)
    {
        for (var y = 0; y < height; y++)
        {
            var row = destination.Slice(y * rowStride, width * 4);
            for (var x = 3; x < row.Length; x += 4) row[x] = 255;
        }
    }

    /// <summary>An icon image's transparency: the AND mask's set bits are transparent, the rest opaque.</summary>
    private static void ApplyAndMask(ReadOnlySpan<byte> data, BmpHeader h, Span<byte> destination, int rowStride)
    {
        int width = h.Width, height = h.Height, maskStride = (width + 31) / 32 * 4;
        for (var r = 0; r < height; r++)
        {
            var y = h.TopDown ? r : height - 1 - r;
            var row = destination.Slice(y * rowStride, width * 4);
            if (h.AndMaskOffset < 0)
            {
                for (var x = 3; x < row.Length; x += 4) row[x] = 255;
                continue;
            }
            var mask = data.Slice(h.AndMaskOffset + r * maskStride, (width + 7) / 8);
            for (var x = 0; x < width; x++)
                row[4 * x + 3] = (mask[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
        }
    }

    // ---- Run-length coding (RLE8, RLE4, OS/2 RLE24) --------------------------------------------------------------

    /// <summary>
    /// Writes runs into the output, rows stored bottom-up (compressed bitmaps are never top-down). Pixels the
    /// stream never reaches stay opaque black; runs past the right edge are clipped.
    /// </summary>
    private ref struct Canvas
    {
        private readonly Span<byte> _destination;
        private readonly int _rowStride, _bytes, _width, _height;

        public Canvas(Span<byte> destination, int rowStride, ImagePixelFormat format, int width, int height)
        {
            _destination = destination;
            _rowStride = rowStride;
            _bytes = PixelConverter.BytesPerPixel(format);
            _width = width;
            _height = height;
            var black = Pack(0, 0, 0, format);
            for (var y = 0; y < height; y++) Fill(destination.Slice(y * rowStride, width * _bytes), black, width);
        }

        public void Put(int x, int y, uint value, int count)
        {
            if (y >= _height || x >= _width) return;
            count = Math.Min(count, _width - x);
            var row = _destination.Slice((_height - 1 - y) * _rowStride + x * _bytes, count * _bytes);
            Fill(row, value, count);
        }

        private void Fill(Span<byte> span, uint value, int count)
        {
            switch (_bytes)
            {
                case 4:
                    MemoryMarshal.Cast<byte, uint>(span[..(count * 4)]).Fill(value); // unaligned access is fine on every target .NET runs
                    break;
                case 1:
                    span[..count].Fill((byte)value);
                    break;
                default:
                    for (var i = 0; i < count; i++)
                    {
                        span[3 * i] = (byte)value;
                        span[3 * i + 1] = (byte)(value >> 8);
                        span[3 * i + 2] = (byte)(value >> 16);
                    }
                    break;
            }
        }
    }

    private static void DecodeRle(ReadOnlySpan<byte> data, BmpHeader h, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        var canvas = new Canvas(destination, rowStride, format, h.Width, h.Height);
        Span<uint> lut = stackalloc uint[256];
        if (h.Compression != BmpCompression.Rle24) BuildPalette(data, h, format, lut);
        var s = data[h.PixelOffset..];
        int pos = 0, x = 0, y = 0;
        // Each step consumes at least two bytes, so the stream's length bounds the work.
        while (pos + 1 < s.Length && y < h.Height)
        {
            int count = s[pos], code = s[pos + 1];
            pos += 2;
            if (count > 0)
            {
                // An encoded run: one index (RLE8), two alternating ones (RLE4), or a B, G, R triple (RLE24).
                switch (h.Compression)
                {
                    case BmpCompression.Rle8:
                        canvas.Put(x, y, lut[code], count);
                        break;
                    case BmpCompression.Rle4:
                        uint hi = lut[code >> 4], lo = lut[code & 15];
                        for (var i = 0; i < count && x + i < h.Width; i++) canvas.Put(x + i, y, (i & 1) == 0 ? hi : lo, 1);
                        break;
                    default:
                        if (pos + 1 >= s.Length) return;
                        canvas.Put(x, y, Pack(s[pos + 1], s[pos], code, format), count);
                        pos += 2;
                        break;
                }
                x += count;
                continue;
            }
            switch (code)
            {
                case 0: // end of line
                    x = 0;
                    y++;
                    break;
                case 1: // end of bitmap
                    return;
                case 2: // delta: move right and up
                    if (pos + 1 >= s.Length) return;
                    x += s[pos];
                    y += s[pos + 1];
                    pos += 2;
                    break;
                default:
                {
                    // Absolute mode: `code` literal pixels, padded to a 16-bit boundary.
                    var n = code;
                    var bytes = h.Compression switch
                    {
                        BmpCompression.Rle8 => n,
                        BmpCompression.Rle4 => (n + 1) / 2,
                        _ => 3 * n,
                    };
                    var available = Math.Min(bytes, s.Length - pos);
                    var literal = s.Slice(pos, available);
                    for (var i = 0; i < n; i++)
                    {
                        uint value;
                        switch (h.Compression)
                        {
                            case BmpCompression.Rle8:
                                if (i >= literal.Length) goto done;
                                value = lut[literal[i]];
                                break;
                            case BmpCompression.Rle4:
                                if (i / 2 >= literal.Length) goto done;
                                value = lut[(i & 1) == 0 ? literal[i / 2] >> 4 : literal[i / 2] & 15];
                                break;
                            default:
                                if (3 * i + 2 >= literal.Length) goto done;
                                value = Pack(literal[3 * i + 2], literal[3 * i + 1], literal[3 * i], format);
                                break;
                        }
                        canvas.Put(x + i, y, value, 1);
                    }
                done:
                    x += n;
                    pos += bytes + (bytes & 1);
                    break;
                }
            }
        }
    }

    // ---- OS/2 Huffman 1D ---------------------------------------------------------------------------------------

    /// <summary>
    /// A 1-bit bitmap in the Modified Huffman code of ITU-T T.4 §4.1: each row is runs of white and black
    /// alternating, starting with white, coded with the T.4 terminating and makeup tables. White is palette
    /// index 0 and black index 1 (a 0 bit is white, as in T.4). Rows follow one another without padding; an
    /// EOL code (eleven or more 0 bits, then a 1) may come before any row. A row that runs past the edge is
    /// corrupt; a stream that ends early leaves the rows it did not reach opaque black.
    /// </summary>
    private static void DecodeHuffman(ReadOnlySpan<byte> data, BmpHeader h, Span<byte> destination, int rowStride, ImagePixelFormat format)
    {
        var canvas = new Canvas(destination, rowStride, format, h.Width, h.Height);
        Span<uint> lut = stackalloc uint[256];
        BuildPalette(data, h, format, lut);
        var reader = new BitReader(data[h.PixelOffset..]);
        for (var y = 0; y < h.Height; y++)
        {
            reader.SkipEols();
            if (reader.AtEnd) return;
            var x = 0;
            var colour = 0;
            while (x < h.Width)
            {
                var run = reader.ReadRun(colour);
                if (run > h.Width - x)
                    throw new InvalidDataException($"Huffman 1D row {y} runs past the {h.Width}-pixel edge.");
                canvas.Put(x, y, lut[colour], run);
                x += run;
                colour ^= 1;
            }
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _position;

        public readonly bool AtEnd => _position >= (long)_data.Length * 8;

        /// <summary>Skips fill and EOL codes: eleven or more 0 bits, then a 1.</summary>
        public void SkipEols()
        {
            var end = (long)_data.Length * 8;
            while (!AtEnd)
            {
                var zeros = 0;
                while (zeros < 64 && _position + zeros < end && Bit(_position + zeros) == 0) zeros++;
                if (zeros < 11) return; // a run code: no T.4 code starts with more than seven 0 bits
                _position += zeros;
                if (AtEnd) return; // fill to the end of the data
                if (zeros == 64 && Bit(_position) == 0) continue; // a long fill, not yet its EOL
                _position++; // the EOL's closing 1
            }
        }

        /// <summary>One run: makeup codes, which chain for runs past 2560, closed by a terminating code.</summary>
        public int ReadRun(int colour)
        {
            var bits = colour == 0 ? MmrCodes.WhiteBits : MmrCodes.BlackBits;
            var total = 0;
            while (true)
            {
                if (AtEnd) throw new InvalidDataException("The Huffman 1D data ends inside a row.");
                var run = MmrCodes.LookupRun(colour, (int)Peek(bits));
                if (run.Length == 0)
                    throw new InvalidDataException($"A Huffman 1D {(colour == 0 ? "white" : "black")} run code is not in the T.4 tables.");
                _position += run.Length;
                total += run.Value;
                if (total > 1 << 24) throw new InvalidDataException("A Huffman 1D run is longer than any row.");
                if (run.Value < MmrCodes.MakeupThreshold) return total;
            }
        }

        private readonly int Bit(long position)
        {
            var index = position >> 3;
            return index < _data.Length ? (_data[(int)index] >> (7 - (int)(position & 7))) & 1 : 0;
        }

        // The next `count` (at most 25) bits, most significant first; past the end they read as 0.
        private readonly uint Peek(int count)
        {
            var index = _position >> 3;
            var shift = (int)(_position & 7);
            uint window = 0;
            for (var i = 0; i < 4; i++)
            {
                var at = index + i;
                window = (window << 8) | (at < _data.Length ? _data[(int)at] : 0u);
            }
            return (window << shift) >> (32 - count);
        }
    }
}
