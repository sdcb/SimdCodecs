using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for GIF, bridging <see cref="GifDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade. A GIF decodes as 8-bit RGB when every frame covers the canvas with no
/// transparent index (<see cref="GifInfo.HasAlpha"/> false), otherwise as 8-bit RGBA. <see cref="TryDecode"/>
/// is the first frame; <see cref="TryOpenFrames"/> reads all of them. Malformed data makes the
/// <c>Try</c> methods return false rather than throw.
/// </summary>
public sealed class GifImageDecoder : IMultiFrameImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 6;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) => GifContainer.HasSignature(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        try
        {
            info = ToImageInfo(GifContainer.Read(data));
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        if (!TryFirstFrame(data, out var canvas, out var info)) return false;
        image = GifFrameReader.ToImage(canvas, info.Channels);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        if (!TryReadInfo(data, out var info)
            || !PixelConverter.TryGetLayout(info.Width, info.Height, format, stride, destination.Length, out var rowStride)
            || !TryFirstFrame(data, out var canvas, out _))
            return false;
        GifFrameReader.Convert(canvas, destination, rowStride, format);
        return true;
    }

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);

    /// <inheritdoc />
    /// <remarks>Frames are the composited canvas. Decoding them in order draws each once; any other order
    /// replays from the nearest frame that does not depend on the ones before it.</remarks>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        try
        {
            var container = GifContainer.Read(data.Span);
            reader = new GifFrameReader(data, new GifCanvas(container), container, ToImageInfo(container) with { FrameCount = 1 });
            return true;
        }
        catch (InvalidDataException)
        {
            reader = null;
            return false;
        }
    }

    // The first frame, composited from the span itself: a still decode keeps nothing, so nothing is copied.
    private static bool TryFirstFrame(ReadOnlySpan<byte> data, [NotNullWhen(true)] out GifCanvas? canvas, out ImageInfo info)
    {
        canvas = null;
        info = default;
        try
        {
            var container = GifContainer.Read(data);
            info = ToImageInfo(container);
            canvas = new GifCanvas(container);
            canvas.DecodeFrame(data, 0);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // Palette entries are 8-bit, whatever the index depth.
    internal static ImageInfo ToImageInfo(GifContainer container) =>
        new(container.Width, container.Height, container.IsOpaque ? 3 : 4, SampleFormat.UInt8)
        {
            Format = ImageFormat.Gif,
            BitDepth = 8,
            FrameCount = container.Frames.Length,
        };
}
