using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
/// profile. Refused, so that <see cref="TryDecode"/> answers false rather than a picture in the
/// wrong colours: two or four components, whose extra channel could be alpha or black and which
/// nothing here can tell apart without the channel-definition box this decoder does not read; and
/// sYCC, whose samples are still YCbCr. A caller that needs those has
/// <see cref="Jpeg2000Decoder.Decode(ReadOnlySpan{byte}, CancellationToken)"/>, which hands back every component and
/// reports the colour box without applying it.
/// </para>
/// <para>
/// Samples up to 8 bits come back as <see cref="SampleFormat.UInt8"/>, deeper ones as
/// <see cref="SampleFormat.UInt16"/>, each scaled to fill its format's range (a 12-bit 4095 becomes
/// 65535), because the facade divides by the format's maximum, not the codestream's.
/// </para>
/// </summary>
public sealed class Jpeg2000ImageDecoder : IImageDecoder
{
    /// <summary>The registration for <c>SimdCodecs.Register</c>, built from this type's method groups.</summary>
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
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (!CanDecode(data)) return false;

        try
        {
            var codestream = data;
            Jp2Colour? colour = null;
            if (Jpeg2000Decoder.IsJp2(data))
            {
                var layout = Jp2Reader.Read(data);
                codestream = data.Slice(layout.CodestreamStart, layout.CodestreamLength);
                colour = layout.Colour;
            }

            var siz = CodestreamReader.Read(codestream).Siz;
            if (!Presentable(siz.Components.Length, colour)) return false;

            var depth = siz.Components[0].BitDepth;
            info = new ImageInfo(siz.Width, siz.Height, siz.Components.Length, FormatFor(depth))
            {
                Format = ImageFormat.Jpeg2000,
                BitDepth = depth,
            };
            return true;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public static bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
        out int width, out int height, out int scaleDenominator) =>
        ImageDecoderFallback.TryGetStoredSize(TryReadInfo, data, out width, out height, out scaleDenominator);

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image,
        DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        image = null;
        if (!CanDecode(data)) return false;
        if (!TryReadInfo(data, out var info)) return false;

        options?.Validate(info.Width, info.Height, supportsScale: false);

        RasterImage? raster;
        try
        {
            var j2k = Jpeg2000Decoder.Decode(data, cancellationToken);
            raster = ToRaster(j2k, options?.Pool);
            if (raster is null) return false;
        }
        catch (Exception e) when (IsUndecodable(e))
        {
            return false;
        }

        try
        {
            image = DecodeOptions.Apply(raster, options);
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
        if (!Presentable(image.Components, image.Colour)) return null;

        var depth = image.BitDepth;
        var format = FormatFor(depth);
        var samples = image.Samples;
        var icc = image.Colour is { Method: 2, IccProfile: { } profile } ? profile : null;
        var bitDepth = (depth is 8 or 16) ? 0 : depth;

        var raster = RasterImage.Create(
            image.Width,
            image.Height,
            image.Components,
            format,
            pool,
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

    /// <summary>One component or three, and not sYCC: the images whose samples are already grey or RGB.</summary>
    private static bool Presentable(int components, Jp2Colour? colour) =>
        components is 1 or 3 && colour is not { EnumeratedColourSpace: Jp2EnumeratedColourSpace.Sycc };

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
