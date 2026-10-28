using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// <see cref="IImageFrameReader"/> for TIFF (<see cref="TiffImageDecoder.TryOpenFrames"/>): every page of
/// the IFD chain, each decoded as <see cref="TiffImageDecoder.TryDecode"/> decodes the first. Pages are
/// independent, so the reader keeps no state between them beyond the encoded data.
/// </summary>
internal sealed class TiffFrameReader(ReadOnlyMemory<byte> data, int pages) : IImageFrameReader
{
    private bool _disposed;

    public int FrameCount { get; } = pages;

    public int? LoopCount => null;

    public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
    {
        Check(index);
        var ok = TiffImageDecoder.TryReadPageInfo(data.Span, index, out var info);
        frame = new ImageFrameInfo(info, null);
        return ok;
    }

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image)
    {
        Check(index);
        var ok = TiffImageDecoder.TryDecodePage(data.Span, index, out var raster);
        image = raster;
        return ok;
    }

    /// <remarks>False for float pages, as <see cref="TiffImageDecoder.TryDecodeInto"/>.</remarks>
    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        Check(index);
        return TiffImageDecoder.TryDecodePage(data.Span, index, out var image)
            && PixelConverter.TryConvert(image, destination, stride, format);
    }

    public void Dispose() => _disposed = true;

    private void Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
