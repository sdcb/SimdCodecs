using System;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// An image read from a stream a band of rows at a time, top to bottom: the forward-only counterpart of
/// <see cref="IImageDecoder.TryDecodeInto"/>. A codec that can decode as its input arrives (<see cref="IsStreaming"/>)
/// holds only a few rows and a small input buffer, and reads the stream no further than the rows asked for need; any
/// other input is read whole and decoded first, behind the same API.
/// <para>
/// The rows are exactly those <see cref="IImageDecoder.TryDecodeInto"/> writes for the same bytes, format and options.
/// Undecodable input throws <see cref="System.IO.InvalidDataException"/> (a truncated stream included); a cancelled token
/// throws <see cref="OperationCanceledException"/>. <see cref="ReadRows"/> reads the stream synchronously and
/// <see cref="ReadRowsAsync"/> asynchronously (a request body that forbids synchronous reads); a reader may mix them.
/// </para>
/// </summary>
public interface IImageRowReader : IDisposable, IAsyncDisposable
{
    /// <summary>The header: stored size, channels, depth, colour encoding (metadata stored after the pixels is not in it).</summary>
    ImageInfo Info { get; }

    /// <summary>The width of a row this reader writes (the stored width, or the scaled / cropped one an option asks for).</summary>
    int Width { get; }

    /// <summary>The number of rows this reader writes.</summary>
    int Height { get; }

    /// <summary>The layout of the rows written.</summary>
    PixelFormat Format { get; }

    /// <summary>Rows written so far.</summary>
    int RowsRead { get; }

    /// <summary>
    /// True while the codec decodes as the stream arrives; false when it read the whole input and decoded it first (a
    /// variant it cannot stream, such as progressive JPEG or interlaced PNG, or an option it cannot honour on the fly).
    /// </summary>
    bool IsStreaming { get; }

    /// <summary>
    /// Writes the next rows (at most <paramref name="rowCount"/>, fewer only at the end of the image) into
    /// <paramref name="destination"/>, row <c>i</c> at byte <c>i × stride</c>; returns how many, 0 once every row is read.
    /// </summary>
    /// <param name="destination">At least <c>stride × (rowCount − 1) + Width × Format.BytesPerPixel</c> bytes.</param>
    /// <param name="rowCount">The most rows to write.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="cancellationToken">Checked between bands of input.</param>
    /// <exception cref="System.IO.InvalidDataException">The stream is malformed or ends early.</exception>
    /// <exception cref="ArgumentException">The destination is too short or the stride shorter than a row.</exception>
    int ReadRows(Span<byte> destination, int rowCount, int stride = 0, CancellationToken cancellationToken = default);

    /// <summary><see cref="ReadRows"/>, reading the stream asynchronously.</summary>
    /// <inheritdoc cref="ReadRows" path="/param"/>
    ValueTask<int> ReadRowsAsync(Memory<byte> destination, int rowCount, int stride = 0, CancellationToken cancellationToken = default);
}

/// <summary>
/// A codec that decodes from a forward-only stream. As for <see cref="IImageDecoder"/>, the facade binds it by method
/// group (<see cref="ImageDecoderRegistration.CreateRowReader"/>), never inside a generic method (Mono AOT).
/// </summary>
public interface IStreamingImageDecoder : IImageDecoder
{
    /// <summary>
    /// Creates a reader over <paramref name="input"/>, positioned at the start of the image (its leading bytes may
    /// already be buffered). The reader owns the input. It reads nothing until its header is read
    /// (<see cref="StreamingRowReader.Initialize"/> / <see cref="StreamingRowReader.InitializeAsync"/>, or the first read).
    /// </summary>
    /// <param name="input">The stream to read.</param>
    /// <param name="format">The layout of the rows to write.</param>
    /// <param name="options">Scale, region, orientation; null for the whole image at full size. The pool is ignored.</param>
    static abstract StreamingRowReader CreateRowReader(StreamInput input, PixelFormat format, DecodeOptions? options);
}
