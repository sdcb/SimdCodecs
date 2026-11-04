using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageEncoder"/> for JPEG: the encoder abstraction's adapter over <see cref="JpegEncoder"/>. JPEG stores
/// 8-bit grey or YCbCr colour, so <see cref="GetClosestInputFormat"/> maps grey (with or without alpha) to
/// <see cref="PixelFormat.Gray8"/>, colour without alpha to <see cref="PixelFormat.Rgb24"/> and colour with alpha to
/// <see cref="PixelFormat.Rgba32"/> (the encoder reads RGBA natively and ignores the alpha). 16-bit input keeps its high
/// byte; BGR(A) is reordered by the session.
/// <para>
/// The output is exactly what <see cref="JpegEncoder.Encode"/> writes for the converted pixels and the
/// <see cref="JpegEncodeOptions"/> given (a still format: one frame). A frame's <see cref="ImageMetadata"/> sets
/// <see cref="JpegEncodeOptions.Exif"/> and <see cref="JpegEncodeOptions.IccProfile"/>, overriding the options' own. A block
/// that cannot be written is dropped, not an error: an Exif blob that is not a TIFF structure or is over
/// <see cref="JpegEncodeOptions.MaxExifBytes"/>, an ICC profile over <see cref="JpegEncodeOptions.MaxIccProfileBytes"/>.
/// </para>
/// </summary>
/// <remarks>
/// A frame's XMP is not written: <see cref="JpegXmp"/> builds its packet from named properties and cannot carry a raw
/// packet. A frame's <see cref="ColorEncoding"/> is ignored (JPEG says its colour space through the ICC profile).
/// </remarks>
public sealed class JpegImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Jpeg,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Jpeg;

    /// <inheritdoc />
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) => Native(requested);

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is a <see cref="JpegEncodeOptions"/> (any other type, or null, means the defaults).</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, options as JpegEncodeOptions ?? new JpegEncodeOptions());

    private static PixelFormat Native(PixelFormat f) => f.Layout switch
    {
        ChannelLayout.Rgba or ChannelLayout.Bgra => PixelFormat.Rgba32,
        ChannelLayout.Rgb or ChannelLayout.Bgr => PixelFormat.Rgb24,
        _ => PixelFormat.Gray8,
    };

    private sealed class Session(IBufferWriter<byte> output, JpegEncodeOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => 1;

        protected override int MaxDimension => 65535;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[0];
            var native = Native(frame.Format);
            var pixels = frame.PixelsAs(native);
            var channels = native.Layout switch { ChannelLayout.Rgba => 4, ChannelLayout.Rgb => 3, _ => 1 };
            WriteOutput(JpegEncoder.Encode(pixels, frame.Width, frame.Height, channels, WithFrame(options, frame)));
        }

        private static JpegEncodeOptions WithFrame(JpegEncodeOptions options, BufferedFrame frame)
        {
            var o = options;
            if (frame.Metadata is { } m)
            {
                if (IsTiffBlob(m.Exif.Span) && m.Exif.Length <= JpegEncodeOptions.MaxExifBytes)
                    o = o with { Exif = m.Exif };
                if (!m.Icc.IsEmpty && m.Icc.Length <= JpegEncodeOptions.MaxIccProfileBytes)
                    o = o with { IccProfile = m.Icc };
            }
            return o;
        }

        private static bool IsTiffBlob(ReadOnlySpan<byte> b) =>
            b.Length >= 8 && (b[..4].SequenceEqual("II*\0"u8) || b[..4].SequenceEqual("MM\0*"u8));
    }
}
