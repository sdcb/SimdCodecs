using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// <see cref="IImageEncoder"/> for Netpbm: the encoder abstraction's adapter over <see cref="PnmEncoder"/>.
/// Netpbm stores 8- and 16-bit grey, grey + alpha, RGB, or RGBA, so <see cref="GetClosestInputFormat"/> keeps depth,
/// straight alpha, and RGB order.
/// </summary>
public sealed class PnmImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Pnm,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Pnm;

    /// <inheritdoc />
    /// <remarks>The same channels and depth with straight alpha, RGB order: Netpbm stores what it is given.</remarks>
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) =>
        PixelFormat.ForChannels(requested.Channels, requested.Sample);

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is a <see cref="PnmEncodeOptions"/> (null means the defaults; any other type
    /// throws <see cref="ArgumentException"/>).</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, options switch
        {
            null => new PnmEncodeOptions(),
            PnmEncodeOptions o => o,
            _ => throw new ArgumentException($"Netpbm encoding takes PnmEncodeOptions, not {options.GetType().Name}.", nameof(options)),
        });

    private sealed class Session(IBufferWriter<byte> output, PnmEncodeOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => 1;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[0];
            var native = GetClosestInputFormat(frame.Format);
            var pixels = frame.PixelsAs(native);
            var file = PnmEncoder.Encode(pixels, frame.Width, frame.Height, native, options);
            WriteOutput(file);
        }
    }
}
