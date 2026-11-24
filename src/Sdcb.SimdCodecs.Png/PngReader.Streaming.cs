using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Png;

public static partial class PngReader
{
    /// <summary>
    /// How a streaming decode's <see cref="InvalidDataException"/> starts when a chunk after the image data changes the
    /// rows it has already written (a PLTE, tRNS or cICP that the PNG specification places before IDAT): the one case
    /// where it refuses a file <see cref="PngImageDecoder.TryDecodeInto"/> accepts.
    /// </summary>
    internal const string LateChunkMessage = "A chunk after the image data changes rows a streaming decode has already written";

    /// <summary>
    /// <see cref="PngImageDecoder.CreateRowReader"/>: a reader that inflates the IDAT stream as it arrives and writes each
    /// row as soon as it is inflated. See <see cref="RowReader"/>.
    /// </summary>
    internal static StreamingRowReader CreateRowReader(StreamInput input, PixelFormat format, DecodeOptions? options) =>
        new RowReader(input, format, options);

    /// <summary>
    /// The streaming decode of a non-interlaced, non-animated PNG. The header is every chunk before the first IDAT,
    /// read as <see cref="ReadHeader"/> reads them. The rows come from one <see cref="ZLibStream"/> over an
    /// <see cref="IdatWalker"/>, which hands it the IDAT payloads straight out of the input window, checking each chunk's
    /// CRC as it ends and reading the chunks after the image data as <see cref="ReadHeader"/> does, up to IEND (or the
    /// end of the stream at a chunk boundary). Each row is unfiltered against the one above and converted to the format
    /// exactly as <see cref="DecodeInto"/> (the five basic 8-bit formats) or <see cref="DecodeWindow"/> (any other) does it,
    /// so the rows are <see cref="PngImageDecoder.TryDecodeInto"/>'s byte for byte. The last row is held back until the
    /// rest of the file is checked, so a file the span decode refuses never reads to the end.
    /// <para>
    /// Interlaced and animated files, an indexed file whose PLTE comes after IDAT, a cICP PQ/HLG file read to 8-bit
    /// samples, and any options but the whole image take the buffered fallback: the span decoder over the whole file.
    /// </para>
    /// <para>
    /// Memory: two rows, the format's working rows, the inflater's window, and an input window of at most a few rows of
    /// compressed data (<see cref="_gate"/>), whatever the height; a chunk before the first IDAT, and a chunk after it that
    /// <see cref="HeaderBuilder"/> reads, is held whole while it is read (as the span decoder holds the file). Chunks
    /// after the first IDAT that it skips are passed over without being held. The row buffers are allocated only once the input
    /// holds enough data that deflate's 1032:1 limit lets it make a row, so a forged IHDR costs no more than the span
    /// decoder's own check allows.
    /// </para>
    /// </summary>
    private sealed class RowReader(StreamInput input, PixelFormat format, DecodeOptions? options) : StreamingRowReader(input, format)
    {
        private readonly HeaderBuilder _builder = new();
        private int _headerPos = 8;     // the end of the last whole chunk DecodeHeader has read
        private bool _animated;         // an acTL before IDAT
        private bool _bufferWhole;      // the fallback is chosen: the header waits for the end of the stream

        // Set once the header is read and the image streams.
        private PngImage _header = null!;
        private IdatWalker _walker = null!;
        private ZLibStream _z = null!;
        private readonly byte[] _one = new byte[1];
        private int _rowBytes;          // a packed row, without its filter byte
        private int _filterBpp;
        private int _spp;
        private long _expected;         // the bytes the image data inflates to
        private long _gate;             // the most input a retry waits for (see DecodeRows)
        private long _retryAt;          // after running dry: the input to wait for before trying again
        private long _dataStart;        // the file offset of the first IDAT
        private long _scratchBytes;
        private int _rgbaBytes, _unpackedBytes, _builtBytes;
        private bool _basic, _copyRgba, _direct;
        private PngRgba8Expander? _expander;   // the basic formats' RGBA expansion
        private PngRgba8Expander? _palette;    // the other formats' index expansion
        private ushort[]? _key;
        private PixelFormat _fidelity;

        private byte[]? _scratch;
        private bool _aIsCurrent = true;
        private int _row;               // rows written
        private int _filled;            // bytes of the current row (filter byte first) inflated
        private bool _rowReady;         // the current row is inflated and unfiltered
        private bool _streamEnded, _fileEnded, _checked;

        protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            out ImageInfo info, out int width, out int height)
        {
            consumed = 0;
            info = default;
            width = height = 0;
            if (_bufferWhole)
                return WholeFileHeader(input, isFinal, out info, out width, out height);

            if (input.Length < Signature.Length)
            {
                if (!input.SequenceEqual(Signature.AsSpan(0, input.Length)))
                    throw new InvalidDataException("PNG signature missing — not a PNG file");
                return OperationStatus.NeedMoreData;
            }
            if (!input[..Signature.Length].SequenceEqual(Signature))
                throw new InvalidDataException("PNG signature missing — not a PNG file");

            // The input starts at the image's first byte on every call; the chunks before _headerPos are read already.
            while (true)
            {
                var pos = _headerPos;
                var left = input.Length - pos;
                if (left < 8)
                {
                    if (!isFinal)
                        return OperationStatus.NeedMoreData;
                    if (left == 0)
                        throw NoImageData();
                    throw new InvalidDataException($"Truncated chunk header at byte {pos}");
                }
                var dataLen = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(pos, 4));
                if (dataLen > int.MaxValue)
                    throw new InvalidDataException($"Chunk length {dataLen} exceeds int.MaxValue");
                var type = input.Slice(pos + 4, 4);
                if (type.SequenceEqual("IDAT"u8))
                    break;
                if (left - 12L < dataLen)
                {
                    if (isFinal)
                        throw new InvalidDataException($"Chunk at byte {pos} extends past end of file");
                    return OperationStatus.NeedMoreData;
                }
                var len = (int)dataLen;
                var chunk = input.Slice(pos + 8, len);
                var declared = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(pos + 8 + len, 4));
                var computed = Crc32(type, chunk);
                if (declared != computed)
                    throw CrcMismatch(type, pos, declared, computed);
                if (type.SequenceEqual("IEND"u8))
                    throw NoImageData();
                if (type.SequenceEqual("acTL"u8))
                    _animated = true;
                _builder.Add(type, chunk);
                _headerPos = pos + 12 + len;
            }

            // The first IDAT: every chunk before it is read. (This point is reached once: the image streams from here,
            // or the fallback takes over.)
            _builder.AddIdat();
            var header = _builder.Snapshot();
            if (header.Interlaced || _animated || !IsFullImage(options)
                || (header.ColorType == 3 && header.Palette is null)
                || (Format.Sample == SampleFormat.UInt8 && header.Cicp is { TransferFunction: TransferFunction.Pq or TransferFunction.Hlg }))
            {
                // Adam7 scatters every pass over the whole image; an APNG's default image is decoded as the span decoder
                // does; a palette still to come, an HDR refusal a later cICP may lift, options other than the whole
                // image: all decided by the span decoder over the whole file.
                _bufferWhole = true;
                return WholeFileHeader(input, isFinal, out info, out width, out height);
            }
            if ((long)header.Width * Format.BytesPerPixel > Array.MaxLength)
                throw new InvalidDataException($"A {header.Width}-pixel row of {Format} is longer than an array can hold.");

            // TryReadInfo over the chunks before the image data: the same info, but for what only later chunks say.
            info = PngImageDecoder.TryReadInfo(input[.._headerPos], out var read) ? read : PngImageDecoder.InfoOf(header);
            Prepare(header);
            consumed = _headerPos;
            _dataStart = Input.Position + _headerPos;
            width = header.Width;
            height = header.Height;
            return OperationStatus.Done;
        }

        // The span decoder's verdict on a file with no image data: no IHDR, or no IDAT.
        private InvalidDataException NoImageData()
        {
            try
            {
                _builder.Build(0);
            }
            catch (InvalidDataException e)
            {
                return e;
            }
            return new InvalidDataException("No IDAT chunks found");
        }

        // The fallback's header: the whole file, checked as the span decoder checks it before it allocates anything.
        private OperationStatus WholeFileHeader(ReadOnlySpan<byte> input, bool isFinal, out ImageInfo info, out int width, out int height)
        {
            info = default;
            width = height = 0;
            if (!isFinal)
                return OperationStatus.NeedMoreData;
            PngImage header;
            try
            {
                header = PngReader.ReadHeader(input, out _);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException($"The PNG data is invalid: {e.Message}", e);
            }
            info = PngImageDecoder.TryReadInfo(input, out var read) ? read : PngImageDecoder.InfoOf(header);
            (width, height) = (options ?? DecodeOptions.Default).GetOutputSize(info);
            UseBufferedFallback(DecodeWhole, options);
            return OperationStatus.Done;
        }

        // The fallback's decode: TryDecodeInto, with the reason when it refuses the file.
        private static bool DecodeWhole(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
            DecodeOptions? options, CancellationToken cancellationToken)
        {
            if (PngImageDecoder.TryDecodeInto(data, destination, stride, format, options, cancellationToken))
                return true;
            if (format.Sample == SampleFormat.UInt8 && TryDecode(data, out var png, cancellationToken)
                && png.Cicp is { TransferFunction: TransferFunction.Pq or TransferFunction.Hlg })
                throw new InvalidDataException("The PNG is tagged PQ or HLG (cICP), which has no 8-bit projection: read it to 16-bit samples.");
            try
            {
                Decode(data, cancellationToken);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidDataException($"The PNG could not be decoded: {e.Message}", e);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                throw new InvalidDataException($"The PNG data is invalid: {e.Message}", e);
            }
            return false;
        }

        // The per-image constants of the row loop, as DecodeInto (basic formats) and DecodeWindow (the rest) set them.
        private void Prepare(PngImage header)
        {
            _header = header;
            int width = header.Width, bitDepth = header.SourceBitDepth, colorType = header.ColorType;
            _spp = SamplesPerPixel(colorType);
            var rowBytes = ((long)width * _spp * bitDepth + 7) / 8;
            if (rowBytes >= Array.MaxLength)
                throw new InvalidDataException($"A {width}-pixel row is longer than an array can hold.");
            _rowBytes = (int)rowBytes;
            _filterBpp = bitDepth < 8 ? 1 : _spp * (bitDepth / 8);
            _expected = InflatedSize(width, header.Height, bitDepth, colorType, interlaced: false);
            _basic = Format.IsBasic8;
            long rgba = 0, unpacked = 0, built = 0;
            if (_basic)
            {
                _expander = new PngRgba8Expander(colorType, bitDepth, header.Palette, header.PaletteAlpha, header.TransparentColor);
                _copyRgba = colorType == 6 && bitDepth == 8 && Format == PixelFormat.Rgba32;
                _direct = Format != PixelFormat.Rgba32 && bitDepth == 8 && colorType != 3 && header.TransparentColor is null;
                rgba = _copyRgba || _direct || Format == PixelFormat.Rgba32 ? 0 : 4L * width;
            }
            else
            {
                _fidelity = FidelityFormat(header);
                _key = header.UnpackedTransparentColor();
                _palette = colorType == 3 ? new PngRgba8Expander(3, 8, header.Palette, header.PaletteAlpha) : null;
                unpacked = bitDepth < 8 ? width : 0;
                built = colorType == 3 || _key is not null || bitDepth == 16 ? (long)width * _fidelity.BytesPerPixel : 0;
            }
            _scratchBytes = 2 * (rowBytes + 1) + rgba + unpacked + built;
            if (_scratchBytes > Array.MaxLength)
                throw new InvalidDataException($"A {width}-pixel row is longer than an array can hold.");
            (_rgbaBytes, _unpackedBytes, _builtBytes) = ((int)rgba, (int)unpacked, (int)built);
            // A row's compressed form is at most a little longer than the row (stored blocks): with two rows' worth
            // buffered, a row's inflate does not run out of input midway. Kept between 32 KiB and 16 MiB.
            _gate = Math.Clamp(2 * (rowBytes + 1) + 1024, 32 * 1024, 16 * 1024 * 1024);
            _walker = new IdatWalker(_builder);
            _z = new ZLibStream(_walker, CompressionMode.Decompress, leaveOpen: true);
        }

        protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            Span<byte> destination, int stride, int rowCount, out int rowsWritten)
        {
            rowsWritten = 0;
            consumed = 0;
            if (_scratch is null)
            {
                // Deflate makes at most 1032 bytes of each compressed byte: allocate a row's buffers only once the
                // input could hold one (when the whole file cannot, the span decoder refuses it by the same limit).
                if (_rowBytes + 1L > input.Length * 1032L + 1024)
                {
                    if (isFinal)
                        throw new InvalidDataException(
                            $"{input.Length} bytes of image data cannot inflate to the {_expected} a {_header.Width}×{_header.Height} image needs");
                    return OperationStatus.NeedMoreData;
                }
                _scratch = ArrayPool<byte>.Shared.Rent((int)_scratchBytes);
                _scratch.AsSpan(0, 2 * (_rowBytes + 1)).Clear();   // the row above the first is zeros
            }

            _walker.Begin(Input.BufferedMemory, Input.Position, isFinal);
            var outRowBytes = _header.Width * Format.BytesPerPixel;
            while (rowsWritten < rowCount)
            {
                // A row is tried as soon as input is there. Running dry inside one unwinds the inflater with an
                // exception, so after that the next try waits for twice the input that was not enough (up to _gate):
                // a row costs a few such tries at most, however its bytes arrive.
                // Before that, a row waits for twice the input an average row so far has taken, so that a steady stream
                // seldom runs dry at all.
                var available = input.Length - _walker.Consumed;
                var fed = Input.Position + _walker.Consumed - _dataStart;
                var expected = _row == 0 || _rowReady ? 0 : Math.Min(_gate, 2 * fed / _row + 64);
                if (!isFinal && !_walker.Ended && available < Math.Max(expected, _retryAt))
                    break;
                if (!_rowReady)
                {
                    if (!InflateRow())
                    {
                        _retryAt = Math.Min(_gate, 2L * available + 64);
                        break;
                    }
                    var raw = Current;
                    PngPredictor.UnfilterRow(raw[0], raw[1..], Above[1..], _filterBpp);
                    _rowReady = true;
                }
                if (_row == _header.Height - 1 && !FinishFile())
                {
                    _retryAt = Math.Min(_gate, 2L * available + 64);
                    break;
                }
                _retryAt = 0;
                WriteRow(Current[1..], destination.Slice(rowsWritten * stride, outRowBytes));
                _aIsCurrent = !_aIsCurrent;
                _row++;
                rowsWritten++;
                _filled = 0;
                _rowReady = false;
            }
            consumed = _walker.Consumed;
            return rowsWritten == rowCount ? OperationStatus.Done : OperationStatus.NeedMoreData;
        }

        private Span<byte> Current => _scratch.AsSpan(_aIsCurrent ? 0 : _rowBytes + 1, _rowBytes + 1);

        private Span<byte> Above => _scratch.AsSpan(_aIsCurrent ? _rowBytes + 1 : 0, _rowBytes + 1);

        // Inflates the rest of the current row (filter byte, then samples). False when the input runs out first; the
        // bytes inflated so far stay, and the inflater resumes from there.
        private bool InflateRow()
        {
            var raw = Current;
            while (_filled < raw.Length)
            {
                int n;
                try
                {
                    n = _z.Read(raw[_filled..]);
                }
                catch (InputStarvedException)
                {
                    return false;
                }
                catch (IOException e)
                {
                    // zlib's own error codes (a corrupt stream that is none of the cases DeflateStream names) arrive as an IOException.
                    throw new InvalidDataException("The image data is not a valid zlib stream", e);
                }
                if (n == 0)
                    throw LengthMismatch((long)_row * raw.Length + _filled, _expected, _header.Width, _header.Height, _filterBpp);
                _filled += n;
            }
            return true;
        }

        // Before the last row goes out: no byte past it in the zlib stream, the rest of the file read to IEND with
        // every CRC and chunk checked, and the whole file's checks; then the chunks after the image data must not have
        // changed what the rows decode to. False when the input runs out first.
        private bool FinishFile()
        {
            if (!_streamEnded)
            {
                int extra;
                try
                {
                    extra = _z.Read(_one);
                }
                catch (InputStarvedException)
                {
                    return false;
                }
                catch (IOException e)
                {
                    throw new InvalidDataException("The image data is not a valid zlib stream", e);
                }
                if (extra != 0)
                    throw LengthMismatch(_expected + 1, _expected, _header.Width, _header.Height, _filterBpp);
                _streamEnded = true;
            }
            if (!_fileEnded)
            {
                if (!_walker.DrainToEnd())
                    return false;
                _fileEnded = true;
            }
            if (!_checked)
            {
                var whole = _builder.Build(_walker.Compressed);
                // A late tRNS changes only alpha, which a format without alpha drops (the expansion's colour is the same).
                if (_header.ColorType == 3 && !SameBytes(whole.Palette, _header.Palette))
                    throw new InvalidDataException($"{LateChunkMessage}: a PLTE after IDAT (the PNG specification puts it before it).");
                if (Format.HasAlpha && _header.ColorType == 3 && !SameBytes(whole.PaletteAlpha, _header.PaletteAlpha))
                    throw new InvalidDataException($"{LateChunkMessage}: a tRNS after IDAT (the PNG specification puts it before it).");
                if (Format.HasAlpha && !Same(whole.TransparentColor, _header.TransparentColor))
                    throw new InvalidDataException($"{LateChunkMessage}: a tRNS after IDAT (the PNG specification puts it before it).");
                if (Format.Sample == SampleFormat.UInt8 && whole.Cicp is { TransferFunction: TransferFunction.Pq or TransferFunction.Hlg })
                    throw new InvalidDataException($"{LateChunkMessage}: a cICP after IDAT tags the image PQ or HLG, which has no 8-bit projection.");
                _checked = true;
            }
            return true;
        }

        private static bool SameBytes(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

        private static bool Same(ushort[]? a, ushort[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

        // One unfiltered row into the destination, as DecodeInto (basic formats) or DecodeWindow (the rest) writes it.
        private void WriteRow(ReadOnlySpan<byte> row, Span<byte> target)
        {
            var width = _header.Width;
            if (_basic)
            {
                if (_copyRgba)
                {
                    row.CopyTo(target);
                }
                else if (_direct)
                {
                    PixelConverter.ConvertRow(row, _spp, SampleFormat.UInt8, target, Format, width);
                }
                else if (Format == PixelFormat.Rgba32)
                {
                    _expander!.Expand(row, target, width);
                }
                else
                {
                    var rgbaRow = _scratch.AsSpan(2 * (_rowBytes + 1), _rgbaBytes);
                    _expander!.Expand(row, rgbaRow, width);
                    PixelConverter.ConvertRow(rgbaRow, 4, SampleFormat.UInt8, target, Format, width);
                }
                return;
            }
            var at = 2 * (_rowBytes + 1);
            var unpacked = _scratch.AsSpan(at, _unpackedBytes);
            var built = _scratch.AsSpan(at + _unpackedBytes, _builtBytes);
            var fidelityRow = FidelityRow(row, 0, width, _header.SourceBitDepth, _header.ColorType, _spp, _key, _palette, unpacked, built);
            PixelConverter.ConvertRow(fidelityRow, _fidelity, target, Format, width);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _z?.Dispose();
                if (_scratch is not null)
                    ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The input window ran out inside the image data before the stream ended: unwinds the inflater to retry.</summary>
    private sealed class InputStarvedException : Exception;

    /// <summary>
    /// The chunks from the first IDAT on, walked over a window of the input that <see cref="Begin"/> moves forward: as a
    /// <see cref="Stream"/>, the IDAT payloads in order (what <see cref="ZLibStream"/> inflates), each chunk's CRC checked
    /// as it ends; the chunks between and after the IDATs read through the <see cref="HeaderBuilder"/> as
    /// <see cref="ReadHeader"/> reads them, those it skips passed over without being held; up to IEND, or the end of the
    /// stream at a chunk boundary (both of which <see cref="ReadHeader"/> accepts). When the window ends inside a chunk
    /// before the stream does, <see cref="Read(Span{byte})"/> throws <see cref="InputStarvedException"/> (returning 0 there
    /// would read as the end of the data, and is refused outright under <c>System.IO.Compression.UseStrictValidation</c>);
    /// the inflater has taken nothing in that call, so it resumes where it was once the window holds more.
    /// </summary>
    private sealed class IdatWalker(HeaderBuilder builder) : Stream
    {
        private enum State { Header, Idat, Skip, Crc, End }

        private ReadOnlyMemory<byte> _window;
        private int _pos;               // bytes of _window consumed
        private long _offset;           // the file offset of _window[0]
        private bool _final;
        private State _state;
        private long _chunkStart;       // the file offset of the current chunk
        private uint _type;             // the current chunk's type, big-endian
        private long _left;             // payload bytes of the current IDAT or skipped chunk still to come
        private uint _crc;              // the raw CRC register over the current chunk's type and payload so far

        /// <summary>The IDAT payloads' total length so far.</summary>
        public long Compressed { get; private set; }

        /// <summary>True once IEND, or the end of the stream at a chunk boundary, is reached.</summary>
        public bool Ended => _state == State.End;

        /// <summary>Bytes of the window consumed since <see cref="Begin"/>.</summary>
        public int Consumed => _pos;

        /// <summary>The next window: the unconsumed input, from file offset <paramref name="offset"/>.</summary>
        public void Begin(ReadOnlyMemory<byte> window, long offset, bool isFinal)
        {
            _window = window;
            _pos = 0;
            _offset = offset;
            _final = isFinal;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;
            while (true)
            {
                if (_state == State.Idat && _left > 0)
                {
                    var available = _window.Length - _pos;
                    if (available == 0)
                    {
                        if (_final)
                            throw PastEnd();
                        throw new InputStarvedException();
                    }
                    var n = (int)Math.Min(Math.Min(_left, available), buffer.Length);
                    var bytes = _window.Span.Slice(_pos, n);
                    bytes.CopyTo(buffer);
                    _crc = PngCrc32.Update(_crc, bytes);
                    _pos += n;
                    _left -= n;
                    return n;
                }
                if (_state == State.End)
                    return 0;
                if (!Step())
                    throw new InputStarvedException();
            }
        }

        /// <summary>
        /// Walks to the end of the file, passing over what is left of the IDAT payloads (the zlib stream has ended).
        /// False when the window runs out first.
        /// </summary>
        public bool DrainToEnd()
        {
            while (_state != State.End)
            {
                if (!Step())
                    return false;
            }
            return true;
        }

        // One step that hands the inflater nothing: the rest of a payload passed over, a CRC, or a chunk header (and a
        // chunk the header builder reads, whole). False when the window ends inside it before the stream does.
        private bool Step()
        {
            var span = _window.Span;
            var available = span.Length - _pos;
            switch (_state)
            {
                case State.Idat or State.Skip:
                {
                    var n = (int)Math.Min(_left, available);
                    _crc = PngCrc32.Update(_crc, span.Slice(_pos, n));
                    _pos += n;
                    _left -= n;
                    if (_left > 0)
                        return _final ? throw PastEnd() : false;
                    _state = State.Crc;
                    return true;
                }
                case State.Crc:
                {
                    if (available < 4)
                        return _final ? throw PastEnd() : false;
                    var declared = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(_pos, 4));
                    var computed = _crc ^ 0xFFFFFFFFu;
                    if (declared != computed)
                        throw CrcMismatch(TypeBytes(_type), _chunkStart, declared, computed);
                    _pos += 4;
                    _state = State.Header;
                    return true;
                }
                case State.Header:
                {
                    if (available < 8)
                    {
                        if (!_final)
                            return false;
                        // The file ends at a chunk boundary without IEND, as ReadHeader accepts; a few bytes more are a
                        // cut chunk header.
                        if (available == 0)
                        {
                            _state = State.End;
                            return true;
                        }
                        throw new InvalidDataException($"Truncated chunk header at byte {_offset + _pos}");
                    }
                    var dataLen = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(_pos, 4));
                    if (dataLen > int.MaxValue)
                        throw new InvalidDataException($"Chunk length {dataLen} exceeds int.MaxValue");
                    var type = span.Slice(_pos + 4, 4);
                    _chunkStart = _offset + _pos;
                    if (type.SequenceEqual("IDAT"u8) || !(HeaderBuilder.Reads(type) || type.SequenceEqual("IEND"u8)))
                    {
                        // A payload passed through (IDAT) or over (a chunk ReadHeader skips) without being held.
                        var idat = type.SequenceEqual("IDAT"u8);
                        if (idat)
                        {
                            builder.AddIdat();
                            Compressed += dataLen;
                        }
                        _type = BinaryPrimitives.ReadUInt32BigEndian(type);
                        _crc = PngCrc32.Update(0xFFFFFFFFu, type);
                        _left = dataLen;
                        _pos += 8;
                        _state = idat ? State.Idat : State.Skip;
                        return true;
                    }
                    // A chunk the header builder reads, whole: as ReadHeader reads it.
                    if (available - 12L < dataLen)
                        return _final ? throw PastEnd() : false;
                    var len = (int)dataLen;
                    var chunk = span.Slice(_pos + 8, len);
                    var declared = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(_pos + 8 + len, 4));
                    var computed = Crc32(type, chunk);
                    if (declared != computed)
                        throw CrcMismatch(type, _chunkStart, declared, computed);
                    if (type.SequenceEqual("IEND"u8))
                        _state = State.End;
                    else
                        builder.Add(type, chunk);
                    _pos += 12 + len;
                    return true;
                }
                default:
                    return true;
            }
        }

        private static byte[] TypeBytes(uint type)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, type);
            return bytes;
        }

        private InvalidDataException PastEnd() => new($"Chunk at byte {_chunkStart} extends past end of file");

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
