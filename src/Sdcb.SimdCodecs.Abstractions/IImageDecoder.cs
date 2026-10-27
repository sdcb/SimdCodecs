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
    /// caller can size a destination for <see cref="TryDecodeIntoRgba8"/>. Returns
    /// false when the header is truncated or malformed.
    /// </summary>
    static abstract bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info);

    /// <summary>
    /// Fully decodes <paramref name="data"/> into an <see cref="IDecodedImage"/>,
    /// preserving the source bit depth and channel count (the fidelity tier).
    /// Returns false for undecodable input.
    /// </summary>
    static abstract bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image);

    /// <summary>
    /// Decodes directly into a caller-provided 8-bit RGBA destination (row-major,
    /// 4 bytes/pixel, R,G,B,A byte order) with no intermediate full-frame buffer -
    /// the zero-copy display hot path. <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes (size it from
    /// <see cref="TryReadInfo"/>). Returns false when the destination is too small
    /// or the payload is undecodable.
    /// </summary>
    static abstract bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination);
}
