using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif.Hevc;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// Pure-managed HEIF / HEIC decoder (ISO/IEC 23008-12).
/// Decodes HEVC-coded still images and tiled grids from an ISOBMFF container.
/// </summary>
/// <remarks>
/// <para>
/// HEVC (H.265) is patent-encumbered. This package conveys no patent licence.
/// </para>
/// <para>
/// Only intra (I-frame) decoding is implemented; image sequences (<c>.heics</c>) are not supported.
/// Supported profiles: Main, Main 10 and Main Still Picture (4:2:0, 8 or 10 bits). The range-extension
/// profiles (Main 4:4:4, Main 12, Main 10 Intra, …) are rejected with <see cref="InvalidDataException"/>.
/// Auxiliary images (alpha, depth, gain maps) are ignored: the primary colour image is decoded.
/// </para>
/// <para>
/// The transformative properties of the primary item — clean aperture (<c>clap</c>), rotation
/// (<c>irot</c>) and mirroring (<c>imir</c>) — are applied in the order the file associates them, so the
/// pixels come out display-oriented. An Exif orientation tag in the file is not applied (HEIF readers
/// use the item properties instead).
/// </para>
/// </remarks>
public static class HeifDecoder
{
    /// <summary>
    /// Reads the HEIF container metadata without decoding any HEVC payload.
    /// </summary>
    /// <param name="data">HEIF file bytes.</param>
    /// <returns>Container-level information: primary image dimensions, bit depth, colour info.</returns>
    /// <exception cref="InvalidDataException">The data is not a decodable HEIF container.</exception>
    public static HeifInfo ReadInfo(ReadOnlySpan<byte> data)
    {
        var container = HeifContainer.Parse(data);
        var sps = container.TileConfigs[0].ParseSps();
        var (width, height) = container.DisplaySize();
        return new HeifInfo
        {
            Width = width,
            Height = height,
            BitDepth = sps.BitDepthY,
            TileCount = container.Tiles.Count,
            IccProfile = container.IccProfile,
            Orientation = ImageOrientation.Normal,
        };
    }

    /// <summary>
    /// Decodes the primary image from a HEIF container into a new <see cref="HeifImage"/>.
    /// </summary>
    /// <param name="data">HEIF file bytes.</param>
    /// <returns>Decoded image with RGBA pixels.</returns>
    /// <exception cref="InvalidDataException">The container is malformed or uses an unsupported codec.</exception>
    public static HeifImage Decode(ReadOnlySpan<byte> data)
    {
        var container = HeifContainer.Parse(data);
        var (width, height) = container.DisplaySize();
        var pixels = new byte[(long)width * height * 4];
        var bitDepth = DecodeRgba(data, container, pixels);
        return new HeifImage(width, height, pixels, bitDepth, container.IccProfile);
    }

    /// <summary>
    /// Decodes the primary image into a caller-supplied RGBA buffer.
    /// </summary>
    /// <param name="data">HEIF file bytes.</param>
    /// <param name="rgbaDestination">
    /// Destination buffer, tightly packed row-major RGBA8. Must be at least
    /// <c>width × height × 4</c> bytes; call <see cref="ReadInfo"/> to obtain dimensions first.
    /// </param>
    /// <returns>The decoded (width, height) in pixels.</returns>
    /// <exception cref="InvalidDataException">The container is malformed or uses an unsupported codec.</exception>
    /// <exception cref="ArgumentException"><paramref name="rgbaDestination"/> is too small.</exception>
    public static (int Width, int Height) DecodeTo(ReadOnlySpan<byte> data, Span<byte> rgbaDestination)
    {
        var container = HeifContainer.Parse(data);
        var (width, height) = container.DisplaySize();
        if (rgbaDestination.Length < (long)width * height * 4)
        {
            throw new ArgumentException($"The destination needs {(long)width * height * 4} bytes for a {width}x{height} image.", nameof(rgbaDestination));
        }
        DecodeRgba(data, container, rgbaDestination);
        return (width, height);
    }

    /// <summary>Decodes every coded image (grid tile) of the primary item, in grid order, in parallel.</summary>
    internal static HevcPicture[] DecodeCodedImages(ReadOnlySpan<byte> data, HeifContainer container)
    {
        var tiles = container.Tiles;
        var payloads = new byte[tiles.Count][];
        for (var i = 0; i < tiles.Count; i++)
        {
            var length = Isobmff.ItemContainer.GetItemLength(tiles[i]);
            if (length > data.Length) throw new InvalidDataException("An image item is larger than the file.");
            payloads[i] = new byte[length];
            container.Items.Resolve(data, tiles[i], payloads[i]);
        }

        var pictures = new HevcPicture[tiles.Count];
        var configs = container.TileConfigs;
        if (tiles.Count == 1)
        {
            pictures[0] = HevcImageDecoder.Decode(configs[0], payloads[0]);
            return pictures;
        }
        try
        {
            Parallel.For(0, tiles.Count, i => pictures[i] = HevcImageDecoder.Decode(configs[i], payloads[i]));
        }
        catch (AggregateException e) when (e.InnerException is InvalidDataException inner)
        {
            throw new InvalidDataException(inner.Message, inner);
        }
        return pictures;
    }

    /// <summary>Checks the tiles agree with each other and cover the canvas; returns the tile size.</summary>
    private static (int Width, int Height) ValidateTiles(HeifContainer container, HevcPicture[] pictures)
    {
        var first = pictures[0];
        int tileW = first.OutputWidth, tileH = first.OutputHeight;
        foreach (var p in pictures)
        {
            if (p.OutputWidth != tileW || p.OutputHeight != tileH || p.BitDepthY != first.BitDepthY ||
                p.BitDepthC != first.BitDepthC || p.ChromaFormatIdc != first.ChromaFormatIdc)
            {
                throw new InvalidDataException("The grid tiles differ in size, bit depth or chroma format.");
            }
        }
        if (first.BitDepthY != first.BitDepthC) throw new InvalidDataException("Different luma and chroma bit depths are not supported.");
        if ((long)tileW * container.GridColumns < container.CanvasWidth || (long)tileH * container.GridRows < container.CanvasHeight)
        {
            throw new InvalidDataException("The grid tiles do not cover the output image.");
        }
        return (tileW, tileH);
    }

    /// <summary>
    /// The <c>matrix_coefficients</c> the colour conversion uses: the <c>colr</c> / nclx box's, else the SPS VUI's
    /// (2, unspecified, when neither signals one).
    /// </summary>
    internal static int MatrixCoefficients(HeifContainer container) =>
        container.NclxColour is { } nclx ? nclx.MatrixCoefficients : container.TileConfigs[0].ParseSps().MatrixCoefficients;

    /// <summary>Decodes, converts and transforms the primary image into <paramref name="destination"/> (tightly packed
    /// RGBA8, display size); returns the bit depth.</summary>
    internal static int DecodeRgba(ReadOnlySpan<byte> data, HeifContainer container, Span<byte> destination) =>
        DecodeCanvas(data, container, destination, gray: false);

    /// <summary>
    /// Decodes the primary image's luma into <paramref name="destination"/> (tightly packed, one byte per pixel,
    /// display size) with <see cref="HeifColor.ToGray"/>; returns the bit depth. Not for an identity matrix
    /// (<see cref="MatrixCoefficients"/> 0), whose Y plane carries G, not luma.
    /// </summary>
    internal static int DecodeGray(ReadOnlySpan<byte> data, HeifContainer container, Span<byte> destination) =>
        DecodeCanvas(data, container, destination, gray: true);

    private static int DecodeCanvas(ReadOnlySpan<byte> data, HeifContainer container, Span<byte> destination, bool gray)
    {
        var pictures = DecodeCodedImages(data, container);
        var (tileW, tileH) = ValidateTiles(container, pictures);
        var first = pictures[0];
        int canvasW = container.CanvasWidth, canvasH = container.CanvasHeight;
        var bytesPerPixel = gray ? 1 : 4;

        var sps = container.TileConfigs[0].ParseSps();
        var coefficients = container.NclxColour is { } nclx
            ? HeifColor.For(nclx.MatrixCoefficients, nclx.FullRange, first.BitDepthY)
            : HeifColor.For(sps.MatrixCoefficients, sps.VideoFullRange, first.BitDepthY);
        if (gray && coefficients.Identity) throw new InvalidOperationException("An identity-matrix image has no luma plane.");

        var direct = container.Transforms.Count == 0;
        var canvas = direct ? destination : new byte[(long)canvasW * canvasH * bytesPerPixel];
        for (var i = 0; i < pictures.Length; i++)
        {
            var p = pictures[i];
            var x0 = i % container.GridColumns * tileW;
            var y0 = i / container.GridColumns * tileH;
            if (x0 >= canvasW || y0 >= canvasH) continue;
            var w = Math.Min(tileW, canvasW - x0);
            var h = Math.Min(tileH, canvasH - y0);
            var lumaOffset = p.CropTop * p.Width + p.CropLeft;
            var target = canvas[(int)(((long)y0 * canvasW + x0) * bytesPerPixel)..];
            if (gray)
            {
                HeifColor.ToGray(p.Planes[0].AsSpan(lumaOffset), p.Width, w, h, coefficients, target, canvasW);
            }
            else
            {
                var chromaOffset = (p.CropTop / p.SubHeightC) * p.ChromaWidth + p.CropLeft / p.SubWidthC;
                HeifColor.ToRgba420(p.Planes[0].AsSpan(lumaOffset), p.Width, p.Planes[1].AsSpan(chromaOffset), p.Planes[2].AsSpan(chromaOffset),
                    p.ChromaWidth, w, h, coefficients, target, canvasW * 4);
            }
        }

        if (!direct) ApplyTransforms(container, canvas, canvasW, canvasH, destination, bytesPerPixel);
        return first.BitDepthY;
    }

    /// <summary>Applies clap / irot / imir in association order (23008-12 §6.5.1) to pixels of
    /// <paramref name="bytesPerPixel"/> bytes.</summary>
    private static void ApplyTransforms(HeifContainer container, Span<byte> source, int width, int height, Span<byte> destination, int bytesPerPixel)
    {
        var bpp = bytesPerPixel;
        var current = source.ToArray();
        foreach (var t in container.Transforms)
        {
            byte[] next;
            int nextW = width, nextH = height;
            switch (t.Type)
            {
                case HeifContainer.PropClap:
                {
                    var (x0, y0, w, h) = HeifContainer.ClapRectangle(t.Clap!, width, height);
                    next = new byte[(long)w * h * bpp];
                    for (var y = 0; y < h; y++)
                    {
                        current.AsSpan(((y0 + y) * width + x0) * bpp, w * bpp).CopyTo(next.AsSpan(y * w * bpp));
                    }
                    nextW = w;
                    nextH = h;
                    break;
                }
                case HeifContainer.PropIrot:
                {
                    var quarterTurns = t.A; // anti-clockwise
                    if (quarterTurns == 0) continue;
                    if ((quarterTurns & 1) != 0) (nextW, nextH) = (height, width);
                    next = new byte[(long)nextW * nextH * bpp];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var (dx, dy) = quarterTurns switch
                            {
                                1 => (y, width - 1 - x),          // 90° anti-clockwise
                                2 => (width - 1 - x, height - 1 - y),
                                _ => (height - 1 - y, x),         // 270° anti-clockwise
                            };
                            current.AsSpan((y * width + x) * bpp, bpp).CopyTo(next.AsSpan((dy * nextW + dx) * bpp));
                        }
                    }
                    break;
                }
                default: // imir: axis 0 = vertical axis (left ↔ right), 1 = horizontal axis (top ↔ bottom)
                {
                    next = new byte[(long)width * height * bpp];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var (dx, dy) = t.A == 0 ? (width - 1 - x, y) : (x, height - 1 - y);
                            current.AsSpan((y * width + x) * bpp, bpp).CopyTo(next.AsSpan((dy * width + dx) * bpp));
                        }
                    }
                    break;
                }
            }
            current = next;
            width = nextW;
            height = nextH;
        }
        current.AsSpan().CopyTo(destination);
    }

    /// <summary>
    /// The primary image's Y, Cb and Cr planes on the canvas, before colour conversion and transforms (for the
    /// byte-exact oracle tests): each tile's conformance-window output pasted at its grid position and cropped
    /// to the grid's output size.
    /// </summary>
    internal static (ushort[] Y, ushort[] Cb, ushort[] Cr, int Width, int Height, int BitDepth) DecodeYuv(ReadOnlySpan<byte> data)
    {
        var container = HeifContainer.Parse(data);
        var pictures = DecodeCodedImages(data, container);
        var (tileW, tileH) = ValidateTiles(container, pictures);
        int w = container.CanvasWidth, h = container.CanvasHeight;
        int cw = (w + 1) >> 1, ch = (h + 1) >> 1;
        var planes = new[] { new ushort[w * h], new ushort[cw * ch], new ushort[cw * ch] };
        for (var i = 0; i < pictures.Length; i++)
        {
            var p = pictures[i];
            for (var c = 0; c < 3; c++)
            {
                var shift = c == 0 ? 0 : 1;
                int canvasW = c == 0 ? w : cw, canvasH = c == 0 ? h : ch;
                var x0 = (i % container.GridColumns * tileW) >> shift;
                var y0 = (i / container.GridColumns * tileH) >> shift;
                var srcStride = p.PlaneWidth(c);
                var srcX = p.CropLeft >> shift;
                var srcY = p.CropTop >> shift;
                var copyW = Math.Min(tileW >> shift, canvasW - x0);
                var copyH = Math.Min(tileH >> shift, canvasH - y0);
                for (var y = 0; y < copyH; y++)
                {
                    p.Planes[c].AsSpan((srcY + y) * srcStride + srcX, copyW).CopyTo(planes[c].AsSpan((y0 + y) * canvasW + x0));
                }
            }
        }
        return (planes[0], planes[1], planes[2], w, h, pictures[0].BitDepthY);
    }
}
