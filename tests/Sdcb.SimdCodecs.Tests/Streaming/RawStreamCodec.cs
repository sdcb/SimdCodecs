using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Tests.Streaming;

/// <summary>
/// A made-up streaming format for testing the streaming plumbing without a real codec:
/// <c>"SRAW" | u32 width | u32 height | u8 mode | RGBA rows | "END!"</c>. Mode 0 streams; mode 1 stands in for a variant a
/// codec cannot stream (progressive JPEG, Adam7) and takes the buffered fallback. Rows are converted to the requested
/// format with <see cref="PixelConverter.ConvertRow(ReadOnlySpan{byte}, PixelFormat, Span{byte}, PixelFormat, int)"/>.
/// Registered under <see cref="ImageFormat.Qoi"/>'s name only because a registration needs a defined format; its
/// signature collides with nothing.
/// </summary>
internal static class RawStreamCodec
{
    public const int HeaderLength = 13;
    private static ReadOnlySpan<byte> Magic => "SRAW"u8;
    private static ReadOnlySpan<byte> Trailer => "END!"u8;

    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Qoi,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        CreateRowReader = CreateRowReader,
    };

    /// <summary>The same codec without a streaming decoder (the facade then buffers it).</summary>
    public static ImageDecoderRegistration NonStreamingRegistration { get; } = new()
    {
        Format = ImageFormat.Qoi,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
    };

    /// <summary>Builds a file from tightly packed RGBA pixels.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height, bool fallback = false)
    {
        var file = new byte[HeaderLength + rgba.Length + Trailer.Length];
        Magic.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)height);
        file[12] = fallback ? (byte)1 : (byte)0;
        rgba.CopyTo(file.AsSpan(HeaderLength));
        Trailer.CopyTo(file.AsSpan(HeaderLength + rgba.Length));
        return file;
    }

    /// <summary>Seeded random RGBA pixels.</summary>
    public static byte[] RandomRgba(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 4];
        new Random(seed).NextBytes(pixels);
        return pixels;
    }

    public static bool CanDecode(ReadOnlySpan<byte> header) => header.StartsWith(Magic);

    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (data.Length < HeaderLength || !CanDecode(data)) return false;
        var w = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var h = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (w is 0 or > 1 << 16 || h is 0 or > 1 << 16 || data[12] > 1) return false;
        info = new ImageInfo((int)w, (int)h, 4, SampleFormat.UInt8) { Format = ImageFormat.Qoi };
        return true;
    }

    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator)
    {
        scaleDenominator = 1;
        var ok = TryReadInfo(data, out var info);
        (width, height) = (info.Width, info.Height);
        return ok;
    }

    private static bool TryGetRows(ReadOnlySpan<byte> data, out ImageInfo info, out ReadOnlySpan<byte> rows)
    {
        rows = default;
        if (!TryReadInfo(data, out info)) return false;
        var length = (long)info.Width * info.Height * 4;
        if (data.Length != HeaderLength + length + Trailer.Length || !data[^4..].SequenceEqual(Trailer)) return false;
        rows = data.Slice(HeaderLength, (int)length);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image, DecodeOptions? options,
        CancellationToken cancellationToken)
    {
        image = null;
        if (options is { IsFullImage: false } || !TryGetRows(data, out var info, out var rows)) return false;
        image = RasterImage.Wrap(info.Width, info.Height, 4, SampleFormat.UInt8, rows.ToArray());
        return true;
    }

    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options, CancellationToken cancellationToken)
    {
        if (options is { IsFullImage: false } || !TryGetRows(data, out var info, out var rows)) return false;
        if (!PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var step)) return false;
        for (var y = 0; y < info.Height; y++)
            PixelConverter.ConvertRow(rows.Slice(y * info.Width * 4, info.Width * 4), PixelFormat.Rgba32,
                destination.Slice(y * step, info.Width * format.BytesPerPixel), format, info.Width);
        return true;
    }

    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = TryReadInfo(data, out _) ? ImageMetadata.Empty : null;
        return metadata is not null;
    }

    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        thumbnail = null;
        return false;
    }

    public static StreamingRowReader CreateRowReader(StreamInput input, PixelFormat format, DecodeOptions? options) =>
        new Reader(input, format, options);

    private sealed class Reader(StreamInput input, PixelFormat format, DecodeOptions? options) : StreamingRowReader(input, format)
    {
        private int _width, _height, _row;
        private bool _trailerSeen;

        protected override OperationStatus DecodeHeader(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            out ImageInfo info, out int width, out int height)
        {
            consumed = 0;
            width = height = 0;
            info = default;
            if (input.Length < HeaderLength)
            {
                if (input.Length >= 4 && !CanDecode(input))
                    throw new InvalidDataException("Not a SRAW stream.");
                return OperationStatus.NeedMoreData;
            }
            if (!TryReadInfo(input, out info))
                throw new InvalidDataException("A malformed SRAW header.");
            (width, height) = (_width, _height) = (info.Width, info.Height);
            consumed = HeaderLength;
            if (input[12] == 1 || !IsFullImage(options))
            {
                UseBufferedFallback(TryDecodeInto, options);
                (width, height) = (options ?? DecodeOptions.Default).GetOutputSize(info);
            }
            return OperationStatus.Done;
        }

        protected override OperationStatus DecodeRows(ReadOnlySpan<byte> input, bool isFinal, out int consumed,
            Span<byte> destination, int stride, int rowCount, out int rowsWritten)
        {
            consumed = rowsWritten = 0;
            var rowBytes = _width * 4;
            while (rowsWritten < rowCount)
            {
                if (input.Length - consumed < rowBytes)
                    return OperationStatus.NeedMoreData;
                PixelConverter.ConvertRow(input.Slice(consumed, rowBytes), PixelFormat.Rgba32,
                    destination.Slice(rowsWritten * stride, _width * Format.BytesPerPixel), Format, _width);
                consumed += rowBytes;
                rowsWritten++;
                _row++;
            }
            if (_row == _height && !_trailerSeen)
            {
                // The trailer is checked with the last row, as a codec checks its end marker.
                if (input.Length - consumed < Trailer.Length)
                {
                    // Keep the last row unwritten until the trailer is there: back off it.
                    _row--;
                    rowsWritten--;
                    consumed -= rowBytes;
                    return OperationStatus.NeedMoreData;
                }
                if (!input.Slice(consumed, Trailer.Length).SequenceEqual(Trailer))
                    throw new InvalidDataException("The SRAW trailer is missing.");
                consumed += Trailer.Length;
                _trailerSeen = true;
            }
            return OperationStatus.Done;
        }
    }
}
