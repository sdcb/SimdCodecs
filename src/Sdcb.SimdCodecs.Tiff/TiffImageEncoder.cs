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
/// layout. A frame's <see cref="ImageMetadata"/> overrides the template's: its ICC profile becomes the page's ICC tag
/// (34675), its XMP packet tag 700, and its Exif block is merged into the page as <see cref="TiffPageOptions.Exif"/>
/// describes. An Exif block that is not a TIFF structure is dropped, not an error. A frame's <see cref="ColorEncoding"/>
/// is ignored.
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
    /// <remarks><paramref name="options"/> is a <see cref="TiffEncodeOptions"/> (null means the defaults; any other type
    /// throws <see cref="ArgumentException"/>).</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null) =>
        new Session(output, Options(options));

    /// <summary>
    /// Starts a streaming session that writes the file to <paramref name="output"/> as the rows arrive
    /// (<see cref="IImageEncoderSession.IsStreaming"/> is true): each strip, or each row of tiles, is compressed and written
    /// once its rows are in, and a page's IFD follows its last segment, so memory stays at a band of rows whatever the
    /// image size. The file is byte for byte the one <see cref="Create(IBufferWriter{byte}, EncodeOptions?)"/> writes.
    /// </summary>
    /// <remarks>
    /// A TIFF header and each IFD point forward to an IFD written later, so the session patches those offsets by seeking:
    /// <paramref name="output"/> must be writable and seekable (for a non-seekable stream, encode into an
    /// <see cref="IBufferWriter{T}"/> and copy). The file starts at the stream's current position, and offsets count from
    /// there. The file is complete once <see cref="IImageEncoderSession.Commit"/> returns; a session disposed before that
    /// leaves a truncated file behind. Disposing the session disposes <paramref name="output"/> unless
    /// <paramref name="leaveOpen"/> is set. <paramref name="options"/> is a <see cref="TiffEncodeOptions"/> (null means the
    /// defaults; any other type throws <see cref="ArgumentException"/>). A compression the writer cannot apply, or an Exif
    /// block in <see cref="TiffPageOptions.Exif"/> that is not a TIFF structure, fails at
    /// <see cref="IImageEncoderSession.AddFrame"/>, before the page is written.
    /// </remarks>
    public static IImageEncoderSession Create(Stream output, EncodeOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("The stream is not writable.", nameof(output));
        if (!output.CanSeek)
            throw new ArgumentException("A streaming TIFF session patches offsets and needs a seekable stream; for a non-seekable one, use Create(IBufferWriter<byte>, EncodeOptions?) and copy the result.", nameof(output));
        return TiffStreamingEncoderSession.Create(output, Options(options), leaveOpen);
    }

    private static TiffEncodeOptions Options(EncodeOptions? options) => options switch
    {
        null => new TiffEncodeOptions(),
        TiffEncodeOptions o => o,
        _ => throw new ArgumentException($"TIFF encoding takes TiffEncodeOptions, not {options.GetType().Name}.", nameof(options)),
    };

    internal static PixelFormat Native(PixelFormat f) => f.Layout switch
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
                    writer.AddPageAsync(pixels, frame.Width, frame.Height, PageOptions(options, native, frame.Metadata), cancellationToken)
                        .GetAwaiter().GetResult();
                }
            }
            finally
            {
                writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            WriteOutput(stream.ToArray());
        }
    }

    /// <summary>The page a frame in <paramref name="native"/> format becomes: the template with the frame's sample layout
    /// and metadata (shared by the buffered and the streaming session).</summary>
    internal static TiffPageOptions PageOptions(TiffEncodeOptions options, PixelFormat native, ImageMetadata? metadata)
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
        if (metadata is { } m)
        {
            if (!m.Icc.IsEmpty)
                o = o with { IccProfile = m.Icc };
            if (!m.Xmp.IsEmpty)
                o = o with { Xmp = m.Xmp };
            if (!m.Exif.IsEmpty && TiffExif.ParseBlock(m.Exif.Span) is not null)
                o = o with { Exif = m.Exif };
        }
        return o;
    }
}
