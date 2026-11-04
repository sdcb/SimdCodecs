using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Ico;

/// <summary>
/// <see cref="IImageEncoder"/> for Windows icon (.ico) files: the encoder abstraction's adapter over
/// <see cref="IcoEncoder"/>. Stores each frame as an icon image (PNG for sizes 256 or more, DIB otherwise).
/// </summary>
public sealed class IcoImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Ico,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Ico;

    /// <inheritdoc />
    /// <remarks>
    /// Windows icons store 8-bit samples natively in RGBA, BGRA, RGB, BGR, or grey. 16-bit, premultiplied, and
    /// grey + alpha formats are converted to the matching 8-bit straight layout.
    /// </remarks>
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) => requested.Layout switch
    {
        ChannelLayout.Bgra => PixelFormat.Bgra32,
        ChannelLayout.Rgb => PixelFormat.Rgb24,
        ChannelLayout.Bgr => PixelFormat.Bgr24,
        ChannelLayout.Gray => PixelFormat.Gray8,
        _ => PixelFormat.Rgba32,
    };

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is unused: icons carry no encoder-level options.</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output);

    private sealed class Session(IBufferWriter<byte> output) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => 65535;

        protected override int MaxDimension => 256;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var icoFrames = new IcoFrame[frames.Count];
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var native = GetClosestInputFormat(frame.Format);
                var pixels = frame.PixelsAs(native).ToArray();
                icoFrames[i] = new IcoFrame(pixels, frame.Width, frame.Height, native);
            }
            var file = IcoEncoder.Encode(icoFrames);
            WriteOutput(file);
        }
    }
}
