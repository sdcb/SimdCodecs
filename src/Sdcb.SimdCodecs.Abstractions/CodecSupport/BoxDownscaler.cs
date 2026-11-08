using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Sdcb.SimdCodecs.Abstractions.CodecSupport;

/// <summary>
/// The frozen box filter behind <see cref="DecodeOptions.ScaleDenominator"/> beyond what a codec scales to natively, as a
/// streaming row accumulator: push the source rows top to bottom (<see cref="PushRow"/>); after every
/// <see cref="Factor"/> rows, and after the last one, an output row is ready to take (<see cref="TakeRow"/>). A decoder
/// can so downscale while it decodes, holding one output row of sums instead of the full-size image.
/// <para>
/// The filter: output pixel (bx, by) averages the source pixels x ∈ [bx·d, min(bx·d + d, width)) of the rows
/// y ∈ [by·d, min(by·d + d, height)), d = <see cref="Factor"/>; boxes at the right and bottom edges are partial, and n is
/// the number of pixels in the clipped box. Without alpha (1 or 3 channels) each channel is <c>(Σc + n/2) / n</c>. With
/// alpha (2 or 4 channels, alpha last), A = Σa; alpha is <c>(A + n/2) / n</c> and each colour channel
/// <c>(Σ(c·a) + A/2) / A</c>, or 0 where A is 0: weighted by alpha, without rounding per pixel. Integer samples are
/// summed exactly, so the result does not depend on the order of the sums; it is computed on the stored code values
/// (no linearisation). <see cref="SampleFormat.Float32"/> sums in <see cref="double"/>, row by row and left to right in
/// each box, and divides without rounding: <c>Σc / n</c>; with alpha <c>A / n</c> and <c>Σ(c·a) / A</c> (0 where A is 0).
/// </para>
/// <para>
/// A window of a larger image: push only columns <c>x0 .. x0 + width − 1</c> of rows <c>y0 .. y0 + height − 1</c>,
/// where x0 and y0 are multiples of the factor and the window ends at a multiple of the factor or at the image's edge
/// (<see cref="GetSourceRect"/> computes it from a region of the scaled image). The output is then exactly that part
/// of the whole image's downscale.
/// </para>
/// </summary>
public sealed class BoxDownscaler
{
    internal const int Scalar = 0;
    internal const int Vector128Tier = 1;
    internal const int Vector256Tier = 2;

    /// <summary>The fastest tier on this machine (the vector loops assume little-endian lane order).</summary>
    internal static int BestTier =>
        !BitConverter.IsLittleEndian ? Scalar
        : Vector256.IsHardwareAccelerated ? Vector256Tier
        : Vector128.IsHardwareAccelerated ? Vector128Tier
        : Scalar;

    // The largest box, in pixels. It keeps every sum inside 64 bits: Σ(c·a) ≤ 65535² · 2^32 = 2^64 − 2^49 + 2^32, and
    // adding the rounding term A/2 ≤ 65535 · 2^31 < 2^47 still stays below 2^64.
    private const long MaxBoxPixels = 1L << 32;

    // The column sums of the 8-bit path are 32-bit; it is taken while a box has at most this many rows:
    // 255 · 255 · 65536 = 4 261 478 400 < 2^32 bounds a column of products c·a, and 255 · 65536 a column of samples.
    private const int MaxRowsForColumnSums32 = 65536;

    private readonly int _tier;
    private readonly int _samples;          // samples per source row: Width × Channels
    private readonly int _boxRows;          // rows of a full box: min(Factor, Height)
    private readonly bool _alpha;
    private readonly uint[]? _columns32;    // 8-bit path: per source sample, the sum over the box's rows so far
    private readonly ulong[]? _boxes64;     // other integer paths: per output sample, the box's sum so far
    private readonly double[]? _boxesF;     // Float32: per output sample, the box's sum so far
    private int _rowsInBox;
    private bool _ready;

    /// <summary>
    /// Creates an accumulator for <paramref name="height"/> source rows of <paramref name="width"/> pixels each.
    /// </summary>
    /// <param name="width">Pixels per pushed row (&gt; 0): the image width, or a column window's.</param>
    /// <param name="height">Rows to push (&gt; 0).</param>
    /// <param name="channels">Samples per pixel, 1..4; 2 and 4 carry alpha last.</param>
    /// <param name="sampleFormat">The sample type, in host byte order as in <see cref="IDecodedImage.Pixels"/>.</param>
    /// <param name="factor">The box side d (&gt; 0); <see cref="DecodeOptions.ScaleDenominator"/> is a power of two.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range, a source row holds more than
    /// <see cref="Array.MaxLength"/> bytes, or a box would hold more than 2^32 pixels.</exception>
    public BoxDownscaler(int width, int height, int channels, SampleFormat sampleFormat, int factor)
        : this(width, height, channels, sampleFormat, factor, BestTier)
    {
    }

    /// <summary>The same, capped at <paramref name="tier"/> (<see cref="Scalar"/>, <see cref="Vector128Tier"/>,
    /// <see cref="Vector256Tier"/>), so the tests can hold each tier against the scalar one.</summary>
    internal BoxDownscaler(int width, int height, int channels, SampleFormat sampleFormat, int factor, int tier)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        if (channels is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channels must be 1..4.");
        if (sampleFormat is not (SampleFormat.UInt8 or SampleFormat.UInt16 or SampleFormat.Float32))
            throw new ArgumentOutOfRangeException(nameof(sampleFormat), sampleFormat, "Not a defined sample format.");
        if ((long)width * channels * RasterImage.BytesPerSample(sampleFormat) > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(width), width, "A source row holds more than Array.MaxLength bytes.");
        if ((long)Math.Min(factor, width) * Math.Min(factor, height) > MaxBoxPixels)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "A box would hold more than 2^32 pixels.");

        Width = width;
        Height = height;
        Channels = channels;
        SampleFormat = sampleFormat;
        Factor = factor;
        OutputWidth = (int)(((long)width + factor - 1) / factor);
        OutputHeight = (int)(((long)height + factor - 1) / factor);
        var bytesPerSample = RasterImage.BytesPerSample(sampleFormat);
        SourceRowBytes = width * channels * bytesPerSample; // ≤ Array.MaxLength, checked above
        OutputRowBytes = OutputWidth * channels * bytesPerSample; // ≤ SourceRowBytes
        _tier = tier;
        _samples = width * channels;
        _boxRows = Math.Min(factor, height);
        _alpha = channels is 2 or 4;
        if (sampleFormat == SampleFormat.Float32)
            _boxesF = new double[OutputWidth * channels];
        else if (sampleFormat == SampleFormat.UInt8 && _boxRows <= MaxRowsForColumnSums32)
            _columns32 = new uint[_samples];
        else
            _boxes64 = new ulong[OutputWidth * channels];
    }

    /// <summary>Pixels per pushed row.</summary>
    public int Width { get; }

    /// <summary>Rows to push.</summary>
    public int Height { get; }

    /// <summary>Samples per pixel; 2 and 4 carry alpha last.</summary>
    public int Channels { get; }

    /// <summary>The sample type.</summary>
    public SampleFormat SampleFormat { get; }

    /// <summary>The box side d.</summary>
    public int Factor { get; }

    /// <summary>Pixels per output row: <c>⌈Width / Factor⌉</c>.</summary>
    public int OutputWidth { get; }

    /// <summary>Output rows: <c>⌈Height / Factor⌉</c>.</summary>
    public int OutputHeight { get; }

    /// <summary>Bytes of a source row that <see cref="PushRow"/> reads: <c>Width × Channels × bytesPerSample</c>.</summary>
    public int SourceRowBytes { get; }

    /// <summary>Bytes of an output row that <see cref="TakeRow"/> writes: <c>OutputWidth × Channels × bytesPerSample</c>.</summary>
    public int OutputRowBytes { get; }

    /// <summary>Source rows pushed so far.</summary>
    public int RowsPushed { get; private set; }

    /// <summary>Output rows taken so far; the next <see cref="TakeRow"/> writes output row <c>RowsTaken</c>.</summary>
    public int RowsTaken { get; private set; }

    /// <summary>True once every output row has been taken.</summary>
    public bool IsComplete => RowsTaken == OutputHeight;

    /// <summary>
    /// Adds the next source row. Returns true when it completes an output row (every <see cref="Factor"/> rows, and at the
    /// last row): take it with <see cref="TakeRow"/> before pushing again.
    /// </summary>
    /// <param name="row">The row's samples; only the first <see cref="SourceRowBytes"/> bytes are read.</param>
    /// <exception cref="ArgumentException"><paramref name="row"/> is shorter than <see cref="SourceRowBytes"/>.</exception>
    /// <exception cref="InvalidOperationException">All <see cref="Height"/> rows have been pushed, or an output row is
    /// waiting to be taken.</exception>
    public bool PushRow(ReadOnlySpan<byte> row)
    {
        if (_ready)
            throw new InvalidOperationException("An output row is waiting: call TakeRow before pushing the next source row.");
        if (RowsPushed == Height)
            throw new InvalidOperationException("Every source row has been pushed.");
        if (row.Length < SourceRowBytes)
            throw new ArgumentException($"The row holds {row.Length} bytes, fewer than the {SourceRowBytes} of a source row.", nameof(row));
        row = row[..SourceRowBytes];

        if (_columns32 is not null)
            AccumulateColumns8(row, _columns32, Channels, _tier);
        else if (_boxesF is not null)
            AccumulateFloat(MemoryMarshal.Cast<byte, float>(row));
        else if (SampleFormat == SampleFormat.UInt8)
            AccumulateBoxes(row);
        else
            AccumulateBoxes(MemoryMarshal.Cast<byte, ushort>(row));

        RowsPushed++;
        _rowsInBox++;
        _ready = _rowsInBox == _boxRows || RowsPushed == Height;
        return _ready;
    }

    /// <summary>
    /// Writes the output row the last <see cref="PushRow"/> completed into <paramref name="destination"/> (its first
    /// <see cref="OutputRowBytes"/> bytes; the rest is untouched) and starts the next one.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="OutputRowBytes"/>.</exception>
    /// <exception cref="InvalidOperationException">No output row is ready.</exception>
    public void TakeRow(Span<byte> destination)
    {
        if (!_ready)
            throw new InvalidOperationException("No output row is ready: PushRow has not completed one.");
        if (destination.Length < OutputRowBytes)
            throw new ArgumentException($"The destination holds {destination.Length} bytes, fewer than the {OutputRowBytes} of an output row.", nameof(destination));
        destination = destination[..OutputRowBytes];

        if (_columns32 is not null)
            EmitColumns8(destination);
        else if (_boxesF is not null)
            EmitFloat(MemoryMarshal.Cast<byte, float>(destination));
        else if (SampleFormat == SampleFormat.UInt8)
            EmitBoxes(destination);
        else
            EmitBoxes(MemoryMarshal.Cast<byte, ushort>(destination));

        RowsTaken++;
        _rowsInBox = 0;
        _ready = false;
    }

    /// <summary>
    /// The source pixels the region <paramref name="scaledRegion"/> of the image scaled by <paramref name="factor"/>
    /// averages: columns <c>X·d .. min((X + Width)·d, width) − 1</c>, rows likewise. Pushing that window gives exactly
    /// the region.
    /// </summary>
    /// <param name="scaledRegion">A region of the <c>⌈width / d⌉ × ⌈height / d⌉</c> scaled image.</param>
    /// <param name="width">The source width.</param>
    /// <param name="height">The source height.</param>
    /// <param name="factor">The box side d (&gt; 0).</param>
    /// <exception cref="ArgumentException"><paramref name="scaledRegion"/> does not lie inside the scaled image.</exception>
    public static PixelRect GetSourceRect(PixelRect scaledRegion, int width, int height, int factor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var scaledWidth = (int)(((long)width + factor - 1) / factor);
        var scaledHeight = (int)(((long)height + factor - 1) / factor);
        if (!scaledRegion.FitsIn(scaledWidth, scaledHeight))
            throw new ArgumentException($"The region {scaledRegion} does not lie inside the {scaledWidth}x{scaledHeight} image.", nameof(scaledRegion));
        // X < ⌈width / d⌉ gives X·d < width, so x0 fits an int; x1 ≤ width.
        var x0 = (int)((long)scaledRegion.X * factor);
        var y0 = (int)((long)scaledRegion.Y * factor);
        var x1 = (int)Math.Min(((long)scaledRegion.X + scaledRegion.Width) * factor, width);
        var y1 = (int)Math.Min(((long)scaledRegion.Y + scaledRegion.Height) * factor, height);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Downscales a whole image by <paramref name="factor"/> with the box filter, or only the part of it that
    /// <paramref name="scaledRegion"/> (in the scaled image's coordinates) covers, into a new image (rented from
    /// <paramref name="pool"/> when it is not null) with the source's channels, sample format, bit depth, colour
    /// signalling and metadata. The source is not disposed.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scaledRegion"/> does not lie inside the scaled image.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not positive.</exception>
    public static RasterImage Downscale(IDecodedImage source, int factor, PixelRect? scaledRegion = null, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var region = scaledRegion ?? new PixelRect(0, 0,
            (int)(((long)source.Width + factor - 1) / factor), (int)(((long)source.Height + factor - 1) / factor));
        return Downscale(source, factor, region, pool, BestTier);
    }

    internal static RasterImage Downscale(IDecodedImage source, int factor, PixelRect scaledRegion, ArrayPool<byte>? pool, int tier)
    {
        var rect = GetSourceRect(scaledRegion, source.Width, source.Height, factor);
        var box = new BoxDownscaler(rect.Width, rect.Height, source.Channels, source.SampleFormat, factor, tier);
        var image = RasterImage.Create(scaledRegion.Width, scaledRegion.Height, source.Channels, source.SampleFormat, pool,
            source.ColorEncoding, source.BitDepth == RasterImage.BytesPerSample(source.SampleFormat) * 8 ? 0 : source.BitDepth,
            source.Metadata);
        try
        {
            var bpp = source.Channels * RasterImage.BytesPerSample(source.SampleFormat);
            var inRow = (long)source.Width * bpp;
            var pixels = source.Pixels;
            var output = image.WritablePixels;
            for (var y = 0; y < rect.Height; y++)
            {
                if (box.PushRow(pixels.Slice((int)((rect.Y + y) * inRow + (long)rect.X * bpp), box.SourceRowBytes)))
                    box.TakeRow(output.Slice(box.RowsTaken * box.OutputRowBytes, box.OutputRowBytes));
            }
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    // ---- 8-bit path: 32-bit column sums --------------------------------------------------------------------------

    /// <summary>
    /// Adds a row of 8-bit samples to the column sums: <c>columns[i] += row[i]</c>, except that a colour sample of a
    /// pixel with alpha (2 or 4 channels) adds <c>c·a</c>. A 256-bit loop, then a 128-bit one, then the scalar twin for
    /// the rest (all of it at <see cref="Scalar"/>); integer sums, so every tier gives the same sums.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void AccumulateColumns8(ReadOnlySpan<byte> row, Span<uint> columns, int channels, int tier)
    {
        if (columns.Length < row.Length)
            throw new ArgumentException("The column sums are shorter than the row.", nameof(columns));
        var n = row.Length;
        ref var s = ref MemoryMarshal.GetReference(row);
        ref var c = ref MemoryMarshal.GetReference(columns);
        var i = 0;

        // In range: a 256-bit step at i reads row[i, i + 32) and reads and writes columns[i, i + 32), with i + 32 ≤ n ≤
        // columns.Length; a 128-bit step likewise with 16. Each step starts at a multiple of 16, which is a pixel
        // boundary for 2 and 4 channels, so the alpha of every pixel in the step is in the step.
        // Arithmetic: a product c·a ≤ 255² = 65025 fits a 16-bit lane; the column sum is bounded by the caller (at most
        // MaxRowsForColumnSums32 rows, ≤ 65025 · 65536 < 2^32), and uint addition is exact below that.
        if (tier >= Vector256Tier && Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            var (mask, one) = WeightConstants256(channels);
            for (; i + 32 <= n; i += 32)
            {
                var (lo, hi) = Vector256.Widen(Vector256.LoadUnsafe(ref s, (nuint)i));
                if (channels is 2 or 4)
                {
                    lo *= Weights256(lo, channels, mask, one);
                    hi *= Weights256(hi, channels, mask, one);
                }
                var (a, b) = Vector256.Widen(lo);
                var (e, f) = Vector256.Widen(hi);
                (Vector256.LoadUnsafe(ref c, (nuint)i) + a).StoreUnsafe(ref c, (nuint)i);
                (Vector256.LoadUnsafe(ref c, (nuint)(i + 8)) + b).StoreUnsafe(ref c, (nuint)(i + 8));
                (Vector256.LoadUnsafe(ref c, (nuint)(i + 16)) + e).StoreUnsafe(ref c, (nuint)(i + 16));
                (Vector256.LoadUnsafe(ref c, (nuint)(i + 24)) + f).StoreUnsafe(ref c, (nuint)(i + 24));
            }
        }
        if (tier >= Vector128Tier && Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            var (mask, one) = WeightConstants128(channels);
            for (; i + 16 <= n; i += 16)
            {
                var (lo, hi) = Vector128.Widen(Vector128.LoadUnsafe(ref s, (nuint)i));
                if (channels is 2 or 4)
                {
                    lo *= Weights128(lo, channels, mask, one);
                    hi *= Weights128(hi, channels, mask, one);
                }
                var (a, b) = Vector128.Widen(lo);
                var (e, f) = Vector128.Widen(hi);
                (Vector128.LoadUnsafe(ref c, (nuint)i) + a).StoreUnsafe(ref c, (nuint)i);
                (Vector128.LoadUnsafe(ref c, (nuint)(i + 4)) + b).StoreUnsafe(ref c, (nuint)(i + 4));
                (Vector128.LoadUnsafe(ref c, (nuint)(i + 8)) + e).StoreUnsafe(ref c, (nuint)(i + 8));
                (Vector128.LoadUnsafe(ref c, (nuint)(i + 12)) + f).StoreUnsafe(ref c, (nuint)(i + 12));
            }
        }
        AccumulateColumns8Scalar(row, columns, channels, i);
    }

    /// <summary>The scalar twin of <see cref="AccumulateColumns8"/>, from sample <paramref name="from"/> (a pixel boundary).</summary>
    private static void AccumulateColumns8Scalar(ReadOnlySpan<byte> row, Span<uint> columns, int channels, int from)
    {
        columns = columns[..row.Length];
        if (channels is 1 or 3)
        {
            for (var i = from; i < row.Length; i++)
                columns[i] += row[i];
            return;
        }
        for (var i = from; i < row.Length; i += channels)
        {
            uint a = row[i + channels - 1];
            for (var k = 0; k < channels - 1; k++)
                columns[i + k] += row[i + k] * a;
            columns[i + channels - 1] += a;
        }
    }

    // The per-lane multipliers of a widened row: each colour lane gets its pixel's alpha, each alpha lane 1. With lanes
    // read as 64-bit (4 channels: one pixel per 64 bits) or 32-bit (2 channels) words, alpha is the top 16 bits:
    // shifting it down and copying it up fills the word with it, and the mask puts 1 in the alpha lane.
    private static (Vector128<ulong> Mask, Vector128<ulong> One) WeightConstants128(int channels) => channels == 4
        ? (Vector128.Create(0x0000_FFFF_FFFF_FFFFUL), Vector128.Create(0x0001_0000_0000_0000UL))
        : (Vector128.Create(0x0000_FFFF_0000_FFFFUL), Vector128.Create(0x0001_0000_0001_0000UL));

    private static (Vector256<ulong> Mask, Vector256<ulong> One) WeightConstants256(int channels) => channels == 4
        ? (Vector256.Create(0x0000_FFFF_FFFF_FFFFUL), Vector256.Create(0x0001_0000_0000_0000UL))
        : (Vector256.Create(0x0000_FFFF_0000_FFFFUL), Vector256.Create(0x0001_0000_0001_0000UL));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ushort> Weights128(Vector128<ushort> v, int channels, Vector128<ulong> mask, Vector128<ulong> one)
    {
        Vector128<ulong> t;
        if (channels == 4)
        {
            t = v.AsUInt64() >>> 48;
            t |= t << 16;
            t |= t << 32;
        }
        else
        {
            var u = v.AsUInt32() >>> 16;
            t = (u | (u << 16)).AsUInt64();
        }
        return ((t & mask) | one).AsUInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ushort> Weights256(Vector256<ushort> v, int channels, Vector256<ulong> mask, Vector256<ulong> one)
    {
        Vector256<ulong> t;
        if (channels == 4)
        {
            t = v.AsUInt64() >>> 48;
            t |= t << 16;
            t |= t << 32;
        }
        else
        {
            var u = v.AsUInt32() >>> 16;
            t = (u | (u << 16)).AsUInt64();
        }
        return ((t & mask) | one).AsUInt16();
    }

    private void EmitColumns8(Span<byte> destination)
    {
        var columns = _columns32!.AsSpan();
        var ch = Channels;
        var rows = (ulong)_rowsInBox;
        Span<ulong> sums = stackalloc ulong[4];
        for (var bx = 0; bx < OutputWidth; bx++)
        {
            // Box columns x0 .. x1 − 1: x0 = bx·d < Width, so it fits an int.
            var x0 = (int)((long)bx * Factor);
            var x1 = (int)Math.Min((long)x0 + Factor, Width);
            sums.Clear();
            for (var i = x0 * ch; i < x1 * ch; i += ch)
            {
                for (var k = 0; k < ch; k++)
                    sums[k] += columns[i + k];
            }
            var n = (ulong)(x1 - x0) * rows;
            Finish(sums, ch, n, destination.Slice(bx * ch, ch));
        }
        columns.Clear();
    }

    // ---- Other integer paths: 64-bit box sums ---------------------------------------------------------------------

    private void AccumulateBoxes(ReadOnlySpan<byte> row)
    {
        var boxes = _boxes64!.AsSpan();
        var ch = Channels;
        for (var bx = 0; bx < OutputWidth; bx++)
        {
            var x0 = (int)((long)bx * Factor);
            var x1 = (int)Math.Min((long)x0 + Factor, Width);
            var box = boxes.Slice(bx * ch, ch);
            for (var i = x0 * ch; i < x1 * ch; i += ch)
            {
                if (_alpha)
                {
                    ulong a = row[i + ch - 1];
                    for (var k = 0; k < ch - 1; k++)
                        box[k] += row[i + k] * a;
                    box[ch - 1] += a;
                }
                else
                {
                    for (var k = 0; k < ch; k++)
                        box[k] += row[i + k];
                }
            }
        }
    }

    private void AccumulateBoxes(ReadOnlySpan<ushort> row)
    {
        var boxes = _boxes64!.AsSpan();
        var ch = Channels;
        for (var bx = 0; bx < OutputWidth; bx++)
        {
            var x0 = (int)((long)bx * Factor);
            var x1 = (int)Math.Min((long)x0 + Factor, Width);
            var box = boxes.Slice(bx * ch, ch);
            for (var i = x0 * ch; i < x1 * ch; i += ch)
            {
                if (_alpha)
                {
                    // c·a ≤ 65535² < 2^32, and a box sums at most 2^32 of them (MaxBoxPixels).
                    ulong a = row[i + ch - 1];
                    for (var k = 0; k < ch - 1; k++)
                        box[k] += row[i + k] * a;
                    box[ch - 1] += a;
                }
                else
                {
                    for (var k = 0; k < ch; k++)
                        box[k] += row[i + k];
                }
            }
        }
    }

    private void EmitBoxes(Span<byte> destination)
    {
        var boxes = _boxes64!.AsSpan();
        var ch = Channels;
        for (var bx = 0; bx < OutputWidth; bx++)
        {
            var x0 = (long)bx * Factor;
            var n = (ulong)(Math.Min(x0 + Factor, Width) - x0) * (ulong)_rowsInBox;
            Finish(boxes.Slice(bx * ch, ch), ch, n, destination.Slice(bx * ch, ch));
        }
        boxes.Clear();
    }

    private void EmitBoxes(Span<ushort> destination)
    {
        var boxes = _boxes64!.AsSpan();
        var ch = Channels;
        for (var bx = 0; bx < OutputWidth; bx++)
        {
            var x0 = (long)bx * Factor;
            var n = (ulong)(Math.Min(x0 + Factor, Width) - x0) * (ulong)_rowsInBox;
            Finish(boxes.Slice(bx * ch, ch), ch, n, destination.Slice(bx * ch, ch));
        }
        boxes.Clear();
    }

    // The rounded averages of one box from its sums (n pixels). Without alpha (Σc + n/2) / n; with alpha (last sum A = Σa)
    // (A + n/2) / n, and (Σ(c·a) + A/2) / A for each colour, 0 when A is 0. No sum overflows (MaxBoxPixels), and each
    // quotient is at most the sample maximum, since Σc ≤ max·n and Σ(c·a) ≤ max·A.
    private static void Finish(ReadOnlySpan<ulong> sums, int channels, ulong n, Span<byte> output)
    {
        if (channels is 1 or 3)
        {
            for (var k = 0; k < channels; k++)
                output[k] = (byte)((sums[k] + n / 2) / n);
            return;
        }
        var a = sums[channels - 1];
        output[channels - 1] = (byte)((a + n / 2) / n);
        for (var k = 0; k < channels - 1; k++)
            output[k] = a == 0 ? (byte)0 : (byte)((sums[k] + a / 2) / a);
    }

    private static void Finish(ReadOnlySpan<ulong> sums, int channels, ulong n, Span<ushort> output)
    {
        if (channels is 1 or 3)
        {
            for (var k = 0; k < channels; k++)
                output[k] = (ushort)((sums[k] + n / 2) / n);
            return;
        }
        var a = sums[channels - 1];
        output[channels - 1] = (ushort)((a + n / 2) / n);
        for (var k = 0; k < channels - 1; k++)
            output[k] = a == 0 ? (ushort)0 : (ushort)((sums[k] + a / 2) / a);
    }

    // ---- Float32: double box sums, row-major -----------------------------------------------------------------------

    private void AccumulateFloat(ReadOnlySpan<float> row)
    {
        var boxes = _boxesF!.AsSpan();
        var ch = Channels;
        for (var bx = 0; bx < OutputWidth; bx++)
        {
            var x0 = (int)((long)bx * Factor);
            var x1 = (int)Math.Min((long)x0 + Factor, Width);
            var box = boxes.Slice(bx * ch, ch);
            for (var i = x0 * ch; i < x1 * ch; i += ch)
            {
                if (_alpha)
                {
                    double a = row[i + ch - 1];
                    for (var k = 0; k < ch - 1; k++)
                        box[k] += row[i + k] * a;
                    box[ch - 1] += a;
                }
                else
                {
                    for (var k = 0; k < ch; k++)
                        box[k] += row[i + k];
                }
            }
        }
    }

    private void EmitFloat(Span<float> destination)
    {
        var boxes = _boxesF!.AsSpan();
        var ch = Channels;
        for (var bx = 0; bx < OutputWidth; bx++)
        {
            var x0 = (long)bx * Factor;
            var n = (double)((Math.Min(x0 + Factor, Width) - x0) * _rowsInBox);
            var sums = boxes.Slice(bx * ch, ch);
            var output = destination.Slice(bx * ch, ch);
            if (!_alpha)
            {
                for (var k = 0; k < ch; k++)
                    output[k] = (float)(sums[k] / n);
                continue;
            }
            var a = sums[ch - 1];
            output[ch - 1] = (float)(a / n);
            for (var k = 0; k < ch - 1; k++)
                output[k] = a == 0 ? 0f : (float)(sums[k] / a);
        }
        boxes.Clear();
    }
}
