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
/// most bilevel scans use) is inverted to MinIsBlack. Layouts <see cref="IDecodedImage"/> cannot
/// represent (palette / CMYK / YCbCr photometrics, signed or 32-bit-uint samples) return false
/// rather than decode wrongly.
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
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 4 &&
        (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8));

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
            if (!TryMapLayout(layout.Photometric, layout.SamplesPerPixel, layout.BitsPerSample, layout.SampleFormat, out var format))
                return false;
            info = new ImageInfo(layout.Width, layout.Height, layout.SamplesPerPixel, format)
            {
                Format = ImageFormat.Tiff,
                BitDepth = layout.StoredBitsPerSample,
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
    /// <remarks>The ICC profile (tag 34675) and the XMP packet (tag 700, raw bytes) of the first page.
    /// <see cref="ImageMetadata.Exif"/> is always empty: a TIFF file is itself a TIFF structure, so its tags
    /// (and the Exif IFD behind tag 34665) are read from the file by <c>Sdcb.SimdCodecs.Exif</c> directly rather than
    /// repackaged as an Exif block.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = null;
        try
        {
            var layout = TiffReader.ReadLayout(data, 0);
            metadata = new ImageMetadata { Xmp = layout.Xmp, Icc = layout.IccProfile };
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

        if (options is not null)
        {
            // Check the options against the page before any strip is decoded (outside the try: they throw).
            TiffPageLayout layout;
            try
            {
                layout = TiffReader.ReadLayout(data, pageIndex);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
            {
                return false;
            }
            if (!TryMapLayout(layout.Photometric, layout.SamplesPerPixel, layout.BitsPerSample, layout.SampleFormat, out _))
                return false;
            options.Validate(layout.Width, layout.Height, supportsScale: false);
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

        if (!TryMapLayout(page.Photometric, page.SamplesPerPixel, page.BitsPerSample, page.SampleFormat, out var format))
            return false;
        var channels = page.SamplesPerPixel;

        if (page.Photometric == TiffPhotometric.MinIsWhite)
        {
            if (page.BitsPerSample == 8)
            {
                foreach (ref var b in page.Pixels.AsSpan()) b = (byte)~b;
            }
            else if (page.BitsPerSample == 16)
            {
                foreach (ref var v in MemoryMarshal.Cast<byte, ushort>(page.Pixels.AsSpan())) v = (ushort)~v;
            }
        }

        var color = ColorOf(page.SampleFormat);
        // Real precision below the container: 1/2/4-bit grey (expanded to fill 8 bits) and 16-bit half floats.
        var fullBits = format switch { SampleFormat.UInt8 => 8, SampleFormat.UInt16 => 16, _ => 32 };
        var bitDepth = page.StoredBitsPerSample > 0 && page.StoredBitsPerSample < fullBits ? page.StoredBitsPerSample : 0;

        var metadata = page.IccProfile is null ? null : new ImageMetadata { Icc = page.IccProfile };
        RasterImage raster;
        if (page.BitsPerSample == 16 && page.SampleFormat == TiffSampleFormat.IeeeFloat)
        {
            // 16-bit IEEE float (half): widen losslessly to Float32.
            var halves = MemoryMarshal.Cast<byte, Half>(page.Pixels);
            var widened = new float[halves.Length];
            for (var i = 0; i < halves.Length; i++) widened[i] = (float)halves[i];
            raster = RasterImage.Wrap(page.Width, page.Height, channels, SampleFormat.Float32,
                MemoryMarshal.AsBytes(widened.AsSpan()).ToArray(), color, bitDepth, metadata);
        }
        else
        {
            raster = RasterImage.Wrap(page.Width, page.Height, channels, format, page.Pixels, color, bitDepth, metadata);
        }
        image = DecodeOptions.Apply(raster, options);
        return true;
    }

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

    /// <summary>
    /// The sample format <see cref="TryDecode"/> presents a page as, or false for a page it refuses;
    /// <paramref name="bitsPerSample"/> is the decoded width (8 for an expanded 1/2/4-bit page). Shared by
    /// <see cref="TryReadInfo"/> and the decode, so the two cannot disagree.
    /// </summary>
    private static bool TryMapLayout(TiffPhotometric photometric, int channels, int bitsPerSample,
        TiffSampleFormat sampleFormat, out SampleFormat format)
    {
        format = default;
        // Only photometrics whose samples IDecodedImage represents directly: gray
        // (MinIsBlack, or MinIsWhite once inverted) and RGB. Palette would need an
        // expansion, CMYK/YCbCr/CieLab a colour transform - meaning changes this
        // adapter refuses to make silently.
        if (photometric is not (TiffPhotometric.MinIsBlack or TiffPhotometric.MinIsWhite or TiffPhotometric.Rgb))
            return false;
        if (channels is < 1 or > 4) return false;
        // Inversion is only defined here for unsigned grey; anything else under MinIsWhite is refused.
        if (photometric == TiffPhotometric.MinIsWhite && (channels != 1 || sampleFormat != TiffSampleFormat.Uint))
            return false;

        switch (bitsPerSample, sampleFormat)
        {
            case (8, TiffSampleFormat.Uint): format = SampleFormat.UInt8; return true;
            case (16, TiffSampleFormat.Uint): format = SampleFormat.UInt16; return true;
            case (16 or 32, TiffSampleFormat.IeeeFloat): format = SampleFormat.Float32; return true;
            default: return false; // signed ints, 32-bit uint
        }
    }
}
