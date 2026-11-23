using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// <see cref="IImageEncoder"/> for BMP: the encoder abstraction's adapter over <see cref="BmpEncoder"/>.
/// BMP stores 8-bit samples in RGBA, BGRA, RGB, BGR, or grey; <see cref="GetClosestInputFormat"/> maps 16-bit
/// and premultiplied inputs to 8-bit straight alpha and maps grey + alpha to RGBA.
/// </summary>
public sealed class BmpImageEncoder : IImageEncoder
{
    /// <summary>The registration for the facade's encoder registry, built from this type's method groups.</summary>
    public static ImageEncoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Bmp,
        GetClosestInputFormat = GetClosestInputFormat,
        Create = Create,
    };

    /// <inheritdoc />
    public static ImageFormat Format => ImageFormat.Bmp;

    /// <inheritdoc />
    /// <remarks>The closest 8-bit format: RGBA, BGRA, RGB, BGR, or grey with straight alpha.</remarks>
    public static PixelFormat GetClosestInputFormat(PixelFormat requested) => requested.Layout switch
    {
        ChannelLayout.Rgba => PixelFormat.Rgba32,
        ChannelLayout.Bgra => PixelFormat.Bgra32,
        ChannelLayout.Rgb => PixelFormat.Rgb24,
        ChannelLayout.Bgr => PixelFormat.Bgr24,
        ChannelLayout.Gray => PixelFormat.Gray8,
        _ => PixelFormat.Rgba32,
    };

    /// <inheritdoc />
    /// <remarks><paramref name="options"/> is a <see cref="BmpEncodeOptions"/> (null means the defaults; any other
    /// type throws <see cref="ArgumentException"/>). An ICC profile given to <see cref="IImageEncoderSession.AddFrame"/> overrides the matching
    /// option.
    /// <para>
    /// The session streams (<see cref="IImageEncoderSession.IsStreaming"/>) when the options fix the file's size and
    /// offsets before any pixel arrives: <see cref="BmpEncodeOptions.TopDown"/>, an explicit direct-colour
    /// <see cref="BmpEncodeOptions.Format"/> (<see cref="BmpEncodeFormat.Rgb555"/>, <see cref="BmpEncodeFormat.Rgb565"/>,
    /// <see cref="BmpEncodeFormat.Rgb24"/>, <see cref="BmpEncodeFormat.Rgb32"/>, <see cref="BmpEncodeFormat.Argb32"/>)
    /// and no RLE. Each band of rows is then written as it arrives, the same bytes as the buffered session's (which
    /// writes an array, so stops at <see cref="Array.MaxLength"/> bytes where a streamed file may reach 4 GiB). Its
    /// options are checked at <see cref="IImageEncoderSession.AddFrame"/>, where the headers are written, rather than
    /// at commit. Otherwise (bottom-up, <see cref="BmpEncodeFormat.Auto"/>, a palette) the image is collected and
    /// encoded at commit.
    /// </para>
    /// </remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null)
    {
        var o = options switch
        {
            null => new BmpEncodeOptions(),
            BmpEncodeOptions b => b,
            _ => throw new ArgumentException($"BMP encoding takes BmpEncodeOptions, not {options.GetType().Name}.", nameof(options)),
        };
        return BmpEncoder.CanStream(o) ? new StreamingSession(output, o) : new Session(output, o);
    }

    private static BmpEncodeOptions WithMetadata(BmpEncodeOptions options, ImageMetadata? metadata) =>
        metadata is { } m && !m.Icc.IsEmpty ? options with { IccProfile = m.Icc } : options;

    /// <summary>A top-down direct-colour file, written in file order: headers at the frame, rows as they come, the
    /// profile at commit.</summary>
    private sealed class StreamingSession(IBufferWriter<byte> output, BmpEncodeOptions options) : StreamingEncoderSession(output)
    {
        private BmpEncodeOptions _options = options;
        private BmpEncoder.FilePlan _plan;
        private PixelFormat _input, _native;
        private int _width;
        private byte[]? _converted;

        public override int MaxFrames => 1;

        protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
            ImageMetadata? metadata, TimeSpan? duration)
        {
            _options = WithMetadata(_options, metadata);
            var headers = BmpEncoder.StreamHeaders(width, height, _options, out _plan);
            (_width, _input, _native) = (width, format, GetClosestInputFormat(format));
            if (_native != _input)
                _converted = ArrayPool<byte>.Shared.Rent(checked(width * _native.BytesPerPixel));
            WriteOutput(headers);
        }

        protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
        {
            var rowBytes = _plan.RowBytes(_width);
            var inBytes = _width * _input.BytesPerPixel;
            for (var i = 0; i < rowCount; i++)
            {
                if ((i & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var source = rows.Slice(i * stride, inBytes);
                if (_converted is not null)
                {
                    var native = _converted.AsSpan(0, _width * _native.BytesPerPixel);
                    PixelConverter.ConvertRow(source, _input, native, _native, _width);
                    source = native;
                }
                var row = Output.GetSpan(rowBytes)[..rowBytes];
                BmpEncoder.WriteStreamRow(source, _width, _native, _plan, row);
                Output.Advance(rowBytes);
            }
        }

        protected override void Finish(CancellationToken cancellationToken)
        {
            if (_plan.Header == BmpHeaderVersion.V5)
                WriteOutput(_options.IccProfile.Span);
        }

        protected override void Dispose(bool disposing)
        {
            if (_converted is not null)
                ArrayPool<byte>.Shared.Return(_converted);
            _converted = null;
        }
    }

    private sealed class Session(IBufferWriter<byte> output, BmpEncodeOptions options) : BufferedEncoderSession(output)
    {
        public override int MaxFrames => 1;

        protected override void Encode(IReadOnlyList<BufferedFrame> frames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[0];
            var native = GetClosestInputFormat(frame.Format);
            var pixels = frame.PixelsAs(native);
            var o = WithMetadata(options, frame.Metadata);

            var file = BmpEncoder.Encode(pixels, frame.Width, frame.Height, native, o);
            WriteOutput(file);
        }

    }
}
