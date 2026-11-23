using System.IO;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs;

public static partial class ImageCodecs
{
    /// <summary>
    /// The leading bytes <see cref="OpenRowReader"/> sniffs: the longest fixed signature a codec reads. An ISO-BMFF
    /// <c>ftyp</c> box (HEIF, AVIF) lists brands to its end, so for one the sniff takes the whole box, up to
    /// <see cref="MaxSniffLength"/>. The sniff always sees exactly that many bytes (fewer only for a shorter stream),
    /// whatever the stream's reads return, so the codec chosen never depends on how the bytes arrive.
    /// </summary>
    internal const int SniffLength = 64;

    /// <summary>The most bytes the sniff reads for an <c>ftyp</c> box (a thousand brands).</summary>
    internal const int MaxSniffLength = 4096;

    /// <summary>
    /// Opens a reader that decodes <paramref name="stream"/> a band of rows at a time, top to bottom, as the stream
    /// arrives: a request body, a network or pipe stream, a file too large to read whole. The stream need not seek. The
    /// codec is chosen as <see cref="DetectFormat"/> chooses it, from the first 64 bytes (a leading ISO-BMFF <c>ftyp</c> box whole, up to 4 KiB). A codec that decodes as its input
    /// arrives (<see cref="IImageRowReader.IsStreaming"/> true) holds a few rows and a small input buffer, and reads the
    /// stream no further than the rows asked for need; any other codec, variant or option (an interlaced PNG, a
    /// progressive JPEG, a scaled decode) reads the stream to its end and decodes it first, behind the same API
    /// (<see cref="IImageRowReader.IsStreaming"/> false).
    /// <para>
    /// The rows are exactly those <see cref="TryDecodeInto"/> writes for the same bytes, <paramref name="format"/> and
    /// <paramref name="options"/>. The header is read before this returns, so <see cref="IImageRowReader.Width"/> and
    /// <see cref="IImageRowReader.Height"/> size the destination. A gain-map JPEG reads as the plain JPEG it carries (its SDR
    /// base, which is <see cref="TryDecodeInto"/>'s output for it too): the gain map lies past the sniffed bytes. Reads
    /// the stream synchronously; <see cref="OpenRowReaderAsync"/> never does. Dispose the reader when done.
    /// </para>
    /// </summary>
    /// <param name="stream">The image, from its current position; it need not seek.</param>
    /// <param name="format">The layout of the rows.</param>
    /// <param name="options">Scale, region and orientation; null for the whole image at full size, as stored. The pool is ignored.</param>
    /// <param name="leaveOpen">Keep <paramref name="stream"/> open when the reader is disposed (or when this throws).</param>
    /// <param name="cancellationToken">Checked while the header (or, for a buffered decode, the whole stream) is read.</param>
    /// <exception cref="InvalidDataException">No registered codec decodes the stream (the message names
    /// <see cref="DetectFormat"/>'s answer), or its header is malformed or truncated.</exception>
    /// <exception cref="NotSupportedException">The image has no projection to <paramref name="format"/> (float samples, an
    /// HDR transfer; see <see cref="Decode(ReadOnlySpan{byte}, PixelFormat, DecodeOptions?, CancellationToken)"/>).</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read, or
    /// <see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IImageRowReader OpenRowReader(Stream stream, PixelFormat format, DecodeOptions? options = null,
        bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        var input = OpenInput(stream, leaveOpen);
        StreamingRowReader? reader = null;
        try
        {
            input.Fill(SniffLength);
            if (FtypLength(input.Buffered) is var ftyp and > SniffLength)
                input.Fill(ftyp);
            reader = CreateRowReader(input, format, options);
            reader.Initialize(cancellationToken);
            return reader;
        }
        catch
        {
            if (reader is not null) reader.Dispose();
            else input.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <see cref="OpenRowReader"/>, reading the stream asynchronously only: for a stream that forbids synchronous reads (an
    /// ASP.NET Core request body). Read the rows with <see cref="IImageRowReader.ReadRowsAsync"/>.
    /// </summary>
    /// <inheritdoc cref="OpenRowReader" path="/param"/>
    /// <inheritdoc cref="OpenRowReader" path="/exception"/>
    public static async ValueTask<IImageRowReader> OpenRowReaderAsync(Stream stream, PixelFormat format,
        DecodeOptions? options = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        var input = OpenInput(stream, leaveOpen);
        StreamingRowReader? reader = null;
        try
        {
            await input.FillAsync(SniffLength, cancellationToken).ConfigureAwait(false);
            if (FtypLength(input.Buffered) is var ftyp and > SniffLength)
                await input.FillAsync(ftyp, cancellationToken).ConfigureAwait(false);
            reader = CreateRowReader(input, format, options);
            await reader.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return reader;
        }
        catch
        {
            if (reader is not null) await reader.DisposeAsync().ConfigureAwait(false);
            else input.Dispose();
            throw;
        }
    }

    private static StreamInput OpenInput(Stream stream, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new StreamInput(stream, leaveOpen);
    }

    // The bytes to sniff: SniffLength, or a leading ftyp box whole (capped at MaxSniffLength).
    private static int FtypLength(ReadOnlySpan<byte> header) =>
        header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8)
            ? (int)Math.Clamp(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header), SniffLength, MaxSniffLength)
            : SniffLength;

    // The reader for the codec that claims the sniffed bytes: its streaming decoder, or the whole-input fallback.
    private static StreamingRowReader CreateRowReader(StreamInput input, PixelFormat format, DecodeOptions? options)
    {
        var buffered = input.Buffered;
        var header = buffered[..Math.Min(buffered.Length, FtypLength(buffered))];
        var entry = Find(header) ?? throw new InvalidDataException(NoCodecMessage(header));
        if (entry.CreateRowReader is { } create)
            return create(input, format, options);
        return new BufferedRowReader(input, format, options,
            (ReadOnlySpan<byte> data, out ImageInfo info) => ReadInfoOrThrow(entry, data, out info),
            (data, destination, stride, pixelFormat, decodeOptions, cancellationToken) =>
                DecodeIntoOrThrow(entry, data, destination, stride, pixelFormat, decodeOptions, cancellationToken));
    }

    // The buffered reader's header read, failing as Decode does: with the codec's reason when it has one.
    private static bool ReadInfoOrThrow(ImageDecoderRegistration entry, ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (entry.TryReadInfo(data, out info))
            return true;
        throw DecodeFailure(entry, data, null, CancellationToken.None, $"The {FormatName(entry.Format)} header cannot be read.")!;
    }

    // The buffered reader's decode, failing as Decode(data, format) does: a corrupt file with the codec's reason, an image
    // without an integer projection with NotSupportedException.
    private static bool DecodeIntoOrThrow(ImageDecoderRegistration entry, ReadOnlySpan<byte> data, Span<byte> destination,
        int stride, PixelFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        if (entry.TryDecodeInto(data, destination, stride, format, options, cancellationToken))
            return true;
        if (DecodeFailure(entry, data, options, cancellationToken, null) is { } failure)
            throw failure;
        throw entry.TryReadInfo(data, out var info)
            ? NoProjection(entry.Format, info, format)
            : new InvalidDataException($"The {FormatName(entry.Format)} file cannot be decoded.");
    }

    /// <summary>
    /// Opens an encoding session for <paramref name="format"/> writing to <paramref name="output"/>: the session of
    /// <see cref="CreateEncoder(ImageFormat, System.Buffers.IBufferWriter{byte}, EncodeOptions?)"/>, its bytes written to
    /// the stream about every 64 KiB as the encoder produces them. An encoder that encodes rows as they come
    /// (<see cref="StreamEncoderSession.IsStreaming"/> true) so writes the file while its rows are still arriving, holding
    /// a band of rows and about a chunk of output; one that needs the whole image (palette quantisation, WebP lossless)
    /// writes it at <see cref="StreamEncoderSession.Commit"/>. The bytes are those the buffer-writer session writes. The
    /// synchronous members write a chunk as soon as it fills; the asynchronous ones hold what one call produces (the whole
    /// file, for an encoder that is not streaming) until the call ends, then write it.
    /// <para>
    /// <see cref="StreamFrameEncoder.WriteRows"/> and <see cref="StreamEncoderSession.Commit"/> write the stream
    /// synchronously; <see cref="StreamFrameEncoder.WriteRowsAsync"/> and <see cref="StreamEncoderSession.CommitAsync"/>
    /// only asynchronously (an ASP.NET Core response body). Commit flushes the stream. Disposing a session that was not
    /// committed leaves the bytes already written in the stream: an incomplete file.
    /// </para>
    /// </summary>
    /// <param name="format">The format to write.</param>
    /// <param name="output">Where the file is written, from its current position.</param>
    /// <param name="options">The format's options; null for its defaults.</param>
    /// <param name="leaveOpen">Keep <paramref name="output"/> open when the session is disposed (or when this throws for
    /// the options).</param>
    /// <exception cref="NotSupportedException">No registered encoder writes <paramref name="format"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> cannot be written, or <paramref name="options"/> is
    /// not null and not an options type <paramref name="format"/>'s encoder takes.</exception>
    public static StreamEncoderSession CreateEncoder(ImageFormat format, Stream output, EncodeOptions? options = null,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("The stream cannot be written.", nameof(output));
        var encoder = RequireEncoder(format);
        var writer = new StreamBufferWriter(output);
        try
        {
            return new StreamEncoderSession(encoder.Create(writer, options), writer, output, leaveOpen);
        }
        catch
        {
            writer.Dispose();
            if (!leaveOpen)
                output.Dispose();
            throw;
        }
    }
}
