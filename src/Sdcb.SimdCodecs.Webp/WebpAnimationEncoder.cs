using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Webp;

/// <summary>How the frames of an animation are compressed.</summary>
public enum WebpAnimationMode
{
    /// <summary>Every frame lossy (VP8, with an ALPH plane where it has transparency). The default.</summary>
    Lossy,

    /// <summary>Every frame lossless (VP8L): every visible pixel and every alpha value comes back exactly.</summary>
    Lossless,

    /// <summary>Each frame lossy or lossless, whichever is smaller.</summary>
    Mixed,
}

/// <summary>Options for <see cref="WebpAnimationEncoder"/> and <see cref="WebpEncoder.EncodeAnimation(IReadOnlyList{WebpAnimationCanvasFrame}, int, int, PixelFormat, WebpAnimationEncodeOptions?)"/>.</summary>
public sealed record WebpAnimationEncodeOptions
{
    /// <summary>Lossy (the default), lossless, or the smaller of the two per frame.</summary>
    public WebpAnimationMode Mode { get; init; }

    /// <summary>Quality of lossy frames, 0 to 100, on the scale of <see cref="WebpLossyEncodeOptions.Quality"/>. Default 75.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>Quality of the alpha plane of lossy frames, as <see cref="WebpLossyEncodeOptions.AlphaQuality"/>. Default 100 (exact).</summary>
    public int AlphaQuality { get; init; } = 100;

    /// <summary>Effort of lossy frames, 0 to 6, as <see cref="WebpLossyEncodeOptions.Effort"/>. Default 4.</summary>
    public int LossyEffort { get; init; } = 4;

    /// <summary>Effort of lossless frames, 0 to 9, as <see cref="WebpLosslessEncodeOptions.Effort"/>. Default 6.</summary>
    public int LosslessEffort { get; init; } = 6;

    /// <summary>How many times the animation plays: 0 (the default) is forever, up to 65535.</summary>
    public int LoopCount { get; init; }

    /// <summary>
    /// The ANIM background colour, 0xAARRGGBB (packed as <see cref="WebpAnimation.BackgroundColor"/>). A hint
    /// for players; decoders that follow libwebp ignore it and clear to transparent. Default opaque white, as
    /// libwebp's own animation encoder writes.
    /// </summary>
    public uint BackgroundColor { get; init; } = 0xFFFFFFFFu;

    /// <summary>
    /// 0 (the default): a frame is independent of the ones before it only where that is smaller. N &gt; 0: at
    /// least one frame in every N is independent, so a player can seek to any frame by decoding at most N.
    /// Applies to frames given whole (<see cref="WebpAnimationEncoder.AddFrame(ReadOnlySpan{byte}, PixelFormat, TimeSpan, int)"/>).
    /// </summary>
    public int MaxKeyframeDistance { get; init; }

    /// <summary>An ICC profile to embed (<c>ICCP</c> chunk), or null.</summary>
    public byte[]? IccProfile { get; init; }

    /// <summary>Exif metadata to embed (<c>EXIF</c> chunk), or null.</summary>
    public byte[]? Exif { get; init; }

    /// <summary>XMP metadata to embed (<c>XMP </c> chunk), or null.</summary>
    public byte[]? Xmp { get; init; }

    /// <summary>Tuning: the candidate search level (0..2), or −1 to take it from the efforts.</summary>
    internal int Search { get; init; } = -1;

    /// <summary>Tuning: below the exhaustive search, blend when at least this percentage of the rectangle is unchanged.</summary>
    internal int BlendThreshold { get; init; } = 10;
}

/// <summary>One whole-canvas frame for <see cref="WebpEncoder.EncodeAnimation(IReadOnlyList{WebpAnimationCanvasFrame}, int, int, PixelFormat, WebpAnimationEncodeOptions?)"/>.</summary>
/// <param name="Pixels">The whole canvas, tightly packed in the format given to <c>EncodeAnimation</c>.</param>
/// <param name="Duration">How long the frame is shown; stored in whole milliseconds (rounded), at most 16777215.</param>
public readonly record struct WebpAnimationCanvasFrame(ReadOnlyMemory<byte> Pixels, TimeSpan Duration);

/// <summary>
/// One ANMF frame exactly as given: a rectangle of the canvas, how it is combined with the canvas, and
/// what happens to it before the next frame. For callers who manage frame differences themselves.
/// </summary>
public sealed record WebpAnimationFrame
{
    /// <summary>The rectangle's pixels, <see cref="Width"/> × <see cref="Height"/>, in <see cref="Format"/>.</summary>
    public required ReadOnlyMemory<byte> Pixels { get; init; }

    /// <summary>The layout of <see cref="Pixels"/>. Default RGBA.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba32;

    /// <summary>Bytes from one row of <see cref="Pixels"/> to the next; 0 means tightly packed.</summary>
    public int Stride { get; init; }

    /// <summary>Left edge on the canvas; must be even (the format stores X / 2).</summary>
    public int X { get; init; }

    /// <summary>Top edge on the canvas; must be even (the format stores Y / 2).</summary>
    public int Y { get; init; }

    /// <summary>Width of the rectangle, at least 1.</summary>
    public required int Width { get; init; }

    /// <summary>Height of the rectangle, at least 1.</summary>
    public required int Height { get; init; }

    /// <summary>How long the frame is shown; whole milliseconds (rounded), at most 16777215.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Alpha-blend the rectangle over the canvas (otherwise it replaces the canvas there, alpha included).</summary>
    public bool Blend { get; init; }

    /// <summary>Clear the rectangle to transparent before the next frame is drawn.</summary>
    public bool DisposeToBackground { get; init; }

    /// <summary>Lossless (true) or lossy (false); null follows <see cref="WebpAnimationEncodeOptions.Mode"/>.</summary>
    public bool? Lossless { get; init; }

    /// <summary>Lossy quality of this frame; null follows <see cref="WebpAnimationEncodeOptions.Quality"/>.</summary>
    public int? Quality { get; init; }
}

/// <summary>
/// Pure-managed animated WebP encoder (RFC 9649 Section 2.7.1.1: <c>ANIM</c> and <c>ANMF</c> chunks).
/// Frames are added one at a time and only the current canvas and the compressed frames are kept.
/// <para>A frame given whole is reduced to what changed: unchanged frames only lengthen the previous one;
/// otherwise the encoder tries the bounding rectangle of the changed pixels, drawn over the canvas or with
/// its unchanged pixels made transparent and blended, after the previous frame is kept or cleared, and the
/// whole canvas, and keeps the smallest. Lossless output reproduces every visible pixel and alpha value of
/// every frame. A <see cref="WebpAnimationFrame"/> is written exactly as given.</para>
/// </summary>
public sealed class WebpAnimationEncoder
{
    private const int MaxDuration = 0xFFFFFF;

    private readonly int _w, _h;
    private readonly WebpAnimationEncodeOptions _options;
    private readonly uint[] _intent; // the canvas the caller means to show (before the pending frame's disposal)
    private readonly uint[] _shown;  // the canvas a decoder shows, from the frames as encoded
    private readonly List<byte[]> _done = [];
    private Pending? _pending;
    private int _sinceIndependent;
    private bool _hasAlpha, _encoded;

    /// <summary>A frame written but not yet final: its disposal may still be chosen, its duration extended.</summary>
    private sealed class Pending
    {
        public int X, Y, Width, Height, DurationMs;
        public bool Blend, Dispose, Chosen; // Chosen: the encoder may set Dispose when the next frame is decided
        public required EncodedImage Image;
    }

    private sealed record EncodedImage(bool Lossless, byte[] Bitstream, byte[]? Alph, bool HasAlpha)
    {
        public long ChunkBytes => WebpContainerWriter.ChunkSize(Bitstream.Length) + (Alph is null ? 0 : WebpContainerWriter.ChunkSize(Alph.Length));
    }

    /// <summary>Starts an animation on a canvas of <paramref name="canvasWidth"/> × <paramref name="canvasHeight"/>.</summary>
    /// <exception cref="ArgumentException">The canvas size or an option is out of range.</exception>
    public WebpAnimationEncoder(int canvasWidth, int canvasHeight, WebpAnimationEncodeOptions? options = null)
    {
        options ??= new WebpAnimationEncodeOptions();
        if (canvasWidth is < 1 or > WebpEncoder.MaximumDimension || canvasHeight is < 1 or > WebpEncoder.MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), $"An animation canvas is 1..{WebpEncoder.MaximumDimension} on each side, not {canvasWidth}x{canvasHeight}.");
        }

        if ((long)canvasWidth * canvasHeight > WebpLimits.MaximumPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), $"{canvasWidth}x{canvasHeight} is above the limit of {WebpLimits.MaximumPixels:N0} pixels.");
        }

        Check(options.Quality is >= 0 and <= 100, options.Quality, "Quality must be 0..100.");
        Check(options.AlphaQuality is >= 0 and <= 100, options.AlphaQuality, "AlphaQuality must be 0..100.");
        Check(options.LossyEffort is >= 0 and <= 6, options.LossyEffort, "LossyEffort must be 0..6.");
        Check(options.LosslessEffort is >= 0 and <= 9, options.LosslessEffort, "LosslessEffort must be 0..9.");
        Check(options.LoopCount is >= 0 and <= 65535, options.LoopCount, "LoopCount must be 0..65535.");
        Check(options.MaxKeyframeDistance >= 0, options.MaxKeyframeDistance, "MaxKeyframeDistance must not be negative.");
        Check(Enum.IsDefined(options.Mode), options.Mode, "Unknown animation mode.");
        _w = canvasWidth;
        _h = canvasHeight;
        _options = options;
        _intent = new uint[canvasWidth * canvasHeight];
        _shown = new uint[canvasWidth * canvasHeight];

        static void Check(bool ok, object value, string message)
        {
            if (!ok)
            {
                throw new ArgumentOutOfRangeException(nameof(options), value, message);
            }
        }
    }

    /// <summary>The frames written so far (unchanged whole frames that only lengthened the previous one not counted).</summary>
    public int FrameCount => _done.Count + (_pending is null ? 0 : 1);

    /// <summary>Adds a frame given as the whole canvas.</summary>
    /// <param name="canvas">The canvas, top row first.</param>
    /// <param name="format">Its layout: any <see cref="PixelFormat"/>.</param>
    /// <param name="duration">How long it is shown.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The buffer does not hold the canvas, or the duration is out of range.</exception>
    /// <exception cref="InvalidOperationException">The animation was already encoded.</exception>
    public void AddFrame(ReadOnlySpan<byte> canvas, PixelFormat format, TimeSpan duration, int stride = 0)
    {
        ThrowIfEncoded();
        int durationMs = DurationMs(duration, nameof(duration));
        uint[] target = ArrayPool<uint>.Shared.Rent(_w * _h);
        try
        {
            Convert(canvas, _w, _h, format, stride, target, nameof(canvas));
            AddCanvas(target.AsSpan(0, _w * _h), durationMs);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(target);
        }
    }

    /// <summary>Adds one ANMF frame exactly as described.</summary>
    /// <exception cref="ArgumentException">An odd offset, a rectangle outside the canvas, a buffer that does not hold it, or a duration or quality out of range.</exception>
    /// <exception cref="InvalidOperationException">The animation was already encoded.</exception>
    public void AddFrame(WebpAnimationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfEncoded();
        if (frame.Width < 1 || frame.Height < 1 || frame.X < 0 || frame.Y < 0 ||
            (long)frame.X + frame.Width > _w || (long)frame.Y + frame.Height > _h)
        {
            throw new ArgumentException($"Frame rectangle {frame.X},{frame.Y} {frame.Width}x{frame.Height} is not inside the {_w}x{_h} canvas.", nameof(frame));
        }

        if ((frame.X | frame.Y) % 2 != 0)
        {
            throw new ArgumentException($"Frame offsets must be even, not {frame.X},{frame.Y}.", nameof(frame));
        }

        if (frame.Quality is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), frame.Quality, "Quality must be 0..100.");
        }

        int durationMs = DurationMs(frame.Duration, nameof(frame));
        int n = frame.Width * frame.Height;
        uint[] pixels = ArrayPool<uint>.Shared.Rent(n);
        try
        {
            Convert(frame.Pixels.Span, frame.Width, frame.Height, frame.Format, frame.Stride, pixels, nameof(frame));
            ReadOnlySpan<uint> rect = pixels.AsSpan(0, n);
            WebpAnimationMode mode = frame.Lossless switch
            {
                true => WebpAnimationMode.Lossless,
                false => WebpAnimationMode.Lossy,
                null => _options.Mode,
            };
            EncodedImage image = Encode(rect, frame.Width, frame.Height, mode, frame.Quality ?? _options.Quality);
            Finish(disposePrevious: _pending?.Dispose ?? false);

            // The caller's intent: the rectangle drawn as they asked.
            Draw(_intent, rect, frame.X, frame.Y, frame.Width, frame.Height, frame.Blend);
            Emit(new Pending
            {
                X = frame.X, Y = frame.Y, Width = frame.Width, Height = frame.Height, DurationMs = durationMs,
                Blend = frame.Blend, Dispose = frame.DisposeToBackground, Image = image,
            });
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(pixels);
        }
    }

    /// <summary>The animated WebP file.</summary>
    /// <exception cref="InvalidOperationException">No frame was added.</exception>
    public byte[] Encode()
    {
        if (_pending is null && _done.Count == 0)
        {
            throw new InvalidOperationException("An animation needs at least one frame.");
        }

        if (!_encoded)
        {
            Finish(disposePrevious: _pending!.Dispose);
            _pending = null;
            _encoded = true;
        }

        return WebpContainerWriter.Animation(_w, _h, _hasAlpha, _options.BackgroundColor, _options.LoopCount, _done,
            new WebpMetadata(_options.IccProfile, _options.Exif, _options.Xmp));
    }

    /// <summary>Writes the animated WebP file to <paramref name="output"/>, from its current position.</summary>
    /// <inheritdoc cref="Encode()"/>
    public void Encode(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(Encode());
    }

    // ---------------------------------------------------------------- whole-canvas frames

    private readonly record struct Candidate(bool DisposePrevious, int X, int Y, int Width, int Height, bool Blend, EncodedImage Image)
    {
        public long Bytes => Image.ChunkBytes;
    }

    private void AddCanvas(ReadOnlySpan<uint> target, int durationMs)
    {
        // Nothing visible changed: the previous frame is shown longer.
        if (_pending is { } last && (long)last.DurationMs + durationMs <= MaxDuration && VisiblyEqual(target, _intent))
        {
            last.DurationMs += durationMs;
            return;
        }

        bool forceKey = _pending is null ||
            (_options.MaxKeyframeDistance > 0 && _sinceIndependent >= _options.MaxKeyframeDistance - 1);
        int search = SearchLevel();

        var candidates = new List<Candidate>();
        int n = _w * _h;
        if (!forceKey)
        {
            Pending previous = _pending!;
            uint[] intentBase = ArrayPool<uint>.Shared.Rent(n), rect = ArrayPool<uint>.Shared.Rent(n);
            try
            {
                // Disposal of the previous frame: as it is; and cleared, when this encoder chose its flags and
                // either the search is exhaustive or clearing is what makes pixels transparent that are not now.
                bool tryClear = previous.Chosen && !previous.Dispose &&
                    (search >= 2 || (search >= 1 && Uncovers(target, previous)));
                for (int d = 0; d < (tryClear ? 2 : 1); d++)
                {
                    bool dispose = d == 1 || previous.Dispose;
                    _intent.CopyTo(intentBase, 0);
                    if (dispose)
                    {
                        Clear(intentBase, previous.X, previous.Y, previous.Width, previous.Height);
                    }

                    if (!ChangedBounds(target, intentBase, out int x0, out int y0, out int x1, out int y1, out long changed))
                    {
                        continue; // nothing to draw after a clear that changed nothing: covered by "as it is"
                    }

                    x0 &= ~1;
                    y0 &= ~1;
                    int w = x1 - x0, h = y1 - y0;
                    bool blendValid = BlendValid(target, intentBase, x0, y0, w, h);

                    // Below the exhaustive search, blend only where it leaves enough of the rectangle to the
                    // canvas, and draw over it otherwise: the two cost the same when nearly everything changed.
                    long area = (long)w * h;
                    bool preferBlend = blendValid && (area - changed) * 100 >= area * _options.BlendThreshold;
                    if (search >= 2 || !preferBlend)
                    {
                        CopyRect(target, rect, x0, y0, w, h, transparentWhereEqual: null);
                        candidates.Add(new Candidate(dispose, x0, y0, w, h, false, Encode(rect.AsSpan(0, w * h), w, h, _options.Mode, _options.Quality)));
                    }

                    if (blendValid && (search >= 2 || preferBlend))
                    {
                        CopyRect(target, rect, x0, y0, w, h, transparentWhereEqual: intentBase);
                        candidates.Add(new Candidate(dispose, x0, y0, w, h, true, Encode(rect.AsSpan(0, w * h), w, h, _options.Mode, _options.Quality)));
                    }
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(intentBase);
                ArrayPool<uint>.Shared.Return(rect);
            }
        }

        // The whole canvas drawn over, a frame a decoder can start from: when forced, when nothing else is
        // possible, and (unless a candidate already is the whole canvas) in the exhaustive search or when the
        // changes cover at least half the canvas.
        bool haveFull = candidates.Exists(c => !c.Blend && !c.DisposePrevious && c.Width == _w && c.Height == _h);
        long largest = candidates.Count == 0 ? 0 : candidates.Max(c => (long)c.Width * c.Height);
        if (forceKey || candidates.Count == 0 || (!haveFull && (search >= 2 || (search >= 1 && largest * 2 >= n))))
        {
            candidates.Add(new Candidate(false, 0, 0, _w, _h, false, Encode(target, _w, _h, _options.Mode, _options.Quality)));
        }

        Candidate best = candidates[0];
        foreach (Candidate c in candidates)
        {
            if (c.Bytes < best.Bytes)
            {
                best = c;
            }
        }

        if (forceKey)
        {
            best = candidates[^1];
        }

        Finish(best.DisposePrevious);
        target.CopyTo(_intent);
        Emit(new Pending
        {
            X = best.X, Y = best.Y, Width = best.Width, Height = best.Height, DurationMs = durationMs,
            Blend = best.Blend, Image = best.Image, Chosen = true,
        });
    }

    /// <summary>
    /// How hard to look for the smallest frame: 0 one candidate (blend or draw over, the previous frame
    /// kept); 1 also the previous frame cleared where that is what makes pixels transparent, and the whole
    /// canvas when the changes cover half of it; 2 every combination. From the efforts unless set for tuning.
    /// </summary>
    private int SearchLevel()
    {
        if (_options.Search >= 0)
        {
            return _options.Search;
        }

        int lossy = _options.LossyEffort switch { >= 6 => 2, >= 2 => 1, _ => 0 };
        int lossless = _options.LosslessEffort switch { >= 8 => 2, >= 3 => 1, _ => 0 };
        return _options.Mode switch
        {
            WebpAnimationMode.Lossy => lossy,
            WebpAnimationMode.Lossless => lossless,
            _ => Math.Min(lossy, lossless),
        };
    }

    /// <summary>Whether the target is transparent somewhere in the previous frame's rectangle where the canvas is not.</summary>
    private bool Uncovers(ReadOnlySpan<uint> target, Pending previous)
    {
        for (int y = previous.Y; y < previous.Y + previous.Height; y++)
        {
            for (int x = previous.X; x < previous.X + previous.Width; x++)
            {
                int i = y * _w + x;
                if (target[i] < 0x01000000u && _intent[i] >= 0x01000000u)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether every visible pixel (and transparency) of <paramref name="a"/> is that of <paramref name="b"/>.</summary>
    private static bool VisiblyEqual(ReadOnlySpan<uint> a, ReadOnlySpan<uint> b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && (a[i] >= 0x01000000u || b[i] >= 0x01000000u))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Same(uint a, uint b) => a == b || (a < 0x01000000u && b < 0x01000000u);

    /// <summary>The bounding box [x0, x1) × [y0, y1) of the pixels of <paramref name="target"/> that differ visibly from <paramref name="canvas"/>.</summary>
    private bool ChangedBounds(ReadOnlySpan<uint> target, ReadOnlySpan<uint> canvas, out int x0, out int y0, out int x1, out int y1,
        out long changed)
    {
        x0 = _w;
        y0 = _h;
        x1 = y1 = 0;
        changed = 0;
        for (int y = 0; y < _h; y++)
        {
            int row = y * _w;
            int first = -1, last = -1;
            for (int x = 0; x < _w; x++)
            {
                if (!Same(target[row + x], canvas[row + x]))
                {
                    changed++;
                    if (first < 0)
                    {
                        first = x;
                    }

                    last = x;
                }
            }

            if (first >= 0)
            {
                x0 = Math.Min(x0, first);
                x1 = Math.Max(x1, last + 1);
                y0 = Math.Min(y0, y);
                y1 = y + 1;
            }
        }

        return x1 > 0;
    }

    /// <summary>
    /// Whether blending can draw the rectangle exactly with its unchanged pixels transparent: every changed
    /// pixel is opaque. (A transparent source pixel keeps the canvas and an opaque one replaces it; a partly
    /// transparent one is mixed with the canvas with libwebp's rounding, which does not give the source back
    /// even over a transparent canvas: its colour can come out one lower.)
    /// </summary>
    private bool BlendValid(ReadOnlySpan<uint> target, ReadOnlySpan<uint> intent, int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int i = y * _w + x;
                if (target[i] < 0xFF000000u && !Same(target[i], intent[i]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The rectangle of <paramref name="canvas"/>; pixels equal to <paramref name="transparentWhereEqual"/> (when given) as transparent black.</summary>
    private void CopyRect(ReadOnlySpan<uint> canvas, uint[] rect, int x0, int y0, int w, int h, uint[]? transparentWhereEqual)
    {
        for (int y = 0; y < h; y++)
        {
            int src = (y0 + y) * _w + x0;
            Span<uint> dst = rect.AsSpan(y * w, w);
            canvas.Slice(src, w).CopyTo(dst);
            if (transparentWhereEqual is not null)
            {
                for (int x = 0; x < w; x++)
                {
                    if (Same(dst[x], transparentWhereEqual[src + x]))
                    {
                        dst[x] = 0;
                    }
                }
            }
        }
    }

    // ---------------------------------------------------------------- frames

    /// <summary>Encodes one frame's pixels in a mode: lossy, lossless, or the smaller of the two.</summary>
    private EncodedImage Encode(ReadOnlySpan<uint> argb, int width, int height, WebpAnimationMode mode, int quality)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(argb);
        bool lossyFits = width <= WebpEncoder.MaximumLossyDimension && height <= WebpEncoder.MaximumLossyDimension;
        EncodedImage? lossy = null, lossless = null;
        if (mode != WebpAnimationMode.Lossless && lossyFits)
        {
            (byte[] vp8, byte[]? alph) = WebpEncoder.EncodeVp8(bytes, width, height, PixelFormat.Bgra32,
                new WebpLossyEncodeOptions { Quality = quality, Effort = _options.LossyEffort, AlphaQuality = _options.AlphaQuality }, 0, null);
            lossy = new EncodedImage(false, vp8, alph, alph is not null);
        }

        if (mode != WebpAnimationMode.Lossy || lossy is null)
        {
            using Vp8LBitWriter writer = WebpEncoder.EncodeVp8L(bytes, width, height, PixelFormat.Bgra32,
                new WebpLosslessEncodeOptions { Effort = _options.LosslessEffort }, 0, Vp8LAblation.None);
            byte[] vp8l = writer.Finish().ToArray();
            lossless = new EncodedImage(true, vp8l, null, (vp8l[4] & 0x10) != 0);
        }

        return lossy is null ? lossless! : lossless is null || lossy.ChunkBytes <= lossless.ChunkBytes ? lossy : lossless;
    }

    /// <summary>Makes the pending frame final, with its disposal, and applies that disposal to both canvases.</summary>
    private void Finish(bool disposePrevious)
    {
        if (_pending is not { } p)
        {
            return;
        }

        if (p.Chosen)
        {
            p.Dispose = disposePrevious; // a caller's own frame keeps the disposal it was given
        }

        _done.Add(Anmf(p));
        if (p.Dispose)
        {
            Clear(_intent, p.X, p.Y, p.Width, p.Height);
            Clear(_shown, p.X, p.Y, p.Width, p.Height);
        }
    }

    /// <summary>
    /// Makes <paramref name="frame"/> the pending one: draws its decoded pixels on the shown canvas, as any
    /// decoder will, and counts the frames since the last one a decoder can start from.
    /// </summary>
    private void Emit(Pending frame)
    {
        EncodedImage image = frame.Image;
        byte[] rgba = WebpDecoder.DecodeFrameRgba(image.Bitstream, image.Lossless ? WebpFormat.Lossless : WebpFormat.Lossy,
            image.Alph ?? ReadOnlySpan<byte>.Empty, frame.Width, frame.Height);
        int n = frame.Width * frame.Height;
        uint[] argb = ArrayPool<uint>.Shared.Rent(n);
        try
        {
            for (int i = 0; i < n; i++)
            {
                argb[i] = (uint)rgba[4 * i + 3] << 24 | (uint)rgba[4 * i] << 16 | (uint)rgba[4 * i + 1] << 8 | rgba[4 * i + 2];
            }

            Draw(_shown, argb.AsSpan(0, n), frame.X, frame.Y, frame.Width, frame.Height, frame.Blend);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(argb);
        }

        // The decoder's rule (WebpAnimationDecoder): the first frame; a whole-canvas frame that does not
        // blend, or blends but has nothing transparent; or a frame after a whole canvas cleared.
        bool fullCanvas = frame.X == 0 && frame.Y == 0 && frame.Width == _w && frame.Height == _h;
        Pending? previous = _done.Count == 0 ? null : _lastFinished;
        bool independent = _done.Count == 0 ||
            (fullCanvas && (!frame.Blend || (!image.Lossless && !image.HasAlpha))) ||
            (previous is not null && previous.Dispose && previous.X == 0 && previous.Y == 0 && previous.Width == _w && previous.Height == _h);
        _sinceIndependent = independent ? 0 : _sinceIndependent + 1;
        _hasAlpha |= image.HasAlpha || (_done.Count == 0 && !fullCanvas) || (previous?.Dispose ?? false);
        _pending = frame;
    }

    private Pending? _lastFinished;

    /// <summary>The ANMF payload: offset / 2, size − 1, duration, flags (bit 1: do not blend, bit 0: dispose), then the image chunks.</summary>
    private byte[] Anmf(Pending p)
    {
        _lastFinished = p;
        EncodedImage image = p.Image;
        var payload = new byte[16 + image.ChunkBytes];
        Span<byte> s = payload;
        WriteUInt24(s, p.X / 2);
        WriteUInt24(s[3..], p.Y / 2);
        WriteUInt24(s[6..], p.Width - 1);
        WriteUInt24(s[9..], p.Height - 1);
        WriteUInt24(s[12..], p.DurationMs);
        s[15] = (byte)((p.Blend ? 0 : 2) | (p.Dispose ? 1 : 0));
        int at = 16;
        if (image.Alph is not null)
        {
            at = Chunk(s, at, "ALPH"u8, image.Alph);
        }

        Chunk(s, at, image.Lossless ? "VP8L"u8 : "VP8 "u8, image.Bitstream);
        return payload;

        static int Chunk(Span<byte> s, int at, ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> data)
        {
            fourCc.CopyTo(s[at..]);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(at + 4)..], (uint)data.Length);
            data.CopyTo(s[(at + 8)..]);
            return at + 8 + data.Length + (data.Length & 1); // the pad byte is already 0
        }

        static void WriteUInt24(Span<byte> s, int value)
        {
            s[0] = (byte)value;
            s[1] = (byte)(value >> 8);
            s[2] = (byte)(value >> 16);
        }
    }

    // ---------------------------------------------------------------- canvases

    /// <summary>Draws a rectangle on a canvas as a decoder does: copied, or alpha-blended with the decoder's arithmetic.</summary>
    private void Draw(uint[] canvas, ReadOnlySpan<uint> rect, int x0, int y0, int w, int h, bool blend)
    {
        for (int y = 0; y < h; y++)
        {
            Span<uint> dst = canvas.AsSpan((y0 + y) * _w + x0, w);
            ReadOnlySpan<uint> src = rect.Slice(y * w, w);
            if (blend)
            {
                // The blend reads and writes alpha at byte 3 and treats the colour bytes alike, so it works on
                // little-endian ARGB words (B, G, R, A) as on RGBA.
                WebpAnimationDecoder.BlendRow(MemoryMarshal.AsBytes(src), MemoryMarshal.AsBytes(dst));
            }
            else
            {
                src.CopyTo(dst);
            }
        }
    }

    private void Clear(uint[] canvas, int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            canvas.AsSpan(y * _w + x0, w).Clear();
        }
    }

    private static void Convert(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, int stride, uint[] argb, string name)
    {
        if (!PixelConverter.TryGetLayout(width, height, format, stride, pixels.Length, out int rowStride))
        {
            throw new ArgumentException($"The pixels do not hold a {width}x{height} {format} image with stride {stride}.", name);
        }

        WebpEncoder.ToArgb(pixels, width, height, format, rowStride, argb);
    }

    private static int DurationMs(TimeSpan duration, string name)
    {
        double ms = Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero);
        if (!(ms >= 0 && ms <= MaxDuration))
        {
            throw new ArgumentOutOfRangeException(name, duration, $"A frame duration is 0..{MaxDuration} ms.");
        }

        return (int)ms;
    }

    private void ThrowIfEncoded()
    {
        if (_encoded)
        {
            throw new InvalidOperationException("The animation was already encoded; start a new encoder.");
        }
    }
}

public static partial class WebpEncoder
{
    /// <summary>Encodes an animation from whole-canvas frames, in order (see <see cref="WebpAnimationEncoder"/>).</summary>
    /// <param name="frames">The frames; each holds the whole canvas in <paramref name="format"/>, tightly packed.</param>
    /// <param name="width">Canvas width, 1..16384.</param>
    /// <param name="height">Canvas height, 1..16384.</param>
    /// <param name="format">The layout of every frame: any <see cref="PixelFormat"/>.</param>
    /// <param name="options">Mode, quality, effort, looping and metadata; null for the defaults.</param>
    /// <exception cref="ArgumentException">No frames, or a size, buffer or duration that does not fit.</exception>
    public static byte[] EncodeAnimation(IReadOnlyList<WebpAnimationCanvasFrame> frames, int width, int height, PixelFormat format,
        WebpAnimationEncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("An animation needs at least one frame.", nameof(frames));
        }

        var encoder = new WebpAnimationEncoder(width, height, options);
        foreach (WebpAnimationCanvasFrame frame in frames)
        {
            encoder.AddFrame(frame.Pixels.Span, format, frame.Duration);
        }

        return encoder.Encode();
    }

    /// <summary>Encodes an animation into <paramref name="output"/>, from its current position.</summary>
    /// <inheritdoc cref="EncodeAnimation(IReadOnlyList{WebpAnimationCanvasFrame}, int, int, PixelFormat, WebpAnimationEncodeOptions?)"/>
    public static void EncodeAnimation(IReadOnlyList<WebpAnimationCanvasFrame> frames, int width, int height, PixelFormat format,
        Stream output, WebpAnimationEncodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(EncodeAnimation(frames, width, height, format, options));
    }
}
