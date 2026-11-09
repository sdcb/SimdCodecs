using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// Turns stored pixels into the picture as it should be shown: the eight transforms of the EXIF / TIFF Orientation tag
/// (<see cref="ImageOrientation"/>). What <see cref="DecodeOptions.ApplyOrientation"/> applies after a decode, and what a
/// caller that decoded without it applies itself.
/// <para>
/// Stored pixel (x, y) of a <c>w × h</c> image lands at displayed pixel (u, v): (x, y) for
/// <see cref="ImageOrientation.Normal"/>, (w − 1 − x, y) for <see cref="ImageOrientation.FlipHorizontal"/>,
/// (w − 1 − x, h − 1 − y) for <see cref="ImageOrientation.Rotate180"/>, (x, h − 1 − y) for
/// <see cref="ImageOrientation.FlipVertical"/>, (y, x) for <see cref="ImageOrientation.Transpose"/>, (h − 1 − y, x) for
/// <see cref="ImageOrientation.Rotate90"/>, (h − 1 − y, w − 1 − x) for <see cref="ImageOrientation.Transverse"/> and
/// (y, w − 1 − x) for <see cref="ImageOrientation.Rotate270"/>. The last four display an <c>h × w</c> image. Pixels move
/// whole: their bytes are copied, never converted.
/// </para>
/// </summary>
public static class PixelTransform
{
    // The side of the square tiles a quarter turn walks: 16 source rows of 16 pixels stay in L1 while the tile's
    // columns become destination rows.
    private const int Tile = 16;

    /// <summary>
    /// Writes the <paramref name="width"/> × <paramref name="height"/> stored image <paramref name="source"/> into
    /// <paramref name="destination"/> as <paramref name="orientation"/> displays it: <paramref name="width"/> ×
    /// <paramref name="height"/>, or <paramref name="height"/> × <paramref name="width"/> for orientations 5 to 8
    /// (<see cref="ImageInfo.SwapsDimensions"/>). Bytes between destination rows are left untouched.
    /// </summary>
    /// <param name="source">The stored pixels: row <c>y</c> starts at byte <c>y × sourceStride</c>.</param>
    /// <param name="width">The stored width, in pixels.</param>
    /// <param name="height">The stored height, in rows.</param>
    /// <param name="sourceStride">Bytes from one source row to the next; 0 means tightly packed.</param>
    /// <param name="format">The layout of both buffers; only its <see cref="PixelFormat.BytesPerPixel"/> matters.</param>
    /// <param name="orientation">The stored image's orientation (<see cref="ImageInfo.Orientation"/>).</param>
    /// <param name="destination">The displayed pixels: row <c>v</c> starts at byte <c>v × destinationStride</c>. It must
    /// not overlap <paramref name="source"/>.</param>
    /// <param name="destinationStride">Bytes from one destination row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive, a stride is negative, or
    /// <paramref name="orientation"/> is not 1..8.</exception>
    /// <exception cref="ArgumentException">A stride is shorter than its row, a buffer is too small, or the two buffers
    /// overlap.</exception>
    public static void Orient(ReadOnlySpan<byte> source, int width, int height, int sourceStride, PixelFormat format,
        ImageOrientation orientation, Span<byte> destination, int destinationStride) =>
        Orient(source, width, height, sourceStride, format.BytesPerPixel, orientation, destination, destinationStride);

    /// <summary>
    /// <paramref name="image"/> as <paramref name="orientation"/> displays it, in a new <see cref="RasterImage"/> (rented
    /// from <paramref name="pool"/> when it is not null): the same channels, sample format, bit depth and colour
    /// encoding, with <see cref="SwapsDimensions"/> orientations exchanging width and height. The metadata is kept, but
    /// its Exif block, when it records an Orientation other than 1, is a copy with that value set to 1 (everything else
    /// in it byte for byte), so a viewer or a transcode does not turn the pixels a second time.
    /// <para>
    /// The result is always a new image, <see cref="ImageOrientation.Normal"/> included (a copy): dispose it and
    /// <paramref name="image"/> separately. <paramref name="image"/> is left as it is.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is not 1..8.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="image"/> has been disposed.</exception>
    public static RasterImage Orient(IDecodedImage image, ImageOrientation orientation, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        CheckOrientation(orientation);
        var swaps = SwapsDimensions(orientation);
        var (w, h) = (image.Width, image.Height);
        var full = RasterImage.BytesPerSample(image.SampleFormat) * 8;
        var result = RasterImage.Create(swaps ? h : w, swaps ? w : h, image.Channels, image.SampleFormat, pool,
            image.ColorEncoding, image.BitDepth == full ? 0 : image.BitDepth, ResetOrientation(image.Metadata));
        try
        {
            Orient(image.Pixels, w, h, 0, image.Channels * RasterImage.BytesPerSample(image.SampleFormat), orientation,
                result.WritablePixels, 0);
        }
        catch
        {
            result.Dispose();
            throw;
        }
        return result;
    }

    /// <summary>
    /// <paramref name="buffer"/> as <paramref name="orientation"/> displays it, in a new <see cref="PixelBuffer"/> of the
    /// same format (rented from <paramref name="pool"/> when it is not null), with the colour encoding and the metadata
    /// kept as <see cref="Orient(IDecodedImage, ImageOrientation, ArrayPool{byte})"/> keeps them (the Exif Orientation set
    /// to 1). Always a new buffer, <see cref="ImageOrientation.Normal"/> included: dispose both.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> is not 1..8.</exception>
    /// <exception cref="ObjectDisposedException"><paramref name="buffer"/> has been disposed.</exception>
    public static PixelBuffer Orient(PixelBuffer buffer, ImageOrientation orientation, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        CheckOrientation(orientation);
        var swaps = SwapsDimensions(orientation);
        var (w, h) = (buffer.Width, buffer.Height);
        var source = buffer.Pixels; // throws once disposed, before anything is rented
        var result = PixelBuffer.Create(swaps ? h : w, swaps ? w : h, buffer.Format, pool, buffer.ColorEncoding,
            ResetOrientation(buffer.Metadata));
        try
        {
            Orient(source, w, h, buffer.Stride, buffer.Format.BytesPerPixel, orientation, result.WritablePixels, result.Stride);
        }
        catch
        {
            result.Dispose();
            throw;
        }
        return result;
    }

    /// <summary>True for orientations 5..8, which display a stored <c>w × h</c> image as <c>h × w</c>.</summary>
    internal static bool SwapsDimensions(ImageOrientation orientation) => orientation >= ImageOrientation.Transpose;

    /// <summary>
    /// The stored rectangle that <paramref name="orientation"/> displays as <paramref name="displayed"/>, in an image whose
    /// stored size is <paramref name="width"/> × <paramref name="height"/> (the inverse of the mapping in the class
    /// remarks). The caller has checked that <paramref name="displayed"/> lies inside the displayed image.
    /// </summary>
    internal static PixelRect ToStoredRect(PixelRect displayed, int width, int height, ImageOrientation orientation)
    {
        var (x, y, w, h) = displayed;
        return orientation switch
        {
            ImageOrientation.FlipHorizontal => new(width - x - w, y, w, h),
            ImageOrientation.Rotate180 => new(width - x - w, height - y - h, w, h),
            ImageOrientation.FlipVertical => new(x, height - y - h, w, h),
            ImageOrientation.Transpose => new(y, x, h, w),
            ImageOrientation.Rotate90 => new(y, height - x - w, h, w),
            ImageOrientation.Transverse => new(width - y - h, height - x - w, h, w),
            ImageOrientation.Rotate270 => new(width - y - h, x, h, w),
            _ => displayed,
        };
    }

    /// <summary>
    /// <paramref name="metadata"/> with its Exif Orientation set to 1 in a copy of the block, or
    /// <paramref name="metadata"/> itself when the block is empty, malformed, has no Orientation or already holds 1.
    /// </summary>
    internal static ImageMetadata ResetOrientation(ImageMetadata metadata)
    {
        if (metadata.Exif.IsEmpty)
            return metadata;
        var copy = metadata.Exif.ToArray();
        return ExifOrientation.TryReset(copy) ? metadata with { Exif = copy } : metadata;
    }

    private static void CheckOrientation(ImageOrientation orientation)
    {
        if (orientation is < ImageOrientation.Normal or > ImageOrientation.Rotate270)
            throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "Orientation must be 1..8.");
    }

    /// <summary>The span form, on a pixel size in bytes (1..16).</summary>
    internal static void Orient(ReadOnlySpan<byte> source, int width, int height, int sourceStride, int bytesPerPixel,
        ImageOrientation orientation, Span<byte> destination, int destinationStride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceStride);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationStride);
        CheckOrientation(orientation);
        var swaps = SwapsDimensions(orientation);
        var (outWidth, outHeight) = swaps ? (height, width) : (width, height);
        var inRow = (long)width * bytesPerPixel;
        var outRow = (long)outWidth * bytesPerPixel;
        long inStep = sourceStride == 0 ? inRow : sourceStride;
        long outStep = destinationStride == 0 ? outRow : destinationStride;
        if (inStep < inRow || outStep < outRow)
            throw new ArgumentException("A stride is shorter than a row.");
        if (source.Length < inStep * (height - 1) + inRow)
            throw new ArgumentException($"The source holds {source.Length} bytes, too few for a {width}x{height} image at stride {inStep}.", nameof(source));
        if (destination.Length < outStep * (outHeight - 1) + outRow)
            throw new ArgumentException($"The destination holds {destination.Length} bytes, too few for a {outWidth}x{outHeight} image at stride {outStep}.", nameof(destination));
        if (source.Overlaps(destination))
            throw new ArgumentException("The source and destination overlap.", nameof(destination));

        // Every offset the kernels form is a pixel inside one of the two images just checked: source pixel (x, y) at
        // y·inStep + x·bpp with x < width, y < height, destination pixel (u, v) at v·outStep + u·bpp with u < outWidth,
        // v < outHeight. Both lengths fit an int, so the offsets fit an nint.
        ref var src = ref MemoryMarshal.GetReference(source);
        ref var dst = ref MemoryMarshal.GetReference(destination);
        var layout = new Layout(width, height, (nint)inStep, (nint)outStep, orientation);
        switch (bytesPerPixel)
        {
            case 1: Run<byte>(ref src, ref dst, layout); break;
            case 2: Run<ushort>(ref src, ref dst, layout); break;
            case 3: Run<Pixel3>(ref src, ref dst, layout); break;
            case 4: Run<uint>(ref src, ref dst, layout); break;
            case 6: Run<Pixel6>(ref src, ref dst, layout); break;
            case 8: Run<ulong>(ref src, ref dst, layout); break;
            case 12: Run<Pixel12>(ref src, ref dst, layout); break;
            case 16: Run<Pixel16>(ref src, ref dst, layout); break;
            default: throw new ArgumentOutOfRangeException(nameof(bytesPerPixel), bytesPerPixel, "Pixels are 1, 2, 3, 4, 6, 8, 12 or 16 bytes.");
        }
    }

    private readonly record struct Layout(int Width, int Height, nint InStep, nint OutStep, ImageOrientation Orientation);

    private static void Run<T>(ref byte src, ref byte dst, Layout layout) where T : unmanaged
    {
        if (SwapsDimensions(layout.Orientation))
            Transpose<T>(ref src, ref dst, layout);
        else
            Mirror<T>(ref src, ref dst, layout);
    }

    // Orientations 1..4: destination row v is source row v (or h − 1 − v), copied as is or reversed pixel by pixel.
    private static void Mirror<T>(ref byte src, ref byte dst, Layout layout) where T : unmanaged
    {
        var (w, h) = (layout.Width, layout.Height);
        var o = layout.Orientation;
        var flipX = o is ImageOrientation.FlipHorizontal or ImageOrientation.Rotate180;
        var flipY = o is ImageOrientation.Rotate180 or ImageOrientation.FlipVertical;
        var size = Unsafe.SizeOf<T>();
        var rowBytes = (nint)w * size;
        for (var v = 0; v < h; v++)
        {
            var y = flipY ? h - 1 - v : v;
            ref var from = ref Unsafe.Add(ref src, y * layout.InStep);
            ref var to = ref Unsafe.Add(ref dst, v * layout.OutStep);
            if (!flipX)
            {
                MemoryMarshal.CreateReadOnlySpan(ref from, (int)rowBytes).CopyTo(MemoryMarshal.CreateSpan(ref to, (int)rowBytes));
                continue;
            }
            // Source pixel w − 1 − u to destination pixel u.
            ref var last = ref Unsafe.Add(ref from, rowBytes - size);
            for (nint u = 0; u < w; u++)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, u * size), Unsafe.ReadUnaligned<T>(ref Unsafe.Subtract(ref last, u * size)));
        }
    }

    // Orientations 5..8, a quarter turn: destination row v is source column v (or w − 1 − v), destination column u is
    // source row u (or h − 1 − u). Walked in Tile × Tile squares so the source rows a square reads stay cached while
    // its columns are written out as destination rows.
    private static void Transpose<T>(ref byte src, ref byte dst, Layout layout) where T : unmanaged
    {
        var (w, h) = (layout.Width, layout.Height);
        var o = layout.Orientation;
        var flipX = o is ImageOrientation.Transverse or ImageOrientation.Rotate270; // source column w − 1 − v
        var flipY = o is ImageOrientation.Rotate90 or ImageOrientation.Transverse;  // source row h − 1 − u
        var size = Unsafe.SizeOf<T>();
        var rowStep = flipY ? -layout.InStep : layout.InStep; // the source offset from one destination column to the next
        // Destination: outWidth = h columns (u), outHeight = w rows (v).
        for (var v0 = 0; v0 < w; v0 += Tile)
        {
            var v1 = Math.Min(v0 + Tile, w);
            for (var u0 = 0; u0 < h; u0 += Tile)
            {
                var u1 = Math.Min(u0 + Tile, h);
                var y0 = flipY ? h - 1 - u0 : u0;
                for (var v = v0; v < v1; v++)
                {
                    var x = flipX ? w - 1 - v : v;
                    ref var from = ref Unsafe.Add(ref src, y0 * layout.InStep + (nint)x * size);
                    ref var to = ref Unsafe.Add(ref dst, v * layout.OutStep + (nint)u0 * size);
                    nint at = 0;
                    for (nint u = 0; u < u1 - u0; u++, at += rowStep)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, u * size), Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref from, at)));
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 3)]
    private struct Pixel3
    {
        private byte _0, _1, _2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 6)]
    private struct Pixel6
    {
        private ushort _0, _1, _2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 12)]
    private struct Pixel12
    {
        private uint _0, _1, _2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct Pixel16
    {
        private ulong _0, _1;
    }
}
