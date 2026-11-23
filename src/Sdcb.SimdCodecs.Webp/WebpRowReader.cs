using System.Buffers;
using System.Buffers.Binary;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>
/// The forward-only reader behind <see cref="WebpImageDecoder.CreateRowReader"/>: a lossy still (simple <c>VP8</c>,
/// or <c>VP8X</c> with <c>VP8</c> and optional <c>ALPH</c>) decodes macroblock row by macroblock row as its bytes
/// arrive; lossless, animated input and any option other than the whole image read the stream to its end and decode it
/// with <see cref="WebpImageDecoder.TryDecodeInto"/> instead (<see cref="StreamingRowReader.IsStreaming"/> false).
/// <para>
/// The header reaches the VP8 chunk's first ten bytes, so every chunk before the image (ICCP, EXIF, ALPH, unknown ones)
/// is buffered with it. The first partition (modes and probabilities, at most 512 KiB) is copied whole before the
/// first row. With one token partition the rows then follow the token bytes: memory is that partition copy, the row
/// caches and a window of compressed input about two macroblock rows long — O(width). With several partitions
/// (macroblock row y reads partition y mod n, and they are stored one after another) the whole VP8 chunk is buffered
/// before the first row (O(compressed chunk) + O(width)), the rows then come as asked. An ALPH plane is decoded whole
/// with the header (width × height bytes).
/// </para>
/// <para>
/// The rows are <see cref="WebpImageDecoder.TryDecodeInto"/>'s byte for byte: the same frame decoder, fed exactly the
/// same bytes (a row whose token bytes have not all arrived is decoded again once they have), and the same RGBA → format
/// conversion. The last row is held back until the container after the image has been checked to the RIFF end, so a
/// file the span decoder refuses fails here too. Two container layouts the span decoder accepts are refused, because
/// they change the image after its pixels: an ALPH chunk stored after the VP8 chunk, and a VP8X chunk after it that
/// declares an animation. <see cref="StreamingRowReader.Info"/> is <see cref="WebpImageDecoder.TryReadInfo"/>'s except
/// for chunks stored after the image (usually EXIF and XMP): their orientation, ICC profile or alpha flag is not in it.
/// </para>
/// </summary>
internal sealed class WebpRowReader : StreamingRowReader
{
    // The most rows one macroblock row's output spans: the last row, from the half row the one before held back.
    private const int BandRows = 24;

    private readonly DecodeOptions? _options;

    // Header walk: DecodeHeader sees the same bytes from the file's start on every call, so it resumes here.
    private long _walkOffset = 12;
    private long _riffEnd;
    private bool _extended;
    private bool _alphaFlag;
    private int _canvasWidth;
    private int _canvasHeight;
    private int _alphaOffset = -1;
    private int _alphaLength;
    private int _iccOffset = -1;
    private int _iccLength;
    private int _exifOffset = -1;
    private int _exifLength;
    private bool _hasXmp;
    private bool _buffered;

    // The VP8 chunk.
    private int _width;
    private int _height;
    private int _vp8Length;
    private long _vp8End;           // file offset after the chunk and its pad byte
    private int _firstPartitionLength;
    private byte[]? _firstPartition;
    private Vp8Decoder.FrameState _frame;
    private bool _frameStarted;
    private bool _frameDone;
    private int _tokenLength;       // bytes after the first partition
    private int _tokenConsumed;     // one partition: token bytes the decoder has read and the input consumed
    private int _retryWindow;       // one partition: token bytes to wait for before decoding a starved row again
    private Cursor _header;
    private Cursor[]? _partitions;  // several partitions: one each
    private Cursor _partitionState = Cursor.Fresh(0, 0);  // one partition: its decoder state (the input holds its position)

    // Output.
    private byte[]? _alpha;
    private byte[]? _band;          // RGBA rows [_bandFirst, _bandEnd)
    private ushort[]? _tRows;
    private byte[]? _chromaRows;
    private int _bandFirst;
    private int _bandEnd;
    private int _bandNext;

    // The container after the image.
    private long _trailerNext;      // file offset of the next chunk header
    private bool _trailerDone;

    public WebpRowReader(StreamInput input, PixelFormat format, DecodeOptions? options) : base(input, format) =>
        _options = options;

    /// <summary>A bool decoder between rows: the bytes it has read and its arithmetic state.</summary>
    private struct Cursor
    {
        public int Start;
        public int Length;
        public int Used;
        public ulong Value;
        public int Bits;
        public uint Range;

        public static Cursor Fresh(int start, int length)
        {
            new Vp8BoolDecoder(default).Load(out ulong value, out int bits, out uint range);
            return new Cursor { Start = start, Length = length, Value = value, Bits = bits, Range = range };
        }

        public readonly Vp8BoolDecoder Resume(ReadOnlySpan<byte> data) =>
            new(data.Slice(Start + Used, Length - Used), Value, Bits, Range);

        public void Save(in Vp8BoolDecoder decoder)
        {
            decoder.Load(out Value, out Bits, out Range);
            Used = Length - decoder.Remaining;
        }
    }

    protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = 0;
        info = default;
        width = height = 0;
        try
        {
            return ReadHeader(input, isFinal, out consumed, out info, out width, out height);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or OverflowException)
        {
            throw Invalid(e);
        }
    }

    private OperationStatus ReadHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = 0;
        info = default;
        width = height = 0;
        if (_buffered)
            return ReadWhole(input, isFinal, out info, out width, out height);
        if (input.Length < 12)
        {
            if (input.Length >= 4 && !input[..4].SequenceEqual("RIFF"u8))
                throw new InvalidDataException("The data does not have a WebP RIFF header.");
            return OperationStatus.NeedMoreData;
        }

        if (!WebpImageDecoder.CanDecode(input))
            throw new InvalidDataException("The data does not have a WebP RIFF header.");
        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(input[4..8]);
        if (riffLength < 4)
            throw new InvalidDataException("The WebP RIFF size is invalid.");
        _riffEnd = 8L + riffLength;

        while (true)
        {
            long offset = _walkOffset;
            if (offset >= _riffEnd)
                throw new InvalidDataException("The WebP container has no valid image bitstream.");
            if (_riffEnd - offset < 8)
                throw new InvalidDataException("The WebP RIFF chunk header is truncated.");
            if (input.Length - offset < 8)
                return OperationStatus.NeedMoreData;

            int at = (int)offset;
            ReadOnlySpan<byte> type = input.Slice(at, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(at + 4, 4));
            long payloadEnd = offset + 8 + chunkLength;
            long next = payloadEnd + (chunkLength & 1);
            if (next > _riffEnd)
                throw new InvalidDataException("A WebP RIFF chunk exceeds the declared container size.");
            if (chunkLength > int.MaxValue)
                throw new InvalidDataException("A WebP RIFF chunk is longer than an array can hold.");
            int length = (int)chunkLength;
            int payload = at + 8;

            if (type.SequenceEqual("VP8X"u8))
            {
                if (_extended || length < 10)
                    throw new InvalidDataException("The WebP VP8X chunk is invalid.");
                if (input.Length - payload < 10)
                    return OperationStatus.NeedMoreData;
                byte flags = input[payload];
                if ((flags & 0x02) != 0)
                    return StartWhole(input, isFinal, out info, out width, out height);
                _extended = true;
                _alphaFlag = (flags & 0x10) != 0;
                _canvasWidth = 1 + ReadUInt24(input.Slice(payload + 4, 3));
                _canvasHeight = 1 + ReadUInt24(input.Slice(payload + 7, 3));
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                if (length < 10)
                    throw new InvalidDataException("The VP8 key-frame header is invalid or truncated.");
                if (input.Length - payload < 10)
                    return OperationStatus.NeedMoreData;
                return StartLossy(input, payload, length, isFinal, out consumed, out info, out width, out height);
            }
            else if (type.SequenceEqual("VP8L"u8) || type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
            {
                // Lossless and animated images need the whole image (or file) before their first row.
                return StartWhole(input, isFinal, out info, out width, out height);
            }
            else if (type.SequenceEqual("ALPH"u8))
            {
                if (_alphaOffset >= 0)
                    throw new InvalidDataException("The WebP contains more than one ALPH chunk.");
                (_alphaOffset, _alphaLength) = (payload, length);
            }
            else if (type.SequenceEqual("ICCP"u8))
            {
                if (_iccOffset >= 0)
                    throw new InvalidDataException("The WebP contains more than one ICC profile.");
                (_iccOffset, _iccLength) = (payload, length);
            }
            else if (type.SequenceEqual("EXIF"u8))
            {
                if (_exifOffset >= 0)
                    throw new InvalidDataException("The WebP contains more than one EXIF chunk.");
                (_exifOffset, _exifLength) = (payload, length);
            }
            else if (type.SequenceEqual("XMP "u8))
            {
                if (_hasXmp)
                    throw new InvalidDataException("The WebP contains more than one XMP chunk.");
                _hasXmp = true;
            }

            _walkOffset = next;
        }
    }

    // A variant this reader does not stream: wait for the whole file, then let the span decoder have it.
    private OperationStatus StartWhole(ReadOnlySpan<byte> input, bool isFinal, out ImageInfo info, out int width, out int height)
    {
        _buffered = true;
        return ReadWhole(input, isFinal, out info, out width, out height);
    }

    private OperationStatus ReadWhole(ReadOnlySpan<byte> input, bool isFinal, out ImageInfo info, out int width, out int height)
    {
        info = default;
        width = height = 0;
        if (!isFinal)
            return OperationStatus.NeedMoreData;
        if (!WebpImageDecoder.TryReadInfo(input, out info))
            throw new InvalidDataException("The WebP container is invalid.");
        UseBufferedFallback(WebpImageDecoder.TryDecodeInto, _options);
        (width, height) = (_options ?? DecodeOptions.Default).GetOutputSize(info);
        return OperationStatus.Done;
    }

    // The VP8 chunk's first ten bytes are at input[payload]: the checks WebpContainer makes, then the streaming state.
    private OperationStatus StartLossy(ReadOnlySpan<byte> input, int payload, int length, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = 0;
        info = default;
        width = height = 0;
        ReadOnlySpan<byte> frame = input.Slice(payload, 10);
        if ((frame[0] & 1) != 0 || frame[3] != 0x9d || frame[4] != 0x01 || frame[5] != 0x2a)
            throw new InvalidDataException("The VP8 key-frame header is invalid or truncated.");
        int imageWidth = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) & 0x3fff;
        int imageHeight = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) & 0x3fff;
        if (imageWidth == 0 || imageHeight == 0)
            throw new InvalidDataException("The VP8 dimensions must be nonzero.");
        int firstPartitionLength = (frame[0] | frame[1] << 8 | frame[2] << 16) >> 5;
        if (firstPartitionLength > length - 10)
            throw new InvalidDataException("The VP8 first partition exceeds the frame.");
        if (!_extended)
            (_canvasWidth, _canvasHeight) = (imageWidth, imageHeight);
        else if (imageWidth != _canvasWidth || imageHeight != _canvasHeight)
            throw new InvalidDataException(
                $"The WebP VP8X canvas ({_canvasWidth}x{_canvasHeight}) does not match its image bitstream ({imageWidth}x{imageHeight}).");
        if ((long)imageWidth * imageHeight > WebpLimits.MaximumPixels)
            throw new InvalidDataException("The WebP dimensions exceed the supported pixel limit.");
        if (!IsFullImage(_options))
            return StartWhole(input, isFinal, out info, out width, out height);

        bool hasAlpha = _alphaFlag || _alphaOffset >= 0;
        ReadOnlySpan<byte> icc = _iccOffset >= 0 ? input.Slice(_iccOffset, _iccLength) : default;
        info = new ImageInfo(imageWidth, imageHeight, hasAlpha ? 4 : 3, SampleFormat.UInt8)
        {
            Format = ImageFormat.Webp,
            Orientation = _exifOffset >= 0 ? ExifOrientation.Read(input.Slice(_exifOffset, _exifLength)) : ImageOrientation.Normal,
            FrameCount = 1,
            ColorEncoding = WebpImageDecoder.EncodingOf(icc),
        };

        // The ALPH chunk is buffered (it precedes this one) and is consumed with the header: decode it now.
        if (_alphaOffset >= 0)
            _alpha = WebpDecoder.DecodeAndUnfilterAlpha(input.Slice(_alphaOffset, _alphaLength), imageWidth, imageHeight);

        (_width, _height) = (imageWidth, imageHeight);
        _vp8Length = length;
        _vp8End = payload + (long)length + (length & 1);
        _firstPartitionLength = firstPartitionLength;
        int rgbaRow = checked(imageWidth * 4);
        _band = WebpBufferPool<byte>.Rent(checked(rgbaRow * BandRows));
        _tRows = WebpBufferPool<ushort>.Rent(checked(((imageWidth + 1) >> 1) * 2));
        _chromaRows = WebpBufferPool<byte>.Rent(checked(imageWidth * 2));
        consumed = payload;
        (width, height) = (imageWidth, imageHeight);
        return OperationStatus.Done;
    }

    protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        Span<byte> destination, int stride, int rowCount, out int rowsWritten)
    {
        consumed = 0;
        rowsWritten = 0;
        try
        {
            while (true)
            {
                while (_bandNext < _bandEnd && rowsWritten < rowCount)
                {
                    // The last row waits for the rest of the container: a file the span decoder refuses fails here.
                    if (_bandNext == _height - 1 && !_trailerDone && !ReadTrailer(input, isFinal, ref consumed))
                        return OperationStatus.NeedMoreData;
                    WriteRow(_bandNext, destination.Slice(rowsWritten * stride));
                    _bandNext++;
                    rowsWritten++;
                }

                if (rowsWritten == rowCount)
                    return OperationStatus.Done;
                if (!DecodeMacroblockRow(input, isFinal, ref consumed))
                    return OperationStatus.NeedMoreData;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or OverflowException)
        {
            throw Invalid(e);
        }
    }

    // Decodes the next macroblock row into the band (or, first, reads the frame header). False when the input ends
    // inside what it needs; nothing is consumed or changed then.
    private bool DecodeMacroblockRow(ReadOnlySpan<byte> input, bool isFinal, ref int consumed)
    {
        ReadOnlySpan<byte> data = input[consumed..];
        if (!_frameStarted)
        {
            int headerBytes = 10 + _firstPartitionLength;
            if (data.Length < headerBytes)
                return Starved(isFinal);
            Vp8Decoder.ReadFrameTag(data, _vp8Length, _width, _height);
            _firstPartition = WebpBufferPool<byte>.Rent(Math.Max(_firstPartitionLength, 1));
            data.Slice(10, _firstPartitionLength).CopyTo(_firstPartition);
            var header = new Vp8BoolDecoder(_firstPartition.AsSpan(0, _firstPartitionLength));
            _frame = Vp8Decoder.FrameState.Create(ref header, _width, _height, WebpAblation.None);
            _frameStarted = true;
            _header = Cursor.Fresh(0, _firstPartitionLength);
            _header.Save(header);
            _tokenLength = _vp8Length - headerBytes;
            consumed += headerBytes;
            return true;
        }

        if (_frame.PartitionCount == 1)
        {
            int left = _tokenLength - _tokenConsumed;
            int available = Math.Min(data.Length, left);
            bool complete = available == left;
            if (!complete)
            {
                if (isFinal)
                    throw Truncated();
                if (available < _retryWindow)
                    return false;
            }

            var header = _header.Resume(_firstPartition!);
            var tokens = new Vp8BoolDecoder(data[..available], _partitionState.Value, _partitionState.Bits, _partitionState.Range);
            _frame.SaveContexts();
            // Over a window that ends before the partition does, zero bits past it are not the stream's: a row that
            // read them is decoded again once more of the partition is there. A starved row costs its decode time,
            // so the next attempt waits for twice the window.
            _frame.ReconstructRow(ref header, ref tokens);
            if (!complete && tokens.Padded)
            {
                _frame.RestoreContexts();
                _retryWindow = (int)Math.Min((long)available * 2 + 64, int.MaxValue);
                return false;
            }

            _header.Save(header);
            tokens.Load(out _partitionState.Value, out _partitionState.Bits, out _partitionState.Range);
            int used = available - tokens.Remaining;
            _tokenConsumed += used;
            consumed += used;
            _retryWindow = 0;
        }
        else
        {
            // Several partitions: the whole chunk first (row y reads partition y mod n), never consumed until the end.
            if (data.Length < _tokenLength)
                return Starved(isFinal);
            ReadOnlySpan<byte> tokenData = data[.._tokenLength];
            int count = _frame.PartitionCount;
            if (_partitions is null)
            {
                Span<int> offsets = stackalloc int[8];
                Span<int> lengths = stackalloc int[8];
                Vp8Decoder.ReadPartitionBounds(tokenData, count, offsets, lengths);
                var partitions = new Cursor[count];
                for (int i = 0; i < count; i++)
                    partitions[i] = Cursor.Fresh(offsets[i], lengths[i]);
                _partitions = partitions;
            }

            ref Cursor cursor = ref _partitions[_frame.MacroblockY & (count - 1)];
            var header = _header.Resume(_firstPartition!);
            var tokens = cursor.Resume(tokenData);
            _frame.ReconstructRow(ref header, ref tokens);
            _header.Save(header);
            cursor.Save(tokens);
        }

        _bandFirst = _frame.Emitted;
        var sink = new WebpDecoder.RgbaSink(_band, _width * 4, _width, _height, fancy: true,
            _alpha is null ? default : _alpha.AsSpan(0, _width * _height), _tRows!, _chromaRows!, outputRow0: _bandFirst);
        _frame.FinishRow(ref sink, int.MaxValue);
        _bandEnd = _frame.Emitted;
        _bandNext = _bandFirst;
        if (_frame.MacroblockY == _frame.MacroblockHeight)
        {
            _frame.Dispose();
            _frameDone = true;
            _trailerNext = _vp8End;
        }

        return true;
    }


    private static bool Starved(bool isFinal) => isFinal ? throw Truncated() : false;

    private static InvalidDataException Truncated() => new("The WebP stream ends inside its VP8 chunk.");

    // Skips the rest of the VP8 chunk and walks the chunks after it to the RIFF end, applying WebpContainer's rules.
    // True once at the end; false when the input ends first.
    private bool ReadTrailer(ReadOnlySpan<byte> input, bool isFinal, ref int consumed)
    {
        while (true)
        {
            long at = Input.Position + consumed;
            if (at < _trailerNext)
            {
                int skip = (int)Math.Min(_trailerNext - at, input.Length - consumed);
                consumed += skip;
                at += skip;
                if (at < _trailerNext)
                    return isFinal ? throw new InvalidDataException("The WebP RIFF size exceeds the supplied data.") : false;
            }

            if (_trailerNext == _riffEnd)
            {
                _trailerDone = true;
                return true;
            }

            if (_riffEnd - _trailerNext < 8)
                throw new InvalidDataException("The WebP RIFF chunk header is truncated.");
            ReadOnlySpan<byte> chunk = input[consumed..];
            if (chunk.Length < 8)
                return isFinal ? throw new InvalidDataException("The WebP RIFF size exceeds the supplied data.") : false;
            ReadOnlySpan<byte> type = chunk[..4];
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
            long next = _trailerNext + 8 + chunkLength + (chunkLength & 1);
            if (next > _riffEnd)
                throw new InvalidDataException("A WebP RIFF chunk exceeds the declared container size.");
            if (chunkLength > int.MaxValue)
                throw new InvalidDataException("A WebP RIFF chunk is longer than an array can hold.");

            if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8))
                throw new InvalidDataException("The WebP contains more than one image bitstream.");
            if (type.SequenceEqual("ALPH"u8))
                throw new InvalidDataException(_alphaOffset >= 0
                    ? "The WebP contains more than one ALPH chunk."
                    : "The WebP stores its ALPH chunk after the image bitstream, which a streaming decoder cannot apply.");
            if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8))
                throw new InvalidDataException("The WebP has animation chunks without the VP8X animation flag.");
            if (type.SequenceEqual("VP8X"u8))
            {
                if (_extended || chunkLength < 10)
                    throw new InvalidDataException("The WebP VP8X chunk is invalid.");
                if (chunk.Length < 18)
                    return isFinal ? throw new InvalidDataException("The WebP RIFF size exceeds the supplied data.") : false;
                if ((chunk[8] & 0x02) != 0)
                    throw new InvalidDataException(
                        "The WebP stores an animated VP8X chunk after the image bitstream, which a streaming decoder cannot apply.");
                if (1 + ReadUInt24(chunk.Slice(12, 3)) != _width || 1 + ReadUInt24(chunk.Slice(15, 3)) != _height)
                    throw new InvalidDataException("The WebP VP8X canvas does not match its image bitstream.");
                _extended = true;
            }
            else if (type.SequenceEqual("ICCP"u8))
            {
                if (_iccOffset >= 0)
                    throw new InvalidDataException("The WebP contains more than one ICC profile.");
                _iccOffset = int.MaxValue;
            }
            else if (type.SequenceEqual("EXIF"u8))
            {
                if (_exifOffset >= 0)
                    throw new InvalidDataException("The WebP contains more than one EXIF chunk.");
                _exifOffset = int.MaxValue;
            }
            else if (type.SequenceEqual("XMP "u8))
            {
                if (_hasXmp)
                    throw new InvalidDataException("The WebP contains more than one XMP chunk.");
                _hasXmp = true;
            }

            consumed += 8;
            _trailerNext = next;
        }
    }

    private void WriteRow(int row, Span<byte> destination)
    {
        int rgbaRow = _width * 4;
        ReadOnlySpan<byte> source = _band.AsSpan((row - _bandFirst) * rgbaRow, rgbaRow);
        if (Format == PixelFormat.Rgba32)
            source.CopyTo(destination);
        else
            PixelConverter.ConvertRow(source, PixelFormat.Rgba32, destination, Format, _width);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    private static InvalidDataException Invalid(Exception e) =>
        new(e is NotSupportedException ? $"The WebP uses a feature this decoder does not support: {e.Message}"
            : $"The WebP data is invalid: {e.Message}", e);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_frameStarted && !_frameDone)
                _frame.Dispose();
            ReturnTo(ref _firstPartition);
            ReturnTo(ref _alpha);
            ReturnTo(ref _band);
            ReturnTo(ref _chromaRows);
            if (_tRows is not null)
                WebpBufferPool<ushort>.Return(_tRows);
            _tRows = null;
        }

        base.Dispose(disposing);
    }

    private static void ReturnTo(ref byte[]? buffer)
    {
        if (buffer is not null)
            WebpBufferPool<byte>.Return(buffer);
        buffer = null;
    }
}
