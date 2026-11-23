using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Qoi;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for QOI, bridging <see cref="QoiDecoder"/> into the <c>Sdcb.SimdCodecs</c>
/// facade. An image decodes as 8-bit RGB or RGBA, as its header declares; a linear colour space is reported as
/// a linear transfer in <see cref="IDecodedImage.ColorEncoding"/>. <see cref="TryDecodeInto"/> writes the
/// caller's buffer directly. Malformed data makes the <c>Try</c> methods return false rather than throw. QOI carries
/// no metadata and no thumbnail, and does not scale natively.
/// <para>
/// <see cref="CreateRowReader"/> decodes from a forward-only stream a row at a time, holding the decoder state and
/// the input of about one row; scaling, cropping or orientation options read the stream whole first.
/// </para>
/// </summary>
public sealed class QoiImageDecoder : IStreamingImageDecoder
{
    private static readonly ColorEncoding LinearSrgb = new() { Transfer = TransferFunction.Linear };

    /// <summary>The registration for <c>ImageCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Qoi,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        Decode = Decode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        CreateRowReader = CreateRowReader,
    };

    /// <inheritdoc />
    /// <remarks>The magic <c>qoif</c>.</remarks>
    public static int SignatureLength => 4;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => QoiCodec.HasSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            info = InfoOf(QoiCodec.ReadHeader(data));
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator) =>
        ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        ImageDecoderFallback.TryDecode(Decode, data, out image, options, cancellationToken);

    /// <inheritdoc />
    public static IDecodedImage Decode(ReadOnlySpan<byte> data, DecodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var info = QoiCodec.ReadHeader(data);
        options?.Validate(info.Width, info.Height);
        var raster = RasterImage.Create(info.Width, info.Height, info.Channels, SampleFormat.UInt8, options?.Pool,
            colorEncoding: ColorOf(info.ColorSpace), bitDepth: 8);
        try
        {
            QoiDecoder.Decode(data, info, raster.WritablePixels, info.Width * info.Channels,
                info.Channels == 4 ? PixelFormat.Rgba32 : PixelFormat.Rgb24, cancellationToken);
        }
        catch
        {
            raster.Dispose();
            throw;
        }
        return DecodeOptions.Apply(raster, options);
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);
        try
        {
            var info = QoiCodec.ReadHeader(data);
            if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride))
                return false;
            QoiDecoder.Decode(data, info, destination, rowStride, format, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>QOI has no metadata blocks: a readable file gives <see cref="ImageMetadata.Empty"/>.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata) =>
        ImageDecoderFallback.TryReadNoMetadata(TryReadInfo, data, out metadata);

    /// <inheritdoc />
    /// <remarks>QOI has no thumbnails: always false.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be at
    /// least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    /// <inheritdoc />
    /// <remarks>
    /// The whole image at full size streams: a row is decoded once the input holds all of its chunks, so the reader
    /// buffers about one row of the file. Any other <paramref name="options"/> read the stream to its end and decode
    /// it with <see cref="TryDecodeInto"/> (<see cref="IImageRowReader.IsStreaming"/> false). As for the span
    /// decoder, the 8-byte end marker is not checked.
    /// </remarks>
    public static StreamingRowReader CreateRowReader(StreamInput input, PixelFormat format, DecodeOptions? options) =>
        new RowReader(input, format, options);

    private static ImageInfo InfoOf(QoiInfo qoi) => new(qoi.Width, qoi.Height, qoi.Channels, SampleFormat.UInt8)
    {
        Format = ImageFormat.Qoi,
        BitDepth = 8,
        ColorEncoding = ColorOf(qoi.ColorSpace),
    };

    private static ColorEncoding ColorOf(QoiColorSpace space) =>
        space == QoiColorSpace.Linear ? LinearSrgb : ColorEncoding.AssumedSrgb;

    /// <summary>
    /// The streaming reader. A row is the unit: <see cref="QoiDecoder.TryDecodeRow"/> runs on a copy of the decoder
    /// state over the buffered input and the copy is kept only when the whole row decoded, so a row never splits
    /// across calls and the destination of a failed attempt is simply written again. After a failed attempt the next
    /// one waits for twice the bytes (capped at the 5 bytes a pixel a row can take at most), so a stream that arrives a
    /// byte at a time costs a few decodes of each row, not one per byte. Before a row it asks for more input while the
    /// bytes buffered are fewer than twice what the previous row took, so a partial row is seldom decoded only to be
    /// decoded again.
    /// </summary>
    private sealed class RowReader(StreamInput input, PixelFormat format, DecodeOptions? options) : StreamingRowReader(input, format)
    {
        private QoiCodec.Reader _reader;
        private int _width;
        private long _rowWorstBytes;   // the most input one row's chunks take: 5 bytes a pixel
        private int _retryAt;          // after a failed row: the input length worth another attempt (0: any)
        private int _lastRowBytes;     // the input the previous row took

        protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            out ImageInfo info, out int width, out int height)
        {
            consumed = 0;
            info = default;
            width = height = 0;
            if (input.Length < QoiCodec.HeaderSize)
            {
                if (!"qoif"u8.StartsWith(input[..Math.Min(input.Length, 4)]))
                    throw new InvalidDataException("Not a QOI image.");
                return OperationStatus.NeedMoreData;
            }
            var qoi = QoiCodec.ReadHeader(input);
            if ((long)qoi.Width * Format.BytesPerPixel > Array.MaxLength)
                throw new InvalidDataException($"A {qoi.Width}-pixel {Format} row does not fit in one buffer.");
            info = InfoOf(qoi);
            if (!IsFullImage(options))
            {
                UseBufferedFallback(TryDecodeInto, options);
                (width, height) = options!.GetOutputSize(info);
                return OperationStatus.Done;
            }
            _reader = new QoiCodec.Reader(qoi.Channels == 3);
            _width = qoi.Width;
            _rowWorstBytes = 5L * qoi.Width;
            (width, height) = (qoi.Width, qoi.Height);
            consumed = QoiCodec.HeaderSize;
            return OperationStatus.Done;
        }

        protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            Span<byte> destination, int stride, int rowCount, out int rowsWritten)
        {
            consumed = rowsWritten = 0;
            if (!isFinal && input.Length < _retryAt)
                return OperationStatus.NeedMoreData;
            var rowBytes = _width * Format.BytesPerPixel;
            Span<uint> chunk = stackalloc uint[QoiDecoder.RowChunk];
            var attempt = _reader;
            while (rowsWritten < rowCount)
            {
                var left = input.Length - consumed;
                if (!isFinal && left < Math.Min(2L * _lastRowBytes + 16, _rowWorstBytes))
                    return OperationStatus.NeedMoreData;
                attempt.Position = consumed;
                if (!QoiDecoder.TryDecodeRow(ref attempt, input, destination.Slice(rowsWritten * stride, rowBytes), _width, Format, chunk))
                {
                    // The bytes left end inside this row; they start the next call's input. Wait for twice as many,
                    // never for more than the row can need (with that many it cannot fail).
                    _retryAt = (int)Math.Min(Math.Min(Math.Max(2L * left, 64), _rowWorstBytes), Array.MaxLength);
                    return OperationStatus.NeedMoreData;
                }
                _reader = attempt;
                _lastRowBytes = attempt.Position - consumed;
                consumed = attempt.Position;
                rowsWritten++;
                _retryAt = 0;
            }
            return OperationStatus.Done;
        }
    }
}
