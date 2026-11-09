using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Gif;
using Sdcb.SimdCodecs.Ico;
using Sdcb.SimdCodecs.Pnm;
using Sdcb.SimdCodecs.Qoi;
using Sdcb.SimdCodecs.Jbig2;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Jpeg2000;
using Sdcb.SimdCodecs.Png;
using Sdcb.SimdCodecs.Tiff;
using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs;

/// <summary>
/// The image-codec facade: sniff a byte stream's format by its leading magic-byte
/// signature and dispatch to the matching Sdcb.SimdCodecs codec. One reference to this
/// package pulls the whole codec family in lockstep; consumers call these entry
/// points instead of hard-wiring per-format decoders.
/// <para>
/// Registers PNG, JPEG, TIFF, JBIG2, JPEG 2000, WebP, GIF, BMP, ICO, CUR, QOI, Netpbm (PBM, PGM, PPM, PAM) and PFM. To add a
/// format, implement <see cref="IImageDecoder"/> in its codec package and add a
/// line binding its members to the built-in registry - the order is the sniff order.
/// </para>
/// <para>
/// HEIF / HEIC is not built in: HEVC is patent-encumbered, so <c>Sdcb.SimdCodecs.Heif</c> is a separate
/// package that this one does not reference, and it conveys no patent licence. A consumer that installs it
/// enables it explicitly with <see cref="Register"/>(<c>HeifImageDecoder.Registration</c>); until then
/// <see cref="DetectFormat"/> recognises HEIF but <see cref="IsDecodable"/> is false for it. Registered codecs
/// are sniffed after the built-in ones.
/// </para>
/// <para>
/// JBIG2 is registered for standalone <c>.jb2</c> files only. Its real use -
/// PDF's <c>/JBIG2Decode</c> filter - cannot come through here at all: an
/// embedded JBIG2 stream has no file header to sniff, keeps its segment
/// dictionaries in a separate <c>/JBIG2Globals</c> stream, and takes its
/// dimensions from the image dictionary. Those callers use
/// <c>Jbig2Decoder.Decode(embedded, globals, width, height)</c> directly.
/// </para>
/// <para>
/// JPEG 2000 is registered for the images whose samples are grey or RGB once
/// decoded: one or three components, not sYCC. Its JP2 and raw-J2K signatures
/// collide with nothing above. What the facade cannot present (alpha, CMYK,
/// sYCC) is refused rather than guessed, and <c>Jpeg2000Decoder.Decode</c>
/// still hands back every component, with the JP2 colour box reported.
/// </para>
/// <para>
/// WebP is decoded by <see cref="WebpImageDecoder"/>: lossy (VP8), lossless (VP8L)
/// and, for an animation, its first composited frame. It returns RGB8 or RGBA8
/// according to the container's alpha declaration. Every frame is available through
/// <see cref="TryOpenFrames"/>, which also reads a multi-page TIFF's pages.
/// </para>
/// <para>
/// Float-sample content (e.g. float TIFF) decodes through
/// <see cref="TryDecode"/> with <see cref="IDecodedImage.ColorEncoding"/>
/// declaring its meaning; <see cref="TryDecodeInto"/> serves the
/// integer-sample path only (8- and 16-bit <see cref="PixelFormat"/>s) - projecting HDR float to integers is a
/// consumer tone/stretch decision, not a codec one. For viewing,
/// <see cref="PixelConverter.StretchToRgba8(IDecodedImage, bool)"/> stretches the colour range linearly onto 0..255.
/// </para>
/// <para>
/// A gain-map ("Ultra HDR") JPEG is recognised too: <see cref="TryDecode"/> +
/// <see cref="IDecodedImage.ToFloats"/> reconstruct the authored HDR (linear,
/// display-referred), while <see cref="TryDecodeInto"/> and
/// <see cref="IDecodedImage.ToRgba8"/> return the SDR base rendition - so the
/// float path is HDR and the 8-bit path is the graceful SDR fallback. Plain JPEGs
/// are unaffected.
/// </para>
/// </summary>
public static partial class ImageCodecs
{
    // Explicit registry: IImageDecoder's static-abstract members are not enumerable
    // and reflection-scanning for implementors is not AOT-safe, so each codec is
    // registered by hand, through its Registration property. Each codec builds that from
    // its concrete type's method groups, never inside a generic method
    // (Register<T>() => new(T.CanDecode, ...)): Mono AOT
    // (browser-wasm, iOS, Android) compiles a delegate over a static abstract member
    // of a type parameter into a call that traps with "function signature mismatch".
    // tests/Sdcb.SimdCodecs.WasmSmoke runs every entry point under Mono AOT.
    // A null TryOpenFrames marks a still-only codec: the facade presents its image as one frame.
    //
    // Register appends copy-on-write: a published array is never mutated, so a reader
    // iterates whichever snapshot it read, without a lock.
    private static ImageDecoderRegistration[] s_registry =
    [
        PngImageDecoder.Registration,
        // GainMap must precede the plain JPEG decoder: both answer the FF D8 FF
        // signature, but GainMapImageDecoder.CanDecode only claims a JPEG that
        // actually carries a locatable gain map, so ordinary JPEGs fall through.
        // It makes ToFloats() reconstruct the authored HDR while the 8-bit path
        // keeps returning the SDR base.
        GainMapImageDecoder.Registration,
        JpegImageDecoder.Registration,
        TiffImageDecoder.Registration,
        Jbig2ImageDecoder.Registration,
        Jpeg2000ImageDecoder.Registration,
        WebpImageDecoder.Registration,
        GifImageDecoder.Registration,
        BmpImageDecoder.Registration,
        IcoImageDecoder.Registration,
        CurImageDecoder.Registration,
        QoiImageDecoder.Registration,
        // Netpbm signatures are two ASCII bytes and a space: sniffed last, after every binary magic.
        PnmImageDecoder.Registration,
        PfmImageDecoder.Registration,
    ];

    private static readonly Lock s_registerLock = new();

    /// <summary>The registry as it stands: the built-in codecs, then registered ones in registration order.</summary>
    private static ImageDecoderRegistration[] Registry => Volatile.Read(ref s_registry);

    /// <summary>Codecs in the registry, built-in ones included (for tests).</summary>
    internal static int RegisteredCount => Registry.Length;

    /// <summary>The registry as it stands (for tests, to put it back with <see cref="ExchangeRegistry"/>).</summary>
    internal static ImageDecoderRegistration[] RegistrySnapshot => Registry;

    /// <summary>Replaces the registry and returns the one it replaced: lets a test register a codec and put the
    /// process back as it found it. Not part of the API — registration is permanent for a consumer.</summary>
    internal static ImageDecoderRegistration[] ExchangeRegistry(ImageDecoderRegistration[] registry)
    {
        lock (s_registerLock)
        {
            var previous = s_registry;
            Volatile.Write(ref s_registry, registry);
            return previous;
        }
    }

    /// <summary>
    /// Enables a codec this package does not reference — today <c>Sdcb.SimdCodecs.Heif</c>, kept out of the
    /// meta-package because HEVC is patent-encumbered: install it, then call
    /// <c>ImageCodecs.Register(HeifImageDecoder.Registration)</c> once at start-up. Every entry point —
    /// <see cref="CanDecode"/>, <see cref="DetectFormat"/>, <see cref="IsDecodable"/>, <see cref="TryReadInfo"/>,
    /// <see cref="Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/>, <see cref="TryDecode"/>,
    /// <see cref="TryDecodeInto"/>, <see cref="TryOpenFrames"/> — then dispatches to it.
    /// <para>
    /// A registered codec is sniffed after every built-in one, in registration order, so it never takes a
    /// stream a built-in codec claims. Registering the same codec again (the same format, and delegates over
    /// the same methods) does nothing. Safe to call while other threads decode: each call sees the registry
    /// from before or after the codec was added. A registration cannot be removed.
    /// </para>
    /// </summary>
    /// <param name="registration">The codec's entry, e.g. <c>HeifImageDecoder.Registration</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registration"/> is null.</exception>
    /// <exception cref="ArgumentException">Only one of the registration's two planar delegates is set.</exception>
    public static void Register(ImageDecoderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Validate();
        lock (s_registerLock)
        {
            var current = s_registry;
            foreach (var entry in current)
            {
                if (IsSameCodec(entry, registration))
                    return;
            }

            Volatile.Write(ref s_registry, [.. current, registration]);
        }
    }

    // Delegates over the same method compare equal, so two registrations built from one codec's
    // method groups are the same codec even when they are different objects.
    private static bool IsSameCodec(ImageDecoderRegistration a, ImageDecoderRegistration b) =>
        ReferenceEquals(a, b)
        || (a.Format == b.Format && a.CanDecode.Equals(b.CanDecode) && a.TryReadInfo.Equals(b.TryReadInfo)
            && a.TryGetClosestSize.Equals(b.TryGetClosestSize)
            && a.TryDecode.Equals(b.TryDecode) && Equals(a.Decode, b.Decode) && a.TryDecodeInto.Equals(b.TryDecodeInto)
            && a.TryReadMetadata.Equals(b.TryReadMetadata) && a.TryDecodeThumbnail.Equals(b.TryDecodeThumbnail)
            && Equals(a.TryOpenFrames, b.TryOpenFrames) && Equals(a.TryReadPlanarInfo, b.TryReadPlanarInfo)
            && Equals(a.TryDecodePlanes, b.TryDecodePlanes));

    /// <summary>
    /// Opens a reader over every frame of <paramref name="data"/> — an animated WebP's frames, a multi-page
    /// TIFF's pages — to decode them one at a time by index (see <see cref="IImageFrameReader"/>). Any
    /// other image opens as a reader of one frame, its still image, so a caller handles every format the
    /// same way. The reader keeps <paramref name="data"/>; dispose it when done. Returns false when no
    /// registered codec recognises the stream or the file is malformed.
    /// </summary>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        foreach (var entry in Registry)
        {
            if (!entry.CanDecode(data.Span))
                continue;
            if (entry.TryOpenFrames is { } open)
                return open(data, out reader);
            if (!entry.TryReadInfo(data.Span, out var info))
                break;
            reader = new StillFrameReader(data, info with { FrameCount = 1 }, entry);
            return true;
        }

        reader = null;
        return false;
    }

    /// <summary>A still image as a reader of one frame, decoded through its codec's entry.</summary>
    private sealed class StillFrameReader(ReadOnlyMemory<byte> data, ImageInfo info, ImageDecoderRegistration entry) : IImageFrameReader
    {
        private bool _disposed;

        public int FrameCount => 1;

        public int? LoopCount => null;

        public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
        {
            Check(index);
            frame = new ImageFrameInfo(info, null);
            return true;
        }

        public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image,
            DecodeOptions? options = null, CancellationToken cancellationToken = default)
        {
            Check(index);
            return entry.TryDecode(data.Span, out image, options, cancellationToken);
        }

        public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, PixelFormat format,
            DecodeOptions? options = null, CancellationToken cancellationToken = default)
        {
            Check(index);
            return entry.TryDecodeInto(data.Span, destination, stride, format, options, cancellationToken);
        }

        public void Dispose() => _disposed = true;

        private void Check(int index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 1);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>The registry entry that claims <paramref name="data"/>, or null.</summary>
    internal static ImageDecoderRegistration? Find(ReadOnlySpan<byte> data)
    {
        foreach (var entry in Registry)
        {
            if (entry.CanDecode(data))
                return entry;
        }
        return null;
    }

    /// <summary>
    /// Decodes <paramref name="data"/> into a codec-neutral <see cref="IDecodedImage"/> (fidelity tier: the source's
    /// bit depth and channel count, as <see cref="TryDecode"/>), and says why when it cannot: the exception names the
    /// format and the codec's reason, for a caller that reports it ("file corrupt: the zlib stream ends early"). Dispose
    /// the image when done with it (it returns pooled memory, see <see cref="DecodeOptions.Pool"/>).
    /// </summary>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="options">Scale, region and pool; null for the whole image at full size.</param>
    /// <param name="cancellationToken">Checked between coarse units of work.</param>
    /// <exception cref="NotSupportedException">No registered codec decodes the data: it is not a recognised image
    /// format, or one the facade recognises but does not decode (AVIF, JPEG XL, …, and HEIF until
    /// <c>Sdcb.SimdCodecs.Heif</c> is registered with <see cref="Register"/>). The message names
    /// <see cref="DetectFormat"/>'s answer.</exception>
    /// <exception cref="System.IO.InvalidDataException">The codec cannot decode the file: it is malformed or truncated,
    /// or uses a variant the codec does not decode. The message starts with the format; the codec's own exception is
    /// the <see cref="Exception.InnerException"/>.</exception>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IDecodedImage Decode(ReadOnlySpan<byte> data, DecodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(data) ?? throw new NotSupportedException(NoCodecMessage(data));
        if (entry.Decode is not { } decode)
        {
            // A registration from before IImageDecoder.Decode: decode through TryDecode, without a reason.
            return entry.TryDecode(data, out var image, options, cancellationToken)
                ? image
                : throw new System.IO.InvalidDataException($"The {FormatName(entry.Format)} file cannot be decoded.");
        }
        try
        {
            return decode(data, options, cancellationToken);
        }
        catch (System.IO.InvalidDataException e)
        {
            throw Wrap(entry.Format, e);
        }
    }

    /// <summary>
    /// Decodes <paramref name="data"/> straight to <paramref name="format"/> in one call: the image (scaled and cropped
    /// as <paramref name="options"/> say) as tightly packed pixels in that layout — <see cref="PixelFormat.Rgba32"/> for a
    /// browser canvas or an ML tensor, <see cref="PixelFormat.Bgr24"/> for an OpenCV <c>Mat</c>,
    /// <see cref="PixelFormat.Gray8"/> for OCR — with the file's <see cref="PixelBuffer.Metadata"/> and
    /// <see cref="PixelBuffer.ColorEncoding"/>. It is <see cref="TryReadInfo"/>, a buffer of
    /// <see cref="DecodeOptions.GetOutputSize"/> (rented from <see cref="DecodeOptions.Pool"/> when set), and
    /// <see cref="TryDecodeInto"/>, so the pixels are exactly that path's and a codec's native writer (JPEG's colour
    /// conversion, PNG's row expansion) fills the buffer without an intermediate image. Dispose the result when done with
    /// it.
    /// <para>
    /// When the codec's fidelity tier is float but it has an integer projection (a gain-map JPEG: HDR floats, SDR base
    /// pixels), the pixels are that projection and <see cref="PixelBuffer.ColorEncoding"/> describes it: sRGB with the
    /// image's primaries.
    /// </para>
    /// </summary>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="format">The layout of the result.</param>
    /// <param name="options">Scale, region and pool; null for the whole image at full size in new memory.</param>
    /// <param name="cancellationToken">Checked between coarse units of work.</param>
    /// <exception cref="NotSupportedException">No registered codec decodes the data (see
    /// <see cref="Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/>); or the image has no projection to
    /// <paramref name="format"/>: float samples (a float TIFF, a PFM), which have no integer form without a tone policy
    /// (decode it with <see cref="Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/> and use
    /// <see cref="IDecodedImage.ToFloats"/> or <see cref="PixelConverter.StretchToRgba8(IDecodedImage, bool)"/>), or an
    /// HDR transfer (PQ, HLG) that a codec refuses to show as display pixels.</exception>
    /// <exception cref="System.IO.InvalidDataException">The codec cannot decode the file (see
    /// <see cref="Decode(ReadOnlySpan{byte}, DecodeOptions?, CancellationToken)"/>), or its pixels in
    /// <paramref name="format"/> need more than <see cref="Array.MaxLength"/> bytes.</exception>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static PixelBuffer Decode(ReadOnlySpan<byte> data, PixelFormat format, DecodeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var entry = Find(data) ?? throw new NotSupportedException(NoCodecMessage(data));
        if (!entry.TryReadInfo(data, out var info))
            throw DecodeFailure(entry, data, options, cancellationToken, $"The {FormatName(entry.Format)} header cannot be read.")!;
        options?.Validate(info.Width, info.Height);
        var (width, height) = (options ?? DecodeOptions.Default).GetOutputSize(info.Width, info.Height);
        var bytes = (long)width * height * format.BytesPerPixel;
        if (bytes > Array.MaxLength)
        {
            throw new System.IO.InvalidDataException(
                $"The {FormatName(entry.Format)} image is {width}x{height}: as {format} its pixels take {bytes} bytes, more than " +
                $"the {Array.MaxLength} bytes one buffer holds. Decode it at a reduced scale (DecodeOptions.ScaleDenominator) " +
                "or a region of it (DecodeOptions.Region).");
        }

        // Metadata is a header walk (no pixels), cheap beside the decode; the codec does the same walk for TryDecode.
        var metadata = entry.TryReadMetadata(data, out var blocks) ? blocks : ImageMetadata.Empty;
        var buffer = PixelBuffer.Create(width, height, format, options?.Pool, ProjectionEncoding(info), metadata);
        try
        {
            if (entry.TryDecodeInto(data, buffer.WritablePixels, 0, format, options, cancellationToken))
                return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        buffer.Dispose();

        // The buffer fits, so the codec either cannot decode the file (its Decode says why, and a corrupt file is
        // reported as one whatever it holds) or has no projection of it to the format.
        if (DecodeFailure(entry, data, options, cancellationToken, null) is { } failure)
            throw failure;
        var name = FormatName(entry.Format);
        if (info.SampleFormat == SampleFormat.Float32)
        {
            throw new NotSupportedException(
                $"The {name} image has float samples, which have no {format} form without a tone policy: decode it with " +
                "ImageCodecs.Decode(data) and use IDecodedImage.ToFloats or PixelConverter.StretchToRgba8.");
        }
        throw new NotSupportedException(
            $"The {name} image decodes, but its codec has no {format} projection of it (an HDR transfer such as PQ " +
            "or HLG has no display pixels without tone mapping): decode it with ImageCodecs.Decode(data) instead.");
    }

    // The colour encoding of TryDecodeInto's pixels: the image's own, unless its fidelity tier is float and the codec
    // still projects integers (a gain-map JPEG's SDR base), which are display pixels in the image's primaries.
    private static ColorEncoding ProjectionEncoding(ImageInfo info) =>
        info.SampleFormat != SampleFormat.Float32 ? info.ColorEncoding
        : info.ColorEncoding.Primaries is ColorPrimaries.DisplayP3 or ColorPrimaries.BT2020
            ? ColorEncoding.AssumedSrgb with { Primaries = info.ColorEncoding.Primaries }
            : ColorEncoding.AssumedSrgb;

    /// <summary>
    /// The exception that says why <paramref name="entry"/> cannot decode <paramref name="data"/>, found by running
    /// its <see cref="ImageDecoderRegistration.Decode"/>: that exception, its message prefixed with the format. When
    /// the decode succeeds (or the registration has none), an <see cref="System.IO.InvalidDataException"/> with
    /// <paramref name="fallback"/>, or null when that is null. Only failure paths call it, so the extra decode costs a
    /// successful one nothing.
    /// </summary>
    internal static System.IO.InvalidDataException? DecodeFailure(ImageDecoderRegistration entry, ReadOnlySpan<byte> data,
        DecodeOptions? options, CancellationToken cancellationToken, string? fallback)
    {
        if (entry.Decode is { } decode)
        {
            try
            {
                // Into new memory: the image is thrown away.
                decode(data, options is null ? null : options with { Pool = null }, cancellationToken).Dispose();
            }
            catch (System.IO.InvalidDataException e)
            {
                return Wrap(entry.Format, e);
            }
        }
        return fallback is null ? null : new System.IO.InvalidDataException(fallback);
    }

    // A codec's InvalidDataException with the format in front: "The PNG file cannot be decoded: <the codec's reason>".
    // A reason the codec already prefixes with the format ("JBIG2: truncated segment header.") loses that prefix.
    private static System.IO.InvalidDataException Wrap(ImageFormat format, System.IO.InvalidDataException e)
    {
        var name = FormatName(format);
        var reason = e.Message.StartsWith(name + ": ", StringComparison.Ordinal) ? e.Message[(name.Length + 2)..] : e.Message;
        return new System.IO.InvalidDataException($"The {name} file cannot be decoded: {reason}", e);
    }

    /// <summary>
    /// Why no registered codec takes <paramref name="data"/>, from <see cref="DetectFormat"/>: not a recognised format,
    /// a format the facade recognises but does not decode, or HEIF, whose package is installed and registered apart.
    /// </summary>
    internal static string NoCodecMessage(ReadOnlySpan<byte> data) => DetectFormat(data) switch
    {
        ImageFormat.Unknown => "The data is not a recognised image format.",
        ImageFormat.Heif =>
            "The data is HEIF, which no registered codec decodes: install the Sdcb.SimdCodecs.Heif package and register it " +
            "once at start-up with ImageCodecs.Register(HeifImageDecoder.Registration).",
        var format when IsDecodable(format) =>
            $"The data looks like {FormatName(format)}, but no registered codec accepts it.",
        var format => $"The data is {FormatName(format)}, which Sdcb.SimdCodecs recognises but does not decode.",
    };

    /// <summary>The name a message gives <paramref name="format"/>: "PNG", "JPEG 2000", "WebP".</summary>
    internal static string FormatName(ImageFormat format) => format switch
    {
        ImageFormat.Png => "PNG",
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Tiff => "TIFF",
        ImageFormat.Jbig2 => "JBIG2",
        ImageFormat.Jpeg2000 => "JPEG 2000",
        ImageFormat.Webp => "WebP",
        ImageFormat.Gif => "GIF",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Ico => "ICO",
        ImageFormat.Cur => "CUR",
        ImageFormat.Heif => "HEIF",
        ImageFormat.Avif => "AVIF",
        ImageFormat.JpegXl => "JPEG XL",
        ImageFormat.JpegXr => "JPEG XR",
        ImageFormat.OpenExr => "OpenEXR",
        ImageFormat.Psd => "PSD",
        ImageFormat.Qoi => "QOI",
        ImageFormat.Pnm => "Netpbm",
        ImageFormat.Pfm => "PFM",
        _ => format.ToString(),
    };

    /// <summary>
    /// Decodes <paramref name="data"/> into a codec-neutral <see cref="IDecodedImage"/>
    /// (fidelity tier - preserves source bit depth and channel count). Returns false
    /// when no registered codec recognises the stream or the payload is undecodable.
    /// Dispose the image when done with it (it returns pooled memory, see <see cref="DecodeOptions.Pool"/>).
    /// </summary>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="image">The decoded image.</param>
    /// <param name="options">Scale, region and pool; null for the whole image at full size.</param>
    /// <param name="cancellationToken">Checked between coarse units of work.</param>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Find(data) is { } entry)
            return entry.TryDecode(data, out image, options, cancellationToken);
        image = null;
        return false;
    }

    /// <summary>
    /// Reads dimensions + layout from the header without a full decode, so a caller
    /// can size a destination for <see cref="TryDecodeInto"/>. Returns false when
    /// no registered codec recognises the stream or the header is malformed.
    /// </summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (Find(data) is { } entry)
            return entry.TryReadInfo(data, out info);
        info = default;
        return false;
    }

    /// <summary>
    /// The size closest to <paramref name="requestedWidth"/> × <paramref name="requestedHeight"/> that the codec decodes
    /// to natively, and the <see cref="DecodeOptions.ScaleDenominator"/> that gives it (see
    /// <see cref="IImageDecoder.TryGetClosestSize"/>): the stored size and 1 for a codec that does not scale. Every codec
    /// accepts every other power of two too, at the cost of a decode at its native size and a box filter. False when
    /// no codec recognises the stream or the header is malformed.
    /// </summary>
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator)
    {
        if (Find(data) is { } entry)
            return entry.TryGetClosestSize(data, requestedWidth, requestedHeight, out width, out height, out scaleDenominator);
        width = height = 0;
        scaleDenominator = 1;
        return false;
    }

    /// <summary>
    /// Decodes directly into a caller-provided destination in <paramref name="format"/>, rows
    /// <paramref name="stride"/> bytes apart (0: tightly packed) — the display hot path, straight into a
    /// UI surface (<see cref="PixelFormat.Bgra32"/>, or <see cref="PixelFormat.Bgra32Premultiplied"/> for WPF,
    /// Direct2D and most compositors), an OpenCV <c>Mat</c> (<see cref="PixelFormat.Bgr24"/>), an OCR input
    /// (<see cref="PixelFormat.Gray8"/>) or a 16-bit pipeline (<see cref="PixelFormat.Rgba64"/>).
    /// Size the destination via <see cref="TryReadInfo"/> (and <see cref="DecodeOptions.GetOutputSize"/> with options)
    /// and <see cref="PixelConverter.TryGetLayout"/>. See <see cref="IImageDecoder.TryDecodeInto"/> for what the
    /// pixels are. Returns false when no codec recognises the stream, the destination does not fit, or the payload is
    /// undecodable.
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        Find(data) is { } entry && entry.TryDecodeInto(data, destination, stride, format, options, cancellationToken);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA (row-major, 4 bytes/pixel, R,G,B,A byte
    /// order). <paramref name="rgbaDestination"/> must be at least <c>Width * Height * 4</c> bytes (size it
    /// via <see cref="TryReadInfo"/>).
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    /// <summary>
    /// Extracts the metadata blocks (Exif, XMP, ICC) of <paramref name="data"/> without decoding pixels — what a
    /// transcoder hands to an encoder. False when no codec recognises the stream or the file is malformed.
    /// </summary>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        if (Find(data) is { } entry)
            return entry.TryReadMetadata(data, out metadata);
        metadata = null;
        return false;
    }

    /// <summary>
    /// Decodes the reduced-size preview <paramref name="data"/> embeds (Exif IFD1, a HEIF <c>thmb</c> item), when
    /// <see cref="ImageInfo.HasThumbnail"/> says there is one. False otherwise.
    /// </summary>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        if (Find(data) is { } entry)
            return entry.TryDecodeThumbnail(data, out thumbnail);
        thumbnail = null;
        return false;
    }

    /// <summary>
    /// Describes the YCbCr planes the codec decodes <paramref name="data"/> to before colour conversion (see
    /// <see cref="IPlanarImageDecoder"/>). False when no codec recognises the stream, the codec has no planar output,
    /// or this image is not available as planes.
    /// </summary>
    public static bool TryReadPlanarInfo(ReadOnlySpan<byte> data, out PlanarInfo info)
    {
        if (Find(data) is { TryReadPlanarInfo: { } read })
            return read(data, out info);
        info = default;
        return false;
    }

    /// <summary>
    /// Decodes the YCbCr planes of <paramref name="data"/> as coded (see <see cref="IPlanarImageDecoder.TryDecodePlanes"/>);
    /// size them from <see cref="TryReadPlanarInfo"/>. False when the image is not available as planes, a plane is too
    /// small or the payload is undecodable.
    /// </summary>
    public static bool TryDecodePlanes(ReadOnlySpan<byte> data, Span<ushort> y, Span<ushort> cb, Span<ushort> cr,
        CancellationToken cancellationToken = default) =>
        Find(data) is { TryDecodePlanes: { } decode } && decode(data, y, cb, cr, cancellationToken);

    /// <summary>
    /// The format of <paramref name="data"/> by its leading signature: one the facade decodes (the
    /// codec that would be dispatched to), or one it recognises but does not decode — AVIF, JPEG XL,
    /// JPEG XR, OpenEXR, PSD, and HEIF until its package is registered (<see cref="Register"/>) — so a
    /// caller can name what it cannot open.
    /// <see cref="ImageFormat.Unknown"/> otherwise. Like <see cref="CanDecode"/>, it reads the
    /// signature only; pass at least the first 64 bytes (an ISO-BMFF <c>ftyp</c> box lists HEIF and
    /// AVIF brands past its start).
    /// </summary>
    public static ImageFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        foreach (var entry in Registry)
        {
            if (entry.CanDecode(data))
                return entry.Format;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0x0A])
            || data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x00, 0x0C, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A]))
            return ImageFormat.JpegXl;
        if (data.StartsWith((ReadOnlySpan<byte>)[(byte)'I', (byte)'I', 0xBC, 0x01])) return ImageFormat.JpegXr;
        if (data.StartsWith((ReadOnlySpan<byte>)[0x76, 0x2F, 0x31, 0x01])) return ImageFormat.OpenExr;
        if (data.StartsWith("8BPS"u8)) return ImageFormat.Psd;
        return DetectIsoBmff(data);
    }

    /// <summary>
    /// True when the facade decodes <paramref name="format"/> — a format some registered codec reports,
    /// as opposed to one <see cref="DetectFormat"/> only recognises. False for
    /// <see cref="ImageFormat.Unknown"/>.
    /// </summary>
    public static bool IsDecodable(ImageFormat format)
    {
        foreach (var entry in Registry)
        {
            if (entry.Format == format)
                return true;
        }

        return false;
    }

    // HEIF and AVIF are ISO-BMFF files that open with an 'ftyp' box: size, 'ftyp', a major brand,
    // a minor version, then compatible brands. An AVIF brand anywhere wins over the HEIF ones,
    // since AVIF files commonly also list the generic 'mif1'.
    private static ImageFormat DetectIsoBmff(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data[4..8].SequenceEqual("ftyp"u8))
            return ImageFormat.Unknown;
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), (uint)data.Length);
        var heif = false;
        for (var at = 8; at + 4 <= size; at += at == 8 ? 8 : 4) // skip the minor version after the major brand
        {
            var brand = data.Slice(at, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return ImageFormat.Avif;
            heif |= brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("heim"u8)
                || brand.SequenceEqual("heis"u8) || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
        }

        return heif ? ImageFormat.Heif : ImageFormat.Unknown;
    }

    /// <summary>
    /// True if any registered codec recognises the stream's leading bytes. Does not
    /// validate the full payload - a positive result only means the signature matched.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> data)
    {
        foreach (var entry in Registry)
        {
            if (entry.CanDecode(data))
                return true;
        }

        return false;
    }
}
