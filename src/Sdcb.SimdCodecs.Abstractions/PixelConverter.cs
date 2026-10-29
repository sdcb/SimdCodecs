using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// Converts interleaved grey / grey+alpha / RGB / RGBA rows (8- or 16-bit, host byte order — the
/// <see cref="IDecodedImage.Pixels"/> layout) into an <see cref="ImagePixelFormat"/>. The shared
/// back end of <see cref="IImageDecoder.TryDecodeInto"/>: a codec without a native path for a
/// layout decodes, then converts here; one with a native path must produce exactly these bytes.
/// <para>
/// The mapping: 16-bit samples keep their high byte; grey broadcasts to R, G and B; a missing alpha
/// is 255; alpha is dropped (never composited) by the layouts without it; and colour reaches
/// <see cref="ImagePixelFormat.Gray8"/> as BT.601 luma, <c>(77·R + 150·G + 29·B + 128) &gt;&gt; 8</c>
/// — the T.871 weights 0.299 / 0.587 / 0.114 scaled to 256 and rounded, so they sum to exactly 256
/// and a grey pixel (R = G = B) keeps its value.
/// </para>
/// </summary>
public static class PixelConverter
{
    private const byte Z = 0x80; // a shuffle index that yields 0, for both pshufb and TBL

    // Byte shuffles need a table lookup with zeroing: SSSE3 pshufb or arm64 TBL. Both read an index
    // with its high bit set as "write 0", which is what every mask below relies on.
    private static bool UseShuffle => BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);
    private static bool UseV128 => Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    /// <summary>Bytes per pixel of <paramref name="format"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The format is not an <see cref="ImagePixelFormat"/>.</exception>
    public static int BytesPerPixel(ImagePixelFormat format) => format switch
    {
        ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32 => 4,
        ImagePixelFormat.Rgb24 or ImagePixelFormat.Bgr24 => 3,
        ImagePixelFormat.Gray8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
    };

    /// <summary>
    /// Checks a destination for a <paramref name="width"/> × <paramref name="height"/> image in
    /// <paramref name="format"/>: row <c>y</c> starts at byte <c>y × rowStride</c>, and the buffer
    /// must hold <c>rowStride × (height − 1) + width × bytesPerPixel</c> bytes.
    /// </summary>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="rowStride">The stride to use: <paramref name="stride"/>, or the packed row size for 0.</param>
    /// <returns>False for an unknown format, a non-positive size, a negative stride or one shorter than a
    /// row, or a destination that is too small.</returns>
    public static bool TryGetLayout(int width, int height, ImagePixelFormat format, int stride, int destinationLength, out int rowStride)
    {
        rowStride = 0;
        if (width <= 0 || height <= 0 || stride < 0 || (uint)format > (uint)ImagePixelFormat.Gray8)
            return false;
        var rowBytes = (long)width * BytesPerPixel(format);
        long step = stride == 0 ? rowBytes : stride;
        if (step < rowBytes || step > int.MaxValue || step * (height - 1) + rowBytes > destinationLength)
            return false;
        rowStride = (int)step;
        return true;
    }

    /// <summary>
    /// Converts <paramref name="image"/> into <paramref name="destination"/>, row by row.
    /// </summary>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed. Bytes between rows
    /// are left untouched.</param>
    /// <returns>False when the destination does not fit (see <see cref="TryGetLayout"/>) or the image has
    /// <see cref="SampleFormat.Float32"/> samples, which have no 8-bit projection without a tone policy.</returns>
    public static bool TryConvert(IDecodedImage image, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.SampleFormat == SampleFormat.Float32 || image.Channels is < 1 or > 4)
            return false;
        if (!TryGetLayout(image.Width, image.Height, format, stride, destination.Length, out var rowStride))
            return false;

        var sourceRow = image.Width * image.Channels * (image.SampleFormat == SampleFormat.UInt16 ? 2 : 1);
        var pixels = image.Pixels;
        if (pixels.Length < (long)sourceRow * image.Height)
            return false;
        var rowBytes = image.Width * BytesPerPixel(format);
        for (var y = 0; y < image.Height; y++)
        {
            ConvertRow(pixels.Slice(y * sourceRow, sourceRow), image.Channels, image.SampleFormat,
                destination.Slice(y * rowStride, rowBytes), format, image.Width);
        }
        return true;
    }

    /// <summary>
    /// Projects <paramref name="image"/> to 8-bit RGBA by stretching its colour range linearly onto
    /// 0..255: the display path for float rasters (a float TIFF, a reconstructed HDR), which
    /// <see cref="TryConvert"/> refuses because 8 bits need a tone decision. This is that decision at
    /// its simplest, for viewing: the smallest finite R, G or B value becomes 0 and the largest 255
    /// (<see cref="TryGetColorRange"/>), every channel on the same scale so hues keep their ratios. A
    /// constant or wholly non-finite image falls back to the range 0..1. Integer rasters are stretched
    /// too, from their <see cref="IDecodedImage.ToFloats"/> values. See
    /// <see cref="StretchToRgba8(ReadOnlySpan{float}, Span{byte}, float, float)"/> for the mapping.
    /// </summary>
    public static byte[] StretchToRgba8(IDecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rgba = image.ToFloats();
        if (!TryGetColorRange(rgba, out var min, out var max) || !(max > min))
            (min, max) = (0f, 1f);
        var destination = new byte[rgba.Length];
        StretchToRgba8(rgba, destination, min, max);
        return destination;
    }

    /// <summary>
    /// <see cref="StretchToRgba8(IDecodedImage)"/> onto a range the caller chooses — fixed across the
    /// frames of a sequence, or clipped to a percentile.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="min"/> and <paramref name="max"/> are not finite
    /// with <c>min &lt; max</c>.</exception>
    public static byte[] StretchToRgba8(IDecodedImage image, float min, float max)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rgba = image.ToFloats();
        var destination = new byte[rgba.Length];
        StretchToRgba8(rgba, destination, min, max);
        return destination;
    }

    /// <summary>
    /// The smallest and largest finite R, G and B values of interleaved float RGBA (the
    /// <see cref="IDecodedImage.ToFloats"/> layout); alpha, NaN and ±∞ are ignored.
    /// </summary>
    /// <returns>False when there is no finite colour value; <paramref name="min"/> and
    /// <paramref name="max"/> are then 0.</returns>
    /// <exception cref="ArgumentException">The length is not a multiple of 4.</exception>
    public static bool TryGetColorRange(ReadOnlySpan<float> rgba, out float min, out float max)
    {
        if (rgba.Length % 4 != 0)
            throw new ArgumentException("Interleaved RGBA needs a multiple of 4 values.", nameof(rgba));
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var v = rgba[i + c];
                if (!float.IsFinite(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        if (min > max)
        {
            min = max = 0;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Stretches interleaved float RGBA onto 8-bit RGBA: R, G and B map <paramref name="min"/>..
    /// <paramref name="max"/> linearly onto 0..255, rounded to nearest and clamped outside the range;
    /// alpha maps 0..1 the same way. NaN becomes 0, +∞ 255 and −∞ 0. The arithmetic is in double
    /// precision, so a range spanning the whole float line does not overflow.
    /// </summary>
    /// <param name="rgba">Interleaved RGBA floats, the <see cref="IDecodedImage.ToFloats"/> layout.</param>
    /// <param name="destination">At least <c>rgba.Length</c> bytes; must not overlap <paramref name="rgba"/>.</param>
    /// <exception cref="ArgumentException">The length is not a multiple of 4, the destination is too
    /// small, or <paramref name="min"/> and <paramref name="max"/> are not finite with <c>min &lt; max</c>.</exception>
    public static void StretchToRgba8(ReadOnlySpan<float> rgba, Span<byte> destination, float min, float max)
    {
        if (rgba.Length % 4 != 0)
            throw new ArgumentException("Interleaved RGBA needs a multiple of 4 values.", nameof(rgba));
        if (destination.Length < rgba.Length)
            throw new ArgumentException("Destination too small.", nameof(destination));
        if (!float.IsFinite(min) || !float.IsFinite(max) || !(min < max))
            throw new ArgumentException("The range must be finite with min < max.", nameof(max));

        var scale = 255.0 / ((double)max - min);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            destination[i] = Stretch(rgba[i], min, scale);
            destination[i + 1] = Stretch(rgba[i + 1], min, scale);
            destination[i + 2] = Stretch(rgba[i + 2], min, scale);
            destination[i + 3] = Stretch(rgba[i + 3], 0, 255.0);
        }
    }

    private static byte Stretch(float value, double min, double scale)
    {
        // NaN fails both comparisons and lands on 0; ±∞ clamps like any out-of-range value.
        var x = (value - min) * scale + 0.5;
        return x >= 255 ? (byte)255 : x >= 1 ? (byte)x : (byte)0;
    }

    /// <summary>
    /// Converts one row of <paramref name="width"/> pixels.
    /// </summary>
    /// <param name="source">Interleaved samples, <paramref name="channels"/> per pixel (1 grey, 2 grey+alpha,
    /// 3 RGB, 4 RGBA), in host byte order.</param>
    /// <param name="destination">At least <c>width × BytesPerPixel(format)</c> bytes. It may be the same
    /// memory as <paramref name="source"/> when a pixel is the same size in both (RGBA → BGRA in place);
    /// otherwise the two must not overlap.</param>
    /// <exception cref="ArgumentException">A span is too short, or <paramref name="channels"/> is not 1..4.</exception>
    /// <exception cref="NotSupportedException"><paramref name="sampleFormat"/> is <see cref="SampleFormat.Float32"/>.</exception>
    public static void ConvertRow(ReadOnlySpan<byte> source, int channels, SampleFormat sampleFormat,
        Span<byte> destination, ImagePixelFormat format, int width)
    {
        if (channels is < 1 or > 4)
            throw new ArgumentException("Channels must be 1..4.", nameof(channels));
        if (width < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var outBpp = BytesPerPixel(format);
        if (destination.Length < (long)width * outBpp)
            throw new ArgumentException("Destination too small for the row.", nameof(destination));

        switch (sampleFormat)
        {
            case SampleFormat.UInt8:
                if (source.Length < (long)width * channels)
                    throw new ArgumentException("Source too small for the row.", nameof(source));
                Convert8(source, channels, destination, format, width);
                break;
            case SampleFormat.UInt16:
                if (source.Length < (long)width * channels * 2)
                    throw new ArgumentException("Source too small for the row.", nameof(source));
                Convert16(source, channels, destination, format, width, outBpp);
                break;
            case SampleFormat.Float32:
                throw new NotSupportedException("Float32 samples have no 8-bit projection without an explicit tone/stretch policy.");
            default:
                throw new ArgumentOutOfRangeException(nameof(sampleFormat));
        }
    }

    // 16-bit rows go through a stack chunk of their high bytes, then the 8-bit kernels.
    private static void Convert16(ReadOnlySpan<byte> source, int channels, Span<byte> destination, ImagePixelFormat format,
        int width, int outBpp)
    {
        const int ChunkPixels = 256;
        Span<byte> narrow = stackalloc byte[ChunkPixels * 4];
        var samples = MemoryMarshal.Cast<byte, ushort>(source);
        for (var x = 0; x < width; x += ChunkPixels)
        {
            var n = Math.Min(ChunkPixels, width - x);
            var chunk = narrow[..(n * channels)];
            HighBytes(samples.Slice(x * channels, n * channels), chunk);
            Convert8(chunk, channels, destination.Slice(x * outBpp, n * outBpp), format, n);
        }
    }

    // Callers have checked: source holds width * channels bytes, destination width * BytesPerPixel(format).
    private static void Convert8(ReadOnlySpan<byte> source, int channels, Span<byte> destination, ImagePixelFormat format, int width)
    {
        var grey = format == ImagePixelFormat.Gray8;
        var bgr = format is ImagePixelFormat.Bgra32 or ImagePixelFormat.Bgr24;
        var outBpp = BytesPerPixel(format);
        // The kernels' 16-byte stores may run past a step's pixels; cut both spans to the row so that
        // they never run past it.
        source = source[..(width * channels)];
        destination = destination[..(width * outBpp)];

        // Same layout: a copy (in place, nothing to do).
        if ((channels == 4 && format == ImagePixelFormat.Rgba32) || (channels == 3 && format == ImagePixelFormat.Rgb24) || (channels == 1 && grey))
        {
            source.CopyTo(destination);
            return;
        }

        var i = 0;
        if (grey && channels >= 3)
        {
            if (UseV128 && (channels == 4 || UseShuffle))
                i = LumaV128(source, channels, destination, width);
        }
        else if (UseShuffle)
        {
            var (mask, alpha, step) = Plan(channels, format, bgr);
            i = ShuffleV128(source, channels, destination, outBpp, width, mask, alpha, step);
        }
        ConvertScalar(source, channels, destination, format, i, width);
    }

    // The shuffle mask for 16 source bytes, the alpha bytes to set, and the pixels one step converts.
    private static (Vector128<byte> Mask, Vector128<byte> Alpha, int Pixels) Plan(int channels, ImagePixelFormat format, bool bgr)
    {
        var alpha4 = Vector128.Create(0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, (byte)255);
        var four = format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32;
        return (channels, four) switch
        {
            // RGBA -> BGRA (RGBA -> RGBA is a copy).
            (4, true) => (Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15), Vector128<byte>.Zero, 4),
            (4, false) => (bgr
                ? Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, Z, Z, Z, Z)
                : Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, Z, Z, Z, Z), Vector128<byte>.Zero, 4),
            (3, true) => (bgr
                ? Vector128.Create((byte)2, 1, 0, Z, 5, 4, 3, Z, 8, 7, 6, Z, 11, 10, 9, Z)
                : Vector128.Create((byte)0, 1, 2, Z, 3, 4, 5, Z, 6, 7, 8, Z, 9, 10, 11, Z), alpha4, 4),
            // RGB -> BGR (RGB -> RGB is a copy): five pixels a step.
            (3, false) => (Vector128.Create((byte)2, 1, 0, 5, 4, 3, 8, 7, 6, 11, 10, 9, 14, 13, 12, Z), Vector128<byte>.Zero, 5),
            (2, true) => (Vector128.Create((byte)0, 0, 0, 1, 2, 2, 2, 3, 4, 4, 4, 5, 6, 6, 6, 7), Vector128<byte>.Zero, 4),
            (2, false) when format == ImagePixelFormat.Gray8 =>
                (Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, Z, Z, Z, Z, Z, Z, Z, Z), Vector128<byte>.Zero, 8),
            (2, false) => (Vector128.Create((byte)0, 0, 0, 2, 2, 2, 4, 4, 4, 6, 6, 6, 8, 8, 8, Z), Vector128<byte>.Zero, 5),
            (1, true) => (Vector128.Create((byte)0, 0, 0, Z, 1, 1, 1, Z, 2, 2, 2, Z, 3, 3, 3, Z), alpha4, 4),
            _ => (Vector128.Create((byte)0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, Z), Vector128<byte>.Zero, 5), // grey -> RGB
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Shuffle(Vector128<byte> v, Vector128<byte> mask) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(v, mask) : AdvSimd.Arm64.VectorTableLookup(v, mask);

    /// <summary>
    /// Converts whole steps of <paramref name="step"/> pixels: each loads 16 source bytes, shuffles them
    /// and stores 16 bytes, of which the first <c>step × outBpp</c> are the step's pixels; the rest are
    /// overwritten by the next step or the scalar tail. Returns the pixels converted.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int ShuffleV128(ReadOnlySpan<byte> source, int inBpp, Span<byte> destination, int outBpp, int width,
        Vector128<byte> mask, Vector128<byte> alpha, int step)
    {
        // Every access is in range: step k reads bytes [k*step*inBpp, +16) and writes
        // [k*step*outBpp, +16), and `steps` is the largest count for which the last step's both ends
        // stay inside their spans and its pixels inside the row.
        if (source.Length < 16 || destination.Length < 16)
            return 0;
        var steps = Math.Min(width / step, Math.Min((source.Length - 16) / (step * inBpp), (destination.Length - 16) / (step * outBpp)) + 1);
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        nuint inStep = (nuint)(step * inBpp), outStep = (nuint)(step * outBpp), si = 0, di = 0;
        for (var k = 0; k < steps; k++, si += inStep, di += outStep)
            (Shuffle(Vector128.LoadUnsafe(ref s, si), mask) | alpha).StoreUnsafe(ref d, di);
        return steps * step;
    }

    /// <summary>RGB(A) → luma, 16 pixels a step. Returns the pixels converted.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int LumaV128(ReadOnlySpan<byte> source, int channels, Span<byte> destination, int width)
    {
        // In range: a step at pixel i reads the four 16-byte blocks starting at (i + 4k) * channels,
        // k = 0..3, the last ending at (i + 12) * channels + 16 <= source.Length, and writes 16 bytes
        // at i <= destination.Length - 16.
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var expand = Vector128.Create((byte)0, 1, 2, Z, 3, 4, 5, Z, 6, 7, 8, Z, 9, 10, 11, Z);
        var i = 0;
        for (; i + 16 <= width && (long)(i + 12) * channels + 16 <= source.Length && i + 16 <= destination.Length; i += 16)
        {
            var a = Luma4(Load4(ref s, (i + 0) * channels, channels, expand));
            var b = Luma4(Load4(ref s, (i + 4) * channels, channels, expand));
            var c = Luma4(Load4(ref s, (i + 8) * channels, channels, expand));
            var e = Luma4(Load4(ref s, (i + 12) * channels, channels, expand));
            Vector128.Narrow(Vector128.Narrow(a, b), Vector128.Narrow(c, e)).StoreUnsafe(ref d, (nuint)i);
        }
        return i;
    }

    // Four pixels as 32-bit lanes R | G << 8 | B << 16 (| A << 24, ignored).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Load4(ref byte s, int offset, int channels, Vector128<byte> expand)
    {
        var v = Vector128.LoadUnsafe(ref s, (nuint)offset);
        return (channels == 4 ? v : Shuffle(v, expand)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Luma4(Vector128<uint> v)
    {
        var low = Vector128.Create(0xFFu);
        var r = v & low;
        var g = (v >>> 8) & low;
        var b = (v >>> 16) & low;
        return (r * 77 + g * 150 + b * 29 + Vector128.Create(128u)) >>> 8;
    }

    // The scalar twin of every kernel above, and their tail.
    private static void ConvertScalar(ReadOnlySpan<byte> source, int channels, Span<byte> destination, ImagePixelFormat format, int from, int width)
    {
        var outBpp = BytesPerPixel(format);
        var bgr = format is ImagePixelFormat.Bgra32 or ImagePixelFormat.Bgr24;
        for (var x = from; x < width; x++)
        {
            var p = source.Slice(x * channels, channels);
            byte r, g, b, a = 255;
            switch (channels)
            {
                case 1: r = g = b = p[0]; break;
                case 2: r = g = b = p[0]; a = p[1]; break;
                case 3: r = p[0]; g = p[1]; b = p[2]; break;
                default: r = p[0]; g = p[1]; b = p[2]; a = p[3]; break;
            }

            var o = destination.Slice(x * outBpp, outBpp);
            if (format == ImagePixelFormat.Gray8)
            {
                o[0] = channels <= 2 ? r : (byte)((77 * r + 150 * g + 29 * b + 128) >> 8);
                continue;
            }
            o[0] = bgr ? b : r;
            o[1] = g;
            o[2] = bgr ? r : b;
            if (outBpp == 4)
                o[3] = a;
        }
    }

    // The high byte of each 16-bit sample.
    private static void HighBytes(ReadOnlySpan<ushort> source, Span<byte> destination)
    {
        var i = 0;
        if (UseV128)
            i = HighBytesV128(source, destination);
        for (; i < source.Length; i++)
            destination[i] = (byte)(source[i] >> 8);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int HighBytesV128(ReadOnlySpan<ushort> source, Span<byte> destination)
    {
        // In range: a step at i reads source[i, i + 16) and writes destination[i, i + 16), and the
        // caller passes a destination at least as long as the source.
        if (destination.Length < source.Length)
            throw new ArgumentException("Destination too small.", nameof(destination));
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var i = 0;
        for (; i + 16 <= source.Length; i += 16)
        {
            var lo = Vector128.LoadUnsafe(ref s, (nuint)i) >>> 8;
            var hi = Vector128.LoadUnsafe(ref s, (nuint)(i + 8)) >>> 8;
            Vector128.Narrow(lo, hi).StoreUnsafe(ref d, (nuint)i);
        }
        return i;
    }
}
