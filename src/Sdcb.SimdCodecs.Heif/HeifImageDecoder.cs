using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Heif;

/// <summary>
/// <see cref="IImageDecoder"/> adapter for HEIF / HEIC, bridging <see cref="HeifDecoder"/> into the
/// <c>Sdcb.SimdCodecs</c> facade. The facade does not reference this package (HEVC is patent-encumbered and
/// this package conveys no patent licence), so a consumer that installs it enables it explicitly, once, at
/// start-up: <c>SimdCodecs.Register(HeifImageDecoder.Registration)</c>.
/// <para>
/// The primary image is decoded to RGB, display-oriented: the clean aperture (<c>clap</c>), rotation
/// (<c>irot</c>) and mirror (<c>imir</c>) properties are applied, so <see cref="ImageInfo.Width"/> /
/// <see cref="ImageInfo.Height"/> are the displayed size and <see cref="ImageInfo.Orientation"/> is always
/// <see cref="ImageOrientation.Normal"/>. Alpha, depth and gain-map auxiliary images are not decoded, so the
/// raster has 3 channels. An 8-bit (Main) image decodes to 8-bit samples; a Main 10 image to 16-bit samples
/// with <see cref="IDecodedImage.BitDepth"/> 10 (the full 10 bits of precision, scaled to 0..65535 by the 16-bit
/// conversion). <see cref="TryDecodeInto"/> converts straight from the coded samples in either depth, so its 8-bit
/// formats keep the frozen 1.x conversion (one rounding from 10 bits): for a Main 10 image they can differ by one code
/// value from narrowing the 16-bit image (<c>v &gt;&gt; 8</c>), the one exception besides stored-luma grey to
/// <see cref="IImageDecoder.TryDecodeInto"/>'s "the converted <see cref="IDecodedImage.Pixels"/>" rule.
/// </para>
/// <para>
/// Metadata: the <c>Exif</c> and XMP (<c>mime</c>) items describing the primary image are passed through
/// (<see cref="TryReadMetadata"/>); its <c>thmb</c> thumbnail decodes through <see cref="TryDecodeThumbnail"/>. The
/// Exif orientation tag is not applied (the item properties are): transcode with care, or rewrite it to 1.
/// The planes (<see cref="IPlanarImageDecoder"/>) are the canvas before the transformative properties, at the coded
/// bit depth; an image with any transform has no planar output (<see cref="TryReadPlanarInfo"/> is false).
/// </para>
/// <para>
/// An ICC profile in the container is passed through as is. <see cref="IDecodedImage.ColorEncoding"/> takes
/// its primaries and transfer function from the <c>colr</c> / nclx box. A <c>colr</c> box overrides the HEVC
/// VUI, so with an ICC profile and no nclx it is <see cref="ColorEncoding.AssumedSrgb"/> (the encoding is never
/// derived from a profile; phones commonly pair a Display P3 profile with a BT.601 VUI). With no <c>colr</c>
/// box at all, the VUI colour description is used when it specifies one.
/// It is always full range: limited-range Y′CbCr is expanded by the colour conversion. An image whose transfer
/// function is PQ or HLG decodes through <see cref="TryDecode"/>, but <see cref="TryDecodeInto"/> refuses it, as
/// PNG does for cICP PQ / HLG: its code values are encoded for an HDR display.
/// </para>
/// <para>
/// Image sequences (major brand <c>msf1</c>, <c>hevc</c>, <c>hevx</c>, <c>hevm</c> or <c>hevs</c>) are recognised by
/// <see cref="CanDecode"/> but not decoded. Malformed or unsupported data makes the <c>Try</c> methods return
/// false rather than throw.
/// </para>
/// </summary>
public sealed class HeifImageDecoder : IPlanarImageDecoder
{
    /// <summary>
    /// This codec's entry for <c>SimdCodecs.Register</c> — its members bound by method group, as the facade's
    /// registry requires (see <see cref="ImageDecoderRegistration"/>). Still images only: the facade presents one
    /// as a single frame.
    /// </summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Heif,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
        TryReadPlanarInfo = TryReadPlanarInfo,
        TryDecodePlanes = TryDecodePlanes,
    };

    /// <summary>The <c>ftyp</c> box with its compatible brands; an AVIF brand can sit anywhere in the list.</summary>
    public static int SignatureLength => 64;

    /// <summary>
    /// True for an ISO-BMFF file whose <c>ftyp</c> box lists a HEIF brand (<c>heic</c>, <c>heix</c>, <c>heim</c>,
    /// <c>heis</c>, <c>hevc</c>, <c>hevx</c>, <c>mif1</c>, <c>msf1</c>) and no AVIF brand (<c>avif</c>, <c>avis</c>):
    /// AVIF files commonly list <c>mif1</c> too, and are not HEVC. The same test as <c>SimdCodecs.DetectFormat</c>.
    /// </summary>
    public static bool CanDecode(ReadOnlySpan<byte> header)
    {
        if (header.Length < 16 || !header[4..8].SequenceEqual("ftyp"u8))
            return false;
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header), (uint)header.Length);
        var heif = false;
        for (var at = 8; at + 4 <= size; at += at == 8 ? 8 : 4) // skip the minor version after the major brand
        {
            var brand = header.Slice(at, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return false;
            heif |= brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("heim"u8)
                || brand.SequenceEqual("heis"u8) || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
        }

        return heif;
    }

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (!TryParse(data, out var container)) return false;
        try
        {
            var (width, height) = container.DisplaySize();
            var depth = container.TileConfigs[0].ParseSps().BitDepthY;
            info = new ImageInfo(width, height, 3, depth > 8 ? SampleFormat.UInt16 : SampleFormat.UInt8)
            {
                Format = ImageFormat.Heif,
                BitDepth = depth,
                ColorEncoding = ColorEncodingOf(container),
                HasThumbnail = HeifMetadata.FindThumbnail(container) is not null,
            };
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>HEIF does not scale natively: the display size, at scale 1.</remarks>
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator) =>
        ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);

    /// <inheritdoc />
    /// <remarks>RGB, 3 channels, at the display size: 8-bit samples for an 8-bit image, 16-bit (bit depth 10) for a
    /// Main 10 one. A region (in display coordinates) is decoded natively: only the grid tiles it touches are decoded,
    /// and only its pixels converted.</remarks>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        image = null;
        if (!TryParse(data, out var container)) return false;
        (int Width, int Height) size;
        int depth;
        try
        {
            size = container.DisplaySize();
            depth = container.TileConfigs[0].ParseSps().BitDepthY;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        options?.Validate(size.Width, size.Height, supportsScale: false);
        var region = options?.Region;
        if (region is { } r)
            size = (r.Width, r.Height);

        var wide = depth > 8;
        var raster = RasterImage.Create(size.Width, size.Height, 3, wide ? SampleFormat.UInt16 : SampleFormat.UInt8,
            options?.Pool, ColorEncodingOf(container), bitDepth: wide && depth < 16 ? depth : 0,
            metadata: HeifMetadata.Read(data, container));
        try
        {
            HeifDecoder.DecodeInto(data, container, raster.WritablePixels, size.Width * 3 * (wide ? 2 : 1),
                wide ? PixelFormat.Rgb48 : PixelFormat.Rgb24, cancellationToken, region);
        }
        catch (Exception e)
        {
            raster.Dispose();
            if (e is InvalidDataException or ArgumentException or OverflowException)
                return false;
            throw;
        }
        // The region is decoded natively: only the grid tiles it touches are decoded, only its pixels converted.
        image = raster;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every layout and stride is written straight into <paramref name="destination"/>, one grid tile at a time,
    /// with no image-sized intermediate: 8-bit formats through the frozen 8-bit conversion, 16-bit ones through the
    /// 16-bit conversion, both from the coded samples. <see cref="PixelFormat.Gray8"/> is the stored luma,
    /// range-mapped to 8 bits (see <see cref="IImageDecoder.TryDecodeInto"/> on codecs that store luma; the formula is
    /// frozen in <c>HeifColor</c>), except for an identity-matrix (GBR) image, which takes the luma of the RGB. A
    /// region decodes only the tiles it touches. False, without throwing, for malformed or unsupported data, a
    /// destination that does not fit, and a PQ or HLG transfer function.
    /// </remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!TryParse(data, out var container)) return false;
        (int Width, int Height) size;
        try
        {
            size = container.DisplaySize();
        }
        catch (InvalidDataException)
        {
            return false;
        }
        options?.Validate(size.Width, size.Height, supportsScale: false);
        var region = options?.Region;
        if (region is { } r)
            size = (r.Width, r.Height);
        if (!PixelConverter.TryGetLayout(size.Width, size.Height, format, stride, destination.Length, out var rowStride)) return false;
        try
        {
            if (ColorEncodingOf(container).Transfer is TransferFunction.Pq or TransferFunction.Hlg) return false;

            // Every layout and stride is written straight into the destination, a tile at a time (HeifCanvas).
            HeifDecoder.DecodeInto(data, container, destination, rowStride, format, cancellationToken, region);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>The <c>Exif</c> item (from its TIFF header) and XMP <c>mime</c> item that describe the primary image
    /// (<c>cdsc</c>), and the <c>colr</c> ICC profile.</remarks>
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = null;
        if (!TryParse(data, out var container)) return false;
        metadata = HeifMetadata.Read(data, container);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>The primary image's <c>thmb</c> item, an HEVC image of its own, decoded with its own properties.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail)
    {
        thumbnail = null;
        if (!TryParse(data, out var container) || HeifMetadata.FindThumbnail(container) is not { } item) return false;
        try
        {
            var thumb = HeifContainer.Parse(data, item.Id);
            var (width, height) = thumb.DisplaySize();
            var depth = thumb.TileConfigs[0].ParseSps().BitDepthY;
            var wide = depth > 8;
            var raster = RasterImage.Allocate(width, height, 3, wide ? SampleFormat.UInt16 : SampleFormat.UInt8,
                ColorEncodingOf(thumb), bitDepth: wide && depth < 16 ? depth : 0,
                metadata: thumb.IccProfile is { Length: > 0 } icc ? new ImageMetadata { Icc = icc } : null);
            HeifDecoder.DecodeInto(data, thumb, raster.WritablePixels, width * 3 * (wide ? 2 : 1), wide ? PixelFormat.Rgb48 : PixelFormat.Rgb24);
            thumbnail = raster;
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>4:2:0 at the coded bit depth, on the canvas (the grid output before transforms). False for an image
    /// with a <c>clap</c>, <c>irot</c> or <c>imir</c> property: the planes would not be the displayed image, and an odd
    /// crop cannot be carried by 4:2:0 planes at all; decode such an image through <see cref="TryDecode"/>.</remarks>
    public static bool TryReadPlanarInfo(ReadOnlySpan<byte> data, out PlanarInfo info)
    {
        info = default;
        if (!TryParse(data, out var container) || container.Transforms.Count != 0) return false;
        try
        {
            info = PlanarInfoOf(container);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodePlanes(ReadOnlySpan<byte> data, Span<ushort> y, Span<ushort> cb, Span<ushort> cr,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(data, out var container) || container.Transforms.Count != 0) return false;
        try
        {
            var info = PlanarInfoOf(container);
            if (y.Length < info.LumaLength || cb.Length < info.ChromaLength || cr.Length < info.ChromaLength) return false;
            var planes = HeifDecoder.DecodeYuv(data, container, cancellationToken);
            planes.Y.AsSpan(0, info.LumaLength).CopyTo(y);
            planes.Cb.AsSpan(0, info.ChromaLength).CopyTo(cb);
            planes.Cr.AsSpan(0, info.ChromaLength).CopyTo(cr);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static PlanarInfo PlanarInfoOf(HeifContainer container)
    {
        var sps = container.TileConfigs[0].ParseSps();
        var (matrix, fullRange) = container.NclxColour is { } nclx
            ? (nclx.MatrixCoefficients, nclx.FullRange)
            : (sps.MatrixCoefficients, sps.VideoFullRange);
        // H.265 E.3.1: chroma_sample_loc_type defaults to 0 (left) when the VUI does not signal it.
        return new PlanarInfo(container.CanvasWidth, container.CanvasHeight, 2, 2, sps.BitDepthY,
            (MatrixCoefficients)(matrix is >= 0 and <= 255 ? matrix : 2), fullRange,
            (ChromaLocation)Math.Clamp(sps.ChromaSampleLocType, 0, 5));
    }

    /// <summary>Parses the container of a still image; false for anything <see cref="HeifDecoder"/> cannot decode.</summary>
    private static bool TryParse(ReadOnlySpan<byte> data, [NotNullWhen(true)] out HeifContainer? container)
    {
        container = null;
        if (!CanDecode(data) || IsSequenceBrand(data.Slice(8, 4))) return false;
        try
        {
            container = HeifContainer.Parse(data);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    // HEIF image-sequence brands (23008-12 §10): the file is a track of pictures, not a still image.
    private static bool IsSequenceBrand(ReadOnlySpan<byte> major) =>
        major.SequenceEqual("msf1"u8) || major.SequenceEqual("hevc"u8) || major.SequenceEqual("hevx"u8)
        || major.SequenceEqual("hevm"u8) || major.SequenceEqual("hevs"u8);

    /// <summary>
    /// The colour encoding of the decoded RGB: primaries and transfer from nclx; with an ICC profile and no
    /// nclx, <see cref="ColorEncoding.AssumedSrgb"/> (the <c>colr</c> box overrides the VUI); with no <c>colr</c>,
    /// the VUI colour description when it specifies one. Always full range (the conversion expands limited
    /// range). H.273 code points are 8-bit; a larger nclx value reads as unspecified.
    /// </summary>
    internal static ColorEncoding ColorEncodingOf(HeifContainer container)
    {
        int primaries, transfer;
        if (container.NclxColour is { } nclx)
        {
            (primaries, transfer) = (nclx.ColourPrimaries, nclx.TransferCharacteristics);
        }
        else if (container.IccProfile is not null)
        {
            return ColorEncoding.AssumedSrgb;
        }
        else
        {
            var sps = container.TileConfigs[0].ParseSps();
            if (sps.ColourPrimaries == 2 && sps.TransferCharacteristics == 2) return ColorEncoding.AssumedSrgb;
            (primaries, transfer) = (sps.ColourPrimaries, sps.TransferCharacteristics);
        }

        return new ColorEncoding
        {
            Primaries = (ColorPrimaries)(primaries is >= 0 and <= 255 ? primaries : 2),
            Transfer = (TransferFunction)(transfer is >= 0 and <= 255 ? transfer : 2),
            FullRange = true,
        };
    }
}
