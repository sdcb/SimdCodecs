using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// <see cref="IImageFrameReader"/> for AVIF (<see cref="AvifImageDecoder.TryOpenFrames"/>).
/// A still AVIF presents a single frame decoded identically to <see cref="AvifImageDecoder.TryDecode"/>;
/// an animated <c>avis</c> sequence decodes frames from its <c>moov</c> primary (and optional alpha) tracks,
/// reporting per-frame durations from <c>stts</c> and loop count from <c>elst</c>.
/// </summary>
internal sealed class AvifFrameReader : IImageFrameReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly ImageInfo _frameInfo;
    private readonly ImageMetadata _metadata;
    private readonly AvifSequence? _sequence;
    private AvifSequenceDecoder? _decoder;
    private bool _disposed;

    private AvifFrameReader(
        ReadOnlyMemory<byte> data,
        ImageInfo frameInfo,
        ImageMetadata metadata,
        AvifSequence? sequence,
        AvifSequenceDecoder? decoder)
    {
        _data = data;
        _frameInfo = frameInfo with { FrameCount = 1 };
        _metadata = metadata;
        _sequence = sequence;
        _decoder = decoder;
        FrameCount = sequence is not null ? sequence.FrameCount : 1;
        LoopCount = sequence?.LoopCount;
    }

    internal static bool TryOpen(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out AvifFrameReader? reader)
    {
        reader = null;
        if (!AvifImageDecoder.CanDecode(data.Span))
            return false;

        if (!AvifImageDecoder.TryReadInfo(data.Span, out var info))
            return false;

        if (AvifSequence.TryParse(data.Span, out var seq) && seq.FrameCount > 1)
        {
            var meta = AvifImageDecoder.TryReadMetadata(data.Span, out var m)
                ? m
                : seq.IccProfile is { Length: > 0 } icc ? new ImageMetadata { Icc = icc } : ImageMetadata.Empty;
            var decoder = new AvifSequenceDecoder(seq, data.Span);
            reader = new AvifFrameReader(data, info, meta, seq, decoder);
            return true;
        }

        reader = new AvifFrameReader(data, info, ImageMetadata.Empty, null, null);
        return true;
    }

    public int FrameCount { get; }

    public int? LoopCount { get; }

    public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
    {
        Check(index);
        frame = new ImageFrameInfo(_frameInfo, _sequence is null ? null : _sequence.Durations[index]);
        return true;
    }

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        if (options is { ApplyOrientation: true })
            return ImageDecoderFallback.TryDecodeFrameOriented(this, index, out image, options, cancellationToken);
        if (_sequence is null || _decoder is null)
            return AvifImageDecoder.TryDecode(_data.Span, out image, options, cancellationToken);

        image = null;
        options?.Validate(_frameInfo.Width, _frameInfo.Height);
        try
        {
            var planes = _decoder.DecodeFramePlanes(_data.Span, index, cancellationToken);
            var raster = RenderToRaster(planes);
            image = DecodeOptions.Apply(raster, options);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        if (options is { ApplyOrientation: true })
            return ImageDecoderFallback.TryDecodeFrameIntoOriented(this, index, destination, stride, format, options, cancellationToken);
        if (_sequence is null || _decoder is null)
            return AvifImageDecoder.TryDecodeInto(_data.Span, destination, stride, format, options, cancellationToken);

        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeFrameInto(this, index, destination, stride, format, options, cancellationToken);

        options?.Validate(_frameInfo.Width, _frameInfo.Height);
        if (_frameInfo.ColorEncoding.Transfer is TransferFunction.Pq or TransferFunction.Hlg)
            return false;

        int w = _frameInfo.Width, h = _frameInfo.Height;
        if (!PixelConverter.TryGetLayout(w, h, format, stride, destination.Length, out int rowStride))
            return false;

        try
        {
            var planes = _decoder.DecodeFramePlanes(_data.Span, index, cancellationToken);
            using var raster = RenderToRaster(planes);
            return raster.TryCopyTo(destination, rowStride, format);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _decoder = null;
    }

    private void Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private RasterImage RenderToRaster(AvifDecodedPlanes planes)
    {
        var (w, h) = _sequence!.DisplaySize();
        int depth = planes.BitDepth;
        bool hasAlpha = planes.Alpha is not null;
        if (depth <= 8)
        {
            var rgba = new byte[checked((long)w * h * 4)];
            AvifDecoder.RenderRgba(_sequence.Transforms, planes, rgba, w, h);
            if (hasAlpha)
                return RasterImage.Wrap(w, h, 4, SampleFormat.UInt8, rgba, _frameInfo.ColorEncoding, metadata: _metadata);

            var rgb = new byte[checked((long)w * h * 3)];
            PixelConverter.Convert(rgba, w * 4, PixelFormat.Rgba32, rgb, w * 3, PixelFormat.Rgb24, w, h);
            return RasterImage.Wrap(w, h, 3, SampleFormat.UInt8, rgb, _frameInfo.ColorEncoding, metadata: _metadata);
        }

        var rgba16Bytes = new byte[checked((long)w * h * 4 * sizeof(ushort))];
        var rgba16 = MemoryMarshal.Cast<byte, ushort>(rgba16Bytes.AsSpan());
        AvifDecoder.RenderRgba16(_sequence.Transforms, planes, rgba16, w, h);
        if (hasAlpha)
            return RasterImage.Wrap(w, h, 4, SampleFormat.UInt16, rgba16Bytes, _frameInfo.ColorEncoding, bitDepth: depth < 16 ? depth : 0, metadata: _metadata);

        var rgb16Bytes = new byte[checked((long)w * h * 3 * sizeof(ushort))];
        var rgb16 = MemoryMarshal.Cast<byte, ushort>(rgb16Bytes.AsSpan());
        for (int i = 0; i < w * h; i++)
        {
            rgb16[i * 3] = rgba16[i * 4];
            rgb16[i * 3 + 1] = rgba16[i * 4 + 1];
            rgb16[i * 3 + 2] = rgba16[i * 4 + 2];
        }
        return RasterImage.Wrap(w, h, 3, SampleFormat.UInt16, rgb16Bytes, _frameInfo.ColorEncoding, bitDepth: depth < 16 ? depth : 0, metadata: _metadata);
    }
}
