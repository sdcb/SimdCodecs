using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// <see cref="IImageEncoder"/> for PNG: the encoder abstraction's adapter over <see cref="PngWriter"/>. It stores every
/// <see cref="PixelFormat"/>'s channels at its own depth — 8- or 16-bit grey, grey + alpha, RGB or RGBA — so
/// <see cref="GetClosestInputFormat"/> keeps the depth and alpha and only reorders BGR to RGB and premultiplied to straight
/// (both lossless conversions done in the session). A frame's <see cref="ImageMetadata"/> becomes <c>iCCP</c>, <c>eXIf</c>
/// and an XMP <c>iTXt</c> chunk, and a non-sRGB <see cref="ColorEncoding"/> a <c>cICP</c> chunk.
/// <para>
/// The output is exactly the file <see cref="PngWriter"/> writes for the converted pixels and the
/// <see cref="PngWriteOptions"/> given (a still format: one frame). PNG needs the whole image for its filter pass and
/// deflate stream, so the session collects the rows (<see cref="BufferedEncoderSession"/>).
/// </para>
/// </summary>
public sealed class PngImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Png,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Png;

    /// <inheritdoc />
    /// <remarks>The same channels and depth with straight alpha, RGB order: PNG stores what it is given.</remarks>
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) => Native(requested);

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is a <see cref="PngWriteOptions"/> (null means the defaults; any other type
    /// throws <see cref="ArgumentException"/>). Metadata and colour given to <see cref="IImageEncoderSession.AddFrame"/> override the matching
    /// options.</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, options switch
        {
            null => new PngWriteOptions(),
            PngWriteOptions o => o,
            _ => throw new ArgumentException($"PNG encoding takes PngWriteOptions, not {options.GetType().Name}.", nameof(options)),
        });

    private static PixelFormat Native(PixelFormat f) => f.Layout switch
    {
        ChannelLayout.Rgba or ChannelLayout.Bgra => new PixelFormat(ChannelLayout.Rgba, f.Sample),
        ChannelLayout.Rgb or ChannelLayout.Bgr => new PixelFormat(ChannelLayout.Rgb, f.Sample),
        ChannelLayout.Gray => new PixelFormat(ChannelLayout.Gray, f.Sample),
        _ => new PixelFormat(ChannelLayout.GrayAlpha, f.Sample),
    };

    private sealed class Session(IBufferWriter<byte> output, PngWriteOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => int.MaxValue;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frames.Count > 1)
            {
                EncodeAnimation(frames, cancellationToken);
                return;
            }
            var frame = frames[0];
            var native = Native(frame.Format);
            var pixels = frame.PixelsAs(native);
            var o = WithFrame(options, frame);
            int w = frame.Width, h = frame.Height;
            byte[] file = native.Sample == SampleFormat.UInt16
                ? native.Layout switch
                {
                    ChannelLayout.Rgba => PngWriter.EncodeRgba16(MemoryMarshal.Cast<byte, ushort>(pixels), w, h, o),
                    ChannelLayout.Rgb => PngWriter.EncodeRgb16(MemoryMarshal.Cast<byte, ushort>(pixels), w, h, o),
                    ChannelLayout.Gray => PngWriter.EncodeGray16(MemoryMarshal.Cast<byte, ushort>(pixels), w, h, o),
                    _ => PngWriter.EncodeGrayAlpha16(MemoryMarshal.Cast<byte, ushort>(pixels), w, h, o),
                }
                : native.Layout switch
                {
                    ChannelLayout.Rgba => PngWriter.Encode(pixels, w, h, o),
                    ChannelLayout.Rgb => PngWriter.EncodeRgb8(pixels, w, h, o),
                    ChannelLayout.Gray => PngWriter.EncodeGray8(pixels, w, h, o),
                    _ => PngWriter.EncodeGrayAlpha8(pixels, w, h, o),
                };
            WriteOutput(file);
        }

        // Several frames are an animated PNG: the first frame's size is the canvas, every frame is a whole canvas shown for its
        // duration (100 ms when none is given), and one 16-bit frame makes the animation 16-bit.
        private void EncodeAnimation(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            var first = frames[0];
            var wide = frames.Any(f => f.Format.Sample == SampleFormat.UInt16);
            var encoder = new PngAnimationEncoder(first.Width, first.Height, WithFrame(options, first), wide);
            for (var i = 0; i < frames.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var f = frames[i];
                if (f.Width != first.Width || f.Height != first.Height)
                    throw new ArgumentException($"Frame {i} is {f.Width}x{f.Height}, but the animation canvas is {first.Width}x{first.Height}.");
                encoder.AddFrame(f.Pixels, f.Format, f.Duration ?? TimeSpan.FromMilliseconds(100));
            }
            WriteOutput(encoder.Encode());
        }

        // The frame's metadata and colour override the options': ICC, Exif, an XMP iTXt, cICP.
        private static PngWriteOptions WithFrame(PngWriteOptions options, BufferedFrame frame)
        {
            var o = options;
            if (frame.Metadata is { } m)
            {
                if (!m.Icc.IsEmpty)
                    o = o with { IccProfile = m.Icc.ToArray() };
                if (!m.Exif.IsEmpty)
                    o = o with { Exif = m.Exif.ToArray() };
                if (!m.Xmp.IsEmpty)
                {
                    var text = new List<PngText>(o.Text ?? []) { new("XML:com.adobe.xmp", Encoding.UTF8.GetString(m.Xmp.Span)) };
                    o = o with { Text = text };
                }
            }
            if (frame.Color is { } c && c != ColorEncoding.AssumedSrgb && o.Cicp is null)
                o = o with { Cicp = new CicpChunk(c.Primaries, c.Transfer, MatrixCoefficients.Identity, c.FullRange) };
            return o;
        }
    }
}
