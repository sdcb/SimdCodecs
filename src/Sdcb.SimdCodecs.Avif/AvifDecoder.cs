using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Avif.Av1;
using Sdcb.SimdCodecs.Avif.Isobmff;

namespace Sdcb.SimdCodecs.Avif;

/// <summary>
/// Decoded canvas planes prior to colour conversion and transformative properties (for plane-exact oracle testing).
/// </summary>
internal sealed class AvifDecodedPlanes
{
    internal required int Width { get; init; }
    internal required int Height { get; init; }
    internal required int BitDepth { get; init; }
    internal required int SubsamplingX { get; init; }
    internal required int SubsamplingY { get; init; }
    internal required bool Monochrome { get; init; }
    internal required ushort[] Y { get; init; }
    internal ushort[]? Cb { get; init; }
    internal ushort[]? Cr { get; init; }
    internal ushort[]? Alpha { get; init; }
    internal int AlphaBitDepth { get; init; }
    internal bool AlphaFullRange { get; init; } = true;
    internal int MatrixCoefficients { get; init; } = 2;
    internal bool FullRange { get; init; }
    internal bool IsPremultiplied { get; init; }
}

/// <summary>
/// Pure-managed AVIF decoder (ISO/IEC 23008-12 / AOM AVIF v1.1.0).
/// </summary>
public static class AvifDecoder
{
    /// <summary>
    /// Reads the container metadata of an AVIF file without decoding any AV1 payload.
    /// </summary>
    public static AvifInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        int frameCount = 1;
        if (AvifSequence.TryParse(data, out var seq) && seq.FrameCount > 0)
            frameCount = seq.FrameCount;

        var container = AvifContainer.Parse(data);
        EnsureCanvasDimensions(data, container);
        var (width, height) = container.DisplaySize();
        return new AvifInfo
        {
            Width = width,
            Height = height,
            BitDepth = container.BitDepth,
            HasAlpha = container.HasAlpha,
            FrameCount = frameCount,
            TileCount = container.TileCount,
            IccProfile = container.IccProfile,
            Orientation = ImageOrientation.Normal,
        };
    }

    /// <summary>
    /// Decodes an AVIF image to a newly allocated RGBA8 <see cref="AvifImage"/>.
    /// </summary>
    public static AvifImage Decode(ReadOnlySpan<byte> data)
    {
        var container = AvifContainer.Parse(data);
        return DecodeContainer(data, container);
    }

    /// <summary>
    /// Decodes an AVIF image into a caller-supplied RGBA8 buffer.
    /// </summary>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        var container = AvifContainer.Parse(data);
        EnsureCanvasDimensions(data, container);
        var (width, height) = container.DisplaySize();
        long needed = (long)width * height * 4;
        if (rgbaDestination.Length < needed)
            throw new ArgumentException($"The destination needs {needed} bytes for a {width}x{height} image.", nameof(rgbaDestination));
        DecodeContainerToRgba8(data, container, rgbaDestination, width, height);
        return (width, height);
    }

    internal static AvifImage DecodeContainer(ReadOnlySpan<byte> data, AvifContainer container)
    {
        EnsureCanvasDimensions(data, container);
        var (width, height) = container.DisplaySize();
        var pixels = new byte[checked((long)width * height * 4)];
        int bitDepth = DecodeContainerToRgba8(data, container, pixels, width, height);
        return new AvifImage(width, height, pixels, bitDepth, container.HasAlpha, container.IccProfile);
    }

    internal static int DecodeContainerToRgba8(ReadOnlySpan<byte> data, AvifContainer container, Span<byte> destination, int outW, int outH, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (container.Overlay is not null)
        {
            RenderOverlayRgba8(data, container, destination, outW, outH, cancellationToken);
            return container.BitDepth;
        }
        if (container.Transforms.Count == 0)
        {
            int directBd = TryDecodeDirectToRgba8(data, container, destination, outW, outH);
            if (directBd > 0)
                return directBd;
        }
        var planes = DecodePlanes(data, container);
        RenderRgba(container, planes, destination, outW, outH);
        return planes.BitDepth;
    }

    private static unsafe int TryDecodeDirectToRgba8(ReadOnlySpan<byte> data, AvifContainer container, Span<byte> destination, int outW, int outH)
    {
        var primary = container.Primary;
        var alphaPlan = container.Alpha;
        if (primary.Tiles.Count == 0)
            return 0;

        // Case 1: single primary tile + optional single alpha tile (no ComposePlane copy!)
        if (primary.Tiles.Count == 1 && (alphaPlan is null || alphaPlan.Tiles.Count == 1))
        {
            Av1Picture? primPic = null;
            Av1Picture? alphaPic = null;
            try
            {
                if (alphaPlan is null)
                {
                    primPic = DecodeSingleItem(data, container.Items, primary, 0);
                }
                else
                {
                    byte[] primPayload = ResolveItemBytes(data, container.Items, primary.Tiles[0]);
                    byte[] primCfg = primary.TileConfigs[0].ConfigObusLength > 0
                        ? data.Slice(primary.TileConfigs[0].ConfigObusOffset, primary.TileConfigs[0].ConfigObusLength).ToArray() : [];
                    byte[] alphaPayload = ResolveItemBytes(data, container.Items, alphaPlan.Tiles[0]);
                    byte[] alphaCfg = alphaPlan.TileConfigs[0].ConfigObusLength > 0
                        ? data.Slice(alphaPlan.TileConfigs[0].ConfigObusOffset, alphaPlan.TileConfigs[0].ConfigObusLength).ToArray() : [];

                    Av1Picture? p0 = null, a0 = null;
                    RunParallel(() => Parallel.Invoke(
                        AvifDiagnostics.ParallelOptions,
                        () => p0 = Av1FrameDecoder.DecodeStill(primPayload, primCfg),
                        () => a0 = Av1FrameDecoder.DecodeStill(alphaPayload, alphaCfg)));
                    primPic = p0!;
                    alphaPic = a0!;
                }

                if (primary.CanvasWidth == 0 || primary.CanvasHeight == 0)
                {
                    primary.CanvasWidth = primPic.Width;
                    primary.CanvasHeight = primPic.Height;
                }
                int canvasW = primary.CanvasWidth;
                int canvasH = primary.CanvasHeight;
                if (canvasW != outW || canvasH != outH || primPic.Width < canvasW || primPic.Height < canvasH)
                    throw new InvalidDataException("AVIF decoded dimensions do not match canvas.");

                if (alphaPlan is not null && alphaPic is not null)
                {
                    if (alphaPlan.CanvasWidth == 0 || alphaPlan.CanvasHeight == 0)
                    {
                        alphaPlan.CanvasWidth = alphaPic.Width;
                        alphaPlan.CanvasHeight = alphaPic.Height;
                    }
                    if (alphaPlan.CanvasWidth != canvasW || alphaPlan.CanvasHeight != canvasH ||
                        alphaPic.Width < canvasW || alphaPic.Height < canvasH)
                        throw new InvalidDataException($"AVIF alpha canvas {alphaPlan.CanvasWidth}x{alphaPlan.CanvasHeight} does not match primary {canvasW}x{canvasH}.");
                }

                int matrix = container.NclxColour is { } nclx ? nclx.MatrixCoefficients : primPic.MatrixCoefficients;
                bool fullRange = container.NclxColour is { } nclx2 ? nclx2.FullRange : primPic.FullRange;
                if ((AvifDiagnostics.Ablation & AvifAblation.SkipColor) == 0)
                {
                    RenderPictureRectToRgba8(
                        primPic, alphaPic, container.IsPremultiplied, matrix, fullRange,
                        destination, outW, dstX0: 0, dstY0: 0, width: outW, height: outH, parallelRows: outH >= 64);
                }
                return primPic.BitDepth;
            }
            finally
            {
                primPic?.Dispose();
                alphaPic?.Dispose();
            }
        }

        // Case 2: grid primary without alpha -> decode & render each tile directly into destination in parallel, freeing tile planes immediately!
        if (alphaPlan is null && primary.Tiles.Count > 1 && primary.CanvasWidth == outW && primary.CanvasHeight == outH)
        {
            int count = primary.Tiles.Count;
            var payloads = new byte[count][];
            var configObus = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                payloads[i] = ResolveItemBytes(data, container.Items, primary.Tiles[i]);
                var cfg = primary.TileConfigs[i];
                configObus[i] = cfg.ConfigObusLength > 0
                    ? data.Slice(cfg.ConfigObusOffset, cfg.ConfigObusLength).ToArray()
                    : [];
            }

            // Decode tile 0 first to lock in validated tile dimensions & color metadata before streaming all remaining tiles directly to RGBA.
            int tileW, tileH, bitDepth, subX, subY, matrix;
            bool mono, fullRange;
            using (var first = Av1FrameDecoder.DecodeStill(payloads[0], configObus[0]))
            {
                tileW = first.Width;
                tileH = first.Height;
                bitDepth = first.BitDepth;
                subX = first.SubsamplingX;
                subY = first.SubsamplingY;
                mono = first.Monochrome;
                if ((long)tileW * primary.GridColumns < outW || (long)tileH * primary.GridRows < outH)
                    throw new InvalidDataException("AVIF grid tiles do not cover the output canvas.");
                matrix = container.NclxColour is { } nclx ? nclx.MatrixCoefficients : first.MatrixCoefficients;
                fullRange = container.NclxColour is { } nclx2 ? nclx2.FullRange : first.FullRange;

                if ((AvifDiagnostics.Ablation & AvifAblation.SkipColor) == 0)
                {
                    RenderPictureRectToRgba8(
                        first, null, container.IsPremultiplied, matrix, fullRange,
                        destination, outW, dstX0: 0, dstY0: 0,
                        width: Math.Min(tileW, outW), height: Math.Min(tileH, outH), parallelRows: false);
                }
            }

            bool skipColor = (AvifDiagnostics.Ablation & AvifAblation.SkipColor) != 0;
            bool prem = container.IsPremultiplied;
            int gridCols = primary.GridColumns;
            fixed (byte* dstBase = destination)
            {
                nint dstPtr = (nint)dstBase;
                int dstLen = destination.Length;
                RunParallel(() => Parallel.For(1, count, AvifDiagnostics.ParallelOptions, i =>
                {
                    using var pic = Av1FrameDecoder.DecodeStill(payloads[i], configObus[i]);
                    if (pic.Width != tileW || pic.Height != tileH || pic.BitDepth != bitDepth ||
                        pic.Monochrome != mono || pic.SubsamplingX != subX || pic.SubsamplingY != subY)
                    {
                        throw new InvalidDataException("AVIF grid tiles differ in dimensions, bit depth, or subsampling.");
                    }
                    if (skipColor) return;

                    int gx = i % gridCols;
                    int gy = i / gridCols;
                    int dstX0 = gx * tileW;
                    int dstY0 = gy * tileH;
                    if (dstX0 >= outW || dstY0 >= outH) return;
                    int copyW = Math.Min(tileW, outW - dstX0);
                    int copyH = Math.Min(tileH, outH - dstY0);

                    var dstSpan = new Span<byte>((byte*)dstPtr, dstLen);
                    RenderPictureRectToRgba8(
                        pic, null, prem, matrix, fullRange,
                        dstSpan, outW, dstX0, dstY0, copyW, copyH, parallelRows: false);
                }));
            }
            return bitDepth;
        }

        return 0;
    }

    private static byte[] ResolveItemBytes(ReadOnlySpan<byte> data, ItemContainer items, ItemContainer.Item tile)
    {
        long len = ItemContainer.GetItemLength(tile);
        if (len <= 0 || len > data.Length)
            throw new InvalidDataException("AVIF item payload has invalid size.");
        var buf = new byte[len];
        items.Resolve(data, tile, buf);
        return buf;
    }

    private static Av1Picture DecodeSingleItem(ReadOnlySpan<byte> data, ItemContainer items, AvifContainer.ImagePlan plan, int index)
    {
        var tile = plan.Tiles[index];
        long len = ItemContainer.GetItemLength(tile);
        if (len <= 0 || len > data.Length)
            throw new InvalidDataException("AVIF item payload has invalid size.");
        byte[] rented = ArrayPool<byte>.Shared.Rent((int)len);
        try
        {
            items.Resolve(data, tile, rented.AsSpan(0, (int)len));
            var cfg = plan.TileConfigs[index];
            ReadOnlySpan<byte> cfgSpan = cfg.ConfigObusLength > 0
                ? data.Slice(cfg.ConfigObusOffset, cfg.ConfigObusLength)
                : default;
            return Av1FrameDecoder.DecodeStill(rented.AsSpan(0, (int)len), cfgSpan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void RunParallel(Action parallelAction)
    {
        try
        {
            parallelAction();
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerExceptions;
            foreach (var e in inner)
            {
                if (e is InvalidDataException ide)
                    throw new InvalidDataException(ide.Message, ide);
            }
            ExceptionDispatchInfo.Capture(inner[0]).Throw();
            throw;
        }
    }

    private static unsafe void RenderPictureRectToRgba8(
        Av1Picture pic, Av1Picture? alphaPic, bool premultiplied, int matrixCoefficients, bool fullRange,
        Span<byte> destination, int dstStridePx, int dstX0, int dstY0, int width, int height, bool parallelRows)
    {
        var coeff = AvifColor.For(matrixCoefficients, fullRange, pic.BitDepth);
        int subX = pic.SubsamplingX;
        int subY = pic.SubsamplingY;
        bool mono = pic.Monochrome;
        ushort[] yBuf = pic.Planes[0];
        int yStride = pic.PlaneStrides[0];
        ushort[]? cbBuf = mono ? null : pic.Planes[1];
        ushort[]? crBuf = mono ? null : pic.Planes[2];
        int cStride = mono ? 0 : pic.PlaneStrides[1];
        ushort[]? aBuf = alphaPic?.Planes[0];
        int aStride = alphaPic?.PlaneStrides[0] ?? 0;
        int aBd = alphaPic?.BitDepth ?? 8;
        bool aFull = alphaPic?.FullRange ?? true;

        if (parallelRows)
        {
            fixed (byte* dstBase = destination)
            {
                nint dstPtr = (nint)dstBase;
                int dstLen = destination.Length;
                Parallel.For(0, height, AvifDiagnostics.ParallelOptions, y =>
                {
                    var dstSpan = new Span<byte>((byte*)dstPtr, dstLen);
                    RenderRowRgba8(
                        yBuf, yStride, cbBuf, crBuf, cStride, aBuf, aStride, aBd, aFull,
                        mono, subX, subY, in coeff, premultiplied,
                        dstSpan, dstStridePx, dstX0, dstY0, width, y);
                });
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                RenderRowRgba8(
                    yBuf, yStride, cbBuf, crBuf, cStride, aBuf, aStride, aBd, aFull,
                    mono, subX, subY, in coeff, premultiplied,
                    destination, dstStridePx, dstX0, dstY0, width, y);
            }
        }
    }

    private static void RenderRowRgba8(
        ushort[] yBuf, int yStride, ushort[]? cbBuf, ushort[]? crBuf, int cStride,
        ushort[]? aBuf, int aStride, int aBd, bool aFull,
        bool mono, int subX, int subY, in AvifColor.Coefficients coeff, bool prem,
        Span<byte> destination, int dstStridePx, int dstX0, int dstY0, int width, int y)
    {
        int yRow = y * yStride;
        int cRow = (y >> subY) * cStride;
        int aRow = y * aStride;
        int dstRow = ((dstY0 + y) * dstStridePx + dstX0) * 4;
        if (!prem && !coeff.Identity)
        {
            // The row kernel writes opaque pixels; straight alpha then only replaces the alpha bytes.
            var dst = destination.Slice(dstRow, width * 4);
            AvifColor.RowToRgba8(yBuf.AsSpan(yRow, width), mono ? default : cbBuf.AsSpan(cRow), mono ? default : crBuf.AsSpan(cRow),
                mono, subX, in coeff, dst);
            if (aBuf is not null)
            {
                for (int x = 0; x < width; x++)
                    dst[x * 4 + 3] = AvifColor.SampleAlphaTo8(aBuf[aRow + x], aBd, aFull);
            }
            return;
        }
        for (int x = 0; x < width; x++)
        {
            ushort ys = yBuf[yRow + x];
            ushort cbs = mono ? (ushort)0 : cbBuf![cRow + (x >> subX)];
            ushort crs = mono ? (ushort)0 : crBuf![cRow + (x >> subX)];
            byte alpha = aBuf is not null ? AvifColor.SampleAlphaTo8(aBuf[aRow + x], aBd, aFull) : (byte)255;
            AvifColor.PixelToRgba(ys, cbs, crs, mono, in coeff, alpha, prem,
                out destination[dstRow + x * 4],
                out destination[dstRow + x * 4 + 1],
                out destination[dstRow + x * 4 + 2],
                out destination[dstRow + x * 4 + 3]);
        }
    }

    internal static int DecodeContainerToRgba16(ReadOnlySpan<byte> data, AvifContainer container, Span<ushort> destination, int outW, int outH, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (container.Overlay is not null)
        {
            RenderOverlayRgba16(data, container, destination, outW, outH, cancellationToken);
            return container.BitDepth;
        }
        var planes = DecodePlanes(data, container);
        RenderRgba16(container, planes, destination, outW, outH);
        return planes.BitDepth;
    }

    /// <summary>
    /// Decodes the primary image's Y/Cb/Cr (and optional Alpha) planes on the canvas, before colour conversion
    /// and transformative properties (<c>clap</c>/<c>irot</c>/<c>imir</c>).
    /// </summary>
    internal static AvifDecodedPlanes DecodePlanes(ReadOnlySpan<byte> data)
    {
        var container = AvifContainer.Parse(data);
        return DecodePlanes(data, container);
    }

    internal static AvifDecodedPlanes DecodePlanes(ReadOnlySpan<byte> data, AvifContainer container)
    {
        if (container.Overlay is not null)
            throw new InvalidDataException("Planar YCbCr output is not defined for an 'iovl' overlay image.");
        var primaryPics = DecodePlanTiles(data, container.Items, container.Primary);
        Av1Picture[]? alphaPics = null;
        try
        {
            var first = primaryPics[0];

            if (container.Primary.CanvasWidth == 0 || container.Primary.CanvasHeight == 0)
            {
                container.Primary.CanvasWidth = first.Width;
                container.Primary.CanvasHeight = first.Height;
            }

            int canvasW = container.Primary.CanvasWidth;
            int canvasH = container.Primary.CanvasHeight;
            ValidateGridTiles(container.Primary, primaryPics, canvasW, canvasH);

            int subX = first.Monochrome ? 1 : first.SubsamplingX;
            int subY = first.Monochrome ? 1 : first.SubsamplingY;
            var yPlane = ComposePlane(primaryPics, container.Primary, planeIdx: 0, canvasW, canvasH, 0, 0);
            ushort[]? cbPlane = null;
            ushort[]? crPlane = null;
            if (!first.Monochrome)
            {
                cbPlane = ComposePlane(primaryPics, container.Primary, planeIdx: 1, canvasW, canvasH, subX, subY);
                crPlane = ComposePlane(primaryPics, container.Primary, planeIdx: 2, canvasW, canvasH, subX, subY);
            }

            ushort[]? alphaPlane = null;
            int alphaBitDepth = first.BitDepth;
            bool alphaFullRange = true;
            if (container.Alpha is { } alphaPlan)
            {
                alphaPics = DecodePlanTiles(data, container.Items, alphaPlan);
                var firstAlpha = alphaPics[0];
                if (alphaPlan.CanvasWidth == 0 || alphaPlan.CanvasHeight == 0)
                {
                    alphaPlan.CanvasWidth = firstAlpha.Width;
                    alphaPlan.CanvasHeight = firstAlpha.Height;
                }
                if (alphaPlan.CanvasWidth != canvasW || alphaPlan.CanvasHeight != canvasH)
                    throw new InvalidDataException($"AVIF alpha canvas {alphaPlan.CanvasWidth}x{alphaPlan.CanvasHeight} does not match primary {canvasW}x{canvasH}.");

                ValidateGridTiles(alphaPlan, alphaPics, canvasW, canvasH);
                alphaPlane = ComposePlane(alphaPics, alphaPlan, planeIdx: 0, canvasW, canvasH, 0, 0);
                alphaBitDepth = firstAlpha.BitDepth;
                alphaFullRange = firstAlpha.FullRange;
            }

            int matrix = container.NclxColour is { } nclx ? nclx.MatrixCoefficients : first.MatrixCoefficients;
            bool fullRange = container.NclxColour is { } nclx2 ? nclx2.FullRange : first.FullRange;

            return new AvifDecodedPlanes
            {
                Width = canvasW,
                Height = canvasH,
                BitDepth = first.BitDepth,
                SubsamplingX = first.SubsamplingX,
                SubsamplingY = first.SubsamplingY,
                Monochrome = first.Monochrome,
                Y = yPlane,
                Cb = cbPlane,
                Cr = crPlane,
                Alpha = alphaPlane,
                AlphaBitDepth = alphaBitDepth,
                AlphaFullRange = alphaFullRange,
                MatrixCoefficients = matrix,
                FullRange = fullRange,
                IsPremultiplied = container.IsPremultiplied,
            };
        }
        finally
        {
            foreach (var p in primaryPics) p?.Dispose();
            if (alphaPics is not null)
                foreach (var p in alphaPics) p?.Dispose();
        }
    }

    internal static void EnsureCanvasDimensions(ReadOnlySpan<byte> data, AvifContainer container)
    {
        if (container.Overlay is not null)
            return;
        if (container.Primary.CanvasWidth > 0 && container.Primary.CanvasHeight > 0)
            return;

        // Fallback when 'ispe' is absent on a single 'av01' item: read dimensions from sequence header OBU.
        var seq = TryReadSequenceHeader(data, container)
            ?? throw new InvalidDataException("AV1 item has no sequence header.");
        container.Primary.CanvasWidth = seq.MaxFrameWidth;
        container.Primary.CanvasHeight = seq.MaxFrameHeight;
    }

    internal static Av1SequenceHeader? TryReadSequenceHeader(ReadOnlySpan<byte> data, AvifContainer container)
    {
        if (container.Overlay is not null)
        {
            return container.Overlay.Layers.Count > 0
                ? TryReadSequenceHeader(data, container.Overlay.Layers[0].Image)
                : null;
        }
        if (container.Primary.Tiles.Count == 0)
            return null;

        var tile = container.Primary.Tiles[0];
        var cfg = container.Primary.TileConfigs[0];
        if (cfg.ConfigObusLength > 0)
        {
            var cfgSpan = data.Slice(cfg.ConfigObusOffset, cfg.ConfigObusLength);
            foreach (var obu in Av1ObuParser.ParseAll(cfgSpan))
            {
                if (obu.Type == Av1Constants.ObuSequenceHeader)
                    return Av1SequenceHeader.Parse(cfgSpan.Slice(obu.PayloadOffset, obu.PayloadLength));
            }
        }

        long len = ItemContainer.GetItemLength(tile);
        if (len <= 0 || len > data.Length)
            throw new InvalidDataException("Invalid AV1 item payload length.");
        // Sequence header OBU is always near the start of the item; resolve up to first 4 KB when possible, or full item if small.
        var buf = new byte[len];
        container.Items.Resolve(data, tile, buf);
        foreach (var obu in Av1ObuParser.ParseAll(buf))
        {
            if (obu.Type == Av1Constants.ObuSequenceHeader)
                return Av1SequenceHeader.Parse(buf.AsSpan(obu.PayloadOffset, obu.PayloadLength));
        }
        return null;
    }

    private static Av1Picture[] DecodePlanTiles(ReadOnlySpan<byte> data, ItemContainer items, AvifContainer.ImagePlan plan)
    {
        int count = plan.Tiles.Count;
        var payloads = new byte[count][];
        var configObus = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            long len = ItemContainer.GetItemLength(plan.Tiles[i]);
            if (len <= 0 || len > data.Length)
                throw new InvalidDataException("AVIF item payload has invalid size.");
            payloads[i] = new byte[len];
            items.Resolve(data, plan.Tiles[i], payloads[i]);

            var cfg = plan.TileConfigs[i];
            configObus[i] = cfg.ConfigObusLength > 0
                ? data.Slice(cfg.ConfigObusOffset, cfg.ConfigObusLength).ToArray()
                : [];
        }

        var pictures = new Av1Picture[count];
        if (count == 1)
        {
            pictures[0] = Av1FrameDecoder.DecodeStill(payloads[0], configObus[0]);
            return pictures;
        }

        try
        {
            Parallel.For(0, count, AvifDiagnostics.ParallelOptions, i =>
            {
                pictures[i] = Av1FrameDecoder.DecodeStill(payloads[i], configObus[i]);
            });
        }
        catch (AggregateException ex)
        {
            var inner = ex.Flatten().InnerExceptions;
            foreach (var e in inner)
            {
                if (e is InvalidDataException ide)
                    throw new InvalidDataException(ide.Message, ide);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner[0]).Throw();
            throw;
        }
        return pictures;
    }

    private static void ValidateGridTiles(AvifContainer.ImagePlan plan, Av1Picture[] pics, int canvasW, int canvasH)
    {
        var first = pics[0];
        int tileW = first.Width;
        int tileH = first.Height;
        foreach (var p in pics)
        {
            if (p.Width != tileW || p.Height != tileH || p.BitDepth != first.BitDepth ||
                p.Monochrome != first.Monochrome || p.SubsamplingX != first.SubsamplingX || p.SubsamplingY != first.SubsamplingY)
            {
                throw new InvalidDataException("AVIF grid tiles differ in dimensions, bit depth, or subsampling.");
            }
        }
        if ((long)tileW * plan.GridColumns < canvasW || (long)tileH * plan.GridRows < canvasH)
            throw new InvalidDataException("AVIF grid tiles do not cover the output canvas.");
    }

    private static ushort[] ComposePlane(Av1Picture[] pics, AvifContainer.ImagePlan plan, int planeIdx, int canvasW, int canvasH, int subX, int subY)
    {
        int outW = (canvasW + subX) >> subX;
        int outH = (canvasH + subY) >> subY;
        var dst = new ushort[outW * outH];
        int tileW = pics[0].Width;
        int tileH = pics[0].Height;
        int tilePlaneW = pics[0].OutputPlaneWidth(planeIdx);
        int tilePlaneH = pics[0].OutputPlaneHeight(planeIdx);

        for (int i = 0; i < pics.Length; i++)
        {
            int gx = i % plan.GridColumns;
            int gy = i / plan.GridColumns;
            int dstX0 = (gx * tileW) >> subX;
            int dstY0 = (gy * tileH) >> subY;
            if (dstX0 >= outW || dstY0 >= outH) continue;
            int copyW = Math.Min(tilePlaneW, outW - dstX0);
            int copyH = Math.Min(tilePlaneH, outH - dstY0);

            ushort[] src = pics[i].Planes[planeIdx];
            int srcStride = pics[i].PlaneStrides[planeIdx];
            for (int y = 0; y < copyH; y++)
            {
                src.AsSpan(y * srcStride, copyW).CopyTo(dst.AsSpan((dstY0 + y) * outW + dstX0));
            }
        }
        return dst;
    }

    private static (int A, int B, int C, int D, int E, int F) ComposeAffine(List<AvifContainer.Transform> transforms, int width, int height)
    {
        int a = 1, b = 0, c = 0, d = 0, e = 1, f = 0;
        foreach (var t in transforms)
        {
            switch (t.Type)
            {
                case AvifContainer.PropClap:
                {
                    var (x0, y0, w, h) = AvifContainer.ClapRectangle(t.Clap!, width, height);
                    c -= x0;
                    f -= y0;
                    width = w;
                    height = h;
                    break;
                }
                case AvifContainer.PropIrot:
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
                case AvifContainer.PropImir:
                    // ISO/IEC 23008-12:2022 §6.5.12 / AOM AVIF: axis 0 = horizontal axis (top<->bottom), axis 1 = vertical axis (left<->right).
                    if (t.A == 0) (d, e, f) = (-d, -e, height - 1 - f);
                    else (a, b, c) = (-a, -b, width - 1 - c);
                    break;
            }
        }
        return (a, b, c, d, e, f);
    }

    private static void RenderRgba(AvifContainer container, AvifDecodedPlanes planes, Span<byte> destination, int outW, int outH)
        => RenderRgba(container.Transforms, planes, destination, outW, outH);

    internal static unsafe void RenderRgba(List<AvifContainer.Transform> transforms, AvifDecodedPlanes planes, Span<byte> destination, int outW, int outH)
    {
        if ((AvifDiagnostics.Ablation & AvifAblation.SkipColor) != 0)
            return;
        var (a, b, c, d, e, f) = ComposeAffine(transforms, planes.Width, planes.Height);

        var coeff = AvifColor.For(planes.MatrixCoefficients, planes.FullRange, planes.BitDepth);
        int cw = (planes.Width + planes.SubsamplingX) >> planes.SubsamplingX;
        int subX = planes.SubsamplingX;
        int subY = planes.SubsamplingY;
        bool mono = planes.Monochrome;
        ushort[] yBuf = planes.Y;
        ushort[]? cbBuf = planes.Cb;
        ushort[]? crBuf = planes.Cr;
        ushort[]? aBuf = planes.Alpha;
        int aBd = planes.AlphaBitDepth;
        bool aFull = planes.AlphaFullRange;
        bool prem = planes.IsPremultiplied;

        bool isIdentity = a == 1 && b == 0 && c == 0 && d == 0 && e == 1 && f == 0 && outW == planes.Width && outH == planes.Height;
        if (isIdentity)
        {
            if (outH >= 64)
            {
                fixed (byte* dstBase = destination)
                {
                    nint dstPtr = (nint)dstBase;
                    int dstLen = destination.Length;
                    int yStride = planes.Width;
                    Parallel.For(0, outH, AvifDiagnostics.ParallelOptions, y =>
                    {
                        var dstSpan = new Span<byte>((byte*)dstPtr, dstLen);
                        RenderRowRgba8(
                            yBuf, yStride, cbBuf, crBuf, cw, aBuf, yStride, aBd, aFull,
                            mono, subX, subY, in coeff, prem,
                            dstSpan, outW, 0, 0, outW, y);
                    });
                }
            }
            else
            {
                for (int y = 0; y < outH; y++)
                {
                    RenderRowRgba8(
                        yBuf, planes.Width, cbBuf, crBuf, cw, aBuf, planes.Width, aBd, aFull,
                        mono, subX, subY, in coeff, prem,
                        destination, outW, 0, 0, outW, y);
                }
            }
            return;
        }

        for (int y = 0; y < planes.Height; y++)
        {
            int yRow = y * planes.Width;
            int cRow = (y >> subY) * cw;
            for (int x = 0; x < planes.Width; x++)
            {
                int ox = a * x + b * y + c;
                int oy = d * x + e * y + f;
                if ((uint)ox >= (uint)outW || (uint)oy >= (uint)outH) continue;

                ushort ys = yBuf[yRow + x];
                ushort cbs = mono ? (ushort)0 : cbBuf![cRow + (x >> subX)];
                ushort crs = mono ? (ushort)0 : crBuf![cRow + (x >> subX)];
                byte alpha = aBuf is not null ? AvifColor.SampleAlphaTo8(aBuf[yRow + x], aBd, aFull) : (byte)255;
                int dstOff = (oy * outW + ox) * 4;
                AvifColor.PixelToRgba(ys, cbs, crs, mono, in coeff, alpha, prem,
                    out destination[dstOff],
                    out destination[dstOff + 1],
                    out destination[dstOff + 2],
                    out destination[dstOff + 3]);
            }
        }
    }

    private static void RenderRgba16(AvifContainer container, AvifDecodedPlanes planes, Span<ushort> destination, int outW, int outH)
        => RenderRgba16(container.Transforms, planes, destination, outW, outH);

    internal static unsafe void RenderRgba16(List<AvifContainer.Transform> transforms, AvifDecodedPlanes planes, Span<ushort> destination, int outW, int outH)
    {
        var (a, b, c, d, e, f) = ComposeAffine(transforms, planes.Width, planes.Height);

        var coeff = AvifColor.For16(planes.MatrixCoefficients, planes.FullRange, planes.BitDepth);
        int cw = (planes.Width + planes.SubsamplingX) >> planes.SubsamplingX;
        int subX = planes.SubsamplingX;
        int subY = planes.SubsamplingY;
        bool mono = planes.Monochrome;
        ushort[] yBuf = planes.Y;
        ushort[]? cbBuf = planes.Cb;
        ushort[]? crBuf = planes.Cr;
        ushort[]? aBuf = planes.Alpha;
        int aBd = planes.AlphaBitDepth;
        bool aFull = planes.AlphaFullRange;
        bool prem = planes.IsPremultiplied;

        bool isIdentity = a == 1 && b == 0 && c == 0 && d == 0 && e == 1 && f == 0 && outW == planes.Width && outH == planes.Height;
        if (isIdentity)
        {
            int yStride = planes.Width;
            if (outH < 64)
            {
                for (int y = 0; y < outH; y++)
                {
                    RenderRowRgba16(yBuf, yStride, cbBuf, crBuf, cw, aBuf, yStride, aBd, aFull, mono, subX, subY, in coeff, prem,
                        y, destination.Slice(y * outW * 4, outW * 4));
                }
                return;
            }
            fixed (ushort* dstBase = destination)
            {
                nint dstPtr = (nint)dstBase;
                int dstLen = destination.Length;
                Parallel.For(0, outH, AvifDiagnostics.ParallelOptions, y =>
                {
                    var row = new Span<ushort>((ushort*)dstPtr, dstLen).Slice(y * outW * 4, outW * 4);
                    RenderRowRgba16(yBuf, yStride, cbBuf, crBuf, cw, aBuf, yStride, aBd, aFull, mono, subX, subY, in coeff, prem, y, row);
                });
            }
            return;
        }

        for (int y = 0; y < planes.Height; y++)
        {
            int yRow = y * planes.Width;
            int cRow = (y >> subY) * cw;
            for (int x = 0; x < planes.Width; x++)
            {
                int ox = a * x + b * y + c;
                int oy = d * x + e * y + f;
                if ((uint)ox >= (uint)outW || (uint)oy >= (uint)outH) continue;

                ushort ys = yBuf[yRow + x];
                ushort cbs = mono ? (ushort)0 : cbBuf![cRow + (x >> subX)];
                ushort crs = mono ? (ushort)0 : crBuf![cRow + (x >> subX)];
                ushort alpha = aBuf is not null ? AvifColor.SampleAlphaTo16(aBuf[yRow + x], aBd, aFull) : (ushort)65535;
                int dstOff = (oy * outW + ox) * 4;
                AvifColor.PixelToRgba16(ys, cbs, crs, mono, in coeff, alpha, prem,
                    out destination[dstOff],
                    out destination[dstOff + 1],
                    out destination[dstOff + 2],
                    out destination[dstOff + 3]);
            }
        }
    }

    /// <summary>Row <paramref name="y"/> of a picture as RGBA16 into <paramref name="dst"/> (its width × 4 samples).</summary>
    private static void RenderRowRgba16(
        ushort[] yBuf, int yStride, ushort[]? cbBuf, ushort[]? crBuf, int cStride,
        ushort[]? aBuf, int aStride, int aBd, bool aFull,
        bool mono, int subX, int subY, in AvifColor.Coefficients16 coeff, bool prem, int y, Span<ushort> dst)
    {
        int width = dst.Length / 4;
        int yRow = y * yStride;
        int cRow = (y >> subY) * cStride;
        int aRow = y * aStride;
        if (!prem && !coeff.Identity)
        {
            // The row kernel writes opaque pixels; straight alpha then only replaces the alpha samples.
            AvifColor.RowToRgba16(yBuf.AsSpan(yRow, width), mono ? default : cbBuf.AsSpan(cRow), mono ? default : crBuf.AsSpan(cRow),
                mono, subX, in coeff, dst);
            if (aBuf is not null)
            {
                for (int x = 0; x < width; x++)
                    dst[x * 4 + 3] = AvifColor.SampleAlphaTo16(aBuf[aRow + x], aBd, aFull);
            }
            return;
        }
        for (int x = 0; x < width; x++)
        {
            ushort ys = yBuf[yRow + x];
            ushort cbs = mono ? (ushort)0 : cbBuf![cRow + (x >> subX)];
            ushort crs = mono ? (ushort)0 : crBuf![cRow + (x >> subX)];
            ushort alpha = aBuf is not null ? AvifColor.SampleAlphaTo16(aBuf[aRow + x], aBd, aFull) : (ushort)65535;
            AvifColor.PixelToRgba16(ys, cbs, crs, mono, in coeff, alpha, prem,
                out dst[x * 4], out dst[x * 4 + 1], out dst[x * 4 + 2], out dst[x * 4 + 3]);
        }
    }

    /// <summary>
    /// Renders a whole decoded picture (no transforms, straight alpha) into a destination laid out by
    /// <see cref="PixelConverter.TryGetLayout"/>, row by row in parallel: each row is converted to RGBA8 (8-bit) or
    /// RGBA16 (deeper), then to <paramref name="format"/>, which is what rendering the whole image to RGBA and
    /// converting it gives. 8-bit into <see cref="PixelFormat.Rgba32"/> renders straight into the destination.
    /// </summary>
    internal static unsafe void RenderPictureInto(Av1Picture pic, Av1Picture? alphaPic, int matrixCoefficients, bool fullRange,
        Span<byte> destination, int rowStride, PixelFormat format)
    {
        if ((AvifDiagnostics.Ablation & AvifAblation.SkipColor) != 0)
            return;
        int w = pic.Width, h = pic.Height;
        bool wide = pic.BitDepth > 8;
        var coeff8 = wide ? default : AvifColor.For(matrixCoefficients, fullRange, pic.BitDepth);
        var coeff16 = wide ? AvifColor.For16(matrixCoefficients, fullRange, pic.BitDepth) : default;
        int subX = pic.SubsamplingX, subY = pic.SubsamplingY;
        bool mono = pic.Monochrome;
        ushort[] yBuf = pic.Planes[0];
        int yStride = pic.PlaneStrides[0];
        ushort[]? cbBuf = mono ? null : pic.Planes[1];
        ushort[]? crBuf = mono ? null : pic.Planes[2];
        int cStride = mono ? 0 : pic.PlaneStrides[1];
        ushort[]? aBuf = alphaPic?.Planes[0];
        int aStride = alphaPic?.PlaneStrides[0] ?? 0;
        int aBd = alphaPic?.BitDepth ?? 8;
        bool aFull = alphaPic?.FullRange ?? true;
        var source = wide ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        bool direct = !wide && format == PixelFormat.Rgba32;
        int srcRow = w * source.BytesPerPixel;
        int dstRowBytes = w * format.BytesPerPixel;

        fixed (byte* dstBase = destination)
        {
            nint dstPtr = (nint)dstBase;
            int dstLen = destination.Length;
            Parallel.For(0, h, AvifDiagnostics.ParallelOptions,
                () => direct ? null : new byte[srcRow],
                (y, _, rowBuf) =>
                {
                    var dst = new Span<byte>((byte*)dstPtr, dstLen).Slice(y * rowStride, dstRowBytes);
                    Span<byte> rgba = direct ? dst : rowBuf;
                    if (wide)
                    {
                        RenderRowRgba16(yBuf, yStride, cbBuf, crBuf, cStride, aBuf, aStride, aBd, aFull, mono, subX, subY,
                            in coeff16, prem: false, y, MemoryMarshal.Cast<byte, ushort>(rgba));
                    }
                    else
                    {
                        RenderRowRgba8(yBuf, yStride, cbBuf, crBuf, cStride, aBuf, aStride, aBd, aFull, mono, subX, subY,
                            in coeff8, prem: false, rgba, w, 0, -y, w, y); // dstY0 = -y: row y lands at the start of rgba
                    }
                    if (!direct)
                        PixelConverter.Convert(rgba, srcRow, source, dst, dstRowBytes, format, w, 1);
                    return rowBuf;
                },
                _ => { });
        }
    }

    private static void RenderOverlayRgba8(ReadOnlySpan<byte> data, AvifContainer container, Span<byte> destination, int outW, int outH, CancellationToken cancellationToken)
    {
        var ov = container.Overlay!;
        int cw = ov.CanvasWidth;
        int ch = ov.CanvasHeight;
        bool hasTransforms = container.Transforms.Count > 0;
        byte[]? canvasArr = hasTransforms ? new byte[checked((long)cw * ch * 4)] : null;
        Span<byte> canvas = hasTransforms ? canvasArr : destination;

        byte fr = (byte)(ov.FillR >> 8);
        byte fg = (byte)(ov.FillG >> 8);
        byte fb = (byte)(ov.FillB >> 8);
        byte fa = (byte)(ov.FillA >> 8);
        for (int i = 0; i < cw * ch; i++)
        {
            int p = i * 4;
            canvas[p] = fr;
            canvas[p + 1] = fg;
            canvas[p + 2] = fb;
            canvas[p + 3] = fa;
        }

        foreach (var layer in ov.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCanvasDimensions(data, layer.Image);
            var (lw, lh) = layer.Image.DisplaySize();
            var layerBuf = new byte[checked((long)lw * lh * 4)];
            DecodeContainerToRgba8(data, layer.Image, layerBuf, lw, lh, cancellationToken);

            int x0 = Math.Max(0, layer.HorizontalOffset);
            int y0 = Math.Max(0, layer.VerticalOffset);
            int x1 = Math.Min(cw, layer.HorizontalOffset + lw);
            int y1 = Math.Min(ch, layer.VerticalOffset + lh);
            for (int y = y0; y < y1; y++)
            {
                int sy = y - layer.VerticalOffset;
                for (int x = x0; x < x1; x++)
                {
                    int sx = x - layer.HorizontalOffset;
                    int srcOff = (sy * lw + sx) * 4;
                    int dstOff = (y * cw + x) * 4;
                    byte sa = layerBuf[srcOff + 3];
                    if (sa == 255)
                    {
                        canvas[dstOff] = layerBuf[srcOff];
                        canvas[dstOff + 1] = layerBuf[srcOff + 1];
                        canvas[dstOff + 2] = layerBuf[srcOff + 2];
                        canvas[dstOff + 3] = 255;
                    }
                    else if (sa > 0)
                    {
                        int inv = 255 - sa;
                        int da = canvas[dstOff + 3];
                        int oa = sa + ((da * inv + 127) / 255);
                        if (oa > 0)
                        {
                            canvas[dstOff] = (byte)((layerBuf[srcOff] * sa * 255 + canvas[dstOff] * da * inv + oa * 127) / (oa * 255));
                            canvas[dstOff + 1] = (byte)((layerBuf[srcOff + 1] * sa * 255 + canvas[dstOff + 1] * da * inv + oa * 127) / (oa * 255));
                            canvas[dstOff + 2] = (byte)((layerBuf[srcOff + 2] * sa * 255 + canvas[dstOff + 2] * da * inv + oa * 127) / (oa * 255));
                        }
                        canvas[dstOff + 3] = (byte)oa;
                    }
                }
            }
        }

        if (hasTransforms)
        {
            var (a, b, c, d, e, f) = ComposeAffine(container.Transforms, cw, ch);
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int ox = a * x + b * y + c;
                    int oy = d * x + e * y + f;
                    if ((uint)ox >= (uint)outW || (uint)oy >= (uint)outH) continue;
                    int srcOff = (y * cw + x) * 4;
                    int dstOff = (oy * outW + ox) * 4;
                    destination[dstOff] = canvas[srcOff];
                    destination[dstOff + 1] = canvas[srcOff + 1];
                    destination[dstOff + 2] = canvas[srcOff + 2];
                    destination[dstOff + 3] = canvas[srcOff + 3];
                }
            }
        }
    }

    private static void RenderOverlayRgba16(ReadOnlySpan<byte> data, AvifContainer container, Span<ushort> destination, int outW, int outH, CancellationToken cancellationToken)
    {
        var ov = container.Overlay!;
        int cw = ov.CanvasWidth;
        int ch = ov.CanvasHeight;
        bool hasTransforms = container.Transforms.Count > 0;
        ushort[]? canvasArr = hasTransforms ? new ushort[checked((long)cw * ch * 4)] : null;
        Span<ushort> canvas = hasTransforms ? canvasArr : destination;

        for (int i = 0; i < cw * ch; i++)
        {
            int p = i * 4;
            canvas[p] = ov.FillR;
            canvas[p + 1] = ov.FillG;
            canvas[p + 2] = ov.FillB;
            canvas[p + 3] = ov.FillA;
        }

        foreach (var layer in ov.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCanvasDimensions(data, layer.Image);
            var (lw, lh) = layer.Image.DisplaySize();
            var layerBuf = new ushort[checked((long)lw * lh * 4)];
            DecodeContainerToRgba16(data, layer.Image, layerBuf, lw, lh, cancellationToken);

            int x0 = Math.Max(0, layer.HorizontalOffset);
            int y0 = Math.Max(0, layer.VerticalOffset);
            int x1 = Math.Min(cw, layer.HorizontalOffset + lw);
            int y1 = Math.Min(ch, layer.VerticalOffset + lh);
            for (int y = y0; y < y1; y++)
            {
                int sy = y - layer.VerticalOffset;
                for (int x = x0; x < x1; x++)
                {
                    int sx = x - layer.HorizontalOffset;
                    int srcOff = (sy * lw + sx) * 4;
                    int dstOff = (y * cw + x) * 4;
                    ushort sa = layerBuf[srcOff + 3];
                    if (sa == 65535)
                    {
                        canvas[dstOff] = layerBuf[srcOff];
                        canvas[dstOff + 1] = layerBuf[srcOff + 1];
                        canvas[dstOff + 2] = layerBuf[srcOff + 2];
                        canvas[dstOff + 3] = 65535;
                    }
                    else if (sa > 0)
                    {
                        long inv = 65535 - sa;
                        long da = canvas[dstOff + 3];
                        long oa = sa + ((da * inv + 32767) / 65535);
                        if (oa > 0)
                        {
                            canvas[dstOff] = (ushort)((layerBuf[srcOff] * (long)sa * 65535 + canvas[dstOff] * da * inv + oa * 32767) / (oa * 65535));
                            canvas[dstOff + 1] = (ushort)((layerBuf[srcOff + 1] * (long)sa * 65535 + canvas[dstOff + 1] * da * inv + oa * 32767) / (oa * 65535));
                            canvas[dstOff + 2] = (ushort)((layerBuf[srcOff + 2] * (long)sa * 65535 + canvas[dstOff + 2] * da * inv + oa * 32767) / (oa * 65535));
                        }
                        canvas[dstOff + 3] = (ushort)oa;
                    }
                }
            }
        }

        if (hasTransforms)
        {
            var (a, b, c, d, e, f) = ComposeAffine(container.Transforms, cw, ch);
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int ox = a * x + b * y + c;
                    int oy = d * x + e * y + f;
                    if ((uint)ox >= (uint)outW || (uint)oy >= (uint)outH) continue;
                    int srcOff = (y * cw + x) * 4;
                    int dstOff = (oy * outW + ox) * 4;
                    destination[dstOff] = canvas[srcOff];
                    destination[dstOff + 1] = canvas[srcOff + 1];
                    destination[dstOff + 2] = canvas[srcOff + 2];
                    destination[dstOff + 3] = canvas[srcOff + 3];
                }
            }
        }
    }
}
