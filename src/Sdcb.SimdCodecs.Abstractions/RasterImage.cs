using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The default concrete <see cref="IDecodedImage"/>: an interleaved, row-major, tightly-packed pixel buffer plus the
/// file's colour signalling and metadata. Codec packages create one from their decode output; consumers read
/// <see cref="Pixels"/> for the fidelity tier or call <see cref="IDecodedImage.TryCopyTo"/> / <see cref="ToRgba8"/> for
/// display.
/// <para>
/// Four ways to make one: <see cref="Wrap(int, int, int, SampleFormat, byte[], ColorEncoding, int, ImageMetadata)"/>
/// takes over an array the caller filled; <see cref="Allocate"/> and <see cref="Rent"/> create an empty one to fill
/// through <see cref="WritablePixels"/> (from new memory or from a pool); and
/// <see cref="Wrap(int, int, int, SampleFormat, IMemoryOwner{byte}, ColorEncoding, int, ImageMetadata)"/>
/// takes over memory with an owner. <see cref="Dispose"/> returns pooled or owned memory; afterwards
/// <see cref="Pixels"/> throws <see cref="ObjectDisposedException"/>, whichever way the image was made.
/// </para>
/// </summary>
public sealed class RasterImage : IDecodedImage
{
    private readonly int _length;
    private byte[]? _array;            // array storage (allocated, rented or wrapped)
    private ArrayPool<byte>? _pool;    // the pool _array came from, if rented
    private IMemoryOwner<byte>? _owner; // owned storage
    private readonly int _bitDepth;
    private int _disposed;

    private RasterImage(int width, int height, int channels, SampleFormat sampleFormat, int bitDepth,
        ColorEncoding? colorEncoding, ImageMetadata? metadata)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if (channels is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channels must be 1..4.");
        var full = BytesPerSample(sampleFormat) * 8;
        if (bitDepth != 0 && (bitDepth < 1 || bitDepth > full))
            throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, $"Bit depth must be 1..{full} for {sampleFormat}.");

        Width = width;
        Height = height;
        Channels = channels;
        SampleFormat = sampleFormat;
        _bitDepth = bitDepth;
        _length = RasterSize.Elements(width, height, channels * BytesPerSample(sampleFormat));
        ColorEncoding = colorEncoding ?? ColorEncoding.AssumedSrgb;
        Metadata = metadata ?? ImageMetadata.Empty;
    }

    /// <summary>
    /// Takes over <paramref name="pixels"/>, which the caller has filled: ownership transfers to the image, so do not
    /// change the array afterwards.
    /// </summary>
    /// <param name="width">Pixel width (&gt; 0).</param>
    /// <param name="height">Pixel height (&gt; 0).</param>
    /// <param name="channels">Samples per pixel, 1..4.</param>
    /// <param name="sampleFormat">The per-channel sample type of <paramref name="pixels"/>.</param>
    /// <param name="pixels">Interleaved, row-major, tightly-packed samples in host byte order, straight alpha; at least
    /// <c>width × height × channels × bytesPerSample</c> bytes.</param>
    /// <param name="colorEncoding">How the samples encode colour; null means <see cref="ColorEncoding.AssumedSrgb"/>.</param>
    /// <param name="bitDepth">Bits of real precision per sample; 0 means the full width of <paramref name="sampleFormat"/>.</param>
    /// <param name="metadata">The file's metadata blocks (Exif, XMP, ICC profile).</param>
    /// <exception cref="ArgumentException">An argument is out of range (the image needs more than <see cref="Array.MaxLength"/>
    /// bytes, among others), or <paramref name="pixels"/> is too small.</exception>
    public static RasterImage Wrap(int width, int height, int channels, SampleFormat sampleFormat, byte[] pixels,
        ColorEncoding? colorEncoding = null, int bitDepth = 0, ImageMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var image = new RasterImage(width, height, channels, sampleFormat, bitDepth, colorEncoding, metadata);
        if (pixels.Length < image._length)
            throw new ArgumentException($"Pixel buffer too small: {pixels.Length} < {image._length} (width*height*channels*bytesPerSample).", nameof(pixels));
        image._array = pixels;
        return image;
    }

    /// <summary>
    /// Takes over memory with an owner: the image reads the first <c>width × height × channels × bytesPerSample</c>
    /// bytes of <c>owner.Memory</c>, and disposes <paramref name="owner"/> when it is disposed.
    /// </summary>
    /// <param name="width">Pixel width (&gt; 0).</param>
    /// <param name="height">Pixel height (&gt; 0).</param>
    /// <param name="channels">Samples per pixel, 1..4.</param>
    /// <param name="sampleFormat">The per-channel sample type.</param>
    /// <param name="owner">The memory holding the samples, laid out as <see cref="Pixels"/> describes.</param>
    /// <param name="colorEncoding">How the samples encode colour; null means <see cref="ColorEncoding.AssumedSrgb"/>.</param>
    /// <param name="bitDepth">Bits of real precision per sample; 0 means the full width of <paramref name="sampleFormat"/>.</param>
    /// <param name="metadata">The file's metadata blocks (Exif, XMP, ICC profile).</param>
    /// <exception cref="ArgumentException">An argument is out of range, or the memory is too small.</exception>
    public static RasterImage Wrap(int width, int height, int channels, SampleFormat sampleFormat, IMemoryOwner<byte> owner,
        ColorEncoding? colorEncoding = null, int bitDepth = 0, ImageMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var image = new RasterImage(width, height, channels, sampleFormat, bitDepth, colorEncoding, metadata);
        if (owner.Memory.Length < image._length)
            throw new ArgumentException($"Pixel memory too small: {owner.Memory.Length} < {image._length}.", nameof(owner));
        image._owner = owner;
        return image;
    }

    /// <summary>Creates an image over new, zeroed memory, to fill through <see cref="WritablePixels"/>.</summary>
    /// <param name="width">Pixel width (&gt; 0).</param>
    /// <param name="height">Pixel height (&gt; 0).</param>
    /// <param name="channels">Samples per pixel, 1..4.</param>
    /// <param name="sampleFormat">The per-channel sample type.</param>
    /// <param name="colorEncoding">How the samples encode colour; null means <see cref="ColorEncoding.AssumedSrgb"/>.</param>
    /// <param name="bitDepth">Bits of real precision per sample; 0 means the full width of <paramref name="sampleFormat"/>.</param>
    /// <param name="metadata">The file's metadata blocks (Exif, XMP, ICC profile).</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, or the image needs more than
    /// <see cref="Array.MaxLength"/> bytes (2 GB), the most one array holds.</exception>
    public static RasterImage Allocate(int width, int height, int channels, SampleFormat sampleFormat,
        ColorEncoding? colorEncoding = null, int bitDepth = 0, ImageMetadata? metadata = null)
    {
        var image = new RasterImage(width, height, channels, sampleFormat, bitDepth, colorEncoding, metadata);
        image._array = GC.AllocateUninitializedArray<byte>(image._length);
        image._array.AsSpan().Clear();
        return image;
    }

    /// <summary>
    /// Creates an image over memory rented from <paramref name="pool"/>, to fill through <see cref="WritablePixels"/>;
    /// <see cref="Dispose"/> returns it. The memory is not cleared.
    /// </summary>
    /// <param name="width">Pixel width (&gt; 0).</param>
    /// <param name="height">Pixel height (&gt; 0).</param>
    /// <param name="channels">Samples per pixel, 1..4.</param>
    /// <param name="sampleFormat">The per-channel sample type.</param>
    /// <param name="pool">The pool; null means <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <param name="colorEncoding">How the samples encode colour; null means <see cref="ColorEncoding.AssumedSrgb"/>.</param>
    /// <param name="bitDepth">Bits of real precision per sample; 0 means the full width of <paramref name="sampleFormat"/>.</param>
    /// <param name="metadata">The file's metadata blocks (Exif, XMP, ICC profile).</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, or the image needs more than
    /// <see cref="Array.MaxLength"/> bytes (2 GB), the most one array holds.</exception>
    public static RasterImage Rent(int width, int height, int channels, SampleFormat sampleFormat, ArrayPool<byte>? pool = null,
        ColorEncoding? colorEncoding = null, int bitDepth = 0, ImageMetadata? metadata = null)
    {
        var image = new RasterImage(width, height, channels, sampleFormat, bitDepth, colorEncoding, metadata);
        image._pool = pool ?? ArrayPool<byte>.Shared;
        image._array = image._pool.Rent(image._length);
        return image;
    }

    /// <summary>
    /// Creates an image in new or pooled memory: <see cref="Rent"/> from <paramref name="pool"/> when it is not null,
    /// otherwise <see cref="Allocate"/>. What a codec calls with <see cref="DecodeOptions.Pool"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, or the image needs more than
    /// <see cref="Array.MaxLength"/> bytes (2 GB), the most one array holds.</exception>
    public static RasterImage Create(int width, int height, int channels, SampleFormat sampleFormat, ArrayPool<byte>? pool,
        ColorEncoding? colorEncoding = null, int bitDepth = 0, ImageMetadata? metadata = null) =>
        pool is null
            ? Allocate(width, height, channels, sampleFormat, colorEncoding, bitDepth, metadata)
            : Rent(width, height, channels, sampleFormat, pool, colorEncoding, bitDepth, metadata);

    /// <summary>
    /// Copies the pixels of <paramref name="region"/> out of <paramref name="source"/> into a new image (rented from
    /// <paramref name="pool"/> when it is not null), keeping its layout, colour signalling and metadata.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="region"/> does not lie inside the image.</exception>
    public static RasterImage Crop(IDecodedImage source, PixelRect region, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!region.FitsIn(source.Width, source.Height))
            throw new ArgumentException($"The region {region} does not lie inside the {source.Width}x{source.Height} image.", nameof(region));
        var image = Create(region.Width, region.Height, source.Channels, source.SampleFormat, pool,
            source.ColorEncoding, source.BitDepth == BytesPerSample(source.SampleFormat) * 8 ? 0 : source.BitDepth,
            source.Metadata);
        var bpp = source.Channels * BytesPerSample(source.SampleFormat);
        var inRow = source.Width * bpp;
        var outRow = region.Width * bpp;
        var from = source.Pixels;
        var to = image.WritablePixels;
        for (var y = 0; y < region.Height; y++)
            from.Slice((region.Y + y) * inRow + region.X * bpp, outRow).CopyTo(to.Slice(y * outRow, outRow));
        return image;
    }

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public int Channels { get; }

    /// <inheritdoc />
    public SampleFormat SampleFormat { get; }

    /// <inheritdoc />
    public int BitDepth => _bitDepth != 0 ? _bitDepth : BytesPerSample(SampleFormat) * 8;

    /// <inheritdoc />
    public ReadOnlySpan<byte> Pixels => WritablePixels;

    /// <summary>
    /// The pixel buffer, for the code that fills the image (after <see cref="Allocate"/> or <see cref="Rent"/>). An image
    /// handed to someone else is theirs to read: do not write to it then.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public Span<byte> WritablePixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _array is { } a ? a.AsSpan(0, _length) : _owner!.Memory.Span[.._length];
        }
    }

    /// <inheritdoc />
    public ColorEncoding ColorEncoding { get; }

    /// <inheritdoc />
    public ImageMetadata Metadata { get; }

    /// <summary>Bytes per single channel sample for a given <see cref="SampleFormat"/>.</summary>
    public static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.UInt8 => 1,
        SampleFormat.UInt16 => 2,
        SampleFormat.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <inheritdoc />
    public byte[] ToRgba8()
    {
        if (SampleFormat == SampleFormat.Float32)
        {
            // Float rasters have no canonical 8-bit mapping without a tone/stretch policy (black/white
            // point, gamma) - that is a consumer decision, not a codec one. Read Pixels and apply your
            // own conversion, or PixelConverter.StretchToRgba8.
            throw new NotSupportedException(
                $"{SampleFormat} rasters cannot be expanded to 8-bit RGBA without an explicit tone/stretch policy; read Pixels instead.");
        }
        var dst = new byte[RasterSize.Elements(Width, Height, 4)];
        // Tightly packed on both sides, so the whole raster is one long row.
        PixelConverter.ConvertRow(Pixels, Channels, SampleFormat, dst, PixelFormat.Rgba32, Width * Height);
        return dst;
    }

    /// <inheritdoc />
    public ushort[] ToRgba16()
    {
        if (SampleFormat == SampleFormat.Float32)
            throw new NotSupportedException($"{SampleFormat} rasters have no integer projection without a tone/stretch policy; read Pixels instead.");
        var dst = new ushort[RasterSize.Elements(Width, Height, 4, sizeof(ushort))];
        PixelConverter.ConvertRow(Pixels, Channels, SampleFormat, MemoryMarshal.AsBytes(dst.AsSpan()), PixelFormat.Rgba64, Width * Height);
        return dst;
    }

    /// <inheritdoc />
    public bool TryCopyTo(Span<byte> destination, int stride, PixelFormat format) =>
        PixelConverter.TryConvert(this, destination, stride, format);

    /// <inheritdoc />
    public float[] ToFloats()
    {
        var dst = new float[RasterSize.Elements(Width, Height, 4, sizeof(float))];
        ExpandToFloats(dst);
        return dst;
    }

    /// <summary>
    /// Widens into a caller-provided RGBA float32 destination (row-major, 4
    /// floats/pixel, R,G,B,A order) — the allocation-free form of
    /// <see cref="ToFloats"/>. <paramref name="destination"/> must be at least
    /// <c>Width * Height * 4</c> floats. Integer samples divide by their max code
    /// (exact at the endpoints); Float32 samples copy verbatim.
    /// </summary>
    public void ExpandToFloats(Span<float> destination)
    {
        var pixelCount = Width * Height;
        if (destination.Length < (long)pixelCount * 4)
            throw new ArgumentException("Destination too small for Width*Height*4 RGBA floats.", nameof(destination));

        var pixels = Pixels;
        switch (SampleFormat)
        {
            case SampleFormat.UInt8:
                ExpandU8F(pixels, Channels, pixelCount, destination);
                break;
            case SampleFormat.UInt16:
                ExpandU16F(pixels, Channels, pixelCount, destination);
                break;
            default:
                ExpandF32(pixels, Channels, pixelCount, destination);
                break;
        }
    }

    /// <summary>
    /// Returns rented or owned memory; afterwards <see cref="Pixels"/> throws <see cref="ObjectDisposedException"/>.
    /// Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_pool is { } pool && _array is { } array)
            pool.Return(array);
        _owner?.Dispose();
        _array = null;
        _pool = null;
        _owner = null;
    }

    // Division (not multiply-by-reciprocal) so the endpoints are exact:
    // 255/255f == 1f and 65535/65535f == 1f in IEEE single.
    private static void ExpandU8F(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<float> dst)
    {
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1: // gray -> RGB, opaque
                    dst[d] = dst[d + 1] = dst[d + 2] = src[s] / 255f;
                    dst[d + 3] = 1f;
                    break;
                case 2: // gray + alpha
                    dst[d] = dst[d + 1] = dst[d + 2] = src[s] / 255f;
                    dst[d + 3] = src[s + 1] / 255f;
                    break;
                case 3: // RGB, opaque
                    dst[d] = src[s] / 255f;
                    dst[d + 1] = src[s + 1] / 255f;
                    dst[d + 2] = src[s + 2] / 255f;
                    dst[d + 3] = 1f;
                    break;
                default: // 4: RGBA
                    dst[d] = src[s] / 255f;
                    dst[d + 1] = src[s + 1] / 255f;
                    dst[d + 2] = src[s + 2] / 255f;
                    dst[d + 3] = src[s + 3] / 255f;
                    break;
            }
        }
    }

    private static void ExpandU16F(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<float> dst)
    {
        // Host byte order per the IDecodedImage contract, so a straight reinterpret is valid.
        var s16 = MemoryMarshal.Cast<byte, ushort>(src);
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1:
                    dst[d] = dst[d + 1] = dst[d + 2] = s16[s] / 65535f;
                    dst[d + 3] = 1f;
                    break;
                case 2:
                    dst[d] = dst[d + 1] = dst[d + 2] = s16[s] / 65535f;
                    dst[d + 3] = s16[s + 1] / 65535f;
                    break;
                case 3:
                    dst[d] = s16[s] / 65535f;
                    dst[d + 1] = s16[s + 1] / 65535f;
                    dst[d + 2] = s16[s + 2] / 65535f;
                    dst[d + 3] = 1f;
                    break;
                default: // 4
                    dst[d] = s16[s] / 65535f;
                    dst[d + 1] = s16[s + 1] / 65535f;
                    dst[d + 2] = s16[s + 2] / 65535f;
                    dst[d + 3] = s16[s + 3] / 65535f;
                    break;
            }
        }
    }

    private static void ExpandF32(ReadOnlySpan<byte> src, int channels, int pixelCount, Span<float> dst)
    {
        // Verbatim: scene-/display-referred float values pass through unclamped
        // (HDR highlights > 1 and wide-gamut negatives survive); only the channel
        // layout is widened to RGBA.
        var sf = MemoryMarshal.Cast<byte, float>(src);
        for (int p = 0, s = 0, d = 0; p < pixelCount; p++, s += channels, d += 4)
        {
            switch (channels)
            {
                case 1:
                    dst[d] = dst[d + 1] = dst[d + 2] = sf[s];
                    dst[d + 3] = 1f;
                    break;
                case 2:
                    dst[d] = dst[d + 1] = dst[d + 2] = sf[s];
                    dst[d + 3] = sf[s + 1];
                    break;
                case 3:
                    dst[d] = sf[s];
                    dst[d + 1] = sf[s + 1];
                    dst[d + 2] = sf[s + 2];
                    dst[d + 3] = 1f;
                    break;
                default: // 4
                    dst[d] = sf[s];
                    dst[d + 1] = sf[s + 1];
                    dst[d + 2] = sf[s + 2];
                    dst[d + 3] = sf[s + 3];
                    break;
            }
        }
    }
}
