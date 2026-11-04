using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// <see cref="IImageEncoder"/> for TIFF: the encoder abstraction's adapter over <see cref="TiffWriter"/>. Each frame is
/// one page, stored at its own depth: 8- or 16-bit grey, grey + alpha, RGB or RGBA (straight alpha, marked as unassociated
/// extra samples), so <see cref="GetClosestInputFormat"/> only reorders BGR to RGB and unpremultiplies.
/// <para>
/// Pages take their compression, layout and resolution from <see cref="TiffEncodeOptions.Page"/>; the frame sets the sample
/// layout. A frame's ICC profile becomes the page's ICC tag. Exif and XMP are not written (the writer has no tags for them)
/// and a frame's <see cref="ColorEncoding"/> is ignored.
/// </para>
/// </summary>
public sealed class TiffImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Tiff,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Tiff;

    /// <inheritdoc />
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) => Native(requested);

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is a <see cref="TiffEncodeOptions"/> (any other type, or null, means the defaults).</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, options as TiffEncodeOptions ?? new TiffEncodeOptions());

    private static PixelFormat Native(PixelFormat f) => f.Layout switch
    {
        ChannelLayout.Rgba or ChannelLayout.Bgra => new PixelFormat(ChannelLayout.Rgba, f.Sample),
        ChannelLayout.Rgb or ChannelLayout.Bgr => new PixelFormat(ChannelLayout.Rgb, f.Sample),
        ChannelLayout.Gray => new PixelFormat(ChannelLayout.Gray, f.Sample),
        _ => new PixelFormat(ChannelLayout.GrayAlpha, f.Sample),
    };

    private sealed class Session(IBufferWriter<byte> output, TiffEncodeOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => int.MaxValue;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            // Every await below is over a MemoryStream and completes synchronously.
            using var stream = new MemoryStream();
            var writer = TiffWriter.Create(stream);
            try
            {
                foreach (var frame in frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var native = Native(frame.Format);
                    var pixels = frame.PixelsAs(native).ToArray();
                    writer.AddPageAsync(pixels, frame.Width, frame.Height, PageOptions(native, frame), cancellationToken)
                        .GetAwaiter().GetResult();
                }
            }
            finally
            {
                writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            WriteOutput(stream.ToArray());
        }

        private TiffPageOptions PageOptions(PixelFormat native, BufferedFrame frame)
        {
            var gray = native.Layout is ChannelLayout.Gray or ChannelLayout.GrayAlpha;
            var alpha = native.Layout is ChannelLayout.GrayAlpha or ChannelLayout.Rgba;
            var o = (options.Page ?? TiffPageOptions.Default) with
            {
                SamplesPerPixel = native.Layout switch { ChannelLayout.Rgba => 4, ChannelLayout.Rgb => 3, ChannelLayout.GrayAlpha => 2, _ => 1 },
                BitsPerSample = native.Sample == SampleFormat.UInt16 ? 16 : 8,
                SampleFormat = TiffSampleFormat.Uint,
                Photometric = gray ? TiffPhotometric.MinIsBlack : TiffPhotometric.Rgb,
                ExtraSamples = alpha ? TiffExtraSamples.UnassociatedAlpha : null,
            };
            if (frame.Metadata is { } m && !m.Icc.IsEmpty)
                o = o with { IccProfile = m.Icc };
            return o;
        }
    }
}
