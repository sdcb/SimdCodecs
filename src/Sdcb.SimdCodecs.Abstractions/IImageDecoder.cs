using System;
using System.Diagnostics.CodeAnalysis;

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
/// so the registry is populated explicitly.
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
    /// the file's bit depth, EXIF orientation and frame count (see <see cref="ImageInfo"/>).
    /// Returns false when the header is truncated or malformed.
    /// </summary>
    static abstract bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info);

    /// <summary>
    /// Fully decodes <paramref name="data"/> into an <see cref="IDecodedImage"/>,
    /// preserving the source bit depth and channel count (the fidelity tier).
    /// Returns false for undecodable input.
    /// </summary>
    static abstract bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image);

    /// <summary>
    /// Decodes into a caller-provided 8-bit destination in <paramref name="format"/>
    /// — the display hot path, straight into a UI surface, an OpenCV <c>Mat</c> or
    /// an OCR input. Row <c>y</c> starts at byte <c>y × stride</c>; bytes between
    /// rows are left untouched. Size the destination from <see cref="TryReadInfo"/>
    /// (<see cref="PixelConverter.TryGetLayout"/> states the requirement).
    /// <para>
    /// The pixels are exactly <see cref="IDecodedImage.ToRgba8"/> of the image
    /// <see cref="TryDecode"/> returns, converted to <paramref name="format"/> by
    /// <see cref="PixelConverter.ConvertRow"/> (which, for every source, is the same
    /// as converting <see cref="IDecodedImage.Pixels"/> directly). A codec with a
    /// native path for a layout (JPEG writing BGR from its colour conversion, PNG
    /// expanding a row at a time) produces those bytes without the intermediate
    /// full-frame image. The one exception is <see cref="ImagePixelFormat.Gray8"/>
    /// from a codec that stores luma: it returns the stored luma (a YCbCr JPEG's Y
    /// plane), which may differ by a code value or two from the luma of the RGB.
    /// </para>
    /// </summary>
    /// <param name="data">The complete encoded image.</param>
    /// <param name="destination">The pixels, <c>stride × (height − 1) + width × bytesPerPixel</c> bytes at least.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <param name="format">The pixel layout to write.</param>
    /// <returns>False when the destination does not fit, the payload is undecodable,
    /// or the image has no 8-bit projection (float samples; a codec may also refuse
    /// HDR-encoded integer samples, as PNG does for cICP PQ/HLG).</returns>
    static abstract bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format);
}
