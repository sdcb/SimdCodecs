using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Abstractions.CodecSupport;

namespace Sdcb.SimdCodecs.Jpeg2000;

/// <summary>
/// <see cref="IImageDecoder"/> adapter registering JPEG 2000 with the <c>Sdcb.SimdCodecs</c>
/// facade: a raw J2K codestream (<c>FF 4F FF 51</c>) or a JP2 file (its twelve-byte signature box).
/// <para>
/// <b>What the facade gets, and what it does not.</b> The facade's contract is samples that are
/// already RGB or grey, with <see cref="IDecodedImage.ColorEncoding"/> or an ICC profile saying
/// which. So one component decodes as grey and three as RGB, which is what a raw codestream's
/// components conventionally are once its RCT or ICT is undone, and what a JP2 file's sRGB or
/// greyscale <c>colr</c> box says; a restricted ICC profile is passed through as the image's
/// profile.
/// </para>
/// <para>
/// <b>Alpha.</b> A JP2 file's channel definition box (<c>cdef</c>) is what says a channel is opacity,
/// so with one the facade presents grey + alpha (2 channels) and RGB + alpha (4): colour in the colour
/// space's order whatever the codestream's, alpha last, straight. Opacity the box marks as premultiplied
/// is divided out: <c>c' = min(max, (c * max + a / 2) / a)</c> in integers at the codestream's precision
/// (<c>max</c> the largest sample, <c>a</c> the opacity), and 0 where <c>a</c> is 0. Two or four
/// components without the box stay refused, because nothing says whether the extra channel is alpha,
/// black (CMYK) or data.
/// </para>
/// <para>
/// Refused, so that <see cref="TryDecode"/> answers false rather than a picture in the wrong colours:
/// those; sYCC, whose samples are still YCbCr; any other enumerated colour space than sRGB and
/// greyscale (CMYK, CIELab, ...); an opacity channel that applies to a single colour rather than the
/// whole image; more than one opacity channel; a channel the box leaves unspecified; and a colour
/// space that does not match the number of colour channels. A caller that needs those has
/// <see cref="Jpeg2000Decoder.Decode(ReadOnlySpan{byte}, CancellationToken)"/>, which hands back every
/// component (reordered as the box says) and reports the colour box without applying it.
/// </para>
/// <para>
/// <b>Scaled decode.</b> <see cref="DecodeOptions.ScaleDenominator"/> is a resolution reduction: any
/// power of two up to 2 to the codestream's decomposition levels, decoded by leaving the finer levels
/// out (they are never entropy-decoded), with the same samples as <c>opj_decompress -r</c>. A larger
/// one decodes at that largest reduction, then applies the box filter of <see cref="BoxDownscaler"/>
/// for the rest. A <see cref="DecodeOptions.Region"/> is in the scaled image's coordinates and is
/// cropped from it. An image whose SIZ image offset is not zero has no native reduction (every scale
/// is the box filter of the full-size decode): on the reference grid a halving rounds relative to the
/// origin, which can differ by a pixel from the facade's <c>⌈w / d⌉</c> (the decoder's own
/// <see cref="Jpeg2000Decoder.Decode(ReadOnlySpan{byte}, int, CancellationToken)"/> still reduces it).
/// </para>
/// <para>
/// Samples up to 8 bits come back as <see cref="SampleFormat.UInt8"/>, deeper ones as
/// <see cref="SampleFormat.UInt16"/>, each scaled to fill its format's range (a 12-bit 4095 becomes
/// 65535), because the facade divides by the format's maximum, not the codestream's.
/// </para>
/// </summary>
public sealed class Jpeg2000ImageDecoder : IImageDecoder
{
    /// <summary>The registration for <c>ImageCodecs.Register</c>, built from this type's method groups.</summary>
    public static ImageDecoderRegistration Registration { get; } = new()
    {
        Format = ImageFormat.Jpeg2000,
        CanDecode = CanDecode,
        TryReadInfo = TryReadInfo,
        TryGetClosestSize = TryGetClosestSize,
        TryDecode = TryDecode,
        TryDecodeInto = TryDecodeInto,
        TryReadMetadata = TryReadMetadata,
        TryDecodeThumbnail = TryDecodeThumbnail,
    };

    /// <inheritdoc />
    public static int SignatureLength => 12;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        Jpeg2000Decoder.IsCodestream(header) || Jpeg2000Decoder.IsJp2(header);

    /// <inheritdoc />
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info) =>
        TryReadInfo(data, out info, out _);

    private static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info, [NotNullWhen(true)] out Jpeg2000Info? j2k)
    {
        info = default;
        j2k = null;
        if (!CanDecode(data)) return false;

        try
        {
            // ReadInfo, not SIZ alone: a JP2 palette changes how many channels, and of what depth,
            // the decoded image has.
            var read = Jpeg2000Decoder.ReadInfo(data);
            if (!Presentable(read.Components, read.Colour, read.Channels)) return false;

            var depth = read.BitDepth;
            info = new ImageInfo(read.Width, read.Height, read.Components, FormatFor(depth))
            {
                Format = ImageFormat.Jpeg2000,
                BitDepth = depth,
                ColorEncoding = ColorOf(read.Colour),
            };
            j2k = read;
            return true;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }
    }

    /// <summary>
    /// The largest scale denominator this image decodes at natively: 2 to the number of resolution
    /// levels that can be left out. A reduction halves each side on the reference grid (T.800 B-14),
    /// which is the facade's <c>⌈w / d⌉</c> only when the image region starts at the origin; an image
    /// whose SIZ offset is not zero can differ by a pixel at some scales, so it is decoded at 1 natively
    /// (<see cref="Jpeg2000Decoder.Decode(ReadOnlySpan{byte}, int, CancellationToken)"/> still reduces it).
    /// </summary>
    private static int MaxScaleDenominator(Jpeg2000Info j2k) =>
        j2k.Region.X0 == 0 && j2k.Region.Y0 == 0 ? 1 << Math.Min(j2k.DecompositionLevels, 30) : 1;

    /// <inheritdoc />
    /// <remarks>The smallest image, a power-of-two reduction of the stored one, that is at least the requested
    /// size in both directions (the stored size when the request is larger). A reduction costs only the
    /// levels it keeps: the finer ones are never entropy-decoded. Smaller scales are accepted too, but cost the
    /// largest reduction's decode and a box filter.</remarks>
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator)
    {
        width = height = 0;
        scaleDenominator = 1;
        if (!TryReadInfo(data, out var info, out var j2k)) return false;

        (width, height) = (info.Width, info.Height);
        var largest = MaxScaleDenominator(j2k);
        for (var d = largest; d >= 2; d >>= 1)
        {
            var (w, h) = new DecodeOptions { ScaleDenominator = d }.GetScaledSize(info.Width, info.Height);
            if (w >= requestedWidth && h >= requestedHeight)
            {
                (width, height, scaleDenominator) = (w, h, d);
                break;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        image = null;
        if (!CanDecode(data)) return false;
        if (!TryReadInfo(data, out var info, out var read)) return false;

        options?.Validate(info.Width, info.Height);
        // Resolution levels give the scales up to MaxScaleDenominator; the box filter does the rest.
        var native = options?.GetNativeScale(MaxScaleDenominator(read)) ?? 1;
        var reduce = BitOperations.Log2((uint)native);

        RasterImage? raster;
        try
        {
            var j2k = Jpeg2000Decoder.Decode(data, reduce, cancellationToken);
            raster = ToRaster(j2k, options?.Pool);
            if (raster is null) return false;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }

        try
        {
            // The raster is the image at 1/native; the region is in the scaled image's coordinates.
            image = DecodeOptions.Apply(raster, options, native);
            return true;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, PixelFormat format,
        DecodeOptions? options = null, CancellationToken cancellationToken = default) =>
        ImageDecoderFallback.TryDecodeInto(TryDecode, data, destination, stride, format, options, cancellationToken);

    /// <inheritdoc />
    public static bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata)
    {
        metadata = null;
        if (!TryReadInfo(data, out _)) return false;

        try
        {
            if (Jpeg2000Decoder.IsJp2(data))
            {
                var layout = Jp2Reader.Read(data);
                if (layout.Colour is { Method: 2, IccProfile: { } profile })
                {
                    metadata = new ImageMetadata { Icc = profile };
                    return true;
                }
            }

            metadata = ImageMetadata.Empty;
            return true;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>JPEG 2000 has no embedded thumbnails in Part 1: always false.</remarks>
    public static bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail) =>
        ImageDecoderFallback.NoThumbnail(out thumbnail);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, PixelFormat.Rgba32);

    /// <summary>
    /// The decoded image as a facade raster, or null when its components cannot be presented as RGB
    /// or grey (see the class remarks).
    /// </summary>
    internal static RasterImage? ToRaster(Jpeg2000Image image, ArrayPool<byte>? pool = null)
    {
        if (!Presentable(image.Components, image.Colour, image.Channels)) return null;

        var depth = image.BitDepth;
        var format = FormatFor(depth);
        var samples = image.Samples;
        if (image.Channels is [.., { Type: Jp2ChannelType.PremultipliedOpacity }])
        {
            // The image is the decoder's own, so its samples can be rewritten in place.
            Unpremultiply(samples, image.Components, (1 << depth) - 1);
        }

        var icc = image.Colour is { Method: 2, IccProfile: { } profile } ? profile : null;
        var bitDepth = (depth is 8 or 16) ? 0 : depth;

        var raster = RasterImage.Create(
            image.Width,
            image.Height,
            image.Components,
            format,
            pool,
            colorEncoding: ColorOf(image.Colour),
            bitDepth: bitDepth,
            metadata: icc is null ? null : new ImageMetadata { Icc = icc });

        if (format == SampleFormat.UInt8)
        {
            var dst = raster.WritablePixels;
            var maximum = (1 << depth) - 1;
            for (var i = 0; i < samples.Length; i++)
                dst[i] = depth == 8 ? (byte)samples[i] : (byte)((samples[i] * 255 + maximum / 2) / maximum);
        }
        else
        {
            var dst = MemoryMarshal.Cast<byte, ushort>(raster.WritablePixels);
            var maximum = (1 << depth) - 1;
            for (var i = 0; i < samples.Length; i++)
                dst[i] = depth == 16 ? samples[i] : (ushort)((samples[i] * 65535L + maximum / 2) / maximum);
        }

        return raster;
    }

    /// <summary>
    /// Divides the last channel's opacity out of the others, in integers: <c>min(max, (c * max + a / 2) / a)</c>,
    /// and 0 where the opacity is 0 (the colour of a transparent pixel is nothing to recover).
    /// </summary>
    private static void Unpremultiply(ushort[] samples, int channels, int maximum)
    {
        for (var p = 0; p + channels <= samples.Length; p += channels)
        {
            long alpha = samples[p + channels - 1];
            for (var c = 0; c < channels - 1; c++)
            {
                samples[p + c] = alpha == 0
                    ? (ushort)0
                    : (ushort)Math.Min(maximum, (samples[p + c] * (long)maximum + alpha / 2) / alpha);
            }
        }
    }

    /// <summary>
    /// The images whose samples are already grey or RGB, with at most one whole-image opacity channel
    /// after the colour: one component or three, not sYCC, when there is no channel definition; with
    /// one, exactly the colour channels (1 or 3, matching an enumerated colour space) plus at most
    /// one opacity channel that applies to the whole image.
    /// </summary>
    // A JP2 colr box is either an enumerated colour space (sRGB / grey, the assumption) or a restricted ICC
    // profile, whose primaries are reported when H.273 names them.
    private static ColorEncoding ColorOf(Jp2Colour? colour) =>
        colour is { Method: 2, IccProfile: { } profile } ? IccPrimaries.Encoding(profile) ?? ColorEncoding.AssumedSrgb
            : ColorEncoding.AssumedSrgb;

    private static bool Presentable(int components, Jp2Colour? colour, IReadOnlyList<Jp2Channel>? channels)
    {
        if (colour is { EnumeratedColourSpace: { } space } && space is not (Jp2EnumeratedColourSpace.Srgb or Jp2EnumeratedColourSpace.Greyscale))
            return false;
        if (channels is null) return components is 1 or 3;

        var colours = 0;
        var opacities = 0;
        foreach (var channel in channels)
        {
            switch (channel.Type)
            {
                case Jp2ChannelType.Colour:
                    colours++;
                    break;
                case Jp2ChannelType.Opacity or Jp2ChannelType.PremultipliedOpacity when channel.Association == 0:
                    opacities++;
                    break;
                default:
                    return false;
            }
        }

        if (colours is not (1 or 3) || opacities > 1 || components != colours + opacities) return false;
        return colour switch
        {
            { EnumeratedColourSpace: Jp2EnumeratedColourSpace.Srgb } => colours == 3,
            { EnumeratedColourSpace: Jp2EnumeratedColourSpace.Greyscale } => colours == 1,
            _ => true,
        };
    }

    private static SampleFormat FormatFor(int bitDepth) => bitDepth <= 8 ? SampleFormat.UInt8 : SampleFormat.UInt16;

    /// <summary>
    /// What "undecodable" means for a <c>bool</c>-returning <c>Try</c> method: malformed, hostile or
    /// unsupported input, for which the caller wants <c>false</c> rather than an exception. The same
    /// list <c>Jbig2ImageDecoder</c> uses, for the same reason: the facade delegates here with no
    /// catch of its own.
    /// </summary>
    private static bool IsUndecodable(Exception e) =>
        e is InvalidDataException or ArgumentException or NotSupportedException
          or OverflowException or OutOfMemoryException or IndexOutOfRangeException;
}
