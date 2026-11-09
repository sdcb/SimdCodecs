using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Sdcb.SimdCodecs.Abstractions;

/// <summary>
/// A codec's entry in the <c>Sdcb.SimdCodecs</c> facade's registry: its <see cref="IImageDecoder"/> members as
/// delegates, plus the <see cref="ImageFormat"/> it decodes. A codec package that the facade does not reference
/// (e.g. <c>Sdcb.SimdCodecs.Heif</c>, kept out for patent reasons) exposes one as a static property, built from
/// its own members, and a consumer enables the codec once, at start-up, with <c>ImageCodecs.Register</c>.
/// <para>
/// Build it with an object initializer over the concrete type's method groups — <c>new() { Format = ImageFormat.Heif, CanDecode = HeifImageDecoder.CanDecode, … }</c> —
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


    /// <summary>The format the codec decodes (what <c>ImageCodecs.DetectFormat</c> reports for it).</summary>
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

    /// <summary>The codec's signature test.</summary>
    public required CanDecodeHandler CanDecode { get; init => field = NotNull(value); }

    /// <summary>The codec's header read.</summary>
    public required TryReadInfoHandler TryReadInfo { get; init => field = NotNull(value); }

    /// <summary>The codec's native-size negotiation.</summary>
    public required TryGetClosestSizeHandler TryGetClosestSize { get; init => field = NotNull(value); }

    /// <summary>The codec's fidelity decode.</summary>
    public required TryDecodeHandler TryDecode { get; init => field = NotNull(value); }

    /// <summary>The codec's decode into a caller's buffer.</summary>
    public required TryDecodeIntoHandler TryDecodeInto { get; init => field = NotNull(value); }

    /// <summary>The codec's metadata extraction.</summary>
    public required TryReadMetadataHandler TryReadMetadata { get; init => field = NotNull(value); }

    /// <summary>The codec's embedded-preview decode.</summary>
    public required TryDecodeThumbnailHandler TryDecodeThumbnail { get; init => field = NotNull(value); }

    /// <summary>The codec's frame reader, or null for a still-only codec: the facade then presents its image as one frame.</summary>
    public TryOpenFramesHandler? TryOpenFrames { get; init; }

    /// <summary>The codec's planar description, or null when it has no planar output. Set together with
    /// <see cref="TryDecodePlanes"/>.</summary>
    public TryReadPlanarInfoHandler? TryReadPlanarInfo { get; init; }

    /// <summary>The codec's planar decode; set exactly when <see cref="TryReadPlanarInfo"/> is.</summary>
    public TryDecodePlanesHandler? TryDecodePlanes { get; init; }

    /// <summary>
    /// Checks what the initializer cannot check property by property: that both planar delegates or neither are set.
    /// The facade calls it on registration.
    /// </summary>
    /// <exception cref="ArgumentException">Only one of the two planar delegates is set.</exception>
    public void Validate()
    {
        if ((TryReadPlanarInfo is null) != (TryDecodePlanes is null))
            throw new ArgumentException("Set both planar delegates or neither.", nameof(TryDecodePlanes));
    }

    private static T NotNull<T>(T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}
