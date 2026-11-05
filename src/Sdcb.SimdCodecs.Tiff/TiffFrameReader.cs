using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

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

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        return TiffImageDecoder.TryDecodePage(data.Span, index, options, cancellationToken, out image);
    }

    /// <remarks>Written strip by strip into the destination, as <see cref="TiffImageDecoder.TryDecodeInto"/>.</remarks>
    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        return TiffImageDecoder.TryDecodePageInto(data.Span, index, destination, stride, format, options, cancellationToken);
    }

    public void Dispose() => _disposed = true;

    private void Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
