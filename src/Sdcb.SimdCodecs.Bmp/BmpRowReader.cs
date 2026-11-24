using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Bmp;

/// <summary>
/// The streaming BMP reader (<see cref="BmpImageDecoder.CreateRowReader"/>): uncompressed rows (<c>BI_RGB</c>,
/// <c>BI_BITFIELDS</c>, <c>BI_ALPHABITFIELDS</c>, every depth and palette) decoded a band at a time with the span
/// decoder's row kernels, so its rows are <see cref="BmpImageDecoder.TryDecodeInto"/>'s byte for byte.
/// <list type="bullet">
/// <item>A top-down bitmap streams from any stream, front to back.</item>
/// <item>
/// A bottom-up bitmap stores its top row last. From a seekable stream the reader seeks to the last band of stored rows
/// (about <see cref="BandBytes"/>), then back a band at a time, and hands each band out last stored row first: memory
/// stays a band and each pixel byte is read once. From a stream whose bytes the input retains it reads through to the last
/// band; from a stream that cannot seek it falls back.
/// </item>
/// <item>
/// A 32-bit <c>BI_RGB</c> bitmap's fourth byte is alpha unless it is 0 in every pixel, which only the last pixel can
/// settle. Decoded to a format with alpha from a seekable stream, the reader scans every pixel's fourth byte first
/// (one forward pass over the pixels), then streams; from a stream that cannot seek it falls back. To a
/// format without alpha the byte is never read, and the bitmap streams as any other.
/// </item>
/// <item>
/// Everything else falls back (<see cref="StreamingRowReader.IsStreaming"/> false): RLE, Huffman 1D, <c>BI_PNG</c>,
/// output formats other than the 8-bit basic ones, decode options other than the whole image, a pixel offset that points
/// into the headers or more than <see cref="MaxPrefix"/> bytes in, and a V5 profile stored after the pixels (the image's
/// <see cref="ImageInfo.ColorEncoding"/> comes from the profile, so <see cref="StreamingRowReader.Info"/> could not be
/// known before the rows).
/// </item>
/// </list>
/// </summary>
internal sealed class BmpRowReader(StreamInput input, PixelFormat format, DecodeOptions? options) : StreamingRowReader(input, format)
{
    /// <summary>The bytes of stored rows a bottom-up band holds (at least one row).</summary>
    internal const int BandBytes = 256 * 1024;

    /// <summary>The furthest pixel offset streamed: the headers, palette and anything before the pixels are buffered whole.</summary>
    internal const int MaxPrefix = 1 << 20;

    private bool _fallback;
    private BmpHeader _h;
    private BmpPixels.RowKind _kind;
    private PixelFormat _target;
    private readonly byte[] _control = new byte[4];
    private uint _set;
    private uint[]? _lut;
    private BmpPixels.Fields? _fields;
    private byte[]? _scratch;
    private int _tier;
    private int _stride, _lastRowLength;
    private long _regionLength; // the pixel bytes the decode reads: every stored row, the last one possibly short

    // The alpha scan's forward pass, and a bottom-up bitmap's first move to its last band.
    private bool _scanning, _scanAlpha, _forceOpaque, _seekPending;
    private long _scanned;
    private uint _alphaSeen;

    // Rows.
    private int _row;                // output rows written
    private int _bandStart, _next;   // bottom-up: the band's first stored row, and the next stored row to hand out

    protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = 0;
        info = default;
        width = height = 0;
        if (!_fallback)
        {
            var needed = BmpHeader.PrefixLength(input, out var pixelOffset);
            if (needed == 0)
            {
                BmpHeader.ReadFile(input); // throws the reason
                throw new InvalidDataException("Not a BMP file.");
            }
            if (needed < 0 || needed > input.Length)
            {
                if (needed > MaxPrefix || isFinal)
                    _fallback = true;
                else
                    return OperationStatus.NeedMoreData;
            }
            if (!_fallback)
            {
                var h = BmpHeader.ReadFile(input, prefix: true);
                if (TryStream(h, pixelOffset))
                {
                    info = BmpImageDecoder.InfoOf(input, h);
                    (width, height) = (h.Width, h.Height);
                    consumed = h.PixelOffset;
                    Plan(input, h);
                    return OperationStatus.Done;
                }
                _fallback = true;
            }
        }

        // The fallback decodes the whole file, so its header is read from the whole file too: wait for the end.
        if (!isFinal)
            return OperationStatus.NeedMoreData;
        if (!BmpImageDecoder.TryReadInfo(input, out info))
        {
            BmpHeader.ReadFile(input); // throws the reason, when it is the header
            throw new InvalidDataException("The bitmap could not be read.");
        }
        UseBufferedFallback(BmpImageDecoder.TryDecodeInto, options);
        (width, height) = (options ?? DecodeOptions.Default).GetOutputSize(info);
        return OperationStatus.Done;
    }

    // Whether this header streams, and whether it needs the seekable forward pass.
    private bool TryStream(BmpHeader h, long pixelOffset)
    {
        if (!IsFullImage(options) || !Format.IsBasic8)
            return false;
        if (h.Compression is not (BmpCompression.Rgb or BmpCompression.Bitfields or BmpCompression.AlphaBitfields))
            return false;
        // An offset into the headers makes the whole-file parser derive the pixels' place from the file's length.
        if (h.PixelOffset != pixelOffset || h.IccBeyondPrefix)
            return false;
        // Going back needs a seekable stream, or one whose bytes the input retains (then the file is in memory anyway,
        // but the output frame is not).
        var seekable = Input.Length >= 0;
        var scanAlpha = h.AlphaUnlessAllZero && Format.HasAlpha;
        if ((!h.TopDown || scanAlpha) && !Input.CanRewind)
            return false;

        _stride = h.Stride;
        _lastRowLength = (int)(((long)h.Width * h.BitsPerPixel + 7) / 8);
        var needed = (long)_stride * (h.Height - 1) + _lastRowLength;
        if (seekable)
        {
            var available = Input.Length - h.PixelOffset;
            if (available < needed)
                throw new InvalidDataException($"The pixel data is truncated: {Math.Max(0, available)} of {needed} bytes.");
        }
        _regionLength = (long)_stride * (h.Height - 1) + _lastRowLength;
        _scanAlpha = scanAlpha;
        // A bottom-up bitmap without the scan starts at its last band: a seek forward, or a read through when the
        // stream cannot seek (StreamInput.Seek).
        _scanning = scanAlpha;
        _seekPending = !h.TopDown && !scanAlpha;
        return true;
    }

    private void Plan(ReadOnlySpan<byte> prefix, BmpHeader h)
    {
        _h = h;
        _tier = BmpKernels.BestTier;
        _kind = BmpPixels.PlanRows(h, Format, _control, out _set, out _target);
        if (h.BitsPerPixel <= 8)
        {
            _lut = new uint[256];
            BmpPixels.BuildPalette(prefix, h, _target, _lut);
        }
        _fields = BmpPixels.FieldsFor(_kind, h);
        if (_target != Format)
            _scratch = ArrayPool<byte>.Shared.Rent(h.Width * 4);
        _next = h.Height - 1;
        _bandStart = h.Height; // no band yet
    }

    protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        Span<byte> destination, int stride, int rowCount, out int rowsWritten)
    {
        consumed = rowsWritten = 0;
        if (_scanning)
        {
            var take = (int)Math.Min(input.Length, _regionLength - _scanned);
            if (_scanAlpha)
                _alphaSeen |= AlphaOr(input[..take], _scanned);
            _scanned += take;
            if (_scanned < _regionLength)
            {
                consumed = take;
                return OperationStatus.NeedMoreData;
            }
            // The whole raster has gone by: go back to the first rows handed out.
            Input.Consume(take);
            _scanning = false;
            _forceOpaque = _scanAlpha && (_alphaSeen == 0);
            if (_h.TopDown)
            {
                Input.Rewind(_h.PixelOffset);
            }
            else
            {
                StartBand(_h.Height);
            }
            input = Input.Buffered;
        }
        if (_seekPending)
        {
            // Nothing is consumed: the seek moved the input itself, or consumed what it read through.
            if (!StartBand(_h.Height))
                return OperationStatus.NeedMoreData;
            _seekPending = false;
            input = Input.Buffered;
        }
        return _h.TopDown
            ? TopDownRows(input, ref consumed, destination, stride, rowCount, ref rowsWritten)
            : BottomUpRows(input, destination, stride, rowCount, ref rowsWritten);
    }

    private OperationStatus TopDownRows(ReadOnlySpan<byte> input, ref int consumed,
        Span<byte> destination, int stride, int rowCount, ref int rowsWritten)
    {
        var height = _h.Height;
        while (rowsWritten < rowCount)
        {
            // The last row may stop short of its padding, which some writers leave out; the kernels read only a row's
            // pixels, so it decodes from its pixel bytes whatever follows them.
            var available = input.Length - consumed;
            var last = _row == height - 1;
            if (available < (last ? _lastRowLength : _stride))
                return OperationStatus.NeedMoreData;
            var length = last ? Math.Min(available, _stride) : _stride;
            Row(input.Slice(consumed, length), destination.Slice(rowsWritten * stride));
            consumed += length;
            rowsWritten++;
            if (++_row == height)
                return OperationStatus.Done;
        }
        return OperationStatus.Done;
    }

    private OperationStatus BottomUpRows(ReadOnlySpan<byte> input, Span<byte> destination, int stride, int rowCount,
        ref int rowsWritten)
    {
        // The input starts at the band's first stored row; nothing is consumed until the band is done.
        while (rowsWritten < rowCount)
        {
            var bandLength = BandLength(_bandStart, _next);
            if (input.Length < bandLength)
                return OperationStatus.NeedMoreData;
            var at = (long)(_next - _bandStart) * _stride;
            var length = _next == _h.Height - 1 ? _lastRowLength : _stride;
            Row(input.Slice((int)at, length), destination.Slice(rowsWritten * stride));
            rowsWritten++;
            _row++;
            if (--_next < _bandStart)
            {
                if (_row == _h.Height)
                    return OperationStatus.Done;
                StartBand(_bandStart);
                input = Input.Buffered;
            }
        }
        return OperationStatus.Done;
    }

    // Moves the input to the band that ends before stored row `end`; false while a read-through is still on its way.
    private bool StartBand(int end)
    {
        var rows = Math.Max(1, BandBytes / _stride);
        _bandStart = Math.Max(0, end - rows);
        _next = end - 1;
        return Input.Seek(_h.PixelOffset + (long)_bandStart * _stride, BandLength(_bandStart, _next));
    }

    // The bytes from stored row `start` through `last` (the file's last row possibly short).
    private int BandLength(int start, int last) =>
        (int)((long)(last - start) * _stride + (last == _h.Height - 1 ? _lastRowLength : _stride));

    private void Row(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var width = _h.Width;
        var row = destination[..(width * Format.BytesPerPixel)];
        var output = _scratch is null ? row : _scratch.AsSpan(0, width * 4);
        BmpPixels.DecodeRow(_kind, in _h, _target, source, output, _lut, _control, _set, _fields, _tier);
        if (_scratch is not null)
            PixelConverter.ConvertRow(output, 4, SampleFormat.UInt8, row, Format, width);
        if (_forceOpaque)
        {
            for (var x = 3; x < row.Length; x += 4) row[x] = 255;
        }
    }

    // The fourth byte of every 32-bit pixel in `bytes`, which starts `offset` bytes into the raster, ORed together:
    // the bytes before the first whole pixel and after the last one a byte at a time, the whole pixels as 32-bit words
    // ORed with Vector<uint> (any width, or one element without vector hardware: OR is the same in every order).
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static uint AlphaOr(ReadOnlySpan<byte> bytes, long offset)
    {
        uint seen = 0;
        var head = (int)Math.Min(bytes.Length, (4 - (offset & 3)) & 3);
        for (var i = (int)((3 - (offset & 3)) & 3); i < head; i += 4)
            seen |= bytes[i];
        var words = MemoryMarshal.Cast<byte, uint>(bytes[head..]);
        var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(words);
        var acc = Vector<uint>.Zero;
        foreach (var v in vectors) acc |= v;
        uint all = 0;
        for (var i = 0; i < Vector<uint>.Count; i++) all |= acc[i];
        for (var i = vectors.Length * Vector<uint>.Count; i < words.Length; i++) all |= words[i];
        // A part pixel after the words holds no fourth byte.
        return seen | all >> 24;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _scratch is not null)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = null;
        }
        base.Dispose(disposing);
    }
}
