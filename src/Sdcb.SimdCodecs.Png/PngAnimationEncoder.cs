using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Png;

/// <summary>
/// Pure-managed animated PNG (APNG) encoder: <c>acTL</c>, <c>fcTL</c> and <c>fdAT</c>, PNG 3rd edition 4.9 and 11.3.6. Frames are
/// added whole, as the canvas the viewer should see, and reduced to what changed; the file is written by <see cref="Encode()"/>.
/// <para>
/// A frame equal to the one before only lengthens that frame's delay. Otherwise the encoder tries, for each way of disposing of
/// the previous frame (leave it, clear it, restore what was under it), the bounding rectangle of the pixels that differ, drawn
/// over the canvas (<see cref="PngBlendOp.Source"/>) or, where every changed pixel is opaque, with its unchanged pixels
/// transparent and blended (<see cref="PngBlendOp.Over"/>, which only ever replaces a pixel or leaves it), and keeps the one
/// that deflates smallest (judged by a fast deflate of the rectangle). The first frame is the whole canvas and is the default
/// image, so a reader that does not know APNG shows it. Decoding the result with <see cref="PngAnimationDecoder"/> (or any APNG
/// decoder) gives back every visible pixel and alpha of every frame; the colour of a pixel with alpha 0 is not kept.
/// </para>
/// <para>
/// One colour type, depth and palette serve the whole animation. By default frames are written as RGBA; with
/// <see cref="PngWriteOptions.LosslessReduction"/> the smallest exact layout for all the rectangles together is used (indexed
/// colour, greyscale, a transparent colour, 8 bits for 16-bit samples that are 8-bit values, ...). The frames' rectangles are
/// kept in memory until <see cref="Encode()"/>, since the layout is known only once the last frame is in.
/// <see cref="PngWriteOptions.DiscardAlpha"/> and <see cref="PngWriteOptions.TransparentColor"/> are not accepted (a frame's
/// transparent pixels are how a rectangle is cut down); the metadata and compression options are, and
/// <see cref="PngWriteOptions.LoopCount"/> sets how often the animation plays.
/// </para>
/// </summary>
public sealed class PngAnimationEncoder
{
    private const long MaxFrameMilliseconds = 65535;

    private readonly int _w, _h;
    private readonly bool _wide;
    private readonly PngWriteOptions _options;
    private readonly ulong _max;
    private readonly ulong[] _canvas; // what a decoder shows with the pending frame drawn, before its disposal
    private readonly ulong[] _before; // what it showed before the pending frame was drawn (what Previous restores)
    private readonly List<Frame> _done = [];
    private Frame? _pending;
    private bool _encoded;

    private sealed class Frame(int x, int y, int width, int height, long durationMs, PngBlendOp blend, byte[] pixels)
    {
        public readonly int X = x, Y = y, Width = width, Height = height;
        public readonly PngBlendOp Blend = blend;
        public readonly byte[] Pixels = pixels; // the rectangle, RGBA, host-order samples
        public long DurationMs = durationMs;
        public PngDisposeOp Dispose;
    }

    /// <summary>Starts an animation on a canvas of <paramref name="canvasWidth"/> × <paramref name="canvasHeight"/>.</summary>
    /// <param name="canvasWidth">The canvas width in pixels.</param>
    /// <param name="canvasHeight">The canvas height in pixels.</param>
    /// <param name="options">Metadata, compression and <see cref="PngWriteOptions.LoopCount"/>; null for the defaults.</param>
    /// <param name="sixteenBit">Store 16-bit samples (frames given as 8-bit are widened as <c>v · 257</c>); otherwise 8-bit.</param>
    /// <exception cref="ArgumentOutOfRangeException">The canvas is empty or does not fit in memory.</exception>
    /// <exception cref="ArgumentException">An option that animations do not take is set.</exception>
    public PngAnimationEncoder(int canvasWidth, int canvasHeight, PngWriteOptions? options = null, bool sixteenBit = false)
    {
        options ??= new PngWriteOptions();
        if (canvasWidth < 1 || canvasHeight < 1 || (long)canvasWidth * canvasHeight * 8 > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), $"A {canvasWidth}x{canvasHeight} animation canvas is empty or does not fit in memory.");
        if (options.DiscardAlpha)
            throw new ArgumentException("DiscardAlpha does not apply to an animation; LosslessReduction drops an alpha channel that no frame uses.", nameof(options));
        if (options.TransparentColor is not null)
            throw new ArgumentException("A transparent colour (tRNS) does not apply to an animation; LosslessReduction finds one where it fits.", nameof(options));
        if (options.LoopCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.LoopCount, "LoopCount must not be negative.");
        PngWriter.ValidateText(options);
        _w = canvasWidth;
        _h = canvasHeight;
        _wide = sixteenBit;
        _max = sixteenBit ? 65535UL : 255UL;
        _options = options;
        _canvas = new ulong[canvasWidth * canvasHeight];
        _before = new ulong[canvasWidth * canvasHeight];
    }

    /// <summary>The frames written so far (frames equal to the one before, which only lengthened it, are not counted).</summary>
    public int FrameCount => _done.Count + (_pending is null ? 0 : 1);

    /// <summary>Adds a frame given as the whole canvas.</summary>
    /// <param name="canvas">The canvas, top row first.</param>
    /// <param name="format">Its layout: any <see cref="PixelFormat"/>.</param>
    /// <param name="duration">How long it is shown: stored as a fraction of a second, to the millisecond up to 65.535 s.</param>
    /// <param name="stride">Bytes from one row to the next; 0 means tightly packed.</param>
    /// <exception cref="ArgumentException">The buffer does not hold the canvas, or the duration is negative or above 65535 seconds.</exception>
    /// <exception cref="InvalidOperationException">The animation was already encoded.</exception>
    public void AddFrame(ReadOnlySpan<byte> canvas, PixelFormat format, TimeSpan duration, int stride = 0)
    {
        if (_encoded)
            throw new InvalidOperationException("The animation is already encoded.");
        if (duration < TimeSpan.Zero || duration.TotalSeconds > 65535)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A frame's delay is 0 to 65535 seconds.");
        var native = _wide ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        var bytes = ArrayPool<byte>.Shared.Rent(checked(_w * _h * native.BytesPerPixel));
        var lanes = ArrayPool<ulong>.Shared.Rent(_w * _h);
        try
        {
            PixelConverter.Convert(canvas, stride, format, bytes, 0, native, _w, _h);
            var target = lanes.AsSpan(0, _w * _h);
            if (_wide)
            {
                var s = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan(0, _w * _h * 8));
                for (var i = 0; i < target.Length; i++)
                    target[i] = s[4 * i] | ((ulong)s[4 * i + 1] << 16) | ((ulong)s[4 * i + 2] << 32) | ((ulong)s[4 * i + 3] << 48);
            }
            else
            {
                for (var i = 0; i < target.Length; i++)
                    target[i] = bytes[4 * i] | ((ulong)bytes[4 * i + 1] << 16) | ((ulong)bytes[4 * i + 2] << 32) | ((ulong)bytes[4 * i + 3] << 48);
            }
            AddCanvas(target, (long)Math.Round(duration.TotalMilliseconds));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<ulong>.Shared.Return(lanes);
        }
    }

    /// <summary>The animated PNG file.</summary>
    /// <exception cref="InvalidOperationException">No frame was added.</exception>
    public byte[] Encode()
    {
        if (_pending is null && _done.Count == 0)
            throw new InvalidOperationException("An animation needs at least one frame.");
        if (!_encoded)
        {
            _done.Add(_pending!);
            _pending = null;
            _encoded = true;
        }
        return Write();
    }

    /// <summary>Writes the animated PNG file to <paramref name="output"/>, from its current position.</summary>
    /// <inheritdoc cref="Encode()"/>
    public void Encode(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(Encode());
    }

    // ---------------------------------------------------------------- choosing the frames

    private readonly record struct Candidate(PngDisposeOp Dispose, int X, int Y, int Width, int Height, PngBlendOp Blend,
        byte[] Pixels, long Cost);

    private void AddCanvas(ReadOnlySpan<ulong> target, long durationMs)
    {
        if (_pending is null)
        {
            _pending = new Frame(0, 0, _w, _h, durationMs, PngBlendOp.Source, Extract(target, 0, 0, _w, _h, null));
            target.CopyTo(_canvas);
            return;
        }

        // Nothing visible changed: the previous frame is shown longer.
        if (_pending.DurationMs + durationMs <= MaxFrameMilliseconds && VisiblyEqual(target, _canvas))
        {
            _pending.DurationMs += durationMs;
            return;
        }

        var previous = _pending;
        var candidates = new List<Candidate>();
        // Previous can only restore a canvas that was there; before the first frame there is none.
        var disposals = _done.Count > 0
            ? new[] { PngDisposeOp.None, PngDisposeOp.Background, PngDisposeOp.Previous }
            : new[] { PngDisposeOp.None, PngDisposeOp.Background };
        var baseCanvas = ArrayPool<ulong>.Shared.Rent(_w * _h);
        try
        {
            foreach (var dispose in disposals)
            {
                var basis = baseCanvas.AsSpan(0, _w * _h);
                _canvas.CopyTo(basis);
                if (dispose == PngDisposeOp.Background)
                    ClearRect(basis, previous);
                else if (dispose == PngDisposeOp.Previous)
                    RestoreRect(basis, previous);
                if (dispose != PngDisposeOp.None && RectVisiblyEqual(basis, _canvas, previous))
                    continue; // the same canvas as leaving it

                if (!ChangedBounds(target, basis, out var x0, out var y0, out var x1, out var y1))
                    (x0, y0, x1, y1) = (0, 0, 1, 1); // nothing changed, but a frame is due: one pixel that changes nothing
                int w = x1 - x0, h = y1 - y0;

                var source = Extract(target, x0, y0, w, h, null);
                candidates.Add(new Candidate(dispose, x0, y0, w, h, PngBlendOp.Source, source, Cost(source, w, h)));
                if (OverIsExact(target, basis, x0, y0, w, h))
                {
                    var over = Extract(target, x0, y0, w, h, basis);
                    candidates.Add(new Candidate(dispose, x0, y0, w, h, PngBlendOp.Over, over, Cost(over, w, h)));
                }
            }

            // Where the changes cover half the canvas or more, the whole canvas is a candidate too: it starts a replay.
            var largest = candidates.Max(c => (long)c.Width * c.Height);
            if (largest * 2 >= (long)_w * _h && !candidates.Exists(c => c.Width == _w && c.Height == _h && c.Blend == PngBlendOp.Source && c.Dispose == PngDisposeOp.None))
            {
                var whole = Extract(target, 0, 0, _w, _h, null);
                candidates.Add(new Candidate(PngDisposeOp.None, 0, 0, _w, _h, PngBlendOp.Source, whole, Cost(whole, _w, _h)));
            }

            var best = candidates[0];
            foreach (var c in candidates)
            {
                if (c.Cost < best.Cost)
                    best = c;
            }

            // The canvas the decoder has once the previous frame is disposed as chosen, then this frame is drawn.
            var chosen = baseCanvas.AsSpan(0, _w * _h);
            _canvas.CopyTo(chosen);
            if (best.Dispose == PngDisposeOp.Background)
                ClearRect(chosen, previous);
            else if (best.Dispose == PngDisposeOp.Previous)
                RestoreRect(chosen, previous);
            previous.Dispose = best.Dispose;
            _done.Add(previous);
            chosen.CopyTo(_before);
            chosen.CopyTo(_canvas);
            Draw(_canvas, target, best);
            _pending = new Frame(best.X, best.Y, best.Width, best.Height, durationMs, best.Blend, best.Pixels);
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(baseCanvas);
        }
    }

    private bool Same(ulong a, ulong b) => a == b || (a >> 48 == 0 && b >> 48 == 0);

    private bool VisiblyEqual(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            if (!Same(a[i], b[i]))
                return false;
        }
        return true;
    }

    private bool RectVisiblyEqual(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b, Frame rect)
    {
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            var row = y * _w + rect.X;
            if (!VisiblyEqual(a.Slice(row, rect.Width), b.Slice(row, rect.Width)))
                return false;
        }
        return true;
    }

    private void ClearRect(Span<ulong> canvas, Frame rect)
    {
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            canvas.Slice(y * _w + rect.X, rect.Width).Clear();
    }

    // Previous: the rectangle goes back to what it held before the frame was drawn.
    private void RestoreRect(Span<ulong> canvas, Frame rect)
    {
        for (var y = rect.Y; y < rect.Y + rect.Height; y++)
            _before.AsSpan(y * _w + rect.X, rect.Width).CopyTo(canvas.Slice(y * _w + rect.X, rect.Width));
    }

    /// <summary>The bounding box [x0, x1) × [y0, y1) of the pixels of <paramref name="target"/> that differ visibly from <paramref name="canvas"/>.</summary>
    private bool ChangedBounds(ReadOnlySpan<ulong> target, ReadOnlySpan<ulong> canvas, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = _w;
        y0 = _h;
        x1 = y1 = 0;
        for (var y = 0; y < _h; y++)
        {
            var row = y * _w;
            int first = -1, last = -1;
            for (var x = 0; x < _w; x++)
            {
                if (!Same(target[row + x], canvas[row + x]))
                {
                    if (first < 0)
                        first = x;
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
    /// Whether OVER draws the rectangle exactly with its unchanged pixels transparent: every changed pixel must be opaque, so
    /// that blending replaces it. (A partly transparent changed pixel would be mixed with the canvas, and a decoder's rounding of
    /// that mix is its own.)
    /// </summary>
    private bool OverIsExact(ReadOnlySpan<ulong> target, ReadOnlySpan<ulong> canvas, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                var i = y * _w + x;
                if (target[i] >> 48 != _max && !Same(target[i], canvas[i]))
                    return false;
            }
        }
        return true;
    }

    // What drawing the chosen rectangle does to the canvas, as a decoder does it.
    private void Draw(Span<ulong> canvas, ReadOnlySpan<ulong> target, Candidate c)
    {
        for (var y = c.Y; y < c.Y + c.Height; y++)
        {
            for (var x = c.X; x < c.X + c.Width; x++)
            {
                var i = y * _w + x;
                if (c.Blend == PngBlendOp.Source || !Same(target[i], canvas[i]))
                    canvas[i] = target[i];
            }
        }
    }

    // The rectangle as RGBA samples in host order; with `canvas`, a pixel that is already there (visibly) becomes transparent black.
    private byte[] Extract(ReadOnlySpan<ulong> target, int x0, int y0, int w, int h, ReadOnlySpan<ulong> canvas)
    {
        var bytes = new byte[checked(w * h * (_wide ? 8 : 4))];
        var wide = MemoryMarshal.Cast<byte, ushort>(bytes.AsSpan());
        var k = 0;
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++, k++)
            {
                var i = y * _w + x;
                var p = !canvas.IsEmpty && Same(target[i], canvas[i]) ? 0UL : target[i];
                if (_wide)
                {
                    wide[4 * k] = (ushort)p;
                    wide[4 * k + 1] = (ushort)(p >> 16);
                    wide[4 * k + 2] = (ushort)(p >> 32);
                    wide[4 * k + 3] = (ushort)(p >> 48);
                }
                else
                {
                    bytes[4 * k] = (byte)p;
                    bytes[4 * k + 1] = (byte)(p >> 16);
                    bytes[4 * k + 2] = (byte)(p >> 32);
                    bytes[4 * k + 3] = (byte)(p >> 48);
                }
            }
        }
        return bytes;
    }

    // What a rectangle costs, estimated by a fast deflate of its filtered RGBA rows.
    private long Cost(byte[] pixels, int w, int h)
    {
        var plan = PngEncodePlan.Direct(4, 4, _wide ? 2 : 1, null);
        var counter = new CountingStream();
        using (var deflate = new DeflateStream(counter, CompressionLevel.Fastest, leaveOpen: true))
            PngWriter.CompressRows(deflate, pixels, 0, h, w, plan, CompressionLevel.Fastest, checksum: false);
        return counter.Length;
    }

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }

    // ---------------------------------------------------------------- the file

    private byte[] Write()
    {
        var sampleBytes = _wide ? 2 : 1;
        var plan = _options.LosslessReduction
            ? PngEncodePlan.AnalyzeUnion([.. _done.Select(f => (ReadOnlyMemory<byte>)f.Pixels)], 4, 4, sampleBytes,
                keepColourModel: _options.IccProfile is { Length: > 0 })
            : PngEncodePlan.Direct(4, 4, sampleBytes, null);

        using var ms = new MemoryStream();
        ms.Write(PngWriter.Signature);
        PngWriter.WritePreImageChunks(ms, _w, _h, plan, _options);

        Span<byte> actl = stackalloc byte[8];
        PngWriter.WriteBE(actl, (uint)_done.Count);
        PngWriter.WriteBE(actl[4..], (uint)_options.LoopCount);
        PngWriter.WriteChunk(ms, "acTL"u8, actl);

        uint sequence = 0;
        Span<byte> fctl = stackalloc byte[26];
        for (var i = 0; i < _done.Count; i++)
        {
            var f = _done[i];
            PngWriter.WriteBE(fctl, sequence++);
            PngWriter.WriteBE(fctl[4..], (uint)f.Width);
            PngWriter.WriteBE(fctl[8..], (uint)f.Height);
            PngWriter.WriteBE(fctl[12..], (uint)f.X);
            PngWriter.WriteBE(fctl[16..], (uint)f.Y);
            var (num, den) = Delay(f.DurationMs);
            fctl[20] = (byte)(num >> 8);
            fctl[21] = (byte)num;
            fctl[22] = (byte)(den >> 8);
            fctl[23] = (byte)den;
            fctl[24] = (byte)f.Dispose;
            fctl[25] = (byte)f.Blend;
            PngWriter.WriteChunk(ms, "fcTL"u8, fctl);
            if (i == 0)
                PngWriter.WriteImageDataChunk(ms, "IDAT"u8, null, f.Pixels, f.Width, f.Height, plan, _options);
            else
                PngWriter.WriteImageDataChunk(ms, "fdAT"u8, sequence++, f.Pixels, f.Width, f.Height, plan, _options);
        }

        PngWriter.WritePostImageChunks(ms, _options);
        return ms.ToArray();
    }

    // delay_num / delay_den seconds: milliseconds where they fit, else centiseconds, else seconds.
    private static (int Num, int Den) Delay(long ms)
    {
        if (ms <= MaxFrameMilliseconds)
            return ((int)ms, 1000);
        var centiseconds = (ms + 5) / 10;
        if (centiseconds <= MaxFrameMilliseconds)
            return ((int)centiseconds, 100);
        return ((int)Math.Min((ms + 500) / 1000, MaxFrameMilliseconds), 1);
    }
}
