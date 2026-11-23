using System;
using System.Buffers;
using System.IO;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// The streaming JPEG reader (<see cref="JpegImageDecoder.CreateRowReader"/>). A sequential frame whose first scan
/// interleaves every component — every baseline file in practice — is decoded as it arrives, MCU row by MCU row, holding
/// the engine's two-MCU-row strips and the compressed bytes of about one MCU row. Its rows are exactly
/// <see cref="JpegImageDecoder.TryDecodeInto"/>'s, at full size or a DCT-domain scale of 1/2, 1/4 or 1/8. Progressive
/// frames, frames split over several scans, frames with no scan, and the options it does not do on the fly (a region,
/// orientation, a scale past 1/8) take the buffered fallback.
/// </summary>
internal sealed class JpegRowReader : StreamingRowReader
{
    /// <summary>Rows decoded at a time when the output format is converted from the engine's.</summary>
    private const int ConvertBand = 16;

    /// <summary>
    /// The most rows one <see cref="DecodeRows"/> call writes. The driver checks its cancellation token between calls, and
    /// the engine has none of its own here, so a large request over input that is all buffered is cut into calls of this
    /// many rows: a call that stops at this limit reports NeedMoreData with rows written, which makes the driver check the
    /// token and call again without reading.
    /// </summary>
    private const int YieldBand = 256;

    private readonly DecodeOptions? _options;
    private JpegCore.Suspended? _core;
    private int _headerMin;           // the input length to wait for before trying the header again
    private PixelFormat _coreFormat;  // what the engine writes: Format, or Gray8 / Rgb24 converted to it
    private byte[]? _band;            // _coreFormat rows, when they are converted
    private bool _failed;

    public JpegRowReader(StreamInput input, PixelFormat format, DecodeOptions? options)
        : base(input, format)
    {
        _options = options;
    }

    protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        out ImageInfo info, out int width, out int height)
    {
        consumed = 0;
        info = default;
        width = height = 0;
        if (!isFinal && input.Length < _headerMin)
            return OperationStatus.NeedMoreData;

        // The header segments up to the first SOS, as JpegSegmentScanner reads them: the metadata in ImageInfo comes from
        // them, so they must all be here before it is built (a cheap walk, before the engine parses anything).
        var prefix = input.Length;
        if (!isFinal)
        {
            switch (WalkHeader(input, out var end))
            {
                case Walk.NeedMore:
                    _headerMin = Grow(input.Length);
                    return OperationStatus.NeedMoreData;
                case Walk.Found:
                    prefix = end;
                    break;
            }
        }

        var scale = StreamingScale(_options);
        var core = new JpegCore(input, scale == 0 ? 1 : scale);
        var keep = false;
        try
        {
            bool streams;
            JpegInfo jpeg;
            try
            {
                streams = core.StreamHeader(out jpeg);
            }
            catch (InvalidDataException) when (!isFinal && core.Overrun)
            {
                _headerMin = Grow(input.Length);
                return OperationStatus.NeedMoreData;
            }

            if (!isFinal && core.Overrun)
            {
                _headerMin = Grow(input.Length);
                return OperationStatus.NeedMoreData;
            }

            info = JpegImageDecoder.ToImageInfo(jpeg, input[..prefix]);
            if (!streams || scale == 0)
            {
                UseBufferedFallback(JpegImageDecoder.TryDecodeInto, _options);
                (width, height) = (_options ?? DecodeOptions.Default).GetOutputSize(info);
                return OperationStatus.Done;
            }

            _options?.Validate(info.Width, info.Height);
            // The formats JPEG does not write natively are the decode's Gray8 / Rgb24 rows converted, as TryDecodeInto
            // converts them.
            _coreFormat = Format.IsBasic8 ? Format : jpeg.Components == 1 ? PixelFormat.Gray8 : PixelFormat.Rgb24;
            core.BeginStreaming(JpegImageDecoder.ToJpegFormat(_coreFormat));
            (width, height) = (core.OutX, core.OutY);
            if (!Format.IsBasic8)
                _band = ArrayPool<byte>.Shared.Rent(checked(width * _coreFormat.BytesPerPixel * ConvertBand));
            consumed = core.Position;
            _core = new JpegCore.Suspended();
            core.Suspend(_core, consumed);
            keep = true;
            return OperationStatus.Done;
        }
        finally
        {
            if (!keep)
                core.ReturnBuffers();
        }
    }

    protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
        Span<byte> destination, int stride, int rowCount, out int rowsWritten)
    {
        if (_failed)
            throw new InvalidDataException("JPEG: an earlier read of this stream failed.");
        var suspended = _core ?? throw new InvalidOperationException("The header is not read.");
        var core = new JpegCore(input, suspended);
        var asked = rowCount;
        rowCount = Math.Min(rowCount, YieldBand);
        try
        {
            OperationStatus status;
            if (_band is null)
            {
                status = core.StreamRows(isFinal, destination, stride, rowCount, out rowsWritten);
            }
            else
            {
                var width = Width;
                var bandRow = width * _coreFormat.BytesPerPixel;
                var outRow = width * Format.BytesPerPixel;
                rowsWritten = 0;
                do
                {
                    status = core.StreamRows(isFinal, _band, bandRow, Math.Min(ConvertBand, rowCount - rowsWritten), out var got);
                    for (var i = 0; i < got; i++)
                    {
                        PixelConverter.ConvertRow(_band.AsSpan(i * bandRow, bandRow), _coreFormat,
                            destination.Slice((rowsWritten + i) * stride, outRow), Format, width);
                    }

                    rowsWritten += got;
                } while (status == OperationStatus.Done && rowsWritten < rowCount);
            }

            consumed = core.Position;
            core.Suspend(suspended, consumed);
            return status == OperationStatus.Done && rowsWritten < asked ? OperationStatus.NeedMoreData : status;
        }
        catch
        {
            // The engine's state is part-way through a unit: nothing can resume from it.
            _failed = true;
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_core is { } suspended)
            {
                _core = null;
                new JpegCore(default, suspended).ReturnBuffers();
            }

            if (_band is not null)
            {
                ArrayPool<byte>.Shared.Return(_band);
                _band = null;
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>The DCT-domain scale the streaming decode runs at for <paramref name="options"/>; 0 when they need the
    /// buffered fallback.</summary>
    private static int StreamingScale(DecodeOptions? options)
    {
        if (options is null)
            return 1;
        if (options.ApplyOrientation || options.Region is not null)
            return 0;
        return options.ScaleDenominator is 1 or 2 or 4 or 8 ? options.ScaleDenominator : 0;
    }

    private static int Grow(int length) => (int)Math.Min(Array.MaxLength, length + (long)length / 4 + 1);

    private enum Walk
    {
        NeedMore,
        Found,
        Irregular,
    }

    /// <summary>
    /// <see cref="JpegSegmentScanner.Scan"/>'s walk over the header segments: <see cref="Walk.Found"/> with the end of the
    /// first SOS segment (or of an EOI before it); <see cref="Walk.NeedMore"/> where the scanner would run off the end
    /// of <paramref name="jpeg"/>; <see cref="Walk.Irregular"/> where it would throw whatever followed.
    /// </summary>
    private static Walk WalkHeader(ReadOnlySpan<byte> jpeg, out int end)
    {
        end = 0;
        if (jpeg.Length < 2)
            return jpeg.Length == 0 || jpeg[0] == 0xFF ? Walk.NeedMore : Walk.Irregular;
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return Walk.Irregular;
        var pos = 2;
        while (true)
        {
            if (pos >= jpeg.Length)
                return Walk.NeedMore;
            if (jpeg[pos] != 0xFF)
                return Walk.Irregular;
            while (pos + 1 < jpeg.Length && jpeg[pos + 1] == 0xFF)
                pos++;
            if (pos + 1 >= jpeg.Length)
                return Walk.NeedMore;
            var marker = jpeg[pos + 1];
            if (marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                if (marker == 0xD9)
                {
                    end = pos + 2;
                    return Walk.Found;
                }

                pos += 2;
                continue;
            }

            if (pos + 4 > jpeg.Length)
                return Walk.NeedMore;
            var length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (length < 2)
                return Walk.Irregular;
            if (pos + 2 + length > jpeg.Length)
                return Walk.NeedMore;
            if (marker == JpegSegmentScanner.Sos)
            {
                end = pos + 2 + length;
                return Walk.Found;
            }

            pos += 2 + length;
        }
    }
}
