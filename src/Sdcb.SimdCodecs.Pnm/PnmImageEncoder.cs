using System.Buffers;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// <see cref="IImageEncoder"/> for Netpbm: the encoder abstraction's adapter over <see cref="PnmEncoder"/>.
/// Netpbm stores 8- and 16-bit grey, grey + alpha, RGB, or RGBA, so <see cref="GetClosestInputFormat"/> keeps depth,
/// straight alpha, and RGB order. Every session streams (<see cref="IImageEncoderSession.IsStreaming"/>): the header
/// reaches the writer at <c>AddFrame</c> and each band of rows as it is written.
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

    // Every Netpbm option encodes row by row (a plain raster starts each row on a new line), so every session streams:
    // the header at AddFrame, each band's raster as WriteRows hands it over.
    private sealed class Session(IBufferWriter<byte> output, PnmEncodeOptions options) : StreamingEncoderSession(output)
    {
        private PnmEncoder.RowWriter? _writer;

        public override int MaxFrames => 1;

        protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
            ImageMetadata? metadata, TimeSpan? duration) =>
            _writer = PnmEncoder.RowWriter.Begin(width, height, format, options, Output);

        protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _writer!.Write(rows, rowCount, stride, Output);
        }

        protected override void Finish(CancellationToken cancellationToken) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _writer?.Dispose();
            _writer = null;
            base.Dispose(disposing);
        }
    }
}