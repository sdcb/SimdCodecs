using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;
using Sdcb.SimdCodecs.Heif.Hevc;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// The output of one decode: a caller's buffer in a <see cref="PixelFormat"/> with a row stride, and the
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
/// are written by the row kernel itself; Gray8 is <see cref="HeifColor.ToGrayRow"/> (the stored luma), except for
/// the identity matrix, whose Y plane is G: that takes the luma of the RGBA row; every other 8-bit format converts
/// the RGBA row with <see cref="PixelConverter"/>. 16-bit formats come from the 16-bit conversion
/// (<see cref="HeifColor.ToRgba64420Row"/>), RGBA64 written directly, the others converted from its row. Those
/// are exactly the bytes of decoding to RGBA (8- or 16-bit) and converting.
/// </para>
/// <para>
/// <b>Scaling.</b> With a factor d &gt; 1 an output pixel is a box of d × d displayed pixels (<see cref="BoxDownscaler"/>).
/// Its preimage on the canvas is a d × d square too, so when every tile's displayed rectangle starts on a box edge and
/// ends on one or at the image's edge (<see cref="BoxesAlignWithTiles"/>), a tile holds whole boxes: it converts its
/// pixels to Rgb24 / Rgb48, pushes them in display order (rows, reversed rows, or columns after a quarter turn) through a
/// <see cref="BoxDownscaler"/> of its own and converts each finished row of boxes into the output. The integer box sums
/// do not depend on the order of the pixels, so the boxes are those of downscaling the whole image.
/// </para>
/// </remarks>
internal sealed unsafe class HeifCanvas
{
    private const int ChunkPixels = 256;

    private readonly byte* _destination;
    private readonly long _length;
    private readonly int _stride;
    private readonly PixelFormat _format;
    private readonly int _bpp;
    private readonly HeifColor.Coefficients _k;
    private readonly bool _wide; // 16-bit output: _k is the 16-bit coefficient set
    private readonly CancellationToken _cancellationToken;

    // Scaled output (_factor > 1): _format is the layout the boxes average (Rgb24, or Rgb48 above 8 bits: TryDecode's),
    // _outFormat the destination's, converted from each finished output row.
    private readonly int _factor;
    private readonly PixelFormat _outFormat;
    private readonly int _outBpp;

    // output = (a·x + b·y + c, d·x + e·y + f) for canvas (x, y) inside the region.
    private readonly int _a, _b, _c, _d, _e, _f;
    private readonly int _regionX0, _regionY0, _regionX1, _regionY1;

    /// <summary>The displayed size: the output's width and height. When scaled, the size of the displayed pixels the
    /// output's boxes average (<see cref="BoxDownscaler.GetSourceRect"/>), not the output's.</summary>
    internal int Width { get; }

    /// <inheritdoc cref="Width"/>
    internal int Height { get; }

    /// <summary>
    /// Sets up the output for <paramref name="container"/>'s canvas. <paramref name="destination"/> must stay pinned
    /// while tiles are written; it must hold the display size at <paramref name="stride"/> bytes per row.
    /// <para>
    /// With <paramref name="factor"/> &gt; 1 the output is the display image box-filtered by that factor
    /// (<see cref="BoxDownscaler"/>), or its <paramref name="region"/> (then in scaled coordinates): each tile boxes the
    /// <paramref name="boxFormat"/> pixels it converts and writes the finished boxes, converted to
    /// <paramref name="format"/>; <paramref name="destination"/> holds the scaled size. That needs every tile's pixels
    /// to make whole boxes (<see cref="BoxesAlignWithTiles"/>).
    /// </para>
    /// </summary>
    internal HeifCanvas(HeifContainer container, byte* destination, long length, int stride, PixelFormat format,
        in HeifColor.Coefficients coefficients, CancellationToken cancellationToken = default, PixelRect? region = null,
        int factor = 1, PixelFormat boxFormat = default)
    {
        if (factor > 1 && boxFormat != PixelFormat.Rgb24 && boxFormat != PixelFormat.Rgb48)
            throw new ArgumentException("A scaled output boxes Rgb24 or Rgb48 pixels.", nameof(boxFormat));
        _destination = destination;
        _length = length;
        _stride = stride;
        _factor = Math.Max(factor, 1);
        _outFormat = format;
        _outBpp = format.BytesPerPixel;
        _format = _factor > 1 ? boxFormat : format;
        _bpp = _format.BytesPerPixel;
        _k = coefficients;
        _wide = _format.Sample == SampleFormat.UInt16;
        _cancellationToken = cancellationToken;

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
        // A region of the displayed image is one more translation: output' = output − (X, Y), on a Width × Height
        // rectangle. The preimage below then shrinks to the canvas pixels that land in it, so tiles outside it are
        // skipped (Intersects) and only its pixels are converted.
        // Scaled, the translation is to the displayed pixels the scaled region's boxes average (all of them without a
        // region): output box (bx, by) then averages output pixels [bx·d, bx·d + d) × [by·d, by·d + d), clipped.
        int outW, outH;
        if (_factor > 1)
        {
            var scaledW = (int)(((long)width + _factor - 1) / _factor);
            var scaledH = (int)(((long)height + _factor - 1) / _factor);
            var scaled = region ?? new PixelRect(0, 0, scaledW, scaledH);
            if (!scaled.FitsIn(scaledW, scaledH))
                throw new ArgumentException($"The region {scaled} does not lie inside the {scaledW}x{scaledH} image.");
            region = BoxDownscaler.GetSourceRect(scaled, width, height, _factor);
            (outW, outH) = (scaled.Width, scaled.Height);
        }
        else
        {
            (outW, outH) = region is { } q ? (q.Width, q.Height) : (width, height);
        }
        if (region is { } r)
        {
            if (!r.FitsIn(width, height))
                throw new ArgumentException($"The region {r} does not lie inside the {width}x{height} image.");
            c -= r.X;
            f -= r.Y;
            (width, height) = (r.Width, r.Height);
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

        // The output rectangle the destination must hold: the region (scaled, when scaled) in the output format.
        if (width <= 0 || height <= 0 || outW <= 0 || outH <= 0 || stride < (long)outW * _outBpp
            || length < (long)(outH - 1) * stride + (long)outW * _outBpp)
        {
            throw new ArgumentException("The destination does not fit the image.");
        }
    }

    /// <summary>
    /// The output rectangle (before scaling) that canvas pixels [x0, x1) × [y0, y1) map onto: a signed permutation and a
    /// translation send a rectangle to a rectangle.
    /// </summary>
    private (int U0, int U1, int V0, int V1) ToOutput(int x0, int x1, int y0, int y1)
    {
        if (_a != 0)
        {
            var (u0, u1) = MapRange(_a, _c, x0, x1);
            var (v0, v1) = MapRange(_e, _f, y0, y1);
            return (u0, u1, v0, v1);
        }
        else
        {
            var (u0, u1) = MapRange(_b, _c, y0, y1);
            var (v0, v1) = MapRange(_d, _f, x0, x1);
            return (u0, u1, v0, v1);
        }
    }

    /// <summary>The canvas rectangle that output pixels [u0, u1) × [v0, v1) come from: <see cref="ToOutput"/> inverted.</summary>
    private (int X0, int X1, int Y0, int Y1) ToCanvas(int u0, int u1, int v0, int v1)
    {
        // v = s·x + o with s = ±1 inverts to x = s·v − s·o: the same form, with offset −s·o.
        if (_a != 0)
        {
            var (x0, x1) = MapRange(_a, -_a * _c, u0, u1);
            var (y0, y1) = MapRange(_e, -_e * _f, v0, v1);
            return (x0, x1, y0, y1);
        }
        else
        {
            var (y0, y1) = MapRange(_b, -_b * _c, u0, u1);
            var (x0, x1) = MapRange(_d, -_d * _f, v0, v1);
            return (x0, x1, y0, y1);
        }
    }

    /// <summary>The image of [lo, hi) under v ↦ s·v + offset (s = ±1).</summary>
    private static (int Lo, int Hi) MapRange(int s, int offset, int lo, int hi) =>
        s > 0 ? (lo + offset, hi + offset) : (offset - hi + 1, offset - lo + 1);

    /// <summary>
    /// True when every tile's visible pixels make whole boxes of the scaled output, so each tile can box its own pixels
    /// (<see cref="WriteTile"/>): the output rectangle of every tile that reaches the output starts on a multiple of the
    /// factor and ends on one or at the output's edge, on both axes. Always true for one coded image (its rectangle is
    /// the whole output), and for a grid when the tile seams, carried through clap / irot / imir to display coordinates,
    /// fall on multiples of the factor (a region does not change that: its boxes start on multiples of the factor).
    /// </summary>
    internal bool BoxesAlignWithTiles(int tileCount, int columns, int tileW, int tileH)
    {
        if (_factor == 1) return true;
        for (var i = 0; i < tileCount; i++)
        {
            int x = i % columns * tileW, y = i / columns * tileH;
            if (!Intersects(x, y, tileW, tileH)) continue;
            var (u0, u1, v0, v1) = ToOutput(Math.Max(x, _regionX0), Math.Min(x + tileW, _regionX1),
                Math.Max(y, _regionY0), Math.Min(y + tileH, _regionY1));
            if (!Aligned(u0, u1, Width) || !Aligned(v0, v1, Height)) return false;
        }
        return true;
    }

    private bool Aligned(int lo, int hi, int size) => lo % _factor == 0 && (hi % _factor == 0 || hi == size);

    /// <summary>True when canvas pixels [x, x + w) × [y, y + h) reach the output: a tile outside the region is skipped.</summary>
    internal bool Intersects(int x, int y, int w, int h) =>
        x < _regionX1 && x + w > _regionX0 && y < _regionY1 && y + h > _regionY0;

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
    internal void WriteTile(HevcPicture picture, int canvasX, int canvasY, int tileW, int tileH, bool parallel = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_factor > 1)
        {
            WriteTileScaled(picture, canvasX, canvasY, tileW, tileH, parallel);
            return;
        }
        var ys = Math.Max(canvasY, _regionY0);
        var ye = Math.Min(canvasY + tileH, _regionY1);
        const int Band = 64;
        if (!parallel || ye - ys <= Band || Environment.ProcessorCount < 2)
        {
            WriteTileRows(picture, canvasX, canvasY, tileW, ys, ye);
            return;
        }
        // Bands of rows write disjoint output pixels.
        Parallel.For(0, (ye - ys + Band - 1) / Band, new ParallelOptions { CancellationToken = _cancellationToken }, band =>
            WriteTileRows(picture, canvasX, canvasY, tileW, ys + band * Band, Math.Min(ys + (band + 1) * Band, ye)));
    }

    private void WriteTileRows(HevcPicture picture, int canvasX, int canvasY, int tileW, int ys, int ye)
    {
        var xs = Math.Max(canvasX, _regionX0);
        var xe = Math.Min(canvasX + tileW, _regionX1);
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

    // Output rows (or, rotated, columns) a band of a scaled tile spans before the factor rounds it up: a band boxes its
    // pixels on its own, so it bounds the per-band buffers (one row; rotated, this many canvas rows).
    private const int ScaledBand = 64;

    /// <summary>
    /// The scaled <see cref="WriteTile"/>: the tile's visible pixels, which make whole boxes (<see cref="BoxesAlignWithTiles"/>),
    /// are converted to the box layout, boxed and written into the output boxes they make, in bands of whole boxes
    /// across the output axis canvas rows run along (in parallel for a coded image decoded alone). Bands, like tiles,
    /// write disjoint output pixels.
    /// </summary>
    private void WriteTileScaled(HevcPicture picture, int canvasX, int canvasY, int tileW, int tileH, bool parallel)
    {
        var xs = Math.Max(canvasX, _regionX0);
        var xe = Math.Min(canvasX + tileW, _regionX1);
        var ys = Math.Max(canvasY, _regionY0);
        var ye = Math.Min(canvasY + tileH, _regionY1);
        if (xs >= xe || ys >= ye) return;
        var (u0, u1, v0, v1) = ToOutput(xs, xe, ys, ye);
        if (!Aligned(u0, u1, Width) || !Aligned(v0, v1, Height))
            throw new InvalidOperationException("A tile's pixels do not make whole boxes: check BoxesAlignWithTiles first.");

        var band = Math.Max(ScaledBand, _factor); // both powers of two: a multiple of the factor
        var rows = _a != 0; // canvas rows land on output rows; otherwise (a quarter turn) on output columns
        int lo = rows ? v0 : u0, hi = rows ? v1 : u1;
        var count = (int)(((long)hi - lo + band - 1) / band);
        void Band(int k)
        {
            var s = lo + k * band;
            var t = (int)Math.Min((long)s + band, hi);
            if (rows) BoxBand(picture, canvasX, canvasY, u0, u1, s, t);
            else BoxBand(picture, canvasX, canvasY, s, t, v0, v1);
        }
        if (!parallel || count == 1 || Environment.ProcessorCount < 2)
        {
            for (var k = 0; k < count; k++) Band(k);
            return;
        }
        Parallel.For(0, count, new ParallelOptions { CancellationToken = _cancellationToken }, Band);
    }

    /// <summary>
    /// Boxes output pixels [u0, u1) × [v0, v1) (before scaling; whole boxes, from multiples of the factor) of the picture
    /// and writes the boxes, converted to the output format. Output row v is pushed as its own pixels in order: canvas
    /// row e·(v − f) read left to right (mirrored: right to left), or after a quarter turn canvas column d·(v − f), read
    /// from a scratch of the band's converted canvas rows.
    /// </summary>
    private void BoxBand(HevcPicture picture, int canvasX, int canvasY, int u0, int u1, int v0, int v1)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var (x0, x1, y0, y1) = ToCanvas(u0, u1, v0, v1);
        int width = u1 - u0, bpp = _bpp, rowBytes = width * bpp;
        var box = new BoxDownscaler(width, v1 - v0, 3, _format.Sample, _factor);
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var taken = _outFormat != _format ? pool.Rent(box.OutputRowBytes) : null;
        var row = pool.Rent(rowBytes);
        byte[]? other = null;
        try
        {
            int ox = u0 / _factor, oy = v0 / _factor;
            if (_a != 0)
            {
                // Output row v is canvas row y = e·(v − f), columns x0 .. x1 − 1 (= u0 .. u1 − 1, reversed when a = −1).
                if (_a < 0) other = pool.Rent(rowBytes);
                for (var v = v0; v < v1; v++)
                {
                    ConvertRow(picture, _e * (v - _f) - canvasY, x0 - canvasX, width, row);
                    var pushed = row;
                    if (other is not null)
                    {
                        for (int i = 0, j = rowBytes - bpp; i < rowBytes; i += bpp, j -= bpp)
                            row.AsSpan(j, bpp).CopyTo(other.AsSpan(i, bpp));
                        pushed = other;
                    }
                    if (box.PushRow(pushed)) Emit(box, ox, oy, taken);
                }
                return;
            }

            // A quarter turn: output row v is canvas column x = d·(v − f), and output pixel u canvas row b·(u − c). The
            // band's canvas rows [y0, y1) are converted first (columns x0 .. x1 − 1), then each output row gathered.
            var canvasRowBytes = (x1 - x0) * bpp;
            other = pool.Rent(canvasRowBytes * (y1 - y0));
            for (var y = y0; y < y1; y++)
                ConvertRow(picture, y - canvasY, x0 - canvasX, x1 - x0, other.AsSpan((y - y0) * canvasRowBytes, canvasRowBytes));
            var step = _b * canvasRowBytes;
            for (var v = v0; v < v1; v++)
            {
                var source = (_b * (u0 - _c) - y0) * canvasRowBytes + (_d * (v - _f) - x0) * bpp;
                for (var i = 0; i < rowBytes; i += bpp, source += step)
                {
                    for (var k = 0; k < bpp; k++)
                        row[i + k] = other[source + k];
                }
                if (box.PushRow(row)) Emit(box, ox, oy, taken);
            }
        }
        finally
        {
            pool.Return(row);
            if (other is not null) pool.Return(other);
            if (taken is not null) pool.Return(taken);
        }
    }

    /// <summary>Takes the output row <paramref name="box"/> finished into the output at box column <paramref name="ox"/>,
    /// row <paramref name="oy"/> + the rows taken before it, through <paramref name="taken"/> when the formats differ.</summary>
    private void Emit(BoxDownscaler box, int ox, int oy, byte[]? taken)
    {
        var offset = (long)(oy + box.RowsTaken) * _stride + (long)ox * _outBpp;
        if (taken is null)
        {
            box.TakeRow(Output(offset, box.OutputRowBytes));
            return;
        }
        box.TakeRow(taken);
        PixelConverter.ConvertRow(taken.AsSpan(0, box.OutputRowBytes), _format, Output(offset, box.OutputWidth * _outBpp),
            _outFormat, box.OutputWidth);
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
        if (_format == PixelFormat.Gray8 && !_k.Identity)
        {
            HeifColor.ToGrayRow(yRow, target, count, _k);
            return;
        }
        if (_wide)
        {
            ConvertRow16(p, row, column, count, yRow, target);
            return;
        }

        // Chroma of the window's pixel x is (CropLeft / 2 + x / 2, CropTop / 2 + row / 2). A run that starts on an
        // odd column takes its first pixel alone so that the kernels see each chroma sample's pair from its start.
        var chromaRow = (p.CropTop / p.SubHeightC + (row >> 1)) * p.ChromaWidth + p.CropLeft / p.SubWidthC;
        var cb = p.Planes[1].AsSpan(chromaRow);
        var cr = p.Planes[2].AsSpan(chromaRow);
        var native = _format == PixelFormat.Rgba32 || _format == PixelFormat.Bgra32;
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
                HeifColor.ToRgba420Row(ySeg, cbSeg, crSeg, target.Slice(done * 4, run * 4), run, _k, _format == PixelFormat.Bgra32);
            }
            else
            {
                var rgba = chunk[..(run * 4)];
                HeifColor.ToRgba420Row(ySeg, cbSeg, crSeg, rgba, run, _k);
                PixelConverter.ConvertRow(rgba, PixelFormat.Rgba32, target.Slice(done * _bpp, run * _bpp), _format, run);
            }
            done += run;
        }
    }

    // The 16-bit twin of ConvertRow: RGBA64 rows from HeifColor.ToRgba64420Row, then the output layout. Grey from a
    // non-identity matrix takes the luma of that RGBA64 row too (the 8-bit path's stored-luma shortcut has no 16-bit
    // counterpart in the frozen conversion), via PixelConverter.
    private void ConvertRow16(HevcPicture p, int row, int column, int count, ReadOnlySpan<ushort> yRow, Span<byte> target)
    {
        var chromaRow = (p.CropTop / p.SubHeightC + (row >> 1)) * p.ChromaWidth + p.CropLeft / p.SubWidthC;
        var cb = p.Planes[1].AsSpan(chromaRow);
        var cr = p.Planes[2].AsSpan(chromaRow);
        var native = _format == PixelFormat.Rgba64 || _format == PixelFormat.Bgra64;
        Span<ushort> chunk = stackalloc ushort[native ? 0 : ChunkPixels * 4];
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
                var out16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(target.Slice(done * 8, run * 8));
                HeifColor.ToRgba64420Row(ySeg, cbSeg, crSeg, out16, run, _k, _format == PixelFormat.Bgra64);
            }
            else
            {
                var rgba = chunk[..(run * 4)];
                HeifColor.ToRgba64420Row(ySeg, cbSeg, crSeg, rgba, run, _k);
                PixelConverter.ConvertRow(System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgba), PixelFormat.Rgba64,
                    target.Slice(done * _bpp, run * _bpp), _format, run);
            }
            done += run;
        }
    }
}
