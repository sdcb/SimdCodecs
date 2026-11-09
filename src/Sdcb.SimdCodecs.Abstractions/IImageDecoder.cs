using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// The contract each Sdcb.SimdCodecs codec package implements so the
/// <c>Sdcb.SimdCodecs</c> facade can sniff a byte stream's format and dispatch
/// to the right decoder.
/// <para>
/// Members are <c>static abstract</c> because the codecs are stateless static
/// decoders (e.g. <c>JpegDecoder</c>, <c>PngReader</c>). The facade wraps each
/// implementor into a delegate registry - static-abstract members are not
/// directly enumerable, and reflection-scanning for implementors is not AOT-safe,
/// so the registry is populated explicitly (<see cref="ImageDecoderRegistration"/>).
/// </para>
/// <para>
/// Errors: undecodable input makes the <c>Try</c> members return false (it never hangs, allocates without bound or
/// throws <see cref="System.IO.InvalidDataException"/> out of them) and <see cref="Decode"/> throw
/// <see cref="System.IO.InvalidDataException"/> saying why; a cancelled token throws
/// <see cref="OperationCanceledException"/>; options a decoder cannot honour throw <see cref="NotSupportedException"/>
/// or <see cref="ArgumentException"/> (see <see cref="DecodeOptions"/>).
/// </para>
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// The number of leading bytes <see cref="CanDecode"/> needs to inspect. The
    /// facade passes at least this many bytes (or the whole stream if shorter).
    /// </summary>
    static abstract int SignatureLength { get; }

    /// <summary>
    /// True if <paramref name="header"/> (at least <see cref="SignatureLength"/>
    /// bytes when available) begins with this format's magic-byte signature.
    /// </summary>
    static abstract bool CanDecode(ReadOnlySpan<byte> header);

    /// <summary>
    /// Reads dimensions + layout from the header without decoding pixels, so a
    /// caller can size a destination for <see cref="TryDecodeInto"/>, together with
    /// the file's bit depth, orientation, frame count, colour encoding and resolution (see <see cref="ImageInfo"/>).
    /// Returns false when the header is truncated or malformed.
    /// </summary>
    static abstract bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info);

    /// <summary>
    /// The size closest to <paramref name="requestedWidth"/> × <paramref name="requestedHeight"/> that this codec can
    /// decode to natively — never smaller than requested in either direction unless the full size is — and the
    /// <see cref="DecodeOptions.ScaleDenominator"/> that produces it. A codec that does not scale natively reports the
    /// stored size and 1. Only native sizes are reported, because only they are cheap: every codec accepts every power
    /// of two as <see cref="DecodeOptions.ScaleDenominator"/>, but one beyond its native scales costs a decode at the
    /// largest native scale and the box filter of <see cref="CodecSupport.BoxDownscaler"/>. False when the header is
    /// unreadable.
    /// </summary>
    static abstract bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator);

    /// <summary>
    /// Fully decodes <paramref name="data"/> into an <see cref="IDecodedImage"/>,
    /// preserving the source bit depth and channel count (the fidelity tier).
    /// Returns false for undecodable input. Dispose the image when done with it (it returns pooled memory).
    /// </summary>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="image">The decoded image.</param>
    /// <param name="options">Scale, region and pool; null for the whole image at full size in new memory.</param>
    /// <param name="cancellationToken">Checked between coarse units of work (rows of blocks, tiles).</param>
    static abstract bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="TryDecode"/> that says why it fails: the same image for the same input, but undecodable input
    /// throws <see cref="System.IO.InvalidDataException"/> whose message names what is wrong (a truncated chunk, an
    /// unsupported variant, an image too large for one buffer) instead of returning false. <see cref="TryDecode"/>
    /// returns false exactly when this throws <see cref="System.IO.InvalidDataException"/>. Untrusted input never
    /// makes it throw anything else (no <see cref="IndexOutOfRangeException"/>, <see cref="OverflowException"/> or
    /// <see cref="OutOfMemoryException"/>). Dispose the image when done with it.
    /// </summary>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="options">Scale, region and pool; null for the whole image at full size in new memory.</param>
    /// <param name="cancellationToken">Checked between coarse units of work (rows of blocks, tiles).</param>
    /// <exception cref="System.IO.InvalidDataException">The data is malformed, or uses a variant this decoder does
    /// not decode.</exception>
    /// <exception cref="ArgumentException"><see cref="DecodeOptions.Region"/> does not lie inside the image.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    static abstract IDecodedImage Decode(ReadOnlySpan<byte> data, DecodeOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes into a caller-provided destination in <paramref name="format"/>
    /// — the display hot path, straight into a UI surface, an OpenCV <c>Mat</c> or
    /// an OCR input. Row <c>y</c> starts at byte <c>y × stride</c>; bytes between
    /// rows are left untouched. Size the destination from <see cref="TryReadInfo"/> and
    /// <see cref="DecodeOptions.GetOutputSize(in ImageInfo)"/> (<see cref="PixelConverter.TryGetLayout"/> states the requirement).
    /// <para>
    /// The pixels are exactly those of the image <see cref="TryDecode"/> returns (with the same options), converted to
    /// <paramref name="format"/> by <see cref="PixelConverter"/> from its <see cref="IDecodedImage.Pixels"/>. A codec
    /// with a native path for a layout (JPEG writing BGR from its colour conversion, PNG expanding a row at a time)
    /// produces those bytes without the intermediate full-frame image. The one exception is
    /// <see cref="ChannelLayout.Gray"/> from a codec that stores luma: it returns the stored luma (a YCbCr JPEG's Y
    /// plane), which may differ by a code value or two from the luma of the RGB. A codec may also convert colour
    /// straight from coded samples deeper than its 8-bit output (HEIF Main 10 to an 8-bit format), rounding once
    /// instead of narrowing its 16-bit image; it then states so, and differs by at most one code value.
    /// </para>
    /// </summary>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="destination">The pixels, <c>stride × (height − 1) + width × format.BytesPerPixel</c> bytes at least.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="format">The pixel layout to write.</param>
    /// <param name="options">Scale and region; null for the whole image at full size. The pool is ignored.</param>
    /// <param name="cancellationToken">Checked between coarse units of work.</param>
    /// <returns>False when the destination does not fit, the payload is undecodable,
    /// or the image has no integer projection (float samples; a codec may also refuse
    /// HDR-encoded integer samples, as PNG does for cICP PQ/HLG).</returns>
    static abstract bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the file's metadata blocks (Exif, XMP, ICC) without decoding pixels: what a transcoder hands to an
    /// encoder. An image without any reads as <see cref="ImageMetadata.Empty"/>. False when the file is malformed.
    /// </summary>
    static abstract bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata);

    /// <summary>
    /// Decodes the reduced-size preview the file embeds (an Exif IFD1 JPEG, a HEIF <c>thmb</c> item, a TIFF
    /// reduced-resolution subfile), when <see cref="ImageInfo.HasThumbnail"/> says there is one. False otherwise.
    /// </summary>
    static abstract bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail);
}
