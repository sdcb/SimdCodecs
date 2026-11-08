using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Webp;

public static partial class WebpDecoder
{
    /// <summary>
    /// Decodes <paramref name="scaledRegion"/> of a lossy still scaled by <paramref name="factor"/> (the
    /// <see cref="BoxDownscaler"/> filter, applied while decoding) and writes it to <paramref name="destination"/> in
    /// <paramref name="destinationFormat"/>, with exactly the pixels a whole decode, box-filtered, cropped and converted
    /// from the decoder's layout (RGB, or RGBA with <paramref name="channels"/> 4), would give.
    /// <para>
    /// Only the source rows the region's boxes cover are converted, a column window of each; macroblock rows below the
    /// last of them are not decoded at all, so damage there goes unnoticed. The ALPH plane, when there is one, is still
    /// decoded whole.
    /// </para>
    /// Why the window is exact: chroma is upsampled across the full width (fancy upsampling reads neighbouring chroma
    /// columns), and YUV → RGBA is a function of each pixel's own Y / U / V / A alone on every tier, so converting
    /// columns [x0, x0 + w) of the upsampled row writes exactly those columns of the full conversion.
    /// </summary>
    internal static void DecodeLossyRegion(
        ReadOnlySpan<byte> bitstream,
        ReadOnlySpan<byte> alphaData,
        bool hasAlphaChunk,
        int width,
        int height,
        int channels,
        int factor,
        PixelRect scaledRegion,
        Span<byte> destination,
        int destinationStride,
        PixelFormat destinationFormat,
        bool fancy,
        CancellationToken cancellationToken = default)
    {
        Debug.Assert(channels is 3 or 4);
        PixelRect source = factor == 1 ? scaledRegion : BoxDownscaler.GetSourceRect(scaledRegion, width, height, factor);
        if (factor == 1 && !scaledRegion.FitsIn(width, height))
        {
            throw new ArgumentException($"The region {scaledRegion} does not lie inside the {width}x{height} image.", nameof(scaledRegion));
        }

        PixelFormat layout = channels == 4 ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        int outputRowBytes = checked(scaledRegion.Width * destinationFormat.BytesPerPixel);
        if (destinationStride < outputRowBytes ||
            destination.Length < (long)destinationStride * (scaledRegion.Height - 1) + outputRowBytes)
        {
            throw new ArgumentException("The destination is too small for the region.", nameof(destination));
        }

        BoxDownscaler? box = factor == 1 ? null : new BoxDownscaler(source.Width, source.Height, channels, SampleFormat.UInt8, factor);
        byte[]? alpha = hasAlphaChunk ? DecodeAndUnfilterAlpha(alphaData, width, height) : null;
        int chromaWidth = (width + 1) >> 1;
        ushort[]? tRows = null;
        byte[]? chromaRows = null;
        byte[]? rgbaRow = null;
        byte[]? layoutRow = null;
        byte[]? boxRow = null;
        try
        {
            tRows = WebpBufferPool<ushort>.Rent(checked(chromaWidth * 2));
            chromaRows = WebpBufferPool<byte>.Rent(checked(width * 2));
            rgbaRow = WebpBufferPool<byte>.Rent(checked(source.Width * 4));
            layoutRow = channels == 3 ? WebpBufferPool<byte>.Rent(checked(source.Width * 3)) : null;
            boxRow = box is null ? null : WebpBufferPool<byte>.Rent(box.OutputRowBytes);
            ReadOnlySpan<byte> alphaSpan = alpha is null ? ReadOnlySpan<byte>.Empty : alpha.AsSpan(0, checked(width * height));
            var sink = new RegionSink(width, height, fancy, alphaSpan, tRows, chromaRows, source, layout, rgbaRow, layoutRow,
                box, boxRow, destination, destinationStride, destinationFormat, scaledRegion.Width);
            Vp8Decoder.DecodeFrame(bitstream, width, height, WebpAblation.None, ref sink, cancellationToken,
                rowLimit: source.Y + source.Height);
            if (sink.RowsWritten != scaledRegion.Height)
            {
                throw new InvalidDataException("The VP8 frame ended before the region.");
            }
        }
        finally
        {
            if (alpha is not null) WebpBufferPool<byte>.Return(alpha);
            if (tRows is not null) WebpBufferPool<ushort>.Return(tRows);
            if (chromaRows is not null) WebpBufferPool<byte>.Return(chromaRows);
            if (rgbaRow is not null) WebpBufferPool<byte>.Return(rgbaRow);
            if (layoutRow is not null) WebpBufferPool<byte>.Return(layoutRow);
            if (boxRow is not null) WebpBufferPool<byte>.Return(boxRow);
        }
    }

    /// <summary>
    /// Upsamples chroma (fancy or nearest) of the rows of <c>source</c> exactly as <see cref="RgbaSink"/> does,
    /// converts the window's columns to RGBA, puts each row into the decoder's layout, box-filters it when there is a
    /// box, and converts each finished output row into the destination.
    /// </summary>
    private ref struct RegionSink : IVp8RowSink
    {
        private readonly int _width;
        private readonly int _height;
        private readonly bool _fancy;
        private readonly ReadOnlySpan<byte> _alpha;
        private readonly ushort[] _tRows;
        private readonly byte[] _chromaRows;
        private readonly PixelRect _source;
        private readonly PixelFormat _layout;
        private readonly byte[] _rgbaRow;
        private readonly byte[]? _layoutRow;
        private readonly BoxDownscaler? _box;
        private readonly byte[]? _boxRow;
        private readonly Span<byte> _destination;
        private readonly int _destinationStride;
        private readonly PixelFormat _destinationFormat;
        private readonly int _outputWidth;

        public RegionSink(int width, int height, bool fancy, ReadOnlySpan<byte> alpha, ushort[] tRows, byte[] chromaRows,
            PixelRect source, PixelFormat layout, byte[] rgbaRow, byte[]? layoutRow, BoxDownscaler? box, byte[]? boxRow,
            Span<byte> destination, int destinationStride, PixelFormat destinationFormat, int outputWidth)
        {
            _width = width;
            _height = height;
            _fancy = fancy;
            _alpha = alpha;
            _tRows = tRows;
            _chromaRows = chromaRows;
            _source = source;
            _layout = layout;
            _rgbaRow = rgbaRow;
            _layoutRow = layoutRow;
            _box = box;
            _boxRow = boxRow;
            _destination = destination;
            _destinationStride = destinationStride;
            _destinationFormat = destinationFormat;
            _outputWidth = outputWidth;
        }

        /// <summary>Output rows written to the destination so far.</summary>
        public int RowsWritten { get; private set; }

        public void Emit(ref byte y, int yStride, int yRow0, ref byte u, ref byte v, int uvStride, int uvRow0,
            int first, int end)
        {
            int from = Math.Max(first, _source.Y);
            int to = Math.Min(end, _source.Y + _source.Height);
            if (from >= to)
            {
                return;
            }

            int width = _width;
            int chromaWidth = (width + 1) >> 1;
            int chromaHeight = (_height + 1) >> 1;
            int x0 = _source.X;
            int windowWidth = _source.Width;
            bool hasAlpha = !_alpha.IsEmpty;
            // x0 + windowWidth ≤ width (the window lies inside the image), and the scratch rows hold width bytes per
            // plane, the alpha plane width per row and rgbaRow windowWidth × 4: every column the converter touches,
            // x0 + [0, windowWidth), is inside the luma cache row, both upsampled rows, the alpha row and rgbaRow.
            ref byte uScratch = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_chromaRows), x0);
            ref byte vScratch = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_chromaRows), width + x0);
            ref byte rgba = ref MemoryMarshal.GetArrayDataReference(_rgbaRow);
            for (int row = from; row < to; row++)
            {
                int chromaRow = row >> 1;
                ref byte uRow = ref Unsafe.Add(ref u, (chromaRow - uvRow0) * uvStride);
                ref byte vRow = ref Unsafe.Add(ref v, (chromaRow - uvRow0) * uvStride);
                ref byte yRow = ref Unsafe.Add(ref y, (row - yRow0) * yStride + x0);
                ref byte alphaRow = ref Unsafe.NullRef<byte>();
                if (hasAlpha)
                {
                    alphaRow = ref Unsafe.Add(ref MemoryMarshal.GetReference(_alpha), row * width + x0);
                }

                if (_fancy)
                {
                    int adjacentRow = row % 2 == 0
                        ? (chromaRow == 0 ? 0 : chromaRow - 1)
                        : (chromaRow == chromaHeight - 1 ? chromaRow : chromaRow + 1);
                    ref byte adjacentU = ref Unsafe.Add(ref u, (adjacentRow - uvRow0) * uvStride);
                    ref byte adjacentV = ref Unsafe.Add(ref v, (adjacentRow - uvRow0) * uvStride);
                    Span<ushort> tU = _tRows.AsSpan(0, chromaWidth);
                    Span<ushort> tV = _tRows.AsSpan(chromaWidth, chromaWidth);
                    Span<byte> uUpsampled = _chromaRows.AsSpan(0, width);
                    Span<byte> vUpsampled = _chromaRows.AsSpan(width, width);
                    ReadOnlySpan<byte> currentU = MemoryMarshal.CreateReadOnlySpan(ref uRow, chromaWidth);
                    ReadOnlySpan<byte> currentV = MemoryMarshal.CreateReadOnlySpan(ref vRow, chromaWidth);
                    ReadOnlySpan<byte> adjacentUSpan = MemoryMarshal.CreateReadOnlySpan(ref adjacentU, chromaWidth);
                    ReadOnlySpan<byte> adjacentVSpan = MemoryMarshal.CreateReadOnlySpan(ref adjacentV, chromaWidth);
                    if (Vector128.IsHardwareAccelerated)
                    {
                        UpsampleFancyVector128(currentU, adjacentUSpan, tU, uUpsampled);
                        UpsampleFancyVector128(currentV, adjacentVSpan, tV, vUpsampled);
                    }
                    else
                    {
                        UpsampleFancyScalar(currentU, adjacentUSpan, tU, uUpsampled);
                        UpsampleFancyScalar(currentV, adjacentVSpan, tV, vUpsampled);
                    }
                }
                else
                {
                    // Nearest: column c takes chroma column c >> 1; only the window's columns are needed.
                    ref byte uOut = ref MemoryMarshal.GetArrayDataReference(_chromaRows);
                    ref byte vOut = ref Unsafe.Add(ref uOut, width);
                    for (int column = x0; column < x0 + windowWidth; column++)
                    {
                        Unsafe.Add(ref uOut, column) = Unsafe.Add(ref uRow, column >> 1);
                        Unsafe.Add(ref vOut, column) = Unsafe.Add(ref vRow, column >> 1);
                    }
                }

                if (Vector256.IsHardwareAccelerated)
                {
                    ConvertRowVector256Core(ref yRow, ref uScratch, ref vScratch, ref alphaRow, hasAlpha, ref rgba, windowWidth);
                }
                else if (Vector128.IsHardwareAccelerated)
                {
                    ConvertRowVector128Core(ref yRow, ref uScratch, ref vScratch, ref alphaRow, hasAlpha, ref rgba, windowWidth);
                }
                else
                {
                    ConvertRowScalarCore(ref yRow, ref uScratch, ref vScratch, ref alphaRow, hasAlpha, ref rgba, windowWidth);
                }

                ReadOnlySpan<byte> layoutRow = _rgbaRow.AsSpan(0, windowWidth * 4);
                if (_layoutRow is not null)
                {
                    // Opaque: the decoder's layout is RGB, as the whole decode stores it.
                    PixelConverter.ConvertRow(layoutRow, PixelFormat.Rgba32, _layoutRow, PixelFormat.Rgb24, windowWidth);
                    layoutRow = _layoutRow.AsSpan(0, windowWidth * 3);
                }

                if (_box is null)
                {
                    WriteOutputRow(layoutRow);
                }
                else if (_box.PushRow(layoutRow))
                {
                    _box.TakeRow(_boxRow!);
                    WriteOutputRow(_boxRow!.AsSpan(0, _box.OutputRowBytes));
                }
            }
        }

        private void WriteOutputRow(ReadOnlySpan<byte> row)
        {
            PixelConverter.ConvertRow(row, _layout,
                _destination.Slice(RowsWritten * _destinationStride, _outputWidth * _destinationFormat.BytesPerPixel),
                _destinationFormat, _outputWidth);
            RowsWritten++;
        }
    }
}
