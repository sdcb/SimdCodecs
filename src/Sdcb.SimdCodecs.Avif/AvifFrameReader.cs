using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Avif.Av1;

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
            image = DecodeOptions.Apply(DecodeToRaster(index, cancellationToken), options);
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
            var (primary, alpha) = _decoder.DecodeFramePictures(_data.Span, index, cancellationToken);
            if (RendersDirectly(primary))
            {
                var (matrix, fullRange) = _decoder.ColorOf(primary);
                AvifDecoder.RenderPictureInto(primary, alpha, matrix, fullRange, destination, rowStride, format);
                return true;
            }
            using var raster = RenderToRaster(_decoder.DecodeFramePlanes(_data.Span, index, cancellationToken));
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

    /// <summary>
    /// Whether a frame renders straight from its decoded picture (<see cref="AvifDecoder.RenderPictureInto"/>): no
    /// transforms, and the picture is the display size.
    /// </summary>
    private bool RendersDirectly(Av1Picture primary) =>
        _sequence!.Transforms.Count == 0 && _sequence.DisplaySize() == (primary.Width, primary.Height);

    private RasterImage DecodeToRaster(int index, CancellationToken cancellationToken)
    {
        var (primary, alpha) = _decoder!.DecodeFramePictures(_data.Span, index, cancellationToken);
        if (!RendersDirectly(primary))
            return RenderToRaster(_decoder.DecodeFramePlanes(_data.Span, index, cancellationToken));

        int w = primary.Width, h = primary.Height;
        int depth = primary.BitDepth;
        bool wide = depth > 8;
        var rgbaFormat = wide ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        var rgba = new byte[checked((long)w * h * rgbaFormat.BytesPerPixel)];
        var (matrix, fullRange) = _decoder.ColorOf(primary);
        AvifDecoder.RenderPictureInto(primary, alpha, matrix, fullRange, rgba, w * rgbaFormat.BytesPerPixel, rgbaFormat);
        var sample = wide ? SampleFormat.UInt16 : SampleFormat.UInt8;
        int bitDepth = wide && depth < 16 ? depth : 0;
        if (alpha is not null)
            return RasterImage.Wrap(w, h, 4, sample, rgba, _frameInfo.ColorEncoding, bitDepth: bitDepth, metadata: _metadata);

        var rgbFormat = wide ? PixelFormat.Rgb48 : PixelFormat.Rgb24;
        var rgb = new byte[checked((long)w * h * rgbFormat.BytesPerPixel)];
        PixelConverter.Convert(rgba, w * rgbaFormat.BytesPerPixel, rgbaFormat, rgb, w * rgbFormat.BytesPerPixel, rgbFormat, w, h);
        return RasterImage.Wrap(w, h, 3, sample, rgb, _frameInfo.ColorEncoding, bitDepth: bitDepth, metadata: _metadata);
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
        PixelConverter.Convert(rgba16Bytes, w * 8, PixelFormat.Rgba64, rgb16Bytes, w * 6, PixelFormat.Rgb48, w, h);
        return RasterImage.Wrap(w, h, 3, SampleFormat.UInt16, rgb16Bytes, _frameInfo.ColorEncoding, bitDepth: depth < 16 ? depth : 0, metadata: _metadata);
    }
}
