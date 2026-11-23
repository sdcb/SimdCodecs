using System;
using System.Buffers;
using System.IO;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>
/// The <see cref="IImageRowReader"/> for a codec (or a variant, or options) that cannot decode as the stream arrives: it
/// reads the stream to its end, decodes it with the codec's span decoder, and hands out the rows. Its rows are those of
/// <see cref="IImageDecoder.TryDecodeInto"/> by construction; <see cref="IImageRowReader.IsStreaming"/> is false.
/// </summary>
public sealed class BufferedRowReader : StreamingRowReader
{
    private readonly DecodeOptions? _options;
    private readonly ImageDecoderRegistration.TryReadInfoHandler _readInfo;
    private readonly ImageDecoderRegistration.TryDecodeIntoHandler _decodeInto;

    /// <summary>A reader that decodes <paramref name="input"/> whole with the codec's members.</summary>
    /// <param name="input">The stream to read; the reader owns it.</param>
    /// <param name="format">The layout of the rows to write.</param>
    /// <param name="options">Scale, region, orientation, as <see cref="IImageDecoder.TryDecodeInto"/> takes them.</param>
    /// <param name="readInfo">The codec's <see cref="IImageDecoder.TryReadInfo"/>.</param>
    /// <param name="decodeInto">The codec's <see cref="IImageDecoder.TryDecodeInto"/> (or a lambda over its throwing decoder,
    /// to fail with the reason).</param>
    public BufferedRowReader(StreamInput input, PixelFormat format, DecodeOptions? options,
        ImageDecoderRegistration.TryReadInfoHandler readInfo, ImageDecoderRegistration.TryDecodeIntoHandler decodeInto)
        : base(input, format)
    {
        ArgumentNullException.ThrowIfNull(readInfo);
        ArgumentNullException.ThrowIfNull(decodeInto);
        _options = options;
        _readInfo = readInfo;
        _decodeInto = decodeInto;
    }

    /// <summary>A reader over a codec's registration (what the facade uses for a codec without a streaming decoder).</summary>
    public BufferedRowReader(StreamInput input, PixelFormat format, DecodeOptions? options, ImageDecoderRegistration codec)
        : this(input, format, options, (codec ?? throw new ArgumentNullException(nameof(codec))).TryReadInfo, codec.TryDecodeInto)
    {
    }

    /// <inheritdoc />
    protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = 0;
        width = height = 0;
        info = default;
        if (!isFinal)
            return OperationStatus.NeedMoreData;
        if (!_readInfo(input, out info))
            throw new InvalidDataException("The image header could not be read.");
        (width, height) = (_options ?? DecodeOptions.Default).GetOutputSize(info);
        UseBufferedFallback(_decodeInto, _options);
        return OperationStatus.Done;
    }

    /// <inheritdoc />
    protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        Span<byte> destination, int stride, int rowCount, out int rowsWritten) =>
        throw new InvalidOperationException("A buffered reader hands out its decoded rows.");
}
