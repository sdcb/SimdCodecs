using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Pnm;

/// <summary>
/// The streaming decoder of P1 to P7 (<see cref="PnmImageDecoder.CreateRowReader"/>). The header is scanned as it
/// arrives until its last field and terminator are there, then parsed by <see cref="PnmHeader.Read"/> exactly as the
/// span decoder parses it. A raw row is decoded once all its bytes are buffered; a plain raster is read token by token
/// into a row of samples, its state (inside a number, inside a comment) carried across reads so that no token is split
/// and no comment is buffered. Rows equal <see cref="PnmImageDecoder.TryDecodeInto"/>'s: the same row decoders, and the
/// same acceptance (bytes after the last row are not read).
/// </summary>
internal sealed class PnmRowReader(StreamInput input, PixelFormat format, DecodeOptions? options) : StreamingRowReader(input, format)
{
    /// <summary>The longest header read, comments included: it is buffered whole before it is parsed.</summary>
    public const int MaxHeaderBytes = 1 << 20;

    private enum Scan { Space, Token, Keyword, FieldSpace, Digits, TupleLine, EndLine }

    // The header scan, resumed where the previous call stopped: each call sees the input from the first byte, with the
    // bytes of the call before unchanged.
    private int _scan = 2;
    private Scan _state;
    private bool _comment;
    private int _tokens, _keywordStart, _digits;

    // The raster.
    private PnmInfo _info;
    private int _rowBytes;        // a raw row's bytes
    private bool _sixteen;        // 16-bit samples into a 16-bit layout: rows are converted from native 16-bit samples
    private byte[]? _scratch;     // a raw row's chunk scratch, or a plain row's samples

    // A plain raster's position: the sample of the current row, and a number being read.
    private int _sample;
    private bool _inNumber;
    private uint _value;

    protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = width = height = 0;
        info = default;
        if ((input.Length > 0 && input[0] != 'P')
            || (input.Length > 1 && input[1] is < (byte)'1' or > (byte)'7')
            || (input.Length > 2 && !PnmHeader.IsSpace(input[2])))
            throw new InvalidDataException("Not a PBM, PGM, PPM or PAM image.");
        if (!isFinal && (input.Length < 3 || !ScanHeader(input)))
        {
            if (_scan > MaxHeaderBytes)
                throw new InvalidDataException($"The Netpbm header is longer than the {MaxHeaderBytes:N0} bytes a stream is read with.");
            return OperationStatus.NeedMoreData;
        }

        // The whole header is buffered: it parses as it does from the complete file.
        var pnm = PnmHeader.Read(input, out var offset, tupleType: false);
        info = PnmAdapter.ToImageInfo(pnm, pfm: false);
        (width, height) = (pnm.Width, pnm.Height);
        if (!IsFullImage(options))
            (width, height) = (options ?? DecodeOptions.Default).GetOutputSize(info);
        if ((long)width * Format.BytesPerPixel > Array.MaxLength)
            throw new InvalidDataException($"A {width}-pixel {Format} row does not fit in one buffer.");
        if (!IsFullImage(options))
        {
            UseBufferedFallback(PnmImageDecoder.TryDecodeInto, options);
            return OperationStatus.Done;
        }
        var rowBytes = PnmPixels.RowBytes(pnm);
        if (rowBytes > Array.MaxLength)
            throw new InvalidDataException($"A {pnm.Width}-pixel Netpbm row does not fit in one buffer.");
        _info = pnm;
        _rowBytes = (int)rowBytes;
        _sixteen = pnm.SampleFormat == SampleFormat.UInt16 && Format.Sample == SampleFormat.UInt16;
        consumed = offset;
        return OperationStatus.Done;
    }

    // True once the bytes scanned hold the header's last field and the byte after it (P1 to P6), or the newline after
    // ENDHDR (PAM); or once they hold something PnmHeader.Read rejects whatever follows (it then says why). Mirrors the
    // fields PnmHeader.Read takes: decimal tokens after whitespace and '#' comments, PAM keyword lines.
    private bool ScanHeader(ReadOnlySpan<byte> input)
    {
        var pam = input[1] == '7';
        var fields = input[1] is (byte)'1' or (byte)'4' ? 2 : 3;
        var p = _scan;
        try
        {
            while (p < input.Length)
            {
                var b = input[p];
                switch (_state)
                {
                    case Scan.Space or Scan.FieldSpace:
                        if (_comment)
                        {
                            if (b is (byte)'\n' or (byte)'\r') _comment = false;
                            p++;
                        }
                        else if (PnmHeader.IsSpace(b)) p++;
                        else if (b == '#') { _comment = true; p++; }
                        else if (_state == Scan.FieldSpace) { _state = Scan.Digits; _digits = 0; }
                        else if (pam) { _state = Scan.Keyword; _keywordStart = p; }
                        else _state = Scan.Token;
                        break;
                    case Scan.Token:
                        if (IsDigit(b)) p++;
                        else if (PnmHeader.IsSpace(b) || b == '#')
                        {
                            if (++_tokens == fields) return true;
                            _state = Scan.Space;
                        }
                        else return true;
                        break;
                    case Scan.Keyword:
                        if (!PnmHeader.IsSpace(b))
                        {
                            // The longest keyword is TUPLTYPE: anything longer is unknown.
                            if (p - _keywordStart >= 8) return true;
                            p++;
                            break;
                        }
                        var keyword = input[_keywordStart..p];
                        if (keyword.SequenceEqual("ENDHDR"u8)) _state = Scan.EndLine;
                        else if (keyword.SequenceEqual("TUPLTYPE"u8)) _state = Scan.TupleLine;
                        else if (keyword.SequenceEqual("WIDTH"u8) || keyword.SequenceEqual("HEIGHT"u8)
                            || keyword.SequenceEqual("DEPTH"u8) || keyword.SequenceEqual("MAXVAL"u8)) _state = Scan.FieldSpace;
                        else return true;
                        break;
                    case Scan.Digits:
                        if (IsDigit(b)) { p++; _digits++; }
                        else if (_digits > 0 && (PnmHeader.IsSpace(b) || b == '#')) _state = Scan.Space;
                        else return true;
                        break;
                    case Scan.TupleLine:
                        if (b is (byte)'\n' or (byte)'\r') _state = Scan.Space;
                        else p++;
                        break;
                    default: // EndLine: whitespace to the newline
                        if (b == '\n' || !PnmHeader.IsSpace(b)) return true;
                        p++;
                        break;
                }
            }
            return false;
        }
        finally
        {
            _scan = p;
        }
    }

    /// <summary>The scratch buffer's size (for tests).</summary>
    internal int ScratchBytes => _scratch?.Length ?? 0;

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        Span<byte> destination, int stride, int rowCount, out int rowsWritten)
    {
        consumed = rowsWritten = 0;
        var outRowBytes = _info.Width * Format.BytesPerPixel;
        if (!_info.IsPlain)
        {
            _scratch ??= ArrayPool<byte>.Shared.Rent(PnmPixels.RowScratchBytes);
            var scratch = _scratch.AsSpan(0, PnmPixels.RowScratchBytes);
            while (rowsWritten < rowCount)
            {
                if (input.Length - consumed < _rowBytes)
                    return OperationStatus.NeedMoreData;
                PnmPixels.DecodeRawRow(input.Slice(consumed, _rowBytes), _info, destination.Slice(rowsWritten * stride, outRowBytes),
                    Format, scratch);
                consumed += _rowBytes;
                rowsWritten++;
            }
            return OperationStatus.Done;
        }

        var samples = _info.Width * _info.Channels;
        var p = 0;
        while (rowsWritten < rowCount)
        {
            if (!ReadPlainRow(input, ref p, isFinal, samples))
            {
                consumed = p;
                return OperationStatus.NeedMoreData;
            }
            PixelConverter.ConvertRow(_scratch.AsSpan(0, samples * (_sixteen ? 2 : 1)), _info.Channels,
                _sixteen ? SampleFormat.UInt16 : SampleFormat.UInt8, destination.Slice(rowsWritten * stride, outRowBytes), Format, _info.Width);
            _sample = 0;
            rowsWritten++;
        }
        consumed = p;
        return OperationStatus.Done;
    }

    // Reads the current plain row's samples from input[p..] into _scratch, as PnmPixels' plain readers do: a number ends
    // at the first byte that is not a digit (or the stream's end), whitespace and comments separate. True once the row is
    // complete; false when the input ends first, with every byte up to p taken into the state.
    [MemberNotNull(nameof(_scratch))]
    private bool ReadPlainRow(ReadOnlySpan<byte> input, ref int p, bool isFinal, int samples)
    {
        _scratch ??= ArrayPool<byte>.Shared.Rent(Math.Min(samples * (_sixteen ? 2 : 1), 4096));
        var bitmap = _info.Format == PnmFormat.PlainBitmap;
        while (_sample < samples)
        {
            if (p == input.Length)
            {
                if (!isFinal) return false;
                if (!_inNumber) throw new InvalidDataException("Netpbm raster is truncated.");
                _inNumber = false;
                Emit(_value, samples);
                continue;
            }
            var b = input[p];
            if (_comment)
            {
                if (b is (byte)'\n' or (byte)'\r') _comment = false;
                p++;
            }
            else if (_inNumber)
            {
                if (IsDigit(b))
                {
                    // Anything above 65535 is above every maximum and clamps alike.
                    _value = Math.Min(_value * 10 + (uint)(b - '0'), 65536);
                    p++;
                }
                else
                {
                    _inNumber = false;
                    Emit(_value, samples);
                }
            }
            else if (PnmHeader.IsSpace(b)) p++;
            else if (b == '#') { _comment = true; p++; }
            else if (bitmap)
            {
                Reserve(samples);
                _scratch[_sample++] = b switch
                {
                    (byte)'0' => 255,
                    (byte)'1' => 0,
                    _ => throw new InvalidDataException("PBM plain raster has a character other than 0 or 1."),
                };
                p++;
            }
            else if (IsDigit(b))
            {
                _inNumber = true;
                _value = (uint)(b - '0');
                p++;
            }
            else
            {
                throw new InvalidDataException("Netpbm plain raster has a non-numeric sample.");
            }
        }
        return true;
    }

    // Makes room in _scratch for sample _sample. The scratch doubles as samples arrive (each takes at least one input
    // byte), up to a row, so a header alone never makes it allocate a row.
    [MemberNotNull(nameof(_scratch))]
    private void Reserve(int samples)
    {
        var sampleBytes = _sixteen ? 2 : 1;
        if (_scratch is not null && (_sample + 1) * sampleBytes <= _scratch.Length) return;
        var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)samples * sampleBytes, Math.Max(4096L, 2L * (_scratch?.Length ?? 0))));
        if (_scratch is not null)
        {
            _scratch.AsSpan(0, _sample * sampleBytes).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_scratch);
        }
        _scratch = grown;
    }

    // One plain sample, scaled as PnmPixels scales it for this output.
    private void Emit(uint value, int samples)
    {
        Reserve(samples);
        var max = (uint)_info.MaxValue;
        if (_sixteen)
            MemoryMarshal.Cast<byte, ushort>(_scratch.AsSpan())[_sample++] = PnmPixels.Scale16(value, max);
        else if (max > 255)
            _scratch[_sample++] = (byte)(PnmPixels.Scale16(value, max) >> 8);
        else if (max == 255)
            _scratch[_sample++] = (byte)Math.Min(value, 255);
        else
            _scratch[_sample++] = PnmPixels.Scale8(value, max);
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
