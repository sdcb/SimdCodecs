using System;
using System.Buffers;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// Options every encoder takes; each format derives its own (quality, compression level, lossless or lossy, …). A
/// session given none uses its format's defaults; one given another format's options throws
/// <see cref="ArgumentException"/>.
/// </summary>
public abstract record EncodeOptions;

/// <summary>
/// The contract each codec package with an encoder implements so the <c>Sdcb.SimdCodecs</c> facade can encode to
/// it — as for <see cref="IImageDecoder"/>, through <c>static abstract</c> members bound by method group
/// (<see cref="ImageEncoderRegistration"/>), never inside a generic method (Mono AOT).
/// <para>
/// Encoding is a session, after WIC's frame encoder: <see cref="Create"/> opens one over the output, each
/// <see cref="IImageEncoderSession.AddFrame"/> starts a frame, its rows are pushed with
/// <see cref="IImageFrameEncoder.WriteRows"/>, and <see cref="IImageEncoderSession.Commit"/> finishes the file. An
/// encoder that needs the whole image (palette quantisation, WebP lossless's global transforms) buffers it inside the
/// session; the contract is the same, only without the memory saving.
/// </para>
/// <para>
/// Input: every encoder accepts every <see cref="PixelFormat"/> at any stride. <see cref="GetClosestInputFormat"/> names
/// the format it stores without loss (8-bit for an 8-bit-only format: <c>Rgba64</c> → <c>Rgba32</c>), so a caller can see
/// a precision loss before making it; a format it is given anyway is converted by <see cref="PixelConverter"/> (16 → 8 bits
/// keeps the high byte, premultiplied colour is unpremultiplied, a missing alpha is opaque, a dropped alpha is discarded,
/// never composited). Lossless encoders produce exactly the bytes of converting first and encoding that.
/// </para>
/// </summary>
public interface IImageEncoder
{
    /// <summary>The format this encoder writes.</summary>
    static abstract ImageFormat Format { get; }

    /// <summary>
    /// The format closest to <paramref name="requested"/> that this encoder stores without converting: the format itself
    /// when it can, otherwise the one it will convert to (e.g. JPEG turns <c>Rgba64</c> into <c>Rgba32</c>).
    /// </summary>
    static abstract PixelFormat GetClosestInputFormat(PixelFormat requested);

    /// <summary>Opens an encoding session writing to <paramref name="output"/>.</summary>
    /// <param name="output">Where the file is written.</param>
    /// <param name="options">The format's options (its own <see cref="EncodeOptions"/> type); null for its defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="options"/> is not null and not a type this format takes (the
    /// message names the type received and the types accepted).</exception>
    static abstract IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null);
}

/// <summary>One file being encoded: add its frames, then <see cref="Commit"/>.</summary>
public interface IImageEncoderSession : IDisposable
{
    /// <summary>The most frames the format holds: 1 for a still-only format.</summary>
    int MaxFrames { get; }

    /// <summary>
    /// Starts frame <c>n</c>: a <paramref name="width"/> × <paramref name="height"/> image whose rows will arrive in
    /// <paramref name="format"/>. The previous frame must be complete.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">The layout of the rows <see cref="IImageFrameEncoder.WriteRows"/> will be given.</param>
    /// <param name="color">How the samples encode colour, written where the format can say it (PNG cICP, …); null for sRGB.</param>
    /// <param name="metadata">Metadata blocks to write where the format keeps them (Exif, XMP, ICC); null for none.</param>
    /// <param name="duration">How long an animation shows the frame; ignored by still formats.</param>
    /// <exception cref="InvalidOperationException">The previous frame is incomplete, the session is committed, or the
    /// format holds no more frames.</exception>
    /// <exception cref="ArgumentException">The size is not positive or exceeds the format's limits.</exception>
    IImageFrameEncoder AddFrame(int width, int height, PixelFormat format, ColorEncoding? color = null,
        ImageMetadata? metadata = null, TimeSpan? duration = null);

    /// <summary>Finishes the file: writes what the frames left buffered, and the trailer.</summary>
    /// <exception cref="InvalidOperationException">No frame was added, or the last one is incomplete.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    void Commit(CancellationToken cancellationToken = default);
}

/// <summary>The rows of one frame being encoded, pushed top to bottom.</summary>
public interface IImageFrameEncoder
{
    /// <summary>Rows written so far.</summary>
    int RowsWritten { get; }

    /// <summary>
    /// Writes the next <paramref name="rowCount"/> rows, <paramref name="stride"/> bytes apart (0: tightly packed), in
    /// the frame's format.
    /// </summary>
    /// <exception cref="ArgumentException">The rows run past the frame or <paramref name="rows"/> is too short.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    void WriteRows(ReadOnlySpan<byte> rows, int rowCount, int stride = 0, CancellationToken cancellationToken = default);
}

/// <summary>
/// A codec's entry in the facade's encoder registry: its <see cref="IImageEncoder"/> members as delegates. Build it with
/// an object initializer over the concrete type's method groups, never inside a generic method (Mono AOT; see
/// <see cref="ImageDecoderRegistration"/>).
/// </summary>
public sealed class ImageEncoderRegistration
{
    /// <summary><see cref="IImageEncoder.GetClosestInputFormat"/>.</summary>
    public delegate PixelFormat GetClosestInputFormatHandler(PixelFormat requested);

    /// <summary><see cref="IImageEncoder.Create"/>.</summary>
    public delegate IImageEncoderSession CreateHandler(IBufferWriter<byte> output, EncodeOptions? options);

    /// <summary>The format the encoder writes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Set to <see cref="ImageFormat.Unknown"/> or an undefined value.</exception>
    public required ImageFormat Format
    {
        get;
        init
        {
            if (value == ImageFormat.Unknown || !Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "A registration names a defined, known format.");
            field = value;
        }
    }

    /// <summary>The encoder's input-format negotiation.</summary>
    public required GetClosestInputFormatHandler GetClosestInputFormat
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>The encoder's session factory.</summary>
    public required CreateHandler Create
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }
}
