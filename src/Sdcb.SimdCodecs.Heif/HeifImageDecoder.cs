using System.Buffers;
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
/// The primary image is decoded to 8-bit RGB, display-oriented: the clean aperture (<c>clap</c>), rotation
/// (<c>irot</c>) and mirror (<c>imir</c>) properties are applied, so <see cref="ImageInfo.Width"/> /
/// <see cref="ImageInfo.Height"/> are the displayed size and <see cref="ImageInfo.Orientation"/> is always
/// <see cref="ImageOrientation.Normal"/>. Alpha, depth and gain-map auxiliary images are not decoded, so the
/// raster has 3 channels. A Main 10 source is converted to 8-bit samples in 1.x (it is not kept at 10 bits),
/// so <see cref="ImageInfo.BitDepth"/> reports 8 — the precision of the samples handed back, which is what a
/// consumer can rely on; <see cref="HeifDecoder.ReadInfo"/> reports the coded depth.
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
public sealed class HeifImageDecoder : IImageDecoder
{
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
            info = new ImageInfo(width, height, 3, SampleFormat.UInt8)
            {
                Format = ImageFormat.Heif,
                BitDepth = 8,
            };
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>8-bit RGB, 3 channels, at the display size; a Main 10 source is converted to 8 bits (see the class
    /// remarks).</remarks>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        image = null;
        if (!TryParse(data, out var container)) return false;
        try
        {
            var (width, height) = container.DisplaySize();
            var pixelCount = (long)width * height;
            var rgba = new byte[pixelCount * 4];
            HeifDecoder.DecodeRgba(data, container, rgba);
            var rgb = new byte[pixelCount * 3];
            for (long source = 0, target = 0; source < rgba.Length; source += 4, target += 3)
            {
                rgb[target] = rgba[source];
                rgb[target + 1] = rgba[source + 1];
                rgb[target + 2] = rgba[source + 2];
            }

            image = new RasterImage(width, height, 3, SampleFormat.UInt8, rgb, container.IccProfile, ColorEncodingOf(container));
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Tightly packed <see cref="ImagePixelFormat.Rgba32"/> is the decoder's own output; the other layouts convert
    /// from a pooled RGBA frame. <see cref="ImagePixelFormat.Gray8"/> is the stored luma, range-mapped to 8 bits
    /// (see <see cref="IImageDecoder.TryDecodeInto"/> on codecs that store luma; the formula is frozen in
    /// <c>HeifColor</c>), except for an identity-matrix (GBR) image, which takes the luma of the RGB. False, without
    /// throwing, for malformed or unsupported data, a destination that does not fit, and a PQ or HLG transfer
    /// function.
    /// </remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format)
    {
        if (!TryParse(data, out var container)) return false;
        byte[]? rented = null;
        try
        {
            var (width, height) = container.DisplaySize();
            if (!PixelConverter.TryGetLayout(width, height, format, stride, destination.Length, out var rowStride)) return false;
            if (ColorEncodingOf(container).Transfer is TransferFunction.Pq or TransferFunction.Hlg) return false;

            if (format == ImagePixelFormat.Gray8 && HeifDecoder.MatrixCoefficients(container) != 0)
            {
                if (rowStride == width)
                {
                    HeifDecoder.DecodeGray(data, container, destination);
                    return true;
                }

                rented = ArrayPool<byte>.Shared.Rent(checked(width * height));
                HeifDecoder.DecodeGray(data, container, rented);
                for (var y = 0; y < height; y++)
                {
                    rented.AsSpan(y * width, width).CopyTo(destination.Slice(y * rowStride, width));
                }

                return true;
            }

            var rgbaRow = width * 4;
            if (format == ImagePixelFormat.Rgba32 && rowStride == rgbaRow)
            {
                HeifDecoder.DecodeRgba(data, container, destination);
                return true;
            }

            rented = ArrayPool<byte>.Shared.Rent(checked(rgbaRow * height));
            HeifDecoder.DecodeRgba(data, container, rented);
            var rowBytes = width * PixelConverter.BytesPerPixel(format);
            for (var y = 0; y < height; y++)
            {
                PixelConverter.ConvertRow(rented.AsSpan(y * rgbaRow, rgbaRow), 4, SampleFormat.UInt8,
                    destination.Slice(y * rowStride, rowBytes), format, width);
            }

            return true;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
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
