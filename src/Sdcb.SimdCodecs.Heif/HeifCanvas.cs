using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif.Hevc;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// The output of one decode: a caller's buffer in an 8-bit <see cref="ImagePixelFormat"/> with a row stride, and the
/// mapping from the canvas (the grid image before its transformative properties) to it. Each coded image (grid
/// tile) is converted straight into the rectangle of the output it covers as soon as it is decoded
/// (<see cref="WriteTile"/>), so no canvas-sized intermediate exists and tiles can be written in parallel: they
/// cover disjoint output pixels.
/// </summary>
/// <remarks>
/// <para>
/// <b>Geometry.</b> <c>clap</c> (a crop), <c>irot</c> (quarter turns) and <c>imir</c> (a mirror) compose, in
/// association order (23008-12 §6.5.1), into one map of the form output = (a·x + b·y + c, d·x + e·y + f) with
/// a, b, d, e ∈ {−1, 0, 1} — a signed permutation followed by a translation. Each map is a bijection between the
/// current rectangle and the next one, so a canvas pixel reaches the output exactly when the composed map sends it
/// inside the final rectangle; the preimage of that rectangle (<see cref="_regionX0"/> …) is itself a rectangle.
/// The pixels that land are therefore the same, in the same places, as applying the transforms one whole image at
/// a time.
/// </para>
/// <para>
/// <b>Pixels.</b> Each pixel's bytes come from the frozen <see cref="HeifColor"/> conversion. Rgba32 and Bgra32
/// are written by the row kernel itself; Rgb24 and Bgr24 by converting the row to RGBA and packing it with
/// <see cref="PixelConverter.ConvertRow"/>; Gray8 is <see cref="HeifColor.ToGrayRow"/> (the stored luma), except for
/// the identity matrix, whose Y plane is G: that takes the luma of the RGBA row through
/// <see cref="PixelConverter.ConvertRow"/>. Those are exactly the bytes of decoding to RGBA and converting.
/// </para>
/// </remarks>
internal sealed unsafe class HeifCanvas
{
    private const int ChunkPixels = 256;

    private readonly byte* _destination;
    private readonly long _length;
    private readonly int _stride;
    private readonly ImagePixelFormat _format;
    private readonly int _bpp;
    private readonly HeifColor.Coefficients _k;

    // output = (a·x + b·y + c, d·x + e·y + f) for canvas (x, y) inside the region.
    private readonly int _a, _b, _c, _d, _e, _f;
    private readonly int _regionX0, _regionY0, _regionX1, _regionY1;

    /// <summary>The displayed size: the output's width and height.</summary>
    internal int Width { get; }

    /// <inheritdoc cref="Width"/>
    internal int Height { get; }

    /// <summary>
    /// Sets up the output for <paramref name="container"/>'s canvas. <paramref name="destination"/> must stay pinned
    /// while tiles are written; it must hold the display size at <paramref name="stride"/> bytes per row.
    /// </summary>
    internal HeifCanvas(HeifContainer container, byte* destination, long length, int stride, ImagePixelFormat format,
        in HeifColor.Coefficients coefficients)
    {
        _destination = destination;
        _length = length;
        _stride = stride;
        _format = format;
        _bpp = PixelConverter.BytesPerPixel(format);
        _k = coefficients;

        int canvasW = container.CanvasWidth, canvasH = container.CanvasHeight;
        int a = 1, b = 0, c = 0, d = 0, e = 1, f = 0;
        int width = canvasW, height = canvasH;
        foreach (var t in container.Transforms)
        {
            switch (t.Type)
            {
                case HeifContainer.PropClap:
                {
                    var (x0, y0, w, h) = HeifContainer.ClapRectangle(t.Clap!, width, height);
                    c -= x0;
                    f -= y0;
                    width = w;
                    height = h;
                    break;
                }
                case HeifContainer.PropIrot:
                    // Anti-clockwise quarter turns: 1 → (y, W − 1 − x), 2 → (W − 1 − x, H − 1 − y), 3 → (H − 1 − y, x).
                    switch (t.A)
                    {
                        case 1:
                            (a, b, c, d, e, f) = (d, e, f, -a, -b, width - 1 - c);
                            (width, height) = (height, width);
                            break;
                        case 2:
                            (a, b, c, d, e, f) = (-a, -b, width - 1 - c, -d, -e, height - 1 - f);
                            break;
                        case 3:
                            (a, b, c, d, e, f) = (-d, -e, height - 1 - f, a, b, c);
                            (width, height) = (height, width);
                            break;
                    }
                    break;
                default: // imir: axis 0 mirrors left ↔ right, 1 top ↔ bottom
                    if (t.A == 0) (a, b, c) = (-a, -b, width - 1 - c);
                    else (d, e, f) = (-d, -e, height - 1 - f);
                    break;
            }
        }
        (_a, _b, _c, _d, _e, _f) = (a, b, c, d, e, f);
        Width = width;
        Height = height;

        // The canvas rectangle the final rectangle [0, width) × [0, height) comes from.
        var (xLo, xHi, yLo, yHi) = (0, canvasW, 0, canvasH);
        if (a != 0)
        {
            Clip(a, c, width, ref xLo, ref xHi);
            Clip(e, f, height, ref yLo, ref yHi);
        }
        else
        {
            Clip(b, c, width, ref yLo, ref yHi);
            Clip(d, f, height, ref xLo, ref xHi);
        }
        (_regionX0, _regionX1, _regionY0, _regionY1) = (xLo, Math.Max(xLo, xHi), yLo, Math.Max(yLo, yHi));

        if (width <= 0 || height <= 0 || stride < (long)width * _bpp || length < (long)(height - 1) * stride + (long)width * _bpp)
        {
            throw new ArgumentException("The destination does not fit the image.");
        }
    }

    /// <summary>Narrows [lo, hi) to the values v with 0 ≤ s·v + offset &lt; size (s = ±1).</summary>
    private static void Clip(int s, int offset, int size, ref int lo, ref int hi)
    {
        int from, to;
        if (s > 0)
        {
            from = -offset;
            to = size - offset;
        }
        else
        {
            from = offset - size + 1;
            to = offset + 1;
        }
        lo = Math.Max(lo, from);
        hi = Math.Min(hi, to);
    }

    /// <summary>
    /// Converts the output window of the decoded picture <paramref name="picture"/>, whose top-left output sample sits
    /// at canvas (<paramref name="canvasX"/>, <paramref name="canvasY"/>) and which covers <paramref name="tileW"/> ×
    /// <paramref name="tileH"/> canvas pixels, into the output.
    /// </summary>
    internal void WriteTile(HevcPicture picture, int canvasX, int canvasY, int tileW, int tileH)
    {
        var xs = Math.Max(canvasX, _regionX0);
        var xe = Math.Min(canvasX + tileW, _regionX1);
        var ys = Math.Max(canvasY, _regionY0);
        var ye = Math.Min(canvasY + tileH, _regionY1);
        if (xs >= xe || ys >= ye) return;
        var n = xe - xs;
        var direct = _a == 1 && _d == 0; // a canvas row lands on one output row, left to right
        var row = direct ? null : System.Buffers.ArrayPool<byte>.Shared.Rent(n * _bpp);
        try
        {
            for (var y = ys; y < ye; y++)
            {
                if (direct)
                {
                    var offset = (long)(_e * y + _f) * _stride + (long)(xs + _c) * _bpp;
                    ConvertRow(picture, y - canvasY, xs - canvasX, n, Output(offset, n * _bpp));
                    continue;
                }

                ConvertRow(picture, y - canvasY, xs - canvasX, n, row);
                for (var i = 0; i < n; i++)
                {
                    var x = xs + i;
                    var offset = (long)(_d * x + _e * y + _f) * _stride + (long)(_a * x + _b * y + _c) * _bpp;
                    row.AsSpan(i * _bpp, _bpp).CopyTo(Output(offset, _bpp));
                }
            }
        }
        finally
        {
            if (row is not null) System.Buffers.ArrayPool<byte>.Shared.Return(row);
        }
    }

    /// <summary>
    /// The output bytes at <paramref name="offset"/>. Every pixel the region maps to lies inside the width × height
    /// rectangle the constructor checked against the buffer; the check here keeps a mistake in that reasoning from
    /// writing outside it.
    /// </summary>
    private Span<byte> Output(long offset, int count)
    {
        if (offset < 0 || offset + count > _length) throw new InvalidOperationException("A tile pixel maps outside the output.");
        return new Span<byte>(_destination + offset, count);
    }

    /// <summary>
    /// Converts <paramref name="count"/> pixels of output-window row <paramref name="row"/>, from column
    /// <paramref name="column"/>, into <paramref name="target"/> in the output format.
    /// </summary>
    private void ConvertRow(HevcPicture p, int row, int column, int count, Span<byte> target)
    {
        var yRow = p.Planes[0].AsSpan((p.CropTop + row) * p.Width + p.CropLeft + column, count);
        if (_format == ImagePixelFormat.Gray8 && !_k.Identity)
        {
            HeifColor.ToGrayRow(yRow, target, count, _k);
            return;
        }

        // Chroma of the window's pixel x is (CropLeft / 2 + x / 2, CropTop / 2 + row / 2). A run that starts on an
        // odd column takes its first pixel alone so that the kernels see each chroma sample's pair from its start.
        var chromaRow = (p.CropTop / p.SubHeightC + (row >> 1)) * p.ChromaWidth + p.CropLeft / p.SubWidthC;
        var cb = p.Planes[1].AsSpan(chromaRow);
        var cr = p.Planes[2].AsSpan(chromaRow);
        var native = _format is ImagePixelFormat.Rgba32 or ImagePixelFormat.Bgra32;
        Span<byte> chunk = stackalloc byte[native ? 0 : ChunkPixels * 4];
        var done = 0;
        while (done < count)
        {
            var x = column + done;
            var run = (x & 1) != 0 ? 1 : Math.Min(count - done, ChunkPixels);
            var ySeg = yRow.Slice(done, run);
            var cbSeg = cb.Slice(x >> 1);
            var crSeg = cr.Slice(x >> 1);
            if (native)
            {
                HeifColor.ToRgba420Row(ySeg, cbSeg, crSeg, target.Slice(done * 4, run * 4), run, _k, _format == ImagePixelFormat.Bgra32);
            }
            else
            {
                var rgba = chunk[..(run * 4)];
                HeifColor.ToRgba420Row(ySeg, cbSeg, crSeg, rgba, run, _k);
                PixelConverter.ConvertRow(rgba, 4, SampleFormat.UInt8, target.Slice(done * _bpp, run * _bpp), _format, run);
            }
            done += run;
        }
    }
}
