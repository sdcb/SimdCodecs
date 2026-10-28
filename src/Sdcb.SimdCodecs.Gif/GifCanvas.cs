using System.Buffers;
using System.Runtime.InteropServices;

namespace Sdcb.SimdCodecs.Gif;

/// <summary>
/// Composites a GIF's frames onto its canvas (tightly packed RGBA), one at a time, as a viewer shows them:
/// before a frame is drawn the previous one is disposed (left, cleared, or restored to what it covered),
/// then the frame's pixels are drawn over the canvas, except those of its transparent index. Pixels outside
/// the canvas are clipped; a palette index outside the colour table (or a frame with no colour table at
/// all) draws opaque black.
/// <para>
/// The canvas starts, and is cleared to, transparent black, except in an opaque GIF
/// (<see cref="GifContainer.IsOpaque"/>), where it is opaque black: there a pixel only stays undrawn when
/// image data is cut short, and the image is presented as RGB, which has no transparency to show.
/// </para>
/// <para>
/// Frames are decoded in order cheaply; any other frame is reached by replaying from the nearest frame that
/// does not depend on those before it: one that covers the whole canvas with no transparent index, or one
/// whose predecessor covered the whole canvas and was cleared. The canvas keeps no reference to the file:
/// each call is given it, so a still decode can composite straight from the caller's span.
/// </para>
/// </summary>
internal sealed class GifCanvas
{
    private readonly GifContainer _container;
    private readonly bool[] _independent;
    private readonly byte[] _canvas;
    private readonly uint _empty;      // what the canvas starts as and is cleared to
    private byte[]? _saved;            // what a frame disposed to "previous" covered
    private int _pending = -1;         // the frame drawn last, whose disposal is still to apply
    private int _next;                 // the frame the canvas is ready for

    public GifCanvas(GifContainer container)
    {
        _container = container;
        _canvas = new byte[checked(container.Width * container.Height * 4)];
        _empty = container.IsOpaque ? (BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu) : 0u;
        Fill(0, 0, container.Width, container.Height);

        var frames = container.Frames;
        _independent = new bool[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            _independent[i] = i == 0
                || (Covers(frames[i]) && frames[i].TransparentIndex < 0)
                || (frames[i - 1].Disposal == GifDisposal.Background && Covers(frames[i - 1]));
        }
    }

    public int Width => _container.Width;

    public int Height => _container.Height;

    /// <summary>The canvas as the last composited frame left it.</summary>
    public ReadOnlySpan<byte> Pixels => _canvas;

    /// <summary>
    /// Composites frame <paramref name="index"/> of <paramref name="data"/> (the file the container was read
    /// from), replaying from an independent frame unless it is the next one.
    /// </summary>
    /// <exception cref="InvalidDataException">The frame's LZW minimum code size is invalid.</exception>
    public void DecodeFrame(ReadOnlySpan<byte> data, int index)
    {
        if (index != _next)
        {
            var start = index;
            while (!_independent[start]) start--;
            Fill(0, 0, Width, Height);
            _pending = -1;
            for (_next = start; _next < index;) Draw(data, _next);
        }
        Draw(data, index);
    }

    private bool Covers(GifFrame f) => f.X == 0 && f.Y == 0 && f.Width >= Width && f.Height >= Height;

    private void Draw(ReadOnlySpan<byte> data, int index)
    {
        var frame = _container.Frames[index];
        // A frame that throws leaves _next unchanged, so the canvas still matches it: the disposal below is
        // applied again on the next attempt, which is idempotent, and nothing else has changed yet.
        if (_pending >= 0)
        {
            var previous = _container.Frames[_pending];
            if (previous.Disposal == GifDisposal.Background)
                ClearRect(previous);
            else if (previous.Disposal == GifDisposal.Previous)
                RestoreRect(previous);
        }

        var pixels = frame.Width * frame.Height;
        var indices = ArrayPool<byte>.Shared.Rent(Math.Max(pixels, 1));
        try
        {
            int written;
            var codes = GifLzw.GatherSubBlocks(data, frame.DataOffset, out var codeLength);
            try
            {
                written = GifLzw.Decode(codes.AsSpan(0, codeLength), frame.MinCodeSize, indices.AsSpan(0, pixels));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(codes);
            }

            if (frame.Disposal == GifDisposal.Previous) SaveRect(frame);
            Blit(data, frame, indices.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(indices);
        }

        _pending = index;
        _next = index + 1;
    }

    // Draws the decoded indices (in stored row order, possibly cut short) over the canvas.
    private void Blit(ReadOnlySpan<byte> data, GifFrame frame, ReadOnlySpan<byte> indices)
    {
        if (frame.Width == 0 || frame.X >= Width || frame.Y >= Height) return; // nothing on the canvas
        Span<uint> palette = stackalloc uint[256];
        BuildPalette(data, frame, palette);
        var transparent = frame.TransparentIndex;
        var canvas = MemoryMarshal.Cast<byte, uint>(_canvas.AsSpan());

        // Clipped to the canvas: columns [0, right) and rows below bottom of the image.
        var right = Math.Min(frame.Width, Width - frame.X);
        var bottom = Math.Min(frame.Y + frame.Height, Height);

        // An interlaced image stores rows 0, 8, 16, ..., then 4, 12, ..., then 2, 6, ..., then 1, 3, ...
        // (GIF89a Appendix E). Only the rows the data reached are drawn.
        var rows = (indices.Length + frame.Width - 1) / frame.Width;
        for (var r = 0; r < rows; r++)
        {
            var y = frame.Y + (frame.Interlaced ? InterlacedRow(r, frame.Height) : r);
            if (y >= bottom) continue;
            var source = indices.Slice(r * frame.Width, Math.Min(frame.Width, indices.Length - r * frame.Width));
            var row = canvas.Slice(y * Width + frame.X);
            var end = Math.Min(right, source.Length);
            for (var i = 0; i < end; i++)
            {
                var p = source[i];
                if (p != transparent) row[i] = palette[p];
            }
        }
    }

    // RGBA as a native uint per entry; entries past the table are opaque black.
    private static void BuildPalette(ReadOnlySpan<byte> data, GifFrame frame, Span<uint> palette)
    {
        palette.Fill(BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu);
        if (frame.PaletteOffset < 0) return;
        var table = data.Slice(frame.PaletteOffset, frame.PaletteCount * 3);
        Span<byte> rgba = [0, 0, 0, 255];
        for (var i = 0; i < frame.PaletteCount; i++)
        {
            rgba[0] = table[3 * i];
            rgba[1] = table[3 * i + 1];
            rgba[2] = table[3 * i + 2];
            palette[i] = MemoryMarshal.Read<uint>(rgba);
        }
    }

    /// <summary>The canvas row (from the image's top) of stored row <paramref name="r"/> of an interlaced image.</summary>
    internal static int InterlacedRow(int r, int height)
    {
        var pass1 = (height + 7) / 8;
        if (r < pass1) return r * 8;
        r -= pass1;
        var pass2 = (height + 3) / 8;
        if (r < pass2) return 4 + r * 8;
        r -= pass2;
        var pass3 = (height + 1) / 4;
        if (r < pass3) return 2 + r * 4;
        return 1 + (r - pass3) * 2;
    }

    private (int X, int Y, int Width, int Height) Clip(GifFrame f)
    {
        int x0 = Math.Min(f.X, Width), y0 = Math.Min(f.Y, Height);
        return (x0, y0, Math.Min(f.X + f.Width, Width) - x0, Math.Min(f.Y + f.Height, Height) - y0);
    }

    private void ClearRect(GifFrame f)
    {
        var (x, y, w, h) = Clip(f);
        Fill(x, y, w, h);
    }

    private void Fill(int x, int y, int w, int h)
    {
        var canvas = MemoryMarshal.Cast<byte, uint>(_canvas.AsSpan());
        for (var row = y; row < y + h; row++)
            canvas.Slice(row * Width + x, w).Fill(_empty);
    }

    private void SaveRect(GifFrame f)
    {
        var (x, y, w, h) = Clip(f);
        if (_saved is null || _saved.Length < w * h * 4) _saved = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            _canvas.AsSpan(((y + row) * Width + x) * 4, w * 4).CopyTo(_saved.AsSpan(row * w * 4));
    }

    private void RestoreRect(GifFrame f)
    {
        var (x, y, w, h) = Clip(f);
        for (var row = 0; row < h; row++)
            _saved.AsSpan(row * w * 4, w * 4).CopyTo(_canvas.AsSpan(((y + row) * Width + x) * 4));
    }
}
