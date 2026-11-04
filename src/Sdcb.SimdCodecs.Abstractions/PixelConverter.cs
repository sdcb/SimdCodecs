using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// Converts interleaved pixels between <see cref="PixelFormat"/>s: the shared back end of
/// <see cref="IImageDecoder.TryDecodeInto"/> (a codec without a native path for a layout decodes, then converts
/// here; one with a native path must produce exactly these bytes) and of every encoder's input.
/// <para>
/// The mapping is fixed — changing it changes decoded output:
/// </para>
/// <list type="bullet">
/// <item>Depth: 16 → 8 bits keeps the high byte, <c>v &gt;&gt; 8</c>; 8 → 16 bits is <c>v × 257</c> (exact at both ends).</item>
/// <item>Layout: grey broadcasts to R, G and B; a missing alpha is opaque (255 or 65535); a layout without alpha
/// drops it, never composites; colour reaches grey as BT.601 luma, <c>(77·R + 150·G + 29·B + 128) &gt;&gt; 8</c>
/// at the destination depth — the T.871 weights 0.299 / 0.587 / 0.114 scaled to 256, so they sum to exactly 256
/// and a grey pixel (R = G = B) keeps its value.</item>
/// <item>Premultiplying: <c>round(c × a / max)</c>, at the destination depth.</item>
/// <item>Unpremultiplying: colour 0 where <c>a = 0</c>, otherwise <c>min(max, round(c × max / a))</c> (halves
/// round up), at the source depth.</item>
/// <item>Order: a premultiplied source going to a straight destination is unpremultiplied first, at its own depth;
/// then the depth changes, then the layout; a straight source going to a premultiplied destination is
/// premultiplied last, at the destination depth. A premultiplied source going to a premultiplied destination
/// keeps its values (only the depth and channel order change).</item>
/// </list>
/// </summary>
public static class PixelConverter
{
    private const byte Z = 0x80; // a shuffle index that yields 0, for both pshufb and TBL
    private const int ChunkPixels = 256;

    // Byte shuffles need a table lookup with zeroing: SSSE3 pshufb or arm64 TBL. Both read an index
    // with its high bit set as "write 0", which is what every mask below relies on.
    private static bool UseShuffle => BitConverter.IsLittleEndian && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);
    private static bool UseV128 => Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian;

    /// <summary>
    /// Checks a destination for a <paramref name="width"/> × <paramref name="height"/> image in
    /// <paramref name="format"/>: row <c>y</c> starts at byte <c>y × rowStride</c>, and the buffer
    /// must hold <c>rowStride × (height − 1) + width × format.BytesPerPixel</c> bytes.
    /// </summary>
    /// <param name="width">The image width in pixels.</param>
    /// <param name="height">The image height in pixels.</param>
    /// <param name="format">The destination pixel layout.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="destinationLength">The destination's length in bytes.</param>
    /// <param name="rowStride">The stride to use: <paramref name="stride"/>, or the packed row size for 0.</param>
    /// <returns>False for a non-positive size, a negative stride or one shorter than a row, or a destination that
    /// is too small.</returns>
    public static bool TryGetLayout(int width, int height, PixelFormat format, int stride, int destinationLength, out int rowStride)
    {
        rowStride = 0;
        if (width <= 0 || height <= 0 || stride < 0)
            return false;
        var rowBytes = (long)width * format.BytesPerPixel;
        long step = stride == 0 ? rowBytes : stride;
        if (step < rowBytes || step > int.MaxValue || step * (height - 1) + rowBytes > destinationLength)
            return false;
        rowStride = (int)step;
        return true;
    }

    /// <summary>
    /// Converts <paramref name="image"/> into <paramref name="destination"/>, row by row, in <paramref name="format"/>.
    /// </summary>
    /// <param name="image">The source image; its samples are straight (unassociated) alpha, as every
    /// <see cref="IDecodedImage"/>'s are.</param>
    /// <param name="destination">The pixels, laid out as <see cref="TryGetLayout"/> describes.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed. Bytes between rows
    /// are left untouched.</param>
    /// <param name="format">The pixel layout to write.</param>
    /// <returns>False when the destination does not fit (see <see cref="TryGetLayout"/>) or the image has
    /// <see cref="SampleFormat.Float32"/> samples, which have no integer projection without a tone policy.</returns>
    public static bool TryConvert(IDecodedImage image, Span<byte> destination, int stride, PixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.SampleFormat is not (SampleFormat.UInt8 or SampleFormat.UInt16) || image.Channels is < 1 or > 4)
            return false;
        if (!TryGetLayout(image.Width, image.Height, format, stride, destination.Length, out var rowStride))
            return false;

        var source = PixelFormat.ForChannels(image.Channels, image.SampleFormat);
        var sourceRow = image.Width * source.BytesPerPixel;
        var pixels = image.Pixels;
        if (pixels.Length < (long)sourceRow * image.Height)
            return false;
        Convert(pixels, sourceRow, source, destination, rowStride, format, image.Width, image.Height);
        return true;
    }

    /// <summary>
    /// Converts a <paramref name="width"/> × <paramref name="height"/> image between formats and strides. Bytes
    /// between the rows of <paramref name="destination"/> are left untouched.
    /// </summary>
    /// <param name="source">The source pixels; row <c>y</c> starts at <c>y × sourceStride</c>.</param>
    /// <param name="sourceStride">Bytes from one source row to the next; 0 means tightly packed.</param>
    /// <param name="sourceFormat">The source pixel layout.</param>
    /// <param name="destination">The destination pixels; row <c>y</c> starts at <c>y × destinationStride</c>. It
    /// may be the same memory as <paramref name="source"/> (same strides, same pixel size): an in-place
    /// conversion, such as premultiplying a decoded frame.</param>
    /// <param name="destinationStride">Bytes from one destination row to the next; 0 means tightly packed.</param>
    /// <param name="destinationFormat">The destination pixel layout.</param>
    /// <param name="width">Pixels per row.</param>
    /// <param name="height">Rows.</param>
    /// <exception cref="ArgumentException">A span is too short, a stride is shorter than a row, or the two
    /// buffers overlap other than exactly in place.</exception>
    public static void Convert(ReadOnlySpan<byte> source, int sourceStride, PixelFormat sourceFormat,
        Span<byte> destination, int destinationStride, PixelFormat destinationFormat, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceStride);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationStride);
        if (width == 0 || height == 0)
            return;
        var inRow = width * sourceFormat.BytesPerPixel;
        var outRow = width * destinationFormat.BytesPerPixel;
        var inStep = sourceStride == 0 ? inRow : sourceStride;
        var outStep = destinationStride == 0 ? outRow : destinationStride;
        if (inStep < inRow || outStep < outRow)
            throw new ArgumentException("A stride is shorter than a row.");
        if (source.Length < (long)inStep * (height - 1) + inRow)
            throw new ArgumentException("Source too small for the image.", nameof(source));
        if (destination.Length < (long)outStep * (height - 1) + outRow)
            throw new ArgumentException("Destination too small for the image.", nameof(destination));
        for (var y = 0; y < height; y++)
        {
            ConvertRow(source.Slice(y * inStep, inRow), sourceFormat, destination.Slice(y * outStep, outRow),
                destinationFormat, width);
        }
    }

    /// <summary>
    /// Converts a <paramref name="width"/> × <paramref name="height"/> image into a new, tightly-packed array in
    /// <paramref name="destinationFormat"/>: how an encoder without a native path for a format turns its input into
    /// one it has (its output is then exactly that of encoding the converted pixels).
    /// </summary>
    /// <param name="source">The source pixels; row <c>y</c> starts at <c>y × sourceStride</c>.</param>
    /// <param name="sourceStride">Bytes from one source row to the next; 0 means tightly packed.</param>
    /// <param name="sourceFormat">The source pixel layout.</param>
    /// <param name="destinationFormat">The layout to convert to.</param>
    /// <param name="width">Pixels per row.</param>
    /// <param name="height">Rows.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> is too short or the stride shorter than a row.</exception>
    public static byte[] ToPacked(ReadOnlySpan<byte> source, int sourceStride, PixelFormat sourceFormat,
        PixelFormat destinationFormat, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        var result = new byte[checked(width * height * destinationFormat.BytesPerPixel)];
        Convert(source, sourceStride, sourceFormat, result, 0, destinationFormat, width, height);
        return result;
    }

    /// <summary>
    /// Converts one row of <paramref name="width"/> pixels from <paramref name="sourceFormat"/> to
    /// <paramref name="destinationFormat"/>.
    /// </summary>
    /// <param name="source">At least <c>width × sourceFormat.BytesPerPixel</c> bytes.</param>
    /// <param name="sourceFormat">The source pixel layout.</param>
    /// <param name="destination">At least <c>width × destinationFormat.BytesPerPixel</c> bytes. It may be the
    /// same memory as <paramref name="source"/> when a pixel is the same size in both (RGBA → BGRA, or premultiplying,
    /// in place); otherwise the two must not overlap.</param>
    /// <param name="destinationFormat">The destination pixel layout.</param>
    /// <param name="width">Pixels to convert.</param>
    /// <exception cref="ArgumentException">A span is too short, or the spans overlap other than exactly in place.</exception>
    public static void ConvertRow(ReadOnlySpan<byte> source, PixelFormat sourceFormat, Span<byte> destination,
        PixelFormat destinationFormat, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        var inBpp = sourceFormat.BytesPerPixel;
        var outBpp = destinationFormat.BytesPerPixel;
        if (source.Length < (long)width * inBpp)
            throw new ArgumentException("Source too small for the row.", nameof(source));
        if (destination.Length < (long)width * outBpp)
            throw new ArgumentException("Destination too small for the row.", nameof(destination));
        // The kernels' 16-byte stores may run past a step's pixels; cut both spans to the row so that
        // they never run past it.
        source = source[..(width * inBpp)];
        destination = destination[..(width * outBpp)];
        if (width == 0)
            return;

        var overlaps = source.Overlaps(destination, out var offset);
        if (overlaps && (offset != 0 || inBpp != outBpp))
            throw new ArgumentException("Source and destination overlap: convert in place (same memory, same pixel size) or into a separate buffer.");

        if (sourceFormat == destinationFormat)
        {
            if (!overlaps)
                source.CopyTo(destination);
            return;
        }

        var unpremultiply = sourceFormat.IsPremultiplied && !destinationFormat.IsPremultiplied;
        var premultiply = destinationFormat.IsPremultiplied && !sourceFormat.IsPremultiplied;
        var wide = destinationFormat.Sample == SampleFormat.UInt16;
        var depthChanges = sourceFormat.Sample != destinationFormat.Sample;
        var layoutChanges = sourceFormat.Layout != destinationFormat.Layout;
        // The working format after each stage: the source's layout at the destination's depth.
        var mid = sourceFormat.WithSample(destinationFormat.Sample);

        // One stage straight from source to destination: the fast paths of the 1.x converter. In place only for
        // pixels that tile 16 bytes (1, 2, 4, 8 bytes): a vector step then stores exactly the bytes it loaded, so it
        // never clobbers the next step's input, and the scalar tail reads a whole pixel before writing it. (A
        // depth change alters the pixel size, so it is never in place.)
        if ((!overlaps || 16 % inBpp == 0) && !unpremultiply && !premultiply && !(depthChanges && layoutChanges))
        {
            if (depthChanges)
                ChangeDepth(source, destination, sourceFormat.Sample, width * sourceFormat.Channels);
            else
                ChangeLayout(source, sourceFormat.Layout, destination, destinationFormat.Layout, wide, width);
            return;
        }

        // Otherwise in chunks through stack buffers: unpremultiply, depth, layout, premultiply.
        Span<byte> a = stackalloc byte[ChunkPixels * 8];
        Span<byte> b = stackalloc byte[ChunkPixels * 8];
        for (var x = 0; x < width; x += ChunkPixels)
        {
            var n = Math.Min(ChunkPixels, width - x);
            scoped ReadOnlySpan<byte> cur = source.Slice(x * inBpp, n * inBpp);
            var dst = destination.Slice(x * outBpp, n * outBpp);
            var curFormat = sourceFormat;
            scoped Span<byte> t;

            if (overlaps)
            {
                cur.CopyTo(a);
                cur = a[..(n * inBpp)];
            }
            if (unpremultiply)
            {
                t = (overlaps ? b : a)[..(n * inBpp)];
                Unpremultiply(cur, t, sourceFormat.Sample, n);
                cur = t;
                curFormat = new PixelFormat(sourceFormat.Layout, sourceFormat.Sample);
            }
            if (depthChanges)
            {
                var len = n * mid.BytesPerPixel;
                if (layoutChanges)
                    t = PickOther(cur, a, b)[..len];
                else
                    t = dst;
                ChangeDepth(cur, t, curFormat.Sample, n * curFormat.Channels);
                cur = t;
                curFormat = curFormat.WithSample(destinationFormat.Sample);
            }
            if (layoutChanges)
            {
                ChangeLayout(cur, curFormat.Layout, dst, destinationFormat.Layout, wide, n);
            }
            else if (!depthChanges && !Unsafe.AreSame(ref MemoryMarshal.GetReference(cur), ref MemoryMarshal.GetReference(dst)))
            {
                cur.CopyTo(dst);
            }
            if (premultiply)
                Premultiply(dst, destinationFormat.Sample, n);
        }
    }

    /// <summary>
    /// Converts one row of a raster laid out as <see cref="IDecodedImage.Pixels"/> is: <paramref name="channels"/>
    /// samples per pixel (1 grey, 2 grey+alpha, 3 RGB, 4 RGBA), straight alpha. The same as
    /// <see cref="ConvertRow(ReadOnlySpan{byte}, PixelFormat, Span{byte}, PixelFormat, int)"/> with
    /// <see cref="PixelFormat.ForChannels"/> as the source format.
    /// </summary>
    /// <param name="source">Interleaved samples in host byte order.</param>
    /// <param name="channels">Samples per pixel, 1..4.</param>
    /// <param name="sampleFormat">The sample type of <paramref name="source"/>.</param>
    /// <param name="destination">At least <c>width × format.BytesPerPixel</c> bytes; see the other overload for overlap.</param>
    /// <param name="format">The destination pixel layout.</param>
    /// <param name="width">Pixels to convert.</param>
    /// <exception cref="ArgumentException">A span is too short, or <paramref name="channels"/> is not 1..4.</exception>
    /// <exception cref="NotSupportedException"><paramref name="sampleFormat"/> is <see cref="SampleFormat.Float32"/>.</exception>
    public static void ConvertRow(ReadOnlySpan<byte> source, int channels, SampleFormat sampleFormat,
        Span<byte> destination, PixelFormat format, int width)
    {
        if (channels is < 1 or > 4)
            throw new ArgumentException("Channels must be 1..4.", nameof(channels));
        if (sampleFormat == SampleFormat.Float32)
            throw new NotSupportedException("Float32 samples have no integer projection without an explicit tone/stretch policy.");
        ConvertRow(source, PixelFormat.ForChannels(channels, sampleFormat), destination, format, width);
    }

    // The scratch buffer of a and b that cur is not in.
    private static Span<byte> PickOther(ReadOnlySpan<byte> cur, Span<byte> a, Span<byte> b) =>
        cur.Overlaps(a) ? b : a;

    // ---- Depth ---------------------------------------------------------------------------------------------

    // samples values from `from` width to the other width: 16 → 8 keeps the high byte, 8 → 16 is v × 257.
    private static void ChangeDepth(ReadOnlySpan<byte> source, Span<byte> destination, SampleFormat from, int samples)
    {
        if (from == SampleFormat.UInt16)
            HighBytes(MemoryMarshal.Cast<byte, ushort>(source)[..samples], destination[..samples]);
        else
            Widen(source[..samples], MemoryMarshal.Cast<byte, ushort>(destination)[..samples]);
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

    // v × 257 for each 8-bit sample: the exact map of 0..255 onto 0..65535.
    private static void Widen(ReadOnlySpan<byte> source, Span<ushort> destination)
    {
        var i = 0;
        if (UseV128)
            i = WidenV128(source, destination);
        for (; i < source.Length; i++)
            destination[i] = (ushort)(source[i] * 257);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int WidenV128(ReadOnlySpan<byte> source, Span<ushort> destination)
    {
        // In range: a step at i reads source[i, i + 16) and writes destination[i, i + 16), and the
        // destination is at least as long as the source.
        if (destination.Length < source.Length)
            throw new ArgumentException("Destination too small.", nameof(destination));
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var k = Vector128.Create((ushort)257);
        var i = 0;
        for (; i + 16 <= source.Length; i += 16)
        {
            var (lo, hi) = Vector128.Widen(Vector128.LoadUnsafe(ref s, (nuint)i));
            (lo * k).StoreUnsafe(ref d, (nuint)i);
            (hi * k).StoreUnsafe(ref d, (nuint)(i + 8));
        }
        return i;
    }

    // ---- Alpha ---------------------------------------------------------------------------------------------

    // round(c × a / max) for the three colour samples of each 4-channel pixel (alpha last, in place).
    private static void Premultiply(Span<byte> pixels, SampleFormat sample, int width)
    {
        if (sample == SampleFormat.UInt16)
        {
            var p = MemoryMarshal.Cast<byte, ushort>(pixels)[..(width * 4)];
            for (var i = 0; i < p.Length; i += 4)
            {
                uint a = p[i + 3];
                // max is odd, so c·a/max is never a half and (c·a + (max − 1)/2) / max is round(c·a/max);
                // c·a + 32767 ≤ 65535² + 32767 < 2³².
                p[i] = (ushort)((p[i] * a + 32767u) / 65535u);
                p[i + 1] = (ushort)((p[i + 1] * a + 32767u) / 65535u);
                p[i + 2] = (ushort)((p[i + 2] * a + 32767u) / 65535u);
            }
            return;
        }

        var x = 0;
        if (UseShuffle)
            x = Premultiply8V128(pixels[..(width * 4)]);
        Premultiply8Scalar(pixels, x, width);
    }

    private static void Premultiply8Scalar(Span<byte> pixels, int from, int width)
    {
        for (var i = from * 4; i < width * 4; i += 4)
        {
            uint a = pixels[i + 3];
            pixels[i] = (byte)((pixels[i] * a + 127u) / 255u);
            pixels[i + 1] = (byte)((pixels[i + 1] * a + 127u) / 255u);
            pixels[i + 2] = (byte)((pixels[i + 2] * a + 127u) / 255u);
        }
    }

    /// <summary>The vector twin of <see cref="Premultiply8Scalar"/>, four pixels a step. Returns the pixels done.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int Premultiply8V128(Span<byte> pixels)
    {
        // In range: a step at byte i reads and writes pixels[i, i + 16), with i + 16 <= pixels.Length.
        // Arithmetic: t = c·m + 128 ≤ 255·255 + 128 = 65153 and t + (t >> 8) ≤ 65407 both fit 16 bits, and
        // (t + (t >> 8)) >> 8 equals round(c·m / 255) for every c·m in 0..65025 (the scalar twin's result:
        // 255 is odd, so there are no halves). The alpha lane multiplies by 255, which gives back a.
        ref var p = ref MemoryMarshal.GetReference(pixels);
        var spread = Vector128.Create((byte)3, 3, 3, Z, 7, 7, 7, Z, 11, 11, 11, Z, 15, 15, 15, Z);
        var opaque = Vector128.Create(0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, (byte)255);
        var round = Vector128.Create((ushort)128);
        var i = 0;
        for (; i + 16 <= pixels.Length; i += 16)
        {
            var v = Vector128.LoadUnsafe(ref p, (nuint)i);
            var m = Shuffle(v, spread) | opaque;
            var (vl, vh) = Vector128.Widen(v);
            var (ml, mh) = Vector128.Widen(m);
            var tl = vl * ml + round;
            var th = vh * mh + round;
            tl = (tl + (tl >>> 8)) >>> 8;
            th = (th + (th >>> 8)) >>> 8;
            Vector128.Narrow(tl, th).StoreUnsafe(ref p, (nuint)i);
        }
        return i / 4;
    }

    // Unpremultiplies each 4-channel pixel (alpha last) from source into destination (which may be the same).
    private static void Unpremultiply(ReadOnlySpan<byte> source, Span<byte> destination, SampleFormat sample, int width)
    {
        if (sample == SampleFormat.UInt16)
        {
            var s = MemoryMarshal.Cast<byte, ushort>(source)[..(width * 4)];
            var d = MemoryMarshal.Cast<byte, ushort>(destination)[..(width * 4)];
            for (var i = 0; i < s.Length; i += 4)
            {
                uint a = s[i + 3];
                d[i] = Unpremultiply16(s[i], a);
                d[i + 1] = Unpremultiply16(s[i + 1], a);
                d[i + 2] = Unpremultiply16(s[i + 2], a);
                d[i + 3] = (ushort)a;
            }
            return;
        }

        for (var i = 0; i < width * 4; i += 4)
        {
            uint a = source[i + 3];
            destination[i] = Unpremultiply8(source[i], a);
            destination[i + 1] = Unpremultiply8(source[i + 1], a);
            destination[i + 2] = Unpremultiply8(source[i + 2], a);
            destination[i + 3] = (byte)a;
        }
    }

    // 0 for a = 0, else min(255, round(c·255 / a)) with halves up: floor((2·c·255 + a) / (2·a)).
    private static byte Unpremultiply8(uint c, uint a) =>
        a == 0 ? (byte)0 : (byte)Math.Min(255u, (2 * c * 255 + a) / (2 * a));

    private static ushort Unpremultiply16(uint c, uint a) =>
        a == 0 ? (ushort)0 : (ushort)Math.Min(65535ul, (2ul * c * 65535 + a) / (2ul * a));

    // ---- Layout --------------------------------------------------------------------------------------------

    // Layout change at one depth (8-bit or 16-bit samples on both sides), straight alpha.
    private static void ChangeLayout(ReadOnlySpan<byte> source, ChannelLayout from, Span<byte> destination,
        ChannelLayout to, bool wide, int width)
    {
        if (from == to)
        {
            source.CopyTo(destination);
            return;
        }

        var toGrey = to is ChannelLayout.Gray or ChannelLayout.GrayAlpha;
        var fromColour = from is not (ChannelLayout.Gray or ChannelLayout.GrayAlpha);
        var i = 0;
        if (toGrey && fromColour)
        {
            if (!wide && to == ChannelLayout.Gray && UseV128 && (Channels(from) == 4 || UseShuffle))
                i = LumaV128(source, Channels(from), from is ChannelLayout.Bgra or ChannelLayout.Bgr, destination, width);
        }
        else if (UseShuffle)
        {
            var (mask, alpha, step) = Plan(from, to, wide ? 2 : 1);
            i = ShuffleV128(source, Channels(from) * (wide ? 2 : 1), destination, Channels(to) * (wide ? 2 : 1),
                width, mask, alpha, step);
        }

        if (wide)
            LayoutScalar16(MemoryMarshal.Cast<byte, ushort>(source), from, MemoryMarshal.Cast<byte, ushort>(destination), to, i, width);
        else
            LayoutScalar8(source, from, destination, to, i, width);
    }

    private static int Channels(ChannelLayout layout) => layout switch
    {
        ChannelLayout.Rgba or ChannelLayout.Bgra => 4,
        ChannelLayout.Rgb or ChannelLayout.Bgr => 3,
        ChannelLayout.Gray => 1,
        _ => 2,
    };

    // Which source sample holds R, G, B and A (−1: absent, so opaque); grey sources hold all three in sample 0.
    private static (int R, int G, int B, int A) Positions(ChannelLayout layout) => layout switch
    {
        ChannelLayout.Rgba => (0, 1, 2, 3),
        ChannelLayout.Bgra => (2, 1, 0, 3),
        ChannelLayout.Rgb => (0, 1, 2, -1),
        ChannelLayout.Bgr => (2, 1, 0, -1),
        ChannelLayout.Gray => (0, 0, 0, -1),
        _ => (0, 0, 0, 1),
    };

    // The source sample each destination sample comes from (−1: alpha, absent in the source). Only for layout
    // pairs that need no arithmetic: a grey destination needs a grey source.
    private static int SourceSample(ChannelLayout from, ChannelLayout to, int k)
    {
        var (r, g, b, a) = Positions(from);
        return to switch
        {
            ChannelLayout.Rgba => k switch { 0 => r, 1 => g, 2 => b, _ => a },
            ChannelLayout.Bgra => k switch { 0 => b, 1 => g, 2 => r, _ => a },
            ChannelLayout.Rgb => k switch { 0 => r, 1 => g, _ => b },
            ChannelLayout.Bgr => k switch { 0 => b, 1 => g, _ => r },
            ChannelLayout.Gray => r,
            _ => k == 0 ? r : a,
        };
    }

    /// <summary>
    /// The shuffle mask for 16 source bytes, the opaque-alpha bytes to OR in, and the pixels one step converts: as
    /// many as fit 16 bytes on both sides. <paramref name="sampleBytes"/> is 1 or 2.
    /// </summary>
    private static (Vector128<byte> Mask, Vector128<byte> Alpha, int Pixels) Plan(ChannelLayout from, ChannelLayout to, int sampleBytes)
    {
        var inBpp = Channels(from) * sampleBytes;
        var outBpp = Channels(to) * sampleBytes;
        var step = Math.Min(16 / inBpp, 16 / outBpp);
        Span<byte> mask = stackalloc byte[16];
        Span<byte> alpha = stackalloc byte[16];
        mask.Fill(Z);
        alpha.Clear();
        for (var p = 0; p < step; p++)
        {
            for (var k = 0; k < Channels(to); k++)
            {
                var s = SourceSample(from, to, k);
                for (var b = 0; b < sampleBytes; b++)
                {
                    var o = p * outBpp + k * sampleBytes + b;
                    if (s < 0)
                        alpha[o] = 0xFF;
                    else
                        mask[o] = (byte)(p * inBpp + s * sampleBytes + b); // < step × inBpp ≤ 16
                }
            }
        }
        return (Vector128.Create((ReadOnlySpan<byte>)mask), Vector128.Create((ReadOnlySpan<byte>)alpha), step);
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
        // stay inside their spans and its pixels inside the row. When the spans are the same
        // memory (in place), ConvertRow only gets here for equal pixel sizes that tile 16 bytes, so each step
        // stores exactly the 16 bytes it loaded and never touches a later step's input.
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

    /// <summary>8-bit RGB(A) or BGR(A) → luma, 16 pixels a step. Returns the pixels converted.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int LumaV128(ReadOnlySpan<byte> source, int channels, bool bgr, Span<byte> destination, int width)
    {
        // In range: a step at pixel i reads the four 16-byte blocks starting at (i + 4k) * channels,
        // k = 0..3, the last ending at (i + 12) * channels + 16 <= source.Length, and writes 16 bytes
        // at i <= destination.Length - 16.
        ref var s = ref MemoryMarshal.GetReference(source);
        ref var d = ref MemoryMarshal.GetReference(destination);
        var expand = Vector128.Create((byte)0, 1, 2, Z, 3, 4, 5, Z, 6, 7, 8, Z, 9, 10, 11, Z);
        // Weights by byte position: byte 0 is R (or B for BGR), byte 2 is B (or R).
        var w0 = Vector128.Create(bgr ? 29u : 77u);
        var w2 = Vector128.Create(bgr ? 77u : 29u);
        var i = 0;
        for (; i + 16 <= width && (long)(i + 12) * channels + 16 <= source.Length && i + 16 <= destination.Length; i += 16)
        {
            var a = Luma4(Load4(ref s, (i + 0) * channels, channels, expand), w0, w2);
            var b = Luma4(Load4(ref s, (i + 4) * channels, channels, expand), w0, w2);
            var c = Luma4(Load4(ref s, (i + 8) * channels, channels, expand), w0, w2);
            var e = Luma4(Load4(ref s, (i + 12) * channels, channels, expand), w0, w2);
            Vector128.Narrow(Vector128.Narrow(a, b), Vector128.Narrow(c, e)).StoreUnsafe(ref d, (nuint)i);
        }
        return i;
    }

    // Four pixels as 32-bit lanes byte0 | byte1 << 8 | byte2 << 16 (| A << 24, ignored).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Load4(ref byte s, int offset, int channels, Vector128<byte> expand)
    {
        var v = Vector128.LoadUnsafe(ref s, (nuint)offset);
        return (channels == 4 ? v : Shuffle(v, expand)).AsUInt32();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Luma4(Vector128<uint> v, Vector128<uint> w0, Vector128<uint> w2)
    {
        var low = Vector128.Create(0xFFu);
        var c0 = v & low;
        var g = (v >>> 8) & low;
        var c2 = (v >>> 16) & low;
        return (c0 * w0 + g * 150 + c2 * w2 + Vector128.Create(128u)) >>> 8;
    }

    // The scalar twin of every 8-bit layout kernel above, and their tail.
    private static void LayoutScalar8(ReadOnlySpan<byte> source, ChannelLayout from, Span<byte> destination,
        ChannelLayout to, int start, int width)
    {
        int inC = Channels(from), outC = Channels(to);
        var (pr, pg, pb, pa) = Positions(from);
        var greySource = from is ChannelLayout.Gray or ChannelLayout.GrayAlpha;
        for (var x = start; x < width; x++)
        {
            var p = source.Slice(x * inC, inC);
            int r = p[pr], g = p[pg], b = p[pb], a = pa < 0 ? 255 : p[pa];
            var o = destination.Slice(x * outC, outC);
            switch (to)
            {
                case ChannelLayout.Rgba: o[0] = (byte)r; o[1] = (byte)g; o[2] = (byte)b; o[3] = (byte)a; break;
                case ChannelLayout.Bgra: o[0] = (byte)b; o[1] = (byte)g; o[2] = (byte)r; o[3] = (byte)a; break;
                case ChannelLayout.Rgb: o[0] = (byte)r; o[1] = (byte)g; o[2] = (byte)b; break;
                case ChannelLayout.Bgr: o[0] = (byte)b; o[1] = (byte)g; o[2] = (byte)r; break;
                case ChannelLayout.Gray: o[0] = greySource ? (byte)r : (byte)((77 * r + 150 * g + 29 * b + 128) >> 8); break;
                default:
                    o[0] = greySource ? (byte)r : (byte)((77 * r + 150 * g + 29 * b + 128) >> 8);
                    o[1] = (byte)a;
                    break;
            }
        }
    }

    // The 16-bit twin: the same mapping on 16-bit samples (luma's sum stays below 2³¹).
    private static void LayoutScalar16(ReadOnlySpan<ushort> source, ChannelLayout from, Span<ushort> destination,
        ChannelLayout to, int start, int width)
    {
        int inC = Channels(from), outC = Channels(to);
        var (pr, pg, pb, pa) = Positions(from);
        var greySource = from is ChannelLayout.Gray or ChannelLayout.GrayAlpha;
        for (var x = start; x < width; x++)
        {
            var p = source.Slice(x * inC, inC);
            int r = p[pr], g = p[pg], b = p[pb], a = pa < 0 ? 65535 : p[pa];
            var o = destination.Slice(x * outC, outC);
            switch (to)
            {
                case ChannelLayout.Rgba: o[0] = (ushort)r; o[1] = (ushort)g; o[2] = (ushort)b; o[3] = (ushort)a; break;
                case ChannelLayout.Bgra: o[0] = (ushort)b; o[1] = (ushort)g; o[2] = (ushort)r; o[3] = (ushort)a; break;
                case ChannelLayout.Rgb: o[0] = (ushort)r; o[1] = (ushort)g; o[2] = (ushort)b; break;
                case ChannelLayout.Bgr: o[0] = (ushort)b; o[1] = (ushort)g; o[2] = (ushort)r; break;
                case ChannelLayout.Gray: o[0] = greySource ? (ushort)r : (ushort)((77 * r + 150 * g + 29 * b + 128) >> 8); break;
                default:
                    o[0] = greySource ? (ushort)r : (ushort)((77 * r + 150 * g + 29 * b + 128) >> 8);
                    o[1] = (ushort)a;
                    break;
            }
        }
    }

    // ---- Float stretch (unchanged from 1.x) ------------------------------------------------------------------

    /// <summary>
    /// Projects <paramref name="image"/> to 8-bit RGBA by stretching its colour range linearly onto
    /// 0..255: the display path for float rasters (a float TIFF, a reconstructed HDR), which
    /// <see cref="TryConvert"/> refuses because 8 bits need a tone decision. This is that decision at
    /// its simplest, for viewing: the smallest finite R, G or B value becomes 0 and the largest 255
    /// (<see cref="TryGetColorRange"/>), every channel on the same scale so hues keep their ratios. A
    /// constant or wholly non-finite image falls back to the range 0..1. Integer rasters are stretched
    /// too, from their <see cref="IDecodedImage.ToFloats"/> values. See
    /// <see cref="StretchToRgba8(ReadOnlySpan{float}, Span{byte}, float, float, bool)"/> for the mapping.
    /// </summary>
    /// <param name="image">The image to project.</param>
    /// <param name="srgbEncode">Apply the sRGB transfer function after stretching. Pass true for linear-light
    /// data meant for display, such as a reconstructed gain-map HDR (<see cref="TransferFunction.Linear"/>
    /// with <see cref="FloatSemantics.DisplayReferred"/>), which otherwise looks far too dark. It is not
    /// inferred from <see cref="IDecodedImage.ColorEncoding"/>: a float TIFF reports linear too, whatever
    /// its values hold.</param>
    public static byte[] StretchToRgba8(IDecodedImage image, bool srgbEncode = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rgba = image.ToFloats();
        if (!TryGetColorRange(rgba, out var min, out var max) || !(max > min))
            (min, max) = (0f, 1f);
        var destination = new byte[rgba.Length];
        StretchToRgba8(rgba, destination, min, max, srgbEncode);
        return destination;
    }

    /// <summary>
    /// <see cref="StretchToRgba8(IDecodedImage, bool)"/> onto a range the caller chooses — fixed across the
    /// frames of a sequence, or clipped to a percentile.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="min"/> and <paramref name="max"/> are not finite
    /// with <c>min &lt; max</c>.</exception>
    public static byte[] StretchToRgba8(IDecodedImage image, float min, float max, bool srgbEncode = false)
    {
        ArgumentNullException.ThrowIfNull(image);
        var rgba = image.ToFloats();
        var destination = new byte[rgba.Length];
        StretchToRgba8(rgba, destination, min, max, srgbEncode);
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
    /// <para>
    /// With <paramref name="srgbEncode"/>, R, G and B are first stretched onto 0..1, quantised to 16 bits
    /// (<c>round(t × 65535)</c>) and then encoded with the IEC 61966-2-1 sRGB curve
    /// (<c>12.92·t</c> up to 0.0031308, else <c>1.055·t^(1/2.4) − 0.055</c>) and rounded to 8 bits, through a
    /// table built once. Alpha is never encoded.
    /// </para>
    /// </summary>
    /// <param name="rgba">Interleaved RGBA floats, the <see cref="IDecodedImage.ToFloats"/> layout.</param>
    /// <param name="destination">At least <c>rgba.Length</c> bytes; must not overlap <paramref name="rgba"/>.</param>
    /// <param name="min">The value that maps to 0.</param>
    /// <param name="max">The value that maps to 255.</param>
    /// <param name="srgbEncode">Apply the sRGB transfer function after stretching (linear-light input).</param>
    /// <exception cref="ArgumentException">The length is not a multiple of 4, the destination is too
    /// small, or <paramref name="min"/> and <paramref name="max"/> are not finite with <c>min &lt; max</c>.</exception>
    public static void StretchToRgba8(ReadOnlySpan<float> rgba, Span<byte> destination, float min, float max, bool srgbEncode = false)
    {
        if (rgba.Length % 4 != 0)
            throw new ArgumentException("Interleaved RGBA needs a multiple of 4 values.", nameof(rgba));
        if (destination.Length < rgba.Length)
            throw new ArgumentException("Destination too small.", nameof(destination));
        if (!float.IsFinite(min) || !float.IsFinite(max) || !(min < max))
            throw new ArgumentException("The range must be finite with min < max.", nameof(max));

        if (srgbEncode)
        {
            var table = SrgbEncodeTable;
            var unit = 65535.0 / ((double)max - min);
            for (var i = 0; i < rgba.Length; i += 4)
            {
                destination[i] = table[Quantise16(rgba[i], min, unit)];
                destination[i + 1] = table[Quantise16(rgba[i + 1], min, unit)];
                destination[i + 2] = table[Quantise16(rgba[i + 2], min, unit)];
                destination[i + 3] = Stretch(rgba[i + 3], 0, 255.0);
            }
            return;
        }

        var scale = 255.0 / ((double)max - min);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            destination[i] = Stretch(rgba[i], min, scale);
            destination[i + 1] = Stretch(rgba[i + 1], min, scale);
            destination[i + 2] = Stretch(rgba[i + 2], min, scale);
            destination[i + 3] = Stretch(rgba[i + 3], 0, 255.0);
        }
    }

    // The 16-bit index into SrgbEncodeTable; NaN lands on 0 like Stretch.
    private static int Quantise16(float value, double min, double unit)
    {
        var x = (value - min) * unit + 0.5;
        return x >= 65535 ? 65535 : x >= 1 ? (int)x : 0;
    }

    // round(255 × sRGB(i / 65535)) for every 16-bit linear value: one pow per entry, built on first use.
    private static byte[] SrgbEncodeTable => _srgbEncodeTable ??= BuildSrgbEncodeTable();
    private static byte[]? _srgbEncodeTable;

    private static byte[] BuildSrgbEncodeTable()
    {
        var table = new byte[65536];
        for (var i = 0; i < table.Length; i++)
        {
            var t = i / 65535.0;
            var encoded = t <= 0.0031308 ? 12.92 * t : 1.055 * Math.Pow(t, 1 / 2.4) - 0.055;
            table[i] = (byte)Math.Min(255, (int)(encoded * 255 + 0.5));
        }
        return table;
    }

    private static byte Stretch(float value, double min, double scale)
    {
        // NaN fails both comparisons and lands on 0; ±∞ clamps like any out-of-range value.
        var x = (value - min) * scale + 0.5;
        return x >= 255 ? (byte)255 : x >= 1 ? (byte)x : (byte)0;
    }
}
