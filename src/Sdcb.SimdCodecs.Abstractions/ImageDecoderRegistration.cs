using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// A codec's entry in the <c>Sdcb.SimdCodecs</c> facade's registry: its <see cref="IImageDecoder"/> members as
/// delegates, plus the <see cref="ImageFormat"/> it decodes. A codec package that the facade does not reference
/// (e.g. <c>Sdcb.SimdCodecs.Heif</c>, kept out for patent reasons) exposes one as a static property, built from
/// its own members, and a consumer enables the codec once, at start-up, with <c>SimdCodecs.Register</c>.
/// <para>
/// Build it from the concrete type's method groups — <c>new(ImageFormat.Heif, HeifImageDecoder.CanDecode, …)</c> —
/// never inside a generic method over <c>T : IImageDecoder</c>: Mono AOT (browser-wasm, iOS, Android) compiles a
/// delegate over a type parameter's static abstract member into a call that traps. That is why there is no
/// generic factory.
/// </para>
/// </summary>
public sealed class ImageDecoderRegistration
{
    /// <summary><see cref="IImageDecoder.CanDecode"/>.</summary>
    public delegate bool CanDecodeHandler(ReadOnlySpan<byte> header);

    /// <summary><see cref="IImageDecoder.TryReadInfo"/>.</summary>
    public delegate bool TryReadInfoHandler(ReadOnlySpan<byte> data, out ImageInfo info);

    /// <summary><see cref="IImageDecoder.TryGetClosestSize"/>.</summary>
    public delegate bool TryGetClosestSizeHandler(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator);

    /// <summary><see cref="IImageDecoder.TryDecode"/>.</summary>
    public delegate bool TryDecodeHandler(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options, CancellationToken cancellationToken);

    /// <summary><see cref="IImageDecoder.TryDecodeInto"/>.</summary>
    public delegate bool TryDecodeIntoHandler(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options, CancellationToken cancellationToken);

    /// <summary><see cref="IImageDecoder.TryReadMetadata"/>.</summary>
    public delegate bool TryReadMetadataHandler(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata);

    /// <summary><see cref="IImageDecoder.TryDecodeThumbnail"/>.</summary>
    public delegate bool TryDecodeThumbnailHandler(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail);

    /// <summary><see cref="IMultiFrameImageDecoder.TryOpenFrames"/>.</summary>
    public delegate bool TryOpenFramesHandler(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader);

    /// <summary><see cref="IPlanarImageDecoder.TryReadPlanarInfo"/>.</summary>
    public delegate bool TryReadPlanarInfoHandler(ReadOnlySpan<byte> data, out PlanarInfo info);

    /// <summary><see cref="IPlanarImageDecoder.TryDecodePlanes"/>.</summary>
    public delegate bool TryDecodePlanesHandler(ReadOnlySpan<byte> data, Span<ushort> y, Span<ushort> cb, Span<ushort> cr,
        CancellationToken cancellationToken);

    /// <summary>Describes a codec for the facade's registry.</summary>
    /// <param name="format">The format the codec decodes (what <c>SimdCodecs.DetectFormat</c> reports for it).</param>
    /// <param name="canDecode">The codec's signature test.</param>
    /// <param name="tryReadInfo">The codec's header read.</param>
    /// <param name="tryGetClosestSize">The codec's native-size negotiation.</param>
    /// <param name="tryDecode">The codec's fidelity decode.</param>
    /// <param name="tryDecodeInto">The codec's decode into a caller's buffer.</param>
    /// <param name="tryReadMetadata">The codec's metadata extraction.</param>
    /// <param name="tryDecodeThumbnail">The codec's embedded-preview decode.</param>
    /// <param name="tryOpenFrames">The codec's frame reader, or null for a still-only codec: the facade then
    /// presents its image as one frame.</param>
    /// <param name="tryReadPlanarInfo">The codec's planar description, or null when it has no planar output.</param>
    /// <param name="tryDecodePlanes">The codec's planar decode; given exactly when <paramref name="tryReadPlanarInfo"/> is.</param>
    /// <exception cref="ArgumentNullException">A required delegate is null.</exception>
    /// <exception cref="ArgumentException">Only one of the two planar delegates is given.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="ImageFormat.Unknown"/>
    /// or not a defined member.</exception>
    public ImageDecoderRegistration(
        ImageFormat format,
        CanDecodeHandler canDecode,
        TryReadInfoHandler tryReadInfo,
        TryGetClosestSizeHandler tryGetClosestSize,
        TryDecodeHandler tryDecode,
        TryDecodeIntoHandler tryDecodeInto,
        TryReadMetadataHandler tryReadMetadata,
        TryDecodeThumbnailHandler tryDecodeThumbnail,
        TryOpenFramesHandler? tryOpenFrames = null,
        TryReadPlanarInfoHandler? tryReadPlanarInfo = null,
        TryDecodePlanesHandler? tryDecodePlanes = null)
    {
        if (format == ImageFormat.Unknown || !Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, "A registration names a defined, known format.");
        ArgumentNullException.ThrowIfNull(canDecode);
        ArgumentNullException.ThrowIfNull(tryReadInfo);
        ArgumentNullException.ThrowIfNull(tryGetClosestSize);
        ArgumentNullException.ThrowIfNull(tryDecode);
        ArgumentNullException.ThrowIfNull(tryDecodeInto);
        ArgumentNullException.ThrowIfNull(tryReadMetadata);
        ArgumentNullException.ThrowIfNull(tryDecodeThumbnail);
        if ((tryReadPlanarInfo is null) != (tryDecodePlanes is null))
            throw new ArgumentException("Give both planar delegates or neither.", nameof(tryDecodePlanes));
        Format = format;
        CanDecode = canDecode;
        TryReadInfo = tryReadInfo;
        TryGetClosestSize = tryGetClosestSize;
        TryDecode = tryDecode;
        TryDecodeInto = tryDecodeInto;
        TryReadMetadata = tryReadMetadata;
        TryDecodeThumbnail = tryDecodeThumbnail;
        TryOpenFrames = tryOpenFrames;
        TryReadPlanarInfo = tryReadPlanarInfo;
        TryDecodePlanes = tryDecodePlanes;
    }

    /// <summary>The format the codec decodes.</summary>
    public ImageFormat Format { get; }

    /// <summary>The codec's signature test.</summary>
    public CanDecodeHandler CanDecode { get; }

    /// <summary>The codec's header read.</summary>
    public TryReadInfoHandler TryReadInfo { get; }

    /// <summary>The codec's native-size negotiation.</summary>
    public TryGetClosestSizeHandler TryGetClosestSize { get; }

    /// <summary>The codec's fidelity decode.</summary>
    public TryDecodeHandler TryDecode { get; }

    /// <summary>The codec's decode into a caller's buffer.</summary>
    public TryDecodeIntoHandler TryDecodeInto { get; }

    /// <summary>The codec's metadata extraction.</summary>
    public TryReadMetadataHandler TryReadMetadata { get; }

    /// <summary>The codec's embedded-preview decode.</summary>
    public TryDecodeThumbnailHandler TryDecodeThumbnail { get; }

    /// <summary>The codec's frame reader; null for a still-only codec.</summary>
    public TryOpenFramesHandler? TryOpenFrames { get; }

    /// <summary>The codec's planar description; null when it has no planar output.</summary>
    public TryReadPlanarInfoHandler? TryReadPlanarInfo { get; }

    /// <summary>The codec's planar decode; null when it has no planar output.</summary>
    public TryDecodePlanesHandler? TryDecodePlanes { get; }
}
