using System.Buffers;
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
/// the frame's colour encoding has <see cref="ColorEncoding.Transfer"/> set to
/// <see cref="TransferFunction.Linear"/>, else <see cref="QoiColorSpace.Srgb"/>.
/// <para>
/// The session streams (<see cref="IImageEncoderSession.IsStreaming"/> true): the header is written by
/// <see cref="IImageEncoderSession.AddFrame"/>, each band of rows is encoded and written as it arrives, and the end
/// marker at <see cref="IImageEncoderSession.Commit"/>. The bytes are those <see cref="QoiEncoder"/> writes for the
/// whole image, however the rows are split into bands.
/// </para>
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

    private sealed class Session(IBufferWriter<byte> output) : StreamingEncoderSession(output)
    {
        // The encoder state between bands: previous pixel, open run, index. A run open at the end of one band goes on
        // into the next, as it does between rows of a whole image.
        private QoiEncoder.Writer _state;
        private int _width;
        private PixelFormat _format;

        public override int MaxFrames => 1;

        protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
            ImageMetadata? metadata, TimeSpan? duration)
        {
            var colorSpace = color?.Transfer == TransferFunction.Linear ? QoiColorSpace.Linear : QoiColorSpace.Srgb;
            QoiEncoder.ValidateSize(width, height, colorSpace);
            _state = new QoiEncoder.Writer();
            _width = width;
            _format = format;
            QoiEncoder.WriteHeader(width, height, format, colorSpace, Output);
        }

        protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
        {
            Span<uint> rgba = stackalloc uint[QoiEncoder.Chunk];
            var rowBytes = _width * _format.BytesPerPixel;
            for (var y = 0; y < rowCount; y++)
            {
                if ((y & 63) == 63)
                    cancellationToken.ThrowIfCancellationRequested();
                QoiEncoder.WriteRow(ref _state, rows.Slice(y * stride, rowBytes), _width, _format, rgba, Output);
            }
        }

        protected override void Finish(CancellationToken cancellationToken) => QoiEncoder.WriteEnd(ref _state, Output);
    }
}
