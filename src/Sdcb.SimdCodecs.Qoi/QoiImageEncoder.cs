using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Qoi;

/// <summary>
/// <see cref="IImageEncoder"/> for QOI: the encoder abstraction's adapter over <see cref="QoiEncoder"/>.
/// QOI stores 8-bit RGB or RGBA, so <see cref="GetClosestInputFormat"/> maps any input with alpha to
/// <see cref="PixelFormat.Rgba32"/> and any without to <see cref="PixelFormat.Rgb24"/>.
/// QOI has no options record: <see cref="Create"/> accepts null only.
/// The colour space byte is written as <see cref="QoiColorSpace.Linear"/> when
/// <see cref="BufferedFrame.Color"/> has <see cref="ColorEncoding.Transfer"/> set to
/// <see cref="TransferFunction.Linear"/>, else <see cref="QoiColorSpace.Srgb"/>.
/// </summary>
public sealed class QoiImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Qoi,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Qoi;

    /// <inheritdoc />
    /// <remarks>QOI stores 8-bit RGB or RGBA: maps any input to <see cref="PixelFormat.Rgba32"/> if it has alpha else <see cref="PixelFormat.Rgb24"/>.</remarks>
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) =>
        requested.HasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;

    /// <inheritdoc />
    /// <remarks>QOI has no options record: pass null. Any options object throws <see cref="ArgumentException"/>.</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        options is null
            ? new Session(output)
            : throw new ArgumentException($"QOI encoding takes no options (pass null), not {options.GetType().Name}.", nameof(options));

    private sealed class Session(IBufferWriter<byte> output) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => 1;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[0];
            var colorSpace = frame.Color?.Transfer == TransferFunction.Linear ? QoiColorSpace.Linear : QoiColorSpace.Srgb;
            QoiEncoder.Encode(frame.Pixels, frame.Width, frame.Height, frame.Format, Output, colorSpace, 0, cancellationToken);
        }
    }
}
