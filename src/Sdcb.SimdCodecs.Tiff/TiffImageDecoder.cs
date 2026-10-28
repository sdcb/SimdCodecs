using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Sdcb.SimdCodecs.Abstractions;

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
/// </summary>
public sealed class TiffImageDecoder : IImageDecoder
{
    /// <inheritdoc />
    public static int SignatureLength => 4;

    /// <inheritdoc />
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 4 &&
        (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8));

    /// <inheritdoc />
    /// <remarks>TIFF geometry lives in the IFD chain, not at a fixed header offset,
    /// so this decodes the first page rather than peeking a fixed-size prefix.</remarks>
    public static bool TryReadInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = default;
        if (!TryDecodeCore(data, out var image)) return false;
        info = new ImageInfo(image.Width, image.Height, image.Channels, image.SampleFormat);
        return true;
    }

    /// <inheritdoc />
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? image)
    {
        var ok = TryDecodeCore(data, out var raster);
        image = raster;
        return ok;
    }

    /// <inheritdoc />
    /// <remarks>False for float pages: scene-linear float has no canonical 8-bit
    /// projection (see <see cref="RasterImage.ExpandToRgba8"/>) — use
    /// <see cref="TryDecode"/> + <see cref="IDecodedImage.ToFloats"/> instead.</remarks>
    public static bool TryDecodeInto(ReadOnlySpan<byte> data, Span<byte> destination, int stride, ImagePixelFormat format) =>
        TryDecode(data, out var image) && PixelConverter.TryConvert(image, destination, stride, format);

    /// <summary>
    /// <see cref="TryDecodeInto"/> to tightly packed 8-bit RGBA: <paramref name="rgbaDestination"/> must be
    /// at least <c>Width * Height * 4</c> bytes.
    /// </summary>
    public static bool TryDecodeIntoRgba8(ReadOnlySpan<byte> data, Span<byte> rgbaDestination) =>
        TryDecodeInto(data, rgbaDestination, 0, ImagePixelFormat.Rgba32);

    private static bool TryDecodeCore(ReadOnlySpan<byte> data, [NotNullWhen(true)] out RasterImage? image)
    {
        image = null;
        TiffPage page;
        try
        {
            page = TiffReader.ReadPage(data, 0);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        // Only photometrics whose samples IDecodedImage represents directly: gray
        // (MinIsBlack, or MinIsWhite once inverted) and RGB. Palette would need an
        // expansion, CMYK/YCbCr/CieLab a colour transform - meaning changes this
        // adapter refuses to make silently.
        if (page.Photometric is not (TiffPhotometric.MinIsBlack or TiffPhotometric.MinIsWhite or TiffPhotometric.Rgb))
            return false;
        var channels = page.SamplesPerPixel;
        if (channels is < 1 or > 4) return false;

        if (page.Photometric == TiffPhotometric.MinIsWhite)
        {
            // Inversion is only defined here for unsigned grey; anything else under MinIsWhite is refused.
            if (channels != 1 || page.SampleFormat != TiffSampleFormat.Uint) return false;
            if (page.BitsPerSample == 8)
            {
                foreach (ref var b in page.Pixels.AsSpan()) b = (byte)~b;
            }
            else if (page.BitsPerSample == 16)
            {
                foreach (ref var v in MemoryMarshal.Cast<byte, ushort>(page.Pixels.AsSpan())) v = (ushort)~v;
            }
        }

        var color = page.SampleFormat == TiffSampleFormat.IeeeFloat
            ? new ColorEncoding { Transfer = TransferFunction.Linear, Float = FloatSemantics.SceneReferred }
            : ColorEncoding.AssumedSrgb;

        switch (page.BitsPerSample, page.SampleFormat)
        {
            case (8, TiffSampleFormat.Uint):
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.UInt8, page.Pixels, page.IccProfile, color);
                return true;
            case (16, TiffSampleFormat.Uint):
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.UInt16, page.Pixels, page.IccProfile, color);
                return true;
            case (32, TiffSampleFormat.IeeeFloat):
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.Float32, page.Pixels, page.IccProfile, color);
                return true;
            case (16, TiffSampleFormat.IeeeFloat):
            {
                // 16-bit IEEE float (half): widen losslessly to Float32.
                var halves = MemoryMarshal.Cast<byte, Half>(page.Pixels);
                var widened = new float[halves.Length];
                for (var i = 0; i < halves.Length; i++) widened[i] = (float)halves[i];
                image = new RasterImage(page.Width, page.Height, channels, SampleFormat.Float32,
                    MemoryMarshal.AsBytes(widened.AsSpan()).ToArray(), page.IccProfile, color);
                return true;
            }
            default:
                return false; // signed ints, 32-bit uint, sub-byte depths
        }
    }
}
