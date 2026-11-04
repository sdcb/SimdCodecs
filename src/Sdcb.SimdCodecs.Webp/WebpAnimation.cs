namespace Sdcb.SimdCodecs.Webp;

/// <summary>A WebP animation decoded to full-canvas, non-premultiplied RGBA frames.</summary>
/// <param name="CanvasWidth">The canvas width in pixels.</param>
/// <param name="CanvasHeight">The canvas height in pixels.</param>
/// <param name="LoopCount">The ANIM loop count; zero means infinite looping.</param>
/// <param name="BackgroundColor">The ANIM BGRA bytes packed as 0xAARRGGBB.</param>
/// <param name="Frames">The full-canvas frames in display order.</param>
public sealed record WebpAnimation(
    int CanvasWidth,
    int CanvasHeight,
    int LoopCount,
    uint BackgroundColor,
    IReadOnlyList<WebpFrame> Frames)
{
    /// <summary>The embedded ICC profile, or null when no ICCP chunk is present.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>The embedded EXIF payload, or null when no EXIF chunk is present.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>The embedded XMP payload, or null when no XMP chunk is present.</summary>
    public byte[]? Xmp { get; init; }
}

/// <summary>A full-canvas frame with its presentation timestamp and duration.</summary>
/// <param name="Rgba">Tightly packed, non-premultiplied RGBA8 canvas pixels.</param>
/// <param name="TimestampMs">The frame start time in milliseconds.</param>
/// <param name="DurationMs">The frame display duration in milliseconds.</param>
public sealed record WebpFrame(byte[] Rgba, int TimestampMs, int DurationMs);

/// <summary>Encoded rectangle and timing information for one WebP animation frame.</summary>
/// <param name="Index">The frame's zero-based display-order index.</param>
/// <param name="X">The frame's left coordinate in canvas pixels.</param>
/// <param name="Y">The frame's top coordinate in canvas pixels.</param>
/// <param name="Width">The frame rectangle width in pixels.</param>
/// <param name="Height">The frame rectangle height in pixels.</param>
/// <param name="TimestampMs">The frame start time in milliseconds.</param>
/// <param name="DurationMs">The frame display duration in milliseconds.</param>
/// <param name="Blend">True when the frame is alpha-blended over the prior canvas.</param>
/// <param name="DisposeToBackground">True when the rectangle is cleared before the next frame.</param>
/// <param name="IsIndependent">True when decoding can start from this frame without prior frames.</param>
public readonly record struct WebpFrameInfo(
    int Index,
    int X,
    int Y,
    int Width,
    int Height,
    int TimestampMs,
    int DurationMs,
    bool Blend,
    bool DisposeToBackground,
    bool IsIndependent);

/// <summary>
/// Decodes WebP frames on demand and retains a full-canvas working buffer for compositing.
/// Seeking starts at the nearest frame that is independent of preceding animation state.
/// The canvas starts transparent black and disposal clears to transparent black; the ANIM
/// background color is retained as metadata but ignored, matching anim_dump 1.6.0.
/// </summary>
public sealed class WebpAnimationDecoder
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly WebpFrameData[] _frameData;
    private readonly WebpFrameInfo[] _frameInfos;
    private readonly byte[] _canvas;
    private readonly uint _backgroundColor;
    private readonly bool _fancy;
    private int _nextFrameIndex;

    /// <summary>Creates a decoder for an animated WebP or a still WebP image.</summary>
    /// <param name="data">The complete WebP file in memory.</param>
    public WebpAnimationDecoder(ReadOnlyMemory<byte> data) : this(data, fancy: true)
    {
    }

    internal WebpAnimationDecoder(ReadOnlyMemory<byte> data, bool fancy)
    {
        _data = data;
        _fancy = fancy;
        WebpContainer container = WebpContainer.Read(data.Span);
        CanvasWidth = container.Info.Width;
        CanvasHeight = container.Info.Height;
        LoopCount = container.LoopCount;
        _backgroundColor = container.AnimationBackgroundColor;
        _frameData = container.Frames;
        _canvas = new byte[checked(CanvasWidth * CanvasHeight * 4)];
        _frameInfos = new WebpFrameInfo[_frameData.Length];

        long timestamp = 0;
        for (int index = 0; index < _frameData.Length; index++)
        {
            WebpFrameData frame = _frameData[index];
            if (timestamp > int.MaxValue)
            {
                throw new InvalidDataException("The WebP animation timeline exceeds the supported range.");
            }

            bool fullCanvas = frame.X == 0 && frame.Y == 0 &&
                frame.Width == CanvasWidth && frame.Height == CanvasHeight;
            bool previousDisposesFullCanvas = index > 0 &&
                _frameData[index - 1].X == 0 &&
                _frameData[index - 1].Y == 0 &&
                _frameData[index - 1].Width == CanvasWidth &&
                _frameData[index - 1].Height == CanvasHeight &&
                _frameData[index - 1].DisposeToBackground;
            bool independent = index == 0 ||
                (fullCanvas &&
                    (!frame.Blend ||
                     (frame.Format != WebpFormat.Lossless && !frame.HasAlpha))) ||
                previousDisposesFullCanvas;

            _frameInfos[index] = new WebpFrameInfo(
                index,
                frame.X,
                frame.Y,
                frame.Width,
                frame.Height,
                (int)timestamp,
                frame.DurationMs,
                frame.Blend,
                frame.DisposeToBackground,
                independent);
            timestamp += frame.DurationMs;
        }

        _frameDataView = Array.AsReadOnly(_frameInfos);
        FillCanvas();
    }

    private readonly IReadOnlyList<WebpFrameInfo> _frameDataView;

    /// <summary>The animation canvas width in pixels.</summary>
    public int CanvasWidth { get; }

    /// <summary>The animation canvas height in pixels.</summary>
    public int CanvasHeight { get; }

    /// <summary>The number of frames, or one for a still image.</summary>
    public int FrameCount => _frameData.Length;

    /// <summary>The ANIM loop count; zero means infinite looping.</summary>
    public int LoopCount { get; }

    /// <summary>
    /// The ANIM BGRA bytes packed as 0xAARRGGBB, or zero for a still image. This color is
    /// reported but is not used to initialize or clear the compositing canvas.
    /// </summary>
    public uint BackgroundColor => _backgroundColor;

    /// <summary>Frame rectangles and timing in display order.</summary>
    public IReadOnlyList<WebpFrameInfo> Frames => _frameDataView;

    /// <summary>The index that the next call to <see cref="TryDecodeNextFrame"/> will decode.</summary>
    public int NextFrameIndex => _nextFrameIndex;

    /// <summary>Decodes and copies the next full-canvas frame into <paramref name="canvasRgba"/>.</summary>
    /// <param name="canvasRgba">Destination with at least width × height × 4 bytes.</param>
    /// <param name="frame">Metadata for the decoded frame, or the default value at end of input.</param>
    /// <param name="cancellationToken">Cancellation token checked before decoding the frame.</param>
    /// <returns>True when a frame was decoded; false after the final frame.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public bool TryDecodeNextFrame(Span<byte> canvasRgba, out WebpFrameInfo frame, CancellationToken cancellationToken = default)
    {
        if (canvasRgba.Length < _canvas.Length)
        {
            throw new ArgumentException("The destination is too small for the animation canvas.", nameof(canvasRgba));
        }

        if (!DecodeNextFrame(cancellationToken))
        {
            frame = default;
            return false;
        }

        frame = _frameInfos[_nextFrameIndex - 1];
        _canvas.CopyTo(canvasRgba);
        return true;
    }

    /// <summary>Positions the decoder so the next frame returned has the requested index.</summary>
    /// <param name="frameIndex">A zero-based frame index.</param>
    /// <param name="cancellationToken">Cancellation token checked between frames.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the animation.</exception>
    public void Seek(int frameIndex, CancellationToken cancellationToken = default)
    {
        if ((uint)frameIndex >= (uint)_frameData.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        int start = frameIndex;
        while (start > 0 && !_frameInfos[start].IsIndependent)
        {
            start--;
        }

        FillCanvas();
        _nextFrameIndex = start;
        while (_nextFrameIndex < frameIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DecodeNextFrame(cancellationToken);
        }
    }

    /// <summary>Positions the decoder at frame zero.</summary>
    public void Reset() => Seek(0);

    /// <summary>
    /// The working canvas, tightly packed RGBA, as the last decoded frame left it. For
    /// <see cref="WebpFrameReader"/>, which converts it row by row instead of copying it out first.
    /// </summary>
    internal ReadOnlySpan<byte> Canvas => _canvas;

    /// <summary>
    /// Composites frame <paramref name="frameIndex"/> into <see cref="Canvas"/>: directly when it is the
    /// next frame, otherwise after <see cref="Seek"/> replays from the nearest independent frame.
    /// </summary>
    internal void DecodeFrame(int frameIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frameIndex != _nextFrameIndex)
        {
            Seek(frameIndex, cancellationToken);
        }
        DecodeNextFrame(cancellationToken);
    }

    /// <summary>
    /// Returns the frame visible at a timestamp. Frame intervals include their start and exclude
    /// their end; timestamps beyond the animation clamp to the final frame.
    /// </summary>
    /// <param name="timestampMs">The timestamp in milliseconds; nonpositive values select frame zero.</param>
    public int FrameIndexAt(long timestampMs)
    {
        if (timestampMs <= 0)
        {
            return 0;
        }

        long end = 0;
        for (int index = 0; index < _frameInfos.Length; index++)
        {
            end += _frameInfos[index].DurationMs;
            if (timestampMs < end)
            {
                return index;
            }
        }

        return _frameInfos.Length - 1;
    }

    private bool DecodeNextFrame(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_nextFrameIndex >= _frameData.Length)
        {
            return false;
        }

        if (_nextFrameIndex > 0)
        {
            WebpFrameData previous = _frameData[_nextFrameIndex - 1];
            if (previous.DisposeToBackground)
            {
                FillRect(previous.X, previous.Y, previous.Width, previous.Height);
            }
        }

        WebpFrameData frame = _frameData[_nextFrameIndex];
        ReadOnlySpan<byte> data = _data.Span;
        ReadOnlySpan<byte> bitstream = data.Slice(frame.BitstreamOffset, frame.BitstreamLength);
        ReadOnlySpan<byte> alpha = frame.AlphaOffset < 0
            ? ReadOnlySpan<byte>.Empty
            : data.Slice(frame.AlphaOffset, frame.AlphaLength);
        byte[] frameRgba = WebpDecoder.DecodeFrameRgba(
            bitstream,
            frame.Format,
            alpha,
            frame.Width,
            frame.Height,
            _fancy,
            cancellationToken);

        for (int y = 0; y < frame.Height; y++)
        {
            Span<byte> destination = _canvas.AsSpan(
                ((frame.Y + y) * CanvasWidth + frame.X) * 4,
                frame.Width * 4);
            ReadOnlySpan<byte> source = frameRgba.AsSpan(y * frame.Width * 4, frame.Width * 4);
            if (frame.Blend)
            {
                BlendRow(source, destination);
            }
            else
            {
                source.CopyTo(destination);
            }
        }

        _nextFrameIndex++;
        return true;
    }

    private void FillCanvas() => FillRect(0, 0, CanvasWidth, CanvasHeight);

    private void FillRect(int x, int y, int width, int height)
    {
        for (int row = y; row < y + height; row++)
        {
            Span<byte> pixels = _canvas.AsSpan((row * CanvasWidth + x) * 4, width * 4);
            pixels.Clear();
        }
    }

    /// <summary>
    /// Alpha-blends a row of a frame over the canvas (non-premultiplied, alpha at byte 3, the colour bytes
    /// treated alike). RFC 9649 gives the blend in real numbers; the integer rounding here is libwebp
    /// 1.6.0's anim_dump's, found black-box (benchmark <c>blendprobe</c>: every source / canvas alpha pair,
    /// 4.7 million colour samples, no difference): the canvas keeps the weight w = ⌊canvasA·(256 − srcA) / 256⌋,
    /// the result's alpha is srcA + w, and each colour is ⌊(src·srcA + canvas·w) · ⌊2^24 / alpha⌋ / 2^24⌋;
    /// a transparent source leaves the canvas as it is, an opaque one replaces it.
    /// </summary>
    internal static void BlendRow(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int offset = 0; offset < source.Length; offset += 4)
        {
            int sourceAlpha = source[offset + 3];
            if (sourceAlpha == 0)
            {
                // The canvas stays; a canvas pixel that is transparent too is cleared, as before.
                if (destination[offset + 3] == 0)
                {
                    destination.Slice(offset, 4).Clear();
                }

                continue;
            }

            if (sourceAlpha == 255)
            {
                source.Slice(offset, 4).CopyTo(destination[offset..]);
                continue;
            }

            int weight = (destination[offset + 3] * (256 - sourceAlpha)) >> 8;
            int alpha = sourceAlpha + weight; // 1..255
            // ⌊2^24 / alpha⌋ · (src·srcA + canvas·w) ≤ 2^24 · 255: fits in 32 unsigned bits.
            uint scale = (1u << 24) / (uint)alpha;
            destination[offset] = BlendChannel(source[offset], destination[offset], sourceAlpha, weight, scale);
            destination[offset + 1] = BlendChannel(source[offset + 1], destination[offset + 1], sourceAlpha, weight, scale);
            destination[offset + 2] = BlendChannel(source[offset + 2], destination[offset + 2], sourceAlpha, weight, scale);
            destination[offset + 3] = (byte)alpha;
        }
    }

    private static byte BlendChannel(int source, int destination, int sourceAlpha, int weight, uint scale) =>
        (byte)(((uint)(source * sourceAlpha + destination * weight) * scale) >> 24);
}
