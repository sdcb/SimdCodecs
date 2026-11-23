using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>
/// The base of every <see cref="IImageRowReader"/>: one suspendable decode loop that both <see cref="ReadRows"/> and
/// <see cref="ReadRowsAsync"/> drive, so a codec writes its streaming decoder once and gets the synchronous and
/// asynchronous readers from it.
/// <para>
/// A codec implements two steps over <see cref="StreamInput.Buffered"/>. Each returns
/// <see cref="OperationStatus.Done"/> when it got what it was asked for, or <see cref="OperationStatus.NeedMoreData"/>
/// when the bytes given end inside a unit it cannot split (a chunk header, an MCU row, a compressed run). On
/// <see cref="OperationStatus.NeedMoreData"/> the codec stops at the last unit it completed — what it consumed and the
/// rows it wrote are kept, and its state resumes from there — and the driver reads more input (synchronously or
/// asynchronously) and calls again. <c>isFinal</c> says the stream has ended: a codec still needing more then has a
/// truncated image, and the driver throws <see cref="InvalidDataException"/>. Malformed input throws
/// <see cref="InvalidDataException"/> from the step itself, saying why.
/// </para>
/// <para>
/// <see cref="DecodeHeader"/> sees the input from the image's first byte each time it is called, and nothing it reads is
/// consumed until it returns <see cref="OperationStatus.Done"/>, so a codec that finds a variant it cannot stream calls
/// <see cref="UseBufferedFallback"/> from there: the driver then reads the whole stream, decodes it with the codec's
/// span decoder, and hands out its rows (<see cref="IsStreaming"/> false).
/// </para>
/// </summary>
public abstract class StreamingRowReader : IImageRowReader
{
    private ImageDecoderRegistration.TryDecodeIntoHandler? _fallback;
    private DecodeOptions? _fallbackOptions;
    private byte[]? _frame;           // the fallback's decoded rows, tightly packed
    private bool _initialized;
    private bool _disposed;

    /// <summary>A reader over <paramref name="input"/> writing rows in <paramref name="format"/>; it owns the input.</summary>
    protected StreamingRowReader(StreamInput input, PixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(input);
        Input = input;
        Format = format;
    }

    /// <summary>The input the codec decodes.</summary>
    protected StreamInput Input { get; }

    /// <inheritdoc />
    public ImageInfo Info { get { EnsureInitialized(); return field; } private set; }

    /// <inheritdoc />
    public int Width { get { EnsureInitialized(); return field; } private set; }

    /// <inheritdoc />
    public int Height { get { EnsureInitialized(); return field; } private set; }

    /// <inheritdoc />
    public PixelFormat Format { get; }

    /// <inheritdoc />
    public int RowsRead { get; private set; }

    /// <inheritdoc />
    public bool IsStreaming => _fallback is null;

    /// <summary>True once the header is read.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// True when <paramref name="options"/> asks for nothing but the whole image at full size, as stored: what a codec's
    /// streaming path decodes. Any other options go to <see cref="UseBufferedFallback"/> (or a <see cref="BufferedRowReader"/>)
    /// unless the codec scales or crops on the fly itself.
    /// </summary>
    public static bool IsFullImage(DecodeOptions? options) => options is null || (options.IsFullImage && !options.ApplyOrientation);

    /// <summary>Reads the header (synchronously) if it is not read yet.</summary>
    /// <exception cref="InvalidDataException">The header is malformed or the stream ends inside it.</exception>
    public void Initialize(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!_initialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StepHeader())
                Input.Fill(checked(Input.Buffered.Length + 1));
        }
        if (_fallback is not null && _frame is null)
        {
            Input.ReadToEnd();
            DecodeFallback(cancellationToken);
        }
    }

    /// <summary>Reads the header (asynchronously) if it is not read yet.</summary>
    /// <exception cref="InvalidDataException">The header is malformed or the stream ends inside it.</exception>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!_initialized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StepHeader())
                await Input.FillAsync(checked(Input.Buffered.Length + 1), cancellationToken).ConfigureAwait(false);
        }
        if (_fallback is not null && _frame is null)
        {
            await Input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            DecodeFallback(cancellationToken);
        }
    }

    /// <inheritdoc />
    public int ReadRows(Span<byte> destination, int rowCount, int stride = 0, CancellationToken cancellationToken = default)
    {
        Initialize(cancellationToken);
        var (count, step) = CheckRead(destination.Length, rowCount, stride);
        if (_frame is not null)
            return CopyFallbackRows(destination, count, step);
        var written = 0;
        while (written < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StepRows(destination, step, count, ref written))
                Input.Fill(checked(Input.Buffered.Length + 1));
        }
        return written;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReadRowsAsync(Memory<byte> destination, int rowCount, int stride = 0, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var (count, step) = CheckRead(destination.Length, rowCount, stride);
        if (_frame is not null)
            return CopyFallbackRows(destination.Span, count, step);
        var written = 0;
        while (written < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StepRows(destination.Span, step, count, ref written))
                await Input.FillAsync(checked(Input.Buffered.Length + 1), cancellationToken).ConfigureAwait(false);
        }
        return written;
    }

    /// <summary>
    /// Parses the header from <paramref name="input"/>, which starts at the image's first byte on every call. On
    /// <see cref="OperationStatus.Done"/> it sets <paramref name="info"/> and the output size, and <paramref name="consumed"/>
    /// to where its row data starts; or it calls <see cref="UseBufferedFallback"/> (then <paramref name="consumed"/> is
    /// ignored).
    /// </summary>
    /// <param name="input">The buffered input, from the image's first byte.</param>
    /// <param name="isFinal">True when the stream has ended after <paramref name="input"/>.</param>
    /// <param name="consumed">The header's length.</param>
    /// <param name="info">The header.</param>
    /// <param name="width">The width of the rows the reader writes.</param>
    /// <param name="height">The number of rows the reader writes.</param>
    /// <exception cref="InvalidDataException">The header is malformed.</exception>
    protected abstract OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height);

    /// <summary>
    /// Decodes up to <paramref name="rowCount"/> rows from <paramref name="input"/> into <paramref name="destination"/>
    /// (row <c>i</c> at <c>i × stride</c>). <see cref="OperationStatus.Done"/> when it wrote <paramref name="rowCount"/> rows
    /// or the last row of the image; <see cref="OperationStatus.NeedMoreData"/> when the input ends inside the next unit,
    /// having consumed only whole units. Rows already written and bytes already consumed count either way.
    /// </summary>
    /// <param name="input">The unconsumed input.</param>
    /// <param name="isFinal">True when the stream has ended after <paramref name="input"/>.</param>
    /// <param name="consumed">The bytes of <paramref name="input"/> it is done with.</param>
    /// <param name="destination">Where the rows go.</param>
    /// <param name="stride">Bytes from one row to the next (at least a row).</param>
    /// <param name="rowCount">The most rows to write (at least 1, never past the image).</param>
    /// <param name="rowsWritten">The rows it wrote.</param>
    /// <exception cref="InvalidDataException">The data is malformed.</exception>
    protected abstract OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        Span<byte> destination, int stride, int rowCount, out int rowsWritten);

    /// <summary>
    /// Called from <see cref="DecodeHeader"/>: decode this image whole instead, with <paramref name="decode"/> (the codec's
    /// <see cref="IImageDecoder.TryDecodeInto"/>, or a lambda that throws <see cref="InvalidDataException"/> with the
    /// reason) over the complete file, once the stream has been read to its end.
    /// </summary>
    protected void UseBufferedFallback(ImageDecoderRegistration.TryDecodeIntoHandler decode, DecodeOptions? options)
    {
        ArgumentNullException.ThrowIfNull(decode);
        if (_initialized)
            throw new InvalidOperationException("The fallback is chosen while the header is read.");
        _fallback = decode;
        _fallbackOptions = options;
    }

    /// <summary>Releases the codec's resources; the base disposes the input.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        Input.Dispose();
        if (_frame is not null)
            ArrayPool<byte>.Shared.Return(_frame);
        _frame = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("The header is not read yet: call Initialize or InitializeAsync first.");
    }

    // One header attempt. True when more input is needed.
    private bool StepHeader()
    {
        var input = Input.Buffered;
        var final = Input.IsCompleted;
        var status = DecodeHeader(input, final, out var consumed, out var info, out var width, out var height);
        if (status == OperationStatus.NeedMoreData)
        {
            if (final)
                throw new InvalidDataException("The stream ends inside the image header.");
            return true;
        }
        if (status != OperationStatus.Done)
            throw new InvalidDataException($"The image header could not be read ({status}).");
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"The image is {width}x{height}.");
        Info = info;
        Width = width;
        Height = height;
        if (_fallback is null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(consumed, input.Length);
            Input.Consume(consumed);
        }
        _initialized = true;
        return false;
    }

    // One row attempt. True when more input is needed before the next.
    private bool StepRows(Span<byte> destination, int step, int count, ref int written)
    {
        var final = Input.IsCompleted;
        var status = DecodeRows(Input.Buffered, final, out var consumed, destination[(written * step)..], step,
            count - written, out var rows);
        if ((uint)rows > (uint)(count - written))
            throw new InvalidOperationException($"The codec wrote {rows} rows of {count - written} asked for.");
        Input.Consume(consumed);
        written += rows;
        RowsRead += rows;
        if (status == OperationStatus.Done)
        {
            if (written < count)
                throw new InvalidOperationException($"The codec reported Done after {written} of {count} rows.");
            return false;
        }
        if (status != OperationStatus.NeedMoreData)
            throw new InvalidDataException($"The image data could not be decoded ({status}).");
        if (rows > 0 || consumed > 0)
            return false;
        if (final)
            throw new InvalidDataException($"The stream ends after {RowsRead} of {Height} rows.");
        return true;
    }

    private (int Count, int Step) CheckRead(int destinationLength, int rowCount, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(stride);
        var rowBytes = checked(Width * Format.BytesPerPixel);
        var step = stride == 0 ? rowBytes : stride;
        if (step < rowBytes)
            throw new ArgumentException($"A stride of {stride} is shorter than a {rowBytes}-byte row.", nameof(stride));
        var count = Math.Min(rowCount, Height - RowsRead);
        if (count > 0 && destinationLength < (long)step * (count - 1) + rowBytes)
            throw new ArgumentException($"{count} rows at stride {step} need more than {destinationLength} bytes.", "destination");
        return (count, step);
    }

    private void DecodeFallback(CancellationToken cancellationToken)
    {
        var rowBytes = (long)Width * Format.BytesPerPixel;
        var total = rowBytes * Height;
        if (total > Array.MaxLength)
            throw new InvalidDataException($"A {Width}x{Height} {Format} image does not fit in one buffer.");
        _frame = ArrayPool<byte>.Shared.Rent((int)total);
        var data = Input.Buffered;
        if (!_fallback!(data, _frame.AsSpan(0, (int)total), (int)rowBytes, Format, _fallbackOptions, cancellationToken))
            throw new InvalidDataException("The image could not be decoded.");
        Input.Consume(data.Length);
    }

    private int CopyFallbackRows(Span<byte> destination, int count, int step)
    {
        var rowBytes = Width * Format.BytesPerPixel;
        for (var i = 0; i < count; i++)
            _frame.AsSpan((RowsRead + i) * rowBytes, rowBytes).CopyTo(destination.Slice(i * step, rowBytes));
        RowsRead += count;
        return count;
    }
}
