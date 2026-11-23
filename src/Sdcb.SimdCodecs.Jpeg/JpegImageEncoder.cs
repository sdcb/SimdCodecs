using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// <see cref="IImageEncoder"/> for JPEG: the encoder abstraction's adapter over <see cref="JpegEncoder"/>. JPEG stores
/// 8-bit grey or YCbCr colour, so <see cref="GetClosestInputFormat"/> maps grey (with or without alpha) to
/// <see cref="PixelFormat.Gray8"/>, colour without alpha to <see cref="PixelFormat.Rgb24"/> and colour with alpha to
/// <see cref="PixelFormat.Rgba32"/> (the encoder reads RGBA natively and ignores the alpha). 16-bit input keeps its high
/// byte; BGR(A) is reordered by the session.
/// <para>
/// The output is exactly what <see cref="JpegEncoder.Encode"/> writes for the converted pixels and the
/// <see cref="JpegEncodeOptions"/> given (a still format: one frame). Baseline encoding with the standard tables streams
/// (<see cref="IImageEncoderSession.IsStreaming"/> true): the headers reach the writer at <c>AddFrame</c> and each MCU row
/// (8 rows, 16 for 4:2:0) as soon as its last row is written, so the session holds one MCU row, not the image.
/// <see cref="JpegEncodeOptions.OptimizeHuffman"/> and <see cref="JpegEncodeOptions.Progressive"/> need every coefficient
/// first; their session collects the image and encodes it at <c>Commit</c> (<c>IsStreaming</c> false). A frame's <see cref="ImageMetadata"/> sets
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
    /// <remarks><paramref name="options"/> is a <see cref="JpegEncodeOptions"/> (null means the defaults; any other type
    /// throws <see cref="ArgumentException"/>).</remarks>
    public static IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null)
    {
        var o = options switch
        {
            null => new JpegEncodeOptions(),
            JpegEncodeOptions jpeg => jpeg,
            _ => throw new ArgumentException($"JPEG encoding takes JpegEncodeOptions, not {options.GetType().Name}.", nameof(options)),
        };
        // Optimised Huffman tables and progressive scans need every coefficient before the first scan is written.
        return o.Progressive || o.OptimizeHuffman ? new Session(output, o) : new StreamingSession(output, o);
    }

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
            var channels = Channels(native);
            WriteOutput(JpegEncoder.Encode(pixels, frame.Width, frame.Height, channels, WithMetadata(options, frame.Metadata)));
        }
    }

    private static int Channels(PixelFormat native) => native.Layout switch { ChannelLayout.Rgba => 4, ChannelLayout.Rgb => 3, _ => 1 };

    private static JpegEncodeOptions WithMetadata(JpegEncodeOptions options, ImageMetadata? metadata)
    {
        var o = options;
        if (metadata is { } m)
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

    /// <summary>
    /// Baseline with the Annex K tables: the headers go out at <c>AddFrame</c>, then each MCU row (8 image rows, 16 for
    /// 4:2:0) is transformed, coded and handed to the writer as soon as its last row arrives, so memory is one MCU row of
    /// pixels and of output. It is the buffered encoder fed a row at a time: the same bytes for the same pixels, however
    /// the rows are split across <c>WriteRows</c> calls.
    /// </summary>
    private sealed class StreamingSession(IBufferWriter<byte> output, JpegEncodeOptions options) : StreamingEncoderSession(output)
    {
        private JpegBitWriter _w;
        private JpegEncoder.McuRowEncoder? _rows;
        private byte[]? _band; // rows of the MCU row being collected, in the native format, tightly packed
        private PixelFormat _format, _native;
        private int _width, _height, _rowBytes, _mcuRows, _filled, _my;
        private bool _faulted;

        public override int MaxFrames => 1;

        protected override int MaxDimension => 65535;

        protected override void BeginFrame(int index, int width, int height, PixelFormat format, ColorEncoding? color,
            ImageMetadata? metadata, TimeSpan? duration)
        {
            var native = Native(format);
            var channels = Channels(native);
            var setup = JpegEncoder.Prepare(width, height, channels, WithMetadata(options, metadata));
            ThrowIfFaulted();
            (_format, _native, _width, _height) = (format, native, width, height);
            _rowBytes = width * channels;
            _w = new JpegBitWriter(4096);
            _faulted = true; // until the headers are out: a retried AddFrame must not write them twice
            JpegEncoder.WriteBaselinePreamble(ref _w, setup);
            Flush();
            _faulted = false;
            _rows = new JpegEncoder.McuRowEncoder(setup.Frame, channels, JpegEncoder.StdDcTables, JpegEncoder.StdAcTables);
            _mcuRows = _rows.Rows;
        }

        protected override void EncodeRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
        {
            // A call that stops part way (cancelled, or the writer threw) has coded rows the session does not count as
            // written (or part of the headers or trailer is out); going on would write a corrupt file, so the session refuses.
            ThrowIfFaulted();
            _faulted = true;
            EncodeRowsCore(rows, rowCount, stride, cancellationToken);
            _faulted = false;
        }

        private void EncodeRowsCore(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken cancellationToken)
        {
            while (rowCount > 0)
            {
                // Rows the current MCU row still needs: all of them, fewer only in the image's last MCU row.
                var need = Math.Min(_mcuRows, _height - _my * _mcuRows);
                if (_filled == 0 && rowCount >= need && _format == _native)
                {
                    // A whole MCU row in the native format: coded straight from the caller's rows.
                    EncodeMcuRow(rows, stride, cancellationToken);
                }
                else
                {
                    var take = Math.Min(rowCount, need - _filled);
                    _band ??= ArrayPool<byte>.Shared.Rent(_mcuRows * _rowBytes);
                    PixelConverter.Convert(rows, stride, _format, _band.AsSpan(_filled * _rowBytes), _rowBytes, _native, _width, take);
                    _filled += take;
                    rowCount -= take;
                    if (rowCount > 0)
                        rows = rows[(take * stride)..];
                    if (_filled == need)
                    {
                        _filled = 0;
                        EncodeMcuRow(_band, _rowBytes, cancellationToken);
                    }
                    continue;
                }

                rowCount -= need;
                if (rowCount > 0)
                    rows = rows[(need * stride)..];
            }
        }

        private void EncodeMcuRow(ReadOnlySpan<byte> band, int stride, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _rows!.Encode(band, stride, _my++, ref _w);
            Flush();
        }

        protected override void EndFrame(CancellationToken cancellationToken)
        {
            _faulted = true; // cleared once EOI is out, so a writer failure here fails Commit too
            _w.FlushBits();
            _w.WriteByte(0xFF);
            _w.WriteByte(0xD9);
            Flush();
            _faulted = false;
            Release();
        }

        protected override void Finish(CancellationToken cancellationToken) => ThrowIfFaulted();

        private void ThrowIfFaulted()
        {
            if (_faulted)
                throw new InvalidOperationException("An earlier call failed part way through writing the file; the session cannot continue.");
        }

        /// <summary>
        /// Hands the finished bytes to the output and starts the buffer over. Every coded MCU ends with the stuffing pass
        /// (<see cref="JpegBitWriter.Stuff"/>) and a marker or header ends on a byte, so <c>[0, Position)</c> is final;
        /// the under-8 pending bits stay in the accumulator and the next store rewrites them at the new position.
        /// </summary>
        private void Flush()
        {
            WriteOutput(_w.Buffer.AsSpan(0, _w.Position));
            _w.Position = 0;
            _w.Mark = 0;
        }

        private void Release()
        {
            _rows?.Dispose();
            _rows = null;
            if (_band is not null) ArrayPool<byte>.Shared.Return(_band);
            _band = null;
            if (_w.Buffer is { Length: > 0 }) ArrayPool<byte>.Shared.Return(_w.Buffer);
            _w.Buffer = [];
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Release();
        }
    }
}
