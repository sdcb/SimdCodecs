using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// Decodes still and animated WebP images. Spec-derived from the prose and tables of RFC 6386 /
/// RFC 9649; neither the reference decoder source in RFC 6386 section 20 nor libwebp/libvpx source
/// was consulted — their binaries are used only as test oracles.
/// <para>
/// Output is non-premultiplied RGBA8. Malformed input throws <see cref="InvalidDataException"/>,
/// input using features this decoder refuses throws <see cref="NotSupportedException"/>, and
/// canvases above 67,108,864 pixels are rejected as malformed before any raster is allocated.
/// </para>
/// </summary>
public static class WebpDecoder
{
    private static readonly Vector128<byte> ChromaPairShuffleIndices = Vector128.Create(
        (byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);

    /// <summary>
    /// Reads dimensions, alpha, format and frame count from the container headers without decoding
    /// pixels. Returns false instead of throwing for malformed or unsupported data.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="info">The header information, or default when false is returned.</param>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out WebpInfo info)
    {
        try
        {
            info = WebpContainer.Read(data).Info;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            info = default;
            return false;
        }
    }

    /// <summary>Reads dimensions, alpha, format and frame count from the container headers.</summary>
    /// <param name="data">The complete WebP file.</param>
    /// <exception cref="InvalidDataException">The container is malformed or above the pixel limit.</exception>
    /// <exception cref="NotSupportedException">The container uses a feature this decoder does not support.</exception>
    public static WebpInfo ReadInfo(ReadOnlySpan<byte> data) => WebpContainer.Read(data).Info;

    /// <summary>
    /// The default ceiling on the total pixels <see cref="DecodeAnimation(ReadOnlySpan{byte})"/> may
    /// materialize: canvas width × height × frame count, 2^28. That admits, say, 128 frames of 1080p.
    /// <para>
    /// The returned frames hold up to 1 GiB of RGBA at this limit, and the decoder's working canvas
    /// (up to 256 MiB) comes on top of them. Measured: an 8192×8192 × 4-frame file of 244 bytes, which
    /// is exactly at the limit, allocates 1.25 GiB. A caller with a tighter memory budget should pass
    /// a smaller limit to <see cref="DecodeAnimation(ReadOnlySpan{byte}, long)"/>, or stream with
    /// <see cref="WebpAnimationDecoder"/>.
    /// </para>
    /// </summary>
    public const long DefaultMaximumAnimationPixels = 1L << 28;

    /// <summary>
    /// Decodes every frame, returning the full composited canvas for each one. At most
    /// <see cref="DefaultMaximumAnimationPixels"/> pixels are materialized across all frames;
    /// use <see cref="WebpAnimationDecoder"/> to stream larger animations through one canvas.
    /// </summary>
    /// <param name="data">A still or animated WebP image.</param>
    /// <exception cref="InvalidDataException">
    /// The data is malformed, or canvas × frame count exceeds <see cref="DefaultMaximumAnimationPixels"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">The data uses a feature this decoder does not support.</exception>
    public static WebpAnimation DecodeAnimation(ReadOnlySpan<byte> data) =>
        DecodeAnimation(data, DefaultMaximumAnimationPixels);

    /// <summary>
    /// Decodes every frame, returning the full composited canvas for each one, refusing up front
    /// (before any frame is decoded or canvas allocated) when canvas × frame count exceeds
    /// <paramref name="maximumTotalPixels"/>.
    /// </summary>
    /// <param name="data">A still or animated WebP image.</param>
    /// <param name="maximumTotalPixels">The most pixels all returned frames may hold together.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumTotalPixels"/> is not positive.</exception>
    /// <exception cref="InvalidDataException">
    /// The data is malformed, or canvas × frame count exceeds <paramref name="maximumTotalPixels"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">The data uses a feature this decoder does not support.</exception>
    public static WebpAnimation DecodeAnimation(ReadOnlySpan<byte> data, long maximumTotalPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalPixels);
        WebpContainer container = WebpContainer.Read(data);

        // Every returned frame is a full-canvas copy, so memory is canvas x frames, and a one-pixel
        // ANMF costs ~50 input bytes: 3 KB can declare 64 frames of a 4096x4096 canvas, 4 GiB.
        // The per-canvas limit cannot see that, so the product is charged here, before anything is
        // allocated. Both factors are bounded (2^26 pixels; frames by input length), so no overflow.
        long canvasPixels = (long)container.Info.Width * container.Info.Height;
        long totalPixels = canvasPixels * Math.Max(container.Frames.Length, 1);
        if (totalPixels > maximumTotalPixels)
        {
            throw new InvalidDataException(
                $"WebP: the animation would materialize {totalPixels:N0} pixels " +
                $"({container.Frames.Length:N0} frames of {container.Info.Width}x{container.Info.Height}), " +
                $"above the limit of {maximumTotalPixels:N0}. Use WebpAnimationDecoder to stream it.");
        }

        // Each frame is copied straight from the decoder's working canvas into the array that is
        // returned, so the peak is the frames plus that one working canvas.
        var decoder = new WebpAnimationDecoder(data.ToArray());
        int canvasBytes = checked(decoder.CanvasWidth * decoder.CanvasHeight * 4);
        var frames = new List<WebpFrame>(decoder.FrameCount);
        for (int index = 0; index < decoder.FrameCount; index++)
        {
            byte[] rgba = new byte[canvasBytes];
            if (!decoder.TryDecodeNextFrame(rgba, out WebpFrameInfo frame))
            {
                throw new InvalidDataException("The WebP animation ended before its declared frame count.");
            }

            frames.Add(new WebpFrame(rgba, frame.TimestampMs, frame.DurationMs));
        }

        return new WebpAnimation(
            decoder.CanvasWidth,
            decoder.CanvasHeight,
            decoder.LoopCount,
            decoder.BackgroundColor,
            frames.AsReadOnly())
        {
            IccProfile = container.HasIccProfile ? container.IccProfile.ToArray() : null,
            Exif = container.HasExif ? container.Exif.ToArray() : null,
            Xmp = container.HasXmp ? container.Xmp.ToArray() : null,
        };
    }

    /// <summary>
    /// Decodes to a new tightly packed RGBA8 image with its ICC / EXIF / XMP payloads. Animated input
    /// yields the first frame composited onto the transparent canvas, at canvas size. That costs two
    /// canvases, the decoder's working canvas plus the result. Measured: a 94-byte animation with an
    /// 8192×8192 canvas allocates 512 MiB here, and through the facade.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    /// <exception cref="NotSupportedException">The data uses a feature this decoder does not support.</exception>
    public static WebpImage Decode(ReadOnlySpan<byte> data)
    {
        WebpContainer container = WebpContainer.Read(data);
        WebpInfo info = container.Info;
        byte[] rgba;
        if (info.Format == WebpFormat.Animated)
        {
            rgba = DecodeFirstAnimationFrame(data, info.Width, info.Height);
        }
        else if (info.Format == WebpFormat.Lossless)
        {
            rgba = Vp8LDecoder.DecodeRgba(container.Vp8Bitstream);
        }
        else
        {
            rgba = new byte[checked(info.Width * info.Height * 4)];
            byte[]? alpha = container.HasAlphaChunk
                ? DecodeAndUnfilterAlpha(container.AlphaData, info.Width, info.Height)
                : null;
            try
            {
                ReadOnlySpan<byte> alphaSpan = alpha is null
                    ? ReadOnlySpan<byte>.Empty
                    : alpha.AsSpan(0, checked(info.Width * info.Height));
                DecodeLossyRgba(container.Vp8Bitstream, info.Width, info.Height, rgba, checked(info.Width * 4),
                    fancy: true, alphaSpan, WebpAblation.None);
            }
            finally
            {
                if (alpha is not null)
                {
                    WebpBufferPool<byte>.Return(alpha);
                }
            }
        }

        byte[]? icc = container.HasIccProfile ? container.IccProfile.ToArray() : null;
        byte[]? exif = container.HasExif ? container.Exif.ToArray() : null;
        byte[]? xmp = container.HasXmp ? container.Xmp.ToArray() : null;
        return new WebpImage(info.Width, info.Height, info.HasAlpha, rgba, icc, exif, xmp);
    }

    /// <summary>
    /// Decodes into a caller-owned RGBA8 buffer. Animated input yields the first composited frame.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="rgba">
    /// The destination; at least (height - 1) × stride + width × 4 bytes. Bytes between the end of one
    /// row's pixels and the start of the next are not written.
    /// </param>
    /// <param name="stride">
    /// Bytes from the start of one row to the start of the next; 0 means tightly packed (width × 4).
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="stride"/> is smaller than width × 4, or <paramref name="rgba"/> is too small.
    /// </exception>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    /// <exception cref="NotSupportedException">The data uses a feature this decoder does not support.</exception>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride = 0)
    {
        DecodeTo(data, rgba, stride, fancy: true, WebpAblation.None);
    }

    /// <summary>
    /// <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/> with <paramref name="options"/>
    /// (null: the defaults).
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="rgba">The destination, as for <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/>.</param>
    /// <param name="stride">Row pitch in bytes; 0 means tightly packed.</param>
    /// <param name="options">Decoding options, such as opt-in multithreading.</param>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride, WebpDecodeOptions? options)
    {
        DecodeTo(data, rgba, stride, fancy: true, WebpAblation.None, options?.Multithreaded ?? false);
    }

    internal static void DecodeTo(
        ReadOnlySpan<byte> data,
        Span<byte> rgba,
        int stride,
        bool fancy,
        WebpAblation ablation,
        bool multithreaded = false)
    {
        WebpContainer container = WebpContainer.Read(data);
        WebpInfo info = container.Info;
        int minimumStride = checked(info.Width * 4);
        if (stride == 0)
        {
            stride = minimumStride;
        }

        if (stride < minimumStride)
        {
            throw new ArgumentException("The stride is smaller than one RGBA row.", nameof(stride));
        }

        int requiredLength = checked((info.Height - 1) * stride + minimumStride);
        if (rgba.Length < requiredLength)
        {
            throw new ArgumentException("The destination is too small for the decoded image.", nameof(rgba));
        }

        if (info.Format == WebpFormat.Animated)
        {
            if (stride == minimumStride)
            {
                // Tightly packed: the frame goes straight from the working canvas into the caller's
                // buffer, with no intermediate copy.
                DecodeFirstAnimationFrame(data, rgba[..requiredLength], fancy);
                return;
            }

            byte[] firstFrame = DecodeFirstAnimationFrame(data, info.Width, info.Height, fancy);
            for (int row = 0; row < info.Height; row++)
            {
                firstFrame.AsSpan(row * minimumStride, minimumStride)
                    .CopyTo(rgba.Slice(row * stride, minimumStride));
            }

            return;
        }

        if (info.Format == WebpFormat.Lossless)
        {
            Vp8LDecoder.DecodeRgbaTo(container.Vp8Bitstream, rgba, stride, ablation);
        }
        else if (multithreaded && container.HasAlphaChunk && (ablation & WebpAblation.SkipAlpha) == 0)
        {
            DecodeLossyWithConcurrentAlpha(container.Vp8Bitstream, container.AlphaData, info.Width, info.Height,
                rgba, stride, fancy, ablation);
        }
        else
        {
            byte[]? alpha = container.HasAlphaChunk && (ablation & WebpAblation.SkipAlpha) == 0
                ? DecodeAndUnfilterAlpha(container.AlphaData, info.Width, info.Height)
                : null;
            try
            {
                ReadOnlySpan<byte> alphaSpan = alpha is null
                    ? ReadOnlySpan<byte>.Empty
                    : alpha.AsSpan(0, checked(info.Width * info.Height));
                DecodeLossyRgba(container.Vp8Bitstream, info.Width, info.Height, rgba, stride, fancy, alphaSpan, ablation);
            }
            finally
            {
                if (alpha is not null)
                {
                    WebpBufferPool<byte>.Return(alpha);
                }
            }
        }
    }

    /// <summary>
    /// <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/> that returns false instead of throwing for malformed or unsupported
    /// data, a stride below width × 4, or a destination that is too small. The destination may be
    /// partly written when false is returned.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="rgba">The destination, as for <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/>.</param>
    /// <param name="stride">Row pitch in bytes; 0 means tightly packed.</param>
    public static bool TryDecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride = 0) =>
        TryDecodeTo(data, rgba, stride, null);

    /// <summary>
    /// <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, int, WebpDecodeOptions?)"/> that returns false
    /// instead of throwing, as <see cref="TryDecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/> does.
    /// </summary>
    /// <param name="data">The complete WebP file.</param>
    /// <param name="rgba">The destination, as for <see cref="DecodeTo(ReadOnlySpan{byte}, Span{byte}, int)"/>.</param>
    /// <param name="stride">Row pitch in bytes; 0 means tightly packed.</param>
    /// <param name="options">Decoding options, such as opt-in multithreading.</param>
    public static bool TryDecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride, WebpDecodeOptions? options)
    {
        try
        {
            DecodeTo(data, rgba, stride, options);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static Vp8Yuv DecodeYuv(ReadOnlySpan<byte> data, bool applyLoopFilter = true)
    {
        WebpContainer container = WebpContainer.Read(data);
        if (container.Info.Format != WebpFormat.Lossy)
        {
            throw new NotSupportedException("VP8 YUV output is unavailable for lossless WebP.");
        }

        return Vp8Decoder.Decode(container.Vp8Bitstream, container.Info.Width, container.Info.Height, applyLoopFilter);
    }

    internal static void DecodeToNoFancy(ReadOnlySpan<byte> data, Span<byte> rgba)
    {
        DecodeTo(data, rgba, 0, fancy: false, WebpAblation.None);
    }

    internal static byte[] DecodeFrameRgba(
        ReadOnlySpan<byte> bitstream,
        WebpFormat format,
        ReadOnlySpan<byte> alphaData,
        int width,
        int height,
        bool fancy = true)
    {
        if (format == WebpFormat.Lossless)
        {
            if (!alphaData.IsEmpty)
            {
                throw new InvalidDataException("A VP8L animation frame cannot have a separate ALPH chunk.");
            }

            return Vp8LDecoder.DecodeRgba(bitstream);
        }

        byte[] rgba = new byte[checked(width * height * 4)];
        byte[]? alpha = alphaData.IsEmpty
            ? null
            : DecodeAndUnfilterAlpha(alphaData, width, height);
        try
        {
            ReadOnlySpan<byte> alphaSpan = alpha is null
                ? ReadOnlySpan<byte>.Empty
                : alpha.AsSpan(0, checked(width * height));
            DecodeLossyRgba(bitstream, width, height, rgba, checked(width * 4), fancy, alphaSpan, WebpAblation.None);
        }
        finally
        {
            if (alpha is not null)
            {
                WebpBufferPool<byte>.Return(alpha);
            }
        }

        return rgba;
    }

    private static byte[] DecodeFirstAnimationFrame(
        ReadOnlySpan<byte> data,
        int width,
        int height,
        bool fancy = true)
    {
        byte[] rgba = new byte[checked(width * height * 4)];
        DecodeFirstAnimationFrame(data, rgba, fancy);
        return rgba;
    }

    private static void DecodeFirstAnimationFrame(ReadOnlySpan<byte> data, Span<byte> canvasRgba, bool fancy)
    {
        var decoder = new WebpAnimationDecoder(data.ToArray(), fancy);
        if (!decoder.TryDecodeNextFrame(canvasRgba, out _))
        {
            throw new InvalidDataException("The WebP animation has no frames.");
        }
    }

    /// <summary>
    /// Opt-in multithreaded lossy + alpha decode: the ALPH plane is decoded on a thread-pool thread
    /// while this one decodes the VP8 frame. Rows emitted before the plane is ready are written
    /// opaque and get their alpha in a pass afterwards; later rows take it directly.
    /// </summary>
    private static void DecodeLossyWithConcurrentAlpha(
        ReadOnlySpan<byte> bitstream,
        ReadOnlySpan<byte> alphaData,
        int width,
        int height,
        Span<byte> output,
        int stride,
        bool fancy,
        WebpAblation ablation)
    {
        // The task cannot hold a span of the caller's data: it gets a pooled copy of the chunk.
        int alphaLength = alphaData.Length;
        byte[] alphaCopy = WebpBufferPool<byte>.Rent(Math.Max(alphaLength, 1));
        alphaData.CopyTo(alphaCopy);
        var pending = new PendingAlpha();
        Task<byte[]> alphaTask;
        try
        {
            alphaTask = Task.Run(() =>
            {
                byte[] decoded = DecodeAndUnfilterAlpha(alphaCopy.AsSpan(0, alphaLength), width, height);
                pending.Publish(decoded);
                return decoded;
            });
        }
        catch
        {
            WebpBufferPool<byte>.Return(alphaCopy);
            throw;
        }

        bool frameDecoded = false;
        try
        {
            DecodeLossyRgba(bitstream, width, height, output, stride, fancy, ReadOnlySpan<byte>.Empty, ablation, pending);
            frameDecoded = true;
        }
        finally
        {
            // Join whatever happened (the task reads alphaCopy); its own failure is rethrown below.
            try
            {
                alphaTask.Wait();
            }
            catch (AggregateException)
            {
            }

            WebpBufferPool<byte>.Return(alphaCopy);
            if (!frameDecoded && alphaTask.IsCompletedSuccessfully)
            {
                WebpBufferPool<byte>.Return(alphaTask.Result);
            }
        }

        byte[] plane = alphaTask.GetAwaiter().GetResult();
        try
        {
            StoreAlphaRows(output, stride, width, plane, pending.RowsWithoutAlpha);
        }
        finally
        {
            WebpBufferPool<byte>.Return(plane);
        }
    }

    /// <summary>An ALPH plane decoded on another thread, handed over once complete.</summary>
    private sealed class PendingAlpha
    {
        private byte[]? _plane;

        /// <summary>Rows emitted (opaque) before the plane was there; written and read by the frame's thread only.</summary>
        public int RowsWithoutAlpha;

        public byte[]? TryGetPlane() => Volatile.Read(ref _plane);

        public void Publish(byte[] plane) => Volatile.Write(ref _plane, plane);
    }

    // Writes the alpha byte of every pixel of rows [0, rows) from the plane.
    internal static void StoreAlphaRows(Span<byte> output, int stride, int width, ReadOnlySpan<byte> alpha, int rows)
    {
        for (int row = 0; row < rows; row++)
        {
            Span<byte> destination = output.Slice(row * stride, width * 4);
            ReadOnlySpan<byte> source = alpha.Slice(row * width, width);
            int x = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                Vector128<uint> rgbMask = Vector128.Create(0x00ffffffu);
                for (; x + 4 <= width; x += 4)
                {
                    Vector128<uint> pixels = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)(x * 4)).AsUInt32();
                    Vector128<uint> values = Vector128.WidenLower(Vector128.WidenLower(
                        Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.AsRef(in source[x]))).AsByte()));
                    ((pixels & rgbMask) | (values << 24)).AsByte()
                        .StoreUnsafe(ref MemoryMarshal.GetReference(destination), (nuint)(x * 4));
                }
            }

            for (; x < width; x++)
            {
                destination[x * 4 + 3] = source[x];
            }
        }
    }

    /// <summary>Decodes a VP8 frame straight into RGBA rows (alpha, if any, already decoded).</summary>
    private static void DecodeLossyRgba(
        ReadOnlySpan<byte> bitstream,
        int width,
        int height,
        Span<byte> output,
        int stride,
        bool fancy,
        ReadOnlySpan<byte> alpha,
        WebpAblation ablation,
        PendingAlpha? pendingAlpha = null)
    {
        int chromaWidth = (width + 1) >> 1;
        ushort[] tRows = WebpBufferPool<ushort>.Rent(checked(chromaWidth * 2));
        byte[] chromaRows = WebpBufferPool<byte>.Rent(checked(width * 2));
        try
        {
            var sink = new RgbaSink(output, stride, width, height, fancy, alpha, tRows, chromaRows, pendingAlpha);
            Vp8Decoder.DecodeFrame(bitstream, width, height, ablation, ref sink);
        }
        finally
        {
            WebpBufferPool<ushort>.Return(tRows);
            WebpBufferPool<byte>.Return(chromaRows);
        }
    }

    /// <summary>Upsamples chroma (fancy or nearest) and converts finished rows to RGBA.</summary>
    private readonly ref struct RgbaSink(
        Span<byte> output,
        int stride,
        int width,
        int height,
        bool fancy,
        ReadOnlySpan<byte> alpha,
        ushort[] tRows,
        byte[] chromaRows,
        PendingAlpha? pendingAlpha = null) : IVp8RowSink
    {
        private readonly Span<byte> _output = output;
        private readonly ReadOnlySpan<byte> _alpha = alpha;

        public void Emit(ref byte y, int yStride, int yRow0, ref byte u, ref byte v, int uvStride, int uvRow0,
            int first, int end)
        {
            int chromaWidth = (width + 1) >> 1;
            int chromaHeight = (height + 1) >> 1;
            ref byte outputBase = ref MemoryMarshal.GetReference(_output);
            ref byte alphaBase = ref MemoryMarshal.GetReference(_alpha);
            bool hasAlpha = !_alpha.IsEmpty;
            if (pendingAlpha is not null)
            {
                // The alpha plane is being decoded on another thread: once it is there, rows take
                // it directly; rows emitted before get it in a pass after the frame.
                if (pendingAlpha.TryGetPlane() is { } plane)
                {
                    alphaBase = ref MemoryMarshal.GetArrayDataReference(plane);
                    hasAlpha = true;
                }
                else
                {
                    pendingAlpha.RowsWithoutAlpha = end;
                }
            }
            ref byte uScratchBase = ref MemoryMarshal.GetArrayDataReference(chromaRows);
            ref byte vScratchBase = ref Unsafe.Add(ref uScratchBase, width);

            for (int row = first; row < end; row++)
            {
                int chromaRow = row >> 1;
                ref byte uRow = ref Unsafe.Add(ref u, (chromaRow - uvRow0) * uvStride);
                ref byte vRow = ref Unsafe.Add(ref v, (chromaRow - uvRow0) * uvStride);
                ref byte yRow = ref Unsafe.Add(ref y, (row - yRow0) * yStride);
                ref byte destinationRow = ref Unsafe.Add(ref outputBase, row * stride);
                ref byte alphaRow = ref Unsafe.NullRef<byte>();
                if (hasAlpha)
                {
                    alphaRow = ref Unsafe.Add(ref alphaBase, row * width);
                }

                if (fancy)
                {
                    int adjacentRow = row % 2 == 0
                        ? (chromaRow == 0 ? 0 : chromaRow - 1)
                        : (chromaRow == chromaHeight - 1 ? chromaRow : chromaRow + 1);
                    ref byte adjacentU = ref Unsafe.Add(ref u, (adjacentRow - uvRow0) * uvStride);
                    ref byte adjacentV = ref Unsafe.Add(ref v, (adjacentRow - uvRow0) * uvStride);
                    Span<ushort> tU = tRows.AsSpan(0, chromaWidth);
                    Span<ushort> tV = tRows.AsSpan(chromaWidth, chromaWidth);
                    Span<byte> uUpsampled = chromaRows.AsSpan(0, width);
                    Span<byte> vUpsampled = chromaRows.AsSpan(width, width);
                    ReadOnlySpan<byte> currentU = MemoryMarshal.CreateReadOnlySpan(ref uRow, chromaWidth);
                    ReadOnlySpan<byte> currentV = MemoryMarshal.CreateReadOnlySpan(ref vRow, chromaWidth);
                    ReadOnlySpan<byte> adjacentUSpan = MemoryMarshal.CreateReadOnlySpan(ref adjacentU, chromaWidth);
                    ReadOnlySpan<byte> adjacentVSpan = MemoryMarshal.CreateReadOnlySpan(ref adjacentV, chromaWidth);
                    if (Vector128.IsHardwareAccelerated)
                    {
                        UpsampleFancyVector128(currentU, adjacentUSpan, tU, uUpsampled);
                        UpsampleFancyVector128(currentV, adjacentVSpan, tV, vUpsampled);
                    }
                    else
                    {
                        UpsampleFancyScalar(currentU, adjacentUSpan, tU, uUpsampled);
                        UpsampleFancyScalar(currentV, adjacentVSpan, tV, vUpsampled);
                    }
                }
                else
                {
                    for (int chromaColumn = 0; chromaColumn < chromaWidth; chromaColumn++)
                    {
                        byte uValue = Unsafe.Add(ref uRow, chromaColumn);
                        byte vValue = Unsafe.Add(ref vRow, chromaColumn);
                        int column = chromaColumn * 2;
                        Unsafe.Add(ref uScratchBase, column) = uValue;
                        Unsafe.Add(ref vScratchBase, column) = vValue;
                        int nextColumn = column + 1;
                        if (nextColumn < width)
                        {
                            Unsafe.Add(ref uScratchBase, nextColumn) = uValue;
                            Unsafe.Add(ref vScratchBase, nextColumn) = vValue;
                        }
                    }
                }

                if (Vector256.IsHardwareAccelerated)
                {
                    ConvertRowVector256Core(ref yRow, ref uScratchBase, ref vScratchBase,
                        ref alphaRow, hasAlpha, ref destinationRow, width);
                }
                else if (Vector128.IsHardwareAccelerated)
                {
                    ConvertRowVector128Core(ref yRow, ref uScratchBase, ref vScratchBase,
                        ref alphaRow, hasAlpha, ref destinationRow, width);
                }
                else
                {
                    ConvertRowScalarCore(ref yRow, ref uScratchBase, ref vScratchBase,
                        ref alphaRow, hasAlpha, ref destinationRow, width);
                }
            }
        }
    }

    internal static void UpsampleFancyScalar(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> adjacent,
        Span<ushort> tRow,
        Span<byte> output)
    {
        ref byte currentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(current));
        ref byte adjacentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(adjacent));
        ref ushort tBase = ref MemoryMarshal.GetReference(tRow);
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        UpsampleFancyScalarCore(ref currentBase, ref adjacentBase, ref tBase,
            ref outputBase, current.Length, output.Length);
    }

    internal static void UpsampleFancyVector128(
        ReadOnlySpan<byte> current,
        ReadOnlySpan<byte> adjacent,
        Span<ushort> tRow,
        Span<byte> output)
    {
        ref byte currentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(current));
        ref byte adjacentBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(adjacent));
        ref ushort tBase = ref MemoryMarshal.GetReference(tRow);
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        if (Vector128.IsHardwareAccelerated)
        {
            UpsampleFancyVector128Core(ref currentBase, ref adjacentBase, ref tBase,
                ref outputBase, current.Length, output.Length);
        }
        else
        {
            UpsampleFancyScalarCore(ref currentBase, ref adjacentBase, ref tBase,
                ref outputBase, current.Length, output.Length);
        }
    }

    private static void UpsampleFancyScalarCore(
        ref byte current,
        ref byte adjacent,
        ref ushort tRow,
        ref byte output,
        int chromaWidth,
        int width)
    {
        for (int column = 0; column < chromaWidth; column++)
        {
            Unsafe.Add(ref tRow, column) = (ushort)(
                3 * Unsafe.Add(ref current, column) + Unsafe.Add(ref adjacent, column));
        }

        ushort first = tRow;
        output = (byte)((4 * first + 8) >> 4);
        if (width > 1)
        {
            ushort next = chromaWidth > 1 ? Unsafe.Add(ref tRow, 1) : first;
            Unsafe.Add(ref output, 1) = (byte)((3 * first + next + 8) >> 4);
        }

        for (int column = 1; column < chromaWidth - 1; column++)
        {
            int currentT = Unsafe.Add(ref tRow, column);
            int outputColumn = column * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, column - 1) + 8) >> 4);
            Unsafe.Add(ref output, outputColumn + 1) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, column + 1) + 8) >> 4);
        }

        if (chromaWidth > 1)
        {
            int lastColumn = chromaWidth - 1;
            int last = Unsafe.Add(ref tRow, lastColumn);
            int outputColumn = lastColumn * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * last + Unsafe.Add(ref tRow, lastColumn - 1) + 8) >> 4);
            if (outputColumn + 1 < width)
            {
                Unsafe.Add(ref output, outputColumn + 1) = (byte)((4 * last + 8) >> 4);
            }
        }
    }

    private static void UpsampleFancyVector128Core(
        ref byte current,
        ref byte adjacent,
        ref ushort tRow,
        ref byte output,
        int chromaWidth,
        int width)
    {
        Vector128<ushort> three = Vector128.Create((ushort)3);
        int column = 0;
        if (Avx2.IsSupported)
        {
            for (; column <= chromaWidth - 32; column += 32)
            {
                Vector256<byte> currentBytes = Vector256.LoadUnsafe(ref current, (nuint)column);
                Vector256<byte> adjacentBytes = Vector256.LoadUnsafe(ref adjacent, (nuint)column);
                Vector256<ushort> tLow = Vector256.WidenLower(currentBytes) * 3 + Vector256.WidenLower(adjacentBytes);
                Vector256<ushort> tHigh = Vector256.WidenUpper(currentBytes) * 3 + Vector256.WidenUpper(adjacentBytes);
                tLow.StoreUnsafe(ref tRow, (nuint)column);
                tHigh.StoreUnsafe(ref tRow, (nuint)(column + 16));
            }
        }

        for (; column <= chromaWidth - 16; column += 16)
        {
            Vector128<byte> currentBytes = Vector128.LoadUnsafe(ref current, (nuint)column);
            Vector128<byte> adjacentBytes = Vector128.LoadUnsafe(ref adjacent, (nuint)column);
            Vector128<ushort> currentLow = Vector128.WidenLower(currentBytes);
            Vector128<ushort> currentHigh = Vector128.WidenUpper(currentBytes);
            Vector128<ushort> adjacentLow = Vector128.WidenLower(adjacentBytes);
            Vector128<ushort> adjacentHigh = Vector128.WidenUpper(adjacentBytes);
            Vector128<ushort> tLow = Vector128.Add(Vector128.Multiply(currentLow, three), adjacentLow);
            Vector128<ushort> tHigh = Vector128.Add(Vector128.Multiply(currentHigh, three), adjacentHigh);
            ref ushort tChunk = ref Unsafe.Add(ref tRow, column);
            Vector128.StoreUnsafe(tLow, ref tChunk);
            Vector128.StoreUnsafe(tHigh, ref Unsafe.Add(ref tChunk, 8));
        }

        for (; column < chromaWidth; column++)
        {
            Unsafe.Add(ref tRow, column) = (ushort)(
                3 * Unsafe.Add(ref current, column) + Unsafe.Add(ref adjacent, column));
        }

        ushort first = tRow;
        output = (byte)((4 * first + 8) >> 4);
        if (width > 1)
        {
            ushort next = chromaWidth > 1 ? Unsafe.Add(ref tRow, 1) : first;
            Unsafe.Add(ref output, 1) = (byte)((3 * first + next + 8) >> 4);
        }

        int pair = 1;
        if (Avx2.IsSupported)
        {
            // 16 pairs per step; the pack and the byte shuffle both work within 128-bit lanes, so
            // lane 0 holds pairs 0-7 and lane 1 pairs 8-15, already in output order.
            Vector256<byte> pairShuffle = Vector256.Create(ChromaPairShuffleIndices, ChromaPairShuffleIndices);
            Vector256<ushort> rounding = Vector256.Create((ushort)8);
            for (; pair <= chromaWidth - 17; pair += 16)
            {
                Vector256<ushort> currentT = Vector256.LoadUnsafe(ref tRow, (nuint)pair);
                Vector256<ushort> left = Vector256.LoadUnsafe(ref tRow, (nuint)(pair - 1));
                Vector256<ushort> right = Vector256.LoadUnsafe(ref tRow, (nuint)(pair + 1));
                Vector256<ushort> scaled = currentT * 3 + rounding;
                Vector256<ushort> even = Vector256.ShiftRightLogical(scaled + left, 4);
                Vector256<ushort> odd = Vector256.ShiftRightLogical(scaled + right, 4);
                Vector256<byte> separated = Avx2.PackUnsignedSaturate(even.AsInt16(), odd.AsInt16());
                Avx2.Shuffle(separated, pairShuffle).StoreUnsafe(ref output, (nuint)(pair * 2));
            }
        }

        for (; pair <= chromaWidth - 9; pair += 8)
        {
            Vector128<ushort> currentT = Vector128.LoadUnsafe(ref tRow, (nuint)pair);
            Vector128<ushort> left = Vector128.LoadUnsafe(ref tRow, (nuint)(pair - 1));
            Vector128<ushort> right = Vector128.LoadUnsafe(ref tRow, (nuint)(pair + 1));
            Vector128<ushort> scaled = Vector128.Multiply(currentT, three);
            Vector128<ushort> even = Vector128.ShiftRightLogical(
                Vector128.Add(Vector128.Add(scaled, left), Vector128.Create((ushort)8)), 4);
            Vector128<ushort> odd = Vector128.ShiftRightLogical(
                Vector128.Add(Vector128.Add(scaled, right), Vector128.Create((ushort)8)), 4);
            Vector128<byte> separated = Vector128.Narrow(even, odd);
            Vector128<byte> interleaved = Vector128.Shuffle(separated, ChromaPairShuffleIndices);
            ref byte outputChunk = ref Unsafe.Add(ref output, pair * 2);
            Vector128.StoreUnsafe(interleaved, ref outputChunk);
        }

        for (; pair < chromaWidth - 1; pair++)
        {
            int currentT = Unsafe.Add(ref tRow, pair);
            int outputColumn = pair * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, pair - 1) + 8) >> 4);
            Unsafe.Add(ref output, outputColumn + 1) = (byte)(
                (3 * currentT + Unsafe.Add(ref tRow, pair + 1) + 8) >> 4);
        }

        if (chromaWidth > 1)
        {
            int lastColumn = chromaWidth - 1;
            int last = Unsafe.Add(ref tRow, lastColumn);
            int outputColumn = lastColumn * 2;
            Unsafe.Add(ref output, outputColumn) = (byte)(
                (3 * last + Unsafe.Add(ref tRow, lastColumn - 1) + 8) >> 4);
            if (outputColumn + 1 < width)
            {
                Unsafe.Add(ref output, outputColumn + 1) = (byte)((4 * last + 8) >> 4);
            }
        }
    }

    internal static void ConvertRowScalar(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha,
        Span<byte> output)
    {
        int width = y.Length;
        ref byte yBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(y));
        ref byte uBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(u));
        ref byte vBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(v));
        ref byte alphaBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(alpha));
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        ConvertRowScalarCore(ref yBase, ref uBase, ref vBase, ref alphaBase,
            !alpha.IsEmpty, ref outputBase, width);
    }

    internal static void ConvertRowVector128(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha,
        Span<byte> output)
    {
        int width = y.Length;
        ref byte yBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(y));
        ref byte uBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(u));
        ref byte vBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(v));
        ref byte alphaBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(alpha));
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        ConvertRowVector128Core(ref yBase, ref uBase, ref vBase, ref alphaBase,
            !alpha.IsEmpty, ref outputBase, width);
    }

    internal static void ConvertRowVector256(
        ReadOnlySpan<byte> y,
        ReadOnlySpan<byte> u,
        ReadOnlySpan<byte> v,
        ReadOnlySpan<byte> alpha,
        Span<byte> output)
    {
        int width = y.Length;
        ref byte yBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(y));
        ref byte uBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(u));
        ref byte vBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(v));
        ref byte alphaBase = ref Unsafe.AsRef(in MemoryMarshal.GetReference(alpha));
        ref byte outputBase = ref MemoryMarshal.GetReference(output);
        ConvertRowVector256Core(ref yBase, ref uBase, ref vBase, ref alphaBase,
            !alpha.IsEmpty, ref outputBase, width);
    }

    private static void ConvertRowVector256Core(
        ref byte y,
        ref byte u,
        ref byte v,
        ref byte alpha,
        bool hasAlpha,
        ref byte output,
        int width)
    {
        int column = 0;
        if (Avx2.IsSupported)
        {
            column = ConvertRowAvx2(ref y, ref u, ref v, ref alpha, hasAlpha, ref output, width);
        }
        else
        {
            for (; column <= width - 32; column += 32)
            {
                Vector256<byte> yBytes = Vector256.LoadUnsafe(ref y, (nuint)column);
                Vector256<byte> uBytes = Vector256.LoadUnsafe(ref u, (nuint)column);
                Vector256<byte> vBytes = Vector256.LoadUnsafe(ref v, (nuint)column);
                Vector256<byte> alphaBytes = hasAlpha
                    ? Vector256.LoadUnsafe(ref alpha, (nuint)column)
                    : Vector256.Create(byte.MaxValue);
                ConvertVector32(yBytes, uBytes, vBytes, alphaBytes, ref output, column * 4);
            }
        }

        for (; column <= width - 16; column += 16)
        {
            Vector128<byte> yBytes = Vector128.LoadUnsafe(ref y, (nuint)column);
            Vector128<byte> uBytes = Vector128.LoadUnsafe(ref u, (nuint)column);
            Vector128<byte> vBytes = Vector128.LoadUnsafe(ref v, (nuint)column);
            Vector128<byte> alphaBytes = hasAlpha
                ? Vector128.LoadUnsafe(ref alpha, (nuint)column)
                : Vector128.Create(byte.MaxValue);
            ConvertVector16(yBytes, uBytes, vBytes, alphaBytes, ref output, column * 4);
        }

        for (; column < width; column++)
        {
            WritePixel(ref Unsafe.Add(ref output, column * 4),
                Unsafe.Add(ref y, column), Unsafe.Add(ref u, column), Unsafe.Add(ref v, column),
                hasAlpha ? Unsafe.Add(ref alpha, column) : (byte)255);
        }
    }

    /// <summary>
    /// 32 pixels per step. Every product term of <see cref="WritePixel"/> is (s × k) >> 8 for a
    /// byte s; with s in the high byte of a 16-bit lane (an interleave with zero puts it there for
    /// free) that is exactly the high half of the unsigned 16×16 product, one vpmulhuw. The sums
    /// stay below 2^16; negative results are clamped by max-then-subtract, >> 6, and the 255
    /// clamp is the unsigned saturation of the final pack. Returns the first unconverted column.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int ConvertRowAvx2(ref byte y, ref byte u, ref byte v, ref byte alpha, bool hasAlpha,
        ref byte output, int width)
    {
        Vector256<byte> zero = Vector256<byte>.Zero;
        Vector256<ushort> kY = Vector256.Create((ushort)19077);
        Vector256<ushort> kVr = Vector256.Create((ushort)26149);
        Vector256<ushort> kUg = Vector256.Create((ushort)6419);
        Vector256<ushort> kVg = Vector256.Create((ushort)13320);
        Vector256<ushort> kUb = Vector256.Create((ushort)33050);
        Vector256<ushort> rBias = Vector256.Create((ushort)14234);
        Vector256<ushort> gBias = Vector256.Create((ushort)8708);
        Vector256<ushort> bBias = Vector256.Create((ushort)17685);
        Vector256<byte> opaque = Vector256.Create(byte.MaxValue);
        int column = 0;
        for (; column <= width - 32; column += 32)
        {
            Vector256<byte> yBytes = Vector256.LoadUnsafe(ref y, (nuint)column);
            Vector256<byte> uBytes = Vector256.LoadUnsafe(ref u, (nuint)column);
            Vector256<byte> vBytes = Vector256.LoadUnsafe(ref v, (nuint)column);
            Vector256<byte> aBytes = hasAlpha ? Vector256.LoadUnsafe(ref alpha, (nuint)column) : opaque;

            // Per 128-bit lane: Low = pixels 0-7 / 16-23, High = 8-15 / 24-31.
            Vector256<byte> r = ConvertChannelsAvx2(
                Avx2.UnpackLow(zero, yBytes).AsUInt16(), Avx2.UnpackLow(zero, uBytes).AsUInt16(),
                Avx2.UnpackLow(zero, vBytes).AsUInt16(),
                Avx2.UnpackHigh(zero, yBytes).AsUInt16(), Avx2.UnpackHigh(zero, uBytes).AsUInt16(),
                Avx2.UnpackHigh(zero, vBytes).AsUInt16(),
                kY, kVr, kUg, kVg, kUb, rBias, gBias, bBias, out Vector256<byte> g, out Vector256<byte> b);

            Vector256<byte> rgLow = Avx2.UnpackLow(r, g);
            Vector256<byte> rgHigh = Avx2.UnpackHigh(r, g);
            Vector256<byte> baLow = Avx2.UnpackLow(b, aBytes);
            Vector256<byte> baHigh = Avx2.UnpackHigh(b, aBytes);
            Vector256<byte> p0 = Avx2.UnpackLow(rgLow.AsUInt16(), baLow.AsUInt16()).AsByte();   // 0-3 | 16-19
            Vector256<byte> p1 = Avx2.UnpackHigh(rgLow.AsUInt16(), baLow.AsUInt16()).AsByte();  // 4-7 | 20-23
            Vector256<byte> p2 = Avx2.UnpackLow(rgHigh.AsUInt16(), baHigh.AsUInt16()).AsByte(); // 8-11 | 24-27
            Vector256<byte> p3 = Avx2.UnpackHigh(rgHigh.AsUInt16(), baHigh.AsUInt16()).AsByte(); // 12-15 | 28-31
            ref byte destination = ref Unsafe.Add(ref output, column * 4);
            Avx2.Permute2x128(p0, p1, 0x20).StoreUnsafe(ref destination);
            Avx2.Permute2x128(p2, p3, 0x20).StoreUnsafe(ref destination, 32);
            Avx2.Permute2x128(p0, p1, 0x31).StoreUnsafe(ref destination, 64);
            Avx2.Permute2x128(p2, p3, 0x31).StoreUnsafe(ref destination, 96);
        }

        return column;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> ConvertChannelsAvx2(
        Vector256<ushort> yLow, Vector256<ushort> uLow, Vector256<ushort> vLow,
        Vector256<ushort> yHigh, Vector256<ushort> uHigh, Vector256<ushort> vHigh,
        Vector256<ushort> kY, Vector256<ushort> kVr, Vector256<ushort> kUg, Vector256<ushort> kVg, Vector256<ushort> kUb,
        Vector256<ushort> rBias, Vector256<ushort> gBias, Vector256<ushort> bBias,
        out Vector256<byte> green, out Vector256<byte> blue)
    {
        Vector256<ushort> yTermLow = Avx2.MultiplyHigh(yLow, kY);
        Vector256<ushort> yTermHigh = Avx2.MultiplyHigh(yHigh, kY);
        Vector256<ushort> redLow = Avx2.MultiplyHigh(vLow, kVr) + yTermLow;
        Vector256<ushort> redHigh = Avx2.MultiplyHigh(vHigh, kVr) + yTermHigh;
        redLow = Avx2.ShiftRightLogical(Avx2.Max(redLow, rBias) - rBias, 6);
        redHigh = Avx2.ShiftRightLogical(Avx2.Max(redHigh, rBias) - rBias, 6);
        Vector256<ushort> greenNegLow = Avx2.MultiplyHigh(uLow, kUg) + Avx2.MultiplyHigh(vLow, kVg);
        Vector256<ushort> greenNegHigh = Avx2.MultiplyHigh(uHigh, kUg) + Avx2.MultiplyHigh(vHigh, kVg);
        Vector256<ushort> greenLow = Avx2.ShiftRightLogical(Avx2.Max(yTermLow + gBias, greenNegLow) - greenNegLow, 6);
        Vector256<ushort> greenHigh = Avx2.ShiftRightLogical(Avx2.Max(yTermHigh + gBias, greenNegHigh) - greenNegHigh, 6);
        Vector256<ushort> blueLow = Avx2.MultiplyHigh(uLow, kUb) + yTermLow;
        Vector256<ushort> blueHigh = Avx2.MultiplyHigh(uHigh, kUb) + yTermHigh;
        blueLow = Avx2.ShiftRightLogical(Avx2.Max(blueLow, bBias) - bBias, 6);
        blueHigh = Avx2.ShiftRightLogical(Avx2.Max(blueHigh, bBias) - bBias, 6);
        green = Avx2.PackUnsignedSaturate(greenLow.AsInt16(), greenHigh.AsInt16());
        blue = Avx2.PackUnsignedSaturate(blueLow.AsInt16(), blueHigh.AsInt16());
        return Avx2.PackUnsignedSaturate(redLow.AsInt16(), redHigh.AsInt16());
    }

    private static void ConvertVector32(
        Vector256<byte> yBytes,
        Vector256<byte> uBytes,
        Vector256<byte> vBytes,
        Vector256<byte> alphaBytes,
        ref byte output,
        int outputOffset)
    {
        Vector256<ushort> yLow = Vector256.WidenLower(yBytes);
        Vector256<ushort> yHigh = Vector256.WidenUpper(yBytes);
        Vector256<ushort> uLow = Vector256.WidenLower(uBytes);
        Vector256<ushort> uHigh = Vector256.WidenUpper(uBytes);
        Vector256<ushort> vLow = Vector256.WidenLower(vBytes);
        Vector256<ushort> vHigh = Vector256.WidenUpper(vBytes);
        Vector256<ushort> alphaLow = Vector256.WidenLower(alphaBytes);
        Vector256<ushort> alphaHigh = Vector256.WidenUpper(alphaBytes);

        ConvertVector16Avx2(yLow, uLow, vLow, alphaLow, ref output, outputOffset);
        ConvertVector16Avx2(yHigh, uHigh, vHigh, alphaHigh, ref output, outputOffset + 64);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> MultiplyConstant(
        Vector256<ushort> value,
        ushort high,
        ushort low) =>
        Vector256.Add(
            Vector256.Multiply(value, Vector256.Create(high)),
            Vector256.ShiftRightLogical(
                Vector256.Multiply(value, Vector256.Create(low)), 8));

    private static void ConvertVector16Avx2(
        Vector256<ushort> y,
        Vector256<ushort> u,
        Vector256<ushort> v,
        Vector256<ushort> alpha,
        ref byte output,
        int outputOffset)
    {
        Vector256<ushort> yTerm = MultiplyConstant(y, 74, 133);
        Vector256<ushort> vRed = MultiplyConstant(v, 102, 37);
        Vector256<ushort> uGreen = MultiplyConstant(u, 25, 19);
        Vector256<ushort> vGreen = MultiplyConstant(v, 52, 8);
        Vector256<ushort> uBlue = MultiplyConstant(u, 129, 26);

        Vector256<ushort> redSum = Vector256.Add(yTerm, vRed);
        Vector256<ushort> red = Vector256.Min(
            Vector256.ShiftRightLogical(
                Vector256.Subtract(Vector256.Max(redSum, Vector256.Create((ushort)14234)),
                    Vector256.Create((ushort)14234)), 6),
            Vector256.Create((ushort)255));
        Vector256<ushort> greenNegative = Vector256.Add(uGreen, vGreen);
        Vector256<ushort> greenPositive = Vector256.Add(yTerm, Vector256.Create((ushort)8708));
        Vector256<ushort> green = Vector256.Min(
            Vector256.ShiftRightLogical(
                Vector256.Subtract(Vector256.Max(greenPositive, greenNegative), greenNegative), 6),
            Vector256.Create((ushort)255));
        Vector256<ushort> blueSum = Vector256.Add(yTerm, uBlue);
        Vector256<ushort> blue = Vector256.Min(
            Vector256.ShiftRightLogical(
                Vector256.Subtract(Vector256.Max(blueSum, Vector256.Create((ushort)17685)),
                    Vector256.Create((ushort)17685)), 6),
            Vector256.Create((ushort)255));

        Vector256<ushort> redGreen = Vector256.BitwiseOr(red, Vector256.ShiftLeft(green, 8));
        Vector256<ushort> blueAlpha = Vector256.BitwiseOr(blue, Vector256.ShiftLeft(alpha, 8));
        Vector256<uint> rgbaLow = Vector256.BitwiseOr(
            Vector256.WidenLower(redGreen),
            Vector256.ShiftLeft(Vector256.WidenLower(blueAlpha), 16));
        Vector256<uint> rgbaHigh = Vector256.BitwiseOr(
            Vector256.WidenUpper(redGreen),
            Vector256.ShiftLeft(Vector256.WidenUpper(blueAlpha), 16));

        ref uint output32 = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref output, outputOffset));
        Vector256.StoreUnsafe(rgbaLow, ref output32);
        Vector256.StoreUnsafe(rgbaHigh, ref Unsafe.Add(ref output32, 8));
    }

    private static void ConvertRowScalarCore(
        ref byte y,
        ref byte u,
        ref byte v,
        ref byte alpha,
        bool hasAlpha,
        ref byte output,
        int width)
    {
        for (int column = 0; column < width; column++)
        {
            WritePixel(ref Unsafe.Add(ref output, column * 4),
                Unsafe.Add(ref y, column), Unsafe.Add(ref u, column), Unsafe.Add(ref v, column),
                hasAlpha ? Unsafe.Add(ref alpha, column) : (byte)255);
        }
    }

    private static void ConvertRowVector128Core(
        ref byte y,
        ref byte u,
        ref byte v,
        ref byte alpha,
        bool hasAlpha,
        ref byte output,
        int width)
    {
        int column = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            for (; column <= width - 16; column += 16)
            {
                Vector128<byte> yBytes = Vector128.LoadUnsafe(ref y, (nuint)column);
                Vector128<byte> uBytes = Vector128.LoadUnsafe(ref u, (nuint)column);
                Vector128<byte> vBytes = Vector128.LoadUnsafe(ref v, (nuint)column);
                Vector128<byte> alphaBytes = hasAlpha
                    ? Vector128.LoadUnsafe(ref alpha, (nuint)column)
                    : Vector128.Create(byte.MaxValue);
                ConvertVector16(yBytes, uBytes, vBytes, alphaBytes,
                    ref output, column * 4);
            }
        }

        for (; column < width; column++)
        {
            WritePixel(ref Unsafe.Add(ref output, column * 4),
                Unsafe.Add(ref y, column), Unsafe.Add(ref u, column), Unsafe.Add(ref v, column),
                hasAlpha ? Unsafe.Add(ref alpha, column) : (byte)255);
        }
    }

    private static void ConvertVector16(
        Vector128<byte> yBytes,
        Vector128<byte> uBytes,
        Vector128<byte> vBytes,
        Vector128<byte> alphaBytes,
        ref byte output,
        int outputOffset)
    {
        Vector128<ushort> yLow = Vector128.WidenLower(yBytes);
        Vector128<ushort> yHigh = Vector128.WidenUpper(yBytes);
        Vector128<ushort> uLow = Vector128.WidenLower(uBytes);
        Vector128<ushort> uHigh = Vector128.WidenUpper(uBytes);
        Vector128<ushort> vLow = Vector128.WidenLower(vBytes);
        Vector128<ushort> vHigh = Vector128.WidenUpper(vBytes);
        Vector128<ushort> alphaLow = Vector128.WidenLower(alphaBytes);
        Vector128<ushort> alphaHigh = Vector128.WidenUpper(alphaBytes);

        ConvertVector8(yLow, uLow, vLow, alphaLow, ref output, outputOffset);
        ConvertVector8(yHigh, uHigh, vHigh, alphaHigh, ref output, outputOffset + 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> MultiplyConstant(
        Vector128<ushort> value,
        ushort high,
        ushort low) =>
        Vector128.Add(
            Vector128.Multiply(value, Vector128.Create(high)),
            Vector128.ShiftRightLogical(
                Vector128.Multiply(value, Vector128.Create(low)), 8));

    private static void ConvertVector8(
        Vector128<ushort> y,
        Vector128<ushort> u,
        Vector128<ushort> v,
        Vector128<ushort> alpha,
        ref byte output,
        int outputOffset)
    {
        Vector128<ushort> yTerm = MultiplyConstant(y, 74, 133);
        Vector128<ushort> vRed = MultiplyConstant(v, 102, 37);
        Vector128<ushort> uGreen = MultiplyConstant(u, 25, 19);
        Vector128<ushort> vGreen = MultiplyConstant(v, 52, 8);
        Vector128<ushort> uBlue = MultiplyConstant(u, 129, 26);

        Vector128<ushort> redSum = Vector128.Add(yTerm, vRed);
        Vector128<ushort> red = Vector128.Min(
            Vector128.ShiftRightLogical(
                Vector128.Subtract(Vector128.Max(redSum, Vector128.Create((ushort)14234)),
                    Vector128.Create((ushort)14234)), 6),
            Vector128.Create((ushort)255));
        Vector128<ushort> greenNegative = Vector128.Add(uGreen, vGreen);
        Vector128<ushort> greenPositive = Vector128.Add(yTerm, Vector128.Create((ushort)8708));
        Vector128<ushort> green = Vector128.Min(
            Vector128.ShiftRightLogical(
                Vector128.Subtract(Vector128.Max(greenPositive, greenNegative), greenNegative), 6),
            Vector128.Create((ushort)255));
        Vector128<ushort> blueSum = Vector128.Add(yTerm, uBlue);
        Vector128<ushort> blue = Vector128.Min(
            Vector128.ShiftRightLogical(
                Vector128.Subtract(Vector128.Max(blueSum, Vector128.Create((ushort)17685)),
                    Vector128.Create((ushort)17685)), 6),
            Vector128.Create((ushort)255));

        Vector128<ushort> redGreen = Vector128.BitwiseOr(red, Vector128.ShiftLeft(green, 8));
        Vector128<ushort> blueAlpha = Vector128.BitwiseOr(blue, Vector128.ShiftLeft(alpha, 8));
        Vector128<uint> rgbaLow = Vector128.BitwiseOr(
            Vector128.WidenLower(redGreen),
            Vector128.ShiftLeft(Vector128.WidenLower(blueAlpha), 16));
        Vector128<uint> rgbaHigh = Vector128.BitwiseOr(
            Vector128.WidenUpper(redGreen),
            Vector128.ShiftLeft(Vector128.WidenUpper(blueAlpha), 16));

        ref uint output32 = ref Unsafe.As<byte, uint>(ref Unsafe.Add(ref output, outputOffset));
        Vector128.StoreUnsafe(rgbaLow, ref output32);
        Vector128.StoreUnsafe(rgbaHigh, ref Unsafe.Add(ref output32, 4));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePixel(ref byte destination, int y, int u, int v, byte alpha)
    {
        int yTerm = y * 19077 >> 8;
        int red = (yTerm + (v * 26149 >> 8) - 14234) >> 6;
        int green = (yTerm - (u * 6419 >> 8) - (v * 13320 >> 8) + 8708) >> 6;
        int blue = (yTerm + (u * 33050 >> 8) - 17685) >> 6;
        Unsafe.Add(ref destination, 0) = (byte)(red < 0 ? 0 : red > 255 ? 255 : red);
        Unsafe.Add(ref destination, 1) = (byte)(green < 0 ? 0 : green > 255 ? 255 : green);
        Unsafe.Add(ref destination, 2) = (byte)(blue < 0 ? 0 : blue > 255 ? 255 : blue);
        Unsafe.Add(ref destination, 3) = alpha;
    }

    private static byte[] DecodeAndUnfilterAlpha(
        ReadOnlySpan<byte> alphaChunk,
        int width,
        int height)
    {
        if (alphaChunk.Length < 1)
        {
            throw new InvalidDataException("The WebP ALPH chunk is empty.");
        }

        int header = alphaChunk[0];
        int compression = header & 3;
        int filter = (header >> 2) & 3;
        int pixelCount = checked(width * height);
        byte[] values = WebpBufferPool<byte>.Rent(pixelCount);
        try
        {
            if (compression == 0)
            {
                if (alphaChunk.Length - 1 != pixelCount)
                {
                    throw new InvalidDataException("The raw WebP alpha stream has the wrong size.");
                }

                alphaChunk[1..].CopyTo(values);
            }
            else if (compression == 1)
            {
                Vp8LDecoder.DecodeAlphaTo(alphaChunk[1..], width, height, values.AsSpan(0, pixelCount));
            }
            else
            {
                throw new InvalidDataException("The WebP alpha compression method is unsupported.");
            }

            if (filter == 1)
            {
                for (int row = 0; row < height; row++)
                {
                    int offset = row * width;
                    int above = row == 0 ? 0 : values[offset - width];
                    values[offset] = (byte)(values[offset] + above);
                    for (int column = 1; column < width; column++)
                    {
                        values[offset + column] = (byte)(values[offset + column] + values[offset + column - 1]);
                    }
                }
            }
            else if (filter == 2)
            {
                for (int row = 0; row < height; row++)
                {
                    int offset = row * width;
                    if (row == 0)
                    {
                        values[offset] = (byte)(values[offset] + (byte)0);
                        for (int column = 1; column < width; column++)
                        {
                            values[offset + column] = (byte)(values[offset + column] + values[offset + column - 1]);
                        }
                    }
                    else
                    {
                        int aboveOffset = offset - width;
                        for (int column = 0; column < width; column++)
                        {
                            values[offset + column] = (byte)(values[offset + column] + values[aboveOffset + column]);
                        }
                    }
                }
            }
            else if (filter == 3)
            {
                for (int row = 0; row < height; row++)
                {
                    int offset = row * width;
                    if (row == 0)
                    {
                        for (int column = 1; column < width; column++)
                        {
                            values[offset + column] = (byte)(values[offset + column] + values[offset + column - 1]);
                        }
                    }
                    else
                    {
                        int aboveOffset = offset - width;
                        int above = values[aboveOffset];
                        values[offset] = (byte)(values[offset] + above);
                        for (int column = 1; column < width; column++)
                        {
                            int left = values[offset + column - 1];
                            int top = values[aboveOffset + column];
                            int topLeft = above;
                            int predictor = Math.Clamp(left + top - topLeft, 0, 255);
                            values[offset + column] = (byte)(values[offset + column] + predictor);
                            above = top;
                        }
                    }
                }
            }

            return values;
        }
        catch
        {
            WebpBufferPool<byte>.Return(values);
            throw;
        }
    }
}
