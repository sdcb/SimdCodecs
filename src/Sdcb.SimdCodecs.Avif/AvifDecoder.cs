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
        var container = AvifContainer.Parse(data);
        EnsureCanvasDimensions(data, container);
        var (width, height) = container.DisplaySize();
        return new AvifInfo
        {
            Width = width,
            Height = height,
            BitDepth = container.Primary.TileConfigs[0].BitDepth,
            HasAlpha = container.Alpha is not null,
            FrameCount = 1,
            TileCount = container.Primary.Tiles.Count,
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
        var planes = DecodePlanes(data, container);
        var (width, height) = container.DisplaySize();
        var pixels = new byte[(long)width * height * 4];
        RenderRgba(container, planes, pixels, width, height);
        return new AvifImage(width, height, pixels, planes.BitDepth, planes.Alpha is not null, container.IccProfile);
    }

    /// <summary>
    /// Decodes an AVIF image into a caller-supplied RGBA8 buffer.
    /// </summary>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        var container = AvifContainer.Parse(data);
        var planes = DecodePlanes(data, container);
        var (width, height) = container.DisplaySize();
        long needed = (long)width * height * 4;
        if (rgbaDestination.Length < needed)
            throw new ArgumentException($"The destination needs {needed} bytes for a {width}x{height} image.", nameof(rgbaDestination));
        RenderRgba(container, planes, rgbaDestination, width, height);
        return (width, height);
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

    private static AvifDecodedPlanes DecodePlanes(ReadOnlySpan<byte> data, AvifContainer container)
    {
        var primaryPics = DecodePlanTiles(data, container.Items, container.Primary);
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
            var alphaPics = DecodePlanTiles(data, container.Items, alphaPlan);
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

    private static void EnsureCanvasDimensions(ReadOnlySpan<byte> data, AvifContainer container)
    {
        if (container.Primary.CanvasWidth > 0 && container.Primary.CanvasHeight > 0)
            return;

        // Fallback when 'ispe' is absent on a single 'av01' item: read dimensions from sequence header OBU.
        var tile = container.Primary.Tiles[0];
        var cfg = container.Primary.TileConfigs[0];
        long len = ItemContainer.GetItemLength(tile);
        if (len <= 0 || len > data.Length)
            throw new InvalidDataException("Invalid AV1 item payload length.");
        var buf = new byte[len];
        container.Items.Resolve(data, tile, buf);

        Av1SequenceHeader? seq = null;
        if (cfg.ConfigObusLength > 0)
        {
            var cfgSpan = data.Slice(cfg.ConfigObusOffset, cfg.ConfigObusLength);
            foreach (var obu in Av1ObuParser.ParseAll(cfgSpan))
            {
                if (obu.Type == Av1Constants.ObuSequenceHeader)
                {
                    seq = Av1SequenceHeader.Parse(cfgSpan.Slice(obu.PayloadOffset, obu.PayloadLength));
                    break;
                }
            }
        }
        foreach (var obu in Av1ObuParser.ParseAll(buf))
        {
            if (obu.Type == Av1Constants.ObuSequenceHeader)
            {
                seq = Av1SequenceHeader.Parse(buf.AsSpan(obu.PayloadOffset, obu.PayloadLength));
                break;
            }
        }
        if (seq is null)
            throw new InvalidDataException("AV1 item has no sequence header.");
        container.Primary.CanvasWidth = seq.MaxFrameWidth;
        container.Primary.CanvasHeight = seq.MaxFrameHeight;
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
            Parallel.For(0, count, i =>
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

    private static void RenderRgba(AvifContainer container, AvifDecodedPlanes planes, Span<byte> destination, int outW, int outH)
    {
        // Compose transformative properties (clap, irot, imir) in association order into an affine map:
        // output = (a*x + b*y + c, d*x + e*y + f) from canvas (x, y).
        int width = planes.Width;
        int height = planes.Height;
        int a = 1, b = 0, c = 0, d = 0, e = 1, f = 0;

        foreach (var t in container.Transforms)
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

        // Fast path: no transform (identity map)
        bool isIdentity = a == 1 && b == 0 && c == 0 && d == 0 && e == 1 && f == 0 && outW == planes.Width && outH == planes.Height;
        if (isIdentity)
        {
            for (int y = 0; y < outH; y++)
            {
                int yRow = y * planes.Width;
                int cRow = (y >> subY) * cw;
                int dstRow = y * outW * 4;
                for (int x = 0; x < outW; x++)
                {
                    ushort ys = yBuf[yRow + x];
                    ushort cbs = mono ? (ushort)0 : cbBuf![cRow + (x >> subX)];
                    ushort crs = mono ? (ushort)0 : crBuf![cRow + (x >> subX)];
                    byte alpha = aBuf is not null ? AvifColor.SampleAlphaTo8(aBuf[yRow + x], aBd, aFull) : (byte)255;
                    AvifColor.PixelToRgba(ys, cbs, crs, mono, in coeff, alpha, prem,
                        out destination[dstRow + x * 4],
                        out destination[dstRow + x * 4 + 1],
                        out destination[dstRow + x * 4 + 2],
                        out destination[dstRow + x * 4 + 3]);
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
}
