using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// Expands one row of PNG samples to 8-bit RGBA: the one mapping behind
/// <see cref="PngImage.ExpandToRgba8"/> and the streaming RGBA decode, which calls it on each row
/// as soon as the row is unfiltered, so no image-sized sample buffer exists.
/// <para>Rows are in file layout: 16-bit samples big-endian (the high byte first, which is the
/// byte kept), and, for <see cref="SourceBitDepth"/> 1, 2 and 4, still packed. Greyscale, grey +
/// alpha, RGB and RGBA are byte shuffles, sixteen output bytes at a time where the CPU has a
/// byte shuffle (SSSE3 / AdvSimd), scalar otherwise. Indexed and packed greyscale go through a
/// 256-entry RGBA table — the palette with its tRNS alpha, or the grey levels scaled to 0-255 —
/// one load and one 32-bit store per pixel.</para>
/// </summary>
internal sealed class PngRgba8Expander
{
    private readonly int _colorType;
    private readonly int _bitDepth;

    // RGBA per index (indexed) or per packed grey level (greyscale under 8 bits); each uint holds the
    // four bytes in memory order, so storing it writes R, G, B, A whatever the host's endianness.
    private readonly uint[]? _table;

    // Palette entries; an index at or past it is an error. 256 when every byte is a valid index.
    private readonly int _entries = 256;

    /// <param name="colorType">PNG colour type: 0, 2, 3, 4 or 6.</param>
    /// <param name="bitDepth">
    /// Depth of the rows this will be given: 1, 2 or 4 (packed, greyscale or indexed), 8 or 16. A
    /// <see cref="PngImage"/>'s unpacked sub-byte samples are 8-bit rows (the grey already scaled).
    /// </param>
    /// <param name="palette">PLTE, required for colour type 3.</param>
    /// <param name="paletteAlpha">tRNS for colour type 3; entries past its end are opaque.</param>
    public PngRgba8Expander(int colorType, int bitDepth, byte[]? palette, byte[]? paletteAlpha)
    {
        if (colorType is not (0 or 2 or 3 or 4 or 6))
            throw new InvalidDataException($"Cannot expand PNG color type {colorType} to RGBA8");
        if (bitDepth is not (1 or 2 or 4 or 8 or 16) || (bitDepth < 8 && colorType is not (0 or 3)) || (bitDepth == 16 && colorType == 3))
            throw new InvalidDataException($"Cannot expand PNG color type {colorType} at {bitDepth} bits to RGBA8");

        _colorType = colorType;
        _bitDepth = bitDepth;
        if (colorType == 3)
        {
            if (palette is null)
                throw new InvalidDataException("Indexed PNG has no palette to expand to RGBA8");
            _entries = Math.Min(256, palette.Length / 3);
            _table = new uint[256];
            for (var i = 0; i < _entries; i++)
            {
                var alpha = paletteAlpha is not null && i < paletteAlpha.Length ? paletteAlpha[i] : (byte)255;
                _table[i] = Pack(palette[3 * i], palette[3 * i + 1], palette[3 * i + 2], alpha);
            }
        }
        else if (colorType == 0 && bitDepth < 8)
        {
            var max = (1 << bitDepth) - 1;
            _table = new uint[256];
            for (var level = 0; level <= max; level++)
            {
                var v = (byte)(level * 255 / max);
                _table[level] = Pack(v, v, v, 255);
            }
        }
    }

    /// <summary>Depth of the rows this expands (1, 2, 4, 8 or 16).</summary>
    public int SourceBitDepth => _bitDepth;

    /// <summary>Bytes in one row of <paramref name="width"/> pixels as this expander reads it.</summary>
    public int RowBytes(int width) => _bitDepth < 8
        ? checked((int)(((long)width * _bitDepth + 7) / 8))
        : checked(width * PngReader.SamplesPerPixel(_colorType) * (_bitDepth / 8));

    /// <summary>
    /// Expands <paramref name="width"/> pixels of <paramref name="row"/> into the first
    /// <c>4 * width</c> bytes of <paramref name="rgba"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">An index at or past the palette's end.</exception>
    public void Expand(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        // These two checks are what every unchecked access below relies on: the row holds all its
        // samples and the destination all its pixels.
        if (row.Length < RowBytes(width))
            throw new ArgumentException($"Row too short: {row.Length} < {RowBytes(width)}", nameof(row));
        if (rgba.Length < 4L * width)
            throw new ArgumentException($"Destination too short: {rgba.Length} < {4L * width}", nameof(rgba));

        if (_bitDepth < 8)
        {
            ExpandPacked(row, rgba, width);
            return;
        }

        switch ((_colorType, _bitDepth))
        {
            case (0, 8): Gray8(row, rgba, width); break;
            case (0, 16): Gray16(row, rgba, width); break;
            case (2, 8): Rgb8(row, rgba, width); break;
            case (2, 16): Rgb16(row, rgba, width); break;
            case (3, 8): Indexed8(row, rgba, width); break;
            case (4, 8): GrayAlpha8(row, rgba, width); break;
            case (4, 16): GrayAlpha16(row, rgba, width); break;
            case (6, 8): row[..(4 * width)].CopyTo(rgba); break;
            default: Rgba16(row, rgba, width); break;
        }
    }

    /// <summary>
    /// Unpacks a row of 1-, 2- or 4-bit samples (most significant bits first) to one byte each:
    /// a grey level scaled to 0-255 when <paramref name="scaleGray"/>, an index as it is otherwise.
    /// </summary>
    public static void UnpackSubByte(ReadOnlySpan<byte> packed, Span<byte> output, int width, int bitDepth, bool scaleGray)
    {
        var max = (1 << bitDepth) - 1;
        Span<byte> levels = stackalloc byte[16];
        for (var level = 0; level <= max; level++)
            levels[level] = (byte)(scaleGray ? level * 255 / max : level);

        output = output[..width];
        var perByte = 8 / bitDepth;
        var whole = width / perByte;
        var x = 0;
        switch (bitDepth)
        {
            case 1:
                for (var i = 0; i < whole; i++, x += 8)
                {
                    int b = packed[i];
                    var o = output.Slice(x, 8);
                    o[7] = levels[b & 1]; o[6] = levels[(b >> 1) & 1]; o[5] = levels[(b >> 2) & 1]; o[4] = levels[(b >> 3) & 1];
                    o[3] = levels[(b >> 4) & 1]; o[2] = levels[(b >> 5) & 1]; o[1] = levels[(b >> 6) & 1]; o[0] = levels[b >> 7];
                }
                break;
            case 2:
                for (var i = 0; i < whole; i++, x += 4)
                {
                    int b = packed[i];
                    var o = output.Slice(x, 4);
                    o[3] = levels[b & 3]; o[2] = levels[(b >> 2) & 3]; o[1] = levels[(b >> 4) & 3]; o[0] = levels[b >> 6];
                }
                break;
            default:
                for (var i = 0; i < whole; i++, x += 2)
                {
                    int b = packed[i];
                    var o = output.Slice(x, 2);
                    o[1] = levels[b & 15]; o[0] = levels[b >> 4];
                }
                break;
        }

        for (; x < width; x++)
        {
            var bit = x * bitDepth;
            output[x] = levels[(packed[bit >> 3] >> (8 - bitDepth - (bit & 7))) & max];
        }
    }

    private static uint Pack(byte r, byte g, byte b, byte a)
    {
        Span<byte> bytes = [r, g, b, a];
        return MemoryMarshal.Read<uint>(bytes);
    }

    private static bool CanShuffle => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;

    // Byte shuffle with "index ≥ 16 gives 0": pshufb zeroes on the top bit, tbl on any index past
    // the table, so the masks below use only 0-15 and 0x80.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> indices) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(value, indices) : AdvSimd.Arm64.VectorTableLookup(value, indices);

    private static Vector128<byte> OpaqueAlpha => Vector128.Create((byte)0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF);

    // Every vector loop below runs while its last load and its last store end inside the row
    // (row.Length >= RowBytes(width) and rgba.Length >= 4 * width, checked in Expand); each loop
    // condition states the load bound, from which the store bound follows as noted.

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Gray8(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var x = 0;
        if (CanShuffle)
        {
            ref var s = ref MemoryMarshal.GetReference(row);
            ref var d = ref MemoryMarshal.GetReference(rgba);
            var m0 = Vector128.Create((byte)0, 0, 0, 0x80, 1, 1, 1, 0x80, 2, 2, 2, 0x80, 3, 3, 3, 0x80);
            var m1 = Vector128.Create((byte)4, 4, 4, 0x80, 5, 5, 5, 0x80, 6, 6, 6, 0x80, 7, 7, 7, 0x80);
            var m2 = Vector128.Create((byte)8, 8, 8, 0x80, 9, 9, 9, 0x80, 10, 10, 10, 0x80, 11, 11, 11, 0x80);
            var m3 = Vector128.Create((byte)12, 12, 12, 0x80, 13, 13, 13, 0x80, 14, 14, 14, 0x80, 15, 15, 15, 0x80);
            var alpha = OpaqueAlpha;
            // Load [x, x+16) of width bytes; store [4x, 4x+64) of 4·width.
            for (; x + 16 <= width; x += 16)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)x);
                ref var o = ref Unsafe.Add(ref d, 4 * x);
                (Shuffle(v, m0) | alpha).StoreUnsafe(ref o);
                (Shuffle(v, m1) | alpha).StoreUnsafe(ref o, 16);
                (Shuffle(v, m2) | alpha).StoreUnsafe(ref o, 32);
                (Shuffle(v, m3) | alpha).StoreUnsafe(ref o, 48);
            }
        }

        for (; x < width; x++)
        {
            var g = row[x];
            var o = rgba.Slice(4 * x, 4);
            o[0] = g; o[1] = g; o[2] = g; o[3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Gray16(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var x = 0;
        if (CanShuffle)
        {
            ref var s = ref MemoryMarshal.GetReference(row);
            ref var d = ref MemoryMarshal.GetReference(rgba);
            var m0 = Vector128.Create((byte)0, 0, 0, 0x80, 2, 2, 2, 0x80, 4, 4, 4, 0x80, 6, 6, 6, 0x80);
            var m1 = Vector128.Create((byte)8, 8, 8, 0x80, 10, 10, 10, 0x80, 12, 12, 12, 0x80, 14, 14, 14, 0x80);
            var alpha = OpaqueAlpha;
            // Load [2x, 2x+16) of 2·width bytes; store [4x, 4x+32) of 4·width.
            for (; x + 8 <= width; x += 8)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)(2 * x));
                ref var o = ref Unsafe.Add(ref d, 4 * x);
                (Shuffle(v, m0) | alpha).StoreUnsafe(ref o);
                (Shuffle(v, m1) | alpha).StoreUnsafe(ref o, 16);
            }
        }

        for (; x < width; x++)
        {
            var g = row[2 * x];
            var o = rgba.Slice(4 * x, 4);
            o[0] = g; o[1] = g; o[2] = g; o[3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Rgb8(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var x = 0;
        if (CanShuffle)
        {
            ref var s = ref MemoryMarshal.GetReference(row);
            ref var d = ref MemoryMarshal.GetReference(rgba);
            var m = Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
            var alpha = OpaqueAlpha;
            // Load [3x, 3x+16) of 3·width bytes; then x + 4 < width, so the store [4x, 4x+16) fits.
            for (; 3 * x + 16 <= 3 * width; x += 4)
                (Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(3 * x)), m) | alpha).StoreUnsafe(ref d, (nuint)(4 * x));
        }

        for (; x < width; x++)
        {
            var i = row.Slice(3 * x, 3);
            var o = rgba.Slice(4 * x, 4);
            o[0] = i[0]; o[1] = i[1]; o[2] = i[2]; o[3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Rgb16(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var x = 0;
        if (CanShuffle)
        {
            ref var s = ref MemoryMarshal.GetReference(row);
            ref var d = ref MemoryMarshal.GetReference(rgba);
            // Four pixels are 24 bytes: the first load holds pixels 0, 1 and R, G of pixel 2; the
            // second, 8 bytes on, holds B of pixel 2 (at 8) and pixel 3 (at 10, 12, 14).
            var m0 = Vector128.Create((byte)0, 2, 4, 0x80, 6, 8, 10, 0x80, 12, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            var m1 = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 8, 0x80, 10, 12, 14, 0x80);
            var alpha = OpaqueAlpha;
            // Loads [6x, 6x+24) of 6·width bytes; store [4x, 4x+16) of 4·width.
            for (; x + 4 <= width; x += 4)
            {
                var a = Vector128.LoadUnsafe(ref s, (nuint)(6 * x));
                var b = Vector128.LoadUnsafe(ref s, (nuint)(6 * x + 8));
                (Shuffle(a, m0) | Shuffle(b, m1) | alpha).StoreUnsafe(ref d, (nuint)(4 * x));
            }
        }

        for (; x < width; x++)
        {
            var i = row.Slice(6 * x, 6);
            var o = rgba.Slice(4 * x, 4);
            o[0] = i[0]; o[1] = i[2]; o[2] = i[4]; o[3] = 255;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void GrayAlpha8(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var x = 0;
        if (CanShuffle)
        {
            ref var s = ref MemoryMarshal.GetReference(row);
            ref var d = ref MemoryMarshal.GetReference(rgba);
            var m0 = Vector128.Create((byte)0, 0, 0, 1, 2, 2, 2, 3, 4, 4, 4, 5, 6, 6, 6, 7);
            var m1 = Vector128.Create((byte)8, 8, 8, 9, 10, 10, 10, 11, 12, 12, 12, 13, 14, 14, 14, 15);
            // Load [2x, 2x+16) of 2·width bytes; store [4x, 4x+32) of 4·width.
            for (; x + 8 <= width; x += 8)
            {
                var v = Vector128.LoadUnsafe(ref s, (nuint)(2 * x));
                ref var o = ref Unsafe.Add(ref d, 4 * x);
                Shuffle(v, m0).StoreUnsafe(ref o);
                Shuffle(v, m1).StoreUnsafe(ref o, 16);
            }
        }

        for (; x < width; x++)
        {
            var g = row[2 * x];
            var o = rgba.Slice(4 * x, 4);
            o[0] = g; o[1] = g; o[2] = g; o[3] = row[2 * x + 1];
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void GrayAlpha16(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var x = 0;
        if (CanShuffle)
        {
            ref var s = ref MemoryMarshal.GetReference(row);
            ref var d = ref MemoryMarshal.GetReference(rgba);
            var m = Vector128.Create((byte)0, 0, 0, 2, 4, 4, 4, 6, 8, 8, 8, 10, 12, 12, 12, 14);
            // Load [4x, 4x+16) of 4·width bytes; store [4x, 4x+16) of 4·width.
            for (; x + 4 <= width; x += 4)
                Shuffle(Vector128.LoadUnsafe(ref s, (nuint)(4 * x)), m).StoreUnsafe(ref d, (nuint)(4 * x));
        }

        for (; x < width; x++)
        {
            var g = row[4 * x];
            var o = rgba.Slice(4 * x, 4);
            o[0] = g; o[1] = g; o[2] = g; o[3] = row[4 * x + 2];
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Rgba16(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var x = 0;
        if (CanShuffle)
        {
            ref var s = ref MemoryMarshal.GetReference(row);
            ref var d = ref MemoryMarshal.GetReference(rgba);
            var lo = Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            var hi = Vector128.Create((byte)0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0, 2, 4, 6, 8, 10, 12, 14);
            // Loads [8x, 8x+32) of 8·width bytes; store [4x, 4x+16) of 4·width.
            for (; x + 4 <= width; x += 4)
            {
                var a = Vector128.LoadUnsafe(ref s, (nuint)(8 * x));
                var b = Vector128.LoadUnsafe(ref s, (nuint)(8 * x + 16));
                (Shuffle(a, lo) | Shuffle(b, hi)).StoreUnsafe(ref d, (nuint)(4 * x));
            }
        }

        for (; x < width; x++)
        {
            var i = row.Slice(8 * x, 8);
            var o = rgba.Slice(4 * x, 4);
            o[0] = i[0]; o[1] = i[2]; o[2] = i[4]; o[3] = i[6];
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void Indexed8(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        row = row[..width];
        if (_entries < 256 && MaxByte(row) is var top && top >= _entries)
            throw IndexOutOfPalette(top);

        // A byte indexes the 256-entry table, so every table read is in range; the store is
        // [4x, 4x+4) of 4·width.
        ref var table = ref MemoryMarshal.GetArrayDataReference(_table!);
        ref var d = ref MemoryMarshal.GetReference(rgba);
        for (var x = 0; x < row.Length; x++)
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 4 * x), Unsafe.Add(ref table, row[x]));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void ExpandPacked(ReadOnlySpan<byte> row, Span<byte> rgba, int width)
    {
        var bitDepth = _bitDepth;
        var perByte = 8 / bitDepth;
        var whole = width / perByte;
        var mask = (1 << bitDepth) - 1;
        // Indexed with fewer palette entries than the depth can address: check every sample.
        var check = _colorType == 3 && _entries <= mask;

        // A sample is at most 4 bits, so every table read is in range; whole·perByte <= width, so
        // the stores of the whole bytes are within 4·width.
        ref var table = ref MemoryMarshal.GetArrayDataReference(_table!);
        ref var d = ref MemoryMarshal.GetReference(rgba);
        var bad = 0;
        var x = 0;
        switch (bitDepth)
        {
            case 1:
                for (var i = 0; i < whole; i++, x += 8)
                {
                    int b = row[i];
                    ref var o = ref Unsafe.Add(ref d, 4 * x);
                    for (var k = 0; k < 8; k++)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 4 * k), Unsafe.Add(ref table, (b >> (7 - k)) & 1));
                    bad |= check ? b : 0; // with one palette entry, any 1 bit is out of range
                }
                break;
            case 2:
                for (var i = 0; i < whole; i++, x += 4)
                {
                    int b = row[i];
                    ref var o = ref Unsafe.Add(ref d, 4 * x);
                    int s0 = b >> 6, s1 = (b >> 4) & 3, s2 = (b >> 2) & 3, s3 = b & 3;
                    Unsafe.WriteUnaligned(ref o, Unsafe.Add(ref table, s0));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 4), Unsafe.Add(ref table, s1));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 8), Unsafe.Add(ref table, s2));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 12), Unsafe.Add(ref table, s3));
                    bad |= check ? Math.Max(Math.Max(s0, s1), Math.Max(s2, s3)) >= _entries ? 1 : 0 : 0;
                }
                break;
            default:
                for (var i = 0; i < whole; i++, x += 2)
                {
                    int b = row[i];
                    ref var o = ref Unsafe.Add(ref d, 4 * x);
                    int s0 = b >> 4, s1 = b & 15;
                    Unsafe.WriteUnaligned(ref o, Unsafe.Add(ref table, s0));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, 4), Unsafe.Add(ref table, s1));
                    bad |= check ? Math.Max(s0, s1) >= _entries ? 1 : 0 : 0;
                }
                break;
        }

        // The last byte's padding bits are not samples: only real ones are expanded and checked.
        for (; x < width; x++)
        {
            var bit = x * bitDepth;
            var sample = (row[bit >> 3] >> (8 - bitDepth - (bit & 7))) & mask;
            if (check && sample >= _entries)
                throw IndexOutOfPalette(sample);
            MemoryMarshal.Write(rgba.Slice(4 * x, 4), _table![sample]);
        }

        if (bad != 0)
            throw IndexOutOfPalette(FirstBadIndex(row, width));
    }

    // Only on the error path: name the offending index in the message.
    private int FirstBadIndex(ReadOnlySpan<byte> row, int width)
    {
        var mask = (1 << _bitDepth) - 1;
        for (var x = 0; x < width; x++)
        {
            var bit = x * _bitDepth;
            var sample = (row[bit >> 3] >> (8 - _bitDepth - (bit & 7))) & mask;
            if (sample >= _entries)
                return sample;
        }
        return _entries;
    }

    private InvalidDataException IndexOutOfPalette(int index) =>
        new($"Palette index {index} out of range (palette has {_entries} entries)");

    private static int MaxByte(ReadOnlySpan<byte> values)
    {
        var x = 0;
        var max = 0;
        if (Vector128.IsHardwareAccelerated && values.Length >= 16)
        {
            var acc = Vector128<byte>.Zero;
            for (; x + 16 <= values.Length; x += 16)
                acc = Vector128.Max(acc, Vector128.Create(values.Slice(x, 16)));
            for (var i = 0; i < 16; i++)
                max = Math.Max(max, acc.GetElement(i));
        }

        for (; x < values.Length; x++)
            max = Math.Max(max, values[x]);
        return max;
    }
}
