using System;
using System.Diagnostics.CodeAnalysis;

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

    /// <summary><see cref="IImageDecoder.TryDecode"/>.</summary>
    public delegate bool TryDecodeHandler(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image);

    /// <summary><see cref="IImageDecoder.TryDecodeInto"/>.</summary>
    public delegate bool TryDecodeIntoHandler(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format);

    /// <summary><see cref="IMultiFrameImageDecoder.TryOpenFrames"/>.</summary>
    public delegate bool TryOpenFramesHandler(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader);

    /// <summary>Describes a codec for the facade's registry.</summary>
    /// <param name="format">The format the codec decodes (what <c>SimdCodecs.DetectFormat</c> reports for it).</param>
    /// <param name="canDecode">The codec's signature test.</param>
    /// <param name="tryReadInfo">The codec's header read.</param>
    /// <param name="tryDecode">The codec's fidelity decode.</param>
    /// <param name="tryDecodeInto">The codec's 8-bit decode into a caller's buffer.</param>
    /// <param name="tryOpenFrames">The codec's frame reader, or null for a still-only codec: the facade then
    /// presents its image as one frame.</param>
    /// <exception cref="ArgumentNullException">A required delegate is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="ImageFormat.Unknown"/>
    /// or not a defined member.</exception>
    public ImageDecoderRegistration(
        ImageFormat format,
        CanDecodeHandler canDecode,
        TryReadInfoHandler tryReadInfo,
        TryDecodeHandler tryDecode,
        TryDecodeIntoHandler tryDecodeInto,
        TryOpenFramesHandler? tryOpenFrames = null)
    {
        if (format == ImageFormat.Unknown || !Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, "A registration names a defined, known format.");
        ArgumentNullException.ThrowIfNull(canDecode);
        ArgumentNullException.ThrowIfNull(tryReadInfo);
        ArgumentNullException.ThrowIfNull(tryDecode);
        ArgumentNullException.ThrowIfNull(tryDecodeInto);
        Format = format;
        CanDecode = canDecode;
        TryReadInfo = tryReadInfo;
        TryDecode = tryDecode;
        TryDecodeInto = tryDecodeInto;
        TryOpenFrames = tryOpenFrames;
    }

    /// <summary>The format the codec decodes.</summary>
    public ImageFormat Format { get; }

    /// <summary>The codec's signature test.</summary>
    public CanDecodeHandler CanDecode { get; }

    /// <summary>The codec's header read.</summary>
    public TryReadInfoHandler TryReadInfo { get; }

    /// <summary>The codec's fidelity decode.</summary>
    public TryDecodeHandler TryDecode { get; }

    /// <summary>The codec's 8-bit decode into a caller's buffer.</summary>
    public TryDecodeIntoHandler TryDecodeInto { get; }

    /// <summary>The codec's frame reader; null for a still-only codec.</summary>
    public TryOpenFramesHandler? TryOpenFrames { get; }
}
