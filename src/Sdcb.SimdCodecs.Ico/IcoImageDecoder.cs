using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Ico;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for icons (<c>.ico</c>), bridging <see cref="IcoDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade. <see cref="TryDecode"/> is the largest, deepest image as 8-bit RGBA;
/// <see cref="TryOpenFrames"/> opens every image as a page, each its own size. Malformed data makes the
/// <c>Try</c> methods return false rather than throw. Cursors have their own adapter, <see cref="CurImageDecoder"/>.
/// </summary>
public sealed class IcoImageDecoder : IMultiFrameImageDecoder
{
    /// <inheritdoc />
    /// <remarks>The directory and its first entry.</remarks>
    public static int SignatureLength => 22;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => IcoContainer.HasSignature(header, IcoKind.Icon);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info) => IcoAdapter.TryReadInfo(data, IcoKind.Icon, out info);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image) =>
        IcoAdapter.TryDecode(data, IcoKind.Icon, out image);

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format) =>
        IcoAdapter.TryDecodeInto(data, IcoKind.Icon, destination, stride, format);

    /// <inheritdoc />
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader) =>
        IcoAdapter.TryOpenFrames(data, IcoKind.Icon, out reader);
}

/// <summary>
/// <see cref="IImageDecoder"/> adapter for cursors (<c>.cur</c>): <see cref="IcoImageDecoder"/> for a directory of
/// type 2, reported as <see cref="ImageFormat.Cur"/>. Hotspots come through <see cref="IcoDecoder.ReadInfo"/>.
/// </summary>
public sealed class CurImageDecoder : IMultiFrameImageDecoder
{
    /// <inheritdoc />
    /// <remarks>The directory and its first entry.</remarks>
    public static int SignatureLength => 22;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => IcoContainer.HasSignature(header, IcoKind.Cursor);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info) => IcoAdapter.TryReadInfo(data, IcoKind.Cursor, out info);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image) =>
        IcoAdapter.TryDecode(data, IcoKind.Cursor, out image);

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format) =>
        IcoAdapter.TryDecodeInto(data, IcoKind.Cursor, destination, stride, format);

    /// <inheritdoc />
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader) =>
        IcoAdapter.TryOpenFrames(data, IcoKind.Cursor, out reader);
}

/// <summary>The two adapters' shared body: the same container, told apart by the directory type.</summary>
internal static class IcoAdapter
{
    private static IcoContainer Read(ReadOnlySpan<byte> data, IcoKind kind)
    {
        var container = IcoContainer.Read(data);
        if (container.Kind != kind)
            throw new InvalidDataException($"The directory is a{(container.Kind == IcoKind.Icon ? "n icon" : " cursor")}, not a{(kind == IcoKind.Icon ? "n icon" : " cursor")}.");
        return container;
    }

    internal static ImageInfo Info(IcoContainer container, int index) => new(container.Entries[index].Width, container.Entries[index].Height, 4, SampleFormat.UInt8)
    {
        Format = container.Kind == IcoKind.Cursor ? ImageFormat.Cur : ImageFormat.Ico,
        BitDepth = 8,
        FrameCount = container.Entries.Length,
    };

    public static bool TryReadInfo(ReadOnlySpan<byte> data, IcoKind kind, out ImageInfo info)
    {
        try
        {
            var container = Read(data, kind);
            info = Info(container, container.Best);
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, IcoKind kind, [NotNullWhen(true)] out IDecodedImage? image)
    {
        try
        {
            var container = Read(data, kind);
            var decoded = container.Decode(data, container.Best);
            image = new RasterImage(decoded.Width, decoded.Height, 4, SampleFormat.UInt8, decoded.Rgba);
            return true;
        }
        catch (InvalidDataException)
        {
            image = null;
            return false;
        }
    }

    public static bool TryDecodeInto(ReadOnlySpan<byte> data, IcoKind kind, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        try
        {
            var container = Read(data, kind);
            var entry = container.Entries[container.Best];
            if (!PixelConverter.TryGetLayout(entry.Width, entry.Height, format, stride, destination.Length, out var rowStride))
                return false;
            container.DecodeInto(data, container.Best, destination, rowStride, format);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, IcoKind kind, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        try
        {
            reader = new IcoFrameReader(data, Read(data.Span, kind));
            return true;
        }
        catch (InvalidDataException)
        {
            reader = null;
            return false;
        }
    }
}

/// <summary>
/// <see cref="IImageFrameReader"/> over an icon's images, in directory order: pages, each its own size, with no
/// duration and no loop count.
/// </summary>
internal sealed class IcoFrameReader(ReadOnlyMemory<byte> data, IcoContainer container) : IImageFrameReader
{
    private bool _disposed;

    public int FrameCount => container.Entries.Length;

    public int? LoopCount => null;

    public bool TryGetFrameInfo(int index, out ImageFrameInfo frame)
    {
        Check(index);
        frame = new ImageFrameInfo(IcoAdapter.Info(container, index), null);
        return true;
    }

    public bool TryDecodeFrame(int index, [NotNullWhen(true)] out IDecodedImage? image)
    {
        Check(index);
        try
        {
            var decoded = container.Decode(data.Span, index);
            image = new RasterImage(decoded.Width, decoded.Height, 4, SampleFormat.UInt8, decoded.Rgba);
            return true;
        }
        catch (InvalidDataException)
        {
            image = null;
            return false;
        }
    }

    public bool TryDecodeFrameInto(int index, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        Check(index);
        var entry = container.Entries[index];
        if (!PixelConverter.TryGetLayout(entry.Width, entry.Height, format, stride, destination.Length, out var rowStride))
            return false;
        try
        {
            container.DecodeInto(data.Span, index, destination, rowStride, format);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public void Dispose() => _disposed = true;

    private void Check(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
