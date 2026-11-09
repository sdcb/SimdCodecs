using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>
/// <see cref="IImageEncoder"/> for GIF: the encoder abstraction's adapter over <see cref="GifEncoder"/>.
/// Encodes stills and animations with up to 256 colours and 1-bit transparency using 8-bit RGB, RGBA, or grey pixels.
/// </summary>
public sealed class GifImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Gif,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Gif;

    /// <inheritdoc />
    /// <remarks>
    /// GIF stores 8-bit samples in 1 (grey), 3 (RGB), or 4 (RGBA) channels. Formats with alpha map to
    /// <see cref="PixelFormat.Rgba32"/>, colour formats without alpha map to <see cref="PixelFormat.Rgb24"/>,
    /// and grey formats without alpha map to <see cref="PixelFormat.Gray8"/>.
    /// </remarks>
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) =>
        requested.HasAlpha ? PixelFormat.Rgba32 : (requested.IsGray ? PixelFormat.Gray8 : PixelFormat.Rgb24);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="options"/> is a <see cref="GifEncodeOptions"/> (null means the defaults; any other type throws
    /// <see cref="ArgumentException"/>).
    /// </remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, options switch
        {
            null => new GifEncodeOptions(),
            GifEncodeOptions o => o,
            _ => throw new ArgumentException($"GIF encoding takes GifEncodeOptions, not {options.GetType().Name}.", nameof(options)),
        });

    private sealed class Session(IBufferWriter<byte> output, GifEncodeOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => int.MaxValue;

        protected override int MaxDimension => 65535;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frames.Count == 1)
            {
                var frame = frames[0];
                var native = GetClosestInputFormat(frame.Format);
                var pixels = frame.PixelsAs(native);
                var file = GifEncoder.Encode(pixels, frame.Width, frame.Height, native.Channels, options);
                WriteOutput(file);
            }
            else
            {
                int width = frames[0].Width;
                int height = frames[0].Height;
                var native = DetermineAnimationFormat(frames);
                var animFrames = new GifAnimationFrame[frames.Count];
                for (var i = 0; i < frames.Count; i++)
                {
                    var f = frames[i];
                    if (f.Width != width || f.Height != height)
                        throw new ArgumentException($"Frame {i} is {f.Width}x{f.Height}, but the animation canvas is {width}x{height}.");
                    animFrames[i] = new GifAnimationFrame(f.PixelsAs(native).ToArray(), f.Duration ?? default(GifAnimationFrame).Duration);
                }
                var file = GifEncoder.EncodeAnimation(animFrames, width, height, native.Channels, options);
                WriteOutput(file);
            }
        }

        private static PixelFormat DetermineAnimationFormat(IReadOnlyList<BufferedFrame> frames)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames[i].Format.HasAlpha)
                    return PixelFormat.Rgba32;
            }
            for (var i = 0; i < frames.Count; i++)
            {
                if (!frames[i].Format.IsGray)
                    return PixelFormat.Rgb24;
            }
            return PixelFormat.Gray8;
        }
    }
}
