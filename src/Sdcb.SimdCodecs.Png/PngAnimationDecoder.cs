using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Png;

/// <summary>What happens to a frame's region of the output buffer once its delay is over (APNG <c>dispose_op</c>).</summary>
public enum PngDisposeOp : byte
{
    /// <summary>The region is left as it is (<c>APNG_DISPOSE_OP_NONE</c>).</summary>
    None = 0,

    /// <summary>The region is cleared to fully transparent black (<c>APNG_DISPOSE_OP_BACKGROUND</c>).</summary>
    Background = 1,

    /// <summary>
    /// The region goes back to what it held before the frame was drawn (<c>APNG_DISPOSE_OP_PREVIOUS</c>). On the
    /// first frame, where nothing precedes it, it means <see cref="Background"/>.
    /// </summary>
    Previous = 2,
}

/// <summary>How a frame is combined with the output buffer (APNG <c>blend_op</c>).</summary>
public enum PngBlendOp : byte
{
    /// <summary>Every sample of the frame, alpha included, replaces the region (<c>APNG_BLEND_OP_SOURCE</c>).</summary>
    Source = 0,

    /// <summary>The frame is composited over the region by its alpha, straight (non-premultiplied) alpha (<c>APNG_BLEND_OP_OVER</c>).</summary>
    Over = 1,
}

/// <summary>One APNG frame: where it goes, how long it is shown, and how it is drawn.</summary>
/// <param name="Index">The frame's zero-based display-order index.</param>
/// <param name="X">The left edge of the frame's rectangle on the canvas.</param>
/// <param name="Y">The top edge of the frame's rectangle on the canvas.</param>
/// <param name="Width">The rectangle's width in pixels.</param>
/// <param name="Height">The rectangle's height in pixels.</param>
/// <param name="DelayNumerator">The <c>delay_num</c> field.</param>
/// <param name="DelayDenominator">The <c>delay_den</c> field as stored (0 means 100).</param>
/// <param name="Delay">How long the frame is shown: <c>delay_num / delay_den</c> seconds, where a denominator of 0 counts as 100.</param>
/// <param name="Dispose">The disposal applied after the frame's delay.</param>
/// <param name="Blend">How the frame is drawn onto the canvas.</param>
/// <param name="IsIndependent">True when decoding can start at this frame without the frames before it.</param>
public readonly record struct PngFrameInfo(
    int Index,
    int X,
    int Y,
    int Width,
    int Height,
    int DelayNumerator,
    int DelayDenominator,
    TimeSpan Delay,
    PngDisposeOp Dispose,
    PngBlendOp Blend,
    bool IsIndependent);

/// <summary>
/// Decodes the frames of an animated PNG (APNG: <c>acTL</c>, <c>fcTL</c> and <c>fdAT</c>, PNG 3rd edition section 4.9 and
/// 11.3.6) to full-canvas RGBA frames, one at a time, composited as the specification defines. A PNG without an
/// <c>acTL</c> chunk is a one-frame animation of its still image.
/// <para>
/// <b>Compositing.</b> The canvas (IHDR's width and height) starts fully transparent black. A frame's pixels replace
/// its rectangle (<see cref="PngBlendOp.Source"/>) or go over it (<see cref="PngBlendOp.Over"/>); once the frame's delay
/// is over its rectangle is disposed (<see cref="PngDisposeOp"/>) before the next frame is drawn. Frame <c>i</c> is the
/// canvas just after frame <c>i</c> is drawn, before its own disposal. The canvas is RGBA at the image's depth: 8
/// bits, or 16 bits when IHDR says 16 (<see cref="BitDepth"/>); a tRNS colour key or palette alpha becomes the pixel's alpha.
/// </para>
/// <para>
/// <b>The OVER arithmetic</b> is defined here, since the specification gives the operation in real numbers: with
/// <c>M</c> = 255 (8-bit) or 65535 (16-bit), source alpha <c>as</c> and canvas alpha <c>ab</c>, a source alpha of 0 keeps the
/// canvas pixel and a source alpha of <c>M</c> replaces it; otherwise <c>X = as·M + ab·(M − as)</c>, the result alpha is
/// <c>⌊(X + M/2) / M⌋</c> and each colour is <c>⌊(cs·as·M + cb·ab·(M − as) + X/2) / X⌋</c> (a rounded real OVER). Over a canvas
/// pixel with alpha 0 it gives the source pixel back exactly.
/// </para>
/// <para>
/// Frames are decoded on demand. Decoding frame <c>i + 1</c> after frame <c>i</c> is the cheap path; <see cref="Seek"/>
/// starts at the nearest frame that does not depend on the earlier ones and replays from there.
/// A malformed container (sequence numbers out of order, a rectangle outside the canvas, a frame count that disagrees
/// with <c>acTL</c>, ...) is an <see cref="InvalidDataException"/> from the constructor; corrupt frame data is one from the
/// decode methods.
/// </para>
/// </summary>
public sealed class PngAnimationDecoder
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly PngImage _header;
    private readonly ApngFrame[] _frames;
    private readonly PngFrameInfo[] _infos;
    private readonly bool _wide;
    private readonly byte[] _canvas;
    private byte[] _saved = [];
    private int _next;
    private bool _freshStart;

    /// <summary>Reads the container of <paramref name="data"/>, a complete PNG or APNG file in memory.</summary>
    /// <exception cref="InvalidDataException">The file is not a PNG, or its animation chunks are malformed.</exception>
    public PngAnimationDecoder(ReadOnlyMemory<byte> data)
    {
        _data = data;
        var header = PngReader.ReadHeader(data.Span, out var idat);
        _header = header;
        _wide = header.SourceBitDepth == 16;
        var container = ApngContainer.Read(data.Span, header, idat);
        if (container is null)
        {
            _frames = [new ApngFrame(0, 0, header.Width, header.Height, 0, 0, PngDisposeOp.None, PngBlendOp.Source, idat)];
            IsAnimated = false;
            LoopCount = 1;
            DefaultImageIsFrame = true;
        }
        else
        {
            _frames = container.Frames;
            IsAnimated = true;
            LoopCount = container.Plays;
            DefaultImageIsFrame = container.DefaultIsFrame;
        }

        CanvasWidth = header.Width;
        CanvasHeight = header.Height;
        var canvasBytes = (long)CanvasWidth * CanvasHeight * (_wide ? 8 : 4);
        if (canvasBytes > Array.MaxLength)
            throw new InvalidDataException($"A {CanvasWidth}x{CanvasHeight} animation canvas does not fit in memory.");
        _canvas = new byte[canvasBytes];

        var opaqueSource = header.ColorType switch
        {
            0 or 2 => header.TransparentColor is null,
            3 => header.PaletteAlpha is null || Array.TrueForAll(header.PaletteAlpha, a => a == 255),
            _ => false,
        };
        var hasAlpha = !opaqueSource || !IsFull(_frames[0]);
        _infos = new PngFrameInfo[_frames.Length];
        for (var i = 0; i < _frames.Length; i++)
        {
            var f = _frames[i];
            var independent = i == 0
                // A frame that covers the canvas and replaces what was there starts a replay, unless it is restored by
                // Previous afterwards: the restored canvas is the one before it, which a replay would not have.
                || (IsFull(f) && (f.Blend == PngBlendOp.Source || opaqueSource) && f.Dispose != PngDisposeOp.Previous)
                || (IsFull(_frames[i - 1]) && (_frames[i - 1].Dispose == PngDisposeOp.Background
                    || (_frames[i - 1].Dispose == PngDisposeOp.Previous && i == 1)));
            // Disposal that clears to transparent black shows through an otherwise opaque animation, except after the last frame.
            if (i < _frames.Length - 1 && (f.Dispose == PngDisposeOp.Background || (f.Dispose == PngDisposeOp.Previous && i == 0)))
                hasAlpha = true;
            var den = f.DelayDen == 0 ? 100 : f.DelayDen;
            _infos[i] = new PngFrameInfo(i, f.X, f.Y, f.Width, f.Height, f.DelayNum, f.DelayDen,
                TimeSpan.FromTicks(f.DelayNum * TimeSpan.TicksPerSecond / den), f.Dispose, f.Blend, independent);
        }
        HasAlpha = hasAlpha;
        Frames = Array.AsReadOnly(_infos);
        Reset();
    }

    private bool IsFull(ApngFrame f) => f.X == 0 && f.Y == 0 && f.Width == CanvasWidth && f.Height == CanvasHeight;

    /// <summary>The canvas width in pixels (IHDR's).</summary>
    public int CanvasWidth { get; }

    /// <summary>The canvas height in pixels (IHDR's).</summary>
    public int CanvasHeight { get; }

    /// <summary>The number of frames: <c>acTL</c>'s count, or 1 for a PNG that is not animated.</summary>
    public int FrameCount => _frames.Length;

    /// <summary>True when the file has an <c>acTL</c> chunk before its image data.</summary>
    public bool IsAnimated { get; }

    /// <summary>How many times the animation plays (<c>acTL</c>'s <c>num_plays</c>): 0 is forever; 1 for a PNG that is not animated.</summary>
    public int LoopCount { get; }

    /// <summary>
    /// True when the default image (the IDAT data, which every PNG reader shows) is frame 0 of the animation; false when
    /// it is only a fallback for readers that do not know APNG and the animation's frames are all <c>fdAT</c> data.
    /// </summary>
    public bool DefaultImageIsFrame { get; }

    /// <summary>The bit depth of the canvas samples: 16 for a 16-bit image, otherwise 8.</summary>
    public int BitDepth => _wide ? 16 : 8;

    /// <summary>
    /// False when every pixel of every frame is opaque (an opaque image whose first frame covers the canvas and
    /// that never clears a region to transparent): the alpha channel of the decoded frames is then constant.
    /// </summary>
    public bool HasAlpha { get; }

    /// <summary>The frames' rectangles, timing and drawing operations, in display order.</summary>
    public IReadOnlyList<PngFrameInfo> Frames { get; }

    /// <summary>The index that the next call to a <c>TryDecodeNextFrame</c> overload decodes.</summary>
    public int NextFrameIndex => _next;

    /// <summary>The image's header and metadata (the pixels are empty).</summary>
    internal PngImage Header => _header;

    /// <summary>The working canvas as the last decoded frame left it: RGBA, tightly packed, host-order <see cref="ushort"/> samples when <see cref="BitDepth"/> is 16.</summary>
    internal ReadOnlySpan<byte> Canvas => _canvas;

    /// <summary>Decodes the next frame, and copies the canvas as 8-bit RGBA (a 16-bit canvas keeps its high bytes).</summary>
    /// <param name="canvasRgba">At least <c>CanvasWidth × CanvasHeight × 4</c> bytes.</param>
    /// <param name="frame">The decoded frame's information, or the default value at the end.</param>
    /// <param name="cancellationToken">Checked every 64 rows of a frame.</param>
    /// <returns>True when a frame was decoded; false after the last frame.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    /// <exception cref="InvalidDataException">The frame's data is corrupt.</exception>
    public bool TryDecodeNextFrame(Span<byte> canvasRgba, out PngFrameInfo frame, CancellationToken cancellationToken = default)
    {
        var size = (long)CanvasWidth * CanvasHeight * 4;
        if (canvasRgba.Length < size)
            throw new ArgumentException("The destination is too small for the animation canvas.", nameof(canvasRgba));
        if (!DecodeNextFrame(cancellationToken))
        {
            frame = default;
            return false;
        }
        frame = _infos[_next - 1];
        if (!_wide)
            _canvas.CopyTo(canvasRgba);
        else
            Narrow(_canvas, canvasRgba);
        return true;
    }

    /// <summary>Decodes the next frame, and copies the canvas as 16-bit RGBA (an 8-bit canvas is widened as <c>v · 257</c>).</summary>
    /// <param name="canvasRgba16">At least <c>CanvasWidth × CanvasHeight × 4</c> samples.</param>
    /// <param name="frame">The decoded frame's information, or the default value at the end.</param>
    /// <param name="cancellationToken">Checked every 64 rows of a frame.</param>
    /// <returns>True when a frame was decoded; false after the last frame.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    /// <exception cref="InvalidDataException">The frame's data is corrupt.</exception>
    public bool TryDecodeNextFrame(Span<ushort> canvasRgba16, out PngFrameInfo frame, CancellationToken cancellationToken = default)
    {
        var samples = (long)CanvasWidth * CanvasHeight * 4;
        if (canvasRgba16.Length < samples)
            throw new ArgumentException("The destination is too small for the animation canvas.", nameof(canvasRgba16));
        if (!DecodeNextFrame(cancellationToken))
        {
            frame = default;
            return false;
        }
        frame = _infos[_next - 1];
        if (_wide)
        {
            _canvas.CopyTo(MemoryMarshal.AsBytes(canvasRgba16));
        }
        else
        {
            for (var i = 0; i < samples; i++)
                canvasRgba16[i] = (ushort)(_canvas[i] * 257);
        }
        return true;
    }

    /// <summary>Positions the decoder so that the next frame returned has index <paramref name="frameIndex"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the animation.</exception>
    /// <exception cref="InvalidDataException">A frame the replay needs is corrupt.</exception>
    public void Seek(int frameIndex, CancellationToken cancellationToken = default)
    {
        if ((uint)frameIndex >= (uint)_frames.Length)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        // Already there, or one decode away.
        if (frameIndex == _next)
            return;
        var start = frameIndex;
        while (start > 0 && !_infos[start].IsIndependent)
            start--;
        // Going forward from a position at or after the independent frame is the same replay, without starting over.
        if (frameIndex < _next || _next < start)
            Reset(start);
        while (_next < frameIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecodeNextFrame(cancellationToken);
        }
    }

    /// <summary>Positions the decoder before frame 0, on a transparent canvas.</summary>
    public void Reset() => Reset(0);

    private void Reset(int start)
    {
        Array.Clear(_canvas);
        _next = start;
        // A frame an independent start was chosen at does not depend on the disposal of the one before it.
        _freshStart = true;
    }

    /// <summary>
    /// Composites frame <paramref name="index"/> into <see cref="Canvas"/>: directly when it is the next frame,
    /// otherwise after <see cref="Seek"/> replays from the nearest independent frame.
    /// </summary>
    internal void DecodeFrame(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (index != _next)
            Seek(index, cancellationToken);
        DecodeNextFrame(cancellationToken);
    }

    // The frame's pixels are decoded before the canvas is touched, so a corrupt frame leaves the decoder where it was.
    private bool DecodeNextFrame(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_next >= _frames.Length)
            return false;

        var frame = _frames[_next];
        var pixelBytes = _wide ? 8 : 4;
        var rectBytes = checked(frame.Width * frame.Height * pixelBytes);
        var pixels = ArrayPool<byte>.Shared.Rent(rectBytes);
        try
        {
            DecodePixels(frame, pixels.AsSpan(0, rectBytes), cancellationToken);

            if (_next > 0 && !_freshStart)
                Dispose(_frames[_next - 1], _next - 1 == 0);
            _freshStart = false;

            var rowBytes = frame.Width * pixelBytes;
            if (frame.Dispose == PngDisposeOp.Previous && _next > 0)
            {
                if (_saved.Length < rectBytes)
                    _saved = new byte[rectBytes];
                for (var y = 0; y < frame.Height; y++)
                    _canvas.AsSpan(CanvasOffset(frame.X, frame.Y + y), rowBytes).CopyTo(_saved.AsSpan(y * rowBytes, rowBytes));
            }

            for (var y = 0; y < frame.Height; y++)
            {
                var source = pixels.AsSpan(y * rowBytes, rowBytes);
                var destination = _canvas.AsSpan(CanvasOffset(frame.X, frame.Y + y), rowBytes);
                if (frame.Blend == PngBlendOp.Source)
                    source.CopyTo(destination);
                else if (_wide)
                    Over16(source, destination);
                else
                    Over8(source, destination);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }

        _next++;
        return true;
    }

    private int CanvasOffset(int x, int y) => (y * CanvasWidth + x) * (_wide ? 8 : 4);

    // The disposal of the frame drawn last, before the next one is. Previous on the first frame is Background.
    private void Dispose(ApngFrame frame, bool isFirst)
    {
        var pixelBytes = _wide ? 8 : 4;
        var rowBytes = frame.Width * pixelBytes;
        switch (frame.Dispose)
        {
            case PngDisposeOp.Background:
                ClearRect(frame, rowBytes);
                break;
            case PngDisposeOp.Previous when isFirst:
                ClearRect(frame, rowBytes);
                break;
            case PngDisposeOp.Previous:
                for (var y = 0; y < frame.Height; y++)
                    _saved.AsSpan(y * rowBytes, rowBytes).CopyTo(_canvas.AsSpan(CanvasOffset(frame.X, frame.Y + y), rowBytes));
                break;
        }
    }

    private void ClearRect(ApngFrame frame, int rowBytes)
    {
        for (var y = 0; y < frame.Height; y++)
            _canvas.AsSpan(CanvasOffset(frame.X, frame.Y + y), rowBytes).Clear();
    }

    // The frame as RGBA at the canvas depth: the header with the frame's size decodes it exactly as a still image of that size.
    private void DecodePixels(ApngFrame frame, Span<byte> destination, CancellationToken cancellationToken)
    {
        var header = _header with { Width = frame.Width, Height = frame.Height };
        var data = _data.Span;
        if (!_wide)
        {
            PngReader.DecodeInto(data, header, frame.Data, destination, 0, PixelFormat.Rgba32, PngAblation.None, cancellationToken);
            return;
        }

        var raw = header.Interlaced
            ? PngReader.InflateAdam7(data, frame.Data, frame.Width, frame.Height, 16, header.ColorType, cancellationToken)
            : PngReader.InflateAndUnfilter(data, frame.Data, frame.Width, frame.Height, 16, header.ColorType, cancellationToken);
        Expand16(raw, destination, frame.Width * frame.Height, header.ColorType, header.TransparentColor);
    }

    // 16-bit samples, big-endian as PNG stores them, to RGBA with host-order samples; a tRNS colour key makes a pixel's alpha 0.
    private static void Expand16(ReadOnlySpan<byte> raw, Span<byte> destination, int pixels, int colorType, ushort[]? key)
    {
        var d = MemoryMarshal.Cast<byte, ushort>(destination);
        for (var i = 0; i < pixels; i++)
        {
            ushort r, g, b, a = ushort.MaxValue;
            switch (colorType)
            {
                case 0:
                    r = g = b = BinaryPrimitives.ReadUInt16BigEndian(raw[(2 * i)..]);
                    if (key is not null && r == key[0]) a = 0;
                    break;
                case 2:
                    r = BinaryPrimitives.ReadUInt16BigEndian(raw[(6 * i)..]);
                    g = BinaryPrimitives.ReadUInt16BigEndian(raw[(6 * i + 2)..]);
                    b = BinaryPrimitives.ReadUInt16BigEndian(raw[(6 * i + 4)..]);
                    if (key is not null && r == key[0] && g == key[1] && b == key[2]) a = 0;
                    break;
                case 4:
                    r = g = b = BinaryPrimitives.ReadUInt16BigEndian(raw[(4 * i)..]);
                    a = BinaryPrimitives.ReadUInt16BigEndian(raw[(4 * i + 2)..]);
                    break;
                default:
                    r = BinaryPrimitives.ReadUInt16BigEndian(raw[(8 * i)..]);
                    g = BinaryPrimitives.ReadUInt16BigEndian(raw[(8 * i + 2)..]);
                    b = BinaryPrimitives.ReadUInt16BigEndian(raw[(8 * i + 4)..]);
                    a = BinaryPrimitives.ReadUInt16BigEndian(raw[(8 * i + 6)..]);
                    break;
            }
            d[4 * i] = r;
            d[4 * i + 1] = g;
            d[4 * i + 2] = b;
            d[4 * i + 3] = a;
        }
    }

    private static void Narrow(ReadOnlySpan<byte> canvas16, Span<byte> destination)
    {
        var s = MemoryMarshal.Cast<byte, ushort>(canvas16);
        for (var i = 0; i < s.Length; i++)
            destination[i] = (byte)(s[i] >> 8);
    }

    // OVER on RGBA8 rows, per the class remarks. Sources of alpha 0 and 255 never reach the arithmetic.
    private static void Over8(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (var i = 0; i < source.Length; i += 4)
        {
            int sa = source[i + 3];
            if (sa == 0)
                continue;
            if (sa == 255)
            {
                source.Slice(i, 4).CopyTo(destination[i..]);
                continue;
            }
            int ab = destination[i + 3];
            var x = sa * 255 + ab * (255 - sa);
            destination[i] = Mix8(source[i], destination[i], sa, ab, x);
            destination[i + 1] = Mix8(source[i + 1], destination[i + 1], sa, ab, x);
            destination[i + 2] = Mix8(source[i + 2], destination[i + 2], sa, ab, x);
            destination[i + 3] = (byte)((x + 127) / 255);
        }
    }

    private static byte Mix8(int cs, int cb, int sa, int ab, int x) =>
        (byte)((cs * sa * 255 + cb * ab * (255 - sa) + x / 2) / x);

    // The same on RGBA16 rows of host-order samples.
    private static void Over16(ReadOnlySpan<byte> sourceBytes, Span<byte> destinationBytes)
    {
        var source = MemoryMarshal.Cast<byte, ushort>(sourceBytes);
        var destination = MemoryMarshal.Cast<byte, ushort>(destinationBytes);
        for (var i = 0; i < source.Length; i += 4)
        {
            long sa = source[i + 3];
            if (sa == 0)
                continue;
            if (sa == 65535)
            {
                source.Slice(i, 4).CopyTo(destination[i..]);
                continue;
            }
            long ab = destination[i + 3];
            var x = sa * 65535 + ab * (65535 - sa);
            destination[i] = Mix16(source[i], destination[i], sa, ab, x);
            destination[i + 1] = Mix16(source[i + 1], destination[i + 1], sa, ab, x);
            destination[i + 2] = Mix16(source[i + 2], destination[i + 2], sa, ab, x);
            destination[i + 3] = (ushort)((x + 32767) / 65535);
        }
    }

    private static ushort Mix16(long cs, long cb, long sa, long ab, long x) =>
        (ushort)((cs * sa * 65535 + cb * ab * (65535 - sa) + x / 2) / x);
}

/// <summary>One frame as the container describes it: its rectangle, timing and operations, and where its compressed data lies.</summary>
internal sealed record ApngFrame(int X, int Y, int Width, int Height, int DelayNum, int DelayDen,
    PngDisposeOp Dispose, PngBlendOp Blend, List<(int Offset, int Length)> Data)
{
    public ApngFrame(int x, int y, int width, int height, int delayNum, int delayDen, PngDisposeOp dispose, PngBlendOp blend)
        : this(x, y, width, height, delayNum, delayDen, dispose, blend, []) { }
}

/// <summary>
/// The animation chunks of a PNG (PNG 3rd edition 4.9, 11.3.6), read after <see cref="PngReader.ReadHeader"/> has
/// walked the file and checked every CRC: <c>acTL</c> before the first IDAT, one <c>fcTL</c> per frame, <c>fdAT</c> data after
/// the IDATs, and one sequence of numbers shared by <c>fcTL</c> and <c>fdAT</c>.
/// </summary>
internal sealed class ApngContainer
{
    public required int Plays { get; init; }
    public required bool DefaultIsFrame { get; init; }
    public required ApngFrame[] Frames { get; init; }

    /// <summary>Null when the file has no <c>acTL</c> before its first IDAT (it is not an APNG: such chunks later are ignored).</summary>
    /// <exception cref="InvalidDataException">The animation chunks break the rules above.</exception>
    public static ApngContainer? Read(ReadOnlySpan<byte> data, PngImage header, List<(int Offset, int Length)> idat)
    {
        long frameCount = -1;
        var plays = 0;
        var seenIdat = false;
        long expectedSequence = 0;
        var frames = new List<ApngFrame>();
        var defaultIsFrame = false;
        ApngFrame? current = null;
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            if (length > data.Length - pos - 12)
                throw new InvalidDataException($"Chunk at byte {pos} extends past end of file");
            var type = data.Slice(pos + 4, 4);
            var payload = data.Slice(pos + 8, (int)length);

            if (type.SequenceEqual("IEND"u8))
                break;
            if (type.SequenceEqual("IDAT"u8))
            {
                seenIdat = true;
            }
            else if (type.SequenceEqual("acTL"u8) && !seenIdat)
            {
                if (frameCount >= 0)
                    throw new InvalidDataException("More than one acTL chunk");
                if (length != 8)
                    throw new InvalidDataException($"acTL length must be 8, got {length}");
                frameCount = BinaryPrimitives.ReadUInt32BigEndian(payload);
                var numPlays = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
                if (frameCount == 0 || frameCount > int.MaxValue)
                    throw new InvalidDataException($"acTL frame count {frameCount} is not valid");
                plays = (int)Math.Min(numPlays, int.MaxValue);
            }
            else if (type.SequenceEqual("fcTL"u8) && frameCount >= 0)
            {
                if (length != 26)
                    throw new InvalidDataException($"fcTL length must be 26, got {length}");
                if (BinaryPrimitives.ReadUInt32BigEndian(payload) != expectedSequence++)
                    throw new InvalidDataException("fcTL sequence number out of order");
                var width = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
                var height = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
                var x = BinaryPrimitives.ReadUInt32BigEndian(payload[12..]);
                var y = BinaryPrimitives.ReadUInt32BigEndian(payload[16..]);
                if (width == 0 || height == 0)
                    throw new InvalidDataException("fcTL frame size must be positive");
                if ((long)x + width > header.Width || (long)y + height > header.Height)
                    throw new InvalidDataException($"The frame {width}x{height} at {x},{y} lies outside the {header.Width}x{header.Height} canvas");
                if (payload[24] > 2)
                    throw new InvalidDataException($"fcTL dispose_op {payload[24]} is not valid");
                if (payload[25] > 1)
                    throw new InvalidDataException($"fcTL blend_op {payload[25]} is not valid");
                if (frames.Count >= frameCount)
                    throw new InvalidDataException("More fcTL chunks than acTL declares");

                if (!seenIdat)
                {
                    // The default image is the first frame; its rectangle is the whole canvas.
                    if (frames.Count > 0)
                        throw new InvalidDataException("More than one fcTL before the image data");
                    if (x != 0 || y != 0 || width != header.Width || height != header.Height)
                        throw new InvalidDataException("The fcTL of the default image must cover the whole canvas");
                    defaultIsFrame = true;
                }
                else if (current is not null && current.Data.Count == 0)
                {
                    throw new InvalidDataException("A frame has no fdAT chunk");
                }

                current = new ApngFrame((int)x, (int)y, (int)width, (int)height,
                    BinaryPrimitives.ReadUInt16BigEndian(payload[20..]), BinaryPrimitives.ReadUInt16BigEndian(payload[22..]),
                    (PngDisposeOp)payload[24], (PngBlendOp)payload[25], !seenIdat ? idat : []);
                frames.Add(current);
            }
            else if (type.SequenceEqual("fdAT"u8) && frameCount >= 0)
            {
                if (!seenIdat)
                    throw new InvalidDataException("fdAT before the image data");
                if (current is null || (defaultIsFrame && frames.Count == 1 && ReferenceEquals(current.Data, idat)))
                    throw new InvalidDataException("fdAT chunk without the fcTL of its frame");
                if (length < 4)
                    throw new InvalidDataException("fdAT chunk is shorter than its sequence number");
                if (BinaryPrimitives.ReadUInt32BigEndian(payload) != expectedSequence++)
                    throw new InvalidDataException("fdAT sequence number out of order");
                current.Data.Add((pos + 12, (int)length - 4));
            }
            pos += 12 + (int)length;
        }

        if (frameCount < 0)
            return null;
        if (frames.Count != frameCount)
            throw new InvalidDataException($"acTL declares {frameCount} frames but the file has {frames.Count} fcTL chunks");
        if (current!.Data.Count == 0)
            throw new InvalidDataException("A frame has no fdAT chunk");

        // Deflate cannot expand more than 1032:1 (as for the default image): a frame whose data could not fill its size is refused
        // before anything is allocated for it.
        foreach (var frame in frames)
        {
            long compressed = 0;
            foreach (var (_, len) in frame.Data)
                compressed += len;
            var inflated = PngReader.InflatedSize(frame.Width, frame.Height, header.SourceBitDepth, header.ColorType, header.Interlaced);
            if (inflated > compressed * 1032 + 1024)
                throw new InvalidDataException(
                    $"{compressed} bytes of frame data cannot inflate to the {inflated} a {frame.Width}x{frame.Height} frame needs");
        }

        return new ApngContainer { Plays = plays, DefaultIsFrame = defaultIsFrame, Frames = [.. frames] };
    }
}
