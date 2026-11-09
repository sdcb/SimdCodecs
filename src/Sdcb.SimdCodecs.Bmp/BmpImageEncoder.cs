using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// <see cref="IImageEncoder"/> for BMP: the encoder abstraction's adapter over <see cref="BmpEncoder"/>.
/// BMP stores 8-bit samples in RGBA, BGRA, RGB, BGR, or grey; <see cref="GetClosestInputFormat"/> maps 16-bit
/// and premultiplied inputs to 8-bit straight alpha and maps grey + alpha to RGBA.
/// </summary>
public sealed class BmpImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Bmp,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Bmp;

    /// <inheritdoc />
    /// <remarks>The closest 8-bit format: RGBA, BGRA, RGB, BGR, or grey with straight alpha.</remarks>
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) => requested.Layout switch
    {
        ChannelLayout.Rgba => PixelFormat.Rgba32,
        ChannelLayout.Bgra => PixelFormat.Bgra32,
        ChannelLayout.Rgb => PixelFormat.Rgb24,
        ChannelLayout.Bgr => PixelFormat.Bgr24,
        ChannelLayout.Gray => PixelFormat.Gray8,
        _ => PixelFormat.Rgba32,
    };

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is a <see cref="BmpEncodeOptions"/> (null means the defaults; any other
    /// type throws <see cref="ArgumentException"/>). An ICC profile given to <see cref="IImageEncoderSession.AddFrame"/> overrides the matching
    /// option.</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, options switch
        {
            null => new BmpEncodeOptions(),
            BmpEncodeOptions o => o,
            _ => throw new ArgumentException($"BMP encoding takes BmpEncodeOptions, not {options.GetType().Name}.", nameof(options)),
        });

    private sealed class Session(IBufferWriter<byte> output, BmpEncodeOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => 1;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[0];
            var native = GetClosestInputFormat(frame.Format);
            var pixels = frame.PixelsAs(native);
            var o = WithFrame(options, frame);
            var file = BmpEncoder.Encode(pixels, frame.Width, frame.Height, native, o);
            WriteOutput(file);
        }

        private static BmpEncodeOptions WithFrame(BmpEncodeOptions options, BufferedFrame frame)
        {
            var o = options;
            if (frame.Metadata is { } m && !m.Icc.IsEmpty)
                o = o with { IccProfile = m.Icc };
            return o;
        }
    }
}
