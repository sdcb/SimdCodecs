using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Jbig2;

/// <summary>
/// <see cref="IImageFrameReader"/> for standalone <c>.jb2</c> files (<see cref="Jbig2ImageDecoder.TryOpenFrames"/>):
/// frame <c>i</c> is page <c>i + 1</c>, each decoded as <see cref="Jbig2ImageDecoder.TryDecode"/> decodes page 1.
/// Pages are independent, so the reader keeps no state between them beyond the encoded data.
/// </summary>
internal sealed class Jbig2FrameReader(ReadOnlyMemory<byte> data, int pages) : IImageFrameReader
{
    private bool _disposed;

    public int FrameCount { get; } = pages;

    public int? LoopCount => null;

    public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
    {
        Check(index);
        var ok = Jbig2ImageDecoder.TryReadPageInfo(data.Span, index + 1, out var info);
        frame = new ImageFrameInfo(info, null);
        return ok;
    }

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        return Jbig2ImageDecoder.TryDecodePage(data.Span, index + 1, options, cancellationToken, out image);
    }

    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        Check(index);
        if (options is { IsFullImage: false })
            return ImageDecoderFallback.TryDecodeFrameInto(this, index, destination, stride, format, options, cancellationToken);
        return Jbig2ImageDecoder.TryDecodePageInto(data.Span, index + 1, destination, stride, format, options, cancellationToken);
    }

    public void Dispose() => _disposed = true;

    private void Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
