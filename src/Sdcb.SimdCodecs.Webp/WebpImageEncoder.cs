using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// <see cref="IImageEncoder"/> for WebP: the encoder abstraction's adapter over <see cref="WebpEncoder"/> and
/// <see cref="WebpAnimationEncoder"/>. The options type chooses the coding: <see cref="WebpLosslessEncodeOptions"/> (or none)
/// is lossless VP8L, <see cref="WebpLossyEncodeOptions"/> lossy VP8, and <see cref="WebpAnimationEncodeOptions"/> an
/// animation. A session given several frames writes an animation too; with lossless or lossy options their effort,
/// quality and metadata become the animation's, with the matching <see cref="WebpAnimationMode"/>.
/// <para>
/// WebP stores 8-bit samples with straight alpha, so <see cref="GetClosestInputFormat"/> is the requested format at
/// 8 bits with straight alpha; the encoders take any layout, and the session converts. A frame's
/// <see cref="ImageMetadata"/> (ICC, Exif, XMP) overrides the options' own; an animation takes the first frame's. A frame's
/// <see cref="ColorEncoding"/> is ignored. The output is exactly what the existing encoder writes for the converted pixels.
/// </para>
/// </summary>
/// <remarks>
/// An animation's canvas is the first frame's size and every frame must have it (<see cref="ArgumentException"/> from
/// <see cref="IImageEncoderSession.Commit"/> otherwise). Its frames share one pixel format: the first frame's, or
/// <see cref="PixelFormat.Rgba32"/> when the frames' formats differ. A frame given no duration shows for 100 ms.
/// </remarks>
public sealed class WebpImageEncoder : IImageEncoder
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Webp,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Webp;

    /// <inheritdoc />
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) => Native(requested);

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is a <see cref="WebpLosslessEncodeOptions"/>, <see cref="WebpLossyEncodeOptions"/> or
    /// <see cref="WebpAnimationEncodeOptions"/>; any other type, or null, means lossless defaults.</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, options is WebpLossyEncodeOptions or WebpAnimationEncodeOptions or WebpLosslessEncodeOptions
            ? options : new WebpLosslessEncodeOptions());

    private static PixelFormat Native(PixelFormat f) => f.To8Bit().ToStraight();

    private sealed class Session(IBufferWriter<byte> output, EncodeOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => int.MaxValue;

        protected override int MaxDimension =>
            options is WebpLossyEncodeOptions ? WebpEncoder.MaximumLossyDimension : WebpEncoder.MaximumDimension;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = frames[0];
            if (frames.Count == 1 && options is not WebpAnimationEncodeOptions)
            {
                var native = Native(first.Format);
                var pixels = first.PixelsAs(native);
                WriteOutput(options is WebpLossyEncodeOptions lossy
                    ? WebpEncoder.EncodeLossy(pixels, first.Width, first.Height, native, WithMetadata(lossy, first.Metadata))
                    : WebpEncoder.EncodeLossless(pixels, first.Width, first.Height, native, WithMetadata((WebpLosslessEncodeOptions)options, first.Metadata)));
                return;
            }

            var format = Native(first.Format);
            foreach (var f in frames)
            {
                if (f.Width != first.Width || f.Height != first.Height)
                    throw new ArgumentException($"Every frame of a WebP animation has the canvas size {first.Width}x{first.Height}, not {f.Width}x{f.Height}.");
                if (Native(f.Format) != format)
                    format = PixelFormat.Rgba32;
            }
            var canvas = new List<WebpAnimationCanvasFrame>(frames.Count);
            foreach (var f in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                canvas.Add(new WebpAnimationCanvasFrame(f.PixelsAs(format).ToArray(), f.Duration ?? DefaultDuration));
            }
            WriteOutput(WebpEncoder.EncodeAnimation(canvas, first.Width, first.Height, format, AnimationOptions(first.Metadata)));
        }

        private WebpAnimationEncodeOptions AnimationOptions(ImageMetadata? m)
        {
            var o = options switch
            {
                WebpAnimationEncodeOptions a => a,
                WebpLossyEncodeOptions l => new WebpAnimationEncodeOptions
                {
                    Mode = WebpAnimationMode.Lossy, Quality = l.Quality, AlphaQuality = l.AlphaQuality, LossyEffort = l.Effort,
                    IccProfile = l.IccProfile, Exif = l.Exif, Xmp = l.Xmp,
                },
                WebpLosslessEncodeOptions l => new WebpAnimationEncodeOptions
                {
                    Mode = WebpAnimationMode.Lossless, LosslessEffort = l.Effort,
                    IccProfile = l.IccProfile, Exif = l.Exif, Xmp = l.Xmp,
                },
                _ => new WebpAnimationEncodeOptions(),
            };
            if (m is null) return o;
            return o with
            {
                IccProfile = m.Icc.IsEmpty ? o.IccProfile : m.Icc.ToArray(),
                Exif = m.Exif.IsEmpty ? o.Exif : m.Exif.ToArray(),
                Xmp = m.Xmp.IsEmpty ? o.Xmp : m.Xmp.ToArray(),
            };
        }

        private static WebpLosslessEncodeOptions WithMetadata(WebpLosslessEncodeOptions o, ImageMetadata? m) => m is null ? o : o with
        {
            IccProfile = m.Icc.IsEmpty ? o.IccProfile : m.Icc.ToArray(),
            Exif = m.Exif.IsEmpty ? o.Exif : m.Exif.ToArray(),
            Xmp = m.Xmp.IsEmpty ? o.Xmp : m.Xmp.ToArray(),
        };

        private static WebpLossyEncodeOptions WithMetadata(WebpLossyEncodeOptions o, ImageMetadata? m) => m is null ? o : o with
        {
            IccProfile = m.Icc.IsEmpty ? o.IccProfile : m.Icc.ToArray(),
            Exif = m.Exif.IsEmpty ? o.Exif : m.Exif.ToArray(),
            Xmp = m.Xmp.IsEmpty ? o.Xmp : m.Xmp.ToArray(),
        };
    }
}
