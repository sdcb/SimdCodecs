using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for TIFF, bridging <see cref="TiffReader"/>
/// into the <c>Sdcb.SimdCodecs</c> facade.
/// <para>
/// Fidelity mapping (<see cref="TryDecode"/>, first page of multi-page files -- only that page is
/// decoded, so a later page this reader cannot handle does not fail the file):
/// 8/16-bit unsigned-integer and 32-bit IEEE-float samples map directly —
/// <see cref="TiffPage.Pixels"/> is already interleaved, row-major, host byte
/// order — 1/2/4-bit grey arrives from the reader expanded to 8 bits, and 16-bit IEEE-float
/// (half) widens losslessly to <see cref="SampleFormat.Float32"/>. MinIsWhite grey (what fax and
/// most bilevel scans use) is inverted to MinIsBlack. Palette, CMYK and (non-JPEG and JPEG) YCbCr pages
/// are converted to RGB, associated alpha is un-premultiplied and ExtraSamples choose the alpha channel,
/// by the arithmetic <see cref="TiffColor"/> defines. Layouts <see cref="IDecodedImage"/> cannot
/// represent (CIELab, signed or 32-bit-uint samples) return false rather than decode wrongly.
/// </para>
/// <para>
/// Colour meaning: float pages surface <see cref="TransferFunction.Linear"/> +
/// <see cref="FloatSemantics.SceneReferred"/> on
/// <see cref="IDecodedImage.ColorEncoding"/> (the float-TIFF scene-linear
/// convention — see <see cref="TiffPageOptions"/>); integer pages keep the sRGB
/// assumption. SMin/SMax stay available via <see cref="TiffReader"/> for
/// consumers that stretch.
/// </para>
/// <para>
/// 1/2/4-bit grey is expanded to fill 8 bits (the maximum code becomes 255) and reports
/// <see cref="IDecodedImage.BitDepth"/> 1/2/4; 16-bit float reports 16. Scaling is not supported (a
/// <see cref="DecodeOptions.ScaleDenominator"/> other than 1 throws <see cref="NotSupportedException"/>); a
/// <see cref="DecodeOptions.Region"/> reads only the strips or tiles it intersects. Cancellation is checked per strip,
/// per row of tiles and per page.
/// </para>
/// </summary>
public sealed class TiffImageDecoder : IMultiFrameImageDecoder
{
    /// <summary>The registration for <c>SimdCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Tiff,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        TryOpenFrames = TryOpenFrames,
    };

    /// <inheritdoc />
    public static int SignatureLength => 4;

    /// <inheritdoc />
    /// <remarks>Classic TIFF (<c>II*\0</c>, <c>MM\0*</c>) and BigTIFF (<c>II+\0</c>, <c>MM\0+</c>: version 43).</remarks>
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 4 &&
        (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8)
            || header[..4].SequenceEqual("II+\0"u8) || header[..4].SequenceEqual("MM\0+"u8));

    /// <inheritdoc />
    /// <remarks>TIFF geometry lives in the IFD chain, not at a fixed header offset, so this parses the
    /// first page's directory (no strip or tile is decoded) and walks the chain to count the pages.</remarks>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        if (!TryReadPageInfo(data, 0, out info)) return false;
        info = info with { FrameCount = TiffReader.CountPages(data) };
        return true;
    }

    /// <inheritdoc />
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator) =>
        ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        TryDecodePage(data, 0, options, cancellationToken, out image);
    /// <inheritdoc />
    /// <remarks>Every page is a frame, with its own size, layout and orientation; a page this adapter
    /// refuses (palette, CMYK, ...) is listed but fails to decode, without stopping the others. Pages are
    /// independent, so any order costs the same: the IFD chain's next pointers up to that page, then its
    /// directory and segments.</remarks>
    public static bool TryOpenFrames(ReadOnlyMemory<byte> data, [NotNullWhen(true)] out IImageFrameReader? reader)
    {
        reader = null;
        int pages;
        try
        {
            pages = TiffReader.CountPages(data.Span);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (pages == 0) return false;
        reader = new TiffFrameReader(data, pages);
        return true;
    }

    /// <summary>The layout <see cref="TryDecodePage"/> gives page <paramref name="pageIndex"/>, from its directory alone.</summary>
    internal static bool TryReadPageInfo(ReadOnlySpan<byte> data, int pageIndex, out ImageInfo info)
    {
        info = default;
        try
        {
            var layout = TiffReader.ReadLayout(data, pageIndex);
            if (!TiffColor.TryCreate(layout, out var plan))
                return false;
            info = new ImageInfo(layout.Width, layout.Height, plan.OutChannels, plan.OutFormat)
            {
                Format = ImageFormat.Tiff,
                BitDepth = layout.Photometric == TiffPhotometric.Palette ? (layout.StoredBitsPerSample == 16 ? 16 : 8) : layout.StoredBitsPerSample,
                Orientation = (ImageOrientation)layout.Orientation,
                ColorEncoding = ColorOf(layout),
                Resolution = ResolutionOf(layout),
            };
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>TIFF has no scaling. Integer pages are decoded strip by strip (or by row of tiles) straight into
    /// <paramref name="destination"/>: only the segments a <see cref="DecodeOptions.Region"/> intersects are read, and
    /// no raster of the page exists, only one band of segments. The pixels are exactly those of
    /// <see cref="ImageDecoderFallback.TryDecodeInto"/> (decode, then <see cref="PixelConverter"/>), which float pages
    /// still take.</remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        TryDecodePageInto(data, 0, destination, stride, format, options, cancellationToken);

    /// <summary>Page <paramref name="pageIndex"/> as <see cref="TryDecodeInto"/> writes the first.</summary>
    internal static bool TryDecodePageInto(ReadOnlySpan<byte> data, int pageIndex, Span<byte> destination, int stride,
        PixelFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TiffPageLayout layout;
        try
        {
            layout = TiffReader.ReadLayout(data, pageIndex);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (!TiffColor.TryCreate(layout, out var plan))
            return false;
        options?.Validate(layout.Width, layout.Height, supportsScale: false);
        if (plan.OutFormat == SampleFormat.Float32)
        {
            // PixelConverter defines float output through the decoded image; keep exactly that.
            if (!TryDecodePage(data, pageIndex, options is null ? null : options with { Pool = null }, cancellationToken, out var image))
                return false;
            using (image)
                return PixelConverter.TryConvert(image, destination, stride, format);
        }

        var rect = options?.Region ?? new PixelRect(0, 0, layout.Width, layout.Height);
        if (!PixelConverter.TryGetLayout(rect.Width, rect.Height, format, stride, destination.Length, out var rowStride))
            return false;
        var bandRows = Math.Min(layout.Geometry.SegmentHeight, rect.Height);
        var bandStride = rect.Width * plan.OutBytesPerPixel;
        var band = ArrayPool<byte>.Shared.Rent(bandRows * bandStride);
        try
        {
            var first = layout.Geometry.DownIndexForRow(rect.Y);
            var last = layout.Geometry.DownIndexForRow(rect.Y + rect.Height - 1);
            for (var down = first; down <= last; down++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (y0, rows) = BandRows(layout, rect, down);
                DecodeBand(data, layout, plan, rect, down, band.AsSpan(0, rows * bandStride), bandStride);
                for (var r = 0; r < rows; r++)
                {
                    PixelConverter.ConvertRow(band.AsSpan(r * bandStride, bandStride), plan.OutChannels, plan.OutFormat,
                        destination.Slice((y0 - rect.Y + r) * rowStride), format, rect.Width);
                }
            }
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(band);
        }
    }

    /// <summary>The first image row of segment row <paramref name="down"/> inside <paramref name="rect"/>, and how many there are.</summary>
    private static (int Y0, int Rows) BandRows(TiffPageLayout layout, PixelRect rect, int down)
    {
        var g = layout.Geometry;
        var y0 = Math.Max(rect.Y, g.FirstRow(down));
        var y1 = Math.Min(rect.Y + rect.Height, g.FirstRow(down) + g.RowCount(down));
        return (y0, y1 - y0);
    }

    /// <summary>
    /// Decodes the strips or tiles of segment row <paramref name="down"/> that intersect <paramref name="rect"/> and
    /// converts their pixels inside it into <paramref name="band"/>: the band's rows are the image rows
    /// <see cref="BandRows"/> gives, at the rectangle's width. Segments are read exactly as
    /// <see cref="TiffReader.ReadSegment(ReadOnlySpan{byte}, TiffPageLayout, int, Span{byte})"/> reads them, which
    /// equals the same rectangle of the whole page; a chunky strip the file does not list (a short strip list, which
    /// the whole-page read tolerates) is left as zeros, as the whole-page read leaves it.
    /// </summary>
    private static void DecodeBand(ReadOnlySpan<byte> data, TiffPageLayout layout, TiffColor plan, PixelRect rect,
        int down, Span<byte> band, int bandStride)
    {
        var g = layout.Geometry;
        var (y0, rows) = BandRows(layout, rect, down);
        var firstAcross = g.AcrossIndexForColumn(rect.X);
        var lastAcross = g.AcrossIndexForColumn(rect.X + rect.Width - 1);
        var outBpp = plan.OutBytesPerPixel;
        var inBpp = plan.InBytesPerPixel;
        byte[]? segment = null;
        try
        {
            for (var across = firstAcross; across <= lastAcross; across++)
            {
                var index = (down * g.SegmentsAcross) + across;
                var seg = layout.SegmentRectangle(index);
                var x0 = Math.Max(rect.X, seg.X);
                var x1 = Math.Min(rect.X + rect.Width, seg.X + seg.Width);
                if (!layout.IsTiled && !layout.IsPlanar && index >= layout.Offsets.Length)
                {
                    for (var r = 0; r < rows; r++)
                        band.Slice((r * bandStride) + ((x0 - rect.X) * outBpp), (x1 - x0) * outBpp).Clear();
                    continue;
                }
                var need = layout.SegmentBytes(index);
                if (segment is null || segment.Length < need)
                {
                    if (segment is not null) ArrayPool<byte>.Shared.Return(segment);
                    segment = ArrayPool<byte>.Shared.Rent(need);
                }
                TiffReader.ReadSegment(data, layout, index, segment);
                var segStride = seg.Width * inBpp;
                for (var r = 0; r < rows; r++)
                {
                    var source = segment.AsSpan(((y0 - seg.Y + r) * segStride) + ((x0 - seg.X) * inBpp), (x1 - x0) * inBpp);
                    plan.ConvertRow(source, band.Slice((r * bandStride) + ((x0 - rect.X) * outBpp), (x1 - x0) * outBpp), x1 - x0);
                }
            }
        }
        finally
        {
            if (segment is not null) ArrayPool<byte>.Shared.Return(segment);
        }
    }

    /// <inheritdoc />
    /// <remarks>The ICC profile (tag 34675), the XMP packet (tag 700, raw bytes) and the Exif block of the first page.
    /// A TIFF page's own IFD is the Exif IFD0, so the block is assembled from the file, in host byte order: the page's
    /// descriptive tags (everything but the tags that describe image data, XMP and ICC) as IFD0, plus the Exif IFD
    /// (tag 34665) with its Interoperability IFD and the GPS IFD (tag 34853). A page with neither an Exif nor a GPS IFD
    /// has no Exif block; entries of unknown type or outside the file are left out.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = null;
        try
        {
            metadata = MetadataOf(data, TiffReader.ReadLayout(data, 0));
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>This reader does not extract thumbnails: always false.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    /// <summary>Page <paramref name="pageIndex"/> as <see cref="TryDecode"/> presents the first.</summary>
    internal static bool TryDecodePage(ReadOnlySpan<byte> data, int pageIndex, DecodeOptions? options,
        CancellationToken cancellationToken, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        cancellationToken.ThrowIfCancellationRequested();

        TiffPageLayout layout;
        TiffColor plan;
        try
        {
            layout = TiffReader.ReadLayout(data, pageIndex);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (!TiffColor.TryCreate(layout, out plan))
            return false;
        // Check the options against the page before any strip is decoded (outside the try: they throw).
        options?.Validate(layout.Width, layout.Height, supportsScale: false);

        ImageMetadata? metadata;
        try
        {
            metadata = MetadataOf(data, layout);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            metadata = null; // metadata never fails a decode
        }
        if (metadata is { IsEmpty: true })
            metadata = null;

        if (options?.Region is { } region)
        {
            // Only the strips or tiles the region intersects are read, decoded and converted, straight into the
            // result: the same pixels as decoding the page whole and cropping it.
            var result = RasterImage.Create(region.Width, region.Height, plan.OutChannels, plan.OutFormat, options.Pool,
                ColorOf(layout), BitDepthOf(layout, plan), metadata);
            try
            {
                var target = result.WritablePixels;
                var bandStride = region.Width * plan.OutBytesPerPixel;
                var first = layout.Geometry.DownIndexForRow(region.Y);
                var last = layout.Geometry.DownIndexForRow(region.Y + region.Height - 1);
                for (var down = first; down <= last; down++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (y0, rows) = BandRows(layout, region, down);
                    DecodeBand(data, layout, plan, region, down,
                        target.Slice((y0 - region.Y) * bandStride, rows * bandStride), bandStride);
                }
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                result.Dispose();
                return false;
            }
            catch
            {
                result.Dispose();
                throw;
            }
            image = result;
            return true;
        }

        TiffPage page;
        try
        {
            page = TiffReader.ReadPage(data, pageIndex, cancellationToken);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        byte[] pixels;
        if (plan.IsIdentity)
        {
            pixels = page.Pixels;
        }
        else
        {
            pixels = new byte[(long)page.Width * page.Height * plan.OutBytesPerPixel];
            var inRow = page.Width * plan.InBytesPerPixel;
            var outRow = page.Width * plan.OutBytesPerPixel;
            for (var y = 0; y < page.Height; y++)
                plan.ConvertRow(page.Pixels.AsSpan(y * inRow, inRow), pixels.AsSpan(y * outRow, outRow), page.Width);
        }

        var raster = RasterImage.Wrap(page.Width, page.Height, plan.OutChannels, plan.OutFormat, pixels,
            ColorOf(layout), BitDepthOf(layout, plan), metadata);
        image = DecodeOptions.Apply(raster, options);
        return true;
    }

    /// <summary>
    /// Real precision below the output container, or 0 for the container's full width: 1/2/4-bit grey (expanded to
    /// fill 8 bits) and 16-bit half floats. Palette output is the colour map's precision, never the index width.
    /// </summary>
    private static int BitDepthOf(TiffPageLayout layout, TiffColor plan)
    {
        if (layout.Photometric == TiffPhotometric.Palette) return 0;
        var fullBits = plan.OutFormat switch { SampleFormat.UInt8 => 8, SampleFormat.UInt16 => 16, _ => 32 };
        return layout.StoredBitsPerSample > 0 && layout.StoredBitsPerSample < fullBits ? layout.StoredBitsPerSample : 0;
    }
    /// <summary>The ICC profile, XMP packet and Exif block of the page <paramref name="layout"/> describes.</summary>
    private static ImageMetadata MetadataOf(ReadOnlySpan<byte> data, TiffPageLayout layout) => new()
    {
        Icc = layout.IccProfile,
        Xmp = layout.Xmp,
        Exif = TiffExif.Extract(data, layout.Format, layout.IfdOffset),
    };

    // A float page is scene-linear, which TIFF itself says; any other page has no signalling of its own, so
    // its ICC profile's primaries are reported when H.273 names them.
    private static ColorEncoding ColorOf(TiffPageLayout layout) =>
        layout.SampleFormat == TiffSampleFormat.IeeeFloat
            ? new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.SceneReferred }
            : IccPrimaries.Encoding(layout.IccProfile) ?? ColorEncoding.AssumedSrgb;

    /// <summary>XResolution / YResolution / ResolutionUnit (tags 282 / 283 / 296); null unless both densities are present and positive.</summary>
    private static Resolution? ResolutionOf(TiffPageLayout layout)
    {
        if (!(layout.XResolution > 0) || !(layout.YResolution > 0)
            || double.IsInfinity(layout.XResolution) || double.IsInfinity(layout.YResolution))
            return null;
        var unit = layout.ResolutionUnit switch
        {
            1 => ResolutionUnit.AspectRatio,
            3 => ResolutionUnit.PerCentimeter,
            _ => ResolutionUnit.PerInch,
        };
        return new Resolution(layout.XResolution, layout.YResolution, unit);
    }
}
