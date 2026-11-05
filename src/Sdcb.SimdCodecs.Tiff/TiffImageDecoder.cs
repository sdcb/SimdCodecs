using System;
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
/// <see cref="DecodeOptions.Region"/> decodes the whole page and crops it. Cancellation is checked per strip,
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
                ColorEncoding = ColorOf(layout.SampleFormat),
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
    /// <remarks>TIFF has no scaling. The result of the generic decode-then-convert path
    /// (<see cref="ImageDecoderFallback.TryDecodeInto"/>); a float page is converted by
    /// <see cref="PixelConverter"/> like any other. A <see cref="DecodeOptions.Region"/> decodes the whole page
    /// and crops it.</remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);

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

        ImageMetadata? metadata;
        try
        {
            metadata = MetadataOf(data, layout);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            metadata = null; // the page decoded, so this cannot happen; metadata never fails a decode
        }
        if (metadata is { IsEmpty: true })
            metadata = null;
        var raster = RasterImage.Wrap(page.Width, page.Height, plan.OutChannels, plan.OutFormat, pixels,
            ColorOf(layout.SampleFormat), BitDepthOf(layout, plan), metadata);
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

    private static ColorEncoding ColorOf(TiffSampleFormat sampleFormat) =>
        sampleFormat == TiffSampleFormat.IeeeFloat
            ? new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.SceneReferred }
            : ColorEncoding.AssumedSrgb;

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
