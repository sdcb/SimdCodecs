namespace Sdcb.SimdCodecs.Gif;

/// <summary>What a GIF's headers say, read without decoding a pixel.</summary>
/// <param name="Width">The canvas width: the logical screen, or the images' bounding box when that is empty.</param>
/// <param name="Height">The canvas height.</param>
/// <param name="FrameCount">The images in the file; more than one is an animation.</param>
/// <param name="HasAlpha">False when every frame covers the canvas with no transparent index, so every
/// composited pixel is opaque.</param>
/// <param name="LoopCount">The NETSCAPE2.0 loop count as stored (0: forever), or null when the file has
/// none. It counts repetitions after the first play (as browsers and ImageMagick read it), so a stored
/// <c>n</c> plays <c>n + 1</c> times; the frame reader's <c>LoopCount</c> reports the plays.</param>
public readonly record struct GifInfo(int Width, int Height, int FrameCount, bool HasAlpha, int? LoopCount);

/// <summary>A decoded GIF frame: tightly packed, non-premultiplied RGBA over the whole canvas.</summary>
public sealed class GifImage
{
    internal GifImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>RGBA pixels, exactly <c>Width * Height * 4</c> bytes.</summary>
    public byte[] Rgba { get; }
}

/// <summary>
/// Pure-managed GIF (87a / 89a) decoder, derived from the GIF89a specification: global and local colour
/// tables, interlacing, transparency, every disposal method and the NETSCAPE2.0 loop count. A frame is the
/// canvas as a viewer shows it, every earlier frame composited in.
/// <para>
/// This class decodes the first frame. Every frame, by index or played in order, comes through
/// <see cref="GifImageDecoder.TryOpenFrames"/> (or the <c>Sdcb.SimdCodecs</c> facade's
/// <c>TryOpenFrames</c>). The decisions the specification leaves open are made as browsers make them; see
/// the package README.
/// </para>
/// </summary>
public static class GifDecoder
{
    /// <summary>Reads the canvas size, frame count, alpha and loop count from the block structure.</summary>
    /// <exception cref="InvalidDataException">The data is not a GIF, holds no image, or is above the pixel limit (2^26).</exception>
    public static GifInfo ReadInfo(ReadOnlySpan<byte> data) => Info(GifContainer.Read(data));

    /// <summary><see cref="ReadInfo"/> that returns false instead of throwing.</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out GifInfo info)
    {
        try
        {
            info = ReadInfo(data);
            return true;
        }
        catch (InvalidDataException)
        {
            info = default;
            return false;
        }
    }

    /// <summary>Decodes the first frame to a new RGBA image.</summary>
    /// <exception cref="InvalidDataException">The data is not a decodable GIF.</exception>
    public static GifImage Decode(ReadOnlySpan<byte> data)
    {
        var canvas = FirstFrame(data);
        return new GifImage(canvas.Width, canvas.Height, canvas.Pixels.ToArray());
    }

    /// <summary>
    /// Decodes the first frame into <paramref name="rgba"/>, rows <paramref name="stride"/> bytes apart
    /// (0: tightly packed, <c>Width * 4</c>). Size it with <see cref="ReadInfo"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The data is not a decodable GIF.</exception>
    /// <exception cref="ArgumentException">The destination is too small, or the stride shorter than a row.</exception>
    public static void DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgba, int stride = 0)
    {
        var canvas = FirstFrame(data);
        var rowBytes = canvas.Width * 4;
        if (stride == 0) stride = rowBytes;
        if (stride < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"Stride must be at least {rowBytes} bytes.");
        if (rgba.Length < (long)stride * (canvas.Height - 1) + rowBytes)
            throw new ArgumentException("The destination is too small for the GIF canvas.", nameof(rgba));
        for (var y = 0; y < canvas.Height; y++)
            canvas.Pixels.Slice(y * rowBytes, rowBytes).CopyTo(rgba.Slice(y * stride));
    }

    internal static GifInfo Info(GifContainer container) =>
        new(container.Width, container.Height, container.Frames.Length, !container.IsOpaque, container.LoopCount);

    private static GifCanvas FirstFrame(ReadOnlySpan<byte> data)
    {
        var canvas = new GifCanvas(GifContainer.Read(data));
        canvas.DecodeFrame(data, 0);
        return canvas;
    }
}
