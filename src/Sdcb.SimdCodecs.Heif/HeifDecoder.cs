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
/// use the item properties instead); the Exif block in the decoded metadata has it reset to 1 to match the pixels.
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
        var bitDepth = DecodeInto(data, container, pixels, width * 4, PixelFormat.Rgba32);
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
        DecodeInto(data, container, rgbaDestination, width * 4, PixelFormat.Rgba32);
        return (width, height);
    }

    /// <summary>The payload (length-prefixed NAL units) of every coded image of the primary item, in grid order.</summary>
    private static byte[][] ResolvePayloads(ReadOnlySpan<byte> data, HeifContainer container)
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
        return payloads;
    }

    /// <summary>
    /// Runs <paramref name="body"/> for every index, in parallel when there is more than one. A failure surfaces as
    /// the exception the body threw (an <see cref="InvalidDataException"/> when any tile threw one), never as an
    /// <see cref="AggregateException"/>, so the parallel path fails the way the serial one does.
    /// </summary>
    internal static void ForEachTile(int count, Action<int> body, CancellationToken cancellationToken = default)
    {
        if (count == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            body(0);
            return;
        }
        try
        {
            Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellationToken }, body);
        }
        catch (AggregateException e)
        {
            var inner = e.Flatten().InnerExceptions;
            foreach (var exception in inner)
            {
                if (exception is InvalidDataException invalid) throw new InvalidDataException(invalid.Message, invalid);
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner[0]).Throw();
            throw;
        }
    }

    /// <summary>Decodes every coded image (grid tile) of the primary item, in grid order, in parallel, keeping the planes.</summary>
    internal static HevcPicture[] DecodeCodedImages(ReadOnlySpan<byte> data, HeifContainer container)
    {
        var payloads = ResolvePayloads(data, container);
        var configs = container.TileConfigs;
        var pictures = new HevcPicture[payloads.Length];
        ForEachTile(payloads.Length, i => pictures[i] = HevcImageDecoder.Decode(configs[i], payloads[i], parallel: payloads.Length == 1));
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

    /// <summary>The size of a coded image's output (its conformance window), checked to cover the canvas with the grid.</summary>
    private static (int Width, int Height) TileSize(HeifContainer container)
    {
        var sps = container.TileConfigs[0].ParseSps();
        if (sps.BitDepthY != sps.BitDepthC) throw new InvalidDataException("Different luma and chroma bit depths are not supported.");
        int tileW = sps.Width - sps.ConfLeft - sps.ConfRight, tileH = sps.Height - sps.ConfTop - sps.ConfBottom;
        if ((long)tileW * container.GridColumns < container.CanvasWidth || (long)tileH * container.GridRows < container.CanvasHeight)
        {
            throw new InvalidDataException("The grid tiles do not cover the output image.");
        }
        return (tileW, tileH);
    }

    /// <summary>
    /// True when <see cref="DecodeInto"/> can box by <paramref name="factor"/> while it writes the tiles: every tile that
    /// reaches the output (or the scaled <paramref name="region"/>) covers whole boxes, so the tiles' seams, carried
    /// through clap / irot / imir to display coordinates, fall on multiples of the factor
    /// (<see cref="HeifCanvas.BoxesAlignWithTiles"/>). Always true for a single coded image and for a factor of 1.
    /// </summary>
    /// <exception cref="InvalidDataException">The container's tiles are malformed.</exception>
    /// <exception cref="ArgumentException"><paramref name="region"/> does not lie inside the scaled image.</exception>
    internal static unsafe bool ScalesNatively(HeifContainer container, int factor, PixelRect? region)
    {
        if (factor <= 1) return true;
        var (tileW, tileH) = TileSize(container);
        // Geometry only: a canvas over no buffer, through which nothing is written.
        HeifColor.Coefficients none = default;
        var canvas = new HeifCanvas(container, null, long.MaxValue, int.MaxValue, PixelFormat.Rgb24, none, default, region,
            factor, PixelFormat.Rgb24);
        return canvas.BoxesAlignWithTiles(container.Tiles.Count, container.GridColumns, tileW, tileH);
    }

    /// <summary>
    /// Decodes the primary image, display-oriented, into <paramref name="destination"/> in <paramref name="format"/>
    /// with <paramref name="stride"/> bytes per row; returns the coded bit depth. Each tile is converted into the
    /// output as soon as it is decoded and its planes go back to the pool (<see cref="HeifCanvas"/>), so the only
    /// image-sized buffer is the destination. A 16-bit <paramref name="format"/> takes the 16-bit conversion
    /// (<see cref="HeifColor.For16"/>); an 8-bit one the frozen 8-bit conversion, straight from the coded samples.
    /// Cancellation is checked per tile and per band of output rows.
    /// <para>
    /// With <paramref name="factor"/> &gt; 1 the output is the box filter (<see cref="Abstractions.CodecSupport.BoxDownscaler"/>)
    /// of the Rgb24 image (Rgb48 above 8 bits) by that factor, or its <paramref name="region"/> in scaled coordinates,
    /// converted to <paramref name="format"/>: each tile boxes the pixels it converts, so no full-size image exists. It
    /// requires <see cref="ScalesNatively"/>.
    /// </para>
    /// </summary>
    internal static unsafe int DecodeInto(ReadOnlySpan<byte> data, HeifContainer container, Span<byte> destination, int stride,
        PixelFormat format, CancellationToken cancellationToken = default, PixelRect? region = null, int factor = 1)
    {
        var payloads = ResolvePayloads(data, container);
        var configs = container.TileConfigs;
        var sps = configs[0].ParseSps();
        var (tileW, tileH) = TileSize(container);
        var (matrix, fullRange) = container.NclxColour is { } nclx
            ? (nclx.MatrixCoefficients, nclx.FullRange)
            : (sps.MatrixCoefficients, sps.VideoFullRange);
        // Scaled, the boxes average TryDecode's layout, so the conversion is the one for it.
        var boxFormat = sps.BitDepthY > 8 ? PixelFormat.Rgb48 : PixelFormat.Rgb24;
        var coefficients = (factor > 1 ? boxFormat : format).Sample == SampleFormat.UInt16
            ? HeifColor.For16(matrix, fullRange, sps.BitDepthY)
            : HeifColor.For(matrix, fullRange, sps.BitDepthY);

        fixed (byte* pointer = destination)
        {
            var canvas = new HeifCanvas(container, pointer, destination.Length, stride, format, coefficients, cancellationToken, region,
                factor, boxFormat);
            var columns = container.GridColumns;
            if (!canvas.BoxesAlignWithTiles(payloads.Length, columns, tileW, tileH))
                throw new InvalidOperationException("The boxes do not line up with the tiles: check ScalesNatively first.");
            ForEachTile(payloads.Length, i =>
            {
                // A tile none of whose pixels reach the output (a region of a grid) is not decoded at all.
                if (!canvas.Intersects(i % columns * tileW, i / columns * tileH, tileW, tileH))
                    return;
                // A single coded image is decoded alone, so its rows (WPP wavefront, filters, colour) go parallel instead.
                var alone = payloads.Length == 1;
                var picture = HevcImageDecoder.Decode(configs[i], payloads[i], pooled: true, parallel: alone);
                try
                {
                    if (picture.OutputWidth != tileW || picture.OutputHeight != tileH || picture.BitDepthY != sps.BitDepthY ||
                        picture.BitDepthC != sps.BitDepthC || picture.ChromaFormatIdc != sps.ChromaFormatIdc)
                    {
                        throw new InvalidDataException("The grid tiles differ in size, bit depth or chroma format.");
                    }
                    if (!HeifDiagnostics.Skips(HeifAblation.SkipColor))
                    {
                        canvas.WriteTile(picture, i % columns * tileW, i / columns * tileH, tileW, tileH, parallel: alone);
                    }
                }
                finally
                {
                    picture.Release();
                }
            }, cancellationToken);
        }
        return sps.BitDepthY;
    }

    /// <summary>
    /// The primary image's Y, Cb and Cr planes on the canvas, before colour conversion and transforms (for the
    /// byte-exact oracle tests): each tile's conformance-window output pasted at its grid position and cropped
    /// to the grid's output size.
    /// </summary>
    internal static (ushort[] Y, ushort[] Cb, ushort[] Cr, int Width, int Height, int BitDepth) DecodeYuv(ReadOnlySpan<byte> data)
    {
        var container = HeifContainer.Parse(data);
        return DecodeYuv(data, container, default);
    }

    /// <inheritdoc cref="DecodeYuv(ReadOnlySpan{byte})"/>
    internal static (ushort[] Y, ushort[] Cb, ushort[] Cr, int Width, int Height, int BitDepth) DecodeYuv(ReadOnlySpan<byte> data,
        HeifContainer container, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pictures = DecodeCodedImages(data, container);
        cancellationToken.ThrowIfCancellationRequested();
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
